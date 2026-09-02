using System.Numerics;

namespace Coppelia.Models;

internal static class CoppeliaPairedActionPolicy
{
    public static bool ShouldHold(bool paired, bool mounted, bool mounting) =>
        paired && (mounted || mounting);
}

internal enum CoppeliaFollowPhase
{
    Idle,
    Follow,
    Mount,
    WaitForMount,
    Land,
    Dismount,
}

internal sealed record CoppeliaFollowDecision(
    CoppeliaFollowPhase Phase,
    bool IsFollowing,
    bool UseFlight,
    float RouteRange);

internal sealed class CoppeliaFollowPolicy
{
    public const float MountedResumeDistance = 30f;
    public const float MountedStopDistance = 5f;
    public const float OnFootResumeDistance = 20f;
    public const float OnFootStopDistance = 10f;
    public const float MountCatchUpDistance = 50f;

    public bool IsFollowing { get; private set; }

    public CoppeliaFollowDecision Evaluate(
        float distance,
        bool questerMounted,
        bool questerFlying,
        bool helperMounted,
        bool helperMounting,
        bool helperFlying,
        bool flightAvailable)
    {
        var resumeDistance = questerMounted ? MountedResumeDistance : OnFootResumeDistance;
        var stopDistance = questerMounted ? MountedStopDistance : OnFootStopDistance;
        IsFollowing = IsFollowing ? distance > stopDistance : distance > resumeDistance;

        if (helperMounting)
        {
            return new CoppeliaFollowDecision(
                CoppeliaFollowPhase.WaitForMount,
                IsFollowing,
                UseFlight: false,
                RouteRange: stopDistance);
        }

        var wantsMount = IsFollowing && (questerMounted || distance > MountCatchUpDistance);
        if (wantsMount && !helperMounted)
        {
            return new CoppeliaFollowDecision(
                CoppeliaFollowPhase.Mount,
                IsFollowing,
                UseFlight: false,
                RouteRange: stopDistance);
        }

        var shouldLand = helperFlying &&
                         !questerFlying &&
                         (questerMounted
                             ? !IsFollowing || distance <= MountedStopDistance
                             : distance <= OnFootResumeDistance);
        if (shouldLand)
        {
            return new CoppeliaFollowDecision(
                CoppeliaFollowPhase.Land,
                IsFollowing,
                UseFlight: false,
                RouteRange: stopDistance);
        }

        if (!questerMounted && helperMounted && !helperMounting && distance <= OnFootResumeDistance)
        {
            return new CoppeliaFollowDecision(
                CoppeliaFollowPhase.Dismount,
                IsFollowing,
                UseFlight: false,
                RouteRange: OnFootStopDistance);
        }

        if (!IsFollowing)
        {
            return new CoppeliaFollowDecision(
                CoppeliaFollowPhase.Idle,
                IsFollowing: false,
                UseFlight: false,
                RouteRange: stopDistance);
        }

        var mountedChase = helperMounted && !helperMounting;
        return new CoppeliaFollowDecision(
            CoppeliaFollowPhase.Follow,
            IsFollowing: true,
            UseFlight: mountedChase && flightAvailable,
            RouteRange: mountedChase || questerMounted
                ? MountedStopDistance
                : OnFootStopDistance);
    }

    public void Reset() => IsFollowing = false;
}

internal enum CoppeliaRouteActivity
{
    Idle,
    Owned,
    Other,
    Completed,
    Rejected,
}

internal enum CoppeliaRouteInterruption
{
    None,
    StopPath,
    StopPathAndCancelPending,
}

internal sealed class CoppeliaRoutePolicy
{
    public static readonly TimeSpan StartupActivityTimeout = TimeSpan.FromSeconds(2);
    public static readonly TimeSpan DestinationRefreshInterval = TimeSpan.FromSeconds(10);
    public const float DestinationRefreshDistance = 5f;

    private bool observedOwnedActivity;
    private bool restartAfterPause;
    private DateTime startupAcceptedUtc;
    private Vector3 startedDestination;
    private long activeSequence;
    private long lastStartedSequence;
    private long rejectedSequence;

    public bool OwnsRoute { get; private set; }
    public bool OwnsPendingPathfind { get; private set; }
    public bool StartupRejected { get; private set; }
    public bool HasDestination { get; private set; }
    public Vector3 LatestDestination { get; private set; }
    public long LatestSequence { get; private set; }

    public void AcceptSnapshot(long sequence, Vector3 destination)
    {
        if (sequence <= LatestSequence)
            return;

        LatestSequence = sequence;
        LatestDestination = destination;
        HasDestination = true;
        if (StartupRejected && sequence > rejectedSequence)
            StartupRejected = false;
    }

    public CoppeliaRouteActivity Observe(bool pathfindInProgress, bool pathRunning, DateTime utcNow)
    {
        var busy = pathfindInProgress || pathRunning;
        if (OwnsRoute)
        {
            if (busy)
            {
                observedOwnedActivity = true;
                if (OwnsPendingPathfind && pathRunning)
                    OwnsPendingPathfind = false;
                return CoppeliaRouteActivity.Owned;
            }

            if (!observedOwnedActivity && utcNow - startupAcceptedUtc < StartupActivityTimeout)
                return CoppeliaRouteActivity.Owned;

            OwnsRoute = false;
            OwnsPendingPathfind = false;
            if (!observedOwnedActivity)
            {
                StartupRejected = LatestSequence <= activeSequence;
                rejectedSequence = activeSequence;
                restartAfterPause = false;
                return CoppeliaRouteActivity.Rejected;
            }

            observedOwnedActivity = false;
            return CoppeliaRouteActivity.Completed;
        }

        if (StartupRejected)
            return CoppeliaRouteActivity.Rejected;

        return busy ? CoppeliaRouteActivity.Other : CoppeliaRouteActivity.Idle;
    }

    public bool CanStart(bool pathfindInProgress, bool pathRunning) =>
        HasDestination &&
        !OwnsRoute &&
        !StartupRejected &&
        !pathfindInProgress &&
        !pathRunning &&
        (LatestSequence > lastStartedSequence || restartAfterPause);

    public void MarkStartupAccepted(DateTime utcNow)
    {
        OwnsRoute = true;
        OwnsPendingPathfind = true;
        observedOwnedActivity = false;
        startupAcceptedUtc = utcNow;
        startedDestination = LatestDestination;
        activeSequence = LatestSequence;
        lastStartedSequence = LatestSequence;
        restartAfterPause = false;
        StartupRejected = false;
    }

    public void MarkStartupRejected(bool pathfindInProgress, bool pathRunning)
    {
        OwnsRoute = false;
        OwnsPendingPathfind = false;
        observedOwnedActivity = false;
        StartupRejected = true;
        rejectedSequence = LatestSequence;
        activeSequence = 0;
        lastStartedSequence = LatestSequence;
        restartAfterPause = false;
    }

    public CoppeliaRouteInterruption RefreshStaleDestination(
        bool pathfindInProgress,
        bool pathRunning,
        DateTime utcNow)
    {
        if (!OwnsRoute ||
            utcNow - startupAcceptedUtc < DestinationRefreshInterval ||
            Vector3.Distance(startedDestination, LatestDestination) <= DestinationRefreshDistance ||
            (!pathRunning && (!OwnsPendingPathfind || !pathfindInProgress)))
        {
            return CoppeliaRouteInterruption.None;
        }

        var latestSequence = LatestSequence;
        var latestDestination = LatestDestination;
        var interruption = Release(pathfindInProgress, pathRunning);
        AcceptSnapshot(latestSequence, latestDestination);
        return interruption;
    }

    public CoppeliaRouteInterruption Pause(bool pathRunning)
    {
        if (!OwnsRoute || !pathRunning)
            return CoppeliaRouteInterruption.None;

        restartAfterPause = true;
        return CoppeliaRouteInterruption.StopPath;
    }

    public CoppeliaRouteInterruption Release(bool pathfindInProgress, bool pathRunning)
    {
        var interruption = !OwnsRoute
            ? CoppeliaRouteInterruption.None
            : OwnsPendingPathfind && pathfindInProgress
                ? CoppeliaRouteInterruption.StopPathAndCancelPending
                : pathRunning
                    ? CoppeliaRouteInterruption.StopPath
                    : CoppeliaRouteInterruption.None;

        OwnsRoute = false;
        OwnsPendingPathfind = false;
        observedOwnedActivity = false;
        restartAfterPause = false;
        startupAcceptedUtc = default;
        startedDestination = default;
        activeSequence = 0;
        lastStartedSequence = 0;
        rejectedSequence = 0;
        StartupRejected = false;
        HasDestination = false;
        LatestDestination = default;
        LatestSequence = 0;
        return interruption;
    }
}

internal enum CoppeliaFlightEligibility
{
    Locked,
    Unlocked,
    UnknownButMountable,
}

internal enum CoppeliaFlightProbeState
{
    None,
    Pending,
    Proven,
    Rejected,
}

internal sealed class CoppeliaFlightPolicy
{
    private uint territoryId;

    public CoppeliaFlightEligibility Eligibility { get; private set; } = CoppeliaFlightEligibility.Locked;
    public CoppeliaFlightProbeState ProbeState { get; private set; }

    public static CoppeliaFlightEligibility Classify(
        bool mountable,
        bool hasStandardAetherCurrentSet,
        bool standardSetComplete,
        bool isArrTerritory)
    {
        if (!mountable)
            return CoppeliaFlightEligibility.Locked;
        if (hasStandardAetherCurrentSet)
            return standardSetComplete
                ? CoppeliaFlightEligibility.Unlocked
                : CoppeliaFlightEligibility.Locked;
        return isArrTerritory
            ? CoppeliaFlightEligibility.UnknownButMountable
            : CoppeliaFlightEligibility.Locked;
    }

    public void EnterTerritory(uint newTerritoryId, CoppeliaFlightEligibility eligibility)
    {
        if (territoryId == newTerritoryId && Eligibility == eligibility)
            return;

        territoryId = newTerritoryId;
        Eligibility = eligibility;
        ProbeState = CoppeliaFlightProbeState.None;
    }

    public bool ShouldUseFlight => Eligibility switch
    {
        CoppeliaFlightEligibility.Unlocked => true,
        CoppeliaFlightEligibility.UnknownButMountable => ProbeState != CoppeliaFlightProbeState.Rejected,
        _ => false,
    };

    public bool IsProbePending =>
        Eligibility == CoppeliaFlightEligibility.UnknownButMountable &&
        ProbeState == CoppeliaFlightProbeState.Pending;

    public void MarkRouteQueued(bool useFlight)
    {
        if (useFlight && Eligibility == CoppeliaFlightEligibility.UnknownButMountable &&
            ProbeState == CoppeliaFlightProbeState.None)
        {
            ProbeState = CoppeliaFlightProbeState.Pending;
        }
    }

    public void ObserveRouteActivity(bool active)
    {
        if (active && IsProbePending)
            ProbeState = CoppeliaFlightProbeState.Proven;
    }

    public void MarkProbeRejected()
    {
        if (Eligibility == CoppeliaFlightEligibility.UnknownButMountable &&
            ProbeState is CoppeliaFlightProbeState.None or CoppeliaFlightProbeState.Pending)
        {
            ProbeState = CoppeliaFlightProbeState.Rejected;
        }
    }

    public void Reset()
    {
        territoryId = 0;
        Eligibility = CoppeliaFlightEligibility.Locked;
        ProbeState = CoppeliaFlightProbeState.None;
    }
}

internal enum CoppeliaLifestreamActivity
{
    Idle,
    WaitingForBusy,
    Busy,
    Completed,
    Failed,
}

internal sealed class CoppeliaLifestreamRequestPolicy
{
    public static readonly TimeSpan BusyStartupTimeout = TimeSpan.FromSeconds(5);

    private DateTime startedUtc;

    public long Sequence { get; private set; }
    public CoppeliaLifestreamActivity Activity { get; private set; }
    public bool HasRequest => Activity != CoppeliaLifestreamActivity.Idle;

    public void Begin(long sequence, bool accepted, DateTime utcNow)
    {
        Sequence = sequence;
        startedUtc = utcNow;
        Activity = accepted
            ? CoppeliaLifestreamActivity.WaitingForBusy
            : CoppeliaLifestreamActivity.Failed;
    }

    public CoppeliaLifestreamActivity Observe(bool busy, bool targetReached, DateTime utcNow)
    {
        if (!HasRequest)
            return Activity;
        if (targetReached)
            return Activity = CoppeliaLifestreamActivity.Completed;

        if (Activity == CoppeliaLifestreamActivity.WaitingForBusy)
        {
            if (busy)
                return Activity = CoppeliaLifestreamActivity.Busy;
            if (utcNow - startedUtc >= BusyStartupTimeout)
                return Activity = CoppeliaLifestreamActivity.Failed;
        }
        else if (Activity == CoppeliaLifestreamActivity.Busy && !busy)
        {
            return Activity = CoppeliaLifestreamActivity.Failed;
        }

        return Activity;
    }

    public void Fail() => Activity = CoppeliaLifestreamActivity.Failed;

    public bool CanReplaceWith(long sequence) =>
        Activity is CoppeliaLifestreamActivity.Completed or CoppeliaLifestreamActivity.Failed &&
        sequence > Sequence;

    public void Reset()
    {
        Sequence = 0;
        startedUtc = default;
        Activity = CoppeliaLifestreamActivity.Idle;
    }
}

internal enum CoppeliaTerritoryHandoffPhase
{
    Idle,
    Armed,
    Probing,
    WaitingForLoad,
    Fallback,
}

internal enum CoppeliaTerritoryHandoffDecision
{
    None,
    StartForwardProbe,
    ContinueForwardProbe,
    WaitForLoad,
    DestinationReached,
    UseTeleportFallback,
}

internal sealed class CoppeliaTerritoryHandoffPolicy
{
    private DateTime probeStartedUtc;
    private TimeSpan probeDuration;

    public CoppeliaTerritoryHandoffPhase Phase { get; private set; }
    public uint SourceTerritoryId { get; private set; }
    public bool IsActive => Phase != CoppeliaTerritoryHandoffPhase.Idle;
    public DateTime ProbeStartedUtc => probeStartedUtc;
    public TimeSpan ProbeDuration => probeDuration;

    public bool TryArm(
        bool hasPreviousSnapshot,
        bool explicitTeleport,
        bool worldChanged,
        uint previousTerritoryId,
        uint destinationTerritoryId)
    {
        if (IsActive ||
            !hasPreviousSnapshot ||
            explicitTeleport ||
            worldChanged ||
            previousTerritoryId == 0 ||
            destinationTerritoryId == 0 ||
            destinationTerritoryId == previousTerritoryId)
        {
            return false;
        }

        SourceTerritoryId = previousTerritoryId;
        Phase = CoppeliaTerritoryHandoffPhase.Armed;
        return true;
    }

    public CoppeliaTerritoryHandoffDecision Evaluate(
        uint currentTerritoryId,
        uint latestDestinationTerritoryId,
        bool betweenAreas,
        bool helperLoaded,
        TimeSpan forwardProbeDuration,
        DateTime utcNow)
    {
        if (!IsActive)
            return CoppeliaTerritoryHandoffDecision.None;

        if (helperLoaded && !betweenAreas && currentTerritoryId == latestDestinationTerritoryId)
        {
            Reset();
            return CoppeliaTerritoryHandoffDecision.DestinationReached;
        }

        if (Phase is CoppeliaTerritoryHandoffPhase.Armed or CoppeliaTerritoryHandoffPhase.WaitingForLoad)
        {
            if (betweenAreas || !helperLoaded || currentTerritoryId == 0)
            {
                Phase = CoppeliaTerritoryHandoffPhase.WaitingForLoad;
                return CoppeliaTerritoryHandoffDecision.WaitForLoad;
            }

            if (currentTerritoryId != SourceTerritoryId)
            {
                Phase = CoppeliaTerritoryHandoffPhase.Fallback;
                return CoppeliaTerritoryHandoffDecision.UseTeleportFallback;
            }

            if (probeStartedUtc == default)
            {
                probeStartedUtc = utcNow;
                probeDuration = forwardProbeDuration;
            }
            else if (utcNow - probeStartedUtc >= probeDuration)
            {
                Phase = CoppeliaTerritoryHandoffPhase.Fallback;
                return CoppeliaTerritoryHandoffDecision.UseTeleportFallback;
            }

            Phase = CoppeliaTerritoryHandoffPhase.Probing;
            return CoppeliaTerritoryHandoffDecision.StartForwardProbe;
        }

        if (Phase == CoppeliaTerritoryHandoffPhase.Probing)
        {
            if (betweenAreas || !helperLoaded || currentTerritoryId == 0)
            {
                Phase = CoppeliaTerritoryHandoffPhase.WaitingForLoad;
                return CoppeliaTerritoryHandoffDecision.WaitForLoad;
            }

            if (currentTerritoryId != SourceTerritoryId || utcNow - probeStartedUtc >= probeDuration)
            {
                Phase = CoppeliaTerritoryHandoffPhase.Fallback;
                return CoppeliaTerritoryHandoffDecision.UseTeleportFallback;
            }

            return CoppeliaTerritoryHandoffDecision.ContinueForwardProbe;
        }

        return CoppeliaTerritoryHandoffDecision.UseTeleportFallback;
    }

    public void FailProbe() => Phase = CoppeliaTerritoryHandoffPhase.Fallback;

    public void Reset()
    {
        Phase = CoppeliaTerritoryHandoffPhase.Idle;
        SourceTerritoryId = 0;
        probeStartedUtc = default;
        probeDuration = default;
    }
}

internal sealed class CoppeliaTravelSequencePolicy
{
    public long LastAcceptedSequence { get; private set; }
    public long BlockedSequence { get; private set; }
    public string BlockedState { get; private set; } = string.Empty;

    public bool TryAccept(long sequence)
    {
        if (sequence <= LastAcceptedSequence)
            return false;

        LastAcceptedSequence = sequence;
        BlockedSequence = 0;
        BlockedState = string.Empty;
        return true;
    }

    public bool IsBlocked(long sequence) => BlockedSequence == sequence;

    public void Block(long sequence, string state)
    {
        BlockedSequence = sequence;
        BlockedState = state;
    }

    public void Reset()
    {
        LastAcceptedSequence = 0;
        BlockedSequence = 0;
        BlockedState = string.Empty;
    }
}

internal static class CoppeliaTeleportIntentPolicy
{
    public static bool IsStale(
        long exactSequence,
        uint exactSourceTerritory,
        uint exactDestinationTerritory,
        long latestSequence,
        uint latestDestinationTerritory) =>
        latestSequence > exactSequence &&
        latestDestinationTerritory != exactSourceTerritory &&
        latestDestinationTerritory != exactDestinationTerritory;

    public static bool CanUseFallback(
        long exactSequence,
        uint exactDestinationTerritory,
        long latestSequence,
        uint latestDestinationTerritory) =>
        latestSequence > exactSequence &&
        latestDestinationTerritory == exactDestinationTerritory;

    public static bool TryFindExact(
        IReadOnlyList<CoppeliaTeleportListEntry> teleportList,
        uint aetheryteId,
        byte subIndex,
        out CoppeliaTeleportListEntry selected)
    {
        selected = teleportList.FirstOrDefault(entry =>
            entry.AetheryteId == aetheryteId &&
            entry.SubIndex == subIndex)!;
        return selected != null;
    }
}

internal sealed record CoppeliaTeleportListEntry(uint AetheryteId, byte SubIndex, uint GilCost);

internal static class CoppeliaTeleportListPolicy
{
    public static bool IsAvailable(bool hasTelepoInstance, int rawEntryCount, int validEntryCount) =>
        hasTelepoInstance && rawEntryCount > 0 && validEntryCount > 0;
}

internal sealed record CoppeliaAetheryteLevelReference(uint RowId, Vector3? EmbeddedPosition);

internal sealed record CoppeliaAetheryteMapMarker(float X, float Y, byte DataType, uint DataKeyId);

internal sealed record CoppeliaAetheryteMapTransform(float SizeFactor, float OffsetX, float OffsetY);

internal sealed record CoppeliaMapLocationSelection(
    string AetheryteName,
    bool HasRealXYZ,
    Vector3 RealPosition);

internal sealed record CoppeliaAetheryteCandidate(
    uint Id,
    byte SubIndex,
    uint TerritoryId,
    string Name,
    uint PlaceNameId,
    uint GilCost,
    Vector3 Position,
    bool HasStoredPosition);

internal sealed record CoppeliaAetheryteSelection(
    CoppeliaAetheryteCandidate Candidate,
    double Distance,
    bool UsedXyzComparison,
    bool WinnerUsedXyz);

internal static class CoppeliaAetherytePolicy
{
    public const uint TamamizuAetheryteId = 105;

    public static Vector3 ResolveLevelPosition(
        IEnumerable<CoppeliaAetheryteLevelReference> levelReferences,
        Func<uint, Vector3?> directLevelLookup)
    {
        foreach (var levelReference in levelReferences)
        {
            if (levelReference.EmbeddedPosition is { } embeddedPosition)
            {
                if (embeddedPosition.X != 0 || embeddedPosition.Z != 0)
                    return embeddedPosition;
            }
            else if (levelReference.RowId > 0 && directLevelLookup(levelReference.RowId) is { } directPosition)
            {
                if (directPosition.X != 0 || directPosition.Z != 0)
                    return directPosition;
            }
        }

        return Vector3.Zero;
    }

    public static CoppeliaAetheryteSelection? Resolve(
        uint territoryId,
        Vector3 targetPosition,
        IEnumerable<CoppeliaAetheryteCandidate> sourceCandidates,
        bool avoidTamamizu,
        CoppeliaAetheryteMapTransform? mapTransform = null,
        IEnumerable<CoppeliaAetheryteMapMarker>? mapMarkers = null,
        CoppeliaMapLocationSelection? mapLocation = null)
    {
        var candidates = sourceCandidates
            .Where(candidate => candidate.TerritoryId == territoryId)
            .Where(candidate => !avoidTamamizu || candidate.Id != TamamizuAetheryteId)
            .ToList();
        if (candidates.Count == 0)
            return null;

        if (mapTransform != null && candidates.Any(candidate => candidate.Position == Vector3.Zero))
            ApplyMapMarkerFallback(candidates, targetPosition, mapTransform, mapMarkers ?? []);

        if (targetPosition != default && !string.IsNullOrEmpty(mapLocation?.AetheryteName))
        {
            var overrideCandidate = candidates.FirstOrDefault(candidate =>
                string.Equals(candidate.Name, mapLocation.AetheryteName, StringComparison.OrdinalIgnoreCase));
            if (overrideCandidate != null)
                return new CoppeliaAetheryteSelection(overrideCandidate, double.MaxValue, false, false);
        }

        var hasRealDestination = targetPosition != default && mapLocation?.HasRealXYZ == true;
        var comparisonTarget = hasRealDestination ? mapLocation!.RealPosition : targetPosition;
        var candidatesWithPositions = candidates
            .Where(candidate => candidate.Position != Vector3.Zero)
            .ToList();

        if (targetPosition != default && candidatesWithPositions.Count > 0)
        {
            var closest = candidatesWithPositions
                .OrderBy(candidate => DistanceSquared(candidate, comparisonTarget, hasRealDestination))
                .First();
            var useXyz = hasRealDestination && closest.HasStoredPosition;
            return new CoppeliaAetheryteSelection(
                closest,
                Math.Sqrt(DistanceSquared(closest, comparisonTarget, hasRealDestination)),
                hasRealDestination,
                useXyz);
        }

        var cheapest = candidates.OrderBy(candidate => candidate.GilCost).First();
        return new CoppeliaAetheryteSelection(cheapest, double.MaxValue, hasRealDestination, false);
    }

    public static Vector3 ConvertMapMarker(
        CoppeliaAetheryteMapMarker marker,
        CoppeliaAetheryteMapTransform mapTransform)
    {
        var scaleFactor = mapTransform.SizeFactor / 100f;
        return new Vector3(
            (marker.X / scaleFactor - 1024f) / scaleFactor + mapTransform.OffsetX,
            0f,
            (marker.Y / scaleFactor - 1024f) / scaleFactor + mapTransform.OffsetY);
    }

    public static bool IsArrivalWithinRecordingRange(
        Vector3 playerPosition,
        Vector3 estimatedAetherytePosition)
    {
        if (playerPosition == Vector3.Zero || estimatedAetherytePosition == Vector3.Zero)
            return false;

        var dx = playerPosition.X - estimatedAetherytePosition.X;
        var dz = playerPosition.Z - estimatedAetherytePosition.Z;
        return dx * dx + dz * dz <= 20f * 20f;
    }

    private static double DistanceSquared(
        CoppeliaAetheryteCandidate candidate,
        Vector3 comparisonTarget,
        bool hasRealDestination)
    {
        var dx = candidate.Position.X - comparisonTarget.X;
        var dz = candidate.Position.Z - comparisonTarget.Z;
        if (!hasRealDestination || !candidate.HasStoredPosition)
            return dx * dx + dz * dz;

        var dy = candidate.Position.Y - comparisonTarget.Y;
        return dx * dx + dy * dy + dz * dz;
    }

    private static void ApplyMapMarkerFallback(
        List<CoppeliaAetheryteCandidate> candidates,
        Vector3 targetPosition,
        CoppeliaAetheryteMapTransform mapTransform,
        IEnumerable<CoppeliaAetheryteMapMarker> sourceMarkers)
    {
        var markers = sourceMarkers
            .Where(marker => marker.DataType is 3 or 4)
            .ToList();
        if (markers.Count == 0)
            return;

        var markerWorldPositions = markers
            .Select(marker => ConvertMapMarker(marker, mapTransform))
            .ToList();

        var dataKeyToCandidateIndex = new Dictionary<uint, int>();
        for (var candidateIndex = 0; candidateIndex < candidates.Count; candidateIndex++)
        {
            var candidate = candidates[candidateIndex];
            if (candidate.Position != Vector3.Zero)
                continue;

            dataKeyToCandidateIndex[candidate.Id] = candidateIndex;
            if (candidate.PlaceNameId > 0 && !dataKeyToCandidateIndex.ContainsKey(candidate.PlaceNameId))
                dataKeyToCandidateIndex[candidate.PlaceNameId] = candidateIndex;
        }

        var matchedCount = 0;
        for (var markerIndex = 0; markerIndex < markers.Count; markerIndex++)
        {
            if (!dataKeyToCandidateIndex.TryGetValue(markers[markerIndex].DataKeyId, out var candidateIndex) ||
                candidates[candidateIndex].Position != Vector3.Zero)
            {
                continue;
            }

            candidates[candidateIndex] = candidates[candidateIndex] with
            {
                Position = markerWorldPositions[markerIndex],
            };
            matchedCount++;
        }

        if (matchedCount != 0 || targetPosition == default)
            return;

        var unassignedCandidateIndexes = candidates
            .Select((candidate, index) => (candidate, index))
            .Where(item => item.candidate.Position == Vector3.Zero)
            .Select(item => item.index)
            .ToList();
        for (var markerIndex = 0;
             markerIndex < markerWorldPositions.Count && markerIndex < unassignedCandidateIndexes.Count;
             markerIndex++)
        {
            var candidateIndex = unassignedCandidateIndexes[markerIndex];
            candidates[candidateIndex] = candidates[candidateIndex] with
            {
                Position = markerWorldPositions[markerIndex],
            };
        }
    }
}

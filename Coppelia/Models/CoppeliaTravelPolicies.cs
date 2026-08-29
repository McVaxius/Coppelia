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

    private bool observedOwnedActivity;
    private bool restartAfterPause;
    private DateTime startupAcceptedUtc;
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

internal sealed record CoppeliaAetheryteCandidate(uint Id, Vector3 Position, bool Unlocked);

internal static class CoppeliaAetherytePolicy
{
    public static CoppeliaAetheryteCandidate? SelectNearest(
        Vector3 destination,
        IEnumerable<CoppeliaAetheryteCandidate> candidates) =>
        candidates
            .Where(candidate => candidate.Unlocked)
            .OrderBy(candidate => Vector3.DistanceSquared(candidate.Position, destination))
            .FirstOrDefault();
}

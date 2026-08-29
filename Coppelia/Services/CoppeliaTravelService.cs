using System.Numerics;
using Coppelia.Models;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Ipc;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using Lumina.Excel.Sheets;

namespace Coppelia.Services;

internal sealed class CoppeliaTravelService
{
    private const uint MountRouletteGeneralActionId = 9;
    private const uint DismountGeneralActionId = 23;

    private readonly ICallGateSubscriber<bool> lifestreamBusy;
    private readonly ICallGateSubscriber<uint, bool> changeWorld;
    private readonly ICallGateSubscriber<uint, byte, bool> teleport;
    private readonly ICallGateSubscriber<bool> navReady;
    private readonly ICallGateSubscriber<bool> pathRunning;
    private readonly ICallGateSubscriber<bool> pathfindInProgress;
    private readonly ICallGateSubscriber<Vector3, bool, float, bool> moveCloseTo;
    private readonly ICallGateSubscriber<object> pathStop;
    private readonly ICallGateSubscriber<object> cancelAll;
    private readonly CoppeliaFollowPolicy followPolicy = new();
    private readonly CoppeliaRoutePolicy routePolicy = new();
    private readonly CoppeliaFlightPolicy flightPolicy = new();
    private readonly CoppeliaLifestreamRequestPolicy lifestreamPolicy = new();

    private CoppeliaQstCommand? latestTravel;
    private CoppeliaQstCommand? pendingExactTravel;
    private LifestreamRequest? lifestreamRequest;
    private long lastTravelSequence;
    private long blockedTravelSequence;
    private string blockedTravelState = string.Empty;
    private DateTime actionHoldUntilUtc = DateTime.MinValue;
    private DateTime nextMountActionUtc = DateTime.MinValue;

    public CoppeliaTravelService()
    {
        lifestreamBusy = Plugin.PluginInterface.GetIpcSubscriber<bool>("Lifestream.IsBusy");
        changeWorld = Plugin.PluginInterface.GetIpcSubscriber<uint, bool>("Lifestream.ChangeWorldById");
        teleport = Plugin.PluginInterface.GetIpcSubscriber<uint, byte, bool>("Lifestream.Teleport");
        navReady = Plugin.PluginInterface.GetIpcSubscriber<bool>("vnavmesh.Nav.IsReady");
        pathRunning = Plugin.PluginInterface.GetIpcSubscriber<bool>("vnavmesh.Path.IsRunning");
        pathfindInProgress = Plugin.PluginInterface.GetIpcSubscriber<bool>("vnavmesh.SimpleMove.PathfindInProgress");
        moveCloseTo = Plugin.PluginInterface.GetIpcSubscriber<Vector3, bool, float, bool>("vnavmesh.SimpleMove.PathfindAndMoveCloseTo");
        pathStop = Plugin.PluginInterface.GetIpcSubscriber<object>("vnavmesh.Path.Stop");
        cancelAll = Plugin.PluginInterface.GetIpcSubscriber<object>("vnavmesh.Nav.PathfindCancelAll");
    }

    public string State { get; private set; } = "Idle";

    public void PauseForAction()
    {
        actionHoldUntilUtc = DateTime.UtcNow.AddSeconds(2);
        PauseOwnedRoute();
        State = "Paused for a Coppelia action";
    }

    public (bool Ready, string Blocker) EvaluateReadiness()
    {
        if (!lifestreamBusy.HasFunction || !changeWorld.HasFunction || !teleport.HasFunction)
            return (false, "Required Lifestream travel IPC is unavailable.");
        if (!navReady.HasFunction || !pathRunning.HasFunction || !pathfindInProgress.HasFunction || !moveCloseTo.HasFunction ||
            !pathStop.HasAction || !cancelAll.HasAction)
            return (false, "Required vnavmesh travel IPC is unavailable.");

        try
        {
            _ = lifestreamBusy.InvokeFunc();
        }
        catch (Exception)
        {
            return (false, "Lifestream travel IPC is unavailable.");
        }

        try
        {
            _ = navReady.InvokeFunc();
        }
        catch (Exception)
        {
            return (false, "vnavmesh travel IPC is unavailable.");
        }

        return (true, string.Empty);
    }

    public CoppeliaQstCommandResponse Apply(CoppeliaQstCommand command)
    {
        if (command.TravelSequence <= lastTravelSequence)
            return new CoppeliaQstCommandResponse(true, "Travel update was already applied.");
        if (command.QuesterCurrentWorldId == 0 || command.TerritoryId == 0)
            return new CoppeliaQstCommandResponse(false, "Travel update is missing world or territory metadata.");

        lastTravelSequence = command.TravelSequence;
        latestTravel = command;
        blockedTravelSequence = 0;
        blockedTravelState = string.Empty;
        routePolicy.AcceptSnapshot(command.TravelSequence, new Vector3(command.X, command.Y, command.Z));
        if (command.AetheryteId.HasValue)
            pendingExactTravel = command;
        return new CoppeliaQstCommandResponse(true, "Travel update accepted.");
    }

    public void Update()
    {
        if (latestTravel == null)
            return;

        if (Plugin.Condition[ConditionFlag.BoundByDuty])
        {
            Stop("Suspended inside duty");
            return;
        }

        var localPlayer = Plugin.ObjectTable.LocalPlayer;
        if (localPlayer == null)
        {
            State = "Waiting for the helper character";
            return;
        }

        if (localPlayer is IBattleChara battleChara && battleChara.IsCasting)
        {
            actionHoldUntilUtc = DateTime.UtcNow.AddMilliseconds(250);
            PauseOwnedRoute();
            State = $"Paused for cast {battleChara.CastActionId}";
            return;
        }
        if (DateTime.UtcNow < actionHoldUntilUtc)
        {
            PauseOwnedRoute();
            State = "Paused for a Coppelia action";
            return;
        }

        var travel = latestTravel;
        var currentWorld = (ushort)localPlayer.CurrentWorld.RowId;
        if (ObserveLifestreamRequest(localPlayer, travel))
            return;

        if (blockedTravelSequence == travel.TravelSequence)
        {
            State = blockedTravelState;
            return;
        }

        if (currentWorld != travel.QuesterCurrentWorldId)
        {
            BeginLifestreamRequest(
                travel.TravelSequence,
                new LifestreamRequest(
                    IsWorld: true,
                    WorldId: travel.QuesterCurrentWorldId,
                    TerritoryId: 0,
                    ActiveState: $"Visiting world {travel.QuesterCurrentWorldId}",
                    FailureState: $"World visit to {travel.QuesterCurrentWorldId} did not reach its target"),
                () => changeWorld.InvokeFunc(travel.QuesterCurrentWorldId));
            return;
        }

        if (pendingExactTravel != null)
        {
            PauseOwnedRoute();
            if (!TryResolvePendingExactTeleport(
                    pendingExactTravel,
                    travel,
                    out var exactTerritory,
                    out var exactAetheryteId,
                    out var exactSubIndex,
                    out var exactName,
                    out var exactBlocker))
            {
                BlockTravel(travel.TravelSequence, exactBlocker);
                return;
            }

            pendingExactTravel = null;
            BeginLifestreamRequest(
                travel.TravelSequence,
                new LifestreamRequest(
                    IsWorld: false,
                    WorldId: 0,
                    TerritoryId: exactTerritory,
                    ActiveState: $"Teleporting to {exactName}",
                    FailureState: $"Teleport to {exactName} did not reach territory {exactTerritory}"),
                () => teleport.InvokeFunc(exactAetheryteId, exactSubIndex));
            return;
        }

        if (Plugin.ClientState.TerritoryType != travel.TerritoryId)
        {
            PauseOwnedRoute();
            if (!TryResolveTeleport(travel, out var aetheryteId, out var subIndex, out var name, out var blocker))
            {
                BlockTravel(travel.TravelSequence, blocker);
                return;
            }

            BeginLifestreamRequest(
                travel.TravelSequence,
                new LifestreamRequest(
                    IsWorld: false,
                    WorldId: 0,
                    TerritoryId: travel.TerritoryId,
                    ActiveState: $"Teleporting to {name}",
                    FailureState: $"Teleport to {name} did not reach territory {travel.TerritoryId}"),
                () => teleport.InvokeFunc(aetheryteId, subIndex));
            return;
        }

        var destination = routePolicy.LatestDestination;
        var distance = Vector3.Distance(localPlayer.Position, destination);
        var mounted = Plugin.Condition[ConditionFlag.Mounted];
        var mounting = Plugin.Condition[ConditionFlag.Mounting71];
        var flying = Plugin.Condition[ConditionFlag.InFlight];
        var fallbackBlocker = string.Empty;
        var flightEligibility = GetFlightEligibility(out var flightFailure);
        flightPolicy.EnterTerritory(Plugin.ClientState.TerritoryType, flightEligibility);
        if (flightEligibility == CoppeliaFlightEligibility.UnknownButMountable &&
            flightPolicy.ProbeState == CoppeliaFlightProbeState.Rejected)
        {
            flightFailure = "the ARR flight probe was rejected for this territory";
        }
        var flightAvailable = mounted && !mounting && flightPolicy.ShouldUseFlight;
        var decision = followPolicy.Evaluate(
            distance,
            travel.QuesterMounted,
            travel.QuesterFlying,
            mounted,
            mounting,
            flying,
            flightAvailable);

        if (decision.Phase == CoppeliaFollowPhase.Mount)
        {
            if (!IsMountingAllowed(out var mountFailure))
            {
                fallbackBlocker = $"mount unavailable: {mountFailure}";
            }
            else if (DateTime.UtcNow < nextMountActionUtc)
            {
                fallbackBlocker = "waiting to retry Mount Roulette";
            }
            else
            {
                nextMountActionUtc = DateTime.UtcNow.AddSeconds(2);
                PauseOwnedRoute();
                if (TryUseGeneralAction(MountRouletteGeneralActionId, out mountFailure))
                {
                    State = travel.QuesterMounted
                        ? "Mirroring the Quester with Mount Roulette"
                        : $"Mounting for catch-up beyond {CoppeliaFollowPolicy.MountCatchUpDistance:F0} yalms";
                    return;
                }

                fallbackBlocker = $"mount unavailable: {mountFailure}";
            }

            decision = decision with
            {
                Phase = CoppeliaFollowPhase.Follow,
                UseFlight = false,
                RouteRange = travel.QuesterMounted
                    ? CoppeliaFollowPolicy.MountedStopDistance
                    : CoppeliaFollowPolicy.OnFootStopDistance,
            };
        }
        else if (decision.Phase == CoppeliaFollowPhase.WaitForMount)
        {
            PauseOwnedRoute();
            State = travel.QuesterMounted
                ? "Waiting to mirror the Quester's mount"
                : distance > CoppeliaFollowPolicy.MountCatchUpDistance
                    ? "Waiting to mount for catch-up"
                    : "Waiting for mounting to finish before dismounting";
            return;
        }

        if (decision.Phase == CoppeliaFollowPhase.Land)
        {
            PauseOwnedRoute();
            if (DateTime.UtcNow < nextMountActionUtc)
            {
                State = travel.QuesterMounted
                    ? "Landing while remaining mounted"
                    : $"Landing within {CoppeliaFollowPolicy.OnFootResumeDistance:F0} yalms before dismounting";
                return;
            }

            nextMountActionUtc = DateTime.UtcNow.AddSeconds(2);
            if (TryUseGeneralAction(DismountGeneralActionId, out var landingFailure))
            {
                State = travel.QuesterMounted
                    ? "Landing while remaining mounted"
                    : $"Landing within {CoppeliaFollowPolicy.OnFootResumeDistance:F0} yalms before dismounting";
                return;
            }

            State = $"Landing unavailable: {landingFailure}";
            return;
        }

        if (decision.Phase == CoppeliaFollowPhase.Dismount)
        {
            PauseOwnedRoute();
            if (DateTime.UtcNow < nextMountActionUtc)
            {
                State = $"Waiting to dismount within {CoppeliaFollowPolicy.OnFootResumeDistance:F0} yalms";
                return;
            }

            nextMountActionUtc = DateTime.UtcNow.AddSeconds(2);
            if (TryUseGeneralAction(DismountGeneralActionId, out var dismountFailure))
            {
                State = $"Dismounting within {CoppeliaFollowPolicy.OnFootResumeDistance:F0} yalms";
                return;
            }

            State = $"Dismount unavailable: {dismountFailure}";
            return;
        }

        if (decision.Phase == CoppeliaFollowPhase.Follow && mounted && !decision.UseFlight)
            fallbackBlocker = $"flight unavailable: {flightFailure}";

        if (!TryGetRouteActivity(out var isPathfinding, out var isPathRunning))
        {
            State = "vnavmesh follow IPC failed";
            return;
        }

        var routeActivity = routePolicy.Observe(isPathfinding, isPathRunning, DateTime.UtcNow);
        flightPolicy.ObserveRouteActivity(isPathfinding || isPathRunning);
        if (routeActivity == CoppeliaRouteActivity.Rejected)
            flightPolicy.MarkProbeRejected();
        if (!decision.IsFollowing)
        {
            if (routePolicy.Pause(isPathRunning) == CoppeliaRouteInterruption.StopPath)
                InvokePathStop();
            State = BuildFollowState($"Close follow ({distance:F1} yalms)", fallbackBlocker);
            return;
        }

        if (routeActivity == CoppeliaRouteActivity.Owned)
        {
            State = BuildFollowState(
                decision.UseFlight
                    ? $"Flying at {distance:F1} yalms"
                    : $"Following on the ground at {distance:F1} yalms",
                fallbackBlocker);
            return;
        }

        if (routeActivity == CoppeliaRouteActivity.Other)
        {
            State = "Waiting for existing vnavmesh activity to clear";
            return;
        }

        if (routeActivity == CoppeliaRouteActivity.Rejected)
        {
            State = "vnavmesh rejected route startup; waiting for activity to clear or a new travel snapshot";
            return;
        }

        try
        {
            if (!navReady.InvokeFunc())
            {
                State = "Waiting for vnavmesh";
                return;
            }

            if (!routePolicy.CanStart(isPathfinding, isPathRunning))
                return;

            destination = routePolicy.LatestDestination;
            var routeLabel = decision.UseFlight ? "fly-to" : "ground follow";
            if (moveCloseTo.InvokeFunc(destination, decision.UseFlight, decision.RouteRange))
            {
                routePolicy.MarkStartupAccepted(DateTime.UtcNow);
                flightPolicy.MarkRouteQueued(decision.UseFlight);
                State = BuildFollowState(
                    $"Queueing {routeLabel} at {distance:F1} yalms",
                    fallbackBlocker);
                Plugin.Log.Information(
                    $"[Coppelia][QST] Queueing {routeLabel} to travel snapshot {routePolicy.LatestSequence} " +
                    $"with {decision.RouteRange:F0}-yalm tolerance.");
                return;
            }

            if (!TryGetRouteActivity(out isPathfinding, out isPathRunning))
            {
                isPathfinding = false;
                isPathRunning = false;
            }
            routePolicy.MarkStartupRejected(isPathfinding, isPathRunning);
            flightPolicy.MarkProbeRejected();
            State = $"vnavmesh rejected {routeLabel} route startup; waiting for activity to clear or a new travel snapshot";
            Plugin.Log.Warning($"[Coppelia][QST] vnavmesh rejected {routeLabel} route startup.");
        }
        catch (Exception ex)
        {
            routePolicy.MarkStartupRejected(pathfindInProgress: false, pathRunning: false);
            flightPolicy.MarkProbeRejected();
            State = "vnavmesh follow IPC failed; waiting for a new travel snapshot";
            Plugin.Log.Warning(ex, "[Coppelia][QST] vnavmesh route startup IPC failed.");
        }
    }

    public void Release()
    {
        ReleaseOwnedRoute();
        latestTravel = null;
        pendingExactTravel = null;
        lifestreamRequest = null;
        lifestreamPolicy.Reset();
        lastTravelSequence = 0;
        blockedTravelSequence = 0;
        blockedTravelState = string.Empty;
        actionHoldUntilUtc = DateTime.MinValue;
        nextMountActionUtc = DateTime.MinValue;
        followPolicy.Reset();
        flightPolicy.Reset();
        State = "Idle";
    }

    private static string BuildFollowState(string state, string blocker) =>
        string.IsNullOrWhiteSpace(blocker) ? state : $"{state}; safe ground fallback ({blocker})";

    private static bool IsMountingAllowed(out string blocker)
    {
        blocker = string.Empty;
        try
        {
            if (!Plugin.DataManager.GetExcelSheet<TerritoryType>()
                    .TryGetRow(Plugin.ClientState.TerritoryType, out var territory) ||
                !territory.Mount)
            {
                blocker = $"territory {Plugin.ClientState.TerritoryType} does not allow mounting";
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            blocker = $"territory mount check failed: {ex.Message}";
            return false;
        }
    }

    private static unsafe CoppeliaFlightEligibility GetFlightEligibility(out string blocker)
    {
        blocker = string.Empty;
        try
        {
            if (!Plugin.DataManager.GetExcelSheet<TerritoryType>()
                    .TryGetRow(Plugin.ClientState.TerritoryType, out var territory) ||
                !territory.Mount)
            {
                blocker = $"flight is not available in territory {Plugin.ClientState.TerritoryType}";
                return CoppeliaFlightEligibility.Locked;
            }

            var hasStandardSet = territory.AetherCurrentCompFlgSet.IsValid &&
                                 territory.AetherCurrentCompFlgSet.RowId != 0;
            if (hasStandardSet)
            {
                var playerState = PlayerState.Instance();
                var complete = playerState != null &&
                               playerState->IsAetherCurrentZoneComplete((byte)territory.AetherCurrentCompFlgSet.RowId);
                if (!complete)
                    blocker = "the helper has not unlocked flight in this territory";
                return complete
                    ? CoppeliaFlightEligibility.Unlocked
                    : CoppeliaFlightEligibility.Locked;
            }

            if (territory.ExVersion.RowId == 0)
            {
                blocker = "probing ARR flight availability";
                return CoppeliaFlightEligibility.UnknownButMountable;
            }

            blocker = $"flight is not available in territory {Plugin.ClientState.TerritoryType}";
            return CoppeliaFlightEligibility.Locked;
        }
        catch (Exception ex)
        {
            blocker = $"flight check failed: {ex.Message}";
            return CoppeliaFlightEligibility.Locked;
        }
    }

    private static unsafe bool TryUseGeneralAction(uint actionId, out string blocker)
    {
        blocker = string.Empty;
        try
        {
            var actionManager = ActionManager.Instance();
            if (actionManager == null)
            {
                blocker = "ActionManager is unavailable";
                return false;
            }

            var status = actionManager->GetActionStatus(ActionType.GeneralAction, actionId);
            if (actionId == MountRouletteGeneralActionId && status != 0)
            {
                blocker = $"general action {actionId} is unavailable (status {status})";
                return false;
            }

            if (!actionManager->UseAction(ActionType.GeneralAction, actionId))
            {
                blocker = $"general action {actionId} was rejected";
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            blocker = $"general action {actionId} failed: {ex.Message}";
            return false;
        }
    }

    private bool ObserveLifestreamRequest(IPlayerCharacter localPlayer, CoppeliaQstCommand travel)
    {
        if (lifestreamRequest == null)
            return false;

        var targetReached = lifestreamRequest.IsWorld
            ? localPlayer.CurrentWorld.RowId == lifestreamRequest.WorldId
            : Plugin.ClientState.TerritoryType == lifestreamRequest.TerritoryId;
        if (targetReached)
        {
            lifestreamPolicy.Observe(busy: false, targetReached: true, utcNow: DateTime.UtcNow);
            lifestreamPolicy.Reset();
            lifestreamRequest = null;
            return false;
        }

        if (!TryGetLifestreamBusy(out var busy))
        {
            lifestreamPolicy.Fail();
            lifestreamRequest = lifestreamRequest with
            {
                FailureState = "Lifestream travel status IPC failed",
            };
        }
        else
        {
            lifestreamPolicy.Observe(busy, targetReached: false, utcNow: DateTime.UtcNow);
        }

        if (lifestreamPolicy.Activity is CoppeliaLifestreamActivity.WaitingForBusy or
            CoppeliaLifestreamActivity.Busy)
        {
            State = lifestreamRequest.ActiveState;
            return true;
        }

        if (lifestreamPolicy.Activity == CoppeliaLifestreamActivity.Failed)
        {
            if (lifestreamPolicy.CanReplaceWith(travel.TravelSequence) && !busy)
            {
                lifestreamPolicy.Reset();
                lifestreamRequest = null;
                return false;
            }

            State = $"{lifestreamRequest.FailureState}; waiting for a newer travel snapshot";
            return true;
        }

        lifestreamPolicy.Reset();
        lifestreamRequest = null;
        return false;
    }

    private void BeginLifestreamRequest(long sequence, LifestreamRequest request, Func<bool> start)
    {
        PauseOwnedRoute();
        if (!TryGetLifestreamBusy(out var busy))
        {
            BlockTravel(sequence, "Lifestream travel status IPC failed; waiting for a newer travel snapshot");
            return;
        }
        if (busy)
        {
            State = "Waiting for existing Lifestream travel to finish";
            return;
        }

        bool accepted;
        try
        {
            accepted = start();
        }
        catch (Exception)
        {
            accepted = false;
            request = request with { FailureState = "Lifestream travel IPC failed" };
        }

        if (!accepted && !string.Equals(request.FailureState, "Lifestream travel IPC failed", StringComparison.Ordinal))
            request = request with { FailureState = $"Lifestream rejected the request: {request.FailureState}" };

        lifestreamRequest = request;
        lifestreamPolicy.Begin(sequence, accepted, DateTime.UtcNow);
        State = accepted
            ? request.ActiveState
            : $"{request.FailureState}; waiting for a newer travel snapshot";
    }

    private bool TryGetLifestreamBusy(out bool busy)
    {
        busy = false;
        try
        {
            busy = lifestreamBusy.InvokeFunc();
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private void BlockTravel(long sequence, string state)
    {
        blockedTravelSequence = sequence;
        blockedTravelState = state;
        State = state;
    }

    private static unsafe bool TryResolveTeleport(
        CoppeliaQstCommand travel,
        out uint aetheryteId,
        out byte subIndex,
        out string name,
        out string blocker)
    {
        aetheryteId = 0;
        subIndex = 0;
        name = string.Empty;
        blocker = string.Empty;

        if (travel.AetheryteId is { } exactId)
        {
            if (UIState.Instance() == null || !UIState.Instance()->IsAetheryteUnlocked(exactId))
            {
                blocker = "The exact Quester teleport destination is not unlocked on this helper.";
                return false;
            }

            aetheryteId = exactId;
            subIndex = travel.AetheryteSubIndex ?? 0;
            name = string.IsNullOrWhiteSpace(travel.AetheryteName) ? $"aetheryte {exactId}" : travel.AetheryteName;
            return true;
        }

        if (UIState.Instance() == null)
        {
            blocker = "Aetheryte unlock state is unavailable.";
            return false;
        }

        var target = new Vector3(travel.X, travel.Y, travel.Z);
        var candidates = Plugin.DataManager.GetExcelSheet<Aetheryte>()
            .Where(aetheryte => aetheryte.IsAetheryte &&
                                aetheryte.Territory.RowId == travel.TerritoryId)
            .Select(aetheryte =>
            {
                var level = aetheryte.Level[0].ValueNullable;
                var position = level == null
                    ? Vector3.Zero
                    : new Vector3(level.Value.X, level.Value.Y, level.Value.Z);
                return new
                {
                    Aetheryte = aetheryte,
                    Position = position,
                    HasPosition = level != null,
                    Unlocked = UIState.Instance()->IsAetheryteUnlocked(aetheryte.RowId),
                };
            })
            .Where(candidate => candidate.HasPosition)
            .ToArray();
        var nearestCandidate = CoppeliaAetherytePolicy.SelectNearest(
            target,
            candidates.Select(candidate => new CoppeliaAetheryteCandidate(
                candidate.Aetheryte.RowId,
                candidate.Position,
                candidate.Unlocked)));
        if (nearestCandidate == null)
        {
            blocker = "No unlocked aetheryte is available in the Quester's territory.";
            return false;
        }

        var nearest = candidates.First(candidate => candidate.Aetheryte.RowId == nearestCandidate.Id).Aetheryte;
        aetheryteId = nearest.RowId;
        name = nearest.PlaceName.ValueNullable?.Name.ExtractText() ?? $"aetheryte {nearest.RowId}";
        return true;
    }

    private static unsafe bool TryResolvePendingExactTeleport(
        CoppeliaQstCommand exactTravel,
        CoppeliaQstCommand latestTravel,
        out uint territoryId,
        out uint aetheryteId,
        out byte subIndex,
        out string name,
        out string blocker)
    {
        territoryId = 0;
        aetheryteId = 0;
        subIndex = 0;
        name = string.Empty;
        blocker = string.Empty;

        if (exactTravel.AetheryteId is not { } exactId ||
            !Plugin.DataManager.GetExcelSheet<Aetheryte>().TryGetRow(exactId, out var exactAetheryte))
        {
            blocker = "The accepted Quester teleport destination is unavailable.";
            return false;
        }

        territoryId = exactAetheryte.Territory.RowId;
        if (UIState.Instance() != null && UIState.Instance()->IsAetheryteUnlocked(exactId))
        {
            aetheryteId = exactId;
            subIndex = exactTravel.AetheryteSubIndex ?? 0;
            name = string.IsNullOrWhiteSpace(exactTravel.AetheryteName)
                ? exactAetheryte.PlaceName.ValueNullable?.Name.ExtractText() ?? $"aetheryte {exactId}"
                : exactTravel.AetheryteName;
            return true;
        }

        if (latestTravel.TravelSequence <= exactTravel.TravelSequence ||
            latestTravel.TerritoryId != territoryId)
        {
            blocker = "Waiting for the Quester's destination-territory position for nearest-aetheryte fallback.";
            return false;
        }

        var fallbackTravel = latestTravel with
        {
            AetheryteId = null,
            AetheryteSubIndex = null,
            AetheryteName = null,
        };
        return TryResolveTeleport(fallbackTravel, out aetheryteId, out subIndex, out name, out blocker);
    }

    private void Stop(string state)
    {
        PauseOwnedRoute();
        State = state;
    }

    private bool TryGetRouteActivity(out bool isPathfinding, out bool isPathRunning)
    {
        isPathfinding = false;
        isPathRunning = false;
        try
        {
            isPathfinding = pathfindInProgress.InvokeFunc();
            isPathRunning = pathRunning.InvokeFunc();
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private void PauseOwnedRoute()
    {
        if (!routePolicy.OwnsRoute)
            return;

        var activityKnown = TryGetRouteActivity(out var isPathfinding, out var isPathRunning);
        if (activityKnown)
            routePolicy.Observe(isPathfinding, isPathRunning, DateTime.UtcNow);
        if (routePolicy.Pause(activityKnown ? isPathRunning : true) == CoppeliaRouteInterruption.StopPath)
            InvokePathStop();
    }

    private void ReleaseOwnedRoute()
    {
        var activityKnown = TryGetRouteActivity(out var isPathfinding, out var isPathRunning);
        if (activityKnown)
            routePolicy.Observe(isPathfinding, isPathRunning, DateTime.UtcNow);
        var interruption = routePolicy.Release(
            activityKnown && isPathfinding,
            activityKnown ? isPathRunning : routePolicy.OwnsRoute);
        if (interruption == CoppeliaRouteInterruption.None)
            return;

        InvokePathStop();
        if (interruption != CoppeliaRouteInterruption.StopPathAndCancelPending)
            return;

        try
        {
            cancelAll.InvokeAction();
        }
        catch (Exception)
        {
        }
    }

    private void InvokePathStop()
    {
        try
        {
            pathStop.InvokeAction();
        }
        catch (Exception)
        {
        }
    }

    private sealed record LifestreamRequest(
        bool IsWorld,
        ushort WorldId,
        uint TerritoryId,
        string ActiveState,
        string FailureState);
}

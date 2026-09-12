using System.Numerics;
using Coppelia.Models;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Plugin.Ipc;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using Lumina.Excel.Sheets;

namespace Coppelia.Services;

internal sealed partial class CoppeliaTravelService
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
    private readonly ICallGateSubscriber<List<Vector3>, bool, object> moveDirect;
    private readonly ICallGateSubscriber<object> pathStop;
    private readonly ICallGateSubscriber<object> cancelAll;
    private readonly Configuration configuration;
    private readonly AetherytePositionDatabase aetherytePositionDatabase;
    private readonly MapLocationDatabase mapLocationDatabase;
    private readonly CoppeliaFollowPolicy followPolicy = new();
    private readonly CoppeliaRoutePolicy routePolicy = new();
    private readonly CoppeliaRoutePolicy lineOfSightRescueRoutePolicy = new();
    private readonly CoppeliaFlightPolicy flightPolicy = new();
    private readonly CoppeliaLifestreamRequestPolicy lifestreamPolicy = new();
    private readonly CoppeliaTerritoryHandoffPolicy territoryHandoffPolicy = new();
    private readonly CoppeliaTravelSequencePolicy travelSequencePolicy = new();

    private CoppeliaQstCommand? latestTravel;
    private CoppeliaQstCommand? pendingExactTravel;
    private LifestreamRequest? lifestreamRequest;
    private DateTime actionHoldUntilUtc = DateTime.MinValue;
    private DateTime nextMountActionUtc = DateTime.MinValue;
    private bool forwardProbeOwnsPath;
    private long pendingPriorityDestinationSequence;
    private long lastExistingLifestreamBusySequence;
    private bool lifestreamObservedLoading;
    private PendingAetheryteArrival? pendingAetheryteArrival;
    private long lineOfSightRescueSequence;
    private ulong lineOfSightRescueTargetId;
    private Vector3 lineOfSightRescueDestination;
    private bool lineOfSightRescueHasDestination;
    private bool lineOfSightRescueOwnStopPending;
    private Func<uint?> instanceReader = ReadCurrentInstanceId;
    private Func<uint, bool> isMainAetheryte = IsMainAetheryte;
    private delegate bool TeleportListReader(out IReadOnlyList<CoppeliaTeleportListEntry> entries, out string blocker);
    private TeleportListReader teleportListReader = TryGetTeleportListSnapshot;
    private DateTime nextInstanceAttemptUtc;
    private bool instanceSwitchPending;
    private (uint World, uint Territory, uint Map, uint? Instance)? approachLocation;

    public uint? CurrentInstanceId => instanceReader();

    private static unsafe uint? ReadCurrentInstanceId()
    {
        if (!Plugin.ClientState.IsLoggedIn || !Plugin.PlayerState.IsLoaded || Plugin.ObjectTable.LocalPlayer == null ||
            Plugin.ClientState.TerritoryType == 0 || IsBetweenAreas())
            return null;
        try
        {
            var state = UIState.Instance();
            return state == null ? null : state->PublicInstance.InstanceId;
        }
        catch (Exception) { return null; }
    }

    private static bool IsMainAetheryte(uint baseId)
    {
        try
        {
            return Plugin.DataManager.GetExcelSheet<Aetheryte>().TryGetRow(baseId, out var aetheryte) && aetheryte.IsAetheryte;
        }
        catch (Exception) { return false; }
    }

    public CoppeliaTravelService(
        Configuration configuration,
        AetherytePositionDatabase aetherytePositionDatabase,
        MapLocationDatabase mapLocationDatabase)
    {
        this.configuration = configuration;
        this.aetherytePositionDatabase = aetherytePositionDatabase;
        this.mapLocationDatabase = mapLocationDatabase;
        lifestreamBusy = Plugin.PluginInterface.GetIpcSubscriber<bool>("Lifestream.IsBusy");
        changeWorld = Plugin.PluginInterface.GetIpcSubscriber<uint, bool>("Lifestream.ChangeWorldById");
        teleport = Plugin.PluginInterface.GetIpcSubscriber<uint, byte, bool>("Lifestream.Teleport");
        navReady = Plugin.PluginInterface.GetIpcSubscriber<bool>("vnavmesh.Nav.IsReady");
        pathRunning = Plugin.PluginInterface.GetIpcSubscriber<bool>("vnavmesh.Path.IsRunning");
        pathfindInProgress = Plugin.PluginInterface.GetIpcSubscriber<bool>("vnavmesh.SimpleMove.PathfindInProgress");
        moveCloseTo = Plugin.PluginInterface.GetIpcSubscriber<Vector3, bool, float, bool>("vnavmesh.SimpleMove.PathfindAndMoveCloseTo");
        moveDirect = Plugin.PluginInterface.GetIpcSubscriber<List<Vector3>, bool, object>("vnavmesh.Path.MoveTo");
        pathStop = Plugin.PluginInterface.GetIpcSubscriber<object>("vnavmesh.Path.Stop");
        cancelAll = Plugin.PluginInterface.GetIpcSubscriber<object>("vnavmesh.Nav.PathfindCancelAll");
    }

    public string State { get; private set; } = "Idle";
    public string LineOfSightRescueState { get; private set; } = "Inactive";

    public string UpdateLineOfSightRescue(ulong targetGameObjectId, Vector3 liveDestination)
    {
        if (HealRiderActive || travelSequencePolicy.WaitingForFreshSnapshot || hinterlandsRoute != null)
            return SetLineOfSightRescueState("LOS movement held for HealBot transport");
        var travel = latestTravel;
        var localPlayer = Plugin.ObjectTable.LocalPlayer;
        if (travel != null && localPlayer != null &&
            (travel.QuesterCurrentWorldId != localPlayer.CurrentWorld.RowId ||
             travel.TerritoryId != Plugin.ClientState.TerritoryType))
        {
            var hadRescueRoute = lineOfSightRescueHasDestination || lineOfSightRescueRoutePolicy.OwnsRoute;
            var state = YieldLineOfSightRescue("LOS blocked; paired travel resolving remote location");
            if (hadRescueRoute)
            {
                Plugin.Log.Information(
                    $"[Coppelia][HealBot] LOS rescue released to paired travel because the target is remote " +
                    $"(world {travel.QuesterCurrentWorldId}/{localPlayer.CurrentWorld.RowId}, " +
                    $"territory {travel.TerritoryId}/{Plugin.ClientState.TerritoryType}).");
            }
            return state;
        }

        if (!TryGetRouteActivity(out var isPathfinding, out var isPathRunning))
            return SetLineOfSightRescueState("LOS blocked; moving to 1y");

        if (routePolicy.OwnsRoute)
        {
            routePolicy.Observe(isPathfinding, isPathRunning, DateTime.UtcNow);
            if (routePolicy.OwnsRoute)
                return YieldLineOfSightRescue("LOS blocked; paired travel active");
        }

        var destinationChanged = targetGameObjectId != lineOfSightRescueTargetId ||
                                 !lineOfSightRescueHasDestination ||
                                 Vector3.DistanceSquared(liveDestination, lineOfSightRescueDestination) > 1f;
        if (destinationChanged)
        {
            StopLineOfSightRescueRoute();
            lineOfSightRescueTargetId = targetGameObjectId;
            lineOfSightRescueDestination = liveDestination;
            lineOfSightRescueHasDestination = true;
            lineOfSightRescueRoutePolicy.AcceptSnapshot(++lineOfSightRescueSequence, liveDestination);
        }

        if (lineOfSightRescueRoutePolicy.OwnsRoute)
        {
            var rescueActivity = lineOfSightRescueRoutePolicy.Observe(isPathfinding, isPathRunning, DateTime.UtcNow);
            if (rescueActivity == CoppeliaRouteActivity.Owned)
                return SetLineOfSightRescueState("LOS blocked; moving to 1y");
        }

        if (isPathfinding || isPathRunning)
        {
            if (lineOfSightRescueOwnStopPending)
                return SetLineOfSightRescueState("LOS blocked; moving to 1y");

            return SetLineOfSightRescueState("LOS blocked; waiting for current movement owner");
        }

        lineOfSightRescueOwnStopPending = false;
        try
        {
            if (!navReady.HasFunction || !moveCloseTo.HasFunction || !navReady.InvokeFunc())
                return SetLineOfSightRescueState("LOS blocked; moving to 1y");

            if (!lineOfSightRescueRoutePolicy.CanStart(isPathfinding, isPathRunning))
                return SetLineOfSightRescueState("LOS blocked; moving to 1y");

            if (moveCloseTo.InvokeFunc(lineOfSightRescueDestination, false, 1f))
            {
                lineOfSightRescueRoutePolicy.MarkStartupAccepted(DateTime.UtcNow);
                Plugin.Log.Information(
                    "[Coppelia][HealBot] Queueing owned LOS rescue to target {TargetId} with 1-yalm tolerance.",
                    targetGameObjectId);
                return SetLineOfSightRescueState("LOS blocked; moving to 1y");
            }

            if (!TryGetRouteActivity(out isPathfinding, out isPathRunning))
            {
                isPathfinding = false;
                isPathRunning = false;
            }
            lineOfSightRescueRoutePolicy.MarkStartupRejected(isPathfinding, isPathRunning);
        }
        catch (Exception ex)
        {
            lineOfSightRescueRoutePolicy.MarkStartupRejected(pathfindInProgress: false, pathRunning: false);
            Plugin.Log.Warning(ex, "[Coppelia][HealBot] LOS rescue vnavmesh IPC failed.");
        }

        return SetLineOfSightRescueState("LOS blocked; moving to 1y");
    }

    public void ClearLineOfSightRescue()
    {
        if (!lineOfSightRescueHasDestination && !lineOfSightRescueRoutePolicy.OwnsRoute)
        {
            LineOfSightRescueState = "Inactive";
            return;
        }

        StopLineOfSightRescueRoute();
        lineOfSightRescueTargetId = 0;
        lineOfSightRescueDestination = default;
        lineOfSightRescueHasDestination = false;
        lineOfSightRescueOwnStopPending = false;
        LineOfSightRescueState = "Inactive";
        RearmLatestTravelRoute();
    }

    public string YieldLineOfSightRescueToTravel() =>
        YieldLineOfSightRescue(
            latestTravel == null
                ? "LOS blocked; target outside rescue range"
                : "LOS blocked; paired travel active");

    public void PauseForAction()
    {
        if (HealRiderActive)
            return;
        actionHoldUntilUtc = DateTime.UtcNow.AddSeconds(2);
        PauseOwnedRoute();
        StopForwardProbePath();
        State = "Paused for a HealBot action";
        Plugin.Log.Information("[Coppelia][QST] Accepted action entered the two-second travel hold.");
    }

    public (bool Ready, string Blocker) EvaluateReadiness()
    {
        if (!lifestreamBusy.HasFunction || !changeWorld.HasFunction || !teleport.HasFunction)
            return (false, "Required Lifestream travel IPC is unavailable.");
        if (!navReady.HasFunction || !pathRunning.HasFunction || !pathfindInProgress.HasFunction || !moveCloseTo.HasFunction ||
            !moveDirect.HasAction ||
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
        if (command.TravelSequence <= travelSequencePolicy.LastAcceptedSequence)
            return new CoppeliaQstCommandResponse(true, "Travel snapshot was already accepted; arrival is not implied.");
        if (command.QuesterCurrentWorldId == 0 || command.TerritoryId == 0)
            return new CoppeliaQstCommandResponse(false, "Travel update is missing world or territory metadata.");

        var previousTravel = latestTravel;
        if (hinterlandsRoute != null &&
            (command.TerritoryId != 399 || command.AetheryteId.HasValue ||
             command.QuesterCurrentWorldId != previousTravel?.QuesterCurrentWorldId))
            ClearHinterlandsRoute();
        var worldChanged = previousTravel != null &&
                           command.QuesterCurrentWorldId != previousTravel.QuesterCurrentWorldId;
        var instanceChanged = previousTravel != null && command.InstanceId != previousTravel.InstanceId;
        if (worldChanged || instanceChanged || previousTravel?.TerritoryId != command.TerritoryId)
            followPolicy.ResetApproach();
        if (command.AetheryteId.HasValue || worldChanged || instanceChanged)
            ResetTerritoryHandoff();
        if (instanceChanged)
        {
            ReleaseOwnedRoute();
            // Preserve an in-flight command's quiet period while following the newest target.
            if (!instanceSwitchPending) nextInstanceAttemptUtc = DateTime.MinValue;
        }

        travelSequencePolicy.TryAccept(command.TravelSequence);

        latestTravel = command;
        routePolicy.AcceptSnapshot(command.TravelSequence, new Vector3(command.X, command.Y, command.Z));
        if (command.AetheryteId.HasValue)
            pendingExactTravel = command;

        var localPlayer = Plugin.ObjectTable.LocalPlayer;
        var handoffArmed = territoryHandoffPolicy.TryArm(
            previousTravel != null,
            command.AetheryteId.HasValue,
            worldChanged ||
            localPlayer != null && localPlayer.CurrentWorld.RowId != command.QuesterCurrentWorldId,
            previousTravel?.TerritoryId ?? 0,
            command.TerritoryId);
        if (handoffArmed && pendingExactTravel != null)
        {
            Plugin.Log.Information(
                $"[Coppelia][QST] Discarded older exact teleport snapshot {pendingExactTravel.TravelSequence}; " +
                $"newer physical territory handoff {command.TravelSequence} targets territory {command.TerritoryId}.");
            pendingExactTravel = null;
        }

        var prioritySnapshot = previousTravel == null ||
                               command.AetheryteId.HasValue ||
                               worldChanged ||
                               instanceChanged ||
                               command.TerritoryId != previousTravel.TerritoryId;
        if (prioritySnapshot)
        {
            pendingPriorityDestinationSequence = command.TravelSequence;
            Plugin.Log.Information(
                $"[Coppelia][QST] Destination snapshot {command.TravelSequence} is pending " +
                $"(world {command.QuesterCurrentWorldId}, territory {command.TerritoryId}" +
                (command.AetheryteId.HasValue ? $", exact aetheryte {command.AetheryteId.Value}" : string.Empty) +
                ").");
        }

        return new CoppeliaQstCommandResponse(true, "Travel snapshot accepted; destination remains pending.");
    }

    public void Update()
    {
        var observationPlayer = Plugin.ObjectTable.LocalPlayer;
        var location = observationPlayer == null || IsBetweenAreas()
            ? ((uint World, uint Territory, uint Map, uint? Instance)?)null
            : (observationPlayer.CurrentWorld.RowId, Plugin.ClientState.TerritoryType, Plugin.ClientState.MapId, CurrentInstanceId);
        if (location == null || location != approachLocation || HealRiderActive ||
            latestTravel == null || Plugin.Condition[ConditionFlag.BoundByDuty] || rideMountsSuspended)
            followPolicy.ResetApproach();
        approachLocation = location;
        if (UpdateHealRider())
            return;
        if (rideMode != null && rideMountsSuspended)
        {
            PauseOwnedRoute();
            State = "HealRider following suspended for duty ownership";
            return;
        }
        if (ordinaryMountDeadline.HasValue && RideNow >= ordinaryMountDeadline.Value)
        {
            ordinaryMountBlocker = "Selected passenger mount preparation exceeded the pickup allowance.";
            ordinaryMountDeadline = null;
        }
        if (latestTravel == null)
            return;

        if (instanceSwitchPending)
        {
            PauseOwnedRoute();
            if (IsBetweenAreas() || !TryGetLifestreamBusy(out var instanceBusy) || instanceBusy)
            {
                nextInstanceAttemptUtc = RideNow.AddSeconds(5);
                State = "Waiting for Lifestream instance switching and loading to finish";
                return;
            }
            if (RideNow < nextInstanceAttemptUtc)
            {
                State = "Verifying the Helper public instance after /li";
                return;
            }
            instanceSwitchPending = false;
        }

        if (travelSequencePolicy.WaitingForFreshSnapshot)
        {
            State = "Waiting for a fresh Quester travel snapshot after HealRider";
            return;
        }

        if (Plugin.Condition[ConditionFlag.BoundByDuty])
        {
            ClearHinterlandsRoute();
            ResetTerritoryHandoff();
            Stop("Suspended inside duty");
            return;
        }

        var travel = latestTravel;
        if (hinterlandsRoute != null && UpdateHinterlandsRoute(travel))
            return;
        if (lifestreamRequest != null && IsBetweenAreas())
            lifestreamObservedLoading = true;

        var localPlayer = Plugin.ObjectTable.LocalPlayer;
        if (localPlayer == null)
        {
            if (territoryHandoffPolicy.IsActive &&
                (IsBetweenAreas() || Plugin.ClientState.TerritoryType != territoryHandoffPolicy.SourceTerritoryId))
            {
                HandleTerritoryHandoff(null, travel);
                return;
            }

            State = "Waiting for the helper character";
            return;
        }

        if (lineOfSightRescueHasDestination &&
            (travel.QuesterCurrentWorldId != localPlayer.CurrentWorld.RowId ||
             travel.TerritoryId != Plugin.ClientState.TerritoryType))
        {
            YieldLineOfSightRescue("LOS blocked; paired travel resolving remote location");
            Plugin.Log.Information(
                $"[Coppelia][HealBot] LOS rescue released to paired travel because the target is remote " +
                $"(world {travel.QuesterCurrentWorldId}/{localPlayer.CurrentWorld.RowId}, " +
                $"territory {travel.TerritoryId}/{Plugin.ClientState.TerritoryType}).");
        }

        if (localPlayer is IBattleChara battleChara && battleChara.IsCasting)
        {
            actionHoldUntilUtc = DateTime.UtcNow.AddMilliseconds(250);
            PauseOwnedRoute();
            StopForwardProbePath();
            State = $"Paused for cast {battleChara.CastActionId}";
            return;
        }

        TryRecordPendingAetheryteArrival(localPlayer);

        if (hinterlandsRoute == null && lifestreamRequest == null && pendingExactTravel == null &&
            !territoryHandoffPolicy.IsActive && !travelSequencePolicy.IsBlocked(travel.TravelSequence) &&
            travel.TerritoryId == 399 && Plugin.ClientState.TerritoryType != 399 && !travel.AetheryteId.HasValue)
        {
            if (!CanFlyInTerritory(399))
                BlockTravel(travel.TravelSequence, "Dravanian Hinterlands travel requires flying on this Helper");
            else
            {
                hinterlandsRoute = new HinterlandsRoute(DateTime.UtcNow.AddMinutes(2));
                UpdateHinterlandsRoute(travel);
            }
            return;
        }

        var currentWorld = (ushort)localPlayer.CurrentWorld.RowId;
        if (ObserveLifestreamRequest(localPlayer, travel))
            return;

        if (travelSequencePolicy.IsBlocked(travel.TravelSequence))
        {
            State = travelSequencePolicy.BlockedState;
            return;
        }

        if (currentWorld != travel.QuesterCurrentWorldId)
        {
            ResetTerritoryHandoff();
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
            ResetTerritoryHandoff();
            PauseOwnedRoute();
            if (!TryResolvePendingExactTeleport(
                    pendingExactTravel,
                    travel,
                    out var exactTerritory,
                    out var exactAetheryteId,
                    out var exactSubIndex,
                    out var exactName,
                    out var exactUsedFallback,
                    out var staleExactIntent,
                    out var exactTeleportListUnavailable,
                    out var exactBlocker))
            {
                if (staleExactIntent)
                {
                    Plugin.Log.Information(
                        $"[Coppelia][QST] Discarded exact teleport snapshot {pendingExactTravel.TravelSequence}; " +
                        $"newer snapshot {travel.TravelSequence} targets territory {travel.TerritoryId}.");
                    pendingExactTravel = null;
                }
                else if (exactTeleportListUnavailable)
                {
                    State = exactBlocker;
                    return;
                }
                else
                {
                    BlockTravel(travel.TravelSequence, exactBlocker);
                    return;
                }
            }
            else
            {
                var startResult = BeginLifestreamRequest(
                    travel.TravelSequence,
                    new LifestreamRequest(
                        IsWorld: false,
                        WorldId: 0,
                        TerritoryId: exactTerritory,
                        ActiveState: $"Teleporting to {exactName}",
                        FailureState: $"Teleport to {exactName} did not reach territory {exactTerritory}",
                        RequireBusyCompletion: Plugin.ClientState.TerritoryType == exactTerritory,
                        AetheryteId: exactAetheryteId,
                        AetheryteName: exactName),
                    () => teleport.InvokeFunc(exactAetheryteId, exactSubIndex));
                if (startResult == LifestreamStartResult.Attempted)
                {
                    pendingExactTravel = null;
                    Plugin.Log.Information(
                        exactUsedFallback
                            ? $"[Coppelia][QST] Selected fallback aetheryte {exactName} ({exactAetheryteId}) in territory {exactTerritory} for the accepted exact teleport."
                            : $"[Coppelia][QST] Selected exact aetheryte {exactName} ({exactAetheryteId}) in territory {exactTerritory}.");
                }
                return;
            }
        }

        if (territoryHandoffPolicy.IsActive && HandleTerritoryHandoff(localPlayer, travel))
            return;

        if (travel.TerritoryId == 399 && Plugin.ClientState.TerritoryType != 399 &&
            !travel.AetheryteId.HasValue)
        {
            if (!CanFlyInTerritory(399))
                BlockTravel(travel.TravelSequence, "Dravanian Hinterlands travel requires flying on this Helper");
            else
            {
                hinterlandsRoute = new HinterlandsRoute(DateTime.UtcNow.AddMinutes(2));
                UpdateHinterlandsRoute(travel);
            }
            return;
        }

        if (Plugin.ClientState.TerritoryType != travel.TerritoryId)
        {
            TeleportToQuester(travel);
            return;
        }

        if (UpdateInstanceRendezvous(localPlayer, travel))
            return;

        MarkPriorityDestinationReached(travel, "the helper is loaded in the requested world, territory, and instance");

        // Remote travel and cleanup gates above still use the authenticated snapshot.
        // A visible exact pair also proves the local instance for legacy Newb following.
        var visibleQuester = !IsBetweenAreas()
            ? Plugin.ObjectTable.OfType<IPlayerCharacter>().FirstOrDefault(player =>
                player.Name.ToString() == travel.QuesterName &&
                player.HomeWorld.RowId == travel.QuesterWorldId &&
                player.CurrentWorld.RowId == travel.QuesterCurrentWorldId)
            : null;
        var destination = visibleQuester?.Position ?? new Vector3(travel.X, travel.Y, travel.Z);
        routePolicy.UpdateVisibleDestination(destination);
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
            flightAvailable,
            keepPassengerMount: rideMode != null && !rideMountsSuspended && string.IsNullOrEmpty(ordinaryMountBlocker));

        var approachStalled = false;
        if (location.HasValue && (decision.IsFollowing || decision.Phase == CoppeliaFollowPhase.Land))
            approachStalled = followPolicy.ObserveApproach(localPlayer.Position, RideNow);
        else
            followPolicy.ResetApproach();

        if (IsBetweenAreas()) return;
        if (UpdateOrdinaryRideMount(decision.Phase == CoppeliaFollowPhase.Mount))
            return;
        if (DateTime.UtcNow < actionHoldUntilUtc)
        {
            PauseOwnedRoute();
            State = "Paused for a HealBot action";
            return;
        }

        if (approachStalled)
        {
            if (!TryGetRouteActivity(out var recoveryPathfinding, out var recoveryPathRunning) ||
                !routePolicy.OwnsRoute && (recoveryPathfinding || recoveryPathRunning))
            {
                State = "Waiting for existing vnavmesh activity to clear";
                return;
            }
            ReleaseOwnedRoute();
            routePolicy.AcceptSnapshot(travel.TravelSequence, destination);
            TeleportToQuester(travel);
            return;
        }

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
                if (TryUseGeneralAction(MountRouletteGeneralActionId, out mountFailure))
                {
                    PauseForAction();
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
            if (!TryGetRouteActivity(out var landingPathfinding, out var landingPathRunning) ||
                !followPolicy.CanLand(routePolicy.Observe(landingPathfinding, landingPathRunning, RideNow), RideNow))
            {
                State = "Following the active route before landing";
                return;
            }
            if (DateTime.UtcNow < nextMountActionUtc)
            {
                State = travel.QuesterMounted
                    ? "Landing while remaining mounted"
                    : $"Landing within {CoppeliaFollowPolicy.OnFootDismountDistance:F0} yalms before dismounting";
                return;
            }

            nextMountActionUtc = DateTime.UtcNow.AddSeconds(2);
            if (TryUseGeneralAction(DismountGeneralActionId, out var landingFailure))
            {
                PauseForAction();
                State = travel.QuesterMounted
                    ? "Landing while remaining mounted"
                    : $"Landing within {CoppeliaFollowPolicy.OnFootDismountDistance:F0} yalms before dismounting";
                return;
            }

            State = $"Landing unavailable: {landingFailure}";
            return;
        }

        if (decision.Phase == CoppeliaFollowPhase.Dismount)
        {
            if (DateTime.UtcNow < nextMountActionUtc)
            {
                State = $"Waiting to dismount within {CoppeliaFollowPolicy.OnFootDismountDistance:F0} yalms";
                return;
            }

            nextMountActionUtc = DateTime.UtcNow.AddSeconds(2);
            if (TryUseGeneralAction(DismountGeneralActionId, out var dismountFailure))
            {
                PauseForAction();
                State = $"Dismounting within {CoppeliaFollowPolicy.OnFootDismountDistance:F0} yalms";
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

        var utcNow = DateTime.UtcNow;
        var lineOfSightRescueActive = lineOfSightRescueRoutePolicy.OwnsRoute &&
                                      lineOfSightRescueRoutePolicy.Observe(
                                          isPathfinding,
                                          isPathRunning,
                                          utcNow) == CoppeliaRouteActivity.Owned;
        var routeActivity = routePolicy.Observe(isPathfinding, isPathRunning, utcNow);
        if (lineOfSightRescueActive && !routePolicy.OwnsRoute)
            routeActivity = CoppeliaRouteActivity.Other;
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
            var refreshInterruption = routePolicy.RefreshStaleDestination(
                isPathfinding,
                isPathRunning,
                utcNow);
            if (refreshInterruption != CoppeliaRouteInterruption.None)
            {
                InterruptOwnedRoute(refreshInterruption);
                State = BuildFollowState("Refreshing the latest Quester destination", fallbackBlocker);
                return;
            }

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
            if (TryProbeFailedFollow(localPlayer, travel))
                return;
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

            if (!routePolicy.CanStart(isPathfinding, isPathRunning, resumeCompletedRoute: true))
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
            if (TryProbeFailedFollow(localPlayer, travel))
                return;
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

    private void TeleportToQuester(CoppeliaQstCommand travel)
    {
        PauseOwnedRoute();
        if (!TryResolveTeleport(travel, out var aetheryteId, out var subIndex, out var name,
                out var teleportListUnavailable, out var blocker))
        {
            if (teleportListUnavailable) State = blocker;
            else BlockTravel(travel.TravelSequence, blocker);
            return;
        }

        var startResult = BeginLifestreamRequest(
            travel.TravelSequence,
            new LifestreamRequest(
                IsWorld: false,
                WorldId: 0,
                TerritoryId: travel.TerritoryId,
                ActiveState: $"Teleporting to {name}",
                FailureState: $"Teleport to {name} did not reach territory {travel.TerritoryId}",
                RequireBusyCompletion: Plugin.ClientState.TerritoryType == travel.TerritoryId,
                AetheryteId: aetheryteId,
                AetheryteName: name),
            () => teleport.InvokeFunc(aetheryteId, subIndex));
        if (startResult == LifestreamStartResult.Attempted)
            Plugin.Log.Information(
                $"[Coppelia][QST] Selected fallback aetheryte {name} ({aetheryteId}) in latest destination territory {travel.TerritoryId}.");
    }

    public void Release()
    {
        CancelHealRider("Assignment released");
        rideMode = null;
        ordinaryMountDeadline = null;
        ordinaryMountBlocker = string.Empty;
        instanceSwitchPending = false;
        nextInstanceAttemptUtc = DateTime.MinValue;
        approachLocation = null;
        followPolicy.ResetApproach();
        ClearHinterlandsRoute();
        ClearLineOfSightRescue();
        ResetTerritoryHandoff();
        ReleaseOwnedRoute();
        latestTravel = null;
        pendingExactTravel = null;
        lifestreamRequest = null;
        lifestreamPolicy.Reset();
        travelSequencePolicy.Reset();
        actionHoldUntilUtc = DateTime.MinValue;
        nextMountActionUtc = DateTime.MinValue;
        pendingPriorityDestinationSequence = 0;
        lastExistingLifestreamBusySequence = 0;
        lifestreamObservedLoading = false;
        pendingAetheryteArrival = null;
        followPolicy.Reset();
        flightPolicy.Reset();
        State = "Idle";
    }

    private static string BuildFollowState(string state, string blocker) =>
        string.IsNullOrWhiteSpace(blocker) ? state : $"{state}; safe ground fallback ({blocker})";

    private bool UpdateInstanceRendezvous(IPlayerCharacter local, CoppeliaQstCommand travel)
    {
        // Legacy direct following has no instance target. HealRider must always verify one.
        if (rideMode == null && !travel.InstanceId.HasValue)
            return false;
        var currentInstance = CurrentInstanceId;
        if (HealRiderPolicy.SameInstance(travel.InstanceId, currentInstance))
            return false;
        ReleaseOwnedRoute();
        RearmLatestTravelRoute();
        ClearLineOfSightRescue();
        if (!travel.InstanceId.HasValue || !currentInstance.HasValue)
        {
            State = "Waiting for verified Helper and Quester public-instance metadata";
            return true;
        }
        if (travel.InstanceId.Value == 0)
        {
            State = "Waiting for the Quester's verified non-instanced area";
            return true;
        }
        if (IsBetweenAreas() || local.IsDead || Plugin.Condition[ConditionFlag.InCombat] ||
            Plugin.Condition[ConditionFlag.BoundByDuty] || Plugin.Condition[ConditionFlag.Casting] ||
            Plugin.Condition[ConditionFlag.Occupied] || Plugin.Condition[ConditionFlag.OccupiedInEvent] ||
            Plugin.Condition[ConditionFlag.Mounting71] || Plugin.Condition[ConditionFlag.OccupiedInQuestEvent] ||
            Plugin.Condition[ConditionFlag.OccupiedInCutSceneEvent] || Plugin.Condition[ConditionFlag.Occupied33] ||
            Plugin.Condition[ConditionFlag.Occupied39] || Plugin.Condition[ConditionFlag.WatchingCutscene] ||
            Plugin.Condition[ConditionFlag.Occupied30] || Plugin.Condition[ConditionFlag.Occupied38] ||
            Plugin.Condition[ConditionFlag.OccupiedSummoningBell] || Plugin.Condition[ConditionFlag.WatchingCutscene78] ||
            RideNow < actionHoldUntilUtc ||
            !TryGetLifestreamBusy(out var busy) || busy)
        {
            State = "Waiting for safe idle travel before changing public instance";
            return true;
        }
        if (RideNow < nextInstanceAttemptUtc)
        {
            State = $"Waiting to retry public instance {travel.InstanceId.Value}";
            return true;
        }
        if (!TryGetRouteActivity(out var finding, out var running) || finding || running)
        {
            State = "Waiting for navigation to stop before changing public instance";
            return true;
        }

        var besideAetheryte = Plugin.ObjectTable.Any(obj => obj.ObjectKind == ObjectKind.Aetheryte &&
            Vector3.Distance(local.Position, obj.Position) < 10f && isMainAetheryte(obj.BaseId));
        if (!besideAetheryte)
        {
            if (!TryResolveTeleport(travel, out var aetheryteId, out var subIndex, out var name,
                    out _, out var blocker))
            {
                State = $"Public-instance rendezvous: {blocker}";
                return true;
            }
            BeginLifestreamRequest(travel.TravelSequence,
                new LifestreamRequest(false, 0, travel.TerritoryId,
                    $"Travelling to {name} to change public instance", $"Instance-change aetheryte {name} was not reached",
                    RequireBusyCompletion: true, AetheryteId: aetheryteId, AetheryteName: name),
                () => teleport.InvokeFunc(aetheryteId, subIndex));
            return true;
        }
        if (Plugin.Condition[ConditionFlag.InFlight] || Plugin.Condition[ConditionFlag.Mounted])
        {
            if (RideNow >= nextMountActionUtc)
            {
                nextMountActionUtc = RideNow.AddSeconds(2);
                TryUseGeneralAction(DismountGeneralActionId, out _);
            }
            State = "Landing and dismounting beside the aetheryte before /li";
            return true;
        }
        nextInstanceAttemptUtc = RideNow.AddSeconds(5);
        instanceSwitchPending = true;
        try
        {
            var accepted = Plugin.CommandManager.ProcessCommand($"/li {travel.InstanceId.Value}");
            State = accepted ? $"Verifying public instance {travel.InstanceId.Value} after /li" :
                $"Lifestream rejected /li {travel.InstanceId.Value}; waiting five seconds to retry";
            Plugin.Log.Information($"[Coppelia][HealRider] Issued /li {travel.InstanceId.Value}; actual instance verification remains pending.");
        }
        catch (Exception) { State = "Lifestream instance command failed; waiting five seconds to retry"; }
        return true;
    }

    private bool TryProbeFailedFollow(IPlayerCharacter localPlayer, CoppeliaQstCommand travel)
    {
        if (pendingExactTravel != null || localPlayer.CurrentWorld.RowId != travel.QuesterCurrentWorldId ||
            Plugin.Condition[ConditionFlag.InFlight] || HealRiderActive || lineOfSightRescueHasDestination)
            return false;
        if (!territoryHandoffPolicy.TryArmFollowFailure(Plugin.ClientState.TerritoryType, true))
            return false;
        return HandleTerritoryHandoff(localPlayer, travel);
    }

    private bool HandleTerritoryHandoff(IPlayerCharacter? localPlayer, CoppeliaQstCommand travel)
    {
        var betweenAreas = IsBetweenAreas();
        var helperLoaded = Plugin.ClientState.IsLoggedIn && localPlayer != null && !betweenAreas;
        var previousPhase = territoryHandoffPolicy.Phase;
        var decision = territoryHandoffPolicy.Evaluate(
            Plugin.ClientState.TerritoryType,
            travel.TerritoryId,
            betweenAreas,
            helperLoaded,
            TimeSpan.FromSeconds(configuration.TerritoryForwardProbeSeconds),
            DateTime.UtcNow);

        switch (decision)
        {
            case CoppeliaTerritoryHandoffDecision.StartForwardProbe:
                if (localPlayer == null ||
                    !TryGetRouteActivity(out var isPathfinding, out var isPathRunning))
                {
                    territoryHandoffPolicy.FailProbe();
                    Plugin.Log.Warning(
                        "[Coppelia][QST] Forward probe could not inspect vnavmesh ownership; using teleport fallback.");
                    return false;
                }

                if ((isPathfinding || isPathRunning) && !routePolicy.OwnsRoute)
                {
                    territoryHandoffPolicy.FailProbe();
                    Plugin.Log.Warning(
                        "[Coppelia][QST] Forward probe found non-Coppelia vnavmesh activity and left it untouched; using teleport fallback.");
                    return false;
                }

                ReleaseOwnedRoute();
                routePolicy.AcceptSnapshot(
                    travel.TravelSequence,
                    new Vector3(travel.X, travel.Y, travel.Z));

                var direction = new Vector3(MathF.Sin(localPlayer.Rotation), 0f, MathF.Cos(localPlayer.Rotation));
                var forwardDestination = localPlayer.Position + direction * 200f;
                try
                {
                    moveDirect.InvokeAction(new List<Vector3> { forwardDestination }, false);
                    forwardProbeOwnsPath = true;
                    State = "Probing forward for the observed territory crossing";
                    Plugin.Log.Information(
                        $"[Coppelia][QST] Started one {territoryHandoffPolicy.ProbeDuration.TotalSeconds:0}-second forward probe from territory " +
                        $"{territoryHandoffPolicy.SourceTerritoryId} toward latest destination territory {travel.TerritoryId}.");
                    return true;
                }
                catch (Exception ex)
                {
                    territoryHandoffPolicy.FailProbe();
                    Plugin.Log.Warning(
                        ex,
                        "[Coppelia][QST] vnavmesh rejected the forward probe; using teleport fallback.");
                    return false;
                }

            case CoppeliaTerritoryHandoffDecision.ContinueForwardProbe:
                State = "Probing forward for the observed territory crossing";
                return true;

            case CoppeliaTerritoryHandoffDecision.WaitForLoad:
                StopForwardProbePath();
                State = "Territory transition observed; waiting for the helper to load safely";
                if (previousPhase != CoppeliaTerritoryHandoffPhase.WaitingForLoad)
                {
                    Plugin.Log.Information(
                        $"[Coppelia][QST] Forward probe observed a territory transition from " +
                        $"{territoryHandoffPolicy.SourceTerritoryId}; waiting for a safe loaded result.");
                }
                return true;

            case CoppeliaTerritoryHandoffDecision.DestinationReached:
                StopForwardProbePath();
                MarkPriorityDestinationReached(travel, "the forward probe reached the latest territory");
                Plugin.Log.Information(
                    $"[Coppelia][QST] Forward probe reached latest destination territory {travel.TerritoryId}; teleport fallback was skipped.");
                return false;

            case CoppeliaTerritoryHandoffDecision.UseTeleportFallback:
                StopForwardProbePath();
                if (previousPhase != CoppeliaTerritoryHandoffPhase.Fallback)
                {
                    var result = territoryHandoffPolicy.ProbeStartedUtc != default &&
                                 Plugin.ClientState.TerritoryType == territoryHandoffPolicy.SourceTerritoryId
                        ? $"ended after {territoryHandoffPolicy.ProbeDuration.TotalSeconds:0} seconds without a territory transition"
                        : $"settled in territory {Plugin.ClientState.TerritoryType} instead of latest territory {travel.TerritoryId}";
                    Plugin.Log.Information($"[Coppelia][QST] Forward probe {result}; selecting teleport fallback.");
                }
                return false;

            default:
                return false;
        }
    }

    private void MarkPriorityDestinationReached(CoppeliaQstCommand travel, string evidence)
    {
        if ((rideMode != null || travel.InstanceId.HasValue) &&
            !HealRiderPolicy.SameInstance(travel.InstanceId, CurrentInstanceId))
            return;
        if (pendingPriorityDestinationSequence == 0 ||
            travel.TravelSequence < pendingPriorityDestinationSequence)
        {
            return;
        }

        Plugin.Log.Information(
            $"[Coppelia][QST] Reached pending destination snapshot {pendingPriorityDestinationSequence}: {evidence}.");
        pendingPriorityDestinationSequence = 0;
    }

    private void ResetTerritoryHandoff()
    {
        StopForwardProbePath();
        territoryHandoffPolicy.Reset();
    }

    private void StopForwardProbePath()
    {
        if (!forwardProbeOwnsPath)
            return;

        forwardProbeOwnsPath = false;
        InvokePathStop();
    }

    private static bool IsBetweenAreas() =>
        Plugin.Condition[ConditionFlag.BetweenAreas] ||
        Plugin.Condition[ConditionFlag.BetweenAreas51];

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

        var targetMatches = !IsBetweenAreas() &&
                            (lifestreamRequest.IsWorld
                                ? localPlayer.CurrentWorld.RowId == lifestreamRequest.WorldId
                                : Plugin.ClientState.TerritoryType == lifestreamRequest.TerritoryId);
        var previousActivity = lifestreamPolicy.Activity;
        var busyAvailable = TryGetLifestreamBusy(out var busy);
        // Native teleports may never make Lifestream busy. Observed loading or
        // physical arrival at the exact crystal also proves same-territory arrival.
        var atExactAetheryte = targetMatches && !lifestreamRequest.IsWorld && lifestreamRequest.AetheryteId > 0 &&
            Plugin.ObjectTable.Any(obj => obj.ObjectKind == ObjectKind.Aetheryte &&
                obj.BaseId == lifestreamRequest.AetheryteId && Vector3.Distance(localPlayer.Position, obj.Position) < 10f);
        var targetReached = targetMatches &&
                            (!lifestreamRequest.RequireBusyCompletion ||
                             busyAvailable && !busy &&
                             (previousActivity == CoppeliaLifestreamActivity.Busy || lifestreamObservedLoading || atExactAetheryte));
        if (targetReached)
        {
            lifestreamPolicy.Observe(busy: false, targetReached: true, utcNow: DateTime.UtcNow);
            if (!lifestreamRequest.IsWorld &&
                lifestreamRequest.AetheryteId > 0 &&
                lifestreamObservedLoading)
            {
                pendingAetheryteArrival = new PendingAetheryteArrival(
                    lifestreamRequest.TerritoryId,
                    lifestreamRequest.AetheryteId,
                    lifestreamRequest.AetheryteName,
                    DateTime.UtcNow);
            }
            Plugin.Log.Information(
                $"[Coppelia][QST] Lifestream reached the accepted destination for travel snapshot {lifestreamPolicy.Sequence}.");
            lifestreamPolicy.Reset();
            lifestreamRequest = null;
            lifestreamObservedLoading = false;
            if (localPlayer.CurrentWorld.RowId == travel.QuesterCurrentWorldId &&
                Plugin.ClientState.TerritoryType == travel.TerritoryId &&
                pendingExactTravel == null)
            {
                MarkPriorityDestinationReached(travel, "Lifestream reached the requested destination");
            }
            return false;
        }

        if (busyAvailable && IsBetweenAreas() &&
            previousActivity == CoppeliaLifestreamActivity.Busy && !busy)
        {
            State = lifestreamRequest.ActiveState;
            return true;
        }

        if (!busyAvailable)
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

        if (previousActivity != lifestreamPolicy.Activity)
        {
            if (lifestreamPolicy.Activity == CoppeliaLifestreamActivity.Busy)
            {
                Plugin.Log.Information(
                    $"[Coppelia][QST] Lifestream became busy for travel snapshot {lifestreamPolicy.Sequence}.");
            }
            else if (lifestreamPolicy.Activity == CoppeliaLifestreamActivity.Failed)
            {
                Plugin.Log.Warning(
                    $"[Coppelia][QST] Lifestream did not reach the accepted destination for travel snapshot {lifestreamPolicy.Sequence}: " +
                    $"{lifestreamRequest.FailureState}.");
            }
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

    private void TryRecordPendingAetheryteArrival(IPlayerCharacter localPlayer)
    {
        if (pendingAetheryteArrival == null ||
            DateTime.UtcNow - pendingAetheryteArrival.LoadedAtUtc < TimeSpan.FromSeconds(1))
        {
            return;
        }

        var arrival = pendingAetheryteArrival;
        pendingAetheryteArrival = null;
        if (Plugin.ClientState.TerritoryType != arrival.TerritoryId ||
            localPlayer.Position == Vector3.Zero)
        {
            return;
        }

        var estimatedPosition = GetEstimatedAetherytePosition(arrival.AetheryteId);
        if (estimatedPosition == Vector3.Zero)
        {
            Plugin.Log.Debug(
                $"[Coppelia][Aetheryte] No MapMarker estimate was available for {arrival.AetheryteName} ({arrival.AetheryteId}); arrival was not recorded.");
            return;
        }

        var dx = localPlayer.Position.X - estimatedPosition.X;
        var dz = localPlayer.Position.Z - estimatedPosition.Z;
        var xzDistance = Math.Sqrt(dx * dx + dz * dz);
        Plugin.Log.Debug(
            $"[Coppelia][Aetheryte] Arrival validation for {arrival.AetheryteName} ({arrival.AetheryteId}) was {xzDistance:F1}y XZ from its MapMarker estimate.");
        if (!CoppeliaAetherytePolicy.IsArrivalWithinRecordingRange(
                localPlayer.Position,
                estimatedPosition))
            return;

        aetherytePositionDatabase.RecordPosition(
            arrival.AetheryteId,
            arrival.AetheryteName,
            localPlayer.Position.X,
            localPlayer.Position.Y,
            localPlayer.Position.Z);
    }

    private static Vector3 GetEstimatedAetherytePosition(uint aetheryteId)
    {
        try
        {
            if (!Plugin.DataManager.GetExcelSheet<Aetheryte>().TryGetRow(aetheryteId, out var aetheryte))
                return Vector3.Zero;

            var mapTransform = TryGetMapMarkerData(aetheryte.Territory.RowId, out var mapMarkers);
            if (mapTransform == null)
                return Vector3.Zero;

            foreach (var marker in mapMarkers)
            {
                if (marker.DataType is not (3 or 4) ||
                    marker.DataKeyId != aetheryteId && marker.DataKeyId != aetheryte.PlaceName.RowId)
                {
                    continue;
                }

                return CoppeliaAetherytePolicy.ConvertMapMarker(marker, mapTransform);
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Debug(ex, $"[Coppelia][Aetheryte] Arrival estimate failed for aetheryte {aetheryteId}.");
        }

        return Vector3.Zero;
    }

    private LifestreamStartResult BeginLifestreamRequest(long sequence, LifestreamRequest request, Func<bool> start)
    {
        PauseOwnedRoute();
        if (!TryGetLifestreamBusy(out var busy))
        {
            ResetTerritoryHandoff();
            BlockTravel(sequence, "Lifestream travel status IPC failed; waiting for a newer travel snapshot");
            return LifestreamStartResult.Blocked;
        }
        if (busy)
        {
            State = "Waiting for existing Lifestream travel to finish";
            if (lastExistingLifestreamBusySequence != sequence)
            {
                lastExistingLifestreamBusySequence = sequence;
                Plugin.Log.Information(
                    $"[Coppelia][QST] Lifestream is already busy; destination snapshot {sequence} remains pending.");
            }
            return LifestreamStartResult.WaitingForExisting;
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
        followPolicy.ResetApproach();
        lifestreamObservedLoading = false;
        if (accepted)
            pendingAetheryteArrival = null;
        lifestreamPolicy.Begin(sequence, accepted, DateTime.UtcNow);
        ResetTerritoryHandoff();
        State = accepted
            ? request.ActiveState
            : $"{request.FailureState}; waiting for a newer travel snapshot";
        if (accepted)
        {
            Plugin.Log.Information($"[Coppelia][QST] Lifestream accepted travel snapshot {sequence}: {request.ActiveState}.");
        }
        else
        {
            Plugin.Log.Warning($"[Coppelia][QST] Lifestream rejected travel snapshot {sequence}: {request.FailureState}.");
        }
        return LifestreamStartResult.Attempted;
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
        travelSequencePolicy.Block(sequence, state);
        State = state;
        Plugin.Log.Warning($"[Coppelia][QST] Travel snapshot {sequence} is visibly blocked: {state}");
    }

    private unsafe bool TryResolveTeleport(
        CoppeliaQstCommand travel,
        out uint aetheryteId,
        out byte subIndex,
        out string name,
        out bool teleportListUnavailable,
        out string blocker)
    {
        aetheryteId = 0;
        subIndex = 0;
        name = string.Empty;
        teleportListUnavailable = false;
        blocker = string.Empty;

        if (!teleportListReader(out var teleportList, out blocker))
        {
            teleportListUnavailable = true;
            return false;
        }

        return TryResolveTeleport(travel, teleportList, out aetheryteId, out subIndex, out name, out blocker);
    }

    private bool TryResolveTeleport(
        CoppeliaQstCommand travel,
        IReadOnlyList<CoppeliaTeleportListEntry> teleportList,
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
            var requestedSubIndex = travel.AetheryteSubIndex ?? 0;
            if (!CoppeliaTeleportIntentPolicy.TryFindExact(
                    teleportList,
                    exactId,
                    requestedSubIndex,
                    out var exactEntry))
            {
                blocker = "The exact Quester teleport destination is not available in this helper's teleport list.";
                return false;
            }

            aetheryteId = exactEntry.AetheryteId;
            subIndex = exactEntry.SubIndex;
            name = string.IsNullOrWhiteSpace(travel.AetheryteName) ? $"aetheryte {exactId}" : travel.AetheryteName;
            return true;
        }

        try
        {
            var target = new Vector3(travel.X, travel.Y, travel.Z);
            var aetheryteSheet = Plugin.DataManager.GetExcelSheet<Aetheryte>();
            var levelSheet = Plugin.DataManager.GetExcelSheet<Level>();
            var candidates = new List<CoppeliaAetheryteCandidate>();
            foreach (var destination in teleportList)
            {
                if (configuration.AvoidTamamizuAetheryte &&
                    destination.AetheryteId == CoppeliaAetherytePolicy.TamamizuAetheryteId)
                {
                    continue;
                }

                if (!aetheryteSheet.TryGetRow(destination.AetheryteId, out var aetheryte) ||
                    aetheryte.Territory.RowId != travel.TerritoryId)
                {
                    continue;
                }

                var storedPosition = aetherytePositionDatabase.GetPosition(destination.AetheryteId);
                var worldPosition = storedPosition == null
                    ? Vector3.Zero
                    : new Vector3(storedPosition.X, storedPosition.Y, storedPosition.Z);

                if (worldPosition == Vector3.Zero)
                {
                    try
                    {
                        var levelReferences = new List<CoppeliaAetheryteLevelReference>();
                        foreach (var levelReference in aetheryte.Level)
                        {
                            var embedded = levelReference.ValueNullable;
                            levelReferences.Add(new CoppeliaAetheryteLevelReference(
                                levelReference.RowId,
                                embedded == null
                                    ? null
                                    : new Vector3(embedded.Value.X, embedded.Value.Y, embedded.Value.Z)));
                        }

                        worldPosition = CoppeliaAetherytePolicy.ResolveLevelPosition(
                            levelReferences,
                            levelRowId =>
                            {
                                try
                                {
                                    var directLevel = levelSheet.GetRow(levelRowId);
                                    return new Vector3(directLevel.X, directLevel.Y, directLevel.Z);
                                }
                                catch (Exception)
                                {
                                    return null;
                                }
                            });
                    }
                    catch (Exception ex)
                    {
                        Plugin.Log.Debug(
                            ex,
                            $"[Coppelia][Aetheryte] Level traversal failed for aetheryte {destination.AetheryteId}; continuing with MapMarker fallback.");
                    }
                }

                candidates.Add(new CoppeliaAetheryteCandidate(
                    destination.AetheryteId,
                    destination.SubIndex,
                    aetheryte.Territory.RowId,
                    aetheryte.PlaceName.ValueNullable?.Name.ExtractText() ?? $"ID {destination.AetheryteId}",
                    aetheryte.PlaceName.RowId,
                    destination.GilCost,
                    worldPosition,
                    aetherytePositionDatabase.HasPosition(destination.AetheryteId)));
            }

            CoppeliaAetheryteMapTransform? mapTransform = null;
            IReadOnlyList<CoppeliaAetheryteMapMarker> mapMarkers = Array.Empty<CoppeliaAetheryteMapMarker>();
            if (candidates.Any(candidate => candidate.Position == Vector3.Zero))
            {
                mapTransform = TryGetMapMarkerData(
                    travel.TerritoryId,
                    out mapMarkers);
            }
            var mapLocationEntry = target == default
                ? null
                : mapLocationDatabase.FindEntry(travel.TerritoryId, target.X, target.Z);
            var mapLocation = mapLocationEntry == null
                ? null
                : new CoppeliaMapLocationSelection(
                    mapLocationEntry.AetheryteName,
                    mapLocationEntry.HasRealXYZ,
                    new Vector3(mapLocationEntry.RealX, mapLocationEntry.RealY, mapLocationEntry.RealZ));

            var selection = CoppeliaAetherytePolicy.Resolve(
                travel.TerritoryId,
                target,
                candidates,
                configuration.AvoidTamamizuAetheryte,
                mapTransform,
                mapMarkers,
                mapLocation);
            if (selection == null)
            {
                blocker = "No aetheryte in the Quester's territory is available in this helper's teleport list.";
                return false;
            }

            aetheryteId = selection.Candidate.Id;
            subIndex = selection.Candidate.SubIndex;
            name = selection.Candidate.Name;
            Plugin.Log.Debug(
                $"[Coppelia][Aetheryte] Selected {name} ({aetheryteId}, sub-index {subIndex}) " +
                (selection.Distance == double.MaxValue
                    ? $"by map override or cheapest {selection.Candidate.GilCost} gil fallback."
                    : $"at {selection.Distance:F0}y using {(selection.WinnerUsedXyz ? "XYZ" : "XZ")} distance."));
            return true;
        }
        catch (Exception ex)
        {
            blocker = $"Aetheryte resolution failed: {ex.Message}";
            Plugin.Log.Warning(ex, "[Coppelia][Aetheryte] Resolver failed.");
            return false;
        }
    }

    private static CoppeliaAetheryteMapTransform? TryGetMapMarkerData(
        uint territoryId,
        out IReadOnlyList<CoppeliaAetheryteMapMarker> mapMarkers)
    {
        mapMarkers = Array.Empty<CoppeliaAetheryteMapMarker>();
        try
        {
            if (!Plugin.DataManager.GetExcelSheet<TerritoryType>().TryGetRow(territoryId, out var territory) ||
                territory.Map.RowId == 0)
            {
                return null;
            }

            var map = territory.Map.Value;
            var transform = new CoppeliaAetheryteMapTransform(map.SizeFactor, map.OffsetX, map.OffsetY);
            var markerSheet = Plugin.DataManager.GetSubrowExcelSheet<MapMarker>();
            var markers = new List<CoppeliaAetheryteMapMarker>();
            for (ushort subIndex = 0; subIndex < 500; subIndex++)
            {
                var marker = markerSheet.GetSubrowOrDefault(territory.Map.RowId, subIndex);
                if (marker == null)
                    break;

                markers.Add(new CoppeliaAetheryteMapMarker(
                    marker.Value.X,
                    marker.Value.Y,
                    marker.Value.DataType,
                    marker.Value.DataKey.RowId));
            }

            mapMarkers = markers;
            return transform;
        }
        catch (Exception ex)
        {
            Plugin.Log.Debug(ex, $"[Coppelia][Aetheryte] MapMarker lookup failed for territory {territoryId}.");
            return null;
        }
    }

    private unsafe bool TryResolvePendingExactTeleport(
        CoppeliaQstCommand exactTravel,
        CoppeliaQstCommand latestTravel,
        out uint territoryId,
        out uint aetheryteId,
        out byte subIndex,
        out string name,
        out bool usedFallback,
        out bool staleExactIntent,
        out bool teleportListUnavailable,
        out string blocker)
    {
        territoryId = 0;
        aetheryteId = 0;
        subIndex = 0;
        name = string.Empty;
        usedFallback = false;
        staleExactIntent = false;
        teleportListUnavailable = false;
        blocker = string.Empty;

        if (exactTravel.AetheryteId is not { } exactId ||
            !Plugin.DataManager.GetExcelSheet<Aetheryte>().TryGetRow(exactId, out var exactAetheryte))
        {
            blocker = "The accepted Quester teleport destination is unavailable.";
            return false;
        }

        territoryId = exactAetheryte.Territory.RowId;
        if (CoppeliaTeleportIntentPolicy.IsStale(
                exactTravel.TravelSequence,
                exactTravel.TerritoryId,
                territoryId,
                latestTravel.TravelSequence,
                latestTravel.TerritoryId))
        {
            staleExactIntent = true;
            return false;
        }

        if (!TryGetTeleportListSnapshot(out var teleportList, out blocker))
        {
            teleportListUnavailable = true;
            return false;
        }

        var requestedSubIndex = exactTravel.AetheryteSubIndex ?? 0;
        if (CoppeliaTeleportIntentPolicy.TryFindExact(
                teleportList,
                exactId,
                requestedSubIndex,
                out var exactEntry))
        {
            aetheryteId = exactEntry.AetheryteId;
            subIndex = exactEntry.SubIndex;
            name = string.IsNullOrWhiteSpace(exactTravel.AetheryteName)
                ? exactAetheryte.PlaceName.ValueNullable?.Name.ExtractText() ?? $"aetheryte {exactId}"
                : exactTravel.AetheryteName;
            return true;
        }

        if (!CoppeliaTeleportIntentPolicy.CanUseFallback(
                exactTravel.TravelSequence,
                territoryId,
                latestTravel.TravelSequence,
                latestTravel.TerritoryId))
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
        usedFallback = true;
        return TryResolveTeleport(fallbackTravel, teleportList, out aetheryteId, out subIndex, out name, out blocker);
    }

    private static unsafe bool TryGetTeleportListSnapshot(
        out IReadOnlyList<CoppeliaTeleportListEntry> teleportList,
        out string blocker)
    {
        teleportList = Array.Empty<CoppeliaTeleportListEntry>();
        blocker = string.Empty;

        try
        {
            var telepo = Telepo.Instance();
            if (telepo == null)
            {
                blocker = "The helper's teleport list is unavailable or still loading.";
                return false;
            }

            telepo->UpdateAetheryteList();
            var rawEntryCount = telepo->TeleportList.Count;
            var snapshot = new List<CoppeliaTeleportListEntry>();
            for (var i = 0; i < rawEntryCount; i++)
            {
                var entry = telepo->TeleportList[i];
                if (entry.AetheryteId != 0)
                {
                    snapshot.Add(new CoppeliaTeleportListEntry(
                        entry.AetheryteId,
                        entry.SubIndex,
                        entry.GilCost));
                }
            }

            if (!CoppeliaTeleportListPolicy.IsAvailable(
                    hasTelepoInstance: true,
                    rawEntryCount,
                    snapshot.Count))
            {
                blocker = "The helper's teleport list is unavailable or still loading.";
                return false;
            }

            teleportList = snapshot;
            return true;
        }
        catch (Exception ex)
        {
            blocker = "The helper's teleport list is unavailable or still loading.";
            Plugin.Log.Debug(ex, "[Coppelia][Aetheryte] Teleport list refresh failed while loading.");
            return false;
        }
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
        InterruptOwnedRoute(interruption);
    }

    private void InterruptOwnedRoute(CoppeliaRouteInterruption interruption)
    {
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

    private void StopLineOfSightRescueRoute()
    {
        var activityKnown = TryGetRouteActivity(out var isPathfinding, out var isPathRunning);
        if (activityKnown && routePolicy.OwnsRoute)
            routePolicy.Observe(isPathfinding, isPathRunning, DateTime.UtcNow);
        if (routePolicy.OwnsRoute)
        {
            lineOfSightRescueRoutePolicy.Release(pathfindInProgress: false, pathRunning: false);
            return;
        }

        if (activityKnown && lineOfSightRescueRoutePolicy.OwnsRoute)
            lineOfSightRescueRoutePolicy.Observe(isPathfinding, isPathRunning, DateTime.UtcNow);
        var interruption = lineOfSightRescueRoutePolicy.Release(
            activityKnown && isPathfinding,
            activityKnown ? isPathRunning : lineOfSightRescueRoutePolicy.OwnsRoute);
        if (interruption == CoppeliaRouteInterruption.None)
            return;

        lineOfSightRescueOwnStopPending = true;
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

    private void RearmLatestTravelRoute()
    {
        if (latestTravel == null)
            return;

        routePolicy.AcceptSnapshot(
            latestTravel.TravelSequence,
            new Vector3(latestTravel.X, latestTravel.Y, latestTravel.Z));
    }

    private string YieldLineOfSightRescue(string state)
    {
        StopLineOfSightRescueRoute();
        lineOfSightRescueTargetId = 0;
        lineOfSightRescueDestination = default;
        lineOfSightRescueHasDestination = false;
        lineOfSightRescueOwnStopPending = false;
        RearmLatestTravelRoute();
        return SetLineOfSightRescueState(state);
    }

    private string SetLineOfSightRescueState(string state)
    {
        LineOfSightRescueState = state;
        return state;
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
        string FailureState,
        bool RequireBusyCompletion = false,
        uint AetheryteId = 0,
        string AetheryteName = "");

    private sealed record PendingAetheryteArrival(
        uint TerritoryId,
        uint AetheryteId,
        string AetheryteName,
        DateTime LoadedAtUtc);

    private enum LifestreamStartResult
    {
        WaitingForExisting,
        Blocked,
        Attempted,
    }
}

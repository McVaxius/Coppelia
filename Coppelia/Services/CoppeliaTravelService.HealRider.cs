using System.Numerics;
using Coppelia.Models;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.SubKinds;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using Lumina.Excel.Sheets;

namespace Coppelia.Services;

internal sealed partial class CoppeliaTravelService
{
    private HealRiderCommand? ride;
    private HealRiderStatus rideStatus = new();
    private readonly CoppeliaRoutePolicy rideRoute = new();
    private DateTime rideUpdatedUtc;
    private DateTime rideExpiresUtc;
    private DateTime nextRideActionUtc;
    private bool rideOwnsMount;
    private bool boardingConfirmed;
    private (bool Quester, bool Native)? lastPassengerObservation;
    private long ridePathSequence;
    private DateTime? rideCleanupDeadline;
    private ulong rideHelperContentId;
    private HealRiderCommand? lastRide;
    private HealRiderCommand? rideMode;
    private TimeSpan mountPreparationAllowance = TimeSpan.FromSeconds(60);
    private DateTime? ordinaryMountDeadline;
    private string ordinaryMountBlocker = string.Empty;
    private TimeProvider rideClock = TimeProvider.System;
    private DateTime RideNow => rideClock.GetUtcNow().UtcDateTime;

    private void SetRideState(string state, string? blocker = null)
    {
        if (rideStatus.State != state)
            Plugin.Log.Information($"[HealRider] Leg {lastRide?.LegId}: {rideStatus.State} -> {state}.");
        rideStatus = rideStatus with { State = state, Blocker = blocker ?? rideStatus.Blocker };
    }

    public void SuspendHealRiderMounts(bool suspended) => rideMountsSuspended = suspended;
    private bool rideMountsSuspended;

    private HealRiderStatus ApplyRideMode(HealRiderCommand command)
    {
        if (command.Action is not ("EnableMode" or "DisableMode" or "Inspect"))
            return RiderReply(command, false, "Blocked", "Unsupported assignment mount-mode action.");
        if (command.Action == "Inspect")
            return RiderReply(command, rideMode?.SessionId == command.SessionId,
                string.IsNullOrEmpty(ordinaryMountBlocker) ? "Enabled" : "Blocked", ordinaryMountBlocker);
        if (ride != null)
        {
            CancelHealRider("Assignment mount policy changed");
            return RiderReply(command, false, "Cancelling", "Finish the active ride before changing mount policy.");
        }
        if (command.Action == "DisableMode")
        {
            rideMode = null;
            ordinaryMountDeadline = null;
            ordinaryMountBlocker = string.Empty;
            return RiderReply(command, true, "Disabled", string.Empty);
        }
        if (!IsPassengerMountUnlocked(command.MountId))
            return RiderReply(command, false, "Blocked", "Select an unlocked mount with passenger seats on the Helper.");
        if (rideMode?.SessionId != command.SessionId || rideMode.MountId != command.MountId ||
            !string.IsNullOrEmpty(ordinaryMountBlocker))
        {
            rideMode = command;
            mountPreparationAllowance = TimeSpan.FromSeconds(Math.Clamp(
                (command.PickupDeadlineUtc - RideNow).TotalSeconds, 5, 300));
            ordinaryMountDeadline = null;
            ordinaryMountBlocker = string.Empty;
        }
        return RiderReply(command, true, "Enabled", ordinaryMountBlocker);
    }

    private bool UpdateOrdinaryRideMount(bool mountRequired)
    {
        if (rideMode == null || rideMountsSuspended || Plugin.Condition[ConditionFlag.BoundByDuty])
            return false;
        if (!string.IsNullOrEmpty(ordinaryMountBlocker))
        {
            PauseOwnedRoute();
            State = $"HealRider Blocked: {ordinaryMountBlocker}";
            return true;
        }
        var mounted = Plugin.Condition[ConditionFlag.Mounted];
        var mounting = Plugin.Condition[ConditionFlag.Mounting71];
        if (!ordinaryMountDeadline.HasValue && !mounted && !mounting && !IsMountingAllowed(out _))
            return false;
        if (!ordinaryMountDeadline.HasValue && !mountRequired &&
            (!mounted && !mounting || mounted && SelectedMountIsActive(rideMode.MountId)))
            return false;
        ordinaryMountDeadline ??= RideNow + mountPreparationAllowance;
        PauseOwnedRoute();
        var result = PreparePassengerMount(rideMode.MountId, ordinaryMountDeadline.Value, false);
        if (result == "Ready")
        {
            ordinaryMountDeadline = null;
            return false;
        }
        if (result == "Blocked")
            ordinaryMountBlocker = "Selected passenger mount preparation was rejected or exceeded the pickup allowance.";
        State = result == "Blocked" ? $"HealRider Blocked: {ordinaryMountBlocker}" : "Preparing selected passenger mount for following";
        return true;
    }

    private string PreparePassengerMount(uint mountId, DateTime deadline, bool grounded) =>
        HealRiderPolicy.PrepareMount(RideNow, deadline, ref nextMountActionUtc,
            Plugin.Condition[ConditionFlag.Mounted], Plugin.Condition[ConditionFlag.Mounting71],
            Plugin.Condition[ConditionFlag.Casting], Plugin.Condition[ConditionFlag.InFlight],
            SelectedMountIsActive(mountId), grounded,
            action =>
            {
                Plugin.Log.Information($"[HealRider] Issued selected mount preparation action: {action}.");
                return action == "Summon" ? SummonRideMount(mountId) : TryUseGeneralAction(DismountGeneralActionId, out _);
            });

    public bool HealRiderActive => ride != null;

    public bool OwnsHealRiderCleanup(HealRiderCommand command) =>
        lastRide != null && HealRiderPolicy.SameLeg(lastRide, command) &&
        Plugin.PlayerState.ContentId == rideHelperContentId &&
        command.Action is "Inspect" or "Cancel" or "DutyHandoff" or "RetryCleanup";

    public void RetryHealRiderCleanup()
    {
        if (ride == null || rideStatus.State is not ("Blocked" or "Cancelling" or "Arriving") || Plugin.ObjectTable.LocalPlayer == null ||
            Plugin.PlayerState.ContentId != rideHelperContentId ||
            Plugin.PartyList.Length > 1 && !RidePartyContains(ride))
            return;
        rideCleanupDeadline = RideNow.AddSeconds(60);
        SetRideState("Cancelling", "Explicit activation is retrying cleanup");
    }

    public HealRiderStatus ApplyHealRider(HealRiderCommand command)
    {
        if (command.Version != 1 || command.LegId < 0 ||
            !float.IsFinite(command.X) || !float.IsFinite(command.Y) || !float.IsFinite(command.Z))
            return RiderReply(command, false, "Blocked", "Invalid HealRider v1 request.");
        if (command.LegId == 0)
            return ApplyRideMode(command);
        if (lastRide?.SessionId == command.SessionId && command.LegId < lastRide.LegId)
            return RiderReply(command, false, "Blocked", "This ride leg was retired by a newer leg.");
        if (ride != null && (!HealRiderPolicy.SameLeg(ride, command) || ride.MountId != command.MountId))
        {
            CancelHealRider("The destination or ride leg changed");
            return RiderReply(command, false, "Cancelling", "Waiting for the previous ride to dismount.");
        }
        if (command.Action == "RetryCleanup")
        {
            if (!OwnsHealRiderCleanup(command) || Plugin.ObjectTable.LocalPlayer == null ||
                Plugin.PartyList.Length > 1 && !RidePartyContains(command))
                return RiderReply(command, false, "Blocked", "Cleanup retry requires the captured character and party.");
            RetryHealRiderCleanup();
            return RiderReply(command, true);
        }
        if (command.Action is "Cancel" or "DutyHandoff")
        {
            if (ride == null)
            {
                // Party preparation can be cancelled before Pickup reaches this endpoint.
                travelSequencePolicy.RequireFreshSnapshot();
                ReleaseOwnedRoute();
                lastRide = command;
                rideHelperContentId = Plugin.PlayerState.ContentId;
                rideOwnsMount = false;
                boardingConfirmed = false;
                ordinaryMountDeadline = null;
                rideStatus = new HealRiderStatus { SessionId = command.SessionId, LegId = command.LegId, State = "Cancelled" };
                Plugin.Log.Information($"[HealRider] Leg {command.LegId}: cancelled before pickup; no transport mount acquired.");
            }
            CancelHealRider(command.Action == "DutyHandoff" ? "Duty handoff" : "Pickup cancelled");
            return RiderReply(command, true);
        }
        if (command.Action == "Inspect")
        {
            if (ride == null && (lastRide == null || !HealRiderPolicy.SameLeg(lastRide, command)))
            {
                lastRide = command;
                rideHelperContentId = Plugin.PlayerState.ContentId;
            }
            if (ride != null)
                ObserveRidePassenger(command);
            return RiderReply(command, true);
        }
        if (command.Action != "Pickup")
            return RiderReply(command, false, "Blocked", "Unsupported HealRider action.");
        if (rideMode?.SessionId != command.SessionId || rideMode.MountId != command.MountId || rideMountsSuspended ||
            !string.IsNullOrEmpty(ordinaryMountBlocker))
            return RiderReply(command, false, "Blocked", "The assignment passenger-mount policy is not ready.");
        if (ride != null)
        {
            rideUpdatedUtc = RideNow;
            return RiderReply(command, true);
        }
        if (rideStatus.SessionId == command.SessionId && rideStatus.LegId == command.LegId)
            return RiderReply(command, false, rideStatus.State, "This ride leg has already ended.");

        var now = RideNow;
        command = command with
        {
            PickupDeadlineUtc = command.PickupDeadlineUtc == DateTime.MinValue
                ? now.AddSeconds(60) : command.PickupDeadlineUtc,
        };
        if (now >= command.PickupDeadlineUtc)
            return RiderReply(command, false, "Blocked", "The shared pickup wait expired before pickup was accepted.");

        var local = Plugin.ObjectTable.LocalPlayer;
        var quester = FindRideQuester(command);
        var sameLocation = RidePickupLocationReady(command);
        var distance = quester == null ? 0 : Vector3.Distance(quester.Position, HealRiderPolicy.Destination(command));
        if (!HealRiderPolicy.Eligible(distance, command.WalkingThreshold, CanFlyInTerritory(command.TerritoryId),
                command.QuesterCanFly, IsPassengerMountUnlocked(command.MountId), sameLocation) ||
            !RidePartyContains(command) || Plugin.Condition[ConditionFlag.InCombat] ||
            Plugin.Condition[ConditionFlag.BoundByDuty] || lifestreamRequest != null || hinterlandsRoute != null)
            return RiderReply(command, false, "Blocked", "Pickup requires an unlocked passenger mount, Helper flight, a non-flying Quester beyond the walking threshold, and the exact party in this area.");

        ClearLineOfSightRescue();
        ResetTerritoryHandoff();
        ReleaseOwnedRoute();
        ride = command;
        lastRide = command;
        rideHelperContentId = Plugin.PlayerState.ContentId;
        rideCleanupDeadline = null;
        rideStatus = new HealRiderStatus { SessionId = command.SessionId, LegId = command.LegId, State = "Preparing" };
        Plugin.Log.Information($"[HealRider] Leg {command.LegId}: pickup accepted; preparing selected mount.");
        rideUpdatedUtc = RideNow;
        rideExpiresUtc = rideUpdatedUtc.AddMinutes(3);
        nextRideActionUtc = DateTime.MinValue;
        rideOwnsMount = Plugin.Condition[ConditionFlag.Mounted] || Plugin.Condition[ConditionFlag.Mounting71];
        boardingConfirmed = false;
        lastPassengerObservation = null;
        return RiderReply(command, true);
    }

    private void ObserveRidePassenger(HealRiderCommand command)
    {
        rideUpdatedUtc = RideNow;
        boardingConfirmed = command.PassengerConfirmed;
    }

    private HealRiderStatus RiderReply(HealRiderCommand command, bool accepted, string? state = null, string? blocker = null)
    {
        var matching = rideStatus.SessionId == command.SessionId && rideStatus.LegId == command.LegId;
        var local = Plugin.ObjectTable.LocalPlayer;
        var quester = FindRideQuester(command);
        return new HealRiderStatus
        {
            SessionId = command.SessionId,
            LegId = command.LegId,
            Accepted = accepted,
            State = state ?? (matching ? rideStatus.State : "Idle"),
            Blocker = blocker ?? (matching ? rideStatus.Blocker : string.Empty),
            CanFly = CanFlyInTerritory(command.TerritoryId),
            MountReady = IsPassengerMountUnlocked(command.MountId),
            PickupReady = RidePickupLocationReady(command),
            BoardingReady = matching && rideStatus.State == "Boarding" && local != null && quester != null &&
                HealRiderPolicy.GroundedForBoarding(Vector3.Distance(local.Position, quester.Position),
                    Plugin.Condition[ConditionFlag.InFlight], Plugin.Condition[ConditionFlag.Mounting71],
                    Plugin.Condition[ConditionFlag.Mounted] && SelectedMountIsActive(command.MountId)),
            TransportOwnsMount = rideOwnsMount,
            Mounted = Plugin.Condition[ConditionFlag.Mounted] || Plugin.Condition[ConditionFlag.Mounting71] ||
                rideOwnsMount && (Plugin.Condition[ConditionFlag.Casting] || RideNow < nextRideActionUtc ||
                    RideNow < nextMountActionUtc),
        };
    }

    private bool RidePickupLocationReady(HealRiderCommand command)
    {
        var local = Plugin.ObjectTable.LocalPlayer;
        if (local == null || FindRideQuester(command) == null ||
            local.CurrentWorld.RowId != command.CurrentWorldId || Plugin.ClientState.TerritoryType != command.TerritoryId ||
            IsBetweenAreas() || Plugin.Condition[ConditionFlag.InCombat] || Plugin.Condition[ConditionFlag.Casting] ||
            Plugin.Condition[ConditionFlag.BoundByDuty] || lifestreamRequest != null || hinterlandsRoute != null ||
            pendingExactTravel != null || pendingAetheryteArrival != null || territoryHandoffPolicy.IsActive)
            return false;
        try { return !lifestreamBusy.InvokeFunc(); }
        catch { return false; }
    }

    public void CancelHealRider(string reason)
    {
        if (ride == null || rideStatus.State is "Cancelling" or "Blocked")
            return;
        StopRidePath();
        rideCleanupDeadline = HealRiderPolicy.BeginCleanup(rideCleanupDeadline, RideNow);
        SetRideState("Cancelling", reason);
        State = $"HealRider: {reason}";
    }

    private bool UpdateHealRider()
    {
        if (ride is not { } command)
            return false;
        var now = RideNow;
        var local = Plugin.ObjectTable.LocalPlayer;
        if (rideStatus.State == "Blocked")
        {
            State = $"HealRider Blocked: {rideStatus.Blocker}";
            return true;
        }
        if (rideCleanupDeadline.HasValue && now >= rideCleanupDeadline.Value)
        {
            StopRidePath();
            var pending = local == null || IsBetweenAreas() || Plugin.PlayerState.ContentId != rideHelperContentId
                ? "the captured character to become ready" : Plugin.Condition[ConditionFlag.InFlight] ? "landing" :
                Plugin.Condition[ConditionFlag.Mounting71] || Plugin.Condition[ConditionFlag.Casting] ||
                now < nextRideActionUtc || now < nextMountActionUtc ? "the mount action to finish" : "dismount";
            SetRideState("Blocked", $"Helper cleanup timed out waiting for {pending}; activate Helper to retry.");
            State = $"HealRider Blocked: {rideStatus.Blocker}";
            return true;
        }
        if (local == null || !Plugin.ClientState.IsLoggedIn || IsBetweenAreas() ||
            Plugin.ClientState.TerritoryType != command.TerritoryId ||
            local.CurrentWorld.RowId != command.CurrentWorldId ||
            Plugin.Condition[ConditionFlag.BoundByDuty] || Plugin.Condition[ConditionFlag.InCombat] ||
            Plugin.PlayerState.ContentId != rideHelperContentId ||
            local.IsDead || now - rideUpdatedUtc > TimeSpan.FromSeconds(10) || now >= rideExpiresUtc)
            CancelHealRider("Ride context lost, blocked, or timed out");
        if (HealRiderPolicy.PickupExpired(command.PickupDeadlineUtc, now, rideStatus.State))
            CancelHealRider("The shared pickup wait expired during approach or boarding");

        if (rideStatus.State == "Cancelling")
        {
            StopRidePath();
            if (local == null || IsBetweenAreas() || Plugin.PlayerState.ContentId != rideHelperContentId)
                return true;
            if (!rideOwnsMount || !Plugin.Condition[ConditionFlag.Mounted] && !Plugin.Condition[ConditionFlag.Mounting71] &&
                !Plugin.Condition[ConditionFlag.Casting] && now >= nextRideActionUtc && now >= nextMountActionUtc)
                FinishRide("Cancelled");
            else if (!Plugin.Condition[ConditionFlag.Casting] && !Plugin.Condition[ConditionFlag.Mounting71] &&
                now >= nextRideActionUtc && now >= nextMountActionUtc)
            {
                nextRideActionUtc = now.AddSeconds(2);
                Plugin.Log.Information($"[HealRider] Leg {command.LegId}: issued cleanup landing/dismount.");
                TryUseGeneralAction(DismountGeneralActionId, out _);
            }
            return true;
        }
        if (local == null)
            return true;

        var quester = FindRideQuester(command);
        if (quester == null || !RidePartyContains(command))
        {
            CancelHealRider("The exact Quester or party is no longer available");
            return true;
        }
        var destination = HealRiderPolicy.Destination(command);
        var mounted = Plugin.Condition[ConditionFlag.Mounted];
        var mounting = Plugin.Condition[ConditionFlag.Mounting71];
        var nativePassenger = ExactPassengerIsAboard(command, quester);
        if (rideStatus.State is "Boarding" or "Transit" &&
            lastPassengerObservation != (boardingConfirmed, nativePassenger))
        {
            lastPassengerObservation = (boardingConfirmed, nativePassenger);
            Plugin.Log.Information($"[HealRider] Leg {command.LegId}: passenger observations: Quester={boardingConfirmed}, Helper native={nativePassenger}.");
        }
        var transition = HealRiderPolicy.AdvanceTransport(rideStatus.State,
            Vector3.Distance(local.Position, quester.Position), Vector3.Distance(local.Position, destination),
            mounted, mounting, Plugin.Condition[ConditionFlag.InFlight], SelectedMountIsActive(command.MountId),
            boardingConfirmed, nativePassenger, RidePartyContains(command), action =>
            {
                switch (action)
                {
                    case "Approach":
                        MoveRideTo(quester.Position, Plugin.Condition[ConditionFlag.InFlight], HealRiderPolicy.BoardingTolerance);
                        break;
                    case "Stop":
                        StopRidePath();
                        break;
                    case "PrepareMount":
                        rideOwnsMount = true;
                        return PreparePassengerMount(command.MountId, command.PickupDeadlineUtc, true);
                    case "Travel":
                        MoveRideTo(destination, true, HealRiderPolicy.ArrivalTolerance);
                        break;
                    case "Dismount":
                        if (now >= nextRideActionUtc)
                        {
                            nextRideActionUtc = now.AddSeconds(2);
                            Plugin.Log.Information($"[HealRider] Leg {command.LegId}: issued arrival landing/dismount.");
                            if (!TryUseGeneralAction(DismountGeneralActionId, out _))
                                CancelHealRider("Landing or dismount was rejected");
                        }
                        break;
                }
                return "Waiting";
            });
        if (rideStatus.State == "Cancelling") return true;
        if (transition.State == "Cancelling")
            CancelHealRider(transition.Blocker);
        else if (transition.State == "Arrived")
            FinishRide("Arrived");
        else
        {
            if (transition.State == "Arriving")
                rideCleanupDeadline = HealRiderPolicy.BeginCleanup(rideCleanupDeadline, now);
            SetRideState(transition.State, transition.Blocker);
        }
        State = $"HealRider: {rideStatus.State}" +
            (string.IsNullOrWhiteSpace(rideStatus.Blocker) ? string.Empty : $": {rideStatus.Blocker}");
        return true;
    }
    private void MoveRideTo(Vector3 destination, bool fly, float tolerance)
    {
        if (!TryGetRouteActivity(out var finding, out var running))
        {
            CancelHealRider("Ride navigation status is unavailable");
            return;
        }
        var activity = rideRoute.Observe(finding, running, RideNow);
        if (activity == CoppeliaRouteActivity.Rejected ||
            activity == CoppeliaRouteActivity.Completed &&
            Vector3.Distance(Plugin.ObjectTable.LocalPlayer!.Position, destination) > tolerance)
        {
            if (activity == CoppeliaRouteActivity.Completed && rideStatus.State == "Preparing")
            {
                // The Quester may have walked on before accepting pickup. Approach its live position.
                StopRidePath();
                return;
            }
            CancelHealRider("The passenger route failed before arrival");
            return;
        }
        if (activity is CoppeliaRouteActivity.Owned or CoppeliaRouteActivity.Other)
            return;
        try
        {
            if (!navReady.InvokeFunc())
                return;
            rideRoute.AcceptSnapshot(++ridePathSequence, destination);
            Plugin.Log.Information($"[HealRider] Leg {ride?.LegId}: submitting {(rideStatus.State == "Preparing" ? "pickup approach" : "captured destination")} navigation.");
            if (moveCloseTo.InvokeFunc(destination, fly, tolerance))
                rideRoute.MarkStartupAccepted(RideNow);
            else
                CancelHealRider("Ride navigation rejected the destination");
        }
        catch (Exception) { CancelHealRider("Ride navigation IPC failed"); }
    }

    private void StopRidePath()
    {
        var known = TryGetRouteActivity(out var finding, out var running);
        InterruptOwnedRoute(rideRoute.Release(known && finding, known ? running : rideRoute.OwnsRoute));
    }

    private void FinishRide(string state)
    {
        StopRidePath();
        SetRideState(state);
        ride = null;
        rideOwnsMount = false;
        boardingConfirmed = false;
        // The last ordinary snapshot may still point at the pickup. Companion sends
        // a fresh snapshot once dismount/party cleanup has completed.
        travelSequencePolicy.RequireFreshSnapshot();
        ReleaseOwnedRoute();
    }

    private static IPlayerCharacter? FindRideQuester(HealRiderCommand command) =>
        Plugin.ObjectTable.OfType<IPlayerCharacter>().FirstOrDefault(player =>
            player.Name.ToString() == command.QuesterName && player.HomeWorld.RowId == command.QuesterWorldId);

    private static bool RidePartyContains(HealRiderCommand command) =>
        Plugin.PartyList.Length == 2 && Plugin.PartyList.Any(member => member.ContentId == Plugin.PlayerState.ContentId) &&
        Plugin.PartyList.Any(member => member.Name.ToString() == command.QuesterName &&
                                      member.World.RowId == command.QuesterWorldId);

    private static unsafe bool IsPassengerMountUnlocked(uint mountId)
    {
        var state = PlayerState.Instance();
        return mountId != 0 && state != null &&
               Plugin.DataManager.GetExcelSheet<Mount>().TryGetRow(mountId, out var mount) &&
               mount.ExtraSeats > 0 && state->IsMountUnlocked(mountId);
    }

    private static unsafe bool SummonRideMount(uint mountId)
    {
        var actions = ActionManager.Instance();
        return IsPassengerMountUnlocked(mountId) && actions != null &&
               actions->GetActionStatus(ActionType.Mount, mountId) == 0 &&
               actions->UseAction(ActionType.Mount, mountId);
    }

    private static unsafe bool SelectedMountIsActive(uint mountId)
    {
        var player = Plugin.ObjectTable.LocalPlayer;
        return mountId != 0 && player is { Address: not 0 } && ((Character*)player.Address)->Mount.MountId == mountId;
    }

    private unsafe bool ExactPassengerIsAboard(HealRiderCommand command, IPlayerCharacter quester)
    {
        var player = Plugin.ObjectTable.LocalPlayer;
        if (player == null || quester.Address == 0 || player.EntityId == quester.EntityId ||
            rideHelperContentId == 0 || Plugin.PlayerState.ContentId != rideHelperContentId ||
            quester.Name.ToString() != command.QuesterName || quester.HomeWorld.RowId != command.QuesterWorldId ||
            player.CurrentWorld.RowId != command.CurrentWorldId || quester.CurrentWorld.RowId != command.CurrentWorldId ||
            Plugin.ClientState.TerritoryType != command.TerritoryId || IsBetweenAreas() ||
            !Plugin.Condition[ConditionFlag.Mounted] || !SelectedMountIsActive(command.MountId) || !RidePartyContains(command))
            return false;
        // The seat-ID array can remain empty or stale after attachment. Observe the
        // identified Quester's native mode independently of its serialized confirmation.
        return ((Character*)quester.Address)->Mode == CharacterModes.RidingPillion;
    }
}

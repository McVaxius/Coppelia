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
    private long ridePathSequence;

    public bool HealRiderActive => ride != null;

    public HealRiderStatus ApplyHealRider(HealRiderCommand command)
    {
        if (command.Version != 1 || command.LegId <= 0 ||
            !float.IsFinite(command.X) || !float.IsFinite(command.Y) || !float.IsFinite(command.Z))
            return RiderReply(command, false, "Blocked", "Invalid HealRider v1 request.");
        if (ride != null && (!HealRiderPolicy.SameLeg(ride, command) || ride.MountId != command.MountId))
        {
            CancelHealRider("The destination or ride leg changed");
            return RiderReply(command, false, "Cancelling", "Waiting for the previous ride to dismount.");
        }
        if (command.Action is "Cancel" or "DutyHandoff")
        {
            CancelHealRider(command.Action == "DutyHandoff" ? "Duty handoff" : "Pickup cancelled");
            return RiderReply(command, true);
        }
        if (command.Action == "Inspect")
        {
            if (ride != null)
            {
                rideUpdatedUtc = DateTime.UtcNow;
                boardingConfirmed = command.PassengerConfirmed;
            }
            return RiderReply(command, true);
        }
        if (command.Action != "Pickup")
            return RiderReply(command, false, "Blocked", "Unsupported HealRider action.");
        if (ride != null)
        {
            rideUpdatedUtc = DateTime.UtcNow;
            return RiderReply(command, true);
        }
        if (rideStatus.SessionId == command.SessionId && rideStatus.LegId == command.LegId)
            return RiderReply(command, false, rideStatus.State, "This ride leg has already ended.");

        var now = DateTime.UtcNow;
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
        rideStatus = new HealRiderStatus { SessionId = command.SessionId, LegId = command.LegId, State = "Preparing" };
        rideUpdatedUtc = DateTime.UtcNow;
        rideExpiresUtc = rideUpdatedUtc.AddMinutes(3);
        nextRideActionUtc = DateTime.MinValue;
        rideOwnsMount = Plugin.Condition[ConditionFlag.Mounted] || Plugin.Condition[ConditionFlag.Mounting71];
        boardingConfirmed = false;
        return RiderReply(command, true);
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
                rideOwnsMount && (Plugin.Condition[ConditionFlag.Casting] || DateTime.UtcNow < nextRideActionUtc),
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
        if (ride == null || rideStatus.State == "Cancelling")
            return;
        StopRidePath();
        rideStatus = rideStatus with { State = "Cancelling", Blocker = reason };
        State = $"HealRider: {reason}";
    }

    private bool UpdateHealRider()
    {
        if (ride is not { } command)
            return false;
        var now = DateTime.UtcNow;
        var local = Plugin.ObjectTable.LocalPlayer;
        if (local == null || !Plugin.ClientState.IsLoggedIn || IsBetweenAreas() ||
            Plugin.ClientState.TerritoryType != command.TerritoryId ||
            local.CurrentWorld.RowId != command.CurrentWorldId ||
            Plugin.Condition[ConditionFlag.BoundByDuty] || Plugin.Condition[ConditionFlag.InCombat] ||
            local.IsDead || now - rideUpdatedUtc > TimeSpan.FromSeconds(10) || now >= rideExpiresUtc)
            CancelHealRider("Ride context lost, blocked, or timed out");
        if (HealRiderPolicy.PickupExpired(command.PickupDeadlineUtc, now, rideStatus.State))
            CancelHealRider("The shared pickup wait expired during approach or boarding");

        if (rideStatus.State == "Cancelling")
        {
            StopRidePath();
            if (!rideOwnsMount || !Plugin.Condition[ConditionFlag.Mounted] && !Plugin.Condition[ConditionFlag.Mounting71] &&
                !Plugin.Condition[ConditionFlag.Casting] && now >= nextRideActionUtc)
                FinishRide("Cancelled");
            else if (local != null && !IsBetweenAreas() && now >= nextRideActionUtc)
            {
                nextRideActionUtc = now.AddSeconds(2);
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
        State = $"HealRider: {rideStatus.State}";
        switch (rideStatus.State)
        {
            case "Preparing":
                // Approach first, even when ordinary following left us mounted far away.
                if (Vector3.Distance(local.Position, quester.Position) > HealRiderPolicy.BoardingTolerance)
                {
                    if (!mounting)
                        MoveRideTo(quester.Position, Plugin.Condition[ConditionFlag.InFlight], HealRiderPolicy.BoardingTolerance);
                    return true;
                }
                StopRidePath();
                if (Plugin.Condition[ConditionFlag.InFlight])
                {
                    if (now >= nextRideActionUtc)
                    {
                        nextRideActionUtc = now.AddSeconds(2);
                        TryUseGeneralAction(DismountGeneralActionId, out _);
                    }
                    return true;
                }
                if (mounted || mounting)
                {
                    if (HealRiderPolicy.GroundedForBoarding(Vector3.Distance(local.Position, quester.Position),
                            Plugin.Condition[ConditionFlag.InFlight], mounting, mounted && SelectedMountIsActive(command.MountId)))
                    {
                        rideOwnsMount = true;
                        rideStatus = rideStatus with { State = "Boarding" };
                    }
                    else if (!mounting && now >= nextRideActionUtc)
                    {
                        nextRideActionUtc = now.AddSeconds(2);
                        if (TryUseGeneralAction(DismountGeneralActionId, out _))
                            rideOwnsMount = true;
                    }
                    return true;
                }
                if (now < nextRideActionUtc || Plugin.Condition[ConditionFlag.Casting])
                    return true;
                nextRideActionUtc = now.AddSeconds(2);
                if (SummonRideMount(command.MountId))
                    rideOwnsMount = true;
                else
                    CancelHealRider("The selected passenger mount could not be summoned");
                return true;

            case "Boarding":
                if (!mounted || !SelectedMountIsActive(command.MountId))
                {
                    CancelHealRider("The selected mount was dismounted before boarding");
                    return true;
                }
                if (HealRiderPolicy.CanDepart(boardingConfirmed, ExactPassengerIsAboard(command, quester), RidePartyContains(command)))
                    rideStatus = rideStatus with { State = "Transit" };
                else if (!HealRiderPolicy.GroundedForBoarding(Vector3.Distance(local.Position, quester.Position),
                             Plugin.Condition[ConditionFlag.InFlight], mounting, true))
                    rideStatus = rideStatus with { State = "Preparing" };
                return true;

            case "Transit":
                if (!ExactPassengerIsAboard(command, quester))
                {
                    CancelHealRider("The exact Quester left the passenger seat");
                    return true;
                }
                if (Vector3.Distance(local.Position, destination) > HealRiderPolicy.ArrivalTolerance)
                {
                    MoveRideTo(destination, true, HealRiderPolicy.ArrivalTolerance);
                    return true;
                }
                StopRidePath();
                rideStatus = rideStatus with { State = "Arriving" };
                return true;

            case "Arriving":
                if (Vector3.Distance(local.Position, destination) > HealRiderPolicy.ArrivalTolerance)
                {
                    CancelHealRider("Landing moved outside the arrival tolerance");
                    return true;
                }
                if (!mounted && !mounting)
                {
                    FinishRide("Arrived");
                    return true;
                }
                if (now >= nextRideActionUtc)
                {
                    nextRideActionUtc = now.AddSeconds(2);
                    TryUseGeneralAction(DismountGeneralActionId, out _);
                }
                return true;
        }
        return true;
    }

    private void MoveRideTo(Vector3 destination, bool fly, float tolerance)
    {
        if (!TryGetRouteActivity(out var finding, out var running))
        {
            CancelHealRider("Ride navigation status is unavailable");
            return;
        }
        var activity = rideRoute.Observe(finding, running, DateTime.UtcNow);
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
            if (moveCloseTo.InvokeFunc(destination, fly, tolerance))
                rideRoute.MarkStartupAccepted(DateTime.UtcNow);
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
        rideStatus = rideStatus with { State = state };
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
        Plugin.PartyList.Length == 2 && Plugin.PartyList.Any(member => member.Name.ToString() == command.QuesterName &&
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
        return player != null && ((Character*)player.Address)->Mount.MountId == mountId;
    }

    private static unsafe bool ExactPassengerIsAboard(HealRiderCommand command, IPlayerCharacter quester)
    {
        var player = Plugin.ObjectTable.LocalPlayer;
        if (player == null || !SelectedMountIsActive(command.MountId))
            return false;
        foreach (var passenger in ((Character*)player.Address)->Mount.MountedEntityIds)
            if (passenger == quester.EntityId)
                return true;
        return false;
    }
}

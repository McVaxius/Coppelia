using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace Coppelia.Models;

// Separate from QST v3: old follow/duty peers remain compatible.
internal sealed record HealRiderCommand
{
    public int Version { get; init; }
    public string Action { get; init; } = string.Empty;
    public string SessionId { get; init; } = string.Empty;
    public string QuesterName { get; init; } = string.Empty;
    public ushort QuesterWorldId { get; init; }
    public ushort CurrentWorldId { get; init; }
    public uint TerritoryId { get; init; }
    public long LegId { get; init; }
    public float X { get; init; }
    public float Y { get; init; }
    public float Z { get; init; }
    public bool HasPickupLocation { get; init; }
    public float PickupX { get; init; }
    public float PickupY { get; init; }
    public float PickupZ { get; init; }
    public bool ContinueMounted { get; init; }
    public uint TargetTerritoryId { get; init; }
    public long ContinuationId { get; init; }
    public uint QuesterTerritoryId { get; init; }
    public bool QuesterLoading { get; init; }
    public float DestinationTolerance { get; init; } = 5f;
    public float WalkingThreshold { get; init; } = 50;
    public bool QuesterCanFly { get; init; }
    public bool PassengerConfirmed { get; init; }
    public uint MountId { get; init; }
    public string PartyInviter { get; init; } = string.Empty;
    public DateTime PickupDeadlineUtc { get; init; }
}

internal sealed record HealRiderStatus
{
    public int Version { get; init; } = 1;
    public string SessionId { get; init; } = string.Empty;
    public long LegId { get; init; }
    public bool Accepted { get; init; }
    public string State { get; init; } = "Idle";
    public string Blocker { get; init; } = string.Empty;
    public bool CanFly { get; init; }
    public bool MountReady { get; init; }
    public bool PickupReady { get; init; }
    public bool BoardingReady { get; init; }
    public bool TransportOwnsMount { get; init; }
    public bool Mounted { get; init; }
    public bool PartyReady { get; init; }
    public bool PartyReleased { get; init; }
    public bool PartyOwned { get; init; }
    public long ContinuationId { get; init; }
    public uint TerritoryId { get; init; }
    public bool PassengerConfirmed { get; init; }
}

internal static class HealRiderPolicy
{
    public static (string State, string Blocker) AdvanceTransport(string state, float pickupDistance,
        float destinationDistance, bool mounted, bool mounting, bool flying, bool selectedMount,
        bool passengerConfirmed, bool nativePassenger, bool partyReady, Func<string, string> issue)
    {
        switch (state)
        {
            case "Preparing":
                if (!mounted || !selectedMount || mounting)
                {
                    issue("Stop");
                    return issue("PrepareTravelMount") == "Blocked"
                        ? ("Cancelling", "The selected passenger mount could not be prepared.") : (state, string.Empty);
                }
                if (pickupDistance > BoardingTolerance)
                {
                    issue("Approach");
                    return (state, string.Empty);
                }
                issue("Stop");
                return issue("PrepareMount") switch
                {
                    "Ready" => ("Boarding", string.Empty),
                    "Blocked" => ("Cancelling", "The selected passenger mount could not be prepared."),
                    _ => (state, string.Empty),
                };
            case "Boarding":
                if (!mounted || !selectedMount)
                    return ("Cancelling", "The selected mount was dismounted before boarding.");
                if (CanDepart(passengerConfirmed, nativePassenger, partyReady))
                {
                    issue("Travel");
                    return ("Transit", string.Empty);
                }
                // Seat attachment can move the Quester beyond the grounded pickup radius
                // before both endpoints observe it. Keep boarding until they agree or expire.
                if (passengerConfirmed || nativePassenger)
                    return (state, passengerConfirmed ? "Waiting for the Helper's native passenger observation."
                        : "Waiting for the Quester's passenger confirmation.");
                return GroundedForBoarding(pickupDistance, flying, mounting, true)
                    ? (state, "Waiting for both passenger confirmations.") : ("Preparing", string.Empty);
            case "Transit":
                if (!mounted || !selectedMount || !CanDepart(passengerConfirmed, nativePassenger, partyReady))
                    return ("Cancelling", "The exact Quester left the passenger seat or party.");
                if (destinationDistance > ArrivalTolerance)
                {
                    issue("Travel");
                    return (state, string.Empty);
                }
                issue("Stop");
                return ("Arriving", string.Empty);
            case "Arriving":
                if (destinationDistance > ArrivalTolerance)
                    return ("Cancelling", "Landing moved outside the arrival tolerance.");
                if (!mounted && !mounting)
                    return ("Arrived", string.Empty);
                issue("Dismount");
                return (state, string.Empty);
            default:
                return (state, string.Empty);
        }
    }

    public static string PrepareMount(DateTime now, DateTime deadline, ref DateTime nextAction,
        bool mounted, bool mounting, bool casting, bool flying, bool selected, bool groundRequired,
        Func<string, bool> issue)
    {
        if (now >= deadline) return "Blocked";
        if (mounting || casting) return "Waiting";
        if (mounted && selected && (!groundRequired || !flying)) return "Ready";
        if (now < nextAction) return "Waiting";
        nextAction = now.AddSeconds(2);
        return issue(flying ? "Land" : mounted ? "Dismount" : "Summon") ? "Waiting" : "Blocked";
    }

    public const float ArrivalTolerance = 5f;
    public const float BoardingTolerance = 3f;

    public static bool AcknowledgementExpired(DateTime sent, DateTime now) => now - sent >= TimeSpan.FromSeconds(5);

    public static bool PartyStillOwned(ulong local, ulong owner, IEnumerable<ulong> members, IReadOnlySet<ulong> captured) =>
        local != 0 && local == owner && members.All(captured.Contains);

    public static DateTime BeginCleanup(DateTime? deadline, DateTime now) => deadline ?? now.AddSeconds(60);

    public static bool ConfirmSolo(bool solo, bool promptVisible, DateTime now, ref DateTime? since)
    {
        if (!solo || promptVisible)
        {
            since = null;
            return false;
        }
        since ??= now;
        return now - since.Value >= TimeSpan.FromSeconds(1);
    }

    public static bool CanRequestPickup(bool rotationActive, bool inCombat, bool passenger) =>
        rotationActive && !inCombat && !passenger;

    public static bool CanAcceptPickup(HealRiderCommand requested, HealRiderCommand observed,
        DateTime now, bool sameStep, bool sameExecution) =>
        now < requested.PickupDeadlineUtc && sameStep && sameExecution && SameLeg(requested, observed);

    public static bool CleanupComplete(bool remoteReleased, string remoteState, bool ownsMount,
        bool mounted, bool passenger, bool localComplete, bool statusFresh) =>
        remoteReleased && remoteState is "Idle" or "Arrived" or "Cancelled" &&
        !MustWaitForDismount(ownsMount, mounted) && !passenger && localComplete && statusFresh;

    public static bool PickupExpired(DateTime deadlineUtc, DateTime now, string state) =>
        now >= deadlineUtc && state is not ("Transit" or "ZoneTransition" or "WaitingContinuation" or "Arriving" or "Arrived");

    public static bool ShouldInvite(DateTime now, DateTime deadlineUtc, DateTime nextInviteUtc,
        bool travelReady, bool partyReady, bool solo) =>
        now < deadlineUtc && now >= nextInviteUtc && travelReady && !partyReady && solo;

    public static bool GroundedForBoarding(float distance, bool flying, bool mounting, bool selectedMount) =>
        float.IsFinite(distance) && distance <= BoardingTolerance && !flying && !mounting && selectedMount;

    public static bool MustWaitForDismount(bool transportOwnsMount, bool mounted) =>
        transportOwnsMount && mounted;

    public static bool Eligible(float distance, float threshold, bool helperCanFly, bool questerCanFly,
        bool mountReady, bool sameLocation) =>
        float.IsFinite(distance) && float.IsFinite(threshold) && threshold >= ArrivalTolerance &&
        distance > threshold && helperCanFly && !questerCanFly && mountReady && sameLocation;

    public static bool CanDepart(bool questerConfirmed, bool exactPassengerOnSelectedMount, bool partyReady) =>
        questerConfirmed && exactPassengerOnSelectedMount && partyReady;

    public static bool CanRegroup(bool mounted, bool passenger, bool partyReleased) =>
        !mounted && !passenger && partyReleased;

    public static bool SameLeg(HealRiderCommand active, HealRiderCommand next) =>
        SameRide(active, next) && active.ContinuationId == next.ContinuationId &&
        active.CurrentWorldId == next.CurrentWorldId && active.TerritoryId == next.TerritoryId &&
        active.X == next.X && active.Y == next.Y && active.Z == next.Z &&
        active.HasPickupLocation == next.HasPickupLocation &&
        active.PickupX == next.PickupX && active.PickupY == next.PickupY && active.PickupZ == next.PickupZ &&
        active.ContinueMounted == next.ContinueMounted && active.TargetTerritoryId == next.TargetTerritoryId &&
        active.DestinationTolerance == next.DestinationTolerance;

    public static bool SameRide(HealRiderCommand active, HealRiderCommand next) =>
        active.SessionId == next.SessionId && active.QuesterName == next.QuesterName &&
        active.QuesterWorldId == next.QuesterWorldId && active.LegId == next.LegId;

    public static Vector3 Pickup(HealRiderCommand command) => new(command.PickupX, command.PickupY, command.PickupZ);

    public static Vector3 Destination(HealRiderCommand command) => new(command.X, command.Y, command.Z);
}

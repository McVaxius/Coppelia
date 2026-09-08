using System;
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
}

internal static class HealRiderPolicy
{
    public const float ArrivalTolerance = 5f;
    public const float BoardingTolerance = 3f;

    public static bool PickupExpired(DateTime deadlineUtc, DateTime now, string state) =>
        now >= deadlineUtc && state is not ("Transit" or "Arriving" or "Arrived");

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
        active.SessionId == next.SessionId && active.QuesterName == next.QuesterName &&
        active.QuesterWorldId == next.QuesterWorldId && active.LegId == next.LegId &&
        active.CurrentWorldId == next.CurrentWorldId && active.TerritoryId == next.TerritoryId &&
        active.X == next.X && active.Y == next.Y && active.Z == next.Z;

    public static Vector3 Destination(HealRiderCommand command) => new(command.X, command.Y, command.Z);
}

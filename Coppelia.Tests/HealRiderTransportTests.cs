using System.Numerics;
using System.Text.Json;
using Coppelia.Models;

namespace Coppelia.Tests;

public sealed class HealRiderTransportTests
{
    private static readonly DateTime Started = new(2026, 9, 7, 0, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData(50, true, false, true, true, false)]
    [InlineData(51, true, false, true, true, true)]
    [InlineData(100, false, false, true, true, false)]
    [InlineData(100, true, true, true, true, false)]
    [InlineData(100, true, false, false, true, false)]
    [InlineData(100, true, false, true, false, false)]
    public void PickupRequiresStrictDistanceFlightMountAndLocation(
        float distance, bool helperFlies, bool questerFlies, bool mountReady, bool sameLocation, bool expected) =>
        Assert.Equal(expected, HealRiderPolicy.Eligible(distance, 50, helperFlies, questerFlies, mountReady, sameLocation));

    [Fact]
    public void InvalidCoordinatesOrThresholdCannotEnablePickup()
    {
        Assert.False(HealRiderPolicy.Eligible(float.NaN, 50, true, false, true, true));
        Assert.False(HealRiderPolicy.Eligible(100, float.NaN, true, false, true, true));
        Assert.False(HealRiderPolicy.Eligible(100, -1, true, false, true, true));
    }

    [Theory]
    [InlineData(false, true, true, false)]
    [InlineData(true, false, true, false)]
    [InlineData(true, true, false, false)]
    [InlineData(true, true, true, true)]
    public void BoardingNeedsBothQuesterAcknowledgementAndExactNativePassenger(
        bool confirmed, bool nativePassenger, bool partyReady, bool expected) =>
        Assert.Equal(expected, HealRiderPolicy.CanDepart(confirmed, nativePassenger, partyReady));

    [Theory]
    [InlineData(true, false, true, false)]
    [InlineData(false, true, true, false)]
    [InlineData(false, false, false, false)]
    [InlineData(false, false, true, true)]
    public void DismountAndPartyReleaseMustFinishBeforeRegroup(
        bool mounted, bool passenger, bool released, bool expected) =>
        Assert.Equal(expected, HealRiderPolicy.CanRegroup(mounted, passenger, released));

    [Fact]
    public void ChangedAssignmentIdentityLegOrDestinationRequiresCancellation()
    {
        var command = new HealRiderCommand
        {
            Version = 1, Action = "Pickup", SessionId = "test-session", LegId = 1,
            QuesterName = "Test Quester", QuesterWorldId = 1, CurrentWorldId = 1,
            TerritoryId = 399, X = 60, Y = 4, Z = 10,
        };
        Assert.True(HealRiderPolicy.SameLeg(command, command with { Action = "Inspect", PassengerConfirmed = true }));
        Assert.False(HealRiderPolicy.SameLeg(command, command with { SessionId = "replacement-session" }));
        Assert.False(HealRiderPolicy.SameLeg(command, command with { QuesterName = "Other Quester" }));
        Assert.False(HealRiderPolicy.SameLeg(command, command with { QuesterWorldId = 2 }));
        Assert.False(HealRiderPolicy.SameLeg(command, command with { LegId = 2 }));
        Assert.False(HealRiderPolicy.SameLeg(command, command with { CurrentWorldId = 2 }));
        Assert.False(HealRiderPolicy.SameLeg(command, command with { TerritoryId = 398 }));
        Assert.False(HealRiderPolicy.SameLeg(command, command with { X = 61 }));

        var requestJson = JsonSerializer.Serialize(command, CoppeliaQstContract.JsonOptions);
        Assert.Equal(command, JsonSerializer.Deserialize<HealRiderCommand>(requestJson, CoppeliaQstContract.JsonOptions));
        Assert.Equal(3, CoppeliaQstContract.Version);
    }

    [Fact]
    public void FailedOwnedPathfindProbesBeforeNewTerritorySnapshotAndKeepsOriginalDeadline()
    {
        var route = new CoppeliaRoutePolicy();
        route.AcceptSnapshot(1, new Vector3(100, 0, 0));
        route.MarkStartupAccepted(Started);
        route.Observe(true, false, Started.AddMilliseconds(100));
        var failure = route.Observe(false, false, Started.AddMilliseconds(200));
        Assert.Equal(CoppeliaRouteActivity.Rejected, failure);
        var handoff = new CoppeliaTerritoryHandoffPolicy();
        Assert.True(handoff.TryArmFollowFailure(100, failure == CoppeliaRouteActivity.Rejected));
        Assert.Equal(CoppeliaTerritoryHandoffDecision.StartForwardProbe,
            handoff.Evaluate(100, 100, false, true, TimeSpan.FromSeconds(5), Started));
        Assert.False(handoff.TryArm(true, false, false, 100, 200));
        Assert.Equal(CoppeliaTerritoryHandoffDecision.ContinueForwardProbe,
            handoff.Evaluate(100, 200, false, true, TimeSpan.FromSeconds(20), Started.AddSeconds(4)));
        Assert.Equal(CoppeliaTerritoryHandoffDecision.UseTeleportFallback,
            handoff.Evaluate(100, 200, false, true, TimeSpan.FromSeconds(20), Started.AddSeconds(5)));
        Assert.False(handoff.TryArmFollowFailure(100, true));
    }

    [Fact]
    public void FailureProbeStopsAtActualTransitionAndUsesNewestDestination()
    {
        var handoff = new CoppeliaTerritoryHandoffPolicy();
        Assert.True(handoff.TryArmFollowFailure(100, true));
        handoff.Evaluate(100, 100, false, true, TimeSpan.FromSeconds(5), Started);
        Assert.Equal(CoppeliaTerritoryHandoffDecision.WaitForLoad,
            handoff.Evaluate(100, 100, true, false, TimeSpan.FromSeconds(5), Started.AddSeconds(1)));
        Assert.Equal(CoppeliaTerritoryHandoffDecision.DestinationReached,
            handoff.Evaluate(200, 200, false, true, TimeSpan.FromSeconds(5), Started.AddSeconds(2)));
        Assert.False(handoff.IsActive);
    }

    [Fact]
    public void FinishedRideCannotFollowOldPickupUntilANewerSnapshotArrives()
    {
        var snapshots = new CoppeliaTravelSequencePolicy();
        Assert.True(snapshots.TryAccept(10));
        snapshots.RequireFreshSnapshot();
        Assert.True(snapshots.WaitingForFreshSnapshot);
        Assert.False(snapshots.TryAccept(10));
        Assert.False(snapshots.TryAccept(9));
        Assert.True(snapshots.WaitingForFreshSnapshot);
        Assert.True(snapshots.TryAccept(11));
        Assert.False(snapshots.WaitingForFreshSnapshot);
        snapshots.RequireFreshSnapshot();
        snapshots.Reset();
        Assert.False(snapshots.WaitingForFreshSnapshot);
    }

    [Fact]
    public void UnrelatedActivityCannotArmOwnedFailureRecoveryAndCancellationReleasesPendingMovement()
    {
        var handoff = new CoppeliaTerritoryHandoffPolicy();
        Assert.False(handoff.TryArmFollowFailure(100, false));
        var route = new CoppeliaRoutePolicy();
        Assert.Equal(CoppeliaRouteActivity.Other, route.Observe(true, false, Started));
        Assert.Equal(CoppeliaRouteInterruption.None, route.Release(true, false));
        route.AcceptSnapshot(1, new Vector3(100, 0, 0));
        route.MarkStartupAccepted(Started);
        route.Observe(true, false, Started);
        Assert.Equal(CoppeliaRouteInterruption.StopPathAndCancelPending, route.Release(true, false));
        Assert.False(route.OwnsRoute);
        Assert.False(route.HasDestination);
    }
}

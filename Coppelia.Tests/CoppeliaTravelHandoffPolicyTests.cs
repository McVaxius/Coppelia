using System.Numerics;
using Coppelia.Models;

namespace Coppelia.Tests;

public sealed class CoppeliaTravelHandoffPolicyTests
{
    private static readonly DateTime Started = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void CentralThanalanStyleArrTerritoryProbesFlightAndCanProveIt()
    {
        var eligibility = CoppeliaFlightPolicy.Classify(
            mountable: true,
            hasStandardAetherCurrentSet: false,
            standardSetComplete: false,
            isArrTerritory: true);
        var policy = new CoppeliaFlightPolicy();
        policy.EnterTerritory(141, eligibility);

        Assert.Equal(CoppeliaFlightEligibility.UnknownButMountable, eligibility);
        Assert.True(policy.ShouldUseFlight);

        policy.MarkRouteQueued(useFlight: true);
        Assert.True(policy.IsProbePending);
        policy.ObserveRouteActivity(active: true);

        Assert.Equal(CoppeliaFlightProbeState.Proven, policy.ProbeState);
        Assert.True(policy.ShouldUseFlight);
    }

    [Fact]
    public void RejectedArrProbeUsesGroundFallbackUntilTerritoryResets()
    {
        var policy = new CoppeliaFlightPolicy();
        policy.EnterTerritory(141, CoppeliaFlightEligibility.UnknownButMountable);
        policy.MarkRouteQueued(useFlight: true);
        policy.MarkProbeRejected();

        Assert.Equal(CoppeliaFlightProbeState.Rejected, policy.ProbeState);
        Assert.False(policy.ShouldUseFlight);

        policy.EnterTerritory(141, CoppeliaFlightEligibility.UnknownButMountable);
        Assert.False(policy.ShouldUseFlight);

        policy.EnterTerritory(145, CoppeliaFlightEligibility.UnknownButMountable);
        Assert.Equal(CoppeliaFlightProbeState.None, policy.ProbeState);
        Assert.True(policy.ShouldUseFlight);
    }

    [Theory]
    [InlineData(false, false, false, true, 0)]
    [InlineData(true, true, false, false, 0)]
    [InlineData(true, true, true, false, 1)]
    [InlineData(true, false, false, false, 0)]
    public void StandardFlightClassificationIsExplicit(
        bool mountable,
        bool hasStandardSet,
        bool standardComplete,
        bool isArr,
        int expected)
    {
        Assert.Equal(
            (CoppeliaFlightEligibility)expected,
            CoppeliaFlightPolicy.Classify(mountable, hasStandardSet, standardComplete, isArr));
    }

    [Fact]
    public void NearestUnlockedAetheryteUsesNewestTerritoryCoordinates()
    {
        var destination = new Vector3(100, 0, 100);
        var candidates = new[]
        {
            new CoppeliaAetheryteCandidate(1, new Vector3(95, 0, 95), Unlocked: false),
            new CoppeliaAetheryteCandidate(2, new Vector3(40, 0, 40), Unlocked: true),
            new CoppeliaAetheryteCandidate(3, new Vector3(110, 0, 110), Unlocked: true),
        };

        var nearest = CoppeliaAetherytePolicy.SelectNearest(destination, candidates);

        Assert.NotNull(nearest);
        Assert.Equal((uint)3, nearest.Id);
    }

    [Fact]
    public void AcceptedRequestThatNeverBecomesBusyFailsAndWaitsForNewerSequence()
    {
        var policy = new CoppeliaLifestreamRequestPolicy();
        policy.Begin(sequence: 4, accepted: true, utcNow: Started);

        Assert.Equal(
            CoppeliaLifestreamActivity.WaitingForBusy,
            policy.Observe(busy: false, targetReached: false, utcNow: Started.AddSeconds(1)));
        Assert.Equal(
            CoppeliaLifestreamActivity.Failed,
            policy.Observe(busy: false, targetReached: false, utcNow: Started + CoppeliaLifestreamRequestPolicy.BusyStartupTimeout));
        Assert.False(policy.CanReplaceWith(4));
        Assert.True(policy.CanReplaceWith(5));
    }

    [Fact]
    public void BusyRequestThatFinishesOutsideTargetFailsInsteadOfStalling()
    {
        var policy = new CoppeliaLifestreamRequestPolicy();
        policy.Begin(sequence: 7, accepted: true, utcNow: Started);

        Assert.Equal(
            CoppeliaLifestreamActivity.Busy,
            policy.Observe(busy: true, targetReached: false, utcNow: Started.AddMilliseconds(100)));
        Assert.Equal(
            CoppeliaLifestreamActivity.Failed,
            policy.Observe(busy: false, targetReached: false, utcNow: Started.AddSeconds(1)));
    }

    [Fact]
    public void WorldOrTerritoryArrivalCompletesTheOwnedRequest()
    {
        var policy = new CoppeliaLifestreamRequestPolicy();
        policy.Begin(sequence: 9, accepted: true, utcNow: Started);

        Assert.Equal(
            CoppeliaLifestreamActivity.Completed,
            policy.Observe(busy: false, targetReached: true, utcNow: Started.AddMilliseconds(10)));
    }
}

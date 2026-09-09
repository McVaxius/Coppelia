using Coppelia.Models;

namespace Coppelia.Tests;

public sealed class CoppeliaFollowPolicyTests
{
    [Theory]
    [InlineData(true, true, false, true)]
    [InlineData(true, false, true, true)]
    [InlineData(true, false, false, false)]
    [InlineData(false, true, true, false)]
    public void PairedActionsHoldOnlyWhileMountedOrMounting(
        bool paired,
        bool mounted,
        bool mounting,
        bool expected)
    {
        Assert.Equal(expected, CoppeliaPairedActionPolicy.ShouldHold(paired, mounted, mounting));
    }

    [Fact]
    public void MountedFollowResumesAtTenAndStopsAtFive()
    {
        var policy = new CoppeliaFollowPolicy();

        Assert.False(Evaluate(policy, 9.99f, questerMounted: true, helperMounted: true).IsFollowing);
        Assert.True(Evaluate(policy, 10f, questerMounted: true, helperMounted: true).IsFollowing);
        Assert.True(Evaluate(policy, 5.1f, questerMounted: true, helperMounted: true).IsFollowing);
        Assert.False(Evaluate(policy, 5f, questerMounted: true, helperMounted: true).IsFollowing);
        Assert.False(Evaluate(policy, 9.99f, questerMounted: true, helperMounted: true).IsFollowing);
        Assert.True(Evaluate(policy, 10.01f, questerMounted: true, helperMounted: true).IsFollowing);
    }

    [Fact]
    public void OnFootFollowResumesAtTenAndStopsAtNine()
    {
        var policy = new CoppeliaFollowPolicy();

        Assert.False(Evaluate(policy, 9.99f).IsFollowing);
        Assert.True(Evaluate(policy, 10f).IsFollowing);
        Assert.True(Evaluate(policy, 9.01f).IsFollowing);
        Assert.False(Evaluate(policy, 9f).IsFollowing);
        Assert.False(Evaluate(policy, 9.99f).IsFollowing);
        Assert.True(Evaluate(policy, 10.01f).IsFollowing);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void PassengerMountPreferenceDoesNotChangeFollowDistances(bool questerMounted, bool keepPassengerMount)
    {
        var policy = new CoppeliaFollowPolicy();
        CoppeliaFollowDecision Decide(float distance) => policy.Evaluate(
            distance, questerMounted, false, questerMounted, false, false, false,
            keepPassengerMount: keepPassengerMount);

        Assert.False(Decide(9.99f).IsFollowing);
        foreach (var distance in new[] { 10f, 10.01f, 15f, 20f })
        {
            policy.Reset();
            var approach = Decide(distance);
            Assert.Equal(CoppeliaFollowPhase.Follow, approach.Phase);
            Assert.True(approach.IsFollowing);
            Assert.Equal(questerMounted ? 5f : 9f, approach.RouteRange);
        }

        var stopDistance = questerMounted ? 5f : 9f;
        Assert.True(Decide(stopDistance + .1f).IsFollowing);
        Assert.False(Decide(stopDistance).IsFollowing);
        Assert.False(Decide(9f).IsFollowing);
        Assert.True(Decide(10f).IsFollowing);
    }

    [Fact]
    public void LatchSurvivesMountFlightLandingAndDismountTransitions()
    {
        var policy = new CoppeliaFollowPolicy();

        Assert.True(Evaluate(policy, 25f).IsFollowing);
        Assert.Equal(
            CoppeliaFollowPhase.WaitForMount,
            Evaluate(policy, 15f, helperMounting: true).Phase);
        Assert.True(policy.IsFollowing);

        var mounted = Evaluate(
            policy,
            12f,
            questerMounted: true,
            helperMounted: true,
            flightAvailable: true);
        Assert.Equal(CoppeliaFollowPhase.Follow, mounted.Phase);
        Assert.True(mounted.IsFollowing);

        var landing = Evaluate(
            policy,
            12f,
            helperMounted: true,
            helperFlying: true,
            flightAvailable: true);
        Assert.Equal(CoppeliaFollowPhase.Land, landing.Phase);
        Assert.True(landing.IsFollowing);

        var dismounting = Evaluate(policy, 12f, helperMounted: true);
        Assert.Equal(CoppeliaFollowPhase.Dismount, dismounting.Phase);
        Assert.True(dismounting.IsFollowing);
    }

    [Fact]
    public void EveryPermittedMountedChaseUsesFlightAndFiveYalmRouteTolerance()
    {
        var mountedQuester = new CoppeliaFollowPolicy();
        var mountedDecision = Evaluate(
            mountedQuester,
            40f,
            questerMounted: true,
            helperMounted: true,
            flightAvailable: true);

        Assert.Equal(CoppeliaFollowPhase.Follow, mountedDecision.Phase);
        Assert.True(mountedDecision.UseFlight);
        Assert.Equal(5f, mountedDecision.RouteRange);

        var groundFallback = new CoppeliaFollowPolicy();
        var fallbackDecision = Evaluate(
            groundFallback,
            40f,
            questerMounted: true,
            helperMounted: true,
            flightAvailable: false);
        Assert.Equal(CoppeliaFollowPhase.Follow, fallbackDecision.Phase);
        Assert.False(fallbackDecision.UseFlight);
        Assert.Equal(5f, fallbackDecision.RouteRange);

        var catchUp = new CoppeliaFollowPolicy();
        var catchUpDecision = Evaluate(
            catchUp,
            60f,
            helperMounted: true,
            flightAvailable: true);

        Assert.Equal(CoppeliaFollowPhase.Follow, catchUpDecision.Phase);
        Assert.True(catchUpDecision.UseFlight);
        Assert.Equal(5f, catchUpDecision.RouteRange);
    }

    [Fact]
    public void OnFootCatchUpFliesToTwentyThenLandsDismountsAndFinishesOnFoot()
    {
        var policy = new CoppeliaFollowPolicy();

        var flying = Evaluate(
            policy,
            60f,
            helperMounted: true,
            flightAvailable: true);
        Assert.Equal(CoppeliaFollowPhase.Follow, flying.Phase);
        Assert.True(flying.UseFlight);

        var landing = Evaluate(
            policy,
            20f,
            helperMounted: true,
            helperFlying: true,
            flightAvailable: true);
        Assert.Equal(CoppeliaFollowPhase.Land, landing.Phase);

        var dismounting = Evaluate(policy, 20f, helperMounted: true);
        Assert.Equal(CoppeliaFollowPhase.Dismount, dismounting.Phase);

        var groundFinish = Evaluate(policy, 15f);
        Assert.Equal(CoppeliaFollowPhase.Follow, groundFinish.Phase);
        Assert.False(groundFinish.UseFlight);
        Assert.Equal(9f, groundFinish.RouteRange);

        Assert.Equal(CoppeliaFollowPhase.Idle, Evaluate(policy, 9f).Phase);
    }

    [Fact]
    public void GroundedMountedQuesterLandsAtFiveWithoutDismounting()
    {
        var policy = new CoppeliaFollowPolicy();

        var flying = Evaluate(
            policy,
            40f,
            questerMounted: true,
            helperMounted: true,
            helperFlying: true,
            flightAvailable: true);
        Assert.Equal(CoppeliaFollowPhase.Follow, flying.Phase);
        Assert.True(flying.UseFlight);

        Assert.Equal(
            CoppeliaFollowPhase.Land,
            Evaluate(
                policy,
                5f,
                questerMounted: true,
                helperMounted: true,
                helperFlying: true,
                flightAvailable: true).Phase);
        Assert.Equal(
            CoppeliaFollowPhase.Idle,
            Evaluate(policy, 5f, questerMounted: true, helperMounted: true).Phase);
    }

    [Fact]
    public void FlyingQuesterKeepsHelperAirborneAtMountedStopDistance()
    {
        var policy = new CoppeliaFollowPolicy();

        Assert.Equal(
            CoppeliaFollowPhase.Follow,
            Evaluate(
                policy,
                40f,
                questerMounted: true,
                questerFlying: true,
                helperMounted: true,
                helperFlying: true,
                flightAvailable: true).Phase);

        var arrived = Evaluate(
            policy,
            5f,
            questerMounted: true,
            questerFlying: true,
            helperMounted: true,
            helperFlying: true,
            flightAvailable: true);
        Assert.Equal(CoppeliaFollowPhase.Idle, arrived.Phase);
        Assert.False(arrived.IsFollowing);
    }

    [Fact]
    public void CatchUpMountingStartsOnlyBeyondFifty()
    {
        var atThreshold = new CoppeliaFollowPolicy();
        Assert.Equal(CoppeliaFollowPhase.Follow, Evaluate(atThreshold, 50f).Phase);

        var beyondThreshold = new CoppeliaFollowPolicy();
        Assert.Equal(CoppeliaFollowPhase.Mount, Evaluate(beyondThreshold, 50.1f).Phase);
    }

    private static CoppeliaFollowDecision Evaluate(
        CoppeliaFollowPolicy policy,
        float distance,
        bool questerMounted = false,
        bool questerFlying = false,
        bool helperMounted = false,
        bool helperMounting = false,
        bool helperFlying = false,
        bool flightAvailable = false)
        => policy.Evaluate(
            distance,
            questerMounted,
            questerFlying,
            helperMounted,
            helperMounting,
            helperFlying,
            flightAvailable);
}

using System.Numerics;
using Coppelia.Models;
using Coppelia.Services;

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
    public void HawthorneHutInAuthoritativeTeleportListIsSelectedForEastShroud()
    {
        const uint eastShroudTerritoryId = 152;
        var newestQuesterPosition = new Vector3(-180, 2, 275);
        var defaults = DefaultAetheryteData.GetDefaults();
        Assert.Equal(107, defaults.Count);
        Assert.Equal(new Vector3(-188.9f, 2f, 283.5f), new Vector3(defaults[4].X, defaults[4].Y, defaults[4].Z));
        var teleportList = new[]
        {
            Candidate(2, 0, 132, defaults[2]),
            Candidate(4, 0, eastShroudTerritoryId, defaults[4]),
            Candidate(5, 0, 153, defaults[5]),
        };

        var nearest = CoppeliaAetherytePolicy.Resolve(
            eastShroudTerritoryId,
            newestQuesterPosition,
            teleportList,
            avoidTamamizu: true);

        Assert.NotNull(nearest);
        Assert.Equal((uint)4, nearest.Candidate.Id);
        Assert.Equal((byte)0, nearest.Candidate.SubIndex);
        Assert.Equal("The Hawthorne Hut", nearest.Candidate.Name);
    }

    [Fact]
    public void NonTeleportTerritoryFallbackRearmsFromNewerPostArrivalSnapshot()
    {
        var sequencePolicy = new CoppeliaTravelSequencePolicy();
        var destinationTerritoryAetherytes = new[]
        {
            new CoppeliaAetheryteCandidate(10, 0, 200, "Near", 0, 100, new Vector3(5, 0, 5), true),
            new CoppeliaAetheryteCandidate(20, 0, 200, "Far", 0, 100, new Vector3(95, 0, 95), true),
        };

        Assert.True(sequencePolicy.TryAccept(40));
        sequencePolicy.Block(40, "Waiting for a newer destination snapshot");
        Assert.True(sequencePolicy.IsBlocked(40));
        Assert.Equal(
            (uint)10,
            CoppeliaAetherytePolicy.Resolve(
                200,
                new Vector3(10, 0, 10),
                destinationTerritoryAetherytes,
                avoidTamamizu: true)!.Candidate.Id);

        Assert.True(sequencePolicy.TryAccept(41));
        Assert.False(sequencePolicy.IsBlocked(41));
        Assert.Equal(
            (uint)20,
            CoppeliaAetherytePolicy.Resolve(
                200,
                new Vector3(90, 0, 90),
                destinationTerritoryAetherytes,
                avoidTamamizu: true)!.Candidate.Id);
        Assert.False(CoppeliaTeleportIntentPolicy.IsStale(40, 100, 200, 41, 100));
        Assert.True(CoppeliaTeleportIntentPolicy.IsStale(40, 100, 200, 41, 300));
    }

    [Fact]
    public void ForwardProbeKeepsOneFiveSecondWindowAcrossNewerSnapshots()
    {
        var policy = new CoppeliaTerritoryHandoffPolicy();

        Assert.True(policy.TryArm(true, false, false, true, 100, 100, 200));
        Assert.Equal(
            CoppeliaTerritoryHandoffDecision.StartForwardProbe,
            policy.Evaluate(100, 200, betweenAreas: false, helperLoaded: true, Started));
        Assert.False(policy.TryArm(true, false, false, true, 100, 100, 300));
        Assert.Equal(Started, policy.ProbeStartedUtc);
        Assert.Equal(
            CoppeliaTerritoryHandoffDecision.ContinueForwardProbe,
            policy.Evaluate(100, 300, betweenAreas: false, helperLoaded: true, Started.AddMilliseconds(4999)));
        Assert.Equal(
            CoppeliaTerritoryHandoffDecision.UseTeleportFallback,
            policy.Evaluate(100, 300, betweenAreas: false, helperLoaded: true, Started.AddSeconds(5)));
    }

    [Fact]
    public void ObservedTransitionWaitsForLoadAndCompletesInLatestTerritory()
    {
        var policy = new CoppeliaTerritoryHandoffPolicy();
        Assert.True(policy.TryArm(true, false, false, true, 100, 100, 200));
        Assert.Equal(
            CoppeliaTerritoryHandoffDecision.StartForwardProbe,
            policy.Evaluate(100, 200, betweenAreas: false, helperLoaded: true, Started));

        Assert.Equal(
            CoppeliaTerritoryHandoffDecision.WaitForLoad,
            policy.Evaluate(100, 200, betweenAreas: true, helperLoaded: false, Started.AddSeconds(1)));
        Assert.Equal(
            CoppeliaTerritoryHandoffDecision.DestinationReached,
            policy.Evaluate(200, 200, betweenAreas: false, helperLoaded: true, Started.AddSeconds(2)));
        Assert.False(policy.IsActive);
    }

    [Fact]
    public void WrongIntermediateTerritoryUsesLatestDestinationFallback()
    {
        var policy = new CoppeliaTerritoryHandoffPolicy();
        Assert.True(policy.TryArm(true, false, false, true, 100, 100, 200));
        Assert.Equal(
            CoppeliaTerritoryHandoffDecision.StartForwardProbe,
            policy.Evaluate(100, 200, betweenAreas: false, helperLoaded: true, Started));
        Assert.Equal(
            CoppeliaTerritoryHandoffDecision.WaitForLoad,
            policy.Evaluate(150, 300, betweenAreas: true, helperLoaded: false, Started.AddSeconds(1)));
        Assert.Equal(
            CoppeliaTerritoryHandoffDecision.UseTeleportFallback,
            policy.Evaluate(150, 300, betweenAreas: false, helperLoaded: true, Started.AddSeconds(2)));
    }

    [Theory]
    [InlineData(false, false, false, true, 100, 100, 200)]
    [InlineData(true, false, false, true, 999, 100, 200)]
    [InlineData(true, true, false, true, 100, 100, 200)]
    [InlineData(true, false, true, true, 100, 100, 200)]
    public void InitialReloadExactAndWorldMismatchesDoNotArmBlindProbe(
        bool hasPreviousSnapshot,
        bool explicitTeleport,
        bool worldChanged,
        bool helperLoaded,
        uint observedTerritory,
        uint previousTerritory,
        uint destinationTerritory)
    {
        var policy = new CoppeliaTerritoryHandoffPolicy();

        Assert.False(policy.TryArm(
            hasPreviousSnapshot,
            explicitTeleport,
            worldChanged,
            helperLoaded,
            observedTerritory,
            previousTerritory,
            destinationTerritory));
        Assert.False(policy.IsActive);
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

    private static CoppeliaAetheryteCandidate Candidate(
        uint id,
        byte subIndex,
        uint territoryId,
        AetherytePosition position) =>
        new(
            id,
            subIndex,
            territoryId,
            position.Name,
            0,
            100,
            new Vector3(position.X, position.Y, position.Z),
            true);
}

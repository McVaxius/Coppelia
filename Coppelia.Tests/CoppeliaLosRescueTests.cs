using Coppelia.Models;
using Coppelia.Services;

namespace Coppelia.Tests;

public sealed class CoppeliaLosRescueTests
{
    [Fact]
    public void RescueRequiresVisibleBlockedTargetStrictlyInsideThirtyYalms()
    {
        Assert.True(LineOfSightService.ShouldRescue(true, 29.999f, true));
        Assert.False(LineOfSightService.ShouldRescue(true, 30f, true));
        Assert.False(LineOfSightService.ShouldRescue(true, 10f, false));
        Assert.False(LineOfSightService.ShouldRescue(false, 10f, true));
    }

    [Fact]
    public void PairedRescueIsRemoteAwareBoundedAndSuppressedUntilContextImproves()
    {
        var started = new DateTime(2026, 8, 31, 12, 0, 0, DateTimeKind.Utc);

        var worldChange = new CoppeliaLosRescuePolicy();
        Assert.Equal(CoppeliaLosRescueDecision.Rescue,
            Evaluate(worldChange, started));
        Assert.Equal(CoppeliaLosRescueDecision.CancelRemote,
            Evaluate(worldChange, started.AddSeconds(1), questerWorld: 2));

        var territoryChange = new CoppeliaLosRescuePolicy();
        Assert.Equal(CoppeliaLosRescueDecision.Rescue,
            Evaluate(territoryChange, started));
        Assert.Equal(CoppeliaLosRescueDecision.CancelRemote,
            Evaluate(territoryChange, started.AddSeconds(1), questerTerritory: 2));

        var lifetime = new CoppeliaLosRescuePolicy();
        Assert.Equal(CoppeliaLosRescueDecision.Rescue,
            Evaluate(lifetime, started, distance: 20f));
        Assert.Equal(CoppeliaLosRescueDecision.Rescue,
            Evaluate(lifetime, started.AddMilliseconds(19_999), distance: 29f));
        Assert.Equal(CoppeliaLosRescueDecision.Rescue,
            Evaluate(lifetime, started.AddSeconds(20), distance: 10f));
        Assert.Equal(CoppeliaLosRescueDecision.Timeout,
            Evaluate(lifetime, started.AddSeconds(20), distance: 10.001f));
        Assert.Equal(CoppeliaLosRescueDecision.Suppressed,
            Evaluate(lifetime, started.AddSeconds(21), distance: 15f));
        Assert.Equal(CoppeliaLosRescueDecision.Suppressed,
            Evaluate(lifetime, started.AddSeconds(40), distance: 25f));
        Assert.Equal(CoppeliaLosRescueDecision.Rescue,
            Evaluate(lifetime, started.AddSeconds(41), distance: 10f));

        Assert.Equal(CoppeliaLosRescueDecision.Rescue,
            Evaluate(TimedOutPolicy(), started.AddSeconds(21), targetId: 2));
        Assert.Equal(CoppeliaLosRescueDecision.Rescue,
            Evaluate(TimedOutPolicy(), started.AddSeconds(21), questerWorld: 2, helperWorld: 2));
        Assert.Equal(CoppeliaLosRescueDecision.Rescue,
            Evaluate(TimedOutPolicy(), started.AddSeconds(21), questerTerritory: 2, helperTerritory: 2));
        Assert.Equal(CoppeliaLosRescueDecision.Rescue,
            Evaluate(TimedOutPolicy(), started.AddSeconds(21), assignmentId: "assignment-2"));

        var resetAssignment = TimedOutPolicy();
        resetAssignment.Reset();
        Assert.Equal(CoppeliaLosRescueDecision.Rescue,
            Evaluate(resetAssignment, started.AddSeconds(21)));

        CoppeliaLosRescuePolicy TimedOutPolicy()
        {
            var policy = new CoppeliaLosRescuePolicy();
            Assert.Equal(CoppeliaLosRescueDecision.Rescue,
                Evaluate(policy, started, distance: 11f));
            Assert.Equal(CoppeliaLosRescueDecision.Timeout,
                Evaluate(policy, started.AddSeconds(20), distance: 11f));
            return policy;
        }
    }

    private static CoppeliaLosRescueDecision Evaluate(
        CoppeliaLosRescuePolicy policy,
        DateTime utcNow,
        string assignmentId = "assignment-1",
        ulong targetId = 1,
        uint questerWorld = 1,
        uint helperWorld = 1,
        uint questerTerritory = 1,
        uint helperTerritory = 1,
        float distance = 11f) =>
        policy.Evaluate(
            assignmentId,
            targetId,
            questerWorld,
            helperWorld,
            questerTerritory,
            helperTerritory,
            distance,
            utcNow);
}

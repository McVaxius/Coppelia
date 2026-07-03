using Coppelia.Models;

namespace Coppelia.Tests;

public sealed class PowerlevelTargetSelectorTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 2, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void EligibilityRequiresDamagedTargetableCombatantTargetingFrenOrLocal()
    {
        var eligible = Candidate(1, targetId: 100, hp: 50, maxHp: 100);
        var fullHp = Candidate(2, targetId: 100, hp: 100, maxHp: 100);
        var targetingOther = Candidate(3, targetId: 999, hp: 50, maxHp: 100);

        Assert.True(PowerlevelTargetSelector.IsInitiallyEligible(eligible, frenObjectId: 100, localPlayerObjectId: 200));
        Assert.False(PowerlevelTargetSelector.IsInitiallyEligible(fullHp, frenObjectId: 100, localPlayerObjectId: 200));
        Assert.False(PowerlevelTargetSelector.IsInitiallyEligible(targetingOther, frenObjectId: 100, localPlayerObjectId: 200));
    }

    [Fact]
    public void SelectsLowestHpThenNearestThenStableObjectId()
    {
        var selector = new PowerlevelTargetSelector();
        var candidates = new[]
        {
            Candidate(30, targetId: 100, hp: 40, maxHp: 100, distance: 3),
            Candidate(10, targetId: 100, hp: 20, maxHp: 100, distance: 8),
            Candidate(20, targetId: 100, hp: 20, maxHp: 100, distance: 8),
        };

        var selection = selector.Select(candidates, frenObjectId: 100, localPlayerObjectId: 200, Now);

        Assert.Equal(10ul, selection.Target?.GameObjectId);
        Assert.False(selection.Retained);
    }

    [Fact]
    public void RetainsTargetAfterActionUntilInvalidOrUnusableForFiveSeconds()
    {
        var selector = new PowerlevelTargetSelector();
        selector.MarkActionLanded(10);
        var candidates = new[]
        {
            Candidate(10, targetId: 100, hp: 80, maxHp: 100, distance: 30, usable: false),
            Candidate(20, targetId: 100, hp: 10, maxHp: 100, distance: 5),
        };

        var retained = selector.Select(candidates, frenObjectId: 100, localPlayerObjectId: 200, Now);
        var stillRetained = selector.Select(candidates, frenObjectId: 100, localPlayerObjectId: 200, Now.AddSeconds(4.9));
        var released = selector.Select(candidates, frenObjectId: 100, localPlayerObjectId: 200, Now.AddSeconds(5.1));

        Assert.Equal(10ul, retained.Target?.GameObjectId);
        Assert.True(retained.Retained);
        Assert.Equal(10ul, stillRetained.Target?.GameObjectId);
        Assert.True(stillRetained.Retained);
        Assert.Equal(20ul, released.Target?.GameObjectId);
        Assert.False(released.Retained);
    }

    private static PowerlevelTargetSnapshot Candidate(
        ulong id,
        ulong targetId,
        uint hp,
        uint maxHp,
        float distance = 5,
        bool usable = true)
        => new(
            id,
            $"Enemy {id}",
            hp,
            maxHp,
            distance,
            IsDead: hp == 0,
            IsTargetable: true,
            IsCombatant: true,
            targetId,
            usable);
}

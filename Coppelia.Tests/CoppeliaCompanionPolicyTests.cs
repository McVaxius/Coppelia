using Coppelia.Models;

namespace Coppelia.Tests;

public sealed class CoppeliaCompanionPolicyTests
{
    [Fact]
    public void CompanionStartsWithoutQstOwnership()
    {
        var policy = new CoppeliaCompanionPolicy();

        Assert.False(policy.IsQstOwned);
        Assert.Equal(CoppeliaCompanionDecision.Disabled, policy.Evaluate(Safe(), 0));
    }

    [Fact]
    public void QstCanEnableDisableAndClearOwnership()
    {
        var policy = new CoppeliaCompanionPolicy();

        policy.SetQstOwnership(true);
        Assert.True(policy.Enabled);
        Assert.Equal(CoppeliaCompanionDecision.Summon, policy.Evaluate(Safe(), 0));

        policy.SetQstOwnership(false);
        Assert.True(policy.IsQstOwned);
        Assert.False(policy.Enabled);
        Assert.Equal(CoppeliaCompanionDecision.Disabled, policy.Evaluate(Safe(), 0));

        policy.ClearQstOwnership();
        Assert.False(policy.IsQstOwned);
    }

    [Theory]
    [InlineData(false, true, false, false, false, false, false)]
    [InlineData(true, false, false, false, false, false, false)]
    [InlineData(true, true, true, false, false, false, false)]
    [InlineData(true, true, false, true, false, false, false)]
    [InlineData(true, true, false, false, true, false, false)]
    [InlineData(true, true, false, false, false, true, false)]
    [InlineData(true, true, false, false, false, false, true)]
    public void EveryRuntimeSafetyGateBlocksSummoning(
        bool loggedIn,
        bool onFoot,
        bool inCombat,
        bool inDuty,
        bool inSanctuary,
        bool casting,
        bool occupied)
    {
        var policy = EnabledPolicy();
        var conditions = new CoppeliaCompanionConditions(
            loggedIn,
            onFoot,
            inCombat,
            inDuty,
            inSanctuary,
            casting,
            occupied,
            HasGysahlGreens: true,
            BuddyTimeRemainingSeconds: 0f);

        Assert.Equal(CoppeliaCompanionDecision.Unsafe, policy.Evaluate(conditions, 0));
    }

    [Fact]
    public void BuddyThresholdIsStrictlyBelowNineHundredSeconds()
    {
        var atThreshold = EnabledPolicy();
        Assert.Equal(
            CoppeliaCompanionDecision.TimerHealthy,
            atThreshold.Evaluate(Safe() with { BuddyTimeRemainingSeconds = 900f }, 0));

        var belowThreshold = EnabledPolicy();
        Assert.Equal(
            CoppeliaCompanionDecision.Summon,
            belowThreshold.Evaluate(Safe() with { BuddyTimeRemainingSeconds = 899.9f }, 0));
    }

    [Fact]
    public void GreensAndFifteenSecondThrottleAreEnforced()
    {
        var noGreens = EnabledPolicy();
        Assert.Equal(
            CoppeliaCompanionDecision.NoGreens,
            noGreens.Evaluate(Safe() with { HasGysahlGreens = false }, 0));

        var policy = EnabledPolicy();
        Assert.Equal(CoppeliaCompanionDecision.Summon, policy.Evaluate(Safe(), 0));
        Assert.Equal(CoppeliaCompanionDecision.Throttled, policy.Evaluate(Safe(), 14_999));
        Assert.Equal(CoppeliaCompanionDecision.Summon, policy.Evaluate(Safe(), 15_000));
    }

    private static CoppeliaCompanionPolicy EnabledPolicy()
    {
        var policy = new CoppeliaCompanionPolicy();
        policy.SetQstOwnership(true);
        return policy;
    }

    private static CoppeliaCompanionConditions Safe() => new(
        LoggedIn: true,
        OnFoot: true,
        InCombat: false,
        InDuty: false,
        InSanctuary: false,
        Casting: false,
        Occupied: false,
        HasGysahlGreens: true,
        BuddyTimeRemainingSeconds: 0f);
}

using Coppelia.Models;

namespace Coppelia.Tests;

public sealed class PowerlevelActionPolicyTests
{
    [Fact]
    public void AcceptsInstantAvailableHostileSingleTargetAction()
    {
        Assert.True(PowerlevelActionPolicy.IsAllowed(Ready(), out _));
    }

    [Theory]
    [InlineData(false, true, false, false, 0, 0, true, true, true, "not unlocked")]
    [InlineData(true, false, false, false, 0, 0, true, true, true, "hostile")]
    [InlineData(true, true, true, false, 0, 0, true, true, true, "hostile-only")]
    [InlineData(true, true, false, false, 0, 1500, true, true, true, "casted")]
    [InlineData(true, true, false, true, 0, 0, true, true, true, "AoE")]
    [InlineData(true, true, false, false, 5, 0, true, true, true, "AoE")]
    [InlineData(true, true, false, false, 0, 0, false, true, true, "unavailable")]
    [InlineData(true, true, false, false, 0, 0, true, false, true, "cooling down")]
    [InlineData(true, true, false, false, 0, 0, true, true, false, "out of range")]
    public void RejectsUnsafeOrUnavailableAdjustedActions(
        bool unlocked,
        bool hostile,
        bool friendly,
        bool area,
        int effectRange,
        int castMs,
        bool available,
        bool offCooldown,
        bool inRange,
        string expectedReasonPart)
    {
        var allowed = PowerlevelActionPolicy.IsAllowed(new PowerlevelActionMetadata(
            "Test Action",
            unlocked,
            hostile,
            friendly,
            area,
            effectRange,
            castMs,
            available,
            offCooldown,
            inRange), out var reason);

        Assert.False(allowed);
        Assert.Contains(expectedReasonPart, reason, StringComparison.OrdinalIgnoreCase);
    }

    private static PowerlevelActionMetadata Ready()
        => new(
            "Test Action",
            IsUnlocked: true,
            CanTargetHostile: true,
            CanTargetFriendly: false,
            IsAreaAction: false,
            EffectRange: 0,
            CastTimeMs: 0,
            IsAvailable: true,
            IsOffCooldown: true,
            IsInRange: true);
}

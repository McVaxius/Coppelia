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
}

using Coppelia.Models;

namespace Coppelia.Tests;

public sealed class PowerlevelActivationPolicyTests
{
    [Fact]
    public void ReadyWhenAllPowerlevelGatesPass()
    {
        var result = PowerlevelActivationPolicy.Evaluate(ValidInput());

        Assert.True(result.Ready);
    }

    [Theory]
    [InlineData(PowerlevelJob.None, true, 23u, true, true, true, true, true, false, "Select BRD or MCH")]
    [InlineData(PowerlevelJob.BRD, false, 23u, true, true, true, true, true, false, "not unlocked")]
    [InlineData(PowerlevelJob.BRD, true, 31u, true, true, true, true, true, false, "Current job")]
    [InlineData(PowerlevelJob.BRD, true, 23u, false, true, true, true, true, false, "IPC is unavailable")]
    [InlineData(PowerlevelJob.BRD, true, 23u, true, false, true, true, true, false, "incompatible")]
    [InlineData(PowerlevelJob.BRD, true, 23u, true, true, false, true, true, false, "must be enabled")]
    [InlineData(PowerlevelJob.BRD, true, 23u, true, true, true, false, true, false, "configured Fren")]
    [InlineData(PowerlevelJob.BRD, true, 23u, true, true, true, true, false, false, "not visible")]
    [InlineData(PowerlevelJob.BRD, true, 23u, true, true, true, true, true, true, "companion chocobo")]
    public void ReportsSpecificGateFailure(
        PowerlevelJob selectedJob,
        bool selectedJobUnlocked,
        uint currentJobId,
        bool ipcAvailable,
        bool compatible,
        bool frenRiderEnabled,
        bool frenConfigured,
        bool frenVisible,
        bool companionActive,
        string expectedReasonPart)
    {
        var result = PowerlevelActivationPolicy.Evaluate(new PowerlevelActivationInput(
            selectedJob,
            selectedJobUnlocked,
            currentJobId,
            ipcAvailable,
            compatible,
            frenRiderEnabled,
            frenConfigured,
            frenVisible,
            companionActive));

        Assert.False(result.Ready);
        Assert.Contains(expectedReasonPart, result.Reason, StringComparison.OrdinalIgnoreCase);
    }

    private static PowerlevelActivationInput ValidInput()
        => new(
            PowerlevelJob.BRD,
            SelectedJobUnlocked: true,
            CurrentJobId: 23,
            FrenRiderIpcAvailable: true,
            FrenRiderCompatible: true,
            FrenRiderEnabled: true,
            FrenConfigured: true,
            FrenVisible: true,
            CompanionActive: false);
}

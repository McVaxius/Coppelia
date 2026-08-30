using Coppelia.Models;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Coppelia.Tests;

public sealed class CoppeliaCompanionPolicyTests
{
    [Fact]
    public void CompanionStartsFromTheSavedLocalPreference()
    {
        var policy = new CoppeliaCompanionPolicy();

        Assert.False(policy.IsQstOwned);
        Assert.Equal(CoppeliaCompanionDecision.Disabled, policy.Evaluate(Safe(), 0));

        policy.SetLocalState(summonEnabled: true, OperatingRole.StandAlone);
        Assert.True(policy.Enabled);
        Assert.Equal(CoppeliaCompanionDecision.Summon, policy.Evaluate(Safe(), 0));

        policy.SetLocalState(summonEnabled: false, OperatingRole.StandAlone);
        Assert.False(policy.Enabled);
        Assert.Equal(CoppeliaCompanionDecision.Disabled, policy.Evaluate(Safe(), 0));
    }

    [Theory]
    [InlineData(OperatingRole.Off, false)]
    [InlineData(OperatingRole.StandAlone, true)]
    [InlineData(OperatingRole.Helper, true)]
    [InlineData(OperatingRole.Newb, true)]
    public void LocalSummoningRunsInEveryRoleExceptOff(OperatingRole role, bool expectedEnabled)
    {
        var policy = new CoppeliaCompanionPolicy();
        policy.SetLocalState(summonEnabled: true, role);

        Assert.Equal(expectedEnabled, policy.Enabled);
        Assert.Equal(
            expectedEnabled ? CoppeliaCompanionDecision.Summon : CoppeliaCompanionDecision.Disabled,
            policy.Evaluate(Safe(), 0));
    }

    [Fact]
    public void QstOverridesLocalPreferenceAndClearRestoresIt()
    {
        var policy = new CoppeliaCompanionPolicy();
        policy.SetLocalState(summonEnabled: true, OperatingRole.StandAlone);

        policy.SetQstOwnership(false);
        Assert.True(policy.IsQstOwned);
        Assert.False(policy.Enabled);
        Assert.Equal(CoppeliaCompanionDecision.Disabled, policy.Evaluate(Safe(), 0));

        policy.ClearQstOwnership();
        Assert.False(policy.IsQstOwned);
        Assert.True(policy.Enabled);
        Assert.Equal(CoppeliaCompanionDecision.Summon, policy.Evaluate(Safe(), 0));

        policy.SetLocalState(summonEnabled: false, OperatingRole.StandAlone);

        policy.SetQstOwnership(true);
        Assert.True(policy.Enabled);
        Assert.Equal(CoppeliaCompanionDecision.Summon, policy.Evaluate(Safe(), 0));

        policy.SetQstOwnership(false);
        Assert.True(policy.IsQstOwned);
        Assert.False(policy.Enabled);
        Assert.Equal(CoppeliaCompanionDecision.Disabled, policy.Evaluate(Safe(), 0));

        policy.ClearQstOwnership();
        Assert.False(policy.IsQstOwned);
        Assert.False(policy.Enabled);
    }

    [Fact]
    public void QstCanSummonWhileTheLocalRoleIsOff()
    {
        var policy = new CoppeliaCompanionPolicy();
        policy.SetLocalState(summonEnabled: true, OperatingRole.Off);
        Assert.False(policy.Enabled);

        policy.SetQstOwnership(true);
        Assert.True(policy.Enabled);
        Assert.Equal(CoppeliaCompanionDecision.Summon, policy.Evaluate(Safe(), 0));
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

    [Fact]
    public void ConfigurationDefaultsToSummoningWithFreeStance()
    {
        var configuration = new Configuration();

        Assert.True(configuration.SummonCompanionChocobo);
        Assert.Equal(CoppeliaCompanionPolicy.FreeStance, configuration.CompanionStance);
        Assert.Equal(9, configuration.Version);
    }

    [Fact]
    public void MissingPersistedCompanionFieldsReceiveDefaultsWithoutSchemaChange()
    {
        var configuration = JsonSerializer.Deserialize<Configuration>("""{"Version":9}""")!;

        Assert.True(configuration.SummonCompanionChocobo);
        Assert.Equal(CoppeliaCompanionPolicy.FreeStance, configuration.CompanionStance);
        Assert.Equal(9, configuration.Version);
    }

    [Fact]
    public void PowerlevelBotUsesTheStandaloneRoleCompanionPolicy()
    {
        var configuration = new Configuration
        {
            OperatingRole = OperatingRole.StandAlone,
            BotMode = BotMode.PowerlevelBot,
            SummonCompanionChocobo = true,
        };
        var policy = new CoppeliaCompanionPolicy();

        policy.SetLocalState(configuration.SummonCompanionChocobo, configuration.OperatingRole);

        Assert.True(policy.Enabled);
        Assert.Equal(CoppeliaCompanionDecision.Summon, policy.Evaluate(Safe(), 0));
    }

    [Theory]
    [InlineData("Free Stance", "/cac \"Free Stance\"")]
    [InlineData("Defender Stance", "/cac \"Defender Stance\"")]
    [InlineData("Attacker Stance", "/cac \"Attacker Stance\"")]
    [InlineData("Healer Stance", "/cac \"Healer Stance\"")]
    [InlineData("Follow", "/cac \"Follow\"")]
    [InlineData("invalid", "/cac \"Free Stance\"")]
    [InlineData(null, "/cac \"Free Stance\"")]
    public void SavedStancesMapToFixedCommandsWithFreeFallback(string? savedStance, string expectedCommand)
        => Assert.Equal(expectedCommand, CoppeliaCompanionPolicy.GetStanceCommand(savedStance));

    [Fact]
    public void SuccessfulSummonSchedulesStanceForThreeSecondsLater()
    {
        var policy = EnabledPolicy();
        policy.ScheduleStance(CoppeliaCompanionPolicy.HealerStance, 1_000);

        Assert.True(policy.HasPendingStance);
        Assert.Null(policy.TakeDueStance(3_999, runtimeEffective: true));
        Assert.Equal(
            "/cac \"Healer Stance\"",
            policy.TakeDueStance(4_000, runtimeEffective: true));
        Assert.False(policy.HasPendingStance);
        Assert.Null(policy.TakeDueStance(4_001, runtimeEffective: true));
    }

    [Fact]
    public void PendingStanceCancelsWhenSummoningBecomesIneffective()
    {
        var policy = EnabledPolicy();
        policy.ScheduleStance(CoppeliaCompanionPolicy.AttackerStance, 0);

        policy.SetLocalState(summonEnabled: false, OperatingRole.StandAlone);
        policy.ClearQstOwnership();

        Assert.False(policy.Enabled);
        Assert.False(policy.HasPendingStance);
        Assert.Null(policy.TakeDueStance(3_000, runtimeEffective: true));
    }

    [Fact]
    public void RuntimeLossCancelsPendingStance()
    {
        var policy = EnabledPolicy();
        policy.ScheduleStance(CoppeliaCompanionPolicy.AttackerStance, 0);

        Assert.Null(policy.TakeDueStance(1_000, runtimeEffective: false));
        Assert.False(policy.HasPendingStance);
    }

    [Fact]
    public void ActiveCompanionStanceSelectionIsImmediateAndCancelsScheduledDuplicate()
    {
        var policy = EnabledPolicy();
        policy.ScheduleStance(CoppeliaCompanionPolicy.FreeStance, 0);

        var command = policy.SelectStance(CoppeliaCompanionPolicy.DefenderStance, companionActive: true);

        Assert.Equal("/cac \"Defender Stance\"", command);
        Assert.False(policy.HasPendingStance);
        Assert.Null(policy.TakeDueStance(3_000, runtimeEffective: true));
    }

    [Fact]
    public void StanceSelectionBeforeCompanionAppearsUpdatesThePendingCommand()
    {
        var policy = EnabledPolicy();
        policy.ScheduleStance(CoppeliaCompanionPolicy.FreeStance, 0);

        Assert.Null(policy.SelectStance(CoppeliaCompanionPolicy.HealerStance, companionActive: false));
        Assert.True(policy.HasPendingStance);
        Assert.Equal(
            "/cac \"Healer Stance\"",
            policy.TakeDueStance(3_000, runtimeEffective: true));
    }

    [Fact]
    public void MiniContainsSavedSummonControlLiveCountAndAllFiveStableStanceRadios()
    {
        var source = ReadSource("Coppelia", "Windows", "MiniWindow.cs");

        Assert.Equal(1, Count(source, "ImGui.Checkbox(companionLabel"));
        Assert.Contains("configuration.SummonCompanionChocobo = summonCompanion;", source, StringComparison.Ordinal);
        Assert.Contains("GetGysahlGreensCount()", source, StringComparison.Ordinal);
        Assert.Contains("Gysahl Greens: {greensCount.Value}", source, StringComparison.Ordinal);
        Assert.Contains("Gysahl Greens: unavailable", source, StringComparison.Ordinal);
        Assert.Contains("Free Stance##MiniCompanionStanceFree", source, StringComparison.Ordinal);
        Assert.Contains("Defender Stance##MiniCompanionStanceDefender", source, StringComparison.Ordinal);
        Assert.Contains("Attacker Stance##MiniCompanionStanceAttacker", source, StringComparison.Ordinal);
        Assert.Contains("Healer Stance##MiniCompanionStanceHealer", source, StringComparison.Ordinal);
        Assert.Contains("Follow##MiniCompanionStanceFollow", source, StringComparison.Ordinal);
    }

    private static CoppeliaCompanionPolicy EnabledPolicy()
    {
        var policy = new CoppeliaCompanionPolicy();
        policy.SetLocalState(summonEnabled: true, OperatingRole.StandAlone);
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

    private static string ReadSource(params string[] relativePath)
    {
        var repoRoot = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(GetThisFilePath())!, ".."));
        return File.ReadAllText(Path.Combine([repoRoot, .. relativePath]));
    }

    private static string GetThisFilePath([CallerFilePath] string path = "") => path;

    private static int Count(string source, string value)
        => source.Split(value, StringSplitOptions.None).Length - 1;
}

using Coppelia.Models;

namespace Coppelia.Tests;

public sealed class ConfigurationMigrationTests
{
    [Fact]
    public void LegacyEnabledHealbotMigratesToHealBotAutomation()
    {
        var configuration = new Configuration
        {
            Version = 4,
            HealbotEnabled = true,
            AutomationEnabled = false,
            BotMode = BotMode.PowerlevelBot,
            PowerlevelJob = PowerlevelJob.MCH,
        };

        var changed = configuration.MigrateIfNeeded();

        Assert.True(changed);
        Assert.Equal(9, configuration.Version);
        Assert.True(configuration.SetupWizardCompleted);
        Assert.False(configuration.ShouldAutoOpenSetup());
        Assert.True(configuration.AutomationEnabled);
        Assert.Equal(BotMode.HealBot, configuration.BotMode);
    }

    [Fact]
    public void NewInstallRemainsIncompleteAndAutoOpensSetup()
    {
        var configuration = new Configuration();

        configuration.MigrateIfNeeded();

        Assert.Equal(9, configuration.Version);
        Assert.False(configuration.SetupWizardCompleted);
        Assert.True(configuration.ShouldAutoOpenSetup());
    }

    [Fact]
    public void VersionFiveMigrationSuppressesSetupAndPreservesConfiguration()
    {
        var configuration = new Configuration
        {
            Version = 5,
            SetupWizardCompleted = false,
            AutomationEnabled = true,
            BotMode = BotMode.PowerlevelBot,
            PowerlevelJob = PowerlevelJob.BRD,
            WatchPlayers = false,
            WatchCompanionChocobos = true,
            WatchPartyNpcs = true,
            WatchFriendlyBattleNpcs = true,
            SaveHealTargets = true,
            SavedTargetScanRangeYalms = 87,
        };
        configuration.ActiveWatchedTargets.Add(new PersistedWatchTarget
        {
            GameObjectId = 1234,
            Name = "Existing Target",
            Category = WatchTargetCategory.Player,
            CategoryLabel = "Player",
            JobLabel = "WHM",
            LastSeenUnixTimeSeconds = 100,
        });
        configuration.MainWindowPosition.Set(new System.Numerics.Vector2(120f, 240f));

        var changed = configuration.MigrateIfNeeded();

        Assert.True(changed);
        Assert.Equal(9, configuration.Version);
        Assert.True(configuration.SetupWizardCompleted);
        Assert.False(configuration.ShouldAutoOpenSetup());
        Assert.True(configuration.AutomationEnabled);
        Assert.Equal(BotMode.PowerlevelBot, configuration.BotMode);
        Assert.Equal(PowerlevelJob.BRD, configuration.PowerlevelJob);
        Assert.False(configuration.WatchPlayers);
        Assert.True(configuration.WatchCompanionChocobos);
        Assert.True(configuration.WatchPartyNpcs);
        Assert.True(configuration.WatchFriendlyBattleNpcs);
        Assert.True(configuration.SaveHealTargets);
        Assert.Equal(87, configuration.SavedTargetScanRangeYalms);
        Assert.Single(configuration.ActiveWatchedTargets);
        Assert.Equal("Existing Target", configuration.ActiveWatchedTargets[0].Name);
        Assert.True(configuration.MainWindowPosition.HasValue);
        Assert.Equal(120f, configuration.MainWindowPosition.X);
        Assert.Equal(240f, configuration.MainWindowPosition.Y);
    }

    [Fact]
    public void CompletedSetupRemainsCompletedAtCurrentSchema()
    {
        var configuration = new Configuration
        {
            SetupWizardCompleted = true,
            BotMode = BotMode.PowerlevelBot,
            PowerlevelJob = PowerlevelJob.MCH,
        };

        configuration.MigrateIfNeeded();

        Assert.Equal(9, configuration.Version);
        Assert.True(configuration.SetupWizardCompleted);
        Assert.False(configuration.ShouldAutoOpenSetup());
        Assert.Equal(BotMode.PowerlevelBot, configuration.BotMode);
        Assert.Equal(PowerlevelJob.MCH, configuration.PowerlevelJob);
    }

    [Fact]
    public void VersionSixMigrationEnablesLootGoblinTravelDefaults()
    {
        var configuration = new Configuration
        {
            Version = 6,
            AvoidTamamizuAetheryte = false,
            AutoUpdateMapLocationsOnLogin = false,
        };

        Assert.True(configuration.MigrateIfNeeded());
        Assert.Equal(9, configuration.Version);
        Assert.True(configuration.AvoidTamamizuAetheryte);
        Assert.True(configuration.AutoUpdateMapLocationsOnLogin);
    }

    [Fact]
    public void TerritoryForwardProbeDefaultsToFiveSecondsAndNormalizesToSupportedRange()
    {
        Assert.Equal(5, new Configuration().TerritoryForwardProbeSeconds);

        var belowMinimum = new Configuration { TerritoryForwardProbeSeconds = 0 };
        var aboveMaximum = new Configuration { TerritoryForwardProbeSeconds = 21 };

        belowMinimum.MigrateIfNeeded();
        aboveMaximum.MigrateIfNeeded();

        Assert.Equal(1, belowMinimum.TerritoryForwardProbeSeconds);
        Assert.Equal(20, aboveMaximum.TerritoryForwardProbeSeconds);
    }

    [Fact]
    public void QuickSetupDraftDoesNotMutateConfigurationUntilApplied()
    {
        var configuration = new Configuration
        {
            BotMode = BotMode.HealBot,
            PowerlevelJob = PowerlevelJob.None,
            WatchPlayers = true,
            SaveHealTargets = false,
            SavedTargetScanRangeYalms = 20,
        };
        var draft = QuickSetupDraft.FromConfiguration(configuration);

        draft.Mode = BotMode.PowerlevelBot;
        draft.PowerlevelJob = PowerlevelJob.MCH;
        draft.WatchPlayers = false;
        draft.SaveHealTargets = true;
        draft.SavedTargetScanRangeYalms = 64;

        Assert.Equal(BotMode.HealBot, configuration.BotMode);
        Assert.Equal(PowerlevelJob.None, configuration.PowerlevelJob);
        Assert.True(configuration.WatchPlayers);
        Assert.False(configuration.SaveHealTargets);
        Assert.Equal(20, configuration.SavedTargetScanRangeYalms);

        draft.ApplyTo(configuration);

        Assert.Equal(BotMode.PowerlevelBot, configuration.BotMode);
        Assert.Equal(PowerlevelJob.MCH, configuration.PowerlevelJob);
        Assert.False(configuration.WatchPlayers);
        Assert.True(configuration.SaveHealTargets);
        Assert.Equal(64, configuration.SavedTargetScanRangeYalms);
    }

    [Fact]
    public void ModePolicyKeepsOnlyHealBotLegacyFlagEnabled()
    {
        var configuration = new Configuration
        {
            AutomationEnabled = true,
            HealbotEnabled = true,
            BotMode = BotMode.HealBot,
        };

        var changed = AutomationModePolicy.ApplyMode(configuration, BotMode.PowerlevelBot);

        Assert.True(changed);
        Assert.True(configuration.AutomationEnabled);
        Assert.Equal(BotMode.PowerlevelBot, configuration.BotMode);
        Assert.False(configuration.HealbotEnabled);
    }
}

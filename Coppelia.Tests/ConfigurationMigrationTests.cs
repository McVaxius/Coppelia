using Newtonsoft.Json;
using Coppelia.Models;
using Coppelia.Windows;
using AethertekUI;

namespace Coppelia.Tests;

public sealed class ConfigurationMigrationTests
{
    [Fact]
    public void IndependentColourChoicesKeepTheLegacyPaletteAndRoundTripThroughGlobalConfiguration()
    {
        var configuration = JsonConvert.DeserializeObject<Configuration>("""{"Version":9,"AutomationEnabled":true,"UiLanguage":"fr","UiAccentRgb":265,"WatchPlayers":false,"FutureSettings":{"Keep":true}}""")!;
        Assert.Equal(MaterialColorMode.Auto, configuration.UiColors.Mode);
        var automatic = CoppeliaPresentation.Theme(CoppeliaPresentation.ReferenceAccent).Colors;
        Assert.Equal(CoppeliaPresentation.Rgb(0x0D2023), automatic.Background);
        Assert.Equal(CoppeliaPresentation.Rgb(0xECF4F6), automatic.OnSurface);
        Assert.Equal(CoppeliaPresentation.Rgb(0x28E8B4), automatic.Primary);
        configuration.UiColors = new() { Mode = MaterialColorMode.Picker,
            BackgroundRgb = 0x010203, ControlsRgb = 0x040506, TextRgb = 0x070809 };
        var restored = JsonConvert.DeserializeObject<Configuration>(JsonConvert.SerializeObject(configuration))!;
        Assert.Equal(configuration.UiColors, restored.UiColors);
        Assert.Equal(9, restored.Version);
        Assert.True(restored.AutomationEnabled);
        Assert.Equal("fr", restored.UiLanguage);
        Assert.Equal(265u, restored.UiAccentRgb);
        Assert.False(restored.WatchPlayers);
        Assert.True((bool)restored.AdditionalSettings!["FutureSettings"]["Keep"]!);
        var manual = CoppeliaPresentation.Theme(CoppeliaPresentation.ReferenceAccent, restored.UiColors).Colors;
        Assert.Equal(CoppeliaPresentation.Rgb(0x010203), manual.Background);
        Assert.Equal(CoppeliaPresentation.Rgb(0x040506), manual.Primary);
        Assert.Equal(CoppeliaPresentation.Rgb(0x070809), manual.OnSurface);
        Assert.Equal(automatic.Error, manual.Error);
        Assert.Equal(automatic.OnError, manual.OnError);
        restored.UiColors = restored.UiColors with { Mode = MaterialColorMode.Auto };
        var returned = CoppeliaPresentation.Theme(CoppeliaPresentation.ReferenceAccent, restored.UiColors).Colors;
        Assert.Equal(automatic.Background, returned.Background);
        Assert.Equal(automatic.Primary, returned.Primary);
        Assert.Equal(automatic.OnSurface, returned.OnSurface);
        Assert.Equal(0x010203u, restored.UiColors.BackgroundRgb);
        Assert.Equal(0x040506u, restored.UiColors.ControlsRgb);
        Assert.Equal(0x070809u, restored.UiColors.TextRgb);
    }

    [Fact]
    public void EveryAdvertisedLocaleContainsTheColourEditorLabels()
    {
        string[] labels = ["Auto", "Picker", "Default", "Background, controls and text follow your theme.", "Theme colour",
            "Choose each colour independently.", "Background", "Controls", "Text", "Aa  Preview text", "Control",
            "Switch to Picker to set each colour yourself.", "Switch back to Auto to use the automatic theme."];
        using var english = new UiText("en", _ => null!);
        var keys = english.Resources.Cast<System.Collections.DictionaryEntry>()
            .ToDictionary(entry => (string)entry.Value!, entry => (string)entry.Key, StringComparer.OrdinalIgnoreCase);
        foreach (var language in UiText.Languages)
        {
            using var text = new UiText(language.Code, _ => null!);
            foreach (var label in labels)
                Assert.False(string.IsNullOrWhiteSpace(text.Resources.GetString(keys[label], false)), language.Code + ": " + label);
        }
    }

    [Fact]
    public void TitlebarStatusDefaultsAndSavedChoicesPreserveUnrelatedConfiguration()
    {
        var configuration = JsonConvert.DeserializeObject<Configuration>("""{"Version":9,"AutomationEnabled":true,"UiLanguage":"fr","UiAccentRgb":265,"WatchPlayers":false,"SavedTargetScanRangeYalms":87,"FutureSettings":{"Keep":true}}""")!;
        Assert.True(configuration.UiShowTitlebarStatus);
        Assert.True(configuration.UiDetailedTitlebarStatus);

        configuration.UiShowTitlebarStatus = false;
        configuration.UiDetailedTitlebarStatus = false;
        var restored = JsonConvert.DeserializeObject<Configuration>(JsonConvert.SerializeObject(configuration))!;
        restored.MigrateIfNeeded();

        Assert.False(restored.UiShowTitlebarStatus);
        Assert.False(restored.UiDetailedTitlebarStatus);
        Assert.Equal(9, restored.Version);
        Assert.True(restored.AutomationEnabled);
        Assert.Equal("fr", restored.UiLanguage);
        Assert.Equal(265u, restored.UiAccentRgb);
        Assert.False(restored.WatchPlayers);
        Assert.Equal(87, restored.SavedTargetScanRangeYalms);
        Assert.True((bool)restored.AdditionalSettings!["FutureSettings"]["Keep"]!);
    }

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

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
        Assert.Equal(5, configuration.Version);
        Assert.True(configuration.AutomationEnabled);
        Assert.Equal(BotMode.HealBot, configuration.BotMode);
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

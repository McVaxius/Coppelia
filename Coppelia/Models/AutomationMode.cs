namespace Coppelia.Models;

public enum BotMode
{
    HealBot = 0,
    PowerlevelBot = 1,
}

public enum PowerlevelJob
{
    None = 0,
    BRD = 23,
    MCH = 31,
}

internal static class AutomationModeText
{
    public static string GetLabel(this BotMode mode)
        => mode switch
        {
            BotMode.HealBot => "HealBot",
            BotMode.PowerlevelBot => "PowerlevelBot",
            _ => mode.ToString(),
        };

    public static string GetDtrLabel(this BotMode mode)
        => mode switch
        {
            BotMode.PowerlevelBot => "PL",
            _ => "HB",
        };

    public static string GetLabel(this PowerlevelJob job)
        => job switch
        {
            PowerlevelJob.BRD => "Bard (BRD)",
            PowerlevelJob.MCH => "Machinist (MCH)",
            _ => "Select BRD or MCH",
        };

    public static uint ToJobId(this PowerlevelJob job)
        => (uint)job;

    public static bool IsSupportedPowerlevelJob(this PowerlevelJob job)
        => job is PowerlevelJob.BRD or PowerlevelJob.MCH;
}

internal static class AutomationModePolicy
{
    public static bool ApplyMode(Configuration configuration, BotMode mode)
    {
        var changed = configuration.BotMode != mode;
        configuration.BotMode = mode;
        var legacyHealbotEnabled = configuration.AutomationEnabled && mode == BotMode.HealBot;
        if (configuration.HealbotEnabled != legacyHealbotEnabled)
        {
            configuration.HealbotEnabled = legacyHealbotEnabled;
            changed = true;
        }

        return changed;
    }
}

namespace Coppelia.Models;

internal enum QuickSetupStep
{
    ChooseMode,
    Configure,
    Finish,
    Complete,
}

internal enum QuickSetupCompletionChoice
{
    None,
    EnableNow,
    LeaveAutomationOff,
}

internal sealed class QuickSetupDraft
{
    public BotMode Mode { get; set; }
    public PowerlevelJob PowerlevelJob { get; set; }
    public bool WatchPlayers { get; set; }
    public bool WatchCompanionChocobos { get; set; }
    public bool WatchPartyNpcs { get; set; }
    public bool WatchFriendlyBattleNpcs { get; set; }
    public bool SaveHealTargets { get; set; }
    public int SavedTargetScanRangeYalms { get; set; }

    public static QuickSetupDraft FromConfiguration(Configuration configuration)
        => new()
        {
            Mode = configuration.BotMode,
            PowerlevelJob = configuration.PowerlevelJob,
            WatchPlayers = configuration.WatchPlayers,
            WatchCompanionChocobos = configuration.WatchCompanionChocobos,
            WatchPartyNpcs = configuration.WatchPartyNpcs,
            WatchFriendlyBattleNpcs = configuration.WatchFriendlyBattleNpcs,
            SaveHealTargets = configuration.SaveHealTargets,
            SavedTargetScanRangeYalms = configuration.SavedTargetScanRangeYalms,
        };

    public void ApplyTo(Configuration configuration)
    {
        configuration.BotMode = Mode;
        configuration.PowerlevelJob = PowerlevelJob;
        configuration.WatchPlayers = WatchPlayers;
        configuration.WatchCompanionChocobos = WatchCompanionChocobos;
        configuration.WatchPartyNpcs = WatchPartyNpcs;
        configuration.WatchFriendlyBattleNpcs = WatchFriendlyBattleNpcs;
        configuration.SaveHealTargets = SaveHealTargets;
        configuration.SavedTargetScanRangeYalms = Math.Clamp(SavedTargetScanRangeYalms, 1, 200);
    }
}

internal sealed record PowerlevelSetupReadiness(
    PowerlevelJob SelectedJob,
    bool SelectedJobSupported,
    bool SelectedJobUnlocked,
    uint CurrentJobId,
    bool CurrentJobMatches,
    bool FrenRiderIpcAvailable,
    bool FrenRiderCompatible,
    bool FrenRiderEnabled,
    bool FrenConfigured,
    bool FrenVisible,
    bool CompanionClear,
    bool Ready,
    string Reason);

internal sealed record JotSetupReadiness(
    bool HealingDependenciesReady,
    bool SupportedHealer,
    string HealerLabel,
    bool HealerConfigurationEnabled,
    bool WatchedTargetsAvailable,
    bool HealingReady,
    string HealingReason,
    bool FrenRiderIpcAvailable,
    bool FrenRiderCompatible,
    bool FrenRiderEnabled,
    bool FrenConfigured,
    bool FrenVisible,
    bool AttackingReady,
    string AttackingReason);

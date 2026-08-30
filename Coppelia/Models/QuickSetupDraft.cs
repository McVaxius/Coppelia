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
    public OperatingRole Role { get; set; }
    public BotMode Mode { get; set; }
    public PowerlevelJob PowerlevelJob { get; set; }
    public bool JoatFullRsrRotation { get; set; }
    public bool WatchPlayers { get; set; }
    public bool WatchCompanionChocobos { get; set; }
    public bool WatchPartyNpcs { get; set; }
    public bool WatchFriendlyBattleNpcs { get; set; }
    public bool SaveHealTargets { get; set; }
    public int SavedTargetScanRangeYalms { get; set; }
    public bool EnableLanPairing { get; set; }
    public string LanHealBotAddress { get; set; } = "127.0.0.1";
    public int LanPairingPort { get; set; }
    public string LanPairingSecret { get; set; } = string.Empty;

    public static QuickSetupDraft FromConfiguration(Configuration configuration)
        => new()
        {
            Role = configuration.OperatingRole == OperatingRole.Off
                ? configuration.LastNonOffRole
                : configuration.OperatingRole,
            Mode = configuration.BotMode,
            PowerlevelJob = configuration.PowerlevelJob,
            JoatFullRsrRotation = configuration.JoatFullRsrRotation,
            WatchPlayers = configuration.WatchPlayers,
            WatchCompanionChocobos = configuration.WatchCompanionChocobos,
            WatchPartyNpcs = configuration.WatchPartyNpcs,
            WatchFriendlyBattleNpcs = configuration.WatchFriendlyBattleNpcs,
            SaveHealTargets = configuration.SaveHealTargets,
            SavedTargetScanRangeYalms = configuration.SavedTargetScanRangeYalms,
            EnableLanPairing = configuration.EnableLanPairing,
            LanHealBotAddress = configuration.LanHealBotAddress,
            LanPairingPort = configuration.LanPairingPort,
            LanPairingSecret = configuration.LanPairingSecret,
        };

    public void ApplyTo(Configuration configuration)
    {
        configuration.BotMode = Mode == BotMode.Newb ? BotMode.HealBot : Mode;
        configuration.PowerlevelJob = PowerlevelJob;
        configuration.JoatFullRsrRotation = JoatFullRsrRotation;
        configuration.WatchPlayers = WatchPlayers;
        configuration.WatchCompanionChocobos = WatchCompanionChocobos;
        configuration.WatchPartyNpcs = WatchPartyNpcs;
        configuration.WatchFriendlyBattleNpcs = WatchFriendlyBattleNpcs;
        configuration.SaveHealTargets = SaveHealTargets;
        configuration.SavedTargetScanRangeYalms = Math.Clamp(SavedTargetScanRangeYalms, 1, 200);
        configuration.EnableLanPairing = EnableLanPairing;
        configuration.LanHealBotAddress = LanHealBotAddress.Trim();
        configuration.LanPairingPort = LanPairingPort;
        configuration.LanPairingSecret = LanPairingSecret;
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
    bool RotationSolverLoaded,
    bool RotationSolverControlReady,
    bool FrenRiderIpcAvailable,
    bool FrenRiderCompatible,
    bool FrenRiderEnabled,
    bool FrenConfigured,
    bool FrenVisible,
    bool AttackingReady,
    string AttackingReason);

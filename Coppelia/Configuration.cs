using Coppelia.Models;
using Dalamud.Configuration;
using System.Net;
using System.Net.Sockets;

namespace Coppelia;

[Serializable]
public sealed class Configuration : IPluginConfiguration
{
    public const int DefaultLanPairingPort = 47790;
    public const int ReservedLanDiscoveryPort = 47789;

    private const int CurrentConfigurationVersion = 8;
    private const int MaxTrackedTargets = 20;

    public int Version { get; set; } = CurrentConfigurationVersion;
    public bool SetupWizardCompleted { get; set; }
    public bool PluginEnabled { get; set; } = true;
    public bool AutomationEnabled { get; set; }
    public BotMode BotMode { get; set; } = BotMode.HealBot;
    public PowerlevelJob PowerlevelJob { get; set; } = PowerlevelJob.None;

    // Legacy v4 runtime flag kept for migration only.
    public bool HealbotEnabled { get; set; }
    public bool DtrBarEnabled { get; set; } = true;
    public int DtrBarMode { get; set; } = 1;
    public string DtrIconEnabled { get; set; } = "\uE04E";
    public string DtrIconDisabled { get; set; } = "\uE04C";
    public bool ShowDependencyToasts { get; set; } = true;
    public bool WatchPlayers { get; set; } = true;
    public bool WatchCompanionChocobos { get; set; }
    public bool WatchPartyNpcs { get; set; }
    public bool WatchFriendlyBattleNpcs { get; set; }
    public bool KrangleNames { get; set; } = true;
    public bool SaveHealTargets { get; set; }
    public int SavedTargetScanRangeYalms { get; set; } = 20;
    public bool AvoidTamamizuAetheryte { get; set; } = true;
    public bool AutoUpdateMapLocationsOnLogin { get; set; } = true;
    public string LastCommunityLocationsRefreshPluginVersion { get; set; } = string.Empty;
    public bool EnableLanPairing { get; set; }
    public string LanHealBotAddress { get; set; } = "127.0.0.1";
    public int LanPairingPort { get; set; } = DefaultLanPairingPort;
    public string LanPairingSecret { get; set; } = string.Empty;

    // Legacy v3 single-target model kept for migration only.
    public ulong SelectedTargetGameObjectId { get; set; }
    public string SelectedTargetName { get; set; } = string.Empty;
    public List<PersistedWatchTarget> ActiveWatchedTargets { get; set; } = [];
    public List<PersistedWatchTarget> SavedHealTargetEntries { get; set; } = [];
    public SavedWindowPosition MainWindowPosition { get; set; } = new();
    public SavedWindowPosition ConfigWindowPosition { get; set; } = new();
    public SavedWindowPosition WatchWindowPosition { get; set; } = new();

    // Legacy v1 threshold model kept for migration only.
    public HealerJobSettings WhiteMage { get; set; } = new() { AbilityThresholdPercent = 72, SpellThresholdPercent = 67 };
    public HealerJobSettings Scholar { get; set; } = new() { AbilityThresholdPercent = 70, SpellThresholdPercent = 65 };
    public HealerJobSettings Astrologian { get; set; } = new() { AbilityThresholdPercent = 72, SpellThresholdPercent = 66 };
    public HealerJobSettings Sage { get; set; } = new() { AbilityThresholdPercent = 70, SpellThresholdPercent = 65 };
    public HealerJobConfig WhiteMageConfig { get; set; } = new();
    public HealerJobConfig ScholarConfig { get; set; } = new();
    public HealerJobConfig AstrologianConfig { get; set; } = new();
    public HealerJobConfig SageConfig { get; set; } = new();

    internal bool ShouldAutoOpenSetup()
        => !SetupWizardCompleted;

    public HealerJobConfig GetJobConfigForJob(uint classJobId)
        => classJobId switch
        {
            24 => WhiteMageConfig,
            28 => ScholarConfig,
            33 => AstrologianConfig,
            40 => SageConfig,
            _ => WhiteMageConfig,
        };

    public bool MigrateIfNeeded()
    {
        var changed = false;
        var sourceVersion = Version;

        changed |= HealbotActionCatalog.EnsureConfigCoverage(WhiteMageConfig, 24, sourceVersion < 5 ? WhiteMage : null);
        changed |= HealbotActionCatalog.EnsureConfigCoverage(ScholarConfig, 28, sourceVersion < 5 ? Scholar : null);
        changed |= HealbotActionCatalog.EnsureConfigCoverage(AstrologianConfig, 33, sourceVersion < 5 ? Astrologian : null);
        changed |= HealbotActionCatalog.EnsureConfigCoverage(SageConfig, 40, sourceVersion < 5 ? Sage : null);

        if ((SelectedTargetGameObjectId != 0 || !string.IsNullOrWhiteSpace(SelectedTargetName)) &&
            ActiveWatchedTargets.Count == 0)
        {
            ActiveWatchedTargets.Add(new PersistedWatchTarget
            {
                GameObjectId = SelectedTargetGameObjectId,
                Name = SelectedTargetName,
                Category = WatchTargetCategory.ManualSelection,
                CategoryLabel = "Migrated target",
                JobLabel = "?",
                IsExternalSelection = true,
                LastSeenUnixTimeSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            });
            SelectedTargetGameObjectId = 0;
            SelectedTargetName = string.Empty;
            changed = true;
        }

        changed |= NormalizeTrackedTargets(ActiveWatchedTargets);
        changed |= NormalizeTrackedTargets(SavedHealTargetEntries);

        SavedTargetScanRangeYalms = Math.Clamp(SavedTargetScanRangeYalms, 1, 200);
        changed |= NormalizeAutomationMode();
        var normalizedAddress = LanHealBotAddress?.Trim() ?? string.Empty;
        if (!string.Equals(LanHealBotAddress, normalizedAddress, StringComparison.Ordinal))
        {
            LanHealBotAddress = normalizedAddress;
            changed = true;
        }
        if (LanPairingSecret == null)
        {
            LanPairingSecret = string.Empty;
            changed = true;
        }

        if (sourceVersion != CurrentConfigurationVersion)
        {
            if (sourceVersion < 5)
            {
                AutomationEnabled = HealbotEnabled;
                BotMode = BotMode.HealBot;
                if (!PowerlevelJob.IsSupportedPowerlevelJob())
                    PowerlevelJob = PowerlevelJob.None;
                changed = true;
            }

            if (sourceVersion < 5)
            {
                KrangleNames = true;
                SavedTargetScanRangeYalms = 20;
            }

            if (sourceVersion < 6)
                SetupWizardCompleted = true;

            if (sourceVersion < 7)
            {
                AvoidTamamizuAetheryte = true;
                AutoUpdateMapLocationsOnLogin = true;
            }

            if (sourceVersion < 8)
            {
                if (string.IsNullOrWhiteSpace(LanHealBotAddress))
                    LanHealBotAddress = "127.0.0.1";
                if (LanPairingPort == 0)
                    LanPairingPort = DefaultLanPairingPort;
            }

            Version = CurrentConfigurationVersion;
            changed = true;
        }

        return changed;
    }

    public void Save()
        => Plugin.PluginInterface.SavePluginConfig(this);

    public string GetLanPairingBlocker(BotMode role)
    {
        if (!EnableLanPairing)
            return "LAN pairing is disabled.";
        if (!IsValidLanPairingPort(LanPairingPort))
        {
            return LanPairingPort == ReservedLanDiscoveryPort
                ? "Port 47789 is reserved. Choose another pairing port."
                : "The pairing port must be between 1024 and 65535.";
        }
        if (LanPairingSecret.Length < 16)
            return "The pair secret must contain at least 16 characters.";
        if (role == BotMode.Newb && !TryParseLanHealBotAddress(LanHealBotAddress, out _))
            return "Enter one valid IPv4 HealBot address, such as 127.0.0.1 or 192.168.1.25.";

        return string.Empty;
    }

    public static bool IsValidLanPairingPort(int port)
        => port is >= 1024 and <= 65535 && port != ReservedLanDiscoveryPort;

    public static bool TryParseLanHealBotAddress(string? address, out IPAddress parsed)
    {
        if (IPAddress.TryParse(address?.Trim(), out var candidate) &&
            candidate.AddressFamily == AddressFamily.InterNetwork)
        {
            parsed = candidate;
            return true;
        }

        parsed = IPAddress.None;
        return false;
    }

    private static bool NormalizeTrackedTargets(List<PersistedWatchTarget> targets)
    {
        var changed = false;
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var normalized = new List<PersistedWatchTarget>(MaxTrackedTargets);

        foreach (var target in targets
                     .Where(static target => target.GameObjectId != 0 || !string.IsNullOrWhiteSpace(target.Name))
                     .OrderByDescending(static target => target.LastSeenUnixTimeSeconds))
        {
            if (normalized.Any(existing => existing.Matches(target)))
            {
                changed = true;
                continue;
            }

            normalized.Add(new PersistedWatchTarget
            {
                GameObjectId = target.GameObjectId,
                EntityId = target.EntityId,
                Name = target.Name.Trim(),
                Category = target.Category,
                CategoryLabel = string.IsNullOrWhiteSpace(target.CategoryLabel)
                    ? GetDefaultCategoryLabel(target.Category)
                    : target.CategoryLabel,
                JobLabel = string.IsNullOrWhiteSpace(target.JobLabel) ? "?" : target.JobLabel,
                IsExternalSelection = target.IsExternalSelection,
                LastSeenUnixTimeSeconds = target.LastSeenUnixTimeSeconds <= 0 ? now : target.LastSeenUnixTimeSeconds,
            });

            if (normalized.Count == MaxTrackedTargets)
            {
                if (targets.Count > normalized.Count)
                    changed = true;
                break;
            }
        }

        if (!changed && targets.Count == normalized.Count)
        {
            for (var index = 0; index < normalized.Count; index++)
            {
                if (!targets[index].Matches(normalized[index]) ||
                    targets[index].LastSeenUnixTimeSeconds != normalized[index].LastSeenUnixTimeSeconds ||
                    !string.Equals(targets[index].CategoryLabel, normalized[index].CategoryLabel, StringComparison.Ordinal) ||
                    !string.Equals(targets[index].JobLabel, normalized[index].JobLabel, StringComparison.Ordinal))
                {
                    changed = true;
                    break;
                }
            }
        }
        else if (targets.Count != normalized.Count)
        {
            changed = true;
        }

        if (!changed)
            return false;

        targets.Clear();
        targets.AddRange(normalized);
        return true;
    }

    private bool NormalizeAutomationMode()
    {
        var changed = false;

        if (!Enum.IsDefined(BotMode))
        {
            BotMode = BotMode.HealBot;
            changed = true;
        }

        if (!Enum.IsDefined(PowerlevelJob))
        {
            PowerlevelJob = PowerlevelJob.None;
            changed = true;
        }

        if (!PowerlevelJob.IsSupportedPowerlevelJob() && PowerlevelJob != PowerlevelJob.None)
        {
            PowerlevelJob = PowerlevelJob.None;
            changed = true;
        }

        return changed;
    }

    private static string GetDefaultCategoryLabel(WatchTargetCategory category)
        => category switch
        {
            WatchTargetCategory.Player => "Player",
            WatchTargetCategory.CompanionChocobo => "Chocobo",
            WatchTargetCategory.NpcPartyMember => "NPC Party Member",
            WatchTargetCategory.FriendlyBattleNpc => "Battle NPC",
            _ => "Manual Target",
        };
}

using Coppelia.Models;
using Dalamud.Configuration;

namespace Coppelia;

[Serializable]
public sealed class Configuration : IPluginConfiguration
{
    private const int CurrentConfigurationVersion = 4;
    private const int MaxTrackedTargets = 20;

    public int Version { get; set; } = CurrentConfigurationVersion;
    public bool PluginEnabled { get; set; } = true;
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

        changed |= HealbotActionCatalog.EnsureConfigCoverage(WhiteMageConfig, 24, Version < CurrentConfigurationVersion ? WhiteMage : null);
        changed |= HealbotActionCatalog.EnsureConfigCoverage(ScholarConfig, 28, Version < CurrentConfigurationVersion ? Scholar : null);
        changed |= HealbotActionCatalog.EnsureConfigCoverage(AstrologianConfig, 33, Version < CurrentConfigurationVersion ? Astrologian : null);
        changed |= HealbotActionCatalog.EnsureConfigCoverage(SageConfig, 40, Version < CurrentConfigurationVersion ? Sage : null);

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

        if (Version != CurrentConfigurationVersion)
        {
            if (Version < CurrentConfigurationVersion)
            {
                KrangleNames = true;
                SavedTargetScanRangeYalms = 20;
            }

            Version = CurrentConfigurationVersion;
            changed = true;
        }

        return changed;
    }

    public void Save()
        => Plugin.PluginInterface.SavePluginConfig(this);

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

using System.Numerics;
using Coppelia.Models;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.ClientState.Objects.Types;

namespace Coppelia.Services;

internal sealed class WatchTargetService
{
    public const int MaxTrackedTargets = 20;

    private readonly List<WatchTargetSnapshot> targets = new();
    private readonly List<ResolvedWatchTarget> activeTargets = new();
    private readonly List<ResolvedWatchTarget> retainedTargets = new();
    private readonly List<WatchTargetCandidate> runtimeCandidates = new();
    private readonly Dictionary<ulong, ICharacter> liveCharactersById = new();
    private readonly Dictionary<string, ICharacter> liveCharactersByName = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<ulong, WatchTargetSnapshot> liveSnapshotsById = new();
    private DateTimeOffset nextRefreshUtc = DateTimeOffset.MinValue;

    public IReadOnlyList<WatchTargetSnapshot> Targets => targets;
    public IReadOnlyList<ResolvedWatchTarget> ActiveTargets => activeTargets;
    public IReadOnlyList<ResolvedWatchTarget> RetainedTargets => retainedTargets;
    public IReadOnlyList<WatchTargetCandidate> RuntimeCandidates => runtimeCandidates;
    public int SavedTargetCount { get; private set; }

    public void Update(Configuration configuration, bool force = false)
    {
        if (!force && DateTimeOffset.UtcNow < nextRefreshUtc)
            return;

        nextRefreshUtc = DateTimeOffset.UtcNow.AddMilliseconds(250);
        var changed = NormalizeTrackedCollections(configuration);

        BuildLiveTargetCaches(configuration);
        if (configuration.SaveHealTargets)
            changed |= PromoteSavedTargets(configuration);

        ResolveTrackedTargets(configuration);
        SavedTargetCount = configuration.SavedHealTargetEntries.Count;

        if (changed)
            configuration.Save();
    }

    public bool IsWatched(WatchTargetSnapshot target)
        => activeTargets.Any(activeTarget => activeTarget.Entry.Matches(target));

    public bool TryAddWatchedTarget(Configuration configuration, WatchTargetSnapshot target, out string message)
    {
        if (configuration.ActiveWatchedTargets.Any(existingTarget => existingTarget.Matches(target)))
        {
            message = $"{FormatName(configuration, target.Name)} is already in the watched set.";
            return true;
        }

        if (configuration.ActiveWatchedTargets.Count >= MaxTrackedTargets)
        {
            message = $"Watched target cap reached ({MaxTrackedTargets}).";
            return false;
        }

        configuration.ActiveWatchedTargets.Insert(0, PersistedWatchTarget.FromSnapshot(target));
        if (configuration.SaveHealTargets)
            UpsertSavedTarget(configuration, target);

        configuration.Save();
        Update(configuration, force: true);
        message = $"Added {FormatName(configuration, target.Name)} to the watched set.";
        return true;
    }

    public bool TryRemoveWatchedTarget(Configuration configuration, WatchTargetSnapshot target, out string message)
    {
        var removed = configuration.ActiveWatchedTargets.RemoveAll(existingTarget => existingTarget.Matches(target));
        if (removed == 0)
        {
            message = $"{FormatName(configuration, target.Name)} is not currently watched.";
            return false;
        }

        configuration.Save();
        Update(configuration, force: true);
        message = $"Removed {FormatName(configuration, target.Name)} from the watched set.";
        return true;
    }

    public bool TryRemoveRetainedWatchedTarget(Configuration configuration, ResolvedWatchTarget target, bool ctrlHeld, out string message)
    {
        if (!target.IsActive)
        {
            message = $"{FormatName(configuration, target.Name)} is not in the active watched set.";
            return false;
        }

        if (target.RequiresCtrlToRemove && !ctrlHeld)
        {
            message = $"Hold Ctrl while unticking retained target {FormatName(configuration, target.Name)}.";
            return false;
        }

        var removed = configuration.ActiveWatchedTargets.RemoveAll(existingTarget => existingTarget.Matches(target.Entry));
        if (removed == 0)
        {
            message = $"{FormatName(configuration, target.Name)} is not in the active watched set.";
            return false;
        }

        configuration.Save();
        Update(configuration, force: true);
        message = $"Removed {FormatName(configuration, target.Name)} from the watched set.";
        return true;
    }

    public bool TryForgetSavedTarget(Configuration configuration, ResolvedWatchTarget target, out string message)
    {
        if (!target.IsSaved)
        {
            message = $"{FormatName(configuration, target.Name)} is not currently saved.";
            return false;
        }

        var removed = configuration.SavedHealTargetEntries.RemoveAll(existingTarget => existingTarget.Matches(target.Entry));
        if (removed == 0)
        {
            message = $"{FormatName(configuration, target.Name)} is not currently saved.";
            return false;
        }

        configuration.Save();
        Update(configuration, force: true);
        message = $"Forgot saved target {FormatName(configuration, target.Name)}.";
        return true;
    }

    public void ClearWatchedTargets(Configuration configuration)
    {
        if (configuration.ActiveWatchedTargets.Count == 0)
            return;

        configuration.ActiveWatchedTargets.Clear();
        configuration.Save();
        Update(configuration, force: true);
    }

    public bool TryAddCurrentGameTarget(Configuration configuration, out string message)
    {
        if (Plugin.TargetManager.Target is not ICharacter character)
        {
            message = "Current game target is not a character.";
            return false;
        }

        if (!TryResolveSupportedCategory(character, out var category, out var categoryLabel))
        {
            category = WatchTargetCategory.ManualSelection;
            categoryLabel = "Manual Target";
        }

        var snapshot = BuildSnapshot(character, category, categoryLabel, isExternalSelection: category == WatchTargetCategory.ManualSelection);
        return TryAddWatchedTarget(configuration, snapshot, out message);
    }

    private void BuildLiveTargetCaches(Configuration configuration)
    {
        targets.Clear();
        activeTargets.Clear();
        retainedTargets.Clear();
        runtimeCandidates.Clear();
        liveCharactersById.Clear();
        liveCharactersByName.Clear();
        liveSnapshotsById.Clear();

        var localPlayer = Plugin.ObjectTable.LocalPlayer;
        if (localPlayer == null)
            return;

        foreach (var obj in Plugin.ObjectTable)
        {
            if (obj is not ICharacter character)
                continue;

            if (obj.GameObjectId == 0 || obj.GameObjectId == localPlayer.GameObjectId)
                continue;

            var name = obj.Name.TextValue.Trim();
            if (string.IsNullOrWhiteSpace(name))
                continue;

            liveCharactersById[obj.GameObjectId] = character;
            if (!liveCharactersByName.ContainsKey(name))
                liveCharactersByName[name] = character;

            if (!TryResolveSupportedCategory(obj, out var category, out var categoryLabel))
                continue;

            var snapshot = BuildSnapshot(character, category, categoryLabel, isExternalSelection: false);
            liveSnapshotsById[obj.GameObjectId] = snapshot;

            if (IsCategoryEnabled(category, configuration))
                targets.Add(snapshot);
        }

        targets.Sort(static (left, right) =>
        {
            var distanceCompare = left.Distance.CompareTo(right.Distance);
            if (distanceCompare != 0)
                return distanceCompare;

            return string.Compare(left.Name, right.Name, StringComparison.OrdinalIgnoreCase);
        });
    }

    private void ResolveTrackedTargets(Configuration configuration)
    {
        foreach (var activeEntry in configuration.ActiveWatchedTargets
                     .OrderByDescending(static target => target.LastSeenUnixTimeSeconds)
                     .ThenBy(static target => target.Name, StringComparer.OrdinalIgnoreCase))
        {
            var isSaved = configuration.SavedHealTargetEntries.Any(savedTarget => savedTarget.Matches(activeEntry));
            var resolved = ResolveTrackedTarget(activeEntry, configuration, isActive: true, isSaved, out var liveCharacter);
            activeTargets.Add(resolved);

            if (liveCharacter != null && resolved.LiveSnapshot != null)
            {
                runtimeCandidates.Add(new WatchTargetCandidate
                {
                    Character = liveCharacter,
                    Snapshot = resolved.LiveSnapshot,
                    Target = resolved,
                });
            }

            if (!resolved.IsVisibleInObjectTable)
                retainedTargets.Add(resolved);
        }

        foreach (var savedEntry in configuration.SavedHealTargetEntries
                     .Where(savedEntry => configuration.ActiveWatchedTargets.All(activeEntry => !activeEntry.Matches(savedEntry)))
                     .OrderByDescending(static target => target.LastSeenUnixTimeSeconds)
                     .ThenBy(static target => target.Name, StringComparer.OrdinalIgnoreCase))
        {
            var resolved = ResolveTrackedTarget(savedEntry, configuration, isActive: false, isSaved: true, out _);
            if (!resolved.IsVisibleInObjectTable)
                retainedTargets.Add(resolved);
        }

        retainedTargets.Sort(static (left, right) =>
        {
            var activeCompare = right.IsActive.CompareTo(left.IsActive);
            if (activeCompare != 0)
                return activeCompare;

            if ((left.LiveSnapshot is null) != (right.LiveSnapshot is null))
                return left.LiveSnapshot is not null ? -1 : 1;

            return string.Compare(left.Name, right.Name, StringComparison.OrdinalIgnoreCase);
        });
    }

    private ResolvedWatchTarget ResolveTrackedTarget(
        PersistedWatchTarget entry,
        Configuration configuration,
        bool isActive,
        bool isSaved,
        out ICharacter? liveCharacter)
    {
        liveCharacter = null;
        WatchTargetSnapshot? liveSnapshot = null;
        var isVisible = false;
        var isHiddenByFilters = false;

        if (TryResolveLiveTarget(entry, configuration, out liveCharacter, out liveSnapshot, out isVisible, out isHiddenByFilters))
        {
            return new ResolvedWatchTarget
            {
                Entry = entry,
                LiveSnapshot = liveSnapshot,
                IsActive = isActive,
                IsSaved = isSaved,
                IsVisibleInObjectTable = isVisible,
                IsHiddenByFilters = isHiddenByFilters,
                IsWithinScanRange = liveSnapshot!.Distance <= configuration.SavedTargetScanRangeYalms,
                RequiresCtrlToRemove = false,
            };
        }

        return new ResolvedWatchTarget
        {
            Entry = entry,
            IsActive = isActive,
            IsSaved = isSaved,
            IsVisibleInObjectTable = false,
            IsHiddenByFilters = false,
            IsWithinScanRange = false,
            RequiresCtrlToRemove = isActive,
        };
    }

    private bool TryResolveLiveTarget(
        PersistedWatchTarget entry,
        Configuration configuration,
        out ICharacter? liveCharacter,
        out WatchTargetSnapshot? liveSnapshot,
        out bool isVisible,
        out bool isHiddenByFilters)
    {
        liveCharacter = null;
        liveSnapshot = null;
        isVisible = false;
        isHiddenByFilters = false;

        if (entry.GameObjectId != 0 && liveCharactersById.TryGetValue(entry.GameObjectId, out var matchedById))
        {
            liveCharacter = matchedById;
        }
        else if (!string.IsNullOrWhiteSpace(entry.Name) && liveCharactersByName.TryGetValue(entry.Name, out var matchedByName))
        {
            liveCharacter = matchedByName;
        }

        if (liveCharacter == null)
            return false;

        if (liveSnapshotsById.TryGetValue(liveCharacter.GameObjectId, out var supportedSnapshot))
        {
            liveSnapshot = supportedSnapshot;
            isVisible = IsCategoryEnabled(supportedSnapshot.Category, configuration);
            isHiddenByFilters = !isVisible;
            return true;
        }

        if (entry.Category != WatchTargetCategory.ManualSelection && !entry.IsExternalSelection)
            return false;

        liveSnapshot = BuildSnapshot(liveCharacter, WatchTargetCategory.ManualSelection, "Manual Target", isExternalSelection: true);
        return true;
    }

    private bool PromoteSavedTargets(Configuration configuration)
    {
        var changed = false;
        foreach (var savedTarget in configuration.SavedHealTargetEntries
                     .OrderByDescending(static target => target.LastSeenUnixTimeSeconds)
                     .ThenBy(static target => target.Name, StringComparer.OrdinalIgnoreCase))
        {
            if (configuration.ActiveWatchedTargets.Any(activeTarget => activeTarget.Matches(savedTarget)))
                continue;

            if (configuration.ActiveWatchedTargets.Count >= MaxTrackedTargets)
                break;

            if (!TryResolveLiveTarget(savedTarget, configuration, out _, out var liveSnapshot, out _, out _))
                continue;

            if (liveSnapshot == null || liveSnapshot.Distance > configuration.SavedTargetScanRangeYalms)
                continue;

            savedTarget.LastSeenUnixTimeSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var activeTarget = new PersistedWatchTarget
            {
                GameObjectId = savedTarget.GameObjectId,
                EntityId = savedTarget.EntityId,
                Name = savedTarget.Name,
                Category = savedTarget.Category,
                CategoryLabel = savedTarget.CategoryLabel,
                JobLabel = savedTarget.JobLabel,
                IsExternalSelection = savedTarget.IsExternalSelection,
                LastSeenUnixTimeSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            };
            activeTarget.UpdateFromSnapshot(liveSnapshot);
            configuration.ActiveWatchedTargets.Insert(0, activeTarget);
            changed = true;
        }

        return changed;
    }

    private bool NormalizeTrackedCollections(Configuration configuration)
    {
        var changed = false;
        changed |= NormalizeCollection(configuration.ActiveWatchedTargets);
        changed |= NormalizeCollection(configuration.SavedHealTargetEntries);
        return changed;
    }

    private bool NormalizeCollection(List<PersistedWatchTarget> targetsToNormalize)
    {
        var changed = false;
        var normalized = new List<PersistedWatchTarget>(MaxTrackedTargets);

        foreach (var target in targetsToNormalize
                     .Where(static target => target.GameObjectId != 0 || !string.IsNullOrWhiteSpace(target.Name))
                     .OrderByDescending(static target => target.LastSeenUnixTimeSeconds)
                     .ThenBy(static target => target.Name, StringComparer.OrdinalIgnoreCase))
        {
            if (normalized.Any(existingTarget => existingTarget.Matches(target)))
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
                    ? GetCategoryLabel(target.Category)
                    : target.CategoryLabel,
                JobLabel = string.IsNullOrWhiteSpace(target.JobLabel) ? "?" : target.JobLabel,
                IsExternalSelection = target.IsExternalSelection,
                LastSeenUnixTimeSeconds = target.LastSeenUnixTimeSeconds <= 0
                    ? DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                    : target.LastSeenUnixTimeSeconds,
            });

            if (normalized.Count == MaxTrackedTargets)
            {
                if (targetsToNormalize.Count > normalized.Count)
                    changed = true;
                break;
            }
        }

        if (!changed && targetsToNormalize.Count == normalized.Count)
        {
            for (var index = 0; index < normalized.Count; index++)
            {
                if (!targetsToNormalize[index].Matches(normalized[index]) ||
                    targetsToNormalize[index].LastSeenUnixTimeSeconds != normalized[index].LastSeenUnixTimeSeconds)
                {
                    changed = true;
                    break;
                }
            }
        }
        else if (targetsToNormalize.Count != normalized.Count)
        {
            changed = true;
        }

        if (!changed)
            return false;

        targetsToNormalize.Clear();
        targetsToNormalize.AddRange(normalized);
        return true;
    }

    private static WatchTargetSnapshot BuildSnapshot(
        ICharacter character,
        WatchTargetCategory category,
        string categoryLabel,
        bool isExternalSelection)
    {
        var localPlayer = Plugin.ObjectTable.LocalPlayer;
        var jobLabel = character.ClassJob.ValueNullable?.Abbreviation.ToString() ?? "?";
        return new WatchTargetSnapshot
        {
            GameObjectId = character.GameObjectId,
            EntityId = character.EntityId,
            Name = character.Name.TextValue,
            Category = category,
            CategoryLabel = categoryLabel,
            JobLabel = jobLabel,
            Distance = localPlayer == null ? 0f : Vector3.Distance(localPlayer.Position, character.Position),
            CurrentHp = character.CurrentHp,
            MaxHp = character.MaxHp,
            IsDead = character.CurrentHp == 0,
            IsTargetable = character.IsTargetable,
            IsExternalSelection = isExternalSelection,
        };
    }

    private void UpsertSavedTarget(Configuration configuration, WatchTargetSnapshot target)
    {
        var existingTarget = configuration.SavedHealTargetEntries.FirstOrDefault(savedTarget => savedTarget.Matches(target));
        if (existingTarget != null)
        {
            existingTarget.UpdateFromSnapshot(target);
            return;
        }

        if (configuration.SavedHealTargetEntries.Count >= MaxTrackedTargets)
            configuration.SavedHealTargetEntries.RemoveAt(configuration.SavedHealTargetEntries.Count - 1);

        configuration.SavedHealTargetEntries.Insert(0, PersistedWatchTarget.FromSnapshot(target));
    }

    private static bool TryResolveSupportedCategory(
        IGameObject obj,
        out WatchTargetCategory category,
        out string categoryLabel)
    {
        if (obj is IPlayerCharacter)
        {
            category = WatchTargetCategory.Player;
            categoryLabel = "Player";
            return true;
        }

        if (obj is IBattleNpc battleNpc)
        {
            switch (battleNpc.BattleNpcKind)
            {
                case BattleNpcSubKind.Chocobo:
                    category = WatchTargetCategory.CompanionChocobo;
                    categoryLabel = "Chocobo";
                    return true;

                case BattleNpcSubKind.NpcPartyMember:
                    category = WatchTargetCategory.NpcPartyMember;
                    categoryLabel = "NPC Party Member";
                    return true;

                case BattleNpcSubKind.Enemy:
                    category = WatchTargetCategory.FriendlyBattleNpc;
                    categoryLabel = "Battle NPC";
                    return true;
            }
        }

        category = default;
        categoryLabel = string.Empty;
        return false;
    }

    private static bool IsCategoryEnabled(WatchTargetCategory category, Configuration configuration)
        => category switch
        {
            WatchTargetCategory.Player => configuration.WatchPlayers,
            WatchTargetCategory.CompanionChocobo => configuration.WatchCompanionChocobos,
            WatchTargetCategory.NpcPartyMember => configuration.WatchPartyNpcs,
            WatchTargetCategory.FriendlyBattleNpc => configuration.WatchFriendlyBattleNpcs,
            _ => false,
        };

    private static string GetCategoryLabel(WatchTargetCategory category)
        => category switch
        {
            WatchTargetCategory.Player => "Player",
            WatchTargetCategory.CompanionChocobo => "Chocobo",
            WatchTargetCategory.NpcPartyMember => "NPC Party Member",
            WatchTargetCategory.FriendlyBattleNpc => "Battle NPC",
            _ => "Manual Target",
        };

    private static string FormatName(Configuration configuration, string rawName)
        => configuration.KrangleNames ? KrangleService.KrangleName(rawName) : rawName;
}

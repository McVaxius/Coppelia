namespace Coppelia.Models;

internal static class HealbotActionCatalog
{
    public static IReadOnlyList<HealbotActionGroup> ConfigGroupOrder { get; } =
    [
        HealbotActionGroup.CastedGcd,
        HealbotActionGroup.InstantOgcd,
        HealbotActionGroup.CastedBuff,
        HealbotActionGroup.InstantBuff,
    ];

    public static IReadOnlyList<HealbotActionGroup> AliveEvaluationOrder { get; } =
    [
        HealbotActionGroup.InstantBuff,
        HealbotActionGroup.InstantOgcd,
        HealbotActionGroup.CastedBuff,
        HealbotActionGroup.CastedGcd,
    ];

    public static IReadOnlyList<HealbotActionGroup> DeadTargetEvaluationOrder { get; } =
    [
        HealbotActionGroup.InstantBuff,
        HealbotActionGroup.CastedGcd,
        HealbotActionGroup.InstantOgcd,
        HealbotActionGroup.CastedBuff,
    ];

    public static IReadOnlyList<HealbotActionDefinition> GetDefinitions(uint jobId)
        => DefinitionsByJob.TryGetValue(jobId, out var definitions)
            ? definitions
            : [];

    public static bool TryGetDefinition(uint jobId, string actionName, out HealbotActionDefinition definition)
    {
        definition = null!;
        if (!DefinitionsByJob.TryGetValue(jobId, out var definitions))
            return false;

        definition = definitions.FirstOrDefault(item =>
            string.Equals(item.ActionName, actionName, StringComparison.OrdinalIgnoreCase))!;
        return definition != null;
    }

    public static bool EnsureConfigCoverage(HealerJobConfig config, uint jobId, HealerJobSettings? legacySettings = null)
    {
        var changed = false;
        if (config.ActionRules == null)
        {
            config.ActionRules = [];
            changed = true;
        }

        if (legacySettings != null && config.ActionRules.Count == 0)
        {
            config.Enabled = legacySettings.Enabled;
            changed = true;
        }

        var definitions = GetDefinitions(jobId);
        var allowedNames = new HashSet<string>(
            definitions.Select(definition => definition.ActionName),
            StringComparer.OrdinalIgnoreCase);

        for (var index = config.ActionRules.Count - 1; index >= 0; index--)
        {
            var rule = config.ActionRules[index];
            if (!allowedNames.Contains(rule.ActionName))
            {
                config.ActionRules.RemoveAt(index);
                changed = true;
            }
        }

        foreach (var definition in definitions)
        {
            var existingRule = config.ActionRules.FirstOrDefault(rule =>
                string.Equals(rule.ActionName, definition.ActionName, StringComparison.OrdinalIgnoreCase));

            if (existingRule == null)
            {
                config.ActionRules.Add(BuildDefaultRule(definition, legacySettings));
                changed = true;
                continue;
            }

            changed |= NormalizeExistingRule(existingRule, definition);
        }

        config.ActionRules.Sort(static (left, right) =>
            string.Compare(left.ActionName, right.ActionName, StringComparison.OrdinalIgnoreCase));

        return changed;
    }

    private static bool NormalizeExistingRule(HealerActionRule rule, HealbotActionDefinition definition)
    {
        var before = rule.BuildSignature();
        rule.Normalize();

        if (string.IsNullOrWhiteSpace(definition.TrackedStatusName))
        {
            if (rule.TriggerKind == HealbotTriggerKind.MissingBuff)
                rule.TriggerKind = definition.DefaultTriggerKind;

            rule.RequireMissingTrackedStatus = false;
        }

        if (definition.DefaultTriggerKind == HealbotTriggerKind.DeadTarget &&
            rule.TriggerKind != HealbotTriggerKind.DeadTarget &&
            rule.TriggerKind != HealbotTriggerKind.Always)
        {
            rule.TriggerKind = HealbotTriggerKind.DeadTarget;
        }

        return !string.Equals(before, rule.BuildSignature(), StringComparison.Ordinal);
    }

    private static HealerActionRule BuildDefaultRule(HealbotActionDefinition definition, HealerJobSettings? legacySettings)
    {
        var hpThreshold = definition.DefaultHpThresholdPercent;
        if (legacySettings != null && definition.DefaultTriggerKind != HealbotTriggerKind.DeadTarget)
        {
            hpThreshold = definition.Group is HealbotActionGroup.CastedGcd or HealbotActionGroup.CastedBuff
                ? legacySettings.SpellThresholdPercent
                : legacySettings.AbilityThresholdPercent;
        }

        var enabled = definition.DefaultEnabled;
        if (legacySettings != null && definition.DefaultTriggerKind == HealbotTriggerKind.DeadTarget)
            enabled &= legacySettings.RaiseEnabled;

        var rule = new HealerActionRule
        {
            ActionName = definition.ActionName,
            Enabled = enabled,
            Priority = definition.DefaultPriority,
            TriggerKind = definition.DefaultTriggerKind,
            HpThresholdPercent = hpThreshold,
            MinimumMpPercent = definition.DefaultMinimumMpPercent,
            AllowOutOfCombat = definition.DefaultAllowOutOfCombat,
            RequireMissingTrackedStatus = definition.DefaultRequireMissingTrackedStatus && !string.IsNullOrWhiteSpace(definition.TrackedStatusName),
        };

        rule.Normalize();
        return rule;
    }

    private static readonly IReadOnlyDictionary<uint, IReadOnlyList<HealbotActionDefinition>> DefinitionsByJob =
        new Dictionary<uint, IReadOnlyList<HealbotActionDefinition>>
        {
            [24] =
            [
                new("Swiftcast", HealbotActionGroup.InstantBuff, HealbotTargetKind.Self, HealbotTriggerKind.DeadTarget, 0, 10, true, true, 0, true, "Swiftcast", true),
                new("Thin Air", HealbotActionGroup.InstantBuff, HealbotTargetKind.Self, HealbotTriggerKind.DeadTarget, 0, 20, true, true, 0, true, "Thin Air", true),
                new("Divine Benison", HealbotActionGroup.InstantBuff, HealbotTargetKind.SelectedTarget, HealbotTriggerKind.HpBelow, 85, 30, true, true, 0, true, "Divine Benison"),
                new("Aquaveil", HealbotActionGroup.InstantBuff, HealbotTargetKind.SelectedTarget, HealbotTriggerKind.HpBelow, 65, 40, true, true, 0, true, "Aquaveil"),
                new("Benediction", HealbotActionGroup.InstantOgcd, HealbotTargetKind.SelectedTarget, HealbotTriggerKind.HpBelow, 20, 10, true),
                new("Tetragrammaton", HealbotActionGroup.InstantOgcd, HealbotTargetKind.SelectedTarget, HealbotTriggerKind.HpBelow, 72, 20, true),
                new("Afflatus Solace", HealbotActionGroup.InstantOgcd, HealbotTargetKind.SelectedTarget, HealbotTriggerKind.HpBelow, 55, 30, true),
                new("Regen", HealbotActionGroup.CastedBuff, HealbotTargetKind.SelectedTarget, HealbotTriggerKind.MissingBuff, 85, 10, true, true, 0, true, "Regen"),
                new("Cure II", HealbotActionGroup.CastedGcd, HealbotTargetKind.SelectedTarget, HealbotTriggerKind.HpBelow, 67, 20, true),
                new("Cure", HealbotActionGroup.CastedGcd, HealbotTargetKind.SelectedTarget, HealbotTriggerKind.HpBelow, 50, 30, false),
                new("Raise", HealbotActionGroup.CastedGcd, HealbotTargetKind.SelectedTarget, HealbotTriggerKind.DeadTarget, 0, 40, true, true, 24, false, null, true),
            ],
            [28] =
            [
                new("Swiftcast", HealbotActionGroup.InstantBuff, HealbotTargetKind.Self, HealbotTriggerKind.DeadTarget, 0, 10, true, true, 0, true, "Swiftcast", true),
                new("Recitation", HealbotActionGroup.InstantBuff, HealbotTargetKind.Self, HealbotTriggerKind.HpBelow, 55, 20, false, true, 0, true, "Recitation"),
                new("Excogitation", HealbotActionGroup.InstantBuff, HealbotTargetKind.SelectedTarget, HealbotTriggerKind.HpBelow, 55, 30, true, true, 0, true, "Excogitation"),
                new("Protraction", HealbotActionGroup.InstantBuff, HealbotTargetKind.SelectedTarget, HealbotTriggerKind.HpBelow, 60, 40, true, true, 0, true, "Protraction"),
                new("Lustrate", HealbotActionGroup.InstantOgcd, HealbotTargetKind.SelectedTarget, HealbotTriggerKind.HpBelow, 70, 10, true),
                new("Aetherpact", HealbotActionGroup.InstantOgcd, HealbotTargetKind.SelectedTarget, HealbotTriggerKind.HpBelow, 50, 20, false),
                new("Adloquium", HealbotActionGroup.CastedBuff, HealbotTargetKind.SelectedTarget, HealbotTriggerKind.MissingBuff, 75, 10, true, true, 0, true, "Galvanize"),
                new("Physick", HealbotActionGroup.CastedGcd, HealbotTargetKind.SelectedTarget, HealbotTriggerKind.HpBelow, 60, 20, false),
                new("Resurrection", HealbotActionGroup.CastedGcd, HealbotTargetKind.SelectedTarget, HealbotTriggerKind.DeadTarget, 0, 30, true, true, 24, false, null, true),
            ],
            [33] =
            [
                new("Swiftcast", HealbotActionGroup.InstantBuff, HealbotTargetKind.Self, HealbotTriggerKind.DeadTarget, 0, 10, true, true, 0, true, "Swiftcast", true),
                new("Celestial Intersection", HealbotActionGroup.InstantBuff, HealbotTargetKind.SelectedTarget, HealbotTriggerKind.HpBelow, 80, 20, true),
                new("Exaltation", HealbotActionGroup.InstantBuff, HealbotTargetKind.SelectedTarget, HealbotTriggerKind.HpBelow, 65, 30, true),
                new("Synastry", HealbotActionGroup.InstantBuff, HealbotTargetKind.SelectedTarget, HealbotTriggerKind.HpBelow, 45, 40, false),
                new("Essential Dignity", HealbotActionGroup.InstantOgcd, HealbotTargetKind.SelectedTarget, HealbotTriggerKind.HpBelow, 72, 10, true),
                new("Aspected Benefic", HealbotActionGroup.CastedBuff, HealbotTargetKind.SelectedTarget, HealbotTriggerKind.MissingBuff, 80, 10, true, true, 0, true, "Aspected Benefic"),
                new("Benefic II", HealbotActionGroup.CastedGcd, HealbotTargetKind.SelectedTarget, HealbotTriggerKind.HpBelow, 66, 20, true),
                new("Benefic", HealbotActionGroup.CastedGcd, HealbotTargetKind.SelectedTarget, HealbotTriggerKind.HpBelow, 50, 30, false),
                new("Ascend", HealbotActionGroup.CastedGcd, HealbotTargetKind.SelectedTarget, HealbotTriggerKind.DeadTarget, 0, 40, true, true, 24, false, null, true),
            ],
            [40] =
            [
                new("Swiftcast", HealbotActionGroup.InstantBuff, HealbotTargetKind.Self, HealbotTriggerKind.DeadTarget, 0, 10, true, true, 0, true, "Swiftcast", true),
                new("Krasis", HealbotActionGroup.InstantBuff, HealbotTargetKind.SelectedTarget, HealbotTriggerKind.HpBelow, 70, 20, true, true, 0, true, "Krasis"),
                new("Haima", HealbotActionGroup.InstantBuff, HealbotTargetKind.SelectedTarget, HealbotTriggerKind.HpBelow, 55, 30, true, true, 0, true, "Haima"),
                new("Taurochole", HealbotActionGroup.InstantBuff, HealbotTargetKind.SelectedTarget, HealbotTriggerKind.HpBelow, 60, 40, true),
                new("Eukrasia", HealbotActionGroup.InstantBuff, HealbotTargetKind.Self, HealbotTriggerKind.HpBelow, 70, 50, false, true, 0, true, "Eukrasia"),
                new("Druochole", HealbotActionGroup.InstantOgcd, HealbotTargetKind.SelectedTarget, HealbotTriggerKind.HpBelow, 70, 10, true),
                new("Diagnosis", HealbotActionGroup.CastedGcd, HealbotTargetKind.SelectedTarget, HealbotTriggerKind.HpBelow, 65, 20, true),
                new("Egeiro", HealbotActionGroup.CastedGcd, HealbotTargetKind.SelectedTarget, HealbotTriggerKind.DeadTarget, 0, 40, true, true, 24, false, null, true),
            ],
        };
}

internal sealed class HealbotActionDefinition
{
    public HealbotActionDefinition(
        string actionName,
        HealbotActionGroup group,
        HealbotTargetKind targetKind,
        HealbotTriggerKind defaultTriggerKind,
        int defaultHpThresholdPercent,
        int defaultPriority,
        bool defaultEnabled,
        bool defaultAllowOutOfCombat = true,
        int defaultMinimumMpPercent = 0,
        bool defaultRequireMissingTrackedStatus = false,
        string? trackedStatusName = null,
        bool allowedWhenSelectedTargetDead = false)
    {
        ActionName = actionName;
        Group = group;
        TargetKind = targetKind;
        DefaultTriggerKind = defaultTriggerKind;
        DefaultHpThresholdPercent = defaultHpThresholdPercent;
        DefaultPriority = defaultPriority;
        DefaultEnabled = defaultEnabled;
        DefaultAllowOutOfCombat = defaultAllowOutOfCombat;
        DefaultMinimumMpPercent = defaultMinimumMpPercent;
        DefaultRequireMissingTrackedStatus = defaultRequireMissingTrackedStatus;
        TrackedStatusName = trackedStatusName;
        AllowedWhenSelectedTargetDead = allowedWhenSelectedTargetDead;
    }

    public string ActionName { get; }
    public HealbotActionGroup Group { get; }
    public HealbotTargetKind TargetKind { get; }
    public HealbotTriggerKind DefaultTriggerKind { get; }
    public int DefaultHpThresholdPercent { get; }
    public int DefaultPriority { get; }
    public bool DefaultEnabled { get; }
    public bool DefaultAllowOutOfCombat { get; }
    public int DefaultMinimumMpPercent { get; }
    public bool DefaultRequireMissingTrackedStatus { get; }
    public string? TrackedStatusName { get; }
    public bool AllowedWhenSelectedTargetDead { get; }
}

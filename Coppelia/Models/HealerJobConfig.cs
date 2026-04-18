namespace Coppelia.Models;

[Serializable]
public sealed class HealerJobConfig
{
    public bool Enabled { get; set; } = true;
    public List<HealerActionRule> ActionRules { get; set; } = [];

    public string BuildSignature()
    {
        var sorted = ActionRules
            .OrderBy(rule => rule.ActionName, StringComparer.OrdinalIgnoreCase)
            .Select(rule => rule.BuildSignature());
        return string.Join(";", Enabled, string.Join("|", sorted));
    }
}

[Serializable]
public sealed class HealerActionRule
{
    public string ActionName { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;
    public int Priority { get; set; }
    public HealbotTriggerKind TriggerKind { get; set; } = HealbotTriggerKind.HpBelow;
    public int HpThresholdPercent { get; set; } = 70;
    public int MinimumMpPercent { get; set; }
    public bool AllowOutOfCombat { get; set; } = true;
    public bool RequireMissingTrackedStatus { get; set; }

    public void Normalize()
    {
        ActionName = ActionName.Trim();
        Priority = Math.Clamp(Priority, 0, 999);
        HpThresholdPercent = Math.Clamp(HpThresholdPercent, 0, 100);
        MinimumMpPercent = Math.Clamp(MinimumMpPercent, 0, 100);
    }

    public string BuildSignature()
        => string.Join(",",
            ActionName,
            Enabled,
            Priority,
            (int)TriggerKind,
            HpThresholdPercent,
            MinimumMpPercent,
            AllowOutOfCombat,
            RequireMissingTrackedStatus);
}

public enum HealbotActionGroup
{
    CastedGcd,
    InstantOgcd,
    CastedBuff,
    InstantBuff,
}

public enum HealbotTriggerKind
{
    HpBelow,
    DeadTarget,
    MissingBuff,
    Always,
}

internal enum HealbotTargetKind
{
    SelectedTarget,
    Self,
}

internal static class HealbotUiText
{
    public static string GetLabel(this HealbotActionGroup group)
        => group switch
        {
            HealbotActionGroup.CastedGcd => "Casted GCD",
            HealbotActionGroup.InstantOgcd => "Instant oGCD",
            HealbotActionGroup.CastedBuff => "Casted BUFF",
            HealbotActionGroup.InstantBuff => "Instant BUFF",
            _ => group.ToString(),
        };

    public static string GetLabel(this HealbotTriggerKind trigger)
        => trigger switch
        {
            HealbotTriggerKind.HpBelow => "HP Below",
            HealbotTriggerKind.DeadTarget => "Dead Target",
            HealbotTriggerKind.MissingBuff => "Missing Buff",
            HealbotTriggerKind.Always => "Always",
            _ => trigger.ToString(),
        };

    public static string GetLabel(this HealbotTargetKind targetKind)
        => targetKind switch
        {
            HealbotTargetKind.SelectedTarget => "Target",
            HealbotTargetKind.Self => "Self",
            _ => targetKind.ToString(),
        };
}

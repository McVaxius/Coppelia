namespace Coppelia.Models;

[Serializable]
public sealed class HealerJobSettings
{
    public bool Enabled { get; set; } = true;
    public bool RaiseEnabled { get; set; } = true;
    public int AbilityThresholdPercent { get; set; } = 70;
    public int SpellThresholdPercent { get; set; } = 65;

    public float GetHighestTriggerRatio()
        => Math.Clamp(Math.Max(AbilityThresholdPercent, SpellThresholdPercent), 0, 100) / 100f;
}

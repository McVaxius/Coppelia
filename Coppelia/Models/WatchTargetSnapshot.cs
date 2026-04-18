namespace Coppelia.Models;

public enum WatchTargetCategory
{
    Player,
    CompanionChocobo,
    NpcPartyMember,
    FriendlyBattleNpc,
    ManualSelection,
}

internal sealed class WatchTargetSnapshot
{
    public ulong GameObjectId { get; init; }
    public uint EntityId { get; init; }
    public string Name { get; init; } = string.Empty;
    public string CategoryLabel { get; init; } = string.Empty;
    public string JobLabel { get; init; } = string.Empty;
    public WatchTargetCategory Category { get; init; }
    public float Distance { get; init; }
    public uint CurrentHp { get; init; }
    public uint MaxHp { get; init; }
    public bool IsDead { get; init; }
    public bool IsTargetable { get; init; }
    public bool IsExternalSelection { get; init; }
    public float HpRatio => MaxHp == 0 ? 0f : CurrentHp / (float)MaxHp;
    public int HpPercent => MaxHp == 0 ? 0 : (int)MathF.Round(HpRatio * 100f);
}

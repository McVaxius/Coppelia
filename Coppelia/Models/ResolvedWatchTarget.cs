namespace Coppelia.Models;

internal sealed class ResolvedWatchTarget
{
    public PersistedWatchTarget Entry { get; init; } = new();
    public WatchTargetSnapshot? LiveSnapshot { get; init; }
    public bool IsActive { get; init; }
    public bool IsSaved { get; init; }
    public bool IsVisibleInObjectTable { get; init; }
    public bool IsHiddenByFilters { get; init; }
    public bool RequiresCtrlToRemove { get; init; }
    public bool IsWithinScanRange { get; init; }

    public bool IsMissingFromObjectTable => LiveSnapshot == null;
    public string Name => LiveSnapshot?.Name ?? Entry.Name;
    public string CategoryLabel => LiveSnapshot?.CategoryLabel ?? Entry.CategoryLabel;
    public string JobLabel => LiveSnapshot?.JobLabel ?? Entry.JobLabel;
    public float Distance => LiveSnapshot?.Distance ?? float.NaN;
    public int HpPercent => LiveSnapshot?.HpPercent ?? 0;
    public bool IsDead => LiveSnapshot?.IsDead ?? false;
    public bool IsTargetable => LiveSnapshot?.IsTargetable ?? false;
}

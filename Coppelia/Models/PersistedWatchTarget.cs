namespace Coppelia.Models;

[Serializable]
public sealed class PersistedWatchTarget
{
    public ulong GameObjectId { get; set; }
    public uint EntityId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string CategoryLabel { get; set; } = string.Empty;
    public string JobLabel { get; set; } = "?";
    public WatchTargetCategory Category { get; set; } = WatchTargetCategory.ManualSelection;
    public bool IsExternalSelection { get; set; }
    public long LastSeenUnixTimeSeconds { get; set; }

    internal static PersistedWatchTarget FromSnapshot(WatchTargetSnapshot snapshot)
    {
        var target = new PersistedWatchTarget();
        target.UpdateFromSnapshot(snapshot);
        return target;
    }

    internal void UpdateFromSnapshot(WatchTargetSnapshot snapshot)
    {
        GameObjectId = snapshot.GameObjectId;
        EntityId = snapshot.EntityId;
        Name = snapshot.Name;
        Category = snapshot.Category;
        CategoryLabel = snapshot.CategoryLabel;
        JobLabel = string.IsNullOrWhiteSpace(snapshot.JobLabel) ? "?" : snapshot.JobLabel;
        IsExternalSelection = snapshot.IsExternalSelection;
        LastSeenUnixTimeSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    }

    internal bool Matches(WatchTargetSnapshot snapshot)
        => Matches(snapshot.GameObjectId, snapshot.Name);

    public bool Matches(PersistedWatchTarget other)
        => Matches(other.GameObjectId, other.Name);

    public bool Matches(ulong gameObjectId, string name)
    {
        if (GameObjectId != 0 && gameObjectId != 0 && GameObjectId == gameObjectId)
            return true;

        return !string.IsNullOrWhiteSpace(Name) &&
               !string.IsNullOrWhiteSpace(name) &&
               string.Equals(Name, name, StringComparison.OrdinalIgnoreCase);
    }
}

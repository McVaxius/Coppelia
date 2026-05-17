namespace Coppelia.Services;

internal sealed class DependencyService
{
    private DateTimeOffset nextRefreshUtc = DateTimeOffset.MinValue;

    public DependencySnapshot Current { get; private set; } = new();

    public void Refresh(bool force = false)
    {
        if (!force && DateTimeOffset.UtcNow < nextRefreshUtc)
            return;

        nextRefreshUtc = DateTimeOffset.UtcNow.AddSeconds(2);
        var installed = Plugin.PluginInterface.InstalledPlugins;
        var hasRotationSolver = installed.Any(plugin =>
            plugin.IsLoaded &&
            string.Equals(plugin.InternalName, "RotationSolver", StringComparison.OrdinalIgnoreCase));
        var hasFrenRider = installed.Any(plugin =>
            plugin.IsLoaded &&
            string.Equals(plugin.InternalName, "FrenRider", StringComparison.OrdinalIgnoreCase));
        var hasVNavmesh = installed.Any(plugin =>
            plugin.IsLoaded &&
            string.Equals(plugin.InternalName, "vnavmesh", StringComparison.OrdinalIgnoreCase));
        var hasBossModReborn = installed.Any(plugin =>
            plugin.IsLoaded &&
            string.Equals(plugin.InternalName, "BossModReborn", StringComparison.OrdinalIgnoreCase));
        var hasVbm = installed.Any(plugin =>
            plugin.IsLoaded &&
            (string.Equals(plugin.InternalName, "BossMod", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(plugin.InternalName, "vbm", StringComparison.OrdinalIgnoreCase)));

        Current = new DependencySnapshot
        {
            RotationSolverLoaded = hasRotationSolver,
            FrenRiderLoaded = hasFrenRider,
            VNavmeshLoaded = hasVNavmesh,
            BossModRebornLoaded = hasBossModReborn,
            VbmLoaded = hasVbm,
        };
    }

    public string BuildMissingDependencyMessage()
    {
        var missing = new List<string>();
        if (!Current.FrenRiderLoaded)
            missing.Add("FrenRider");
        if (!Current.VNavmeshLoaded)
            missing.Add("vnavmesh");
        if (!Current.HasBossModProvider)
            missing.Add("BMR or VBM");

        return missing.Count == 0
            ? "All Coppelia required dependencies are ready."
            : $"Coppelia requires {string.Join(", ", missing)}. Install the missing plugin(s) or disable healbot in settings.";
    }
}

internal sealed class DependencySnapshot
{
    public bool RotationSolverLoaded { get; init; }
    public bool FrenRiderLoaded { get; init; }
    public bool VNavmeshLoaded { get; init; }
    public bool BossModRebornLoaded { get; init; }
    public bool VbmLoaded { get; init; }

    public bool HasBossModProvider => BossModRebornLoaded || VbmLoaded;
    public bool IsHealbotReady => FrenRiderLoaded && VNavmeshLoaded && HasBossModProvider;
}

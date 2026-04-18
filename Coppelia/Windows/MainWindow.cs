using System.Diagnostics;
using System.Numerics;
using System.Reflection;
using Coppelia.Models;
using Coppelia.Services;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace Coppelia.Windows;

public sealed class MainWindow : Window, IDisposable
{
    private readonly Plugin plugin;
    private DateTimeOffset nextWindowPositionSaveUtc = DateTimeOffset.MinValue;
    private Vector2? pendingWindowPosition;
    private Vector2? lastSavedWindowPosition;
    private bool pendingSavedPositionApply;

    public MainWindow(Plugin plugin)
        : base($"{PluginInfo.DisplayName}###CoppeliaMain")
    {
        this.plugin = plugin;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(760f, 560f),
            MaximumSize = new Vector2(1400f, 1100f),
        };
        Size = new Vector2(980f, 720f);
        SizeCondition = ImGuiCond.FirstUseEver;
    }

    public void Dispose()
    {
    }

    public override void PreDraw()
    {
        if (pendingWindowPosition.HasValue)
        {
            Position = pendingWindowPosition.Value;
            PositionCondition = ImGuiCond.Always;
            pendingSavedPositionApply = true;
            pendingWindowPosition = null;
        }
    }

    public override void Draw()
    {
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(6f, 4f));
        try
        {
            DrawHeader();
            ImGui.Separator();
            DrawStateControls();
            ImGui.Separator();
            DrawDependencyPanel();
            ImGui.Separator();
            DrawWatchedTargetsPanel();
            ImGui.Separator();
            DrawWatchList();
            TrackWindowPosition();
        }
        finally
        {
            ImGui.PopStyleVar();
        }

        if (pendingSavedPositionApply)
        {
            pendingSavedPositionApply = false;
            Position = null;
            PositionCondition = ImGuiCond.None;
        }
    }

    public void ApplySavedPosition()
    {
        if (plugin.TryGetSavedWindowPosition(false, out var saved))
        {
            pendingWindowPosition = saved.ToVector2();
            return;
        }

        pendingWindowPosition = new Vector2(1f, 1f);
    }

    public void QueueRandomVisibleJump()
    {
        var viewport = ImGui.GetMainViewport();
        var targetPosition = WindowPlacementHelper.BuildRandomVisiblePosition(
            Size ?? new Vector2(980f, 720f),
            viewport.WorkPos,
            viewport.WorkSize);

        pendingWindowPosition = targetPosition;
        plugin.SaveCurrentWindowPosition(settingsWindow: false, targetPosition);
    }

    private void DrawHeader()
    {
        var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0.0.0";
        ImGui.Text($"{PluginInfo.DisplayName} v{version}");
        ImGui.SameLine();
        ImGui.TextDisabled($"Commands: {PluginInfo.Command}, {PluginInfo.AliasCommand}, {PluginInfo.Command} ws, {PluginInfo.Command} j");

        if (ImGui.SmallButton("Ko-fi##CoppeliaMain"))
            Process.Start(new ProcessStartInfo { FileName = PluginInfo.SupportUrl, UseShellExecute = true });

        ImGui.SameLine();
        if (ImGui.SmallButton("Settings##CoppeliaMain"))
            plugin.ToggleConfigUi();

        ImGui.SameLine();
        if (ImGui.SmallButton("Watch##CoppeliaMain"))
            plugin.ToggleWatchUi();

        ImGui.SameLine();
        if (ImGui.SmallButton("Status to chat##CoppeliaMain"))
            plugin.PrintStatus(plugin.HealbotRuntimeService.StatusText);
    }

    private void DrawStateControls()
    {
        var configuration = plugin.Configuration;

        var pluginEnabled = configuration.PluginEnabled;
        if (ImGui.Checkbox("Plugin enabled", ref pluginEnabled))
            plugin.SetPluginEnabled(pluginEnabled, printStatus: true);

        ImGui.SameLine();
        var healbotEnabled = configuration.HealbotEnabled;
        if (ImGui.Checkbox("Healbot mode", ref healbotEnabled))
            plugin.SetHealbotEnabled(healbotEnabled, printStatus: true);

        ImGui.SameLine();
        var krangleEnabled = configuration.KrangleNames;
        if (ImGui.Checkbox("Krangle", ref krangleEnabled))
        {
            configuration.KrangleNames = krangleEnabled;
            configuration.Save();
            if (!krangleEnabled)
                KrangleService.ClearCache();
        }

        ImGui.SameLine();
        var dtrEnabled = configuration.DtrBarEnabled;
        if (ImGui.Checkbox("DTR bar", ref dtrEnabled))
        {
            configuration.DtrBarEnabled = dtrEnabled;
            configuration.Save();
            plugin.UpdateDtrBar();
        }

        ImGui.SameLine();
        if (ImGui.SmallButton("Refresh watch list##CoppeliaMain"))
            plugin.WatchTargetService.Update(configuration, force: true);

        ImGui.SameLine();
        if (ImGui.SmallButton("Target current##CoppeliaMain"))
        {
            plugin.WatchTargetService.TryAddCurrentGameTarget(configuration, out var message);
            plugin.PrintStatus(message);
        }

        ImGui.SameLine();
        if (ImGui.SmallButton("Clear watched##CoppeliaMain"))
        {
            plugin.WatchTargetService.ClearWatchedTargets(configuration);
            plugin.PrintStatus("Cleared the active watched set.");
        }

        ImGui.TextWrapped($"Coppelia now scans up to {WatchTargetService.MaxTrackedTargets} watched targets, healer jobs only, with one action decision per cycle and hard dependency gates before healbot mode can arm.");
        ImGui.TextColored(new Vector4(0.80f, 0.88f, 1.0f, 1.0f), plugin.HealbotRuntimeService.StatusText);
        ImGui.TextDisabled($"Last action: {plugin.HealbotRuntimeService.LastIssuedAction}");
        ImGui.TextDisabled($"Last rule: {plugin.HealbotRuntimeService.LastMatchedRule}");
    }

    private void DrawDependencyPanel()
    {
        var snapshot = plugin.DependencyService.Current;
        ImGui.TextUnformatted("Required plugins");
        DrawDependencyLine("RSR", snapshot.RotationSolverLoaded);
        DrawDependencyLine("FrenRider", snapshot.FrenRiderLoaded);
        DrawDependencyLine("vnavmesh", snapshot.VNavmeshLoaded);
        DrawDependencyLine("BMR", snapshot.BossModRebornLoaded);
        DrawDependencyLine("VBM", snapshot.VbmLoaded);

        if (!snapshot.IsHealbotReady)
            ImGui.TextColored(new Vector4(1.0f, 0.55f, 0.55f, 1.0f), plugin.DependencyService.BuildMissingDependencyMessage());

        ImGui.TextDisabled("RSR seam: dependency gate, offensive suppression, and session restore remain active. Heals now fire through direct ActionManager execution.");
    }

    private void DrawWatchedTargetsPanel()
    {
        var activeTargets = plugin.WatchTargetService.ActiveTargets;
        ImGui.TextUnformatted("Watched targets");

        if (!plugin.HealbotRuntimeService.IsSupportedLocalJob(out var profile, out var reason))
            ImGui.TextDisabled(reason);
        else
            ImGui.TextDisabled($"Local healer: {profile!.JobDisplayName} ({profile.JobAbbreviation})");

        ImGui.TextDisabled($"Active: {activeTargets.Count}/{WatchTargetService.MaxTrackedTargets} | Live: {plugin.WatchTargetService.RuntimeCandidates.Count} | Saved: {plugin.WatchTargetService.SavedTargetCount}");

        if (activeTargets.Count == 0)
        {
            ImGui.TextDisabled("No watched targets selected yet.");
            return;
        }

        foreach (var target in activeTargets.Take(6))
        {
            ImGui.BulletText($"{plugin.FormatDisplayName(target.Name)} [{target.JobLabel}] - {BuildStateLabel(target)}");
        }

        if (activeTargets.Count > 6)
            ImGui.TextDisabled($"...and {activeTargets.Count - 6} more watched targets.");

        if (plugin.WatchTargetService.RetainedTargets.Count > 0)
            ImGui.TextColored(new Vector4(0.95f, 0.78f, 0.42f, 1.0f), $"{plugin.WatchTargetService.RetainedTargets.Count} retained/saved target(s) are hidden or absent. Open the watch window to manage them.");
    }

    private void DrawWatchList()
    {
        var targets = plugin.WatchTargetService.Targets;
        ImGui.TextUnformatted($"Visible object table ({targets.Count})");

        if (targets.Count == 0)
        {
            ImGui.TextDisabled("No eligible targets for the current filters.");
            return;
        }

        ImGui.SameLine();
        if (ImGui.SmallButton("Open watch window##CoppeliaMain"))
            plugin.OpenWatchUi();

        if (!ImGui.BeginTable("CoppeliaWatchList", 6, ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.ScrollY | ImGuiTableFlags.BordersInnerV, new Vector2(-1f, 170f)))
            return;

        ImGui.TableSetupColumn("Watch", ImGuiTableColumnFlags.WidthFixed, 54f);
        ImGui.TableSetupColumn("Name", ImGuiTableColumnFlags.WidthStretch, 0.35f);
        ImGui.TableSetupColumn("Type", ImGuiTableColumnFlags.WidthFixed, 150f);
        ImGui.TableSetupColumn("Job", ImGuiTableColumnFlags.WidthFixed, 70f);
        ImGui.TableSetupColumn("HP", ImGuiTableColumnFlags.WidthFixed, 90f);
        ImGui.TableSetupColumn("Dist", ImGuiTableColumnFlags.WidthFixed, 64f);
        ImGui.TableHeadersRow();

        foreach (var target in targets)
        {
            var isWatched = plugin.WatchTargetService.IsWatched(target);
            ImGui.TableNextRow();

            ImGui.TableSetColumnIndex(0);
            var watchedState = isWatched;
            if (ImGui.Checkbox($"##CompactWatch{target.GameObjectId}", ref watchedState))
                ToggleLiveTarget(target, watchedState);

            ImGui.TableSetColumnIndex(1);
            ImGui.TextUnformatted(plugin.FormatDisplayName(target.Name));

            ImGui.TableSetColumnIndex(2);
            ImGui.TextUnformatted(target.CategoryLabel);

            ImGui.TableSetColumnIndex(3);
            ImGui.TextUnformatted(target.JobLabel);

            ImGui.TableSetColumnIndex(4);
            var hpText = target.IsDead
                ? "Dead"
                : $"{target.HpPercent}%";
            var hpColor = target.IsDead
                ? new Vector4(1.0f, 0.55f, 0.55f, 1.0f)
                : target.HpPercent <= 50
                    ? new Vector4(1.0f, 0.80f, 0.40f, 1.0f)
                    : new Vector4(0.75f, 0.90f, 1.0f, 1.0f);
            ImGui.TextColored(hpColor, hpText);

            ImGui.TableSetColumnIndex(5);
            ImGui.TextUnformatted($"{target.Distance:F1}");
        }

        ImGui.EndTable();
    }

    private void ToggleLiveTarget(WatchTargetSnapshot target, bool shouldWatch)
    {
        if (shouldWatch)
        {
            plugin.WatchTargetService.TryAddWatchedTarget(plugin.Configuration, target, out var message);
            plugin.PrintStatus(message);
            return;
        }

        plugin.WatchTargetService.TryRemoveWatchedTarget(plugin.Configuration, target, out var removalMessage);
        plugin.PrintStatus(removalMessage);
    }

    private static string BuildStateLabel(ResolvedWatchTarget target)
    {
        if (target.IsMissingFromObjectTable)
            return "Retained - absent";

        if (target.IsHiddenByFilters)
            return "Hidden by filters";

        if (target.IsDead)
            return "Dead";

        return $"{target.HpPercent}% HP";
    }

    private void DrawDependencyLine(string label, bool available)
    {
        var color = available
            ? new Vector4(0.42f, 1.0f, 0.56f, 1.0f)
            : new Vector4(1.0f, 0.58f, 0.58f, 1.0f);
        ImGui.TextColored(color, $"{label}: {(available ? "Loaded" : "Missing")}");
    }

    private void TrackWindowPosition()
    {
        if (!IsOpen)
            return;

        var currentPosition = ImGui.GetWindowPos();
        if (DateTimeOffset.UtcNow < nextWindowPositionSaveUtc)
            return;

        if (lastSavedWindowPosition.HasValue &&
            Vector2.DistanceSquared(lastSavedWindowPosition.Value, currentPosition) < 0.25f)
        {
            return;
        }

        lastSavedWindowPosition = currentPosition;
        nextWindowPositionSaveUtc = DateTimeOffset.UtcNow.AddMilliseconds(500);
        plugin.SaveCurrentWindowPosition(settingsWindow: false, currentPosition);
    }
}

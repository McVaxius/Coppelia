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
            plugin.PrintStatus(plugin.Configuration.BotMode == BotMode.PowerlevelBot
                ? plugin.PowerlevelRuntimeService.StatusText
                : plugin.HealbotRuntimeService.StatusText);
    }

    private void DrawStateControls()
    {
        var configuration = plugin.Configuration;

        var pluginEnabled = configuration.PluginEnabled;
        if (ImGui.Checkbox("Plugin enabled", ref pluginEnabled))
            plugin.SetPluginEnabled(pluginEnabled, printStatus: true);

        ImGui.SameLine();
        var automationEnabled = configuration.AutomationEnabled;
        if (ImGui.Checkbox("Automation", ref automationEnabled))
            plugin.SetAutomationEnabled(automationEnabled, printStatus: true);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("/healbot on and /healbot off control the selected Coppelia mode.");

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
        if (ImGui.SmallButton("Open watch window##CoppeliaMain"))
            plugin.OpenWatchUi();

        DrawModeControls(configuration);

        ImGui.TextWrapped("HealBot watches selected friendly targets. PowerlevelBot tags damaged enemies already targeting FrenRider's Fren or the local player while FrenRider keeps follow/mount behavior.");
        ImGui.TextDisabled("Manage watched targets only in the Watch window. Ctrl-clearing there removes both active watched targets and saved targets.");
        var runtimeStatus = configuration.BotMode == BotMode.PowerlevelBot
            ? plugin.PowerlevelRuntimeService.StatusText
            : plugin.HealbotRuntimeService.StatusText;
        var lastAction = configuration.BotMode == BotMode.PowerlevelBot
            ? plugin.PowerlevelRuntimeService.LastIssuedAction
            : plugin.HealbotRuntimeService.LastIssuedAction;
        var lastRule = configuration.BotMode == BotMode.PowerlevelBot
            ? plugin.PowerlevelRuntimeService.LastMatchedRule
            : plugin.HealbotRuntimeService.LastMatchedRule;
        ImGui.TextColored(new Vector4(0.80f, 0.88f, 1.0f, 1.0f), runtimeStatus);
        ImGui.TextDisabled($"Last action: {lastAction}");
        ImGui.TextDisabled($"Last rule: {lastRule}");
    }

    private void DrawModeControls(Configuration configuration)
    {
        ImGui.Spacing();
        ImGui.TextUnformatted("Mode");

        var healSelected = configuration.BotMode == BotMode.HealBot;
        if (ImGui.RadioButton("HealBot##MainModeHeal", healSelected))
            plugin.SetBotMode(BotMode.HealBot, printStatus: true);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Casts configured healer actions on watched friendly targets.");

        ImGui.SameLine();
        var powerlevelSelected = configuration.BotMode == BotMode.PowerlevelBot;
        if (ImGui.RadioButton("PowerlevelBot##MainModePowerlevel", powerlevelSelected))
            plugin.SetBotMode(BotMode.PowerlevelBot, printStatus: true);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Uses BRD/MCH instant ranged single-target actions on enemies already fighting the Fren/local player.");

        ImGui.SameLine();
        ImGui.BeginDisabled(configuration.BotMode != BotMode.PowerlevelBot);
        var jobs = new[] { PowerlevelJob.None, PowerlevelJob.BRD, PowerlevelJob.MCH };
        var labels = jobs.Select(job => job.GetLabel()).ToArray();
        var selectedIndex = Math.Max(0, Array.IndexOf(jobs, configuration.PowerlevelJob));
        ImGui.SetNextItemWidth(190f);
        if (ImGui.Combo("Powerlevel job##Main", ref selectedIndex, labels, labels.Length))
        {
            configuration.PowerlevelJob = jobs[selectedIndex];
            configuration.Save();
        }
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip("PowerlevelBot never changes gearsets; your current job must match this selection.");
        ImGui.EndDisabled();
    }

    private void DrawDependencyPanel()
    {
        var snapshot = plugin.DependencyService.Current;
        ImGui.TextUnformatted("Required plugins");
        DrawDependencyLine("FrenRider", snapshot.FrenRiderLoaded);
        DrawDependencyLine("vnavmesh", snapshot.VNavmeshLoaded);
        DrawDependencyLine("BMR", snapshot.BossModRebornLoaded);
        DrawDependencyLine("VBM", snapshot.VbmLoaded);

        ImGui.Spacing();
        ImGui.TextUnformatted("Recommended plugins");
        DrawDependencyLine("RSR", snapshot.RotationSolverLoaded, required: false);

        if (!snapshot.IsHealbotReady)
            ImGui.TextColored(new Vector4(1.0f, 0.55f, 0.55f, 1.0f), plugin.DependencyService.BuildMissingDependencyMessage());

        ImGui.TextDisabled("HealBot also requires vnavmesh plus BMR or VBM. PowerlevelBot requires compatible FrenRider IPC and no battle companion chocobo.");
        ImGui.TextDisabled("RSR isolation/restore is used when loaded for HealBot. Actions fire through direct ActionManager execution.");
    }

    private void DrawWatchedTargetsPanel()
    {
        var activeTargets = plugin.WatchTargetService.ActiveTargets.ToArray();
        var retainedTargetCount = plugin.WatchTargetService.RetainedTargets.Count;
        var liveCandidateCount = plugin.WatchTargetService.RuntimeCandidates.Count;
        ImGui.TextUnformatted(configurationModeHeader());

        string configurationModeHeader()
            => plugin.Configuration.BotMode == BotMode.PowerlevelBot
                ? "Powerlevel target source"
                : "Watched targets";

        if (plugin.Configuration.BotMode == BotMode.PowerlevelBot)
        {
            ImGui.TextDisabled("PowerlevelBot ignores the HealBot watched-target list and uses FrenRider's configured Fren as the leader.");
            ImGui.TextDisabled($"Selected job: {plugin.Configuration.PowerlevelJob.GetLabel()}");
            return;
        }

        if (!plugin.HealbotRuntimeService.IsSupportedLocalJob(out var profile, out var reason))
            ImGui.TextDisabled(reason);
        else
            ImGui.TextDisabled($"Local healer: {profile!.JobDisplayName} ({profile.JobAbbreviation})");

        var savedText = plugin.Configuration.SaveHealTargets
            ? plugin.WatchTargetService.SavedTargetCount.ToString()
            : "Off";
        ImGui.TextDisabled($"Active: {activeTargets.Length}/{WatchTargetService.MaxTrackedTargets} | Live: {liveCandidateCount} | Saved: {savedText}");

        if (activeTargets.Length == 0)
        {
            ImGui.TextDisabled("No watched targets selected yet.");
            return;
        }

        foreach (var target in activeTargets.Take(6))
        {
            ImGui.BulletText($"{plugin.FormatDisplayName(target.Name)} [{target.JobLabel}] - {BuildStateLabel(target)}");
        }

        if (activeTargets.Length > 6)
            ImGui.TextDisabled($"...and {activeTargets.Length - 6} more watched targets.");

        if (retainedTargetCount > 0)
            ImGui.TextColored(new Vector4(0.95f, 0.78f, 0.42f, 1.0f), $"{retainedTargetCount} retained/saved target(s) are hidden or absent. Open the watch window to manage them.");
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

    private void DrawDependencyLine(string label, bool available, bool required = true)
    {
        var color = available
            ? new Vector4(0.42f, 1.0f, 0.56f, 1.0f)
            : required
                ? new Vector4(1.0f, 0.58f, 0.58f, 1.0f)
                : new Vector4(0.95f, 0.72f, 0.30f, 1.0f);
        var state = available
            ? "Loaded"
            : required
                ? "Missing"
                : "Missing (optional)";
        ImGui.TextColored(color, $"{label}: {state}");
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

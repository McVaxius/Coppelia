using System.Diagnostics;
using System.Numerics;
using Coppelia.Models;
using Coppelia.Services;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace Coppelia.Windows;

public sealed class ConfigWindow : Window, IDisposable
{
    private static readonly string[] DtrModes = { "Text only", "Icon + text", "Icon only" };
    private static readonly HealbotTriggerKind[] TriggerKinds = Enum.GetValues<HealbotTriggerKind>();
    private static readonly (uint JobId, string Label)[] JobTabs =
    [
        (24u, "White Mage (WHM)"),
        (28u, "Scholar (SCH)"),
        (33u, "Astrologian (AST)"),
        (40u, "Sage (SGE)"),
    ];

    private readonly Plugin plugin;
    private DateTimeOffset nextWindowPositionSaveUtc = DateTimeOffset.MinValue;
    private Vector2? pendingWindowPosition;
    private Vector2? lastSavedWindowPosition;
    private bool pendingSavedPositionApply;

    public ConfigWindow(Plugin plugin)
        : base($"{PluginInfo.DisplayName} Settings###CoppeliaConfig")
    {
        this.plugin = plugin;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(920f, 680f),
            MaximumSize = new Vector2(1600f, 1200f),
        };
        Size = new Vector2(1180f, 820f);
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
        var configuration = plugin.Configuration;
        var changed = false;

        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(6f, 4f));
        try
        {
            DrawHeader();
            ImGui.Separator();
            DrawGeneralSettings(configuration, ref changed);
            ImGui.Separator();
            DrawJobTabsContent(configuration, ref changed);
            ImGui.Separator();
            DrawRequirements();
        }
        finally
        {
            ImGui.PopStyleVar();
        }

        if (changed)
        {
            configuration.Save();
            plugin.DependencyService.Refresh(force: true);
            plugin.WatchTargetService.Update(configuration, force: true);
            plugin.UpdateDtrBar();
        }

        TrackWindowPosition();
        if (pendingSavedPositionApply)
        {
            pendingSavedPositionApply = false;
            Position = null;
            PositionCondition = ImGuiCond.None;
        }
    }

    public void ApplySavedPosition()
    {
        if (plugin.TryGetSavedWindowPosition(true, out var saved))
        {
            pendingWindowPosition = saved.ToVector2();
            return;
        }

        pendingWindowPosition = new Vector2(1f, 1f);
    }

    private void DrawHeader()
    {
        if (ImGui.SmallButton("Ko-fi##CoppeliaConfig"))
            Process.Start(new ProcessStartInfo { FileName = PluginInfo.SupportUrl, UseShellExecute = true });

        ImGui.SameLine();
        if (ImGui.SmallButton("Discord##CoppeliaConfig"))
            Process.Start(new ProcessStartInfo { FileName = PluginInfo.DiscordUrl, UseShellExecute = true });

        ImGui.SameLine();
        if (ImGui.SmallButton("Watch##CoppeliaConfig"))
            plugin.ToggleWatchUi();

        ImGui.TextDisabled(PluginInfo.DiscordFeedbackNote);
    }

    private void DrawGeneralSettings(Configuration configuration, ref bool changed)
    {
        var pluginEnabled = configuration.PluginEnabled;
        if (ImGui.Checkbox("Plugin enabled", ref pluginEnabled))
        {
            plugin.SetPluginEnabled(pluginEnabled, printStatus: false);
            changed = true;
        }

        ImGui.SameLine();
        var automationEnabled = configuration.AutomationEnabled;
        if (ImGui.Checkbox("Automation enabled", ref automationEnabled))
        {
            plugin.SetAutomationEnabled(automationEnabled, printStatus: false);
            changed = true;
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Controls the currently selected Coppelia mode. /healbot on and /healbot off use this same switch.");

        ImGui.SameLine();
        var krangleEnabled = configuration.KrangleNames;
        if (ImGui.Checkbox("Krangle names", ref krangleEnabled))
        {
            configuration.KrangleNames = krangleEnabled;
            if (!krangleEnabled)
                KrangleService.ClearCache();
            changed = true;
        }

        var dependencyToasts = configuration.ShowDependencyToasts;
        if (ImGui.Checkbox("Show dependency toasts", ref dependencyToasts))
        {
            configuration.ShowDependencyToasts = dependencyToasts;
            changed = true;
        }

        ImGui.SameLine();
        var dtrEnabled = configuration.DtrBarEnabled;
        if (ImGui.Checkbox("Show DTR bar entry", ref dtrEnabled))
        {
            configuration.DtrBarEnabled = dtrEnabled;
            plugin.UpdateDtrBar();
            changed = true;
        }

        var dtrMode = configuration.DtrBarMode;
        ImGui.SetNextItemWidth(180f);
        if (ImGui.Combo("DTR mode", ref dtrMode, DtrModes, DtrModes.Length))
        {
            configuration.DtrBarMode = dtrMode;
            plugin.UpdateDtrBar();
            changed = true;
        }

        ImGui.Separator();
        DrawModeSettings(configuration, ref changed);

        ImGui.Separator();
        ImGui.TextUnformatted("Watch filters");

        var watchPlayers = configuration.WatchPlayers;
        if (ImGui.Checkbox("Players", ref watchPlayers))
        {
            configuration.WatchPlayers = watchPlayers;
            changed = true;
        }

        ImGui.SameLine();
        var watchChocobos = configuration.WatchCompanionChocobos;
        if (ImGui.Checkbox("Companion chocobos", ref watchChocobos))
        {
            configuration.WatchCompanionChocobos = watchChocobos;
            changed = true;
        }

        var watchPartyNpcs = configuration.WatchPartyNpcs;
        if (ImGui.Checkbox("NPC party members", ref watchPartyNpcs))
        {
            configuration.WatchPartyNpcs = watchPartyNpcs;
            changed = true;
        }

        ImGui.SameLine();
        var watchBattleNpcs = configuration.WatchFriendlyBattleNpcs;
        if (ImGui.Checkbox("Friendly battle NPCs", ref watchBattleNpcs))
        {
            configuration.WatchFriendlyBattleNpcs = watchBattleNpcs;
            changed = true;
        }

        var saveHealTargets = configuration.SaveHealTargets;
        if (ImGui.Checkbox("Save heal targets", ref saveHealTargets))
        {
            configuration.SaveHealTargets = saveHealTargets;
            changed = true;
        }

        ImGui.SameLine();
        ImGui.SetNextItemWidth(170f);
        var scanRange = configuration.SavedTargetScanRangeYalms;
        if (ImGui.SliderInt("Saved target scan range", ref scanRange, 1, 200, "%d y"))
        {
            configuration.SavedTargetScanRangeYalms = scanRange;
            changed = true;
        }

        ImGui.TextDisabled($"Multi-target watch cap: {WatchTargetService.MaxTrackedTargets} active and {WatchTargetService.MaxTrackedTargets} saved targets.");
        ImGui.TextDisabled("Use the Watch window to add or remove targets. Save heal targets only persists the targets you explicitly keep watched.");
        ImGui.TextDisabled("Unticking a watched target or Ctrl-clearing the watch set removes it from the saved set too. Scan range only affects saved targets rejoining after they return.");
    }

    private void DrawModeSettings(Configuration configuration, ref bool changed)
    {
        ImGui.TextUnformatted("Mode");

        var healSelected = configuration.BotMode == BotMode.HealBot;
        if (ImGui.RadioButton("HealBot##ConfigModeHeal", healSelected))
            plugin.SetBotMode(BotMode.HealBot, printStatus: false);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Uses the watched-target list and per-healer action matrix.");

        ImGui.SameLine();
        var powerlevelSelected = configuration.BotMode == BotMode.PowerlevelBot;
        if (ImGui.RadioButton("PowerlevelBot##ConfigModePowerlevel", powerlevelSelected))
            plugin.SetBotMode(BotMode.PowerlevelBot, printStatus: false);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Uses FrenRider's configured Fren as the leader and attacks only damaged enemies already targeting that Fren or you.");

        ImGui.SameLine();
        ImGui.BeginDisabled(configuration.BotMode != BotMode.PowerlevelBot);
        var jobs = new[] { PowerlevelJob.None, PowerlevelJob.BRD, PowerlevelJob.MCH };
        var labels = jobs.Select(job => job.GetLabel()).ToArray();
        var selectedIndex = Math.Max(0, Array.IndexOf(jobs, configuration.PowerlevelJob));
        ImGui.SetNextItemWidth(210f);
        if (ImGui.Combo("Required job##ConfigPowerlevelJob", ref selectedIndex, labels, labels.Length))
        {
            configuration.PowerlevelJob = jobs[selectedIndex];
            changed = true;
        }
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip("PowerlevelBot requires this job to be unlocked and currently equipped. It never switches gearsets.");
        ImGui.EndDisabled();

        ImGui.TextDisabled("Modes are mutually exclusive. HealBot configuration below remains HealBot-only.");
    }

    private void DrawJobTabsContent(Configuration configuration, ref bool changed)
    {
        ImGui.TextUnformatted("Per-healer action matrix");
        ImGui.TextDisabled("Alive order: Instant BUFF -> Instant oGCD -> Casted BUFF -> Casted GCD. Dead-target prep checks instant buffs before raise.");

        if (!ImGui.BeginTabBar("CoppeliaJobTabs"))
            return;

        foreach (var (jobId, label) in JobTabs)
        {
            if (!ImGui.BeginTabItem(label))
                continue;

            var jobConfig = configuration.GetJobConfigForJob(jobId);
            var jobEnabled = jobConfig.Enabled;
            if (ImGui.Checkbox($"Enable {label}##JobEnabled{jobId}", ref jobEnabled))
            {
                jobConfig.Enabled = jobEnabled;
                changed = true;
            }

            foreach (var group in HealbotActionCatalog.ConfigGroupOrder)
            {
                DrawActionGroup(jobId, jobConfig, group, ref changed);
                ImGui.Spacing();
            }

            ImGui.EndTabItem();
        }

        ImGui.EndTabBar();
    }

    private void DrawActionGroup(uint jobId, HealerJobConfig jobConfig, HealbotActionGroup group, ref bool changed)
    {
        var definitions = HealbotActionCatalog.GetDefinitions(jobId)
            .Where(definition => definition.Group == group)
            .ToArray();

        if (definitions.Length == 0)
            return;

        ImGui.TextUnformatted(group.GetLabel());
        if (!ImGui.BeginTable(
                $"CoppeliaRules{jobId}{group}",
                8,
                ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.ScrollY,
                new Vector2(-1f, 170f)))
        {
            return;
        }

        ImGui.TableSetupColumn("On", ImGuiTableColumnFlags.WidthFixed, 42f);
        ImGui.TableSetupColumn("Priority", ImGuiTableColumnFlags.WidthFixed, 70f);
        ImGui.TableSetupColumn("Action", ImGuiTableColumnFlags.WidthStretch, 0.32f);
        ImGui.TableSetupColumn("Trigger", ImGuiTableColumnFlags.WidthFixed, 120f);
        ImGui.TableSetupColumn("HP%", ImGuiTableColumnFlags.WidthFixed, 70f);
        ImGui.TableSetupColumn("MP%", ImGuiTableColumnFlags.WidthFixed, 70f);
        ImGui.TableSetupColumn("OOC", ImGuiTableColumnFlags.WidthFixed, 54f);
        ImGui.TableSetupColumn("Need Missing Buff", ImGuiTableColumnFlags.WidthFixed, 150f);
        ImGui.TableHeadersRow();

        foreach (var definition in definitions)
        {
            var rule = jobConfig.ActionRules.First(existingRule =>
                string.Equals(existingRule.ActionName, definition.ActionName, StringComparison.OrdinalIgnoreCase));

            ImGui.TableNextRow();

            ImGui.TableSetColumnIndex(0);
            var enabled = rule.Enabled;
            if (ImGui.Checkbox($"##Enabled{jobId}{group}{rule.ActionName}", ref enabled))
            {
                rule.Enabled = enabled;
                changed = true;
            }

            ImGui.TableSetColumnIndex(1);
            ImGui.SetNextItemWidth(62f);
            var priority = rule.Priority;
            if (ImGui.InputInt($"##Priority{jobId}{group}{rule.ActionName}", ref priority))
            {
                rule.Priority = Math.Clamp(priority, 0, 999);
                changed = true;
            }

            ImGui.TableSetColumnIndex(2);
            ImGui.TextUnformatted(definition.ActionName);
            var meta = definition.TargetKind == HealbotTargetKind.Self
                ? "self"
                : "watched target";
            if (!string.IsNullOrWhiteSpace(definition.TrackedStatusName))
                meta = $"{meta} - tracks {definition.TrackedStatusName}";
            ImGui.TextDisabled(meta);

            ImGui.TableSetColumnIndex(3);
            ImGui.SetNextItemWidth(110f);
            var triggerIndex = Array.IndexOf(TriggerKinds, rule.TriggerKind);
            var triggerLabels = TriggerKinds.Select(item => item.GetLabel()).ToArray();
            if (ImGui.Combo($"##Trigger{jobId}{group}{rule.ActionName}", ref triggerIndex, triggerLabels, triggerLabels.Length) &&
                triggerIndex >= 0 &&
                triggerIndex < TriggerKinds.Length)
            {
                rule.TriggerKind = TriggerKinds[triggerIndex];
                changed = true;
            }

            ImGui.TableSetColumnIndex(4);
            ImGui.BeginDisabled(rule.TriggerKind == HealbotTriggerKind.DeadTarget);
            ImGui.SetNextItemWidth(64f);
            var hpThreshold = rule.HpThresholdPercent;
            if (ImGui.SliderInt($"##Hp{jobId}{group}{rule.ActionName}", ref hpThreshold, 0, 100, "%d"))
            {
                rule.HpThresholdPercent = hpThreshold;
                changed = true;
            }
            ImGui.EndDisabled();

            ImGui.TableSetColumnIndex(5);
            ImGui.SetNextItemWidth(64f);
            var mpThreshold = rule.MinimumMpPercent;
            if (ImGui.SliderInt($"##Mp{jobId}{group}{rule.ActionName}", ref mpThreshold, 0, 100, "%d"))
            {
                rule.MinimumMpPercent = mpThreshold;
                changed = true;
            }

            ImGui.TableSetColumnIndex(6);
            var allowOoc = rule.AllowOutOfCombat;
            if (ImGui.Checkbox($"##Ooc{jobId}{group}{rule.ActionName}", ref allowOoc))
            {
                rule.AllowOutOfCombat = allowOoc;
                changed = true;
            }

            ImGui.TableSetColumnIndex(7);
            ImGui.BeginDisabled(string.IsNullOrWhiteSpace(definition.TrackedStatusName));
            var requireMissing = rule.RequireMissingTrackedStatus;
            if (ImGui.Checkbox($"##NeedBuff{jobId}{group}{rule.ActionName}", ref requireMissing))
            {
                rule.RequireMissingTrackedStatus = requireMissing;
                changed = true;
            }
            ImGui.EndDisabled();
        }

        ImGui.EndTable();
    }

    private static void DrawRequirements()
    {
        ImGui.TextUnformatted("Plugin requirements");
        foreach (var requirement in PluginInfo.RequiredPlugins)
            ImGui.BulletText(requirement);

        ImGui.Spacing();
        ImGui.TextUnformatted("Recommended plugins");
        foreach (var recommendation in PluginInfo.RecommendedPlugins)
            ImGui.BulletText(recommendation);

        ImGui.Spacing();
        ImGui.TextDisabled("Coppelia supports /healbot on|off, /healbot heal, /healbot powerlevel, /copellia, /healbot ws, and /healbot j.");
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
        plugin.SaveCurrentWindowPosition(settingsWindow: true, currentPosition);
    }
}

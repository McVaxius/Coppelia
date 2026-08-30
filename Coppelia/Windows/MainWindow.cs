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
    private PowerlevelSetupReadiness? powerlevelReadiness;
    private DateTimeOffset nextPowerlevelReadinessUtc = DateTimeOffset.MinValue;
    private JotSetupReadiness? jotReadiness;
    private DateTimeOffset nextJotReadinessUtc = DateTimeOffset.MinValue;

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
            CoppeliaUi.SectionHeader("Status dashboard");
            DrawStateControls();
            CoppeliaUi.SectionHeader("Readiness");
            DrawDependencyPanel();
            CoppeliaUi.SectionHeader(
                plugin.Configuration.BotMode == BotMode.PowerlevelBot
                    ? "Powerlevel target source"
                    : plugin.Configuration.BotMode == BotMode.Newb
                        ? "Newb pairing status"
                    : plugin.Configuration.BotMode == BotMode.Jot
                        ? "JOAT healing and target status"
                        : "HealBot target status");
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
        ImGui.TextDisabled($"Commands: {PluginInfo.Command}, {PluginInfo.ShortAliasCommand}, {PluginInfo.LegacyAliasCommand}, {PluginInfo.Command} mini, newb, joat, ws, or j");

        if (CoppeliaUi.PrimaryButton("Quick Setup##CoppeliaMain"))
            plugin.OpenQuickSetupUi();
        CoppeliaUi.Tooltip("Run guided HealBot, JOAT, PowerlevelBot, or Newb setup without changing anything until Finish.");

        ImGui.SameLine();
        if (ImGui.SmallButton("Watch##CoppeliaMain"))
            plugin.ToggleWatchUi();

        ImGui.SameLine();
        if (ImGui.SmallButton("Settings##CoppeliaMain"))
            plugin.ToggleConfigUi();

        ImGui.SameLine();
        if (ImGui.SmallButton("Mini##CoppeliaMain"))
            plugin.ToggleMiniUi();

        ImGui.SameLine();
        if (ImGui.SmallButton("Status to chat##CoppeliaMain"))
            plugin.PrintStatus(plugin.GetSelectedModeStatus());

        ImGui.SameLine();
        if (ImGui.SmallButton("Ko-fi##CoppeliaMain"))
            Process.Start(new ProcessStartInfo { FileName = PluginInfo.SupportUrl, UseShellExecute = true });
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
            ImGui.SetTooltip("/healbot on and /healbot off control the selected HealBot mode.");

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

        CoppeliaUi.WrappedHelp(
            "HealBot watches selected friendly targets. JOAT heals first and attacks only when healing is idle. PowerlevelBot remains the BRD/MCH instant-action mode. Newb pairs to one HealBot and performs no local healing or attacking.");
        CoppeliaUi.WrappedHelp(
            "Manage watched targets only in the Watch window. Ctrl-clearing there removes both active watched targets and saved targets.");
        CoppeliaUi.StatusLine("Plugin", configuration.PluginEnabled, "Enabled", "Disabled");
        CoppeliaUi.StatusLine(
            "Automation",
            configuration.AutomationEnabled,
            $"{configuration.BotMode.GetLabel()} enabled",
            $"{configuration.BotMode.GetLabel()} ready but off",
            optional: true);

        if (configuration.BotMode == BotMode.Jot)
        {
            ImGui.PushStyleColor(ImGuiCol.Text, CoppeliaUi.Accent);
            ImGui.TextWrapped($"Healing: {plugin.HealbotRuntimeService.StatusText}");
            ImGui.PopStyleColor();
            ImGui.TextDisabled($"Healing action: {plugin.HealbotRuntimeService.LastIssuedAction}");
            ImGui.TextDisabled($"Healing rule: {plugin.HealbotRuntimeService.LastMatchedRule}");
            ImGui.PushStyleColor(ImGuiCol.Text, CoppeliaUi.Accent);
            ImGui.TextWrapped($"Attacking: {plugin.JotRuntimeService.StatusText}");
            ImGui.PopStyleColor();
            ImGui.TextDisabled($"Attack action: {plugin.JotRuntimeService.LastIssuedAction}");
            ImGui.TextDisabled($"Attack target: {plugin.JotRuntimeService.LastMatchedRule}");
            return;
        }

        var runtimeStatus = configuration.BotMode switch
        {
            BotMode.PowerlevelBot => plugin.PowerlevelRuntimeService.StatusText,
            BotMode.Newb => plugin.HealBotPairingService.RuntimeStatus,
            _ => plugin.HealbotRuntimeService.StatusText,
        };
        var lastAction = configuration.BotMode switch
        {
            BotMode.PowerlevelBot => plugin.PowerlevelRuntimeService.LastIssuedAction,
            BotMode.Newb => plugin.HealBotPairingService.ConnectionStatus,
            _ => plugin.HealbotRuntimeService.LastIssuedAction,
        };
        var lastRule = configuration.BotMode switch
        {
            BotMode.PowerlevelBot => plugin.PowerlevelRuntimeService.LastMatchedRule,
            BotMode.Newb => string.IsNullOrWhiteSpace(plugin.HealBotPairingService.Blocker) ? "Pairing ready" : plugin.HealBotPairingService.Blocker,
            _ => plugin.HealbotRuntimeService.LastMatchedRule,
        };
        ImGui.PushStyleColor(ImGuiCol.Text, CoppeliaUi.Accent);
        ImGui.TextWrapped(runtimeStatus);
        ImGui.PopStyleColor();
        ImGui.TextDisabled($"Last action: {lastAction}");
        ImGui.TextDisabled($"Last rule: {lastRule}");
    }

    private void DrawModeControls(Configuration configuration)
    {
        ImGui.Spacing();
        ImGui.TextUnformatted("Mode");

        var healSelected = configuration.BotMode == BotMode.HealBot;
        if (ImGui.RadioButton("HealBot##MainModeHeal", healSelected))
        {
            plugin.SetBotMode(BotMode.HealBot, printStatus: true);
            nextPowerlevelReadinessUtc = DateTimeOffset.MinValue;
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Casts configured healer actions on watched friendly targets.");

        ImGui.SameLine();
        var jotSelected = configuration.BotMode == BotMode.Jot;
        if (ImGui.RadioButton("Jacqueline of All Trades (JOAT)##MainModeJot", jotSelected))
        {
            plugin.SetBotMode(BotMode.Jot, printStatus: true);
            nextJotReadinessUtc = DateTimeOffset.MinValue;
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Runs watched-target healing first, then casts a healer filler spell only during genuinely idle healing cycles.");

        ImGui.SameLine();
        var powerlevelSelected = configuration.BotMode == BotMode.PowerlevelBot;
        if (ImGui.RadioButton("PowerlevelBot##MainModePowerlevel", powerlevelSelected))
        {
            plugin.SetBotMode(BotMode.PowerlevelBot, printStatus: true);
            nextPowerlevelReadinessUtc = DateTimeOffset.MinValue;
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Uses BRD/MCH instant ranged single-target actions on enemies already fighting the Fren/local player.");

        ImGui.SameLine();
        var newbSelected = configuration.BotMode == BotMode.Newb;
        if (ImGui.RadioButton("Newb##MainModeNewb", newbSelected))
            plugin.SetBotMode(BotMode.Newb, printStatus: true);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Pairs directly to one HealBot, sends exact identity and travel state, and never heals or attacks locally.");

        if (configuration.BotMode == BotMode.PowerlevelBot)
        {
            var jobs = new[] { PowerlevelJob.None, PowerlevelJob.BRD, PowerlevelJob.MCH };
            var labels = jobs.Select(job => job.GetLabel()).ToArray();
            var selectedIndex = Math.Max(0, Array.IndexOf(jobs, configuration.PowerlevelJob));
            ImGui.SetNextItemWidth(190f);
            if (ImGui.Combo("Powerlevel job##Main", ref selectedIndex, labels, labels.Length))
            {
                configuration.PowerlevelJob = jobs[selectedIndex];
                configuration.Save();
                nextPowerlevelReadinessUtc = DateTimeOffset.MinValue;
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("PowerlevelBot never changes gearsets; your current job must match this selection.");
        }
    }

    private void DrawDependencyPanel()
    {
        if (plugin.Configuration.BotMode == BotMode.Newb)
        {
            var configuration = plugin.Configuration;
            CoppeliaUi.StatusLine("LAN pairing", configuration.EnableLanPairing, "Enabled", "Disabled");
            CoppeliaUi.StatusLine(
                "HealBot IPv4 address",
                Configuration.TryParseLanHealBotAddress(configuration.LanHealBotAddress, out _),
                configuration.LanHealBotAddress,
                "Invalid");
            CoppeliaUi.StatusLine(
                "Pair port",
                Configuration.IsValidLanPairingPort(configuration.LanPairingPort),
                configuration.LanPairingPort.ToString(),
                configuration.LanPairingPort == Configuration.ReservedLanDiscoveryPort ? "47789 is reserved" : "Invalid");
            CoppeliaUi.StatusLine("Pair secret", configuration.LanPairingSecret.Length >= 16, "Configured", "At least 16 characters required");
            ImGui.TextDisabled("Newb ignores the local watched-target list and performs no local healing or attacking.");
            ImGui.TextWrapped($"Connection: {plugin.HealBotPairingService.ConnectionStatus}");
            ImGui.TextWrapped($"Pair: {plugin.HealBotPairingService.PairingIdentity}");
            ImGui.TextWrapped($"Remote healing: {plugin.HealBotPairingService.HealingStatus}");
            ImGui.TextWrapped($"Remote chase: {plugin.HealBotPairingService.ChaseStatus}");
            CoppeliaUi.StatusText(
                string.IsNullOrWhiteSpace(plugin.HealBotPairingService.Blocker)
                    ? "Authenticated direct pairing is ready."
                    : plugin.HealBotPairingService.Blocker,
                string.IsNullOrWhiteSpace(plugin.HealBotPairingService.Blocker));
            CoppeliaUi.WrappedHelp("Traffic is authenticated but not encrypted; names and coordinates remain visible on the network.");
            return;
        }

        if (plugin.Configuration.BotMode == BotMode.PowerlevelBot)
        {
            DrawPowerlevelReadiness();
            return;
        }

        var snapshot = plugin.DependencyService.Current;
        DrawDependencyLine("FrenRider", snapshot.FrenRiderLoaded);
        DrawDependencyLine("vnavmesh", snapshot.VNavmeshLoaded);
        DrawDependencyLine("BMR or VBM", snapshot.HasBossModProvider);
        DrawDependencyLine("RSR", snapshot.RotationSolverLoaded, required: false);

        var healerReady = plugin.HealbotRuntimeService.IsSupportedLocalJob(out var profile, out var reason);
        CoppeliaUi.StatusLine(
            "Supported healer",
            healerReady,
            profile == null ? "Ready" : $"{profile.JobDisplayName} equipped",
            reason);

        if (!snapshot.IsHealbotReady)
            CoppeliaUi.StatusText(plugin.DependencyService.BuildMissingDependencyMessage(), ready: false);

        CoppeliaUi.WrappedHelp("RSR isolation and restore is used when loaded. HealBot and JOAT actions still fire through direct ActionManager execution.");

        if (plugin.Configuration.BotMode == BotMode.Jot)
            DrawJotReadiness();
    }

    private void DrawWatchedTargetsPanel()
    {
        var activeTargets = plugin.WatchTargetService.ActiveTargets.ToArray();
        var retainedTargetCount = plugin.WatchTargetService.RetainedTargets.Count;
        var liveCandidateCount = plugin.WatchTargetService.RuntimeCandidates.Count;
        if (plugin.Configuration.BotMode == BotMode.PowerlevelBot)
        {
            ImGui.TextDisabled("PowerlevelBot ignores the HealBot watched-target list and uses FrenRider's configured Fren as the leader.");
            ImGui.TextDisabled($"Selected job: {plugin.Configuration.PowerlevelJob.GetLabel()}");
            CoppeliaUi.WrappedHelp("Only damaged enemies already targeting the configured visible Fren or the local player are eligible.");
            return;
        }

        if (!plugin.HealbotRuntimeService.IsSupportedLocalJob(out var profile, out var reason))
            ImGui.TextDisabled(reason);
        else
            ImGui.TextDisabled($"Local healer: {profile!.JobDisplayName} ({profile.JobAbbreviation})");

        if (plugin.Configuration.BotMode == BotMode.Jot &&
            !plugin.WatchTargetService.HasEphemeralQstTarget)
            CoppeliaUi.WrappedHelp("JOAT does not auto-add FrenRider's Fren. Select that Fren explicitly in Watch when it is the low-level target that healing must protect.");

        if (plugin.WatchTargetService.HasEphemeralQstTarget)
        {
            var remoteState = plugin.WatchTargetService.IsEphemeralQstTargetVisible
                ? "Visible - native HealBot target"
                : "Selected - remote";
            ImGui.TextDisabled($"Active: 1/{WatchTargetService.MaxTrackedTargets} | Live: {(plugin.WatchTargetService.IsEphemeralQstTargetVisible ? 1 : 0)} | Saved: unchanged");
            ImGui.BulletText($"{plugin.FormatDisplayName(plugin.WatchTargetService.EphemeralQstTargetName)} [{plugin.WatchTargetService.EphemeralAssignmentLabel}] - {remoteState}");
            CoppeliaUi.WrappedHelp("The active QST or Newb session exclusively owns this in-memory target. Saved watched targets are unchanged and resume after release.");
            return;
        }

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
        CoppeliaUi.StatusLine(label, available, "Loaded", required ? "Missing" : "Missing (optional)", optional: !required);
    }

    private void DrawPowerlevelReadiness()
    {
        if (DateTimeOffset.UtcNow >= nextPowerlevelReadinessUtc)
        {
            nextPowerlevelReadinessUtc = DateTimeOffset.UtcNow.AddSeconds(2);
            powerlevelReadiness = plugin.PowerlevelRuntimeService.GetSetupReadiness(plugin.Configuration.PowerlevelJob);
        }

        if (powerlevelReadiness == null)
        {
            ImGui.TextDisabled("Powerlevel readiness has not been checked yet.");
            return;
        }

        var readiness = powerlevelReadiness;
        CoppeliaUi.StatusLine("Selected job", readiness.SelectedJobSupported, readiness.SelectedJob.GetLabel(), "Select BRD or MCH");
        CoppeliaUi.StatusLine("Job unlocked", readiness.SelectedJobUnlocked, "Unlocked", "Not unlocked");
        CoppeliaUi.StatusLine("Job equipped", readiness.CurrentJobMatches, "Equipped", $"Current job ID {readiness.CurrentJobId} does not match");
        CoppeliaUi.StatusLine(
            "FrenRider IPC",
            readiness.FrenRiderIpcAvailable && readiness.FrenRiderCompatible,
            "Available and compatible",
            readiness.FrenRiderIpcAvailable ? "Incompatible" : "Unavailable");
        CoppeliaUi.StatusLine("FrenRider", readiness.FrenRiderEnabled, "Enabled", "Disabled");
        CoppeliaUi.StatusLine("Configured Fren", readiness.FrenConfigured, "Configured", "Not configured");
        CoppeliaUi.StatusLine("Fren visibility", readiness.FrenVisible, "Visible", "Not visible");
        CoppeliaUi.StatusLine("Companion chocobo", readiness.CompanionClear, "Dismissed", "Active - dismiss it");
        CoppeliaUi.StatusText(readiness.Reason, readiness.Ready);
    }

    private void DrawJotReadiness()
    {
        if (DateTimeOffset.UtcNow >= nextJotReadinessUtc)
        {
            nextJotReadinessUtc = DateTimeOffset.UtcNow.AddSeconds(2);
            jotReadiness = plugin.JotRuntimeService.GetSetupReadiness();
        }

        if (jotReadiness == null)
        {
            ImGui.TextDisabled("JOAT readiness has not been checked yet.");
            return;
        }

        var readiness = jotReadiness;
        CoppeliaUi.StatusLine("JOAT healer action matrix", readiness.HealerConfigurationEnabled, "Enabled", "Disabled");
        CoppeliaUi.StatusLine("JOAT watched targets", readiness.WatchedTargetsAvailable, "Selected", "None selected");
        CoppeliaUi.StatusLine("Healing configuration", readiness.HealingReady, "Ready", readiness.HealingReason);
        CoppeliaUi.StatusLine(
            "FrenRider attack IPC",
            readiness.FrenRiderIpcAvailable && readiness.FrenRiderCompatible,
            "Available and compatible",
            readiness.FrenRiderIpcAvailable ? "Incompatible" : "Unavailable");
        CoppeliaUi.StatusLine("FrenRider attack source", readiness.FrenRiderEnabled && readiness.FrenConfigured && readiness.FrenVisible, "Enabled, configured, and visible", readiness.AttackingReason);
        CoppeliaUi.StatusText(readiness.AttackingReason, readiness.AttackingReady);
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

using AethertekUI;
using System.Diagnostics;
using System.Numerics;
using System.Reflection;
using Coppelia.Models;
using Coppelia.Services;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Windowing;

namespace Coppelia.Windows;

public sealed class MainWindow : Window, IDisposable
{
    private readonly AethertekUI.Dalamud.MaterialWindowMotion windowMotion = new();
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
            MinimumSize = new Vector2(650f, 560f),
            MaximumSize = new Vector2(1400f, 1100f),
        };
        Size = new Vector2(980f, 920f);
        SizeCondition = ImGuiCond.FirstUseEver;
        TitleBarButtons.Add(new()
        {
            Icon = FontAwesomeIcon.Cog, Priority = 0, IconOffset = new(2, 1),
            Click = button => { if (button == ImGuiMouseButton.Left) plugin.ToggleConfigUi(); },
            ShowTooltip = () => MaterialText.SetTooltip(UiText.T("Settings")),
        });
        TitleBarButtons.Add(new()
        {
            Icon = FontAwesomeIcon.WindowMinimize, Priority = -10, IconOffset = new(2, 1),
            Click = button => { if (button == ImGuiMouseButton.Left) plugin.ToggleMiniUi(); },
            ShowTooltip = () => MaterialText.SetTooltip(UiText.T("Mini")),
        });
        TitleBarButtons.Add(new()
        {
            Icon = FontAwesomeIcon.PowerOff, Priority = -20, IconOffset = new(2, 1),
            Click = button => { if (button == ImGuiMouseButton.Left) plugin.SetOperatingRole(OperatingRole.Off, printStatus: true); },
            ShowTooltip = () => ShowRoleTitleTooltip(OperatingRole.Off),
        });
        TitleBarButtons.Add(new()
        {
            Icon = FontAwesomeIcon.User, Priority = -30, IconOffset = new(2, 1),
            Click = button => { if (button == ImGuiMouseButton.Left) plugin.SetOperatingRole(OperatingRole.StandAlone, printStatus: true); },
            ShowTooltip = () => ShowRoleTitleTooltip(OperatingRole.StandAlone),
        });
        TitleBarButtons.Add(new()
        {
            Icon = FontAwesomeIcon.HandsHelping, Priority = -40, IconOffset = new(2, 1),
            Click = button => { if (button == ImGuiMouseButton.Left) plugin.SetOperatingRole(OperatingRole.Helper, printStatus: true); },
            ShowTooltip = () => ShowRoleTitleTooltip(OperatingRole.Helper),
        });
        TitleBarButtons.Add(new()
        {
            Icon = FontAwesomeIcon.UserPlus, Priority = -50, IconOffset = new(2, 1),
            Click = button => { if (button == ImGuiMouseButton.Left) plugin.SetOperatingRole(OperatingRole.Newb, printStatus: true); },
            ShowTooltip = () => ShowRoleTitleTooltip(OperatingRole.Newb),
        });
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
        UiGui.ReserveTitleSpace(this, PluginInfo.DisplayName + " " + typeof(Plugin).Assembly.GetName().Version, 650);
        windowMotion.Prepare(this, reducedMotion: false, roundedCorners: true);
    }

    public override void PostDraw()
    {
        windowMotion.Restore(this);
        UiGui.PaintTitleWithImage(this, PluginInfo.DisplayName + " " + typeof(Plugin).Assembly.GetName().Version);
    }

    public override void Draw()
    {
        windowMotion.DrawChrome();
        using var typography = UiText.FontScale(1.25f);
        var root = ImGuiP.GetCurrentWindow().ID;
        var scale = MaterialTheme.Metrics.Scale;
        DrawHeader();
        CoppeliaUi.Panel("##HealBotDashboard", root, new Vector2(0, CoppeliaPresentation.DashboardHeight * scale), () =>
        {
            CoppeliaUi.Heading("Status dashboard", MaterialIcon.Chart);
            DrawStateControls(root);
        });
        ImGui.Dummy(new Vector2(0, CoppeliaPresentation.Gap * scale - ImGui.GetStyle().ItemSpacing.Y));
        var horizontal = ImGui.GetContentRegionAvail().X >= 650 * scale;
        if (horizontal && ImGui.BeginTable("##HealBotSummaryLayout", 2, ImGuiTableFlags.SizingStretchSame))
        {
            ImGui.TableNextColumn();
            CoppeliaUi.Panel("##HealBotDependencies", root, new Vector2(0, CoppeliaPresentation.FooterHeight * scale), () =>
            {
                CoppeliaUi.Heading("Dependency readiness", MaterialIcon.Check);
                DrawDependencyPanel();
            });
            ImGui.TableNextColumn();
            CoppeliaUi.Panel("##HealBotTargets", root, new Vector2(0, CoppeliaPresentation.FooterHeight * scale), () =>
            {
                CoppeliaUi.Heading("Watched targets", MaterialIcon.Person);
                DrawWatchedTargetsPanel();
                if (UiGui.Button("Open watch window##CoppeliaMain")) plugin.OpenWatchUi();
            });
            ImGui.EndTable();
        }
        else if (!horizontal)
        {
            CoppeliaUi.Panel("##HealBotDependencies", root, new Vector2(0, CoppeliaPresentation.FooterHeight * scale), () =>
            {
                CoppeliaUi.Heading("Dependency readiness", MaterialIcon.Check); DrawDependencyPanel();
            });
            CoppeliaUi.Panel("##HealBotTargets", root, new Vector2(0, CoppeliaPresentation.FooterHeight * scale), () =>
            {
                CoppeliaUi.Heading("Watched targets", MaterialIcon.Person); DrawWatchedTargetsPanel();
                if (UiGui.Button("Open watch window##CoppeliaMain")) plugin.OpenWatchUi();
            });
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

    private void ShowRoleTitleTooltip(OperatingRole role)
    {
        var selected = plugin.Configuration.OperatingRole == role;
        var tooltip = UiText.T(role.GetLabel());
        if (selected)
            tooltip += "\n" + UiText.T("Selected") + (string.IsNullOrWhiteSpace(plugin.LastAutomationBlocker) ? "" : "\n" + UiText.F(plugin.LastAutomationBlocker));
        MaterialText.SetTooltip(tooltip);
    }

    private void DrawHeader()
    {
        var scale = MaterialTheme.Metrics.Scale;
        using var headerSpacing = new MaterialStyleScope();
        headerSpacing.Style(ImGuiStyleVar.ItemSpacing, new Vector2(CoppeliaPresentation.Compact ? 6 : 5, ImGui.GetStyle().ItemSpacing.Y / scale) * scale);
        var start = ImGui.GetCursorPosY();
        CoppeliaUi.Brand();
        if (plugin.Configuration.UiCompactVisibleOnMainWindow)
        {
            CoppeliaUi.SameLineFor("C", 12);
            plugin.DrawCompactPreference();
        }
        if (plugin.Configuration.UiTransparencyVisibleOnMainWindow)
        {
            CoppeliaUi.SameLineFor("Transparency", 20);
            plugin.DrawTransparencyToggle();
        }
        if (plugin.Configuration.UiLanguageVisibleOnMainWindow)
        {
            CoppeliaUi.SameLineFor("Language", 230);
            plugin.DrawAppearanceSelector(false);
        }
        CoppeliaUi.SameLineFor("Quick Setup");
        if (CoppeliaUi.PrimaryButton("Quick Setup##CoppeliaMain")) plugin.OpenQuickSetupUi();
        CoppeliaUi.Tooltip("Run guided Stand-alone, Helper, or Newb setup without changing anything until Finish.");
        CoppeliaUi.SameLineFor("Watch");
        if (UiGui.Button("Watch##CoppeliaMain")) plugin.ToggleWatchUi();
        CoppeliaUi.SameLineFor("Settings");
        if (UiGui.Button("Settings##CoppeliaMain")) plugin.ToggleConfigUi();
        CoppeliaUi.SameLineFor("Mini");
        if (UiGui.Button("Mini##CoppeliaMain")) plugin.ToggleMiniUi();
        CoppeliaUi.SameLineFor("Status to chat");
        if (UiGui.Button("Status to chat##CoppeliaMain")) plugin.PrintStatus(plugin.GetSelectedModeStatus());
        CoppeliaUi.SameLineFor("Ko-fi");
        if (UiGui.Button("Ko-fi##CoppeliaMain"))
            Process.Start(new ProcessStartInfo { FileName = PluginInfo.SupportUrl, UseShellExecute = true });
        ImGui.SetCursorPosY(Math.Max(ImGui.GetCursorPosY(), start + CoppeliaPresentation.HeaderHeight * scale));
    }
    private void DrawStateControls(uint windowRoot)
    {
        var configuration = plugin.Configuration;
        if (ImGui.BeginTable("##MainRoleBehaviorLayout", ImGui.GetContentRegionAvail().X >= 720 * MaterialTheme.Metrics.Scale ? 2 : 1, ImGuiTableFlags.SizingStretchSame))
        {
            using var roleSpacing = new MaterialStyleScope();
            roleSpacing.Style(ImGuiStyleVar.ItemSpacing, new Vector2(CoppeliaPresentation.Compact ? 6 : 3, ImGui.GetStyle().ItemSpacing.Y / MaterialTheme.Metrics.Scale) * MaterialTheme.Metrics.Scale);
            ImGui.TableNextColumn(); ImGuiP.PushOverrideID(windowRoot);
            using (UiText.FontScale(.96f))
            using (UiText.Font(UiFontRole.BodyStrong))
            {
                var labelX = ImGui.GetCursorPosX();
                ImGui.SetCursorPosX(labelX + 2 * MaterialTheme.Metrics.Scale);
                UiGui.TextUnformatted("Operating role");
                ImGui.SetCursorPosX(labelX);
            }
            DrawRoleRadio("Off##MainRole", OperatingRole.Off);
            CoppeliaUi.SameLineFor("Stand-alone");
            DrawRoleRadio("Stand-alone##MainRole", OperatingRole.StandAlone);
            CoppeliaUi.SameLineFor("Helper");
            DrawRoleRadio("Helper##MainRole", OperatingRole.Helper);
            CoppeliaUi.SameLineFor("Newb");
            DrawRoleRadio("Newb##MainRole", OperatingRole.Newb);
            ImGui.PopID();
            if (configuration.OperatingRole == OperatingRole.StandAlone)
            {
                ImGui.TableNextColumn(); ImGuiP.PushOverrideID(windowRoot);
                DrawModeControls(configuration); ImGui.PopID();
            }
            ImGui.EndTable();
        }
        ImGui.Spacing();
        var krangleEnabled = configuration.KrangleNames;
        if (UiGui.Checkbox("Krangle", ref krangleEnabled, "Krangle names"))
        {
            configuration.KrangleNames = krangleEnabled;
            configuration.Save();
            if (!krangleEnabled) KrangleService.ClearCache();
        }
        CoppeliaUi.SameLineFor("DTR bar");
        var dtrEnabled = configuration.DtrBarEnabled;
        if (UiGui.Checkbox("DTR bar", ref dtrEnabled))
        {
            configuration.DtrBarEnabled = dtrEnabled;
            configuration.Save();
            plugin.UpdateDtrBar();
        }
        var status = plugin.GetOperationalStatus();
        ImGui.Spacing();
        var root = ImGuiP.GetCurrentWindow().ID;
        // Keep this presentation independent of the native role/behavior controls above.
        var columns = ImGui.GetContentRegionAvail().X >= 580 * MaterialTheme.Metrics.Scale ? 3 : 1;
        if (ImGui.BeginTable("##HealBotStatusCards", columns, ImGuiTableFlags.SizingStretchSame))
        {
            var height = (CoppeliaPresentation.Compact ? 78 : 100) * MaterialTheme.Metrics.Scale;
            ImGui.TableNextColumn();
            CoppeliaUi.Panel("##PrimaryStateCard", root, new Vector2(0, height), () =>
            {
                CoppeliaUi.IconSummary(MaterialIcon.Heart, () =>
                {
                    using (UiText.Font(UiFontRole.Caption)) UiGui.TextUnformatted("Primary state");
                    using (UiText.FontScale(CoppeliaPresentation.Compact ? 1.25f : 1.31f))
                    using (UiText.Font(UiFontRole.Heading)) CoppeliaUi.OperationalState(status.PrimaryState, configuration.OperatingRole, plugin.LastAutomationBlocker);
                }, CoppeliaUi.OperationalColor(status.PrimaryState, configuration.OperatingRole, plugin.LastAutomationBlocker));
            }, true);
            ImGui.TableNextColumn();
            CoppeliaUi.Panel("##NextActionCard", root, new Vector2(0, height), () =>
            {
                CoppeliaUi.IconSummary(MaterialIcon.Clock, () =>
                {
                    using (UiText.Font(UiFontRole.Caption)) UiGui.TextUnformatted("Next action");
                    using (UiText.FontScale(CoppeliaPresentation.Compact ? 1.25f : 1.31f))
                    using (UiText.Font(UiFontRole.Heading)) UiGui.TextWrapped(status.NextAction);
                });
            }, true);
            ImGui.TableNextColumn();
            CoppeliaUi.Panel("##IdentityCard", root, new Vector2(0, height), () =>
            {
                CoppeliaUi.IconSummary(MaterialIcon.Person, () =>
                {
                    using (UiText.Font(UiFontRole.Caption)) UiGui.TextUnformatted("Active / paired identity");
                    using var valueScale = UiText.FontScale(CoppeliaPresentation.Compact ? 1.25f : 1.31f);
                    using var valueFont = UiText.Font(UiFontRole.Heading);
                    ImGui.PushTextWrapPos(0);
                    if (status.Identity == "None") UiGui.TextUnformatted("None"); else ImGui.TextUnformatted(status.Identity);
                    ImGui.PopTextWrapPos();
                });
            }, true);
            ImGui.EndTable();
        }
        ImGui.Spacing();
        var lastAction = configuration.BotMode == BotMode.PowerlevelBot
            ? plugin.PowerlevelRuntimeService.LastIssuedAction : plugin.HealbotRuntimeService.LastIssuedAction;
        var lastRule = configuration.BotMode == BotMode.PowerlevelBot
            ? plugin.PowerlevelRuntimeService.LastMatchedRule : plugin.HealbotRuntimeService.LastMatchedRule;
        if (ImGui.BeginTable("##MainActionRuleLayout", 2, ImGuiTableFlags.SizingStretchSame))
        {
            ImGui.TableNextColumn();
            CoppeliaUi.IconSummary(MaterialIcon.Sword, () =>
            {
                using (UiText.Font(UiFontRole.Caption)) UiGui.TextUnformatted("Last action");
                using (UiText.FontScale(CoppeliaPresentation.Compact ? 1.25f : 1.31f))
                using (UiText.Font(UiFontRole.Heading)) MaterialText.TextWrapped(UiText.Action(lastAction));
            });
            ImGui.TableNextColumn();
            CoppeliaUi.IconSummary(MaterialIcon.Star, () =>
            {
                using (UiText.Font(UiFontRole.Caption)) UiGui.TextUnformatted("Last rule");
                using (UiText.FontScale(CoppeliaPresentation.Compact ? 1.25f : 1.31f))
                using (UiText.Font(UiFontRole.Heading)) UiGui.TextWrapped(UiText.Status(lastRule));
            });
            ImGui.EndTable();
        }
        if (UiGui.CollapsingHeader("Details##MainDiagnostics"))
        {
            CoppeliaUi.WrappedHelp("Stand-alone runs HealBot, JOAT, or PowerlevelBot without networking. Helper listens for one authenticated Newb. Newb connects asynchronously and performs no local healing or attacking.");
            CoppeliaUi.WrappedHelp("Manage watched targets only in the Watch window. Ctrl-clearing there removes both active watched targets and saved targets.");
            if (configuration.OperatingRole is OperatingRole.Helper or OperatingRole.Newb)
            {
                var pairing = plugin.HealBotPairingService.Snapshot;
                UiGui.TextDisabled(UiText.F("Endpoint: {0}", pairing.Endpoint));
                UiGui.TextWrapped(UiText.F("Provider: {0}", UiText.T(pairing.ProviderState)));
                UiGui.TextWrapped(UiText.F("Remote healing: {0}", UiText.T(pairing.JoatState)));
                UiGui.TextWrapped(UiText.F("Remote chase: {0}", UiText.T(pairing.TravelState)));
            }
            else if (configuration.BotMode == BotMode.Jot)
            {
                UiGui.TextWrapped(UiText.F("Attacking: {0}", UiText.T(plugin.JotRuntimeService.StatusText)));
                UiGui.TextDisabled(UiText.F("Attack action: {0}", UiText.Action(plugin.JotRuntimeService.LastIssuedAction)));
                UiGui.TextDisabled(UiText.F("Attack target: {0}", UiText.Status(plugin.JotRuntimeService.LastMatchedRule)));
            }
        }
    }
    private void DrawModeControls(Configuration configuration)
    {
        ImGui.Spacing();
        UiGui.TextUnformatted("Stand-alone behavior");

        var healSelected = configuration.BotMode == BotMode.HealBot;
        if (UiGui.RadioTile("HealBot##MainModeHeal", healSelected, height: CoppeliaPresentation.Compact ? 36 : 44))
        {
            plugin.SetStandaloneBehavior(BotMode.HealBot, printStatus: true);
            nextPowerlevelReadinessUtc = DateTimeOffset.MinValue;
        }
        if (ImGui.IsItemHovered())
            UiGui.SetTooltip("Casts configured healer actions on watched friendly targets.");

        ImGui.SameLine();
        var jotSelected = configuration.BotMode == BotMode.Jot;
        if (UiGui.RadioTile("Jacqueline of All Trades (JOAT)##MainModeJot", jotSelected, display: "JOAT", height: CoppeliaPresentation.Compact ? 36 : 44))
        {
            plugin.SetStandaloneBehavior(BotMode.Jot, printStatus: true);
            nextJotReadinessUtc = DateTimeOffset.MinValue;
        }
        if (ImGui.IsItemHovered())
            UiGui.SetTooltip("Runs watched-target healing first, then lets RSR attack only during genuinely idle healing cycles.");

        ImGui.SameLine();
        var powerlevelSelected = configuration.BotMode == BotMode.PowerlevelBot;
        if (UiGui.RadioTile("PowerlevelBot##MainModePowerlevel", powerlevelSelected, height: CoppeliaPresentation.Compact ? 36 : 44))
        {
            plugin.SetStandaloneBehavior(BotMode.PowerlevelBot, printStatus: true);
            nextPowerlevelReadinessUtc = DateTimeOffset.MinValue;
        }
        if (ImGui.IsItemHovered())
            UiGui.SetTooltip("Uses BRD/MCH instant ranged single-target actions on enemies already fighting the Fren/local player.");

        if (configuration.BotMode == BotMode.PowerlevelBot)
        {
            var jobs = new[] { PowerlevelJob.None, PowerlevelJob.BRD, PowerlevelJob.MCH };
            var labels = jobs.Select(job => job.GetLabel()).ToArray();
            var selectedIndex = Math.Max(0, Array.IndexOf(jobs, configuration.PowerlevelJob));
            ImGui.SetNextItemWidth(190f * MaterialTheme.Metrics.Scale);
            if (UiGui.Combo("Powerlevel job##Main", ref selectedIndex, labels, labels.Length))
            {
                configuration.PowerlevelJob = jobs[selectedIndex];
                configuration.Save();
                nextPowerlevelReadinessUtc = DateTimeOffset.MinValue;
            }
            if (ImGui.IsItemHovered())
                UiGui.SetTooltip("PowerlevelBot never changes gearsets; your current job must match this selection.");
        }
    }

    private void DrawRoleRadio(string label, OperatingRole role)
    {
        if (UiGui.RadioTile(label, plugin.Configuration.OperatingRole == role, height: CoppeliaPresentation.Compact ? 36 : 44))
            plugin.SetOperatingRole(role, printStatus: true);
    }

    private void DrawDependencyPanel()
    {
        if (plugin.Configuration.OperatingRole is OperatingRole.Helper or OperatingRole.Newb)
        {
            var pairing = plugin.HealBotPairingService.Snapshot;
            UiGui.TextWrapped(UiText.F($"Provider: {pairing.ProviderState}"));
            UiGui.TextWrapped(UiText.F($"JOAT: {pairing.JoatState}"));
            UiGui.TextWrapped(UiText.F($"Travel: {pairing.TravelState}"));
            if (!string.IsNullOrWhiteSpace(pairing.Blocker))
                CoppeliaUi.StatusText(pairing.Blocker, ready: false);
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
        DrawDependencyLine("RSR", snapshot.RotationSolverLoaded, required: plugin.Configuration.BotMode == BotMode.Jot);

        var healerReady = plugin.HealbotRuntimeService.IsSupportedLocalJob(out var profile, out var reason);
        CoppeliaUi.StatusLine(
            "Supported healer",
            healerReady,
            profile == null ? "Ready" : $"{profile.JobDisplayName} equipped",
            reason);

        if (!snapshot.IsHealbotReady)
            CoppeliaUi.StatusText(plugin.DependencyService.BuildMissingDependencyMessage(), ready: false);

        CoppeliaUi.WrappedHelp(plugin.Configuration.BotMode == BotMode.Jot
            ? "RSR is required and exclusively executes JOAT damage. Coppelia continues to execute Watch healing directly and owns the RSR off/idle-resume handoff."
            : "RSR isolation and exact session restoration are used when RSR is loaded; HealBot healing still executes directly.");

        if (plugin.Configuration.BotMode == BotMode.Jot)
            DrawJotReadiness();
    }

    private void DrawWatchedTargetsPanel()
    {
        var activeTargets = plugin.WatchTargetService.ActiveTargets.ToArray();
        var retainedTargetCount = plugin.WatchTargetService.RetainedTargets.Count;
        var liveCandidateCount = plugin.WatchTargetService.RuntimeCandidates.Count;
        if (plugin.Configuration.OperatingRole == OperatingRole.Newb)
        {
            UiGui.TextDisabled("Newb performs no local healing or attacking and leaves the Stand-alone watch list unchanged.");
            return;
        }

        if (plugin.Configuration.OperatingRole == OperatingRole.StandAlone &&
            plugin.Configuration.BotMode == BotMode.PowerlevelBot)
        {
            UiGui.TextDisabled("PowerlevelBot ignores the HealBot watched-target list and uses FrenRider's configured Fren as the leader.");
            UiGui.TextDisabled(UiText.F($"Selected job: {plugin.Configuration.PowerlevelJob.GetLabel()}"));
            CoppeliaUi.WrappedHelp("Only damaged enemies already targeting the configured visible Fren or the local player are eligible.");
            return;
        }

        if (!plugin.HealbotRuntimeService.IsSupportedLocalJob(out var profile, out var reason))
            UiGui.TextDisabled(reason);
        else
            UiGui.TextDisabled(UiText.F($"Local healer: {profile!.JobDisplayName} ({profile.JobAbbreviation})"));

        if (plugin.Configuration.BotMode == BotMode.Jot &&
            !plugin.WatchTargetService.HasEphemeralQstTarget)
            CoppeliaUi.WrappedHelp("JOAT does not auto-add FrenRider's Fren. Select that Fren explicitly in Watch when it is the low-level target that healing must protect.");

        if (plugin.WatchTargetService.HasEphemeralQstTarget)
        {
            var remoteState = plugin.WatchTargetService.IsEphemeralQstTargetVisible
                ? "Visible - native HealBot target"
                : "Selected - remote";
            UiGui.TextDisabled(UiText.F($"Active: 1/{WatchTargetService.MaxTrackedTargets} | Live: {(plugin.WatchTargetService.IsEphemeralQstTargetVisible ? 1 : 0)} | Saved: unchanged"));
            UiGui.BulletText(UiText.F($"{plugin.FormatDisplayName(plugin.WatchTargetService.EphemeralQstTargetName)} [{plugin.WatchTargetService.EphemeralAssignmentLabel}] - {remoteState}"));
            CoppeliaUi.WrappedHelp("The active QST or Newb session exclusively owns this in-memory target. Saved watched targets are unchanged and resume after release.");
            return;
        }

        var savedText = plugin.Configuration.SaveHealTargets
            ? plugin.WatchTargetService.SavedTargetCount.ToString(UiText.Current.Culture)
            : "Off";
        UiGui.TextDisabled(UiText.F($"Active: {activeTargets.Length}/{WatchTargetService.MaxTrackedTargets} | Live: {liveCandidateCount} | Saved: {savedText}"));

        if (activeTargets.Length == 0)
        {
            UiGui.TextDisabled("No watched targets selected yet.");
            return;
        }

        foreach (var target in activeTargets.Take(6))
        {
            UiGui.BulletText(UiText.F($"{plugin.FormatDisplayName(target.Name)} [{target.JobLabel}] - {BuildStateLabel(target)}"));
        }

        if (activeTargets.Length > 6)
            UiGui.TextDisabled(UiText.F($"...and {activeTargets.Length - 6} more watched targets."));

        if (retainedTargetCount > 0)
            UiGui.TextColored(new Vector4(0.95f, 0.78f, 0.42f, 1.0f), UiText.F($"{retainedTargetCount} retained/saved target(s) are hidden or absent. Open the watch window to manage them."));
    }

    private static string BuildStateLabel(ResolvedWatchTarget target)
    {
        if (target.IsMissingFromObjectTable)
            return "Retained - absent";

        if (target.IsHiddenByFilters)
            return "Hidden by filters";

        if (target.IsDead)
            return "Dead";

        return UiText.F("{0}% HP",target.HpPercent);
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
            UiGui.TextDisabled("Powerlevel readiness has not been checked yet.");
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
            UiGui.TextDisabled("JOAT readiness has not been checked yet.");
            return;
        }

        var readiness = jotReadiness;
        CoppeliaUi.StatusLine("JOAT healer action matrix", readiness.HealerConfigurationEnabled, "Enabled", "Disabled");
        CoppeliaUi.StatusLine("JOAT watched targets", readiness.WatchedTargetsAvailable, "Selected", "None selected");
        CoppeliaUi.StatusLine("Healing configuration", readiness.HealingReady, "Ready", readiness.HealingReason);
        CoppeliaUi.StatusLine("Rotation Solver Reborn", readiness.RotationSolverLoaded, "Loaded", "Missing");
        CoppeliaUi.StatusLine("RSR control", readiness.RotationSolverControlReady, "Ready", "Not acquired");
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

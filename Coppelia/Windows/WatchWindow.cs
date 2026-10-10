using AethertekUI;
using System.Diagnostics;
using System.Numerics;
using Coppelia.Models;
using Coppelia.Services;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace Coppelia.Windows;

public sealed class WatchWindow : Window, IDisposable
{
    private readonly AethertekUI.Dalamud.MaterialWindowMotion windowMotion = new();
    private const float MinWatchTableHeight = 260f;
    private const float MaxRetainedTableShare = 0.35f;
    private const int MaxVisibleRetainedRows = 6;

    private readonly Plugin plugin;
    private DateTimeOffset nextWindowPositionSaveUtc = DateTimeOffset.MinValue;
    private Vector2? pendingWindowPosition;
    private Vector2? lastSavedWindowPosition;
    private bool pendingSavedPositionApply;
    private string nameFilter = string.Empty;
    private JotSetupReadiness? jotReadiness;
    private DateTimeOffset nextJotReadinessUtc = DateTimeOffset.MinValue;

    public WatchWindow(Plugin plugin)
        : base($"{PluginInfo.DisplayName} Watch###CoppeliaWatch")
    {
        this.plugin = plugin;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(420f, 400f),
            MaximumSize = new Vector2(1700f, 1200f),
        };
        Size = new Vector2(510f, 600f);
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
        windowMotion.Prepare(this, reducedMotion: false, roundedCorners: true);
    }

    public override void PostDraw()
        { windowMotion.Restore(this); plugin.PaintWindowTitle(WindowName,UiText.F("{0} Watch",PluginInfo.DisplayName)); }

    public override void Draw()
    {
        windowMotion.DrawChrome();
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, ImGui.GetStyle().ItemSpacing);
        try
        {
            UiGui.Title("HealBot Watch",UiText.F("{0} Watch",PluginInfo.DisplayName));
            using var typography = UiText.FontScale(1.25f);
            var retainedTargets = plugin.WatchTargetService.RetainedTargets.ToArray();

            DrawHeader();
            DrawToolbar();

            if (retainedTargets.Length > 0)
            {
                var retainedTableHeight = CalculateRetainedTableHeight(retainedTargets.Length, ImGui.GetContentRegionAvail().Y);
                if (retainedTableHeight > 0f)
                {
                    DrawRetainedTargets(retainedTargets, retainedTableHeight);
                }
            }

            DrawWatchTable(MathF.Max(MinWatchTableHeight * MaterialTheme.Metrics.Scale, ImGui.GetContentRegionAvail().Y));
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
        if (plugin.Configuration.WatchWindowPosition.HasValue)
        {
            pendingWindowPosition = plugin.Configuration.WatchWindowPosition.ToVector2();
            return;
        }

        pendingWindowPosition = new Vector2(1f, 1f);
    }

    private void DrawHeader()
    {
        CoppeliaUi.Brand("Watch");
        UiGui.TextUnformatted("Operating role");
        DrawRoleRadio("Off##WatchRole", OperatingRole.Off);
        CoppeliaUi.SameLineFor("Stand-alone");
        DrawRoleRadio("Stand-alone##WatchRole", OperatingRole.StandAlone);
        CoppeliaUi.SameLineFor("Helper");
        DrawRoleRadio("Helper##WatchRole", OperatingRole.Helper);
        CoppeliaUi.SameLineFor("Newb");
        DrawRoleRadio("Newb##WatchRole", OperatingRole.Newb);
        var krangleEnabled = plugin.Configuration.KrangleNames;
        if (UiGui.Checkbox("Krangle names##WatchWindow", ref krangleEnabled))
        {
            plugin.Configuration.KrangleNames = krangleEnabled;
            plugin.Configuration.Save();
            if (!krangleEnabled) KrangleService.ClearCache();
        }
        CoppeliaUi.SameLineFor("Quick Setup");
        if (CoppeliaUi.PrimaryButton("Quick Setup##WatchWindow")) plugin.OpenQuickSetupUi();
        CoppeliaUi.SameLineFor("Main");
        if (UiGui.SmallButton("Main##WatchWindow")) plugin.OpenMainUi();
        CoppeliaUi.SameLineFor("Settings");
        if (UiGui.SmallButton("Settings##WatchWindow")) plugin.OpenConfigUi();
        CoppeliaUi.SameLineFor("Mini");
        if (UiGui.SmallButton("Mini##WatchWindow")) plugin.OpenMiniUi();
        CoppeliaUi.SameLineFor("Ko-fi");
        if (UiGui.SmallButton("Ko-fi##WatchWindow"))
            Process.Start(new ProcessStartInfo { FileName = PluginInfo.SupportUrl, UseShellExecute = true });
        if (UiGui.CollapsingHeader("Status and behavior##WatchDetails"))
        {
            if (plugin.Configuration.OperatingRole == OperatingRole.StandAlone)
            {
                if (UiGui.RadioButton("HealBot##WatchBehavior", plugin.Configuration.BotMode == BotMode.HealBot))
                    plugin.SetStandaloneBehavior(BotMode.HealBot, printStatus: true);
                CoppeliaUi.SameLineFor("JOAT");
                if (UiGui.RadioButton("JOAT##WatchBehavior", plugin.Configuration.BotMode == BotMode.Jot))
                    plugin.SetStandaloneBehavior(BotMode.Jot, printStatus: true);
                CoppeliaUi.SameLineFor("PowerlevelBot");
                if (UiGui.RadioButton("PowerlevelBot##WatchBehavior", plugin.Configuration.BotMode == BotMode.PowerlevelBot))
                    plugin.SetStandaloneBehavior(BotMode.PowerlevelBot, printStatus: true);
            }
            DrawWatchDiagnostics();
        }
    }

    private void DrawWatchDiagnostics()
    {
        if (plugin.WatchTargetService.HasEphemeralQstTarget)
        {
            CoppeliaUi.StatusText(plugin.WatchTargetService.IsEphemeralQstTargetVisible ? "Visible - native HealBot target" : "Selected - remote", plugin.WatchTargetService.IsEphemeralQstTargetVisible);
            CoppeliaUi.WrappedHelp("This session-only exact target overrides saved watched targets for automation without changing or saving them.");
        }
        var operational = plugin.GetOperationalStatus();
        UiGui.TextWrapped(UiText.F($"Primary state: {operational.PrimaryState}"));
        UiGui.TextWrapped(UiText.F($"Next action: {operational.NextAction}"));
        UiGui.TextWrapped(UiText.F($"Active / paired identity: {operational.Identity}"));

        if (plugin.Configuration.OperatingRole != OperatingRole.Newb &&
            plugin.Configuration.BotMode == BotMode.Jot)
        {
            RefreshJotReadiness();
            if (jotReadiness != null)
            {
                CoppeliaUi.StatusLine("Healing readiness", jotReadiness.HealingReady, "Ready", jotReadiness.HealingReason);
                CoppeliaUi.StatusLine("Attacking readiness", jotReadiness.AttackingReady, "Ready when healing is idle", jotReadiness.AttackingReason);
            }

            ImGui.PushStyleColor(ImGuiCol.Text, CoppeliaUi.Accent);
            UiGui.TextWrapped(UiText.F($"Healing: {plugin.HealbotRuntimeService.StatusText}"));
            UiGui.TextWrapped(UiText.F($"Attacking: {plugin.JotRuntimeService.StatusText}"));
            ImGui.PopStyleColor();
            UiGui.TextDisabled(UiText.F($"Healing action: {plugin.HealbotRuntimeService.LastIssuedAction} | Attack action: {plugin.JotRuntimeService.LastIssuedAction}"));
            CoppeliaUi.WrappedHelp("Select FrenRider's configured Fren explicitly when it is the low-level target to heal. JOAT never inserts it into this list.");
            return;
        }

        if (plugin.Configuration.OperatingRole == OperatingRole.Newb)
        {
            var pairing = plugin.HealBotPairingService.Snapshot;
            ImGui.PushStyleColor(ImGuiCol.Text, CoppeliaUi.Accent);
            UiGui.TextWrapped(UiText.F($"Pairing: {pairing.PrimaryState} - {pairing.Identity}"));
            UiGui.TextWrapped(UiText.F($"Remote healing: {pairing.JoatState}"));
            UiGui.TextWrapped(UiText.F($"Remote chase: {pairing.TravelState}"));
            ImGui.PopStyleColor();
            CoppeliaUi.WrappedHelp("Newb performs no local healing or attacking. This watched-target list remains unchanged for later HealBot or JOAT use.");
            return;
        }

        var runtimeStatus = plugin.Configuration.BotMode == BotMode.PowerlevelBot
            ? plugin.PowerlevelRuntimeService.StatusText
            : plugin.HealbotRuntimeService.StatusText;
        ImGui.PushStyleColor(ImGuiCol.Text, CoppeliaUi.Accent);
        UiGui.TextWrapped(runtimeStatus);
        ImGui.PopStyleColor();
    }

    private void DrawRoleRadio(string label, OperatingRole role)
    {
        if (UiGui.RadioTile(label, plugin.Configuration.OperatingRole == role, height: 30))
            plugin.SetOperatingRole(role, printStatus: true);
    }

    private void RefreshJotReadiness()
    {
        if (DateTimeOffset.UtcNow < nextJotReadinessUtc)
            return;

        nextJotReadinessUtc = DateTimeOffset.UtcNow.AddSeconds(2);
        jotReadiness = plugin.JotRuntimeService.GetSetupReadiness();
    }

    private void DrawToolbar()
    {
        var configuration = plugin.Configuration;

        ImGui.Spacing();

        if (UiGui.SmallButton("Refresh##WatchWindow"))
            plugin.WatchTargetService.Update(configuration, force: true);

        CoppeliaUi.SameLineFor("Target current");
        if (UiGui.SmallButton("Target current##WatchWindow"))
        {
            plugin.WatchTargetService.TryAddCurrentGameTarget(configuration, out var message);
            plugin.PrintStatus(message);
        }

        CoppeliaUi.SameLineFor("Clear watched");
        var ctrlHeld = ImGui.GetIO().KeyCtrl;
        ImGui.BeginDisabled(!ctrlHeld);
        if (UiGui.SmallButton("Clear watched##WatchWindow"))
        {
            plugin.WatchTargetService.ClearWatchedTargets(configuration);
            plugin.PrintStatus("Cleared watched and saved targets.");
        }
        ImGui.EndDisabled();
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            UiGui.SetTooltip("Hold Ctrl to clear all watched targets and saved targets.");

        CoppeliaUi.SameLineFor("Save heal targets");
        var saveHealTargets = configuration.SaveHealTargets;
        if (UiGui.Checkbox("Save heal targets##WatchWindow", ref saveHealTargets))
        {
            configuration.SaveHealTargets = saveHealTargets;
            configuration.Save();
            plugin.WatchTargetService.Update(configuration, force: true);
        }

        CoppeliaUi.SameLineFor("Scan y", 110);
        ImGui.SetNextItemWidth(110f * MaterialTheme.Metrics.Scale);
        var scanRange = configuration.SavedTargetScanRangeYalms;
        if (UiGui.SliderInt("Scan y##WatchWindow", ref scanRange, 1, 200, "%d"))
        {
            configuration.SavedTargetScanRangeYalms = scanRange;
            configuration.Save();
            plugin.WatchTargetService.Update(configuration, force: true);
        }

        ImGui.Spacing();

        ImGui.SetNextItemWidth(Math.Min(220f * MaterialTheme.Metrics.Scale, ImGui.GetContentRegionAvail().X));
        UiGui.InputTextWithHint("##WatchFilter", "Filter names...", ref nameFilter, 100);
        CoppeliaUi.SameLineFor("Filter by name, type, or job");
        UiGui.TextDisabled("Filter by name, type, or job");

        var watchPlayers = configuration.WatchPlayers;
        if (UiGui.Checkbox("Players##WatchWindow", ref watchPlayers))
        {
            configuration.WatchPlayers = watchPlayers;
            configuration.Save();
            plugin.WatchTargetService.Update(configuration, force: true);
        }

        CoppeliaUi.SameLineFor("Chocobos");
        var watchChocobos = configuration.WatchCompanionChocobos;
        if (UiGui.Checkbox("Chocobos##WatchWindow", ref watchChocobos))
        {
            configuration.WatchCompanionChocobos = watchChocobos;
            configuration.Save();
            plugin.WatchTargetService.Update(configuration, force: true);
        }

        CoppeliaUi.SameLineFor("NPC Party");
        var watchPartyNpcs = configuration.WatchPartyNpcs;
        if (UiGui.Checkbox("NPC Party##WatchWindow", ref watchPartyNpcs))
        {
            configuration.WatchPartyNpcs = watchPartyNpcs;
            configuration.Save();
            plugin.WatchTargetService.Update(configuration, force: true);
        }

        CoppeliaUi.SameLineFor("Battle NPC");
        var watchBattleNpcs = configuration.WatchFriendlyBattleNpcs;
        if (UiGui.Checkbox("Battle NPC##WatchWindow", ref watchBattleNpcs))
        {
            configuration.WatchFriendlyBattleNpcs = watchBattleNpcs;
            configuration.Save();
            plugin.WatchTargetService.Update(configuration, force: true);
        }

        CoppeliaUi.WrappedHelp("Save heal targets only persists targets you explicitly check. Unticking a target removes it from the saved set too.");
        CoppeliaUi.WrappedHelp("Saved target scan range only affects when a saved target can auto-rejoin after it returns; it does not discover new targets.");
    }

    private void DrawRetainedTargets(ResolvedWatchTarget[] retainedTargets, float tableHeight)
    {
        using var tightRows = CoppeliaPresentation.Compact ? MaterialTable.PushTightRows() : default;
        var widths = MeasureWatchColumns(retainedTargets.Select(target => new[]
        {
            string.Empty, plugin.FormatDisplayName(target.Name), UiText.T(target.CategoryLabel), target.JobLabel,
            UiText.T(BuildRetainedStateLabel(target)), float.IsNaN(target.Distance) ? "--" : target.Distance.ToString("F1", UiText.Current.Culture),
        }), retained: true);
        CoppeliaUi.SectionHeader(
            $"Retained or absent targets ({retainedTargets.Length})",
            "Absent rows require Ctrl+untick. Removing one here also removes its saved copy.");

        if (!ImGui.BeginTable(
                "CoppeliaRetainedTargets",
                6,
                ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.ScrollY | ImGuiTableFlags.ScrollX |
                ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.BordersOuter | ImGuiTableFlags.Resizable,
                new Vector2(-1f, tableHeight), widths.Sum() + 12 * ImGui.GetStyle().CellPadding.X))
        {
            return;
        }

        ImGui.TableSetupColumn("Watch", ImGuiTableColumnFlags.WidthFixed, widths[0]);
        ImGui.TableSetupColumn("Name", ImGuiTableColumnFlags.WidthStretch, 0.34f);
        ImGui.TableSetupColumn("Type", ImGuiTableColumnFlags.WidthFixed, widths[2]);
        ImGui.TableSetupColumn("Job", ImGuiTableColumnFlags.WidthFixed, widths[3]);
        ImGui.TableSetupColumn("State", ImGuiTableColumnFlags.WidthFixed, widths[4]);
        ImGui.TableSetupColumn("Dist", ImGuiTableColumnFlags.WidthFixed, widths[5]);
        UiGui.TableHeadersRow();

        foreach (var target in retainedTargets)
        {
            ImGui.TableNextRow();

            ImGui.TableSetColumnIndex(0);
            var trackedState = target.IsActive || target.IsSaved;
            if (UiGui.Checkbox($"##RetainedWatch{target.Entry.Name}{target.Entry.GameObjectId}", ref trackedState))
            {
                if (trackedState && target.LiveSnapshot != null)
                {
                    plugin.WatchTargetService.TryAddWatchedTarget(plugin.Configuration, target.LiveSnapshot, out var addMessage);
                    plugin.PrintStatus(addMessage);
                }
                else if (!trackedState)
                {
                    RemoveRetainedTarget(target);
                }
            }

            ImGui.TableSetColumnIndex(1);
            ImGui.TextUnformatted(plugin.FormatDisplayName(target.Name));

            ImGui.TableSetColumnIndex(2);
            UiGui.TextUnformatted(target.CategoryLabel);

            ImGui.TableSetColumnIndex(3);
            ImGui.TextUnformatted(target.JobLabel);

            ImGui.TableSetColumnIndex(4);
            UiGui.TextUnformatted(BuildRetainedStateLabel(target));

            ImGui.TableSetColumnIndex(5);
            UiGui.TextUnformatted(float.IsNaN(target.Distance) ? "--" : target.Distance.ToString("F1",UiText.Current.Culture));
        }

        ImGui.EndTable();
    }

    private void DrawWatchTable(float height)
    {
        using var tightRows = CoppeliaPresentation.Compact ? MaterialTable.PushTightRows() : default;
        var targets = FilteredTargets().ToArray();
        var widths = MeasureWatchColumns(targets.Select(target => new[]
        {
            string.Empty, plugin.FormatDisplayName(target.Name), UiText.T(target.CategoryLabel), target.JobLabel,
            target.IsDead ? UiText.T("Dead") : UiText.F("{0}%", target.HpPercent), target.Distance.ToString("F1", UiText.Current.Culture),
        }), retained: false);
        var savedText = plugin.Configuration.SaveHealTargets
            ? plugin.WatchTargetService.SavedTargetCount.ToString(UiText.Current.Culture)
            : "Off";
        CoppeliaUi.SectionHeader(
            $"Live eligible targets ({targets.Length})",
            $"Watching {plugin.WatchTargetService.ActiveTargets.Count}/{WatchTargetService.MaxTrackedTargets} active targets. Saved targets: {savedText}.");

        if (targets.Length == 0)
        {
            CoppeliaUi.WrappedHelp("No eligible targets match the current filters.");
            return;
        }

        if (!ImGui.BeginTable(
                "CoppeliaWatchWindowTable",
                6,
                ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.ScrollY | ImGuiTableFlags.ScrollX |
                ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.BordersOuter | ImGuiTableFlags.Resizable,
                new Vector2(-1f, height), widths.Sum() + 12 * ImGui.GetStyle().CellPadding.X))
        {
            return;
        }

        ImGui.TableSetupColumn("Watch", ImGuiTableColumnFlags.WidthFixed, widths[0]);
        ImGui.TableSetupColumn("Name", ImGuiTableColumnFlags.WidthStretch, 0.34f);
        ImGui.TableSetupColumn("Type", ImGuiTableColumnFlags.WidthFixed, widths[2]);
        ImGui.TableSetupColumn("Job", ImGuiTableColumnFlags.WidthFixed, widths[3]);
        ImGui.TableSetupColumn("HP", ImGuiTableColumnFlags.WidthFixed, widths[4]);
        ImGui.TableSetupColumn("Dist", ImGuiTableColumnFlags.WidthFixed, widths[5]);
        UiGui.TableHeadersRow();

        foreach (var target in targets)
        {
            var isWatched = plugin.WatchTargetService.IsWatched(target);

            ImGui.TableNextRow();

            ImGui.TableSetColumnIndex(0);
            var watchedState = isWatched;
            if (UiGui.Checkbox($"##Watch{target.GameObjectId}", ref watchedState))
                ToggleLiveTarget(target, watchedState);

            ImGui.TableSetColumnIndex(1);
            ImGui.TextUnformatted(plugin.FormatDisplayName(target.Name));

            ImGui.TableSetColumnIndex(2);
            UiGui.TextUnformatted(target.CategoryLabel);

            ImGui.TableSetColumnIndex(3);
            ImGui.TextUnformatted(target.JobLabel);

            ImGui.TableSetColumnIndex(4);
            var hpText = target.IsDead ? "Dead" : UiText.F("{0}%",target.HpPercent);
            UiGui.TextUnformatted(hpText);

            ImGui.TableSetColumnIndex(5);
            UiGui.TextUnformatted(target.Distance.ToString("F1", UiText.Current.Culture));
        }

        ImGui.EndTable();
    }

    private static float[] MeasureWatchColumns(IEnumerable<string[]> rows, bool retained)
    {
        var scale = MaterialTheme.Metrics.Scale;
        var headings = new[] { "Watch", "Name", "Type", "Job", retained ? "State" : "HP", "Dist" };
        var widths = new[] { 32f, 120f, 80f, 40f, retained ? 145f : 50f, 52f };
        for (var column = 0; column < widths.Length; column++)
            widths[column] = MathF.Ceiling(MathF.Max(widths[column] * scale, MaterialText.Measure(UiText.T(headings[column])).X + 2 * scale));
        widths[0] = MathF.Max(widths[0], MathF.Ceiling(ImGui.GetFrameHeight()));
        foreach (var row in rows)
            for (var column = 1; column < widths.Length; column++)
                widths[column] = MathF.Max(widths[column], MathF.Ceiling(MaterialText.Measure(row[column]).X + 2 * scale));
        return widths;
    }

    private static float CalculateRetainedTableHeight(int retainedCount, float availableHeight)
    {
        var maxHeight = MathF.Min(availableHeight * MaxRetainedTableShare, availableHeight - MinWatchTableHeight * MaterialTheme.Metrics.Scale);
        var visibleRows = Math.Clamp(retainedCount, 1, MaxVisibleRetainedRows);
        var rowHeight = ImGui.GetFrameHeightWithSpacing();
        var desiredHeight = ((visibleRows + 1) * rowHeight) + 6f * MaterialTheme.Metrics.Scale;
        // Keep retained targets reachable when the header exceeds a small viewport.
        // The outer window can scroll; its height must not remove this section.
        var minimumHeight = 3 * rowHeight + 6f * MaterialTheme.Metrics.Scale;
        return MathF.Min(desiredHeight, MathF.Max(minimumHeight, maxHeight));
    }

    private IEnumerable<WatchTargetSnapshot> FilteredTargets()
    {
        var filter = nameFilter.Trim();
        foreach (var target in plugin.WatchTargetService.Targets)
        {
            if (string.IsNullOrEmpty(filter))
            {
                yield return target;
                continue;
            }

            if (target.Name.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                plugin.FormatDisplayName(target.Name).Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                target.CategoryLabel.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                target.JobLabel.Contains(filter, StringComparison.OrdinalIgnoreCase))
            {
                yield return target;
            }
        }
    }

    private void ToggleLiveTarget(WatchTargetSnapshot target, bool shouldWatch)
    {
        if (shouldWatch)
        {
            plugin.WatchTargetService.TryAddWatchedTarget(plugin.Configuration, target, out var addMessage);
            plugin.PrintStatus(addMessage);
            return;
        }

        plugin.WatchTargetService.TryRemoveWatchedTarget(plugin.Configuration, target, out var removeMessage);
        plugin.PrintStatus(removeMessage);
    }

    private void RemoveRetainedTarget(ResolvedWatchTarget target)
    {
        var ctrlHeld = ImGui.GetIO().KeyCtrl;
        if (target.IsMissingFromObjectTable && !ctrlHeld)
        {
            plugin.PrintStatus($"Hold Ctrl while unticking absent retained target {plugin.FormatDisplayName(target.Name)}.");
            return;
        }

        if (target.IsActive)
        {
            plugin.WatchTargetService.TryRemoveRetainedWatchedTarget(plugin.Configuration, target, ctrlHeld, out var removeMessage);
            plugin.PrintStatus(removeMessage);
            return;
        }

        if (!target.IsSaved)
            return;

        plugin.WatchTargetService.TryForgetSavedTarget(plugin.Configuration, target, out var forgetMessage);
        plugin.PrintStatus(forgetMessage);
    }

    private static string BuildRetainedStateLabel(ResolvedWatchTarget target)
    {
        var prefix = UiText.T(target.IsActive ? "Watched" : target.IsSaved ? "Saved" : "Tracked");
        if (target.IsMissingFromObjectTable)
            return UiText.F("{0} / absent / Ctrl remove", prefix);

        if (target.IsHiddenByFilters)
            return UiText.F("{0} / hidden by filters", prefix);

        if (!target.IsVisibleInObjectTable)
            return UiText.F("{0} / retained", prefix);

        return target.IsDead ? UiText.F("{0} / dead", prefix) : UiText.F("{0} / {1}% HP", prefix, target.HpPercent);
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
        plugin.Configuration.WatchWindowPosition.Set(currentPosition);
        plugin.Configuration.Save();
    }
}

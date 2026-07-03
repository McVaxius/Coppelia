using System.Diagnostics;
using System.Numerics;
using Coppelia.Models;
using Coppelia.Services;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace Coppelia.Windows;

public sealed class WatchWindow : Window, IDisposable
{
    private const float MinWatchTableHeight = 260f;
    private const float MaxRetainedTableShare = 0.35f;
    private const int MaxVisibleRetainedRows = 6;

    private readonly Plugin plugin;
    private DateTimeOffset nextWindowPositionSaveUtc = DateTimeOffset.MinValue;
    private Vector2? pendingWindowPosition;
    private Vector2? lastSavedWindowPosition;
    private bool pendingSavedPositionApply;
    private string nameFilter = string.Empty;

    public WatchWindow(Plugin plugin)
        : base($"{PluginInfo.DisplayName} Watch###CoppeliaWatch")
    {
        this.plugin = plugin;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(920f, 660f),
            MaximumSize = new Vector2(1700f, 1200f),
        };
        Size = new Vector2(1200f, 860f);
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
            var retainedTargets = plugin.WatchTargetService.RetainedTargets.ToArray();

            DrawHeader();
            ImGui.Separator();
            DrawToolbar();

            if (retainedTargets.Length > 0)
            {
                var retainedTableHeight = CalculateRetainedTableHeight(retainedTargets.Length, ImGui.GetContentRegionAvail().Y);
                if (retainedTableHeight > 0f)
                {
                    ImGui.Separator();
                    DrawRetainedTargets(retainedTargets, retainedTableHeight);
                }
            }

            ImGui.Separator();
            DrawWatchTable(MathF.Max(MinWatchTableHeight, ImGui.GetContentRegionAvail().Y));
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
        var automationEnabled = plugin.Configuration.AutomationEnabled;
        if (ImGui.Checkbox("Automation##WatchWindow", ref automationEnabled))
            plugin.SetAutomationEnabled(automationEnabled, printStatus: true);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Controls the currently selected Coppelia mode.");

        ImGui.SameLine();
        var healSelected = plugin.Configuration.BotMode == BotMode.HealBot;
        if (ImGui.RadioButton("HealBot##WatchModeHeal", healSelected))
            plugin.SetBotMode(BotMode.HealBot, printStatus: true);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Watched targets in this window are HealBot-only.");

        ImGui.SameLine();
        var powerlevelSelected = plugin.Configuration.BotMode == BotMode.PowerlevelBot;
        if (ImGui.RadioButton("PowerlevelBot##WatchModePowerlevel", powerlevelSelected))
            plugin.SetBotMode(BotMode.PowerlevelBot, printStatus: true);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("PowerlevelBot ignores this watched-target list and follows FrenRider's configured Fren.");

        ImGui.SameLine();
        var krangleEnabled = plugin.Configuration.KrangleNames;
        if (ImGui.Checkbox("Krangle names##WatchWindow", ref krangleEnabled))
        {
            plugin.Configuration.KrangleNames = krangleEnabled;
            plugin.Configuration.Save();
            if (!krangleEnabled)
                KrangleService.ClearCache();
        }

        ImGui.SameLine();
        if (ImGui.SmallButton("Main##WatchWindow"))
            plugin.OpenMainUi();

        ImGui.SameLine();
        if (ImGui.SmallButton("Settings##WatchWindow"))
            plugin.OpenConfigUi();

        ImGui.SameLine();
        if (ImGui.SmallButton("Ko-fi##WatchWindow"))
            Process.Start(new ProcessStartInfo { FileName = PluginInfo.SupportUrl, UseShellExecute = true });
    }

    private void DrawToolbar()
    {
        var configuration = plugin.Configuration;

        if (ImGui.SmallButton("Refresh##WatchWindow"))
            plugin.WatchTargetService.Update(configuration, force: true);

        ImGui.SameLine();
        if (ImGui.SmallButton("Target current##WatchWindow"))
        {
            plugin.WatchTargetService.TryAddCurrentGameTarget(configuration, out var message);
            plugin.PrintStatus(message);
        }

        ImGui.SameLine();
        var ctrlHeld = ImGui.GetIO().KeyCtrl;
        ImGui.BeginDisabled(!ctrlHeld);
        if (ImGui.SmallButton("Clear watched##WatchWindow"))
        {
            plugin.WatchTargetService.ClearWatchedTargets(configuration);
            plugin.PrintStatus("Cleared watched and saved targets.");
        }
        ImGui.EndDisabled();
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip("Hold Ctrl to clear all watched targets and saved targets.");

        ImGui.SameLine();
        var saveHealTargets = configuration.SaveHealTargets;
        if (ImGui.Checkbox("Save heal targets##WatchWindow", ref saveHealTargets))
        {
            configuration.SaveHealTargets = saveHealTargets;
            configuration.Save();
            plugin.WatchTargetService.Update(configuration, force: true);
        }

        ImGui.SameLine();
        ImGui.SetNextItemWidth(110f);
        var scanRange = configuration.SavedTargetScanRangeYalms;
        if (ImGui.SliderInt("Scan y##WatchWindow", ref scanRange, 1, 200, "%d"))
        {
            configuration.SavedTargetScanRangeYalms = scanRange;
            configuration.Save();
            plugin.WatchTargetService.Update(configuration, force: true);
        }

        ImGui.SetNextItemWidth(220f);
        ImGui.InputTextWithHint("##WatchFilter", "Filter names...", ref nameFilter, 100);

        var watchPlayers = configuration.WatchPlayers;
        if (ImGui.Checkbox("Players##WatchWindow", ref watchPlayers))
        {
            configuration.WatchPlayers = watchPlayers;
            configuration.Save();
            plugin.WatchTargetService.Update(configuration, force: true);
        }

        ImGui.SameLine();
        var watchChocobos = configuration.WatchCompanionChocobos;
        if (ImGui.Checkbox("Chocobos##WatchWindow", ref watchChocobos))
        {
            configuration.WatchCompanionChocobos = watchChocobos;
            configuration.Save();
            plugin.WatchTargetService.Update(configuration, force: true);
        }

        ImGui.SameLine();
        var watchPartyNpcs = configuration.WatchPartyNpcs;
        if (ImGui.Checkbox("NPC Party##WatchWindow", ref watchPartyNpcs))
        {
            configuration.WatchPartyNpcs = watchPartyNpcs;
            configuration.Save();
            plugin.WatchTargetService.Update(configuration, force: true);
        }

        ImGui.SameLine();
        var watchBattleNpcs = configuration.WatchFriendlyBattleNpcs;
        if (ImGui.Checkbox("Battle NPC##WatchWindow", ref watchBattleNpcs))
        {
            configuration.WatchFriendlyBattleNpcs = watchBattleNpcs;
            configuration.Save();
            plugin.WatchTargetService.Update(configuration, force: true);
        }

        ImGui.TextDisabled("Save heal targets only persists targets you explicitly check. Unticking a target removes it from the saved set too.");
        ImGui.TextDisabled("Saved target scan range only affects when a saved target can auto-rejoin after it returns; it does not discover new targets.");
    }

    private void DrawRetainedTargets(ResolvedWatchTarget[] retainedTargets, float tableHeight)
    {
        ImGui.TextUnformatted($"Hidden / absent tracked targets ({retainedTargets.Length})");
        ImGui.TextDisabled("Absent retained rows require Ctrl+untick. Unticking here removes the retained target from Coppelia's tracked set, including any saved copy.");

        if (!ImGui.BeginTable(
                "CoppeliaRetainedTargets",
                6,
                ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.ScrollY | ImGuiTableFlags.BordersInnerV,
                new Vector2(-1f, tableHeight)))
        {
            return;
        }

        ImGui.TableSetupColumn("Watch", ImGuiTableColumnFlags.WidthFixed, 54f);
        ImGui.TableSetupColumn("Name", ImGuiTableColumnFlags.WidthStretch, 0.34f);
        ImGui.TableSetupColumn("Type", ImGuiTableColumnFlags.WidthFixed, 130f);
        ImGui.TableSetupColumn("Job", ImGuiTableColumnFlags.WidthFixed, 70f);
        ImGui.TableSetupColumn("State", ImGuiTableColumnFlags.WidthFixed, 210f);
        ImGui.TableSetupColumn("Dist", ImGuiTableColumnFlags.WidthFixed, 64f);
        ImGui.TableHeadersRow();

        foreach (var target in retainedTargets)
        {
            ImGui.TableNextRow();

            ImGui.TableSetColumnIndex(0);
            var trackedState = target.IsActive || target.IsSaved;
            if (ImGui.Checkbox($"##RetainedWatch{target.Entry.Name}{target.Entry.GameObjectId}", ref trackedState))
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
            ImGui.TextUnformatted(target.CategoryLabel);

            ImGui.TableSetColumnIndex(3);
            ImGui.TextUnformatted(target.JobLabel);

            ImGui.TableSetColumnIndex(4);
            ImGui.TextUnformatted(BuildRetainedStateLabel(target));

            ImGui.TableSetColumnIndex(5);
            ImGui.TextUnformatted(float.IsNaN(target.Distance) ? "--" : $"{target.Distance:F1}");
        }

        ImGui.EndTable();
    }

    private void DrawWatchTable(float height)
    {
        var targets = FilteredTargets().ToArray();
        ImGui.TextUnformatted($"Visible object table ({targets.Length})");
        var savedText = plugin.Configuration.SaveHealTargets
            ? plugin.WatchTargetService.SavedTargetCount.ToString()
            : "Off";
        ImGui.TextDisabled($"Watching {plugin.WatchTargetService.ActiveTargets.Count}/{WatchTargetService.MaxTrackedTargets} active targets. Saved targets: {savedText}.");

        if (targets.Length == 0)
        {
            ImGui.TextDisabled("No eligible targets for the current filters.");
            return;
        }

        if (!ImGui.BeginTable(
                "CoppeliaWatchWindowTable",
                6,
                ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.ScrollY | ImGuiTableFlags.BordersInnerV,
                new Vector2(-1f, height)))
        {
            return;
        }

        ImGui.TableSetupColumn("Watch", ImGuiTableColumnFlags.WidthFixed, 54f);
        ImGui.TableSetupColumn("Name", ImGuiTableColumnFlags.WidthStretch, 0.34f);
        ImGui.TableSetupColumn("Type", ImGuiTableColumnFlags.WidthFixed, 130f);
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
            if (ImGui.Checkbox($"##Watch{target.GameObjectId}", ref watchedState))
                ToggleLiveTarget(target, watchedState);

            ImGui.TableSetColumnIndex(1);
            ImGui.TextUnformatted(plugin.FormatDisplayName(target.Name));

            ImGui.TableSetColumnIndex(2);
            ImGui.TextUnformatted(target.CategoryLabel);

            ImGui.TableSetColumnIndex(3);
            ImGui.TextUnformatted(target.JobLabel);

            ImGui.TableSetColumnIndex(4);
            var hpText = target.IsDead ? "Dead" : $"{target.HpPercent}%";
            ImGui.TextUnformatted(hpText);

            ImGui.TableSetColumnIndex(5);
            ImGui.TextUnformatted($"{target.Distance:F1}");
        }

        ImGui.EndTable();
    }

    private static float CalculateRetainedTableHeight(int retainedCount, float availableHeight)
    {
        var maxHeight = MathF.Min(availableHeight * MaxRetainedTableShare, availableHeight - MinWatchTableHeight);
        if (maxHeight <= 0f)
            return 0f;

        var visibleRows = Math.Clamp(retainedCount, 1, MaxVisibleRetainedRows);
        var rowHeight = ImGui.GetFrameHeightWithSpacing();
        var desiredHeight = ((visibleRows + 1) * rowHeight) + 6f;
        return MathF.Min(desiredHeight, maxHeight);
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
        var prefix = target.IsActive ? "Watched" : target.IsSaved ? "Saved" : "Tracked";
        if (target.IsMissingFromObjectTable)
            return $"{prefix} / absent / Ctrl remove";

        if (target.IsHiddenByFilters)
            return $"{prefix} / hidden by filters";

        if (!target.IsVisibleInObjectTable)
            return $"{prefix} / retained";

        return target.IsDead ? $"{prefix} / dead" : $"{prefix} / {target.HpPercent}% HP";
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

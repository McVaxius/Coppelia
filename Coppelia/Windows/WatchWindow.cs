using System.Diagnostics;
using System.Numerics;
using Coppelia.Models;
using Coppelia.Services;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace Coppelia.Windows;

public sealed class WatchWindow : Window, IDisposable
{
    private readonly Plugin plugin;
    private DateTimeOffset nextWindowPositionSaveUtc = DateTimeOffset.MinValue;
    private Vector2? pendingWindowPosition;
    private Vector2? lastSavedWindowPosition;
    private bool pendingSavedPositionApply;
    private string nameFilter = string.Empty;

    public WatchWindow(Plugin plugin)
        : base($"{PluginInfo.DisplayName} Watch List###CoppeliaWatch")
    {
        this.plugin = plugin;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(760f, 560f),
            MaximumSize = new Vector2(1500f, 1100f),
        };
        Size = new Vector2(980f, 760f);
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
            DrawToolbar();
            ImGui.Separator();
            DrawRetainedTargets(180f);
            ImGui.Separator();
            DrawWatchTable(360f);
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
        var healbotEnabled = plugin.Configuration.HealbotEnabled;
        if (ImGui.Checkbox("Healbot mode##WatchWindow", ref healbotEnabled))
            plugin.SetHealbotEnabled(healbotEnabled, printStatus: true);

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
        if (ImGui.SmallButton("Clear watched##WatchWindow"))
        {
            plugin.WatchTargetService.ClearWatchedTargets(configuration);
            plugin.PrintStatus("Cleared the active watched set.");
        }

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
    }

    private void DrawRetainedTargets(float height)
    {
        var retainedTargets = plugin.WatchTargetService.RetainedTargets;
        ImGui.TextUnformatted($"Retained / saved targets ({retainedTargets.Count})");
        ImGui.TextDisabled("Absent active targets require Ctrl+untick to remove from the watched set.");

        if (retainedTargets.Count == 0)
        {
            ImGui.TextDisabled("No hidden or retained targets right now.");
            return;
        }

        if (!ImGui.BeginTable(
                "CoppeliaRetainedTargets",
                8,
                ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.ScrollY | ImGuiTableFlags.BordersInnerV,
                new Vector2(-1f, height)))
        {
            return;
        }

        ImGui.TableSetupColumn("Watch", ImGuiTableColumnFlags.WidthFixed, 54f);
        ImGui.TableSetupColumn("Saved", ImGuiTableColumnFlags.WidthFixed, 48f);
        ImGui.TableSetupColumn("Name", ImGuiTableColumnFlags.WidthStretch, 0.28f);
        ImGui.TableSetupColumn("Type", ImGuiTableColumnFlags.WidthFixed, 130f);
        ImGui.TableSetupColumn("Job", ImGuiTableColumnFlags.WidthFixed, 70f);
        ImGui.TableSetupColumn("State", ImGuiTableColumnFlags.WidthFixed, 150f);
        ImGui.TableSetupColumn("Dist", ImGuiTableColumnFlags.WidthFixed, 64f);
        ImGui.TableSetupColumn("Action", ImGuiTableColumnFlags.WidthFixed, 100f);
        ImGui.TableHeadersRow();

        foreach (var target in retainedTargets)
        {
            ImGui.TableNextRow();

            ImGui.TableSetColumnIndex(0);
            var watchedState = target.IsActive;
            var canActivate = target.LiveSnapshot != null;
            if (!canActivate && !target.IsActive)
                ImGui.BeginDisabled();
            if (ImGui.Checkbox($"##RetainedWatch{target.Entry.Name}{target.Entry.GameObjectId}", ref watchedState))
            {
                if (watchedState && target.LiveSnapshot != null)
                {
                    plugin.WatchTargetService.TryAddWatchedTarget(plugin.Configuration, target.LiveSnapshot, out var addMessage);
                    plugin.PrintStatus(addMessage);
                }
                else if (!watchedState)
                {
                    plugin.WatchTargetService.TryRemoveRetainedWatchedTarget(plugin.Configuration, target, ImGui.GetIO().KeyCtrl, out var removeMessage);
                    plugin.PrintStatus(removeMessage);
                }
            }
            if (!canActivate && !target.IsActive)
                ImGui.EndDisabled();

            ImGui.TableSetColumnIndex(1);
            ImGui.TextUnformatted(target.IsSaved ? "Yes" : string.Empty);

            ImGui.TableSetColumnIndex(2);
            ImGui.TextUnformatted(plugin.FormatDisplayName(target.Name));

            ImGui.TableSetColumnIndex(3);
            ImGui.TextUnformatted(target.CategoryLabel);

            ImGui.TableSetColumnIndex(4);
            ImGui.TextUnformatted(target.JobLabel);

            ImGui.TableSetColumnIndex(5);
            ImGui.TextUnformatted(BuildRetainedStateLabel(target));

            ImGui.TableSetColumnIndex(6);
            ImGui.TextUnformatted(float.IsNaN(target.Distance) ? "--" : $"{target.Distance:F1}");

            ImGui.TableSetColumnIndex(7);
            ImGui.BeginDisabled(!target.IsSaved);
            if (ImGui.SmallButton($"Forget##Retained{target.Entry.Name}{target.Entry.GameObjectId}"))
            {
                plugin.WatchTargetService.TryForgetSavedTarget(plugin.Configuration, target, out var forgetMessage);
                plugin.PrintStatus(forgetMessage);
            }
            ImGui.EndDisabled();
        }

        ImGui.EndTable();
    }

    private void DrawWatchTable(float height)
    {
        var targets = FilteredTargets().ToArray();
        ImGui.TextUnformatted($"Visible object table ({targets.Length})");
        ImGui.TextDisabled($"Watching {plugin.WatchTargetService.ActiveTargets.Count}/{WatchTargetService.MaxTrackedTargets} active targets. Saved targets: {plugin.WatchTargetService.SavedTargetCount}.");

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

    private static string BuildRetainedStateLabel(ResolvedWatchTarget target)
    {
        if (target.IsMissingFromObjectTable)
            return "Absent";

        if (target.IsHiddenByFilters)
            return "Hidden by filters";

        if (!target.IsVisibleInObjectTable)
            return "Live / retained";

        return target.IsDead ? "Dead" : $"{target.HpPercent}% HP";
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

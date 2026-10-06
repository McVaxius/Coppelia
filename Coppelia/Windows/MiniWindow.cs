using AethertekUI;
using System.Numerics;
using Coppelia.Models;
using Coppelia.Services;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Interface.Windowing;
using Lumina.Excel.Sheets;

namespace Coppelia.Windows;

public sealed class MiniWindow : Window, IDisposable
{
    private readonly AethertekUI.Dalamud.MaterialWindowMotion windowMotion = new();
    private readonly Plugin plugin;
    private bool qstMiniOpen;
    private uint uiRoot;

    internal bool IsCloseProtected => plugin.Configuration.OperatingRole != OperatingRole.Off && !qstMiniOpen;

    public MiniWindow(Plugin plugin)
        : base($"{PluginInfo.DisplayName} Mini###CoppeliaMini")
    {
        this.plugin = plugin;
        Size = new Vector2(510f, 650f);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(380f, 440f),
            MaximumSize = new Vector2(1000f, 1200f),
        };
    }

    public void Dispose()
    {
    }

    public void RefreshDrafts()
    {
    }

    public override void PreOpenCheck()
    {
        // Sample only while drawing; command callbacks must not access native ImGui windows.
        var qstMini = ImGuiP.FindWindowByName("QSTComp Mini###QSTCompMini");
        // The previous frame accounts for either plugin drawing first. Old closed windows do not count.
        qstMiniOpen = !qstMini.IsNull && (qstMini.Active || qstMini.WasActive);

        var protect = IsCloseProtected;
        ShowCloseButton = !protect;
        RespectCloseHotkey = !protect;
        if (protect)
            IsOpen = true;
    }

    public override void PreDraw()
    {
        windowMotion.Prepare(this, reducedMotion: false, roundedCorners: true);
    }

    public override void PostDraw()
        { windowMotion.Restore(this); plugin.PaintWindowTitle(WindowName,UiText.F("{0} Mini",PluginInfo.DisplayName)); }

    public override void Draw()
    {
        windowMotion.DrawChrome();
        UiGui.Title("HealBot Mini",UiText.F("{0} Mini",PluginInfo.DisplayName));
        using var typography = UiText.FontScale(1.25f);
        uiRoot = ImGuiP.GetCurrentWindow().ID;
        CoppeliaUi.Brand("Mini");
        var scale = MaterialTheme.Metrics.Scale;
        var footerHeight = CoppeliaPresentation.ActionHeight * scale + ImGui.GetStyle().ItemSpacing.Y;
        CoppeliaUi.Panel("##HealBotMiniBody", uiRoot, new Vector2(0, Math.Max(240 * scale, ImGui.GetContentRegionAvail().Y - footerHeight)), () =>
        {
            ImGui.PushStyleVar(ImGuiStyleVar.CellPadding, new Vector2(4, 2) * scale);
            var roleWidth = RoleTileWidth() * 2 + ImGui.GetStyle().CellPadding.X * 4;
            var krangleLabel = plugin.Configuration.KrangleNames ? "Krangle names: ON" : "Krangle names: OFF";
            var krangleWidth = MaterialText.Measure(UiText.T(krangleLabel)).X + ImGui.GetStyle().FramePadding.X * 2 + ImGui.GetStyle().CellPadding.X * 2;
            var splitRoles = ImGui.GetContentRegionAvail().X >= roleWidth + krangleWidth;
            if (ImGui.BeginTable("##MiniRoleLayout", splitRoles ? 2 : 1, ImGuiTableFlags.SizingStretchProp))
            {
                ImGui.TableSetupColumn("##Roles", ImGuiTableColumnFlags.WidthStretch, roleWidth);
                if (splitRoles) ImGui.TableSetupColumn("##Krangle", ImGuiTableColumnFlags.WidthStretch, krangleWidth);
                ImGui.TableNextColumn(); ImGuiP.PushOverrideID(uiRoot); DrawRoles(); ImGui.PopID();
                ImGui.TableNextColumn(); ImGuiP.PushOverrideID(uiRoot); DrawKrangleToggle(); ImGui.PopID();
                ImGui.EndTable();
            }
            ImGui.PopStyleVar();
            if (plugin.Configuration.OperatingRole == OperatingRole.StandAlone) DrawStandaloneBehavior();
            if (ShouldDrawJoatAttackMode()) DrawJoatAttackMode();
            DrawCompanion();
            DrawHealingContext();
            if (plugin.CoppeliaTravelService.HealRiderActive) UiGui.TextWrapped(plugin.CoppeliaTravelService.State);
            CoppeliaUi.SectionHeader("Operational status");
            var status = plugin.GetOperationalStatus();
            var statusWidth = 0f;
            using (UiText.Font(UiFontRole.Caption))
                foreach (var caption in new[] { "Primary state", "Next action" })
                    statusWidth = Math.Max(statusWidth, MaterialText.Measure(UiText.T(caption)).X);
            statusWidth += (CoppeliaPresentation.Compact ? 38 : 46) * scale + ImGui.GetStyle().CellPadding.X * 2;
            var statusColumns = ImGui.GetContentRegionAvail().X >= statusWidth * 2 ? 2 : 1;
            if (ImGui.BeginTable("##MiniStatusLayout", statusColumns, ImGuiTableFlags.SizingStretchSame))
            {
                ImGui.TableNextColumn();
                CoppeliaUi.IconSummary(MaterialIcon.Heart, () =>
                {
                    using (UiText.Font(UiFontRole.Caption)) UiGui.TextUnformatted("Primary state");
                    using (UiText.Font(UiFontRole.Heading)) CoppeliaUi.OperationalState(status.PrimaryState, plugin.Configuration.OperatingRole, plugin.LastAutomationBlocker);
                }, CoppeliaUi.OperationalColor(status.PrimaryState, plugin.Configuration.OperatingRole, plugin.LastAutomationBlocker));
                ImGui.TableNextColumn();
                CoppeliaUi.IconSummary(MaterialIcon.Clock, () =>
                {
                    using (UiText.Font(UiFontRole.Caption)) UiGui.TextUnformatted("Next action");
                    using (UiText.Font(UiFontRole.Heading)) UiGui.TextWrapped(status.NextAction);
                });
                ImGui.EndTable();
            }
            if (plugin.Configuration.OperatingRole is OperatingRole.Helper or OperatingRole.Newb)
            {
                var pairing = plugin.HealBotPairingService.Snapshot;
                UiGui.TextDisabled(UiText.F("Endpoint: {0}", pairing.Endpoint));
                UiGui.TextWrapped(UiText.F("Provider: {0}", UiText.T(pairing.ProviderState)));
                if (plugin.Configuration.OperatingRole == OperatingRole.Newb && pairing.State is PairingState.Connected or PairingState.Paired)
                {
                    UiGui.TextWrapped(UiText.F("Remote healing: {0}", UiText.T(pairing.JoatState)));
                    UiGui.TextWrapped(UiText.F("Remote chase: {0}", UiText.T(pairing.TravelState)));
                }
            }
        }, padding: CoppeliaPresentation.Compact ? 10 : 12);
        var width = Math.Max(0, (ImGui.GetContentRegionAvail().X - ImGui.GetStyle().ItemSpacing.X * 2) / 3);
        var size = new Vector2(width, CoppeliaPresentation.ActionHeight * scale);
        if (CoppeliaUi.PrimaryButton("Settings##Mini", size)) plugin.OpenConfigUi();
        ImGui.SameLine();
        if (UiGui.Button("Watch##Mini", size)) plugin.OpenWatchUi();
        ImGui.SameLine();
        if (UiGui.Button("Main##Mini", size)) plugin.OpenMainUi();
    }
    private void DrawKrangleToggle()
    {
        var configuration = plugin.Configuration;
        var label = configuration.KrangleNames ? "Krangle names: ON" : "Krangle names: OFF";
        if (!UiGui.Button($"{label}##MiniKrangle"))
            return;

        configuration.KrangleNames = !configuration.KrangleNames;
        configuration.Save();
        if (!configuration.KrangleNames)
            KrangleService.ClearCache();
    }

    private void DrawHealingContext()
    {
        CoppeliaUi.SectionHeader("Healing context");
        var assignment = plugin.CoppeliaQstIpcService.GetAssignmentSnapshot();
        var heading = "Watched target";
        var target = "No active helper/quester";
        var hp = "HP unavailable";
        var los = "Target not visible";
        var rescue = "Inactive";
        var rawIdentity = false;
        if (!string.IsNullOrWhiteSpace(assignment.Source))
        {
            heading = assignment.Source == "Newb" ? "Watched Newb" : "Watched Quester";
            target = FormatIdentity(assignment.Name, assignment.WorldId);
            rawIdentity = true;
            var snapshot = plugin.WatchTargetService.EphemeralQstTargetSnapshot;
            hp = snapshot == null ? "Target not visible" : FormatHp(snapshot.CurrentHp, snapshot.MaxHp);
            los = snapshot == null ? "Target not visible" : plugin.HealbotRuntimeService.CurrentTargetLineOfSightState;
            rescue = plugin.CoppeliaTravelService.LineOfSightRescueState;
        }
        else if (plugin.Configuration.OperatingRole == OperatingRole.Newb)
        {
            heading = "Paired Helper";
            var pairing = plugin.HealBotPairingService.Snapshot;
            if (pairing.Identity != "None") { target = FormatIdentity(pairing.Identity); rawIdentity = true; }
            var local = Plugin.ObjectTable.LocalPlayer as ICharacter;
            hp = local == null ? "HP unavailable" : FormatHp(local.CurrentHp, local.MaxHp);
        }
        else if (plugin.Configuration.OperatingRole == OperatingRole.StandAlone && plugin.Configuration.BotMode is BotMode.HealBot or BotMode.Jot)
        {
            var name = plugin.HealbotRuntimeService.CurrentWatchedTargetName;
            target = string.IsNullOrWhiteSpace(name) ? "No active watched target" : FormatIdentity(name);
            rawIdentity = !string.IsNullOrWhiteSpace(name);
            hp = plugin.HealbotRuntimeService.CurrentWatchedTargetHpText;
            los = plugin.HealbotRuntimeService.CurrentTargetLineOfSightState;
            rescue = plugin.CoppeliaTravelService.LineOfSightRescueState;
        }
        var headings = new[] { heading, "HP", "LOS", "Rescue" };
        var widths = new[] { 2f, 1f, 1f, 1f };
        var padding = ImGui.GetStyle().CellPadding.X * 2;
        var available = ImGui.GetContentRegionAvail().X;
        var content = Math.Max(0, available - padding * 4);
        var grow = false;
        for (var column = 0; column < widths.Length; column++)
        {
            widths[column] *= content / 5;
            var minimum = MaterialText.Measure(UiText.T(headings[column])).X + 2 * MaterialTheme.Metrics.Scale;
            grow |= minimum > widths[column];
            widths[column] = Math.Max(widths[column], minimum);
        }
        var tableWidth = grow ? widths.Sum() + padding * 4 + 4 * MaterialTheme.Metrics.Scale : 0;
        if (!ImGui.BeginTable("##MiniHealingContext", 4, ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.BordersInnerV, new Vector2(tableWidth, 0))) return;
        for (var column = 0; column < widths.Length; column++)
            ImGui.TableSetupColumn(headings[column], ImGuiTableColumnFlags.WidthStretch, widths[column]);
        UiGui.TableHeadersRow();
        ImGui.TableNextRow();
        ImGui.TableNextColumn();
        ImGui.PushTextWrapPos(0);
        if (rawIdentity) ImGui.TextUnformatted(target); else UiGui.TextUnformatted(target);
        ImGui.PopTextWrapPos();
        ImGui.TableNextColumn(); CoppeliaUi.StatusText(hp, !hp.Contains("unavailable") && !hp.Contains("not visible"));
        ImGui.TableNextColumn(); UiGui.TextWrapped(los);
        ImGui.TableNextColumn(); UiGui.TextWrapped(rescue);
        ImGui.EndTable();
    }
    private string FormatIdentity(string rawIdentity)
        => plugin.FormatDisplayName(rawIdentity);

    private string FormatIdentity(string name, ushort worldId)
    {
        var worldSheet = Plugin.DataManager.GetExcelSheet<World>();
        var world = worldSheet.TryGetRow(worldId, out var row) && !row.Name.IsEmpty
            ? row.Name.ExtractText()
            : worldId.ToString();
        return FormatIdentity($"{name}@{world}");
    }

    private static string FormatHp(uint currentHp, uint maxHp)
        => maxHp == 0
            ? "HP unavailable"
            : $"{Math.Clamp((int)MathF.Round(currentHp * 100f / maxHp), 0, 100)}%";

    private void DrawRoles()
    {
        var labelX = ImGui.GetCursorPosX();
        ImGui.SetCursorPosX(labelX + 2 * MaterialTheme.Metrics.Scale);
        UiGui.TextUnformatted("Operating role");
        ImGui.SetCursorPosX(labelX);
        var columns = ImGui.GetContentRegionAvail().X >= RoleTileWidth() * 2 + ImGui.GetStyle().CellPadding.X * 4 ? 2 : 1;
        if (ImGui.BeginTable("##MiniRoleTiles", columns, ImGuiTableFlags.SizingStretchSame))
        {
            ImGui.TableNextColumn(); ImGuiP.PushOverrideID(uiRoot); DrawRoleRadio("Off##MiniRole", OperatingRole.Off); ImGui.PopID();
            ImGui.TableNextColumn(); ImGuiP.PushOverrideID(uiRoot); DrawRoleRadio("Stand-alone##MiniRole", OperatingRole.StandAlone); ImGui.PopID();
            ImGui.TableNextColumn(); ImGuiP.PushOverrideID(uiRoot); DrawRoleRadio("Helper##MiniRole", OperatingRole.Helper); ImGui.PopID();
            ImGui.TableNextColumn(); ImGuiP.PushOverrideID(uiRoot); DrawRoleRadio("Newb##MiniRole", OperatingRole.Newb); ImGui.PopID();
            ImGui.EndTable();
        }
    }
    private static float RoleTileWidth()
    {
        var width = 0f;
        foreach (var label in new[] { "Off", "Stand-alone", "Helper", "Newb" })
            width = Math.Max(width, MaterialText.Measure(UiText.T(label)).X + 48 * MaterialTheme.Metrics.Scale);
        return width;
    }
    private void DrawRoleRadio(string label, OperatingRole role)
    {
        if (UiGui.RadioTile(label, plugin.Configuration.OperatingRole == role, ImGui.GetContentRegionAvail().X, height: 30))
            plugin.SetOperatingRole(role, printStatus: true);
    }

    private void DrawStandaloneBehavior()
    {
        ImGui.Spacing();
        UiGui.TextUnformatted("Stand-alone behavior");
        DrawBehaviorRadio("HealBot##MiniBehavior", BotMode.HealBot);
        CoppeliaUi.SameLineFor("JOAT");
        DrawBehaviorRadio("JOAT##MiniBehavior", BotMode.Jot);
        CoppeliaUi.SameLineFor("PowerlevelBot");
        DrawBehaviorRadio("PowerlevelBot##MiniBehavior", BotMode.PowerlevelBot);
    }

    private void DrawBehaviorRadio(string label, BotMode mode)
    {
        if (UiGui.RadioTile(label, plugin.Configuration.BotMode == mode, height: 30))
            plugin.SetStandaloneBehavior(mode, printStatus: true);
    }

    private bool ShouldDrawJoatAttackMode()
        => plugin.Configuration.OperatingRole == OperatingRole.Helper ||
           (plugin.Configuration.OperatingRole == OperatingRole.StandAlone &&
            plugin.Configuration.BotMode == BotMode.Jot);

    private void DrawJoatAttackMode()
    {
        CoppeliaUi.SectionHeader("JOAT attack mode");
        var configuration = plugin.Configuration;
        var qstOwned = plugin.CoppeliaQstIpcService.IsJoatAttackModeQstOwned;
        var fullRsrRotation = qstOwned
            ? plugin.CoppeliaQstIpcService.EffectiveJoatFullRsrRotation
            : configuration.JoatFullRsrRotation;

        ImGui.BeginDisabled(qstOwned);
        if (UiGui.RadioButton("DoTs only##MiniJoatAttack", !fullRsrRotation) &&
            configuration.JoatFullRsrRotation)
        {
            configuration.JoatFullRsrRotation = false;
            configuration.Save();
        }
        CoppeliaUi.SameLineFor("Full RSR rotation");
        if (UiGui.RadioButton("Full RSR rotation##MiniJoatAttack", fullRsrRotation) &&
            !configuration.JoatFullRsrRotation)
        {
            configuration.JoatFullRsrRotation = true;
            configuration.Save();
        }
        ImGui.EndDisabled();

        if (qstOwned)
        {
            UiGui.TextDisabled(UiText.F("QST controls the active mode ({0}); the saved local choice resumes after release.", UiText.T(fullRsrRotation ? "Full RSR rotation" : "DoTs only")));
        }

        UiGui.TextDisabled("Both choices use RSR Manual targeting. Full RSR changes offensive actions and AoE only.");
    }

    private void DrawCompanion()
    {
        CoppeliaUi.SectionHeader("Companion");
        var configuration = plugin.Configuration;
        var greensCount = plugin.CoppeliaCompanionService.GetGysahlGreensCount();
        var companionLabel = greensCount.HasValue
            ? $"Summon companion chocobo (Gysahl Greens: {greensCount.Value})##MiniCompanionSummon"
            : "Summon companion chocobo (Gysahl Greens: unavailable)##MiniCompanionSummon";
        var summonCompanion = configuration.SummonCompanionChocobo;

        ImGui.BeginDisabled(plugin.CoppeliaCompanionService.IsQstOwned);
        if (UiGui.Checkbox(companionLabel, ref summonCompanion, "Summon companion chocobo"))
        {
            configuration.SummonCompanionChocobo = summonCompanion;
            configuration.Save();
        }
        ImGui.EndDisabled();

        CoppeliaUi.SameLineFor("Gysahl Greens (NQ + HQ): {0}");
        UiGui.TextDisabled(greensCount.HasValue ? UiText.F("Gysahl Greens (NQ + HQ): {0}", greensCount.Value) : "Gysahl Greens (NQ + HQ): unavailable");

        if (plugin.CoppeliaCompanionService.IsQstOwned)
        {
            UiGui.TextDisabled(UiText.F("QST controls summoning ({0}); the saved local setting resumes after release.", UiText.T(plugin.CoppeliaCompanionService.QstSummoningEnabled ? "Enabled" : "Disabled")));
        }

        DrawCompanionStanceRadio("Free Stance##MiniCompanionStanceFree", CoppeliaCompanionPolicy.FreeStance);
        CoppeliaUi.SameLineFor("Defender Stance##MiniCompanionStanceDefender");
        DrawCompanionStanceRadio("Defender Stance##MiniCompanionStanceDefender", CoppeliaCompanionPolicy.DefenderStance);
        CoppeliaUi.SameLineFor("Attacker Stance##MiniCompanionStanceAttacker");
        DrawCompanionStanceRadio("Attacker Stance##MiniCompanionStanceAttacker", CoppeliaCompanionPolicy.AttackerStance);
        DrawCompanionStanceRadio("Healer Stance##MiniCompanionStanceHealer", CoppeliaCompanionPolicy.HealerStance);
        CoppeliaUi.SameLineFor("Follow##MiniCompanionStanceFollow");
        DrawCompanionStanceRadio("Follow##MiniCompanionStanceFollow", CoppeliaCompanionPolicy.FollowStance);
    }

    private void DrawCompanionStanceRadio(string label, string stance)
    {
        var configuration = plugin.Configuration;
        var selectedStance = CoppeliaCompanionPolicy.NormalizeStance(configuration.CompanionStance);
        if (!UiGui.RadioButton(label, string.Equals(selectedStance, stance, StringComparison.Ordinal)))
            return;

        configuration.CompanionStance = stance;
        configuration.Save();
        plugin.CoppeliaCompanionService.ApplySelectedStanceImmediately();
    }
}

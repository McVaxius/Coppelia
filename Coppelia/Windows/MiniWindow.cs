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
    private readonly Plugin plugin;
    private bool qstMiniOpen;

    internal bool IsCloseProtected => plugin.Configuration.OperatingRole != OperatingRole.Off && !qstMiniOpen;

    public MiniWindow(Plugin plugin)
        : base($"{PluginInfo.DisplayName} Mini###CoppeliaMini")
    {
        this.plugin = plugin;
        Size = new Vector2(500f, 540f);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(460f, 500f),
            MaximumSize = new Vector2(720f, 760f),
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

    public override void Draw()
    {
        DrawRoles();
        DrawKrangleToggle();
        if (plugin.Configuration.OperatingRole == OperatingRole.StandAlone)
            DrawStandaloneBehavior();

        if (ShouldDrawJoatAttackMode())
            DrawJoatAttackMode();

        DrawCompanion();
        DrawHealingContext();
        if (plugin.CoppeliaTravelService.HealRiderActive)
            ImGui.TextWrapped(plugin.CoppeliaTravelService.State);

        CoppeliaUi.SectionHeader("Operational status");
        var status = plugin.GetOperationalStatus();
        ImGui.TextWrapped($"Primary state: {status.PrimaryState}");
        ImGui.TextWrapped($"Next action: {status.NextAction}");

        if (plugin.Configuration.OperatingRole is OperatingRole.Helper or OperatingRole.Newb)
        {
            var pairing = plugin.HealBotPairingService.Snapshot;
            ImGui.TextDisabled($"Endpoint: {pairing.Endpoint}");
            ImGui.TextWrapped($"Provider: {pairing.ProviderState}");
            if (plugin.Configuration.OperatingRole == OperatingRole.Newb &&
                pairing.State is PairingState.Connected or PairingState.Paired)
            {
                ImGui.TextWrapped($"Remote healing: {pairing.JoatState}");
                ImGui.TextWrapped($"Remote chase: {pairing.TravelState}");
            }
        }

        ImGui.Spacing();
        if (CoppeliaUi.PrimaryButton("Settings##Mini"))
            plugin.OpenConfigUi();
        ImGui.SameLine();
        if (ImGui.Button("Watch##Mini"))
            plugin.OpenWatchUi();
        ImGui.SameLine();
        if (ImGui.Button("Main##Mini"))
            plugin.OpenMainUi();
    }

    private void DrawKrangleToggle()
    {
        var configuration = plugin.Configuration;
        var label = configuration.KrangleNames ? "Krangle names: ON" : "Krangle names: OFF";
        if (!ImGui.Button($"{label}##MiniKrangle"))
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
        if (!string.IsNullOrWhiteSpace(assignment.Source))
        {
            var targetLabel = assignment.Source == "Newb" ? "Watched Newb" : "Watched Quester";
            ImGui.TextWrapped($"{targetLabel}: {FormatIdentity(assignment.Name, assignment.WorldId)}");
            var snapshot = plugin.WatchTargetService.EphemeralQstTargetSnapshot;
            ImGui.TextUnformatted($"HP: {(snapshot == null ? "Target not visible" : FormatHp(snapshot.CurrentHp, snapshot.MaxHp))}");
            ImGui.TextUnformatted($"LOS: {(snapshot == null ? "Target not visible" : plugin.HealbotRuntimeService.CurrentTargetLineOfSightState)}");
            ImGui.TextWrapped($"Rescue: {plugin.CoppeliaTravelService.LineOfSightRescueState}");
            return;
        }

        if (plugin.Configuration.OperatingRole == OperatingRole.Helper)
        {
            ImGui.TextDisabled("No active helper/quester");
            ImGui.TextUnformatted("HP: Target not visible");
            ImGui.TextUnformatted("LOS: Target not visible");
            ImGui.TextUnformatted("Rescue: Inactive");
            return;
        }

        if (plugin.Configuration.OperatingRole == OperatingRole.Newb)
        {
            var pairing = plugin.HealBotPairingService.Snapshot;
            ImGui.TextWrapped(pairing.Identity == "None"
                ? "No active helper/quester"
                : $"Paired Helper: {FormatIdentity(pairing.Identity)}");
            var local = Plugin.ObjectTable.LocalPlayer as ICharacter;
            ImGui.TextUnformatted($"Local Newb HP: {(local == null ? "HP unavailable" : FormatHp(local.CurrentHp, local.MaxHp))}");
            return;
        }

        if (plugin.Configuration.OperatingRole == OperatingRole.StandAlone &&
            plugin.Configuration.BotMode is BotMode.HealBot or BotMode.Jot)
        {
            var targetName = plugin.HealbotRuntimeService.CurrentWatchedTargetName;
            if (string.IsNullOrWhiteSpace(targetName))
            {
                ImGui.TextDisabled("No active watched target");
                ImGui.TextUnformatted("HP: HP unavailable");
                ImGui.TextUnformatted("LOS: Target not visible");
            }
            else
            {
                ImGui.TextWrapped($"Watched target: {FormatIdentity(targetName)}");
                ImGui.TextUnformatted($"HP: {plugin.HealbotRuntimeService.CurrentWatchedTargetHpText}");
                ImGui.TextUnformatted($"LOS: {plugin.HealbotRuntimeService.CurrentTargetLineOfSightState}");
            }
            ImGui.TextWrapped($"Rescue: {plugin.CoppeliaTravelService.LineOfSightRescueState}");
            return;
        }

        ImGui.TextDisabled("No active helper/quester");
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
        ImGui.TextUnformatted("Operating role");
        DrawRoleRadio("Off##MiniRole", OperatingRole.Off);
        ImGui.SameLine();
        DrawRoleRadio("Stand-alone##MiniRole", OperatingRole.StandAlone);
        ImGui.SameLine();
        DrawRoleRadio("Helper##MiniRole", OperatingRole.Helper);
        ImGui.SameLine();
        DrawRoleRadio("Newb##MiniRole", OperatingRole.Newb);
    }

    private void DrawRoleRadio(string label, OperatingRole role)
    {
        if (ImGui.RadioButton(label, plugin.Configuration.OperatingRole == role))
            plugin.SetOperatingRole(role, printStatus: true);
    }

    private void DrawStandaloneBehavior()
    {
        ImGui.Spacing();
        ImGui.TextUnformatted("Stand-alone behavior");
        DrawBehaviorRadio("HealBot##MiniBehavior", BotMode.HealBot);
        ImGui.SameLine();
        DrawBehaviorRadio("JOAT##MiniBehavior", BotMode.Jot);
        ImGui.SameLine();
        DrawBehaviorRadio("PowerlevelBot##MiniBehavior", BotMode.PowerlevelBot);
    }

    private void DrawBehaviorRadio(string label, BotMode mode)
    {
        if (ImGui.RadioButton(label, plugin.Configuration.BotMode == mode))
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
        if (ImGui.RadioButton("DoTs only##MiniJoatAttack", !fullRsrRotation) &&
            configuration.JoatFullRsrRotation)
        {
            configuration.JoatFullRsrRotation = false;
            configuration.Save();
        }
        ImGui.SameLine();
        if (ImGui.RadioButton("Full RSR rotation##MiniJoatAttack", fullRsrRotation) &&
            !configuration.JoatFullRsrRotation)
        {
            configuration.JoatFullRsrRotation = true;
            configuration.Save();
        }
        ImGui.EndDisabled();

        if (qstOwned)
        {
            ImGui.TextDisabled(
                $"QST controls the active mode ({(fullRsrRotation ? "Full RSR rotation" : "DoTs only")}); " +
                "the saved local choice resumes after release.");
        }

        ImGui.TextDisabled("Both choices use RSR Manual targeting. Full RSR changes offensive actions and AoE only.");
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
        if (ImGui.Checkbox(companionLabel, ref summonCompanion))
        {
            configuration.SummonCompanionChocobo = summonCompanion;
            configuration.Save();
        }
        ImGui.EndDisabled();

        if (plugin.CoppeliaCompanionService.IsQstOwned)
        {
            ImGui.TextDisabled(
                $"QST controls summoning ({(plugin.CoppeliaCompanionService.QstSummoningEnabled ? "enabled" : "disabled")}); " +
                "the saved local setting resumes after release.");
        }

        DrawCompanionStanceRadio("Free Stance##MiniCompanionStanceFree", CoppeliaCompanionPolicy.FreeStance);
        ImGui.SameLine();
        DrawCompanionStanceRadio("Defender Stance##MiniCompanionStanceDefender", CoppeliaCompanionPolicy.DefenderStance);
        ImGui.SameLine();
        DrawCompanionStanceRadio("Attacker Stance##MiniCompanionStanceAttacker", CoppeliaCompanionPolicy.AttackerStance);
        DrawCompanionStanceRadio("Healer Stance##MiniCompanionStanceHealer", CoppeliaCompanionPolicy.HealerStance);
        ImGui.SameLine();
        DrawCompanionStanceRadio("Follow##MiniCompanionStanceFollow", CoppeliaCompanionPolicy.FollowStance);
    }

    private void DrawCompanionStanceRadio(string label, string stance)
    {
        var configuration = plugin.Configuration;
        var selectedStance = CoppeliaCompanionPolicy.NormalizeStance(configuration.CompanionStance);
        if (!ImGui.RadioButton(label, string.Equals(selectedStance, stance, StringComparison.Ordinal)))
            return;

        configuration.CompanionStance = stance;
        configuration.Save();
        plugin.CoppeliaCompanionService.ApplySelectedStanceImmediately();
    }
}

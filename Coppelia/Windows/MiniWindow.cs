using System.Numerics;
using Coppelia.Models;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace Coppelia.Windows;

public sealed class MiniWindow : Window, IDisposable
{
    private readonly Plugin plugin;

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

    public override void Draw()
    {
        DrawRoles();
        if (plugin.Configuration.OperatingRole == OperatingRole.StandAlone)
            DrawStandaloneBehavior();

        if (ShouldDrawJoatAttackMode())
            DrawJoatAttackMode();

        DrawCompanion();

        CoppeliaUi.SectionHeader("Operational status");
        var status = plugin.GetOperationalStatus();
        ImGui.TextWrapped($"Primary state: {status.PrimaryState}");
        ImGui.TextWrapped($"Next action: {status.NextAction}");
        ImGui.TextWrapped($"Active / paired identity: {status.Identity}");

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

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
        Size = new Vector2(500f, 390f);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(460f, 340f),
            MaximumSize = new Vector2(720f, 620f),
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
}

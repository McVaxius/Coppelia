using System.Numerics;
using System.Security.Cryptography;
using Coppelia.Models;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace Coppelia.Windows;

public sealed class MiniWindow : Window, IDisposable
{
    private readonly Plugin plugin;
    private string addressDraft = string.Empty;
    private int portDraft;
    private string secretDraft = string.Empty;
    private bool enabledDraft;

    public MiniWindow(Plugin plugin)
        : base($"{PluginInfo.DisplayName} Mini###CoppeliaMini")
    {
        this.plugin = plugin;
        RefreshDrafts();
        Size = new Vector2(460f, 590f);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(410f, 480f),
            MaximumSize = new Vector2(680f, 850f),
        };
    }

    public void Dispose()
    {
    }

    public void RefreshDrafts()
    {
        addressDraft = plugin.Configuration.LanHealBotAddress;
        portDraft = plugin.Configuration.LanPairingPort;
        secretDraft = plugin.Configuration.LanPairingSecret;
        enabledDraft = plugin.Configuration.EnableLanPairing;
    }

    public override void Draw()
    {
        DrawModes();
        DrawAutomation();
        CoppeliaUi.SectionHeader("Direct Newb pairing");
        DrawPairingSettings();
        CoppeliaUi.SectionHeader("Live status");
        DrawStatus();
    }

    private void DrawModes()
    {
        ImGui.TextUnformatted("Mode");
        DrawModeRadio("HealBot##MiniHeal", BotMode.HealBot);
        ImGui.SameLine();
        DrawModeRadio("JOAT##MiniJoat", BotMode.Jot);
        ImGui.SameLine();
        DrawModeRadio("PowerlevelBot##MiniPowerlevel", BotMode.PowerlevelBot);
        ImGui.SameLine();
        DrawModeRadio("Newb##MiniNewb", BotMode.Newb);
    }

    private void DrawModeRadio(string label, BotMode mode)
    {
        if (ImGui.RadioButton(label, plugin.Configuration.BotMode == mode))
            plugin.SetBotMode(mode, printStatus: true);
    }

    private void DrawAutomation()
    {
        ImGui.Spacing();
        var running = plugin.Configuration.AutomationEnabled;
        if (running)
        {
            if (ImGui.Button("Stop##MiniAutomation", new Vector2(110f, 32f)))
                plugin.SetAutomationEnabled(false, printStatus: true);
        }
        else if (CoppeliaUi.PrimaryButton("Start##MiniAutomation", new Vector2(110f, 32f)))
        {
            plugin.SetAutomationEnabled(true, printStatus: true);
        }

        ImGui.SameLine();
        ImGui.TextColored(
            running ? CoppeliaUi.Ready : CoppeliaUi.Muted,
            running ? $"{plugin.Configuration.BotMode.GetLabel()} running" : "Automation stopped");
    }

    private void DrawPairingSettings()
    {
        var role = plugin.Configuration.BotMode switch
        {
            BotMode.Newb => "Newb client",
            BotMode.HealBot => "HealBot listener",
            _ => $"Inactive in {plugin.Configuration.BotMode.GetLabel()} mode",
        };
        ImGui.TextDisabled($"Role: {role}");

        ImGui.Checkbox("Enable authenticated LAN pairing##Mini", ref enabledDraft);

        ImGui.SetNextItemWidth(-1f);
        ImGui.InputText("HealBot IPv4 address##Mini", ref addressDraft, 45);
        CoppeliaUi.Tooltip(
            plugin.Configuration.BotMode == BotMode.Newb
                ? "Enter the HealBot PC's IPv4 address. Use 127.0.0.1 only for same-PC pairing."
                : "This entry is used when this client runs Newb mode; HealBot listens on all local IPv4 interfaces.");

        ImGui.SetNextItemWidth(140f);
        ImGui.InputInt("TCP port##Mini", ref portDraft);
        CoppeliaUi.Tooltip("Use 1024-65535 except reserved port 47789. Default: 47790.");

        ImGui.SetNextItemWidth(-1f);
        ImGui.InputText("Pair secret##Mini", ref secretDraft, 256, ImGuiInputTextFlags.Password);
        CoppeliaUi.Tooltip("Both clients must use the same secret of at least 16 characters.");

        if (ImGui.SmallButton("Generate##MiniSecret"))
            secretDraft = Convert.ToBase64String(RandomNumberGenerator.GetBytes(24));
        ImGui.SameLine();
        ImGui.BeginDisabled(string.IsNullOrEmpty(secretDraft));
        if (ImGui.SmallButton("Copy##MiniSecret"))
            ImGui.SetClipboardText(secretDraft);
        ImGui.EndDisabled();
        ImGui.SameLine();
        if (CoppeliaUi.PrimaryButton("Save / restart pairing##Mini"))
            SaveAndRestartPairing();

        CoppeliaUi.WrappedHelp(
            "Pairing uses authenticated direct TCP and no discovery. Traffic is not encrypted: character names and coordinates remain visible to the local network.");
    }

    private void SaveAndRestartPairing()
    {
        var configuration = plugin.Configuration;
        configuration.EnableLanPairing = enabledDraft;
        configuration.LanHealBotAddress = addressDraft.Trim();
        configuration.LanPairingPort = portDraft;
        configuration.LanPairingSecret = secretDraft;
        configuration.Save();
        plugin.HealBotPairingService.Restart();
        plugin.PrintStatus("Pairing settings saved; direct pairing is restarting.");
    }

    private void DrawStatus()
    {
        var pairing = plugin.HealBotPairingService;
        ImGui.TextWrapped($"Connection: {pairing.ConnectionStatus}");
        ImGui.TextWrapped($"Pair: {pairing.PairingIdentity}");
        ImGui.TextWrapped($"Runtime: {pairing.RuntimeStatus}");
        ImGui.TextWrapped($"Healing: {pairing.HealingStatus}");
        ImGui.TextWrapped($"Chase: {pairing.ChaseStatus}");
        if (string.IsNullOrWhiteSpace(pairing.Blocker))
            CoppeliaUi.StatusText("Blocker: none", ready: true);
        else
            CoppeliaUi.StatusText($"Blocker: {pairing.Blocker}", ready: false);
    }
}

using System.Diagnostics;
using System.Numerics;
using System.Security.Cryptography;
using Coppelia.Models;
using Coppelia.Services;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace Coppelia.Windows;

public sealed class ConfigWindow : Window, IDisposable
{
    private static readonly string[] DtrModes = { "Text only", "Icon + text", "Icon only" };
    private static readonly HealbotTriggerKind[] TriggerKinds = Enum.GetValues<HealbotTriggerKind>();
    private static readonly (uint JobId, string Label)[] JobTabs =
    [
        (24u, "White Mage (WHM)"),
        (28u, "Scholar (SCH)"),
        (33u, "Astrologian (AST)"),
        (40u, "Sage (SGE)"),
    ];

    private readonly Plugin plugin;
    private DateTimeOffset nextWindowPositionSaveUtc = DateTimeOffset.MinValue;
    private Vector2? pendingWindowPosition;
    private Vector2? lastSavedWindowPosition;
    private bool pendingSavedPositionApply;
    private bool selectQuickSetupTab;
    private bool selectGeneralTab;
    private QuickSetupDraft? setupDraft;
    private QuickSetupStep setupStep;
    private QuickSetupCompletionChoice setupCompletionChoice;
    private string setupMessage = string.Empty;
    private PowerlevelSetupReadiness? powerlevelReadiness;
    private DateTimeOffset nextPowerlevelReadinessUtc = DateTimeOffset.MinValue;
    private JotSetupReadiness? jotReadiness;
    private DateTimeOffset nextJotReadinessUtc = DateTimeOffset.MinValue;
    private string networkAddressDraft;
    private int networkPortDraft;
    private string networkSecretDraft;
    private string networkConfirmation = string.Empty;

    public ConfigWindow(Plugin plugin)
        : base($"{PluginInfo.DisplayName} Settings###CoppeliaConfig")
    {
        this.plugin = plugin;
        networkAddressDraft = plugin.Configuration.LanHealBotAddress;
        networkPortDraft = plugin.Configuration.LanPairingPort;
        networkSecretDraft = plugin.Configuration.LanPairingSecret;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(920f, 680f),
            MaximumSize = new Vector2(1600f, 1200f),
        };
        Size = new Vector2(1180f, 820f);
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
        var configuration = plugin.Configuration;
        var changed = false;

        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(6f, 4f));
        try
        {
            DrawHeader();
            DrawSettingsTabs(configuration, ref changed);
        }
        finally
        {
            ImGui.PopStyleVar();
        }

        if (changed)
        {
            configuration.Save();
            plugin.DependencyService.Refresh(force: true);
            plugin.WatchTargetService.Update(configuration, force: true);
            plugin.UpdateDtrBar();
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
        if (plugin.TryGetSavedWindowPosition(true, out var saved))
        {
            pendingWindowPosition = saved.ToVector2();
            return;
        }

        pendingWindowPosition = new Vector2(1f, 1f);
    }

    internal void OpenQuickSetup()
    {
        StartQuickSetup();
        selectQuickSetupTab = true;
    }

    private void DrawHeader()
    {
        ImGui.TextColored(CoppeliaUi.Accent, $"{PluginInfo.DisplayName} Settings");
        ImGui.SameLine();
        if (ImGui.SmallButton("Ko-fi##CoppeliaConfig"))
            Process.Start(new ProcessStartInfo { FileName = PluginInfo.SupportUrl, UseShellExecute = true });

        ImGui.SameLine();
        if (ImGui.SmallButton("Discord##CoppeliaConfig"))
            Process.Start(new ProcessStartInfo { FileName = PluginInfo.DiscordUrl, UseShellExecute = true });

        ImGui.SameLine();
        if (ImGui.SmallButton("Watch##CoppeliaConfig"))
            plugin.ToggleWatchUi();

        ImGui.TextDisabled(PluginInfo.DiscordFeedbackNote);
    }

    private void DrawSettingsTabs(Configuration configuration, ref bool changed)
    {
        var quickSetupFlags = selectQuickSetupTab ? ImGuiTabItemFlags.SetSelected : ImGuiTabItemFlags.None;
        var generalFlags = selectGeneralTab ? ImGuiTabItemFlags.SetSelected : ImGuiTabItemFlags.None;
        selectQuickSetupTab = false;
        selectGeneralTab = false;

        if (!ImGui.BeginTabBar("CoppeliaSettingsTabs"))
            return;

        if (ImGui.BeginTabItem("Quick Setup", quickSetupFlags))
        {
            DrawQuickSetup(configuration);
            ImGui.EndTabItem();
        }

        if (ImGui.BeginTabItem("General", generalFlags))
        {
            CoppeliaUi.SectionHeader(
                "General",
                "These controls are applied immediately. Quick Setup uses a separate draft and changes nothing until Finish.");
            DrawGeneralSettings(configuration, ref changed);
            ImGui.EndTabItem();
        }

        if (ImGui.BeginTabItem("HealBot Actions"))
        {
            CoppeliaUi.SectionHeader(
                "HealBot Actions",
                "HealBot and JOAT share this per-job healing action matrix. PowerlevelBot does not use these rules.");
            DrawJobTabsContent(configuration, ref changed);
            ImGui.EndTabItem();
        }

        if (ImGui.BeginTabItem("Requirements / Help"))
        {
            CoppeliaUi.SectionHeader("Requirements and help");
            DrawRequirements();
            ImGui.EndTabItem();
        }

        ImGui.EndTabBar();
    }

    private void DrawQuickSetup(Configuration configuration)
    {
        setupDraft ??= QuickSetupDraft.FromConfiguration(configuration);

        CoppeliaUi.SectionHeader(
            "Quick Setup",
            "Choose an operating role. Stand-alone then uses one local behavior. Settings stay in this draft until Finish; Cancel discards the draft.");

        if (setupStep == QuickSetupStep.Complete)
        {
            CoppeliaUi.StatusText(setupMessage, ready: true);
            CoppeliaUi.WrappedHelp("Quick Setup is complete. You can run it again at any time from this permanent tab.");
            if (CoppeliaUi.PrimaryButton("Run Quick Setup again"))
                StartQuickSetup();
            return;
        }

        var visibleStep = setupStep switch
        {
            QuickSetupStep.ChooseMode => 1,
            QuickSetupStep.Configure => 2,
            _ => 3,
        };
        ImGui.TextDisabled($"Step {visibleStep} of 3");

        switch (setupStep)
        {
            case QuickSetupStep.ChooseMode:
                DrawSetupModeChoice();
                break;
            case QuickSetupStep.Configure:
                if (setupDraft.Role == OperatingRole.Helper)
                    DrawPairingSetup(OperatingRole.Helper);
                else if (setupDraft.Role == OperatingRole.Newb)
                    DrawPairingSetup(OperatingRole.Newb);
                else if (setupDraft.Mode == BotMode.PowerlevelBot)
                    DrawPowerlevelSetup();
                else if (setupDraft.Mode == BotMode.Jot)
                    DrawHealbotSetup(jot: true);
                else
                    DrawHealbotSetup();
                break;
            case QuickSetupStep.Finish:
                DrawSetupFinish(configuration);
                break;
        }
    }

    private void DrawSetupModeChoice()
    {
        CoppeliaUi.WrappedHelp(
            "Stand-alone runs HealBot, JOAT, or PowerlevelBot without networking. Helper listens for and remotely activates JOAT for one authenticated Newb. Newb connects asynchronously and performs no local healing or attacking.");
        ImGui.Spacing();

        if (CoppeliaUi.PrimaryButton("Set up Stand-alone", new Vector2(220f, 38f)))
        {
            setupDraft!.Role = OperatingRole.StandAlone;
            if (setupDraft.Mode == BotMode.Newb)
                setupDraft.Mode = BotMode.HealBot;
            setupStep = QuickSetupStep.Configure;
            setupMessage = string.Empty;
        }
        CoppeliaUi.Tooltip("Configure the selected HealBot, JOAT, or PowerlevelBot behavior without networking.");

        ImGui.SameLine();
        if (CoppeliaUi.PrimaryButton("Set up Helper", new Vector2(220f, 38f)))
        {
            setupDraft!.Role = OperatingRole.Helper;
            setupStep = QuickSetupStep.Configure;
            setupMessage = string.Empty;
        }
        CoppeliaUi.Tooltip("Configure the authenticated listener port and visible shared secret.");

        ImGui.SameLine();
        if (CoppeliaUi.PrimaryButton("Set up Newb", new Vector2(220f, 38f)))
        {
            setupDraft!.Role = OperatingRole.Newb;
            setupStep = QuickSetupStep.Configure;
            setupMessage = string.Empty;
        }
        CoppeliaUi.Tooltip("Configure one authenticated direct-TCP HealBot endpoint. Newb never heals or attacks locally.");

        CoppeliaUi.SectionHeader("Stand-alone behavior");
        var heal = setupDraft!.Mode == BotMode.HealBot;
        if (ImGui.RadioButton("HealBot##SetupBehavior", heal))
            setupDraft.Mode = BotMode.HealBot;
        ImGui.SameLine();
        var joat = setupDraft.Mode == BotMode.Jot;
        if (ImGui.RadioButton("JOAT##SetupBehavior", joat))
            setupDraft.Mode = BotMode.Jot;
        ImGui.SameLine();
        var powerlevel = setupDraft.Mode == BotMode.PowerlevelBot;
        if (ImGui.RadioButton("PowerlevelBot##SetupBehavior", powerlevel))
            setupDraft.Mode = BotMode.PowerlevelBot;

        ImGui.Spacing();
        if (ImGui.Button("Cancel##QuickSetupChoose"))
            CancelQuickSetup();
    }

    private void DrawHealbotSetup(bool jot = false)
    {
        var draft = setupDraft!;
        CoppeliaUi.SectionHeader(
            jot ? "JOAT healing path" : "HealBot path",
            $"HealBot evaluates the configured {WatchTargetService.MaxTrackedTargets}-target watch list and uses the existing WHM, SCH, AST, or SGE action matrix. Targets may be friendly players outside your party.");

        var dependencies = plugin.DependencyService.Current;
        plugin.HealbotRuntimeService.IsSupportedLocalJob(out var healerProfile, out var healerReason);
        CoppeliaUi.StatusLine("FrenRider", dependencies.FrenRiderLoaded, "Loaded", "Missing");
        CoppeliaUi.StatusLine("vnavmesh", dependencies.VNavmeshLoaded, "Loaded", "Missing");
        CoppeliaUi.StatusLine("BMR or VBM", dependencies.HasBossModProvider, "Loaded", "Missing");
        CoppeliaUi.StatusLine(
            "Supported healer",
            healerProfile != null,
            healerProfile == null ? "Ready" : $"{healerProfile.JobDisplayName} equipped",
            healerReason);

        CoppeliaUi.SectionHeader(
            "Target discovery and persistence",
            "Choose which friendly objects appear in Watch. Only targets you explicitly check are healed or saved.");

        var watchPlayers = draft.WatchPlayers;
        if (ImGui.Checkbox("Players##Setup", ref watchPlayers))
            draft.WatchPlayers = watchPlayers;

        ImGui.SameLine();
        var watchChocobos = draft.WatchCompanionChocobos;
        if (ImGui.Checkbox("Companion chocobos##Setup", ref watchChocobos))
            draft.WatchCompanionChocobos = watchChocobos;

        var watchPartyNpcs = draft.WatchPartyNpcs;
        if (ImGui.Checkbox("NPC party members##Setup", ref watchPartyNpcs))
            draft.WatchPartyNpcs = watchPartyNpcs;

        ImGui.SameLine();
        var watchBattleNpcs = draft.WatchFriendlyBattleNpcs;
        if (ImGui.Checkbox("Friendly battle NPCs##Setup", ref watchBattleNpcs))
            draft.WatchFriendlyBattleNpcs = watchBattleNpcs;

        var saveTargets = draft.SaveHealTargets;
        if (ImGui.Checkbox("Save explicitly watched heal targets##Setup", ref saveTargets))
            draft.SaveHealTargets = saveTargets;

        ImGui.SameLine();
        ImGui.BeginDisabled(!draft.SaveHealTargets);
        ImGui.SetNextItemWidth(170f);
        var scanRange = draft.SavedTargetScanRangeYalms;
        if (ImGui.SliderInt("Rejoin scan range##Setup", ref scanRange, 1, 200, "%d y"))
            draft.SavedTargetScanRangeYalms = scanRange;
        ImGui.EndDisabled();

        CoppeliaUi.WrappedHelp(
            "The scan range only lets a previously saved target rejoin after it returns. It never discovers or auto-selects a new target.");
        if (ImGui.Button("Open Watch window##QuickSetup"))
            plugin.OpenWatchUi();
        CoppeliaUi.Tooltip("Open the existing Watch window now. Draft filter changes apply only after Finish.");

        if (jot)
        {
            CoppeliaUi.SectionHeader(
                "JOAT attacking readiness",
                "JOAT never adds FrenRider's Fren to Watch. Explicitly select the Fren there so healing can protect it; attacks remain limited to enemies already targeting that visible Fren or the local healer.");
            RefreshJotReadiness();
            if (jotReadiness != null)
            {
                var readiness = jotReadiness;
                CoppeliaUi.StatusLine("Healing dependencies", readiness.HealingDependenciesReady, "Ready", "Missing");
                CoppeliaUi.StatusLine("Supported healer", readiness.SupportedHealer, readiness.HealerLabel, readiness.HealerLabel);
                CoppeliaUi.StatusLine("Healer action matrix", readiness.HealerConfigurationEnabled, "Enabled", "Disabled");
                CoppeliaUi.StatusLine("Watched targets", readiness.WatchedTargetsAvailable, "Selected", "None selected");
                CoppeliaUi.StatusLine("Healing gate", readiness.HealingReady, "Ready", readiness.HealingReason);
                CoppeliaUi.StatusLine(
                    "FrenRider IPC",
                    readiness.FrenRiderIpcAvailable && readiness.FrenRiderCompatible,
                    "Available and compatible",
                    readiness.FrenRiderIpcAvailable ? "Incompatible" : "Unavailable");
                CoppeliaUi.StatusLine("FrenRider", readiness.FrenRiderEnabled, "Enabled", "Disabled");
                CoppeliaUi.StatusLine("Configured Fren", readiness.FrenConfigured, "Configured", "Not configured");
                CoppeliaUi.StatusLine("Fren visibility", readiness.FrenVisible, "Visible", "Not visible");
                CoppeliaUi.StatusText(readiness.AttackingReason, readiness.AttackingReady);
            }

            if (ImGui.SmallButton("Refresh readiness##QuickSetupJot"))
            {
                nextJotReadinessUtc = DateTimeOffset.MinValue;
                RefreshJotReadiness();
            }

            CoppeliaUi.WrappedHelp(
                "Healing wins every 900-ms decision cycle. JOAT attacks only after no healing action was queued or blocked, using the highest available single-target filler spell for the equipped healer. It never uses DoTs, AoE, or oGCD attacks.");
        }

        DrawSetupNavigation(allowContinue: true);
    }

    private void DrawPowerlevelSetup()
    {
        var draft = setupDraft!;
        CoppeliaUi.SectionHeader(
            "PowerlevelBot path",
            "Choose the ranged job that is already equipped. HealBot never switches gearsets and will not use the watched-target list in this mode.");

        var brdSelected = draft.PowerlevelJob == PowerlevelJob.BRD;
        if (ImGui.RadioButton("Bard (BRD)##SetupPowerlevel", brdSelected))
        {
            draft.PowerlevelJob = PowerlevelJob.BRD;
            nextPowerlevelReadinessUtc = DateTimeOffset.MinValue;
        }

        ImGui.SameLine();
        var mchSelected = draft.PowerlevelJob == PowerlevelJob.MCH;
        if (ImGui.RadioButton("Machinist (MCH)##SetupPowerlevel", mchSelected))
        {
            draft.PowerlevelJob = PowerlevelJob.MCH;
            nextPowerlevelReadinessUtc = DateTimeOffset.MinValue;
        }

        RefreshPowerlevelReadiness();
        if (powerlevelReadiness != null)
        {
            var readiness = powerlevelReadiness;
            CoppeliaUi.StatusLine(
                "Selected job",
                readiness.SelectedJobSupported,
                readiness.SelectedJob.GetLabel(),
                "Select BRD or MCH");
            CoppeliaUi.StatusLine(
                "Job unlocked",
                readiness.SelectedJobUnlocked,
                "Unlocked",
                $"{readiness.SelectedJob.GetLabel()} is not unlocked");
            CoppeliaUi.StatusLine(
                "Job equipped",
                readiness.CurrentJobMatches,
                $"{readiness.SelectedJob.GetLabel()} equipped",
                $"Current job ID {readiness.CurrentJobId} does not match");
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

        if (ImGui.SmallButton("Refresh readiness##QuickSetupPowerlevel"))
        {
            nextPowerlevelReadinessUtc = DateTimeOffset.MinValue;
            RefreshPowerlevelReadiness();
        }

        CoppeliaUi.SectionHeader("Restricted enemy policy");
        CoppeliaUi.WrappedHelp(
            "PowerlevelBot considers only living, targetable, damaged combatant enemies already targeting FrenRider's configured visible Fren or the local player. It uses instant, hostile-only, single-target BRD/MCH actions and does not pull untouched enemies.");

        DrawSetupNavigation(allowContinue: draft.PowerlevelJob.IsSupportedPowerlevelJob());
    }

    private void DrawPairingSetup(OperatingRole role)
    {
        var draft = setupDraft!;
        CoppeliaUi.SectionHeader(
            $"{role.GetLabel()} direct pairing",
            role == OperatingRole.Helper
                ? "Helper listens for one authenticated Newb and activates JOAT only after a compatible status request."
                : "Newb connects asynchronously to one exact Helper IPv4 endpoint and never heals or attacks locally.");

        if (role == OperatingRole.Newb)
        {
            ImGui.SetNextItemWidth(320f);
            var address = draft.LanHealBotAddress;
            if (ImGui.InputText("Helper IPv4 address##SetupNewb", ref address, 45))
                draft.LanHealBotAddress = address;
        }

        ImGui.SetNextItemWidth(160f);
        var port = draft.LanPairingPort;
        if (ImGui.InputInt("TCP port##SetupNewb", ref port))
            draft.LanPairingPort = port;

        ImGui.SetNextItemWidth(420f);
        var secret = draft.LanPairingSecret;
        if (ImGui.InputText("Visible shared secret##SetupNewb", ref secret, 256))
            draft.LanPairingSecret = secret;

        if (ImGui.SmallButton("Generate secret##SetupNewb"))
            draft.LanPairingSecret = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        ImGui.SameLine();
        ImGui.BeginDisabled(string.IsNullOrEmpty(draft.LanPairingSecret));
        if (ImGui.SmallButton("Copy secret##SetupNewb"))
            ImGui.SetClipboardText(draft.LanPairingSecret);
        ImGui.EndDisabled();

        var localPlayer = Plugin.ObjectTable.LocalPlayer;
        var identityReady = role != OperatingRole.Newb ||
                            localPlayer != null &&
                            !string.IsNullOrWhiteSpace(localPlayer.Name.TextValue) &&
                            localPlayer.HomeWorld.RowId != 0;
        if (role == OperatingRole.Newb)
        {
            CoppeliaUi.StatusLine(
                "Local Newb identity",
                identityReady,
                identityReady ? "Available" : "Unavailable",
                "Log in fully before starting Newb");
        }
        var addressReady = role != OperatingRole.Newb || Configuration.TryParseLanHealBotAddress(draft.LanHealBotAddress, out _);
        var portReady = Configuration.IsValidLanPairingPort(draft.LanPairingPort);
        var secretReady = draft.LanPairingSecret.Length >= 16;
        if (role == OperatingRole.Newb)
            CoppeliaUi.StatusLine("Helper address", addressReady, "Valid IPv4", "Invalid IPv4 address");
        CoppeliaUi.StatusLine("Pair port", portReady, "Valid", draft.LanPairingPort == Configuration.ReservedLanDiscoveryPort ? "47789 is reserved" : "Use 1024-65535");
        CoppeliaUi.StatusLine("Shared secret", secretReady, "At least 16 characters", "Too short");
        CoppeliaUi.WrappedHelp(
            "LAN traffic is authenticated but not encrypted. Character names and coordinates remain visible on the local network. Connection, authentication, assignment, and reconnect work remain asynchronous.");

        DrawSetupNavigation(addressReady && portReady && secretReady && identityReady);
    }

    private void DrawSetupFinish(Configuration configuration)
    {
        var draft = setupDraft!;
        CoppeliaUi.SectionHeader("Review and finish");
        ImGui.TextUnformatted($"Role: {draft.Role.GetLabel()}");
        if (draft.Role is OperatingRole.Helper or OperatingRole.Newb)
        {
            if (draft.Role == OperatingRole.Newb)
                ImGui.TextDisabled($"Helper endpoint: {draft.LanHealBotAddress}:{draft.LanPairingPort}.");
            else
                ImGui.TextDisabled($"Helper listener port: {draft.LanPairingPort}.");
            ImGui.TextDisabled("Authenticated direct pairing uses the visible shared secret.");
        }
        else if (draft.Mode != BotMode.PowerlevelBot)
        {
            ImGui.TextDisabled(
                $"Filters: players {(draft.WatchPlayers ? "on" : "off")}, chocobos {(draft.WatchCompanionChocobos ? "on" : "off")}, NPC party {(draft.WatchPartyNpcs ? "on" : "off")}, friendly battle NPCs {(draft.WatchFriendlyBattleNpcs ? "on" : "off")}.");
            ImGui.TextDisabled(draft.SaveHealTargets
                ? $"Saved targets on; {draft.SavedTargetScanRangeYalms} y rejoin scan."
                : "Saved targets off.");
            if (draft.Mode == BotMode.Jot)
                ImGui.TextDisabled("JOAT attacks only during genuinely idle healing cycles and ignores the Powerlevel job selection.");
        }
        else
        {
            ImGui.TextDisabled($"Powerlevel job: {draft.PowerlevelJob.GetLabel()}.");
            ImGui.TextDisabled("Enemy selection remains restricted to damaged enemies already engaging the Fren or local player.");
        }

        CoppeliaUi.WrappedHelp(
            "Finish must either start the selected role now or save it as the role resumed by /healbot on while remaining Off.");

        var enableNow = setupCompletionChoice == QuickSetupCompletionChoice.EnableNow;
        if (ImGui.RadioButton("Start this role now##QuickSetupFinish", enableNow))
            setupCompletionChoice = QuickSetupCompletionChoice.EnableNow;

        var leaveOff = setupCompletionChoice == QuickSetupCompletionChoice.LeaveAutomationOff;
        if (ImGui.RadioButton("Save setup and leave role Off##QuickSetupFinish", leaveOff))
            setupCompletionChoice = QuickSetupCompletionChoice.LeaveAutomationOff;

        if (!string.IsNullOrWhiteSpace(setupMessage))
            CoppeliaUi.StatusText(setupMessage, ready: false);

        if (ImGui.Button("Back##QuickSetupFinish"))
        {
            setupStep = QuickSetupStep.Configure;
            setupMessage = string.Empty;
        }

        ImGui.SameLine();
        if (ImGui.Button("Cancel##QuickSetupFinish"))
            CancelQuickSetup();

        ImGui.SameLine();
        ImGui.BeginDisabled(setupCompletionChoice == QuickSetupCompletionChoice.None);
        if (CoppeliaUi.PrimaryButton("Finish setup"))
        {
            if (plugin.FinishQuickSetup(draft, setupCompletionChoice, out var resultMessage))
            {
                setupMessage = resultMessage;
                setupStep = QuickSetupStep.Complete;
                setupDraft = QuickSetupDraft.FromConfiguration(configuration);
                RefreshNetworkDrafts();
            }
            else
            {
                setupMessage = resultMessage;
            }
        }
        ImGui.EndDisabled();
    }

    private void DrawSetupNavigation(bool allowContinue)
    {
        ImGui.Spacing();
        if (ImGui.Button("Back##QuickSetupConfigure"))
        {
            setupStep = QuickSetupStep.ChooseMode;
            setupMessage = string.Empty;
        }

        ImGui.SameLine();
        if (ImGui.Button("Cancel##QuickSetupConfigure"))
            CancelQuickSetup();

        ImGui.SameLine();
        ImGui.BeginDisabled(!allowContinue);
        if (CoppeliaUi.PrimaryButton("Continue"))
        {
            setupCompletionChoice = QuickSetupCompletionChoice.None;
            setupMessage = string.Empty;
            setupStep = QuickSetupStep.Finish;
        }
        ImGui.EndDisabled();
    }

    private void RefreshPowerlevelReadiness()
    {
        if (setupDraft == null || DateTimeOffset.UtcNow < nextPowerlevelReadinessUtc)
            return;

        nextPowerlevelReadinessUtc = DateTimeOffset.UtcNow.AddSeconds(2);
        powerlevelReadiness = plugin.PowerlevelRuntimeService.GetSetupReadiness(setupDraft.PowerlevelJob);
    }

    private void RefreshJotReadiness()
    {
        if (DateTimeOffset.UtcNow < nextJotReadinessUtc)
            return;

        nextJotReadinessUtc = DateTimeOffset.UtcNow.AddSeconds(2);
        jotReadiness = plugin.JotRuntimeService.GetSetupReadiness();
    }

    private void StartQuickSetup()
    {
        setupDraft = QuickSetupDraft.FromConfiguration(plugin.Configuration);
        setupStep = QuickSetupStep.ChooseMode;
        setupCompletionChoice = QuickSetupCompletionChoice.None;
        setupMessage = string.Empty;
        powerlevelReadiness = null;
        nextPowerlevelReadinessUtc = DateTimeOffset.MinValue;
        jotReadiness = null;
        nextJotReadinessUtc = DateTimeOffset.MinValue;
    }

    private void CancelQuickSetup()
    {
        setupDraft = null;
        setupStep = QuickSetupStep.ChooseMode;
        setupCompletionChoice = QuickSetupCompletionChoice.None;
        setupMessage = string.Empty;
        powerlevelReadiness = null;
        jotReadiness = null;
        selectGeneralTab = true;
    }

    private void DrawGeneralSettings(Configuration configuration, ref bool changed)
    {
        ImGui.TextDisabled($"Operating role: {configuration.OperatingRole.GetLabel()}");
        var krangleEnabled = configuration.KrangleNames;
        if (ImGui.Checkbox("Krangle names", ref krangleEnabled))
        {
            configuration.KrangleNames = krangleEnabled;
            if (!krangleEnabled)
                KrangleService.ClearCache();
            changed = true;
        }

        var dependencyToasts = configuration.ShowDependencyToasts;
        if (ImGui.Checkbox("Show dependency toasts", ref dependencyToasts))
        {
            configuration.ShowDependencyToasts = dependencyToasts;
            changed = true;
        }

        ImGui.SameLine();
        var dtrEnabled = configuration.DtrBarEnabled;
        if (ImGui.Checkbox("Show DTR bar entry", ref dtrEnabled))
        {
            configuration.DtrBarEnabled = dtrEnabled;
            plugin.UpdateDtrBar();
            changed = true;
        }

        var dtrMode = configuration.DtrBarMode;
        ImGui.SetNextItemWidth(180f);
        if (ImGui.Combo("DTR mode", ref dtrMode, DtrModes, DtrModes.Length))
        {
            configuration.DtrBarMode = dtrMode;
            plugin.UpdateDtrBar();
            changed = true;
        }

        CoppeliaUi.SectionHeader("QST travel");
        var avoidTamamizu = configuration.AvoidTamamizuAetheryte;
        if (ImGui.Checkbox("Do not use Tamamizu aetheryte", ref avoidTamamizu))
        {
            configuration.AvoidTamamizuAetheryte = avoidTamamizu;
            changed = true;
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Skips Tamamizu when choosing a QST teleport destination.");

        var autoUpdateMapLocations = configuration.AutoUpdateMapLocationsOnLogin;
        if (ImGui.Checkbox("Auto-update map locations on login", ref autoUpdateMapLocations))
        {
            configuration.AutoUpdateMapLocationsOnLogin = autoUpdateMapLocations;
            changed = true;
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Refreshes LootGoblin's community map-location data once per HealBot version for nearest-aetheryte selection.");

        var networkRole = configuration.OperatingRole == OperatingRole.Off
            ? configuration.LastNonOffRole
            : configuration.OperatingRole;
        CoppeliaUi.SectionHeader(
            "Role networking",
            "Helper owns its listener port and shared secret. Newb additionally owns the Helper IPv4 address.");
        if (networkRole is OperatingRole.Helper or OperatingRole.Newb)
        {
            if (networkRole == OperatingRole.Newb)
            {
                ImGui.SetNextItemWidth(300f);
                if (ImGui.InputText("Helper IPv4 address", ref networkAddressDraft, 45))
                    networkConfirmation = string.Empty;
                if (ImGui.IsItemDeactivatedAfterEdit())
                    CommitNetworking(networkRole);
            }

            ImGui.SetNextItemWidth(160f);
            if (ImGui.InputInt("Pairing TCP port", ref networkPortDraft))
                networkConfirmation = string.Empty;
            if (ImGui.IsItemDeactivatedAfterEdit())
                CommitNetworking(networkRole);

            ImGui.SetNextItemWidth(520f);
            if (ImGui.InputText("Visible shared secret", ref networkSecretDraft, 256))
                networkConfirmation = string.Empty;
            if (ImGui.IsItemDeactivatedAfterEdit())
                CommitNetworking(networkRole);

            if (ImGui.SmallButton("Generate 64-character hex secret##ConfigPairSecret"))
            {
                networkSecretDraft = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
                CommitNetworking(networkRole);
            }
            ImGui.SameLine();
            ImGui.BeginDisabled(string.IsNullOrEmpty(networkSecretDraft));
            if (ImGui.SmallButton("Copy##ConfigPairSecret"))
                ImGui.SetClipboardText(networkSecretDraft);
            ImGui.EndDisabled();
            if (!string.IsNullOrWhiteSpace(networkConfirmation))
                CoppeliaUi.StatusText(networkConfirmation, ready: true);
            CoppeliaUi.WrappedHelp("Authenticated LAN traffic is not encrypted; character names and coordinates remain visible on the network.");
        }
        else
        {
            ImGui.TextDisabled("Select Helper or Newb to edit direct-pairing settings.");
        }

        CoppeliaUi.SectionHeader(
            "Watch filters",
            "These filters control which friendly objects appear in the HealBot/JOAT Watch window.");

        var watchPlayers = configuration.WatchPlayers;
        if (ImGui.Checkbox("Players", ref watchPlayers))
        {
            configuration.WatchPlayers = watchPlayers;
            changed = true;
        }

        ImGui.SameLine();
        var watchChocobos = configuration.WatchCompanionChocobos;
        if (ImGui.Checkbox("Companion chocobos", ref watchChocobos))
        {
            configuration.WatchCompanionChocobos = watchChocobos;
            changed = true;
        }

        var watchPartyNpcs = configuration.WatchPartyNpcs;
        if (ImGui.Checkbox("NPC party members", ref watchPartyNpcs))
        {
            configuration.WatchPartyNpcs = watchPartyNpcs;
            changed = true;
        }

        ImGui.SameLine();
        var watchBattleNpcs = configuration.WatchFriendlyBattleNpcs;
        if (ImGui.Checkbox("Friendly battle NPCs", ref watchBattleNpcs))
        {
            configuration.WatchFriendlyBattleNpcs = watchBattleNpcs;
            changed = true;
        }

        var saveHealTargets = configuration.SaveHealTargets;
        if (ImGui.Checkbox("Save heal targets", ref saveHealTargets))
        {
            configuration.SaveHealTargets = saveHealTargets;
            changed = true;
        }

        ImGui.SameLine();
        ImGui.SetNextItemWidth(170f);
        var scanRange = configuration.SavedTargetScanRangeYalms;
        if (ImGui.SliderInt("Saved target scan range", ref scanRange, 1, 200, "%d y"))
        {
            configuration.SavedTargetScanRangeYalms = scanRange;
            changed = true;
        }

        ImGui.TextDisabled($"Multi-target watch cap: {WatchTargetService.MaxTrackedTargets} active and {WatchTargetService.MaxTrackedTargets} saved targets.");
        ImGui.TextDisabled("Use the Watch window to add or remove targets. Save heal targets only persists the targets you explicitly keep watched.");
        ImGui.TextDisabled("Unticking a watched target or Ctrl-clearing the watch set removes it from the saved set too. Scan range only affects saved targets rejoining after they return.");
    }

    private void CommitNetworking(OperatingRole role)
    {
        var configuration = plugin.Configuration;
        var address = networkAddressDraft.Trim();
        var changed = configuration.LanPairingPort != networkPortDraft ||
                      !string.Equals(configuration.LanPairingSecret, networkSecretDraft, StringComparison.Ordinal) ||
                      role == OperatingRole.Newb &&
                      !string.Equals(configuration.LanHealBotAddress, address, StringComparison.Ordinal);
        if (!changed)
        {
            networkConfirmation = "Networking is already saved.";
            return;
        }

        if (role == OperatingRole.Newb)
            configuration.LanHealBotAddress = address;
        configuration.LanPairingPort = networkPortDraft;
        configuration.LanPairingSecret = networkSecretDraft;
        configuration.Save();
        plugin.HealBotPairingService.Restart();
        networkConfirmation = "Networking saved; direct pairing restarted once.";
    }

    private void RefreshNetworkDrafts()
    {
        networkAddressDraft = plugin.Configuration.LanHealBotAddress;
        networkPortDraft = plugin.Configuration.LanPairingPort;
        networkSecretDraft = plugin.Configuration.LanPairingSecret;
        networkConfirmation = string.Empty;
    }

    private void DrawJobTabsContent(Configuration configuration, ref bool changed)
    {
        ImGui.TextDisabled("Alive order: Instant BUFF -> Instant oGCD -> Casted BUFF -> Casted GCD. Dead-target prep checks instant buffs before raise.");

        if (!ImGui.BeginTabBar("CoppeliaJobTabs"))
            return;

        foreach (var (jobId, label) in JobTabs)
        {
            if (!ImGui.BeginTabItem(label))
                continue;

            var jobConfig = configuration.GetJobConfigForJob(jobId);
            var jobEnabled = jobConfig.Enabled;
            if (ImGui.Checkbox($"Enable {label}##JobEnabled{jobId}", ref jobEnabled))
            {
                jobConfig.Enabled = jobEnabled;
                changed = true;
            }

            foreach (var group in HealbotActionCatalog.ConfigGroupOrder)
            {
                DrawActionGroup(jobId, jobConfig, group, ref changed);
                ImGui.Spacing();
            }

            ImGui.EndTabItem();
        }

        ImGui.EndTabBar();
    }

    private void DrawActionGroup(uint jobId, HealerJobConfig jobConfig, HealbotActionGroup group, ref bool changed)
    {
        var definitions = HealbotActionCatalog.GetDefinitions(jobId)
            .Where(definition => definition.Group == group)
            .ToArray();

        if (definitions.Length == 0)
            return;

        ImGui.TextUnformatted(group.GetLabel());
        if (!ImGui.BeginTable(
                $"CoppeliaRules{jobId}{group}",
                8,
                ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.ScrollY,
                new Vector2(-1f, 170f)))
        {
            return;
        }

        ImGui.TableSetupColumn("On", ImGuiTableColumnFlags.WidthFixed, 42f);
        ImGui.TableSetupColumn("Priority", ImGuiTableColumnFlags.WidthFixed, 70f);
        ImGui.TableSetupColumn("Action", ImGuiTableColumnFlags.WidthStretch, 0.32f);
        ImGui.TableSetupColumn("Trigger", ImGuiTableColumnFlags.WidthFixed, 120f);
        ImGui.TableSetupColumn("HP%", ImGuiTableColumnFlags.WidthFixed, 70f);
        ImGui.TableSetupColumn("MP%", ImGuiTableColumnFlags.WidthFixed, 70f);
        ImGui.TableSetupColumn("OOC", ImGuiTableColumnFlags.WidthFixed, 54f);
        ImGui.TableSetupColumn("Need Missing Buff", ImGuiTableColumnFlags.WidthFixed, 150f);
        ImGui.TableHeadersRow();

        foreach (var definition in definitions)
        {
            var rule = jobConfig.ActionRules.First(existingRule =>
                string.Equals(existingRule.ActionName, definition.ActionName, StringComparison.OrdinalIgnoreCase));

            ImGui.TableNextRow();

            ImGui.TableSetColumnIndex(0);
            var enabled = rule.Enabled;
            if (ImGui.Checkbox($"##Enabled{jobId}{group}{rule.ActionName}", ref enabled))
            {
                rule.Enabled = enabled;
                changed = true;
            }

            ImGui.TableSetColumnIndex(1);
            ImGui.SetNextItemWidth(62f);
            var priority = rule.Priority;
            if (ImGui.InputInt($"##Priority{jobId}{group}{rule.ActionName}", ref priority))
            {
                rule.Priority = Math.Clamp(priority, 0, 999);
                changed = true;
            }

            ImGui.TableSetColumnIndex(2);
            ImGui.TextUnformatted(definition.ActionName);
            var meta = definition.TargetKind == HealbotTargetKind.Self
                ? "self"
                : "watched target";
            if (!string.IsNullOrWhiteSpace(definition.TrackedStatusName))
                meta = $"{meta} - tracks {definition.TrackedStatusName}";
            ImGui.TextDisabled(meta);

            ImGui.TableSetColumnIndex(3);
            ImGui.SetNextItemWidth(110f);
            var triggerIndex = Array.IndexOf(TriggerKinds, rule.TriggerKind);
            var triggerLabels = TriggerKinds.Select(item => item.GetLabel()).ToArray();
            if (ImGui.Combo($"##Trigger{jobId}{group}{rule.ActionName}", ref triggerIndex, triggerLabels, triggerLabels.Length) &&
                triggerIndex >= 0 &&
                triggerIndex < TriggerKinds.Length)
            {
                rule.TriggerKind = TriggerKinds[triggerIndex];
                changed = true;
            }

            ImGui.TableSetColumnIndex(4);
            ImGui.BeginDisabled(rule.TriggerKind == HealbotTriggerKind.DeadTarget);
            ImGui.SetNextItemWidth(64f);
            var hpThreshold = rule.HpThresholdPercent;
            if (ImGui.SliderInt($"##Hp{jobId}{group}{rule.ActionName}", ref hpThreshold, 0, 100, "%d"))
            {
                rule.HpThresholdPercent = hpThreshold;
                changed = true;
            }
            ImGui.EndDisabled();

            ImGui.TableSetColumnIndex(5);
            ImGui.SetNextItemWidth(64f);
            var mpThreshold = rule.MinimumMpPercent;
            if (ImGui.SliderInt($"##Mp{jobId}{group}{rule.ActionName}", ref mpThreshold, 0, 100, "%d"))
            {
                rule.MinimumMpPercent = mpThreshold;
                changed = true;
            }

            ImGui.TableSetColumnIndex(6);
            var allowOoc = rule.AllowOutOfCombat;
            if (ImGui.Checkbox($"##Ooc{jobId}{group}{rule.ActionName}", ref allowOoc))
            {
                rule.AllowOutOfCombat = allowOoc;
                changed = true;
            }

            ImGui.TableSetColumnIndex(7);
            ImGui.BeginDisabled(string.IsNullOrWhiteSpace(definition.TrackedStatusName));
            var requireMissing = rule.RequireMissingTrackedStatus;
            if (ImGui.Checkbox($"##NeedBuff{jobId}{group}{rule.ActionName}", ref requireMissing))
            {
                rule.RequireMissingTrackedStatus = requireMissing;
                changed = true;
            }
            ImGui.EndDisabled();
        }

        ImGui.EndTable();
    }

    private void DrawRequirements()
    {
        CoppeliaUi.SectionHeader("Stand-alone HealBot requirements");
        foreach (var requirement in PluginInfo.RequiredPlugins)
            ImGui.BulletText(requirement);

        CoppeliaUi.SectionHeader("HealBot optional integration");
        foreach (var recommendation in PluginInfo.RecommendedPlugins)
            ImGui.BulletText(recommendation);

        CoppeliaUi.SectionHeader("PowerlevelBot requirements");
        ImGui.BulletText("A currently equipped and unlocked BRD or MCH.");
        ImGui.BulletText("Compatible FrenRider Powerlevel IPC with FrenRider enabled.");
        ImGui.BulletText("A configured, visible Fren and no active companion chocobo.");

        CoppeliaUi.SectionHeader("Jacqueline of All Trades (JOAT) requirements");
        ImGui.BulletText("The HealBot dependencies, a supported equipped healer, an enabled healer action matrix, and an explicitly watched target.");
        ImGui.BulletText("Compatible FrenRider Powerlevel IPC with FrenRider enabled and its configured Fren visible.");
        ImGui.BulletText("The Fren is never auto-added to Watch; select it explicitly when it is the low-level heal target.");

        CoppeliaUi.SectionHeader("Helper / Newb pairing requirements");
        ImGui.BulletText("Helper needs a valid TCP port and visible shared secret; Newb additionally needs the Helper IPv4 address.");
        ImGui.BulletText("Both peers must run compatible direct-pairing protocol v2 and use the same port and shared secret.");
        ImGui.BulletText("Helper needs the HealBot/JOAT dependencies plus Lifestream and vnavmesh for paired travel.");
        ImGui.BulletText("Traffic is authenticated but not encrypted; names and coordinates remain visible on the network.");

        CoppeliaUi.SectionHeader("Commands and windows");
        ImGui.TextDisabled("HealBot supports /healbot, /hb, and /copellia with off, on, standalone, helper, newb, heal, joat (or jot), powerlevel, mini, status, config, watch, ws, and j.");
        if (ImGui.Button("Open Main##Requirements"))
            plugin.OpenMainUi();
        ImGui.SameLine();
        if (ImGui.Button("Open Watch##Requirements"))
            plugin.OpenWatchUi();
        ImGui.SameLine();
        if (ImGui.Button("Open Mini##Requirements"))
            plugin.OpenMiniUi();
        ImGui.SameLine();
        if (CoppeliaUi.PrimaryButton("Run Quick Setup##Requirements"))
            OpenQuickSetup();
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
        plugin.SaveCurrentWindowPosition(settingsWindow: true, currentPosition);
    }
}

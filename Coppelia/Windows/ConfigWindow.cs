using AethertekUI;
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
    private readonly AethertekUI.Dalamud.MaterialWindowMotion windowMotion = new();
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
        windowMotion.Prepare(this, reducedMotion: false, roundedCorners: true);
    }

    public override void PostDraw()
        { windowMotion.Restore(this); plugin.PaintWindowTitle(WindowName,UiText.F("{0} Settings",PluginInfo.DisplayName)); }

    public override void Draw()
    {
        windowMotion.DrawChrome();
        var configuration = plugin.Configuration;
        var changed = false;

        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, ImGui.GetStyle().ItemSpacing);
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
        UiGui.Title("HealBot Settings",UiText.F("{0} Settings",PluginInfo.DisplayName));
        CoppeliaUi.Brand("Settings");
        CoppeliaUi.SameLineFor("Ko-fi");
        if (UiGui.SmallButton("Ko-fi##CoppeliaConfig"))
            Process.Start(new ProcessStartInfo { FileName = PluginInfo.SupportUrl, UseShellExecute = true });

        ImGui.SameLine();
        if (UiGui.SmallButton("Discord##CoppeliaConfig"))
            Process.Start(new ProcessStartInfo { FileName = PluginInfo.DiscordUrl, UseShellExecute = true });

        ImGui.SameLine();
        if (UiGui.SmallButton("Watch##CoppeliaConfig"))
            plugin.ToggleWatchUi();

        UiGui.TextDisabled(PluginInfo.DiscordFeedbackNote);
    }

    private void DrawSettingsTabs(Configuration configuration, ref bool changed)
    {
        var quickSetupFlags = selectQuickSetupTab ? ImGuiTabItemFlags.SetSelected : ImGuiTabItemFlags.None;
        var generalFlags = selectGeneralTab ? ImGuiTabItemFlags.SetSelected : ImGuiTabItemFlags.None;
        selectQuickSetupTab = false;
        selectGeneralTab = false;

        var appearanceRoot = ImGui.GetID("");
        if (!UiGui.BeginTabBar("CoppeliaSettingsTabs",["Quick Setup","General","HealBot Actions","Window appearance","Requirements / Help"]))
            return;

        if (UiGui.BeginTabItem("Quick Setup", quickSetupFlags))
        {
            DrawQuickSetup(configuration);
            ImGui.EndTabItem();
        }

        if (UiGui.BeginTabItem("General", generalFlags))
        {
            CoppeliaUi.SectionHeader(
                "General",
                "These controls are applied immediately. Quick Setup uses a separate draft and changes nothing until Finish.");
            DrawGeneralSettings(configuration, ref changed);
            ImGui.EndTabItem();
        }

        if (UiGui.BeginTabItem("HealBot Actions"))
        {
            CoppeliaUi.SectionHeader(
                "HealBot Actions",
                "HealBot and JOAT share this per-job healing action matrix. PowerlevelBot does not use these rules.");
            DrawJobTabsContent(configuration, ref changed);
            ImGui.EndTabItem();
        }

        if (UiGui.BeginTabItem("Window appearance", ImGuiTabItemFlags.NoPushId))
        {
            ImGuiP.PushOverrideID(appearanceRoot);
            try { plugin.DrawWindowAppearanceSettings(); }
            finally { ImGui.PopID(); ImGui.EndTabItem(); }
        }
        if (UiGui.BeginTabItem("Requirements / Help"))
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
        UiGui.TextDisabled(UiText.F($"Step {visibleStep} of 3"));

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

        if (CoppeliaUi.PrimaryButton("Set up Stand-alone", new Vector2(220f * MaterialTheme.Metrics.Scale, 0)))
        {
            setupDraft!.Role = OperatingRole.StandAlone;
            if (setupDraft.Mode == BotMode.Newb)
                setupDraft.Mode = BotMode.HealBot;
            setupStep = QuickSetupStep.Configure;
            setupMessage = string.Empty;
        }
        CoppeliaUi.Tooltip("Configure the selected HealBot, JOAT, or PowerlevelBot behavior without networking.");

        CoppeliaUi.SameLineFor("Set up Helper", 80);
        if (CoppeliaUi.PrimaryButton("Set up Helper", new Vector2(220f * MaterialTheme.Metrics.Scale, 0)))
        {
            setupDraft!.Role = OperatingRole.Helper;
            setupStep = QuickSetupStep.Configure;
            setupMessage = string.Empty;
        }
        CoppeliaUi.Tooltip("Configure the authenticated listener port and visible shared secret.");

        CoppeliaUi.SameLineFor("Set up Newb", 80);
        if (CoppeliaUi.PrimaryButton("Set up Newb", new Vector2(220f * MaterialTheme.Metrics.Scale, 0)))
        {
            setupDraft!.Role = OperatingRole.Newb;
            setupStep = QuickSetupStep.Configure;
            setupMessage = string.Empty;
        }
        CoppeliaUi.Tooltip("Configure one authenticated direct-TCP HealBot endpoint. Newb never heals or attacks locally.");

        CoppeliaUi.SectionHeader("Stand-alone behavior");
        var heal = setupDraft!.Mode == BotMode.HealBot;
        if (UiGui.RadioButton("HealBot##SetupBehavior", heal))
            setupDraft.Mode = BotMode.HealBot;
        ImGui.SameLine();
        var joat = setupDraft.Mode == BotMode.Jot;
        if (UiGui.RadioButton("JOAT##SetupBehavior", joat))
            setupDraft.Mode = BotMode.Jot;
        ImGui.SameLine();
        var powerlevel = setupDraft.Mode == BotMode.PowerlevelBot;
        if (UiGui.RadioButton("PowerlevelBot##SetupBehavior", powerlevel))
            setupDraft.Mode = BotMode.PowerlevelBot;

        ImGui.Spacing();
        if (UiGui.Button("Cancel##QuickSetupChoose"))
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
        if (UiGui.Checkbox("Players##Setup", ref watchPlayers))
            draft.WatchPlayers = watchPlayers;

        CoppeliaUi.SameLineFor("Companion chocobos");
        var watchChocobos = draft.WatchCompanionChocobos;
        if (UiGui.Checkbox("Companion chocobos##Setup", ref watchChocobos))
            draft.WatchCompanionChocobos = watchChocobos;

        var watchPartyNpcs = draft.WatchPartyNpcs;
        if (UiGui.Checkbox("NPC party members##Setup", ref watchPartyNpcs))
            draft.WatchPartyNpcs = watchPartyNpcs;

        CoppeliaUi.SameLineFor("Friendly battle NPCs");
        var watchBattleNpcs = draft.WatchFriendlyBattleNpcs;
        if (UiGui.Checkbox("Friendly battle NPCs##Setup", ref watchBattleNpcs))
            draft.WatchFriendlyBattleNpcs = watchBattleNpcs;

        var saveTargets = draft.SaveHealTargets;
        if (UiGui.Checkbox("Save explicitly watched heal targets##Setup", ref saveTargets))
            draft.SaveHealTargets = saveTargets;

        ImGui.SameLine();
        ImGui.BeginDisabled(!draft.SaveHealTargets);
        ImGui.SetNextItemWidth(170f * MaterialTheme.Metrics.Scale);
        var scanRange = draft.SavedTargetScanRangeYalms;
        if (UiGui.SliderInt("Rejoin scan range##Setup", ref scanRange, 1, 200, "%d y"))
            draft.SavedTargetScanRangeYalms = scanRange;
        ImGui.EndDisabled();

        CoppeliaUi.WrappedHelp(
            "The scan range only lets a previously saved target rejoin after it returns. It never discovers or auto-selects a new target.");
        if (UiGui.Button("Open Watch window##QuickSetup"))
            plugin.OpenWatchUi();
        CoppeliaUi.Tooltip("Open the existing Watch window now. Draft filter changes apply only after Finish.");

        if (jot)
        {
            DrawQuickSetupJoatAttackMode();

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
                CoppeliaUi.StatusLine("Rotation Solver Reborn", readiness.RotationSolverLoaded, "Loaded", "Missing");
                CoppeliaUi.StatusLine("RSR control", readiness.RotationSolverControlReady, "Ready", "Not acquired");
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

            if (UiGui.SmallButton("Refresh readiness##QuickSetupJot"))
            {
                nextJotReadinessUtc = DateTimeOffset.MinValue;
                RefreshJotReadiness();
            }

            CoppeliaUi.WrappedHelp(
                "Healing wins every 900-ms decision cycle. Coppelia switches RSR off before a matching Watch rule acts, holds it off while healing is queued, blocked, or casting, and uses RSR Manual only after a genuinely idle healing decision. Paired JOAT waits in Manual at or below 30 yalms while Coppelia alone selects an engaged hostile.");
        }

        DrawSetupNavigation(allowContinue: true);
    }

    private void DrawQuickSetupJoatAttackMode()
    {
        var draft = setupDraft!;
        CoppeliaUi.SectionHeader(
            "JOAT attack mode",
            "Both choices use RSR Manual targeting. DoTs only enables only the equipped healer's damage-over-time actions; Full RSR restores the captured offensive actions and AoE mode without enabling automatic targeting.");
        ImGui.BeginDisabled(plugin.CoppeliaQstIpcService.IsJoatAttackModeQstOwned);
        if (UiGui.RadioButton("DoTs only##QuickSetupJoatAttack", !draft.JoatFullRsrRotation))
            draft.JoatFullRsrRotation = false;
        CoppeliaUi.SameLineFor("Full RSR rotation");
        if (UiGui.RadioButton("Full RSR rotation##QuickSetupJoatAttack", draft.JoatFullRsrRotation))
            draft.JoatFullRsrRotation = true;
        ImGui.EndDisabled();
        if (plugin.CoppeliaQstIpcService.IsJoatAttackModeQstOwned)
            UiGui.TextDisabled("QST currently controls the active attack mode; the saved local choice resumes after release.");
    }

    private void DrawPowerlevelSetup()
    {
        var draft = setupDraft!;
        CoppeliaUi.SectionHeader(
            "PowerlevelBot path",
            "Choose the ranged job that is already equipped. HealBot never switches gearsets and will not use the watched-target list in this mode.");

        var brdSelected = draft.PowerlevelJob == PowerlevelJob.BRD;
        if (UiGui.RadioButton("Bard (BRD)##SetupPowerlevel", brdSelected))
        {
            draft.PowerlevelJob = PowerlevelJob.BRD;
            nextPowerlevelReadinessUtc = DateTimeOffset.MinValue;
        }

        ImGui.SameLine();
        var mchSelected = draft.PowerlevelJob == PowerlevelJob.MCH;
        if (UiGui.RadioButton("Machinist (MCH)##SetupPowerlevel", mchSelected))
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

        if (UiGui.SmallButton("Refresh readiness##QuickSetupPowerlevel"))
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
            ImGui.SetNextItemWidth(320f * MaterialTheme.Metrics.Scale);
            var address = draft.LanHealBotAddress;
            if (UiGui.InputText("Helper IPv4 address##SetupNewb", ref address, 45))
                draft.LanHealBotAddress = address;
        }

        ImGui.SetNextItemWidth(160f * MaterialTheme.Metrics.Scale);
        var port = draft.LanPairingPort;
        if (UiGui.InputInt("TCP port##SetupNewb", ref port))
            draft.LanPairingPort = port;

        ImGui.SetNextItemWidth(420f * MaterialTheme.Metrics.Scale);
        var secret = draft.LanPairingSecret;
        if (UiGui.InputText("Visible shared secret##SetupNewb", ref secret, 256))
            draft.LanPairingSecret = secret;

        if (UiGui.SmallButton("Generate secret##SetupNewb"))
            draft.LanPairingSecret = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        ImGui.SameLine();
        ImGui.BeginDisabled(string.IsNullOrEmpty(draft.LanPairingSecret));
        if (UiGui.SmallButton("Copy secret##SetupNewb"))
            ImGui.SetClipboardText(draft.LanPairingSecret);
        ImGui.EndDisabled();

        if (role == OperatingRole.Helper)
            DrawQuickSetupJoatAttackMode();

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
        UiGui.TextUnformatted(UiText.F($"Role: {draft.Role.GetLabel()}"));
        if (draft.Role is OperatingRole.Helper or OperatingRole.Newb)
        {
            if (draft.Role == OperatingRole.Newb)
                UiGui.TextDisabled(UiText.F($"Helper endpoint: {draft.LanHealBotAddress}:{draft.LanPairingPort}."));
            else
                UiGui.TextDisabled(UiText.F($"Helper listener port: {draft.LanPairingPort}."));
            UiGui.TextDisabled("Authenticated direct pairing uses the visible shared secret.");
        }
        else if (draft.Mode != BotMode.PowerlevelBot)
        {
            UiGui.TextDisabled(
                UiText.F($"Filters: players {(draft.WatchPlayers ? "on" : "off")}, chocobos {(draft.WatchCompanionChocobos ? "on" : "off")}, NPC party {(draft.WatchPartyNpcs ? "on" : "off")}, friendly battle NPCs {(draft.WatchFriendlyBattleNpcs ? "on" : "off")}."));
            UiGui.TextDisabled(draft.SaveHealTargets
                ? $"Saved targets on; {draft.SavedTargetScanRangeYalms} y rejoin scan."
                : "Saved targets off.");
            if (draft.Mode == BotMode.Jot)
                UiGui.TextDisabled(UiText.F($"JOAT attack mode: {(draft.JoatFullRsrRotation ? "Full RSR rotation" : "DoTs only")}; both use RSR Manual targeting during genuinely idle healing cycles."));
        }
        else
        {
            UiGui.TextDisabled(UiText.F($"Powerlevel job: {draft.PowerlevelJob.GetLabel()}."));
            UiGui.TextDisabled("Enemy selection remains restricted to damaged enemies already engaging the Fren or local player.");
        }

        CoppeliaUi.WrappedHelp(
            "Finish must either start the selected role now or save it as the role resumed by /healbot on while remaining Off.");

        var enableNow = setupCompletionChoice == QuickSetupCompletionChoice.EnableNow;
        if (UiGui.RadioButton("Start this role now##QuickSetupFinish", enableNow))
            setupCompletionChoice = QuickSetupCompletionChoice.EnableNow;

        var leaveOff = setupCompletionChoice == QuickSetupCompletionChoice.LeaveAutomationOff;
        if (UiGui.RadioButton("Save setup and leave role Off##QuickSetupFinish", leaveOff))
            setupCompletionChoice = QuickSetupCompletionChoice.LeaveAutomationOff;

        if (!string.IsNullOrWhiteSpace(setupMessage))
            CoppeliaUi.StatusText(setupMessage, ready: false);

        if (UiGui.Button("Back##QuickSetupFinish"))
        {
            setupStep = QuickSetupStep.Configure;
            setupMessage = string.Empty;
        }

        ImGui.SameLine();
        if (UiGui.Button("Cancel##QuickSetupFinish"))
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
        if (UiGui.Button("Back##QuickSetupConfigure"))
        {
            setupStep = QuickSetupStep.ChooseMode;
            setupMessage = string.Empty;
        }

        ImGui.SameLine();
        if (UiGui.Button("Cancel##QuickSetupConfigure"))
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
        UiGui.TextDisabled(UiText.F($"Operating role: {configuration.OperatingRole.GetLabel()}"));
        var krangleEnabled = configuration.KrangleNames;
        if (UiGui.Checkbox("Krangle names", ref krangleEnabled))
        {
            configuration.KrangleNames = krangleEnabled;
            if (!krangleEnabled)
                KrangleService.ClearCache();
            changed = true;
        }

        var dependencyToasts = configuration.ShowDependencyToasts;
        if (UiGui.Checkbox("Show dependency toasts", ref dependencyToasts))
        {
            configuration.ShowDependencyToasts = dependencyToasts;
            changed = true;
        }

        ImGui.SameLine();
        var dtrEnabled = configuration.DtrBarEnabled;
        if (UiGui.Checkbox("Show DTR bar entry", ref dtrEnabled))
        {
            configuration.DtrBarEnabled = dtrEnabled;
            plugin.UpdateDtrBar();
            changed = true;
        }

        var dtrMode = configuration.DtrBarMode;
        ImGui.SetNextItemWidth(180f * MaterialTheme.Metrics.Scale);
        if (UiGui.Combo("DTR mode", ref dtrMode, DtrModes, DtrModes.Length))
        {
            configuration.DtrBarMode = dtrMode;
            plugin.UpdateDtrBar();
            changed = true;
        }

        CoppeliaUi.SectionHeader("Companion");
        var greensCount = plugin.CoppeliaCompanionService.GetGysahlGreensCount();
        UiGui.TextDisabled(greensCount.HasValue
            ? $"Gysahl Greens (NQ + HQ): {greensCount.Value}"
            : "Gysahl Greens (NQ + HQ): unavailable");

        var summonCompanion = configuration.SummonCompanionChocobo;
        ImGui.BeginDisabled(plugin.CoppeliaCompanionService.IsQstOwned);
        if (UiGui.Checkbox("Summon companion chocobo##GeneralCompanionSummon", ref summonCompanion))
        {
            configuration.SummonCompanionChocobo = summonCompanion;
            changed = true;
        }
        ImGui.EndDisabled();
        if (plugin.CoppeliaCompanionService.IsQstOwned)
        {
            UiGui.TextDisabled(UiText.F("QST controls summoning ({0}); the saved local setting resumes after release.", UiText.T(plugin.CoppeliaCompanionService.QstSummoningEnabled ? "Enabled" : "Disabled")));
        }

        var companionStance = Array.IndexOf(
            CoppeliaCompanionPolicy.StanceNames,
            CoppeliaCompanionPolicy.NormalizeStance(configuration.CompanionStance));
        ImGui.SetNextItemWidth(220f * MaterialTheme.Metrics.Scale);
        if (UiGui.Combo(
                "Companion stance",
                ref companionStance,
                CoppeliaCompanionPolicy.StanceNames,
                CoppeliaCompanionPolicy.StanceNames.Length))
        {
            configuration.CompanionStance = CoppeliaCompanionPolicy.StanceNames[companionStance];
            plugin.CoppeliaCompanionService.ApplySelectedStanceImmediately();
            changed = true;
        }

        CoppeliaUi.SectionHeader(
            "JOAT attack mode",
            "This saved local choice is used by Stand-alone JOAT and resumes after QST releases its temporary override. Both choices use RSR Manual targeting; Full RSR changes captured offensive actions and AoE, not targeting.");
        ImGui.BeginDisabled(plugin.CoppeliaQstIpcService.IsJoatAttackModeQstOwned);
        if (UiGui.RadioButton("DoTs only##GeneralJoatAttack", !configuration.JoatFullRsrRotation))
        {
            configuration.JoatFullRsrRotation = false;
            changed = true;
        }
        CoppeliaUi.SameLineFor("Full RSR rotation");
        if (UiGui.RadioButton("Full RSR rotation##GeneralJoatAttack", configuration.JoatFullRsrRotation))
        {
            configuration.JoatFullRsrRotation = true;
            changed = true;
        }
        ImGui.EndDisabled();
        if (plugin.CoppeliaQstIpcService.IsJoatAttackModeQstOwned)
        {
            UiGui.TextDisabled(UiText.F("QST controls the active mode ({0}); the saved local choice resumes after release.", UiText.T(plugin.CoppeliaQstIpcService.EffectiveJoatFullRsrRotation ? "Full RSR rotation" : "DoTs only")));
        }

        CoppeliaUi.SectionHeader("QST travel");
        var avoidTamamizu = configuration.AvoidTamamizuAetheryte;
        if (UiGui.Checkbox("Do not use Tamamizu aetheryte", ref avoidTamamizu))
        {
            configuration.AvoidTamamizuAetheryte = avoidTamamizu;
            changed = true;
        }
        if (ImGui.IsItemHovered())
            UiGui.SetTooltip("Skips Tamamizu when choosing a QST teleport destination.");

        var territoryForwardProbeSeconds = configuration.TerritoryForwardProbeSeconds;
        ImGui.SetNextItemWidth(260f * MaterialTheme.Metrics.Scale);
        if (UiGui.SliderInt(
                "Forward territory probe duration",
                ref territoryForwardProbeSeconds,
                1,
                20,
                "%d seconds"))
        {
            configuration.TerritoryForwardProbeSeconds = territoryForwardProbeSeconds;
            changed = true;
        }
        CoppeliaUi.WrappedHelp(
            "How long the Helper moves forward when its paired Newb/Quester changes territory without teleporting.");

        var autoUpdateMapLocations = configuration.AutoUpdateMapLocationsOnLogin;
        if (UiGui.Checkbox("Auto-update map locations on login", ref autoUpdateMapLocations))
        {
            configuration.AutoUpdateMapLocationsOnLogin = autoUpdateMapLocations;
            changed = true;
        }
        if (ImGui.IsItemHovered())
            UiGui.SetTooltip("Refreshes LootGoblin's community map-location data once per HealBot version for nearest-aetheryte selection.");

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
                ImGui.SetNextItemWidth(300f * MaterialTheme.Metrics.Scale);
                if (UiGui.InputText("Helper IPv4 address", ref networkAddressDraft, 45))
                    networkConfirmation = string.Empty;
                if (ImGui.IsItemDeactivatedAfterEdit())
                    CommitNetworking(networkRole);
            }

            ImGui.SetNextItemWidth(160f * MaterialTheme.Metrics.Scale);
            if (UiGui.InputInt("Pairing TCP port", ref networkPortDraft))
                networkConfirmation = string.Empty;
            if (ImGui.IsItemDeactivatedAfterEdit())
                CommitNetworking(networkRole);

            ImGui.SetNextItemWidth(520f * MaterialTheme.Metrics.Scale);
            if (UiGui.InputText("Visible shared secret", ref networkSecretDraft, 256))
                networkConfirmation = string.Empty;
            if (ImGui.IsItemDeactivatedAfterEdit())
                CommitNetworking(networkRole);

            if (UiGui.SmallButton("Generate 64-character hex secret##ConfigPairSecret"))
            {
                networkSecretDraft = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
                CommitNetworking(networkRole);
            }
            ImGui.SameLine();
            ImGui.BeginDisabled(string.IsNullOrEmpty(networkSecretDraft));
            if (UiGui.SmallButton("Copy##ConfigPairSecret"))
                ImGui.SetClipboardText(networkSecretDraft);
            ImGui.EndDisabled();
            if (!string.IsNullOrWhiteSpace(networkConfirmation))
                CoppeliaUi.StatusText(networkConfirmation, ready: true);
            CoppeliaUi.WrappedHelp("Authenticated LAN traffic is not encrypted; character names and coordinates remain visible on the network.");
        }
        else
        {
            UiGui.TextDisabled("Select Helper or Newb to edit direct-pairing settings.");
        }

        CoppeliaUi.SectionHeader(
            "Watch filters",
            "These filters control which friendly objects appear in the HealBot/JOAT Watch window.");

        var watchPlayers = configuration.WatchPlayers;
        if (UiGui.Checkbox("Players", ref watchPlayers))
        {
            configuration.WatchPlayers = watchPlayers;
            changed = true;
        }

        CoppeliaUi.SameLineFor("Companion chocobos");
        var watchChocobos = configuration.WatchCompanionChocobos;
        if (UiGui.Checkbox("Companion chocobos", ref watchChocobos))
        {
            configuration.WatchCompanionChocobos = watchChocobos;
            changed = true;
        }

        var watchPartyNpcs = configuration.WatchPartyNpcs;
        if (UiGui.Checkbox("NPC party members", ref watchPartyNpcs))
        {
            configuration.WatchPartyNpcs = watchPartyNpcs;
            changed = true;
        }

        CoppeliaUi.SameLineFor("Friendly battle NPCs");
        var watchBattleNpcs = configuration.WatchFriendlyBattleNpcs;
        if (UiGui.Checkbox("Friendly battle NPCs", ref watchBattleNpcs))
        {
            configuration.WatchFriendlyBattleNpcs = watchBattleNpcs;
            changed = true;
        }

        var saveHealTargets = configuration.SaveHealTargets;
        if (UiGui.Checkbox("Save heal targets", ref saveHealTargets))
        {
            configuration.SaveHealTargets = saveHealTargets;
            changed = true;
        }

        ImGui.SameLine();
        ImGui.SetNextItemWidth(170f * MaterialTheme.Metrics.Scale);
        var scanRange = configuration.SavedTargetScanRangeYalms;
        if (UiGui.SliderInt("Saved target scan range", ref scanRange, 1, 200, "%d y"))
        {
            configuration.SavedTargetScanRangeYalms = scanRange;
            changed = true;
        }

        UiGui.TextDisabled(UiText.F($"Multi-target watch cap: {WatchTargetService.MaxTrackedTargets} active and {WatchTargetService.MaxTrackedTargets} saved targets."));
        UiGui.TextDisabled("Use the Watch window to add or remove targets. Save heal targets only persists the targets you explicitly keep watched.");
        UiGui.TextDisabled("Unticking a watched target or Ctrl-clearing the watch set removes it from the saved set too. Scan range only affects saved targets rejoining after they return.");
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
        UiGui.TextDisabled("Alive order: Instant BUFF -> Instant oGCD -> Casted BUFF -> Casted GCD. Dead-target prep checks instant buffs before raise.");

        if (!UiGui.BeginTabBar("CoppeliaJobTabs",JobTabs.Select(tab=>tab.Item2).ToArray()))
            return;

        foreach (var (jobId, label) in JobTabs)
        {
            if (!UiGui.BeginTabItem(label))
                continue;

            var jobConfig = configuration.GetJobConfigForJob(jobId);
            var jobEnabled = jobConfig.Enabled;
            if (UiGui.Checkbox($"Enable {label}##JobEnabled{jobId}", ref jobEnabled))
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

        var scale=MaterialTheme.Metrics.Scale;
        var priorityWidth=UiGui.NumberMinimum(false)+12*scale;
        var percentWidth=UiGui.NumberMinimum(false)+12*scale;
        var triggerWidth=TriggerKinds.Max(item=>MaterialText.Measure(UiText.T(item.GetLabel())).X)+ImGui.GetFrameHeight()+2*ImGui.GetStyle().FramePadding.X+12*scale;
        var matrixWidth=(42+180+54+150)*scale+priorityWidth+2*percentWidth+triggerWidth;
        UiGui.TextUnformatted(group.GetLabel());
        if (!ImGui.BeginTable(
                $"CoppeliaRules{jobId}{group}",
                8,
                ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.ScrollY | ImGuiTableFlags.ScrollX,
                new Vector2(-1f, (CoppeliaPresentation.Compact ? 140f : 170f) * scale),Math.Max(matrixWidth,ImGui.GetContentRegionAvail().X)))
        {
            return;
        }

        ImGui.TableSetupColumn("On", ImGuiTableColumnFlags.WidthFixed, 42f * MaterialTheme.Metrics.Scale);
        ImGui.TableSetupColumn("Priority", ImGuiTableColumnFlags.WidthFixed, priorityWidth);
        ImGui.TableSetupColumn("Action", ImGuiTableColumnFlags.WidthStretch, 0.32f);
        ImGui.TableSetupColumn("Trigger", ImGuiTableColumnFlags.WidthFixed, triggerWidth);
        ImGui.TableSetupColumn("HP%", ImGuiTableColumnFlags.WidthFixed, percentWidth);
        ImGui.TableSetupColumn("MP%", ImGuiTableColumnFlags.WidthFixed, percentWidth);
        ImGui.TableSetupColumn("OOC", ImGuiTableColumnFlags.WidthFixed, 54f * MaterialTheme.Metrics.Scale);
        ImGui.TableSetupColumn("Need Missing Buff", ImGuiTableColumnFlags.WidthFixed, 150f * MaterialTheme.Metrics.Scale);
        UiGui.TableHeadersRow();

        foreach (var definition in definitions)
        {
            var rule = jobConfig.ActionRules.First(existingRule =>
                string.Equals(existingRule.ActionName, definition.ActionName, StringComparison.OrdinalIgnoreCase));

            ImGui.TableNextRow();

            ImGui.TableSetColumnIndex(0);
            var enabled = rule.Enabled;
            if (UiGui.Checkbox($"##Enabled{jobId}{group}{rule.ActionName}", ref enabled))
            {
                rule.Enabled = enabled;
                changed = true;
            }

            ImGui.TableSetColumnIndex(1);
            ImGui.SetNextItemWidth(62f * MaterialTheme.Metrics.Scale);
            var priority = rule.Priority;
            if (UiGui.InputInt($"##Priority{jobId}{group}{rule.ActionName}", ref priority))
            {
                rule.Priority = Math.Clamp(priority, 0, 999);
                changed = true;
            }

            ImGui.TableSetColumnIndex(2);
            ImGui.TextUnformatted(definition.ActionName);
            var meta = definition.TargetKind == HealbotTargetKind.Self
                ? "self"
                : "watched target";
            if(!string.IsNullOrWhiteSpace(definition.TrackedStatusName))
                UiGui.TextDisabled(UiText.F("{0} - tracks {1}",UiText.T(definition.TargetKind==HealbotTargetKind.Self?"self":"watched target"),definition.TrackedStatusName));
            else UiGui.TextDisabled(meta);

            ImGui.TableSetColumnIndex(3);
            ImGui.SetNextItemWidth(110f * MaterialTheme.Metrics.Scale);
            var triggerIndex = Array.IndexOf(TriggerKinds, rule.TriggerKind);
            var triggerLabels = TriggerKinds.Select(item => item.GetLabel()).ToArray();
            if (UiGui.Combo($"##Trigger{jobId}{group}{rule.ActionName}", ref triggerIndex, triggerLabels, triggerLabels.Length) &&
                triggerIndex >= 0 &&
                triggerIndex < TriggerKinds.Length)
            {
                rule.TriggerKind = TriggerKinds[triggerIndex];
                changed = true;
            }

            ImGui.TableSetColumnIndex(4);
            ImGui.BeginDisabled(rule.TriggerKind == HealbotTriggerKind.DeadTarget);
            ImGui.SetNextItemWidth(64f * MaterialTheme.Metrics.Scale);
            var hpThreshold = rule.HpThresholdPercent;
            if (UiGui.SliderInt($"##Hp{jobId}{group}{rule.ActionName}", ref hpThreshold, 0, 100, "%d"))
            {
                rule.HpThresholdPercent = hpThreshold;
                changed = true;
            }
            ImGui.EndDisabled();

            ImGui.TableSetColumnIndex(5);
            ImGui.SetNextItemWidth(64f * MaterialTheme.Metrics.Scale);
            var mpThreshold = rule.MinimumMpPercent;
            if (UiGui.SliderInt($"##Mp{jobId}{group}{rule.ActionName}", ref mpThreshold, 0, 100, "%d"))
            {
                rule.MinimumMpPercent = mpThreshold;
                changed = true;
            }

            ImGui.TableSetColumnIndex(6);
            var allowOoc = rule.AllowOutOfCombat;
            if (UiGui.Checkbox($"##Ooc{jobId}{group}{rule.ActionName}", ref allowOoc))
            {
                rule.AllowOutOfCombat = allowOoc;
                changed = true;
            }

            ImGui.TableSetColumnIndex(7);
            ImGui.BeginDisabled(string.IsNullOrWhiteSpace(definition.TrackedStatusName));
            var requireMissing = rule.RequireMissingTrackedStatus;
            if (UiGui.Checkbox($"##NeedBuff{jobId}{group}{rule.ActionName}", ref requireMissing))
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
            UiGui.BulletText(requirement);

        CoppeliaUi.SectionHeader("HealBot optional integration");
        foreach (var recommendation in PluginInfo.RecommendedPlugins)
            UiGui.BulletText(recommendation);

        CoppeliaUi.SectionHeader("PowerlevelBot requirements");
        UiGui.BulletText("A currently equipped and unlocked BRD or MCH.");
        UiGui.BulletText("Compatible FrenRider Powerlevel IPC with FrenRider enabled.");
        UiGui.BulletText("A configured, visible Fren and no active companion chocobo.");

        CoppeliaUi.SectionHeader("Jacqueline of All Trades (JOAT) requirements");
        UiGui.BulletText("The HealBot dependencies, a supported equipped healer, an enabled healer action matrix, and an explicitly watched target.");
        UiGui.BulletText("Loaded, working Rotation Solver Reborn; JOAT damage is executed only through RSR.");
        UiGui.BulletText("Compatible FrenRider Powerlevel IPC with FrenRider enabled and its configured Fren visible.");
        UiGui.BulletText("The Fren is never auto-added to Watch; select it explicitly when it is the low-level heal target.");

        CoppeliaUi.SectionHeader("Helper / Newb pairing requirements");
        UiGui.BulletText("Helper needs a valid TCP port and visible shared secret; Newb additionally needs the Helper IPv4 address.");
        UiGui.BulletText("Both peers must run compatible direct-pairing protocol v2 and use the same port and shared secret.");
        UiGui.BulletText("Helper needs the HealBot/JOAT dependencies plus Lifestream and vnavmesh for paired travel.");
        UiGui.BulletText("Traffic is authenticated but not encrypted; names and coordinates remain visible on the network.");

        CoppeliaUi.SectionHeader("Commands and windows");
        UiGui.TextDisabled("HealBot supports /healbot, /hb, and /copellia with off, on, standalone, helper, newb, heal, joat (or jot), powerlevel, mini, status, config, watch, ws, and j.");
        if (UiGui.Button("Open Main##Requirements"))
            plugin.OpenMainUi();
        ImGui.SameLine();
        if (UiGui.Button("Open Watch##Requirements"))
            plugin.OpenWatchUi();
        ImGui.SameLine();
        if (UiGui.Button("Open Mini##Requirements"))
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

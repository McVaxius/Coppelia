using System.Numerics;
using Coppelia.Models;
using Coppelia.Services;
using Dalamud.Game.ClientState.Conditions;
using Coppelia.Windows;
using Dalamud.Game.Command;
using Dalamud.Game.Gui.Dtr;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.IoC;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace Coppelia;

public sealed class Plugin : IDalamudPlugin
{
    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static IClientState ClientState { get; private set; } = null!;
    [PluginService] internal static IPlayerState PlayerState { get; private set; } = null!;
    [PluginService] internal static IObjectTable ObjectTable { get; private set; } = null!;
    [PluginService] internal static ITargetManager TargetManager { get; private set; } = null!;
    [PluginService] internal static ICondition Condition { get; private set; } = null!;
    [PluginService] internal static IFramework Framework { get; private set; } = null!;
    [PluginService] internal static IChatGui ChatGui { get; private set; } = null!;
    [PluginService] internal static IPartyList PartyList { get; private set; } = null!;
    [PluginService] internal static IDataManager DataManager { get; private set; } = null!;
    [PluginService] internal static IUnlockState UnlockState { get; private set; } = null!;
    [PluginService] internal static IDtrBar DtrBar { get; private set; } = null!;
    [PluginService] internal static IToastGui ToastGui { get; private set; } = null!;
    [PluginService] internal static IGameInteropProvider GameInteropProvider { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;

    private readonly MainWindow mainWindow;
    private readonly ConfigWindow configWindow;
    private readonly WatchWindow watchWindow;
    private readonly MiniWindow miniWindow;
    private IDtrBarEntry? dtrEntry;
    private DateTimeOffset nextDependencyToastUtc = DateTimeOffset.MinValue;
    private bool runtimeStartPending = true;
    private bool runtimeStarted;
    private bool pendingInitialWatchRefresh = true;

    public Plugin()
    {
        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        if (Configuration.MigrateIfNeeded())
            Configuration.Save();

        DependencyService = new DependencyService();
        WatchTargetService = new WatchTargetService();
        ActionExecutionService = new ActionExecutionService();
        MapLocationDatabase = new MapLocationDatabase(this, Log);
        MapLocationDatabase.PopulateFromTreasureSpot(DataManager);
        AetherytePositionDatabase = new AetherytePositionDatabase(this, Log);
        CoppeliaTravelService = new CoppeliaTravelService(Configuration, AetherytePositionDatabase, MapLocationDatabase);
        ActionExecutionService.SetOwnedNavigationPause(CoppeliaTravelService.PauseForAction);
        CoppeliaCompanionService = new CoppeliaCompanionService(Configuration, CoppeliaTravelService);
        CoppeliaQstIpcService = new CoppeliaQstIpcService(this, CoppeliaTravelService, CoppeliaCompanionService);
        HealBotPairingService = new HealBotPairingService(this);
        RsrIpcService = new RsrIpcService();
        FrenRiderPowerlevelIpcService = new FrenRiderPowerlevelIpcService();
        var jotFrenRiderIpcService = new FrenRiderPowerlevelIpcService();
        HealbotRuntimeService = new HealbotRuntimeService(this, DependencyService, WatchTargetService, RsrIpcService, ActionExecutionService);
        PowerlevelRuntimeService = new PowerlevelRuntimeService(this, FrenRiderPowerlevelIpcService, ActionExecutionService);
        JotRuntimeService = new JotRuntimeService(this, jotFrenRiderIpcService, RsrIpcService);

        mainWindow = new MainWindow(this);
        configWindow = new ConfigWindow(this);
        watchWindow = new WatchWindow(this);
        miniWindow = new MiniWindow(this);

        WindowSystem.AddWindow(mainWindow);
        WindowSystem.AddWindow(configWindow);
        WindowSystem.AddWindow(watchWindow);
        WindowSystem.AddWindow(miniWindow);

        RegisterCallbacks();
    }

    public Configuration Configuration { get; }
    public WindowSystem WindowSystem { get; } = new(PluginInfo.InternalName);
    internal DependencyService DependencyService { get; }
    internal WatchTargetService WatchTargetService { get; }
    internal CoppeliaQstIpcService CoppeliaQstIpcService { get; }
    internal CoppeliaTravelService CoppeliaTravelService { get; }
    internal CoppeliaCompanionService CoppeliaCompanionService { get; }
    internal HealBotPairingService HealBotPairingService { get; }
    internal RsrIpcService RsrIpcService { get; }
    internal ActionExecutionService ActionExecutionService { get; }
    internal AetherytePositionDatabase AetherytePositionDatabase { get; }
    internal MapLocationDatabase MapLocationDatabase { get; }
    internal FrenRiderPowerlevelIpcService FrenRiderPowerlevelIpcService { get; }
    internal HealbotRuntimeService HealbotRuntimeService { get; }
    internal PowerlevelRuntimeService PowerlevelRuntimeService { get; }
    internal JotRuntimeService JotRuntimeService { get; }
    internal string LastAutomationBlocker { get; private set; } = string.Empty;

    private void RegisterCallbacks()
    {
        var commandRegistered = false;
        var shortAliasRegistered = false;
        var legacyAliasRegistered = false;
        var drawRegistered = false;
        var openConfigRegistered = false;
        var openMainRegistered = false;
        var loginRegistered = false;
        var frameworkRegistered = false;

        try
        {
            commandRegistered = CommandManager.AddHandler(PluginInfo.Command, new CommandInfo(OnCommand)
            {
                HelpMessage = "Open HealBot. Use /healbot mini, config, watch, on, off, standalone, helper, newb, heal, joat, powerlevel, status, ws, or j. /healbot jot remains an alias.",
            });
            if (!commandRegistered)
                throw new InvalidOperationException($"Could not register {PluginInfo.Command}.");

            shortAliasRegistered = CommandManager.AddHandler(PluginInfo.ShortAliasCommand, new CommandInfo(OnCommand)
            {
                HelpMessage = "Short alias for /healbot.",
            });
            if (!shortAliasRegistered)
                throw new InvalidOperationException($"Could not register {PluginInfo.ShortAliasCommand}.");

            legacyAliasRegistered = CommandManager.AddHandler(PluginInfo.LegacyAliasCommand, new CommandInfo(OnCommand)
            {
                HelpMessage = "Compatibility alias for /healbot.",
            });
            if (!legacyAliasRegistered)
                throw new InvalidOperationException($"Could not register {PluginInfo.LegacyAliasCommand}.");

            PluginInterface.UiBuilder.Draw += WindowSystem.Draw;
            drawRegistered = true;
            PluginInterface.UiBuilder.OpenConfigUi += OpenConfigUi;
            openConfigRegistered = true;
            PluginInterface.UiBuilder.OpenMainUi += OpenMainUi;
            openMainRegistered = true;
            ClientState.Login += OnLogin;
            loginRegistered = true;
            Framework.Update += OnFrameworkUpdate;
            frameworkRegistered = true;

            Log.Information("[Coppelia] HealBot plugin loaded.");
        }
        catch
        {
            if (frameworkRegistered)
                Framework.Update -= OnFrameworkUpdate;
            if (loginRegistered)
                ClientState.Login -= OnLogin;
            if (openMainRegistered)
                PluginInterface.UiBuilder.OpenMainUi -= OpenMainUi;
            if (openConfigRegistered)
                PluginInterface.UiBuilder.OpenConfigUi -= OpenConfigUi;
            if (drawRegistered)
                PluginInterface.UiBuilder.Draw -= WindowSystem.Draw;
            if (legacyAliasRegistered)
                CommandManager.RemoveHandler(PluginInfo.LegacyAliasCommand);
            if (shortAliasRegistered)
                CommandManager.RemoveHandler(PluginInfo.ShortAliasCommand);
            if (commandRegistered)
                CommandManager.RemoveHandler(PluginInfo.Command);
            throw;
        }
    }

    public void Dispose()
    {
        ClientState.Login -= OnLogin;
        HealBotPairingService.Dispose();
        CoppeliaQstIpcService.Dispose();
        JotRuntimeService.Dispose();
        PowerlevelRuntimeService.Dispose();
        HealbotRuntimeService.Dispose();
        Framework.Update -= OnFrameworkUpdate;
        PluginInterface.UiBuilder.Draw -= WindowSystem.Draw;
        PluginInterface.UiBuilder.OpenConfigUi -= OpenConfigUi;
        PluginInterface.UiBuilder.OpenMainUi -= OpenMainUi;
        CommandManager.RemoveHandler(PluginInfo.Command);
        CommandManager.RemoveHandler(PluginInfo.ShortAliasCommand);
        CommandManager.RemoveHandler(PluginInfo.LegacyAliasCommand);
        WindowSystem.RemoveAllWindows();
        dtrEntry?.Remove();
        mainWindow.Dispose();
        configWindow.Dispose();
        watchWindow.Dispose();
        miniWindow.Dispose();
        Log.Information("[Coppelia] HealBot plugin unloaded.");
    }

    public bool SetHealbotEnabled(bool enabled, bool printStatus)
        => enabled
            ? SetOperatingRole(Configuration.LastNonOffRole, printStatus)
            : SetOperatingRole(OperatingRole.Off, printStatus);

    public bool SetAutomationEnabled(bool enabled, bool printStatus)
        => enabled
            ? SetOperatingRole(Configuration.LastNonOffRole, printStatus)
            : SetOperatingRole(OperatingRole.Off, printStatus);

    public bool SetOperatingRole(OperatingRole role, bool printStatus)
        => SetOperatingRole(role, behaviorOverride: null, printStatus);

    public bool SetStandaloneBehavior(BotMode mode, bool printStatus)
    {
        if (mode == BotMode.Newb)
            mode = BotMode.HealBot;
        return SetOperatingRole(OperatingRole.StandAlone, mode, printStatus);
    }

    private bool SetOperatingRole(OperatingRole role, BotMode? behaviorOverride, bool printStatus)
    {
        if (!Enum.IsDefined(role))
            role = OperatingRole.Off;

        var previousRole = Configuration.OperatingRole;
        if (previousRole == OperatingRole.Helper && role != OperatingRole.Helper)
        {
            HealBotPairingService.ReleaseForDeactivation("The Helper role changed.");
            CoppeliaQstIpcService.LeaveDirectHelperRole("The Helper role changed.");
        }
        else if (previousRole == OperatingRole.Newb && role != OperatingRole.Newb)
        {
            HealBotPairingService.ReleaseForDeactivation("The Newb role changed.");
        }

        CoppeliaQstIpcService.ReleaseQstForLocalRoleChange("The local operating role changed.");

        if (behaviorOverride.HasValue)
            Configuration.BotMode = behaviorOverride.Value;

        Configuration.OperatingRole = role;
        if (role != OperatingRole.Off)
            Configuration.LastNonOffRole = role;

        bool started;
        switch (role)
        {
            case OperatingRole.Off:
                ApplyProviderState(pluginEnabled: false, automationEnabled: false, Configuration.BotMode);
                LastAutomationBlocker = string.Empty;
                started = true;
                break;
            case OperatingRole.StandAlone:
                started = TryActivateProviderMode(Configuration.BotMode, out var standaloneBlocker);
                LastAutomationBlocker = standaloneBlocker;
                break;
            case OperatingRole.Helper:
                var claim = CoppeliaQstIpcService.BeginDirectHelperRole();
                var helperConfigurationBlocker = Configuration.GetLanPairingBlocker(OperatingRole.Helper);
                LastAutomationBlocker = !claim.Ready ? claim.Blocker : helperConfigurationBlocker;
                started = claim.Ready && string.IsNullOrWhiteSpace(helperConfigurationBlocker);
                break;
            case OperatingRole.Newb:
                ApplyProviderState(pluginEnabled: true, automationEnabled: false, Configuration.BotMode);
                started = HealBotPairingService.TryValidateNewbActivation(out var newbBlocker);
                LastAutomationBlocker = newbBlocker;
                break;
            default:
                started = false;
                LastAutomationBlocker = "Unknown operating role.";
                break;
        }

        Configuration.Save();
        if (runtimeStarted)
            UpdateDtrBar();

        if (printStatus)
            PrintStatus(started
                ? $"{role.GetLabel()} selected."
                : $"{role.GetLabel()} selected but blocked: {LastAutomationBlocker}");

        return started;
    }

    internal bool TryActivateProviderMode(BotMode mode, out string blocker)
    {
        blocker = string.Empty;
        if (mode is BotMode.HealBot or BotMode.Jot)
        {
            DependencyService.Refresh(force: true);
            if (!DependencyService.Current.IsHealbotReady)
                blocker = DependencyService.BuildMissingDependencyMessage();
            else if (mode == BotMode.Jot && !DependencyService.Current.RotationSolverLoaded)
                blocker = "Jacqueline of All Trades requires Rotation Solver Reborn for attacking.";
            else if (!HealbotRuntimeService.IsSupportedLocalJob(out _, out var jobBlocker))
                blocker = jobBlocker;
        }
        else if (mode == BotMode.PowerlevelBot &&
                 !PowerlevelRuntimeService.TryValidateActivation(out var powerlevelBlocker))
        {
            blocker = powerlevelBlocker;
        }
        else if (mode == BotMode.Newb)
        {
            blocker = "Newb is an operating role, not a Stand-alone behavior.";
        }

        if (!string.IsNullOrWhiteSpace(blocker))
        {
            ApplyProviderState(pluginEnabled: true, automationEnabled: false, mode == BotMode.Newb ? BotMode.HealBot : mode);
            return false;
        }

        ApplyProviderState(pluginEnabled: true, automationEnabled: true, mode);
        return true;
    }

    internal void ApplyProviderState(bool pluginEnabled, bool automationEnabled, BotMode mode)
    {
        Configuration.PluginEnabled = pluginEnabled;
        Configuration.AutomationEnabled = automationEnabled;
        Configuration.BotMode = mode == BotMode.Newb ? BotMode.HealBot : mode;
        Configuration.HealbotEnabled = automationEnabled && Configuration.BotMode == BotMode.HealBot;
        if (automationEnabled)
        {
            ActivateSelectedMode();
        }
        else
        {
            HealbotRuntimeService.Deactivate(pluginEnabled ? "Automation is off." : "Plugin disabled.");
            PowerlevelRuntimeService.Deactivate(pluginEnabled ? "Automation is off." : "Plugin disabled.");
            JotRuntimeService.Deactivate(pluginEnabled ? "Automation is off." : "Plugin disabled.");
        }

        Configuration.Save();
    }

    public bool SetBotMode(BotMode mode, bool printStatus)
    {
        if (mode == BotMode.Newb)
            return SetOperatingRole(OperatingRole.Newb, printStatus);
        if (Configuration.OperatingRole != OperatingRole.StandAlone)
        {
            Configuration.BotMode = mode;
            Configuration.Save();
            if (printStatus)
                PrintStatus($"Selected {mode.GetLabel()} as the Stand-alone behavior.");
            return true;
        }
        if (Configuration.BotMode == mode)
        {
            if (printStatus)
                PrintStatus($"{mode.GetLabel()} is already selected.");
            return true;
        }

        CoppeliaQstIpcService.ReleaseQstForLocalRoleChange("The Stand-alone behavior changed.");
        HealbotRuntimeService.Deactivate("Mode switched.");
        PowerlevelRuntimeService.Deactivate("Mode switched.");
        JotRuntimeService.Deactivate("Mode switched.");
        AutomationModePolicy.ApplyMode(Configuration, mode);
        var enabled = TryActivateProviderMode(mode, out var blocker);
        LastAutomationBlocker = blocker;
        UpdateDtrBar();
        if (printStatus)
            PrintStatus(enabled
                ? $"Switched the Stand-alone behavior to {mode.GetLabel()}."
                : $"Selected {mode.GetLabel()}, but Stand-alone is blocked: {blocker}");

        return enabled;
    }

    public void SetPluginEnabled(bool enabled, bool printStatus)
        => SetOperatingRole(enabled ? Configuration.LastNonOffRole : OperatingRole.Off, printStatus);

    public void ToggleMainUi()
    {
        if (!mainWindow.IsOpen)
            mainWindow.ApplySavedPosition();

        mainWindow.Toggle();
    }

    public void OpenMainUi()
    {
        if (!mainWindow.IsOpen)
            mainWindow.ApplySavedPosition();

        mainWindow.IsOpen = true;
    }

    public void ToggleConfigUi()
    {
        if (!configWindow.IsOpen)
            configWindow.ApplySavedPosition();

        configWindow.Toggle();
    }

    public void OpenConfigUi()
    {
        if (!configWindow.IsOpen)
            configWindow.ApplySavedPosition();

        configWindow.IsOpen = true;
    }

    internal void OpenQuickSetupUi()
    {
        configWindow.OpenQuickSetup();
        OpenConfigUi();
    }

    public void ToggleWatchUi()
    {
        if (!watchWindow.IsOpen)
            watchWindow.ApplySavedPosition();

        watchWindow.Toggle();
    }

    public void OpenWatchUi()
    {
        if (!watchWindow.IsOpen)
            watchWindow.ApplySavedPosition();

        watchWindow.IsOpen = true;
    }

    public void ToggleMiniUi()
    {
        if (!miniWindow.IsOpen)
            miniWindow.RefreshDrafts();
        miniWindow.Toggle();
    }

    public void OpenMiniUi()
    {
        if (!miniWindow.IsOpen)
            miniWindow.RefreshDrafts();
        miniWindow.IsOpen = true;
    }

    public void PrintStatus(string message)
    {
        ChatGui.Print($"[{PluginInfo.DisplayName}] {message}");
    }

    public string FormatDisplayName(string rawName)
        => Configuration.KrangleNames ? KrangleService.KrangleName(rawName) : rawName;

    internal bool FinishQuickSetup(
        QuickSetupDraft draft,
        QuickSetupCompletionChoice completionChoice,
        out string message)
    {
        SetOperatingRole(OperatingRole.Off, printStatus: false);
        draft.ApplyTo(Configuration);
        Configuration.LastNonOffRole = draft.Role == OperatingRole.Off
            ? OperatingRole.StandAlone
            : draft.Role;
        Configuration.SetupWizardCompleted = false;
        Configuration.Save();
        DependencyService.Refresh(force: true);
        WatchTargetService.Update(Configuration, force: true);
        UpdateDtrBar();

        if (completionChoice == QuickSetupCompletionChoice.LeaveAutomationOff)
        {
            Configuration.SetupWizardCompleted = true;
            Configuration.Save();
            message = $"{draft.Role.GetLabel()} setup saved. The operating role remains Off.";
            return true;
        }

        if (completionChoice != QuickSetupCompletionChoice.EnableNow)
        {
            message = "Choose whether to enable automation now or leave it off.";
            return false;
        }

        if (!SetOperatingRole(draft.Role, printStatus: true))
        {
            message = string.IsNullOrWhiteSpace(LastAutomationBlocker)
                ? "HealBot could not start the selected role."
                : LastAutomationBlocker;
            return false;
        }

        Configuration.SetupWizardCompleted = true;
        Configuration.Save();
        message = $"{draft.Role.GetLabel()} setup saved and started.";
        return true;
    }

    public bool TryGetSavedWindowPosition(bool settingsWindow, out SavedWindowPosition position)
    {
        position = settingsWindow
            ? Configuration.ConfigWindowPosition
            : Configuration.MainWindowPosition;

        return position.HasValue;
    }

    public void SaveCurrentWindowPosition(bool settingsWindow, Vector2 position)
    {
        var savedPosition = settingsWindow
            ? Configuration.ConfigWindowPosition
            : Configuration.MainWindowPosition;

        if (savedPosition.HasValue && Vector2.DistanceSquared(savedPosition.ToVector2(), position) < 0.25f)
            return;

        savedPosition.Set(position);
        Configuration.Save();
    }

    public void ResetCurrentWindowPositions()
    {
        Configuration.MainWindowPosition.Reset();
        Configuration.ConfigWindowPosition.Reset();
        Configuration.WatchWindowPosition.Reset();
        Configuration.Save();
        mainWindow.ApplySavedPosition();
        configWindow.ApplySavedPosition();
        watchWindow.ApplySavedPosition();
        PrintStatus("Reset HealBot window positions to 1,1.");
    }

    public void JumpMainWindowToRandomVisibleLocation()
    {
        mainWindow.QueueRandomVisibleJump();
        mainWindow.IsOpen = true;
        PrintStatus("Queued a random visible jump for the HealBot main window.");
    }

    public void ShowDependencyToast(string message)
    {
        if (!Configuration.ShowDependencyToasts)
            return;

        if (DateTimeOffset.UtcNow < nextDependencyToastUtc)
            return;

        nextDependencyToastUtc = DateTimeOffset.UtcNow.AddSeconds(10);
        ToastGui.ShowError(message);
    }

    public void UpdateDtrBar()
    {
        if (dtrEntry == null)
        {
            SetupDtrBar();
            if (dtrEntry == null)
                return;
        }

        dtrEntry.Shown = Configuration.DtrBarEnabled;
        if (!Configuration.DtrBarEnabled)
            return;

        var assignment = CoppeliaQstIpcService.GetAssignmentSnapshot();
        var state = assignment.Source == "QST"
            ? "QST paired"
            : Configuration.OperatingRole switch
        {
            OperatingRole.Off => "Off",
            OperatingRole.Helper or OperatingRole.Newb => HealBotPairingService.Snapshot.PrimaryState,
            _ when !Configuration.AutomationEnabled => "Blocked",
            _ => Configuration.BotMode == BotMode.PowerlevelBot
                    ? PowerlevelRuntimeService.LastIssuedAction
                    : Configuration.BotMode == BotMode.Jot
                        ? DependencyService.Current.IsHealbotReady
                            ? $"Heal {HealbotRuntimeService.LastIssuedAction}; attack {JotRuntimeService.LastIssuedAction}"
                            : "Blocked"
                    : DependencyService.Current.IsHealbotReady
                        ? HealbotRuntimeService.LastIssuedAction
                        : "Blocked",
        };

        var glyph = assignment.Source == "QST" || Configuration.OperatingRole != OperatingRole.Off
            ? Configuration.DtrIconEnabled
            : Configuration.DtrIconDisabled;
        var modeLabel = assignment.Source == "QST"
            ? "HELP"
            : Configuration.OperatingRole.GetDtrLabel(Configuration.BotMode);
        dtrEntry.Text = Configuration.DtrBarMode switch
        {
            1 => new SeString(new TextPayload($"{glyph} {modeLabel}")),
            2 => new SeString(new TextPayload(glyph)),
            _ => new SeString(new TextPayload($"{modeLabel}: {state}")),
        };
        dtrEntry.Tooltip = new SeString(new TextPayload($"{PluginInfo.DisplayName}: {GetSelectedModeStatus()} Click to open the main window."));
    }

    private void SetupDtrBar()
    {
        try
        {
            dtrEntry = DtrBar.Get(PluginInfo.DisplayName);
            dtrEntry.OnClick = _ => OpenMainUi();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[Coppelia] Failed to setup DTR bar.");
        }
    }

    private void OnFrameworkUpdate(IFramework framework)
    {
        if (runtimeStartPending)
        {
            runtimeStartPending = false;
            StartRuntime();
            runtimeStarted = true;
        }

        if (!runtimeStarted)
            return;

        CoppeliaQstIpcService.Update();
        HealBotPairingService.Update();
        DependencyService.Refresh();
        WatchTargetService.Update(Configuration, force: pendingInitialWatchRefresh);
        pendingInitialWatchRefresh = false;
        var healingDecision = HealbotRuntimeService.Update();
        JotRuntimeService.Update(healingDecision);
        PowerlevelRuntimeService.Update();
        CoppeliaCompanionService.Update();
        UpdateDtrBar();
    }

    private void StartRuntime()
    {
        DependencyService.Refresh(force: true);
        SetOperatingRole(Configuration.OperatingRole, printStatus: false);
        if (ClientState.IsLoggedIn)
            QueueCommunityLocationRefresh("plugin load while already logged in");
        if (Configuration.ShouldAutoOpenSetup())
            OpenQuickSetupUi();
        UpdateDtrBar();
        CoppeliaQstIpcService.Start();
    }

    private void OnLogin()
        => QueueCommunityLocationRefresh("login");

    private void QueueCommunityLocationRefresh(string reason)
    {
        if (!Configuration.AutoUpdateMapLocationsOnLogin)
            return;

        var currentVersion = typeof(Plugin).Assembly.GetName().Version?.ToString() ?? "0.0.0.0";
        if (string.Equals(
                Configuration.LastCommunityLocationsRefreshPluginVersion,
                currentVersion,
                StringComparison.Ordinal))
        {
            Log.Debug($"[Coppelia][MapLocDB] Community data already refreshed for plugin v{currentVersion}.");
            return;
        }

        _ = ObserveCommunityLocationsRefreshAsync(currentVersion, reason);
    }

    private async Task ObserveCommunityLocationsRefreshAsync(string currentVersion, string reason)
    {
        try
        {
            if (!await MapLocationDatabase.DownloadCommunityDataAsync())
                return;

            Configuration.LastCommunityLocationsRefreshPluginVersion = currentVersion;
            Configuration.Save();
            Log.Information($"[Coppelia][MapLocDB] Refreshed community data for plugin v{currentVersion} ({reason}).");
        }
        catch (Exception ex)
        {
            Log.Error(ex, $"[Coppelia][MapLocDB] Community refresh failed during {reason}.");
        }
    }

    private void OnCommand(string command, string arguments)
    {
        var trimmed = arguments.Trim();
        if (trimmed.Equals("config", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Equals("settings", StringComparison.OrdinalIgnoreCase))
        {
            ToggleConfigUi();
            return;
        }

        if (trimmed.Equals("status", StringComparison.OrdinalIgnoreCase))
        {
            PrintStatus(GetSelectedModeStatus());
            return;
        }

        if (trimmed.Equals("mini", StringComparison.OrdinalIgnoreCase))
        {
            ToggleMiniUi();
            return;
        }

        if (trimmed.Equals("heal", StringComparison.OrdinalIgnoreCase))
        {
            SetStandaloneBehavior(BotMode.HealBot, printStatus: true);
            return;
        }

        if (trimmed.Equals("powerlevel", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Equals("pl", StringComparison.OrdinalIgnoreCase))
        {
            SetStandaloneBehavior(BotMode.PowerlevelBot, printStatus: true);
            return;
        }

        if (trimmed.Equals("joat", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Equals("jot", StringComparison.OrdinalIgnoreCase))
        {
            SetStandaloneBehavior(BotMode.Jot, printStatus: true);
            return;
        }

        if (trimmed.Equals("newb", StringComparison.OrdinalIgnoreCase))
        {
            SetOperatingRole(OperatingRole.Newb, printStatus: true);
            return;
        }

        if (trimmed.Equals("helper", StringComparison.OrdinalIgnoreCase))
        {
            SetOperatingRole(OperatingRole.Helper, printStatus: true);
            return;
        }

        if (trimmed.Equals("standalone", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Equals("stand-alone", StringComparison.OrdinalIgnoreCase))
        {
            SetOperatingRole(OperatingRole.StandAlone, printStatus: true);
            return;
        }

        if (trimmed.Equals(PluginInfo.WatchCommand, StringComparison.OrdinalIgnoreCase))
        {
            ToggleWatchUi();
            return;
        }

        if (trimmed.Equals("ws", StringComparison.OrdinalIgnoreCase))
        {
            ResetCurrentWindowPositions();
            return;
        }

        if (trimmed.Equals("j", StringComparison.OrdinalIgnoreCase))
        {
            JumpMainWindowToRandomVisibleLocation();
            return;
        }

        if (trimmed.Equals("on", StringComparison.OrdinalIgnoreCase))
        {
            SetOperatingRole(Configuration.LastNonOffRole, printStatus: true);
            return;
        }

        if (trimmed.Equals("off", StringComparison.OrdinalIgnoreCase))
        {
            SetOperatingRole(OperatingRole.Off, printStatus: true);
            return;
        }

        ToggleMainUi();
    }

    private void ActivateSelectedMode()
    {
        if (Configuration.BotMode == BotMode.PowerlevelBot)
        {
            HealbotRuntimeService.Deactivate("PowerlevelBot mode selected.");
            JotRuntimeService.Deactivate("PowerlevelBot mode selected.");
            PowerlevelRuntimeService.Activate();
            return;
        }

        PowerlevelRuntimeService.Deactivate($"{Configuration.BotMode.GetLabel()} mode selected.");
        HealbotRuntimeService.Activate();
        if (Configuration.BotMode == BotMode.Jot)
            JotRuntimeService.Activate();
        else
            JotRuntimeService.Deactivate("HealBot mode selected.");
    }

    internal (string PrimaryState, string NextAction, string Identity) GetOperationalStatus()
    {
        var assignment = CoppeliaQstIpcService.GetAssignmentSnapshot();
        if (assignment.Source == "QST")
        {
            return (
                "Helper active (QST-owned)",
                "Use QST to release or deactivate this provider session.",
                $"{assignment.Name}@{assignment.WorldId}");
        }

        if (Configuration.OperatingRole is OperatingRole.Helper or OperatingRole.Newb)
        {
            var pairing = HealBotPairingService.Snapshot;
            return (pairing.PrimaryState, pairing.NextAction, pairing.Identity);
        }

        if (Configuration.OperatingRole == OperatingRole.Off)
            return ("Off", $"Use /healbot on to resume {Configuration.LastNonOffRole.GetLabel()}.", "None");

        var state = Configuration.BotMode switch
        {
            BotMode.PowerlevelBot => PowerlevelRuntimeService.StatusText,
            BotMode.Jot => $"Healing: {HealbotRuntimeService.StatusText} Attacking: {JotRuntimeService.StatusText}",
            _ => HealbotRuntimeService.StatusText,
        };
        var next = string.IsNullOrWhiteSpace(LastAutomationBlocker)
            ? state.Contains("Blocked", StringComparison.OrdinalIgnoreCase)
                ? "Resolve the blocker shown in the primary state."
                : "No action required."
            : $"Resolve: {LastAutomationBlocker}";
        var local = ObjectTable.LocalPlayer;
        var identity = local == null ? "None" : $"{local.Name.TextValue.Trim()}@{local.HomeWorld.RowId}";
        return (state, next, identity);
    }

    internal string GetSelectedModeStatus()
    {
        var status = GetOperationalStatus();
        var behavior = Configuration.OperatingRole == OperatingRole.StandAlone
            ? $" Behavior: {Configuration.BotMode.GetLabel()}."
            : string.Empty;
        return $"Role: {Configuration.OperatingRole.GetLabel()}.{behavior} State: {status.PrimaryState} Next: {status.NextAction} Identity: {status.Identity}";
    }
}

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
        CoppeliaCompanionService = new CoppeliaCompanionService(CoppeliaTravelService);
        CoppeliaQstIpcService = new CoppeliaQstIpcService(this, CoppeliaTravelService, CoppeliaCompanionService);
        HealBotPairingService = new HealBotPairingService(this);
        RsrIpcService = new RsrIpcService();
        FrenRiderPowerlevelIpcService = new FrenRiderPowerlevelIpcService();
        var jotFrenRiderIpcService = new FrenRiderPowerlevelIpcService();
        HealbotRuntimeService = new HealbotRuntimeService(this, DependencyService, WatchTargetService, RsrIpcService, ActionExecutionService);
        PowerlevelRuntimeService = new PowerlevelRuntimeService(this, FrenRiderPowerlevelIpcService, ActionExecutionService);
        JotRuntimeService = new JotRuntimeService(this, jotFrenRiderIpcService, ActionExecutionService);

        mainWindow = new MainWindow(this);
        configWindow = new ConfigWindow(this);
        watchWindow = new WatchWindow(this);
        miniWindow = new MiniWindow(this);

        WindowSystem.AddWindow(mainWindow);
        WindowSystem.AddWindow(configWindow);
        WindowSystem.AddWindow(watchWindow);
        WindowSystem.AddWindow(miniWindow);

        CommandManager.AddHandler(PluginInfo.Command, new CommandInfo(OnCommand)
        {
            HelpMessage = "Open HealBot. Use /healbot mini, config, watch, on, off, heal, joat, powerlevel, newb, status, ws, or j. /healbot jot remains an alias.",
        });

        CommandManager.AddHandler(PluginInfo.ShortAliasCommand, new CommandInfo(OnCommand)
        {
            HelpMessage = "Short alias for /healbot.",
        });

        CommandManager.AddHandler(PluginInfo.LegacyAliasCommand, new CommandInfo(OnCommand)
        {
            HelpMessage = "Compatibility alias for /healbot.",
        });

        PluginInterface.UiBuilder.Draw += WindowSystem.Draw;
        PluginInterface.UiBuilder.OpenConfigUi += OpenConfigUi;
        PluginInterface.UiBuilder.OpenMainUi += OpenMainUi;
        Framework.Update += OnFrameworkUpdate;
        ClientState.Login += OnLogin;

        DependencyService.Refresh(force: true);
        SetupDtrBar();
        UpdateDtrBar();
        if (Configuration.ShouldAutoOpenSetup())
            OpenQuickSetupUi();
        if (ClientState.IsLoggedIn)
            QueueCommunityLocationRefresh("plugin load while already logged in");

        Log.Information("[Coppelia] HealBot plugin loaded.");
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
        => SetAutomationEnabled(enabled, printStatus);

    public bool SetAutomationEnabled(bool enabled, bool printStatus)
    {
        if (enabled)
        {
            if (Configuration.BotMode == BotMode.Newb)
            {
                if (!HealBotPairingService.TryValidateNewbActivation(out var reason))
                {
                    LastAutomationBlocker = reason;
                    if (printStatus)
                        PrintStatus(reason);
                    return false;
                }
            }
            else if (Configuration.BotMode is BotMode.HealBot or BotMode.Jot)
            {
                DependencyService.Refresh(force: true);
                if (!DependencyService.Current.IsHealbotReady)
                {
                    var message = DependencyService.BuildMissingDependencyMessage();
                    LastAutomationBlocker = message;
                    ShowDependencyToast(message);
                    if (printStatus)
                        PrintStatus(message);
                    return false;
                }

                if (!HealbotRuntimeService.IsSupportedLocalJob(out _, out var reason))
                {
                    LastAutomationBlocker = reason;
                    if (printStatus)
                        PrintStatus(reason);
                    return false;
                }
            }
            else if (Configuration.BotMode == BotMode.PowerlevelBot &&
                     !PowerlevelRuntimeService.TryValidateActivation(out var reason))
            {
                LastAutomationBlocker = reason;
                if (printStatus)
                    PrintStatus(reason);
                return false;
            }

            Configuration.PluginEnabled = true;
            Configuration.AutomationEnabled = true;
            Configuration.HealbotEnabled = Configuration.BotMode == BotMode.HealBot;
            LastAutomationBlocker = string.Empty;
            Configuration.Save();
            ActivateSelectedMode();
            UpdateDtrBar();

            if (printStatus)
                PrintStatus($"{Configuration.BotMode.GetLabel()} mode enabled.");

            return true;
        }

        Configuration.AutomationEnabled = false;
        Configuration.HealbotEnabled = false;
        LastAutomationBlocker = string.Empty;
        HealBotPairingService.ReleaseForDeactivation("HealBot automation was disabled.");
        CoppeliaQstIpcService.ReleaseForDeactivation("HealBot automation was disabled.");
        Configuration.Save();
        HealbotRuntimeService.Deactivate("Automation is off.");
        PowerlevelRuntimeService.Deactivate("Automation is off.");
        JotRuntimeService.Deactivate("Automation is off.");
        UpdateDtrBar();

        if (printStatus)
            PrintStatus("HealBot automation disabled.");

        return true;
    }

    public bool SetBotMode(BotMode mode, bool printStatus)
    {
        if (Configuration.BotMode == mode)
        {
            if (printStatus)
                PrintStatus($"{mode.GetLabel()} is already selected.");
            return true;
        }

        var wasAutomationEnabled = Configuration.AutomationEnabled;
        HealBotPairingService.ReleaseForDeactivation("Automation mode changed.");
        CoppeliaQstIpcService.ReleaseForDeactivation("Automation mode changed.");
        HealbotRuntimeService.Deactivate("Mode switched.");
        PowerlevelRuntimeService.Deactivate("Mode switched.");
        JotRuntimeService.Deactivate("Mode switched.");
        if (wasAutomationEnabled && mode == BotMode.Newb)
        {
            Configuration.AutomationEnabled = false;
            Configuration.HealbotEnabled = false;
        }
        AutomationModePolicy.ApplyMode(Configuration, mode);
        Configuration.Save();
        UpdateDtrBar();

        if (!wasAutomationEnabled)
        {
            if (printStatus)
                PrintStatus($"Selected {mode.GetLabel()} mode.");
            return true;
        }

        var enabled = SetAutomationEnabled(true, printStatus: false);
        if (printStatus)
            PrintStatus(enabled
                ? $"Switched to {mode.GetLabel()} mode."
                : $"Selected {mode.GetLabel()} mode, but activation is blocked. Use /healbot status for details.");

        return enabled;
    }

    public void SetPluginEnabled(bool enabled, bool printStatus)
    {
        Configuration.PluginEnabled = enabled;
        if (!enabled)
        {
            Configuration.AutomationEnabled = false;
            Configuration.HealbotEnabled = false;
        }

        Configuration.Save();
        if (!enabled)
        {
            HealBotPairingService.ReleaseForDeactivation("HealBot was disabled.");
            CoppeliaQstIpcService.ReleaseForDeactivation("HealBot was disabled.");
            HealbotRuntimeService.Deactivate("Plugin disabled.");
            PowerlevelRuntimeService.Deactivate("Plugin disabled.");
            JotRuntimeService.Deactivate("Plugin disabled.");
        }

        UpdateDtrBar();
        if (printStatus)
            PrintStatus(enabled ? "Plugin enabled." : "Plugin disabled.");
    }

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
        SetAutomationEnabled(false, printStatus: false);
        draft.ApplyTo(Configuration);
        Configuration.SetupWizardCompleted = false;
        Configuration.Save();
        DependencyService.Refresh(force: true);
        WatchTargetService.Update(Configuration, force: true);
        UpdateDtrBar();

        if (completionChoice == QuickSetupCompletionChoice.LeaveAutomationOff)
        {
            Configuration.SetupWizardCompleted = true;
            Configuration.Save();
            message = $"{Configuration.BotMode.GetLabel()} setup saved. Automation remains off.";
            return true;
        }

        if (completionChoice != QuickSetupCompletionChoice.EnableNow)
        {
            message = "Choose whether to enable automation now or leave it off.";
            return false;
        }

        if (!SetAutomationEnabled(true, printStatus: true))
        {
            message = string.IsNullOrWhiteSpace(LastAutomationBlocker)
                ? "HealBot could not enable the selected mode."
                : LastAutomationBlocker;
            return false;
        }

        Configuration.SetupWizardCompleted = true;
        Configuration.Save();
        message = $"{Configuration.BotMode.GetLabel()} setup saved and enabled.";
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

        var state = !Configuration.PluginEnabled
            ? "Off"
            : !Configuration.AutomationEnabled
                ? "Ready"
                : Configuration.BotMode == BotMode.Newb
                    ? HealBotPairingService.ConnectionStatus
                : Configuration.BotMode == BotMode.PowerlevelBot
                    ? PowerlevelRuntimeService.LastIssuedAction
                    : Configuration.BotMode == BotMode.Jot
                        ? DependencyService.Current.IsHealbotReady
                            ? $"Heal {HealbotRuntimeService.LastIssuedAction}; attack {JotRuntimeService.LastIssuedAction}"
                            : "Blocked"
                    : DependencyService.Current.IsHealbotReady
                        ? HealbotRuntimeService.LastIssuedAction
                        : "Blocked";

        var glyph = Configuration.AutomationEnabled ? Configuration.DtrIconEnabled : Configuration.DtrIconDisabled;
        var modeLabel = Configuration.BotMode.GetDtrLabel();
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
        CoppeliaQstIpcService.Update();
        HealBotPairingService.Update();
        DependencyService.Refresh();
        WatchTargetService.Update(Configuration, force: pendingInitialWatchRefresh);
        pendingInitialWatchRefresh = false;
        var healingDecision = HealbotRuntimeService.Update();
        JotRuntimeService.Update(healingDecision);
        PowerlevelRuntimeService.Update();
        UpdateDtrBar();
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
            SetBotMode(BotMode.HealBot, printStatus: true);
            return;
        }

        if (trimmed.Equals("powerlevel", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Equals("pl", StringComparison.OrdinalIgnoreCase))
        {
            SetBotMode(BotMode.PowerlevelBot, printStatus: true);
            return;
        }

        if (trimmed.Equals("joat", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Equals("jot", StringComparison.OrdinalIgnoreCase))
        {
            SetBotMode(BotMode.Jot, printStatus: true);
            return;
        }

        if (trimmed.Equals("newb", StringComparison.OrdinalIgnoreCase))
        {
            SetBotMode(BotMode.Newb, printStatus: true);
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
            SetAutomationEnabled(true, printStatus: true);
            return;
        }

        if (trimmed.Equals("off", StringComparison.OrdinalIgnoreCase))
        {
            SetAutomationEnabled(false, printStatus: true);
            return;
        }

        ToggleMainUi();
    }

    private void ActivateSelectedMode()
    {
        if (Configuration.BotMode == BotMode.Newb)
        {
            HealbotRuntimeService.Deactivate("Newb mode performs no local healing.");
            JotRuntimeService.Deactivate("Newb mode performs no local attacking.");
            PowerlevelRuntimeService.Deactivate("Newb mode selected.");
            return;
        }

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

    internal string GetSelectedModeStatus()
        => Configuration.BotMode switch
        {
            BotMode.PowerlevelBot => PowerlevelRuntimeService.StatusText,
            BotMode.Jot => $"Healing: {HealbotRuntimeService.StatusText} Attacking: {JotRuntimeService.StatusText}",
            BotMode.Newb => $"Pairing: {HealBotPairingService.ConnectionStatus}. {HealBotPairingService.RuntimeStatus} Blocker: {(string.IsNullOrWhiteSpace(HealBotPairingService.Blocker) ? "none" : HealBotPairingService.Blocker)}",
            _ => HealbotRuntimeService.StatusText,
        };
}

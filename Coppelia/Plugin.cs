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
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;

    private readonly MainWindow mainWindow;
    private readonly ConfigWindow configWindow;
    private readonly WatchWindow watchWindow;
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
        RsrIpcService = new RsrIpcService();
        ActionExecutionService = new ActionExecutionService();
        FrenRiderPowerlevelIpcService = new FrenRiderPowerlevelIpcService();
        HealbotRuntimeService = new HealbotRuntimeService(this, DependencyService, WatchTargetService, RsrIpcService, ActionExecutionService);
        PowerlevelRuntimeService = new PowerlevelRuntimeService(this, FrenRiderPowerlevelIpcService, ActionExecutionService);

        mainWindow = new MainWindow(this);
        configWindow = new ConfigWindow(this);
        watchWindow = new WatchWindow(this);

        WindowSystem.AddWindow(mainWindow);
        WindowSystem.AddWindow(configWindow);
        WindowSystem.AddWindow(watchWindow);

        CommandManager.AddHandler(PluginInfo.Command, new CommandInfo(OnCommand)
        {
            HelpMessage = "Open Coppelia. Use /healbot config, /healbot watch, /healbot on, /healbot off, /healbot heal, /healbot powerlevel, /healbot ws, or /healbot j.",
        });

        CommandManager.AddHandler(PluginInfo.AliasCommand, new CommandInfo(OnCommand)
        {
            HelpMessage = "Alias for /healbot.",
        });

        PluginInterface.UiBuilder.Draw += WindowSystem.Draw;
        PluginInterface.UiBuilder.OpenConfigUi += OpenConfigUi;
        PluginInterface.UiBuilder.OpenMainUi += OpenMainUi;
        Framework.Update += OnFrameworkUpdate;

        DependencyService.Refresh(force: true);
        SetupDtrBar();
        UpdateDtrBar();

        Log.Information("[Coppelia] Plugin loaded.");
    }

    public Configuration Configuration { get; }
    public WindowSystem WindowSystem { get; } = new(PluginInfo.InternalName);
    internal DependencyService DependencyService { get; }
    internal WatchTargetService WatchTargetService { get; }
    internal RsrIpcService RsrIpcService { get; }
    internal ActionExecutionService ActionExecutionService { get; }
    internal FrenRiderPowerlevelIpcService FrenRiderPowerlevelIpcService { get; }
    internal HealbotRuntimeService HealbotRuntimeService { get; }
    internal PowerlevelRuntimeService PowerlevelRuntimeService { get; }

    public void Dispose()
    {
        PowerlevelRuntimeService.Dispose();
        HealbotRuntimeService.Dispose();
        Framework.Update -= OnFrameworkUpdate;
        PluginInterface.UiBuilder.Draw -= WindowSystem.Draw;
        PluginInterface.UiBuilder.OpenConfigUi -= OpenConfigUi;
        PluginInterface.UiBuilder.OpenMainUi -= OpenMainUi;
        CommandManager.RemoveHandler(PluginInfo.Command);
        CommandManager.RemoveHandler(PluginInfo.AliasCommand);
        WindowSystem.RemoveAllWindows();
        dtrEntry?.Remove();
        mainWindow.Dispose();
        configWindow.Dispose();
        watchWindow.Dispose();
        Log.Information("[Coppelia] Plugin unloaded.");
    }

    public bool SetHealbotEnabled(bool enabled, bool printStatus)
        => SetAutomationEnabled(enabled, printStatus);

    public bool SetAutomationEnabled(bool enabled, bool printStatus)
    {
        if (enabled)
        {
            if (Configuration.BotMode == BotMode.HealBot)
            {
                DependencyService.Refresh(force: true);
                if (!DependencyService.Current.IsHealbotReady)
                {
                    var message = DependencyService.BuildMissingDependencyMessage();
                    ShowDependencyToast(message);
                    if (printStatus)
                        PrintStatus(message);
                    return false;
                }

                if (!HealbotRuntimeService.IsSupportedLocalJob(out _, out var reason))
                {
                    if (printStatus)
                        PrintStatus(reason);
                    return false;
                }
            }
            else if (!PowerlevelRuntimeService.TryValidateActivation(out var reason))
            {
                if (printStatus)
                    PrintStatus(reason);
                return false;
            }

            Configuration.PluginEnabled = true;
            Configuration.AutomationEnabled = true;
            Configuration.HealbotEnabled = Configuration.BotMode == BotMode.HealBot;
            Configuration.Save();
            ActivateSelectedMode();
            UpdateDtrBar();

            if (printStatus)
                PrintStatus($"{Configuration.BotMode.GetLabel()} mode enabled.");

            return true;
        }

        Configuration.AutomationEnabled = false;
        Configuration.HealbotEnabled = false;
        Configuration.Save();
        HealbotRuntimeService.Deactivate("Automation is off.");
        PowerlevelRuntimeService.Deactivate("Automation is off.");
        UpdateDtrBar();

        if (printStatus)
            PrintStatus("Coppelia automation disabled.");

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
        HealbotRuntimeService.Deactivate("Mode switched.");
        PowerlevelRuntimeService.Deactivate("Mode switched.");
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
            HealbotRuntimeService.Deactivate("Plugin disabled.");
            PowerlevelRuntimeService.Deactivate("Plugin disabled.");
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

    public void PrintStatus(string message)
    {
        ChatGui.Print($"[{PluginInfo.DisplayName}] {message}");
    }

    public string FormatDisplayName(string rawName)
        => Configuration.KrangleNames ? KrangleService.KrangleName(rawName) : rawName;

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
        PrintStatus("Reset Coppelia window positions to 1,1.");
    }

    public void JumpMainWindowToRandomVisibleLocation()
    {
        mainWindow.QueueRandomVisibleJump();
        mainWindow.IsOpen = true;
        PrintStatus("Queued a random visible jump for the Coppelia main window.");
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
                : Configuration.BotMode == BotMode.PowerlevelBot
                    ? PowerlevelRuntimeService.LastIssuedAction
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
        dtrEntry.Tooltip = new SeString(new TextPayload($"{PluginInfo.DisplayName} {state}. Click to open the main window."));
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
        DependencyService.Refresh();
        WatchTargetService.Update(Configuration, force: pendingInitialWatchRefresh);
        pendingInitialWatchRefresh = false;
        HealbotRuntimeService.Update();
        PowerlevelRuntimeService.Update();
        UpdateDtrBar();
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
            PrintStatus(Configuration.BotMode == BotMode.PowerlevelBot
                ? PowerlevelRuntimeService.StatusText
                : HealbotRuntimeService.StatusText);
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
        if (Configuration.BotMode == BotMode.PowerlevelBot)
        {
            HealbotRuntimeService.Deactivate("PowerlevelBot mode selected.");
            PowerlevelRuntimeService.Activate();
            return;
        }

        PowerlevelRuntimeService.Deactivate("HealBot mode selected.");
        HealbotRuntimeService.Activate();
    }
}

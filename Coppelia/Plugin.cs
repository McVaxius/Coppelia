using System.Numerics;
using AethertekUI;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
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
    [PluginService] internal static ITextureProvider TextureProvider { get; private set; } = null!;
    [PluginService] internal static IUnlockState UnlockState { get; private set; } = null!;
    [PluginService] internal static IDtrBar DtrBar { get; private set; } = null!;
    [PluginService] internal static IToastGui ToastGui { get; private set; } = null!;
    [PluginService] internal static IGameInteropProvider GameInteropProvider { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;

    private readonly MainWindow mainWindow;
    private readonly ConfigWindow configWindow;
    private readonly WatchWindow watchWindow;
    private readonly MiniWindow miniWindow;
    private CoppeliaFonts uiFonts = null!;
    private UiText uiText = null!;
    private readonly AethertekUI.Dalamud.MaterialTextHost shapedText;
    private MaterialTheme uiTheme = null!;
    private readonly MaterialWindowFold fontStatusFold = new();
    private readonly MaterialWindowDecorations fontStatusDecorations = new();
    private readonly Dictionary<string, MaterialWindowOpacity> windowOpacities = new();
    private MaterialOptions<string> languageOptions = null!;
    private string appliedLanguage = string.Empty;
    private uint appliedAccent;
    private Vector3 accentDraft;
    private int checkedFontGeneration = -1;
    private bool fontIssueLogged;
    private IDtrBarEntry? dtrEntry;
    private DateTimeOffset nextDependencyToastUtc = DateTimeOffset.MinValue;
    private bool runtimeStartPending = true;
    private bool runtimeStarted;
    private bool pendingInitialWatchRefresh = true;

    public Plugin()
    {
        shapedText = new(TextureProvider);
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

            PluginInterface.UiBuilder.Draw += DrawUi;
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
                PluginInterface.UiBuilder.Draw -= DrawUi;
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
        PluginInterface.UiBuilder.Draw -= DrawUi;
        uiFonts?.Dispose();
        uiText?.Dispose();
        shapedText.Dispose();
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

    private void DrawUi()
    {
        using var shaping=shapedText.Push();
        ApplyAppearance();
        using var text = uiText.Enter();
        if (!uiFonts.Ready)
        {
            if (!fontIssueLogged && uiFonts.LoadException is { } error)
            { Log.Error(error, "[Coppelia] Required UI fonts failed to load."); fontIssueLogged = true; }
            DrawFontStatus(uiFonts.LoadException is null);
            return;
        }
        if (checkedFontGeneration != uiFonts.Generation)
        {
            try
            {
                var generation = uiFonts.Generation;
                uiFonts.CheckGlyphs(uiText.RequiredText);
                checkedFontGeneration = generation;
            }
            catch (Exception error)
            {
                if (!fontIssueLogged) { Log.Error(error, "[Coppelia] Required UI glyph coverage failed."); fontIssueLogged = true; }
                DrawFontStatus(false); return;
            }
        }
        CoppeliaPresentation.Compact = Configuration.UiCompact;
        using var theme = MaterialTheme.Push(uiTheme, ImGuiHelpers.GlobalScale, MaterialStyleMode.ColorsOnly);
        using var geometry = new MaterialStyleScope();
        var scale = ImGuiHelpers.GlobalScale;
        geometry.Style(ImGuiStyleVar.WindowPadding, new Vector2(Configuration.UiCompact ? 10 : 14) * scale);
        geometry.Style(ImGuiStyleVar.FramePadding, new Vector2(Configuration.UiCompact ? 6 : 8, Configuration.UiCompact ? 3 : 5) * scale);
        geometry.Style(ImGuiStyleVar.ItemSpacing, new Vector2(Configuration.UiCompact ? 6 : 10, Configuration.UiCompact ? 4 : 6) * scale);
        geometry.Style(ImGuiStyleVar.CellPadding, new Vector2(Configuration.UiCompact ? 6 : 10, Configuration.UiCompact ? 4 : 6) * scale);
        geometry.Style(ImGuiStyleVar.FrameRounding, 4 * scale);
        geometry.Style(ImGuiStyleVar.ChildRounding, 4 * scale);
        geometry.Style(ImGuiStyleVar.FrameBorderSize, scale);
        using var font = uiFonts.Push(UiFontRole.Body);
        using var chrome = MaterialWindowChrome.Push();
        WindowSystem.Draw();
        foreach (var window in WindowSystem.Windows)
            if (window.IsOpen) ApplyWindowOpacity(window.WindowName);
    }

    private void DrawFontStatus(bool loading)
    {
        using var statusPalette = MaterialTheme.Push(uiTheme, ImGuiHelpers.GlobalScale, MaterialStyleMode.ColorsOnly);
        using var statusChrome = MaterialWindowChrome.Push();
        ImGui.SetNextWindowSize(new Vector2(460 * ImGuiHelpers.GlobalScale, 0), ImGuiCond.Always);
        fontStatusFold.PreDraw("HealBot##FontStatus", null, null, reducedMotion: false,
            prepareDecorations: fontStatusDecorations.Prepare);
        try
        {
            if (ImGui.Begin("HealBot##FontStatus", ImGuiWindowFlags.AlwaysAutoResize))
            {
                fontStatusDecorations.Paint();
                MaterialText.TextWrapped(UiText.T(loading ? "Loading UI fonts..." : "UI fonts failed to load. See the plugin log."));
            }
        }
        finally
        {
            ImGui.End();
            fontStatusDecorations.Paint();
            fontStatusFold.PostDraw();
            ApplyWindowOpacity("HealBot##FontStatus");
        }
    }

    private void ApplyAppearance()
    {
        var language = UiText.Languages.Any(l => l.Code == Configuration.UiLanguage) ? Configuration.UiLanguage : "en";
        if (language != appliedLanguage)
        {
            uiFonts?.Dispose(); uiText?.Dispose();
            uiText = new(language, role => uiFonts!.Push(role));
            uiFonts = new(PluginInterface.UiBuilder.FontAtlas, uiText.GlyphRanges(), language) { ShapedText=shapedText.Renderer };
            languageOptions = new(UiText.Languages.Select(l => new MaterialOption<string>(l.Code, l.Code, l.Name)).ToArray());
            appliedLanguage = language; checkedFontGeneration = -1; fontIssueLogged = false;
        }
        if (uiTheme is null || appliedAccent != (Configuration.UiAccentRgb & 0xFFFFFF))
        {
            appliedAccent = Configuration.UiAccentRgb & 0xFFFFFF;
            uiTheme = CoppeliaPresentation.Theme(appliedAccent);
            var rgb = CoppeliaPresentation.Rgb(appliedAccent); accentDraft = new(rgb.X, rgb.Y, rgb.Z);
        }
    }

    internal void DrawAppearanceSelector(bool includeAccent = true)
    {
        var language = appliedLanguage;
        using var font = UiText.Font(UiFontRole.Action);
        using var controls = MaterialControls.Push(CoppeliaPresentation.Controls(CoppeliaPresentation.ActionHeight, 18));
        var changed = includeAccent
            ? MaterialAppearanceSelector.Draw("appearance", ref accentDraft, ref language, languageOptions,
                new(UiText.T("Color"), UiText.T("Language"), UiText.T("Teal"), UiText.T("Blue"), UiText.T("Pink"), UiText.T("Custom RGB")), languageWidth: 130)
            : new MaterialAppearanceChange(false, MaterialAppearanceSelector.DrawLanguage("appearance", ref language, languageOptions, 130));
        if (changed.AccentChanged) Configuration.UiAccentRgb = ((uint)Math.Clamp((int)MathF.Round(accentDraft.X * 255), 0, 255) << 16)
            | ((uint)Math.Clamp((int)MathF.Round(accentDraft.Y * 255), 0, 255) << 8) | (uint)Math.Clamp((int)MathF.Round(accentDraft.Z * 255), 0, 255);
        if (changed.LanguageChanged) Configuration.UiLanguage = language;
        if (changed.AccentChanged || changed.LanguageChanged) Configuration.Save();
    }

    internal void DrawCompactPreference()
    {
        var compact = Configuration.UiCompact;
        if (ImGui.Checkbox("C##coppelia-compact", ref compact)) { Configuration.UiCompact = compact; Configuration.Save(); }
        if (ImGui.IsItemHovered()) UiGui.SetTooltip("Compact mode");
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
                // Loading an already-Off role is not an explicit Off request.
                if (runtimeStarted)
                    RsrIpcService.RestoreHealingAfterOff();
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
        if (miniWindow.IsOpen && miniWindow.IsCloseProtected)
            return;

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

        // DTR is presentation text too; keep command and log status strings in their original form.
        using var localization = uiText?.Enter();
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
        // The game-owned DTR cannot use the plugin's Devanagari renderer.
        if (uiText is not null && uiText.Language != "hi") state = UiText.T(state);
        dtrEntry.Text = Configuration.DtrBarMode switch
        {
            1 => new SeString(new TextPayload($"{glyph} {modeLabel}")),
            2 => new SeString(new TextPayload(glyph)),
            _ => new SeString(new TextPayload($"{modeLabel}: {state}")),
        };
        var tooltip = uiText is null || uiText.Language == "hi"
            ? $"{PluginInfo.DisplayName}: {GetSelectedModeStatus()} Click to open the main window."
            : UiText.F("{0}: {1} Click to open the main window.", PluginInfo.DisplayName, GetSelectedModeStatus());
        dtrEntry.Tooltip = new SeString(new TextPayload(tooltip));
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

    private void ApplyWindowOpacity(string windowName)
    {
        if (!windowOpacities.TryGetValue(windowName, out var opacity))
            windowOpacities.Add(windowName, opacity = new MaterialWindowOpacity());
        opacity.Apply(windowName, Configuration.UiWindowOpacityPercent / 100f,
            Configuration.UiTransparencyEnabled, Configuration.UiAutoFade,
            Configuration.UiFadedOpacityPercent / 100f, Configuration.UiUnfocusedDelaySeconds);
    }

    internal void DrawTransparencyToggle()
    {
        var enabled = Configuration.UiTransparencyEnabled;
        if (UiGui.Checkbox("Transparency##MainWindow", ref enabled))
        { Configuration.UiTransparencyEnabled = enabled; Configuration.Save(); }
    }

    internal void PaintWindowTitle(string name,string display)
        => UiGui.PaintWindowTitle(name,display,shapedText.Renderer);

    internal void DrawWindowAppearanceSettings()
    {
        if (!UiGui.CollapsingHeader("Window appearance###UiWindowAppearance")) return;
        DrawCompactPreference();
        ImGui.SameLine();
        MaterialText.Text(UiText.T("Compact mode"));
        DrawAppearanceSelector();
        var compactVisible = Configuration.UiCompactVisibleOnMainWindow;
        if (UiGui.Checkbox("Compact visible on main window", ref compactVisible))
        { Configuration.UiCompactVisibleOnMainWindow = compactVisible; Configuration.Save(); }
        var languageVisible = Configuration.UiLanguageVisibleOnMainWindow;
        if (UiGui.Checkbox("Language visible on main window", ref languageVisible))
        { Configuration.UiLanguageVisibleOnMainWindow = languageVisible; Configuration.Save(); }
        var enabled = Configuration.UiTransparencyEnabled;
        if (UiGui.Checkbox("Transparency", ref enabled))
        { Configuration.UiTransparencyEnabled = enabled; Configuration.Save(); }
        MaterialText.Text(UiText.T("Opacity (%)"));
        ImGui.SetNextItemWidth(MaterialLayout.FitNextItemWidth(160 * MaterialTheme.Metrics.Scale, 80 * MaterialTheme.Metrics.Scale));
        var normalOpacity = Configuration.UiWindowOpacityPercent;
        if (ImGui.InputInt("##UiWindowOpacityPercent", ref normalOpacity))
        { Configuration.UiWindowOpacityPercent = normalOpacity; Configuration.Save(); }
        var autoFade = Configuration.UiAutoFade;
        if (UiGui.Checkbox("Auto-fade when unfocused", ref autoFade))
        { Configuration.UiAutoFade = autoFade; Configuration.Save(); }
        ImGui.BeginDisabled(!autoFade);
        MaterialText.Text(UiText.T("Unfocused opacity (%)"));
        ImGui.SetNextItemWidth(MaterialLayout.FitNextItemWidth(160 * MaterialTheme.Metrics.Scale, 80 * MaterialTheme.Metrics.Scale));
        var fadedOpacity = Configuration.UiFadedOpacityPercent;
        if (ImGui.InputInt("##UiFadedOpacityPercent", ref fadedOpacity))
        { Configuration.UiFadedOpacityPercent = fadedOpacity; Configuration.Save(); }
        MaterialText.Text(UiText.T("Unfocused delay (seconds)"));
        ImGui.SetNextItemWidth(MaterialLayout.FitNextItemWidth(160 * MaterialTheme.Metrics.Scale, 80 * MaterialTheme.Metrics.Scale));
        var delay = Configuration.UiUnfocusedDelaySeconds;
        if (ImGui.InputInt("##UiUnfocusedDelaySeconds", ref delay))
        { Configuration.UiUnfocusedDelaySeconds = delay; Configuration.Save(); }
        ImGui.EndDisabled();
    }
}

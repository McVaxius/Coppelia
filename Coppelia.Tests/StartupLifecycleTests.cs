using System.Runtime.CompilerServices;

namespace Coppelia.Tests;

public sealed class StartupLifecycleTests
{
    [Fact]
    public void ConstructorsDoNotReadLiveStateOrStartRuntimeBehavior()
    {
        var pluginConstructor = ExtractBlock(ReadSource("Coppelia", "Plugin.cs"), "public Plugin()");
        var qstConstructor = ExtractBlock(
            ReadSource("Coppelia", "Services", "CoppeliaQstIpcService.cs"),
            "public CoppeliaQstIpcService(");
        var constructors = pluginConstructor + qstConstructor;

        Assert.DoesNotContain("ObjectTable.LocalPlayer", constructors, StringComparison.Ordinal);
        Assert.DoesNotContain("ClientState.IsLoggedIn", constructors, StringComparison.Ordinal);
        Assert.DoesNotContain("Plugin.Condition[", constructors, StringComparison.Ordinal);
        Assert.DoesNotContain("DependencyService.Refresh", constructors, StringComparison.Ordinal);
        Assert.DoesNotContain("SetOperatingRole(", constructors, StringComparison.Ordinal);
        Assert.DoesNotContain("ApplyProviderState(", constructors, StringComparison.Ordinal);
        Assert.DoesNotContain("ActivateSelectedMode(", constructors, StringComparison.Ordinal);
        Assert.DoesNotContain("SetupDtrBar()", constructors, StringComparison.Ordinal);
        Assert.DoesNotContain("UpdateDtrBar()", constructors, StringComparison.Ordinal);
        Assert.DoesNotContain("HealBotPairingService.Update()", constructors, StringComparison.Ordinal);
        Assert.DoesNotContain("CoppeliaQstIpcService.Start()", constructors, StringComparison.Ordinal);
        Assert.DoesNotContain(".RegisterFunc(", constructors, StringComparison.Ordinal);
    }

    [Fact]
    public void QstProvidersAreRegisteredOnlyByDeferredStartup()
    {
        var pluginSource = ReadSource("Coppelia", "Plugin.cs");
        var qstSource = ReadSource("Coppelia", "Services", "CoppeliaQstIpcService.cs");
        var frameworkUpdate = ExtractBlock(pluginSource, "private void OnFrameworkUpdate(");
        var runtimeStart = ExtractBlock(pluginSource, "private void StartRuntime()");
        var qstStart = ExtractBlock(qstSource, "public void Start()");

        Assert.Contains("StartRuntime();", frameworkUpdate, StringComparison.Ordinal);
        Assert.Contains("CoppeliaQstIpcService.Start();", runtimeStart, StringComparison.Ordinal);
        Assert.Equal(4, Count(qstSource, ".RegisterFunc("));
        Assert.Equal(4, Count(qstStart, ".RegisterFunc("));
        Assert.Contains("Plugin.Condition[ConditionFlag.BoundByDuty]", qstStart, StringComparison.Ordinal);
        AssertInOrder(
            runtimeStart,
            "DependencyService.Refresh(force: true);",
            "SetOperatingRole(Configuration.OperatingRole, printStatus: false);",
            "ClientState.IsLoggedIn",
            "Configuration.ShouldAutoOpenSetup()",
            "UpdateDtrBar();",
            "CoppeliaQstIpcService.Start();");
        AssertInOrder(
            frameworkUpdate,
            "StartRuntime();",
            "CoppeliaQstIpcService.Update();",
            "HealBotPairingService.Update();",
            "DependencyService.Refresh();",
            "WatchTargetService.Update(",
            "HealbotRuntimeService.Update();",
            "JotRuntimeService.Update(healingDecision);",
            "PowerlevelRuntimeService.Update();",
            "CoppeliaCompanionService.Update();",
            "UpdateDtrBar();");
    }

    [Fact]
    public void NormalQstDisposalUnregistersEveryProvider()
    {
        var qstSource = ReadSource("Coppelia", "Services", "CoppeliaQstIpcService.cs");
        var dispose = ExtractBlock(qstSource, "public void Dispose()");
        var unregister = ExtractBlock(qstSource, "private void UnregisterProviders()");

        Assert.Contains("UnregisterProviders();", dispose, StringComparison.Ordinal);
        Assert.Equal(4, Count(unregister, ".UnregisterFunc();"));
        Assert.Contains("joatFullRsrRotationProvider.UnregisterFunc();", unregister, StringComparison.Ordinal);
        Assert.Contains("companionSummoningProvider.UnregisterFunc();", unregister, StringComparison.Ordinal);
        Assert.Contains("commandProvider.UnregisterFunc();", unregister, StringComparison.Ordinal);
        Assert.Contains("statusProvider.UnregisterFunc();", unregister, StringComparison.Ordinal);
    }

    [Fact]
    public void PartialQstStartupRollsBackOnlyRegisteredProviders()
    {
        var qstSource = ReadSource("Coppelia", "Services", "CoppeliaQstIpcService.cs");
        var start = ExtractBlock(qstSource, "public void Start()");
        var unregister = ExtractBlock(qstSource, "private void UnregisterProviders()");

        Assert.Contains("catch", start, StringComparison.Ordinal);
        Assert.Contains("UnregisterProviders();", start, StringComparison.Ordinal);
        AssertRegistrationIsTracked(start, "statusProvider", "statusProviderRegistered");
        AssertRegistrationIsTracked(start, "commandProvider", "commandProviderRegistered");
        AssertRegistrationIsTracked(start, "companionSummoningProvider", "companionSummoningProviderRegistered");
        AssertRegistrationIsTracked(start, "joatFullRsrRotationProvider", "joatFullRsrRotationProviderRegistered");
        Assert.Contains("if (statusProviderRegistered)", unregister, StringComparison.Ordinal);
        Assert.Contains("if (commandProviderRegistered)", unregister, StringComparison.Ordinal);
        Assert.Contains("if (companionSummoningProviderRegistered)", unregister, StringComparison.Ordinal);
        Assert.Contains("if (joatFullRsrRotationProviderRegistered)", unregister, StringComparison.Ordinal);
    }

    [Fact]
    public void PluginCallbacksRegisterTransactionallyWithFrameworkLast()
    {
        var pluginSource = ReadSource("Coppelia", "Plugin.cs");
        var constructor = ExtractBlock(pluginSource, "public Plugin()");
        var registration = ExtractBlock(pluginSource, "private void RegisterCallbacks()");
        var frameworkRegistration = registration.IndexOf("Framework.Update += OnFrameworkUpdate;", StringComparison.Ordinal);

        Assert.Contains("RegisterCallbacks();", constructor, StringComparison.Ordinal);
        Assert.Contains("catch", registration, StringComparison.Ordinal);
        Assert.Contains("Framework.Update -= OnFrameworkUpdate;", registration, StringComparison.Ordinal);
        Assert.Contains("PluginInterface.UiBuilder.Draw -= WindowSystem.Draw;", registration, StringComparison.Ordinal);
        Assert.Contains("CommandManager.RemoveHandler(PluginInfo.Command);", registration, StringComparison.Ordinal);
        Assert.True(frameworkRegistration > registration.IndexOf("ClientState.Login += OnLogin;", StringComparison.Ordinal));
        Assert.True(frameworkRegistration > registration.IndexOf("PluginInterface.UiBuilder.OpenMainUi += OpenMainUi;", StringComparison.Ordinal));
        Assert.Equal(1, Count(registration, "Framework.Update += OnFrameworkUpdate;"));
    }

    private static void AssertRegistrationIsTracked(string start, string provider, string registrationFlag)
    {
        var registration = start.IndexOf($"{provider}.RegisterFunc(", StringComparison.Ordinal);
        var tracked = start.IndexOf($"{registrationFlag} = true;", StringComparison.Ordinal);

        Assert.True(registration >= 0);
        Assert.True(tracked > registration);
    }

    private static void AssertInOrder(string source, params string[] values)
    {
        var previous = -1;
        foreach (var value in values)
        {
            var current = source.IndexOf(value, previous + 1, StringComparison.Ordinal);
            Assert.True(current > previous, $"Could not find '{value}' in the expected order.");
            previous = current;
        }
    }

    private static string ReadSource(params string[] relativePath)
    {
        var repoRoot = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(GetThisFilePath())!, ".."));
        return File.ReadAllText(Path.Combine([repoRoot, .. relativePath]));
    }

    private static string GetThisFilePath([CallerFilePath] string path = "") => path;

    private static string ExtractBlock(string source, string marker)
    {
        var start = source.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Could not find source marker '{marker}'.");
        var openingBrace = source.IndexOf('{', start);
        Assert.True(openingBrace >= 0, $"Could not find opening brace for '{marker}'.");

        var depth = 0;
        for (var index = openingBrace; index < source.Length; index++)
        {
            if (source[index] == '{')
                depth++;
            else if (source[index] == '}' && --depth == 0)
                return source[start..(index + 1)];
        }

        throw new InvalidOperationException($"Could not find closing brace for '{marker}'.");
    }

    private static int Count(string source, string value)
        => source.Split(value, StringSplitOptions.None).Length - 1;
}

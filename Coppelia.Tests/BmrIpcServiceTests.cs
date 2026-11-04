using Coppelia.Services;
using Coppelia.Models;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Coppelia.Tests;

public sealed class BmrIpcServiceTests
{
    [Theory]
    [InlineData(24u, false, "DoTs-only")]
    [InlineData(28u, false, "DoTs-only")]
    [InlineData(33u, false, "DoTs-only")]
    [InlineData(40u, false, "DoTs-only")]
    public void DotsOnlyNeverSubstitutesFullDhogOrChangesProviderState(uint job, bool full, string reason)
    {
        var fixture = new Fixture { Active = "Previous" };
        var previous = fixture.Presets["Previous"];
        var service = fixture.Create();
        Assert.False(service.HasDamageCapability(job, full, out var blocker));
        Assert.Contains(reason, blocker, StringComparison.Ordinal);
        Assert.False(service.PrepareDamage(job, full));
        Assert.False(service.HasSessionSnapshot);
        Assert.Equal("Previous", fixture.Active);
        Assert.Equal(previous, fixture.Presets["Previous"]);
        Assert.False(fixture.Disabled);
        Assert.DoesNotContain(fixture.Presets.Keys, name => name.StartsWith("Coppelia JOAT ", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(24u)]
    [InlineData(28u)]
    [InlineData(33u)]
    [InlineData(40u)]
    public void FullDhogUsesManualTargetingAndNativeHealingOnlyWithinTheOwnedSession(uint job)
    {
        var fixture = new Fixture { Active = "Previous" };
        var previous = fixture.Presets["Previous"];
        var service = fixture.Create();
        Assert.True(service.HasDamageCapability(job, true, out _));
        Assert.True(service.PrepareDamage(job, true), service.LastFailure);
        Assert.True(fixture.Disabled);
        var created = Assert.Single(fixture.Presets.Keys, name => name.StartsWith("Coppelia JOAT ", StringComparison.Ordinal));
        using var document = JsonDocument.Parse(fixture.Presets[created]);
        var choices = document.RootElement.GetProperty("Modules").GetProperty(BmrJoatPresetContract.ModuleName(job)!)
            .EnumerateArray().ToDictionary(value => value.GetProperty("Track").GetString()!, value => value.GetProperty("Option").GetString()!);
        Assert.Equal("Manual", choices["Targeting"]);
        Assert.Equal("ForceST", choices["AOE"]);
        Assert.Equal("Enabled", choices["DhogHeal"]);
        Assert.Equal("SwiftThenSafe", choices["DhogRaise"]);
        Assert.Equal("Nearby", choices["DhogRecipients"]);
        Assert.True(service.Resume(), service.LastFailure);
        Assert.Equal(created, fixture.Active);
        Assert.True(service.Hold());
        Assert.True(fixture.Disabled);
        Assert.True(service.RestoreSessionSnapshot(), service.LastFailure);
        Assert.Equal("Previous", fixture.Active);
        Assert.Equal(previous, fixture.Presets["Previous"]);
        Assert.False(fixture.Presets.ContainsKey(created));
    }

    [Fact]
    public void DroppedManualTargetStrategyAndMissingOptionsCannotEnableFullBmr()
    {
        var missing = new Fixture();
        missing.Options.Remove("Targeting");
        Assert.False(missing.Create().HasDamageCapability(24, true, out _));
        var fixture = new Fixture { Active = "Previous", DropManualTargeting = true };
        var service = fixture.Create();
        Assert.False(service.PrepareDamage(24, true));
        Assert.False(service.Resume());
        Assert.True(fixture.Disabled);
        Assert.True(service.RestoreSessionSnapshot());
        Assert.Equal("Previous", fixture.Active);
    }

    [Fact]
    public void IdleFullBmrCanHealWithoutSelectingAnEnemyAndYieldsToQueuedWatchHealing()
    {
        var fixture = new Fixture { Active = "Previous" };
        var bmr = fixture.Create();
        var fr = new FrenRiderPowerlevelIpcService((_, _) => JsonSerializer.Serialize(
            new FrenRiderPowerlevelResponse(true, "active", CombatSuppressed: true),
            FrenRiderPowerlevelContract.JsonOptions));
        var (plugin, healbot, runtime) = CreateRuntime(bmr, fr);
        plugin.Configuration.PluginEnabled = plugin.Configuration.AutomationEnabled = true;
        Assert.True(fr.Acquire(out _));
        Assert.True(bmr.PrepareDamage(24, true));
        SetField(healbot, "rotationIsolationApplied", true);
        SetField(healbot, "bmrDamageReady", true);
        SetField(healbot, "ownedProvider", JoatRotationProvider.Bmr);
        SetField(runtime, "armed", true);

        var friendly = DispatchProxy.Create<Dalamud.Game.ClientState.Objects.Types.ICharacter,
            HealRiderTransportTests.TransportProxy>();
        ((HealRiderTransportTests.TransportProxy)(object)friendly).Handler = (method, _) =>
            method.Name == "get_GameObjectId" ? 99UL : null;
        Dalamud.Game.ClientState.Objects.Types.IGameObject? selected = null;
        var targets = DispatchProxy.Create<Dalamud.Plugin.Services.ITargetManager,
            HealRiderTransportTests.TransportProxy>();
        ((HealRiderTransportTests.TransportProxy)(object)targets).Handler = (method, args) =>
        {
            if (method.Name == "set_Target") selected = (Dalamud.Game.ClientState.Objects.Types.IGameObject?)args![0];
            return method.Name == "get_Target" ? selected : null;
        };
        var targetProperty = typeof(Plugin).GetProperty("TargetManager", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)!;
        var previousTargets = targetProperty.GetValue(null);
        try
        {
            targetProperty.SetValue(null, targets);
            typeof(JotRuntimeService).GetMethod("ResumeIdleBmr", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(runtime, [friendly]);
            Assert.Same(friendly, selected);
            Assert.False(fixture.Disabled);
            Assert.StartsWith("Coppelia JOAT ", fixture.Active);
            Assert.Contains("native healing and raising", runtime.StatusText, StringComparison.Ordinal);

            runtime.Update(HealbotDecisionOutcome.Queued);
            Assert.True(fixture.Disabled);
            Assert.Equal("Holding", runtime.LastIssuedAction);
        }
        finally { targetProperty.SetValue(null, previousTargets); }
    }

    [Fact]
    public void RestorationRequiresNativeReadbackBeforeTheOriginalSnapshotCanBeReleased()
    {
        var fixture = new Fixture { Active = "Previous" };
        var service = fixture.Create();
        Assert.True(service.PrepareDamage(24, true));
        Assert.True(service.Resume());
        var owned = fixture.Active;
        fixture.RejectWrites = true;
        Assert.False(service.RestoreSessionSnapshot());
        Assert.True(service.HasSessionSnapshot);
        Assert.Equal(owned, fixture.Active);
        fixture.RejectWrites = false;
        Assert.True(service.RestoreSessionSnapshot());
        Assert.Equal("Previous", fixture.Active);
        Assert.False(service.HasSessionSnapshot);
    }

    [Fact]
    public void FrenRiderReleaseFollowsNativeRestorationAndOwnedPresetCleanup()
    {
        var fixture = new Fixture { Active = "Previous" };
        var bmr = fixture.Create();
        var released = false;
        var fr = new FrenRiderPowerlevelIpcService((endpoint, _) =>
        {
            if (endpoint.EndsWith("Release", StringComparison.Ordinal))
            {
                Assert.Equal("Previous", fixture.Active);
                Assert.False(fixture.Disabled);
                Assert.False(bmr.HasSessionSnapshot);
                Assert.DoesNotContain(fixture.Presets.Keys, name => name.StartsWith("Coppelia JOAT ", StringComparison.Ordinal));
                released = true;
            }
            return JsonSerializer.Serialize(new FrenRiderPowerlevelResponse(true, "active", CombatSuppressed: true),
                FrenRiderPowerlevelContract.JsonOptions);
        });
        var (_, _, runtime) = CreateRuntime(bmr, fr);
        Assert.True(fr.Acquire(out _));
        Assert.True(bmr.PrepareDamage(24, true));
        Assert.True(bmr.Resume());
        runtime.Deactivate("stopped");
        Assert.True(released);
        Assert.False(fr.LeaseAcquired);
        Assert.False(runtime.IsBmrHandoffReady);
    }

    [Fact]
    public void RejectedBmrPauseAndRestoreKeepLeaseUntilNativeReadbackSucceeds()
    {
        var fixture = new Fixture { Active = "Previous" };
        var bmr = fixture.Create();
        var releases = 0;
        var fr = new FrenRiderPowerlevelIpcService((endpoint, _) =>
        {
            if (endpoint.EndsWith("Release", StringComparison.Ordinal)) releases++;
            return JsonSerializer.Serialize(new FrenRiderPowerlevelResponse(true, "active", CombatSuppressed: true),
                FrenRiderPowerlevelContract.JsonOptions);
        });
        var (_, _, runtime) = CreateRuntime(bmr, fr);
        Assert.True(fr.Acquire(out _));
        Assert.True(bmr.PrepareDamage(24, true));
        Assert.True(bmr.Resume());
        fixture.RejectWrites = true;
        runtime.Deactivate("stopped");
        Assert.Equal(0, releases);
        Assert.True(fr.LeaseAcquired);
        Assert.True(bmr.HasSessionSnapshot);
        Assert.False(runtime.IsBmrHandoffReady);
        runtime.Update(HealbotDecisionOutcome.Unavailable);
        Assert.Equal(0, releases);
        fixture.RejectWrites = false;
        runtime.Deactivate("retry cleanup");
        Assert.Equal(1, releases);
        Assert.Equal("Previous", fixture.Active);
        Assert.False(fr.LeaseAcquired);
        Assert.False(bmr.HasSessionSnapshot);
    }

    [Fact]
    public void HealingProfileCannotCaptureBmrBeforeFrenRiderAcknowledgesSuppression()
    {
        var fixture = new Fixture { Active = "Previous" };
        var bmr = fixture.Create();
        var suppressed = false;
        var fr = new FrenRiderPowerlevelIpcService((_, _) => JsonSerializer.Serialize(
            new FrenRiderPowerlevelResponse(true, "active", CombatSuppressed: suppressed),
            FrenRiderPowerlevelContract.JsonOptions));
        var (plugin, healbot, _) = CreateRuntime(bmr, fr);
        Assert.True(fr.Acquire(out _));
        Assert.True(HealbotJobProfile.TryResolve(24, out var profile));
        SetField(healbot, "profileArmed", true); // Isolate provider activation from live target capture.
        var apply = typeof(HealbotRuntimeService).GetMethod("ApplyProfile", BindingFlags.Instance | BindingFlags.NonPublic)!;
        apply.Invoke(healbot, [profile, "first", true]);
        Assert.False(bmr.HasSessionSnapshot);
        Assert.Equal("Previous", fixture.Active);
        Assert.False(fixture.Disabled);
        Assert.False(healbot.IsRotationControlReady);

        suppressed = true;
        SetField(fr, "nextHeartbeatUtc", DateTimeOffset.MinValue);
        Assert.True(fr.HeartbeatIfDue(out _));
        apply.Invoke(healbot, [profile, "second", true]);
        Assert.True(bmr.HasSessionSnapshot);
        Assert.True(fixture.Disabled);
        Assert.True(healbot.IsRotationControlReady);
        Assert.True(plugin.HealbotRuntimeService.RestoreRotationOwnership());
    }

    [Fact]
    public void LostSuppressionAcknowledgementPausesBmrEvenBetweenHealingDecisions()
    {
        var fixture = new Fixture { Active = "Previous" };
        var bmr = fixture.Create();
        var suppressed = true;
        var fr = new FrenRiderPowerlevelIpcService((_, _) => JsonSerializer.Serialize(
            new FrenRiderPowerlevelResponse(true, "active", CombatSuppressed: suppressed),
            FrenRiderPowerlevelContract.JsonOptions));
        var (plugin, _, runtime) = CreateRuntime(bmr, fr);
        plugin.Configuration.AutomationEnabled = true;
        SetField(runtime, "armed", true);
        Assert.True(fr.Acquire(out _));
        Assert.True(bmr.PrepareDamage(24, true));
        Assert.True(bmr.Resume());
        suppressed = false;
        SetField(fr, "nextHeartbeatUtc", DateTimeOffset.MinValue);
        runtime.Update(HealbotDecisionOutcome.None);
        Assert.True(fixture.Disabled);
        Assert.True(fr.LeaseAcquired);
        Assert.False(runtime.IsBmrHandoffReady);
        Assert.Equal("Blocked", runtime.LastIssuedAction);
    }

    [Fact]
    public void JobChangeAndPackReloadPreserveTheOriginalSessionWithoutAcceptingReplacementCode()
    {
        var fixture = new Fixture { Active = "Previous" };
        var service = fixture.Create();
        Assert.True(service.PrepareDamage(24, true));
        Assert.True(service.Resume());
        var first = fixture.Active!;
        Assert.True(service.PrepareDamage(40, true));
        Assert.False(fixture.Presets.ContainsKey(first));
        fixture.ModuleIdentity = new object();
        Assert.False(service.Resume());
        Assert.True(service.RestoreSessionSnapshot());
        Assert.Equal("Previous", fixture.Active);
    }

    [Theory]
    [InlineData(24u)]
    [InlineData(28u)]
    [InlineData(33u)]
    [InlineData(40u)]
    public void MissingGodModCompanionRotationsWarnWithoutSelectingAnotherPreset(uint job)
    {
        var fixture = new Fixture { Active = "Previous", ModuleMissing = true };
        var service = fixture.Create();
        Assert.False(service.HasDamageCapability(job, true, out var blocker));
        Assert.Contains("God Mod Companion rotations could not be found", blocker, StringComparison.Ordinal);
        Assert.False(service.PrepareDamage(job, true));
        Assert.False(service.HasSessionSnapshot);
        Assert.Equal("Previous", fixture.Active);
        Assert.False(fixture.Disabled);
    }
    [Theory]
    [InlineData(false, null)]
    [InlineData(false, "Previous")]
    [InlineData(true, null)]
    public void PauseResumeAndReleaseRestoreExactPreviousRuntimeState(bool disabled, string? previous)
    {
        var fixture = new Fixture { Disabled = disabled, Active = previous };
        var service = fixture.Create();

        Assert.True(service.Prepare("Damage"));
        Assert.True(fixture.Disabled);
        Assert.True(service.Resume());
        Assert.False(fixture.Disabled);
        Assert.Equal("Damage", fixture.Active);
        Assert.True(service.Hold());
        Assert.True(fixture.Disabled);
        Assert.True(service.RestoreSessionSnapshot());
        Assert.Equal(disabled, fixture.Disabled);
        Assert.Equal(previous, fixture.Active);
        Assert.False(service.HasSessionSnapshot);
    }

    [Fact]
    public void IsolationDoesNotPretendToProvideAnAttackPreset()
    {
        var fixture = new Fixture { Active = "Previous" };
        var service = fixture.Create();
        Assert.True(service.Prepare());
        Assert.True(fixture.Disabled);
        Assert.False(service.Resume());
        Assert.True(service.RestoreSessionSnapshot());
        Assert.Equal("Previous", fixture.Active);
    }

    [Fact]
    public void ExternalPresetChangeIsNotOverwrittenOnHoldOrRelease()
    {
        var fixture = new Fixture { Active = "Previous" };
        var service = fixture.Create();
        Assert.True(service.Prepare("Damage"));
        Assert.True(service.Resume());
        fixture.Active = "External";
        Assert.False(service.Hold());
        Assert.False(fixture.Disabled);
        Assert.False(service.Prepare("Damage"));
        Assert.True(service.RestoreSessionSnapshot());
        Assert.Equal("External", fixture.Active);
        service.ResetOwnership();
        Assert.True(service.Prepare("Damage"));
        Assert.True(service.RestoreSessionSnapshot());
        Assert.Equal("External", fixture.Active);
    }

    [Fact]
    public void ReplacementProviderDoesNotReceiveDepartedSessionState()
    {
        var fixture = new Fixture { Active = "Previous" };
        var service = fixture.Create();
        Assert.True(service.Prepare("Damage"));
        fixture.Identity = new object();
        fixture.Disabled = false;
        fixture.Active = "Replacement";
        Assert.False(service.RestoreSessionSnapshot());
        Assert.Equal("Replacement", fixture.Active);
        Assert.False(fixture.Disabled);
    }

    [Fact]
    public void RejectedPauseCannotReportReady()
    {
        var fixture = new Fixture { Active = "Previous", RejectWrites = true };
        var service = fixture.Create();
        Assert.False(service.Prepare("Damage"));
        Assert.Equal("Previous", fixture.Active);
        Assert.False(fixture.Disabled);
        Assert.NotEmpty(service.LastFailure);
        Assert.True(service.RestoreSessionSnapshot());
        Assert.False(service.HasSessionSnapshot);
    }

    [Fact]
    public void MissingOriginalPresetBlocksAcquisitionWithoutChangingRuntime()
    {
        var fixture = new Fixture { Active = "Missing" };
        var service = fixture.Create();
        Assert.False(service.Prepare("Damage"));
        Assert.Equal("Missing", fixture.Active);
        Assert.False(fixture.Disabled);
        Assert.False(service.HasSessionSnapshot);
    }

    private static (Plugin Plugin, HealbotRuntimeService Healbot, JotRuntimeService Runtime) CreateRuntime(
        BmrIpcService bmr, FrenRiderPowerlevelIpcService fr)
    {
        var plugin = (Plugin)RuntimeHelpers.GetUninitializedObject(typeof(Plugin));
        SetField(plugin, "<Configuration>k__BackingField", new Configuration
        {
            BotMode = BotMode.Jot,
            JoatRotationProvider = JoatRotationProvider.Bmr,
            JoatFullRsrRotation = true,
        });
        SetField(plugin, "<BmrIpcService>k__BackingField", bmr);
        var dependency = new DependencyService();
        SetField(dependency, "<Current>k__BackingField", new DependencySnapshot { BossModRebornLoaded = true });
        var rsr = (RsrIpcService)RuntimeHelpers.GetUninitializedObject(typeof(RsrIpcService));
        var healbot = new HealbotRuntimeService(plugin, dependency, null!, rsr, null!);
        SetField(plugin, "<HealbotRuntimeService>k__BackingField", healbot);
        var runtime = new JotRuntimeService(plugin, fr);
        SetField(plugin, "<JotRuntimeService>k__BackingField", runtime);
        var qst = (CoppeliaQstIpcService)RuntimeHelpers.GetUninitializedObject(typeof(CoppeliaQstIpcService));
        SetField(qst, "plugin", plugin);
        SetField(qst, "gate", new object());
        SetField(plugin, "<CoppeliaQstIpcService>k__BackingField", qst);
        return (plugin, healbot, runtime);
    }

    private static void SetField(object instance, string name, object value)
        => instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(instance, value);

    private sealed class Fixture
    {
        public object Identity = new();
        public string? Active;
        public bool Disabled;
        public bool RejectWrites;
        public bool ModuleMissing;
        public bool DropManualTargeting;
        public object ModuleIdentity = new();
        public Dictionary<string, string> Presets = new(StringComparer.Ordinal)
        {
            ["Previous"] = JsonSerializer.Serialize(new { Name = "Previous" }),
            ["Damage"] = JsonSerializer.Serialize(new { Name = "Damage" }),
            ["External"] = JsonSerializer.Serialize(new { Name = "External" }),
            ["Replacement"] = JsonSerializer.Serialize(new { Name = "Replacement" }),
        };
        public Dictionary<string, IReadOnlySet<string>> Options = new(StringComparer.Ordinal)
        {
            ["Targeting"] = new HashSet<string>(["Manual", "Auto"]),
            ["AOE"] = new HashSet<string>(["AOE", "ForceST"]),
            ["DhogHeal"] = new HashSet<string>(["Enabled", "Disabled"]),
            ["DhogRaise"] = new HashSet<string>(["SwiftThenSafe", "Disabled"]),
            ["DhogRecipients"] = new HashSet<string>(["Nearby", "Party", "Alliance"]),
            ["DhogRaisePriority"] = new HashSet<string>(["PartyFirst", "HealersFirst", "TanksFirst"]),
            ["Kardia"] = new HashSet<string>(["Auto", "Manual"]),
            ["Druochole"] = new HashSet<string>(["Auto", "Manual"]),
            ["FairyPlace"] = new HashSet<string>(["AutoHeel", "Manual"]),
        };

        public BmrIpcService Create()
            => new(() => Identity,
                name => Presets.GetValueOrDefault(name),
                () => Disabled ? "" : Active,
                () => Disabled,
                name => Set(false, name),
                () => Set(false, null),
                () => Set(true, null),
                (json, overwrite) =>
                {
                    var node = JsonNode.Parse(json)!;
                    var name = node["Name"]!.GetValue<string>();
                    if (!overwrite && Presets.ContainsKey(name)) return false;
                    if (DropManualTargeting)
                    {
                        var settings = node["Modules"]!.AsObject().First().Value!.AsArray();
                        settings.Remove(settings.First(value => value!["Track"]!.GetValue<string>() == "Targeting"));
                    }
                    Presets[name] = node.ToJsonString();
                    return true;
                },
                name => Presets.Remove(name),
                definitionReader: _ => ModuleMissing ? null : (ModuleIdentity, Options));

        private bool Set(bool disabled, string? active)
        {
            if (RejectWrites) return false;
            Disabled = disabled;
            Active = active;
            return true;
        }
    }
}

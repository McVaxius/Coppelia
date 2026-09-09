using System.Numerics;
using System.Text.Json;
using System.Reflection;
using System.Runtime.InteropServices;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Party;
using Dalamud.Game.Text.SeStringHandling;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using Lumina.Excel;
using Lumina.Excel.Sheets;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Coppelia.Models;
using Coppelia.Services;

namespace Coppelia.Tests;

public sealed class HealRiderTransportTests
{
    private static readonly DateTime Started = new(2026, 9, 7, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void InstanceRendezvousRetriesUntilVerifiedAndSupersedesOldTargetsWithoutBoardingDeadline()
    {
        var commands = new List<string>();
        var busy = false;
        var loading = false;
        var occupied = false;
        var mainAetheryte = true;
        uint? helperInstance = 1;
        var clock = new TransportClock();
        var player = Proxy<IPlayerCharacter>((m, _) => m.Name switch
        {
            "get_Name" => new SeString(new Dalamud.Game.Text.SeStringHandling.Payloads.TextPayload("Test Helper")),
            "get_CurrentWorld" => new RowRef<World>(null!, 1), "get_Position" => Vector3.Zero,
            "get_IsCasting" => false, "get_IsDead" => false, "get_ObjectKind" => (ObjectKind)1, _ => null,
        });
        var aetheryte = Proxy<IGameObject>((m, _) => m.Name switch
        {
            "get_ObjectKind" => ObjectKind.Aetheryte, "get_Position" => new Vector3(3, 0, 0), "get_BaseId" => 8U, _ => null,
        });
        var pi = Proxy<IDalamudPluginInterface>((m, args) =>
        {
            if (m.Name != "GetIpcSubscriber") return null;
            var endpoint = (string)args![0]!;
            return Proxy(m.ReturnType, (call, _) => call.Name == "InvokeFunc" && call.ReturnType == typeof(bool)
                ? endpoint == "Lifestream.IsBusy" && busy : null);
        });
        var replacements = new Dictionary<string, object?>
        {
            ["PluginInterface"] = pi,
            ["Log"] = Proxy<IPluginLog>((_, _) => null),
            ["ObjectTable"] = Proxy<IObjectTable>((m, _) => m.Name switch
            {
                "get_LocalPlayer" => loading ? null : player,
                "GetEnumerator" => ((IEnumerable<IGameObject>)new[] { player, aetheryte }).GetEnumerator(), _ => null,
            }),
            ["ClientState"] = Proxy<IClientState>((m, _) => m.Name switch
            {
                "get_TerritoryType" => 141U, "get_IsLoggedIn" => true, _ => null,
            }),
            ["Condition"] = Proxy<ICondition>((m, args) => m.Name == "get_Item" && ((ConditionFlag)args![0]! switch
            { ConditionFlag.BetweenAreas => loading, ConditionFlag.Occupied39 => occupied, _ => false })),
            ["CommandManager"] = Proxy<ICommandManager>((m, args) =>
            {
                if (m.Name != "ProcessCommand") return null;
                commands.Add((string)args![0]!);
                return commands.Count > 1; // rejection and accepted-but-ineffective attempts are both retried
            }),
            ["DataManager"] = null,
        };
        const BindingFlags statics = BindingFlags.Static | BindingFlags.NonPublic;
        const BindingFlags instance = BindingFlags.Instance | BindingFlags.NonPublic;
        var previous = replacements.Keys.ToDictionary(key => key, key => typeof(Plugin).GetProperty(key, statics)!.GetValue(null));
        try
        {
            foreach (var (key, value) in replacements) typeof(Plugin).GetProperty(key, statics)!.SetValue(null, value);
            var service = new CoppeliaTravelService(new Configuration(), null!, null!);
            void Set(string field, object? value) => typeof(CoppeliaTravelService).GetField(field, instance)!.SetValue(service, value);
            object? Get(string field) => typeof(CoppeliaTravelService).GetField(field, instance)!.GetValue(service);
            Set("rideClock", clock);
            Set("instanceReader", (Func<uint?>)(() => loading ? null : helperInstance));
            Set("isMainAetheryte", (Func<uint, bool>)(id => id == 8 && mainAetheryte));
            Set("rideMode", new HealRiderCommand { SessionId = "session", MountId = 10 });
            var travel = new CoppeliaQstCommand("TravelUpdate", "session", "Test Quester", 1, 1, 141,
                0, 0, 0, 1, null, null, null, false, false, false, "Quester", 0, 0, 0, InstanceId: 2);
            Assert.True(service.Apply(travel).Accepted);
            void Tick(double seconds) { clock.Now = Started.AddSeconds(seconds); service.Update(); }
            occupied = true; Tick(0); Assert.Empty(commands);
            occupied = false;
            mainAetheryte = false; Tick(0); Assert.Empty(commands);
            Assert.Contains("teleport list", service.State); // a nearby shard cannot start /li; main-crystal travel is needed
            mainAetheryte = true;
            Tick(0);
            Assert.Equal(new[] { "/li 2" }, commands);
            Assert.Equal(1L, Get("pendingPriorityDestinationSequence"));
            Tick(4.99); Assert.Single(commands);
            Tick(5); Assert.Equal(2, commands.Count);
            busy = true; Tick(6); Tick(10);
            busy = false; loading = true; Tick(11);
            Assert.Equal(2, commands.Count);
            loading = false; Tick(15.99); Assert.Equal(2, commands.Count);
            Tick(16); Assert.Equal(3, commands.Count);
            Assert.Equal(1L, Get("pendingPriorityDestinationSequence"));
            travel = travel with { TravelSequence = 2, InstanceId = 3 };
            service.Apply(travel);
            Assert.Equal(2L, Get("pendingPriorityDestinationSequence"));
            Tick(20.99); Assert.Equal(3, commands.Count);
            Tick(21); Assert.Equal("/li 3", commands.Last());
            helperInstance = 2; Tick(26); // old target success cannot satisfy the newest target
            Assert.Equal(5, commands.Count);
            Assert.Equal("/li 3", commands.Last());
            Assert.Equal(2L, Get("pendingPriorityDestinationSequence"));
            helperInstance = 3; Tick(31);
            Assert.Equal(5, commands.Count);
            Assert.Equal(0L, Get("pendingPriorityDestinationSequence"));
            Assert.Null(Get("ordinaryMountDeadline"));
            Assert.Null(Get("ride"));
            travel = travel with { TravelSequence = 3, InstanceId = null };
            service.Apply(travel); helperInstance = 0; Tick(32);
            Assert.Contains("metadata", service.State);
            Assert.Equal(3L, Get("pendingPriorityDestinationSequence"));
            service.Apply(travel with { TravelSequence = 4, InstanceId = 0 }); Tick(33);
            Assert.Equal(0L, Get("pendingPriorityDestinationSequence"));
            Assert.Equal(5, commands.Count); // verified zero is non-instanced, never /li 0
            service.Apply(travel with { TravelSequence = 5, InstanceId = 2 }); Tick(34);
            Assert.Equal(6, commands.Count);
            service.Release(); Tick(100);
            Assert.Equal(6, commands.Count);
            Assert.Null(Get("latestTravel"));
            Assert.False((bool)Get("instanceSwitchPending")!);
        }
        finally
        {
            foreach (var (key, value) in previous) typeof(Plugin).GetProperty(key, statics)!.SetValue(null, value);
        }
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, false)]
    [InlineData(false, false, true)]
    [InlineData(true, false, true)]
    public unsafe void OrdinaryFollowUsesVisiblePositionsAndResumesCompletedRoutesWithAllHoldsIntact(
        bool mounted, bool healRider, bool newb)
    {
        var address = Marshal.AllocHGlobal(sizeof(Character));
        *(Character*)address = default;
        ((Character*)address)->Mount.MountId = 10;
        var submitted = new List<(Vector3 Destination, float Range)>();
        var running = false;
        var casting = false;
        var visible = true;
        var questerName = "Test Quester";
        uint questerHomeWorld = 1;
        uint questerCurrentWorld = 1;
        var helperPosition = Vector3.Zero;
        var questerPosition = new Vector3(9.99f, 0, 0);
        var pi = Proxy<IDalamudPluginInterface>((method, args) =>
        {
            if (method.Name != "GetIpcSubscriber") return null;
            var endpoint = (string)args![0]!;
            return Proxy(method.ReturnType, (call, values) =>
            {
                if (call.Name == "InvokeAction" && endpoint == "vnavmesh.Path.Stop") { running = false; return null; }
                if (call.Name != "InvokeFunc") return null;
                switch (endpoint)
                {
                    case "vnavmesh.Nav.IsReady": return true;
                    case "vnavmesh.Path.IsRunning": return running;
                    case "vnavmesh.SimpleMove.PathfindAndMoveCloseTo":
                        submitted.Add(((Vector3)values![0]!, (float)values[2]!)); running = true; return true;
                    default: return call.ReturnType == typeof(bool) ? false : null;
                }
            });
        });
        IPlayerCharacter Player(bool helper) => Proxy<IPlayerCharacter>((m, _) => m.Name switch
        {
            "get_Name" => new SeString(new Dalamud.Game.Text.SeStringHandling.Payloads.TextPayload(helper ? "Test Helper" : questerName)),
            "get_HomeWorld" => new RowRef<World>(null!, helper ? 1U : questerHomeWorld),
            "get_CurrentWorld" => new RowRef<World>(null!, helper ? 1U : questerCurrentWorld),
            "get_Position" => helper ? helperPosition : questerPosition,
            "get_IsCasting" => helper && casting, "get_CastActionId" => 1U,
            "get_Address" => helper ? address : (nint)0, _ => null,
        });
        var player = Player(true);
        var quester = Player(false);
        var replacements = new Dictionary<string, object?>
        {
            ["PluginInterface"] = pi,
            ["Log"] = Proxy<IPluginLog>((_, _) => null),
            ["ObjectTable"] = Proxy<IObjectTable>((m, _) => m.Name switch
            {
                "get_LocalPlayer" => player,
                "GetEnumerator" => ((IEnumerable<IGameObject>)(visible ? new[] { player, quester } : new[] { player })).GetEnumerator(),
                _ => null,
            }),
            ["ClientState"] = Proxy<IClientState>((m, _) => m.Name switch
            { "get_TerritoryType" => 141U, "get_IsLoggedIn" => true, _ => null }),
            ["Condition"] = Proxy<ICondition>((m, args) => m.Name == "get_Item" &&
                (ConditionFlag)args![0]! == ConditionFlag.Mounted && mounted),
            ["DataManager"] = null,
        };
        const BindingFlags statics = BindingFlags.Static | BindingFlags.NonPublic;
        const BindingFlags instance = BindingFlags.Instance | BindingFlags.NonPublic;
        var previous = replacements.Keys.ToDictionary(key => key, key => typeof(Plugin).GetProperty(key, statics)!.GetValue(null));
        try
        {
            foreach (var (key, value) in replacements) typeof(Plugin).GetProperty(key, statics)!.SetValue(null, value);
            var service = new CoppeliaTravelService(new Configuration(), null!, null!);
            void Set(string field, object? value) => typeof(CoppeliaTravelService).GetField(field, instance)!.SetValue(service, value);
            Set("instanceReader", (Func<uint?>)(() => 0));
            if (healRider) Set("rideMode", new HealRiderCommand { SessionId = "session", MountId = 10 });
            var travel = new CoppeliaQstCommand("TravelUpdate", "session", "Test Quester", 1, 1, 141,
                9.99f, 0, 0, 1, null, null, null, mounted, false, false, "Quester", 0, 0, 0,
                InstanceId: newb ? null : 0);
            Assert.True(service.Apply(travel).Accepted);
            service.Update(); Assert.Empty(submitted);

            // A 0.01-yalm visible movement crosses the trigger without a LAN update.
            questerPosition.X = 10f;
            service.Update();
            Assert.Equal((questerPosition, mounted ? 5f : 9f), Assert.Single(submitted));
            service.Update(); // Observe owned movement before completion.
            helperPosition.X = questerPosition.X - (mounted ? 5f : 9f);
            running = false; service.Update(); Assert.Single(submitted);
            helperPosition.X = questerPosition.X - 9.99f;
            service.Update(); Assert.Single(submitted);

            helperPosition = Vector3.Zero;
            running = true; service.Update(); // Another navigation owner at ten yalms.
            Assert.Single(submitted);
            running = false; casting = true; service.Update(); Assert.Single(submitted);
            casting = false; Set("actionHoldUntilUtc", DateTime.MinValue);
            if (healRider)
            {
                service.SuspendHealRiderMounts(true); service.Update(); Assert.Single(submitted);
                service.SuspendHealRiderMounts(false);
            }
            service.Update(); Assert.Equal(2, submitted.Count); // Same snapshot and stationary Quester.
            service.Update();
            helperPosition = questerPosition; running = false; service.Update();
            helperPosition.X = -0.01f; service.Update(); // Immediately above ten yalms.
            Assert.Equal(3, submitted.Count);

            // Invisible or inexact objects must restore the authenticated coordinates.
            service.Update(); running = false; visible = false; service.Update();
            Assert.Equal(new Vector3(travel.X, travel.Y, travel.Z), submitted.Last().Destination);
            visible = true;
            foreach (var mismatch in new[] { "name", "home", "current" })
            {
                questerName = mismatch == "name" ? "Another Quester" : "Test Quester";
                questerHomeWorld = mismatch == "home" ? 2U : 1U;
                questerCurrentWorld = mismatch == "current" ? 2U : 1U;
                service.Update(); running = false; service.Update();
                Assert.Equal(new Vector3(travel.X, travel.Y, travel.Z), submitted.Last().Destination);
            }
            questerCurrentWorld = 1;

            // Production ride completion requires a newer snapshot even with a visible target.
            typeof(CoppeliaTravelService).GetMethod("FinishRide", instance)!.Invoke(service, new object[] { "Arrived" });
            var beforeCleanup = submitted.Count;
            service.Update(); Assert.Contains("fresh Quester travel snapshot", service.State);
            service.Apply(travel); service.Update(); Assert.Equal(beforeCleanup, submitted.Count);
            service.Apply(travel with { TravelSequence = 2 }); service.Update();
            Assert.Equal(beforeCleanup + 1, submitted.Count);
            Assert.Equal(questerPosition, submitted.Last().Destination);
        }
        finally
        {
            foreach (var (key, value) in previous) typeof(Plugin).GetProperty(key, statics)!.SetValue(null, value);
            Marshal.FreeHGlobal(address);
        }
    }

    [Fact]
    public void SameTerritoryTeleportCanVerifyPhysicalArrivalWithoutANewerSnapshotOrBusyTransition()
    {
        var busy = false;
        var loading = false;
        uint territory = 141;
        uint crystalId = 8;
        var crystalPosition = new Vector3(100, 0, 0);
        var player = Proxy<IPlayerCharacter>((m, _) => m.Name switch
        { "get_CurrentWorld" => new RowRef<World>(null!, 1), "get_Position" => Vector3.Zero, _ => null });
        var crystal = Proxy<IGameObject>((m, _) => m.Name switch
        {
            "get_ObjectKind" => ObjectKind.Aetheryte, "get_BaseId" => crystalId,
            "get_Position" => crystalPosition, _ => null,
        });
        var replacements = new Dictionary<string, object?>
        {
            ["PluginInterface"] = Proxy<IDalamudPluginInterface>((m, args) =>
            {
                if (m.Name != "GetIpcSubscriber") return null;
                var endpoint = (string)args![0]!;
                return Proxy(m.ReturnType, (call, _) => call.Name == "InvokeFunc" && call.ReturnType == typeof(bool)
                    ? endpoint == "Lifestream.IsBusy" && busy : null);
            }),
            ["Log"] = Proxy<IPluginLog>((_, _) => null),
            ["ObjectTable"] = Proxy<IObjectTable>((m, _) => m.Name switch
            { "get_LocalPlayer" => player, "GetEnumerator" => ((IEnumerable<IGameObject>)new[] { crystal }).GetEnumerator(), _ => null }),
            ["ClientState"] = Proxy<IClientState>((m, _) => m.Name == "get_TerritoryType" ? territory : null),
            ["Condition"] = Proxy<ICondition>((m, args) => m.Name == "get_Item" &&
                (ConditionFlag)args![0]! == ConditionFlag.BetweenAreas && loading),
        };
        const BindingFlags statics = BindingFlags.Static | BindingFlags.NonPublic;
        const BindingFlags instance = BindingFlags.Instance | BindingFlags.NonPublic;
        var previous = replacements.Keys.ToDictionary(key => key, key => typeof(Plugin).GetProperty(key, statics)!.GetValue(null));
        try
        {
            foreach (var (key, value) in replacements) typeof(Plugin).GetProperty(key, statics)!.SetValue(null, value);
            var service = new CoppeliaTravelService(new Configuration(), null!, null!);
            void Set(string name, object value) => typeof(CoppeliaTravelService).GetField(name, instance)!.SetValue(service, value);
            Set("instanceReader", (Func<uint?>)(() => loading ? null : 0));
            var policy = (CoppeliaLifestreamRequestPolicy)typeof(CoppeliaTravelService).GetField("lifestreamPolicy", instance)!.GetValue(service)!;
            var travel = new CoppeliaQstCommand("TravelUpdate", "session", "Test Quester", 1, 1, 141,
                0, 0, 0, 1, null, null, null, false, false, false, "Quester", 0, 0, 0, InstanceId: 0);
            void Arm()
            {
                var type = typeof(CoppeliaTravelService).GetNestedType("LifestreamRequest", BindingFlags.NonPublic)!;
                Set("lifestreamRequest", Activator.CreateInstance(type,
                    false, (ushort)0, 141U, "Teleporting", "Teleport failed", true, 8U, "Test Crystal")!);
                Set("lifestreamObservedLoading", false);
                policy.Begin(1, true, DateTime.UtcNow - CoppeliaLifestreamRequestPolicy.BusyStartupTimeout);
            }
            bool Held() => (bool)typeof(CoppeliaTravelService).GetMethod("ObserveLifestreamRequest", instance)!
                .Invoke(service, new object[] { player, travel })!;
            Arm(); Assert.True(Held()); Assert.Equal(CoppeliaLifestreamActivity.Failed, policy.Activity);
            crystalPosition = new Vector3(3, 0, 0); crystalId = 9;
            Assert.True(Held()); // Another crystal in the same territory is insufficient.
            crystalId = 8; territory = 142; Assert.True(Held());
            territory = 141; loading = true; Assert.True(Held());
            loading = false; busy = true; Assert.True(Held());
            busy = false; Assert.False(Held()); Assert.False(policy.HasRequest);

            Arm(); crystalPosition = new Vector3(100, 0, 0);
            Assert.True(Held()); // Same territory alone cannot prove a same-territory teleport.
            Set("lifestreamObservedLoading", true);
            Assert.False(Held()); Assert.False(policy.HasRequest);
        }
        finally
        {
            foreach (var (key, value) in previous) typeof(Plugin).GetProperty(key, statics)!.SetValue(null, value);
        }
    }

    [Fact]
    public void InstanceContractsPreserveUnknownZeroAndNumberAndRejectThePreviousRideVersion()
    {
        foreach (var instanceId in new uint?[] { null, 0, 3 })
        {
            var command = new HealRiderCommand { Version = 2, InstanceId = instanceId, QuesterInstanceId = instanceId };
            var status = new HealRiderStatus { InstanceId = instanceId, CurrentWorldId = 5, Flying = true, Mounting = true };
            Assert.Equal(command, JsonSerializer.Deserialize<HealRiderCommand>(JsonSerializer.Serialize(command, CoppeliaQstContract.JsonOptions), CoppeliaQstContract.JsonOptions));
            Assert.Equal(status, JsonSerializer.Deserialize<HealRiderStatus>(JsonSerializer.Serialize(status, CoppeliaQstContract.JsonOptions), CoppeliaQstContract.JsonOptions));
            var travel = new CoppeliaQstCommand("TravelUpdate", "session", "Test Quester", 1, 1, 141,
                1, 2, 3, 1, null, null, null, false, false, false, "Quester", 0, 0, 0, InstanceId: instanceId);
            Assert.Equal(travel, JsonSerializer.Deserialize<CoppeliaQstCommand>(JsonSerializer.Serialize(travel, CoppeliaQstContract.JsonOptions), CoppeliaQstContract.JsonOptions));
            var qstStatus = new CoppeliaQstStatus(4, true, true, "", true, "", true, "", "", "", "session", "", "", "", false, instanceId);
            Assert.Equal(qstStatus, JsonSerializer.Deserialize<CoppeliaQstStatus>(JsonSerializer.Serialize(qstStatus, CoppeliaQstContract.JsonOptions), CoppeliaQstContract.JsonOptions));
        }
        Assert.False(HealRiderPolicy.SameInstance(null, null));
        Assert.False(HealRiderPolicy.SameInstance(0, null));
        Assert.False(HealRiderPolicy.SameInstance(1, 2));
        Assert.True(HealRiderPolicy.SameInstance(0, 0));
        Assert.Equal(4, CoppeliaQstContract.Version);
        Assert.Equal(2, new HealRiderStatus().Version);
        var service = (CoppeliaTravelService)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(CoppeliaTravelService));
        var rejected = service.ApplyHealRider(new HealRiderCommand { Version = 1, SessionId = "old-session", LegId = 4 });
        Assert.False(rejected.Accepted);
        Assert.Equal("Blocked", rejected.State);
        Assert.Contains("Update both", rejected.Blocker);
        var qstService = (CoppeliaQstIpcService)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(CoppeliaQstIpcService));
        const BindingFlags fields = BindingFlags.Instance | BindingFlags.NonPublic;
        typeof(CoppeliaQstIpcService).GetField("gate", fields)!.SetValue(qstService, new object());
        var oldQst = new CoppeliaQstCommand("Activate", "session", "Test Quester", 1, 1, 141,
            0, 0, 0, 1, null, null, null, false, false, false, "Quester", 0, 0, 0, ContractVersion: 3);
        var qstRejected = (CoppeliaQstCommandResponse)typeof(CoppeliaQstIpcService).GetMethod("HandleCommand", fields)!
            .Invoke(qstService, new object[] { oldQst })!;
        Assert.False(qstRejected.Accepted);
        Assert.Contains("v4 is required", qstRejected.Reason);
    }

    [Theory]
    [InlineData(false, 10f)]
    [InlineData(false, 15f)]
    [InlineData(false, 20f)]
    [InlineData(false, 100f)]
    [InlineData(true, 10f)]
    [InlineData(true, 15f)]
    [InlineData(true, 20f)]
    [InlineData(true, 100f)]
    public void OrdinaryFollowingSurvivesRejectedOrTimedOutPassengerMountPreparation(bool timeout, float distance)
    {
        var submitted = new List<Vector3>();
        var ranges = new List<float>();
        var running = false;
        var stops = 0;
        var casting = false;
        var mounted = false;
        var flags = new HashSet<ConditionFlag>();
        var pi = Proxy<IDalamudPluginInterface>((method, args) =>
        {
            if (method.Name != "GetIpcSubscriber") return null;
            var endpoint = (string)args![0]!;
            return Proxy(method.ReturnType, (call, values) =>
            {
                if (call.Name == "InvokeAction" && endpoint == "vnavmesh.Path.Stop") { running = false; stops++; return null; }
                if (call.Name != "InvokeFunc") return null;
                switch (endpoint)
                {
                    case "vnavmesh.Nav.IsReady": return true;
                    case "vnavmesh.Path.IsRunning": return running;
                    case "vnavmesh.SimpleMove.PathfindAndMoveCloseTo":
                        ranges.Add((float)values![2]!);
                        submitted.Add((Vector3)values![0]!); running = true; return true;
                    default: return call.ReturnType == typeof(bool) ? false : null;
                }
            });
        });
        var player = Proxy<IPlayerCharacter>((m, _) => m.Name switch
        {
            "get_CurrentWorld" => new RowRef<World>(null!, 1), "get_Position" => Vector3.Zero,
            "get_IsCasting" => casting, "get_CastActionId" => 1U, "get_Address" => (nint)0,
            _ => null,
        });
        var replacements = new Dictionary<string, object?>
        {
            ["PluginInterface"] = pi,
            ["Log"] = Proxy<IPluginLog>((_, _) => null),
            ["ObjectTable"] = Proxy<IObjectTable>((m, _) => m.Name switch
            {
                "get_LocalPlayer" => player,
                "GetEnumerator" => Enumerable.Empty<IGameObject>().GetEnumerator(), _ => null,
            }),
            ["ClientState"] = Proxy<IClientState>((m, _) => m.Name == "get_TerritoryType" ? 141U : null),
            ["Condition"] = Proxy<ICondition>((m, a) => m.Name == "get_Item" &&
                (flags.Contains((ConditionFlag)a![0]!) || (ConditionFlag)a[0]! == ConditionFlag.Mounted && mounted)),
            ["DataManager"] = null, // Unavailable mount data uses the existing ordinary ground-follow rules.
        };
        const BindingFlags statics = BindingFlags.Static | BindingFlags.NonPublic;
        const BindingFlags instance = BindingFlags.Instance | BindingFlags.NonPublic;
        var previous = replacements.Keys.ToDictionary(key => key, key => typeof(Plugin).GetProperty(key, statics)!.GetValue(null));
        try
        {
            foreach (var (key, value) in replacements) typeof(Plugin).GetProperty(key, statics)!.SetValue(null, value);
            var service = new CoppeliaTravelService(new Configuration(), null!, null!);
            void Set(string field, object? value) => typeof(CoppeliaTravelService).GetField(field, instance)!.SetValue(service, value);
            Set("rideClock", new TransportClock());
            Set("instanceReader", (Func<uint?>)(() => 0));
            Set("rideMode", new HealRiderCommand { SessionId = "session", MountId = 10 });
            if (timeout) Set("ordinaryMountDeadline", Started);
            else Set("ordinaryMountBlocker", "Selected passenger mount preparation was rejected.");
            var travel = new CoppeliaQstCommand("TravelUpdate", "session", "Test Quester", 1, 1, 141,
                distance, 0, 0, 1, null, null, null, false, false, false, "Quester", 0, 0, 0, InstanceId: 0);
            Assert.True(service.Apply(travel).Accepted);
            service.Update();
            Assert.Equal(new Vector3(distance, 0, 0), Assert.Single(submitted));
            Assert.Equal(9f, Assert.Single(ranges));
            Assert.Contains("ground follow", service.State);
            for (var i = 0; i < 100; i++) service.Update();
            Assert.Single(submitted);
            Assert.Equal(0, stops);
            Assert.NotEmpty((string)typeof(CoppeliaTravelService).GetField("ordinaryMountBlocker", instance)!.GetValue(service)!);
            Assert.False((bool)typeof(CoppeliaTravelService).GetMethod("UpdateOrdinaryRideMount", instance)!.Invoke(service, new object[] { true })!);

            casting = true; service.Update();
            Assert.Contains("Paused for cast", service.State);
            Assert.Equal(1, stops);
            casting = false; Set("actionHoldUntilUtc", DateTime.MinValue); service.Update();
            Assert.Equal(2, submitted.Count);
            service.PauseForAction(); service.Update();
            Assert.Equal("Paused for a HealBot action", service.State);
            Set("actionHoldUntilUtc", DateTime.MinValue);
            service.SuspendHealRiderMounts(true); service.Update();
            Assert.Contains("duty ownership", service.State);
            service.SuspendHealRiderMounts(false);

            mounted = true;
            Set("nextMountActionUtc", DateTime.UtcNow.AddMinutes(1));
            service.Apply(travel with { TravelSequence = 2, X = 3 }); service.Update();
            Assert.Contains("Waiting to dismount", service.State); // Failed preparation no longer preserves a transport mount.
            Assert.Equal(2, submitted.Count);
            Set("ride", new HealRiderCommand());
            Set("rideStatus", new HealRiderStatus { State = "Blocked", Blocker = "Verified active-ride cleanup required" });
            service.Update();
            Assert.Contains("active-ride cleanup", service.State);
            Assert.Equal(2, submitted.Count);
        }
        finally
        {
            foreach (var (key, value) in previous) typeof(Plugin).GetProperty(key, statics)!.SetValue(null, value);
        }
    }

    [Fact]
    public void SelectedPassengerMountSurvivesOrdinaryRendezvousLandingAndPickup()
    {
        var following = new CoppeliaFollowPolicy();
        Assert.Equal(CoppeliaFollowPhase.Follow, following.Evaluate(100, false, false, true, false, true, true, true).Phase);
        Assert.Equal(CoppeliaFollowPhase.Land, following.Evaluate(3, false, false, true, false, true, true, true).Phase);
        Assert.Equal(CoppeliaFollowPhase.Idle, following.Evaluate(3, false, false, true, false, false, true, true).Phase);
        var actions = new List<string>();
        var nextAction = DateTime.MinValue;
        Assert.Equal("Waiting", HealRiderPolicy.PrepareMount(Started, Started.AddSeconds(60), ref nextAction,
            true, false, false, true, true, true, action => { actions.Add(action); return true; }));
        Assert.Equal("Ready", HealRiderPolicy.PrepareMount(Started.AddSeconds(2), Started.AddSeconds(60), ref nextAction,
            true, false, false, false, true, true, action => { actions.Add(action); return true; }));
        Assert.Equal(new[] { "Land" }, actions);
        Assert.Equal(CoppeliaFollowPhase.Dismount, following.Evaluate(3, false, false, true, false, false, true).Phase);
    }

    [Fact]
    public void GeometryAllowanceRequiresFiveContinuousSecondsUnderTenYalmsAndGroundedSelectedMount()
    {
        DateTime? nearSince = null;
        Assert.False(HealRiderPolicy.ObservePickupRange(9, Started, ref nearSince));
        Assert.False(HealRiderPolicy.ObservePickupRange(9, Started.AddSeconds(4.99), ref nearSince));
        Assert.True(HealRiderPolicy.ObservePickupRange(9.99f, Started.AddSeconds(5), ref nearSince));
        var actions = new List<string>();
        var state = HealRiderPolicy.AdvanceTransport("Preparing", 9, 100, true, false, false, true,
            false, false, true, action => { actions.Add(action); return "Ready"; }, settledPickupRange: true);
        Assert.Equal("Boarding", state.State);
        Assert.Equal(new[] { "Stop", "PrepareMount" }, actions);
        Assert.True(HealRiderPolicy.GroundedForBoarding(9, false, false, true, true));
        Assert.False(HealRiderPolicy.GroundedForBoarding(9, true, false, true, true));
        Assert.False(HealRiderPolicy.GroundedForBoarding(9, false, false, false, true));
        Assert.False(HealRiderPolicy.GroundedForBoarding(10, false, false, true, true));
        Assert.False(HealRiderPolicy.ObservePickupRange(10, Started.AddSeconds(6), ref nearSince));
        Assert.False(HealRiderPolicy.ObservePickupRange(9, Started.AddSeconds(7), ref nearSince));
        Assert.False(HealRiderPolicy.ObservePickupRange(float.NaN, Started.AddSeconds(12), ref nearSince));
        Assert.Null(nearSince);
        Assert.False(HealRiderPolicy.CanDepart(true, false, true));
        Assert.False(HealRiderPolicy.GroundedForBoarding(1, false, false, true));
        Assert.False(HealRiderPolicy.ObservePickupRange(1, Started.AddSeconds(20), ref nearSince, grounded: false));
        Assert.False(HealRiderPolicy.ObservePickupRange(1, Started.AddSeconds(25), ref nearSince));
        Assert.True(HealRiderPolicy.ObservePickupRange(1, Started.AddSeconds(30), ref nearSince));
        Assert.False(HealRiderPolicy.ObservePickupRange(1, Started.AddSeconds(31), ref nearSince, grounded: false));
        Assert.Null(nearSince);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public unsafe void NativeRidingModeDrivesProductionDepartureWithEmptyOrStaleSeatArrays(bool staleSeats, bool helperLoadsFirst)
    {
        var helperAddress = Marshal.AllocHGlobal(sizeof(Character));
        var questerAddress = Marshal.AllocHGlobal(sizeof(Character));
        *(Character*)helperAddress = default;
        *(Character*)questerAddress = default;
        ((Character*)helperAddress)->Mount.MountId = 10;
        if (staleSeats) ((Character*)helperAddress)->Mount.MountedEntityIds[0] = 999;
        var submitted = new List<Vector3>();
        var tolerances = new List<float>();
        var rejectNavigation = false;
        var logs = new List<string>();
        var running = false;
        var partyReady = true;
        var hasHelper = true;
        ulong helperId = 1;
        uint territory = 399;
        uint questerWorld = 1;
        var mounted = true;
        var flying = false;
        uint? helperInstance = 0;
        var helperLoading = false;
        var questerLoading = false;
        var questerPresent = true;
        uint questerTerritory = 399;
        var position = Vector3.Zero;
        var clock = new TransportClock();
        var pi = Proxy<IDalamudPluginInterface>((method, args) =>
        {
            if (method.Name != "GetIpcSubscriber") return null;
            var endpoint = (string)args![0]!;
            return Proxy(method.ReturnType, (call, values) =>
            {
                if (call.Name == "InvokeAction" && endpoint == "vnavmesh.Path.Stop") { running = false; return null; }
                if (call.Name != "InvokeFunc") return null;
                switch (endpoint)
                {
                    case "vnavmesh.Nav.IsReady": return true;
                    case "vnavmesh.Path.IsRunning": return running;
                    case "vnavmesh.Path.Stop": running = false; return null;
                    case "vnavmesh.SimpleMove.PathfindAndMoveCloseTo":
                        Assert.Equal(CharacterModes.RidingPillion, ((Character*)questerAddress)->Mode);
                        Assert.True((bool)values![1]!);
                        submitted.Add((Vector3)values[0]!);
                        tolerances.Add((float)values[2]!);
                        running = !rejectNavigation;
                        return !rejectNavigation;
                    default: return call.ReturnType == typeof(bool) ? false : null;
                }
            });
        });
        IPlayerCharacter Player(string name, uint id, nint address) => Proxy<IPlayerCharacter>((m, _) => m.Name switch
        {
            "get_Name" => new SeString(new Dalamud.Game.Text.SeStringHandling.Payloads.TextPayload(name)),
            "get_HomeWorld" => new RowRef<World>(null!, 1),
            "get_CurrentWorld" => new RowRef<World>(null!, id == 2 ? questerWorld : 1),
            "get_Address" => address, "get_EntityId" => id,
            "get_Position" => position, "get_IsDead" => false,
            _ => null,
        });
        var helper = Player("Test Helper", 1, helperAddress);
        var quester = Player("Test Quester", 2, questerAddress);
        IPartyMember Member(string name, ulong id) => Proxy<IPartyMember>((m, _) => m.Name switch
        {
            "get_Name" => new SeString(new Dalamud.Game.Text.SeStringHandling.Payloads.TextPayload(name)),
            "get_World" => new RowRef<World>(null!, 1), "get_ContentId" => id, _ => null,
        });
        var members = new[] { Member("Test Helper", 1), Member("Test Quester", 2) };
        var replacements = new Dictionary<string, object>
        {
            ["PluginInterface"] = pi,
            ["Log"] = Proxy<IPluginLog>((m, a) => { if (m.Name == "Information") logs.Add(a![0]!.ToString()!); return null; }),
            ["ObjectTable"] = Proxy<IObjectTable>((m, _) => m.Name switch
            {
                "get_LocalPlayer" => helperLoading ? null : helper,
                "GetEnumerator" => ((IEnumerable<IGameObject>)(questerPresent ? new IGameObject[] { helper, quester } : new IGameObject[] { helper })).GetEnumerator(), _ => null,
            }),
            ["PlayerState"] = Proxy<IPlayerState>((m, _) => m.Name == "get_ContentId" ? helperId : null),
            ["ClientState"] = Proxy<IClientState>((m, _) => m.Name switch
            { "get_TerritoryType" => territory, "get_IsLoggedIn" => true, _ => null }),
            ["Condition"] = Proxy<ICondition>((m, a) => m.Name == "get_Item" && ((ConditionFlag)a![0]! switch
            { ConditionFlag.Mounted => mounted, ConditionFlag.InFlight => flying,
                ConditionFlag.BetweenAreas => helperLoading, _ => false })),
            ["PartyList"] = Proxy<IPartyList>((m, _) => m.Name switch
            {
                "get_Length" => partyReady ? 2 : 3,
                "GetEnumerator" => ((IEnumerable<IPartyMember>)(hasHelper ? members : new[] { Member("Unrelated", 3), members[1] })).GetEnumerator(),
                _ => null,
            }),
        };
        const BindingFlags statics = BindingFlags.Static | BindingFlags.NonPublic;
        var previous = replacements.Keys.ToDictionary(key => key, key => typeof(Plugin).GetProperty(key, statics)!.GetValue(null));
        try
        {
            foreach (var (key, value) in replacements) typeof(Plugin).GetProperty(key, statics)!.SetValue(null, value);
            var service = new CoppeliaTravelService(new Configuration(), null!, null!);
            const BindingFlags instance = BindingFlags.Instance | BindingFlags.NonPublic;
            void Set(string field, object value) => typeof(CoppeliaTravelService).GetField(field, instance)!.SetValue(service, value);
            var captured = new HealRiderCommand
            {
                Version = 2, SessionId = "session", LegId = 1, QuesterName = "Test Quester", QuesterWorldId = 1,
                CurrentWorldId = 1, TerritoryId = 399, MountId = 10, X = 120, Y = 15, Z = -30,
                InstanceId = 0, QuesterInstanceId = 0,
                ContinueMounted = true, TargetTerritoryId = 400,
                PickupDeadlineUtc = Started.AddSeconds(60),
            };
            Set("rideClock", clock); Set("ride", captured); Set("lastRide", captured);
            Set("instanceReader", (Func<uint?>)(() => helperLoading ? null : helperInstance));
            Set("rideHelperContentId", 1UL); Set("rideUpdatedUtc", Started); Set("rideExpiresUtc", Started.AddMinutes(3));
            Set("rideStatus", new HealRiderStatus { SessionId = "session", LegId = 1, State = "Boarding" });
            var update = typeof(CoppeliaTravelService).GetMethod("UpdateHealRider", instance)!;
            var forward = typeof(CoppeliaTravelService).GetMethod("ObserveRidePassenger", instance)!;
            var observe = typeof(CoppeliaTravelService).GetMethod("ExactPassengerIsAboard", instance)!;
            bool Native() => (bool)observe.Invoke(service, new object[] { captured, quester })!;
            void Tick(bool confirmed, double seconds = 0)
            {
                clock.Now = Started.AddSeconds(seconds);
                var wire = JsonSerializer.Deserialize<HealRiderCommand>(JsonSerializer.Serialize(captured with
                    { Action = "Inspect", PassengerConfirmed = confirmed, QuesterLoading = questerLoading,
                        QuesterTerritoryId = questerTerritory }, CoppeliaQstContract.JsonOptions), CoppeliaQstContract.JsonOptions)!;
                forward.Invoke(service, new object[] { wire });
                update.Invoke(service, null);
            }
            Assert.False(Native());
            // Even a stale exact seat ID must not substitute for native riding mode.
            ((Character*)helperAddress)->Mount.MountedEntityIds[0] = 2;
            Assert.False(Native());
            ((Character*)helperAddress)->Mount.MountedEntityIds[0] = staleSeats ? 999U : 0;
            Tick(true);
            Assert.Contains("Helper's native", service.State);
            ((Character*)questerAddress)->Mode = CharacterModes.RidingPillion;
            Assert.True(Native());
            helperId = 3; Assert.False(Native()); helperId = 1;
            hasHelper = false; Assert.False(Native()); hasHelper = true;
            partyReady = false; Assert.False(Native()); partyReady = true;
            questerWorld = 2; Assert.False(Native()); questerWorld = 1;
            territory = 400; Assert.False(Native()); territory = 399;
            helperInstance = 2; Assert.False(Native()); helperInstance = null; Assert.False(Native()); helperInstance = 0;
            var pickupReady = typeof(CoppeliaTravelService).GetMethod("RidePickupLocationReady", instance)!;
            Assert.True((bool)pickupReady.Invoke(service, new object[] { captured })!);
            Assert.False((bool)pickupReady.Invoke(service, new object[] { captured with { InstanceId = 2, QuesterInstanceId = 2 } })!);
            Assert.False((bool)pickupReady.Invoke(service, new object[] { captured with { QuesterInstanceId = null } })!);
            mounted = false; Assert.False(Native()); mounted = true;
            ((Character*)helperAddress)->Mount.MountId = 11; Assert.False(Native());
            ((Character*)helperAddress)->Mount.MountId = 10;
            Tick(false);
            Assert.Contains("Quester's passenger", service.State);
            var count = logs.Count;
            for (var i = 0; i < 1000; i++) Tick(false);
            Assert.Equal(count, logs.Count);
            Assert.Empty(submitted);
            Tick(true, 1);
            Assert.Equal("HealRider: Transit", service.State);
            Assert.Equal(HealRiderPolicy.Destination(captured), Assert.Single(submitted));
            count = logs.Count;
            for (var i = 0; i < 1000; i++) Tick(true, 1 + i / 100d);
            Assert.Single(submitted);
            Assert.Equal(count, logs.Count);
            helperLoading = helperLoadsFirst;
            questerLoading = !helperLoadsFirst;
            if (!helperLoadsFirst) questerTerritory = 400;
            Tick(false, 12);
            Assert.True(service.HealRiderActive);
            Assert.Equal("ZoneTransition", ((HealRiderStatus)typeof(CoppeliaTravelService).GetField("rideStatus", instance)!.GetValue(service)!).State);
            helperLoading = false; territory = 400; questerPresent = false; questerLoading = true;
            Tick(false, 12.1);
            Assert.True(service.HealRiderActive);
            Assert.Single(submitted);
            questerPresent = true; questerLoading = false; questerTerritory = 400;
            Tick(true, 12.2);
            var continuation = captured with { Action = "Continue", ContinuationId = 1, TerritoryId = 400,
                QuesterTerritoryId = 400, PassengerConfirmed = true, X = 200, ContinueMounted = false, TargetTerritoryId = 0 };
            var advance = typeof(CoppeliaTravelService).GetMethod("TryContinueRide", instance)!;
            Assert.False((bool)advance.Invoke(service, new object[] { continuation with { PassengerConfirmed = false } })!);
            Assert.False((bool)advance.Invoke(service, new object[] { continuation with { ContinuationId = 2 } })!);
            Assert.True((bool)advance.Invoke(service, new object[] { continuation })!);
            Assert.False((bool)advance.Invoke(service, new object[] { continuation })!);
            captured = continuation;
            Tick(true, 12.3);
            Assert.Equal(2, submitted.Count);
            Assert.Equal(HealRiderPolicy.Destination(continuation), submitted.Last());
            ((Character*)questerAddress)->Mode = default;
            Tick(true, 12.4);
            Assert.Contains("Cancelling", service.State);
            Assert.False(running);
            // A late native confirmation cannot revive cancelled or expired boarding.
            ((Character*)questerAddress)->Mode = CharacterModes.RidingPillion;
            Tick(true, 13);
            Assert.Equal(2, submitted.Count);
            Set("ride", captured); Set("rideStatus", new HealRiderStatus { State = "Boarding" });
            Tick(true, 60);
            Assert.Equal(2, submitted.Count);
            Assert.DoesNotContain("Transit", service.State);
            // Final landing escalates once after five seconds, retaining its original deadline.
            Set("ride", captured); Set("rideCleanupDeadline", null!);
            Set("rideStatus", new HealRiderStatus { State = "Transit" });
            position = HealRiderPolicy.Destination(captured) + new Vector3(0, 3, 0);
            flying = true;
            Tick(true, 61);
            Assert.Contains("Arriving", service.State);
            Set("nextRideActionUtc", Started.AddSeconds(66)); // accepted landing actions remain ineffective
            Tick(true, 65.99);
            Assert.Equal(2, submitted.Count);
            Tick(true, 66);
            Assert.Equal(3, submitted.Count);
            Assert.Equal(HealRiderPolicy.Destination(captured), submitted.Last());
            Assert.Equal(0.5f, tolerances.Last());
            Assert.Equal(Started.AddSeconds(121), typeof(CoppeliaTravelService).GetField("rideCleanupDeadline", instance)!.GetValue(service));
            position += new Vector3(12, 0, 0); // the precise descent may leave the original five-yalm radius
            Tick(true, 66.1);
            Assert.True(running);
            for (var i = 0; i < 100; i++) Tick(true, 66.2 + i / 100d);
            Assert.Equal(3, submitted.Count);
            Assert.True(running);
            position = HealRiderPolicy.Destination(captured) + new Vector3(0, 3, 0);
            mounted = false; flying = false;
            Tick(false, 68);
            Assert.True(service.HealRiderActive); // grounding outside 0.5 cannot complete arrival
            Assert.True(running);
            position = HealRiderPolicy.Destination(captured);
            flying = true;
            running = false; // reaching XYZ and stopping still does not prove grounding
            Tick(false, 68.5);
            Assert.True(service.HealRiderActive);
            Assert.Contains("Arriving", service.State);
            flying = false;
            mounted = false;
            Tick(false, 69);
            Assert.False(service.HealRiderActive);
            Assert.Contains("Arrived", service.State);

            // A rejected precise route keeps the original 60-second cleanup allowance and remains held.
            Set("ride", captured); Set("rideStatus", new HealRiderStatus { State = "Arriving" });
            Set("ridePreciseLandingSubmitted", false);
            rejectNavigation = true; mounted = true; flying = true;
            Tick(true, 70);
            Assert.Contains("Precise landing navigation rejected", service.State);
            Assert.True(service.HealRiderActive);
            Assert.Equal(Started.AddSeconds(121), typeof(CoppeliaTravelService).GetField("rideCleanupDeadline", instance)!.GetValue(service));
            Tick(true, 121);
            Assert.Contains("cleanup", service.State);
            Assert.Contains("landing", service.State);
            Assert.True(service.HealRiderActive);
            service.RetryHealRiderCleanup();
            Assert.Equal(Started.AddSeconds(181), typeof(CoppeliaTravelService).GetField("rideCleanupDeadline", instance)!.GetValue(service));
            Tick(true, 122);
            service.RetryHealRiderCleanup();
            Assert.Equal(Started.AddSeconds(181), typeof(CoppeliaTravelService).GetField("rideCleanupDeadline", instance)!.GetValue(service));
            flying = false; mounted = false;
            Tick(false, 125);
            Assert.False(service.HealRiderActive);
            Assert.Contains("Cancelled", service.State);
            Assert.Equal(4, submitted.Count); // retry only cleans up; it never submits the failed destination again
        }
        finally
        {
            foreach (var (key, value) in previous) typeof(Plugin).GetProperty(key, statics)!.SetValue(null, value);
            Marshal.FreeHGlobal(helperAddress);
            Marshal.FreeHGlobal(questerAddress);
        }
    }

    [Fact]
    public void SeatObservationGapHoldsBoardingThenSubmitsCapturedDestinationOnceThroughNavigation()
    {
        var submitted = new List<(Vector3 Destination, bool Fly, float Tolerance)>();
        var running = false;
        var pi = Proxy<IDalamudPluginInterface>((method, args) =>
        {
            if (method.Name != "GetIpcSubscriber") return null;
            var endpoint = (string)args![0]!;
            return Proxy(method.ReturnType, (call, values) =>
            {
                if (call.Name != "InvokeFunc") return null;
                switch (endpoint)
                {
                    case "vnavmesh.Nav.IsReady": return true;
                    case "vnavmesh.Path.IsRunning": return running;
                    case "vnavmesh.SimpleMove.PathfindAndMoveCloseTo":
                        submitted.Add(((Vector3)values![0]!, (bool)values[1]!, (float)values[2]!));
                        running = true;
                        return true;
                    default: return call.ReturnType == typeof(bool) ? false : null;
                }
            });
        });
        const BindingFlags statics = BindingFlags.Static | BindingFlags.NonPublic;
        var piProperty = typeof(Plugin).GetProperty("PluginInterface", statics)!;
        var logProperty = typeof(Plugin).GetProperty("Log", statics)!;
        var previousPi = piProperty.GetValue(null);
        var previousLog = logProperty.GetValue(null);
        try
        {
            piProperty.SetValue(null, pi);
            logProperty.SetValue(null, Proxy<IPluginLog>((_, _) => null));
            var service = new CoppeliaTravelService(new Configuration(), null!, null!);
            var clock = new TransportClock();
            typeof(CoppeliaTravelService).GetField("rideClock", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(service, clock);
            var move = typeof(CoppeliaTravelService).GetMethod("MoveRideTo", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var captured = new HealRiderCommand { X = 120, Y = 15, Z = -30 };
            var destination = HealRiderPolicy.Destination(captured);
            var state = "Boarding";
            void Tick(bool confirmed, bool native)
            {
                state = HealRiderPolicy.AdvanceTransport(state, 8, 100, true, false, false, true,
                    confirmed, native, true, action =>
                    {
                        Assert.Equal("Travel", action);
                        move.Invoke(service, new object[] { destination, true, HealRiderPolicy.ArrivalTolerance });
                        return "Waiting";
                    }).State;
            }
            for (var i = 0; i < 1000; i++) Tick(false, true);
            Assert.Equal("Boarding", state);
            Assert.Empty(submitted);
            for (var i = 0; i < 1000; i++) Tick(true, false);
            Assert.Equal("Boarding", state);
            Tick(true, true);
            Assert.Equal("Transit", state);
            Assert.Equal((destination, true, 5f), Assert.Single(submitted));
            for (var i = 0; i < 1000; i++)
            {
                clock.Now = Started.AddSeconds(i / 20d);
                Tick(true, true);
            }
            Assert.Single(submitted);
            foreach (var terminal in new[] { "Cancelled", "Blocked", "Cancelling" })
            {
                state = terminal;
                for (var i = 0; i < 100; i++) Tick(true, true);
                Assert.Equal(terminal, state);
            }
            Assert.Single(submitted);
        }
        finally
        {
            piProperty.SetValue(null, previousPi);
            logProperty.SetValue(null, previousLog);
        }
    }

    private sealed class TransportClock : TimeProvider
    {
        public DateTime Now = Started;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static T Proxy<T>(Func<MethodInfo, object?[]?, object?> handler) where T : class => (T)Proxy(typeof(T), handler);
    private static object Proxy(Type type, Func<MethodInfo, object?[]?, object?> handler)
    {
        var result = DispatchProxy.Create(type, typeof(TransportProxy));
        ((TransportProxy)result).Handler = handler;
        return result;
    }
    public class TransportProxy : DispatchProxy
    {
        public Func<MethodInfo, object?[]?, object?> Handler = null!;
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => Handler(targetMethod!, args);
    }

    [Fact]
    public void ProductionMountPreparationLandsReplacesAndVerifiesSelectedMount()
    {
        var commands = new List<string>();
        var nextAction = Started;
        string Tick(double seconds, bool mounted, bool casting, bool flying, bool selected) =>
            HealRiderPolicy.PrepareMount(Started.AddSeconds(seconds), Started.AddSeconds(60), ref nextAction,
                mounted, casting, casting, flying, selected, true, action => { commands.Add(action); return true; });
        Assert.Equal("Waiting", Tick(0, true, false, true, false));
        Assert.Equal(new[] { "Land" }, commands);
        for (var i = 1; i < 200; i++) Tick(i / 100d, true, false, true, false);
        Assert.Single(commands);
        Tick(2, true, false, false, false);
        Tick(4, false, false, false, false);
        Tick(6, false, true, false, false);
        Tick(7, false, false, false, false); // interrupted cast: retry inside original allowance
        Assert.Equal("Waiting", Tick(8, false, true, false, false));
        Assert.Equal("Ready", Tick(9, true, false, false, true));
        Assert.Equal(new[] { "Land", "Dismount", "Summon", "Summon" }, commands);
        Assert.Equal("Blocked", Tick(60, false, false, false, false));
        for (var i = 61; i < 600; i++) Assert.Equal("Blocked", Tick(i, false, false, false, false));
        Assert.Equal(4, commands.Count);
    }

    [Fact]
    public void OrdinaryCatchupUsesTheSameSelectedMountLoopAndRejectionIsTerminal()
    {
        var commands = new List<string>();
        var next = Started;
        Assert.Equal("Waiting", HealRiderPolicy.PrepareMount(Started, Started.AddSeconds(60), ref next,
            false, false, false, false, false, false, action => { commands.Add(action); return true; }));
        Assert.Equal(new[] { "Summon" }, commands);
        Assert.Equal("Ready", HealRiderPolicy.PrepareMount(Started.AddSeconds(3), Started.AddSeconds(60), ref next,
            true, false, false, true, true, false, _ => throw new Exception("Selected flying mount must be retained")));
        Assert.Equal("Blocked", HealRiderPolicy.PrepareMount(Started.AddSeconds(4), Started.AddSeconds(60), ref next,
            false, false, false, false, false, false, _ => false));
    }

    [Fact]
    public void ProductionTransportTransitionsRequireBothPassengerObservationsAndObservedArrivalDismount()
    {
        var issued = new List<string>();
        var state = "Preparing";
        var preparation = "Waiting";
        void Tick(float pickup, float destination, bool mounted = true, bool selected = true,
            bool questerPassenger = false, bool nativePassenger = false, bool flying = false)
        {
            var next = HealRiderPolicy.AdvanceTransport(state, pickup, destination,
                mounted, false, flying, selected, questerPassenger, nativePassenger, true, action =>
                {
                    issued.Add(action);
                    return action == "PrepareMount" ? preparation : "Waiting";
                }, settledPickupRange: pickup < 10 && !flying);
            state = next.State;
        }
        Tick(50, 100, selected: false);
        Assert.Equal(new[] { "Stop", "PrepareTravelMount" }, issued);
        Tick(50, 100);
        Assert.Equal("Approach", issued.Last());
        Tick(3, 100, selected: false);
        Assert.Equal("Preparing", state);
        preparation = "Ready";
        Tick(3, 100);
        Assert.Equal("Boarding", state);
        var count = issued.Count;
        for (var i = 0; i < 1000; i++) Tick(3, 100, questerPassenger: true);
        Assert.Equal("Boarding", state);
        Assert.Equal(count, issued.Count);
        Tick(3, 100, questerPassenger: true, nativePassenger: true);
        Assert.Equal("Transit", state);
        Tick(0, 90, questerPassenger: true, nativePassenger: true, flying: true);
        Assert.Equal("Travel", issued.Last());
        Tick(0, 5, questerPassenger: true, nativePassenger: true, flying: true);
        Assert.Equal("Arriving", state);
        Tick(0, 5, questerPassenger: true, nativePassenger: true, flying: true);
        Assert.Equal("Dismount", issued.Last());
        Assert.Equal("Arriving", state);
        Tick(0, 5, mounted: false, selected: false);
        Assert.Equal("Arrived", state);
        Assert.True(HealRiderPolicy.CleanupComplete(true, state, false, false, false, true, true));
    }

    [Theory]
    [InlineData("Boarding", false, true, true, true)]
    [InlineData("Boarding", true, false, true, true)]
    [InlineData("Transit", true, true, false, true)]
    [InlineData("Transit", true, true, true, false)]
    public void ProductionTransportCancelsMountOrPassengerLoss(string state, bool mounted,
        bool selected, bool confirmed, bool nativePassenger)
    {
        var next = HealRiderPolicy.AdvanceTransport(state, 0, 100, mounted, false, false,
            selected, confirmed, nativePassenger, true, _ => throw new Exception("Must cancel before movement"));
        Assert.Equal("Cancelling", next.State);
        Assert.NotEmpty(next.Blocker);
    }

    [Fact]
    public void ProductionTransportCancelsRejectedPreparationAndMovedLanding()
    {
        Assert.Equal("Cancelling", HealRiderPolicy.AdvanceTransport("Preparing", 3, 100,
            false, false, false, false, false, false, true, _ => "Blocked").State);
        Assert.Equal("Cancelling", HealRiderPolicy.AdvanceTransport("Arriving", 0, 6,
            true, false, false, true, true, true, true, _ => throw new Exception("Out of tolerance")).State);
        foreach (var state in new[] { "Cancelled", "Blocked", "Arrived" })
            for (var i = 0; i < 1000; i++)
                Assert.Equal(state, HealRiderPolicy.AdvanceTransport(state, 100, 100,
                    true, false, false, true, false, false, true, _ => throw new Exception("Terminal command")).State);
    }

    [Fact]
    public void CleanupKeepsItsFirstDeadlineAndNeedsBothEndpointsToFinish()
    {
        DateTime? deadline = null;
        deadline = HealRiderPolicy.BeginCleanup(deadline, Started);
        for (var second = 1; second <= 120; second++)
            Assert.Equal(Started.AddSeconds(60), HealRiderPolicy.BeginCleanup(deadline, Started.AddSeconds(second)));
        DateTime? soloSince = null;
        Assert.False(HealRiderPolicy.ConfirmSolo(true, true, Started, ref soloSince));
        Assert.False(HealRiderPolicy.ConfirmSolo(false, false, Started.AddSeconds(1), ref soloSince));
        Assert.False(HealRiderPolicy.ConfirmSolo(true, false, Started.AddSeconds(2), ref soloSince));
        Assert.True(HealRiderPolicy.ConfirmSolo(true, false, Started.AddSeconds(3), ref soloSince));
        Assert.False(HealRiderPolicy.CleanupComplete(true, "Blocked", true, false, false, true, true));
        Assert.False(HealRiderPolicy.CleanupComplete(false, "Cancelled", false, false, false, true, true));
        Assert.False(HealRiderPolicy.CleanupComplete(true, "Cancelled", false, false, true, true, true));
        Assert.True(HealRiderPolicy.CleanupComplete(true, "Cancelled", false, false, false, true, true));
    }

    [Theory]
    [InlineData("Quester")]
    [InlineData("Coppelia")]
    public void DelayedArrivalAndRejectedInvitationsShareOneFixedPickupWindow(string inviter)
    {
        var command = new HealRiderCommand
        {
            PartyInviter = inviter, PickupDeadlineUtc = Started.AddSeconds(60),
        };
        var nextInvite = DateTime.MinValue;
        var invitations = new List<DateTime>();
        for (var second = 0; second <= 90; second++)
        {
            var now = Started.AddSeconds(second);
            if (HealRiderPolicy.ShouldInvite(now, command.PickupDeadlineUtc, nextInvite,
                    travelReady: second >= 20, partyReady: false, solo: true))
            {
                invitations.Add(now);
                nextInvite = now.AddSeconds(5);
                // Rejected invite: observing it and polling must not extend the window.
                command = command with { Action = "Inspect" };
            }
        }
        Assert.Equal(Enumerable.Range(0, 8).Select(i => Started.AddSeconds(20 + i * 5)), invitations);
        Assert.Equal(Started.AddSeconds(60), command.PickupDeadlineUtc);
        Assert.True(HealRiderPolicy.PickupExpired(command.PickupDeadlineUtc, Started.AddSeconds(60), "Idle"));
        Assert.True(HealRiderPolicy.PickupExpired(command.PickupDeadlineUtc, Started.AddSeconds(60), "Preparing"));
        Assert.True(HealRiderPolicy.PickupExpired(command.PickupDeadlineUtc, Started.AddSeconds(60), "Boarding"));
        Assert.False(HealRiderPolicy.PickupExpired(command.PickupDeadlineUtc, Started.AddSeconds(60), "Transit"));
        Assert.False(HealRiderPolicy.ShouldInvite(Started.AddSeconds(30), command.PickupDeadlineUtc,
            Started, travelReady: true, partyReady: true, solo: false));
        Assert.False(HealRiderPolicy.ShouldInvite(Started.AddSeconds(30), command.PickupDeadlineUtc,
            Started, travelReady: true, partyReady: false, solo: false));
    }

    [Theory]
    [InlineData(50f, false, false, true, false)]
    [InlineData(50f, true, false, true, false)]
    [InlineData(3f, true, false, true, false)]
    [InlineData(3f, false, true, true, false)]
    [InlineData(3f, false, false, false, false)]
    [InlineData(3f, false, false, true, true)]
    public void MountedPickupMustApproachLandAndFinishMountingBeforeBoarding(
        float distance, bool flying, bool mounting, bool selectedMount, bool expected) =>
        Assert.Equal(expected, HealRiderPolicy.GroundedForBoarding(distance, flying, mounting, selectedMount, true));

    [Fact]
    public void FailedPreparationCanReleasePartyWhileOrdinaryMountedButTransportMustDismount()
    {
        Assert.True(HealRiderPolicy.CanRegroup(
            HealRiderPolicy.MustWaitForDismount(false, true), false, true));
        Assert.False(HealRiderPolicy.CanRegroup(
            HealRiderPolicy.MustWaitForDismount(true, true), false, true));
        Assert.False(HealRiderPolicy.CanRegroup(
            HealRiderPolicy.MustWaitForDismount(true, false), true, true));
        Assert.True(HealRiderPolicy.CanRegroup(
            HealRiderPolicy.MustWaitForDismount(true, false), false, true));
    }

    [Theory]
    [InlineData(50, true, false, true, true, false)]
    [InlineData(51, true, false, true, true, true)]
    [InlineData(100, false, false, true, true, false)]
    [InlineData(100, true, true, true, true, false)]
    [InlineData(100, true, false, false, true, false)]
    [InlineData(100, true, false, true, false, false)]
    public void PickupRequiresStrictDistanceFlightMountAndLocation(
        float distance, bool helperFlies, bool questerFlies, bool mountReady, bool sameLocation, bool expected) =>
        Assert.Equal(expected, HealRiderPolicy.Eligible(distance, 50, helperFlies, questerFlies, mountReady, sameLocation));

    [Fact]
    public void InvalidCoordinatesOrThresholdCannotEnablePickup()
    {
        Assert.False(HealRiderPolicy.Eligible(float.NaN, 50, true, false, true, true));
        Assert.False(HealRiderPolicy.Eligible(100, float.NaN, true, false, true, true));
        Assert.False(HealRiderPolicy.Eligible(100, -1, true, false, true, true));
    }

    [Theory]
    [InlineData(false, true, true, false)]
    [InlineData(true, false, true, false)]
    [InlineData(true, true, false, false)]
    [InlineData(true, true, true, true)]
    public void BoardingNeedsBothQuesterAcknowledgementAndExactNativePassenger(
        bool confirmed, bool nativePassenger, bool partyReady, bool expected) =>
        Assert.Equal(expected, HealRiderPolicy.CanDepart(confirmed, nativePassenger, partyReady));

    [Theory]
    [InlineData(true, false, true, false)]
    [InlineData(false, true, true, false)]
    [InlineData(false, false, false, false)]
    [InlineData(false, false, true, true)]
    public void DismountAndPartyReleaseMustFinishBeforeRegroup(
        bool mounted, bool passenger, bool released, bool expected) =>
        Assert.Equal(expected, HealRiderPolicy.CanRegroup(mounted, passenger, released));

    [Fact]
    public void ChangedAssignmentIdentityLegOrDestinationRequiresCancellation()
    {
        var command = new HealRiderCommand
        {
            Version = 2, Action = "Pickup", SessionId = "test-session", LegId = 1,
            QuesterName = "Test Quester", QuesterWorldId = 1, CurrentWorldId = 1,
            TerritoryId = 399, X = 60, Y = 4, Z = 10,
        };
        Assert.True(HealRiderPolicy.SameLeg(command, command with { Action = "Inspect", PassengerConfirmed = true }));
        Assert.False(HealRiderPolicy.SameLeg(command, command with { SessionId = "replacement-session" }));
        Assert.False(HealRiderPolicy.SameLeg(command, command with { QuesterName = "Other Quester" }));
        Assert.False(HealRiderPolicy.SameLeg(command, command with { QuesterWorldId = 2 }));
        Assert.False(HealRiderPolicy.SameLeg(command, command with { LegId = 2 }));
        Assert.False(HealRiderPolicy.SameLeg(command, command with { CurrentWorldId = 2 }));
        Assert.False(HealRiderPolicy.SameLeg(command, command with { TerritoryId = 398 }));
        Assert.False(HealRiderPolicy.SameLeg(command, command with { InstanceId = 2 }));
        Assert.False(HealRiderPolicy.SameLeg(command, command with { X = 61 }));

        var requestJson = JsonSerializer.Serialize(command, CoppeliaQstContract.JsonOptions);
        Assert.Equal(command, JsonSerializer.Deserialize<HealRiderCommand>(requestJson, CoppeliaQstContract.JsonOptions));
        Assert.Equal(4, CoppeliaQstContract.Version);
    }

    [Fact]
    public void FailedOwnedPathfindProbesBeforeNewTerritorySnapshotAndKeepsOriginalDeadline()
    {
        var route = new CoppeliaRoutePolicy();
        route.AcceptSnapshot(1, new Vector3(100, 0, 0));
        route.MarkStartupAccepted(Started);
        route.Observe(true, false, Started.AddMilliseconds(100));
        var failure = route.Observe(false, false, Started.AddMilliseconds(200));
        Assert.Equal(CoppeliaRouteActivity.Rejected, failure);
        var handoff = new CoppeliaTerritoryHandoffPolicy();
        Assert.True(handoff.TryArmFollowFailure(100, failure == CoppeliaRouteActivity.Rejected));
        Assert.Equal(CoppeliaTerritoryHandoffDecision.StartForwardProbe,
            handoff.Evaluate(100, 100, false, true, TimeSpan.FromSeconds(5), Started));
        Assert.False(handoff.TryArm(true, false, false, 100, 200));
        Assert.Equal(CoppeliaTerritoryHandoffDecision.ContinueForwardProbe,
            handoff.Evaluate(100, 200, false, true, TimeSpan.FromSeconds(20), Started.AddSeconds(4)));
        Assert.Equal(CoppeliaTerritoryHandoffDecision.UseTeleportFallback,
            handoff.Evaluate(100, 200, false, true, TimeSpan.FromSeconds(20), Started.AddSeconds(5)));
        Assert.False(handoff.TryArmFollowFailure(100, true));
    }

    [Fact]
    public void FailureProbeStopsAtActualTransitionAndUsesNewestDestination()
    {
        var handoff = new CoppeliaTerritoryHandoffPolicy();
        Assert.True(handoff.TryArmFollowFailure(100, true));
        handoff.Evaluate(100, 100, false, true, TimeSpan.FromSeconds(5), Started);
        Assert.Equal(CoppeliaTerritoryHandoffDecision.WaitForLoad,
            handoff.Evaluate(100, 100, true, false, TimeSpan.FromSeconds(5), Started.AddSeconds(1)));
        Assert.Equal(CoppeliaTerritoryHandoffDecision.DestinationReached,
            handoff.Evaluate(200, 200, false, true, TimeSpan.FromSeconds(5), Started.AddSeconds(2)));
        Assert.False(handoff.IsActive);
    }

    [Fact]
    public void FinishedRideCannotFollowOldPickupUntilANewerSnapshotArrives()
    {
        var snapshots = new CoppeliaTravelSequencePolicy();
        Assert.True(snapshots.TryAccept(10));
        snapshots.RequireFreshSnapshot();
        Assert.True(snapshots.WaitingForFreshSnapshot);
        Assert.False(snapshots.TryAccept(10));
        Assert.False(snapshots.TryAccept(9));
        Assert.True(snapshots.WaitingForFreshSnapshot);
        Assert.True(snapshots.TryAccept(11));
        Assert.False(snapshots.WaitingForFreshSnapshot);
        snapshots.RequireFreshSnapshot();
        snapshots.Reset();
        Assert.False(snapshots.WaitingForFreshSnapshot);
    }

    [Fact]
    public void UnrelatedActivityCannotArmOwnedFailureRecoveryAndCancellationReleasesPendingMovement()
    {
        var handoff = new CoppeliaTerritoryHandoffPolicy();
        Assert.False(handoff.TryArmFollowFailure(100, false));
        var route = new CoppeliaRoutePolicy();
        Assert.Equal(CoppeliaRouteActivity.Other, route.Observe(true, false, Started));
        Assert.Equal(CoppeliaRouteInterruption.None, route.Release(true, false));
        route.AcceptSnapshot(1, new Vector3(100, 0, 0));
        route.MarkStartupAccepted(Started);
        route.Observe(true, false, Started);
        Assert.Equal(CoppeliaRouteInterruption.StopPathAndCancelPending, route.Release(true, false));
        Assert.False(route.OwnsRoute);
        Assert.False(route.HasDestination);
    }
}

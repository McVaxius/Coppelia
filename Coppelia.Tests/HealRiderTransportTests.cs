using System.Numerics;
using System.Text.Json;
using System.Reflection;
using System.Runtime.InteropServices;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.ClientState.Objects.Types;
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public unsafe void NativeRidingModeDrivesProductionDepartureWithEmptyOrStaleSeatArrays(bool staleSeats)
    {
        var helperAddress = Marshal.AllocHGlobal(sizeof(Character));
        var questerAddress = Marshal.AllocHGlobal(sizeof(Character));
        *(Character*)helperAddress = default;
        *(Character*)questerAddress = default;
        ((Character*)helperAddress)->Mount.MountId = 10;
        if (staleSeats) ((Character*)helperAddress)->Mount.MountedEntityIds[0] = 999;
        var submitted = new List<Vector3>();
        var logs = new List<string>();
        var running = false;
        var partyReady = true;
        var hasHelper = true;
        ulong helperId = 1;
        uint territory = 399;
        uint questerWorld = 1;
        var mounted = true;
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
                        running = true;
                        return true;
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
            "get_Position" => Vector3.Zero, "get_IsDead" => false,
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
                "get_LocalPlayer" => helper,
                "GetEnumerator" => ((IEnumerable<IGameObject>)new IGameObject[] { helper, quester }).GetEnumerator(), _ => null,
            }),
            ["PlayerState"] = Proxy<IPlayerState>((m, _) => m.Name == "get_ContentId" ? helperId : null),
            ["ClientState"] = Proxy<IClientState>((m, _) => m.Name switch
            { "get_TerritoryType" => territory, "get_IsLoggedIn" => true, _ => null }),
            ["Condition"] = Proxy<ICondition>((m, a) => m.Name == "get_Item" && (ConditionFlag)a![0]! == ConditionFlag.Mounted && mounted),
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
                Version = 1, SessionId = "session", LegId = 1, QuesterName = "Test Quester", QuesterWorldId = 1,
                CurrentWorldId = 1, TerritoryId = 399, MountId = 10, X = 120, Y = 15, Z = -30,
                PickupDeadlineUtc = Started.AddSeconds(60),
            };
            Set("rideClock", clock); Set("ride", captured); Set("lastRide", captured);
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
                    { Action = "Inspect", PassengerConfirmed = confirmed }, CoppeliaQstContract.JsonOptions), CoppeliaQstContract.JsonOptions)!;
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
            ((Character*)questerAddress)->Mode = default;
            Tick(true, 12);
            Assert.Contains("Cancelling", service.State);
            Assert.False(running);
            // A late native confirmation cannot revive cancelled or expired boarding.
            ((Character*)questerAddress)->Mode = CharacterModes.RidingPillion;
            Tick(true, 13);
            Assert.Single(submitted);
            Set("ride", captured); Set("rideStatus", new HealRiderStatus { State = "Boarding" });
            Tick(true, 60);
            Assert.Single(submitted);
            Assert.DoesNotContain("Transit", service.State);
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
                });
            state = next.State;
        }
        Tick(50, 100, selected: false);
        Assert.Equal(new[] { "Approach" }, issued);
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
        Assert.Equal(expected, HealRiderPolicy.GroundedForBoarding(distance, flying, mounting, selectedMount));

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
            Version = 1, Action = "Pickup", SessionId = "test-session", LegId = 1,
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
        Assert.False(HealRiderPolicy.SameLeg(command, command with { X = 61 }));

        var requestJson = JsonSerializer.Serialize(command, CoppeliaQstContract.JsonOptions);
        Assert.Equal(command, JsonSerializer.Deserialize<HealRiderCommand>(requestJson, CoppeliaQstContract.JsonOptions));
        Assert.Equal(3, CoppeliaQstContract.Version);
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

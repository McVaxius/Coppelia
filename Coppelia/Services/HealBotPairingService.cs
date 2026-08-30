using System.Net;
using System.Net.Sockets;
using System.Numerics;
using System.Text.Json;
using Coppelia.Models;
using Dalamud.Game.ClientState.Conditions;
using Lumina.Excel.Sheets;

namespace Coppelia.Services;

internal sealed class HealBotPairingService : IDisposable
{
    private static readonly TimeSpan TravelInterval = TimeSpan.FromMilliseconds(750);

    private readonly Plugin plugin;
    private readonly object teleportGate = new();

    private HealBotLanServer? server;
    private HealBotLanClient? client;
    private HealBotTeleportObserver? teleportObserver;
    private string serverSignature = string.Empty;
    private string clientSignature = string.Empty;
    private string sessionId = string.Empty;
    private string healBotName = string.Empty;
    private ushort healBotWorldId;
    private long travelSequence;
    private DateTime nextTravelUtc = DateTime.MinValue;
    private DateTime assignmentAcknowledgedUtc = DateTime.MinValue;
    private DateTime recoveryStatusBoundaryUtc = DateTime.MinValue;
    private string releasedRecoverySessionId = string.Empty;
    private PendingAssignment? pendingAssignment;
    private PendingCommand? pendingCommand;
    private PendingRecovery? pendingRecovery;
    private TravelSnapshot? lastAcceptedTravel;
    private PendingTeleport? pendingTeleport;
    private bool disposed;

    public HealBotPairingService(Plugin plugin)
    {
        this.plugin = plugin;
    }

    public string ConnectionStatus { get; private set; } = "Stopped";
    public string PairingIdentity { get; private set; } = "None";
    public string Blocker { get; private set; } = string.Empty;
    public string RuntimeStatus { get; private set; } = "Pairing is stopped.";
    public string HealingStatus { get; private set; } = "Not paired.";
    public string ChaseStatus { get; private set; } = "Idle";

    public void Update()
    {
        if (disposed)
            return;

        ObservePendingAssignment();
        ObservePendingCommand();
        ObservePendingRecovery();

        var configuration = plugin.Configuration;
        if (!configuration.PluginEnabled || !configuration.AutomationEnabled)
        {
            StopResources("Automation is off.");
            return;
        }

        if (configuration.BotMode == BotMode.HealBot)
        {
            StopClient("HealBot mode is listening, not connecting.");
            UpdateHealBotRole();
            return;
        }

        if (configuration.BotMode == BotMode.Newb)
        {
            StopServer("Newb mode is connecting, not listening.");
            UpdateNewbRole();
            return;
        }

        StopResources("The selected mode does not use direct pairing.");
    }

    public bool TryValidateNewbActivation(out string reason)
    {
        reason = plugin.Configuration.GetLanPairingBlocker(BotMode.Newb);
        if (!string.IsNullOrWhiteSpace(reason))
            return false;

        var localPlayer = Plugin.ObjectTable.LocalPlayer;
        if (localPlayer == null ||
            string.IsNullOrWhiteSpace(localPlayer.Name.TextValue) ||
            localPlayer.HomeWorld.RowId == 0)
        {
            reason = "The local Newb identity is unavailable. Log in fully before starting Newb mode.";
            return false;
        }

        if (!Configuration.TryParseLanHealBotAddress(plugin.Configuration.LanHealBotAddress, out var address))
        {
            reason = "Enter one valid IPv4 HealBot address.";
            return false;
        }

        var probe = HealBotLanClient.ProbeAsync(
                address,
                plugin.Configuration.LanPairingPort,
                plugin.Configuration.LanPairingSecret)
            .GetAwaiter()
            .GetResult();
        if (probe.Status == null)
        {
            reason = probe.Failure;
            return false;
        }

        var status = probe.Status;
        if (status.PairProtocolVersion != HealBotLanEnvelope.CurrentProtocolVersion)
        {
            reason = "The paired endpoint uses an incompatible HealBot pairing protocol.";
            return false;
        }
        if (string.IsNullOrWhiteSpace(status.HealBotName) || status.HealBotWorldId == 0)
        {
            reason = "The paired endpoint did not report an exact HealBot identity.";
            return false;
        }
        if (string.Equals(status.AssignmentSource, "QST", StringComparison.Ordinal))
        {
            reason = "The paired HealBot already has an active QST assignment.";
            return false;
        }
        if (string.Equals(status.AssignmentSource, "Newb", StringComparison.Ordinal) &&
            (!string.Equals(status.AssignedName, localPlayer.Name.TextValue.Trim(), StringComparison.Ordinal) ||
             status.AssignedWorldId != localPlayer.HomeWorld.RowId))
        {
            reason = "The paired HealBot already belongs to another Newb session.";
            return false;
        }
        if (!status.Ready)
        {
            reason = string.IsNullOrWhiteSpace(status.Blocker)
                ? "The paired HealBot is not ready."
                : status.Blocker;
            return false;
        }

        reason = string.Empty;
        return true;
    }

    public void Restart()
    {
        StopResources("Pairing settings were saved; restarting.");
        serverSignature = string.Empty;
        clientSignature = string.Empty;
    }

    public void ReleaseForDeactivation(string reason)
        => StopResources(reason);

    public HealBotLanStatusResponse BuildServerStatus(string requestId, HealBotLanStatusRequest? request)
    {
        var localPlayer = Plugin.ObjectTable.LocalPlayer;
        var localName = localPlayer?.Name.TextValue.Trim() ?? string.Empty;
        var localWorld = (ushort)(localPlayer?.HomeWorld.RowId ?? 0);
        var assignment = plugin.CoppeliaQstIpcService.GetAssignmentSnapshot();
        var readiness = plugin.CoppeliaQstIpcService.EvaluateNewbReadiness();
        var blocker = plugin.Configuration.GetLanPairingBlocker(BotMode.HealBot);
        if (request?.PairProtocolVersion != HealBotLanEnvelope.CurrentProtocolVersion)
            blocker = "The Newb client uses an incompatible HealBot pairing protocol.";
        else if (string.IsNullOrWhiteSpace(localName) || localWorld == 0)
            blocker = "The local HealBot identity is unavailable.";
        else if (!readiness.Ready)
            blocker = readiness.Blocker;

        var ready = string.IsNullOrWhiteSpace(blocker);
        return new HealBotLanStatusResponse(
            requestId,
            HealBotLanEnvelope.CurrentProtocolVersion,
            localName,
            localWorld,
            ready,
            blocker,
            plugin.Configuration.BotMode.GetLabel(),
            plugin.Configuration.AutomationEnabled,
            assignment.Source,
            assignment.Name,
            assignment.WorldId,
            assignment.SessionId,
            assignment.Source == "Newb" ? "Paired" : server?.IsRunning == true ? "Listening" : "Stopped",
            plugin.HealbotRuntimeService.StatusText,
            plugin.CoppeliaTravelService.State);
    }

    public HealBotLanCommandResult HandleServerCommand(string requestId, HealBotLanTargetedCommand? targeted)
    {
        if (targeted == null)
            return HealBotLanCommandResult.Rejected(requestId, null, "The pairing command payload is invalid.");

        var localPlayer = Plugin.ObjectTable.LocalPlayer;
        if (localPlayer == null ||
            !string.Equals(targeted.TargetHealBotName, localPlayer.Name.TextValue.Trim(), StringComparison.Ordinal) ||
            targeted.TargetHealBotWorldId != localPlayer.HomeWorld.RowId)
        {
            return HealBotLanCommandResult.Rejected(
                requestId,
                targeted,
                "The command does not target this exact HealBot name and home world.");
        }

        var command = targeted.Command;
        var response = command.Action switch
        {
            "AssignNewb" => plugin.CoppeliaQstIpcService.AssignNewb(command),
            "TravelUpdate" => plugin.CoppeliaQstIpcService.ApplyNewbTravel(command),
            "Release" => plugin.CoppeliaQstIpcService.ReleaseNewb(command.SessionId, "Newb released the assignment."),
            _ => new CoppeliaQstCommandResponse(false, "Unknown Newb pairing action."),
        };
        return new HealBotLanCommandResult(
            requestId,
            command.Action,
            command.SessionId,
            response.Accepted,
            response.Reason);
    }

    public void Dispose()
    {
        if (disposed)
            return;

        disposed = true;
        StopResources("HealBot is unloading.");
    }

    private void UpdateHealBotRole()
    {
        var blocker = plugin.Configuration.GetLanPairingBlocker(BotMode.HealBot);
        if (!string.IsNullOrWhiteSpace(blocker))
        {
            StopServer(blocker);
            ConnectionStatus = "Blocked";
            PairingIdentity = FormatAssignmentIdentity();
            Blocker = blocker;
            RuntimeStatus = "Ordinary HealBot healing remains active; LAN pairing is blocked.";
            HealingStatus = plugin.HealbotRuntimeService.StatusText;
            ChaseStatus = plugin.CoppeliaTravelService.State;
            return;
        }

        var signature = $"{plugin.Configuration.LanPairingPort}|{plugin.Configuration.LanPairingSecret}";
        if (server == null || !string.Equals(serverSignature, signature, StringComparison.Ordinal))
        {
            StopServer("Restarting the HealBot listener.");
            server = new HealBotLanServer(plugin, plugin.Configuration.LanPairingPort, plugin.Configuration.LanPairingSecret);
            serverSignature = signature;
            server.Start();
        }

        var assignment = plugin.CoppeliaQstIpcService.GetAssignmentSnapshot();
        ConnectionStatus = server.IsRunning
            ? server.ConnectedClientCount > 0 ? "Connected" : "Listening"
            : "Starting";
        PairingIdentity = FormatAssignmentIdentity();
        Blocker = server.ListeningBlocker;
        RuntimeStatus = assignment.Source == "Newb"
            ? "Active Newb assignment"
            : server.IsRunning
                ? $"Listening on TCP {plugin.Configuration.LanPairingPort}"
                : "Starting the pairing listener";
        HealingStatus = plugin.HealbotRuntimeService.StatusText;
        ChaseStatus = plugin.CoppeliaTravelService.State;
    }

    private void UpdateNewbRole()
    {
        var blocker = plugin.Configuration.GetLanPairingBlocker(BotMode.Newb);
        if (!string.IsNullOrWhiteSpace(blocker))
        {
            StopClient(blocker);
            ConnectionStatus = "Blocked";
            PairingIdentity = "None";
            Blocker = blocker;
            RuntimeStatus = "Newb fails closed and performs no local healing or attacking.";
            HealingStatus = "Remote HealBot unavailable.";
            ChaseStatus = "Remote chase unavailable.";
            return;
        }

        var signature = $"{plugin.Configuration.LanHealBotAddress}|{plugin.Configuration.LanPairingPort}|{plugin.Configuration.LanPairingSecret}";
        if (client == null || !string.Equals(clientSignature, signature, StringComparison.Ordinal))
        {
            StopClient("Restarting the Newb connection.");
            client = new HealBotLanClient(
                plugin.Configuration.LanHealBotAddress,
                plugin.Configuration.LanPairingPort,
                plugin.Configuration.LanPairingSecret);
            clientSignature = signature;
            client.Start();
            teleportObserver = new HealBotTeleportObserver(Plugin.GameInteropProvider);
            teleportObserver.OnTeleportAccepted += OnTeleportAccepted;
        }

        UpdateNewbWorkflow();
        var status = client.LastStatus;
        ConnectionStatus = client.ConnectionState;
        PairingIdentity = !string.IsNullOrWhiteSpace(sessionId)
            ? FormatIdentity(healBotName, healBotWorldId)
            : status == null
                ? "None"
                : FormatIdentity(status.Status.HealBotName, status.Status.HealBotWorldId);
        Blocker = !string.IsNullOrWhiteSpace(Blocker)
            ? Blocker
            : client.Blocker;
        RuntimeStatus = string.IsNullOrWhiteSpace(sessionId)
            ? pendingAssignment != null ? "Waiting for assignment acknowledgement" : "Waiting for a fresh assignment"
            : "Newb paired; local healing and attacking are disabled";
        HealingStatus = status?.Status.HealingState ?? "Waiting for remote HealBot status.";
        ChaseStatus = status?.Status.ChaseState ?? "Waiting for remote chase status.";
    }

    private void UpdateNewbWorkflow()
    {
        if (client == null)
            return;

        var localPlayer = Plugin.ObjectTable.LocalPlayer;
        if (localPlayer == null || string.IsNullOrWhiteSpace(localPlayer.Name.TextValue) || localPlayer.HomeWorld.RowId == 0)
        {
            Blocker = "The local Newb identity is unavailable.";
            ReleaseSession(Blocker);
            return;
        }

        var observed = client.LastStatus;
        if (observed == null || DateTime.UtcNow - observed.ReceivedUtc > TimeSpan.FromSeconds(10))
        {
            if (!string.IsNullOrWhiteSpace(sessionId))
                ClearSessionState("The paired HealBot connection or authenticated status was lost.");
            Blocker = client.Blocker;
            return;
        }

        var peer = observed.Status;
        if (!string.IsNullOrWhiteSpace(sessionId) &&
            (!string.Equals(healBotName, peer.HealBotName, StringComparison.Ordinal) ||
             healBotWorldId != peer.HealBotWorldId))
        {
            BeginRecovery(peer, observed, "The configured endpoint or HealBot identity changed.");
            return;
        }

        if (string.IsNullOrWhiteSpace(sessionId))
        {
            if (pendingAssignment != null || pendingRecovery != null)
                return;

            if (recoveryStatusBoundaryUtc != DateTime.MinValue)
            {
                if (observed.ReceivedUtc <= recoveryStatusBoundaryUtc)
                    return;

                recoveryStatusBoundaryUtc = DateTime.MinValue;
                if (!string.IsNullOrWhiteSpace(peer.SessionId))
                {
                    Blocker = $"The paired HealBot still reports released session {peer.SessionId}.";
                    return;
                }
            }

            if (string.Equals(peer.AssignmentSource, "QST", StringComparison.Ordinal))
            {
                Blocker = "The paired HealBot has an active QST assignment.";
                return;
            }

            if (string.Equals(peer.AssignmentSource, "Newb", StringComparison.Ordinal) &&
                !string.IsNullOrWhiteSpace(peer.SessionId))
            {
                if (!string.Equals(peer.AssignedName, localPlayer.Name.TextValue.Trim(), StringComparison.Ordinal) ||
                    peer.AssignedWorldId != localPlayer.HomeWorld.RowId)
                {
                    Blocker = "The paired HealBot belongs to another Newb session.";
                    return;
                }

                BeginRecovery(peer, observed, "The paired HealBot reports a stale session for this Newb.");
                return;
            }

            if (!peer.Ready)
            {
                Blocker = string.IsNullOrWhiteSpace(peer.Blocker) ? "The paired HealBot is not ready." : peer.Blocker;
                return;
            }

            BeginAssignment(peer);
            return;
        }

        if (observed.ReceivedUtc > assignmentAcknowledgedUtc &&
            (!string.Equals(peer.AssignmentSource, "Newb", StringComparison.Ordinal) ||
             !string.Equals(peer.SessionId, sessionId, StringComparison.Ordinal)))
        {
            BeginRecovery(peer, observed, "The paired HealBot reported a missing or mismatched Newb session.");
            return;
        }

        Blocker = peer.Ready ? string.Empty : peer.Blocker;
        TrySendTravelUpdate(force: false);
    }

    private void BeginAssignment(HealBotLanStatusResponse peer)
    {
        if (client == null || pendingAssignment != null)
            return;

        var newb = Plugin.ObjectTable.LocalPlayer;
        if (newb == null)
        {
            Blocker = "The local Newb identity is unavailable.";
            return;
        }

        var candidateSessionId = Guid.NewGuid().ToString("N");
        var command = HealBotLanCommand.Empty with
        {
            Action = "AssignNewb",
            SessionId = candidateSessionId,
            NewbName = newb.Name.TextValue.Trim(),
            NewbWorldId = (ushort)newb.HomeWorld.RowId,
        };
        pendingAssignment = new PendingAssignment(
            peer.HealBotName,
            peer.HealBotWorldId,
            candidateSessionId,
            client.SendCommandAsync(Target(peer.HealBotName, peer.HealBotWorldId, command)));
        Blocker = "Waiting for the paired HealBot to acknowledge the exact Newb session.";
    }

    private void ObservePendingAssignment()
    {
        if (pendingAssignment is not { CommandTask.IsCompleted: true } pending)
            return;

        pendingAssignment = null;
        var result = GetTaskResult(pending.CommandTask, pending.SessionId, "AssignNewb");
        if (!result.Accepted)
        {
            QueueRelease(pending.HelperName, pending.HelperWorldId, pending.SessionId);
            Blocker = string.IsNullOrWhiteSpace(result.Blocker)
                ? "The paired HealBot rejected the Newb assignment."
                : result.Blocker;
            return;
        }

        sessionId = pending.SessionId;
        healBotName = pending.HelperName;
        healBotWorldId = pending.HelperWorldId;
        travelSequence = 0;
        lastAcceptedTravel = null;
        nextTravelUtc = DateTime.MinValue;
        assignmentAcknowledgedUtc = DateTime.UtcNow;
        releasedRecoverySessionId = string.Empty;
        Blocker = string.Empty;
        Plugin.Log.Information("[Coppelia][Pairing] The HealBot acknowledged the exact Newb assignment.");
        TrySendTravelUpdate(force: true);
    }

    private void ObservePendingCommand()
    {
        if (pendingCommand is not { CommandTask.IsCompleted: true } pending)
            return;

        pendingCommand = null;
        var result = GetTaskResult(pending.CommandTask, pending.Command.SessionId, pending.Command.Action);
        if (result.Accepted)
        {
            Blocker = string.Empty;
            if (pending.Command.Action == "TravelUpdate")
            {
                travelSequence = pending.Command.TravelSequence;
                lastAcceptedTravel = pending.TravelSnapshot;
                if (pending.TravelSnapshot?.Teleport is { } acceptedTeleport)
                {
                    lock (teleportGate)
                    {
                        if (pendingTeleport == acceptedTeleport)
                            pendingTeleport = null;
                    }
                }
            }
            return;
        }

        Blocker = string.IsNullOrWhiteSpace(result.Blocker)
            ? $"The paired HealBot rejected {pending.Command.Action}."
            : result.Blocker;
        ReleaseSession($"The paired HealBot did not accept {pending.Command.Action} for the active session.");
    }

    private void TrySendTravelUpdate(bool force)
    {
        if (client == null || pendingCommand != null || string.IsNullOrWhiteSpace(sessionId))
            return;

        var newb = Plugin.ObjectTable.LocalPlayer;
        if (newb == null)
            return;

        PendingTeleport? teleport;
        lock (teleportGate)
            teleport = pendingTeleport;
        if (teleport is { Name: null })
        {
            var name = Plugin.DataManager.GetExcelSheet<Aetheryte>().TryGetRow(teleport.AetheryteId, out var aetheryte)
                ? aetheryte.PlaceName.ValueNullable?.Name.ExtractText()
                : null;
            teleport = teleport with { Name = name };
            lock (teleportGate)
                pendingTeleport = teleport;
        }

        var snapshot = new TravelSnapshot(
            (ushort)newb.CurrentWorld.RowId,
            Plugin.ClientState.TerritoryType,
            newb.Position,
            Plugin.Condition[ConditionFlag.Mounted] || Plugin.Condition[ConditionFlag.Mounting71],
            Plugin.Condition[ConditionFlag.InFlight],
            teleport);
        if (!force && DateTime.UtcNow < nextTravelUtc)
            return;
        if (!force && !ShouldSendTravelSnapshot(snapshot))
            return;

        var command = HealBotLanCommand.Empty with
        {
            Action = "TravelUpdate",
            SessionId = sessionId,
            NewbName = newb.Name.TextValue.Trim(),
            NewbWorldId = (ushort)newb.HomeWorld.RowId,
            NewbCurrentWorldId = snapshot.CurrentWorldId,
            TerritoryId = snapshot.TerritoryId,
            X = snapshot.Position.X,
            Y = snapshot.Position.Y,
            Z = snapshot.Position.Z,
            TravelSequence = travelSequence + 1,
            AetheryteId = snapshot.Teleport?.AetheryteId,
            AetheryteSubIndex = snapshot.Teleport?.SubIndex,
            AetheryteName = snapshot.Teleport?.Name,
            NewbMounted = snapshot.Mounted,
            NewbFlying = snapshot.Flying,
        };
        pendingCommand = new PendingCommand(
            command,
            snapshot,
            client.SendCommandAsync(Target(healBotName, healBotWorldId, command)));
        nextTravelUtc = DateTime.UtcNow + TravelInterval;
    }

    private bool ShouldSendTravelSnapshot(TravelSnapshot snapshot)
    {
        if (lastAcceptedTravel == null || snapshot.Teleport != null)
            return true;

        return snapshot.CurrentWorldId != lastAcceptedTravel.CurrentWorldId ||
               snapshot.TerritoryId != lastAcceptedTravel.TerritoryId ||
               snapshot.Mounted != lastAcceptedTravel.Mounted ||
               snapshot.Flying != lastAcceptedTravel.Flying ||
               Vector3.DistanceSquared(snapshot.Position, lastAcceptedTravel.Position) >= 9f;
    }

    private void BeginRecovery(HealBotLanStatusResponse peer, ObservedHealBotStatus observed, string reason)
    {
        if (client == null || pendingRecovery != null)
            return;

        var remoteSessionId = peer.SessionId;
        if (!string.IsNullOrWhiteSpace(sessionId))
            ClearSessionState(reason);
        Blocker = reason;

        if (!string.Equals(peer.AssignmentSource, "Newb", StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(remoteSessionId))
        {
            recoveryStatusBoundaryUtc = observed.ReceivedUtc;
            return;
        }

        var localPlayer = Plugin.ObjectTable.LocalPlayer;
        if (localPlayer == null ||
            !string.Equals(peer.AssignedName, localPlayer.Name.TextValue.Trim(), StringComparison.Ordinal) ||
            peer.AssignedWorldId != localPlayer.HomeWorld.RowId)
        {
            Blocker = "The paired HealBot belongs to another Newb session.";
            return;
        }

        if (string.Equals(releasedRecoverySessionId, remoteSessionId, StringComparison.Ordinal))
        {
            Blocker = $"The paired HealBot still reports released session {remoteSessionId}.";
            return;
        }

        releasedRecoverySessionId = remoteSessionId;
        var release = HealBotLanCommand.Empty with { Action = "Release", SessionId = remoteSessionId };
        pendingRecovery = new PendingRecovery(
            remoteSessionId,
            client.SendCommandAsync(Target(peer.HealBotName, peer.HealBotWorldId, release)));
        Plugin.Log.Information("[Coppelia][Pairing] Releasing a stale Newb session before reassignment.");
    }

    private void ObservePendingRecovery()
    {
        if (pendingRecovery is not { CommandTask.IsCompleted: true } pending)
            return;

        pendingRecovery = null;
        var result = GetTaskResult(pending.CommandTask, pending.SessionId, "Release");
        recoveryStatusBoundaryUtc = DateTime.UtcNow;
        if (!result.Accepted)
        {
            Blocker = string.IsNullOrWhiteSpace(result.Blocker)
                ? "The paired HealBot did not acknowledge its stale-session release."
                : result.Blocker;
        }
    }

    private void ReleaseSession(string reason)
    {
        if (pendingAssignment != null)
        {
            QueueRelease(pendingAssignment.HelperName, pendingAssignment.HelperWorldId, pendingAssignment.SessionId);
            pendingAssignment = null;
        }
        if (!string.IsNullOrWhiteSpace(sessionId))
            QueueRelease(healBotName, healBotWorldId, sessionId);

        ClearSessionState(reason);
    }

    private void QueueRelease(string helperName, ushort helperWorldId, string releaseSessionId)
    {
        if (client == null || !client.IsConnected)
            return;

        var release = HealBotLanCommand.Empty with { Action = "Release", SessionId = releaseSessionId };
        _ = ObserveReleaseAsync(client.SendCommandAsync(Target(helperName, helperWorldId, release)));
    }

    private static async Task ObserveReleaseAsync(Task<HealBotLanCommandResult> task)
    {
        try
        {
            var result = await task.ConfigureAwait(false);
            if (!result.Accepted)
                Plugin.Log.Debug($"[Coppelia][Pairing] Release was not acknowledged: {result.Blocker}");
        }
        catch (Exception ex)
        {
            Plugin.Log.Debug(ex, "[Coppelia][Pairing] Release ended during shutdown.");
        }
    }

    private void ClearSessionState(string reason)
    {
        sessionId = string.Empty;
        healBotName = string.Empty;
        healBotWorldId = 0;
        travelSequence = 0;
        nextTravelUtc = DateTime.MinValue;
        assignmentAcknowledgedUtc = DateTime.MinValue;
        pendingCommand = null;
        lastAcceptedTravel = null;
        if (!string.IsNullOrWhiteSpace(reason))
            Blocker = reason;
    }

    private void StopResources(string reason)
    {
        StopClient(reason);
        StopServer(reason);
        ConnectionStatus = "Stopped";
        PairingIdentity = "None";
        Blocker = reason;
        RuntimeStatus = reason;
        HealingStatus = plugin.Configuration.BotMode == BotMode.HealBot
            ? plugin.HealbotRuntimeService.StatusText
            : "Not paired.";
        ChaseStatus = plugin.CoppeliaTravelService.State;
    }

    private void StopClient(string reason)
    {
        if (client == null && teleportObserver == null && string.IsNullOrWhiteSpace(sessionId) && pendingAssignment == null)
            return;

        ReleaseSession(reason);
        pendingRecovery = null;
        recoveryStatusBoundaryUtc = DateTime.MinValue;
        releasedRecoverySessionId = string.Empty;
        if (teleportObserver != null)
        {
            teleportObserver.OnTeleportAccepted -= OnTeleportAccepted;
            teleportObserver.Dispose();
            teleportObserver = null;
        }
        client?.Dispose();
        client = null;
        clientSignature = string.Empty;
        lock (teleportGate)
            pendingTeleport = null;
    }

    private void StopServer(string reason)
    {
        server?.Dispose();
        server = null;
        serverSignature = string.Empty;
        var assignment = plugin.CoppeliaQstIpcService.GetAssignmentSnapshot();
        if (assignment.Source == "Newb")
            plugin.CoppeliaQstIpcService.ReleaseNewb(assignment.SessionId, reason);
    }

    private void OnTeleportAccepted(uint aetheryteId, byte subIndex)
    {
        lock (teleportGate)
            pendingTeleport = new PendingTeleport(aetheryteId, subIndex, null);
    }

    private string FormatAssignmentIdentity()
    {
        var assignment = plugin.CoppeliaQstIpcService.GetAssignmentSnapshot();
        return string.IsNullOrWhiteSpace(assignment.Source)
            ? "None"
            : $"{assignment.Source}: {FormatIdentity(assignment.Name, assignment.WorldId)}";
    }

    private static string FormatIdentity(string name, ushort worldId)
    {
        if (string.IsNullOrWhiteSpace(name) || worldId == 0)
            return "None";

        var worldSheet = Plugin.DataManager.GetExcelSheet<World>();
        return worldSheet.TryGetRow(worldId, out var world) && !world.Name.IsEmpty
            ? $"{name}@{world.Name.ExtractText()}"
            : $"{name}@{worldId}";
    }

    private static HealBotLanTargetedCommand Target(string helperName, ushort helperWorldId, HealBotLanCommand command)
        => new(helperName, helperWorldId, command);

    private static HealBotLanCommandResult GetTaskResult(
        Task<HealBotLanCommandResult> task,
        string session,
        string action)
    {
        try
        {
            return task.GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            return new HealBotLanCommandResult(string.Empty, action, session, false, ex.Message);
        }
    }

    private sealed record PendingAssignment(
        string HelperName,
        ushort HelperWorldId,
        string SessionId,
        Task<HealBotLanCommandResult> CommandTask);

    private sealed record PendingCommand(
        HealBotLanCommand Command,
        TravelSnapshot TravelSnapshot,
        Task<HealBotLanCommandResult> CommandTask);

    private sealed record PendingRecovery(
        string SessionId,
        Task<HealBotLanCommandResult> CommandTask);

    private sealed record PendingTeleport(uint AetheryteId, byte SubIndex, string? Name);

    private sealed record TravelSnapshot(
        ushort CurrentWorldId,
        uint TerritoryId,
        Vector3 Position,
        bool Mounted,
        bool Flying,
        PendingTeleport? Teleport);
}

internal sealed class HealBotLanServer : IDisposable
{
    private readonly Plugin plugin;
    private readonly int port;
    private readonly string secret;
    private readonly object clientsGate = new();
    private readonly HashSet<TcpClient> clients = [];
    private readonly Dictionary<TcpClient, string> sessions = [];
    private readonly HealBotLanReplayCache replayCache = new();
    private readonly SemaphoreSlim sendGate = new(1, 1);

    private TcpListener? listener;
    private CancellationTokenSource? cancellationTokenSource;
    private Task? acceptTask;
    private int disposed;

    public HealBotLanServer(Plugin plugin, int port, string secret)
    {
        this.plugin = plugin;
        this.port = port;
        this.secret = secret;
    }

    public bool IsRunning { get; private set; }
    public string ListeningBlocker { get; private set; } = string.Empty;
    public int ConnectedClientCount
    {
        get
        {
            lock (clientsGate)
                return clients.Count;
        }
    }

    public void Start()
    {
        if (Volatile.Read(ref disposed) != 0 || IsRunning)
            return;

        try
        {
            listener = new TcpListener(IPAddress.Any, port);
            listener.Start();
            cancellationTokenSource = new CancellationTokenSource();
            IsRunning = true;
            ListeningBlocker = string.Empty;
            acceptTask = AcceptClientsAsync(cancellationTokenSource.Token);
            Plugin.Log.Information($"[Coppelia][Pairing] Listening for one Newb on TCP {port}.");
        }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.AddressAlreadyInUse)
        {
            ListeningBlocker = $"TCP port {port} is already in use.";
            Plugin.Log.Warning($"[Coppelia][Pairing] {ListeningBlocker}");
        }
        catch (Exception ex)
        {
            ListeningBlocker = $"The pairing listener could not start: {ex.Message}";
            Plugin.Log.Warning(ex, "[Coppelia][Pairing] Listener startup failed.");
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
            return;

        IsRunning = false;
        cancellationTokenSource?.Cancel();
        try { listener?.Stop(); } catch { }
        listener = null;

        List<TcpClient> connected;
        lock (clientsGate)
        {
            connected = clients.ToList();
            clients.Clear();
            sessions.Clear();
        }
        foreach (var client in connected)
        {
            try { client.Close(); } catch { }
            try { client.Dispose(); } catch { }
        }
        replayCache.Clear();
        _ = ObserveShutdownAsync(acceptTask, cancellationTokenSource);
        acceptTask = null;
        cancellationTokenSource = null;
    }

    private async Task AcceptClientsAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested && IsRunning)
        {
            try
            {
                var client = await listener!.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
                ConfigureSocket(client);
                lock (clientsGate)
                    clients.Add(client);
                _ = HandleClientAsync(client, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (Exception ex)
            {
                Plugin.Log.Warning(ex, "[Coppelia][Pairing] Failed to accept a Newb connection.");
                try { await Task.Delay(1000, cancellationToken).ConfigureAwait(false); } catch { break; }
            }
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken cancellationToken)
    {
        var endpoint = client.Client.RemoteEndPoint?.ToString() ?? "unknown";
        try
        {
            using var stream = client.GetStream();
            while (!cancellationToken.IsCancellationRequested && client.Connected)
            {
                var line = await HealBotLanContract.ReadFrameAsync(stream, cancellationToken).ConfigureAwait(false);
                if (string.IsNullOrEmpty(line))
                    break;

                HealBotLanEnvelope? message;
                try
                {
                    message = JsonSerializer.Deserialize<HealBotLanEnvelope>(line, HealBotLanContract.JsonOptions);
                }
                catch (JsonException)
                {
                    Plugin.Log.Warning($"[Coppelia][Pairing] Rejected malformed pairing JSON from {endpoint}.");
                    break;
                }

                var failure = "Malformed pairing envelope.";
                if (message == null || !HealBotLanContract.TryValidate(message, secret, replayCache, out failure))
                {
                    Plugin.Log.Warning($"[Coppelia][Pairing] Rejected pairing envelope from {endpoint}: {failure}");
                    break;
                }

                await HandleMessageAsync(client, message, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Plugin.Log.Debug(ex, $"[Coppelia][Pairing] Newb connection {endpoint} ended.");
        }
        finally
        {
            string lostSession;
            lock (clientsGate)
            {
                clients.Remove(client);
                lostSession = sessions.Remove(client, out var session) ? session : string.Empty;
            }
            if (!string.IsNullOrWhiteSpace(lostSession) && Volatile.Read(ref disposed) == 0)
            {
                try
                {
                    await Plugin.Framework.RunOnFrameworkThread(() =>
                        plugin.CoppeliaQstIpcService.ReleaseNewb(
                            lostSession,
                            "The paired Newb connection was lost.")).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Plugin.Log.Debug(ex, "[Coppelia][Pairing] Disconnect cleanup ended during shutdown.");
                }
            }
            try { client.Close(); } catch { }
            try { client.Dispose(); } catch { }
        }
    }

    private async Task HandleMessageAsync(
        TcpClient client,
        HealBotLanEnvelope message,
        CancellationToken cancellationToken)
    {
        if (message.Type == HealBotLanMessageType.StatusRequest)
        {
            var request = message.GetData<HealBotLanStatusRequest>();
            var status = await Plugin.Framework.RunOnFrameworkThread(() =>
                plugin.HealBotPairingService.BuildServerStatus(message.MessageId, request)).ConfigureAwait(false);
            await SendAsync(
                client,
                HealBotLanEnvelope.Create(HealBotLanMessageType.StatusResponse, status),
                cancellationToken).ConfigureAwait(false);
            return;
        }

        if (message.Type is not (HealBotLanMessageType.AssignNewb or
            HealBotLanMessageType.TravelUpdate or
            HealBotLanMessageType.Release))
        {
            return;
        }

        HealBotLanTargetedCommand? targeted = null;
        try
        {
            targeted = message.GetData<HealBotLanTargetedCommand>();
        }
        catch (JsonException)
        {
        }

        var ownershipBlocker = ValidateConnectionOwnership(client, message.Type, targeted);
        if (!string.IsNullOrWhiteSpace(ownershipBlocker))
        {
            await SendAsync(
                client,
                HealBotLanEnvelope.Create(
                    HealBotLanMessageType.CommandResult,
                    HealBotLanCommandResult.Rejected(message.MessageId, targeted, ownershipBlocker)),
                cancellationToken).ConfigureAwait(false);
            return;
        }

        var result = await Plugin.Framework.RunOnFrameworkThread(() =>
            Volatile.Read(ref disposed) != 0
                ? HealBotLanCommandResult.Rejected(
                    message.MessageId,
                    targeted,
                    "The HealBot pairing listener is stopping.")
                : plugin.HealBotPairingService.HandleServerCommand(message.MessageId, targeted)).ConfigureAwait(false);
        if (result.Accepted && targeted != null)
        {
            lock (clientsGate)
            {
                if (targeted.Command.Action == "AssignNewb")
                    sessions[client] = targeted.Command.SessionId;
                else if (targeted.Command.Action == "Release")
                    sessions.Remove(client);
            }
        }

        await SendAsync(
            client,
            HealBotLanEnvelope.Create(HealBotLanMessageType.CommandResult, result),
            cancellationToken).ConfigureAwait(false);
    }

    private string ValidateConnectionOwnership(
        TcpClient connection,
        HealBotLanMessageType messageType,
        HealBotLanTargetedCommand? targeted)
    {
        if (targeted == null)
            return string.Empty;

        var command = targeted.Command;
        var expectedType = command.Action switch
        {
            "AssignNewb" => HealBotLanMessageType.AssignNewb,
            "TravelUpdate" => HealBotLanMessageType.TravelUpdate,
            "Release" => HealBotLanMessageType.Release,
            _ => messageType,
        };
        if (messageType != expectedType)
            return "The pairing message type does not match its command.";

        lock (clientsGate)
        {
            if (command.Action == "AssignNewb")
            {
                if (sessions.TryGetValue(connection, out var ownedSession) &&
                    !string.Equals(ownedSession, command.SessionId, StringComparison.Ordinal))
                {
                    return "This connection already owns a different Newb session.";
                }

                if (sessions.Any(pair =>
                        !ReferenceEquals(pair.Key, connection) &&
                        string.Equals(pair.Value, command.SessionId, StringComparison.Ordinal)))
                {
                    return "The Newb session already belongs to another active connection.";
                }
            }
            else if (command.Action == "TravelUpdate")
            {
                if (!sessions.TryGetValue(connection, out var ownedSession) ||
                    !string.Equals(ownedSession, command.SessionId, StringComparison.Ordinal))
                {
                    return "This connection does not own the active Newb session.";
                }
            }
            else if (command.Action == "Release" &&
                     sessions.Any(pair =>
                         !ReferenceEquals(pair.Key, connection) &&
                         string.Equals(pair.Value, command.SessionId, StringComparison.Ordinal)))
            {
                return "The Newb session still belongs to another active connection.";
            }
        }

        return string.Empty;
    }

    private async Task SendAsync(
        TcpClient client,
        HealBotLanEnvelope message,
        CancellationToken cancellationToken)
    {
        var bytes = HealBotLanContract.SerializeFrame(message, secret);
        await sendGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await client.GetStream().WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            sendGate.Release();
        }
    }

    private static void ConfigureSocket(TcpClient client)
    {
        client.SendTimeout = 5000;
        client.ReceiveTimeout = 60000;
        client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
        client.Client.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveTime, 30);
        client.Client.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveInterval, 5);
    }

    private static async Task ObserveShutdownAsync(Task? task, CancellationTokenSource? source)
    {
        try
        {
            if (task != null)
                await task.ConfigureAwait(false);
        }
        catch
        {
        }
        finally
        {
            source?.Dispose();
        }
    }
}

internal sealed class HealBotLanClient : IDisposable
{
    private readonly string address;
    private readonly int port;
    private readonly string secret;
    private readonly object gate = new();
    private readonly HealBotLanReplayCache replayCache = new();
    private readonly Dictionary<string, PendingStatus> pendingStatuses = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PendingCommandResult> pendingCommands = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim sendGate = new(1, 1);

    private CancellationTokenSource? cancellationTokenSource;
    private TcpClient? client;
    private Task? monitorTask;
    private long generation;
    private int reconnectFailures;
    private int disposed;
    private string connectionState = "Disconnected";
    private string blocker = string.Empty;
    private ObservedHealBotStatus? lastStatus;

    public HealBotLanClient(string address, int port, string secret)
    {
        this.address = address;
        this.port = port;
        this.secret = secret;
    }

    public bool IsConnected
    {
        get
        {
            lock (gate)
                return client?.Connected == true;
        }
    }

    public string ConnectionState
    {
        get
        {
            lock (gate)
                return connectionState;
        }
        private set
        {
            lock (gate)
                connectionState = value;
        }
    }

    public string Blocker
    {
        get
        {
            lock (gate)
                return blocker;
        }
        private set
        {
            lock (gate)
                blocker = value;
        }
    }

    public ObservedHealBotStatus? LastStatus
    {
        get
        {
            lock (gate)
                return lastStatus;
        }
        private set
        {
            lock (gate)
                lastStatus = value;
        }
    }

    public void Start()
    {
        if (Volatile.Read(ref disposed) != 0 || cancellationTokenSource != null)
            return;

        cancellationTokenSource = new CancellationTokenSource();
        monitorTask = Task.Run(() => MonitorAsync(cancellationTokenSource.Token));
    }

    public async Task<HealBotLanCommandResult> SendCommandAsync(HealBotLanTargetedCommand command)
    {
        TcpClient? active;
        long expectedGeneration;
        lock (gate)
        {
            active = client;
            expectedGeneration = generation;
        }
        if (active?.Connected != true || expectedGeneration == 0)
        {
            return HealBotLanCommandResult.Rejected(
                string.Empty,
                command,
                string.IsNullOrWhiteSpace(Blocker) ? "The paired HealBot is not connected." : Blocker);
        }

        var type = command.Command.Action switch
        {
            "AssignNewb" => HealBotLanMessageType.AssignNewb,
            "TravelUpdate" => HealBotLanMessageType.TravelUpdate,
            "Release" => HealBotLanMessageType.Release,
            _ => HealBotLanMessageType.Release,
        };
        var message = HealBotLanEnvelope.Create(type, command);
        var completion = new TaskCompletionSource<HealBotLanCommandResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (gate)
        {
            pendingCommands[message.MessageId] = new PendingCommandResult(
                expectedGeneration,
                command.Command.Action,
                command.Command.SessionId,
                completion);
        }

        if (!await SendAsync(active, expectedGeneration, message, cancellationTokenSource?.Token ?? CancellationToken.None).ConfigureAwait(false))
        {
            lock (gate)
                pendingCommands.Remove(message.MessageId);
            return HealBotLanCommandResult.Rejected(message.MessageId, command, Blocker);
        }

        var finished = await Task.WhenAny(completion.Task, Task.Delay(TimeSpan.FromSeconds(5))).ConfigureAwait(false);
        lock (gate)
            pendingCommands.Remove(message.MessageId);
        return finished == completion.Task
            ? await completion.Task.ConfigureAwait(false)
            : HealBotLanCommandResult.Rejected(message.MessageId, command, "The paired HealBot did not acknowledge the command.");
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
            return;

        cancellationTokenSource?.Cancel();
        DropConnection("The paired connection was closed.");
        replayCache.Clear();
        _ = ObserveShutdownAsync(monitorTask, cancellationTokenSource);
        monitorTask = null;
        cancellationTokenSource = null;
    }

    public static async Task<HealBotLanProbeResult> ProbeAsync(IPAddress address, int port, string secret)
    {
        using var client = new TcpClient();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var replayCache = new HealBotLanReplayCache();
        try
        {
            ConfigureSocket(client);
            await client.ConnectAsync(address, port, cancellation.Token).ConfigureAwait(false);
            var request = HealBotLanEnvelope.Create(
                HealBotLanMessageType.StatusRequest,
                new HealBotLanStatusRequest());
            var bytes = HealBotLanContract.SerializeFrame(request, secret);
            await client.GetStream().WriteAsync(bytes, cancellation.Token).ConfigureAwait(false);
            var line = await HealBotLanContract.ReadFrameAsync(client.GetStream(), cancellation.Token).ConfigureAwait(false);
            if (string.IsNullOrEmpty(line))
                return new HealBotLanProbeResult(null, "The HealBot endpoint closed before returning authenticated status.");

            var envelope = JsonSerializer.Deserialize<HealBotLanEnvelope>(line, HealBotLanContract.JsonOptions);
            var failure = "Malformed pairing envelope.";
            if (envelope == null || !HealBotLanContract.TryValidate(envelope, secret, replayCache, out failure))
                return new HealBotLanProbeResult(null, failure);
            if (envelope.Type != HealBotLanMessageType.StatusResponse)
                return new HealBotLanProbeResult(null, "The endpoint returned an incompatible pairing response.");

            var status = envelope.GetData<HealBotLanStatusResponse>();
            if (status == null || !string.Equals(status.RequestId, request.MessageId, StringComparison.Ordinal))
                return new HealBotLanProbeResult(null, "The endpoint returned an uncorrelated pairing status.");
            return new HealBotLanProbeResult(status, string.Empty);
        }
        catch (OperationCanceledException)
        {
            return new HealBotLanProbeResult(null, $"The HealBot endpoint at {address}:{port} did not return authenticated status in time.");
        }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionRefused)
        {
            return new HealBotLanProbeResult(null, $"No HealBot is listening at {address}:{port}.");
        }
        catch (Exception ex)
        {
            return new HealBotLanProbeResult(null, $"Could not authenticate the HealBot at {address}:{port}: {ex.Message}");
        }
    }

    private async Task MonitorAsync(CancellationToken cancellationToken)
    {
        var nextReconnectUtc = DateTime.MinValue;
        var nextStatusUtc = DateTime.MinValue;
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                if (!IsConnected)
                {
                    ConnectionState = "Connecting";
                    if (DateTime.UtcNow >= nextReconnectUtc)
                    {
                        if (await ConnectAsync(cancellationToken).ConfigureAwait(false))
                        {
                            nextStatusUtc = DateTime.MinValue;
                            nextReconnectUtc = DateTime.MinValue;
                        }
                        else
                        {
                            nextReconnectUtc = ScheduleReconnect();
                        }
                    }

                    await Task.Delay(250, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                if (DateTime.UtcNow >= nextStatusUtc)
                {
                    ConnectionState = "Authenticating";
                    var status = await RequestStatusAsync(cancellationToken).ConfigureAwait(false);
                    if (status == null)
                    {
                        DropConnection(string.IsNullOrWhiteSpace(Blocker)
                            ? "The paired HealBot did not return correlated authenticated status. Check the pair secret and protocol."
                            : Blocker);
                        nextReconnectUtc = ScheduleReconnect();
                        nextStatusUtc = DateTime.MinValue;
                    }
                    else
                    {
                        reconnectFailures = 0;
                        LastStatus = new ObservedHealBotStatus(status, DateTime.UtcNow);
                        Blocker = status.Ready ? string.Empty : status.Blocker;
                        ConnectionState = "Connected";
                        nextStatusUtc = DateTime.UtcNow.AddSeconds(5);
                    }
                }

                await Task.Delay(250, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Blocker = $"Pairing monitor failed: {ex.Message}";
                DropConnection(Blocker);
                nextReconnectUtc = ScheduleReconnect();
                try { await Task.Delay(1000, cancellationToken).ConfigureAwait(false); } catch { break; }
            }
        }
    }

    private async Task<bool> ConnectAsync(CancellationToken cancellationToken)
    {
        TcpClient? candidate = null;
        try
        {
            candidate = new TcpClient();
            ConfigureSocket(candidate);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            await candidate.ConnectAsync(address, port, timeout.Token).ConfigureAwait(false);
            var nextGeneration = Interlocked.Increment(ref generation);
            lock (gate)
            {
                if (Volatile.Read(ref disposed) != 0)
                {
                    candidate.Dispose();
                    return false;
                }
                client = candidate;
            }
            _ = ListenAsync(candidate, nextGeneration, cancellationToken);
            ConnectionState = "Authenticating";
            Blocker = "Connected; waiting for authenticated HealBot status.";
            return true;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            candidate?.Dispose();
            Blocker = $"Connection to {address}:{port} timed out.";
            return false;
        }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionRefused)
        {
            candidate?.Dispose();
            Blocker = $"No HealBot is listening at {address}:{port}.";
            return false;
        }
        catch (Exception ex)
        {
            candidate?.Dispose();
            Blocker = $"Connection to {address}:{port} failed: {ex.Message}";
            return false;
        }
    }

    private async Task ListenAsync(TcpClient connected, long expectedGeneration, CancellationToken cancellationToken)
    {
        try
        {
            using var stream = connected.GetStream();
            while (!cancellationToken.IsCancellationRequested && connected.Connected)
            {
                var line = await HealBotLanContract.ReadFrameAsync(stream, cancellationToken).ConfigureAwait(false);
                if (string.IsNullOrEmpty(line))
                    break;

                HealBotLanEnvelope? message;
                try
                {
                    message = JsonSerializer.Deserialize<HealBotLanEnvelope>(line, HealBotLanContract.JsonOptions);
                }
                catch (JsonException)
                {
                    Blocker = "The paired endpoint returned malformed pairing JSON.";
                    break;
                }
                var failure = "Malformed pairing envelope.";
                if (message == null || !HealBotLanContract.TryValidate(message, secret, replayCache, out failure))
                {
                    Blocker = failure;
                    break;
                }

                HandleMessage(message, expectedGeneration);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Blocker = $"The paired HealBot connection was lost: {ex.Message}";
        }
        finally
        {
            lock (gate)
            {
                if (ReferenceEquals(client, connected) && generation == expectedGeneration)
                {
                    client = null;
                    LastStatus = null;
                    FailPending(expectedGeneration, string.IsNullOrWhiteSpace(Blocker)
                        ? "The paired HealBot connection was lost."
                        : Blocker);
                }
            }
            try { connected.Close(); } catch { }
            try { connected.Dispose(); } catch { }
        }
    }

    private void HandleMessage(HealBotLanEnvelope message, long expectedGeneration)
    {
        if (message.Type == HealBotLanMessageType.StatusResponse)
        {
            var status = message.GetData<HealBotLanStatusResponse>();
            if (status == null || string.IsNullOrWhiteSpace(status.RequestId))
                return;

            PendingStatus? pending;
            lock (gate)
            {
                if (!pendingStatuses.Remove(status.RequestId, out pending))
                    return;
            }
            pending.Completion.TrySetResult(pending.Generation == expectedGeneration ? status : null);
            return;
        }

        if (message.Type != HealBotLanMessageType.CommandResult)
            return;

        var result = message.GetData<HealBotLanCommandResult>();
        if (result == null)
            return;
        PendingCommandResult? pendingCommand;
        lock (gate)
        {
            if (!pendingCommands.Remove(result.RequestId, out pendingCommand))
                return;
        }
        if (pendingCommand.Generation != expectedGeneration ||
            !string.Equals(pendingCommand.Action, result.Action, StringComparison.Ordinal) ||
            !string.Equals(pendingCommand.SessionId, result.SessionId, StringComparison.Ordinal))
        {
            pendingCommand.Completion.TrySetResult(new HealBotLanCommandResult(
                result.RequestId,
                pendingCommand.Action,
                pendingCommand.SessionId,
                false,
                "The paired HealBot returned a mismatched command response."));
            return;
        }
        pendingCommand.Completion.TrySetResult(result);
    }

    private async Task<HealBotLanStatusResponse?> RequestStatusAsync(CancellationToken cancellationToken)
    {
        TcpClient? active;
        long expectedGeneration;
        lock (gate)
        {
            active = client;
            expectedGeneration = generation;
        }
        if (active?.Connected != true || expectedGeneration == 0)
            return null;

        var message = HealBotLanEnvelope.Create(
            HealBotLanMessageType.StatusRequest,
            new HealBotLanStatusRequest());
        var completion = new TaskCompletionSource<HealBotLanStatusResponse?>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (gate)
            pendingStatuses[message.MessageId] = new PendingStatus(expectedGeneration, completion);

        if (!await SendAsync(active, expectedGeneration, message, cancellationToken).ConfigureAwait(false))
        {
            lock (gate)
                pendingStatuses.Remove(message.MessageId);
            return null;
        }

        var finished = await Task.WhenAny(completion.Task, Task.Delay(TimeSpan.FromSeconds(2), cancellationToken)).ConfigureAwait(false);
        lock (gate)
            pendingStatuses.Remove(message.MessageId);
        return finished == completion.Task ? await completion.Task.ConfigureAwait(false) : null;
    }

    private async Task<bool> SendAsync(
        TcpClient expectedClient,
        long expectedGeneration,
        HealBotLanEnvelope message,
        CancellationToken cancellationToken)
    {
        lock (gate)
        {
            if (!ReferenceEquals(client, expectedClient) || generation != expectedGeneration || expectedClient.Connected != true)
                return false;
        }

        try
        {
            var bytes = HealBotLanContract.SerializeFrame(message, secret);
            await sendGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await expectedClient.GetStream().WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                sendGate.Release();
            }
            return true;
        }
        catch (Exception ex)
        {
            Blocker = $"The paired connection failed: {ex.Message}";
            DropConnection(Blocker);
            return false;
        }
    }

    private DateTime ScheduleReconnect()
    {
        reconnectFailures++;
        var delaySeconds = reconnectFailures switch
        {
            1 => 1,
            2 => 2,
            3 => 5,
            _ => 10,
        };
        ConnectionState = $"Reconnect in {delaySeconds}s";
        return DateTime.UtcNow.AddSeconds(delaySeconds);
    }

    private void DropConnection(string blocker)
    {
        TcpClient? dropped;
        long droppedGeneration;
        lock (gate)
        {
            dropped = client;
            droppedGeneration = generation;
            client = null;
            LastStatus = null;
            FailPending(droppedGeneration, blocker);
        }
        Blocker = blocker;
        ConnectionState = "Disconnected";
        try { dropped?.Close(); } catch { }
        try { dropped?.Dispose(); } catch { }
    }

    private void FailPending(long failedGeneration, string blocker)
    {
        foreach (var pair in pendingStatuses
                     .Where(pair => pair.Value.Generation == failedGeneration)
                     .ToList())
        {
            pendingStatuses.Remove(pair.Key);
            pair.Value.Completion.TrySetResult(null);
        }
        foreach (var pair in pendingCommands
                     .Where(pair => pair.Value.Generation == failedGeneration)
                     .ToList())
        {
            pendingCommands.Remove(pair.Key);
            pair.Value.Completion.TrySetResult(new HealBotLanCommandResult(
                pair.Key,
                pair.Value.Action,
                pair.Value.SessionId,
                false,
                blocker));
        }
    }

    private static void ConfigureSocket(TcpClient client)
    {
        client.SendTimeout = 5000;
        client.ReceiveTimeout = 60000;
        client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
        client.Client.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveTime, 30);
        client.Client.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveInterval, 5);
    }

    private static async Task ObserveShutdownAsync(Task? task, CancellationTokenSource? source)
    {
        try
        {
            if (task != null)
                await task.ConfigureAwait(false);
        }
        catch
        {
        }
        finally
        {
            source?.Dispose();
        }
    }

    private sealed record PendingStatus(
        long Generation,
        TaskCompletionSource<HealBotLanStatusResponse?> Completion);

    private sealed record PendingCommandResult(
        long Generation,
        string Action,
        string SessionId,
        TaskCompletionSource<HealBotLanCommandResult> Completion);
}

internal sealed record ObservedHealBotStatus(HealBotLanStatusResponse Status, DateTime ReceivedUtc);
internal sealed record HealBotLanProbeResult(HealBotLanStatusResponse? Status, string Failure);

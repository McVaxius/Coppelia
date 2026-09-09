using System.Text.Json;
using Coppelia.Models;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Ipc;

namespace Coppelia.Services;

internal sealed class CoppeliaQstIpcService : IDisposable
{
    private const string StatusEndpoint = "Coppelia.QST.Status";
    private const string CommandEndpoint = "Coppelia.QST.Command";
    private const string CompanionSummoningEndpoint = "Coppelia.QST.SetCompanionSummoning";
    private const string JoatFullRsrRotationEndpoint = "Coppelia.QST.SetJoatFullRsrRotation";
    private const string FrenStatusEndpoint = "FrenRider.Coppelia.Powerlevel.Status";
    private const string FrenConfigureEndpoint = "FrenRider.Dad.ConfigureAndEnable";
    private const string FrenClearEndpoint = "FrenRider.CombatOnly.ClearFrenName";
    private const string AdsStatusEndpoint = "ADS.GetStatusJson";
    private const string AdsStartInsideEndpoint = "ADS.StartDutyFromInside";
    private const string AdsIsDutyOwnedEndpoint = "ADS.IsDutyOwned";
    private const string DadContentHasPathEndpoint = "dad.Duty.ContentHasPath";
    private const string DadSetConfigEndpoint = "dad.Duty.SetConfig";
    private const string DadRunEndpoint = "dad.Duty.Run";
    private const string DadIsStoppedEndpoint = "dad.Duty.IsStopped";
    private const string DadStopEndpoint = "dad.Duty.Stop";

    private readonly Plugin plugin;
    private readonly CoppeliaTravelService travelService;
    private readonly CoppeliaCompanionService companionService;
    private readonly ICallGateProvider<string> statusProvider;
    private readonly ICallGateProvider<string, string> commandProvider;
    private readonly ICallGateProvider<string, string> healRiderCommandProvider;
    private readonly ICallGateProvider<string, string> healRiderStatusProvider;
    private readonly ICallGateProvider<bool, bool> companionSummoningProvider;
    private readonly ICallGateProvider<bool, bool> joatFullRsrRotationProvider;
    private readonly object gate = new();

    private Assignment? assignment;
    private ActivationSnapshot? activationSnapshot;
    private ActivationOwner activationOwner;
    private string lastReleasedSessionId = string.Empty;
    private bool wasBoundByDuty;
    private DateTime boundByDutySinceUtc = DateTime.MinValue;
    private bool statusProviderRegistered;
    private bool commandProviderRegistered;
    private bool healRiderCommandRegistered;
    private bool healRiderStatusRegistered;
    private bool companionSummoningProviderRegistered;
    private bool joatFullRsrRotationProviderRegistered;
    private bool qstJoatAttackModeOwned;
    private bool qstFullRsrRotation;
    private bool started;
    private bool disposed;

    public CoppeliaQstIpcService(
        Plugin plugin,
        CoppeliaTravelService travelService,
        CoppeliaCompanionService companionService)
    {
        this.plugin = plugin;
        this.travelService = travelService;
        this.companionService = companionService;
        statusProvider = Plugin.PluginInterface.GetIpcProvider<string>(StatusEndpoint);
        commandProvider = Plugin.PluginInterface.GetIpcProvider<string, string>(CommandEndpoint);
        healRiderCommandProvider = Plugin.PluginInterface.GetIpcProvider<string, string>("HealBot.HealRider.Command.v1");
        healRiderStatusProvider = Plugin.PluginInterface.GetIpcProvider<string, string>("HealBot.HealRider.Status.v1");
        companionSummoningProvider = Plugin.PluginInterface.GetIpcProvider<bool, bool>(CompanionSummoningEndpoint);
        joatFullRsrRotationProvider = Plugin.PluginInterface.GetIpcProvider<bool, bool>(JoatFullRsrRotationEndpoint);
    }

    public bool IsJoatAttackModeQstOwned
    {
        get
        {
            lock (gate)
                return qstJoatAttackModeOwned;
        }
    }

    public bool EffectiveJoatFullRsrRotation
    {
        get
        {
            lock (gate)
                return qstJoatAttackModeOwned ? qstFullRsrRotation : plugin.Configuration.JoatFullRsrRotation;
        }
    }

    public void Start()
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (started)
                return;

            wasBoundByDuty = Plugin.Condition[ConditionFlag.BoundByDuty];
            boundByDutySinceUtc = wasBoundByDuty ? DateTime.UtcNow : DateTime.MinValue;

            try
            {
                statusProvider.RegisterFunc(GetStatusJson);
                statusProviderRegistered = true;
                commandProvider.RegisterFunc(HandleCommandJson);
                commandProviderRegistered = true;
                healRiderCommandProvider.RegisterFunc(HandleHealRiderJson);
                healRiderCommandRegistered = true;
                healRiderStatusProvider.RegisterFunc(request => HandleHealRiderJson(request, statusOnly: true));
                healRiderStatusRegistered = true;
                companionSummoningProvider.RegisterFunc(SetCompanionSummoning);
                companionSummoningProviderRegistered = true;
                joatFullRsrRotationProvider.RegisterFunc(SetJoatFullRsrRotation);
                joatFullRsrRotationProviderRegistered = true;
                started = true;
            }
            catch
            {
                UnregisterProviders();
                wasBoundByDuty = false;
                boundByDutySinceUtc = DateTime.MinValue;
                throw;
            }
        }
    }

    public void Update()
    {
        lock (gate)
        {
            if (!started || disposed)
                return;

            var inDuty = Plugin.Condition[ConditionFlag.BoundByDuty];
            if (inDuty && !wasBoundByDuty)
                boundByDutySinceUtc = DateTime.UtcNow;
            else if (!inDuty && wasBoundByDuty)
                CompleteDutyExit();
            wasBoundByDuty = inDuty;

            travelService.SuspendHealRiderMounts(assignment?.PendingDutySequence != 0 && assignment != null ||
                assignment?.DadDutyRunActive == true || assignment?.DutyOwned == true);
            travelService.Update();
            companionService.Update();
            if (!inDuty && assignment?.DadDutyRunActive == true &&
                TryGetDadStopped(out var dadStopped, out _) && dadStopped)
            {
                assignment.DadDutyRunActive = false;
                assignment.DutyState = "Assignment retained outside duty";
                RestoreFrenAfterDuty(assignment);
            }
            if (inDuty && assignment?.PendingDutySequence is > 0 &&
                DateTime.UtcNow - boundByDutySinceUtc >= TimeSpan.FromSeconds(5))
            {
                StartStableInsideDuty(assignment.PendingDutySequence);
            }
        }
    }

    public void ReleaseForDeactivation(string reason)
        => ReleaseQstForLocalRoleChange(reason);

    public (bool Ready, string Blocker) EvaluateNewbReadiness()
    {
        lock (gate)
        {
            if (disposed)
                return (false, "HealBot pairing is unloading.");
            if (plugin.Configuration.OperatingRole != OperatingRole.Helper)
                return (false, "Select the Helper role on the healing client.");
            if (activationOwner != ActivationOwner.DirectNewb)
                return (false, "The Helper provider is not owned by direct Newb pairing.");
            if (assignment?.Source == AssignmentSource.Qst)
                return (false, "HealBot already has an active QST assignment.");

            var joat = EvaluateJoatReadiness();
            if (!joat.Ready)
                return joat;

            return travelService.EvaluateReadiness();
        }
    }

    public (bool Ready, string Blocker) BeginDirectHelperRole()
    {
        lock (gate)
        {
            if (disposed)
                return (false, "HealBot pairing is unloading.");
            if (activationOwner == ActivationOwner.Qst)
                return (false, "QST currently owns provider activation.");
            if (activationOwner == ActivationOwner.DirectNewb)
                return (true, string.Empty);

            CaptureActivation(ActivationOwner.DirectNewb);
            plugin.ApplyProviderState(
                pluginEnabled: activationSnapshot!.PluginEnabled,
                automationEnabled: false,
                activationSnapshot.Mode);
            return (true, string.Empty);
        }
    }

    public (bool Ready, string Blocker) EnsureDirectHelperActivated()
    {
        lock (gate)
        {
            if (plugin.Configuration.OperatingRole != OperatingRole.Helper)
                return (false, "Select the Helper role on the healing client.");
            if (activationOwner != ActivationOwner.DirectNewb)
                return (false, "Direct Newb pairing does not own Helper activation.");

            return ActivateOwned(ActivationOwner.DirectNewb);
        }
    }

    public void LeaveDirectHelperRole(string reason)
    {
        lock (gate)
        {
            if (activationOwner != ActivationOwner.DirectNewb)
                return;
            if (assignment?.Source == AssignmentSource.Newb)
                ReleaseActive(reason);
            RestoreActivationSnapshot(ActivationOwner.DirectNewb);
        }
    }

    public void ReleaseQstForLocalRoleChange(string reason)
    {
        lock (gate)
        {
            if (activationOwner != ActivationOwner.Qst)
                return;
            if (assignment?.Source == AssignmentSource.Qst)
                ReleaseActive(reason);
            companionService.ClearQstOwnership();
            ClearJoatAttackModeOwnership();
            RestoreActivationSnapshot(ActivationOwner.Qst);
        }
    }

    public (bool Ready, string Blocker) EvaluateJoatProviderReadiness()
    {
        lock (gate)
            return EvaluateJoatReadiness();
    }

    public (bool Ready, string Blocker) EvaluateTravelReadiness()
    {
        lock (gate)
            return travelService.EvaluateReadiness();
    }

    public CoppeliaAssignmentSnapshot GetAssignmentSnapshot()
    {
        lock (gate)
        {
            return assignment == null
                ? CoppeliaAssignmentSnapshot.Empty
                : new CoppeliaAssignmentSnapshot(
                    assignment.Source == AssignmentSource.Newb ? "Newb" : "QST",
                    assignment.SessionId,
                    assignment.QuesterName,
                    assignment.QuesterWorldId);
        }
    }

    public CoppeliaQstCommandResponse AssignNewb(HealBotLanCommand command)
    {
        lock (gate)
        {
            if (!IsValidSessionId(command.SessionId))
                return Failed("Invalid Newb session ID.");
            if (activationOwner != ActivationOwner.DirectNewb)
                return Failed("Direct Newb pairing does not own Helper activation.");

            if (assignment != null)
            {
                var sameAssignment = assignment.Source == AssignmentSource.Newb &&
                                     assignment.SessionId == command.SessionId &&
                                     string.Equals(assignment.QuesterName, command.NewbName, StringComparison.Ordinal) &&
                                     assignment.QuesterWorldId == command.NewbWorldId;
                return sameAssignment
                    ? Accepted("Newb assignment is already active.")
                    : Failed("HealBot already has an active QST or Newb assignment.");
            }

            if (!IsValidQuester(command.NewbName, command.NewbWorldId))
                return Failed("Invalid Newb identity.");

            var readiness = EvaluateNewbReadiness();
            if (!readiness.Ready)
                return Failed(readiness.Blocker);

            assignment = new Assignment(
                AssignmentSource.Newb,
                command.SessionId,
                command.NewbName,
                command.NewbWorldId,
                dutyOptIn: false,
                dutyInviter: string.Empty);
            plugin.WatchTargetService.SetEphemeralNewbTarget(command.NewbName, command.NewbWorldId);
            Plugin.Log.Information("[Coppelia][Pairing] Accepted an ephemeral exact-target Newb assignment.");
            return Accepted("Newb assignment accepted.");
        }
    }

    public CoppeliaQstCommandResponse ApplyNewbTravel(HealBotLanCommand command)
    {
        lock (gate)
        {
            if (assignment == null ||
                assignment.Source != AssignmentSource.Newb ||
                assignment.SessionId != command.SessionId)
            {
                return Failed("Newb session does not own the active assignment.");
            }
            if (!string.Equals(command.NewbName, assignment.QuesterName, StringComparison.Ordinal) ||
                command.NewbWorldId != assignment.QuesterWorldId)
            {
                return Failed("Travel update does not match the exact assigned Newb.");
            }

            return travelService.Apply(new CoppeliaQstCommand(
                "TravelUpdate",
                command.SessionId,
                command.NewbName,
                command.NewbWorldId,
                command.NewbCurrentWorldId,
                command.TerritoryId,
                command.X,
                command.Y,
                command.Z,
                command.TravelSequence,
                command.AetheryteId,
                command.AetheryteSubIndex,
                command.AetheryteName,
                command.NewbMounted,
                command.NewbFlying,
                false,
                string.Empty,
                0,
                0,
                0));
        }
    }

    public CoppeliaQstCommandResponse ReleaseNewb(string sessionId, string reason)
    {
        lock (gate)
        {
            if (assignment == null)
                return lastReleasedSessionId == sessionId
                    ? Accepted("Newb session was already released.")
                    : Failed("Newb session does not own an active assignment.");
            if (assignment.Source != AssignmentSource.Newb || assignment.SessionId != sessionId)
                return Failed("Newb session does not own the active assignment.");

            ReleaseActive(reason);
            return Accepted("Newb assignment released.");
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed)
                return;

            disposed = true;
            ReleaseActive("HealBot is unloading.");
            companionService.ClearQstOwnership();
            ClearJoatAttackModeOwnership();
            RestoreActivationSnapshot(activationOwner);
            UnregisterProviders();
            started = false;
        }
    }

    private void UnregisterProviders()
    {
        if (healRiderCommandRegistered)
        {
            healRiderCommandProvider.UnregisterFunc();
            healRiderCommandRegistered = false;
        }
        if (healRiderStatusRegistered)
        {
            healRiderStatusProvider.UnregisterFunc();
            healRiderStatusRegistered = false;
        }
        if (joatFullRsrRotationProviderRegistered)
        {
            joatFullRsrRotationProvider.UnregisterFunc();
            joatFullRsrRotationProviderRegistered = false;
        }

        if (companionSummoningProviderRegistered)
        {
            companionSummoningProvider.UnregisterFunc();
            companionSummoningProviderRegistered = false;
        }

        if (commandProviderRegistered)
        {
            commandProvider.UnregisterFunc();
            commandProviderRegistered = false;
        }

        if (statusProviderRegistered)
        {
            statusProvider.UnregisterFunc();
            statusProviderRegistered = false;
        }
    }

    private string GetStatusJson()
    {
        lock (gate)
        {
            var joat = EvaluateJoatReadiness();
            var travel = travelService.EvaluateReadiness();
            var daf = EvaluateDafReadiness(assignment?.DutyInviter == "Coppelia");
            var currentAssignment = assignment == null
                ? string.Empty
                : FormatNameAtWorld(assignment.QuesterName, assignment.QuesterWorldId);
            return JsonSerializer.Serialize(
                new CoppeliaQstStatus(
                    CoppeliaQstContract.Version,
                    Compatible: true,
                    joat.Ready,
                    joat.Blocker,
                    travel.Ready,
                    travel.Blocker,
                    daf.Ready,
                    daf.Blocker,
                    plugin.Configuration.BotMode.GetLabel(),
                    currentAssignment,
                    assignment?.SessionId ?? string.Empty,
                    travelService.State,
                    assignment?.DutyState ?? "Idle",
                    assignment?.DutyInviter ?? string.Empty,
                    assignment?.DutyOwned == true && IsAdsDutyOwned(),
                    travelService.CurrentInstanceId),
                CoppeliaQstContract.JsonOptions);
        }
    }

    private string HandleCommandJson(string requestJson)
    {
        CoppeliaQstCommandResponse response;
        try
        {
            var command = JsonSerializer.Deserialize<CoppeliaQstCommand>(requestJson, CoppeliaQstContract.JsonOptions);
            response = command == null
                ? Failed("Empty QST command.")
                : HandleCommand(command);
        }
        catch (JsonException)
        {
            response = Failed("Malformed QST command.");
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "[Coppelia][QST] Command failed.");
            response = Failed("HealBot could not process the QST command.");
        }

        return JsonSerializer.Serialize(response, CoppeliaQstContract.JsonOptions);
    }

    private string HandleHealRiderJson(string requestJson) => HandleHealRiderJson(requestJson, false);

    private string HandleHealRiderJson(string requestJson, bool statusOnly)
    {
        lock (gate)
        {
            HealRiderStatus response;
            try
            {
                var command = JsonSerializer.Deserialize<HealRiderCommand>(requestJson, CoppeliaQstContract.JsonOptions);
                if (command == null || command.Version != 2 || !travelService.OwnsHealRiderCleanup(command) &&
                    (assignment?.Source != AssignmentSource.Qst ||
                    assignment.SessionId != command.SessionId || assignment.QuesterName != command.QuesterName ||
                    assignment.QuesterWorldId != command.QuesterWorldId ||
                    assignment.DutyInviter != command.PartyInviter ||
                    (assignment.PendingDutySequence != 0 || assignment.DadDutyRunActive || assignment.DutyOwned) &&
                    command.Action is not ("Cancel" or "DutyHandoff" or "Inspect")))
                {
                    response = new HealRiderStatus
                    {
                        SessionId = command?.SessionId ?? string.Empty,
                        LegId = command?.LegId ?? 0,
                        State = "Blocked",
                        Blocker = command != null && command.Version != 2
                            ? "HealRider v2 is required. Update both Helper and Quester."
                            : "HealRider requires the exact active QST assignment outside duty ownership.",
                    };
                }
                else
                    response = travelService.ApplyHealRider(statusOnly ? command with { Action = "Inspect" } : command);
            }
            catch (Exception ex)
            {
                Plugin.Log.Warning(ex, "[HealRider] Request failed.");
                response = new HealRiderStatus { State = "Blocked", Blocker = "Invalid or unavailable HealRider request." };
            }
            return JsonSerializer.Serialize(response, CoppeliaQstContract.JsonOptions);
        }
    }

    private CoppeliaQstCommandResponse HandleCommand(CoppeliaQstCommand command)
    {
        lock (gate)
        {
            if (command.ContractVersion != CoppeliaQstContract.Version)
            {
                return command.ContractVersion < CoppeliaQstContract.Version
                    ? Failed("Coppelia.QST v4 is required. Update QST before pairing this helper.")
                    : Failed("This HealBot build does not support the QST contract. Update HealBot before pairing this helper.");
            }

            if (command.Action is "Activate" or "Deactivate")
                return command.Action == "Activate" ? ActivateQst() : DeactivateQst();

            if (!IsValidSessionId(command.SessionId))
                return Failed("Invalid QST session ID.");

            return command.Action switch
            {
                "AssignQuester" => AssignQuester(command),
                "TravelUpdate" => ApplyTravel(command),
                "StartDutyDad" => StartDutyDad(command),
                "StartDutyInside" => QueueDutyInside(command),
                "Release" => Release(command.SessionId),
                _ => Failed("Unknown QST action."),
            };
        }
    }

    private CoppeliaQstCommandResponse ActivateQst()
    {
        if (activationOwner == ActivationOwner.DirectNewb)
            return Failed("Direct Newb pairing currently owns Helper activation.");
        if (activationOwner == ActivationOwner.None)
            CaptureActivation(ActivationOwner.Qst);

        var activation = ActivateOwned(ActivationOwner.Qst);
        if (activation.Ready)
            return Accepted("HealBot JOAT is active for QST.");

        RestoreActivationSnapshot(ActivationOwner.Qst);
        return Failed(activation.Blocker);
    }

    private CoppeliaQstCommandResponse DeactivateQst()
    {
        if (activationOwner == ActivationOwner.None)
        {
            ClearJoatAttackModeOwnership();
            return Accepted("QST-owned HealBot activation was already restored.");
        }
        if (activationOwner != ActivationOwner.Qst)
            return Failed("QST does not own the active HealBot provider.");
        if (assignment?.Source == AssignmentSource.Qst)
            ReleaseActive("QST deactivated HealBot.");
        companionService.ClearQstOwnership();
        ClearJoatAttackModeOwnership();
        RestoreActivationSnapshot(ActivationOwner.Qst);
        return Accepted("QST-owned HealBot activation was restored.");
    }

    private bool SetCompanionSummoning(bool enabled)
    {
        lock (gate)
        {
            if (disposed || activationOwner != ActivationOwner.Qst || activationSnapshot == null ||
                !plugin.Configuration.PluginEnabled ||
                !plugin.Configuration.AutomationEnabled ||
                plugin.Configuration.BotMode != BotMode.Jot)
            {
                return false;
            }

            return companionService.SetQstOwnership(enabled);
        }
    }

    private bool SetJoatFullRsrRotation(bool enabled)
    {
        lock (gate)
        {
            if (disposed || activationOwner != ActivationOwner.Qst || activationSnapshot == null ||
                !plugin.Configuration.PluginEnabled ||
                !plugin.Configuration.AutomationEnabled ||
                plugin.Configuration.BotMode != BotMode.Jot)
            {
                return false;
            }

            var wasOwned = qstJoatAttackModeOwned;
            var previousMode = qstFullRsrRotation;
            plugin.DependencyService.Refresh(force: true);
            if (wasOwned && previousMode == enabled && plugin.HealbotRuntimeService.IsRsrControlReady)
                return true;

            qstJoatAttackModeOwned = true;
            qstFullRsrRotation = enabled;
            if (plugin.HealbotRuntimeService.TryApplyRsrProfileNow())
                return true;

            if (wasOwned && previousMode == enabled)
                return false;

            qstJoatAttackModeOwned = wasOwned;
            qstFullRsrRotation = previousMode;
            _ = plugin.HealbotRuntimeService.TryApplyRsrProfileNow();
            return false;
        }
    }

    private CoppeliaQstCommandResponse AssignQuester(CoppeliaQstCommand command)
    {
        if (activationOwner != ActivationOwner.Qst)
            return Failed("QST does not own the active HealBot provider.");
        if (assignment != null)
        {
            var sameAssignment = assignment.Source == AssignmentSource.Qst &&
                                 assignment.SessionId == command.SessionId &&
                                 string.Equals(assignment.QuesterName, command.QuesterName, StringComparison.Ordinal) &&
                                 assignment.QuesterWorldId == command.QuesterWorldId &&
                                 assignment.DutyOptIn == command.DutyOptIn &&
                                 string.Equals(assignment.DutyInviter, command.DutyInviter, StringComparison.Ordinal);
            if (!sameAssignment)
                return Failed("HealBot already has an active QST or Newb assignment.");

            if (command.DutyOptIn)
            {
                var daf = EvaluateDafReadiness(command.DutyInviter == "Coppelia");
                if (!daf.Ready)
                    return Failed(daf.Blocker);
            }

            return Accepted("QST assignment is already active.");
        }

        if (!IsValidQuester(command.QuesterName, command.QuesterWorldId))
            return Failed("Invalid Quester identity.");
        if (command.DutyOptIn && command.DutyInviter is not ("Quester" or "Coppelia"))
            return Failed("Invalid Coppelia duty inviter.");

        var joat = EvaluateJoatReadiness();
        if (!joat.Ready)
            return Failed(joat.Blocker);
        var travel = travelService.EvaluateReadiness();
        if (!travel.Ready)
            return Failed(travel.Blocker);

        assignment = new Assignment(
            AssignmentSource.Qst,
            command.SessionId,
            command.QuesterName,
            command.QuesterWorldId,
            command.DutyOptIn,
            command.DutyInviter);
        plugin.WatchTargetService.SetEphemeralQstTarget(command.QuesterName, command.QuesterWorldId);
        Plugin.Log.Information("[Coppelia][QST] Accepted an ephemeral exact-target JOAT assignment.");
        return Accepted("QST assignment accepted.");
    }

    private CoppeliaQstCommandResponse ApplyTravel(CoppeliaQstCommand command)
    {
        var validation = ValidateOwnedAssignment(command);
        if (validation != null)
            return validation;
        if (!string.Equals(command.QuesterName, assignment!.QuesterName, StringComparison.Ordinal) ||
            command.QuesterWorldId != assignment.QuesterWorldId)
            return Failed("Travel update does not match the exact assigned Quester.");

        return travelService.Apply(command);
    }

    private CoppeliaQstCommandResponse QueueDutyInside(CoppeliaQstCommand command)
    {
        var validation = ValidateDutyCommand(command);
        if (validation != null)
            return validation;
        if (!string.Equals(assignment!.DutyInviter, "Quester", StringComparison.Ordinal))
            return Failed("This assignment does not use Quester-owned duty entry.");

        return QueueDutySequence(command.DutySequence, "Waiting for stable Quester-owned duty entry");
    }

    private CoppeliaQstCommandResponse StartDutyDad(CoppeliaQstCommand command)
    {
        var validation = ValidateDutyCommand(command);
        if (validation != null)
            return validation;
        if (!string.Equals(assignment!.DutyInviter, "Coppelia", StringComparison.Ordinal))
            return Failed("This assignment does not use Coppelia-owned duty entry.");
        if (command.DutyTerritoryId == 0 || command.ContentFinderConditionId == 0)
            return Failed("The live Questionable duty metadata is incomplete.");
        if (!Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.TerritoryType>()
                .TryGetRow(command.DutyTerritoryId, out var territory) ||
            territory.ContentFinderCondition.RowId != command.ContentFinderConditionId)
        {
            return Failed("The live Questionable duty territory and Content Finder Condition do not match.");
        }

        if (assignment.ProcessedDutySequences.Contains(command.DutySequence) ||
            assignment.PendingDutySequence == command.DutySequence)
            return Accepted("Coppelia DAD duty sequence is already active.");

        var daf = EvaluateDafReadiness(assignment!.DutyInviter == "Coppelia");
        if (!daf.Ready)
            return Failed(daf.Blocker);
        if (!TryGetDadStopped(out var dadStopped, out var dadFailure))
            return Failed(dadFailure);
        if (!dadStopped)
            return Failed("DAD is already running another duty.");
        if (!TryDadContentHasPath(command.DutyTerritoryId, out var failure))
            return Failed(failure);

        try
        {
            Plugin.PluginInterface.GetIpcSubscriber<string, string, object>(DadSetConfigEndpoint)
                .InvokeAction("dutyModeEnum", "Regular");
            Plugin.PluginInterface.GetIpcSubscriber<string, string, object>(DadSetConfigEndpoint)
                .InvokeAction("Unsynced", "true");
            Plugin.PluginInterface.GetIpcSubscriber<uint, int, bool, object>(DadRunEndpoint)
                .InvokeAction(command.DutyTerritoryId, 1, false);
        }
        catch (Exception)
        {
            return Failed("DAD rejected the Coppelia-owned unsynced duty start.");
        }

        assignment.DadDutyRunActive = true;
        return QueueDutySequence(command.DutySequence, "DAD is queueing the Coppelia-owned duty");
    }

    private CoppeliaQstCommandResponse QueueDutySequence(long sequence, string state)
    {
        travelService.CancelHealRider("Duty handoff");
        if (assignment!.ProcessedDutySequences.Contains(sequence))
            return Accepted("Coppelia duty sequence was already completed.");
        if (assignment.PendingDutySequence == sequence)
            return Accepted("Coppelia duty sequence is already pending.");

        assignment.PendingDutySequence = sequence;
        assignment.DutyState = state;
        return Accepted("Coppelia duty sequence accepted.");
    }

    private void StartStableInsideDuty(long sequence)
    {
        if (assignment == null || assignment.ProcessedDutySequences.Contains(sequence))
            return;

        var daf = EvaluateDafReadiness(assignment!.DutyInviter == "Coppelia");
        if (!daf.Ready)
        {
            FailStableDutyStart(assignment, daf.Blocker);
            return;
        }

        if (!TryGetFrenStatus(out var frenStatus, out var failure))
        {
            FailStableDutyStart(assignment, failure);
            return;
        }

        assignment.FrenSnapshot ??= new FrenSnapshot(frenStatus.FrenRiderEnabled, frenStatus.ConfiguredFrenName);
        var exactTarget = FormatNameAtWorld(assignment.QuesterName, assignment.QuesterWorldId);
        if (string.IsNullOrWhiteSpace(exactTarget) || !TryConfigureAndEnableFren(exactTarget, out failure))
        {
            FailStableDutyStart(assignment, failure);
            return;
        }

        if (!TryStartAdsInside(out failure))
        {
            FailStableDutyStart(assignment, failure);
            return;
        }

        assignment.ProcessedDutySequences.Add(sequence);
        assignment.PendingDutySequence = 0;
        assignment.DutyOwned = true;
        assignment.DutyState = $"DAF owns duty sequence {sequence}";
        Plugin.Log.Information($"[Coppelia][QST] Stable DAF handoff accepted for duty sequence {sequence}.");
    }

    private static void FailStableDutyStart(Assignment activeAssignment, string failure)
    {
        activeAssignment.DutyState = failure;
        activeAssignment.PendingDutySequence = 0;
        if (activeAssignment.DadDutyRunActive)
        {
            try
            {
                Plugin.PluginInterface.GetIpcSubscriber<object>(DadStopEndpoint).InvokeAction();
                activeAssignment.DadDutyRunActive = false;
            }
            catch (Exception)
            {
                Plugin.Log.Warning("[Coppelia][QST] DAD stop IPC failed after DAF startup failure.");
            }
        }

        RestoreFrenAfterFailedStart(activeAssignment);
    }

    private CoppeliaQstCommandResponse? ValidateOwnedAssignment(CoppeliaQstCommand command)
    {
        if (assignment == null || assignment.Source != AssignmentSource.Qst || assignment.SessionId != command.SessionId)
            return Failed("QST session does not own the active assignment.");
        return null;
    }

    private CoppeliaQstCommandResponse? ValidateDutyCommand(CoppeliaQstCommand command)
    {
        var validation = ValidateOwnedAssignment(command);
        if (validation != null)
            return validation;
        if (!assignment!.DutyOptIn)
            return Failed("This QST assignment did not opt in to Coppelia duties.");
        if (command.DutySequence <= 0)
            return Failed("Invalid Coppelia duty sequence.");

        var daf = EvaluateDafReadiness(assignment!.DutyInviter == "Coppelia");
        return daf.Ready ? null : Failed(daf.Blocker);
    }

    private CoppeliaQstCommandResponse Release(string sessionId)
    {
        if (assignment == null)
            return lastReleasedSessionId == sessionId
                ? Accepted("QST session was already released.")
                : Failed("QST session does not own an active assignment.");
        if (assignment.Source != AssignmentSource.Qst || assignment.SessionId != sessionId)
            return Failed("QST session does not own the active assignment.");

        ReleaseActive("QST released the assignment.");
        return Accepted("QST assignment released.");
    }

    private void CompleteDutyExit()
    {
        if (assignment == null)
            return;

        assignment.PendingDutySequence = 0;
        assignment.DutyOwned = false;
        assignment.DutyState = assignment.DadDutyRunActive
            ? "Waiting for DAD duty cleanup"
            : "Assignment retained outside duty";
        if (!assignment.DadDutyRunActive)
            RestoreFrenAfterDuty(assignment);
    }

    private static void RestoreFrenAfterDuty(Assignment activeAssignment)
    {
        if (activeAssignment.FrenSnapshot == null)
            return;

        if (!RestoreFrenSnapshot(activeAssignment.FrenSnapshot, out var failure))
            Plugin.Log.Warning($"[Coppelia][QST] FrenRider restore failed after duty exit: {failure}");
        activeAssignment.FrenSnapshot = null;
    }

    private static void RestoreFrenAfterFailedStart(Assignment activeAssignment)
    {
        if (activeAssignment.FrenSnapshot == null)
            return;

        if (!RestoreFrenSnapshot(activeAssignment.FrenSnapshot, out var failure))
            Plugin.Log.Warning($"[Coppelia][QST] FrenRider restore failed after DAF startup failure: {failure}");
        activeAssignment.FrenSnapshot = null;
    }

    private void ReleaseActive(string reason)
    {
        lock (gate)
        {
            travelService.Release();
            if (assignment == null)
                return;

            if (assignment.DadDutyRunActive)
            {
                try
                {
                    Plugin.PluginInterface.GetIpcSubscriber<object>(DadStopEndpoint).InvokeAction();
                }
                catch (Exception)
                {
                    Plugin.Log.Warning("[Coppelia][QST] DAD stop IPC failed during assignment release.");
                }
            }

            if (assignment.FrenSnapshot != null && !RestoreFrenSnapshot(assignment.FrenSnapshot, out var restoreFailure))
                Plugin.Log.Warning($"[Coppelia][QST] FrenRider restore failed during release: {restoreFailure}");

            lastReleasedSessionId = assignment.SessionId;
            if (assignment.Source == AssignmentSource.Qst)
                ClearJoatAttackModeOwnership();
            assignment = null;
            plugin.WatchTargetService.ClearEphemeralQstTarget();
            Plugin.Log.Information($"[Coppelia][QST] Assignment released: {reason}");
        }
    }

    private void CaptureActivation(ActivationOwner owner)
    {
        activationSnapshot = new ActivationSnapshot(
            plugin.Configuration.PluginEnabled,
            plugin.Configuration.AutomationEnabled,
            plugin.Configuration.BotMode);
        activationOwner = owner;
    }

    private (bool Ready, string Blocker) ActivateOwned(ActivationOwner owner)
    {
        if (activationOwner != owner)
            return (false, "Another provider owns HealBot activation.");
        if (plugin.Configuration.PluginEnabled &&
            plugin.Configuration.AutomationEnabled &&
            plugin.Configuration.BotMode == BotMode.Jot)
        {
            return (true, string.Empty);
        }

        return plugin.TryActivateProviderMode(BotMode.Jot, out var blocker)
            ? (true, string.Empty)
            : (false, string.IsNullOrWhiteSpace(blocker) ? "HealBot could not activate JOAT." : blocker);
    }

    private void RestoreActivationSnapshot(ActivationOwner expectedOwner)
    {
        if (activationSnapshot == null || activationOwner != expectedOwner)
            return;

        var snapshot = activationSnapshot;
        activationSnapshot = null;
        activationOwner = ActivationOwner.None;
        plugin.ApplyProviderState(snapshot.PluginEnabled, snapshot.AutomationEnabled, snapshot.Mode);
    }

    private (bool Ready, string Blocker) EvaluateJoatReadiness()
    {
        if (!plugin.Configuration.PluginEnabled)
            return (false, "HealBot is disabled.");
        if (!plugin.Configuration.AutomationEnabled)
            return (false, "HealBot automation is disabled.");
        if (plugin.Configuration.BotMode != BotMode.Jot)
            return (false, "Select Jacqueline of All Trades (JOAT).");

        plugin.DependencyService.Refresh(force: true);
        if (!plugin.DependencyService.Current.IsHealbotReady)
            return (false, plugin.DependencyService.BuildMissingDependencyMessage());
        if (!plugin.DependencyService.Current.RotationSolverLoaded)
            return (false, "Rotation Solver Reborn is required for JOAT attacking.");
        if (!plugin.HealbotRuntimeService.IsRsrControlReady)
            return (false, "Coppelia could not acquire working Rotation Solver Reborn control.");
        if (!plugin.HealbotRuntimeService.IsSupportedLocalJob(out _, out var jobFailure))
            return (false, jobFailure);

        return (true, string.Empty);
    }

    private void ClearJoatAttackModeOwnership()
    {
        if (!qstJoatAttackModeOwned)
            return;

        qstJoatAttackModeOwned = false;
        qstFullRsrRotation = false;
        if (!disposed &&
            plugin.Configuration.PluginEnabled &&
            plugin.Configuration.AutomationEnabled &&
            plugin.Configuration.BotMode == BotMode.Jot &&
            !plugin.HealbotRuntimeService.TryApplyRsrProfileNow())
        {
            Plugin.Log.Warning("[Coppelia][QST] Failed to restore the saved local JOAT attack mode.");
        }
    }

    private static (bool Ready, string Blocker) EvaluateDafReadiness(bool requireDad)
    {
        if (!TryGetFrenStatus(out _, out var frenFailure))
            return (false, frenFailure);
        if (!TryGetAdsStatus(out var adsFailure))
            return (false, adsFailure);
        if (requireDad && !TryGetDadStopped(out _, out var dadFailure))
            return (false, dadFailure);

        return (true, string.Empty);
    }

    private static bool TryGetDadStopped(out bool stopped, out string failure)
    {
        try
        {
            stopped = Plugin.PluginInterface.GetIpcSubscriber<bool>(DadIsStoppedEndpoint).InvokeFunc();
            failure = string.Empty;
            return true;
        }
        catch (Exception)
        {
            stopped = false;
            failure = "DAD duty IPC is unavailable.";
            return false;
        }
    }

    private static bool TryDadContentHasPath(uint territoryId, out string failure)
    {
        try
        {
            if (Plugin.PluginInterface.GetIpcSubscriber<uint, bool>(DadContentHasPathEndpoint).InvokeFunc(territoryId))
            {
                failure = string.Empty;
                return true;
            }

            failure = $"DAD has no compatible path for territory {territoryId}.";
            return false;
        }
        catch (Exception)
        {
            failure = "DAD content-path IPC is unavailable.";
            return false;
        }
    }

    private static bool IsValidSessionId(string sessionId) =>
        !string.IsNullOrWhiteSpace(sessionId) &&
        sessionId.Length <= 128 &&
        string.Equals(sessionId, sessionId.Trim(), StringComparison.Ordinal) &&
        !sessionId.Any(char.IsControl);

    private static bool IsValidQuester(string name, ushort worldId) =>
        worldId != 0 &&
        !string.IsNullOrWhiteSpace(name) &&
        name.Length <= 64 &&
        string.Equals(name, name.Trim(), StringComparison.Ordinal) &&
        !name.Any(char.IsControl);

    private static string FormatNameAtWorld(string name, ushort worldId)
    {
        var worldSheet = Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.World>();
        return worldSheet.TryGetRow(worldId, out var world) && !world.Name.IsEmpty
            ? $"{name}@{world.Name.ExtractText()}"
            : string.Empty;
    }

    private static bool TryGetFrenStatus(out FrenRiderPowerlevelStatus status, out string failure)
    {
        try
        {
            var json = Plugin.PluginInterface.GetIpcSubscriber<string>(FrenStatusEndpoint).InvokeFunc();
            status = JsonSerializer.Deserialize<FrenRiderPowerlevelStatus>(json, FrenRiderPowerlevelContract.JsonOptions) ??
                     new FrenRiderPowerlevelStatus(0, false, string.Empty, false, string.Empty, string.Empty, 0, false, string.Empty, 0);
            if (!status.IsCompatible)
            {
                failure = "Compatible FrenRider DAF IPC is unavailable.";
                return false;
            }

            failure = string.Empty;
            return true;
        }
        catch (Exception)
        {
            status = new FrenRiderPowerlevelStatus(0, false, string.Empty, false, string.Empty, string.Empty, 0, false, string.Empty, 0);
            failure = "FrenRider DAF IPC is unavailable.";
            return false;
        }
    }

    private static bool TryConfigureAndEnableFren(string exactTarget, out string failure)
    {
        try
        {
            if (Plugin.PluginInterface.GetIpcSubscriber<string, bool>(FrenConfigureEndpoint).InvokeFunc(exactTarget))
            {
                failure = string.Empty;
                return true;
            }

            failure = "FrenRider rejected the exact Quester target.";
            return false;
        }
        catch (Exception)
        {
            failure = "FrenRider configure/enable IPC is unavailable.";
            return false;
        }
    }

    private static bool TryGetAdsStatus(out string failure)
    {
        try
        {
            var status = Plugin.PluginInterface.GetIpcSubscriber<string>(AdsStatusEndpoint).InvokeFunc();
            if (!string.IsNullOrWhiteSpace(status))
            {
                failure = string.Empty;
                return true;
            }
        }
        catch (Exception)
        {
        }

        failure = "ADS DAF IPC is unavailable.";
        return false;
    }

    private static bool TryStartAdsInside(out string failure)
    {
        try
        {
            if (Plugin.PluginInterface.GetIpcSubscriber<bool>(AdsStartInsideEndpoint).InvokeFunc())
            {
                failure = string.Empty;
                return true;
            }

            failure = "ADS rejected StartDutyFromInside.";
            return false;
        }
        catch (Exception)
        {
            failure = "ADS StartDutyFromInside IPC is unavailable.";
            return false;
        }
    }

    private static bool IsAdsDutyOwned()
    {
        try
        {
            return Plugin.PluginInterface.GetIpcSubscriber<bool>(AdsIsDutyOwnedEndpoint).InvokeFunc();
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool RestoreFrenSnapshot(FrenSnapshot snapshot, out string failure)
    {
        bool restoredTarget;
        if (string.IsNullOrWhiteSpace(snapshot.ConfiguredTarget))
        {
            try
            {
                restoredTarget = Plugin.PluginInterface.GetIpcSubscriber<bool>(FrenClearEndpoint).InvokeFunc();
            }
            catch (Exception)
            {
                restoredTarget = false;
            }
        }
        else
        {
            restoredTarget = TryConfigureAndEnableFren(snapshot.ConfiguredTarget, out _);
        }

        try
        {
            Plugin.CommandManager.ProcessCommand(snapshot.Enabled ? "/fr on" : "/fr off");
        }
        catch (Exception)
        {
            failure = "FrenRider enabled-state command failed.";
            return false;
        }

        failure = restoredTarget ? string.Empty : "FrenRider target restore failed.";
        return restoredTarget;
    }

    private static CoppeliaQstCommandResponse Accepted(string reason) => new(true, reason);
    private static CoppeliaQstCommandResponse Failed(string reason) => new(false, reason);

    private sealed class Assignment
    {
        public Assignment(
            AssignmentSource source,
            string sessionId,
            string questerName,
            ushort questerWorldId,
            bool dutyOptIn,
            string dutyInviter)
        {
            Source = source;
            SessionId = sessionId;
            QuesterName = questerName;
            QuesterWorldId = questerWorldId;
            DutyOptIn = dutyOptIn;
            DutyInviter = dutyInviter;
        }

        public AssignmentSource Source { get; }
        public string SessionId { get; }
        public string QuesterName { get; }
        public ushort QuesterWorldId { get; }
        public bool DutyOptIn { get; }
        public string DutyInviter { get; }
        public long PendingDutySequence { get; set; }
        public HashSet<long> ProcessedDutySequences { get; } = [];
        public FrenSnapshot? FrenSnapshot { get; set; }
        public bool DutyOwned { get; set; }
        public bool DadDutyRunActive { get; set; }
        public string DutyState { get; set; } = "Waiting outside duty";
    }

    private sealed record FrenSnapshot(bool Enabled, string ConfiguredTarget);
    private sealed record ActivationSnapshot(bool PluginEnabled, bool AutomationEnabled, BotMode Mode);

    private enum AssignmentSource
    {
        Qst,
        Newb,
    }

    private enum ActivationOwner
    {
        None,
        Qst,
        DirectNewb,
    }
}

internal sealed record CoppeliaAssignmentSnapshot(
    string Source,
    string SessionId,
    string Name,
    ushort WorldId)
{
    public static readonly CoppeliaAssignmentSnapshot Empty = new(string.Empty, string.Empty, string.Empty, 0);
}

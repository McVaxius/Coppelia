using System.Numerics;
using Coppelia.Models;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.ClientState.Objects.Types;

namespace Coppelia.Services;

internal sealed class JotRuntimeService : IDisposable
{
    private const float PairedAttackRangeYalms = 30f;

    private readonly Plugin plugin;
    private readonly FrenRiderPowerlevelIpcService frenRiderIpcService;
    private readonly PowerlevelTargetSelector targetSelector = new();

    private bool armed;
    private bool activationFailed;
    private bool restorationPending;

    internal bool IsBmrHandoffReady => !restorationPending && frenRiderIpcService.CombatSuppressed;
    internal string BmrHandoffBlocker => string.IsNullOrWhiteSpace(frenRiderIpcService.LastFailure)
        ? "FrenRider combat-suppression acknowledgement is required before BMR control. Update FrenRider if the acknowledgement is unavailable."
        : frenRiderIpcService.LastFailure;

    // Runs before HealbotRuntimeService.Update: the FR off commands must finish
    // before BMR's original preset is captured, not after Coppelia activates it.
    internal void PrepareHealingCycle()
    {
        if (restorationPending)
        {
            _ = frenRiderIpcService.HeartbeatIfDue(out _);
            return;
        }
        if (!plugin.Configuration.PluginEnabled || !plugin.Configuration.AutomationEnabled
            || plugin.Configuration.BotMode != BotMode.Jot
            || plugin.Configuration.JoatRotationProvider != JoatRotationProvider.Bmr)
            return;
        if (!armed && !activationFailed) Activate();
        if (!armed) return;
        if (!plugin.HealbotRuntimeService.IsSupportedLocalJob(out var profile, out _)
            || !plugin.Configuration.GetJobConfigForJob(profile!.JobId).Enabled
            || plugin.WatchTargetService.SelectedTargetCount == 0
            || !plugin.HealbotRuntimeService.HasRotationDamageCapability(profile,
                plugin.CoppeliaQstIpcService.EffectiveJoatFullRsrRotation, out _))
            return;
        if (!frenRiderIpcService.LeaseAcquired && !frenRiderIpcService.Acquire(out var acquireFailure))
        {
            StatusText = $"Blocked: {acquireFailure}";
            LastIssuedAction = "Blocked";
            return;
        }
        if (!frenRiderIpcService.HeartbeatIfDue(out var heartbeatFailure))
        {
            HoldRotationDamage();
            StatusText = $"Blocked: {heartbeatFailure}";
            LastIssuedAction = "Blocked";
        }
        else if (!frenRiderIpcService.CombatSuppressed)
        {
            HoldRotationDamage();
            StatusText = $"Blocked: {BmrHandoffBlocker}";
            LastIssuedAction = "Blocked";
        }
    }

    public JotRuntimeService(
        Plugin plugin,
        FrenRiderPowerlevelIpcService frenRiderIpcService)
    {
        this.plugin = plugin;
        this.frenRiderIpcService = frenRiderIpcService;
    }

    public string StatusText { get; private set; } = "JOAT attacking is off.";
    public string LastIssuedAction { get; private set; } = "Idle";
    public string LastMatchedRule { get; private set; } = "Waiting for an idle healing decision.";

    public void Dispose()
        => Deactivate("Plugin unloading.");

    public void Activate()
    {
        if (restorationPending)
        {
            Deactivate("Completing prior provider restoration before activation.");
            if (restorationPending) return;
        }
        activationFailed = false;
        armed = true;
        targetSelector.Clear();
        if (!frenRiderIpcService.ResetSession())
        {
            armed = false;
            activationFailed = true;
            StatusText = $"Blocked: the prior FrenRider lease release was not acknowledged. {frenRiderIpcService.LastFailure}";
            LastIssuedAction = "Blocked";
            LastMatchedRule = "FrenRider release acknowledgement is required.";
            return;
        }
        StatusText = "JOAT attacking is waiting for healing to be idle.";
        LastIssuedAction = "Idle";
        LastMatchedRule = "Waiting for an idle healing decision.";
    }

    public void Deactivate(string reason)
    {
        HoldRotationDamage();
        var restored = plugin.HealbotRuntimeService.RestoreRotationOwnership();
        var released = restored && frenRiderIpcService.Release(reason);
        restorationPending = !released;
        armed = false;
        activationFailed = !released;
        targetSelector.Clear();
        StatusText = released ? reason : $"Blocked during deactivation: {(restored ? frenRiderIpcService.LastFailure : plugin.BmrIpcService.LastFailure)}";
        LastIssuedAction = "Idle";
        LastMatchedRule = "Waiting for an idle healing decision.";
    }

    public void Update(HealbotDecisionOutcome healingDecision)
    {
        if (restorationPending)
        {
            if (!frenRiderIpcService.HeartbeatIfDue(out var pendingFailure))
                StatusText = $"Restoration is pending; FrenRider heartbeat failed: {pendingFailure}";
            return;
        }
        if (!plugin.Configuration.PluginEnabled ||
            !plugin.Configuration.AutomationEnabled ||
            plugin.Configuration.BotMode != BotMode.Jot)
        {
            if (armed)
                Deactivate("JOAT attacking is off.");
            else if (frenRiderIpcService.LeaseAcquired)
                StatusText = $"Blocked: the FrenRider lease release is not acknowledged. {frenRiderIpcService.LastFailure}";
            else
                StatusText = "JOAT attacking is off.";
            return;
        }

        if (!armed && !activationFailed)
            Activate();
        if (!armed)
            return;

        if (frenRiderIpcService.LeaseAcquired &&
            !frenRiderIpcService.HeartbeatIfDue(out var heartbeatFailure))
        {
            HoldRotationDamage();
            StatusText = $"Blocked: {heartbeatFailure}";
            LastIssuedAction = "Blocked";
            LastMatchedRule = "FrenRider lease heartbeat failed.";
            return;
        }

        if (plugin.Configuration.JoatRotationProvider == JoatRotationProvider.Bmr && !IsBmrHandoffReady)
        {
            HoldRotationDamage();
            StatusText = $"Blocked: {BmrHandoffBlocker}";
            LastIssuedAction = "Blocked";
            LastMatchedRule = "FrenRider combat suppression must remain acknowledged.";
            return;
        }

        switch (healingDecision)
        {
            case HealbotDecisionOutcome.None:
                return;
            case HealbotDecisionOutcome.Queued:
                HoldRotationDamage();
                StatusText = $"Holding: healing queued {plugin.HealbotRuntimeService.LastIssuedAction}.";
                LastIssuedAction = "Holding";
                LastMatchedRule = "Healing action won this decision cycle.";
                return;
            case HealbotDecisionOutcome.Blocked:
                HoldRotationDamage();
                StatusText = plugin.HealbotRuntimeService.LastIssuedAction == "Casting"
                    ? $"Holding: the current cast blocks attacking. {plugin.HealbotRuntimeService.StatusText}"
                    : $"Holding: healing is blocked. {plugin.HealbotRuntimeService.StatusText}";
                LastIssuedAction = "Blocked";
                LastMatchedRule = plugin.HealbotRuntimeService.LastIssuedAction == "Casting"
                    ? "Current cast blocks this attack cycle."
                    : "A matched healing action is blocked.";
                return;
            case HealbotDecisionOutcome.Unavailable:
                HoldRotationDamage();
                StatusText = $"Blocked: healing is not ready. {plugin.HealbotRuntimeService.StatusText}";
                LastIssuedAction = "Blocked";
                LastMatchedRule = "Healing dependencies, configuration, or watched targets are unavailable.";
                return;
            case HealbotDecisionOutcome.Idle:
                EvaluateAttack();
                return;
        }
    }

    internal JotSetupReadiness GetSetupReadiness(JoatRotationProvider? providerOverride = null, bool? fullRotationOverride = null)
    {
        plugin.DependencyService.Refresh();
        var dependenciesReady = plugin.DependencyService.Current.IsHealbotReady;
        var supportedHealer = plugin.HealbotRuntimeService.IsSupportedLocalJob(out var profile, out var healerReason);
        var healerConfigEnabled = supportedHealer && plugin.Configuration.GetJobConfigForJob(profile!.JobId).Enabled;
        var watchedTargetsAvailable = plugin.WatchTargetService.SelectedTargetCount > 0;
        var healingReady = dependenciesReady && supportedHealer && healerConfigEnabled && watchedTargetsAvailable;
        var healingReason = BuildHealingReadinessReason(
            dependenciesReady,
            supportedHealer,
            healerReason,
            healerConfigEnabled,
            watchedTargetsAvailable);

        var provider = providerOverride ?? plugin.Configuration.JoatRotationProvider;
        var providerLoaded = plugin.HealbotRuntimeService.IsRotationProviderLoadedFor(provider);
        var joatRunning = plugin.Configuration.PluginEnabled &&
                          plugin.Configuration.AutomationEnabled &&
                          plugin.Configuration.BotMode == BotMode.Jot;
        var capabilityBlocker = string.Empty;
        var damageCapability = supportedHealer && plugin.HealbotRuntimeService.HasRotationDamageCapability(
            profile!, fullRotationOverride ?? plugin.CoppeliaQstIpcService.EffectiveJoatFullRsrRotation,
            out capabilityBlocker, provider);
        var providerControlReady = providerLoaded && damageCapability &&
                              (!joatRunning || (provider == plugin.Configuration.JoatRotationProvider
                                  && plugin.HealbotRuntimeService.IsRotationControlReady));
        var ipcSucceeded = frenRiderIpcService.TryGetStatus(out var status);
        var ipcAvailable = ipcSucceeded || status.ContractVersion > 0;
        var pairedAssignment = plugin.WatchTargetService.HasEphemeralQstTarget;
        var pairedLeaderVisible = plugin.WatchTargetService.IsEphemeralQstTargetVisible;
        var attackingReady = healingReady &&
                             providerControlReady &&
                             ipcSucceeded &&
                             (pairedAssignment
                                 ? pairedLeaderVisible
                                 : status.FrenRiderEnabled && status.FrenConfigured && status.FrenVisible);
        var attackingReason = BuildAttackingReadinessReason(
            healingReady,
            healingReason,
            providerLoaded,
            providerControlReady,
            HealbotRuntimeService.GetRotationProviderName(provider),
            capabilityBlocker,
            ipcSucceeded,
            status,
            pairedAssignment,
            pairedLeaderVisible);

        return new JotSetupReadiness(
            dependenciesReady,
            supportedHealer,
            profile?.JobDisplayName ?? healerReason,
            healerConfigEnabled,
            watchedTargetsAvailable,
            healingReady,
            healingReason,
            providerLoaded,
            providerControlReady,
            ipcAvailable,
            status.IsCompatible,
            status.FrenRiderEnabled,
            status.FrenConfigured,
            status.FrenVisible,
            attackingReady,
            attackingReason);
    }

    private void EvaluateAttack()
    {
        if (!plugin.HealbotRuntimeService.IsRotationControlReady)
        {
            HoldRotationDamage();
            StatusText = $"Blocked: {plugin.HealbotRuntimeService.RotationProviderName} control is not active, so JOAT cannot attack.";
            LastIssuedAction = "Blocked";
            LastMatchedRule = "Selected rotation provider control failed.";
            return;
        }

        if (ShouldPause(out var pauseReason))
        {
            HoldRotationDamage();
            StatusText = $"Holding: {pauseReason}.";
            LastIssuedAction = "Holding";
            LastMatchedRule = pauseReason;
            return;
        }

        if (!frenRiderIpcService.TryGetStatus(out var status))
        {
            HoldRotationDamage();
            StatusText = $"Blocked: {frenRiderIpcService.LastFailure}";
            LastIssuedAction = "Blocked";
            LastMatchedRule = "FrenRider IPC unavailable.";
            return;
        }

        var pairedLeaderObjectId = plugin.WatchTargetService.EphemeralQstTargetObjectId;
        var pairedAssignment = plugin.WatchTargetService.HasEphemeralQstTarget;
        if (!pairedAssignment && (!status.FrenRiderEnabled || !status.FrenConfigured || !status.FrenVisible))
        {
            HoldRotationDamage();
            StatusText = $"Blocked: {BuildFrenReadinessReason(status)}";
            LastIssuedAction = "Blocked";
            LastMatchedRule = "FrenRider attack gate failed.";
            return;
        }

        if (!frenRiderIpcService.LeaseAcquired && !frenRiderIpcService.Acquire(out var acquireFailure))
        {
            HoldRotationDamage();
            StatusText = $"Blocked: {acquireFailure}";
            LastIssuedAction = "Blocked";
            LastMatchedRule = "FrenRider lease acquire failed.";
            return;
        }

        if (!frenRiderIpcService.HeartbeatIfDue(out var heartbeatFailure))
        {
            HoldRotationDamage();
            StatusText = $"Blocked: {heartbeatFailure}";
            LastIssuedAction = "Blocked";
            LastMatchedRule = "FrenRider lease heartbeat failed.";
            return;
        }

        if (Plugin.ObjectTable.LocalPlayer is not ICharacter localPlayer)
        {
            HoldRotationDamage();
            StatusText = "Blocked: local healer is unavailable.";
            LastIssuedAction = "Blocked";
            LastMatchedRule = "Local healer unavailable.";
            return;
        }

        if (!plugin.HealbotRuntimeService.IsSupportedLocalJob(out _, out var healerReason))
        {
            HoldRotationDamage();
            StatusText = $"Blocked: {healerReason}";
            LastIssuedAction = "Blocked";
            LastMatchedRule = "Supported local healer unavailable.";
            return;
        }

        if (pairedAssignment)
        {
            EvaluatePairedAttack(localPlayer, pairedLeaderObjectId);
            return;
        }

        EvaluateStandaloneAttack(localPlayer, status.VisibleFrenObjectId);
    }

    private void EvaluatePairedAttack(ICharacter localPlayer, ulong pairedLeaderObjectId)
    {
        if (pairedLeaderObjectId == 0 || ResolveCharacter(pairedLeaderObjectId) is not { } pairedLeader)
        {
            HoldRotationDamage();
            StatusText = "Idle: the exact paired target is selected but remote.";
            LastIssuedAction = "Idle";
            LastMatchedRule = "The exact paired target is remote.";
            return;
        }

        var pairDistance = Vector3.Distance(localPlayer.Position, pairedLeader.Position);
        if (pairDistance > PairedAttackRangeYalms)
        {
            HoldRotationDamage();
            StatusText = $"Holding: the exact paired target is {pairDistance:0.0} yalms away.";
            LastIssuedAction = "Holding";
            LastMatchedRule = "The exact paired target is beyond 30 yalms.";
            return;
        }

        var candidates = BuildTargetCandidates(localPlayer, pairedLeaderObjectId, includeFullHp: true);
        var selection = SelectPairedTarget(candidates, pairedLeader, localPlayer.GameObjectId);

        plugin.HealbotRuntimeService.SetOwnedTarget(pairedLeader);
        var bmr = plugin.Configuration.JoatRotationProvider == JoatRotationProvider.Bmr;
        if (!bmr && !plugin.HealbotRuntimeService.ResumeRotationDamage())
        {
            LastIssuedAction = "Blocked";
            LastMatchedRule = "Rotation provider rejected the attack request.";
            StatusText = $"Blocked: {plugin.HealbotRuntimeService.RotationProviderName} rejected the paired JOAT attack request.";
            return;
        }

        var attackMode = plugin.CoppeliaQstIpcService.EffectiveJoatFullRsrRotation
            ? "full rotation"
            : "DoTs only";
        if (selection.Target == null)
        {
            if (bmr)
            {
                ResumeIdleBmr(pairedLeader);
                return;
            }
            LastIssuedAction = plugin.HealbotRuntimeService.RotationProviderName;
            LastMatchedRule = "Waiting on the friendly paired target for an engaged hostile.";
            StatusText = $"{plugin.HealbotRuntimeService.RotationProviderName} ({attackMode}) waiting; no engaged hostile is eligible within the paired range.";
            return;
        }

        var character = ResolveCharacter(selection.Target.GameObjectId);
        if (character == null)
        {
            if (bmr) HoldRotationDamage();
            LastIssuedAction = plugin.HealbotRuntimeService.RotationProviderName;
            LastMatchedRule = "The selected paired hostile disappeared.";
            StatusText = $"{plugin.HealbotRuntimeService.RotationProviderName} ({attackMode}) waiting; the selected hostile disappeared.";
            return;
        }

        plugin.HealbotRuntimeService.SetOwnedTarget(character);
        if (bmr && !plugin.HealbotRuntimeService.ResumeRotationDamage())
        {
            LastIssuedAction = "Blocked";
            LastMatchedRule = "BMR rejected the selected-hostile attack request.";
            StatusText = $"Blocked: {plugin.BmrIpcService.LastFailure}";
            return;
        }
        LastIssuedAction = plugin.HealbotRuntimeService.RotationProviderName;
        LastMatchedRule =
            $"{plugin.FormatDisplayName(selection.Target.Name)} - {(selection.LeaderTarget ? "paired target" : "engaging pair/healer")}";
        StatusText =
            $"{plugin.HealbotRuntimeService.RotationProviderName} ({attackMode}) on {plugin.FormatDisplayName(selection.Target.Name)} at {selection.Target.HpRatio:P0} HP.";
    }

    private void EvaluateStandaloneAttack(ICharacter localPlayer, ulong frenObjectId)
    {
        var candidates = BuildTargetCandidates(localPlayer, frenObjectId, includeFullHp: false);
        var selection = targetSelector.Select(
            candidates,
            frenObjectId,
            localPlayer.GameObjectId,
            DateTimeOffset.UtcNow);

        if (selection.Target == null)
        {
            if (plugin.Configuration.JoatRotationProvider == JoatRotationProvider.Bmr)
            {
                ResumeIdleBmr(localPlayer);
                return;
            }
            HoldRotationDamage();
            StatusText = "Idle: healing is clear, but no damaged enemy is targeting the Fren or local healer.";
            LastIssuedAction = "Idle";
            LastMatchedRule = "No eligible tagged enemy.";
            return;
        }

        var character = ResolveCharacter(selection.Target.GameObjectId);
        if (character == null)
        {
            HoldRotationDamage();
            StatusText = "Idle: the selected enemy disappeared.";
            LastIssuedAction = "Idle";
            LastMatchedRule = "Target disappeared.";
            return;
        }

        plugin.HealbotRuntimeService.SetOwnedTarget(character);
        if (plugin.HealbotRuntimeService.ResumeRotationDamage())
        {
            targetSelector.MarkActionLanded(character.GameObjectId);
            var attackMode = plugin.CoppeliaQstIpcService.EffectiveJoatFullRsrRotation
                ? "full rotation"
                : "DoTs only";
            LastIssuedAction = plugin.HealbotRuntimeService.RotationProviderName;
            LastMatchedRule = $"{plugin.FormatDisplayName(selection.Target.Name)} - {(selection.Retained ? "retained" : "selected")}";
            StatusText = $"{plugin.HealbotRuntimeService.RotationProviderName} ({attackMode}) on {plugin.FormatDisplayName(selection.Target.Name)} at {selection.Target.HpRatio:P0} HP.";
            return;
        }

        LastIssuedAction = "Blocked";
        LastMatchedRule = $"{plugin.FormatDisplayName(selection.Target.Name)} - rotation provider rejected the attack request";
        StatusText = $"Blocked: {plugin.HealbotRuntimeService.RotationProviderName} rejected the attack request for {plugin.FormatDisplayName(selection.Target.Name)}.";
    }

    private void ResumeIdleBmr(ICharacter friendlyTarget)
    {
        if (!IsBmrHandoffReady)
        {
            HoldRotationDamage();
            StatusText = $"Blocked: {BmrHandoffBlocker}";
            LastIssuedAction = "Blocked";
            LastMatchedRule = "FrenRider suppression acknowledgement is required.";
            return;
        }

        // The exact owned preset uses Manual targeting and ForceST. A friendly
        // primary target leaves Dhog's approved idle healing/raising available
        // without selecting an otherwise ineligible enemy for damage.
        plugin.HealbotRuntimeService.SetOwnedTarget(friendlyTarget);
        if (!plugin.HealbotRuntimeService.ResumeRotationDamage())
        {
            HoldRotationDamage();
            StatusText = $"Blocked: {plugin.BmrIpcService.LastFailure}";
            LastIssuedAction = "Blocked";
            LastMatchedRule = "BMR rejected the idle healing request.";
            return;
        }
        StatusText = "Full BMR is idle on a friendly target; native healing and raising remain enabled.";
        LastIssuedAction = "BMR";
        LastMatchedRule = "No eligible hostile; Coppelia's healing rules are idle.";
    }

    private PowerlevelTargetSnapshot[] BuildTargetCandidates(
        ICharacter localPlayer,
        ulong protectedLeaderObjectId,
        bool includeFullHp)
    {
        var result = new List<PowerlevelTargetSnapshot>();

        foreach (var obj in Plugin.ObjectTable)
        {
            if (obj is not IBattleNpc battleNpc)
                continue;

            if (obj.GameObjectId == localPlayer.GameObjectId ||
                obj.GameObjectId == protectedLeaderObjectId ||
                battleNpc.BattleNpcKind != BattleNpcSubKind.Combatant ||
                !battleNpc.StatusFlags.HasFlag(StatusFlags.Hostile))
            {
                continue;
            }

            var preliminary = new PowerlevelTargetSnapshot(
                obj.GameObjectId,
                obj.Name.TextValue,
                battleNpc.CurrentHp,
                battleNpc.MaxHp,
                Vector3.Distance(localPlayer.Position, obj.Position),
                battleNpc.CurrentHp == 0,
                obj.IsTargetable,
                obj.ObjectKind == ObjectKind.BattleNpc,
                obj.TargetObjectId,
                IsUsable: true);

            if (includeFullHp)
            {
                if (preliminary.GameObjectId == 0 || preliminary.IsDead ||
                    !preliminary.IsTargetable || preliminary.MaxHp == 0)
                {
                    continue;
                }
            }
            else if (!PowerlevelTargetSelector.IsInitiallyEligible(
                         preliminary,
                         protectedLeaderObjectId,
                         localPlayer.GameObjectId))
            {
                continue;
            }

            result.Add(preliminary);
        }

        return result.ToArray();
    }

    private static (PowerlevelTargetSnapshot? Target, bool LeaderTarget) SelectPairedTarget(
        IReadOnlyList<PowerlevelTargetSnapshot> candidates,
        ICharacter pairedLeader,
        ulong localPlayerObjectId)
    {
        var leaderTarget = candidates.FirstOrDefault(candidate =>
            candidate.GameObjectId == pairedLeader.TargetObjectId &&
            (candidate.IsDamaged || candidate.TargetObjectId != 0));
        if (leaderTarget != null)
            return (leaderTarget, true);

        var engagingTarget = candidates
            .Where(candidate =>
                candidate.TargetObjectId == pairedLeader.GameObjectId ||
                candidate.TargetObjectId == localPlayerObjectId)
            .OrderBy(candidate => candidate.HpRatio)
            .ThenBy(candidate => candidate.Distance)
            .ThenBy(candidate => candidate.GameObjectId)
            .FirstOrDefault();
        return (engagingTarget, false);
    }

    private static ICharacter? ResolveCharacter(ulong gameObjectId)
    {
        foreach (var obj in Plugin.ObjectTable)
        {
            if (obj.GameObjectId == gameObjectId && obj is ICharacter character)
                return character;
        }

        return null;
    }

    private static bool ShouldPause(out string reason)
    {
        reason = string.Empty;
        if (Plugin.Condition[ConditionFlag.BetweenAreas] || Plugin.Condition[ConditionFlag.BetweenAreas51])
        {
            reason = "between areas";
            return true;
        }

        if (Plugin.Condition[ConditionFlag.Mounted] || Plugin.Condition[ConditionFlag.Mounting71])
        {
            reason = "mounted or mounting";
            return true;
        }

        if (Plugin.Condition[ConditionFlag.Unconscious])
        {
            reason = "dead";
            return true;
        }

        if (Plugin.Condition[ConditionFlag.OccupiedInQuestEvent] ||
            Plugin.Condition[ConditionFlag.OccupiedInCutSceneEvent] ||
            Plugin.Condition[ConditionFlag.Occupied33] ||
            Plugin.Condition[ConditionFlag.Occupied39] ||
            Plugin.Condition[ConditionFlag.WatchingCutscene])
        {
            reason = "occupied or in cutscene";
            return true;
        }

        if (Plugin.ObjectTable.LocalPlayer is IBattleChara localPlayer)
        {
            if (localPlayer.CurrentHp == 0)
            {
                reason = "dead";
                return true;
            }

            if (localPlayer.IsCasting)
            {
                reason = $"casting action {localPlayer.CastActionId}";
                return true;
            }
        }

        return false;
    }

    private static string BuildHealingReadinessReason(
        bool dependenciesReady,
        bool supportedHealer,
        string healerReason,
        bool healerConfigEnabled,
        bool watchedTargetsAvailable)
    {
        if (!dependenciesReady)
            return "Required healing dependencies are missing.";
        if (!supportedHealer)
            return healerReason;
        if (!healerConfigEnabled)
            return "The equipped healer's action matrix is disabled.";
        if (!watchedTargetsAvailable)
            return "Select at least one target in Watch or wait for a QST assignment.";

        return "JOAT healing is ready.";
    }

    private string BuildAttackingReadinessReason(
        bool healingReady,
        string healingReason,
        bool providerLoaded,
        bool providerControlReady,
        string providerName,
        string capabilityBlocker,
        bool ipcSucceeded,
        FrenRiderPowerlevelStatus status,
        bool pairedAssignment,
        bool pairedLeaderVisible)
    {
        if (!healingReady)
            return $"Attacking waits for healing readiness: {healingReason}";
        if (!providerLoaded)
            return $"{providerName} must be loaded for JOAT attacking.";
        if (!providerControlReady)
        {
            if (!string.IsNullOrWhiteSpace(capabilityBlocker))
                return capabilityBlocker;
            return $"Coppelia could not acquire working {providerName} control.";
        }
        if (!ipcSucceeded)
            return frenRiderIpcService.LastFailure;
        if (pairedAssignment)
            return pairedLeaderVisible
                ? "JOAT attacking is ready when healing is idle."
                : "The exact QST target is selected but remote.";

        return BuildFrenReadinessReason(status);
    }

    private static string BuildFrenReadinessReason(FrenRiderPowerlevelStatus status)
    {
        if (!status.FrenRiderEnabled)
            return "FrenRider must be enabled.";
        if (!status.FrenConfigured)
            return "FrenRider must have a configured Fren.";
        if (!status.FrenVisible)
            return "FrenRider's configured Fren must be visible.";

        return "JOAT attacking is ready when healing is idle.";
    }

    private void HoldRotationDamage()
    {
        _ = plugin.HealbotRuntimeService.HoldRotationDamage();
    }
}

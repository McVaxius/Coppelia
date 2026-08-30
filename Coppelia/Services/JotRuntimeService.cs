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
    private readonly RsrIpcService rsrIpcService;
    private readonly PowerlevelTargetSelector targetSelector = new();

    private bool armed;

    public JotRuntimeService(
        Plugin plugin,
        FrenRiderPowerlevelIpcService frenRiderIpcService,
        RsrIpcService rsrIpcService)
    {
        this.plugin = plugin;
        this.frenRiderIpcService = frenRiderIpcService;
        this.rsrIpcService = rsrIpcService;
    }

    public string StatusText { get; private set; } = "JOAT attacking is off.";
    public string LastIssuedAction { get; private set; } = "Idle";
    public string LastMatchedRule { get; private set; } = "Waiting for an idle healing decision.";

    public void Dispose()
        => Deactivate("Plugin unloading.");

    public void Activate()
    {
        armed = true;
        targetSelector.Clear();
        frenRiderIpcService.ResetSession();
        StatusText = "JOAT attacking is waiting for healing to be idle.";
        LastIssuedAction = "Idle";
        LastMatchedRule = "Waiting for an idle healing decision.";
    }

    public void Deactivate(string reason)
    {
        HoldRsrDamage();
        frenRiderIpcService.Release(reason);
        armed = false;
        targetSelector.Clear();
        StatusText = reason;
        LastIssuedAction = "Idle";
        LastMatchedRule = "Waiting for an idle healing decision.";
    }

    public void Update(HealbotDecisionOutcome healingDecision)
    {
        if (!plugin.Configuration.PluginEnabled ||
            !plugin.Configuration.AutomationEnabled ||
            plugin.Configuration.BotMode != BotMode.Jot)
        {
            if (armed || frenRiderIpcService.LeaseAcquired)
                Deactivate("JOAT attacking is off.");
            else
                StatusText = "JOAT attacking is off.";
            return;
        }

        if (!armed)
            Activate();

        if (frenRiderIpcService.LeaseAcquired &&
            !frenRiderIpcService.HeartbeatIfDue(out var heartbeatFailure))
        {
            HoldRsrDamage();
            StatusText = $"Blocked: {heartbeatFailure}";
            LastIssuedAction = "Blocked";
            LastMatchedRule = "FrenRider lease heartbeat failed.";
            return;
        }

        switch (healingDecision)
        {
            case HealbotDecisionOutcome.None:
                return;
            case HealbotDecisionOutcome.Queued:
                HoldRsrDamage();
                StatusText = $"Holding: healing queued {plugin.HealbotRuntimeService.LastIssuedAction}.";
                LastIssuedAction = "Holding";
                LastMatchedRule = "Healing action won this decision cycle.";
                return;
            case HealbotDecisionOutcome.Blocked:
                HoldRsrDamage();
                StatusText = plugin.HealbotRuntimeService.LastIssuedAction == "Casting"
                    ? $"Holding: the current cast blocks attacking. {plugin.HealbotRuntimeService.StatusText}"
                    : $"Holding: healing is blocked. {plugin.HealbotRuntimeService.StatusText}";
                LastIssuedAction = "Blocked";
                LastMatchedRule = plugin.HealbotRuntimeService.LastIssuedAction == "Casting"
                    ? "Current cast blocks this attack cycle."
                    : "A matched healing action is blocked.";
                return;
            case HealbotDecisionOutcome.Unavailable:
                HoldRsrDamage();
                StatusText = $"Blocked: healing is not ready. {plugin.HealbotRuntimeService.StatusText}";
                LastIssuedAction = "Blocked";
                LastMatchedRule = "Healing dependencies, configuration, or watched targets are unavailable.";
                return;
            case HealbotDecisionOutcome.Idle:
                EvaluateAttack();
                return;
        }
    }

    internal JotSetupReadiness GetSetupReadiness()
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

        var rsrLoaded = plugin.DependencyService.Current.RotationSolverLoaded;
        var joatRunning = plugin.Configuration.PluginEnabled &&
                          plugin.Configuration.AutomationEnabled &&
                          plugin.Configuration.BotMode == BotMode.Jot;
        var rsrControlReady = rsrLoaded &&
                              (!joatRunning || plugin.HealbotRuntimeService.IsRsrControlReady);
        var ipcSucceeded = frenRiderIpcService.TryGetStatus(out var status);
        var ipcAvailable = ipcSucceeded || status.ContractVersion > 0;
        var pairedAssignment = plugin.WatchTargetService.HasEphemeralQstTarget;
        var pairedLeaderVisible = plugin.WatchTargetService.IsEphemeralQstTargetVisible;
        var attackingReady = healingReady &&
                             rsrControlReady &&
                             ipcSucceeded &&
                             (pairedAssignment
                                 ? pairedLeaderVisible
                                 : status.FrenRiderEnabled && status.FrenConfigured && status.FrenVisible);
        var attackingReason = BuildAttackingReadinessReason(
            healingReady,
            healingReason,
            rsrLoaded,
            rsrControlReady,
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
            rsrLoaded,
            rsrControlReady,
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
        if (!plugin.HealbotRuntimeService.IsRsrControlReady)
        {
            HoldRsrDamage();
            StatusText = "Blocked: Rotation Solver Reborn control is not active, so JOAT cannot attack.";
            LastIssuedAction = "Blocked";
            LastMatchedRule = "Rotation Solver Reborn control failed.";
            return;
        }

        if (ShouldPause(out var pauseReason))
        {
            HoldRsrDamage();
            StatusText = $"Holding: {pauseReason}.";
            LastIssuedAction = "Holding";
            LastMatchedRule = pauseReason;
            return;
        }

        if (!frenRiderIpcService.TryGetStatus(out var status))
        {
            HoldRsrDamage();
            StatusText = $"Blocked: {frenRiderIpcService.LastFailure}";
            LastIssuedAction = "Blocked";
            LastMatchedRule = "FrenRider IPC unavailable.";
            return;
        }

        var pairedLeaderObjectId = plugin.WatchTargetService.EphemeralQstTargetObjectId;
        var pairedAssignment = plugin.WatchTargetService.HasEphemeralQstTarget;
        if (!pairedAssignment && (!status.FrenRiderEnabled || !status.FrenConfigured || !status.FrenVisible))
        {
            HoldRsrDamage();
            StatusText = $"Blocked: {BuildFrenReadinessReason(status)}";
            LastIssuedAction = "Blocked";
            LastMatchedRule = "FrenRider attack gate failed.";
            return;
        }

        if (!frenRiderIpcService.LeaseAcquired && !frenRiderIpcService.Acquire(out var acquireFailure))
        {
            HoldRsrDamage();
            StatusText = $"Blocked: {acquireFailure}";
            LastIssuedAction = "Blocked";
            LastMatchedRule = "FrenRider lease acquire failed.";
            return;
        }

        if (!frenRiderIpcService.HeartbeatIfDue(out var heartbeatFailure))
        {
            HoldRsrDamage();
            StatusText = $"Blocked: {heartbeatFailure}";
            LastIssuedAction = "Blocked";
            LastMatchedRule = "FrenRider lease heartbeat failed.";
            return;
        }

        if (Plugin.ObjectTable.LocalPlayer is not ICharacter localPlayer)
        {
            HoldRsrDamage();
            StatusText = "Blocked: local healer is unavailable.";
            LastIssuedAction = "Blocked";
            LastMatchedRule = "Local healer unavailable.";
            return;
        }

        if (!plugin.HealbotRuntimeService.IsSupportedLocalJob(out _, out var healerReason))
        {
            HoldRsrDamage();
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
            HoldRsrDamage();
            StatusText = "Idle: the exact paired target is selected but remote.";
            LastIssuedAction = "Idle";
            LastMatchedRule = "The exact paired target is remote.";
            return;
        }

        var pairDistance = Vector3.Distance(localPlayer.Position, pairedLeader.Position);
        if (pairDistance > PairedAttackRangeYalms)
        {
            HoldRsrDamage();
            StatusText = $"Holding: the exact paired target is {pairDistance:0.0} yalms away.";
            LastIssuedAction = "Holding";
            LastMatchedRule = "The exact paired target is beyond 30 yalms.";
            return;
        }

        var candidates = BuildTargetCandidates(localPlayer, pairedLeaderObjectId, includeFullHp: true);
        var selection = SelectPairedTarget(candidates, pairedLeader, localPlayer.GameObjectId);

        Plugin.TargetManager.Target = pairedLeader;
        if (!rsrIpcService.TrySetMode(RsrIpcService.RsrStateCommandType.Manual))
        {
            LastIssuedAction = "Blocked";
            LastMatchedRule = "RSR Manual failed.";
            StatusText = "Blocked: Rotation Solver Reborn rejected Manual mode for the paired JOAT assignment.";
            return;
        }

        var attackMode = plugin.CoppeliaQstIpcService.EffectiveJoatFullRsrRotation
            ? "full RSR rotation"
            : "DoTs only";
        if (selection.Target == null)
        {
            LastIssuedAction = "RSR Manual";
            LastMatchedRule = "Waiting on the friendly paired target for an engaged hostile.";
            StatusText = $"RSR Manual ({attackMode}) waiting; no engaged hostile is eligible within the paired range.";
            return;
        }

        var character = ResolveCharacter(selection.Target.GameObjectId);
        if (character == null)
        {
            LastIssuedAction = "RSR Manual";
            LastMatchedRule = "The selected paired hostile disappeared.";
            StatusText = $"RSR Manual ({attackMode}) waiting; the selected hostile disappeared.";
            return;
        }

        Plugin.TargetManager.Target = character;
        LastIssuedAction = "RSR Manual";
        LastMatchedRule =
            $"{plugin.FormatDisplayName(selection.Target.Name)} - {(selection.LeaderTarget ? "paired target" : "engaging pair/healer")}";
        StatusText =
            $"RSR Manual ({attackMode}) on {plugin.FormatDisplayName(selection.Target.Name)} at {selection.Target.HpRatio:P0} HP.";
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
            HoldRsrDamage();
            StatusText = "Idle: healing is clear, but no damaged enemy is targeting the Fren or local healer.";
            LastIssuedAction = "Idle";
            LastMatchedRule = "No eligible tagged enemy.";
            return;
        }

        var character = ResolveCharacter(selection.Target.GameObjectId);
        if (character == null)
        {
            HoldRsrDamage();
            StatusText = "Idle: the selected enemy disappeared.";
            LastIssuedAction = "Idle";
            LastMatchedRule = "Target disappeared.";
            return;
        }

        Plugin.TargetManager.Target = character;
        if (rsrIpcService.TrySetMode(RsrIpcService.RsrStateCommandType.Manual))
        {
            targetSelector.MarkActionLanded(character.GameObjectId);
            var attackMode = plugin.CoppeliaQstIpcService.EffectiveJoatFullRsrRotation
                ? "full RSR rotation"
                : "DoTs only";
            LastIssuedAction = "RSR Manual";
            LastMatchedRule = $"{plugin.FormatDisplayName(selection.Target.Name)} - {(selection.Retained ? "retained" : "selected")}";
            StatusText = $"RSR Manual ({attackMode}) on {plugin.FormatDisplayName(selection.Target.Name)} at {selection.Target.HpRatio:P0} HP.";
            return;
        }

        LastIssuedAction = "Blocked";
        LastMatchedRule = $"{plugin.FormatDisplayName(selection.Target.Name)} - RSR Manual failed";
        StatusText = $"Blocked: Rotation Solver Reborn rejected Manual mode for {plugin.FormatDisplayName(selection.Target.Name)}.";
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
                battleNpc.BattleNpcKind is BattleNpcSubKind.Buddy or BattleNpcSubKind.NpcPartyMember or BattleNpcSubKind.Combatant)
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
        bool rsrLoaded,
        bool rsrControlReady,
        bool ipcSucceeded,
        FrenRiderPowerlevelStatus status,
        bool pairedAssignment,
        bool pairedLeaderVisible)
    {
        if (!healingReady)
            return $"Attacking waits for healing readiness: {healingReason}";
        if (!rsrLoaded)
            return "Rotation Solver Reborn must be loaded for JOAT attacking.";
        if (!rsrControlReady)
            return "Coppelia could not acquire working Rotation Solver Reborn control.";
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

    private void HoldRsrDamage()
    {
        if (rsrIpcService.HasSessionSnapshot)
            rsrIpcService.TrySetMode(RsrIpcService.RsrStateCommandType.Off);
    }
}

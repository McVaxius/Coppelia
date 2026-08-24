using System.Numerics;
using Coppelia.Models;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.ClientState.Objects.Types;

namespace Coppelia.Services;

internal sealed class JotRuntimeService : IDisposable
{
    private readonly Plugin plugin;
    private readonly FrenRiderPowerlevelIpcService frenRiderIpcService;
    private readonly ActionExecutionService actionExecutionService;
    private readonly PowerlevelTargetSelector targetSelector = new();

    private bool armed;

    public JotRuntimeService(
        Plugin plugin,
        FrenRiderPowerlevelIpcService frenRiderIpcService,
        ActionExecutionService actionExecutionService)
    {
        this.plugin = plugin;
        this.frenRiderIpcService = frenRiderIpcService;
        this.actionExecutionService = actionExecutionService;
    }

    public string StatusText { get; private set; } = "JOT attacking is off.";
    public string LastIssuedAction { get; private set; } = "Idle";
    public string LastMatchedRule { get; private set; } = "Waiting for an idle healing decision.";

    public void Dispose()
        => Deactivate("Plugin unloading.");

    public void Activate()
    {
        armed = true;
        targetSelector.Clear();
        frenRiderIpcService.ResetSession();
        StatusText = "JOT attacking is waiting for healing to be idle.";
        LastIssuedAction = "Idle";
        LastMatchedRule = "Waiting for an idle healing decision.";
    }

    public void Deactivate(string reason)
    {
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
                Deactivate("JOT attacking is off.");
            else
                StatusText = "JOT attacking is off.";
            return;
        }

        if (!armed)
            Activate();

        if (frenRiderIpcService.LeaseAcquired &&
            !frenRiderIpcService.HeartbeatIfDue(out var heartbeatFailure))
        {
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
                StatusText = $"Holding: healing queued {plugin.HealbotRuntimeService.LastIssuedAction}.";
                LastIssuedAction = "Holding";
                LastMatchedRule = "Healing action won this decision cycle.";
                return;
            case HealbotDecisionOutcome.Blocked:
                StatusText = plugin.HealbotRuntimeService.LastIssuedAction == "Casting"
                    ? $"Holding: the current cast blocks attacking. {plugin.HealbotRuntimeService.StatusText}"
                    : $"Holding: healing is blocked. {plugin.HealbotRuntimeService.StatusText}";
                LastIssuedAction = "Blocked";
                LastMatchedRule = plugin.HealbotRuntimeService.LastIssuedAction == "Casting"
                    ? "Current cast blocks this attack cycle."
                    : "A matched healing action is blocked.";
                return;
            case HealbotDecisionOutcome.Unavailable:
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
        var watchedTargetsAvailable = plugin.WatchTargetService.ActiveTargets.Count > 0;
        var healingReady = dependenciesReady && supportedHealer && healerConfigEnabled && watchedTargetsAvailable;
        var healingReason = BuildHealingReadinessReason(
            dependenciesReady,
            supportedHealer,
            healerReason,
            healerConfigEnabled,
            watchedTargetsAvailable);

        var ipcSucceeded = frenRiderIpcService.TryGetStatus(out var status);
        var ipcAvailable = ipcSucceeded || status.ContractVersion > 0;
        var attackingReady = healingReady &&
                             ipcSucceeded &&
                             status.FrenRiderEnabled &&
                             status.FrenConfigured &&
                             status.FrenVisible;
        var attackingReason = BuildAttackingReadinessReason(
            healingReady,
            healingReason,
            ipcSucceeded,
            status);

        return new JotSetupReadiness(
            dependenciesReady,
            supportedHealer,
            profile?.JobDisplayName ?? healerReason,
            healerConfigEnabled,
            watchedTargetsAvailable,
            healingReady,
            healingReason,
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
        if (!plugin.HealbotRuntimeService.IsRsrDamageIsolationReady)
        {
            StatusText = "Blocked: Rotation Solver isolation is not active, so JOT attacking will not compete with it.";
            LastIssuedAction = "Blocked";
            LastMatchedRule = "Rotation Solver isolation failed.";
            return;
        }

        if (ShouldPause(out var pauseReason))
        {
            StatusText = $"Holding: {pauseReason}.";
            LastIssuedAction = "Holding";
            LastMatchedRule = pauseReason;
            return;
        }

        if (!frenRiderIpcService.TryGetStatus(out var status))
        {
            StatusText = $"Blocked: {frenRiderIpcService.LastFailure}";
            LastIssuedAction = "Blocked";
            LastMatchedRule = "FrenRider IPC unavailable.";
            return;
        }

        if (!status.FrenRiderEnabled || !status.FrenConfigured || !status.FrenVisible)
        {
            StatusText = $"Blocked: {BuildFrenReadinessReason(status)}";
            LastIssuedAction = "Blocked";
            LastMatchedRule = "FrenRider attack gate failed.";
            return;
        }

        if (!frenRiderIpcService.LeaseAcquired && !frenRiderIpcService.Acquire(out var acquireFailure))
        {
            StatusText = $"Blocked: {acquireFailure}";
            LastIssuedAction = "Blocked";
            LastMatchedRule = "FrenRider lease acquire failed.";
            return;
        }

        if (!frenRiderIpcService.HeartbeatIfDue(out var heartbeatFailure))
        {
            StatusText = $"Blocked: {heartbeatFailure}";
            LastIssuedAction = "Blocked";
            LastMatchedRule = "FrenRider lease heartbeat failed.";
            return;
        }

        if (Plugin.ObjectTable.LocalPlayer is not ICharacter localPlayer)
        {
            StatusText = "Blocked: local healer is unavailable.";
            LastIssuedAction = "Blocked";
            LastMatchedRule = "Local healer unavailable.";
            return;
        }

        if (!plugin.HealbotRuntimeService.IsSupportedLocalJob(out var profile, out var healerReason))
        {
            StatusText = $"Blocked: {healerReason}";
            LastIssuedAction = "Blocked";
            LastMatchedRule = "Supported local healer unavailable.";
            return;
        }

        var candidates = BuildTargetCandidates(localPlayer, status.VisibleFrenObjectId, profile!);
        var selection = targetSelector.Select(
            candidates,
            status.VisibleFrenObjectId,
            localPlayer.GameObjectId,
            DateTimeOffset.UtcNow);

        if (selection.Target == null)
        {
            StatusText = "Idle: healing is clear, but no damaged enemy is targeting the Fren or local healer.";
            LastIssuedAction = "Idle";
            LastMatchedRule = "No eligible tagged enemy.";
            return;
        }

        var character = ResolveCharacter(selection.Target.GameObjectId);
        if (character == null)
        {
            StatusText = "Idle: the selected enemy disappeared.";
            LastIssuedAction = "Idle";
            LastMatchedRule = "Target disappeared.";
            return;
        }

        Plugin.TargetManager.Target = character;
        if (actionExecutionService.TryExecuteJotFiller(profile!, character, out var action, out var failureReason))
        {
            targetSelector.MarkActionLanded(character.GameObjectId);
            LastIssuedAction = action;
            LastMatchedRule = $"{plugin.FormatDisplayName(selection.Target.Name)} - {(selection.Retained ? "retained" : "selected")}";
            StatusText = $"Queued {action} on {plugin.FormatDisplayName(selection.Target.Name)} at {selection.Target.HpRatio:P0} HP.";
            return;
        }

        LastIssuedAction = "Blocked";
        LastMatchedRule = $"{plugin.FormatDisplayName(selection.Target.Name)} - {failureReason}";
        StatusText = $"Target {plugin.FormatDisplayName(selection.Target.Name)} blocked: {failureReason}";
    }

    private PowerlevelTargetSnapshot[] BuildTargetCandidates(
        ICharacter localPlayer,
        ulong frenObjectId,
        HealbotJobProfile profile)
    {
        var result = new List<PowerlevelTargetSnapshot>();

        foreach (var obj in Plugin.ObjectTable)
        {
            if (obj is not IBattleNpc battleNpc)
                continue;

            if (obj.GameObjectId == localPlayer.GameObjectId ||
                obj.GameObjectId == frenObjectId ||
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

            if (!PowerlevelTargetSelector.IsInitiallyEligible(preliminary, frenObjectId, localPlayer.GameObjectId))
                continue;

            var isUsable = actionExecutionService.CanUseAnyJotFiller(profile, battleNpc, out _);
            result.Add(preliminary with { IsUsable = isUsable });
        }

        return result.ToArray();
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
            return "Select at least one target in Watch; the Fren is never added automatically.";

        return "JOT healing is ready.";
    }

    private string BuildAttackingReadinessReason(
        bool healingReady,
        string healingReason,
        bool ipcSucceeded,
        FrenRiderPowerlevelStatus status)
    {
        if (!healingReady)
            return $"Attacking waits for healing readiness: {healingReason}";
        if (!ipcSucceeded)
            return frenRiderIpcService.LastFailure;

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

        return "JOT attacking is ready when healing is idle.";
    }
}

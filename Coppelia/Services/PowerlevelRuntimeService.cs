using System.Numerics;
using Coppelia.Models;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.ClientState.Objects.Types;
using ClassJobSheet = Lumina.Excel.Sheets.ClassJob;

namespace Coppelia.Services;

internal sealed class PowerlevelRuntimeService : IDisposable
{
    private readonly Plugin plugin;
    private readonly FrenRiderPowerlevelIpcService frenRiderIpcService;
    private readonly ActionExecutionService actionExecutionService;
    private readonly PowerlevelTargetSelector targetSelector = new();

    private DateTimeOffset nextDecisionUtc = DateTimeOffset.MinValue;
    private bool armed;
    private ulong previousTargetGameObjectId;

    public PowerlevelRuntimeService(
        Plugin plugin,
        FrenRiderPowerlevelIpcService frenRiderIpcService,
        ActionExecutionService actionExecutionService)
    {
        this.plugin = plugin;
        this.frenRiderIpcService = frenRiderIpcService;
        this.actionExecutionService = actionExecutionService;
    }

    public string StatusText { get; private set; } = "PowerlevelBot mode is off.";
    public string LastIssuedAction { get; private set; } = "Idle";
    public string LastMatchedRule { get; private set; } = "No target selected.";

    public void Dispose()
        => Deactivate("Plugin unloading.");

    public void Activate()
    {
        if (!armed)
            CapturePreviousTarget();

        armed = true;
        nextDecisionUtc = DateTimeOffset.MinValue;
        targetSelector.Clear();
        frenRiderIpcService.ResetSession();
        LastIssuedAction = "Idle";
        LastMatchedRule = "No target selected.";
        StatusText = "PowerlevelBot arming.";
    }

    public void Deactivate(string reason)
    {
        frenRiderIpcService.Release(reason);
        if (armed)
            RestorePreviousTarget();

        armed = false;
        targetSelector.Clear();
        previousTargetGameObjectId = 0;
        LastIssuedAction = "Idle";
        LastMatchedRule = "No target selected.";
        StatusText = reason;
    }

    public bool TryValidateActivation(out string reason)
    {
        if (!frenRiderIpcService.TryGetStatus(out var status))
        {
            reason = frenRiderIpcService.LastFailure;
            return false;
        }

        var result = BuildActivationResult(status);
        reason = result.Reason;
        return result.Ready;
    }

    public void Update()
    {
        if (!plugin.Configuration.PluginEnabled)
        {
            Deactivate("Plugin disabled.");
            return;
        }

        if (!plugin.Configuration.AutomationEnabled || plugin.Configuration.BotMode != BotMode.PowerlevelBot)
        {
            if (armed || frenRiderIpcService.LeaseAcquired)
                Deactivate("PowerlevelBot mode is off.");
            else
                StatusText = "PowerlevelBot mode is off.";
            return;
        }

        if (!armed)
            Activate();

        if (ShouldPause(out var pauseReason))
        {
            StatusText = $"Holding: {pauseReason}";
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

        var activation = BuildActivationResult(status);
        if (!activation.Ready)
        {
            StatusText = $"Blocked: {activation.Reason}";
            LastIssuedAction = "Blocked";
            LastMatchedRule = "Powerlevel gate failed.";
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

        if (DateTimeOffset.UtcNow < nextDecisionUtc)
            return;

        nextDecisionUtc = DateTimeOffset.UtcNow.AddMilliseconds(650);
        EvaluateTargets(status);
    }

    private PowerlevelActivationResult BuildActivationResult(FrenRiderPowerlevelStatus status)
    {
        var job = plugin.Configuration.PowerlevelJob;
        var selectedJobUnlocked = TryIsPowerlevelJobUnlocked(job);
        var currentJobId = Plugin.PlayerState.ClassJob.RowId;
        var companionActive = status.CompanionActive || HasActiveCompanionChocobo();

        return PowerlevelActivationPolicy.Evaluate(new PowerlevelActivationInput(
            job,
            selectedJobUnlocked,
            currentJobId,
            FrenRiderIpcAvailable: true,
            status.IsCompatible,
            status.FrenRiderEnabled,
            status.FrenConfigured,
            status.FrenVisible,
            companionActive));
    }

    private void EvaluateTargets(FrenRiderPowerlevelStatus status)
    {
        if (Plugin.ObjectTable.LocalPlayer is not ICharacter localPlayer)
        {
            StatusText = "Local player unavailable.";
            LastIssuedAction = "Blocked";
            LastMatchedRule = "No local player.";
            return;
        }

        var job = plugin.Configuration.PowerlevelJob;
        var candidates = BuildTargetCandidates(localPlayer, status.VisibleFrenObjectId, job);
        var selection = targetSelector.Select(
            candidates,
            status.VisibleFrenObjectId,
            localPlayer.GameObjectId,
            DateTimeOffset.UtcNow);

        if (selection.Target == null)
        {
            StatusText = "No damaged enemy is targeting the Fren or local player.";
            LastIssuedAction = "Idle";
            LastMatchedRule = "No eligible target.";
            return;
        }

        var character = ResolveCharacter(selection.Target.GameObjectId);
        if (character == null)
        {
            StatusText = "Selected target disappeared.";
            LastIssuedAction = "Idle";
            LastMatchedRule = "Target disappeared.";
            return;
        }

        Plugin.TargetManager.Target = character;
        if (actionExecutionService.TryExecutePowerlevel(job, character, out var action, out var failureReason))
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

    private PowerlevelTargetSnapshot[] BuildTargetCandidates(ICharacter localPlayer, ulong frenObjectId, PowerlevelJob job)
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
                IsCombatantObject(obj),
                obj.TargetObjectId,
                IsUsable: true);

            if (!PowerlevelTargetSelector.IsInitiallyEligible(preliminary, frenObjectId, localPlayer.GameObjectId))
                continue;

            var isUsable = actionExecutionService.CanUseAnyPowerlevelAction(job, battleNpc, out _);
            result.Add(preliminary with { IsUsable = isUsable });
        }

        return result.ToArray();
    }

    private static bool IsCombatantObject(IGameObject obj)
        => obj.ObjectKind == ObjectKind.BattleNpc;

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

    private static bool HasActiveCompanionChocobo()
    {
        foreach (var obj in Plugin.ObjectTable)
        {
            if (obj is IBattleNpc { BattleNpcKind: BattleNpcSubKind.Buddy })
                return true;
        }

        return false;
    }

    private static bool TryIsPowerlevelJobUnlocked(PowerlevelJob job)
    {
        if (!job.IsSupportedPowerlevelJob())
            return false;

        var classJobSheet = Plugin.DataManager.GetExcelSheet<ClassJobSheet>();
        if (classJobSheet == null || !classJobSheet.TryGetRow(job.ToJobId(), out var classJob))
            return false;

        return Plugin.UnlockState.IsClassJobUnlocked(classJob);
    }

    private void CapturePreviousTarget()
    {
        previousTargetGameObjectId = Plugin.TargetManager.Target?.GameObjectId ?? 0;
    }

    private void RestorePreviousTarget()
    {
        if (previousTargetGameObjectId == 0)
            return;

        foreach (var obj in Plugin.ObjectTable)
        {
            if (obj.GameObjectId != previousTargetGameObjectId)
                continue;

            Plugin.TargetManager.Target = obj;
            return;
        }
    }
}

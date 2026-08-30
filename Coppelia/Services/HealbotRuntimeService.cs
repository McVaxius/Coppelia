using Coppelia.Models;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Types;
using System.Numerics;

namespace Coppelia.Services;

internal sealed class HealbotRuntimeService : IDisposable
{
    private readonly Plugin plugin;
    private readonly DependencyService dependencyService;
    private readonly WatchTargetService watchTargetService;
    private readonly RsrIpcService rsrIpcService;
    private readonly ActionExecutionService actionExecutionService;

    private DateTimeOffset nextDecisionUtc = DateTimeOffset.MinValue;
    private bool profileArmed;
    private bool rsrIsolationApplied;
    private string appliedSignature = string.Empty;
    private ulong previousTargetGameObjectId;

    public HealbotRuntimeService(
        Plugin plugin,
        DependencyService dependencyService,
        WatchTargetService watchTargetService,
        RsrIpcService rsrIpcService,
        ActionExecutionService actionExecutionService)
    {
        this.plugin = plugin;
        this.dependencyService = dependencyService;
        this.watchTargetService = watchTargetService;
        this.rsrIpcService = rsrIpcService;
        this.actionExecutionService = actionExecutionService;
    }

    public string StatusText { get; private set; } = "Healbot mode is off.";
    public string LastIssuedAction { get; private set; } = "Idle";
    public string LastMatchedRule { get; private set; } = "No rule matched.";
    public string CurrentWatchedTargetName { get; private set; } = string.Empty;
    public string CurrentWatchedTargetHpText { get; private set; } = "HP unavailable";
    public string CurrentTargetLineOfSightState { get; private set; } = "Target not visible";
    public bool IsRsrControlReady
        => dependencyService.Current.RotationSolverLoaded && rsrIsolationApplied;

    public void Dispose()
    {
        Deactivate("Plugin unloading.");
    }

    public bool IsSupportedLocalJob(out HealbotJobProfile? profile, out string reason)
    {
        var jobId = Plugin.PlayerState.ClassJob.RowId;
        if (HealbotJobProfile.TryResolve(jobId, out var resolvedProfile))
        {
            profile = resolvedProfile;
            reason = string.Empty;
            return true;
        }

        profile = null;
        reason = "HealBot only supports WHM, SCH, AST, and SGE.";
        return false;
    }

    public void Activate()
    {
        nextDecisionUtc = DateTimeOffset.MinValue;
        profileArmed = false;
        rsrIsolationApplied = false;
        appliedSignature = string.Empty;
        LastIssuedAction = "Idle";
        LastMatchedRule = "No rule matched.";
    }

    public void Deactivate(string reason)
    {
        plugin.CoppeliaTravelService.ClearLineOfSightRescue();
        if (rsrIpcService.HasSessionSnapshot)
            rsrIpcService.RestoreSessionSnapshot();

        if (profileArmed)
            RestorePreviousTarget();

        profileArmed = false;
        rsrIsolationApplied = false;
        appliedSignature = string.Empty;
        LastIssuedAction = "Idle";
        LastMatchedRule = "No rule matched.";
        StatusText = reason;
        previousTargetGameObjectId = 0;
        ClearCurrentTargetContext();
    }

    public HealbotDecisionOutcome Update()
    {
        if (Plugin.Condition[ConditionFlag.BetweenAreas] ||
            Plugin.Condition[ConditionFlag.BetweenAreas51] ||
            Plugin.ObjectTable.LocalPlayer == null)
        {
            plugin.CoppeliaTravelService.ClearLineOfSightRescue();
            ClearCurrentTargetContext();
            StatusText = "Holding during the area transition.";
            LastIssuedAction = "Travel";
            LastMatchedRule = "Area transition in progress.";
            return HealbotDecisionOutcome.Blocked;
        }

        if (!plugin.Configuration.PluginEnabled)
        {
            Deactivate("Plugin disabled.");
            return HealbotDecisionOutcome.Unavailable;
        }

        if (!plugin.Configuration.AutomationEnabled ||
            plugin.Configuration.BotMode is not (BotMode.HealBot or BotMode.Jot))
        {
            if (profileArmed || rsrIsolationApplied)
                Deactivate("Healbot mode is off.");
            else
                StatusText = "Healbot mode is off.";
            return HealbotDecisionOutcome.Unavailable;
        }

        dependencyService.Refresh();
        if (!dependencyService.Current.IsHealbotReady)
        {
            StatusText = $"Blocked: {dependencyService.BuildMissingDependencyMessage()}";
            LastIssuedAction = "Blocked";
            LastMatchedRule = "Dependencies missing.";
            if (plugin.Configuration.ShowDependencyToasts)
                plugin.ShowDependencyToast(dependencyService.BuildMissingDependencyMessage());
            return HealbotDecisionOutcome.Unavailable;
        }

        if (!IsSupportedLocalJob(out var profile, out var reason))
        {
            StatusText = $"Blocked: {reason}";
            LastIssuedAction = "Blocked";
            LastMatchedRule = "Unsupported local job.";
            return HealbotDecisionOutcome.Unavailable;
        }

        var jobConfig = plugin.Configuration.GetJobConfigForJob(profile!.JobId);
        var rsrLoaded = dependencyService.Current.RotationSolverLoaded;
        var signature = BuildSignature(profile, jobConfig, rsrLoaded);
        if (!profileArmed || !string.Equals(signature, appliedSignature, StringComparison.Ordinal))
            ApplyProfile(profile, signature, rsrLoaded);

        if (CoppeliaPairedActionPolicy.ShouldHold(
                watchTargetService.HasEphemeralQstTarget,
                Plugin.Condition[ConditionFlag.Mounted],
                Plugin.Condition[ConditionFlag.Mounting71]))
        {
            StatusText = "Holding paired healing while the helper is mounted or mounting.";
            LastIssuedAction = "Travel";
            LastMatchedRule = "Paired helper is mounted or mounting.";
            return HealbotDecisionOutcome.Blocked;
        }

        if (Plugin.ObjectTable.LocalPlayer is IBattleChara localPlayer && localPlayer.IsCasting)
        {
            StatusText = $"Holding while casting action {localPlayer.CastActionId}.";
            LastIssuedAction = "Casting";
            LastMatchedRule = "Waiting for the current cast to finish.";
            return HealbotDecisionOutcome.Blocked;
        }

        if (watchTargetService.SelectedTargetCount == 0 || watchTargetService.RuntimeCandidates.Count == 0)
        {
            plugin.CoppeliaTravelService.ClearLineOfSightRescue();
            ClearCurrentTargetContext();
        }

        if (DateTimeOffset.UtcNow < nextDecisionUtc)
            return HealbotDecisionOutcome.None;

        nextDecisionUtc = DateTimeOffset.UtcNow.AddMilliseconds(900);
        return EvaluateSelectedTarget(profile, jobConfig);
    }

    internal bool TryApplyRsrProfileNow()
    {
        dependencyService.Refresh(force: true);
        if (!dependencyService.Current.RotationSolverLoaded ||
            !IsSupportedLocalJob(out var profile, out _))
        {
            return false;
        }

        var jobConfig = plugin.Configuration.GetJobConfigForJob(profile!.JobId);
        var signature = BuildSignature(profile, jobConfig, rsrLoaded: true);
        ApplyProfile(profile, signature, rsrLoaded: true);
        return rsrIsolationApplied;
    }

    private void ApplyProfile(HealbotJobProfile profile, string signature, bool rsrLoaded)
    {
        var joatMode = plugin.Configuration.BotMode == BotMode.Jot;
        if (!profileArmed)
            CapturePreviousTarget();

        if (rsrIpcService.HasSessionSnapshot && !rsrLoaded)
        {
            rsrIpcService.RestoreSessionSnapshot();
            rsrIsolationApplied = false;
        }

        if (!rsrLoaded)
        {
            profileArmed = true;
            rsrIsolationApplied = false;
            appliedSignature = signature;
            StatusText = joatMode
                ? $"Healbot action matrix armed for {profile.JobDisplayName}. RSR is required before JOAT can attack."
                : $"Healbot action matrix armed for {profile.JobDisplayName}. RSR isolation unavailable.";
            return;
        }

        if (rsrIpcService.ApplyHealbotProfile(
                profile,
                plugin.Configuration,
                joatMode,
                plugin.CoppeliaQstIpcService.EffectiveJoatFullRsrRotation))
        {
            profileArmed = true;
            rsrIsolationApplied = true;
            appliedSignature = signature;
            StatusText = joatMode
                ? $"Healbot action matrix armed for {profile.JobDisplayName}. RSR JOAT control active."
                : $"Healbot action matrix armed for {profile.JobDisplayName}. RSR isolation active.";
            return;
        }

        rsrIpcService.RestoreSessionSnapshot();
        profileArmed = true;
        rsrIsolationApplied = false;
        appliedSignature = signature;
        StatusText = joatMode
            ? $"Healbot action matrix armed for {profile.JobDisplayName}. RSR control failed; JOAT attacking is blocked and direct healing remains active."
            : $"Healbot action matrix armed for {profile.JobDisplayName}. RSR isolation failed; direct healing remains active.";
    }

    private HealbotDecisionOutcome EvaluateSelectedTarget(HealbotJobProfile profile, HealerJobConfig jobConfig)
    {
        var activeTargetCount = watchTargetService.SelectedTargetCount;
        if (activeTargetCount == 0)
        {
            plugin.CoppeliaTravelService.ClearLineOfSightRescue();
            ClearCurrentTargetContext();
            StatusText = "No watched targets are active.";
            LastIssuedAction = "Idle";
            LastMatchedRule = "No watched targets.";
            return HealbotDecisionOutcome.Unavailable;
        }

        if (!jobConfig.Enabled)
        {
            StatusText = $"{profile.JobAbbreviation} automation is disabled in HealBot settings.";
            LastIssuedAction = "Idle";
            LastMatchedRule = $"{profile.JobAbbreviation} tab disabled.";
            return HealbotDecisionOutcome.Unavailable;
        }

        var orderedCandidates = watchTargetService.RuntimeCandidates
            .OrderBy(candidate => candidate.Snapshot.IsDead ? 0 : 1)
            .ThenBy(candidate => candidate.Snapshot.IsDead ? 0 : candidate.Snapshot.HpPercent)
            .ThenBy(candidate => candidate.Snapshot.Distance)
            .ThenBy(candidate => candidate.Snapshot.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (orderedCandidates.Length == 0)
        {
            plugin.CoppeliaTravelService.ClearLineOfSightRescue();
            ClearCurrentTargetContext();
            StatusText = watchTargetService.HasEphemeralQstTarget
                ? $"{watchTargetService.EphemeralAssignmentLabel} target {watchTargetService.EphemeralQstTargetName} is selected but remote."
                : $"Watching {activeTargetCount} targets. No live watched target is currently available.";
            LastIssuedAction = "Idle";
            LastMatchedRule = watchTargetService.HasEphemeralQstTarget
                ? $"The exact {watchTargetService.EphemeralAssignmentLabel} target is remote."
                : "No live watched targets.";
            return HealbotDecisionOutcome.Unavailable;
        }

        string? blockedStatus = null;
        string? blockedRule = null;
        ClearCurrentTargetContext();

        foreach (var candidate in orderedCandidates)
        {
            var selectedCharacter = ActionExecutionService.ResolveLiveHealTarget(candidate.Snapshot.GameObjectId);
            if (selectedCharacter == null)
                continue;

            CurrentWatchedTargetName = candidate.Snapshot.Name;
            CurrentWatchedTargetHpText = candidate.Snapshot.MaxHp == 0
                ? "HP unavailable"
                : $"{candidate.Snapshot.HpPercent}%";

            var localCharacter = Plugin.ObjectTable.LocalPlayer as ICharacter;
            var distance = localCharacter == null
                ? float.PositiveInfinity
                : Vector3.Distance(localCharacter.Position, selectedCharacter.Position);
            var hasLineOfSight = true;
            var lineOfSightKnown = localCharacter != null &&
                                   LineOfSightService.TryHasLineOfSight(localCharacter, selectedCharacter, out hasLineOfSight);
            CurrentTargetLineOfSightState = !lineOfSightKnown
                ? "LOS unavailable"
                : hasLineOfSight ? "LOS clear" : "LOS blocked";

            if (LineOfSightService.ShouldRescue(
                    targetVisible: true,
                    distance,
                    lineOfSightBlocked: lineOfSightKnown && !hasLineOfSight))
            {
                var rescueState = plugin.CoppeliaTravelService.UpdateLineOfSightRescue(
                    selectedCharacter.GameObjectId,
                    selectedCharacter.Position);
                StatusText = rescueState;
                LastIssuedAction = "Movement";
                LastMatchedRule = $"{plugin.FormatDisplayName(candidate.Snapshot.Name)} - LOS blocked.";
                return HealbotDecisionOutcome.Blocked;
            }

            plugin.CoppeliaTravelService.ClearLineOfSightRescue();

            Plugin.TargetManager.Target = selectedCharacter;

            var evaluationOrder = candidate.Snapshot.IsDead
                ? HealbotActionCatalog.DeadTargetEvaluationOrder
                : HealbotActionCatalog.AliveEvaluationOrder;

            if (TryExecuteRule(profile, jobConfig, candidate.Snapshot, evaluationOrder, out var executedStatus, out var executedAction, out var matchedRule))
            {
                StatusText = $"Watching {activeTargetCount} targets. {executedStatus}";
                LastIssuedAction = executedAction;
                LastMatchedRule = $"{plugin.FormatDisplayName(candidate.Snapshot.Name)} - {matchedRule}";
                return HealbotDecisionOutcome.Queued;
            }

            if (executedAction == "Blocked" && blockedStatus == null)
            {
                blockedStatus = executedStatus;
                blockedRule = $"{plugin.FormatDisplayName(candidate.Snapshot.Name)} - {matchedRule}";
            }
        }

        if (string.IsNullOrWhiteSpace(CurrentWatchedTargetName))
            plugin.CoppeliaTravelService.ClearLineOfSightRescue();

        if (blockedStatus != null && blockedRule != null)
        {
            StatusText = $"Watching {activeTargetCount} targets. {blockedStatus}";
            LastIssuedAction = "Blocked";
            LastMatchedRule = blockedRule;
            return HealbotDecisionOutcome.Blocked;
        }

        var topCandidate = orderedCandidates[0];
        if (topCandidate.Snapshot.IsDead)
        {
            StatusText = $"Watching {activeTargetCount} targets. No enabled dead-target rule can act on {plugin.FormatDisplayName(topCandidate.Snapshot.Name)}.";
            LastIssuedAction = "Idle";
            LastMatchedRule = $"{plugin.FormatDisplayName(topCandidate.Snapshot.Name)} - No dead-target rule matched.";
            return HealbotDecisionOutcome.Idle;
        }

        StatusText = $"Watching {activeTargetCount} targets. Lowest-HP watched target is {plugin.FormatDisplayName(topCandidate.Snapshot.Name)} at {topCandidate.Snapshot.HpPercent}% HP.";
        LastIssuedAction = "Idle";
        LastMatchedRule = $"{plugin.FormatDisplayName(topCandidate.Snapshot.Name)} - No alive-target rule matched.";
        return HealbotDecisionOutcome.Idle;
    }

    private void ClearCurrentTargetContext()
    {
        CurrentWatchedTargetName = string.Empty;
        CurrentWatchedTargetHpText = "HP unavailable";
        CurrentTargetLineOfSightState = "Target not visible";
    }

    private bool TryExecuteRule(
        HealbotJobProfile profile,
        HealerJobConfig jobConfig,
        WatchTargetSnapshot selectedSnapshot,
        IReadOnlyList<HealbotActionGroup> groupOrder,
        out string statusText,
        out string executedAction,
        out string matchedRule)
    {
        statusText = string.Empty;
        executedAction = "Idle";
        matchedRule = "No rule matched.";

        var ruleLookup = jobConfig.ActionRules.ToDictionary(rule => rule.ActionName, StringComparer.OrdinalIgnoreCase);
        var matchingRuleFound = false;
        var lastFailure = string.Empty;

        foreach (var group in groupOrder)
        {
            var orderedRules = HealbotActionCatalog.GetDefinitions(profile.JobId)
                .Where(definition => definition.Group == group && ruleLookup.ContainsKey(definition.ActionName))
                .Select(definition => (definition, rule: ruleLookup[definition.ActionName]))
                .OrderBy(pair => pair.rule.Priority)
                .ThenBy(pair => pair.definition.ActionName, StringComparer.OrdinalIgnoreCase);

            foreach (var (definition, rule) in orderedRules)
            {
                if (!rule.Enabled)
                    continue;

                if (!RuleMatches(definition, rule, selectedSnapshot))
                    continue;

                matchingRuleFound = true;
                matchedRule = BuildRuleLabel(definition, rule);

                if (plugin.Configuration.BotMode == BotMode.Jot &&
                    rsrIsolationApplied &&
                    !rsrIpcService.TrySetMode(RsrIpcService.RsrStateCommandType.Off))
                {
                    rsrIsolationApplied = false;
                    appliedSignature = string.Empty;
                }

                if (actionExecutionService.TryExecute(definition, selectedSnapshot.GameObjectId, rule.MinimumMpPercent, out var failureReason))
                {
                    statusText = BuildSuccessMessage(definition, selectedSnapshot);
                    executedAction = definition.ActionName;
                    return true;
                }

                lastFailure = failureReason;
            }
        }

        if (matchingRuleFound && !string.IsNullOrWhiteSpace(lastFailure))
        {
            statusText = lastFailure;
            executedAction = "Blocked";
            return false;
        }

        return false;
    }

    private bool RuleMatches(
        HealbotActionDefinition definition,
        HealerActionRule rule,
        WatchTargetSnapshot selectedSnapshot)
    {
        if (!rule.AllowOutOfCombat && !Plugin.Condition[ConditionFlag.InCombat])
            return false;

        if (Plugin.ObjectTable.LocalPlayer is ICharacter localPlayer)
        {
            var requiredMp = (uint)(Math.Clamp(rule.MinimumMpPercent, 0, 100) * 100);
            if (localPlayer.CurrentMp < requiredMp)
                return false;
        }

        if (selectedSnapshot.IsDead)
        {
            if (!definition.AllowedWhenSelectedTargetDead)
                return false;

            return rule.TriggerKind is HealbotTriggerKind.DeadTarget or HealbotTriggerKind.Always;
        }

        if (definition.AllowedWhenSelectedTargetDead && rule.TriggerKind == HealbotTriggerKind.DeadTarget)
            return false;

        if (definition.TargetKind == HealbotTargetKind.SelectedTarget && !selectedSnapshot.IsTargetable)
            return false;

        var checksTrackedStatus =
            rule.RequireMissingTrackedStatus || rule.TriggerKind == HealbotTriggerKind.MissingBuff;
        var hasTrackedStatus = false;
        if (checksTrackedStatus &&
            !actionExecutionService.TryHasTrackedStatus(
                definition,
                selectedSnapshot.GameObjectId,
                out hasTrackedStatus))
        {
            return false;
        }

        if (rule.RequireMissingTrackedStatus && hasTrackedStatus)
            return false;

        return rule.TriggerKind switch
        {
            HealbotTriggerKind.HpBelow => selectedSnapshot.HpPercent <= rule.HpThresholdPercent,
            HealbotTriggerKind.DeadTarget => false,
            HealbotTriggerKind.MissingBuff => !hasTrackedStatus,
            HealbotTriggerKind.Always => true,
            _ => false,
        };
    }

    private static string BuildRuleLabel(HealbotActionDefinition definition, HealerActionRule rule)
        => $"{definition.Group.GetLabel()}: {definition.ActionName} [{rule.TriggerKind.GetLabel()}]";

    private string BuildSuccessMessage(HealbotActionDefinition definition, WatchTargetSnapshot selectedSnapshot)
    {
        var targetName = plugin.FormatDisplayName(selectedSnapshot.Name);
        if (selectedSnapshot.IsDead)
        {
            return definition.TargetKind == HealbotTargetKind.Self
                ? $"Queued {definition.ActionName} as dead-target prep for {targetName}."
                : $"Queued {definition.ActionName} for {targetName}.";
        }

        return definition.TargetKind == HealbotTargetKind.Self
            ? $"Queued {definition.ActionName} while watching {targetName} at {selectedSnapshot.HpPercent}% HP."
            : $"Queued {definition.ActionName} for {targetName} at {selectedSnapshot.HpPercent}% HP.";
    }

    private void CapturePreviousTarget()
    {
        var currentTarget = Plugin.TargetManager.Target;
        if (currentTarget == null)
        {
            previousTargetGameObjectId = 0;
            return;
        }

        previousTargetGameObjectId = currentTarget.GameObjectId;
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

    private string BuildSignature(HealbotJobProfile profile, HealerJobConfig jobConfig, bool rsrLoaded)
        => string.Join("|",
            profile.JobId,
            jobConfig.BuildSignature(),
            plugin.Configuration.WatchPartyNpcs,
            plugin.Configuration.WatchCompanionChocobos,
            plugin.Configuration.WatchFriendlyBattleNpcs,
            plugin.CoppeliaQstIpcService.EffectiveJoatFullRsrRotation,
            rsrLoaded);
}

internal enum HealbotDecisionOutcome
{
    None,
    Idle,
    Queued,
    Blocked,
    Unavailable,
}

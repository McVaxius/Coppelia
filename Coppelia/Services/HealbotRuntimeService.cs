using Coppelia.Models;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Types;

namespace Coppelia.Services;

internal sealed class HealbotRuntimeService : IDisposable
{
    private readonly Plugin plugin;
    private readonly DependencyService dependencyService;
    private readonly WatchTargetService watchTargetService;
    private readonly RsrIpcService rsrIpcService;
    private readonly ActionExecutionService actionExecutionService;

    private DateTimeOffset nextDecisionUtc = DateTimeOffset.MinValue;
    private bool profileApplied;
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
        reason = "Coppelia only supports WHM, SCH, AST, and SGE.";
        return false;
    }

    public void Activate()
    {
        nextDecisionUtc = DateTimeOffset.MinValue;
        profileApplied = false;
        appliedSignature = string.Empty;
        LastIssuedAction = "Idle";
        LastMatchedRule = "No rule matched.";
    }

    public void Deactivate(string reason)
    {
        if (profileApplied)
        {
            rsrIpcService.RestoreSessionSnapshot(keepRaiseOutsideDutyEnabled: true);
            RestorePreviousTarget();
        }

        profileApplied = false;
        appliedSignature = string.Empty;
        LastIssuedAction = "Idle";
        LastMatchedRule = "No rule matched.";
        StatusText = reason;
        previousTargetGameObjectId = 0;
    }

    public void Update()
    {
        if (!plugin.Configuration.PluginEnabled)
        {
            Deactivate("Plugin disabled.");
            return;
        }

        if (!plugin.Configuration.HealbotEnabled)
        {
            if (profileApplied)
                Deactivate("Healbot mode is off.");
            else
                StatusText = "Healbot mode is off.";
            return;
        }

        dependencyService.Refresh();
        if (!dependencyService.Current.IsHealbotReady)
        {
            StatusText = $"Blocked: {dependencyService.BuildMissingDependencyMessage()}";
            LastIssuedAction = "Blocked";
            LastMatchedRule = "Dependencies missing.";
            if (plugin.Configuration.ShowDependencyToasts)
                plugin.ShowDependencyToast(dependencyService.BuildMissingDependencyMessage());
            return;
        }

        if (!IsSupportedLocalJob(out var profile, out var reason))
        {
            StatusText = $"Blocked: {reason}";
            LastIssuedAction = "Blocked";
            LastMatchedRule = "Unsupported local job.";
            return;
        }

        var jobConfig = plugin.Configuration.GetJobConfigForJob(profile!.JobId);
        var signature = BuildSignature(profile, jobConfig);
        if (!profileApplied || !string.Equals(signature, appliedSignature, StringComparison.Ordinal))
            ApplyProfile(profile, signature);

        if (Plugin.ObjectTable.LocalPlayer is IBattleChara localPlayer && localPlayer.IsCasting)
        {
            StatusText = $"Holding while casting action {localPlayer.CastActionId}.";
            LastIssuedAction = "Casting";
            LastMatchedRule = "Waiting for the current cast to finish.";
            return;
        }

        if (DateTimeOffset.UtcNow < nextDecisionUtc)
            return;

        nextDecisionUtc = DateTimeOffset.UtcNow.AddMilliseconds(900);
        EvaluateSelectedTarget(profile, jobConfig);
    }

    private void ApplyProfile(HealbotJobProfile profile, string signature)
    {
        if (!profileApplied)
            CapturePreviousTarget();

        if (rsrIpcService.ApplyHealbotProfile(profile, plugin.Configuration))
        {
            profileApplied = true;
            appliedSignature = signature;
            StatusText = $"Healbot action matrix armed for {profile.JobDisplayName}.";
            return;
        }

        StatusText = "Failed to apply the RSR isolation profile.";
    }

    private void EvaluateSelectedTarget(HealbotJobProfile profile, HealerJobConfig jobConfig)
    {
        var activeTargetCount = watchTargetService.ActiveTargets.Count;
        if (activeTargetCount == 0)
        {
            StatusText = "No watched targets are active.";
            LastIssuedAction = "Idle";
            LastMatchedRule = "No watched targets.";
            return;
        }

        if (!jobConfig.Enabled)
        {
            StatusText = $"{profile.JobAbbreviation} automation is disabled in Coppelia settings.";
            LastIssuedAction = "Idle";
            LastMatchedRule = $"{profile.JobAbbreviation} tab disabled.";
            return;
        }

        var orderedCandidates = watchTargetService.RuntimeCandidates
            .OrderBy(candidate => candidate.Snapshot.IsDead ? 0 : 1)
            .ThenBy(candidate => candidate.Snapshot.IsDead ? 0 : candidate.Snapshot.HpPercent)
            .ThenBy(candidate => candidate.Snapshot.Distance)
            .ThenBy(candidate => candidate.Snapshot.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (orderedCandidates.Length == 0)
        {
            StatusText = $"Watching {activeTargetCount} targets. No live watched target is currently available.";
            LastIssuedAction = "Idle";
            LastMatchedRule = "No live watched targets.";
            return;
        }

        string? blockedStatus = null;
        string? blockedRule = null;

        foreach (var candidate in orderedCandidates)
        {
            Plugin.TargetManager.Target = candidate.Character;

            var evaluationOrder = candidate.Snapshot.IsDead
                ? HealbotActionCatalog.DeadTargetEvaluationOrder
                : HealbotActionCatalog.AliveEvaluationOrder;

            if (TryExecuteRule(profile, jobConfig, candidate.Character, candidate.Snapshot, evaluationOrder, out var executedStatus, out var executedAction, out var matchedRule))
            {
                StatusText = $"Watching {activeTargetCount} targets. {executedStatus}";
                LastIssuedAction = executedAction;
                LastMatchedRule = $"{plugin.FormatDisplayName(candidate.Snapshot.Name)} - {matchedRule}";
                return;
            }

            if (executedAction == "Blocked" && blockedStatus == null)
            {
                blockedStatus = executedStatus;
                blockedRule = $"{plugin.FormatDisplayName(candidate.Snapshot.Name)} - {matchedRule}";
            }
        }

        if (blockedStatus != null && blockedRule != null)
        {
            StatusText = $"Watching {activeTargetCount} targets. {blockedStatus}";
            LastIssuedAction = "Blocked";
            LastMatchedRule = blockedRule;
            return;
        }

        var topCandidate = orderedCandidates[0];
        if (topCandidate.Snapshot.IsDead)
        {
            StatusText = $"Watching {activeTargetCount} targets. No enabled dead-target rule can act on {plugin.FormatDisplayName(topCandidate.Snapshot.Name)}.";
            LastIssuedAction = "Idle";
            LastMatchedRule = $"{plugin.FormatDisplayName(topCandidate.Snapshot.Name)} - No dead-target rule matched.";
            return;
        }

        StatusText = $"Watching {activeTargetCount} targets. Lowest-HP watched target is {plugin.FormatDisplayName(topCandidate.Snapshot.Name)} at {topCandidate.Snapshot.HpPercent}% HP.";
        LastIssuedAction = "Idle";
        LastMatchedRule = $"{plugin.FormatDisplayName(topCandidate.Snapshot.Name)} - No alive-target rule matched.";
    }

    private bool TryExecuteRule(
        HealbotJobProfile profile,
        HealerJobConfig jobConfig,
        ICharacter selectedCharacter,
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

                if (!RuleMatches(definition, rule, selectedCharacter, selectedSnapshot))
                    continue;

                matchingRuleFound = true;
                matchedRule = BuildRuleLabel(definition, rule);

                if (actionExecutionService.TryExecute(definition, selectedCharacter, rule.MinimumMpPercent, out var failureReason))
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
        ICharacter selectedCharacter,
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

        if (rule.RequireMissingTrackedStatus && actionExecutionService.HasTrackedStatus(definition, selectedCharacter))
            return false;

        return rule.TriggerKind switch
        {
            HealbotTriggerKind.HpBelow => selectedSnapshot.HpPercent <= rule.HpThresholdPercent,
            HealbotTriggerKind.DeadTarget => false,
            HealbotTriggerKind.MissingBuff => !actionExecutionService.HasTrackedStatus(definition, selectedCharacter),
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

    private string BuildSignature(HealbotJobProfile profile, HealerJobConfig jobConfig)
        => string.Join("|",
            profile.JobId,
            jobConfig.BuildSignature(),
            plugin.Configuration.WatchPartyNpcs,
            plugin.Configuration.WatchCompanionChocobos,
            plugin.Configuration.WatchFriendlyBattleNpcs);
}

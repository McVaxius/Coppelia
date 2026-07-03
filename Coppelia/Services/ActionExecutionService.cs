using Coppelia.Models;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Types;
using FFXIVClientStructs.FFXIV.Client.Game;
using ActionSheet = Lumina.Excel.Sheets.Action;
using StatusSheet = Lumina.Excel.Sheets.Status;

namespace Coppelia.Services;

internal unsafe sealed class ActionExecutionService
{
    private readonly Dictionary<string, uint> actionIdByName = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, uint> statusIdByName = new(StringComparer.OrdinalIgnoreCase);

    public bool TryExecute(HealbotActionDefinition definition, ICharacter selectedTarget, int minimumMpPercent, out string failureReason)
    {
        failureReason = string.Empty;

        if (Plugin.ObjectTable.LocalPlayer is not IBattleChara localPlayer)
        {
            failureReason = "Local player is unavailable.";
            return false;
        }

        if (localPlayer.IsCasting)
        {
            failureReason = "Already casting.";
            return false;
        }

        if (!TryResolveActionId(definition.ActionName, out var actionId))
        {
            failureReason = $"Action {definition.ActionName} was not found in the action sheet.";
            return false;
        }

        if (localPlayer.CurrentMp < (uint)(Math.Clamp(minimumMpPercent, 0, 100) * 100))
        {
            failureReason = $"Insufficient MP for {definition.ActionName}.";
            return false;
        }

        var targetObject = definition.TargetKind == HealbotTargetKind.Self
            ? localPlayer
            : selectedTarget;

        if (definition.TargetKind == HealbotTargetKind.SelectedTarget && !selectedTarget.IsTargetable && !selectedTarget.IsDead)
        {
            failureReason = $"{selectedTarget.Name.TextValue} is not targetable.";
            return false;
        }

        if (ActionManager.Instance() == null)
        {
            failureReason = "ActionManager is unavailable.";
            return false;
        }

        var actionManager = ActionManager.Instance();
        if (!actionManager->IsActionOffCooldown(ActionType.Action, actionId))
        {
            failureReason = $"{definition.ActionName} is on cooldown.";
            return false;
        }

        if (definition.TargetKind == HealbotTargetKind.SelectedTarget)
        {
            Plugin.TargetManager.Target = selectedTarget;
            if (!actionManager->IsActionTargetInRange(ActionType.Action, actionId))
            {
                failureReason = $"{definition.ActionName} is out of range.";
                return false;
            }
        }
        else
        {
            Plugin.TargetManager.Target = localPlayer;
        }

        var queued = false;
        var executed = actionManager->UseAction(
            ActionType.Action,
            actionId,
            targetObject.GameObjectId,
            0xFFFF,
            (ActionManager.UseActionMode)0,
            0,
            &queued);

        if (!executed)
        {
            failureReason = $"UseAction rejected {definition.ActionName}.";
            return false;
        }

        return true;
    }

    public bool CanUseAnyPowerlevelAction(PowerlevelJob job, ICharacter selectedTarget, out string failureReason)
    {
        failureReason = string.Empty;

        if (Plugin.ObjectTable.LocalPlayer is not IBattleChara localPlayer)
        {
            failureReason = "Local player is unavailable.";
            return false;
        }

        if (localPlayer.IsCasting)
        {
            failureReason = "Already casting.";
            return false;
        }

        return TryFindPowerlevelAction(job, selectedTarget, execute: false, out _, out failureReason);
    }

    public bool TryExecutePowerlevel(
        PowerlevelJob job,
        ICharacter selectedTarget,
        out string executedAction,
        out string failureReason)
    {
        executedAction = "Idle";
        failureReason = string.Empty;

        if (Plugin.ObjectTable.LocalPlayer is not IBattleChara localPlayer)
        {
            failureReason = "Local player is unavailable.";
            return false;
        }

        if (localPlayer.IsCasting)
        {
            failureReason = "Already casting.";
            return false;
        }

        return TryFindPowerlevelAction(job, selectedTarget, execute: true, out executedAction, out failureReason);
    }

    private bool TryFindPowerlevelAction(
        PowerlevelJob job,
        ICharacter selectedTarget,
        bool execute,
        out string actionName,
        out string failureReason)
    {
        actionName = "Idle";
        failureReason = "No PowerlevelBot action is currently usable.";

        if (ActionManager.Instance() == null)
        {
            failureReason = "ActionManager is unavailable.";
            return false;
        }

        var actionManager = ActionManager.Instance();
        var lastFailure = failureReason;

        foreach (var definition in PowerlevelActionCatalog.GetDefinitions(job).OrderBy(definition => definition.Priority))
        {
            if (!TryResolveActionId(definition.ActionName, out var actionId))
                continue;

            var adjustedActionId = ResolveAdjustedPowerlevelActionId(actionId);
            if (!TryGetActionRow(adjustedActionId, out var actionRow))
                continue;

            Plugin.TargetManager.Target = selectedTarget;

            var castTimeMs = ActionManager.GetAdjustedCastTime(ActionType.Action, adjustedActionId, true, null);
            var metadata = new PowerlevelActionMetadata(
                actionRow.Name.ToString(),
                Plugin.UnlockState.IsActionUnlocked(actionRow),
                actionRow.CanTargetHostile,
                actionRow.CanTargetSelf || actionRow.CanTargetParty,
                actionRow.TargetArea,
                actionRow.EffectRange,
                castTimeMs,
                actionManager->GetActionStatus(ActionType.Action, adjustedActionId) == 0,
                actionManager->IsActionOffCooldown(ActionType.Action, adjustedActionId),
                actionManager->IsActionTargetInRange(ActionType.Action, adjustedActionId));

            if (!PowerlevelActionPolicy.IsAllowed(metadata, out lastFailure))
                continue;

            if (!execute)
            {
                actionName = metadata.Name;
                failureReason = string.Empty;
                return true;
            }

            var queued = false;
            var executed = actionManager->UseAction(
                ActionType.Action,
                adjustedActionId,
                selectedTarget.GameObjectId,
                0xFFFF,
                (ActionManager.UseActionMode)0,
                0,
                &queued);

            if (!executed)
            {
                lastFailure = $"UseAction rejected {metadata.Name}.";
                continue;
            }

            actionName = metadata.Name;
            failureReason = string.Empty;
            return true;
        }

        failureReason = lastFailure;
        return false;
    }

    public bool HasTrackedStatus(HealbotActionDefinition definition, ICharacter selectedTarget)
    {
        if (string.IsNullOrWhiteSpace(definition.TrackedStatusName))
            return false;

        if (!TryResolveStatusId(definition.TrackedStatusName, out var statusId))
            return false;

        var target = definition.TargetKind == HealbotTargetKind.Self
            ? Plugin.ObjectTable.LocalPlayer as IBattleChara
            : selectedTarget as IBattleChara;

        if (target == null)
            return false;

        foreach (var status in target.StatusList)
        {
            if (status.StatusId == statusId)
                return true;
        }

        return false;
    }

    private bool TryResolveActionId(string actionName, out uint actionId)
    {
        if (actionIdByName.TryGetValue(actionName, out actionId))
            return true;

        var actionSheet = Plugin.DataManager.GetExcelSheet<ActionSheet>();
        if (actionSheet == null)
        {
            actionId = 0;
            return false;
        }

        foreach (var row in actionSheet)
        {
            if (row.RowId == 0)
                continue;

            if (!string.Equals(row.Name.ToString(), actionName, StringComparison.OrdinalIgnoreCase))
                continue;

            actionId = row.RowId;
            actionIdByName[actionName] = actionId;
            return true;
        }

        actionId = 0;
        return false;
    }

    private static uint ResolveAdjustedPowerlevelActionId(uint actionId)
    {
        // Dalamud 15's exposed ActionManager surface in this workspace does not expose a
        // direct adjusted-action-id helper.  The curated catalog includes upgraded action
        // rows next to their base rows, and UseAction itself accepts the normal action row.
        return actionId;
    }

    private static bool TryGetActionRow(uint actionId, out ActionSheet actionRow)
    {
        var actionSheet = Plugin.DataManager.GetExcelSheet<ActionSheet>();
        if (actionSheet != null && actionSheet.TryGetRow(actionId, out actionRow))
            return true;

        actionRow = default;
        return false;
    }

    private bool TryResolveStatusId(string statusName, out uint statusId)
    {
        if (statusIdByName.TryGetValue(statusName, out statusId))
            return true;

        var statusSheet = Plugin.DataManager.GetExcelSheet<StatusSheet>();
        if (statusSheet == null)
        {
            statusId = 0;
            return false;
        }

        foreach (var row in statusSheet)
        {
            if (row.RowId == 0)
                continue;

            if (!string.Equals(row.Name.ToString(), statusName, StringComparison.OrdinalIgnoreCase))
                continue;

            statusId = row.RowId;
            statusIdByName[statusName] = statusId;
            return true;
        }

        statusId = 0;
        return false;
    }
}

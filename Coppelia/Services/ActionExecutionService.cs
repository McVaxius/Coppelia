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

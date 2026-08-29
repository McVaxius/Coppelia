using System.Numerics;
using Coppelia.Models;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Types;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;

namespace Coppelia.Services;

internal unsafe sealed class CoppeliaCompanionService
{
    private const uint GysahlGreensItemId = 4868;
    private const uint MountRouletteGeneralActionId = 9;
    private const float AetheryteSanctuaryDistanceSquared = 50f * 50f;

    private readonly CoppeliaTravelService travelService;
    private readonly CoppeliaCompanionPolicy policy = new();

    public CoppeliaCompanionService(CoppeliaTravelService travelService)
    {
        this.travelService = travelService;
    }

    public bool SetQstOwnership(bool summonEnabled)
    {
        policy.SetQstOwnership(summonEnabled);
        return true;
    }

    public void ClearQstOwnership() => policy.ClearQstOwnership();

    public void Update()
    {
        if (!policy.IsQstOwned)
            return;

        var localPlayer = Plugin.ObjectTable.LocalPlayer;
        var conditions = new CoppeliaCompanionConditions(
            Plugin.ClientState.IsLoggedIn && localPlayer != null,
            !Plugin.Condition[ConditionFlag.Mounted] &&
            !Plugin.Condition[ConditionFlag.Mounting71] &&
            !Plugin.Condition[ConditionFlag.InFlight],
            Plugin.Condition[ConditionFlag.InCombat],
            Plugin.Condition[ConditionFlag.BoundByDuty] || Plugin.Condition[ConditionFlag.BoundByDuty56],
            IsInSanctuary(),
            localPlayer is IBattleChara battleChara && battleChara.IsCasting,
            IsOccupied(),
            GetInventoryItemCount(GysahlGreensItemId) > 0,
            GetBuddyTimeRemaining());
        if (policy.Evaluate(conditions, Environment.TickCount64) != CoppeliaCompanionDecision.Summon)
            return;

        var actionManager = ActionManager.Instance();
        if (actionManager == null ||
            actionManager->GetActionStatus(ActionType.Item, GysahlGreensItemId) != 0)
        {
            return;
        }

        if (actionManager->UseAction(ActionType.Item, GysahlGreensItemId, extraParam: 65535))
        {
            travelService.PauseForAction();
            Plugin.Log.Information("[Coppelia][QST] Summoning the companion chocobo with Gysahl Greens.");
        }
    }

    private static bool IsOccupied() =>
        Plugin.Condition[ConditionFlag.BetweenAreas] ||
        Plugin.Condition[ConditionFlag.BetweenAreas51] ||
        Plugin.Condition[ConditionFlag.OccupiedInQuestEvent] ||
        Plugin.Condition[ConditionFlag.OccupiedInCutSceneEvent] ||
        Plugin.Condition[ConditionFlag.Occupied33] ||
        Plugin.Condition[ConditionFlag.Occupied39] ||
        Plugin.Condition[ConditionFlag.WatchingCutscene];

    private static unsafe bool IsInSanctuary()
    {
        try
        {
            var actionManager = ActionManager.Instance();
            return actionManager == null ||
                   actionManager->GetActionStatus(ActionType.GeneralAction, MountRouletteGeneralActionId) != 0 ||
                   IsNearAetheryteOrAethernet();
        }
        catch (Exception)
        {
            return true;
        }
    }

    private static bool IsNearAetheryteOrAethernet()
    {
        try
        {
            var localPlayer = Plugin.ObjectTable.LocalPlayer;
            if (localPlayer == null)
                return false;

            foreach (var gameObject in Plugin.ObjectTable)
            {
                if (gameObject == null ||
                    !IsAetheryteOrAethernet(gameObject) ||
                    Vector3.DistanceSquared(localPlayer.Position, gameObject.Position) > AetheryteSanctuaryDistanceSquared)
                {
                    continue;
                }

                return true;
            }
        }
        catch (Exception)
        {
        }

        return false;
    }

    private static bool IsAetheryteOrAethernet(IGameObject gameObject)
    {
        if (gameObject.ObjectKind == Dalamud.Game.ClientState.Objects.Enums.ObjectKind.Aetheryte)
            return true;

        var name = gameObject.Name.TextValue;
        return !string.IsNullOrEmpty(name) &&
               (name.Contains("Aetheryte", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("Aethernet", StringComparison.OrdinalIgnoreCase));
    }

    private static unsafe int GetInventoryItemCount(uint itemId)
    {
        try
        {
            var inventory = InventoryManager.Instance();
            return inventory == null
                ? 0
                : inventory->GetInventoryItemCount(itemId, false) +
                  inventory->GetInventoryItemCount(itemId, true);
        }
        catch (Exception)
        {
            return 0;
        }
    }

    private static unsafe float GetBuddyTimeRemaining()
    {
        try
        {
            var uiState = UIState.Instance();
            return uiState == null ? 0f : uiState->Buddy.CompanionInfo.TimeLeft;
        }
        catch (Exception)
        {
            return 0f;
        }
    }
}

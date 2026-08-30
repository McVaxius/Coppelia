using System.Numerics;
using Coppelia.Models;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Types;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.System.String;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Shell;

namespace Coppelia.Services;

internal unsafe sealed class CoppeliaCompanionService
{
    private const uint GysahlGreensItemId = 4868;
    private const uint MountRouletteGeneralActionId = 9;
    private const float AetheryteSanctuaryDistanceSquared = 50f * 50f;

    private readonly Configuration configuration;
    private readonly CoppeliaTravelService travelService;
    private readonly CoppeliaCompanionPolicy policy = new();

    public CoppeliaCompanionService(Configuration configuration, CoppeliaTravelService travelService)
    {
        this.configuration = configuration;
        this.travelService = travelService;
    }

    public bool SetQstOwnership(bool summonEnabled)
    {
        policy.SetQstOwnership(summonEnabled);
        return true;
    }

    public void ClearQstOwnership() => policy.ClearQstOwnership();

    public bool IsQstOwned => policy.IsQstOwned;
    public bool QstSummoningEnabled => policy.QstEnabled;

    public int? GetGysahlGreensCount()
        => Plugin.ClientState.IsLoggedIn ? GetInventoryItemCount(GysahlGreensItemId) : null;

    public void ApplySelectedStanceImmediately()
    {
        var command = policy.SelectStance(
            configuration.CompanionStance,
            Plugin.ClientState.IsLoggedIn && GetBuddyTimeRemaining() > 0f);
        if (command != null)
            ExecuteStanceCommand(command);
    }

    public void Update()
    {
        policy.SetLocalState(
            configuration.SummonCompanionChocobo,
            configuration.OperatingRole);
        if (!policy.Enabled)
            return;

        var nowMilliseconds = Environment.TickCount64;
        var localPlayer = Plugin.ObjectTable.LocalPlayer;
        var loggedIn = Plugin.ClientState.IsLoggedIn && localPlayer != null;
        var pendingStance = policy.TakeDueStance(nowMilliseconds, loggedIn);
        if (pendingStance != null)
            ExecuteStanceCommand(pendingStance);

        var conditions = new CoppeliaCompanionConditions(
            loggedIn,
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
        if (policy.Evaluate(conditions, nowMilliseconds) != CoppeliaCompanionDecision.Summon)
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
            policy.ScheduleStance(configuration.CompanionStance, nowMilliseconds);
            Plugin.Log.Information("[Coppelia][Companion] Summoning the companion chocobo with Gysahl Greens.");
        }
    }

    private static void ExecuteStanceCommand(string command)
    {
        try
        {
            var shellModule = RaptureShellModule.Instance();
            if (shellModule == null || shellModule->UIModule == null)
                return;

            var textCommand = new Utf8String(command);
            try
            {
                shellModule->ExecuteCommandInner(&textCommand, shellModule->UIModule);
                Plugin.Log.Information($"[Coppelia][Companion] Applied stance with {command}.");
            }
            finally
            {
                textCommand.Dtor();
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, $"[Coppelia][Companion] Failed to apply stance with {command}.");
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

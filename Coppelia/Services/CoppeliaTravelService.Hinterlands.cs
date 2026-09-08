using Coppelia.Models;
using Dalamud.Game.ClientState.Conditions;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using Lumina.Excel.Sheets;

namespace Coppelia.Services;

internal sealed partial class CoppeliaTravelService
{
    private HinterlandsRoute? hinterlandsRoute;

    private static unsafe bool CanFlyInTerritory(uint territoryId)
    {
        var player = PlayerState.Instance();
        if (player == null || !Plugin.DataManager.GetExcelSheet<TerritoryType>().TryGetRow(territoryId, out var territory) ||
            !territory.Mount)
            return false;
        if (territoryId == Plugin.ClientState.TerritoryType)
            return player->CanFly;
        return territory.AetherCurrentCompFlgSet.RowId != 0 &&
               player->IsAetherCurrentZoneComplete(territory.AetherCurrentCompFlgSet.RowId);
    }

    private bool UpdateHinterlandsRoute(CoppeliaQstCommand travel)
    {
        if (hinterlandsRoute is not { } route)
            return false;
        if (TryGetLifestreamBusy(out var observedBusy) && observedBusy && route.OwnsLifestream)
            route.ObservedBusy = true;
        var player = Plugin.ObjectTable.LocalPlayer;
        if (travel.TerritoryId != 399 || Plugin.Condition[ConditionFlag.BoundByDuty] ||
            DateTime.UtcNow >= route.ExpiresUtc)
        {
            ClearHinterlandsRoute();
            BlockTravel(travel.TravelSequence, "Epilogue Gate route cancelled or timed out");
            return true;
        }
        if (player == null || IsBetweenAreas() || !Plugin.ClientState.IsLoggedIn)
            return true;
        if (Plugin.ClientState.TerritoryType == 399 && player.CurrentWorld.RowId == travel.QuesterCurrentWorldId)
        {
            ClearHinterlandsRoute();
            RearmLatestTravelRoute();
            return false;
        }
        State = "Travelling through Idyllshire's Epilogue Gate";
        if (Plugin.Condition[ConditionFlag.InCombat] || Plugin.Condition[ConditionFlag.Casting] ||
            Plugin.Condition[ConditionFlag.Occupied] || Plugin.Condition[ConditionFlag.OccupiedInEvent] ||
            Plugin.Condition[ConditionFlag.OccupiedInQuestEvent] || Plugin.Condition[ConditionFlag.OccupiedInCutSceneEvent] ||
            Plugin.Condition[ConditionFlag.WatchingCutscene] || Plugin.Condition[ConditionFlag.WatchingCutscene78] ||
            Plugin.Condition[ConditionFlag.LoggingOut] || !TryGetLifestreamBusy(out var busy) || busy)
            return true;
        // A completed owned request no longer authorizes aborting another Lifestream owner.
        if (route.ObservedBusy)
            route.OwnsLifestream = false;
        if (player.CurrentWorld.RowId != travel.QuesterCurrentWorldId)
        {
            if (DateTime.UtcNow < route.NextWorldAttemptUtc)
                return true;
            ReleaseOwnedRoute();
            try { route.OwnsLifestream = changeWorld.InvokeFunc(travel.QuesterCurrentWorldId); }
            catch (Exception) { route.OwnsLifestream = false; }
            route.ObservedBusy = false;
            route.NextWorldAttemptUtc = DateTime.UtcNow.AddSeconds(15);
            return true;
        }
        if (!route.GateIssued)
        {
            var execute = Plugin.PluginInterface.GetIpcSubscriber<string, object>("Lifestream.ExecuteCommand");
            if (!execute.HasAction)
            {
                ClearHinterlandsRoute();
                BlockTravel(travel.TravelSequence, "Lifestream Epilogue Gate command is unavailable");
                return true;
            }
            ReleaseOwnedRoute();
            ClearLineOfSightRescue();
            try
            {
                execute.InvokeAction("Epilogue Gate");
                route.GateIssued = true;
                route.OwnsLifestream = true;
                route.ObservedBusy = false;
            }
            catch (Exception)
            {
                ClearHinterlandsRoute();
                BlockTravel(travel.TravelSequence, "Lifestream rejected the Epilogue Gate route");
            }
        }
        return true;
    }

    private void ClearHinterlandsRoute()
    {
        var route = hinterlandsRoute;
        hinterlandsRoute = null;
        if (route?.OwnsLifestream != true)
            return;
        try { Plugin.PluginInterface.GetIpcSubscriber<object>("Lifestream.Abort").InvokeAction(); }
        catch (Exception ex) { Plugin.Log.Warning(ex, "[HealBot] Could not abort owned Epilogue Gate travel."); }
    }

    private sealed class HinterlandsRoute(DateTime expiresUtc)
    {
        public DateTime ExpiresUtc { get; } = expiresUtc;
        public DateTime NextWorldAttemptUtc { get; set; }
        public bool GateIssued { get; set; }
        public bool OwnsLifestream { get; set; }
        public bool ObservedBusy { get; set; }
    }
}

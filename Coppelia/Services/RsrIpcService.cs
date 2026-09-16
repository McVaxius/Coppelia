using System.Globalization;
using System.Text.Json.Nodes;
using Coppelia.Models;
using Lumina.Excel.Sheets;
using GameActionSheet = Lumina.Excel.Sheets.Action;

namespace Coppelia.Services;

internal sealed class RsrIpcService
{
    private const string IpcPrefix = "RotationSolverReborn";
    private readonly string? rsrConfigPath = ResolveRsrConfigPath();

    private RsrSessionSnapshot? sessionSnapshot;
    private RsrStateCommandType? lastRequestedMode;

    public bool HasSessionSnapshot => sessionSnapshot != null;

    public bool ApplyHealbotProfile(
        HealbotJobProfile profile,
        Configuration configuration,
        bool joatMode,
        bool fullRsrRotation)
    {
        if (sessionSnapshot == null)
        {
            sessionSnapshot = CaptureSessionSnapshot(profile);
            if (sessionSnapshot == null)
                return false;
        }

        var ok = true;
        if (joatMode)
            ok &= TrySetMode(RsrStateCommandType.Off);
        ok &= TrySetSetting("AutoHeal", "false");
        ok &= TrySetSetting("UseGroundBeneficialAbility", "false");
        ok &= TrySetSetting("UseAoeDefense", "false");
        ok &= TrySetSetting("HealWhenNothingTodo", "false");
        ok &= TrySetSetting("FriendlyPartyNpcHealRaise3", ToBoolString(configuration.WatchPartyNpcs));
        ok &= TrySetSetting("FriendlyBattleNpcHeal", ToBoolString(configuration.WatchFriendlyBattleNpcs));
        ok &= TrySetSetting("ChocoboPartyMember", ToBoolString(configuration.WatchCompanionChocobos));
        ok &= TrySetSetting("RaiseType", RsrRaiseType.AllOutOfDuty.ToString());
        ok &= TrySetSetting("HealthAreaAbilityHot", "0");
        ok &= TrySetSetting("HealthAreaSpellHot", "0");
        ok &= TrySetSetting("HealthAreaAbility", "0");
        ok &= TrySetSetting("HealthAreaSpell", "0");
        ok &= TrySetSetting("HealthSingleAbilityHot", "0");
        ok &= TrySetSetting("HealthSingleSpellHot", "0");
        ok &= TrySetSetting("HealthSingleAbility", "0");
        ok &= TrySetSetting("HealthSingleSpell", "0");

        if (joatMode && fullRsrRotation)
        {
            ok &= TrySetSetting("AoEType", sessionSnapshot.AoEType.ToString());
            ok &= TrySetSetting("HostileType", RsrTargetHostileType.TargetsHaveTarget.ToString());
            foreach (var pair in sessionSnapshot.ActionEnabledByName)
                ok &= TryToggleAction(pair.Key, pair.Value);
        }
        else
        {
            ok &= TrySetSetting("AoEType", RsrAoEType.Off.ToString());
            ok &= TrySetSetting("HostileType", RsrTargetHostileType.TargetsHaveTarget.ToString());
            var dotActions = new HashSet<string>(profile.DotActionNames, StringComparer.OrdinalIgnoreCase);
            foreach (var actionName in profile.OffensiveActionNames.Distinct(StringComparer.OrdinalIgnoreCase))
                ok &= TryToggleAction(actionName, joatMode && dotActions.Contains(actionName));
        }

        if (!joatMode)
            ok &= TrySetMode(RsrStateCommandType.Henched);
        return ok;
    }

    public void RestoreSessionSnapshot()
    {
        if (sessionSnapshot == null)
            return;

        TrySetSetting("AutoHeal", ToBoolString(sessionSnapshot.AutoHeal));
        TrySetSetting("UseGroundBeneficialAbility", ToBoolString(sessionSnapshot.UseGroundBeneficialAbility));
        TrySetSetting("UseAoeDefense", ToBoolString(sessionSnapshot.UseAoeDefense));
        TrySetSetting("HealWhenNothingTodo", ToBoolString(sessionSnapshot.HealWhenNothingTodo));
        TrySetSetting("FriendlyPartyNpcHealRaise3", ToBoolString(sessionSnapshot.FriendlyPartyNpcHealRaise3));
        TrySetSetting("FriendlyBattleNpcHeal", ToBoolString(sessionSnapshot.FriendlyBattleNpcHeal));
        TrySetSetting("ChocoboPartyMember", ToBoolString(sessionSnapshot.ChocoboPartyMember));
        TrySetSetting("AoEType", sessionSnapshot.AoEType.ToString());
        TrySetSetting("HostileType", sessionSnapshot.HostileType.ToString());
        TrySetSetting("RaiseType", sessionSnapshot.RaiseType.ToString());
        TrySetSetting("HealthAreaAbilityHot", sessionSnapshot.HealthAreaAbilityHot.ToString("0.##", CultureInfo.InvariantCulture));
        TrySetSetting("HealthAreaSpellHot", sessionSnapshot.HealthAreaSpellHot.ToString("0.##", CultureInfo.InvariantCulture));
        TrySetSetting("HealthAreaAbility", sessionSnapshot.HealthAreaAbility.ToString("0.##", CultureInfo.InvariantCulture));
        TrySetSetting("HealthAreaSpell", sessionSnapshot.HealthAreaSpell.ToString("0.##", CultureInfo.InvariantCulture));
        TrySetSetting("HealthSingleAbilityHot", sessionSnapshot.HealthSingleAbilityHot.ToString("0.##", CultureInfo.InvariantCulture));
        TrySetSetting("HealthSingleSpellHot", sessionSnapshot.HealthSingleSpellHot.ToString("0.##", CultureInfo.InvariantCulture));
        TrySetSetting("HealthSingleAbility", sessionSnapshot.HealthSingleAbility.ToString("0.##", CultureInfo.InvariantCulture));
        TrySetSetting("HealthSingleSpell", sessionSnapshot.HealthSingleSpell.ToString("0.##", CultureInfo.InvariantCulture));

        foreach (var pair in sessionSnapshot.ActionEnabledByName)
            TryToggleAction(pair.Key, pair.Value);

        TrySetMode(RsrStateCommandType.Off);
        sessionSnapshot = null;
        lastRequestedMode = null;
    }

    internal void RestoreHealingAfterOff()
    {
        // Stop again even when Off was cached before another owner changed RSR.
        lastRequestedMode = null;
        TrySetMode(RsrStateCommandType.Off);
        TrySetSetting("AutoHeal", "true");
        TrySetSetting("UseGroundBeneficialAbility", "true");
        TrySetSetting("HealWhenNothingTodo", "true");
        TrySetSetting("HealthAreaAbilityHot", "0.70");
        TrySetSetting("HealthAreaSpellHot", "0.70");
        TrySetSetting("HealthAreaAbility", "0.90");
        TrySetSetting("HealthAreaSpell", "0.80");
        TrySetSetting("HealthSingleAbilityHot", "0.80");
        TrySetSetting("HealthSingleSpellHot", "0.70");
        TrySetSetting("HealthSingleAbility", "0.85");
        TrySetSetting("HealthSingleSpell", "0.80");
    }

    public bool TryTriggerSingleTargetHeal()
        => TryTriggerSpecial(RsrSpecialCommandType.HealSingle);

    public bool TryTriggerRaise()
        => TryTriggerSpecial(RsrSpecialCommandType.RaiseShirk);

    public bool TrySetMode(RsrStateCommandType mode)
    {
        if (lastRequestedMode == mode)
            return true;

        try
        {
            Plugin.PluginInterface
                .GetIpcSubscriber<RsrStateCommandType, object>($"{IpcPrefix}.ChangeOperatingMode")
                .InvokeAction(mode);
            lastRequestedMode = mode;
            return true;
        }
        catch (Exception ex)
        {
            Plugin.Log.Debug(ex, $"[Coppelia] Failed to set RSR mode to {mode}.");
            return false;
        }
    }

    private bool TryTriggerSpecial(RsrSpecialCommandType special)
    {
        try
        {
            Plugin.PluginInterface
                .GetIpcSubscriber<RsrSpecialCommandType, object>($"{IpcPrefix}.TriggerSpecialState")
                .InvokeAction(special);
            return true;
        }
        catch (Exception ex)
        {
            Plugin.Log.Debug(ex, $"[Coppelia] Failed to trigger RSR special state {special}.");
            return false;
        }
    }

    private bool TrySetSetting(string settingName, string value)
    {
        try
        {
            Plugin.PluginInterface
                .GetIpcSubscriber<RsrOtherCommandType, string, object>($"{IpcPrefix}.OtherCommand")
                .InvokeAction(RsrOtherCommandType.Settings, $"{settingName} {value}");
            return true;
        }
        catch (Exception ex)
        {
            Plugin.Log.Debug(ex, $"[Coppelia] Failed to apply RSR setting {settingName}={value}.");
            return false;
        }
    }

    private bool TryToggleAction(string actionName, bool enabled)
    {
        try
        {
            Plugin.PluginInterface
                .GetIpcSubscriber<RsrOtherCommandType, string, object>($"{IpcPrefix}.OtherCommand")
                .InvokeAction(RsrOtherCommandType.ToggleActions, $"{actionName} {ToBoolString(enabled)}");
            return true;
        }
        catch (Exception ex)
        {
            Plugin.Log.Debug(ex, $"[Coppelia] Failed to toggle RSR action {actionName}={enabled}.");
            return false;
        }
    }

    private RsrSessionSnapshot? CaptureSessionSnapshot(HealbotJobProfile profile)
    {
        var root = LoadRoot();
        if (root == null)
        {
            Plugin.Log.Debug("[Coppelia] RotationSolver.json is unavailable; RSR ownership was not acquired.");
            return null;
        }

        var snapshot = new RsrSessionSnapshot
        {
            AutoHeal = ReadBooleanSetting(root, "AutoHeal", fallback: true),
            UseGroundBeneficialAbility = ReadBooleanSetting(root, "UseGroundBeneficialAbility", fallback: true),
            UseAoeDefense = ReadBooleanSetting(root, "UseAoeDefense", fallback: true),
            HealWhenNothingTodo = ReadBooleanSetting(root, "HealWhenNothingTodo", fallback: true),
            FriendlyBattleNpcHeal = ReadBooleanSetting(root, "FriendlyBattleNpcHeal", fallback: false),
            FriendlyPartyNpcHealRaise3 = ReadBooleanSetting(root, "FriendlyPartyNpcHealRaise3", fallback: false),
            ChocoboPartyMember = ReadBooleanSetting(root, "ChocoboPartyMember", fallback: false),
            AoEType = ReadEnum(root, "AoEType", RsrAoEType.Off),
            HostileType = ReadEnum(root, "_hostileTypeDict", profile.JobAbbreviation, RsrTargetHostileType.AllTargetsCanAttack),
            RaiseType = ReadEnum(root, "_RaiseTypeDict", profile.JobAbbreviation, RsrRaiseType.PartyOnly),
            HealthAreaAbilityHot = ReadFloat(root, "_healthAreaAbilityHotDict", profile.JobAbbreviation, 0.55f),
            HealthAreaSpellHot = ReadFloat(root, "_healthAreaSpellHotDict", profile.JobAbbreviation, 0.55f),
            HealthAreaAbility = ReadFloat(root, "_healthAreaAbilityDict", profile.JobAbbreviation, 0.75f),
            HealthAreaSpell = ReadFloat(root, "_healthAreaSpellDict", profile.JobAbbreviation, 0.65f),
            HealthSingleAbilityHot = ReadFloat(root, "_healthSingleAbilityHotDict", profile.JobAbbreviation, 0.65f),
            HealthSingleSpellHot = ReadFloat(root, "_healthSingleSpellHotDict", profile.JobAbbreviation, 0.55f),
            HealthSingleAbility = ReadFloat(root, "_healthSingleAbilityDict", profile.JobAbbreviation, 0.70f),
            HealthSingleSpell = ReadFloat(root, "_healthSingleSpellDict", profile.JobAbbreviation, 0.65f),
        };

        foreach (var pair in ResolveActionIds(profile.OffensiveActionNames))
        {
            snapshot.ActionEnabledByName[pair.Key] =
                !TryReadActionEnabled(root, profile.JobAbbreviation, pair.Value, out var enabled) || enabled;
        }

        return snapshot;
    }

    private JsonObject? LoadRoot()
    {
        try
        {
            if (string.IsNullOrWhiteSpace(rsrConfigPath) || !File.Exists(rsrConfigPath))
                return null;

            return JsonNode.Parse(File.ReadAllText(rsrConfigPath)) as JsonObject;
        }
        catch (Exception ex)
        {
            Plugin.Log.Debug(ex, "[Coppelia] Failed to read RotationSolver.json for restore snapshot.");
            return null;
        }
    }

    private static string? ResolveRsrConfigPath()
    {
        var pluginConfigDirectory = Plugin.PluginInterface.GetPluginConfigDirectory();
        var activeConfigDirectory = new DirectoryInfo(pluginConfigDirectory).Parent?.FullName;
        return string.IsNullOrWhiteSpace(activeConfigDirectory)
            ? null
            : Path.Combine(activeConfigDirectory, "RotationSolver.json");
    }

    private Dictionary<string, uint> ResolveActionIds(IEnumerable<string> actionNames)
    {
        var result = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);
        var wanted = new HashSet<string>(actionNames, StringComparer.OrdinalIgnoreCase);
        if (wanted.Count == 0)
            return result;

        var actionSheet = Plugin.DataManager.GetExcelSheet<GameActionSheet>();
        if (actionSheet == null)
            return result;

        foreach (var row in actionSheet)
        {
            if (row.RowId == 0)
                continue;

            var name = row.Name.ToString();
            if (!wanted.Contains(name))
                continue;

            result[name] = row.RowId;
            wanted.Remove(name);
            if (wanted.Count == 0)
                break;
        }

        return result;
    }

    private static bool TryReadActionEnabled(
        JsonObject root,
        string jobAbbreviation,
        uint actionRowId,
        out bool enabled)
    {
        try
        {
            var actionNode = root?["_rotationActionConfigDict"]?[jobAbbreviation]?[actionRowId.ToString()]?["IsEnabled"];
            if (actionNode == null)
            {
                enabled = false;
                return false;
            }

            enabled = actionNode.GetValue<bool>();
            return true;
        }
        catch
        {
            enabled = false;
            return false;
        }
    }

    private static bool ReadBooleanSetting(JsonObject? root, string propertyName, bool fallback)
    {
        try
        {
            return root?[propertyName]?["Value"]?.GetValue<bool>() ?? fallback;
        }
        catch
        {
            return fallback;
        }
    }

    private static float ReadFloat(JsonObject? root, string dictionaryName, string jobAbbreviation, float fallback)
    {
        try
        {
            return root?[dictionaryName]?[jobAbbreviation]?.GetValue<float>() ?? fallback;
        }
        catch
        {
            return fallback;
        }
    }

    private static TEnum ReadEnum<TEnum>(JsonObject? root, string propertyName, TEnum fallback)
        where TEnum : struct, Enum
    {
        try
        {
            var numericValue = root?[propertyName]?.GetValue<int>();
            return numericValue.HasValue && Enum.IsDefined(typeof(TEnum), numericValue.Value)
                ? (TEnum)Enum.ToObject(typeof(TEnum), numericValue.Value)
                : fallback;
        }
        catch
        {
            return fallback;
        }
    }

    private static TEnum ReadEnum<TEnum>(JsonObject? root, string dictionaryName, string jobAbbreviation, TEnum fallback)
        where TEnum : struct, Enum
    {
        try
        {
            var numericValue = root?[dictionaryName]?[jobAbbreviation]?.GetValue<int>();
            return numericValue.HasValue && Enum.IsDefined(typeof(TEnum), numericValue.Value)
                ? (TEnum)Enum.ToObject(typeof(TEnum), numericValue.Value)
                : fallback;
        }
        catch
        {
            return fallback;
        }
    }

    private static string ToBoolString(bool value)
        => value ? "true" : "false";

    private sealed class RsrSessionSnapshot
    {
        public bool AutoHeal { get; init; }
        public bool UseGroundBeneficialAbility { get; init; }
        public bool UseAoeDefense { get; init; }
        public bool HealWhenNothingTodo { get; init; }
        public bool FriendlyBattleNpcHeal { get; init; }
        public bool FriendlyPartyNpcHealRaise3 { get; init; }
        public bool ChocoboPartyMember { get; init; }
        public RsrAoEType AoEType { get; init; }
        public RsrTargetHostileType HostileType { get; init; }
        public RsrRaiseType RaiseType { get; init; }
        public float HealthAreaAbilityHot { get; init; }
        public float HealthAreaSpellHot { get; init; }
        public float HealthAreaAbility { get; init; }
        public float HealthAreaSpell { get; init; }
        public float HealthSingleAbilityHot { get; init; }
        public float HealthSingleSpellHot { get; init; }
        public float HealthSingleAbility { get; init; }
        public float HealthSingleSpell { get; init; }
        public Dictionary<string, bool> ActionEnabledByName { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    public enum RsrStateCommandType : byte
    {
        Off,
        Auto,
        TargetOnly,
        Manual,
        AutoDuty,
        Henched,
        PvP,
    }

    public enum RsrOtherCommandType : byte
    {
        Settings,
        Rotations,
        DutyRotations,
        DoActions,
        ToggleActions,
        NextAction,
        Cycle,
    }

    public enum RsrSpecialCommandType : byte
    {
        EndSpecial,
        HealArea,
        HealSingle,
        DefenseArea,
        DefenseSingle,
        MoveForward,
        MoveBack,
        AntiKnockback,
        Burst,
        Speed,
        LimitBreak,
        NoCasting,
        NoPositional,
        HealTank,
        RaiseShirk,
        MeleeRange,
    }

    public enum RsrTargetHostileType : byte
    {
        AllTargetsCanAttack,
        TargetsHaveTarget,
        AllTargetsWhenSoloInDuty,
        AllTargetsWhenSolo,
        SoloDeepDungeonSmart,
    }

    public enum RsrRaiseType : byte
    {
        PartyOnly,
        PartyAndAllianceSupports,
        PartyAndAllianceHealers,
        All,
        AllOutOfDuty,
        PartyHealersOnly,
    }

    public enum RsrAoEType : byte
    {
        Full,
        Cleave,
        Off,
    }
}

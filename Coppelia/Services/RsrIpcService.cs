using System.Globalization;
using System.Text.Json.Nodes;
using Coppelia.Models;
using Lumina.Excel.Sheets;
using GameActionSheet = Lumina.Excel.Sheets.Action;

namespace Coppelia.Services;

internal sealed class RsrIpcService
{
    private const string IpcPrefix = "RotationSolverReborn";
    private readonly string rsrConfigPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "XIVLauncher",
        "pluginConfigs",
        "RotationSolver.json");

    private RsrSessionSnapshot? sessionSnapshot;

    public bool ApplyHealbotProfile(HealbotJobProfile profile, Configuration configuration)
    {
        sessionSnapshot ??= CaptureSessionSnapshot(profile);

        var ok = true;
        ok &= TrySetSetting("AutoHeal", "false");
        ok &= TrySetSetting("UseGroundBeneficialAbility", "false");
        ok &= TrySetSetting("UseAoeDefense", "false");
        ok &= TrySetSetting("HealWhenNothingTodo", "false");
        ok &= TrySetSetting("FriendlyPartyNpcHealRaise3", ToBoolString(configuration.WatchPartyNpcs));
        ok &= TrySetSetting("FriendlyBattleNpcHeal", ToBoolString(configuration.WatchFriendlyBattleNpcs));
        ok &= TrySetSetting("ChocoboPartyMember", ToBoolString(configuration.WatchCompanionChocobos));
        ok &= TrySetSetting("AoEType", RsrAoEType.Off.ToString());
        ok &= TrySetSetting("HostileType", RsrTargetHostileType.TargetsHaveTarget.ToString());
        ok &= TrySetSetting("RaiseType", RsrRaiseType.AllOutOfDuty.ToString());
        ok &= TrySetSetting("HealthAreaAbilityHot", "0");
        ok &= TrySetSetting("HealthAreaSpellHot", "0");
        ok &= TrySetSetting("HealthAreaAbility", "0");
        ok &= TrySetSetting("HealthAreaSpell", "0");
        ok &= TrySetSetting("HealthSingleAbilityHot", "0");
        ok &= TrySetSetting("HealthSingleSpellHot", "0");
        ok &= TrySetSetting("HealthSingleAbility", "0");
        ok &= TrySetSetting("HealthSingleSpell", "0");

        foreach (var actionName in profile.OffensiveActionNames.Distinct(StringComparer.OrdinalIgnoreCase))
            ok &= TryToggleAction(actionName, enabled: false);

        ok &= TrySetMode(RsrStateCommandType.Henched);
        return ok;
    }

    public void RestoreSessionSnapshot(bool keepRaiseOutsideDutyEnabled)
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
        if (!keepRaiseOutsideDutyEnabled)
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
    }

    public bool TryTriggerSingleTargetHeal()
        => TryTriggerSpecial(RsrSpecialCommandType.HealSingle);

    public bool TryTriggerRaise()
        => TryTriggerSpecial(RsrSpecialCommandType.RaiseShirk);

    public bool TrySetMode(RsrStateCommandType mode)
    {
        try
        {
            Plugin.PluginInterface
                .GetIpcSubscriber<RsrStateCommandType, object>($"{IpcPrefix}.ChangeOperatingMode")
                .InvokeAction(mode);
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

    private RsrSessionSnapshot CaptureSessionSnapshot(HealbotJobProfile profile)
    {
        var root = LoadRoot();
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
            snapshot.ActionEnabledByName[pair.Key] = ReadActionEnabled(root, profile.JobAbbreviation, pair.Value, fallback: true);

        return snapshot;
    }

    private JsonObject? LoadRoot()
    {
        try
        {
            if (!File.Exists(rsrConfigPath))
                return null;

            return JsonNode.Parse(File.ReadAllText(rsrConfigPath)) as JsonObject;
        }
        catch (Exception ex)
        {
            Plugin.Log.Debug(ex, "[Coppelia] Failed to read RotationSolver.json for restore snapshot.");
            return null;
        }
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

    private static bool ReadActionEnabled(JsonObject? root, string jobAbbreviation, uint actionRowId, bool fallback)
    {
        try
        {
            var actionNode = root?["_rotationActionConfigDict"]?[jobAbbreviation]?[actionRowId.ToString()]?["IsEnabled"];
            return actionNode?.GetValue<bool>() ?? fallback;
        }
        catch
        {
            return fallback;
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

    private static string FormatRatio(int percent)
        => (Math.Clamp(percent, 0, 100) / 100f).ToString("0.##", CultureInfo.InvariantCulture);

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

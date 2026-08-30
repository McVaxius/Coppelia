namespace Coppelia.Models;

internal enum CoppeliaCompanionDecision
{
    Disabled,
    Throttled,
    Unsafe,
    TimerHealthy,
    NoGreens,
    Summon,
}

internal sealed record CoppeliaCompanionConditions(
    bool LoggedIn,
    bool OnFoot,
    bool InCombat,
    bool InDuty,
    bool InSanctuary,
    bool Casting,
    bool Occupied,
    bool HasGysahlGreens,
    float BuddyTimeRemainingSeconds);

internal sealed class CoppeliaCompanionPolicy
{
    public const long CheckIntervalMilliseconds = 15_000;
    public const long StanceDelayMilliseconds = 3_000;
    public const float ResummonThresholdSeconds = 900f;
    public const string FreeStance = "Free Stance";
    public const string DefenderStance = "Defender Stance";
    public const string AttackerStance = "Attacker Stance";
    public const string HealerStance = "Healer Stance";
    public const string FollowStance = "Follow";

    public static readonly string[] StanceNames =
    [
        FreeStance,
        DefenderStance,
        AttackerStance,
        HealerStance,
        FollowStance,
    ];

    private bool qstOwned;
    private bool qstEnabled;
    private bool localEnabled;
    private OperatingRole localRole = OperatingRole.Off;
    private long nextCheckMilliseconds;
    private string? pendingStanceCommand;
    private long pendingStanceMilliseconds;

    public bool IsQstOwned => qstOwned;
    public bool QstEnabled => qstOwned && qstEnabled;
    public bool Enabled => qstOwned
        ? qstEnabled
        : localEnabled && localRole != OperatingRole.Off;
    public bool HasPendingStance => pendingStanceCommand != null;

    public void SetLocalState(bool summonEnabled, OperatingRole role)
    {
        var wasEnabled = Enabled;
        localEnabled = summonEnabled;
        localRole = role;
        ResetIfEffectivenessChanged(wasEnabled);
    }

    public void SetQstOwnership(bool summonEnabled)
    {
        qstOwned = true;
        qstEnabled = summonEnabled;
        nextCheckMilliseconds = 0;
        if (!Enabled)
            CancelPendingStance();
    }

    public void ClearQstOwnership()
    {
        qstOwned = false;
        qstEnabled = false;
        nextCheckMilliseconds = 0;
        if (!Enabled)
            CancelPendingStance();
    }

    public void ScheduleStance(string? savedStance, long nowMilliseconds)
    {
        pendingStanceCommand = GetStanceCommand(savedStance);
        pendingStanceMilliseconds = nowMilliseconds + StanceDelayMilliseconds;
    }

    public string? TakeDueStance(long nowMilliseconds, bool runtimeEffective)
    {
        if (!Enabled || !runtimeEffective)
        {
            CancelPendingStance();
            return null;
        }

        if (pendingStanceCommand == null || nowMilliseconds < pendingStanceMilliseconds)
            return null;

        var command = pendingStanceCommand;
        CancelPendingStance();
        return command;
    }

    public string? SelectStance(string? savedStance, bool companionActive)
    {
        var command = GetStanceCommand(savedStance);
        if (!companionActive)
        {
            if (pendingStanceCommand != null)
                pendingStanceCommand = command;
            return null;
        }

        CancelPendingStance();
        return command;
    }

    public void CancelPendingStance()
    {
        pendingStanceCommand = null;
        pendingStanceMilliseconds = 0;
    }

    public static string NormalizeStance(string? savedStance)
        => savedStance switch
        {
            DefenderStance => DefenderStance,
            AttackerStance => AttackerStance,
            HealerStance => HealerStance,
            FollowStance => FollowStance,
            _ => FreeStance,
        };

    public static string GetStanceCommand(string? savedStance)
        => NormalizeStance(savedStance) switch
        {
            DefenderStance => "/cac \"Defender Stance\"",
            AttackerStance => "/cac \"Attacker Stance\"",
            HealerStance => "/cac \"Healer Stance\"",
            FollowStance => "/cac \"Follow\"",
            _ => "/cac \"Free Stance\"",
        };

    private void ResetIfEffectivenessChanged(bool wasEnabled)
    {
        if (wasEnabled != Enabled)
            nextCheckMilliseconds = 0;
        if (!Enabled)
            CancelPendingStance();
    }

    public CoppeliaCompanionDecision Evaluate(CoppeliaCompanionConditions conditions, long nowMilliseconds)
    {
        if (!Enabled)
            return CoppeliaCompanionDecision.Disabled;
        if (nowMilliseconds < nextCheckMilliseconds)
            return CoppeliaCompanionDecision.Throttled;

        nextCheckMilliseconds = nowMilliseconds + CheckIntervalMilliseconds;
        if (!conditions.LoggedIn ||
            !conditions.OnFoot ||
            conditions.InCombat ||
            conditions.InDuty ||
            conditions.InSanctuary ||
            conditions.Casting ||
            conditions.Occupied)
        {
            return CoppeliaCompanionDecision.Unsafe;
        }

        if (conditions.BuddyTimeRemainingSeconds >= ResummonThresholdSeconds)
            return CoppeliaCompanionDecision.TimerHealthy;
        if (!conditions.HasGysahlGreens)
            return CoppeliaCompanionDecision.NoGreens;

        return CoppeliaCompanionDecision.Summon;
    }
}

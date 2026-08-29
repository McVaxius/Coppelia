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
    public const float ResummonThresholdSeconds = 900f;

    private bool qstOwned;
    private bool enabled;
    private long nextCheckMilliseconds;

    public bool IsQstOwned => qstOwned;
    public bool Enabled => qstOwned && enabled;

    public void SetQstOwnership(bool summonEnabled)
    {
        qstOwned = true;
        enabled = summonEnabled;
        nextCheckMilliseconds = 0;
    }

    public void ClearQstOwnership()
    {
        qstOwned = false;
        enabled = false;
        nextCheckMilliseconds = 0;
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

namespace Coppelia.Models;

internal sealed record PowerlevelActivationInput(
    PowerlevelJob SelectedJob,
    bool SelectedJobUnlocked,
    uint CurrentJobId,
    bool FrenRiderIpcAvailable,
    bool FrenRiderCompatible,
    bool FrenRiderEnabled,
    bool FrenConfigured,
    bool FrenVisible,
    bool CompanionActive);

internal sealed record PowerlevelActivationResult(bool Ready, string Reason)
{
    public static PowerlevelActivationResult Ok()
        => new(true, "PowerlevelBot ready.");

    public static PowerlevelActivationResult Blocked(string reason)
        => new(false, reason);
}

internal static class PowerlevelActivationPolicy
{
    public static PowerlevelActivationResult Evaluate(PowerlevelActivationInput input)
    {
        if (!input.SelectedJob.IsSupportedPowerlevelJob())
            return PowerlevelActivationResult.Blocked("Select BRD or MCH before enabling PowerlevelBot.");

        if (!input.SelectedJobUnlocked)
            return PowerlevelActivationResult.Blocked($"{input.SelectedJob.GetLabel()} is not unlocked.");

        if (input.CurrentJobId != input.SelectedJob.ToJobId())
            return PowerlevelActivationResult.Blocked($"Current job must match the selected {input.SelectedJob.GetLabel()} PowerlevelBot job.");

        if (!input.FrenRiderIpcAvailable)
            return PowerlevelActivationResult.Blocked("FrenRider Powerlevel IPC is unavailable.");

        if (!input.FrenRiderCompatible)
            return PowerlevelActivationResult.Blocked("FrenRider Powerlevel IPC is incompatible. Update FrenRider.");

        if (!input.FrenRiderEnabled)
            return PowerlevelActivationResult.Blocked("FrenRider must be enabled.");

        if (!input.FrenConfigured)
            return PowerlevelActivationResult.Blocked("FrenRider must have a configured Fren.");

        if (!input.FrenVisible)
            return PowerlevelActivationResult.Blocked("FrenRider's configured Fren is not visible.");

        if (input.CompanionActive)
            return PowerlevelActivationResult.Blocked("Dismiss the active companion chocobo before using PowerlevelBot.");

        return PowerlevelActivationResult.Ok();
    }
}

internal sealed record PowerlevelTargetSnapshot(
    ulong GameObjectId,
    string Name,
    uint CurrentHp,
    uint MaxHp,
    float Distance,
    bool IsDead,
    bool IsTargetable,
    bool IsCombatant,
    ulong TargetObjectId,
    bool IsUsable)
{
    public float HpRatio => MaxHp == 0 ? 1f : CurrentHp / (float)MaxHp;
    public bool IsDamaged => MaxHp > 0 && CurrentHp > 0 && CurrentHp < MaxHp;
}

internal sealed record PowerlevelTargetSelection(PowerlevelTargetSnapshot? Target, bool Retained);

internal sealed class PowerlevelTargetSelector
{
    private static readonly TimeSpan RetainedUnusableHold = TimeSpan.FromSeconds(5);
    private ulong retainedTargetId;
    private DateTimeOffset? retainedUnusableSinceUtc;

    public ulong RetainedTargetId => retainedTargetId;

    public void Clear()
    {
        retainedTargetId = 0;
        retainedUnusableSinceUtc = null;
    }

    public void MarkActionLanded(ulong targetId)
    {
        retainedTargetId = targetId;
        retainedUnusableSinceUtc = null;
    }

    public PowerlevelTargetSelection Select(
        IEnumerable<PowerlevelTargetSnapshot> candidates,
        ulong frenObjectId,
        ulong localPlayerObjectId,
        DateTimeOffset nowUtc)
    {
        var eligible = candidates
            .Where(candidate => IsInitiallyEligible(candidate, frenObjectId, localPlayerObjectId))
            .ToArray();

        if (retainedTargetId != 0)
        {
            var retained = eligible.FirstOrDefault(candidate => candidate.GameObjectId == retainedTargetId);
            if (retained != null)
            {
                if (retained.IsUsable)
                {
                    retainedUnusableSinceUtc = null;
                    return new PowerlevelTargetSelection(retained, Retained: true);
                }

                retainedUnusableSinceUtc ??= nowUtc;
                if (nowUtc - retainedUnusableSinceUtc.Value < RetainedUnusableHold)
                    return new PowerlevelTargetSelection(retained, Retained: true);
            }

            Clear();
        }

        var selected = eligible
            .Where(candidate => candidate.IsUsable)
            .OrderBy(candidate => candidate.HpRatio)
            .ThenBy(candidate => candidate.Distance)
            .ThenBy(candidate => candidate.GameObjectId)
            .FirstOrDefault();

        return new PowerlevelTargetSelection(selected, Retained: false);
    }

    public static bool IsInitiallyEligible(PowerlevelTargetSnapshot candidate, ulong frenObjectId, ulong localPlayerObjectId)
    {
        if (!candidate.IsCombatant ||
            candidate.GameObjectId == 0 ||
            candidate.IsDead ||
            !candidate.IsTargetable ||
            !candidate.IsDamaged)
        {
            return false;
        }

        return candidate.TargetObjectId != 0 &&
               (candidate.TargetObjectId == frenObjectId || candidate.TargetObjectId == localPlayerObjectId);
    }
}

internal sealed record PowerlevelActionMetadata(
    string Name,
    bool IsUnlocked,
    bool CanTargetHostile,
    bool CanTargetFriendly,
    bool IsAreaAction,
    int EffectRange,
    int CastTimeMs,
    bool IsAvailable,
    bool IsOffCooldown,
    bool IsInRange);

internal static class PowerlevelActionPolicy
{
    public static bool IsAllowed(PowerlevelActionMetadata action, out string reason)
    {
        if (!action.IsUnlocked)
        {
            reason = $"{action.Name} is not unlocked.";
            return false;
        }

        if (!action.CanTargetHostile || action.CanTargetFriendly)
        {
            reason = $"{action.Name} is not a hostile-only action.";
            return false;
        }

        if (action.CastTimeMs > 0)
        {
            reason = $"{action.Name} is casted.";
            return false;
        }

        if (action.IsAreaAction || action.EffectRange > 0)
        {
            reason = $"{action.Name} is AoE.";
            return false;
        }

        if (!action.IsAvailable)
        {
            reason = $"{action.Name} is unavailable.";
            return false;
        }

        if (!action.IsOffCooldown)
        {
            reason = $"{action.Name} is cooling down.";
            return false;
        }

        if (!action.IsInRange)
        {
            reason = $"{action.Name} is out of range.";
            return false;
        }

        reason = "Ready.";
        return true;
    }
}

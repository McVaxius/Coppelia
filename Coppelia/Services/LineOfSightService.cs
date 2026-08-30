using System.Numerics;
using Dalamud.Game.ClientState.Objects.Types;
using FFXIVClientStructs.FFXIV.Common.Component.BGCollision;

namespace Coppelia.Services;

internal static class LineOfSightService
{
    public const float RescueDistanceYalms = 30f;

    public static bool ShouldRescue(bool targetVisible, float distance, bool lineOfSightBlocked)
        => targetVisible && distance < RescueDistanceYalms && lineOfSightBlocked;

    public static bool TryHasLineOfSight(ICharacter healer, ICharacter target, out bool hasLineOfSight)
    {
        hasLineOfSight = true;
        var origin = healer.Position + Vector3.UnitY * 1.5f;
        var destination = target.Position + Vector3.UnitY * 1.5f;
        var delta = destination - origin;
        var distance = delta.Length();
        if (distance <= 0.05f)
            return true;

        try
        {
            var direction = delta / distance;
            hasLineOfSight = !BGCollisionModule.RaycastMaterialFilter(
                origin,
                direction,
                out RaycastHit _,
                MathF.Max(0.01f, distance - 0.05f));
            return true;
        }
        catch (Exception ex)
        {
            Plugin.Log.Debug(ex, "[Coppelia][HealBot] Native collision LOS probe failed.");
            return false;
        }
    }
}

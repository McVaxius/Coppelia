using System.Numerics;

namespace Coppelia.Services;

internal static class WindowPlacementHelper
{
    public const float ViewportPadding = 12f;

    public static Vector2 GetViewportTopLeft(Vector2 workPos)
        => workPos + new Vector2(ViewportPadding, ViewportPadding);

    public static Vector2 ClampToWorkArea(Vector2 desiredPosition, Vector2 windowSize, Vector2 workPos, Vector2 workSize)
    {
        var minX = workPos.X + ViewportPadding;
        var minY = workPos.Y + ViewportPadding;
        var maxX = workPos.X + Math.Max(ViewportPadding, workSize.X - windowSize.X - ViewportPadding);
        var maxY = workPos.Y + Math.Max(ViewportPadding, workSize.Y - windowSize.Y - ViewportPadding);

        return new Vector2(
            Math.Clamp(desiredPosition.X, minX, maxX),
            Math.Clamp(desiredPosition.Y, minY, maxY));
    }

    public static Vector2 BuildRandomVisiblePosition(Vector2 windowSize, Vector2 workPos, Vector2 workSize)
    {
        var origin = GetViewportTopLeft(workPos);
        var availableX = Math.Max(0f, workSize.X - windowSize.X - (ViewportPadding * 2f));
        var availableY = Math.Max(0f, workSize.Y - windowSize.Y - (ViewportPadding * 2f));

        return new Vector2(
            origin.X + ((float)Random.Shared.NextDouble() * availableX),
            origin.Y + ((float)Random.Shared.NextDouble() * availableY));
    }
}

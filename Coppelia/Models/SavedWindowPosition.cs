using System.Numerics;

namespace Coppelia.Models;

[Serializable]
public sealed class SavedWindowPosition
{
    public bool HasValue { get; set; }
    public float X { get; set; } = 1f;
    public float Y { get; set; } = 1f;

    public Vector2 ToVector2() => new(X, Y);

    public void Set(Vector2 position)
    {
        X = position.X;
        Y = position.Y;
        HasValue = true;
    }

    public void Reset()
    {
        X = 1f;
        Y = 1f;
        HasValue = true;
    }
}

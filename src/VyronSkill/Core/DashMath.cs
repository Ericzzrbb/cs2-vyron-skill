namespace VyronSkill.Core;

/// <summary>
/// 干员偏好 (operator preference) for the dash direction. Delta Force lets players restrict the
/// boost to the four cardinal directions so that diagonals cannot be triggered by accident.
/// </summary>
public enum DashDirectionMode
{
    /// <summary>Default: forward/back/left/right plus the four diagonals (8 directions).</summary>
    EightWay = 0,

    /// <summary>Only the four cardinal directions, resolved from the most recently pressed key.</summary>
    FourWay = 1,
}

/// <summary>A movement key that can steer the dash.</summary>
public enum DirectionKey
{
    None = 0,
    Forward = 1,
    Back = 2,
    Left = 3,
    Right = 4,
}

/// <summary>A horizontal (XY only) direction vector.</summary>
public readonly struct DashVector
{
    public DashVector(float x, float y)
    {
        X = x;
        Y = y;
    }

    public float X { get; }

    public float Y { get; }

    public float Length => MathF.Sqrt((X * X) + (Y * Y));

    public bool IsZero => X == 0f && Y == 0f;

    public DashVector Normalized()
    {
        var length = Length;
        return length <= 0.0001f ? new DashVector(0f, 0f) : new DashVector(X / length, Y / length);
    }

    public override string ToString() => $"({X:0.###}, {Y:0.###})";
}

/// <summary>
/// Pure yaw based dash direction maths. Deliberately free of any CounterStrikeSharp type so the
/// rules can be unit tested outside of a running game server.
/// <para>
/// Source yaw convention: 0 = +X (east), 90 = +Y (north), rotating counter clockwise when viewed
/// from above. Therefore the horizontal forward vector is <c>(cos yaw, sin yaw)</c> and the right
/// vector is <c>(sin yaw, -cos yaw)</c>.
/// </para>
/// </summary>
public static class DashMath
{
    public const float DegreesToRadians = MathF.PI / 180f;

    /// <summary>Horizontal forward vector for a view yaw in degrees.</summary>
    public static DashVector ForwardVector(float yawDegrees)
    {
        var radians = yawDegrees * DegreesToRadians;
        return new DashVector(MathF.Cos(radians), MathF.Sin(radians));
    }

    /// <summary>Horizontal right vector for a view yaw in degrees (view yaw rotated by -90 degrees).</summary>
    public static DashVector RightVector(float yawDegrees)
    {
        var radians = yawDegrees * DegreesToRadians;
        return new DashVector(MathF.Sin(radians), -MathF.Cos(radians));
    }

    /// <summary>
    /// 8-way resolution (default behaviour). The held movement keys are combined relative to the
    /// view yaw, which naturally produces the four cardinals and the four diagonals. When no key is
    /// held the dash follows the crosshair direction (horizontal component only).
    /// </summary>
    public static DashVector ResolveEightWay(float yawDegrees, bool forward, bool back, bool left, bool right)
    {
        var alongForward = (forward ? 1f : 0f) - (back ? 1f : 0f);
        var alongRight = (right ? 1f : 0f) - (left ? 1f : 0f);

        if (alongForward == 0f && alongRight == 0f)
        {
            return ForwardVector(yawDegrees);
        }

        var forwardVector = ForwardVector(yawDegrees);
        var rightVector = RightVector(yawDegrees);

        return new DashVector(
            (forwardVector.X * alongForward) + (rightVector.X * alongRight),
            (forwardVector.Y * alongForward) + (rightVector.Y * alongRight)).Normalized();
    }

    /// <summary>
    /// 4-way resolution: exactly one cardinal direction, taken from the player's most recently
    /// pressed movement key. Diagonals are impossible by design.
    /// </summary>
    public static DashVector ResolveFourWay(float yawDegrees, DirectionKey key)
    {
        var forward = ForwardVector(yawDegrees);
        var right = RightVector(yawDegrees);

        return key switch
        {
            DirectionKey.Forward => forward,
            DirectionKey.Back => new DashVector(-forward.X, -forward.Y),
            DirectionKey.Right => right,
            DirectionKey.Left => new DashVector(-right.X, -right.Y),
            _ => forward,
        };
    }

    /// <summary>Speed (units per second) required to cover <paramref name="meters"/> in <paramref name="durationSeconds"/>.</summary>
    public static float SpeedForDash(float meters, float durationSeconds)
        => durationSeconds <= 0f ? 0f : GameUnits.MetersToUnits(meters) / durationSeconds;

    /// <summary>
    /// Deterministic fallback used when the key-press history is not available yet (for example the
    /// very first tick after a player connected).
    /// </summary>
    public static DirectionKey KeyFromButtonFlags(bool forward, bool back, bool left, bool right)
    {
        if (forward) return DirectionKey.Forward;
        if (back) return DirectionKey.Back;
        if (left) return DirectionKey.Left;
        if (right) return DirectionKey.Right;
        return DirectionKey.None;
    }
}

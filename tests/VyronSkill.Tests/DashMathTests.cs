using VyronSkill.Core;

namespace VyronSkill.Tests;

/// <summary>
/// Verifies the dash direction maths that implements 威龙 动力推进 directional control, including the
/// 干员偏好 "仅4个正方向" (four directions only) option.
/// </summary>
public class DashMathTests
{
    private const float Tolerance = 0.0005f;

    [Theory]
    [InlineData(0f, 1f, 0f)]
    [InlineData(90f, 0f, 1f)]
    [InlineData(180f, -1f, 0f)]
    [InlineData(270f, 0f, -1f)]
    public void ForwardVector_FollowsSourceYawConvention(float yaw, float expectedX, float expectedY)
    {
        var forward = DashMath.ForwardVector(yaw);

        Assert.Equal(expectedX, forward.X, Tolerance);
        Assert.Equal(expectedY, forward.Y, Tolerance);
    }

    [Theory]
    [InlineData(0f, 0f, -1f)]
    [InlineData(90f, 1f, 0f)]
    [InlineData(180f, 0f, 1f)]
    [InlineData(270f, -1f, 0f)]
    public void RightVector_IsYawMinusNinety(float yaw, float expectedX, float expectedY)
    {
        var right = DashMath.RightVector(yaw);

        Assert.Equal(expectedX, right.X, Tolerance);
        Assert.Equal(expectedY, right.Y, Tolerance);
    }

    [Fact]
    public void EightWay_NoKeyHeld_DashesTowardsTheCrosshair()
    {
        var direction = DashMath.ResolveEightWay(90f, forward: false, back: false, left: false, right: false);

        Assert.Equal(0f, direction.X, Tolerance);
        Assert.Equal(1f, direction.Y, Tolerance);
    }

    [Fact]
    public void EightWay_ForwardAndRightKey_ProducesNormalisedDiagonal()
    {
        var direction = DashMath.ResolveEightWay(0f, forward: true, back: false, left: false, right: true);

        Assert.Equal(0.7071f, direction.X, Tolerance);
        Assert.Equal(-0.7071f, direction.Y, Tolerance);
        Assert.Equal(1f, direction.Length, Tolerance);
    }

    [Fact]
    public void EightWay_OppositeKeysCancelOut_AndFallBackToTheViewDirection()
    {
        var direction = DashMath.ResolveEightWay(0f, forward: true, back: true, left: false, right: false);

        Assert.Equal(1f, direction.X, Tolerance);
        Assert.Equal(0f, direction.Y, Tolerance);
    }

    /// <summary>
    /// The default operator preference must expose exactly the eight promised directions
    /// (前/后/左/右 + 四个斜向), all of them unit length.
    /// </summary>
    [Fact]
    public void EightWay_CoversExactlyEightCardinalAndDiagonalDirections()
    {
        var inputs = new[]
        {
            (f: true, b: false, l: false, r: false),
            (f: false, b: true, l: false, r: false),
            (f: false, b: false, l: true, r: false),
            (f: false, b: false, l: false, r: true),
            (f: true, b: false, l: false, r: true),
            (f: true, b: false, l: true, r: false),
            (f: false, b: true, l: false, r: true),
            (f: false, b: true, l: true, r: false),
        };

        var directions = inputs
            .Select(input => DashMath.ResolveEightWay(0f, input.f, input.b, input.l, input.r))
            .Select(vector => (Angle: MathF.Round((MathF.Atan2(vector.Y, vector.X) * 180f / MathF.PI) + 360f) % 360f, vector.Length))
            .ToList();

        Assert.Equal(8, directions.Count);
        Assert.All(directions, direction => Assert.Equal(1f, direction.Length, Tolerance));

        var distinct = directions.Select(direction => MathF.Round(direction.Angle, 1)).Distinct().ToList();
        Assert.Equal(8, distinct.Count);

        // Every direction sits on a 45 degree step.
        Assert.All(distinct, angle => Assert.Equal(0f, angle % 45f, 0.1f));
    }

    [Fact]
    public void FourWay_MostRecentlyPressedKeyWins_SoDiagonalsCannotHappen()
    {
        // W and A are both held, but A was pressed last.
        var direction = DashMath.ResolveFourWay(0f, DirectionKey.Left);

        Assert.Equal(0f, direction.X, Tolerance);
        Assert.Equal(1f, direction.Y, Tolerance);
    }

    [Theory]
    [InlineData(DirectionKey.Forward, 1f, 0f)]
    [InlineData(DirectionKey.Back, -1f, 0f)]
    [InlineData(DirectionKey.Right, 0f, -1f)]
    [InlineData(DirectionKey.Left, 0f, 1f)]
    [InlineData(DirectionKey.None, 1f, 0f)]
    public void FourWay_EachKeyMapsToACardinalDirection(DirectionKey key, float expectedX, float expectedY)
    {
        var direction = DashMath.ResolveFourWay(0f, key);

        Assert.Equal(expectedX, direction.X, Tolerance);
        Assert.Equal(expectedY, direction.Y, Tolerance);
    }

    [Fact]
    public void FourWay_RotatesWithTheView()
    {
        var direction = DashMath.ResolveFourWay(90f, DirectionKey.Forward);

        Assert.Equal(0f, direction.X, Tolerance);
        Assert.Equal(1f, direction.Y, Tolerance);
    }

    [Fact]
    public void KeyFromButtonFlags_UsesADeterministicPriority()
    {
        Assert.Equal(DirectionKey.Forward, DashMath.KeyFromButtonFlags(true, true, true, true));
        Assert.Equal(DirectionKey.Back, DashMath.KeyFromButtonFlags(false, true, true, false));
        Assert.Equal(DirectionKey.Left, DashMath.KeyFromButtonFlags(false, false, true, true));
        Assert.Equal(DirectionKey.Right, DashMath.KeyFromButtonFlags(false, false, false, true));
        Assert.Equal(DirectionKey.None, DashMath.KeyFromButtonFlags(false, false, false, false));
    }

    /// <summary>10 m in 0.35 s must land at roughly 1125 units/second (10 m = 393.7 Source units).</summary>
    [Fact]
    public void SpeedForDash_MatchesTheTenMetreBoost()
    {
        var speed = DashMath.SpeedForDash(10f, 0.35f);

        Assert.Equal(1124.86f, speed, 0.1f);
        Assert.Equal(GameUnits.MetersToUnits(10f), speed * 0.35f, 0.01f);
    }

    [Fact]
    public void SpeedForDash_GuardsAgainstZeroDuration()
    {
        Assert.Equal(0f, DashMath.SpeedForDash(10f, 0f));
    }

    [Fact]
    public void GameUnits_RoundTripsMetres()
    {
        Assert.Equal(393.701f, GameUnits.MetersToUnits(10f), 0.01f);
        Assert.Equal(10f, GameUnits.UnitsToMeters(GameUnits.MetersToUnits(10f)), 0.001f);
    }
}

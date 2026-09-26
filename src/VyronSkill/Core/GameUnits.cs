namespace VyronSkill.Core;

/// <summary>
/// Source 2 world-unit conversions.
/// Source uses the same scale as Source 1: <c>1 unit = 1 inch</c> and <c>12 units = 1 foot</c>,
/// therefore <c>1 metre = 39.3701 units</c>. A standard player is 72 units tall (~1.83 m) and a
/// player hull is 32 units wide (~0.81 m), which matches this scale.
/// </summary>
public static class GameUnits
{
    /// <summary>Number of world units in one metre (39.3701).</summary>
    public const float UnitsPerMeter = 39.3701f;

    /// <summary>Converts metres to the world units used by the game (Vyron's dash is 10 m).</summary>
    public static float MetersToUnits(float meters) => meters * UnitsPerMeter;

    /// <summary>Converts world units back to metres.</summary>
    public static float UnitsToMeters(float units) => units / UnitsPerMeter;
}

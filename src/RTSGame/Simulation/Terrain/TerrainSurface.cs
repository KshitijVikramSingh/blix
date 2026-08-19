namespace RTSGame.Simulation.Terrain;

/// <summary>
/// What the ground is made of. One byte wide, because there are five of them.
/// </summary>
/// <remarks>
/// The width is not cosmetic at this scale. There is one of these per navigation cell — 1.44M on the
/// 600 m map and 5.76M at 1200 m — so the default 32-bit enum spent 23 MB of resident memory and 23 MB
/// of every save describing a choice between five values. Same habit as the rest of this codebase:
/// hold the information at the resolution that decides something.
/// </remarks>
internal enum TerrainSurface : byte
{
    Grass,
    Road,
    Rough,
    Mud,
    Impassable,
}

internal static class TerrainSurfaceRules
{
    /// <summary>Time multiplier of the quickest surface, for heuristic admissibility.</summary>
    /// <remarks>Kept in step with the road multiplier below; the two disagreeing is a wrong route.</remarks>
    public const float MinimumPathCost = 1f / 1.45f;

    public static bool IsPassable(TerrainSurface surface) => surface != TerrainSurface.Impassable;

    /// <summary>
    /// Seconds to cross this surface relative to open ground.
    /// </summary>
    /// <remarks>
    /// Derived from <see cref="SpeedMultiplier"/> rather than tuned separately.
    /// The two used to be independent tables and disagreed badly — the router
    /// believed a road was 28% cheaper than grass when a unit only moves 9%
    /// faster on it, and that mud cost 2.2x when it actually costs 1.8x. Routing
    /// was therefore optimising a preference number that no part of the
    /// simulation honoured. Everything that hauls goods across this map depends
    /// on route choice reflecting real travel time, so there is exactly one
    /// source of truth for how fast ground is, and cost is its reciprocal.
    /// </remarks>
    public static float PathCost(TerrainSurface surface)
    {
        var speed = SpeedMultiplier(surface);
        return speed <= 0f ? float.PositiveInfinity : 1f / speed;
    }

    /// <summary>
    /// Cheapest per-cell cost any ground can have, which is the fastest ground there is.
    /// </summary>
    /// <remarks>
    /// Derived rather than written down, so it cannot drift from the table below. The
    /// routing hierarchy's heuristic multiplies distance by this and would stop being
    /// admissible — and its answers stop being exact — the moment somebody added a
    /// surface faster than road without noticing this existed.
    /// </remarks>
    public static float FastestPathCost { get; } = Enum.GetValues<TerrainSurface>()
        .Where(IsPassable)
        .Select(PathCost)
        .Where(float.IsFinite)
        .DefaultIfEmpty(1f)
        .Min();

    /// <summary>
    /// How fast this ground is, relative to open grass. The one source of truth for it.
    /// </summary>
    /// <remarks>
    /// <b>Road was 1.10 and is now 1.45, and the reason is a measurement.</b> §6 of
    /// <c>plan-rts-game.md</c> claims that "roads literally grow usable territory, and settlements form
    /// ribbons along them — nobody has to author that". <c>--catchment</c> established that a catchment
    /// reaches along a road by <em>exactly</em> the road's speed multiplier and no more, because a
    /// catchment is a time budget: measured 72 m along the road against 66 m on open ground, which is
    /// 1.09 against a multiplier of 1.10. Nine per cent is not a ribbon, so the claim was false and one
    /// of the two had to move.
    /// <para>
    /// 1.45 is a maintained road against grass rather than a highway, and it makes the catchment reach
    /// about 96 m along a road against 66 m across country — a bulge a player can see and build toward.
    /// It follows through the whole system for free, because path cost is derived from this: routes
    /// prefer roads more strongly, hauling legs along roads are genuinely cheaper in the seconds the
    /// hauling board prices in, and none of that needed a second number.
    /// </para>
    /// </remarks>
    public static float SpeedMultiplier(TerrainSurface surface) => surface switch
    {
        TerrainSurface.Road => 1.45f,
        TerrainSurface.Grass => 1.00f,
        TerrainSurface.Rough => 0.78f,
        TerrainSurface.Mud => 0.55f,
        _ => 0f,
    };
}

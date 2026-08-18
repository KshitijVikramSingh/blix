namespace RTSGame.Simulation.Terrain;

internal enum TerrainSurface
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
    public const float MinimumPathCost = 1f / 1.10f;

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

    public static float SpeedMultiplier(TerrainSurface surface) => surface switch
    {
        TerrainSurface.Road => 1.10f,
        TerrainSurface.Grass => 1.00f,
        TerrainSurface.Rough => 0.78f,
        TerrainSurface.Mud => 0.55f,
        _ => 0f,
    };
}

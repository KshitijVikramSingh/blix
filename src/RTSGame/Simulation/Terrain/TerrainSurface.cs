namespace RTSGame.Simulation.Terrain;

/// <summary>
/// What the ground is made of. One byte wide, because there are six of them.
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

    /// <summary>
    /// Moor: high, exposed, thin-soiled ground. Walkable, and poor going.
    /// </summary>
    /// <remarks>
    /// Added so that high country has a <em>ground</em> of its own rather than being grass with different
    /// plants on it. A map where every stretch of country is the same colour under different scatter reads
    /// as one place with dressing changes; four grounds — pasture, moor, scree, marsh — read as four kinds
    /// of country, which at six hundred metres is what the map is for.
    /// <para>
    /// It costs something to cross, which is what makes it geography rather than paint: a tenth slower than
    /// pasture, against scree's fifth and marsh's near-half. So a route round a moor can be worth taking and
    /// a catchment reaching into one is smaller — without anything having to be impassable.
    /// </para>
    /// </remarks>
    Heath,

    /// <summary>
    /// The inside of a stand of trees: ground you cannot walk through.
    /// </summary>
    /// <remarks>
    /// <b>Not <see cref="Impassable"/>, though it behaves the same way.</b> Impassable is water, and
    /// conflating the two would make every question anybody ever asks about water — can a boat cross it,
    /// does it put out a fire, does it stop an arrow — answer the same about a wood. That is the same
    /// mistake as a building's size standing in for whether you can walk on it, and as a store's fullness
    /// standing in for whether anybody can reach what is in it; both were found and fixed this session, and
    /// both were one enum value short of never happening.
    /// <para>
    /// It is a <em>terrain surface</em> rather than an occupied placement cell because the terrain grid
    /// <b>is</b> the navigation grid — half-metre cells — so painting it blocks routing directly, with no
    /// collider per cell. A forest as placement cells would have been seventy thousand static colliders
    /// describing ground nothing ever touches.
    /// </para>
    /// </remarks>
    Forest,
}

internal static class TerrainSurfaceRules
{
    /// <summary>Time multiplier of the quickest surface, for heuristic admissibility.</summary>
    /// <remarks>Kept in step with the road multiplier below; the two disagreeing is a wrong route.</remarks>
    public const float MinimumPathCost = 1f / 1.45f;

    public static bool IsPassable(TerrainSurface surface) =>
        surface is not (TerrainSurface.Impassable or TerrainSurface.Forest);

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
        TerrainSurface.Heath => 0.90f,
        TerrainSurface.Rough => 0.78f,
        TerrainSurface.Mud => 0.55f,
        _ => 0f,
    };
}

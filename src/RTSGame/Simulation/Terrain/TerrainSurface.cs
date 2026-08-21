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

/// <summary>
/// What a slope costs, in bands rather than as a curve.
/// </summary>
/// <remarks>
/// <b>Bands, and the banding is the load-bearing decision rather than the numbers in it.</b> §54 named the
/// risk and the sweep confirmed it: the routing hierarchy's partition is rectangles of uniform ground, and
/// a continuous per-cell cost gives no two neighbouring cells the same cost, so every rectangle collapses
/// to a cell. Measured on a 600 m map, one rectangle became 53,133 at three metres of amplitude and 250,796
/// at twenty-four, with the tick going from 27 ms to 106 and the worst route in the map 52x longer than it
/// needed to be.
/// <para>
/// Quantised, a hillside of even gradient is <em>one</em> band and therefore one region. Five of them,
/// which is enough that a cart minds a hill and few enough that a hill is a handful of shapes: the
/// alternative is a cost that is exactly right per cell and a router that cannot use it.
/// </para>
/// <para>
/// The pace numbers are the shape Tobler's hiking function has over this range — about a sixth off at a
/// tenth grade, and roughly half gone by the time ground is steep enough to scramble. They are a rate and
/// never a gate: nothing here refuses. What refuses is a <em>step</em> between two cells, which
/// <see cref="TerrainMap.MaximumStepHeight"/> and <see cref="TerrainMap.MaximumTraversableGrade"/> decide,
/// and that is the only way relief closes ground.
/// </para>
/// <para>
/// Note what banding does <em>not</em> break: the slowest a band can make ground is slower, never faster,
/// so the fastest ground on any map is still level road and the routing heuristic stays admissible.
/// </para>
/// </remarks>
internal static class SlopeRules
{
    /// <summary>Bands of grade, by their upper bound. The last one runs to the traversable limit.</summary>
    private static readonly float[] Ceilings = { 0.06f, 0.15f, 0.27f, 0.42f, float.MaxValue };

    /// <summary>What each band does to pace.</summary>
    private static readonly float[] Pace = { 1.00f, 0.87f, 0.73f, 0.57f, 0.42f };

    public static int Bands => Ceilings.Length;

    /// <summary>Which band a grade falls in.</summary>
    public static int BandOf(float grade)
    {
        for (var band = 0; band < Ceilings.Length; band++)
        {
            if (grade <= Ceilings[band]) return band;
        }

        return Ceilings.Length - 1;
    }

    /// <summary>How fast a band's ground is, relative to level ground of the same surface.</summary>
    public static float SpeedMultiplier(int band) => Pace[Math.Clamp(band, 0, Pace.Length - 1)];

    /// <summary>The upper grade of a band, for reporting what a band means.</summary>
    public static float CeilingOf(int band) => Ceilings[Math.Clamp(band, 0, Ceilings.Length - 1)];
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
        TerrainSurface.Rough => 0.78f,
        TerrainSurface.Mud => 0.55f,
        _ => 0f,
    };
}

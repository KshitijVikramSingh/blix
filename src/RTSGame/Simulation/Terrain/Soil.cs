namespace RTSGame.Simulation.Terrain;

using System.Numerics;

/// <summary>
/// What the ground is made of, as against what shape it is: how deep the soil is and how wet.
/// </summary>
/// <remarks>
/// <b>Built as a field of its own rather than as two terms inside the woodland, because farming will want it
/// next.</b> Woodland needs soil and moisture; so does a field's yield, and so will grazing, and so will where
/// a player chooses to found. A term buried in <c>WoodlandCover</c> would have to be dug out and duplicated the
/// first time anything else asked — which is how this codebase came to have two copies of the interior-relief
/// measurement and two scree thresholds.
/// <para>
/// <b>Neither number is new information; both are readings of things already computed.</b> Depth comes off
/// slope, biome and deposition, because soil is what has not washed away plus what has arrived. Moisture comes
/// off the drainage's own wetness index, expressed as a <em>rank</em> within this landscape — which is the same
/// device §60 arrived at for the biome thresholds, and for the same reason: an absolute wetness is not portable
/// between maps and a rank is.
/// </para>
/// <para>
/// The climate then shifts the rank rather than replacing it. Dry scrub's median ground is drier in absolute
/// terms than downland's median ground, and multiplying the rank is how one number says so.
/// </para>
/// </remarks>
internal sealed class Soil
{
    /// <summary>What a meadow's ground is made of, before slope and water have their say.</summary>
    /// <remarks>
    /// Named because the neutral fertility is derived from it rather than pasted in. See
    /// <see cref="NeutralFertility"/>.
    /// </remarks>
    private const float MeadowBed = 0.82f;

    private readonly TerrainMap terrain;
    private readonly float wetScale;
    private readonly float floor;
    private readonly float span;

    private Soil(TerrainMap terrain, float wetScale, float floor, float span)
    {
        this.terrain = terrain;
        this.wetScale = wetScale;
        this.floor = floor;
        this.span = span;
    }

    /// <summary>
    /// The soil of a landscape, holding the height range its biome classifier needs.
    /// </summary>
    /// <remarks>
    /// <b>Captured once rather than passed at every call.</b> The floor and span are a sweep of the map, and
    /// they were being measured in three places in the settlement scenario and once more in the game loop —
    /// which is the duplication the remarks on this class were already complaining about. A reading of the
    /// whole landscape belongs to the landscape.
    /// </remarks>
    public static Soil For(TerrainMap terrain, float floor, float span) =>
        new(terrain, RegionProfile.For(terrain.Region).WaterScale, floor, span);

    /// <summary>
    /// How much soil there is here, from nothing on bare rock to a full profile on a river flat.
    /// </summary>
    /// <remarks>
    /// <b>Soil is what has not washed off plus what has arrived.</b> So the two terms are slope, which takes it
    /// away, and deposition, which brings it — and the biome is the summary of everything slower than either.
    /// A steep hillside keeps none whatever else is true of it, which is why the slope term multiplies rather
    /// than adds.
    /// </remarks>
    public float DepthAt(Vector2 at)
    {
        var biome = Biomes.At(terrain, at, floor, span);
        var bed = biome switch
        {
            Biome.Water => 0f,
            Biome.Crag => 0f,
            Biome.Scree => 0.10f,
            Biome.Moor => 0.34f,
            // Peat: deep in its own way, and useless for most of what soil is wanted for.
            Biome.Marsh => 0.62f,
            Biome.Floodplain => 1f,
            _ => MeadowBed,
        };
        return bed <= 0f ? 0f : Depth(bed, terrain.SampleGrade(at), MoistureAt(at));
    }

    /// <summary>The arithmetic of soil depth, with the readings passed in rather than sampled.</summary>
    /// <remarks>
    /// Separated so the neutral case can be evaluated by the same code that evaluates every other case.
    /// A neutral written out by hand is a copy of this arithmetic that stops agreeing with it the first time
    /// either is touched, and it would stop agreeing silently.
    /// </remarks>
    private static float Depth(float bed, float grade, float moisture)
    {
        var held = 1f - Smoothstep(0.06f, 0.32f, grade);
        // What the water brought. Ground that is both wet and level is ground that things settle on, which is
        // the difference between a valley floor and a valley side at the same height.
        var arrived = moisture * (1f - Smoothstep(0.02f, 0.12f, grade));
        return Math.Clamp(bed * held * (0.72f + 0.55f * arrived), 0f, 1f);
    }

    /// <summary>
    /// How wet the ground is, as a rank within this landscape, shifted by the climate.
    /// </summary>
    /// <remarks>
    /// Zero is the driest ground on the map and one the wettest, before the climate has its say — after which a
    /// dry region's wettest ground is still not very wet. Returns a middling value where no drainage has been
    /// solved, which is the flat map every calibrated scenario runs on: level ground with no relief has no
    /// wet end and no dry one.
    /// </remarks>
    public float MoistureAt(Vector2 at)
    {
        if (terrain.Drainage is not { } water) return 0.5f;
        return Math.Clamp(water.WetnessRankAt(at) * wetScale, 0f, 1f);
    }

    /// <summary>
    /// How well grain grows here, as a multiple of what level neutral ground grows.
    /// </summary>
    /// <remarks>
    /// <b>This is the first thing the economy asks the map, and until it existed the map was decoration.</b>
    /// <c>GrainPerFarmPerYear</c> was a flat constant, so a field on a river flat and a field on a scree
    /// shoulder yielded identically and where a player founded was a scoring function's opinion rather than a
    /// consequence. Thirteen sections of generated geography had no consumer in the simulation at all.
    /// <para>
    /// <b>Two terms, and the second is not the same shape as the first.</b> Depth is monotonic — more soil is
    /// more crop, always. Moisture is a plateau with both ends falling away, because grain wants moist ground
    /// and neither parched nor waterlogged ground, and that is the difference between farmland and fen. Note
    /// that <see cref="DepthAt"/> already rewards wetness monotonically through its deposition term, so a
    /// marsh reads as <em>deep</em> soil; the hump is what makes it deep and useless, which is exactly what
    /// the remark on the peat line above says out loud and could not previously act on.
    /// </para>
    /// <para>
    /// <b>Expressed as a multiple of neutral, and the neutral is derived rather than chosen.</b> One is
    /// literally "the ground every calibrated scenario was measured on" — level, meadow, middling water — so
    /// a number here reads as a claim against that baseline and the flat scenarios are unaffected by
    /// construction. It is <em>absolute</em> rather than a rank within this map, which is the opposite of the
    /// choice §60 made for the biome thresholds, and deliberately: a rank would make every landscape feed its
    /// people equally well, and dry country being poor country is the whole point of <c>WaterScale</c>.
    /// </para>
    /// </remarks>
    public float FertilityAt(Vector2 at) =>
        Fertile(DepthAt(at), MoistureAt(at)) / NeutralFertility;

    /// <summary>Level meadow under middling water: the ground every calibrated scenario was measured on.</summary>
    private static readonly float NeutralFertility = Fertile(Depth(MeadowBed, 0f, 0.5f), 0.5f);

    private static float Fertile(float depth, float moisture)
    {
        // Wide enough that ordinary farmland is not being docked for being slightly damp, narrow enough that
        // a fen and a dry hillside both are. Falls to 0.42 rather than to nothing, because bad farmland is
        // bad farmland and not bare rock — depth is the term that goes to zero.
        var drink = 1f - Math.Clamp((MathF.Abs(moisture - 0.52f) - 0.17f) / 0.30f, 0f, 1f);
        return depth * (0.42f + 0.58f * drink);
    }

    private static float Smoothstep(float from, float to, float at)
    {
        var t = Math.Clamp((at - from) / MathF.Max(1e-4f, to - from), 0f, 1f);
        return t * t * (3f - 2f * t);
    }
}

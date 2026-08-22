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
    private readonly TerrainMap terrain;
    private readonly float wetScale;

    private Soil(TerrainMap terrain, float wetScale)
    {
        this.terrain = terrain;
        this.wetScale = wetScale;
    }

    public static Soil For(TerrainMap terrain) =>
        new(terrain, RegionProfile.For(terrain.Region).WaterScale);

    /// <summary>
    /// How much soil there is here, from nothing on bare rock to a full profile on a river flat.
    /// </summary>
    /// <remarks>
    /// <b>Soil is what has not washed off plus what has arrived.</b> So the two terms are slope, which takes it
    /// away, and deposition, which brings it — and the biome is the summary of everything slower than either.
    /// A steep hillside keeps none whatever else is true of it, which is why the slope term multiplies rather
    /// than adds.
    /// </remarks>
    public float DepthAt(Vector2 at, float floor, float span)
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
            _ => 0.82f,
        };
        if (bed <= 0f) return 0f;

        var grade = terrain.SampleGrade(at);
        var held = 1f - Smoothstep(0.06f, 0.32f, grade);
        // What the water brought. Ground that is both wet and level is ground that things settle on, which is
        // the difference between a valley floor and a valley side at the same height.
        var arrived = MoistureAt(at) * (1f - Smoothstep(0.02f, 0.12f, grade));
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

    private static float Smoothstep(float from, float to, float at)
    {
        var t = Math.Clamp((at - from) / MathF.Max(1e-4f, to - from), 0f, 1f);
        return t * t * (3f - 2f * t);
    }
}

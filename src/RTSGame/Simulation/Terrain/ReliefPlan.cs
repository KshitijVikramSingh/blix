using System.Numerics;

namespace RTSGame.Simulation.Terrain;

/// <summary>
/// One piece of high ground: where it is, how big, how tall, and how stretched.
/// </summary>
/// <remarks>
/// A raised cosine rather than a Gaussian or a cone, for two reasons that both matter downstream. Its slope
/// is zero at the rim and at the summit, so a landform meets the plain without a crease and has a top a
/// settlement can stand on; and its steepest grade is exactly <c>π·Height / 2·Radius</c>, which means the
/// question "is this walkable" has an answer in closed form rather than needing a sweep.
/// </remarks>
internal readonly record struct Landform(
    Vector2 Centre,
    float Radius,
    float Height,
    float Stretch,
    float BearingRadians)
{
    /// <summary>The steepest grade anywhere on this landform's flank.</summary>
    /// <remarks>
    /// The derivative of the raised cosine at half radius, divided by the stretch, since stretching a
    /// landform along a bearing makes its across-bearing flank the steep one.
    /// </remarks>
    public float SteepestGrade => Radius <= 0.01f
        ? float.PositiveInfinity
        : MathF.PI * MathF.Abs(Height) / (2f * Radius) * MathF.Max(1f, Stretch);

    public float HeightAt(Vector2 position)
    {
        var offset = position - Centre;
        // Into the landform's own frame, so a ridge is a circle in a squashed space.
        var along = MathF.Cos(BearingRadians) * offset.X + MathF.Sin(BearingRadians) * offset.Y;
        var across = -MathF.Sin(BearingRadians) * offset.X + MathF.Cos(BearingRadians) * offset.Y;
        var stretched = new Vector2(along / MathF.Max(0.01f, Stretch), across);
        var distance = stretched.Length() / MathF.Max(0.01f, Radius);
        if (distance >= 1f) return 0f;
        return Height * 0.5f * (1f + MathF.Cos(MathF.PI * distance));
    }
}

/// <summary>
/// The land itself: a handful of big shapes and a tilt, from a seed.
/// </summary>
/// <remarks>
/// <b>A list of landforms rather than a field of noise, and that is a decision about which layer this
/// belongs to.</b> §54 draws the line at sixty metres: a feature smaller than a catchment cannot change
/// what a site reaches, and one narrower than the settlement cannot give it a back, so anything below that
/// is dressing. Fractal noise is self-similar by definition, so it puts five-metre bumps everywhere — which
/// is dressing-scale detail generated in the layer the simulation has to agree with, and therefore
/// fingerprinted, saved, and rasterised. A list of shapes cannot do that: every member of it is big enough
/// to matter, it can be printed and argued about, and a landform can be asked how steep it is without
/// anybody sampling anything.
/// <para>
/// <b>Feature size is absolute, not a share of the map.</b> A catchment is 66 m and a raid arrives from
/// 120 m whatever the extent is, so a landform on a 1200 m map is the same size as one on a 600 m map and
/// there are four times as many. The alternative — features as a fraction of the extent — would make the
/// same seed mean something different on every map size, and would scale a hill out of the range where it
/// interacts with the things that make it matter.
/// </para>
/// <para>
/// <b>Amplitude zero is exactly today's ground</b>, which is the whole migration plan: every existing
/// scenario keeps its calibration, the determinism fingerprint does not move, and migrating one is a
/// decision about that scenario rather than a flag day.
/// </para>
/// </remarks>
internal sealed class ReliefPlan
{
    /// <summary>
    /// How much of the map high ground covers, before the landforms are allowed to overlap.
    /// </summary>
    /// <remarks>
    /// Two fifths, so a map has both a landscape and a plain in it. Higher and the open ground between
    /// landforms disappears, which is where a settlement wants to be; lower and the landforms stop meeting
    /// each other, which is what makes a valley rather than a scattering of mounds.
    /// </remarks>
    private const float Coverage = 0.40f;

    /// <summary>Radius of a landform, in metres, and the reason it is this and not a share of the map.</summary>
    /// <remarks>
    /// §54 derives it from the clock: a raid arrives from 120 m in about 59 s at raider pace, and walking
    /// round a 150 m flank costs about 84 s at villager pace, so a landform has to have a frontage in the
    /// 150–250 m range for going round it to cost something comparable to arriving at all. A radius of 100 m
    /// is a 200 m frontage, in the middle of that.
    /// </remarks>
    public const float DefaultRadiusMetres = 100f;

    private readonly List<Landform> landforms = new();

    public IReadOnlyList<Landform> Landforms => landforms;

    /// <summary>Fall across the map, as a grade, and which way it falls.</summary>
    /// <remarks>
    /// Gentle to the point of being invisible on its own — a fraction of a per cent — and it is here for
    /// water rather than for walking: a map with no overall fall has nowhere for a river to go, and adding
    /// the tilt later would move every height on every existing map. Cheaper to have it from the start and
    /// leave it at nothing when the amplitude is nothing.
    /// </remarks>
    public float Tilt { get; private init; }

    public Vector2 Downhill { get; private init; }

    public float Amplitude { get; private init; }

    public static ReliefPlan Flat { get; } = new() { Downhill = Vector2.UnitX };

    /// <summary>
    /// Works out the land for a map: a few big shapes, spaced, and a tilt.
    /// </summary>
    /// <remarks>
    /// The count comes from the coverage and the feature size rather than being chosen, so it follows the
    /// extent by itself: about five on a 600 m map and about nineteen on a 1200 m one. Placement is
    /// rejection-sampled against the ones already placed so that landforms are neighbours rather than a
    /// pile — an overlap of more than half a radius merges two features into one shapeless mass, which is
    /// the one outcome that would defeat the whole point of generating shapes rather than noise.
    /// </remarks>
    public static ReliefPlan For(
        float extentMeters,
        uint seed,
        float amplitudeMetres,
        float radiusMetres = DefaultRadiusMetres)
    {
        if (amplitudeMetres <= 0.001f) return Flat;

        var random = new Deterministic(seed);
        var plan = new ReliefPlan
        {
            Amplitude = amplitudeMetres,
            // Half a metre of fall per hundred, at the default amplitude: enough to give water a direction
            // and far too little to notice on foot.
            Tilt = amplitudeMetres / extentMeters * 0.5f,
            Downhill = Deterministic.OnACircle(random.Next()),
        };

        var area = extentMeters * extentMeters;
        var each = MathF.PI * radiusMetres * radiusMetres;
        var wanted = Math.Max(1, (int)MathF.Round(Coverage * area / each));
        // A generous number of attempts rather than a guarantee: a map that cannot fit its last landform
        // gets one fewer, which is a landscape with a bigger plain in it and not a failure.
        var attempts = wanted * 12;
        var half = extentMeters * 0.5f;
        for (var attempt = 0; attempt < attempts && plan.landforms.Count < wanted; attempt++)
        {
            var at = new Vector2(
                (random.Next() * 2f - 1f) * half,
                (random.Next() * 2f - 1f) * half);
            // Varied, because a landscape of identical hills reads as a pattern however it is placed.
            var radius = radiusMetres * (0.72f + random.Next() * 0.62f);
            var tooClose = false;
            foreach (var placed in plan.landforms)
            {
                if (Vector2.Distance(placed.Centre, at) >= (placed.Radius + radius) * 0.55f) continue;
                tooClose = true;
                break;
            }

            if (tooClose) continue;
            plan.landforms.Add(new Landform(
                at,
                radius,
                // Taller shapes are the wider ones, so a landscape has a scale to it rather than spikes.
                amplitudeMetres * (0.45f + random.Next() * 0.85f) * (radius / radiusMetres),
                // Most are round-ish; a few stretch into ridges, which is what gives a site a back rather
                // than a hummock behind it.
                1f + random.Next() * random.Next() * 2.4f,
                random.Next() * MathF.Tau));
        }

        return plan;
    }

    /// <summary>Ground height at a position, before anything is dug into it.</summary>
    public float HeightAt(Vector2 position)
    {
        var height = -Vector2.Dot(position, Downhill) * Tilt;
        foreach (var landform in landforms) height += landform.HeightAt(position);
        return height;
    }

    /// <summary>The steepest grade any of this plan's flanks reaches.</summary>
    /// <remarks>
    /// From the shapes rather than from the height field, so it can be checked against
    /// <see cref="TerrainMap.MaximumTraversableGrade"/> before a single vertex is written. Overlapping
    /// flanks can beat it, which is why the raster still has the last word — but a plan whose steepest
    /// single shape is already a cliff is a plan that will close most of a map.
    /// </remarks>
    public float SteepestGrade
    {
        get
        {
            var steepest = Tilt;
            foreach (var landform in landforms) steepest = MathF.Max(steepest, landform.SteepestGrade);
            return steepest;
        }
    }

    /// <summary>Writes this land into a terrain map's height field.</summary>
    public void Apply(TerrainMap terrain)
    {
        var transform = terrain.Transform;
        var heights = new float[(transform.Width + 1) * (transform.Height + 1)];
        for (var z = 0; z <= transform.Height; z++)
        for (var x = 0; x <= transform.Width; x++)
        {
            var at = transform.Origin + new Vector2(x, z) * transform.CellSize;
            heights[z * (transform.Width + 1) + x] = HeightAt(at);
        }

        terrain.ReplaceHeights(heights);
    }

    public string Describe() => landforms.Count == 0
        ? "flat"
        : $"{landforms.Count} landforms, amplitude {Amplitude:F1} m, steepest flank " +
          $"{SteepestGrade:F2} grade, fall {Tilt * 100f:F2} m per 100 m";

    /// <summary>
    /// A repeatable sequence from a seed, because generation is the simulation's own truth.
    /// </summary>
    /// <remarks>
    /// Not <c>Random</c>: this decides ground that gets fingerprinted, saved and compared between two runs
    /// of the same world, so it has to be the same sequence on any machine and in any framework version.
    /// The mixing is the usual integer avalanche, which is all this needs.
    /// </remarks>
    private struct Deterministic(uint seed)
    {
        private uint state = seed == 0 ? 0x9E3779B9u : seed;

        public float Next()
        {
            state ^= state << 13;
            state ^= state >> 17;
            state ^= state << 5;
            return (state & 0xFFFFFFu) / (float)0x1000000;
        }

        public static Vector2 OnACircle(float turn)
        {
            var angle = turn * MathF.Tau;
            return new Vector2(MathF.Cos(angle), MathF.Sin(angle));
        }
    }
}

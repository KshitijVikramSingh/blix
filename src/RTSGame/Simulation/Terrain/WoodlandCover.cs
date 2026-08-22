namespace RTSGame.Simulation.Terrain;

using System.Numerics;

/// <summary>
/// How much woodland belongs at a place: one continuous field, asked by everything that cares.
/// </summary>
/// <remarks>
/// <b>Three questions, kept apart on purpose.</b> The biome answers <em>what could plausibly grow here</em>;
/// this field answers <em>whether there is actually a wood here</em>; and the value it returns answers
/// <em>what kind of physical wood it is</em>. Collapsing any two of those gives a lookup table — moor plus
/// sparse equals pine — and a lookup table cannot express a wood that thins toward its edge, which is most of
/// what a wood looks like.
/// <para>
/// <b>It exists as a field because four things need to agree about it.</b> Until now the pressure was an
/// expression evaluated per candidate tree inside the scatter loop: it worked, and nothing else could ask it.
/// So tree <em>species</em> was keyed off the renderer's per-frame canopy counts instead — a dressing-side
/// proxy for this, and this session has spent a great deal of time on proxies that turned out to disagree with
/// what they stood for. Placement, species, morphology and clearings now read one number.
/// </para>
/// <para>
/// <b>And it is pressure, not positions.</b> Which is what buys the thing procedural woodland usually lacks:
/// a dense core, a broken edge, scattered outliers, then meadow — rather than forest that stops at a
/// coordinate. Every term below is continuous, so the boundary is wherever the product happens to fall below
/// what a tree needs, and that is a different line for every wood.
/// </para>
/// <para>
/// Dressing and simulation both read it and neither owns it, so it travels on the terrain like
/// <see cref="Drainage"/> and <see cref="TerrainMap.Region"/> do.
/// </para>
/// </remarks>
internal sealed class WoodlandCover
{
    private readonly TerrainMap terrain;
    private readonly MapLayout? layout;
    private readonly RegionProfile profile;
    private readonly float floor;
    private readonly float span;
    private readonly float strength;
    private readonly float[] baked;
    private readonly int cells;
    private readonly float cellMetres;
    private readonly Vector2 origin;

    private WoodlandCover(
        TerrainMap terrain,
        MapLayout? layout,
        RegionProfile profile,
        float floor,
        float span)
    {
        this.terrain = terrain;
        this.layout = layout;
        this.profile = profile;
        this.floor = floor;
        this.span = span;
        // Faded out as the map's height range goes to nothing, which is the migration guarantee: flat ground
        // comes out at the density every economic constant was measured against, and nothing calibrated moves
        // until somebody generates relief on purpose.
        strength = Math.Clamp(span / 8f, 0f, 1f);

        // <b>Baked once onto an eight-metre grid, because the honest computation is far too expensive to ask
        // per query.</b> One call samples the height field a dozen times for shelter and aspect, asks the
        // classifier — which itself reads grade, height and four drainage fields — and then asks it <em>again</em>
        // inside the soil depth. Fifty-odd terrain reads, which is fine once and ruinous per tree per frame.
        //
        // Measured before this: the renderer asked it 5,690 times a frame, once each for species, crown width
        // and undergrowth, and the frame sat at sixty milliseconds on two and a half million triangles. This
        // file's own history has the same paragraph about the ground cover — "so they are answered on an eight
        // metre grid, once" — and eight metres is borrowed from there deliberately: it is finer than anything
        // this field varies at, since the shelter term is already smoothed over forty metres and the noise
        // octaves are ninety-five and two hundred and thirty.
        var transform = terrain.Transform;
        cellMetres = 8f;
        origin = transform.Origin;
        var extent = MathF.Max(
            transform.Width * transform.CellSize,
            transform.Height * transform.CellSize);
        cells = Math.Max(2, (int)MathF.Ceiling(extent / cellMetres) + 1);
        baked = new float[cells * cells];
        for (var z = 0; z < cells; z++)
        for (var x = 0; x < cells; x++)
        {
            baked[z * cells + x] = Compute(origin + new Vector2(x, z) * cellMetres);
        }
    }

    public static WoodlandCover For(TerrainMap terrain, MapLayout? layout, float floor, float span) =>
        new(terrain, layout, RegionProfile.For(terrain.Region), floor, span);

    /// <summary>
    /// The woodland pressure here. Zero is treeless; around one is closed wood; above that is thicket.
    /// </summary>
    /// <remarks>
    /// A product, because every term is a veto as well as a preference: a wood cannot happen on rock however
    /// much the layout asked for one, and cannot happen in a river however sheltered the river is. The one
    /// exception is the authored boost, which is <em>added</em> — see <see cref="Geography"/>.
    /// </remarks>
    /// <summary>How many times this field has been asked, so the asking can be counted.</summary>
    /// <remarks>
    /// An instrument, added because a frame went to eighty milliseconds on three and a half million triangles —
    /// a tenth of the geometry this renderer has drawn at a quarter of the cost, which means the cost is not
    /// geometry. The first question is how often this is called, because each call is expensive and nothing was
    /// counting.
    /// </remarks>
    public static long Asked;

    /// <summary>
    /// The woodland pressure here, read off the baked grid. Zero is treeless, around one is closed wood.
    /// </summary>
    /// <remarks>
    /// Bilinear, so the field is continuous — which matters, because a wood's edge is where this crosses a
    /// threshold and a nearest-cell read would put an eight-metre staircase along every one of them.
    /// </remarks>
    public float At(Vector2 at)
    {
        Asked++;
        var local = (at - origin) / cellMetres;
        var fx = Math.Clamp(local.X, 0f, cells - 1.001f);
        var fz = Math.Clamp(local.Y, 0f, cells - 1.001f);
        var x0 = (int)fx;
        var z0 = (int)fz;
        var tx = fx - x0;
        var tz = fz - z0;
        var a = baked[z0 * cells + x0];
        var b = baked[z0 * cells + x0 + 1];
        var c = baked[(z0 + 1) * cells + x0];
        var d = baked[(z0 + 1) * cells + x0 + 1];
        return (a + (b - a) * tx) * (1f - tz) + (c + (d - c) * tx) * tz;
    }

    /// <summary>The honest computation, run once per grid cell at construction and never per query.</summary>
    private float Compute(Vector2 at)
    {
        var ground = Ground(at);
        if (ground <= 0f) return 0f;
        return Geography(at) * ground * Shelter(at) * Aspect(at) * Soil(at);
    }

    /// <summary>
    /// What the ground is made of, as woodland sees it: enough soil, and neither too dry nor too wet.
    /// </summary>
    /// <remarks>
    /// <b>The moisture response is a hump, and that matters more than the soil term.</b> Every other term in
    /// this field is monotonic — steeper is more wooded, more sheltered is more wooded — and moisture is not:
    /// a dry ridge has no trees because nothing can drink, and a waterlogged bottom has none because roots
    /// drown. Woodland lives in the middle, which is why a wood so often sits <em>between</em> the exposed top
    /// and the marsh rather than at either.
    /// <para>
    /// Treating moisture as monotonic would have pushed every wood into the wettest ground on the map and put a
    /// forest in the fen, which is the one place a forest visibly should not be.
    /// </para>
    /// <para>
    /// Soil is the gentler term: trees tolerate thin ground better than crops do, so a quarter of the response
    /// survives even where there is almost none. It is farming that will care about the difference between
    /// adequate and good.
    /// </para>
    /// </remarks>
    private float Soil(Vector2 at)
    {
        if (terrain.Soil is not { } soil) return 1f;
        var moisture = soil.MoistureAt(at);
        // <b>A plateau with shoulders, not a peak, and the reason is that moisture is a rank.</b> A rank is
        // uniform across the map by construction — the driest tenth of any map ranks 0.1 whether that map is a
        // fen or a desert — so a narrow hump penalises a fixed forty-five per cent of every landscape whatever
        // its climate. Measured, that took "deeply wooded" down to half open ground.
        //
        // Flat across the broad middle and falling away only at the true extremes: the driest third gets
        // thinner, the wettest fifth drowns, and everything between is simply somewhere a tree can live. Which
        // is also the honest reading of what a rank tells you — that a place is dry <em>for here</em>, and only
        // the ends of that are strong enough to decide anything.
        var drink = 1f - MathF.Max(0f, MathF.Abs(moisture - 0.56f) - 0.24f) / 0.32f;
        var depth = soil.DepthAt(at, floor, span);
        return Math.Clamp(drink, 0f, 1f) * (0.25f + 0.75f * depth);
    }

    /// <summary>
    /// Where woodland belongs, from the noise and from what the layout asked for.
    /// </summary>
    /// <remarks>
    /// <b>Two octaves and a curve, and the curve does the work.</b> Smooth noise gives a smooth gradient of
    /// density, which reads as a haze of trees thinning in every direction. A squared smoothstep gives ground
    /// that is definitely wooded next to ground that is definitely not — and the window sits above the middle
    /// of the noise, because centred it maps almost nothing to the floor and the open third comes out thin
    /// rather than open.
    /// <para>
    /// The authored term is added rather than multiplied. A boost has to be able to put a wood where the noise
    /// left open ground, and multiplying by a floor of a hundredth would let the noise veto every authored wood
    /// it happened not to have chosen. Clearings subtract for the same reason in reverse: a clearing has to be
    /// able to empty ground the noise wanted full.
    /// </para>
    /// </remarks>
    public float Geography(Vector2 at)
    {
        var broad = LatticeNoise.Value(at * (1f / 230f) + new Vector2(11.3f, 47.9f));
        var fine = LatticeNoise.Value(at * (1f / 95f) + new Vector2(83.1f, 5.7f));
        var n = broad * 0.72f + fine * 0.28f;

        // <b>Woodedness moves the threshold; it does not scale the amplitude.</b> Scaling was the obvious
        // reading and it did almost nothing, for a reason worth keeping: where the noise already calls forest
        // the pressure is over one and the placement saturates, so multiplying changes nothing there — it only
        // lifts middling ground, and middling ground is capped by the spacing rule from becoming forest. So a
        // "deeply wooded" map came out at half open with a few more stragglers.
        //
        // Shifting the window changes <em>how much of the map is forest at all</em>, which is the thing being
        // asked for. A fifth of the window either way: pastoral country needs the noise near its peak before a
        // wood happens, deeply wooded country takes almost any excuse.
        var woodedness = layout?.Woodedness ?? 1f;
        var shift = (woodedness - 1f) * 0.19f;
        var t = Math.Clamp((n - (0.50f - shift)) / 0.21f, 0f, 1f);
        t = t * t * (3f - 2f * t);
        var cover = 0.012f + 1.9f * t * t;
        if (layout is not { } plan) return cover;
        // The authored woods still scale, mildly, so a pastoral map's wooded ridge is a copse on a ridge rather
        // than a full belt — the statement survives at the size the country allows.
        var asked = cover + plan.WoodBoost(at) * MathF.Min(1.3f, woodedness);
        return MathF.Max(0f, asked - plan.Clearing(at) * 1.4f);
    }

    /// <summary>What the ground itself will carry: its country, its slope, its height, its climate.</summary>
    private float Ground(Vector2 at)
    {
        var biome = Biomes.At(terrain, at, floor, span);
        // Not soil at all. A veto rather than a discouragement, and it applies however hard anything asked.
        if (biome is Biome.Water or Biome.Crag) return 0f;
        if (strength <= 0f) return 1f;

        var slope = Smoothstep(0.04f, 0.26f, terrain.SampleGrade(at));
        var above = Math.Clamp(terrain.SampleHeight(at) / MathF.Max(1f, span), 0f, 1f);
        // Slope is the human part of it: a slope is hard to plough, so forest survives on it and the flat gets
        // cleared. Height adds a little, because higher is cooler and poorer and keeps its trees.
        var shaped = Math.Clamp(1f + strength * (0.85f * slope + 0.30f * above - 0.35f), 0.15f, 1.7f);
        var country = Suitability(biome) * profile.TreeDensity;
        return shaped * (1f - strength + strength * country);
    }

    /// <summary>
    /// How sheltered a place is, over the scale a wood is sheltered at.
    /// </summary>
    /// <remarks>
    /// Convexity over forty metres. Slope, height and biome together cannot tell a valley from a shoulder at
    /// the same height and grade, and that distinction is most of where trees actually are — a wood survives in
    /// a hollow, on a lee slope, behind a ridge, and fails on an exposed top. Forty metres because a hollow
    /// between two tufts shelters nothing and a whole valley is a climate rather than a shelter.
    /// </remarks>
    private float Shelter(Vector2 at)
    {
        const float reach = 40f;
        var here = terrain.SampleHeight(at);
        var around = (terrain.SampleHeight(at + new Vector2(reach, 0f)) +
                      terrain.SampleHeight(at - new Vector2(reach, 0f)) +
                      terrain.SampleHeight(at + new Vector2(0f, reach)) +
                      terrain.SampleHeight(at - new Vector2(0f, reach))) * 0.25f;
        // Normalised against the drop a tenth grade would give over the same reach, so this is "how much of a
        // hollow is this" rather than a number of metres. Half sheltered on level ground.
        return Math.Clamp(0.5f + (around - here) / (reach * 0.10f), 0f, 1.6f);
    }

    /// <summary>
    /// Which way a slope faces. Small, and it is what makes a hillside's two sides differ.
    /// </summary>
    /// <remarks>
    /// At this latitude a south-facing slope takes the sun and dries and a north-facing one holds its damp and
    /// its trees. Deliberately a small term: aspect decides the <em>edge</em> of a wood rather than whether
    /// there is one.
    /// </remarks>
    private float Aspect(Vector2 at)
    {
        var normal = terrain.SampleNormal(at);
        var facing = new Vector2(normal.X, normal.Z);
        var length = facing.Length();
        if (length < 1e-4f) return 1f;
        // +Z is south here, so a normal leaning that way is a sunward slope.
        return 1f - 0.30f * Math.Clamp(facing.Y / length, -1f, 1f);
    }

    /// <summary>
    /// How much woodland a kind of country carries, as a multiple of what pasture carries.
    /// </summary>
    /// <remarks>
    /// Every number is a reason. Floodplain is cleared, grazed and seasonally wet, so a willow fringe is what
    /// is left of it. Marsh drowns roots. Moor is exposed and thin-soiled, so what grows is stunted and
    /// scattered rather than absent. Scree has little to root in. Meadow is exactly one, which is the migration
    /// guarantee: a map with no relief classifies as all meadow, so nothing here can move a flat map's count.
    /// </remarks>
    private static float Suitability(Biome biome) => biome switch
    {
        Biome.Water => 0f,
        Biome.Crag => 0f,
        Biome.Floodplain => 0.14f,
        Biome.Marsh => 0.26f,
        Biome.Scree => 0.42f,
        Biome.Moor => 0.38f,
        _ => 1f,
    };

    private static float Smoothstep(float from, float to, float at)
    {
        var t = Math.Clamp((at - from) / MathF.Max(1e-4f, to - from), 0f, 1f);
        return t * t * (3f - 2f * t);
    }
}

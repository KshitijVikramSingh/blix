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
        // <b>Two fields, because they answer two different questions.</b> Geography says <em>where a wood is</em>
        // and is coherent over hundreds of metres; ground, shelter, aspect and soil say <em>what kind</em> and
        // vary cell to cell. Baked apart so the first can decide the shape and the second can only vary the
        // density inside it — see Concentrate for why mixing them first destroyed the shape.
        var shape = new float[cells * cells];
        var kind = new float[cells * cells];
        for (var z = 0; z < cells; z++)
        for (var x = 0; x < cells; x++)
        {
            var at = origin + new Vector2(x, z) * cellMetres;
            shape[z * cells + x] = ShapeOf(at);
            kind[z * cells + x] = Ground(at) * Shelter(at) * Aspect(at) * Soil(at);
        }

        Concentrate(shape, kind, layout?.Woodedness ?? 1f);
    }

    /// <summary>
    /// Rescales the baked field against its own distribution, so a share of the map is genuinely wooded.
    /// </summary>
    /// <remarks>
    /// <b>Reported from the chair: no stretches of forest on any archetype, relief or seed — only small groups
    /// lying around.</b> Measured, the field explains it exactly. Across three archetypes the median pressure
    /// was 0.02–0.03, p90 was 0.04–0.53, and the share of the map at closed-canopy pressure was <b>0%, 1% and
    /// 3%</b>. There was nothing for a forest to be.
    /// <para>
    /// <b>Two causes, and the second is the structural one.</b> The floor was too low, which is a dial. But
    /// <see cref="Compute"/> is a product of five sub-unity terms — geography, ground, shelter, aspect, soil —
    /// and a product of five such factors is small almost everywhere: the median came out at 0.02 from a
    /// geography floor of 0.10, so the other four multiplied to about a fifth. Worse, a high value needs all
    /// five to agree at once, and the five have <em>different spatial patterns</em>, so agreement happens at
    /// isolated points rather than over regions. Isolated points are clumps. <b>Forest is a region, and a
    /// product of unrelated fields does not make regions.</b>
    /// </para>
    /// <para>
    /// <b>So the field is renormalised against itself rather than re-tuned.</b> Take the share of the map that
    /// should be wooded, find the value at that quantile, and stretch everything above it into closed-canopy
    /// pressure and everything below it into scattered. The underlying field is spatially coherent — two noise
    /// octaves at 230 m and 95 m — so thresholding it yields connected blobs, which is what a wood is. The five
    /// terms keep their whole say over <em>where</em> the wood goes; what they lose is their accidental veto
    /// over whether any wood exists at all.
    /// </para>
    /// <para>
    /// Exactly the device §60 arrived at for the biome thresholds and §62 for the regions: <b>a rank within
    /// this landscape's own distribution, not an absolute value</b>. An absolute threshold on a product of five
    /// factors is a threshold nobody can predict; a quantile is a statement about area, which is the thing
    /// actually being chosen.
    /// </para>
    /// </remarks>
    /// <summary>One box pass over the grid, in place, so a threshold has something coherent to cut.</summary>
    private void Blur(float[] field)
    {
        var copy = (float[])field.Clone();
        for (var z = 0; z < cells; z++)
        for (var x = 0; x < cells; x++)
        {
            var total = 0f;
            var taken = 0;
            for (var dz = -1; dz <= 1; dz++)
            for (var dx = -1; dx <= 1; dx++)
            {
                var nx = x + dx;
                var nz = z + dz;
                if (nx < 0 || nz < 0 || nx >= cells || nz >= cells) continue;
                total += copy[nz * cells + nx];
                taken++;
            }

            field[z * cells + x] = total / MathF.Max(1, taken);
        }
    }

    private void Concentrate(float[] shape, float[] kind, float woodedness)
    {
        if (baked.Length < 16) return;

        // What share of the map is wood. Pastoral country keeps its copses and hedgerow trees; deeply wooded
        // country is wood with fields in it. Both ends are places, which "a few per cent" was not.
        // <b>Reported from the chair as forests being too big, and it is worse than a look problem.</b> §145.
        // Closed canopy becomes Biome.Wood, which becomes TerrainSurface.Forest, which
        // TerrainSurfaceRules.IsPassable calls <em>impassable</em> — so this number is not "how wooded the
        // country looks", it is "what fraction of the map nobody can walk across". At 0.12 + 0.34 it reached
        // sixty-six per cent on Downland, measured and printed by the farmland report every run: two thirds of
        // the map unwalkable, which is also the whole of why §139's second settlement was founded in a forest
        // with 5,095 of 6,561 cells solid around it.
        //
        // 0.10 + 0.20 tops out near forty per cent, which leaves the wooded end of the range recognisably
        // wood-with-fields-in-it and the pastoral end unchanged. The ceiling comes down with it: 0.68 was
        // reachable and should not have been.
        var share = Math.Clamp(0.10f + 0.20f * Math.Clamp(woodedness, 0f, 1.6f), 0.06f, 0.44f);

        // <b>A map with no relief has no woodland geography, because it has no geography.</b> Every term that
        // decides where a wood belongs — slope, shelter, aspect, height, soil depth — is constant on a plain, so
        // the only thing left shaping it is noise, and thresholding noise at two-thirds gives two-thirds of the
        // map under closed canopy for no reason anybody can see.
        // <para>
        // That matters because the flat map is not a place, it is the fixture every economic rate was
        // calibrated on. It carried about 4,500 trees historically and concentrating woodland took it to
        // 28,976 — a sixfold change to the thing §22's numbers were measured against, arrived at as a side
        // effect of making relief maps read better.
        //
        // (The tick did get dearer with those trees, and I mis-blamed it for the settlement year overrunning a
        // timeout. The timeout was mine: the gate imposes none, and that leg has always taken longer than the
        // ten minutes I gave it. The fixture belongs near its calibrated count either way, which is why this
        // stays.)
        // </para>
        // <para>
        // So the share follows the relief that would justify it. <c>strength</c> is already this class's measure
        // of whether the ground has shape worth reading — zero on a plain, one past eight metres of range — and
        // it is the honest multiplier: no shape, no forest, just trees.
        // </para>
        // Tuned so the flat fixture lands near the 4,500 trees every economic rate was measured against, rather
        // than near a number that merely looks reasonable: see §22 for what depends on it.
        share *= 0.10f + 0.90f * strength;

        // <b>Thresholded on the shape alone, and that is the whole correction.</b> Renormalising the product
        // fixed the distribution and left the structure exactly as speckled as it was: 54% of the map came out
        // at closed-canopy pressure and read, correctly, as "an area with a lot of trees around" rather than as
        // forest — because the product's spatial pattern is the fine terms' pattern, and slope, aspect and
        // biome change every cell. Thresholding a speckled field gives more speckles.
        //
        // Geography is two noise octaves at 230 m and 95 m, so its level sets are blobs hundreds of metres
        // across. Cut that, and the wood has an outline.
        // <b>Smoothed before it is cut, because a threshold on a noisy field frays into specks.</b> Measured
        // before this: 17 patches of which 94% were under a third of a hectare — one real wood and sixteen
        // bits of lint, which is the "bunches of 3-4 tiles" case exactly. The 95 m octave is what does it: it
        // rides on the 230 m one and wanders back and forth across the cut, and every wobble near the line
        // becomes its own island.
        //
        // Two box passes over an 8 m grid is a blur of roughly 40 m, which is small against a wood and large
        // against a speck. What survives it is what was a wood before it.
        Blur(shape);
        Blur(shape);

        var sorted = (float[])shape.Clone();
        Array.Sort(sorted);
        var threshold = sorted[Math.Clamp((int)(sorted.Length * (1f - share)), 0, sorted.Length - 1)];
        // The top of the distribution rather than the single highest cell, so one freak value cannot flatten
        // the whole rescale.
        var ceiling = sorted[Math.Clamp((int)(sorted.Length * 0.995f), 0, sorted.Length - 1)];
        var headroom = MathF.Max(1e-4f, ceiling - threshold);

        for (var i = 0; i < baked.Length; i++)
        {
            var here = shape[i];
            // <b>The other four terms modulate, they no longer veto.</b> Compressed into [0.62, 1] so the
            // thinnest ground inside a wood still carries a wood — a north-facing scree shoulder in the middle
            // of a forest is a thinner part of the forest, not a hole in it. Their old power to multiply the
            // answer to nothing is what made agreement between five fields the price of any tree at all.
            var vary = 0.55f + 0.45f * Math.Clamp(kind[i], 0f, 1f);
            baked[i] = here >= threshold
                // <b>Inside a wood it is a wood, and the modifiers only decide how thick.</b> They multiplied
                // the whole band before, which put the thinner parts of a forest back under the closed-canopy
                // line — so the share of the map that read as wood came out at 3% where 29% was asked for. Now
                // 0.88 is the floor of being wooded and everything above it is what the ground makes of it.
                ? 0.88f + 1.02f * MathF.Min(1f, (here - threshold) / headroom) * vary
                // Below it, open country that still has something in it: hedgerow trees and copses, relative to
                // where this map's wood actually starts.
                : 0.34f * (threshold <= 1e-4f ? 0f : here / threshold) * vary;
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
        var depth = soil.DepthAt(at);
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
    /// <summary>
    /// The raw shape of where woodland belongs: two noise octaves, plus whatever the layout authored.
    /// </summary>
    /// <remarks>
    /// <b>Separated from <see cref="Geography"/> because a quantile needs a field with spread, and Geography's
    /// output is deliberately flat over most of a pastoral map.</b> Its window clamps the shaped noise to zero
    /// below a threshold and adds a floor, so on an open roll almost every cell reads exactly that floor —
    /// which made the cut land on the floor and pass <em>everything</em>. Measured: DiagonalRiver came out 100%
    /// closed canopy, one wood over the whole map.
    /// <para>
    /// The raw noise is spread across its range by construction, so a quantile on it means what a quantile
    /// should. The window and the floor were doing the same job the share now does — deciding how much of the
    /// map is wood — and doing it in units nobody could predict.
    /// </para>
    /// <para>
    /// The authored features stay in, and belong here rather than after: a wood the layout asked for should
    /// clear the threshold on that account, and a clearing should fail it. Applied afterwards, a clearing would
    /// sit inside a wood at closed-canopy pressure with its trees taken out by a second rule.
    /// </para>
    /// </remarks>
    private float ShapeOf(Vector2 at)
    {
        var broad = LatticeNoise.Value(at * (1f / 230f) + new Vector2(11.3f, 47.9f));
        var fine = LatticeNoise.Value(at * (1f / 95f) + new Vector2(83.1f, 5.7f));
        var n = broad * 0.72f + fine * 0.28f;
        if (layout is not { } plan) return n;
        return n + plan.WoodBoost(at) * 0.45f - plan.Clearing(at) * 0.60f;
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
    // <b>Compute and Geography are gone, and their replacement is why.</b> Compute multiplied five fields into
    // one pressure and Geography shaped the noise with a window and a floor. Both are superseded by the split
    // in the constructor — ShapeOf decides where a wood is and the four remaining terms decide how thick it is
    // inside one — and keeping them would have left a working implementation of the design that could not make
    // a forest, one call away from whoever next touches this file.

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

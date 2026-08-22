using System.Numerics;

namespace RTSGame.Simulation.Terrain;

/// <summary>What kind of country a piece of ground is.</summary>
internal enum Biome
{
    /// <summary>Level, well-drained, good soil. Pasture and meadow, and what a settlement is founded on.</summary>
    Meadow,

    /// <summary>High and exposed. Thin soil, wiry grass, twisted scrub.</summary>
    Moor,

    /// <summary>Steep. What the slope has not held on to has gone, so what is left is stone.</summary>
    Scree,

    /// <summary>Low and flat with water arriving. Where the water collects and does not leave.</summary>
    Marsh,

    /// <summary>A watercourse or standing water: the channel itself, not its banks.</summary>
    /// <remarks>
    /// A biome rather than a modifier because it is the one that decides whether you can be there at all. Its
    /// surface is chosen by width — see <see cref="Biomes.SurfaceOf"/> — so the same classification gives a
    /// stream you wade and a river you must find a crossing for.
    /// </remarks>
    Water,

    /// <summary>Rock too steep to climb. The frontier, and whatever the frontier's weather has left.</summary>
    /// <remarks>
    /// <b>Told apart from <see cref="Scree"/> by a plain grade test, and that only works because of what
    /// happens upstream.</b> <c>GradeLimit</c> bounds the interior to the plan's budget and deliberately
    /// exempts the frontier, so ground steeper than the budget can only be frontier — no special case and no
    /// flag on a cell, just the one measurement everything else here is made of.
    /// </remarks>
    Crag,

    /// <summary>Level ground a river has laid silt over. The best soil on the map.</summary>
    /// <remarks>
    /// Kept distinct from <see cref="Meadow"/> because it is the thing a settlement should want, and §54's
    /// founding needs something to want. Meadow is merely level and drained; floodplain is level, drained,
    /// low, and next to water — which is where every real village of this size is.
    /// </remarks>
    Floodplain,
}

/// <summary>
/// Which kind of country a place is, from the shape of the ground and the water on it.
/// </summary>
/// <remarks>
/// <b>Read by both layers, which is why it lives here rather than in either of them.</b> Generation paints the
/// surface a biome implies — and a surface is simulation truth, since path cost derives from it — while the
/// renderer picks which trees and which grass grow there. Two consumers of one rule, so the rule is one
/// function and cannot drift.
/// <para>
/// <b>It reads the height field and the water on it, and never a surface.</b> The generator's output is the
/// surface, so a classifier that consulted surfaces would be reading its own answer.
/// </para>
/// <para>
/// <b>The distribution dials are the wetness ranks, and they live on <see cref="RegionProfile"/>.</b> Read
/// them as sentences about the landscape: the wettest twentieth of it is fen; the wettest fifth of its level
/// low ground is floodplain; the driest of its high ground is moor. Every one is a claim about a place
/// relative to the place it is in, which is what makes the same numbers hold on 600 m and on 1800 m — see
/// <see cref="Drainage.WetnessQuantile"/>. They are shares of the wetness distribution rather than of the map,
/// so the mix still depends on the shape of the ground.
/// </para>
/// </remarks>
internal static class Biomes
{
    /// <summary>How far apart the samples are that decide whether a place is a hollow.</summary>
    /// <remarks>
    /// Twenty metres, which is larger than the eight the ground cover uses and smaller than a landform. A
    /// marsh is a feature of a valley bottom rather than of a dip between two tufts.
    /// </remarks>
    private const float HollowReach = 20f;

    /// <remarks>
    /// <b>These moved out to <see cref="RegionProfile"/>, and the move is what made regions possible.</b> They
    /// were three numbers describing one climate; a region is those three numbers pushed around. A dry country
    /// is the same rule with the fen rank near the ceiling, a fen country the same rule with it pulled down —
    /// no second classifier and no special case. Downland's profile carries the values everything here was
    /// calibrated against, unchanged.
    /// </remarks>
    private static RegionProfile Profile(TerrainMap terrain) => RegionProfile.For(terrain.Region);

    /// <summary>
    /// The grade above which ground stops being climbable at all.
    /// </summary>
    /// <remarks>
    /// Comfortably above what the interior can reach — the grade budget is about a third and the worst a
    /// bilinear corner can turn that into is a half — and comfortably below what an un-limited frontier
    /// reaches. The gap either side is the point: this is not a knob to tune the amount of crag with, it is a
    /// line drawn through empty space so that which side of it a place falls on is never in doubt.
    /// </remarks>
    private const float CragGrade = 0.55f;

    public static Biome At(TerrainMap terrain, Vector2 at, float floor, float span)
    {
        // A map with no relief is all one country, and that is the flat map every scenario is calibrated
        // on: no surface is painted, nothing moves.
        if (span < 1f) return Biome.Meadow;

        var grade = terrain.SampleGrade(at);
        var here = terrain.SampleHeight(at);
        var above = Math.Clamp((here - floor) / span, 0f, 1f);

        if (terrain.Drainage is not { } water)
        {
            // No drainage solved for this map. Falls back to the local reading this classifier used before
            // there was a flow field — kept because a map can be given heights directly, and a scenario that
            // does should still get country rather than an exception.
            if (grade > 0.30f) return Biome.Scree;
            var around = (terrain.SampleHeight(at + new Vector2(HollowReach, 0f)) +
                          terrain.SampleHeight(at - new Vector2(HollowReach, 0f)) +
                          terrain.SampleHeight(at + new Vector2(0f, HollowReach)) +
                          terrain.SampleHeight(at - new Vector2(0f, HollowReach))) * 0.25f;
            var hollow = (around - here) / (HollowReach * 0.10f);
            if (hollow > 0.40f && above < 0.38f) return Biome.Marsh;
            return above > 0.52f ? Biome.Moor : Biome.Meadow;
        }

        // <b>Water first, because it is the only class that overrules the shape of the ground.</b> A channel
        // on a steep slope is still a channel, and a pond in a hollow is not scree however steep its sides.
        var area = water.AreaAt(at);
        // Width from the nearest cell rather than from the interpolated area — see the note on WidthAt for
        // why, and for what it costs.
        // <b>Asked of the water level rather than of three separate tests, which is what let the sea in.</b>
        // Lake depth, channel width and now sea level are three reasons a place can be under water, and the
        // level field already resolves all three into one number — so asking it directly means the classifier
        // and the surface that draws the water agree by construction rather than by keeping three conditions in
        // step. It also means the sea needed no case of its own here.
        var depth = water.LevelAt(at) - here;
        if (depth > Drainage.PondDepthMetres) return Biome.Water;

        // Above the interior's own grade budget, so this is the frontier — see the remarks on
        // <see cref="Biome.Crag"/> for why that inference is sound rather than a guess.
        if (grade > CragGrade) return Biome.Crag;

        // Read off the field the drainage keeps rather than recomputed here, so the value being compared and
        // the ranks being compared against are the same measurement — computing it twice from two different
        // grades is how a percentile stops meaning anything.
        //
        // <b>Normalising this by the map's area was tried, and it was the wrong suspect.</b> A larger canvas
        // came out a quarter marsh, which looked exactly like a threshold that had slid with the extent — and
        // dividing by the map area fixed that reading and broke the other end, leaving an 1800 m canvas 81%
        // pasture. Both symptoms had one cause somewhere else: the inherited river's catchment was written as
        // a multiple of the canvas, so viewing a bigger canvas poured nine times the water into the same
        // river. Upslope area is already the right quantity — a hillslope a hundred metres below its divide
        // has the same catchment whatever else is on the map — and the trunk *should* carry more when more
        // drains into it. See ReliefPlan.InheritedCatchmentMetres2.
        var wetness = water.WetnessAt(at);
        var profile = Profile(terrain);

        // <b>Fen: a lot arrives and none of it leaves.</b> This is the class the old concavity test was
        // reaching for, and the difference is that it now knows the size of the hillside above it — so a
        // shallow dish that half the map drains into is marsh, and a deep dish that nothing feeds is not.
        if (wetness > water.WetnessQuantile(profile.FenRank)) return Biome.Marsh;

        // <b>Moor is tested before scree, and the order is the decision.</b> With scree first, high ground
        // only became moor where it was also gentle — and eroded high ground almost never is, so a map with
        // visible moors on it reported five per cent moor against sixteen per cent scree. But a moor is not
        // level ground; it is ground that <em>sheds water</em>, and in upland country most of it is on a
        // slope. So being high and dry claims a place first and scree takes what is left over and steep,
        // which reads correctly as well: heather over the shoulder of a hill, bare stone where it breaks.
        //
        // The height threshold moved when the frontier did, and had to. Height enters normalised against the
        // interior's range, and a rimmed map's interior is incised far more deeply than a bare one — the
        // measured range went from 24 m to 41 m at the same amplitude — so "above half of it" stopped being
        // anywhere at all.
        if (above > profile.MoorAbove && wetness < water.WetnessQuantile(profile.MoorRank)) return Biome.Moor;

        // Three tenths rather than a quarter, because erosion behind a frontier steepens the interior a good
        // deal more than composed landforms did. Not a threshold that was wrong, a threshold calibrated
        // against different ground.
        // <b>How steep before the soil has gone, which is a property of the country.</b> Thin-soiled upland
        // shows stone on a gentler slope than pasture does; a fen holds its turf on a bank a heath would have
        // lost. Without this every region had identical scree, which is most of why five regions still looked
        // like one.
        if (grade > profile.ScreeGrade) return Biome.Scree;

        // <b>Floodplain: level, low, and beside the water.</b> The three together, because any two of them
        // describe something else — level and low without water is a dry pan, and level and wet without
        // being low is a hanging bog.
        //
        // A wetness of 9.5 is a hillslope with a few hundred square metres above it, which is most of a
        // hillside, and it put a quarter of the map under silt. Half a unit of a logarithm is a factor of e
        // in the catchment above, which is the difference between "water passes here" and "a river laid this
        // down".
        if (grade < 0.08f && above < 0.32f && wetness > water.WetnessQuantile(profile.FloodplainRank))
        {
            return Biome.Floodplain;
        }

        return Biome.Meadow;
    }

    /// <summary>The ground a biome is made of, which is what the simulation reads.</summary>
    /// <remarks>
    /// <b>Water needs its width, because whether you can cross it is the whole of what it is.</b> Everything
    /// else here is a property of the place; a watercourse is a property of the place and its size.
    /// </remarks>
    public static TerrainSurface SurfaceOf(
        Biome biome,
        float channelWidthMetres = 0f,
        float depthMetres = 0f) => biome switch
    {
        Biome.Moor => TerrainSurface.Heath,
        Biome.Scree => TerrainSurface.Rough,
        Biome.Crag => TerrainSurface.Impassable,
        Biome.Marsh => TerrainSurface.Mud,
        // <b>Depth decides, and width was a proxy for it that breaks the moment fords exist.</b> Fordability
        // started as "how far do you have to wade", which is a width question — and it worked only because
        // depth was <em>derived</em> from width, so the two always agreed. A ford is the case where they must
        // not: it is a broad shallow place, gravel rather than gorge, so it is wide and crossable at once. Under
        // the width test a map could author a crossing and get an unbroken barrier, which is exactly what
        // happened.
        //
        // Width keeps one job, as an escape hatch rather than a criterion: a channel narrower than a stride is
        // wadeable whatever the depth field says, because at four metres a lattice cell the depth of something
        // two metres across is not a number anybody should trust.
        Biome.Water => depthMetres < WadeableDepthMetres || channelWidthMetres < FordableWidthMetres
            ? TerrainSurface.Shallows
            : TerrainSurface.Impassable,
        // Floodplain is grass like meadow is, and that is deliberate: the difference between them is soil
        // rather than surface, so it belongs in what a field yields and in where a site scores well, not in
        // what a boot finds underfoot. Giving it its own surface would have made it a different *speed*,
        // which is not what is true about it.
        _ => TerrainSurface.Grass,
    };

    /// <summary>
    /// The widest water something on foot will wade rather than go round.
    /// </summary>
    /// <remarks>
    /// Two metres, which is a stride and a half — a stream, not a river. Above it the water is a barrier and
    /// a crossing has to be found, which is what makes a river worth having on a map at all.
    /// <para>
    /// <b>The middle band is not here yet and this is the note that says so.</b> The intent was three bands,
    /// the middle one fordable "at low water only" — a river you can cross in late summer and not after the
    /// autumn rains. That needs a seasonal water level for the router to read, and inventing one here would
    /// put a second source of truth about how much water there is next to the one that already exists. So
    /// two metres is the line for now, and the middle band collapses into the barrier.
    /// </para>
    /// </remarks>
    public const float FordableWidthMetres = 2f;

    /// <summary>The deepest water something on foot will wade. Waist height, roughly.</summary>
    public const float WadeableDepthMetres = 1.1f;
}

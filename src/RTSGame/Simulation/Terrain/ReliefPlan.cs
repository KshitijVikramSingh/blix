using System.Numerics;

namespace RTSGame.Simulation.Terrain;

/// <summary>
/// One piece of high ground: a flank at a stated grade, a lobed outline, and a summit that is not a lid.
/// </summary>
/// <remarks>
/// <b>Third shape, and the two rejected ones are worth keeping because they fail in opposite directions.</b>
/// A raised cosine has zero slope at both the rim and the summit, so all of its fall is concentrated in a
/// thin ring and the average flank is nearly level — a 7.6 m rise over 130 m of radius was reported, quite
/// correctly, as "less like features and more like paper lying on the ground". A plateau with straight sides
/// fixes the grade and reads as a mesa: the shape the terrain lab used, and its own author's verdict on
/// those is that they were barely placeholders.
/// <para>
/// So: a flank of a <em>stated</em> grade, because that is what both of relief's jobs need — a slope you can
/// see and a cost you can price — wrapped in three things that stop it being a solid of revolution.
/// </para>
/// <para>
/// <b>The outline is lobed</b>, by a few harmonics of the bearing, so a landform has spurs running out of it
/// and re-entrants cut into it rather than a circular skirt. This is the single biggest difference between
/// something that reads as a hill and something that reads as a dome: real high ground is where erosion has
/// <em>not</em> removed the rock, and what erosion leaves is fingered.
/// </para>
/// <para>
/// <b>The summit is not a lid.</b> It falls slightly toward its own edges and carries the ridge line along
/// the landform's bearing, so a stretched landform is a ridge with a crest rather than a runway.
/// </para>
/// <para>
/// The broad undulation that stops all of this reading as arithmetic belongs to the <em>plan</em> rather
/// than to any one landform, because the plain needs it too — see <see cref="ReliefPlan.HeightAt"/>.
/// </para>
/// </remarks>
internal readonly record struct Landform(
    Vector2 Centre,
    float TopRadius,
    float FlankGrade,
    float Height,
    float Stretch,
    float BearingRadians,
    float Lobing,
    float Seed)
{
    /// <summary>
    /// Share of the flank spent easing into the plain at one end and onto the summit at the other.
    /// </summary>
    /// <remarks>
    /// Enough that the mesh facets cleanly instead of carrying a hard ring at the rim, and little enough
    /// that the middle of the flank is a straight line of the stated grade — which is the point of stating
    /// it.
    /// </remarks>
    private const float Ease = 0.12f;

    /// <summary>
    /// How wide the flank is, which follows from how tall the landform is and how steep it falls.
    /// </summary>
    /// <remarks>
    /// Widened for the easing, so the <em>straight</em> part of the flank has exactly the stated grade
    /// rather than the average having it.
    /// </remarks>
    public float FlankWidth =>
        MathF.Abs(Height) / (MathF.Max(0.01f, FlankGrade) * (1f - Ease));

    /// <summary>Where the landform stops and the plain begins, before the outline is lobed.</summary>
    public float BaseRadius => TopRadius + FlankWidth;

    /// <summary>
    /// The steepest grade this landform can reach, as a bound rather than an identity.
    /// </summary>
    /// <remarks>
    /// The flank's stated grade is what the straight part has, and the lobing steepens it wherever it pulls
    /// the rim inward — a re-entrant is a short flank, and a short flank of the same height is a steeper
    /// one. So this is the flank grade divided by the tightest the outline gets, which is an upper bound and
    /// is what a caller wanting to know "can this close ground" needs. What the map <em>actually</em>
    /// reaches is measured by the relief sweep's band histogram, and the two are worth comparing.
    /// </remarks>
    public float SteepestGrade => FlankGrade / MathF.Max(0.35f, 1f - Lobing);

    public float HeightAt(Vector2 position)
    {
        // <b>Two octaves, and the second is what stops a landform reading as a solid of revolution.</b>
        // Reported as looking more like the moon's surface than the earth's, which is exactly what a smooth
        // radial shape is: the moon has no water, so its hills are un-eroded and radially symmetric, and
        // ours were too. What breaks that on earth is drainage — water running off a hill cuts gullies and
        // leaves spurs between them, at a scale of a few tens of metres.
        //
        // A second octave at forty-five metres does that without modelling any of it, and it is the right
        // layer for it: at the landform's own scale it would move the whole shape, and below §54's sixty
        // metre line it would be dressing. The amplitudes are chosen from their slopes rather than their
        // looks — 2πA/λ each — so the two together spend about a quarter of a grade and the flanks are
        // budgeted knowing it.

        var offset = position - Centre;
        // Into the landform's own frame, so a ridge is a circle in a squashed space.
        var along = MathF.Cos(BearingRadians) * offset.X + MathF.Sin(BearingRadians) * offset.Y;
        var across = -MathF.Sin(BearingRadians) * offset.X + MathF.Cos(BearingRadians) * offset.Y;
        var stretched = new Vector2(along / MathF.Max(0.01f, Stretch), across);
        var distance = stretched.Length();

        // <b>Spurs and re-entrants, and their frequency is a slope budget rather than a look.</b> Modulating
        // a rim by bearing steepens the surface <em>tangentially</em>, by the flank's grade times how fast
        // the rim moves along itself: dR/ds is <c>Lobing × Σ(coefficient × harmonic)</c>, and at harmonics
        // 3, 5 and 7 that reached 2.07 — a rim sliding two metres outward for every metre along it, which
        // multiplies the gradient by 2.3 and puts a flank budgeted at 0.42 clean through the traversable
        // limit. Measured before it was understood: ground kept closing at a stated steepest of 0.35, and
        // about one and a half per cent of the map going impassable was enough to fragment a single-rectangle
        // partition into two thousand and take the worst route to 465x optimal.
        //
        // Two harmonics rather than three, and the low ones. Same lobing, a quarter of the rate of change:
        // dR/ds tops out near 0.5 and the gradient multiplier at 1.11, which the budget can afford. What is
        // given up is the finest fingering, which was never load-bearing — spurs at the scale of a whole
        // landform are what makes an outline read as eroded.
        var bearing = MathF.Atan2(stretched.Y, stretched.X);
        var lobes = 1f + Lobing * (
            0.62f * MathF.Sin(2f * bearing + Seed * 6.28f) +
            0.38f * MathF.Sin(3f * bearing - Seed * 11.0f));
        var baseRadius = BaseRadius * lobes;
        var topRadius = TopRadius * lobes;
        if (distance >= baseRadius) return 0f;

        float height;
        if (distance <= topRadius)
        {
            // <b>A crest rather than a lid, and it has to rise above the flank's top rather than dip below
            // it.</b> The first version fell from the centre to 0.84 of the height at the summit's edge —
            // while the flank arrives there at 1.0, so the two met in a step of 0.16 of the landform's
            // whole height. Measured as a grade of 5.25 at twelve metres of amplitude and 8.75 at twenty:
            // proportional to the height, in the same place every time, and invisible to three rounds of
            // budgeting the flank because it was not in the flank.
            //
            // Domed upward instead, so the summit is the highest point and the join is exact. The dome's
            // own grade at the join is <c>0.2 x Height / TopRadius</c>, which is a tenth on a typical
            // landform and part of the same budget the flank spends.
            var acrossTop = topRadius <= 0.01f ? 0f : distance / topRadius;
            height = Height * (1f + 0.10f * (1f - acrossTop * acrossTop));
        }
        else
        {
            var flank = MathF.Max(0.01f, baseRadius - topRadius);
            height = Height * Rise((baseRadius - distance) / flank);
        }

        return height;
    }

    /// <summary>
    /// Rises from nothing to one: a quadratic ramp, a straight run, a quadratic ramp.
    /// </summary>
    /// <remarks>
    /// The slope of the straight run is <c>1/(1 - Ease)</c> rather than one, which is what makes the three
    /// pieces meet and the total come to exactly one. Written out because the first version did not: it
    /// eased both ends with the same expression and was discontinuous at the upper join by <c>Ease</c> — a
    /// step of a metre or so, which would have shown as a ring round every landform and been blamed on the
    /// mesh.
    /// </remarks>
    private static float Rise(float t)
    {
        if (t <= 0f) return 0f;
        if (t >= 1f) return 1f;
        var slope = 1f / (1f - Ease);
        if (t < Ease) return slope * t * t / (2f * Ease);
        if (t > 1f - Ease) return 1f - slope * (1f - t) * (1f - t) / (2f * Ease);
        return slope * (t - Ease * 0.5f);
    }

    /// <summary>The broad undulation, shared with the plan that owns it.</summary>
    internal static float Undulate(Vector2 position, uint seed, float wavelength) =>
        Noise(position * (1f / wavelength) + new Vector2(seed % 977 * 0.31f, seed % 613 * 0.57f));

    /// <summary>Smooth value noise on a unit lattice, in [0,1].</summary>
    /// <remarks>
    /// Written here rather than reached for, because it has to be identical on any machine: this decides
    /// ground the simulation agrees with, gets fingerprinted and gets saved. Bilinear over a hashed lattice
    /// with a smoothstep on each axis — one octave is all that is wanted, since §54 gives everything below
    /// sixty metres to the dressing layer and a second octave would be exactly that.
    /// </remarks>
    private static float Noise(Vector2 at)
    {
        var x0 = (int)MathF.Floor(at.X);
        var y0 = (int)MathF.Floor(at.Y);
        var tx = at.X - x0;
        var ty = at.Y - y0;
        // <b>Quintic rather than cubic, because the gradient has to be continuous and not just the
        // value.</b> Smoothstep has a second derivative that jumps at every lattice line, which on a
        // height field is a faint crease every wavelength — a grid of them, axis-aligned, which is one of
        // the ways ground "changes in weird ways". The quintic is flat to second order at both ends, so the
        // lattice leaves no trace in the slope.
        tx = tx * tx * tx * (tx * (tx * 6f - 15f) + 10f);
        ty = ty * ty * ty * (ty * (ty * 6f - 15f) + 10f);
        var a = Lattice(x0, y0);
        var b = Lattice(x0 + 1, y0);
        var c = Lattice(x0, y0 + 1);
        var d = Lattice(x0 + 1, y0 + 1);
        return (a + (b - a) * tx) * (1f - ty) + (c + (d - c) * tx) * ty;
    }

    private static float Lattice(int x, int y)
    {
        var hash = (uint)(x * 374761393) ^ (uint)(y * 668265263);
        hash = (hash ^ (hash >> 13)) * 1274126177u;
        return ((hash ^ (hash >> 16)) & 0xFFFFFFu) / (float)0x1000000u;
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
    /// Composes the land for a map: a range, a plain beside it, foothills, and a knoll or two.
    /// </summary>
    /// <remarks>
    /// <b>Composed rather than scattered, and the difference is that a scattered map has no answer to
    /// "which way do I expand".</b> Rejection-sampled hills produce a landscape — the previous version did,
    /// and it looked like one — but every direction out of a settlement is then much like every other, so
    /// nothing about <em>where</em> you are can matter. That is the argument §50 made for shaping the
    /// woodland by bearing, applied to the ground the woodland grows on.
    /// <para>
    /// The structure is four things, and it deliberately knows nothing about where anybody will settle. A
    /// generator composed around a predetermined site would be answering §54's founding question on the
    /// player's behalf; the job here is to make a map that <em>has</em> good and bad sites in it and let
    /// the choosing find them.
    /// </para>
    /// <list type="bullet">
    /// <item><b>A range</b>, off-centre: three to five landforms chained along one bearing with their
    /// skirts overlapping, so they merge into a ridge rather than standing as separate mounds — and the
    /// gaps between their summits become saddles, which are the passes through it. Each is stretched along
    /// the chain, so it reads as a length of ridge and not a bead on a string.</item>
    /// <item><b>A plain</b> on the far side, which is not placed but <em>kept</em>: nothing else is allowed
    /// to land there. A map with no open ground in it has nowhere to build.</item>
    /// <item><b>Foothills</b> on the range's own side, lower and smaller, so the high country has a
    /// shoulder rather than an edge.</item>
    /// <item><b>A knoll or two</b> out in the plain, because a plain with nothing in it is a table — and
    /// because an isolated piece of high ground in open country is the most valuable site on a map.</item>
    /// </list>
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
            seed = seed,
            Amplitude = amplitudeMetres,
            // A tenth of the amplitude, and never more than a metre and a half — see HeightAt for why that
            // ceiling is a slope budget rather than a preference.
            Undulation = MathF.Min(2.0f, amplitudeMetres * 0.13f),
            // Half a metre of fall per hundred, at the default amplitude: enough to give water a direction
            // and far too little to notice on foot.
            Tilt = amplitudeMetres / extentMeters * 0.5f,
            Downhill = Deterministic.OnACircle(random.Next()),
        };

        var half = extentMeters * 0.5f;
        // The range's bearing and the direction across it. Everything else is placed relative to these, so
        // a seed produces a map with a grain to it rather than a distribution.
        var along = Deterministic.OnACircle(random.Next());
        var across = new Vector2(-along.Y, along.X);
        // Off-centre, so one side of the map is high country and the other is open. Dead centre gives a map
        // with two identical halves, which is the same failure as a settlement in the middle of everything.
        var toOneSide = (0.10f + random.Next() * 0.16f) * extentMeters *
                        (random.Next() < 0.5f ? -1f : 1f);
        var spine = across * toOneSide;
        // Which way the plain is: away from the range.
        var plainward = toOneSide > 0f ? -1f : 1f;

        var members = 3 + (int)MathF.Round(extentMeters / 600f);
        // <b>The chain has to fit, or its ends are the ones that pay.</b> First measurement of a composed
        // map: a range of four with its ends at 52 m from the border, where the fit-inside-the-map rule cut
        // them from about 12 m tall to 3.7 and 3.2 — so the taper that was meant to give a range shoulders
        // instead gave it two stubs. Half the chain plus a landform's own footprint has to stay inside the
        // half extent, and the length is what gives.
        var reach = MathF.Min(radiusMetres * (members - 1) * 0.72f, half - radiusMetres * 1.35f);
        for (var i = 0; i < members; i++)
        {
            var t = members <= 1 ? 0.5f : i / (float)(members - 1);
            // Along the chain, with a wander across it so the range is not a ruler.
            var at = spine + along * ((t - 0.5f) * 2f * reach) +
                     across * ((random.Next() - 0.5f) * radiusMetres * 0.55f);
            // Tapered toward the ends, so a range has shoulders and a high middle.
            var taper = 0.62f + 0.38f * MathF.Sin(t * MathF.PI);
            Place(
                plan,
                ref random,
                at,
                radiusMetres * (0.85f + random.Next() * 0.35f),
                amplitudeMetres * taper * (0.85f + random.Next() * 0.35f),
                // Stretched along the chain, so each member is a length of ridge.
                1.5f + random.Next() * 1.4f,
                MathF.Atan2(along.Y, along.X),
                half);
        }

        // Foothills, on the range's own side and lower.
        var foothills = 1 + members / 2;
        for (var i = 0; i < foothills; i++)
        {
            var at = spine + along * ((random.Next() - 0.5f) * 2.4f * reach) +
                     across * (-plainward * radiusMetres * (0.9f + random.Next() * 1.3f));
            Place(
                plan,
                ref random,
                at,
                radiusMetres * (0.55f + random.Next() * 0.35f),
                amplitudeMetres * (0.30f + random.Next() * 0.35f),
                1f + random.Next() * 1.6f,
                random.Next() * MathF.Tau,
                half);
        }

        // And a knoll or two out in the open, well clear of the range so the plain stays a plain.
        var knolls = 1 + (int)(random.Next() * 2f);
        for (var i = 0; i < knolls; i++)
        {
            var outward = plainward * (radiusMetres * 2.2f + random.Next() * extentMeters * 0.22f);
            var at = spine + across * outward +
                     along * ((random.Next() - 0.5f) * extentMeters * 0.55f);
            Place(
                plan,
                ref random,
                at,
                radiusMetres * (0.40f + random.Next() * 0.30f),
                amplitudeMetres * (0.40f + random.Next() * 0.40f),
                1f + random.Next() * 0.9f,
                random.Next() * MathF.Tau,
                half);
        }

        return plan;
    }

    /// <summary>
    /// Places one landform if it fits, spending the grade budget and refusing to swallow its neighbours.
    /// </summary>
    /// <remarks>
    /// Everything a landform has to be true about lives here rather than at each of the three call sites
    /// above: it may not close ground, it must fit inside the map, and it must not merge into the landform
    /// beside it so completely that the two stop being two. The composition decides <em>where</em> things
    /// go; this decides whether what was asked for is something this map can contain.
    /// </remarks>
    private static void Place(
        ReliefPlan plan,
        ref Deterministic random,
        Vector2 at,
        float radius,
        float height,
        float stretch,
        float bearing,
        float half)
    {
        // How fingered the outline is. Never zero, because a circular hill is the tell.
        var lobing = 0.14f + random.Next() * 0.22f;
        // <b>The steepest this landform is allowed to get anywhere, and it is a budget rather than a
        // taste.</b> §54: relief is a rate and not a gate, so a generated landform may not close ground.
        // Discounted for the radial bite the lobing takes out of the flank and for the tangential
        // steepening it adds along the rim — the second of which cost two rounds of measurement to find.
        // <b>Steeper than it was, because gentle was reading as "slightly raised ground" rather than as
        // hills.</b> The budget always allowed up to a 0.42 grade before anything closes; the first pass
        // spent 0.20 to 0.42 of it and mostly landed low, which over a hundred-metre radius is a swell.
        // 0.26 to 0.42 is a hillside somebody notices walking up it.
        var steepestAllowed = 0.26f + random.Next() * 0.16f;
        var lobeRate = lobing * (0.62f * 2f + 0.38f * 3f);
        var grade = steepestAllowed * (1f - lobing) / MathF.Sqrt(1f + lobeRate * lobeRate);

        // Whatever is built has to fit inside the map: a landform whose flank is cut off by the edge of the
        // world is a wall of terrain where a hillside should be.
        var toEdge = half - MathF.Max(MathF.Abs(at.X), MathF.Abs(at.Y));
        if (toEdge < radius * 0.30f) return;

        // A tenth rather than a fifth, so a landform can come to a top instead of a plateau. A broad flat
        // summit is what made these read as mesas — and as the moon, since a flat top ringed by an even
        // flank is a crater rim seen from the other side.
        var summit = MathF.Max(radius * 0.10f, radius - height / grade);
        var footprint = (summit + height / (grade * 0.88f)) * (1f + lobing);
        if (footprint > toEdge)
        {
            height *= toEdge / footprint;
            if (height < 1.5f) return;
            summit = MathF.Max(radius * 0.10f, radius - height / grade);
        }

        // Neighbours may share a skirt — that is what makes a range — but not a summit, or two landforms
        // become one shapeless mass and the saddle between them is lost.
        foreach (var placed in plan.landforms)
        {
            if (Vector2.Distance(placed.Centre, at) >= (placed.TopRadius + summit) * 1.15f + 8f) continue;
            return;
        }

        plan.landforms.Add(new Landform(
            at, summit, grade, height, stretch, bearing, lobing, random.Next()));
    }

    /// <summary>
    /// Ground height at a position: the tilt, the landforms, and the undulation under both.
    /// </summary>
    /// <remarks>
    /// <b>The undulation is what stops a plain being a table, and its amplitude is chosen from its
    /// slope rather than from how it looks.</b> A sine of amplitude <c>A</c> and wavelength <c>λ</c> has a
    /// peak grade of <c>2πA/λ</c>, so at the 92 m wavelength used here every metre of amplitude is worth
    /// about 0.07 of grade — which is a real share of the budget landforms are already spending, and the
    /// reason the first version of this (fourteen per cent of a landform's height, so nearly three metres
    /// on a tall one) contributed a fifth of a grade nobody had accounted for.
    /// <para>
    /// Capped at a metre and a half for that reason, and applied to the whole map rather than to the
    /// landforms, because the ground between hills is the part that most needs to not look computed.
    /// </para>
    /// </remarks>
    public float HeightAt(Vector2 position)
    {
        // <b>Two octaves, and the second is what stops a landform reading as a solid of revolution.</b>
        // Reported as looking more like the moon's surface than the earth's, which is exactly what a smooth
        // radial shape is: the moon has no water, so its hills are un-eroded and radially symmetric, and
        // ours were too. What breaks that on earth is drainage — water running off a hill cuts gullies and
        // leaves spurs between them, at a scale of a few tens of metres.
        //
        // A second octave at forty-five metres does that without modelling any of it, and it is the right
        // layer for it: at the landform's own scale it would move the whole shape, and below §54's sixty
        // metre line it would be dressing. The amplitudes are chosen from their slopes rather than their
        // looks — 2πA/λ each — so the two together spend about a quarter of a grade and the flanks are
        // budgeted knowing it.

        // <b>Landforms combine by taking the higher, not by adding.</b> Adding them is the obvious reading
        // of "a sum of shapes" and it is the last of the three things that kept closing ground: placement
        // permits neighbours to overlap by nearly half a radius, and where two flanks overlap their
        // <em>gradients</em> add, so two hills budgeted at a third of a grade each meet at two thirds. That
        // survived two rounds of tightening the budget because the budget was per landform and the breach
        // was between them.
        //
        // Taking the higher bounds the gradient by the steepest single landform, which is what the budget
        // was always meant to guarantee. It is also the better shape: two overlapping hills become a range
        // with a saddle between them rather than one taller hill where they meet, and a range with saddles
        // in it is what high ground actually looks like — and what gives an approach a pass to come through.
        //
        // Softened, because a plain maximum creases where two flanks cross and a crease is a line of steep
        // ground exactly where the saddle should be gentlest.
        //
        // <b>And the softening has to have compact support, which the obvious form does not.</b> The first
        // version was log-sum-exp normalised to be exact where the two are equal — and that is wrong at the
        // other end: it subtracts a constant wherever one term dominates, so every landform contributing
        // <em>nothing</em> at a position still shifted the total by 1.31 m, chaining to nearly eight metres
        // over six of them and switching on across the two metres the blend acts over. Measured as a
        // steepest grade of <b>4.91</b> on a map whose steepest flank was 0.33: a cliff assembled entirely
        // out of a normalisation constant, and three rounds of tightening the flank budget could never have
        // found it because it was not in the flanks.
        var height = -Vector2.Dot(position, Downhill) * Tilt;
        var ground = 0f;
        foreach (var landform in landforms) ground = SmoothMax(ground, landform.HeightAt(position));
        height += ground;
        if (Undulation <= 0f) return height;
        height += (Landform.Undulate(position, seed, 92f) * 2f - 1f) * Undulation;
        return height + (Landform.Undulate(position, seed * 2654435761u + 17u, 45f) * 2f - 1f) *
               Undulation * 0.45f;
    }

    /// <summary>
    /// The higher of two heights, with the crease between them rounded off.
    /// </summary>
    /// <remarks>
    /// Exactly the maximum once the two differ by more than the blend, which is the property that matters:
    /// a landform far from here must not be able to change the ground here at all.
    /// <para>
    /// <b>And flat where the two are equal, which the obvious quadratic is not.</b> The rounding is a
    /// function of <c>|first - second|</c>, and an absolute value has a corner at zero — so any rounding
    /// whose slope is non-zero there inherits the corner and lays a crease along the exact line where two
    /// landforms meet. Which is the worst possible place for one: that line is the saddle, the way through
    /// between two hills, and a crease there is a ridge across the pass.
    /// </para>
    /// <para>
    /// <c>(1 - t²)²</c> is flat at both ends — zero slope at <c>t = 0</c> kills the corner, zero slope and
    /// zero value at <c>t = 1</c> keeps the compact support — so the saddle comes out smooth in the height
    /// <em>and</em> in the slope. It is still worth a quarter of the blend where the two are equal, so a
    /// saddle is gentler than the flanks that form it rather than steeper, which is what a saddle is.
    /// </para>
    /// </remarks>
    private static float SmoothMax(float first, float second)
    {
        const float blend = 1.6f;
        var high = MathF.Max(first, second);
        var gap = MathF.Abs(first - second);
        if (gap >= blend) return high;
        var t = gap / blend;
        var bump = 1f - t * t;
        return high + blend * 0.25f * bump * bump;
    }

    /// <summary>Metres of broad undulation over the whole map, above and below whatever else is there.</summary>
    public float Undulation { get; private init; }

    private uint seed;

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
          $"{SteepestGrade:F2} grade against a limit of {TerrainMap.MaximumTraversableGrade:F2}, " +
          $"undulation {Undulation:F1} m, fall {Tilt * 100f:F2} m per 100 m";

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

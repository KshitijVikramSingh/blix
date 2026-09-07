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
    /// Beyond this from the centre, this landform contributes nothing. A bound, not a size.
    /// </summary>
    /// <remarks>
    /// <b>Lives here so it cannot drift from the shape it bounds, which it did within the hour.</b> The first
    /// version of the culling test used <c>BaseRadius × Stretch</c> and clipped every hill: the lobing
    /// modulates the radius by up to <c>1 + Lobing</c>, so the outline reaches further out than the base
    /// radius wherever a lobe points. It showed up as the map's measured height range dropping from 77 m to
    /// 69 — a shape being quietly trimmed, which is exactly the kind of thing a bound kept in a different file
    /// from the shape gets wrong.
    /// <para>
    /// Deliberately generous: <see cref="HeightAt"/> divides the along-axis by <see cref="Stretch"/> and
    /// leaves the across-axis alone, so the true footprint is an ellipse and this is the circle around it. An
    /// over-estimate costs an evaluation that returns zero.
    /// </para>
    /// </remarks>
    public float SupportRadius => BaseRadius * (1f + Lobing) * MathF.Max(1f, Stretch);

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
    /// One octave is all that is wanted here, since §54 gives everything below sixty metres to the dressing
    /// layer and a second octave would be exactly that. The implementation moved to
    /// <see cref="LatticeNoise"/> when the dressing layer needed the same lattice to warp a colour
    /// boundary — see the note there on why sharing the arithmetic does not share the responsibility.
    /// </remarks>
    private static float Noise(Vector2 at) => LatticeNoise.Value(at);
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

    /// <summary>
    /// Metres of fall across the whole map, which is all the tilt there is.
    /// </summary>
    /// <remarks>
    /// <b>Absolute, and it used to scale with the amplitude — which made every map the same tilted plane.</b>
    /// The old form was <c>amplitude / extent × 0.5</c>, and its comment claimed "half a metre of fall per
    /// hundred, far too little to notice on foot". That was true of the amplitude it was written against.
    /// At the amplitudes actually in use it came to 2.3 m per hundred: <b>fourteen metres of fall on a
    /// thirty-eight metre relief</b>, so a third of every map's entire range was one global ramp, in the same
    /// direction, underneath whatever the archetype was trying to say.
    /// <para>
    /// Found by printing the eight archetypes as bare height and noticing they shared a background. Not by
    /// reading the code, where the constant looks small and the comment agrees with it — the comment was right
    /// about a number the expression no longer produced.
    /// </para>
    /// <para>
    /// Three and a half metres end to end. Enough to give water a direction, which is the only thing tilt is
    /// for, and genuinely too little to see.
    /// </para>
    /// </remarks>
    private const float TiltFallMetres = 3.5f;


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
    /// <summary>
    /// Realises a topological statement as ground.
    /// </summary>
    /// <remarks>
    /// <b>Three kinds of separator, three different mechanisms, and the split is not arbitrary.</b>
    /// <list type="bullet">
    /// <item><b>A ridge is a heightfield along its spine</b> — see <see cref="Crest"/>. It used to be a chain
    /// of overlapping stretched landforms, which inherited the grade budget and the soft-maximum composition
    /// for free and was still wrong: however carefully the blend is tuned, a mountain assembled out of eleven
    /// mounds is a row of mounds, because each one brings its own summit, its own lobing and its own radial
    /// flank and the eye keeps finding the primitive.</item>
    /// <item><b>An escarpment becomes a lattice step</b>, because it is one-sided and a landform is radial.
    /// High on one flank and low on the other is a signed distance to a line, not a sum of hills, and trying
    /// to build it out of hills is how you get a ridge with a plain on both sides.</item>
    /// <item><b>A river becomes a trough and an inflow</b>, carved on the lattice before erosion so that
    /// erosion deepens a valley that is already there rather than inventing one somewhere else.</item>
    /// </list>
    /// <para>
    /// The fall direction is taken from the river when there is one, so the water runs the way the map leans
    /// instead of fighting it. Without a river it is chosen from the seed as before.
    /// </para>
    /// </remarks>
    public static ReliefPlan FromLayout(MapLayout layout, float extentMetres, uint seed)
    {
        ArgumentNullException.ThrowIfNull(layout);
        var random = new Deterministic(seed);
        var river = layout.River();
        var fall = river is { } water && water.Path.Length >= 2
            ? Vector2.Normalize(water.Path[^1] - water.Path[0])
            : Deterministic.OnACircle(random.Next());

        var plan = new ReliefPlan
        {
            seed = seed,
            Amplitude = layout.AmplitudeMetres,
            Undulation = MathF.Min(2.0f, layout.AmplitudeMetres * 0.13f),
            Tilt = TiltFallMetres / MathF.Max(1f, extentMetres),
            Downhill = fall,
            Layout = layout,
        };

        // <b>No landforms. A ridge is a heightfield now — see <see cref="Crest"/>.</b>
        return plan;
    }

    /// <summary>A point a fraction of the way along a polyline, and the direction it is heading.</summary>
    private static Vector2 Along(Vector2[] path, float t, out Vector2 tangent)
    {
        tangent = Vector2.UnitX;
        if (path.Length == 0) return Vector2.Zero;
        if (path.Length == 1) return path[0];

        var total = 0f;
        for (var i = 1; i < path.Length; i++) total += Vector2.Distance(path[i - 1], path[i]);
        var want = Math.Clamp(t, 0f, 1f) * total;
        var walked = 0f;
        for (var i = 1; i < path.Length; i++)
        {
            var span = Vector2.Distance(path[i - 1], path[i]);
            if (span <= 1e-4f) continue;
            if (walked + span >= want || i == path.Length - 1)
            {
                var local = Math.Clamp((want - walked) / span, 0f, 1f);
                tangent = Vector2.Normalize(path[i] - path[i - 1]);
                return Vector2.Lerp(path[i - 1], path[i], local);
            }

            walked += span;
        }

        tangent = Vector2.Normalize(path[^1] - path[^2]);
        return path[^1];
    }

    /// <summary>The lattice cells a polyline can reach, as an inclusive box.</summary>
    private static ((int X, int Z) From, (int X, int Z) To) Band(
        Vector2[] path,
        Vector2 origin,
        int side,
        float reachMetres)
    {
        var low = new Vector2(float.MaxValue);
        var high = new Vector2(float.MinValue);
        foreach (var point in path)
        {
            low = Vector2.Min(low, point);
            high = Vector2.Max(high, point);
        }

        low -= new Vector2(reachMetres);
        high += new Vector2(reachMetres);
        var from = (low - origin) / DrainageCellMetres;
        var to = (high - origin) / DrainageCellMetres;
        return (
            (Math.Clamp((int)MathF.Floor(from.X), 0, side - 1), Math.Clamp((int)MathF.Floor(from.Y), 0, side - 1)),
            (Math.Clamp((int)MathF.Ceiling(to.X), 0, side - 1), Math.Clamp((int)MathF.Ceiling(to.Y), 0, side - 1)));
    }

    /// <summary>Distance from a point to a polyline, and which side of it the point is on.</summary>
    private static float ToPath(Vector2 at, Vector2[] path, out float side)
    {
        side = 0f;
        if (path.Length < 2) return float.MaxValue;
        var best = float.MaxValue;
        for (var i = 1; i < path.Length; i++)
        {
            var from = path[i - 1];
            var to = path[i];
            var span = to - from;
            var lengthSquared = span.LengthSquared();
            if (lengthSquared <= 1e-6f) continue;
            var t = Math.Clamp(Vector2.Dot(at - from, span) / lengthSquared, 0f, 1f);
            var nearest = from + span * t;
            var away = Vector2.Distance(at, nearest);
            if (away >= best) continue;
            best = away;
            // Cross product's sign: which hand of the path this is on.
            side = span.X * (at.Y - from.Y) - span.Y * (at.X - from.X) >= 0f ? 1f : -1f;
        }

        return best;
    }

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
            Tilt = TiltFallMetres / MathF.Max(1f, extentMeters),
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
        foreach (var landform in landforms)
        {
            // <b>A landform has compact support, so most of them are not near most positions.</b> Evaluating
            // all of them everywhere was fine when a map held six; a canvas filled with statements holds
            // twenty-six, and this is called two hundred thousand times to build the lattice. The test is two
            // multiplies against a shape whose own evaluation does trigonometry for its lobing.
            //
            // The bound lives on the landform, because a bound kept apart from the shape it bounds gets it
            // wrong — see the remarks on SupportRadius for the version of this that clipped every hill.
            var reach = landform.SupportRadius;
            if (Vector2.DistanceSquared(position, landform.Centre) > reach * reach) continue;
            ground = SmoothMax(ground, landform.HeightAt(position));
        }
        // <b>No connector suppression here, and the reason is that this path no longer carries a layout's
        // relief.</b> Landforms are the legacy composition — <see cref="For"/> — and a layout's ridges are
        // heightfields now, so for any plan built from a layout this loop sums nothing. Applying an opening to
        // it was suppressing a zero.
        height += ground;
        if (Undulation <= 0f) return height;
        // <b>The forty-five metre octave is gone, and this is what replaced it.</b> It existed to fake
        // gullies — its own comment said so: "what breaks that on earth is drainage". <see cref="Erosion"/>
        // now cuts them for real, and the two cannot both run: noise roughness at the scale erosion works
        // at gives the flow solver a hillside full of pits to fill, so the fake was actively spending the
        // real one's fidelity. One broad octave remains, which is undulation rather than drainage — it is
        // the map having a shape at all before water gets to it.
        return height + (Landform.Undulate(position, seed, 92f) * 2f - 1f) * Undulation;
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
        // <b>Nothing softened against nothing is nothing, and it was 0.4 m.</b> These remarks already claim
        // the property that matters — "a landform far from here must not be able to change the ground here at
        // all" — and the compact support delivers it for a landform that is *far*. It did not deliver it for a
        // landform that is merely absent: with both terms at zero the gap is zero, so the rounding fired at
        // full strength and returned a quarter of the blend out of nothing. Chained over the landforms of a
        // filled canvas that is a spurious floor under the whole map.
        //
        // Found by accident, and only because spatial culling stopped calling this for landforms that
        // contribute zero — which changed the measured height range by eight metres and looked at first like
        // the culling clipping hills. It is the same failure this function's own history records for
        // log-sum-exp, in a smaller costume: a softening that is not exactly the identity where one side
        // contributes nothing.
        if (first <= 0f || second <= 0f) return MathF.Max(first, second);
        var high = MathF.Max(first, second);
        var gap = MathF.Abs(first - second);
        if (gap >= blend) return high;
        var t = gap / blend;
        var bump = 1f - t * t;
        return high + blend * 0.25f * bump * bump;
    }

    /// <summary>Metres of broad undulation over the whole map, above and below whatever else is there.</summary>
    public float Undulation { get; private init; }

    /// <summary>The statement this ground realises, or null for a plan composed the old way.</summary>
    public MapLayout? Layout { get; private init; }

    /// <summary>Which climate is realising this plan. Read for how much water its statements carry.</summary>
    private Region region = Region.Downland;

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

            // <b>It looked at landforms only, and everything not made of landforms was being flattened.</b>
            // An escarpment is cut straight onto the lattice by <see cref="Shelve"/> and a basin by
            // <see cref="Sink"/>; neither places a landform. So a layout whose separators are all escarpments
            // had no landforms at all, this returned the map's bare tilt — about one per cent — and
            // <see cref="GradeLimit"/> then held the whole interior to one per cent. Measured: a pinned
            // Escarpment canvas described itself as "flat", and its entire eighty-nine metres of range was the
            // frontier, which is exempt.
            //
            // The lesson is the one this file keeps relearning in new costumes: a budget derived from a subset
            // of the things that shape the ground is not a budget, it is a cap on the subset it knows about.
            if (Layout is { } layout)
            {
                foreach (var separator in layout.Separators)
                {
                    if (separator.Kind != SeparatorKind.Escarpment) continue;
                    // The face's own grade: its full height over the width it falls across.
                    steepest = MathF.Max(
                        steepest,
                        separator.HeightMetres / MathF.Max(1f, separator.WidthMetres));
                }

                foreach (var separator in layout.Separators)
                {
                    if (separator.Kind != SeparatorKind.Ridge) continue;
                    // The steep flank: full height across a little over half the width.
                    steepest = MathF.Max(
                        steepest,
                        separator.HeightMetres / MathF.Max(1f, separator.WidthMetres * 0.55f));
                }

                foreach (var upland in layout.Uplands)
                {
                    // Its shoulder falls the full height across the outer half of its radius.
                    // Across the grain is the steep way, and stretching does not change it: the across-axis
                    // is left at the radius precisely so the width means the width.
                    steepest = MathF.Max(
                        steepest,
                        upland.HeightMetres * 1.3f / MathF.Max(1f, upland.RadiusMetres * 0.55f));
                }

                foreach (var trough in layout.Troughs)
                {
                    // A trough's banks are as steep as its depth over its half-width, and they have to be
                    // allowed to exist or the limiter fills the valley back in.
                    steepest = MathF.Max(
                        steepest,
                        trough.DepthMetres / MathF.Max(1f, trough.WidthMetres * 0.5f));
                }

                foreach (var basin in layout.Basins)
                {
                    // A raised cosine's steepest point is pi/2 times its average, and the dam behind it is
                    // steeper still — both have to be allowed to exist or the lake is limited flat.
                    steepest = MathF.Max(
                        steepest,
                        basin.DepthMetres * 1.6f / MathF.Max(1f, basin.RadiusMetres * 0.34f));
                }
            }

            return steepest;
        }
    }

    /// <summary>The lattice erosion is solved on, in metres.</summary>
    /// <remarks>
    /// Four metres. The navigation grid is half a metre because that is the resolution a body's clearance is
    /// decided at, and solving drainage there would be 1.44M cells to answer questions about valleys that
    /// are tens of metres across — times the pass count, which is the part that matters, because erosion
    /// solves the whole flow field once per pass. Four metres resolves an eight-metre feature, which is
    /// under §54's sixty-metre line by enough margin to be sure the generation layer is not being asked to
    /// own dressing.
    /// </remarks>
    public const float DrainageCellMetres = 4f;

    /// <summary>How many cut-and-lift rounds the land goes through.</summary>
    /// <remarks>
    /// Enough that channels compete and capture each other rather than merely deepening in place, which is
    /// what turns a set of independent gullies into a network with divides between them. This is the dial
    /// that decides how mature the landscape reads.
    /// </remarks>
    /// <remarks>
    /// <b>Twelve, down from forty-eight, and the change came with the archetype layer.</b> Forty-eight passes
    /// is a shape-<em>maker</em>: run that long and any input converges on the same mature dendritic texture,
    /// which was the right choice while erosion was the only thing deciding what the map looked like. It is
    /// the wrong choice once the shape is authored, because it homogenises the statement it was handed —
    /// two ridges and a saddle put through fifty rounds of stream power come out as generic hill country.
    /// <para>
    /// A finisher instead. Enough for channels to organise, for valley floors to go concave and hilltops
    /// convex, and for the ridges to grow spurs; not enough to forget what it was given.
    /// </para>
    /// </remarks>
    public static int ErosionPasses { get; set; } = 12;

    /// <summary>
    /// How far in from the edge the map's frontier reaches, in metres.
    /// </summary>
    /// <remarks>
    /// <b>The map already ended in a wall; this gives the wall a reason.</b> The relief sweep reports "599
    /// closed, of which 599 are off the map edge", and the comment beside that count says what it is:
    /// "being outside the map is the other way to fail, and a body's whole outline has to fit". So the
    /// boundary of the world was an invisible line at the extent that a body simply could not cross. High
    /// ground and deep water do the same job while being something a player can see and reason about.
    /// <para>
    /// Seventy-eight metres, which is generation's business by §54's rule — above the sixty-metre line, and
    /// wider than the thirty-six-metre settlement so that a frontier is a place rather than a border.
    /// </para>
    /// </remarks>
    public const float RimWidthMetres = 78f;

    /// <summary>
    /// Whether the map gets a mountain frontier at all.
    /// </summary>
    /// <remarks>
    /// <b>Off, and it should have been questioned much earlier.</b> Seventy-eight metres of frontier on each
    /// side of a 600 m map leaves 444 m of interior, which means the border occupies <b>forty-five per cent of
    /// the map's area</b> — and at 1.55 times the amplitude it is also the tallest thing on it. So every map
    /// was making one overwhelming geographic statement before its archetype got a vote: <em>you live inside a
    /// mountain-rimmed arena</em>. That homogenises everything downstream of it, and it explains a great deal
    /// of "the presets all look the same" that the region layer only partly answered.
    /// <para>
    /// Deleted rather than tuned, which is the right move while the macro geography is being judged: a
    /// dominant feature present on every map cannot be evaluated by comparing maps. The code and its notes
    /// stay, because the problem it solved is real — the map ended in an invisible wall — and the answer it
    /// got wrong is <em>where</em> to put the solution. A playable 600 m inside an 800-900 m rendered extent
    /// puts the scenery outside the gameplay instead of eating half of it.
    /// </para>
    /// </remarks>
    public static bool Frontier { get; set; }

    /// <summary>How high the frontier stands, as a multiple of the map's own relief.</summary>
    private const float RimRelief = 1.55f;

    /// <summary>How far a gate's influence reaches, in metres.</summary>
    private const float GateReachMetres = 62f;

    /// <summary>
    /// The catchment the through-river brings with it, in square metres.
    /// </summary>
    /// <remarks>
    /// <b>Because the map is a fragment of a catchment and not a catchment.</b> Drainage organises at
    /// kilometres; six hundred metres is a tile, and the river crossing a tile was already a river before it
    /// arrived. Enough of it that the trunk dominates every local stream by a factor no hillside here can
    /// reach, which is what drops the local streams below the width anything is drawn at — and so what turns
    /// a fractal of identical valleys into one valley with tributaries.
    /// <para>
    /// <b>An absolute area, and it was written as a multiple of the canvas first, which was wrong twice
    /// over.</b> A river's upstream catchment is a fact about the country it came from; it does not change
    /// because the window you are looking through got wider. Written as a multiple it did, and viewing an
    /// 1800 m canvas poured nine times the water into the same watercourse — which read as the wetness
    /// thresholds having slid with the extent, and sent me to normalise the wetness index instead. That fixed
    /// one symptom and broke the opposite one. The lesson is the older one in this file: when two ends of a
    /// range go wrong together, the fault is upstream of both.
    /// </para>
    /// <para>
    /// <b>Brought down from two tiles to a little over one.</b> Two was chosen when the map was a window on a
    /// canvas and the inflow was the only thing making a river look inherited. It is also an <em>erosion</em>
    /// input: stream power goes as the square root of catchment, so doubling the inflow makes the trunk cut
    /// about forty per cent harder than everything else on the map, every pass. Which is how a river ends up
    /// being the only thing a map is about.
    /// </para>
    /// </remarks>
    private const float InheritedCatchmentMetres2 = 1.1f * 600f * 600f;

    /// <summary>Writes this land into a terrain map's height field.</summary>
    /// <remarks>
    /// <b>The shape is no longer what <see cref="HeightAt"/> returns.</b> The analytic field is now an
    /// uplift pattern: it decides where the high ground is, and erosion decides what high ground looks like.
    /// Four steps, in this order and for these reasons:
    /// <list type="number">
    /// <item>Sample the analytic field onto the four-metre lattice.</item>
    /// <item>Erode it, which is where the valleys and the ridges between them come from.</item>
    /// <item><b>Re-establish the grade budget, which erosion does not respect.</b>
    /// <see cref="SteepestGrade"/> is computed from the shapes, and it was a sound guarantee while the
    /// shapes <em>were</em> the ground. Erosion steepens valley sides and headwaters by construction — that
    /// is the mechanism, not a side effect — so the budget has to become a property of the finished surface
    /// instead of of the plan. Everything downstream is calibrated against it: the walkable-rectangle
    /// merge, the closed-cell count, the climb charge per edge.</item>
    /// <item>Upsample to the navigation grid. Bilinear, which cannot invent a slope steeper than the
    /// lattice's own — so limiting the grade on the lattice bounds it on the fine grid too.</item>
    /// </list>
    /// The drainage of the final surface is kept on the map, because it is what tells the rest of the game
    /// where the water is. It is derived, so it is neither saved nor fingerprinted: solving it again from
    /// the same heights gives the same answer on any machine, which is the whole reason
    /// <see cref="Drainage"/> is written the way it is.
    /// </remarks>
    /// <summary>
    /// Whether the ground is built around an authored drainage network rather than eroded into shape.
    /// </summary>
    /// <remarks>
    /// §152, stage B of §150. A switch and not a replacement, because the two have to be measurable on the
    /// same seeds in the same binary or the comparison is between two thermal states of a laptop — §84's
    /// rule, and it holds for a generator as much as for a frame time.
    /// </remarks>
    /// <remarks>
    /// <b>The default from §163, once the ledger let it be.</b> It scores 38 criteria shortfalls against the
    /// eroded generator's 50 and is the model that matches how water works — a valley exists because a river
    /// does. §159 held it back because the two-settlement economy broke conservation on its terrain, which
    /// turned out to be a latent swap in the deposit path that this terrain merely reached first. The old
    /// path stays behind <c>--eroded</c> for as long as there is a comparison worth making.
    /// </remarks>
    public bool DrainageFirst { get; set; } = true;

    /// <summary>
    /// The drainage-first generator's shape, when something has an opinion about it.
    /// </summary>
    /// <remarks>
    /// §154. Null means "derive it from the amplitude as before", so nothing that does not ask is affected —
    /// the sweep and the gate keep the figures §152 measured. The map builder asks, because that is where a
    /// generator gets tuned and every constant in here was previously set by a rebuild.
    /// </remarks>
    public float? ChannelFallPer100M { get; set; }

    public float? ValleyFlank { get; set; }

    public float? ValleyShoulderMetres { get; set; }

    public int? Tributaries { get; set; }



    public void Apply(TerrainMap terrain)
    {
        region = terrain.Region;
        var transform = terrain.Transform;
        var heights = new float[(transform.Width + 1) * (transform.Height + 1)];

        if (landforms.Count == 0 && Undulation <= 0f && Tilt <= 0f)
        {
            // A flat map stays exactly flat, and gets no drainage. Every scenario is calibrated on it, so
            // "no relief" has to mean no relief at all rather than the erosion of nothing.
            terrain.ReplaceHeights(heights);
            terrain.SetDrainage(null);
            return;
        }

        var extent = MathF.Max(
            transform.Width * transform.CellSize,
            transform.Height * transform.CellSize);
        var side = Math.Max(8, (int)MathF.Ceiling(extent / DrainageCellMetres) + 1);
        var lattice = new float[side * side];
        if (DrainageFirst)
        {
            // <b>The whole of the inversion is this branch.</b> §152: the ground comes out of an authored
            // network instead of the network being looked for in the ground. Everything the other path does
            // after its own shaping — the grade limit, the solve, the write into the terrain — is shared, so
            // the two are comparable on the same seed in the same binary. §84's rule.
            PlantRivers(lattice, side, transform.Origin, extent, out var headwaterInflow);
            Finish(terrain, transform, heights, lattice, side, headwaterInflow);
            return;
        }

        for (var z = 0; z < side; z++)
        for (var x = 0; x < side; x++)
        {
            var at = transform.Origin + new Vector2(x, z) * DrainageCellMetres;
            lattice[z * side + x] = HeightAt(at);
        }

        // <b>Gates before the rim, and from the un-rimmed field, because a river leaves at the lowest point
        // there is.</b> Choosing them afterwards would be choosing them from ground the rim had already
        // raised, and a gate cut through the highest part of a frontier is a gate no water reaches. It also
        // avoids the failure this arrangement is prone to: if every outlet sits above much of the interior,
        // depression filling floods everything below the lowest sill and the map becomes a lake.
        // Escarpments and river troughs are lattice work, and they come before the frontier so that a river
        // leaving the map decides where its own gate is rather than being handed one.
        Shelve(lattice, side, transform.Origin, extent);
        // <b>Before erosion, because a trough drains.</b> Open negatives go in first so erosion deepens them
        // and hangs its own tributaries off them; closed negatives — see Sink — have to wait until erosion has
        // finished filling things in. That split is the whole rule.
        // Uplands before troughs, because a trough is cut *into* whatever is there and the order is the
        // difference between a valley in a massif and a valley that was filled in behind you.
        Raise(lattice, side, transform.Origin);
        Crest(lattice, side, transform.Origin);
        Excavate(lattice, side, transform.Origin);
        var inflow = new float[lattice.Length];
        var carved = Carve(lattice, side, transform.Origin, extent, inflow);

        var (entry, exit) = carved ?? FindCrossing(lattice, side, extent);
        if (Frontier) AddRim(lattice, side, transform.Origin, extent, new[] { entry, exit });

        // <b>Eight hundredths, not nine tenths.</b> The old figure only ever worked because the no-pit clamp
        // was catching it — at 0.9 the wettest cell tries to remove most of the map's relief in a single pass,
        // so what shaped the channel was the clamp rather than the law. Small enough that the clamp rarely
        // binds, the profile is the one stream power actually predicts, and forty-eight passes still cut deep.
        Erosion.Carve(lattice, side, DrainageCellMetres, ErosionPasses, 0.08f, inflow);

        // <b>The budget applies to the interior and deliberately not to the frontier.</b> The grade budget
        // exists so the ground a settlement lives on stays connected; a frontier's whole job is to not be
        // connected. Limiting it would cap it at a walkable slope and turn a mountain wall into a hill, so
        // the rim band is exempt — and that exemption is what makes the classifier's crag test work without
        // a special case, since the interior cannot reach a crag grade by construction.
        // <b>Basins go in after erosion, and that ordering is the whole reason they survive.</b> Erosion is a
        // basin-destroying process — filling hollows is the first thing its flow router does and incising
        // their lips is what its stream power does next — so a lake cut before it is a lake erosion spends
        // twelve passes removing. Cut afterwards, the only thing that touches it is the grade limiter, which
        // merely eases its shores.
        Sink(lattice, side, transform.Origin, extent);

        // <b>Normalised to the amplitude that was asked for, because the primitives add.</b> Uplands, ridges
        // and spurs each contribute their own lift, and where they overlap the lifts sum — so a layout with a
        // massif and a ridge system on it came out at 92.9 m from a 34 m amplitude. Erosion's own rescale
        // preserves whatever relief it was handed, which is right for erosion and no use here.
        //
        // Worth more than the tidiness: amplitude is a number a person sets, and it has spent this whole
        // session not meaning what it says — first because the tilt added half again, now because the shaping
        // primitives compound. A dial that does not mean its own units cannot be tuned, only fiddled with.
        Normalise(lattice, Amplitude);

        // <b>After the normalisation, because the sea is a datum and a datum cannot be rescaled.</b> Everything
        // above shapes the land relative to itself; this puts an absolute floor under it, and rescaling
        // afterwards would move the sea and leave the coast somewhere else.
        var seaLevel = Coastline(lattice, side, transform.Origin);

        var ceiling = MathF.Min(SteepestGrade, TerrainMap.MaximumTraversableGrade * 0.92f);
        var inset = (int)MathF.Ceiling(RimWidthMetres / DrainageCellMetres);
        GradeLimit.Apply(lattice, side, DrainageCellMetres, ceiling, inset);
        // <b>Solve, cut the channels in, solve again.</b> §156. The first solve is asked one question only —
        // how wide is the water here — because width comes from upslope area and that is a property of the
        // uncarved surface. Then the channels are incised into the lattice, and the second solve runs on
        // ground that has a bed in it.
        //
        // <b>Why this was the fault under almost everything.</b> The level rule is
        // <c>max(standing ? filled : ground, ground + channel)</c> — the surface is the bed <em>plus</em> a
        // depth that grows with width. §155 measured the consequence: 87% of the seven thousand uphill
        // reaches had a falling bed and a deepening channel, because downstream the depth grows faster than
        // the bed falls on gentle ground. Water stacked on top of the ground climbs whenever it widens, and
        // it also sits above the land beside it — which is "streams creep upstairs" from the first water
        // report, the same defect seen from the chair.
        //
        // Cut in, the same term works the other way: downstream is deeper, so the surface sits <em>lower</em>
        // against its banks the further it goes. The growth that used to fight the gradient now helps it.
        var first = Drainage.Solve(lattice, side, DrainageCellMetres, inflow);
        // <b>Gated on real flow, sized on the width the level rule will use.</b> §158, and the distinction
        // is the whole of why §156's attempt failed. Carving wherever the layout drew a corridor invented low
        // paths the solver's own accumulation never justified — 6,278 uphill reaches became 7,623 and the
        // ratchet caught it. But sizing the depth from area alone leaves an authored river cut to a fraction
        // of the depth its level will claim. So: only cells that actually drain something are carved, and how
        // deep is decided by WidthAt, which is what EnsureLevel reads.
        first.Origin = transform.Origin;
        first.SetWaterScale(RegionProfile.For(region).WaterScale);
        first.PaintAuthoredWidth(AuthoredWidths(side, transform.Origin));
        Incise(lattice, side, transform.Origin, first);

        var drainage = Drainage.Solve(lattice, side, DrainageCellMetres, inflow);
        drainage.Origin = transform.Origin;
        drainage.SetSeaLevel(seaLevel);
        drainage.SetWaterScale(RegionProfile.For(region).WaterScale);
        drainage.PaintAuthoredWidth(AuthoredWidths(side, transform.Origin));

        for (var z = 0; z <= transform.Height; z++)
        for (var x = 0; x <= transform.Width; x++)
        {
            var local = new Vector2(x, z) * transform.CellSize;
            heights[z * (transform.Width + 1) + x] = drainage.Sample(lattice, local);
        }

        terrain.ReplaceHeights(heights);
        terrain.SetDrainage(drainage);
        terrain.SetLayout(Layout);
    }

    /// <summary>
    /// Builds the ground around an authored drainage network. §152.
    /// </summary>
    /// <remarks>
    /// Height at a point is <b>the height of the lowest water near it, plus a rise for how far away that
    /// water is</b> — which is what a valley is. Two consequences fall out for free and they are the two
    /// things §151 measured the old generator failing:
    /// <list type="bullet">
    /// <item>Every channel runs downhill, because a reach's height is its distance from the outlet along its
    /// own course and the ground takes its floor from the reach.</item>
    /// <item>Water is confined, because the ground rises away from it in every direction by construction.
    /// There is nowhere for a sheet to lie on a hillside, because the hillside <em>is</em> the rise.</item>
    /// </list>
    /// <para>
    /// The valley rise is capped at the traversable grade rather than at a look: ground people cannot cross
    /// is the fault §151 counted sixteen maps of, and a generator that can produce it will. Interfluves —
    /// the ground between valleys — keep the authored vocabulary's undulation, scaled by distance from water
    /// so it can roughen a watershed without ever damming a channel.
    /// </para>
    /// </remarks>
    private void PlantRivers(float[] lattice, int side, Vector2 origin, float extent, out float[] inflow)
    {
        // Fall is an input here rather than an outcome. Two and a half metres per hundred is above §151's
        // floor of two, which forty-six of fifty-five old maps were under.
        var fallPerMetre = ChannelFallPer100M is { } asked
            ? asked / 100f
            : MathF.Max(0.025f, Amplitude * 0.45f / MathF.Max(1f, extent));
        var branches = Tributaries ?? 4 + (int)MathF.Round(MathF.Min(Amplitude, 60f) / 12f);
        var network = RiverNetwork.Grow(extent, seed, fallPerMetre, branches);

        // What the valley sides may do. Capped below the traversable limit with room to spare, because the
        // grade the navigation raster measures is over a cell and a half and this is over four metres.
        // Capped against the traversable limit whatever was asked for: a dial that can generate ground nobody
        // can cross is a dial that will, and §151 counted sixteen maps of exactly that.
        var flank = MathF.Min(
            ValleyFlank ?? 0.30f + MathF.Min(Amplitude, 60f) / 60f * 0.28f,
            TerrainMap.MaximumTraversableGrade * 0.62f);
        var shoulder = MathF.Max(2f, ValleyShoulderMetres ?? MathF.Max(6f, Amplitude * 0.75f));

        for (var z = 0; z < side; z++)
        for (var x = 0; x < side; x++)
        {
            var at = origin + new Vector2(x, z) * DrainageCellMetres;
            // Rising, then levelling off: a valley side is steepest near the water and flattens onto the
            // interfluve, which is what stops the far corners of the map from being mountains.
            var (ground, _, distance, width) = network.Floor(at, flank, shoulder);

            // The vocabulary's own undulation on top, held well off the water so it cannot dam anything.
            var bank = MathF.Max(width * 0.5f, DrainageCellMetres);
            var away = Math.Clamp(
                MathF.Max(0f, distance - bank) / MathF.Max(1f, shoulder * 1.5f), 0f, 1f);
            lattice[z * side + x] = ground + Wrinkle(at) * away;
        }

        inflow = new float[lattice.Length];
        foreach (var reach in network.Reaches)
        {
            // Discharge is handed to the solver at the heads, so what it accumulates is what was authored
            // rather than whatever the lattice happens to collect.
            if (!TryCell(reach.To, origin, side, out var index)) continue;
            inflow[index] = MathF.Max(inflow[index], reach.Discharge * 0.02f);
        }
    }

    /// <summary>The authored undulation at a point, without any of the landform shaping around it.</summary>
    private float Wrinkle(Vector2 at) =>
        Undulation <= 0f ? 0f : Undulation * 0.5f * (Noise(at * 0.021f) + Noise(at * 0.047f) * 0.5f);

    private static float Noise(Vector2 at)
    {
        var x = MathF.Sin(at.X * 1.7f + 2.1f) * MathF.Cos(at.Y * 1.3f - 0.7f);
        var y = MathF.Sin(at.X * 0.6f - 1.1f) * MathF.Cos(at.Y * 0.9f + 1.9f);
        return x * 0.6f + y * 0.4f;
    }

    private static bool TryCell(Vector2 at, Vector2 origin, int side, out int index)
    {
        var local = (at - origin) / DrainageCellMetres;
        var x = (int)MathF.Round(local.X);
        var z = (int)MathF.Round(local.Y);
        index = z * side + x;
        return x >= 0 && z >= 0 && x < side && z < side;
    }

    /// <summary>
    /// The tail both generators share: cap the grades, solve the drainage, write the ground.
    /// </summary>
    /// <remarks>
    /// Extracted rather than copied, so that "the new path differs only in how the lattice was made" is a
    /// fact about the code and not a claim in a commit message.
    /// </remarks>
    private void Finish(
        TerrainMap terrain,
        Spatial.GridTransform transform,
        float[] heights,
        float[] lattice,
        int side,
        float[] inflow)
    {
        var seaLevel = Coastline(lattice, side, transform.Origin);
        // <b>The limiter runs on both paths, and I had switched it off for the new one on a hypothesis that
        // measurement refused.</b> §152: it is a relaxation, so a gentle channel looked like exactly the
        // pattern that would invert under it — and skipping it changed the shortfall count by nothing at all.
        // The tails stay identical, so the comparison between the two generators is about the lattice and
        // nothing else.
        var ceiling = MathF.Min(SteepestGrade, TerrainMap.MaximumTraversableGrade * 0.92f);
        var inset = (int)MathF.Ceiling(RimWidthMetres / DrainageCellMetres);
        GradeLimit.Apply(lattice, side, DrainageCellMetres, ceiling, inset);

        var drainage = Drainage.Solve(lattice, side, DrainageCellMetres, inflow);
        drainage.Origin = transform.Origin;
        drainage.SetSeaLevel(seaLevel);
        drainage.SetWaterScale(RegionProfile.For(region).WaterScale);
        drainage.PaintAuthoredWidth(AuthoredWidths(side, transform.Origin));

        for (var z = 0; z <= transform.Height; z++)
        for (var x = 0; x <= transform.Width; x++)
        {
            var local = new Vector2(x, z) * transform.CellSize;
            heights[z * (transform.Width + 1) + x] = drainage.Sample(lattice, local);
        }

        terrain.ReplaceHeights(heights);
        terrain.SetDrainage(drainage);
        terrain.SetLayout(Layout);
    }

    /// <summary>
    /// Cuts the channels the first solve found into the ground, so water lies in the land and not on it.
    /// </summary>
    /// <remarks>
    /// <b>§156, and it is the fault under most of the water arc.</b> A river's bed is incised: it cuts down
    /// into its valley floor and its surface sits at or below the ground beside it. Every previous version of
    /// this model put the surface <em>above</em> the ground by the channel's depth, which produced two of the
    /// three complaints from the chair — a sheet draped over a hillside, and a surface that climbs as the
    /// river widens.
    /// <para>
    /// Carved as a cross-section rather than a slot: each channel cell lowers the ground within half its own
    /// width, deepest at the middle and easing to nothing at the bank, so a twenty-metre river gets a
    /// twenty-metre trough with sides rather than a four-metre gash. Taken as a maximum over the channels
    /// that reach a cell, because at a confluence the ground belongs to the deeper one.
    /// </para>
    /// <para>
    /// Standing water is left alone. A lake is already a basin — the depression it fills is the hole — and
    /// deepening it would be inventing relief the fill has already accounted for.
    /// </para>
    /// </remarks>
    /// <summary>
    /// How much a water surface must fall across one drainage cell. Small: a millimetre in four metres is
    /// still monotone, and a larger figure would carve gorges out of gentle country to satisfy arithmetic.
    /// </summary>
    private const float MinimumFallPerCell = 0.002f;

    private static void Incise(float[] lattice, int side, Vector2 origin, Drainage found)
    {
        var carve = new float[lattice.Length];
        var own = new float[lattice.Length];
        var lake = found.LakeDepth;

        // <b>A channel cell is cut to its own depth, and only the banks take the spread.</b> §156: the first
        // version took a maximum over every cross-section reaching a cell, which is right for a bank and
        // ruinous along the channel — two neighbouring reaches inherit each other's depth, so the deeper one
        // lifts the shallower and the fall between them is erased. Measured: a reach whose width doubled from
        // 4.4 m to 8.9 m had its bed fall one centimetre while its channel deepened twenty-seven, and the
        // carve that should have dropped the wider cell thirty-one centimetres had been flattened by its own
        // smoothing.
        for (var index = 0; index < lattice.Length; index++)
        {
            if (lake[index] > 0.02f) continue;
            // The gate is accumulation: does water actually come through here.
            if (Drainage.WidthOf(found.Area[index]) <= DrainageCellMetres) continue;
            // The depth is the width the level rule will use, authored corridor included.
            var here = found.WidthAt(origin + new Vector2(index % side, index / side) * DrainageCellMetres);
            own[index] = 0.30f * MathF.Sqrt(here) * 1.15f;
            carve[index] = own[index];
        }

        // <b>And then the profile, which is the fix the dump actually justified.</b> §157 closed the
        // arithmetic on the worst offenders: width roughly doubles across one four-metre cell, because
        // accumulated area is discontinuous where a tributary joins, so the stacked depth jumps 27 cm against
        // a bed falling 1 cm. <b>The level rule steps the surface up at every confluence.</b>
        //
        // A real confluence does not raise the water: the channel below it is deeper and the surface keeps
        // falling. So the surface is made to fall, and the bed is cut to wherever it has to be to hold the
        // channel's depth underneath it. Walked from the headwaters down — cells in order of decreasing
        // filled height, so every contributor is settled before the cell it feeds — lowering the downstream
        // surface whenever it would sit above its own upstream.
        var order = new int[lattice.Length];
        for (var i = 0; i < order.Length; i++) order[i] = i;
        Array.Sort(order, (a, b) => found.Filled[b].CompareTo(found.Filled[a]));

        var surface = new float[lattice.Length];
        for (var i = 0; i < surface.Length; i++)
        {
            surface[i] = lattice[i] - carve[i] + (own[i] > 0f ? own[i] / 1.15f : 0f);
        }

        var receiver = found.Receiver;
        var lakeDepth = found.LakeDepth;
        foreach (var index in order)
        {
            if (own[index] <= 0f) continue;
            var to = receiver[index];
            if (to < 0 || to == index || own[to] <= 0f) continue;
            // Left alone where the water is standing: a lake's surface is its sill and does not owe its
            // inflow a gradient.
            if (lakeDepth[to] > 0.02f) continue;
            var wanted = surface[index] - MinimumFallPerCell;
            if (surface[to] <= wanted) continue;
            // The bed drops by exactly what the surface had to.
            carve[to] += surface[to] - wanted;
            surface[to] = wanted;
        }

        for (var index = 0; index < lattice.Length; index++)
        {
            if (lake[index] > 0.02f) continue;
            if (own[index] <= 0f) continue;
            var width = found.WidthAt(origin + new Vector2(index % side, index / side) * DrainageCellMetres);

            // Deeper than the water will fill, so the river has banks rather than brimming over them: the
            // level rule puts the surface at bed plus channel, and this cuts fifteen per cent past that.
            var depth = carve[index];
            if (depth <= 0f) continue;
            var half = MathF.Max(DrainageCellMetres, width * 0.5f);
            var reach = (int)MathF.Ceiling(half / DrainageCellMetres);
            var cx = index % side;
            var cz = index / side;
            for (var dz = -reach; dz <= reach; dz++)
            for (var dx = -reach; dx <= reach; dx++)
            {
                var nx = cx + dx;
                var nz = cz + dz;
                if (nx < 0 || nz < 0 || nx >= side || nz >= side) continue;
                var away = MathF.Sqrt(dx * dx + dz * dz) * DrainageCellMetres / half;
                if (away > 1f) continue;
                var at = nz * side + nx;
                // The channel's own cells keep their own depth, whatever a bigger neighbour would have given
                // them. This is the line that preserves the fall.
                if (own[at] > 0f) continue;
                // A rounded bank: full depth at the water, nothing at the top, and no corner in between.
                var profile = MathF.Cos(away * MathF.PI * 0.5f);
                carve[at] = MathF.Max(carve[at], depth * profile);
            }
        }

        for (var index = 0; index < lattice.Length; index++) lattice[index] -= carve[index];
    }

    /// <summary>
    /// Where the through-river enters and where it leaves.
    /// </summary>
    /// <remarks>
    /// <b>Chosen along the map's own fall, which is the only thing that makes a crossing coherent.</b> The
    /// earlier version took the two lowest points on the perimeter and called them both outlets. That gives
    /// two places for water to leave and no reason for any of it to cross the middle, so the map's structure
    /// stayed local. A river needs somewhere to come <em>from</em>.
    /// <para>
    /// <see cref="Downhill"/> already says which way the land falls, so the upstream edge is the one facing
    /// against it. The entry is the lowest point of that edge — a river arrives through a valley, not over a
    /// shoulder — and the exit is the lowest point of the opposite edge. Between them the water has no choice
    /// but to cross, and with the inherited catchment behind it, it cuts the valley that gives the map its
    /// spine.
    /// </para>
    /// </remarks>
    private (Vector2 Entry, Vector2 Exit) FindCrossing(float[] lattice, int side, float extent)
    {
        var fall = Downhill.LengthSquared() > 1e-6f ? Vector2.Normalize(Downhill) : Vector2.UnitX;
        var half = extent * 0.5f;

        Vector2 Lowest(Vector2 facing)
        {
            var pick = -1;
            var lowest = float.MaxValue;
            for (var z = 0; z < side; z++)
            for (var x = 0; x < side; x++)
            {
                if (x != 0 && z != 0 && x != side - 1 && z != side - 1) continue;
                var local = new Vector2(x, z) * DrainageCellMetres;
                var outward = local - new Vector2(half);
                if (outward.LengthSquared() < 1e-6f) continue;
                // Only the third of the perimeter that faces the way we are asking about, so "the lowest
                // point on the upstream edge" cannot quietly pick a point on the downstream one.
                if (Vector2.Dot(Vector2.Normalize(outward), facing) < 0.45f) continue;
                var index = z * side + x;
                if (lattice[index] > lowest || (lattice[index] == lowest && index >= pick)) continue;
                lowest = lattice[index];
                pick = index;
            }

            // A fall direction pointing at a corner can leave a side with nothing facing it; the centre of
            // that side is a serviceable answer and cannot fail.
            if (pick < 0) return new Vector2(half) + facing * half;
            return new Vector2(pick % side, pick / side) * DrainageCellMetres;
        }

        return (Lowest(-fall), Lowest(fall));
    }

    /// <summary>
    /// Cuts a one-sided shelf for every escarpment in the layout.
    /// </summary>
    /// <remarks>
    /// A signed distance to the path, smoothed over the separator's width: high on one hand, low on the
    /// other, and a ramp where a connector opens it. Done on the lattice rather than out of landforms because
    /// an escarpment is the one statement here that is <em>not</em> symmetric, and a landform is radial — a
    /// chain of hills gives a ridge with a plain on both sides, which is the opposite of a shelf.
    /// </remarks>
    private void Shelve(float[] lattice, int side, Vector2 origin, float extent)
    {
        if (Layout is not { } layout) return;
        for (var index = 0; index < layout.Separators.Length; index++)
        {
            var separator = layout.Separators[index];
            if (separator.Kind != SeparatorKind.Escarpment) continue;
            // <b>The whole lattice, not a band, and that was the difference between a shelf and a ridge.</b>
            // Restricting this to a band around the path meant the high side was lifted for one width and then
            // dropped back to zero — so an "escarpment" came out as a low strip of raised ground with plain on
            // both sides of it. An escarpment is high <em>everywhere</em> on one side; the width is how far the
            // face takes to fall, not how far the shelf extends. Which makes this a signed distance that
            // saturates, and saturation means every cell has to be asked.
            // Bounded by the separator's own reach, so a shelf authored for a 600 m frame does not lift a
            // whole 1800 m canvas — see the remarks on Separator.ReachMetres for what nine of them did.
            var reach = separator.ReachMetres > 0f ? separator.ReachMetres : float.MaxValue;
            var (from, to) = separator.ReachMetres > 0f
                ? Band(separator.Path, origin, side, separator.ReachMetres)
                : (((int X, int Z))(0, 0), ((int X, int Z))(side - 1, side - 1));
            for (var z = from.Z; z <= to.Z; z++)
            for (var x = from.X; x <= to.X; x++)
            {
                var world = origin + new Vector2(x, z) * DrainageCellMetres;
                var away = ToPath(world, separator.Path, out var hand);
                // <b>A ramp stretches the face; it does not lower the shelf.</b> Suppressing height at a ramp
                // would cut a notch in the escarpment, and a notch is a gully rather than a way up. Widening
                // the run the same height falls over is what makes ground climbable — three times the width is
                // a third of the gradient, and the shelf behind it is untouched.
                var ramp = layout.Opening(world, ConnectorOf.Separator, index);
                var face = MathF.Max(1f, separator.WidthMetres * (1f + 2.4f * ramp));
                var t = Math.Clamp(away / face, 0f, 1f);
                var eased = t * t * (3f - 2f * t);
                // Full height once past the face on the high side, zero everywhere on the low side, and the
                // smoothstep between them is the face itself.
                var lift = hand > 0f ? separator.HeightMetres * eased : 0f;
                if (lift <= 0f) continue;
                // And faded out at the edge of its reach, because a shelf that simply stops is a cliff facing
                // the wrong way.
                if (reach < float.MaxValue)
                {
                    var out_ = Math.Clamp(away / reach, 0f, 1f);
                    var bump = 1f - out_ * out_;
                    lift *= bump * bump;
                }

                lattice[z * side + x] += lift;
            }
        }
    }

    /// <summary>
    /// Cuts the river's trough and seeds what it brings with it. Returns where it enters and leaves.
    /// </summary>
    /// <remarks>
    /// <b>Carved before erosion, not instead of it.</b> A trough on its own is a ditch — uniform, straight
    /// between its bends, and obviously drawn. What makes it a river valley is that erosion then finds it,
    /// because the trough guarantees the flow router follows the authored path rather than whatever the
    /// composed relief happened to prefer. So this decides <em>where</em> and erosion decides what it looks
    /// like, which is the same division of labour as the ridges.
    /// <para>
    /// <b>And the inflow is why the map can be a fragment.</b> A river crossing six hundred metres of ground
    /// was a river before it arrived, so its discharge has nothing to do with the thirty-six hectares in
    /// view — see <see cref="Drainage.Solve"/>. Delivered one cell inside the perimeter, because a boundary
    /// cell drains off the map by definition and seeding the edge itself would hand a river's worth of water
    /// straight back to the void.
    /// </para>
    /// </remarks>
    private (Vector2 Entry, Vector2 Exit)? Carve(
        float[] lattice,
        int side,
        Vector2 origin,
        float extent,
        float[] inflow)
    {
        if (Layout is not { } layout) return null;
        var rivers = layout.Rivers().Where(river => river.Path.Length >= 2).ToArray();
        if (rivers.Length == 0) return null;

        // <b>Depth follows width, so a river gets the valley it deserves and not the valley every river gets.</b>
        // A single depth for every watercourse meant a ten-metre consequence carved as deep a trough as a
        // twenty-six-metre statement — and erosion then deepened both, so every archetype came out defined by
        // its river whatever else it was trying to say. Reported as the diagonal river "defining pretty much all
        // the land-masses we have".
        //
        // Proportional needs no new field: the authored width already says how important a river is, because
        // that is what authoring a width means.
        for (var index = 0; index < rivers.Length; index++)
        {
            var river = rivers[index];
            // <b>A tributary is cut shallower than the trunk, and the reason is not looks.</b> Carving them
            // to the same depth makes two channels of equal authority meeting at a junction, and the flow
            // router then has no reason to prefer either — so the trunk wanders into a tributary's bed and
            // out again. Half the depth keeps the hierarchy the drainage is supposed to discover.
            var trunk = index == 0;
            // <b>The ceiling wins over the floor, because they are not two bounds on one intention.</b> The
            // floor says "a river shallower than this is not a river"; the ceiling says "no channel may be
            // deeper than a fraction of this map's relief". On any map with less than about six and a half
            // metres of relief the ceiling falls below the floor and <c>Math.Clamp</c> throws outright —
            // <em>'1.1' cannot be greater than 0.6868</em> — which is what a four-metre relief setting did the
            // first time the panel could ask for one.
            //
            // Not a clamping order to be tidied: on nearly flat ground a river <em>should</em> be a shallow
            // scratch rather than a 1.1 m trench cut through a landscape that has no 1.1 m in it. The floor is
            // an ambition and the ceiling is a fact about the map, so the fact wins.
            var ceiling = Amplitude * 0.17f;
            var cut = MathF.Min(ceiling, MathF.Max(river.WidthMetres * 0.19f, 1.1f)) *
                      (trunk ? 1f : 0.5f);
            var reach = MathF.Max(river.WidthMetres * (trunk ? 2.4f : 1.6f), trunk ? 22f : 14f);
            var (from, to) = Band(river.Path, origin, side, reach);
            for (var z = from.Z; z <= to.Z; z++)
            for (var x = from.X; x <= to.X; x++)
            {
                var world = origin + new Vector2(x, z) * DrainageCellMetres;
                var away = ToPath(world, river.Path, out _);
                if (away >= reach) continue;
                var t = Math.Clamp(away / reach, 0f, 1f);
                // (1-t²)² again: flat-bottomed at the channel and meeting the bank with no crease, which is
                // the same profile SmoothMax uses and for the same reason.
                var bump = 1f - t * t;
                lattice[z * side + x] -= cut * bump * bump;
            }
        }

        // <b>Only the trunk inherits a catchment.</b> A tributary carries what the ground above it sheds and
        // nothing else — that is what makes it a tributary rather than a second river — so it is carved and
        // then left to earn its own discharge from the hillsides it drains.
        var main = rivers[0];
        inflow[Inward(main.Path[0] - origin, side, extent)] = InheritedCatchmentMetres2;
        return (main.Path[0] - origin, main.Path[^1] - origin);
    }

    /// <summary>
    /// Raises every ridge as one continuous heightfield along its spine.
    /// </summary>
    /// <remarks>
    /// <b>Distance along, distance across, and that is the whole idea.</b> For each cell: how far along the
    /// spine its nearest point is, and how far it sits to one side of it. Height is then the crest's own height
    /// at that station times a cross-section of the perpendicular distance — one field, one surface, no members
    /// to detect.
    /// <para>
    /// Three things vary along the spine, and they are what stop it being an extrusion. The <b>crest</b> rises
    /// and falls on a wavelength of about a third of the length, so a ridge is a run of summits and cols rather
    /// than a level wall. The <b>width</b> varies independently, so it swells and narrows. And the <b>two
    /// flanks differ</b> — one steep, one long — which is the single most geographic thing here, because almost
    /// no real ridge is symmetric and a symmetric one reads as extruded whatever else is done to it.
    /// </para>
    /// <para>
    /// The cross-section is <c>cos(πu/2)^1.5</c>: zero slope at the crest so the top is rounded rather than a
    /// knife, zero value <em>and</em> zero slope at the toe so it meets the plain without a crease. The same
    /// requirement the soft maximum had, for the same reason — a crease at the foot of a ridge is a line of
    /// steep ground exactly where the going should be easiest.
    /// </para>
    /// <para>
    /// <b>And spurs are generated here rather than authored.</b> A layout says "a ridge from here to there";
    /// how a ridge sheds its ground sideways is geomorphology, not topology. Two or three short secondary
    /// spines branch off each ridge at about two fifths of its height, which is what gives the flanks re-entrants
    /// for erosion to find and turn into valleys.
    /// </para>
    /// </remarks>
    private void Crest(float[] lattice, int side, Vector2 origin)
    {
        if (Layout is not { } layout) return;
        var ridges = new float[lattice.Length];
        for (var index = 0; index < layout.Separators.Length; index++)
        {
            var separator = layout.Separators[index];
            if (separator.Kind != SeparatorKind.Ridge) continue;
            var salt = seed * 2654435761u + (uint)(index + 1) * 40503u;
            Lay(
                ridges,
                side,
                origin,
                separator.Path,
                separator.WidthMetres,
                separator.HeightMetres,
                salt,
                index);

            // Spurs: short spines running off the flanks, alternating sides.
            var spurs = 2 + (int)(salt % 2u);
            for (var k = 0; k < spurs; k++)
            {
                var t = (k + 0.7f) / (spurs + 0.4f);
                var root = Along(separator.Path, t, out var tangent);
                var hand = (k % 2 == 0 ? 1f : -1f);
                var outward = new Vector2(-tangent.Y, tangent.X) * hand;
                var reach = separator.WidthMetres * (0.75f + (salt >> (k * 3) & 7u) / 14f);
                var tip = root + outward * reach
                    + tangent * ((((salt >> (k * 5)) & 15u) / 15f) - 0.5f) * reach * 0.6f;
                Lay(
                    ridges,
                    side,
                    origin,
                    new[] { root, Vector2.Lerp(root, tip, 0.55f), tip },
                    separator.WidthMetres * 0.55f,
                    separator.HeightMetres * 0.42f,
                    salt * 7919u + (uint)k,
                    // A spur is part of its parent, so a saddle cut in the ridge opens the spur that runs off
                    // it too — which is what a col looks like from the side.
                    index);
            }
        }

        for (var i = 0; i < lattice.Length; i++) lattice[i] += ridges[i];
    }

    /// <summary>One spine, laid into the lattice.</summary>
    private void Lay(
        float[] lattice,
        int sideCount,
        Vector2 origin,
        Vector2[] path,
        float widthMetres,
        float heightMetres,
        uint salt,
        int separatorIndex)
    {
        if (path.Length < 2 || heightMetres <= 0f) return;
        var layout = Layout;
        // The widest the section can get, so the band covers the whole footprint.
        var reach = widthMetres * 1.5f;
        var (from, to) = Band(path, origin, sideCount, reach);

        // Asymmetry, fixed per spine rather than varying along it: a ridge has a steep side and a long side,
        // and one that swapped them halfway along would read as two ridges.
        var lean = 0.62f + (salt % 61u) / 61f * 0.66f;
        for (var z = from.Z; z <= to.Z; z++)
        for (var x = from.X; x <= to.X; x++)
        {
            var world = origin + new Vector2(x, z) * DrainageCellMetres;
            var away = ToSpine(world, path, out var along, out var hand);
            if (away >= reach) continue;

            // Summits and cols along the length, and shoulders at the two ends so it runs out rather than
            // stopping.
            // <b>The sine is clamped non-negative, and without that the whole map was NaN.</b> At along = 1,
            // MathF.Sin(π) returns -8.7e-8 rather than zero, and a negative base raised to a fractional power
            // is NaN — which then propagated through the max-combine into every cell the ridge touched and out
            // through the rescale into the entire lattice. A float sine is not zero at π and a fractional
            // power does not forgive it.
            var ends = MathF.Pow(MathF.Max(0f, MathF.Sin(Math.Clamp(along, 0f, 1f) * MathF.PI)), 0.34f);
            var crest = 0.70f + 0.30f * Landform.Undulate(
                world, salt + 11u, MathF.Max(40f, widthMetres * 2.6f));
            var swell = 0.74f + 0.36f * Landform.Undulate(
                world, salt + 977u, MathF.Max(55f, widthMetres * 3.4f));

            var flank = widthMetres * swell * (hand > 0f ? lean : 2f - lean);
            var u = away / MathF.Max(1f, flank);
            if (u >= 1f) continue;
            var section = MathF.Pow(MathF.Cos(u * MathF.PI * 0.5f), 1.5f);
            var lift = heightMetres * ends * crest * section;
            // <b>Only this ridge's own saddles.</b> The opening used to be global, so a saddle authored to put
            // a pass through one ridge also lowered every other positive within its radius — most often the
            // upland the ridge was standing on.
            if (layout is not null)
            {
                lift *= 1f - 0.88f * layout.Opening(world, ConnectorOf.Separator, separatorIndex);
            }
            var at = z * sideCount + x;
            // <b>Taken as the higher, into a buffer of its own.</b> A spur runs out of its parent's flank, so
            // where the two overlap an addition would put a bump at exactly the junction that should be the
            // smoothest part of it. Maximum instead — and the whole ridge system is combined this way before
            // being added to the ground once, which is why this writes to its own array rather than to the
            // lattice.
            lattice[at] = MathF.Max(lattice[at], lift);
        }
    }

    /// <summary>Perpendicular distance to a spine, how far along it that is, and which side.</summary>
    private static float ToSpine(Vector2 at, Vector2[] path, out float along, out float hand)
    {
        along = 0f;
        hand = 1f;
        if (path.Length < 2) return float.MaxValue;

        var total = 0f;
        for (var i = 1; i < path.Length; i++) total += Vector2.Distance(path[i - 1], path[i]);
        if (total <= 1e-4f) return float.MaxValue;

        var best = float.MaxValue;
        var walked = 0f;
        for (var i = 1; i < path.Length; i++)
        {
            var from = path[i - 1];
            var to = path[i];
            var span = to - from;
            var length = span.Length();
            if (length <= 1e-4f) continue;
            var t = Math.Clamp(Vector2.Dot(at - from, span) / (length * length), 0f, 1f);
            var nearest = from + span * t;
            var away = Vector2.Distance(at, nearest);
            if (away < best)
            {
                best = away;
                along = (walked + t * length) / total;
                hand = span.X * (at.Y - from.Y) - span.Y * (at.X - from.X) >= 0f ? 1f : -1f;
            }

            walked += length;
        }

        return best;
    }

    /// <summary>
    /// Raises the layout's areal high ground: a plateau or a massif, as one field.
    /// </summary>
    /// <remarks>
    /// Flat across the middle and falling away over the outer half, with the radius warped by noise so the
    /// margin is a coastline rather than a circle. One field rather than a chain of landforms, which is the
    /// point — see the remarks on <see cref="Upland"/>.
    /// </remarks>
    private void Raise(float[] lattice, int side, Vector2 origin)
    {
        if (Layout is not { Uplands.Length: > 0 } layout) return;
        for (var slot = 0; slot < layout.Uplands.Length; slot++)
        {
            var upland = layout.Uplands[slot];
            var reach = MathF.Max(12f, upland.RadiusMetres);
            var stretch = MathF.Max(1f, upland.Stretch);
            // The box has to cover the long axis, whichever way it is pointing.
            var span = reach * 1.35f * stretch;
            var from = (upland.Centre - new Vector2(span) - origin) / DrainageCellMetres;
            var to = (upland.Centre + new Vector2(span) - origin) / DrainageCellMetres;
            var x0 = Math.Clamp((int)MathF.Floor(from.X), 0, side - 1);
            var z0 = Math.Clamp((int)MathF.Floor(from.Y), 0, side - 1);
            var x1 = Math.Clamp((int)MathF.Ceiling(to.X), 0, side - 1);
            var z1 = Math.Clamp((int)MathF.Ceiling(to.Y), 0, side - 1);
            for (var z = z0; z <= z1; z++)
            for (var x = x0; x <= x1; x++)
            {
                var world = origin + new Vector2(x, z) * DrainageCellMetres;
                var offset = world - upland.Centre;
                // <b>Into the massif's own frame, so an elongated upland is a circle in a squashed space.</b>
                // The same trick Landform uses for a stretched hill, and for the same reason: every test below
                // stays radial while the shape it describes stops being round. Dividing the along-axis is what
                // lengthens it — the across-axis is left alone so the width is the radius.
                var along = MathF.Cos(upland.BearingRadians) * offset.X
                    + MathF.Sin(upland.BearingRadians) * offset.Y;
                var across = -MathF.Sin(upland.BearingRadians) * offset.X
                    + MathF.Cos(upland.BearingRadians) * offset.Y;
                offset = new Vector2(along / stretch, across);
                var away = offset.Length();
                // <b>Two octaves of outline, not one, and a much wider swing.</b> One octave at fifteen per
                // cent gives a circle with a slight wobble, which the eye reads as a circle.
                var wobble = 0.74f
                    + 0.34f * Landform.Undulate(world, seed * 7919u + 41u, reach * 0.70f)
                    + 0.16f * Landform.Undulate(world, seed * 6151u + 83u, reach * 0.26f);
                var edge = reach * wobble;
                if (away >= edge) continue;
                // Flat over the inner half, smoothstep down across the outer half. The flat top is what makes
                // it a plateau; a shape that peaks in the middle is a hill.
                var t = Math.Clamp((away / edge - 0.45f) / 0.55f, 0f, 1f);
                var shoulder = 1f - t * t * (3f - 2f * t);

                // <b>Relief on the top, and without it the whole thing is a sausage.</b> A radial function with
                // a flat top is a solid of revolution — the same failure §54 recorded for landforms, which came
                // back as "this sausage-like hill is a common feature and looks pretty odd". Warping the outline
                // does not help, because what the eye is reading is the <em>interior</em>: a large area at one
                // height with a smooth shoulder all round is a lozenge whatever shape its edge is.
                //
                // And erosion cannot fix it either, which is the part worth writing down. Stream power needs a
                // slope to bite on and a plateau has none, so twelve passes wash over it while hillslope creep
                // smooths it further. <b>Erosion can only dissect what already has some relief in it.</b> Two
                // octaves at a hundred and sixty and sixty metres give it summits and hollows to work from, and
                // the drainage that comes out of them is what turns a lozenge into a massif.
                //
                // Faded by the shoulder so the edge stays where the outline put it — roughness that reached the
                // margin would fight the shape rather than furnish it.
                var summits =
                    (Landform.Undulate(world, seed * 4513u + 17u, MathF.Max(60f, reach * 1.15f)) - 0.5f) * 0.62f +
                    (Landform.Undulate(world, seed * 9781u + 29u, MathF.Max(28f, reach * 0.42f)) - 0.5f) * 0.30f;
                var relief = 1f + summits * shoulder;

                lattice[z * side + x] += upland.HeightMetres * shoulder * relief
                    * (1f - 0.80f * layout.Opening(world, ConnectorOf.Upland, slot));
            }
        }
    }

    /// <summary>
    /// Cuts the layout's valley floors, so a valley is low ground rather than absent high ground.
    /// </summary>
    /// <remarks>
    /// Flat-bottomed and meeting its banks with no crease — <c>(1 - t²)²</c>, the same profile the river trough
    /// and the soft maximum use, for the same reason. A valley floor with a rounded bottom is a ditch; the
    /// flatness is the part that makes it ground somebody would farm.
    /// </remarks>
    private void Excavate(float[] lattice, int side, Vector2 origin)
    {
        if (Layout is not { Troughs.Length: > 0 } layout) return;
        foreach (var trough in layout.Troughs)
        {
            if (trough.Path.Length < 2) continue;
            var reach = MathF.Max(8f, trough.WidthMetres);
            var (from, to) = Band(trough.Path, origin, side, reach);
            for (var z = from.Z; z <= to.Z; z++)
            for (var x = from.X; x <= to.X; x++)
            {
                var world = origin + new Vector2(x, z) * DrainageCellMetres;
                var away = ToPath(world, trough.Path, out _);
                if (away >= reach) continue;
                var t = away / reach;
                var bump = 1f - t * t;
                lattice[z * side + x] -= trough.DepthMetres * bump * bump;
            }
        }
    }

    /// <summary>
    /// Cuts the layout's basins into the lattice, so there is somewhere for water to stand.
    /// </summary>
    /// <remarks>
    /// A raised-cosine bowl: flat-bottomed in the middle, meeting the surrounding ground with no crease at the
    /// rim. The rim is what matters — depression filling will raise the water to the lowest point on it, so a
    /// creased or notched rim drains the lake through the notch and leaves a damp hollow.
    /// <para>
    /// Cut <em>below</em> the surrounding ground rather than to an absolute level, because the ground it is
    /// being cut into has already been eroded and its height is not something the layout could have known.
    /// </para>
    /// </remarks>
    private void Sink(float[] lattice, int side, Vector2 origin, float extent)
    {
        if (Layout is not { Basins.Length: > 0 } layout) return;
        // <b>The climate decides how much water the layout's statements carry.</b> Applied here rather than in
        // the layout, because a layout states intent — "a lake, this big" — and how much of that a place
        // actually holds is a fact about the place. Dry country gets the same basin as a pan in it.
        var wet = RegionProfile.For(region).WaterScale;
        foreach (var basin in layout.Basins)
        {
            var reach = MathF.Max(8f, basin.RadiusMetres);
            var from = (basin.Centre - new Vector2(reach) - origin) / DrainageCellMetres;
            var to = (basin.Centre + new Vector2(reach) - origin) / DrainageCellMetres;
            var x0 = Math.Clamp((int)MathF.Floor(from.X), 0, side - 1);
            var z0 = Math.Clamp((int)MathF.Floor(from.Y), 0, side - 1);
            var x1 = Math.Clamp((int)MathF.Ceiling(to.X), 0, side - 1);
            var z1 = Math.Clamp((int)MathF.Ceiling(to.Y), 0, side - 1);
            for (var z = z0; z <= z1; z++)
            for (var x = x0; x <= x1; x++)
            {
                var world = origin + new Vector2(x, z) * DrainageCellMetres;
                var t = Vector2.Distance(world, basin.Centre) / reach;
                if (t >= 1f) continue;
                // (1 + cos(pi t)) / 2: one at the centre, zero at the rim, and zero slope at both — the same
                // property SmoothMax needs and for the same reason, which is that a crease is a line of steep
                // ground exactly where it is least wanted.
                var bowl = 0.5f * (1f + MathF.Cos(MathF.PI * t));
                lattice[z * side + x] -= basin.DepthMetres * wet * bowl;
            }

            Dam(lattice, side, origin, basin with { DepthMetres = basin.DepthMetres * wet });
        }

        _ = extent;
    }

    /// <summary>
    /// Raises a bar across the water's way out, so the basin behind it holds something.
    /// </summary>
    /// <remarks>
    /// <b>Without this a basin is a wide place in a river.</b> Depression filling raises water to the lowest
    /// point of a hollow's rim, and a hollow cut into a descending valley has no rim downstream — the lowest
    /// point on it is the way the river was already going. So the fill finds an outlet at the basin floor's own
    /// level and there is nothing to pond.
    /// <para>
    /// The bar is placed just beyond the basin, across the flow, and raised by a little over half the basin's
    /// depth. Over half so the lake is deep enough to be one; only a little over, so the fill spills across the
    /// bar rather than backing up the valley indefinitely — a dam that cannot be overtopped floods everything
    /// upstream of it, which is how a lake becomes an inland sea by accident.
    /// </para>
    /// <para>
    /// Cut with the same raised cosine as the bowl, so it meets the ground either side of it without a crease.
    /// A notch in a dam is where the whole lake leaves.
    /// </para>
    /// </remarks>
    private static void Dam(float[] lattice, int side, Vector2 origin, Basin basin)
    {
        var flow = basin.Downstream.LengthSquared() > 1e-4f
            ? Vector2.Normalize(basin.Downstream)
            : Vector2.UnitX;
        var across = new Vector2(-flow.Y, flow.X);
        var at = basin.Centre + flow * (basin.RadiusMetres * 1.02f);
        // Wider than the basin across the flow, so the water cannot slip round the end of it, and thin along
        // the flow because a dam is a bar and not a plateau.
        var span = basin.RadiusMetres * 1.35f;
        var thick = MathF.Max(10f, basin.RadiusMetres * 0.34f);
        // <b>Under half the basin's depth, because a dam backs water up the valley behind it.</b> At 0.62 the
        // lake was mostly not the basin at all — it was flooded valley reaching four hundred metres upstream,
        // because the backup length is the lift divided by the valley's fall and this valley falls two metres
        // in a hundred. Which is real, and is how a reservoir behaves, and is also unpredictable in a way an
        // authored feature should not be. At 0.45 the basin is the lake and the backup is its tail.
        var lift = basin.DepthMetres * 0.45f;

        var reach = MathF.Max(span, thick) + DrainageCellMetres;
        var from = (at - new Vector2(reach) - origin) / DrainageCellMetres;
        var to = (at + new Vector2(reach) - origin) / DrainageCellMetres;
        var x0 = Math.Clamp((int)MathF.Floor(from.X), 0, side - 1);
        var z0 = Math.Clamp((int)MathF.Floor(from.Y), 0, side - 1);
        var x1 = Math.Clamp((int)MathF.Ceiling(to.X), 0, side - 1);
        var z1 = Math.Clamp((int)MathF.Ceiling(to.Y), 0, side - 1);
        for (var z = z0; z <= z1; z++)
        for (var x = x0; x <= x1; x++)
        {
            var world = origin + new Vector2(x, z) * DrainageCellMetres;
            var offset = world - at;
            var alongFlow = MathF.Abs(Vector2.Dot(offset, flow)) / thick;
            var alongBar = MathF.Abs(Vector2.Dot(offset, across)) / span;
            if (alongFlow >= 1f || alongBar >= 1f) continue;
            var profile = 0.25f
                * (1f + MathF.Cos(MathF.PI * alongFlow))
                * (1f + MathF.Cos(MathF.PI * alongBar));
            lattice[z * side + x] += lift * profile;
        }
    }

    /// <summary>
    /// The width the layout asked for, painted along its watercourses onto the drainage lattice.
    /// </summary>
    /// <remarks>
    /// <b>So that "a twenty-six metre river" is twenty-six metres.</b> The authored width had been shaping the
    /// trough and nothing else, while the water actually drawn came from upslope area alone — so a layout could
    /// state a river and get a stream. See <c>Drainage.PaintAuthoredWidth</c> for which of the two wins where
    /// they disagree.
    /// <para>
    /// Painted with a falloff rather than as a hard band, so a river narrows toward its banks instead of ending
    /// in a wall of water at a fixed radius.
    /// </para>
    /// </remarks>
    private float[] AuthoredWidths(int side, Vector2 origin)
    {
        var widths = new float[side * side];
        if (Layout is not { } layout) return widths;
        var wet = RegionProfile.For(region).WaterScale;
        for (var index = 0; index < layout.Separators.Length; index++)
        {
            var river = layout.Separators[index];
            if (river.Kind != SeparatorKind.River || river.Path.Length < 2) continue;
            // Scaled by the climate: a dry region's river runs in a bed built for more water than it carries,
            // which is exactly what a wadi is.
            var authoredWidth = river.WidthMetres * wet;
            var half = MathF.Max(4f, authoredWidth * 0.5f);
            var (from, to) = Band(river.Path, origin, side, half);
            for (var z = from.Z; z <= to.Z; z++)
            for (var x = from.X; x <= to.X; x++)
            {
                var world = origin + new Vector2(x, z) * DrainageCellMetres;
                var away = ToPath(world, river.Path, out _);
                if (away >= half) continue;
                var t = away / half;
                var bump = 1f - t * t;
                // <b>A ford narrows and shallows its river, which is the whole of what a ford is.</b> Before
                // this the channel was painted at full width straight across one, so a map could author a
                // crossing and get an unbroken barrier.
                var ford = layout.Ford(world, index);
                var here = authoredWidth * (1f - 0.62f * ford);
                var cell = z * side + x;
                widths[cell] = MathF.Max(widths[cell], here * bump);
            }
        }

        return widths;
    }

    /// <summary>
    /// Drowns one side of the map, and returns the height the water stands at.
    /// </summary>
    /// <remarks>
    /// <b>The shoreline is a warped line, not an edge.</b> A coast running straight along a map boundary reads
    /// as exactly what it is; warped on a two-hundred-metre wavelength it becomes bays and headlands, which is
    /// most of what makes a coast recognisable from the ground.
    /// <para>
    /// The land is pulled <em>down toward</em> a shelf rather than replaced by one, so whatever the archetype
    /// put near the coast still shows through as an island, a spit or a cliff. Replacing it would make every
    /// coast identical regardless of what it was cutting into, which is the same mistake the frontier made.
    /// </para>
    /// <para>
    /// Sea level sits just above the pre-coast floor, so the lowest land on the map is a beach rather than
    /// something that happens to be dry. Returns negative infinity when there is no coast, which every
    /// consumer reads as "no cell is ever below this".
    /// </para>
    /// </remarks>
    private float Coastline(float[] lattice, int side, Vector2 origin)
    {
        if (Layout?.Coast is not { } coast) return float.NegativeInfinity;
        var seaward = coast.Seaward.LengthSquared() > 1e-6f
            ? Vector2.Normalize(coast.Seaward)
            : Vector2.UnitX;

        var low = float.MaxValue;
        foreach (var height in lattice) low = MathF.Min(low, height);
        var seaLevel = low + Amplitude * 0.03f;
        // Deep enough that the water reads as water rather than as a flooded field, and shallow enough that a
        // shelf near the shore still shows the bed through it.
        var floorHeight = seaLevel - MathF.Max(2.5f, Amplitude * 0.22f);

        // Where the shore sits along the seaward axis: the far edge, pulled back by the inset.
        var half = side * DrainageCellMetres * 0.5f;
        var anchor = seaward * MathF.Max(0f, half - coast.InsetMetres);
        for (var z = 0; z < side; z++)
        for (var x = 0; x < side; x++)
        {
            var world = origin + new Vector2(x, z) * DrainageCellMetres;
            var wander = (Landform.Undulate(world, seed * 3167u + 601u, 205f) - 0.5f) * coast.WanderMetres;
            var out_ = Vector2.Dot(world, seaward) - Vector2.Dot(anchor, seaward) + wander;
            if (out_ <= 0f) continue;
            // Over about a third of the inset, so there is a beach and a shelf rather than a step.
            var t = Math.Clamp(out_ / MathF.Max(12f, coast.InsetMetres * 0.55f), 0f, 1f);
            var eased = t * t * (3f - 2f * t);
            var index = z * side + x;
            lattice[index] = MathF.Min(
                lattice[index],
                lattice[index] + (floorHeight - lattice[index]) * eased);
        }

        return seaLevel;
    }

    /// <summary>Rescales a lattice so its relief is exactly what was asked for, keeping its floor.</summary>
    private static void Normalise(float[] lattice, float wanted)
    {
        if (wanted <= 0f) return;
        var low = float.MaxValue;
        var high = float.MinValue;
        foreach (var height in lattice)
        {
            low = MathF.Min(low, height);
            high = MathF.Max(high, height);
        }

        var have = high - low;
        if (have <= 1e-4f) return;
        var scale = wanted / have;
        for (var i = 0; i < lattice.Length; i++) lattice[i] = low + (lattice[i] - low) * scale;
    }

    /// <summary>The lattice cell one step inside the perimeter from a point on it.</summary>
    private static int Inward(Vector2 local, int side, float extent)
    {
        var x = Math.Clamp((int)MathF.Round(local.X / DrainageCellMetres), 1, side - 2);
        var z = Math.Clamp((int)MathF.Round(local.Y / DrainageCellMetres), 1, side - 2);
        _ = extent;
        return z * side + x;
    }

    /// <summary>
    /// Raises the map's frontier, unevenly, and lets the gates through.
    /// </summary>
    /// <remarks>
    /// <b>Uneven on purpose: a frontier is a preference, not a wall.</b> A rim of constant height reads as
    /// exactly what it is — the edge of a level — and seals the map into a box. Modulated on a two-hundred
    /// metre wavelength it becomes country: two or three stretches that stand up as crag, two or three that
    /// are merely high ground somebody could walk over, and the gates cut right through. The player is
    /// <em>discouraged</em> from the exact edge in most places and stopped in some, which is the difference
    /// between a region and an arena.
    /// <para>
    /// It earns its keep three times over, which is why it is worth the generation pass. The edge stops being
    /// an invisible line. Water is funnelled into two outlets instead of leaking off all four sides, so the
    /// trunk rivers are bigger and read as rivers. And the interior finally has enclosed ground behind
    /// high land, which is the thing standing water needs — erosion destroys basins, so a lake needs a
    /// reason to exist that erosion cannot take away.
    /// </para>
    /// <para>
    /// <b>The frontier has to carry its own shape, because erosion cannot give it one.</b> The first version
    /// added a smooth ramp and left the shaping to <see cref="Erosion"/>, which was wrong for a reason worth
    /// stating: incision goes as upslope area, and a divide has nothing above it — so the one part of a map
    /// erosion physically cannot cut is the crest of a rim. Its outer face drains straight off the edge and
    /// gathers nothing either. Forty-eight passes of hillslope creep then smoothed away what little the
    /// initial ramp had, and the result was reported from the chair as "curved inwards", which is exactly
    /// what a smoothed ramp is.
    /// </para>
    /// <para>
    /// So two modulations do the work erosion will not. The <b>foot</b> wanders in and out on a ninety-metre
    /// wavelength, which turns the mountain front from a straight offset of the map edge into a line of spurs
    /// and re-entrants — that single change is most of what stops it reading as a wall, because a wall is
    /// recognised by being parallel to something. And the <b>crest</b> carries a second, shorter wavelength
    /// on top of the long one, so it is a run of peaks and cols rather than a level ridge.
    /// </para>
    /// </remarks>
    private void AddRim(float[] lattice, int side, Vector2 origin, float extent, Vector2[] gates)
    {
        var half = extent * 0.5f;
        var height = MathF.Max(Amplitude, 4f) * RimRelief;
        for (var z = 0; z < side; z++)
        for (var x = 0; x < side; x++)
        {
            var local = new Vector2(x, z) * DrainageCellMetres;
            var world = origin + local;
            var edge = MathF.Min(
                MathF.Min(local.X, extent - local.X),
                MathF.Min(local.Y, extent - local.Y));
            // The foot wanders, so the mountain front is spurs and re-entrants rather than a line parallel
            // to the map edge. Half the rim's width of swing, which is enough that no stretch of the foot is
            // straight for long and not so much that a spur reaches the settlement.
            var wander = (Landform.Undulate(world, seed * 40503u + 7u, 90f) - 0.5f) * RimWidthMetres * 0.55f;
            var inset = edge + wander;
            var t = Math.Clamp(1f - inset / RimWidthMetres, 0f, 1f);
            if (t <= 0f) continue;

            // Smoothstep, so the frontier meets the interior with no crease along a line 78 m in.
            var profile = t * t * (3f - 2f * t);
            // Between a third and full height. The floor is not zero: a frontier that drops to nothing
            // somewhere other than a gate is a gap the drainage will find and the gates will lose to.
            var vary = 0.34f + 0.66f * Landform.Undulate(world, seed * 2246822519u + 101u, 205f);
            // Peaks and cols along the crest. Multiplied rather than added so it cannot lift the frontier
            // where the long wavelength meant it to be low — a col has to stay a col.
            vary *= 0.62f + 0.38f * Landform.Undulate(world, seed * 917u + 313u, 68f);

            var gate = 1f;
            foreach (var at in gates)
            {
                var reach = Math.Clamp(Vector2.Distance(local, at) / GateReachMetres, 0f, 1f);
                gate = MathF.Min(gate, reach * reach * (3f - 2f * reach));
            }

            lattice[z * side + x] += height * profile * vary * gate;
        }

        _ = half;
    }

    /// <summary>What this plan actually put on the ground, counted.</summary>
    /// <remarks>
    /// <b>It used to say "flat" for every composed map, and that is worth recording.</b> The line read
    /// <c>landforms.Count == 0 ? "flat" : …</c>, which was true when a plan was nothing but a bag of mounds.
    /// The areal primitives — uplands, troughs, basins, the ridge heightfield, the coast — arrived as separate
    /// collections and none of them is a <see cref="Landform"/>, so a map with two ridges and a fertile floor
    /// over a 32 m height range printed <c>relief: flat</c> beside its own sentence saying otherwise.
    /// <para>
    /// The same failure as the <c>BUILD terrain</c> timer that was also timing the tree instances: an instrument
    /// keyed to a subset of what it claims to measure reads correctly until something is added outside the
    /// subset, and then reads confidently wrong. Counting every collection is the fix; the count being
    /// mechanical is the point.
    /// </para>
    /// </remarks>
    public string Describe()
    {
        var parts = new List<string>();
        if (landforms.Count > 0) parts.Add($"{landforms.Count} landforms");
        if (Layout is { } layout)
        {
            if (layout.Uplands.Length > 0) parts.Add($"{layout.Uplands.Length} uplands");
            if (layout.Troughs.Length > 0) parts.Add($"{layout.Troughs.Length} troughs");
            if (layout.Basins.Length > 0) parts.Add($"{layout.Basins.Length} basins");
            if (layout.Separators.Length > 0) parts.Add($"{layout.Separators.Length} separators");
            if (layout.Connectors.Length > 0) parts.Add($"{layout.Connectors.Length} connectors");
            if (layout.Coast is not null) parts.Add("a coast");
            if (layout.Woods.Length > 0) parts.Add($"{layout.Woods.Length} woods");
        }

        if (parts.Count == 0) return "flat";
        return string.Join(", ", parts) +
               $"; amplitude {Amplitude:F1} m, steepest flank {SteepestGrade:F2} grade against a limit of " +
               $"{TerrainMap.MaximumTraversableGrade:F2}, undulation {Undulation:F1} m, " +
               $"fall {Tilt * 100f:F2} m per 100 m";
    }

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

using RTSGame.Simulation.Agents;

namespace RTSGame.Simulation.Economy;

/// <summary>
/// Outcrops: a finite, physical, placed stock of stone, and what it costs to bring it in.
/// </summary>
/// <remarks>
/// <b>Deliberately the same shape as <see cref="Woodland"/>, because the mechanic is the same mechanic.</b>
/// Stone is never produced; it is in the rock when the world begins and it comes out one whole unit at a time
/// into a quarrier's hands until the outcrop is worked out. <c>Produced.Stone</c> stays permanently zero and
/// the conservation identity is unchanged.
/// <para>
/// <b>What is not the same is where it is, and that is the entire point of adding a third resource.</b> A field
/// is made on any level ground the player likes. Trees are placed by the land but placed nearly everywhere, so
/// which ones to cut is a choice among many. Stone is only where the generator put crag and scree — high,
/// steep, broken ground — and the site scorer rejects exactly that ground as unbuildable. <b>So stone is the
/// first resource that cannot be brought inside the arrangement.</b> No amount of good founding puts a quarry
/// in the back garden the way §70's deleted tree ring put fuel there.
/// </para>
/// <para>
/// That is what makes the forward depot and the cart necessary rather than merely available. Wood could always
/// be answered by founding well; stone cannot be answered that way at all.
/// </para>
/// </remarks>
internal static class Quarrying
{
    /// <summary>Units of stone one block of an outcrop is worth.</summary>
    /// <remarks>
    /// <b>Per block, and it used to be per deposit, which is the same number meaning two different things.</b>
    /// At 300 a deposit was one boulder holding ten villager loads. Deposits are clusters now — several blocks
    /// standing together as one working — so leaving it at 300 quadrupled how much stone a map held without
    /// anybody choosing that.
    /// <para>
    /// At 120 a block is four loads and a middling quarry of four or five blocks holds about what a single
    /// deposit used to, which puts a map's total back where it was judged to be right. Still comfortably
    /// larger than a tree's ninety, and still the same claim: a wood is many small stocks and a quarry is one
    /// big one you go to.
    /// </para>
    /// </remarks>
    internal static float StonePerOutcrop = 120f;

    /// <summary>
    /// Share of its year a quarrier may spend walking, which is a cutter's share because it is the same year.
    /// </summary>
    /// <remarks>
    /// <b>Not a dial of its own, and it was one for an afternoon on an argument I had not measured.</b> The
    /// reasoning went: rock is on the steep tops, the site scorer refuses to found near those, so a tenth of a
    /// year would leave every outcrop unworkable and stone would be scenery — so a quarter. Then the scatter
    /// was measured and the nearest outcrop came in at 52–86 m against the 151 m reach a quarter buys. The
    /// dial had made stone <em>trivially</em> reachable from the granary, which is the opposite of the
    /// paragraph above it, and I would have shipped a resource whose documentation contradicted its numbers.
    /// <para>
    /// <b>How much of its year a body spends on the road is a fact about the body, not about what it is
    /// carrying.</b> So it is the same tenth, and the difference between a cutter's reach and a quarrier's
    /// falls out of the annual figures instead: at 240 stone a year against 500 wood there are fewer, bigger
    /// trips, so the same tenth buys 60 m rather than 29. Which lands where a mechanic wants to be — <b>the
    /// nearest rock is 52–86 m depending on the map, so some maps have stone in reach of the granary and some
    /// do not, and which kind of map you are on is something you find out by looking.</b>
    /// </para>
    /// </remarks>
    internal static float QuarrierWalkShare => Woodland.CutterWalkShare;

    /// <summary>The same physical walking budget a cutter gets. See Woodland.WalkSecondsPerYear.</summary>
    internal static float WalkSecondsPerYear => Woodland.WalkSecondsPerYear;

    /// <summary>Units of stone one second of quarrying frees from an outcrop.</summary>
    /// <remarks>
    /// Same derivation as <see cref="Woodland.CutPerSecond"/>: a quarrier has <c>year × (1 − walkShare)</c>
    /// seconds actually at the rock and has to get <see cref="EconomyRates.StonePerHandPerYear"/> out of them.
    /// </remarks>
    public static float CutPerSecond =>
        EconomyRates.StonePerHandPerYear /
        (WorldCalendar.YearSeconds * MathF.Max(0.01f, 1f - QuarrierWalkShare));

    /// <summary>Seconds of quarrying one body's load is.</summary>
    public static float LoadSeconds(int carryCapacity) =>
        MathF.Max(1f, carryCapacity) / CutPerSecond;

    /// <summary>How far from its store a quarrier will go for stone, in metres.</summary>
    /// <remarks>
    /// Derived exactly as <see cref="Woodland.ReachMetres"/> is, from the walking budget and nothing else: a
    /// year affords <see cref="WalkSecondsPerYear"/> seconds of walking, a year is
    /// <c>StonePerHandPerYear / carry</c> round trips, and half of one trip's seconds is the one-way distance
    /// at the body's pace. Seconds rather than a share of the year for the reason given on the cutter's
    /// budget, and it matters more here: the reach sits inside the 52-86 m band the nearest rock falls in, so
    /// a reach that moved with the calendar would have put every map's stone in reach of the granary and
    /// taken the map variety with it.
    /// <para>
    /// Measured from the store the quarrier delivers to, so the same thing follows here as follows for wood:
    /// when no store is within reach of any rock, the answer is a depot at the quarry, and the stone piling up
    /// in it is stock nobody eats.
    /// </para>
    /// </remarks>
    public static float ReachMetres
    {
        get
        {
            var body = UnitType.Villager;
            var tripsPerYear = EconomyRates.StonePerHandPerYear / MathF.Max(1f, body.CarryCapacity);
            var secondsPerTrip = WalkSecondsPerYear / MathF.Max(0.01f, tripsPerYear);
            return secondsPerTrip * 0.5f * body.MaximumSpeed;
        }
    }

    /// <summary>What an outcrop would say about itself.</summary>
    public static string StateOf(in EconomyNode rock) => rock.Stock.Stone >= StonePerOutcrop - 0.5f
        ? "untouched"
        : rock.Stock.Stone > 0
            ? "being worked"
            : "worked out";
}

using RTSGame.Simulation.Agents;

namespace RTSGame.Simulation.Economy;

/// <summary>
/// Trees: a finite, physical, placed stock of wood, and what it costs to bring it in.
/// </summary>
/// <remarks>
/// <b>Wood is never produced.</b> It is standing on the map when the world begins and it is taken out
/// of a tree into a cutter's hands, one whole unit at a time, until the tree is gone. That single
/// decision is what §19 meant by "resources are physical, placed and finite", and it makes the
/// conservation identity <em>shorter</em> rather than longer: there is no production term for wood at
/// all, so <c>Seeded + Produced − Consumed = Stored + Carried</c> holds with <c>Produced.Wood</c>
/// permanently zero. A settlement's total wood is bounded by the forest it can reach, which is the
/// pressure the whole game is supposed to run on.
/// <para>
/// It also retires the last rate in the economy. A woodcutter node used to accrue wood at
/// <c>WoodPerHandPerYear × seasonalShape × sqrt(hands)</c> whether or not anything was standing near
/// it, which is the same mistake <see cref="CropCycle"/> was written to undo for grain: an abstract
/// producer that can be put anywhere, in a world where the whole point is that distance is the
/// terrain. There is no <c>WoodShape</c> now, because <em>when</em> wood arrives is a fact about when
/// the player has hands to spare — spring and harvest belong to the fields — rather than a curve.
/// </para>
/// </remarks>
internal static class Woodland
{
    /// <summary>Units of wood one tree is worth.</summary>
    /// <remarks>
    /// Three loads for one villager, which is about sixteen minutes of cutting. Chosen for
    /// <em>legibility</em> rather than derived, and that is the honest reason: a settlement burns
    /// twenty-odd trees a year, so at three loads a tree the wood line visibly recedes within a season
    /// and the player can watch their own logging happen. One load a tree fells one every five minutes
    /// and needs the forest packed at three-metre spacing to last a year; ten loads a tree makes a tree
    /// an hour of work and nothing ever visibly changes.
    /// </remarks>
    internal static float WoodPerTree = 90f;

    /// <summary>Share of its year a cutter may spend walking its wood in.</summary>
    /// <remarks>
    /// This is the dial the whole stage turns on, and it is a dial about <em>waste</em> rather than
    /// about distance: a tenth of a year on the road is a tolerable overhead for one pair of hands, and
    /// anything past that is a settlement that has outgrown its arrangement and should be told so. The
    /// reach follows from it — see <see cref="ReachMetres"/> — instead of being a radius somebody liked.
    /// </remarks>
    internal static float CutterWalkShare = 0.10f;

    /// <summary>Units of wood one second of cutting frees from a tree.</summary>
    /// <remarks>
    /// Derived from the annual figure and the walking overhead: a cutter has
    /// <c>year × (1 − walkShare)</c> seconds actually at a tree, and it has to get
    /// <see cref="EconomyRates.WoodPerHandPerYear"/> out of them. Roughly a tenth of a unit a second,
    /// so a thirty-unit load is a little under five minutes of chopping.
    /// </remarks>
    public static float CutPerSecond =>
        EconomyRates.WoodPerHandPerYear /
        (WorldCalendar.YearSeconds * MathF.Max(0.01f, 1f - CutterWalkShare));

    /// <summary>Seconds of cutting one body's load is.</summary>
    /// <remarks>
    /// The shift length for a cutting assignment, so the shift ends about when the hands fill rather
    /// than long before. The forty-five second shift a field uses is a <em>fallback</em> for a body
    /// whose hands fill in seconds; applied to cutting it would send a woodcutter home with four units
    /// and turn the job into nothing but walking.
    /// </remarks>
    public static float LoadSeconds(int carryCapacity) =>
        MathF.Max(1f, carryCapacity) / CutPerSecond;

    /// <summary>
    /// How far from its base a cutter will go for a tree, in metres.
    /// </summary>
    /// <remarks>
    /// Derived from <see cref="CutterWalkShare"/> and nothing else: a year affords
    /// <c>share × year</c> seconds of walking, a year is
    /// <c>WoodPerHandPerYear / carry</c> round trips, so each round trip may be
    /// <c>share × year / trips</c> seconds and half of that is the one-way distance at the body's pace.
    /// Comes out near thirty metres for a villager, which is the band the design guessed at from the
    /// other end.
    /// <para>
    /// The reach is measured from the <em>store the cutter delivers to</em>, which is what makes a
    /// lumber camp a thing you build rather than a thing you are given: when the near trees are gone,
    /// no store is within reach of any tree, the cutters say so, and the answer is a forward depot at
    /// the tree line. Everything that follows from that — wood accumulating somewhere nobody eats, and
    /// therefore carts — follows on its own.
    /// </para>
    /// </remarks>
    public static float ReachMetres
    {
        get
        {
            var body = UnitType.Villager;
            var tripsPerYear = EconomyRates.WoodPerHandPerYear / MathF.Max(1f, body.CarryCapacity);
            var secondsPerTrip = CutterWalkShare * WorldCalendar.YearSeconds /
                                 MathF.Max(0.01f, tripsPerYear);
            return secondsPerTrip * 0.5f * body.MaximumSpeed;
        }
    }

    /// <summary>
    /// How far a tree's crowding reaches, for deciding what is forest interior.
    /// </summary>
    /// <remarks>
    /// A little over the spacing the densest band is scattered at, so a cell in the middle of a stand sees
    /// its whole immediate neighbourhood and a cell at the edge sees only the half of one that has trees in
    /// it. Smaller and the interior comes out speckled; larger and the impassable mass swells out past the
    /// trees that justify it.
    /// </remarks>
    internal static float CoverRadius = 2.6f;

    /// <summary>
    /// Trees within <see cref="CoverRadius"/> that make a patch of ground impassable.
    /// </summary>
    /// <remarks>
    /// <b>This is the whole of "a forest is a wall you cut your way into".</b> Individual trunks cannot
    /// block: measured, the densest band scatters at 2.20 m minimum, which leaves a 1.30 m gap between
    /// trunks — bodies physically fit through it, but the navigation raster quantises clearance to rungs of
    /// 0.25/0.75/1.25 and a 1.30 m gap comes out on the 0.25 rung, below the 0.37 every body routes at. Ten
    /// thousand blocking trunks would be ten thousand unroutable holes. So the <em>interior</em> blocks and
    /// the fringe does not, which is contiguous, is what the routing hierarchy wants, and means the only
    /// trees anybody can reach are the ones on the edge.
    /// <para>
    /// Which is the mechanic rather than a limitation: <b>you fell the fringe, and the fringe moves in.</b>
    /// A settlement starts in a clearing and cuts its way out, and the wood line receding is literally the
    /// passable edge moving outward.
    /// </para>
    /// <para>
    /// Three, against a deep-woodland density of about four trees inside that radius and about two at the
    /// edge of a stand. It is the threshold that separates those two, and it is why the near band — 46 trees
    /// at 3.4 m spacing, about two per radius — stays open for the cutters who start there.
    /// </para>
    /// </remarks>
    internal static int CoverTrees = 3;

    /// <summary>What a tree would say about itself, for a report that has to say something.</summary>
    public static string StateOf(in EconomyNode tree) => tree.Stock.Wood >= WoodPerTree - 0.5f
        ? "standing"
        : tree.Stock.Wood > WoodPerTree * 0.34f
            ? "part-cut"
            : "nearly felled";
}

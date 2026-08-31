using System.Numerics;
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

    /// <summary>Seconds a year one pair of hands may spend walking its load in.</summary>
    /// <remarks>
    /// This is the dial the whole stage turns on, and it is a dial about <em>waste</em> rather than about
    /// distance: five hundred and forty seconds a year on the road is a tolerable overhead for one pair of
    /// hands, and anything past that is a settlement that has outgrown its arrangement and should be told
    /// so. The reach follows from it — see <see cref="ReachMetres"/> — instead of being a radius somebody
    /// liked.
    /// <para>
    /// <b>Seconds, not a share of the year, and §112 is why.</b> It was a tenth of a year, which read well
    /// and was on the wrong clock: walking is physical. A cutter makes a fixed number of round trips a year
    /// — the annual tonnage divided by what it can carry — over a map whose distances do not care how long
    /// a year is, so the seconds it spends walking are fixed and it is the <em>share</em> that moves when
    /// the calendar does. Written as a share, tripling the year tripled the reach: 29 m to 87 m, quietly
    /// putting every tree within reach of a store and deleting the receding wood line that lumber camps
    /// exist to answer. Written as seconds, the reach does not move and the share falls to a thirtieth,
    /// which is the true statement — the same walking is a smaller part of a longer year.
    /// </para></remarks>
    internal static float WalkSecondsPerYear = 540f;

    /// <summary>What that walking budget comes to as a share of the year, for the rates that want one.</summary>
    internal static float CutterWalkShare =>
        MathF.Min(0.9f, WalkSecondsPerYear / WorldCalendar.YearSeconds);

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
    /// Derived from <see cref="WalkSecondsPerYear"/> and nothing else: a year affords that many seconds of
    /// walking, a year is <c>WoodPerHandPerYear / carry</c> round trips, so each round trip may be
    /// <c>budget / trips</c> seconds and half of that is the one-way distance at the body's pace. It is
    /// therefore a physical figure all the way down and does not move when the calendar does.
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
            var secondsPerTrip = WalkSecondsPerYear / MathF.Max(0.01f, tripsPerYear);
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
    internal static float CoverRadius = 2.6f;   // a slider — see WoodlandSettings

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
    /// <b>Two, and it was three, and the difference is measured rather than felt.</b> Three closed 5.2% of
    /// the map, which looked from above like a dense wood with speckled patches of wall in it rather than a
    /// wood you have to go round — the trees were dense and the <em>obstacle</em> was not. Two closes 18.8%,
    /// which is about a third of the wooded ground, and reads as forest.
    /// </para>
    /// <para>
    /// The sweep is in <c>--forestcost</c>, and the column that matters is not the closed share but
    /// <em>in reach</em>: how many trees a cutter based at the granary can still get to. It holds at 34 for
    /// every setting from two trees at 2.2 m to four at 3.4, because the near band is scattered at a 3.4 m
    /// spacing floor and a tree on a small closed patch still has open ground beside it. The thing that
    /// would have broken — a settlement whose own thinned stragglers sealed over — does not, and that is
    /// worth having measured rather than assumed.
    /// </para>
    /// </remarks>
    internal static int CoverTrees = 2;

    /// <summary>
    /// Radial lanes left clear through the wood, and how wide.
    /// </summary>
    /// <remarks>
    /// <b>A wood that seals a settlement in is a wood nothing can come out of.</b> Which sounds like safety
    /// and is actually the end of the game: an impassable ring means no raid can reach the granary, so the
    /// one thing Stage E exists to test cannot happen. Tuning the density down until the ring happens to
    /// have a hole in it would work and would be the wrong kind of answer — a property the map needs should
    /// be built rather than hoped for.
    /// <para>
    /// So the scatter leaves <em>rides</em>: straight lanes out from the settlement where no tree is
    /// planted, the way a managed wood has drove roads and firebreaks through it. They give the raid defined
    /// approaches and the defence something to watch, which is the tactical shaping that blocking movement
    /// was for in the first place — and they are the reason a settlement can reach the rest of the map at
    /// all.
    /// </para>
    /// <para>
    /// Ten metres, because cover reaches <see cref="CoverRadius"/> in from the trees on either side and a
    /// lane has to survive that with enough left to route through: ten less two lots of 2.6 leaves about
    /// five metres of open ground, against the 1.5 m the clearance rungs need. Narrower closes over.
    /// </para>
    /// </remarks>
    internal static int Rides = 4;

    internal static float RideWidth = 10f;

    /// <summary>Whether a point lies in one of the rides, and so should stay clear of trees.</summary>
    /// <remarks>
    /// Perpendicular distance to each ride's centre line, which for a ray from the settlement is the
    /// component of the offset across the ride's bearing. Only outward — the sign check stops a ride's
    /// mirror image on the far side counting, so four rides are four lanes rather than two.
    /// </remarks>
    public static bool OnARide(Vector2 offset)
    {
        if (Rides <= 0) return false;
        var half = RideWidth * 0.5f;
        for (var i = 0; i < Rides; i++)
        {
            var angle = i / (float)Rides * MathF.Tau;
            var along = new Vector2(MathF.Cos(angle), MathF.Sin(angle));
            if (Vector2.Dot(offset, along) <= 0f) continue;
            var across = MathF.Abs(offset.X * -along.Y + offset.Y * along.X);
            if (across <= half) return true;
        }

        return false;
    }

    /// <summary>What a tree would say about itself, for a report that has to say something.</summary>
    public static string StateOf(in EconomyNode tree) => tree.Stock.Wood >= WoodPerTree - 0.5f
        ? "standing"
        : tree.Stock.Wood > WoodPerTree * 0.34f
            ? "part-cut"
            : "nearly felled";
}

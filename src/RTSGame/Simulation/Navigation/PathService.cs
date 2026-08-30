using System.Diagnostics;
using System.Numerics;
using RTSGame.Simulation.Persistence;
using RTSGame.Simulation.Placement;
using RTSGame.Simulation.Spatial;
using RTSGame.Simulation.Terrain;

namespace RTSGame.Simulation.Navigation;

/// <summary>Axis-aligned footprint of a piece of static geometry.</summary>
internal readonly record struct StaticBox(Vector2 Minimum, Vector2 Maximum);

internal readonly record struct PathResult(
    Vector2[] Waypoints,
    Vector2 Destination,
    GridCell[] RouteCells);

internal sealed partial class PathService
{
    public bool HasTerrainVariation => terrain.Revision > 0;
    private const float DiagonalCost = 1.41421356f;
    private const float CongestionAvoidanceRadius = 3f;
    /// <summary>How far ahead a stalled body looks for the constriction blocking it.</summary>
    internal static float ApertureSearchDistance = 4.5f;
    /// <summary>
    /// Peak delay charged at the centre of a granted detour's avoidance bubble.
    /// </summary>
    /// <remarks>
    /// Route cost is seconds, and this is one of them: what it says is what going that
    /// way is expected to cost. Walking round a three-metre bubble is about 1.7 m, or
    /// 0.4 s, so half a second summed over the cells crossing it decides the matter
    /// without overstating it. Enforcement, where it is needed, is
    /// <see cref="SegmentAvoidsCongestion"/> forbidding the crossing outright — this
    /// number only has to bias the search early enough not to need forbidding late.
    /// <para>
    /// It was ten seconds: ninety cells of detour, a barrier wearing the costume of a
    /// cost. Restating it honestly on its own made things worse — the pen stopped using
    /// its alternate exits at all — which looked like proof that the barrier was load
    /// bearing. It was not. It was compensating for a congestion field that took 4.5 s
    /// to forget a jam, so honest costs were competing against pressure that had already
    /// gone. With the field's fade shortened to match how fast a jam actually clears
    /// (see CongestionField.DecaySeconds) an honest half second is not merely adequate,
    /// it is better than the barrier was on every measure: two exits instead of four,
    /// routes at 1.06x optimal instead of 1.19x, and the pen clears in 13.4 s
    /// instead of 19.2 s. Two symptoms, one cause.
    /// </para>
    /// </remarks>
    internal static float DetourAvoidanceSeconds = 0.5f * Agents.AgentDefaults.PaceScale;
    /// <summary>
    /// Nominal unit speed used to express route cost as travel time.
    /// </summary>
    /// <remarks>
    /// A unit's own top speed scales every leg of a route by the same factor, so
    /// it cannot change which route is cheapest and one shared field stays
    /// correct for all of them. Congestion delay is the exception — a jam costs
    /// every unit the same wall-clock seconds regardless of how fast it runs — so
    /// once units genuinely differ in speed, the fastest ones will slightly
    /// under-value detours. Noted rather than solved; every unit walks at the same pace
    /// today. Mirrors <c>AgentDefaults.MaximumSpeed</c> rather than referencing it, so the
    /// navigation layer does not need to know what an agent is; if the two ever disagree,
    /// route cost stops being seconds and starts being a number.
    /// </remarks>
    private const float ReferenceSpeed = 1.79f;

    /// <summary>
    /// What a second of queueing is worth to a body at this speed, against a leg of travel.
    /// </summary>
    /// <remarks>
    /// Route costs are seconds at <see cref="ReferenceSpeed"/>, and a body's own speed scales every
    /// leg of travel equally — which is exactly why one field can serve every unit. Congestion is
    /// the one term that does not scale, because a jam costs whoever is in it the same wall clock
    /// however fast they would otherwise be going. So in the field's units a jam is worth
    /// <c>speed / reference</c> of what it is worth to the reference body: to a scout at twice the
    /// pace a twenty-second queue eats twice as much of the journey, and to a cart at two thirds of
    /// it, two thirds as much.
    /// <para>
    /// Concretely, on a choice between ten reference-seconds of jammed road and a thirty-second
    /// clear detour — a dead tie at the reference pace — the scout's real cost is 25.1 s the short
    /// way against 15.3 s round, and the cart's is 36.3 s against 48.8 s. They should disagree, and
    /// unscaled they cannot: they read the same field and it says the routes are equal.
    /// </para>
    /// <para>
    /// Bucketed to an eighth, because the field is cached per distinct value and this is the term
    /// that decides how many of them there are. A villager and a soldier are 5% apart and land in
    /// one bucket, which is the point — and anything within 6% of another unit is not routing
    /// differently for a reason anybody could see.
    /// </para>
    /// </remarks>
    internal static float CongestionSpeedScale(float agentSpeed)
    {
        if (agentSpeed <= 0f || CongestionSpeedScaling <= 0f) return 1f;
        var full = agentSpeed / ReferenceSpeed;
        var blended = 1f + (full - 1f) * Math.Clamp(CongestionSpeedScaling, 0f, 1f);
        return MathF.Round(blended * 8f) / 8f;
    }

    /// <summary>
    /// How much of the speed correction above to apply: 1 all of it, 0 none.
    /// </summary>
    /// <remarks>
    /// A dial rather than a constant because the arithmetic and the measurement disagree, and this
    /// document's rule is that the clock decides. Swept over one wall with a jammed near gap and a
    /// detour from 2 m to 8 m, across four unit types, exactly three of twenty-four runs changed:
    /// the cart stopped detouring at 5 m and arrived <b>2.3 s sooner</b>, and the scout started
    /// detouring at 6 m and arrived <b>1.4 s later</b>. Net nine tenths of a second, one win and
    /// one loss.
    /// <para>
    /// What the loss most likely exposes is a second missing term rather than a wrong first one.
    /// The field describes the jam as it stands, and a jam drains: a fast body reaches the gap
    /// sooner and meets more of the queue than the field predicts, a slow one arrives later and
    /// meets less. That pushes the opposite way to the correction here and partly cancels it, and
    /// neither effect is modelled. Shipped at 1 because charging every unit the same absolute
    /// seconds inside a field denominated in reference-seconds is dimensionally wrong however the
    /// clock comes out, and left on a dial because one geometry is not a measurement.
    /// </para>
    /// <para>
    /// <b>The decisive measurement is Session 6</b>, where many haulers of differing speeds share
    /// routes continuously. That is the workload this term exists for and nothing before it will
    /// settle the number.
    /// </para>
    /// </remarks>
    internal static float CongestionSpeedScaling = 1f;

    /// <summary>Body radius the congestion terms are quoted against, in metres.</summary>
    /// <remarks>
    /// Same device as <see cref="ReferenceSpeed"/>, mirroring <c>AgentDefaults.Radius</c> so this
    /// layer still does not need to know what an agent is.
    /// </remarks>
    private const float ReferenceRadius = 0.37f;

    /// <summary>
    /// What a queue costs a body of this width, against what it costs the reference body.
    /// </summary>
    /// <remarks>
    /// The speed term above rests on a queue costing whoever is in it the same wall clock. Measured
    /// through one 3 m gap behind thirty villagers, that is simply untrue, and not by a little: a
    /// 0.55 m cart lost 0.2 s to it, a 0.37 m villager 0.3 s, and a <b>0.90 m body 16.2 s</b>. A
    /// wide body waits for a hole it fits through, and in a queue of narrow ones most of the holes
    /// are not it.
    /// <para>
    /// So the same jam is worth vastly more to a wagon than to a villager, and until now the router
    /// charged them identically. Scaled linearly on width because the effect is about how many
    /// bodies fit abreast; the measured ratio is far steeper than linear, but a cost term that tried
    /// to reproduce fifty-fold would be a barrier wearing a cost's clothes, and this document has
    /// paid for that mistake twice already.
    /// </para>
    /// <para>
    /// Free, as it happens: the field is already cached per body radius, so a term that varies with
    /// radius adds no field that was not going to be built anyway.
    /// </para>
    /// </remarks>
    internal static float CongestionSizeScale(float agentRadius)
    {
        if (agentRadius <= 0f || CongestionSizeScaling <= 0f) return 1f;
        var full = agentRadius / ReferenceRadius;
        return 1f + (full - 1f) * Math.Clamp(CongestionSizeScaling, 0f, 1f);
    }

    /// <summary>How much of the width correction above to apply: 1 all of it, 0 none.</summary>
    internal static float CongestionSizeScaling = 1f;
    /// <summary>Seconds of delay represented by one unit of measured backpressure.</summary>
    /// <remarks>
    /// Deliberately large. It looks like it should send units on absurd detours,
    /// and would if the input were crowd density — but the field only accumulates
    /// where bodies want to move and are not moving, so a crowd flowing normally
    /// through a gap deposits almost nothing and costs almost nothing. The
    /// coefficient has to be this size for a jam spread thinly over a handful of
    /// cells to outweigh a detour of ten or twenty. Measured: at 1.5 a symmetric
    /// two-gap wall still sent 29 of 30 units through one gap; at this value they
    /// split, and every scenario's completion time improved rather than regressed.
    /// </remarks>
    /// <summary>Cells of delay one unit of measured backpressure stands for.</summary>
    /// <remarks>
    /// 3.6 was the balance 0.40 s struck at the old run, and it held until the avoidance
    /// horizons were re-based to the walk as well. Those two mechanisms compete for the same
    /// job: a body that sees a jam coming early enough gets out of its way, and one that does
    /// not stalls and deposits pressure. Lengthening the look-ahead therefore meant less
    /// pressure for the same crowd, and the pen stopped splitting across its exits — the exact
    /// behaviour this coefficient exists to produce. 5.4 restores it. 7.2 was also measured and
    /// is too far: it breaks the narrow-chokepoint test, where the crowd starts detouring
    /// around a queue it should simply join.
    /// </remarks>
    internal static float CongestionCellsPerPressure = 8.06f;

    /// <summary>Seconds of delay represented by one unit of measured backpressure.</summary>
    /// <remarks>
    /// Derived from the cell crossing time rather than written down, because what a queue
    /// costs you is the number of bodies ahead times how long each takes to clear the cell —
    /// and how long one takes to clear a cell is exactly <see cref="SecondsPerCell"/>. Held
    /// as a flat 0.40 s, it silently became a different number the moment the body slowed
    /// down: travel got three times dearer in seconds while jams stayed the same price, and
    /// the crowd stopped going round them. The pen scenario funnelled all thirty units
    /// through one exit, which is the failure this coefficient was tuned to prevent in the
    /// first place.
    /// <para>
    /// 3.6 cells is the same balance 0.40 s struck at 4.5 m/s. The tuning underneath it is
    /// unchanged and its history stands: it looks like it should send units on absurd
    /// detours, and would if the input were crowd density — but the field only accumulates
    /// where bodies want to move and are not moving, so a crowd flowing normally through a
    /// gap deposits almost nothing and costs almost nothing.
    /// </para>
    /// </remarks>
    private float CongestionSecondsPerPressure => CongestionCellsPerPressure * SecondsPerCell;
    /// <summary>Extra seconds charged per metre of climb.</summary>
    /// <remarks>
    /// <b>This is where slope costs, and it is the only place it does.</b> Charged per edge on the fine
    /// field, against the height actually climbed, so it is path-dependent in the way that matters: a route
    /// along a contour pays almost nothing where a direct climb pays the lot, and a switchback is therefore
    /// cheaper than going straight up without anybody modelling switchbacks. A slope cost held per cell
    /// cannot express that at all, which is why one was added to the rasteriser during §54's first
    /// milestone and taken straight back out again — see the note there.
    /// <para>
    /// At the constants in force it is a 30% penalty on a tenth grade and 63% on a fifth, which is steeper
    /// than a hiker's rule of thumb and appropriate for people carrying things.
    /// </para>
    /// </remarks>
    internal static float ClimbSecondsPerMetre = 1.68f;
    /// <summary>
    /// Nominal turn rate, in radians per second, that route cost prices turning at.
    /// </summary>
    /// <remarks>
    /// The same device as <see cref="ReferenceSpeed"/> and carrying the same caveat: one
    /// shared field stays correct for every unit only while they all turn at the same
    /// rate. Mirrors <c>AgentDefaults.MaximumTurnSpeed</c> rather than referencing it, so
    /// the navigation layer does not need to know what an agent is; if the two ever
    /// diverge, routes will be planned for a body that does not exist.
    /// </remarks>
    internal static float ReferenceTurnSpeed = 3.03f;
    /// <summary>Seconds to cross one cell of open ground at the reference speed.</summary>
    private float SecondsPerCell => grid.Transform.CellSize / ReferenceSpeed;

    /// <summary>
    /// Seconds of climbing along a straight leg, sampled, or nothing at all on ground with no relief.
    /// </summary>
    /// <remarks>
    /// <b>The term the hierarchy was missing, and the measurement that found it is worth keeping.</b> The
    /// fine field charges climb per edge; a rectangle crossing was priced as a straight line on the flat.
    /// With relief that made the abstract layer cheaper than the routes it was abstracting — measured
    /// against the exact field at 0.977 of it at three metres of amplitude, 0.955 at six, 0.915 at twelve
    /// and 0.845 at twenty-four. <see cref="RectangleFlowField.Expand"/> already states the invariant that
    /// breaks: an over-estimate is the safe direction, and this layer must never claim a route is cheaper
    /// than it is, or a body sets off toward a shortcut that does not exist.
    /// <para>
    /// Sampled rather than taken end to end, because a leg over a hill climbs and then descends and its
    /// endpoints can be at the same height: end-to-end would charge a summit crossing nothing. A sample
    /// every few cells catches the shape of it, and the cost is only paid where there is relief to pay it
    /// on — flat ground returns without a single height read, which is what keeps every calibrated
    /// scenario in the suite bit-identical.
    /// </para>
    /// </remarks>
    internal float ClimbSecondsAlong(float fromX, float fromZ, float toX, float toZ)
    {
        if (!grid.HasRelief) return 0f;
        var dx = toX - fromX;
        var dz = toZ - fromZ;
        var cells = MathF.Sqrt(dx * dx + dz * dz);
        if (cells < 0.5f) return 0f;
        // One sample every four cells — two metres at the half-metre grid — which resolves a landform
        // hundreds of metres across many times over, and capped so a leg the width of the map is bounded.
        var samples = Math.Clamp((int)MathF.Ceiling(cells / 4f), 1, 48);
        var climbed = 0f;
        var last = HeightAtPoint(fromX, fromZ);
        for (var i = 1; i <= samples; i++)
        {
            var t = i / (float)samples;
            var here = HeightAtPoint(fromX + dx * t, fromZ + dz * t);
            climbed += MathF.Abs(here - last);
            last = here;
        }

        return climbed * ClimbSecondsPerMetre;
    }

    private float HeightAtPoint(float x, float z)
    {
        var cell = new GridCell(
            Math.Clamp((int)MathF.Floor(x), 0, grid.Width - 1),
            Math.Clamp((int)MathF.Floor(z), 0, grid.Height - 1));
        return grid.HeightAt(cell);
    }
    /// <summary>Travel time an unreachable cell reports when sampling the flow field.</summary>
    /// <remarks>
    /// Not a predicted cost but a boundary condition: it exists so bilinear sampling of
    /// the cost field near a wall leans away from it instead of returning infinity and
    /// poisoning the gradient with a NaN. It has to outweigh any genuine cost difference
    /// across one probe ring, and stay finite.
    /// <para>
    /// A hundred and eight cells looks like far more than that argument needs, and it is
    /// not: how steeply the field leans away from a wall is also what decides how wide a
    /// body swings round it. Re-measured at 20 / 50 / 108 after the congestion tuning —
    /// 20 gives marginally shorter routes (1.04x against 1.06x) and is 2.5 s slower to
    /// clear the pen, and cost is denominated in time, so this wins. Stated in cells
    /// rather than seconds because it is a boundary value, not a claim about how long
    /// blocked ground takes to cross.
    /// </para>
    /// </remarks>
    private float BlockedFlowSeconds => BlockedFlowCells * SecondsPerCell;

    /// <summary>Cells of open ground the blocked-cell boundary value is worth.</summary>
    internal static float BlockedFlowCells = 108f;
    /// <summary>How many past congestion revisions stay resident for staggered adoption.</summary>
    private const int RetainedCongestionRevisions = 3;
    /// <summary>Directions probed when reading the cost field's downhill.</summary>
    private const int FlowProbeCount = 16;
    /// <summary>How much further than the straight line a slot may be, before it is judged unconnected.</summary>
    /// <summary>
    /// Delay charged for taking a bottleneck another member of the same group has
    /// already reserved — what waiting a turn there is expected to cost.
    /// </summary>
    internal static float BottleneckReservationSeconds = 0.5f * Agents.AgentDefaults.PaceScale;
    private const float SlotDetourTolerance = 2.0f;
    private const float SlotDetourSlack = 0.45f;
    private const float Epsilon = 0.00001f;
    private static readonly GridCell[] NeighborOffsets =
    {
        new(1, 0), new(-1, 0), new(0, 1), new(0, -1),
        new(1, 1), new(1, -1), new(-1, 1), new(-1, -1),
    };

    /// <summary>
    /// Seconds lost to changing heading between two step directions, indexed
    /// <c>[from, to]</c>, for a body arcing through the turn at speed and for one
    /// pivoting on the spot.
    /// </summary>
    /// <remarks>
    /// Route cost is time, and turning takes time, but until now it was free here: a
    /// hairpin priced exactly like a straight line. That is why making bodies turn more
    /// slowly did not make them prefer routes with gentler corners — the router had no
    /// idea turning had become more expensive. It also means a route could be optimal on
    /// paper and unfollowable in practice, which is the most expensive kind of wrong,
    /// because the body discovers it by failing.
    /// <para>
    /// Both tables are derived, not tuned. A body that can arc through a turn keeps its
    /// speed and loses only the difference between the arc it travels and the straight
    /// line it wanted: at turn rate w that is <c>(θ - 2·sin(θ/2)) / w</c> seconds, which
    /// is almost nothing for a gentle bend and rises sharply toward a reversal. A body
    /// that cannot arc — because the walls are closer than its turning circle — has to
    /// stop and turn, costing the full <c>θ / w</c>. Which of the two applies is a
    /// property of the ground, so the cost is interpolated by local clearance.
    /// </para>
    /// </remarks>
    // Radians, not seconds: the turn rate they are divided by is live-tunable, and baking
    // it in made the tables silently stale the moment somebody moved the slider.
    private static readonly float[,] ArcTurnRadians = BuildTurnTable(pivot: false);
    private static readonly float[,] PivotTurnRadians = BuildTurnTable(pivot: true);

    private static float[,] BuildTurnTable(bool pivot)
    {
        var table = new float[NeighborOffsets.Length, NeighborOffsets.Length];
        for (var from = 0; from < NeighborOffsets.Length; from++)
        for (var to = 0; to < NeighborOffsets.Length; to++)
        {
            var a = Vector2.Normalize(new Vector2(NeighborOffsets[from].X, NeighborOffsets[from].Z));
            var b = Vector2.Normalize(new Vector2(NeighborOffsets[to].X, NeighborOffsets[to].Z));
            var theta = MathF.Acos(Math.Clamp(Vector2.Dot(a, b), -1f, 1f));
            table[from, to] = pivot ? theta : theta - 2f * MathF.Sin(theta * 0.5f);
        }
        return table;
    }

    /// <summary>
    /// Seconds charged for arriving at <paramref name="cell"/> heading
    /// <paramref name="fromDirection"/> and leaving it heading <paramref name="toDirection"/>.
    /// </summary>
    private float TurnCost(int fromDirection, int toDirection, GridCell cell, float agentRadius)
    {
        // Either heading unrecorded means there is no turn to charge: the route either
        // starts here and is free to face anywhere, or ends here and turns no further.
        if (fromDirection < 0 || toDirection < 0 || fromDirection == toDirection) return 0f;
        var turnRate = MathF.Max(0.05f, ReferenceTurnSpeed);
        var arc = ArcTurnRadians[fromDirection, toDirection] / turnRate;
        var pivot = PivotTurnRadians[fromDirection, toDirection] / turnRate;
        if (pivot <= arc) return arc;

        // Whether the body can carry its speed through the turn depends on whether its
        // turning circle fits. Same clearance ramp the congestion field uses to decide
        // whether a stalled body is an obstruction or a nuisance.
        var clearance = grid.Clearance(cell);
        // Tried and refused: flooring this at a body diameter, on the reasoning that a body
        // needs room to turn whatever its speed and the ramp had collapsed from two metres of
        // clearance at a run to ninety centimetres at a walk. It reads well and measures
        // badly — the pen went from zero dead stops to fifteen and the one-cell gate from
        // twenty to fifty-three, while mean clearance in the pen actually *fell*. Charging
        // more for turning in tight ground makes routes avoid the gaps rather than prefer
        // room, and bodies pile up at the ones that are left.
        var turningRadius = ReferenceSpeed / ReferenceTurnSpeed;
        var tight = agentRadius + turningRadius * 0.5f;
        var open = agentRadius + turningRadius * 1.5f;
        var confinement = clearance <= tight
            ? 1f
            : clearance >= open
                ? 0f
                : 1f - (clearance - tight) / (open - tight);
        return float.Lerp(arc, pivot, confinement);
    }

    private readonly TerrainMap terrain;
    private readonly PlacementGrid placement;
    private readonly NavigationGrid grid;
    private readonly CongestionField congestion;
    private readonly Dictionary<(int Goal, int Radius, int Speed, int Nav, int Congestion, bool Turns), RectangleFlowField> flowFields = new();
    // Newest field per goal, regardless of congestion revision, so the next revision can
    // adopt whatever of it is still valid. Separate from the retention table above, which
    // exists for a different reason entirely — see GetFlowField.
    private readonly Dictionary<(int Goal, int Radius, int Speed, int Nav, bool Turns), RectangleFlowField> latestFields = new();
    /// <summary>
    /// Per-cell memo of whether a body of a given radius fits at the cell centre,
    /// as 0 unknown / 1 admitted / 2 refused, keyed by radius in centimetres.
    /// </summary>
    /// <remarks>
    /// Every route search asks this of eight neighbours per expansion, and the
    /// honest answer costs a body-shaped terrain sample — nine grade probes, each
    /// four bilinear height lookups — plus a placement test. That is the single
    /// most expensive thing A* did, and it was recomputing an answer that cannot
    /// change until the navigation raster does: cell centres are a fixed, finite
    /// set of positions. Keyed on the raster revision, which is bumped by terrain
    /// edits and by placement edits alike, so the memo cannot outlive its inputs.
    /// </remarks>
    private readonly Dictionary<int, byte[]> cellCenterAdmission = new();
    private int cellCenterAdmissionRevision = -1;
    private readonly Dictionary<(int Cell, int Radius), Vector2> passageAxes = new();
    private int passageAxisRevision = -1;
    private int[] searchCameFrom = Array.Empty<int>();
    private float[] searchCost = Array.Empty<float>();
    private bool[] searchClosed = Array.Empty<bool>();
    private int[] searchArrival = Array.Empty<int>();
    private readonly PriorityQueue<GridCell, float> searchQueue = new();

    /// <summary>Flow fields built since construction. A proxy for route churn:
    /// if this climbs steeply the congestion field is invalidating routes faster
    /// than the crowd can act on them.</summary>
    /// <summary>Cells closed by the cell-level A*, cumulatively, and the worst single search.</summary>
    public long PathExpansions { get; private set; }

    public long PathExpansionsWorst { get; private set; }

    /// <summary>Searches that emptied the open set without reaching the goal — i.e. exhausted the map.</summary>
    public long PathFailures { get; private set; }

    /// <summary>Searches stopped by the expansion budget, which returned a partial route. See ExpansionBudget.</summary>
    public long PathBudgetStops { get; private set; }

    /// <summary>Cells in the navigation grid, so an expansion count can be read as a share of the map.</summary>
    public int GridCells => grid.Width * grid.Height;

    public int FlowFieldBuilds { get; private set; }

    internal long FieldSetupTicks;

    /// <summary>
    /// What routing has spent, split into the three places an order's time can go.
    /// </summary>
    /// <remarks>
    /// Cumulative, so a caller measuring one order snapshots and subtracts. Milliseconds rather than ticks
    /// because every consumer is a report, and the counts travel with them: 340 ms over one mesh build and
    /// 340 ms over forty tile fills are different problems wearing one number.
    /// </remarks>
    public (double MeshMs, int MeshBuilds, int MeshCacheHits, int MeshRectangles,
            double TileMs, long TileFills, double FieldMs, int Fields) RoutingCost =>
        (MeshBuildTicks * 1000.0 / Stopwatch.Frequency, MeshBuilds, MeshCacheHits, MeshRectangles,
         TileFillTicks * 1000.0 / Stopwatch.Frequency, TileRefinements,
         FieldSetupTicks * 1000.0 / Stopwatch.Frequency, FlowFieldBuilds);
    /// <summary>Tiles adopted whole from the previous field for the same goal.</summary>
    public long InheritedTiles { get; private set; }
    /// <summary>A* queries served, for attributing pathfinding cost.</summary>
    public long PathQueries { get; private set; }

    /// <summary>Congestion revision currently published to routing.</summary>
    public int CongestionRevision => congestion.Revision;

    public PathService(
        TerrainMap terrain,
        PlacementGrid placement,
        NavigationGrid grid,
        CongestionField congestion)
    {
        this.terrain = terrain;
        this.placement = placement;
        this.grid = grid;
        this.congestion = congestion;
        partition = new RegionPartition(grid.Transform);
    }

    /// <summary>
    /// Extra traversal cost for routing through cells where movement is failing.
    /// </summary>
    /// <remarks>
    /// This is what makes a crowd spread across alternative routes. As one exit
    /// saturates, the pressure deposited by the bodies stalled in it raises its
    /// cost, and the next route built prefers a longer but clear way round. It is
    /// predictive at the group level rather than reactive per agent: nobody has to
    /// jam and then individually replan.
    /// </remarks>
    private float CongestionCost(
        GridCell from,
        GridCell to,
        float turnSeconds,
        float speedScale,
        float agentRadius)
    {
        // Almost every edge of almost every search crosses ground nobody is stuck on, and
        // on that ground this whole function is a multiplication by zero — two square
        // roots for the directional factors, a normalise, and a clearance ramp, to arrive
        // at nothing. Since Session 1 the field knows exactly which cells hold pressure,
        // so the common case can be two array reads. Not an approximation: zero pressure
        // gives zero cost through every branch below.
        var here = congestion.At(from);
        var there = congestion.At(to);
        if (here <= 0f && there <= 0f) return 0f;

        var travel = grid.CellCenter(to) - grid.CellCenter(from);
        if (travel.LengthSquared() > 0.0001f) travel = Vector2.Normalize(travel);
        var pressure = here * congestion.DirectionalFactor(from, travel) +
                       there * congestion.DirectionalFactor(to, travel);
        return pressure * 0.5f * CongestionSecondsPerPressure * ManoeuvreAmplification(turnSeconds) *
               speedScale * CongestionSizeScale(agentRadius);
    }

    /// <summary>
    /// How much longer a queue takes to drain through ground that is awkward to
    /// manoeuvre through, as a multiple of the same queue on a straight run.
    /// </summary>
    /// <remarks>
    /// Waiting behind a queue costs the queue's length times what one body takes to get
    /// clear — and what one body takes to get clear is crossing the cell <em>plus</em>
    /// whatever turn the cell demands. Charging a flat rate per unit of pressure says
    /// every jam drains at the same speed, which is exactly wrong at the place jams
    /// happen: a one-cell gap set at right angles to the approach makes thirty bodies
    /// pivot, one at a time, and none of that appears in the cost of going that way.
    /// <para>
    /// This is the group-level reading of a turn, and it is what makes tightening the
    /// turn rate divert traffic. A body's own turn is far too cheap to matter — 0.039 s
    /// in the open, a third of a cell, against alternate routes sixty cells further — so
    /// charging it individually can never move a route choice and must not be inflated
    /// until it does. Multiplied by the number of bodies that have to perform it, the
    /// same honest figure is decisive: an open bend amplifies a wait by about 1.35x, a
    /// pivot in a gap by 4.5x, and halving the turn rate takes that to 8x.
    /// </para>
    /// <para>
    /// It costs nothing where nothing is queueing, which is the property that matters —
    /// pressure only accumulates where bodies want to move and are not moving, so an
    /// empty awkward corner stays as cheap as it should be.
    /// </para>
    /// <para>
    /// Charged at full derived strength. A scaling factor was measured at 1.0 / 0.5 / 0.25
    /// against no amplification at all, and full strength is decisively the best on the
    /// metric this exists to move — dead stops at a one-cell gate read 5 / 23 / 72 / 56
    /// across those four — while route length and direction stability are flat across all
    /// of them. So there is no knob here; the derivation is the value.
    /// </para>
    /// </remarks>
    private float ManoeuvreAmplification(float turnSeconds) =>
        turnSeconds <= 0f ? 1f : 1f + turnSeconds / SecondsPerCell;

    /// <summary>
    /// Whether a formation slot is genuinely reachable from its command point.
    /// </summary>
    /// <remarks>
    /// A slot only has to be standable to be offered, which is not the same as
    /// being connected: a formation is routinely wider than the enclosure it is
    /// sent into, so slots land on perfectly good ground on the far side of a
    /// wall. The member then paths out of the enclosure, around, and back in,
    /// which is where the long looping routes came from. Comparing the field's
    /// travel time against the straight-line time catches that for free — the
    /// field is built for the group's transit anyway, and both are in seconds.
    /// </remarks>
    /// <summary>
    /// Best possible travel time from a point to a goal, in seconds, or false if
    /// no route exists. Diagnostics only — this is the denominator for judging
    /// how much further than necessary a body actually walked.
    /// </summary>
    /// <remarks>
    /// Deliberately measured without charging for turns, even though routing does charge
    /// for them. The number it feeds is <c>walked / optimal</c>, a ratio of two distances,
    /// and once the denominator started including turn time it stopped being one: the
    /// figure fell as low as 0.90, a body apparently walking less far than the shortest
    /// route, which is what a corrupted metric looks like rather than a good result. It
    /// would also have silently flattered the turn-cost change and broken comparability
    /// with every baseline recorded before it.
    /// </remarks>
    /// <summary>
    /// The record of work this router has done. Not the caches — those are keyed by revisions and
    /// rebuild themselves — just the totals.
    /// </summary>
    /// <remarks>
    /// Saved because a loaded career should report the same lifetime totals as the one it continues,
    /// and because the determinism check reads them as a canary: two runs of identical code must do
    /// identical work, and a decision that diverged before it moved anybody shows up in a counter
    /// first. A load that reset them to zero would fail that check at the instant of loading, which
    /// is how this came to be here.
    /// </remarks>
    /// <summary>
    /// Throws away everything memoised about routes, putting a running world into the state a
    /// freshly loaded one is in.
    /// </summary>
    /// <remarks>
    /// Not an optimisation and not a reset — it exists so that a save can be tested honestly. A cost
    /// field is built once and then <em>refined tile by tile as things ask about it</em>, so what it
    /// holds depends on the order the questions arrived in, and that order is not state anybody could
    /// write down. A world reloaded from a save therefore rebuilds its fields from scratch and gets
    /// answers that can differ in the last bit of a float from the ones the running world had — which
    /// showed up as exactly one unit in the last place of a body's facing, one tick after a load.
    /// <para>
    /// So the honest claim a save can make is not "the two processes agree" but "the two agree once
    /// both have forgotten what they had been asked". Every peer loading the same save forgets
    /// equally, which is what keeps lockstep intact across one; and a single career continuing from a
    /// save is a new lineage regardless.
    /// </para>
    /// </remarks>
    internal void DropRouteCaches()
    {
        flowFields.Clear();
        latestFields.Clear();
        cellCenterAdmission.Clear();
        cellCenterAdmissionRevision = -1;
        passageAxes.Clear();
        passageAxisRevision = -1;
    }

    internal void WriteCounters(WorldWriter writer)
    {
        writer.Int(FlowFieldBuilds);
        writer.Long(PathQueries);
        writer.Long(RegionSearches);
        writer.Long(TileRefinements);
    }

    internal void ReadCounters(WorldReader reader)
    {
        FlowFieldBuilds = reader.Int();
        PathQueries = reader.Long();
        RegionSearches = reader.Long();
        TileRefinements = reader.Long();
    }

    public bool TryOptimalTravelTime(Vector2 goalPosition, Vector2 from, float agentRadius, out float seconds)
    {
        seconds = 0f;
        if (!grid.TryWorldToCell(goalPosition, out var goalCell)) return false;
        if (!grid.TryWorldToCell(from, out var fromCell)) return false;
        var resolved = grid.IsWalkable(goalCell, agentRadius)
            ? goalCell
            : FindNearestWalkable(goalCell, agentRadius);
        if (resolved is not { } goal) return false;
        var costs = GetFlowField(goal, agentRadius, congestion.Revision, chargeTurns: false);
        // Both ends, not just the goal. Once buildings occupy the cells they stand on, the natural way
        // to ask "how far is my granary from that house" is between two points that are both inside
        // walls — and a cost read at a blocked cell is infinite, so every catchment in the world would
        // come back out of reach. A query from inside a building means from the ground beside it.
        //
        // Resolved *after* the field is obtained, and that ordering is load-bearing. Getting a field has
        // a side effect — it builds and caches one — so resolving the start first meant this query
        // built fields in cases where it used to give up, which changed what later route decisions found
        // in the cache and moved the pen benchmark's clearance and reroute counts. A measurement that
        // perturbs what it measures is worth one line of care to avoid.
        var start = grid.IsWalkable(fromCell, agentRadius)
            ? fromCell
            : FindNearestWalkable(fromCell, agentRadius);
        if (start is not { } standing) return false;
        var cost = costs.CostAt(standing);
        if (!float.IsFinite(cost)) return false;
        seconds = cost;
        return true;
    }

    public bool IsSlotReachable(Vector2 target, Vector2 slot, float agentRadius)
    {
        if (!grid.TryWorldToCell(target, out var goalCell)) return false;
        if (!grid.TryWorldToCell(slot, out var slotCell)) return false;
        var resolved = grid.IsWalkable(goalCell, agentRadius)
            ? goalCell
            : FindNearestWalkable(goalCell, agentRadius);
        if (resolved is not { } goal) return false;

        var costs = GetFlowField(goal, agentRadius, congestion.Revision);
        var cost = costs.CostAt(slotCell);
        if (!float.IsFinite(cost)) return false;
        var direct = Vector2.Distance(slot, target) / ReferenceSpeed;
        return cost <= direct * SlotDetourTolerance + SlotDetourSlack;
    }

    public PathResult? FindPath(
        Vector2 start,
        Vector2 requestedGoal,
        float agentRadius,
        Vector2? congestionAvoidanceCenter = null,
        float[]? additionalNavigationCosts = null,
        float agentSpeed = 0f)
    {
        PathQueries++;
        requestedGoal = terrain.ClampPosition(requestedGoal, agentRadius + BodyFootprint.NavigationMargin);
        if (!grid.TryWorldToCell(start, out var startCell) || !grid.TryWorldToCell(requestedGoal, out var requestedCell))
        {
            return null;
        }
        var startCellCenter = grid.CellCenter(startCell);
        var startCellIsConnected = grid.IsWalkable(startCell, agentRadius) &&
                                   IsInitialBodyStepClear(start, startCellCenter, agentRadius);
        var pathStartCell = startCellIsConnected
            ? startCell
            : FindRecoveryStart(
                startCell,
                agentRadius,
                start,
                requestedGoal,
                grid.Transform.CellSize * 2.25f);
        if (pathStartCell is not { } resolvedStartCell) return null;
        var startWasAdjusted = resolvedStartCell != startCell;

        if (additionalNavigationCosts is null &&
            terrain.Revision == 0 &&
            !startWasAdjusted &&
            grid.IsWalkable(requestedCell, agentRadius) &&
            IsDirectPathClear(start, requestedGoal, agentRadius) &&
            SegmentAvoidsCongestion(start, requestedGoal, congestionAvoidanceCenter))
        {
            return new PathResult(
                new[] { requestedGoal },
                requestedGoal,
                new[] { startCell, requestedCell });
        }

        var goalCell = grid.IsWalkable(requestedCell, agentRadius)
            ? requestedCell
            : FindNearestWalkable(requestedCell, agentRadius);
        if (goalCell is not { } goal) return null;
        var cells = FindCellPath(
            resolvedStartCell,
            goal,
            agentRadius,
            congestionAvoidanceCenter,
            additionalNavigationCosts,
            CongestionSpeedScale(agentSpeed));
        if (cells is null) return null;

        var destination = goal == requestedCell ? requestedGoal : grid.CellCenter(goal);
        var waypoints = SmoothPath(
            start,
            destination,
            cells,
            agentRadius,
            congestionAvoidanceCenter,
            additionalNavigationCosts,
            includeFirstCell: startWasAdjusted || terrain.Revision > 0);
        return waypoints.Length == 0 ||
               !IsInitialBodyStepClear(start, waypoints[0], agentRadius)
            ? null
            : new PathResult(waypoints, destination, cells.ToArray());
    }

    public void ReserveGroupRoute(
        PathResult path,
        float agentRadius,
        float[] routeCosts,
        float destinationExclusionRadius = 0f)
    {
        if (routeCosts.Length != grid.Width * grid.Height)
        {
            throw new ArgumentException("Group route-cost dimensions do not match the navigation grid.");
        }

        foreach (var cell in path.RouteCells)
        {
            if (!grid.Contains(cell)) continue;
            if (destinationExclusionRadius > 0f &&
                Vector2.Distance(grid.CellCenter(cell), path.Destination) <= destinationExclusionRadius)
            {
                continue;
            }
            var clearanceMargin = grid.Clearance(cell) - agentRadius;
            // Shared intent should not fan an army across an open field. Reserve
            // actual bottlenecks only; the arrival system handles convergence at
            // the common destination without manufacturing private approach lanes.
            var bottleneckSeconds = clearanceMargin < 0.80f ? BottleneckReservationSeconds : 0f;
            routeCosts[grid.Transform.Index(cell)] += bottleneckSeconds;
        }
    }

    public bool IsDirectPathClear(Vector2 start, Vector2 end, float agentRadius)
    {
        var expansion = agentRadius + BodyFootprint.NavigationMargin;
        if (!terrain.Contains(start, expansion) || !terrain.Contains(end, expansion)) return false;
        if (!IsContinuousBodyPathClear(start, end, agentRadius)) return false;

        if (terrain.Revision > 0)
        {
            var segment = end - start;
            var distance = segment.Length();
            var samples = Math.Max(1, (int)MathF.Ceiling(distance / (grid.Transform.CellSize * 0.40f)));
            GridCell? previousCell = null;
            for (var sample = 0; sample <= samples; sample++)
            {
                var position = Vector2.Lerp(start, end, sample / (float)samples);
                if (!grid.TryWorldToCell(position, out var cell) || !grid.IsWalkable(cell, agentRadius)) return false;
                if (previousCell is { } previous && previous != cell && !CanTraverse(previous, cell, agentRadius)) return false;
                previousCell = cell;
            }
        }

        // Segment against block, not just the sampled points. This looks like a
        // duplicate of the swept check above and is not: it uses a slightly wider
        // margin, and removing it let smoothed routes hug blocks closely enough to
        // fail both the wall-detour and chokepoint-separation tests. Only cells the
        // segment can actually reach are consulted.
        var halfCell = placement.Transform.CellSize * 0.5f + expansion;
        PlacementCellRange(
            Vector2.Min(start, end), Vector2.Max(start, end), halfCell,
            out var low, out var high);
        for (var z = low.Z; z <= high.Z; z++)
        for (var x = low.X; x <= high.X; x++)
        {
            var cell = new GridCell(x, z);
            if (!placement.IsOccupied(cell)) continue;
            var center = placement.Transform.CellCenter(cell);
            var minimum = center - new Vector2(halfCell);
            var maximum = center + new Vector2(halfCell);
            if (SegmentIntersectsAabb(start, end, minimum, maximum)) return false;
        }
        return true;
    }

    public bool IsStepClear(Vector2 start, Vector2 end, float agentRadius)
    {
        var expansion = agentRadius + BodyFootprint.NavigationMargin;
        if (!terrain.Contains(end, expansion) || !terrain.CanTraverse(start, end)) return false;
        if (!terrain.IsBodyTraversable(end, agentRadius)) return false;
        // Simulation steps are short enough that static collision cannot be
        // tunneled. Test the body's final footprint here; expanding an entire
        // segment against placement boxes makes a valid path stick forever on
        // the exact clearance boundary chosen by A*.
        return IsPositionFreeOfPlacement(end, agentRadius);
    }

    private bool IsContinuousBodyPathClear(
        Vector2 start,
        Vector2 end,
        float agentRadius,
        bool allowPlacementEscape = false)
    {
        // A body spawned or shoved inside a block must be allowed to walk out of
        // it. Validating that first step with the same predicate that declares
        // the start invalid rejects every candidate, so the unit is handed no
        // route at all and stands in the wall forever. When escaping, the
        // placement test becomes a segment-level "strictly moving outward" rule.
        if (allowPlacementEscape &&
            !SegmentAvoidsPlacement(start, end, agentRadius, allowEscapeFromStart: true))
        {
            return false;
        }

        var distance = Vector2.Distance(start, end);
        // Spacing is a fraction of the body, not of the grid. The old value worked
        // out near four centimetres, which was chosen to catch terrain seams
        // thinner than a navigation cell — a real hazard when height fields had
        // vertical steps in them, and eight times more sampling than a smooth
        // one needs. A body cannot pass through anything it does not overlap at
        // a third of its own radius.
        var sampleSpacing = MathF.Max(agentRadius * 0.35f, 0.05f);
        var samples = Math.Max(1, (int)MathF.Ceiling(distance / sampleSpacing));
        var previous = start;
        for (var sample = 1; sample <= samples; sample++)
        {
            var position = Vector2.Lerp(start, end, sample / (float)samples);
            if (!terrain.IsBodyTraversable(position, agentRadius) ||
                !terrain.CanTraverse(previous, position) ||
                !allowPlacementEscape && !IsPositionFreeOfPlacement(position, agentRadius))
            {
                return false;
            }
            previous = position;
        }
        return true;
    }

    public bool IsContinuousStepClear(
        Vector2 start,
        Vector2 end,
        float agentRadius,
        bool allowPlacementEscape = false)
    {
        var expansion = agentRadius + BodyFootprint.NavigationMargin;
        return terrain.Contains(start, expansion) &&
               terrain.Contains(end, expansion) &&
               IsContinuousBodyPathClear(start, end, agentRadius, allowPlacementEscape);
    }

    private bool IsInitialBodyStepClear(Vector2 start, Vector2 end, float agentRadius)
    {
        var escaping = !IsPositionFreeOfPlacement(start, agentRadius);
        var offset = end - start;
        var distance = offset.Length();
        if (distance <= 0.0001f) return IsPositionNavigable(start, agentRadius);
        var prefix = start + offset / distance * MathF.Min(0.25f, distance);
        return IsContinuousStepClear(start, prefix, agentRadius, escaping) &&
               (distance <= 0.25f || IsContinuousStepClear(start, end, agentRadius, escaping));
    }

    /// <summary>
    /// Static geometry within <paramref name="reach"/> of a point, as boxes.
    /// </summary>
    /// <remarks>
    /// The velocity solve needs walls as constraints rather than as a veto applied to
    /// its answer, and this is how it sees them. Navigation cells are the source
    /// because they are the one representation carrying both kinds of static
    /// obstruction — placed blocks and impassable or too-steep ground — as the same
    /// fact, and because the placement grid is cell-aligned with them, so a block is
    /// described exactly rather than approximately.
    /// <para>
    /// Boxes are reported at full cell extent, deliberately not inset the way
    /// <see cref="IsPositionFreeOfPlacement"/> insets its test. That inset is a
    /// tolerance on a point test and not a description of geometry: applying it here
    /// detaches every block from its neighbour by five centimetres, so a solid wall
    /// acquires a slot every cell and the solve steers bodies at gaps that are not
    /// there. Measured, that alone cost four times the dead stops at a one-cell gate.
    /// A wall is contiguous; the tolerance belongs to whoever measures against it.
    /// </para>
    /// <para>
    /// Runs of blocked cells along a row are merged, so a long wall costs the solver
    /// one constraint per row rather than one per cell.
    /// </para>
    /// </remarks>
    public int GatherBlockingBoxes(Vector2 center, float reach, Span<StaticBox> boxes)
    {
        var transform = grid.Transform;
        var lowLocal = (center - new Vector2(reach) - transform.Origin) / transform.CellSize;
        var highLocal = (center + new Vector2(reach) - transform.Origin) / transform.CellSize;
        var lowX = (int)MathF.Floor(lowLocal.X);
        var lowZ = (int)MathF.Floor(lowLocal.Y);
        var highX = (int)MathF.Ceiling(highLocal.X);
        var highZ = (int)MathF.Ceiling(highLocal.Y);

        var count = 0;
        for (var z = lowZ; z <= highZ && count < boxes.Length; z++)
        {
            var runStart = int.MinValue;
            for (var x = lowX; x <= highX + 1; x++)
            {
                // One past the end closes any open run. Cells outside the grid read
                // as blocked, which is correct: the map edge is a wall.
                var blocking = x <= highX && grid.IsBlocked(new GridCell(x, z));
                if (blocking)
                {
                    if (runStart == int.MinValue) runStart = x;
                    continue;
                }
                if (runStart == int.MinValue) continue;
                transform.CellBounds(new GridCell(runStart, z), out var minimum, out _);
                transform.CellBounds(new GridCell(x - 1, z), out _, out var maximum);
                boxes[count++] = new StaticBox(minimum, maximum);
                runStart = int.MinValue;
                if (count >= boxes.Length) break;
            }
        }
        return count;
    }

    /// <summary>
    /// Finds the constriction a body is leaning on: the tightest ground ahead of it, along
    /// the way it is trying to go. False if there is nothing narrow enough to blame.
    /// </summary>
    /// <remarks>
    /// A granted detour is only as good as the region it is told to avoid, and that region
    /// used to be a fixed distance in front of the body's nose. Against a queue several
    /// bodies deep wedged in a corner that is useless — the replan simply routes round the
    /// bubble and back into the same queue a metre later, which is why the unit that was
    /// offered a way out visibly fails to take one. What has to be excluded is the gap
    /// itself, wherever along the route it happens to be.
    /// <para>
    /// Searched along the body's own intent rather than along its stored route, because a
    /// body in this state frequently has no usable route left and its intent is the more
    /// honest statement of where it keeps trying to go. Straight-line, so a constriction
    /// round a bend is missed; the fallback then behaves as before.
    /// </para>
    /// </remarks>
    public bool TryFindObstructingAperture(
        Vector2 position,
        Vector2 direction,
        float agentRadius,
        out Vector2 aperture)
    {
        aperture = default;
        if (direction.LengthSquared() <= Epsilon) return false;
        direction = Vector2.Normalize(direction);

        var step = grid.Transform.CellSize * 0.5f;
        var steps = Math.Max(1, (int)MathF.Ceiling(ApertureSearchDistance / step));
        // Only ground tight enough to serialise a crowd counts as an aperture; a merely
        // crowded patch of open field is not something to route around.
        var constrictionLimit = agentRadius * 3f;
        var tightest = float.PositiveInfinity;
        var found = false;
        for (var i = 1; i <= steps; i++)
        {
            var sample = position + direction * (i * step);
            if (!grid.TryWorldToCell(sample, out var cell)) break;
            var clearance = grid.Clearance(cell);
            if (clearance >= constrictionLimit || clearance >= tightest) continue;
            tightest = clearance;
            aperture = grid.CellCenter(cell);
            found = true;
        }
        return found;
    }

    /// <summary>
    /// The axis a constriction actually lets a body through on, or false in open ground.
    /// </summary>
    /// <remarks>
    /// Found by asking, in each of eight directions, how far a body could travel before the
    /// ground stops taking it, and keeping the opposed pair with the longest shorter run —
    /// which for a gap in a wall is the way through it. Used to judge whether bodies are
    /// arriving at gaps square or oblique; a body that reaches a one-body-wide gap at forty
    /// degrees has to reorient inside the one place there is no room to.
    /// </remarks>
    public bool TryFindPassageAxis(Vector2 position, float agentRadius, out Vector2 axis)
    {
        axis = default;
        if (!grid.TryWorldToCell(position, out var cell)) return false;
        if (grid.Clearance(cell) >= agentRadius * 3f) return false;

        // Memoized: which way a gap lets a body through is a property of the cell and the
        // raster, not of the body standing in it, and probing eight directions per body per
        // tick would not be affordable otherwise.
        if (passageAxisRevision != grid.Revision)
        {
            passageAxes.Clear();
            passageAxisRevision = grid.Revision;
        }
        var key = (grid.Transform.Index(cell), (int)MathF.Round(agentRadius * 100f));
        if (passageAxes.TryGetValue(key, out var cached))
        {
            axis = cached;
            return cached != Vector2.Zero;
        }
        var found = ComputePassageAxis(position, agentRadius, out axis);
        passageAxes[key] = found ? axis : Vector2.Zero;
        return found;
    }

    private bool ComputePassageAxis(Vector2 position, float agentRadius, out Vector2 axis)
    {
        axis = default;
        var step = grid.Transform.CellSize * 0.5f;
        var reach = grid.Transform.CellSize * 8f;
        var bestRun = 0f;
        for (var i = 0; i < 4; i++)
        {
            var angle = i * MathF.PI / 4f;
            var direction = new Vector2(MathF.Cos(angle), MathF.Sin(angle));
            var forward = FreeRun(position, direction, agentRadius, step, reach);
            var backward = FreeRun(position, -direction, agentRadius, step, reach);
            var run = MathF.Min(forward, backward);
            if (run <= bestRun) continue;
            bestRun = run;
            axis = direction;
        }
        return bestRun > 0f;
    }

    private float FreeRun(Vector2 from, Vector2 direction, float agentRadius, float step, float reach)
    {
        var travelled = 0f;
        while (travelled < reach)
        {
            travelled += step;
            var sample = from + direction * travelled;
            if (!grid.TryWorldToCell(sample, out var cell) || !grid.IsWalkable(cell, agentRadius))
            {
                return travelled - step;
            }
        }
        return reach;
    }

    /// <summary>
    /// Standoff, in metres, at which a body lines up on a gap's axis. Zero disables it.
    /// </summary>
    /// <remarks>
    /// Off by default, and deliberately: it does what it says — the gate's mean approach
    /// angle falls from 33 to 28 degrees and route length in the pen from 1.10x to 1.00x —
    /// and it costs stall time and direction stability to get there (pen red 37 to 62
    /// agent-seconds, gate dead stops 29 to 74, mean turn 4.8 to 5.8 degrees a tick).
    /// <para>
    /// That is a trade between how a crowd looks going into a gap and how long it takes to
    /// get through, and no headless measurement settles it. It ships as a slider rather than
    /// as a decision, because whoever is watching can weigh it and the benchmark cannot.
    /// </para>
    /// </remarks>
    internal static float ApertureApproachStandoff;

    /// <summary>
    /// Where a body should aim when a constriction lies ahead: a point on the gap's axis,
    /// rather than the gap's mouth.
    /// </summary>
    /// <remarks>
    /// Descending a cost field aims a body at the cheapest ground next to it, and next to a
    /// gap that is the gap's opening — so bodies converge on the mouth from every angle and
    /// arrive oblique. Measured at 33 degrees mean at a one-cell gate with 44% of samples
    /// past thirty, and a body at that angle presents nearly a fifth more width than it has
    /// and must reorient inside the one place there is no room to.
    /// <para>
    /// Aiming instead at a staging point on the axis, one standoff short of the gap, makes
    /// the body line up before it commits and then go straight through. It also stabilises
    /// the intent: a fixed geometric target does not flip between near-tied probe directions
    /// the way the cheapest-of-sixteen answer does, which is what made bodies cast about at a
    /// chokepoint instead of committing.
    /// </para>
    /// </remarks>
    public bool TryFindApertureApproach(
        Vector2 position,
        Vector2 heading,
        float agentRadius,
        out Vector2 aimPoint)
    {
        aimPoint = default;
        if (ApertureApproachStandoff <= 0f) return false;
        if (!TryFindObstructingAperture(position, heading, agentRadius, out var aperture)) return false;
        if (!TryFindPassageAxis(aperture, agentRadius, out var axis)) return false;
        // The axis is undirected; take the end the body is actually travelling toward.
        if (Vector2.Dot(axis, heading) < 0f) axis = -axis;
        var along = Vector2.Dot(aperture - position, axis);
        // Short of the gap, line up on its axis. At or inside it, aim through and out.
        aimPoint = along > ApertureApproachStandoff
            ? aperture - axis * ApertureApproachStandoff
            : aperture + axis * ApertureApproachStandoff;
        return true;
    }

    public bool IsPositionNavigable(Vector2 position, float agentRadius)
    {
        if (!terrain.IsBodyTraversable(position, agentRadius))
        {
            return false;
        }
        return IsPositionFreeOfPlacement(position, agentRadius);
    }

    public Vector2 FlowDirection(
        Vector2 position,
        Vector2 requestedGoal,
        float agentRadius,
        float agentSpeed = 0f)
    {
        if (!grid.TryWorldToCell(position, out var current) ||
            !grid.TryWorldToCell(requestedGoal, out var requestedGoalCell))
        {
            return Vector2.Zero;
        }
        var goal = grid.IsWalkable(requestedGoalCell, agentRadius)
            ? requestedGoalCell
            : FindNearestWalkable(requestedGoalCell, agentRadius);
        if (goal is not { } resolvedGoal) return Vector2.Zero;

        var costs = GetFlowField(resolvedGoal, agentRadius, congestion.Revision, agentSpeed: agentSpeed);

        var currentCost = costs.CostAt(current);
        var best = current;
        var bestCost = currentCost;
        foreach (var offset in NeighborOffsets)
        {
            var next = new GridCell(current.X + offset.X, current.Z + offset.Z);
            if (!CanTraverse(current, next, agentRadius)) continue;
            var nextCost = costs.CostAt(next);
            if (nextCost >= bestCost - 0.0001f) continue;
            best = next;
            bestCost = nextCost;
        }

        if (best == current)
        {
            var direct = requestedGoal - position;
            return direct.LengthSquared() > 0.0001f ? Vector2.Normalize(direct) : Vector2.Zero;
        }
        var direction = grid.CellCenter(best) - position;
        return direction.LengthSquared() > 0.0001f ? Vector2.Normalize(direction) : Vector2.Zero;
    }

    /// <summary>
    /// Smooth downhill direction of the shared cost field at an arbitrary point,
    /// or zero if the point has no route to the goal.
    /// </summary>
    /// <remarks>
    /// <see cref="FlowDirection"/> picks the cheapest neighbouring cell, so its
    /// answer is quantised to eight directions and flips the instant a body
    /// crosses a cell boundary — following it directly reproduces exactly the
    /// jerky, snapping motion that materialised waypoints used to cause. This
    /// bilinearly samples the field and takes a central difference instead, which
    /// is continuous everywhere. Unreachable cells read as a fixed penalty above
    /// the local cost rather than infinity, so the gradient also leans bodies away
    /// from walls instead of producing a NaN at the boundary.
    /// </remarks>
    public Vector2 SampleFlowGradient(
        Vector2 position,
        Vector2 requestedGoal,
        float agentRadius,
        int congestionRevision = int.MaxValue,
        float agentSpeed = 0f)
    {
        if (!grid.TryWorldToCell(position, out var current) ||
            !grid.TryWorldToCell(requestedGoal, out var requestedGoalCell))
        {
            return Vector2.Zero;
        }
        var goal = grid.IsWalkable(requestedGoalCell, agentRadius)
            ? requestedGoalCell
            : FindNearestWalkable(requestedGoalCell, agentRadius);
        if (goal is not { } resolvedGoal) return Vector2.Zero;

        var costs = GetFlowField(
            resolvedGoal, agentRadius, congestionRevision, agentSpeed: agentSpeed);
        var centerCost = costs.CostAt(current);
        if (!float.IsFinite(centerCost)) return Vector2.Zero;

        var blockedCost = centerCost + BlockedFlowSeconds;
        var probe = grid.Transform.CellSize;

        // Search directions rather than differentiating. A central difference
        // averages what it straddles, and between two comparable routes the cost
        // field has a ridge: sampling across it cancels the two descents and
        // leaves a resultant pointing *along* the ridge — into the empty ground
        // between the exits. A whole group following that walks into the corner
        // between two gates, bunches up, and then trickles out of whichever one
        // it ended up nearest. Taking the cheapest of a ring of probes cannot
        // average across a ridge, because it never combines two samples.
        var bestIndex = -1;
        var bestCost = centerCost;
        Span<float> ringCost = stackalloc float[FlowProbeCount];
        for (var i = 0; i < FlowProbeCount; i++)
        {
            var angle = i * MathF.Tau / FlowProbeCount;
            var offset = new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * probe;
            ringCost[i] = SampleFlowCost(costs, position + offset, blockedCost);
            if (ringCost[i] >= bestCost) continue;
            bestCost = ringCost[i];
            bestIndex = i;
        }

        if (bestIndex >= 0)
        {
            // Refine between probes so the answer is continuous as the body moves.
            // The parabola through the winner and its neighbours shifts away from
            // whichever side is more expensive, which is the ridge if there is one.
            var previous = ringCost[(bestIndex - 1 + FlowProbeCount) % FlowProbeCount];
            var next = ringCost[(bestIndex + 1) % FlowProbeCount];
            var denominator = previous - 2f * bestCost + next;
            var shift = MathF.Abs(denominator) > Epsilon
                ? Math.Clamp(0.5f * (previous - next) / denominator, -0.5f, 0.5f)
                : 0f;
            var angle = (bestIndex + shift) * MathF.Tau / FlowProbeCount;
            return new Vector2(MathF.Cos(angle), MathF.Sin(angle));
        }

        // Flat patch (equidistant routes, or the goal cell itself). Fall back to
        // the discrete answer so the body still commits to a direction.
        return FlowDirection(position, requestedGoal, agentRadius);
    }

    private float SampleFlowCost(RectangleFlowField costs, Vector2 position, float blockedCost)
    {
        var local = (position - grid.Transform.Origin) / grid.Transform.CellSize -
                    new Vector2(0.5f);
        var x0 = (int)MathF.Floor(local.X);
        var z0 = (int)MathF.Floor(local.Y);
        var tx = local.X - x0;
        var tz = local.Y - z0;

        var c00 = FlowCostAt(costs, x0, z0, blockedCost);
        var c10 = FlowCostAt(costs, x0 + 1, z0, blockedCost);
        var c01 = FlowCostAt(costs, x0, z0 + 1, blockedCost);
        var c11 = FlowCostAt(costs, x0 + 1, z0 + 1, blockedCost);
        return float.Lerp(float.Lerp(c00, c10, tx), float.Lerp(c01, c11, tx), tz);
    }

    private float FlowCostAt(RectangleFlowField costs, int x, int z, float blockedCost)
    {
        var cell = new GridCell(
            Math.Clamp(x, 0, grid.Width - 1),
            Math.Clamp(z, 0, grid.Height - 1));
        var cost = costs.CostAt(cell);
        return float.IsFinite(cost) ? cost : blockedCost;
    }

    /// <summary>
    /// Cost field for a goal, preferring a specific congestion revision.
    /// </summary>
    /// <remarks>
    /// Recent revisions are retained rather than discarded so that members of one
    /// crowd can be reading slightly different fields at the same moment. That is
    /// the point: with a single live field every unit adopts a new route on the
    /// same tick, and a crowd re-deciding in lockstep several times a second
    /// reads as indecision even when each decision is correct. A caller that asks
    /// for a revision no longer held simply gets the current one.
    /// </remarks>
    private RectangleFlowField GetFlowField(
        GridCell goal,
        float agentRadius,
        int congestionRevision,
        bool chargeTurns = true,
        float agentSpeed = 0f)
    {
        var goalIndex = grid.Transform.Index(goal);
        var radiusKey = (int)MathF.Round(agentRadius * 100f);
        // Speed is part of the key only because congestion is priced in it, so on ground where
        // nothing is stuck every unit shares one field however fast it is — which is the ordinary
        // case, and the reason this does not multiply the cache by the size of the roster. The
        // moment a jam exists the congestion revision has moved anyway, so the split happens on a
        // rebuild that was going to happen regardless.
        var speedScale = congestion.LiveCellCount == 0 ? 1f : CongestionSpeedScale(agentSpeed);
        var speedKey = (int)MathF.Round(speedScale * 8f);
        if (flowFields.TryGetValue(
                (goalIndex, radiusKey, speedKey, grid.Revision, congestionRevision, chargeTurns),
                out var retained))
        {
            return retained;
        }

        var key = (goalIndex, radiusKey, speedKey, grid.Revision, congestion.Revision, chargeTurns);
        if (flowFields.TryGetValue(key, out var costs)) return costs;
        // A navigation edit makes every older field unreachable, so those go
        // immediately. Congestion revisions are aged out instead of dropped, to
        // keep the staggered-adoption window above alive.
        foreach (var existing in flowFields.Keys
                     .Where(existing => existing.Nav != grid.Revision ||
                                        existing.Congestion < congestion.Revision - RetainedCongestionRevisions)
                     .ToArray())
        {
            flowFields.Remove(existing);
        }

        foreach (var existing in latestFields.Keys.Where(k => k.Nav != grid.Revision).ToArray())
        {
            latestFields.Remove(existing);
        }
        // Corners for the route, a tile for the body. The abstract layer is a Dijkstra over
        // portal corners with no cell-level search behind it; the tiles are filled only where
        // something asks, and are what keeps the gradient continuous.
        var lineage = (goalIndex, radiusKey, speedKey, grid.Revision, chargeTurns);
        var (mesh, meshIndex) = Mesh(agentRadius);
        // After Mesh, so the decomposition's own cost is charged to it and not to the field.
        var fieldStart = Stopwatch.GetTimestamp();
        costs = new RectangleFlowField(
            mesh,
            meshIndex,
            goal,
            SecondsPerCell,
            BendSeconds(goal, agentRadius, chargeTurns),
            congestion,
            CongestionSecondsPerPressure,
            this,
            agentRadius,
            speedScale,
            chargeTurns);
        FieldSetupTicks += Stopwatch.GetTimestamp() - fieldStart;
        FlowFieldBuilds++;
        flowFields[key] = costs;
        latestFields[lineage] = costs;
        return costs;
    }

    /// <summary>
    /// Seconds charged for the one backward step from <paramref name="current"/> to
    /// <paramref name="previous"/> while building a cost-to-goal field.
    /// </summary>
    /// <remarks>
    /// Extracted so the region-local searches underneath the portal graph charge exactly
    /// this and not something that merely resembles it. Two costs quoted in seconds that
    /// disagree by a rounding are two different maps, and the abstract layer's whole
    /// claim is that its numbers are comparable with the fine layer's.
    /// </remarks>
    private float FlowStepCost(
        float costAtCurrent,
        GridCell current,
        GridCell previous,
        int directionIndex,
        int arrivalAtCurrent,
        float agentRadius,
        bool chargeTurns,
        float speedScale,
        out int travelDirection)
    {
        var offset = NeighborOffsets[directionIndex];
        var stepCost = offset.X != 0 && offset.Z != 0 ? DiagonalCost : 1f;
        var surfaceCost = (grid.TraversalCost(current) + grid.TraversalCost(previous)) * 0.5f;
        var elevationCost = MathF.Abs(grid.HeightAt(previous) - grid.HeightAt(current)) *
                            ClimbSecondsPerMetre;
        // Built backwards from the goal, so a body travelling this edge moves from
        // `previous` to `current` and the heading it carries into `current` is the
        // reverse of the offset being explored.
        travelDirection = OppositeDirection(directionIndex);
        var turnSeconds = chargeTurns
            ? TurnCost(travelDirection, arrivalAtCurrent, current, agentRadius)
            : 0f;
        // The running total is summed in here rather than by the caller, and the terms
        // stay in this order, because float addition does not associate: adding the four
        // components together first and the total afterwards is a different number in the
        // last bit, and a different number in the last bit is a different route out of a
        // Dijkstra. Keeping the arithmetic identical is what lets the region-bounded
        // search below be compared against the flat one and any difference be attributed
        // to the hierarchy rather than to having moved an expression.
        return costAtCurrent + stepCost * SecondsPerCell * surfaceCost + elevationCost +
               CongestionCost(current, previous, turnSeconds, speedScale, agentRadius) + turnSeconds;
    }

    private float[] BuildFlowField(
        GridCell goal,
        float agentRadius,
        bool chargeTurns = true,
        float congestionSpeedScale = 1f)
    {
        var costs = new float[grid.Width * grid.Height];
        var closed = new bool[costs.Length];
        // The heading a cell's best-known route leaves it with. Exact turn-aware routing
        // wants the heading in the search state, which multiplies it by eight and puts
        // the group-order hitch back; recording one heading per cell keeps the search the
        // size it was. It is therefore an approximation — a route that would rather reach
        // a cell more slowly but better aligned cannot express that — and what it is for
        // is producing routes a body can follow, not proving optimality.
        var arrival = new int[costs.Length];
        Array.Fill(arrival, -1);
        Array.Fill(costs, float.PositiveInfinity);
        var goalIndex = grid.Transform.Index(goal);
        costs[goalIndex] = 0f;
        var open = new PriorityQueue<GridCell, float>();
        open.Enqueue(goal, 0f);

        while (open.TryDequeue(out var current, out _))
        {
            var currentIndex = grid.Transform.Index(current);
            if (closed[currentIndex]) continue;
            closed[currentIndex] = true;
            for (var directionIndex = 0; directionIndex < NeighborOffsets.Length; directionIndex++)
            {
                var offset = NeighborOffsets[directionIndex];
                var previous = new GridCell(current.X + offset.X, current.Z + offset.Z);
                if (!CanTraverseFlow(previous, current, agentRadius)) continue;
                var previousIndex = grid.Transform.Index(previous);
                if (closed[previousIndex]) continue;
                var nextCost = FlowStepCost(
                    costs[currentIndex],
                    current,
                    previous,
                    directionIndex,
                    arrival[currentIndex],
                    agentRadius,
                    chargeTurns,
                    congestionSpeedScale,
                    out var travelDirection);
                if (nextCost >= costs[previousIndex]) continue;
                costs[previousIndex] = nextCost;
                arrival[previousIndex] = travelDirection;
                open.Enqueue(previous, nextCost);
            }
        }
        return costs;
    }

    private bool CanTraverseFlow(GridCell from, GridCell to, float agentRadius)
    {
        if (!grid.CanTraverse(from, to, agentRadius)) return false;
        var diagonal = from.X != to.X && from.Z != to.Z;
        if (!diagonal) return true;
        var firstCorner = new GridCell(to.X, from.Z);
        var secondCorner = new GridCell(from.X, to.Z);
        return grid.CanTraverse(from, firstCorner, agentRadius) &&
               grid.CanTraverse(from, secondCorner, agentRadius) &&
               grid.CanTraverse(firstCorner, to, agentRadius) &&
               grid.CanTraverse(secondCorner, to, agentRadius);
    }

    /// <summary>
    /// Whether a body at <paramref name="position"/> clears every placed block.
    /// </summary>
    /// <remarks>
    /// This is the single hottest predicate in the simulation: it is consulted per
    /// sample of every swept step, per neighbour of every A* expansion, and per
    /// candidate of the solver's terrain fallback. It used to walk a cached list of
    /// every occupied cell on the map, so its cost scaled with how much had been
    /// built rather than with how much was nearby — a few hundred box tests to
    /// answer a question about one square metre. The placement grid can say which
    /// cells could possibly reach the body, and only those are tested.
    /// </remarks>
    public bool IsPositionFreeOfPlacement(Vector2 position, float agentRadius)
    {
        var halfExtent = placement.Transform.CellSize * 0.5f - 0.025f;
        PlacementCellRange(position, position, halfExtent + agentRadius, out var low, out var high);
        for (var z = low.Z; z <= high.Z; z++)
        for (var x = low.X; x <= high.X; x++)
        {
            var cell = new GridCell(x, z);
            if (!placement.IsOccupied(cell)) continue;
            var center = placement.Transform.CellCenter(cell);
            var closest = Vector2.Clamp(
                position,
                center - new Vector2(halfExtent),
                center + new Vector2(halfExtent));
            if (Vector2.DistanceSquared(position, closest) <
                agentRadius * agentRadius - 0.000001f)
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>
    /// Range of placement cells whose boxes, grown by <paramref name="reach"/>, can
    /// overlap the given world bounds. Clamped to the grid, so cells outside the map
    /// are skipped rather than treated as occupied.
    /// </summary>
    private void PlacementCellRange(
        Vector2 minimum,
        Vector2 maximum,
        float reach,
        out GridCell low,
        out GridCell high)
    {
        var transform = placement.Transform;
        var lowLocal = (minimum - new Vector2(reach) - transform.Origin) / transform.CellSize;
        var highLocal = (maximum + new Vector2(reach) - transform.Origin) / transform.CellSize;
        low = new GridCell(
            Math.Max(0, (int)MathF.Floor(lowLocal.X)),
            Math.Max(0, (int)MathF.Floor(lowLocal.Y)));
        high = new GridCell(
            Math.Min(transform.Width - 1, (int)MathF.Ceiling(highLocal.X)),
            Math.Min(transform.Height - 1, (int)MathF.Ceiling(highLocal.Y)));
    }

    private bool SegmentAvoidsPlacement(
        Vector2 start,
        Vector2 end,
        float expansion,
        bool allowEscapeFromStart)
    {
        var halfCell = placement.Transform.CellSize * 0.5f + expansion;
        PlacementCellRange(
            Vector2.Min(start, end), Vector2.Max(start, end), halfCell,
            out var low, out var high);
        for (var z = low.Z; z <= high.Z; z++)
        for (var x = low.X; x <= high.X; x++)
        {
            var cell = new GridCell(x, z);
            if (!placement.IsOccupied(cell)) continue;
            var center = placement.Transform.CellCenter(cell);
            var minimum = center - new Vector2(halfCell);
            var maximum = center + new Vector2(halfCell);
            if (allowEscapeFromStart && PointInsideAabb(start, minimum, maximum))
            {
                var startOffset = start - center;
                var endOffset = end - center;
                if (endOffset.LengthSquared() > startOffset.LengthSquared() + 0.000001f) continue;
            }
            if (SegmentIntersectsAabb(start, end, minimum, maximum)) return false;
        }
        return true;
    }

    private static bool PointInsideAabb(Vector2 point, Vector2 minimum, Vector2 maximum) =>
        point.X >= minimum.X && point.X <= maximum.X &&
        point.Y >= minimum.Y && point.Y <= maximum.Y;

    private GridCell? FindNearestWalkable(
        GridCell origin,
        float agentRadius,
        Vector2? searchCenter = null,
        float maximumDistance = float.PositiveInfinity)
    {
        var visited = new bool[grid.Width * grid.Height];
        var queue = new Queue<GridCell>();
        queue.Enqueue(origin);
        visited[grid.Transform.Index(origin)] = true;

        while (queue.TryDequeue(out var current))
        {
            var withinSearchRadius = searchCenter is not { } center ||
                                     Vector2.Distance(grid.CellCenter(current), center) <= maximumDistance;
            if (withinSearchRadius && grid.IsWalkable(current, agentRadius)) return current;
            foreach (var offset in NeighborOffsets.Take(4))
            {
                var next = new GridCell(current.X + offset.X, current.Z + offset.Z);
                if (!grid.Contains(next)) continue;
                if (searchCenter is { } boundedCenter &&
                    Vector2.Distance(grid.CellCenter(next), boundedCenter) >
                    maximumDistance + grid.Transform.CellSize)
                {
                    continue;
                }
                var index = grid.Transform.Index(next);
                if (visited[index]) continue;
                visited[index] = true;
                queue.Enqueue(next);
            }
        }
        return null;
    }

    private GridCell? FindRecoveryStart(
        GridCell origin,
        float agentRadius,
        Vector2 start,
        Vector2 goal,
        float maximumDistance)
    {
        var searchCells = (int)MathF.Ceiling(maximumDistance / grid.Transform.CellSize);
        var goalDirection = goal - start;
        goalDirection = goalDirection.LengthSquared() > 0.0001f
            ? Vector2.Normalize(goalDirection)
            : Vector2.Zero;
        GridCell? best = null;
        var bestScore = float.PositiveInfinity;

        for (var z = origin.Z - searchCells; z <= origin.Z + searchCells; z++)
        for (var x = origin.X - searchCells; x <= origin.X + searchCells; x++)
        {
            var candidate = new GridCell(x, z);
            if (!grid.IsWalkable(candidate, agentRadius)) continue;
            var delta = grid.CellCenter(candidate) - start;
            var distance = delta.Length();
            if (distance > maximumDistance) continue;
            if (!IsInitialBodyStepClear(start, grid.CellCenter(candidate), agentRadius)) continue;

            // A recovery cell is not merely the closest valid cell: at a narrow
            // portal that can make two displaced units converge on its center.
            // Prefer equally-near cells that make progress toward the destination.
            var score = distance - Vector2.Dot(delta, goalDirection) * 1.10f;
            if (score > bestScore + 0.0001f) continue;
            if (MathF.Abs(score - bestScore) <= 0.0001f && best is { } currentBest &&
                grid.Transform.Index(candidate) >= grid.Transform.Index(currentBest))
            {
                continue;
            }
            best = candidate;
            bestScore = score;
        }
        return best;
    }

    private List<GridCell>? FindCellPath(
        GridCell start,
        GridCell goal,
        float agentRadius,
        Vector2? congestionAvoidanceCenter,
        float[]? additionalNavigationCosts,
        float congestionSpeedScale)
    {
        // Reused across calls. A* was allocating three full-grid arrays every
        // time it ran, and once every obstructed unit started re-planning against
        // congestion that became the most expensive phase in the tick.
        var count = grid.Width * grid.Height;
        if (searchCameFrom.Length != count)
        {
            searchCameFrom = new int[count];
            searchCost = new float[count];
            searchClosed = new bool[count];
            searchArrival = new int[count];
        }
        var cameFrom = searchCameFrom;
        var cost = searchCost;
        var closed = searchClosed;
        Array.Fill(cameFrom, -1);
        Array.Fill(cost, float.PositiveInfinity);
        Array.Fill(searchArrival, -1);
        Array.Clear(closed);
        searchQueue.Clear();

        var expansionsAtEntry = PathExpansions;
        var startIndex = grid.Transform.Index(start);
        var goalIndex = grid.Transform.Index(goal);
        var bestIndex = startIndex;
        var bestReach = float.PositiveInfinity;
        var open = searchQueue;
        cost[startIndex] = 0f;
        // Costs are seconds now, so the heuristic has to be too: cells to go,
        // at the speed of the quickest ground that exists. Anything larger stops
        // being a lower bound and A* would return non-optimal routes.
        var heuristicScale = SecondsPerCell *
                             (terrain.Revision == 0 ? 1f : TerrainSurfaceRules.MinimumPathCost) *
                             HeuristicWeight;
        open.Enqueue(start, Heuristic(start, goal) * heuristicScale);

        while (open.TryDequeue(out var current, out _))
        {
            var currentIndex = grid.Transform.Index(current);
            if (closed[currentIndex]) continue;
            closed[currentIndex] = true;
            // <b>Counted, because 1.8 seconds a search is either a weak heuristic or an exhausted map</b> and
            // the two want opposite fixes. Expansions against the grid's cell count says which: a search that
            // closes most of the map either could not reach its goal or was steered by a heuristic that had
            // stopped steering.
            PathExpansions++;
            if (currentIndex == goalIndex)
            {
                PathExpansionsWorst = Math.Max(PathExpansionsWorst, PathExpansions - expansionsAtEntry);
                return Reconstruct(cameFrom, startIndex, goalIndex);
            }

            // <b>The closest thing seen to the goal, kept so a bounded search has something to hand back.</b>
            // Straight-line distance rather than cost: this is asking "which of the cells I reached is nearest
            // the thing I was sent to", and a cost-to-here says nothing about that.
            var reach = Heuristic(current, goal);
            if (reach < bestReach)
            {
                bestReach = reach;
                bestIndex = currentIndex;
            }

            if (PathExpansions - expansionsAtEntry >= ExpansionBudget)
            {
                // Out of budget with the goal unreached. Handing back the partial route beats both
                // alternatives: a null makes a reachable destination look unreachable and the body gives up,
                // and carrying on takes the frame. The body walks the part that was solved and asks again from
                // there, which is also what it would do if the world had changed under it.
                PathBudgetStops++;
                PathExpansionsWorst = Math.Max(PathExpansionsWorst, PathExpansions - expansionsAtEntry);
                return bestIndex == startIndex ? null : Reconstruct(cameFrom, startIndex, bestIndex);
            }

            for (var directionIndex = 0; directionIndex < NeighborOffsets.Length; directionIndex++)
            {
                var offset = NeighborOffsets[directionIndex];
                var next = new GridCell(current.X + offset.X, current.Z + offset.Z);
                if (!CanTraverse(current, next, agentRadius)) continue;
                var nextIndex = grid.Transform.Index(next);
                if (closed[nextIndex]) continue;
                var stepCost = offset.X != 0 && offset.Z != 0 ? DiagonalCost : 1f;
                var surfaceCost = (grid.TraversalCost(current) + grid.TraversalCost(next)) * 0.5f;
                var elevationCost = MathF.Abs(grid.HeightAt(next) - grid.HeightAt(current)) *
                                    ClimbSecondsPerMetre;
                var additionalCost = additionalNavigationCosts is null ? 0f : additionalNavigationCosts[nextIndex];
                var turnSeconds = TurnCost(
                    searchArrival[currentIndex], directionIndex, current, agentRadius);
                var nextCost = cost[currentIndex] + stepCost * SecondsPerCell * surfaceCost +
                               elevationCost +
                               PointCongestionCost(next, congestionAvoidanceCenter) +
                               CongestionCost(
                                   current, next, turnSeconds, congestionSpeedScale, agentRadius) +
                               additionalCost +
                               turnSeconds;
                if (nextCost >= cost[nextIndex]) continue;
                cost[nextIndex] = nextCost;
                searchArrival[nextIndex] = directionIndex;
                cameFrom[nextIndex] = currentIndex;
                open.Enqueue(next, nextCost + Heuristic(next, goal) * heuristicScale);
            }
        }

        // Fell out of the loop: the open set emptied without reaching the goal, so every cell reachable from
        // the start is now closed. That is the worst case a grid search has, and it is silent — the caller
        // gets a null and no indication that answering it cost the whole map.
        PathFailures++;
        PathExpansionsWorst = Math.Max(PathExpansionsWorst, PathExpansions - expansionsAtEntry);
        return null;
    }

    /// <summary>
    /// Whether a body of <paramref name="agentRadius"/> stands clear of terrain and
    /// placed blocks at the centre of <paramref name="cell"/>. Memoized per raster
    /// revision; see <see cref="cellCenterAdmission"/>.
    /// </summary>
    private bool CellCenterAdmitsBody(GridCell cell, float agentRadius)
    {
        if (!grid.Contains(cell)) return false;
        if (cellCenterAdmissionRevision != grid.Revision)
        {
            cellCenterAdmission.Clear();
            cellCenterAdmissionRevision = grid.Revision;
        }
        var radiusKey = (int)MathF.Round(agentRadius * 100f);
        if (!cellCenterAdmission.TryGetValue(radiusKey, out var memo))
        {
            cellCenterAdmission[radiusKey] = memo = new byte[grid.Width * grid.Height];
        }

        var index = grid.Transform.Index(cell);
        if (memo[index] != 0) return memo[index] == 1;
        var center = grid.CellCenter(cell);
        var admitted = terrain.IsBodyTraversable(center, agentRadius) &&
                       IsPositionFreeOfPlacement(center, agentRadius);
        memo[index] = admitted ? (byte)1 : (byte)2;
        return admitted;
    }

    private bool CanTraverse(GridCell from, GridCell to, float agentRadius)
    {
        if (!CellCenterAdmitsBody(to, agentRadius)) return false;
        if (terrain.Revision == 0)
        {
            if (!grid.IsWalkable(to, agentRadius)) return false;
            var flatDiagonal = from.X != to.X && from.Z != to.Z;
            return !flatDiagonal ||
                   grid.IsWalkable(new GridCell(to.X, from.Z), agentRadius) &&
                   grid.IsWalkable(new GridCell(from.X, to.Z), agentRadius);
        }

        if (!grid.CanTraverse(from, to, agentRadius)) return false;
        var diagonal = from.X != to.X && from.Z != to.Z;
        if (!diagonal) return true;
        var firstCorner = new GridCell(to.X, from.Z);
        var secondCorner = new GridCell(from.X, to.Z);
        return grid.CanTraverse(from, firstCorner, agentRadius) &&
               grid.CanTraverse(from, secondCorner, agentRadius) &&
               grid.CanTraverse(firstCorner, to, agentRadius) &&
               grid.CanTraverse(secondCorner, to, agentRadius);
    }

    private Vector2[] SmoothPath(
        Vector2 start,
        Vector2 destination,
        IReadOnlyList<GridCell> cells,
        float agentRadius,
        Vector2? congestionAvoidanceCenter,
        float[]? additionalNavigationCosts,
        bool includeFirstCell,
        float maximumSegmentLength = float.PositiveInfinity)
    {
        var candidates = new List<Vector2>(cells.Count + 1);
        for (var i = includeFirstCell ? 0 : 1; i < cells.Count; i++) candidates.Add(grid.CellCenter(cells[i]));
        if (candidates.Count == 0 || candidates[^1] != destination) candidates.Add(destination);

        // A body recovering from inside a block starts its route overlapping
        // one. Its very first segment has to be judged by the escape rule, or
        // smoothing rejects every candidate and discards an otherwise valid A*
        // route, leaving the unit permanently embedded with no destination.
        var escapingStart = !IsPositionFreeOfPlacement(start, agentRadius);
        var result = new List<Vector2>();
        var anchor = start;
        var cursor = 0;
        while (cursor < candidates.Count)
        {
            var farthest = -1;
            for (var candidate = cursor; candidate < candidates.Count; candidate++)
            {
                var segmentCap = terrain.Revision > 0
                    ? MathF.Min(maximumSegmentLength, grid.Transform.CellSize * 2f)
                    : maximumSegmentLength;
                if (Vector2.Distance(anchor, candidates[candidate]) > segmentCap) break;
                if (!SegmentIsBodySafe(
                        anchor, candidates[candidate], agentRadius,
                        includeFirstCell, cursor, candidate, escapingStart) ||
                    !SegmentPreservesTerrainCost(anchor, candidates[candidate]) ||
                    !SegmentAvoidsAdditionalCost(anchor, candidates[candidate], additionalNavigationCosts) ||
                    !SegmentAvoidsCongestion(anchor, candidates[candidate], congestionAvoidanceCenter)) break;
                farthest = candidate;
            }

            if (farthest < cursor)
            {
                // Cost, reservation and congestion filters express a preference:
                // they exist so smoothing cannot erase a bend A* chose on purpose.
                // They must not be able to destroy the route. A diagonal hop
                // between two road cells clips a cheaper neighbour's corner, and
                // failing the whole path there stranded the unit with no route at
                // all. The single-cell step A* already validated stays available.
                if (!SegmentIsBodySafe(
                        anchor, candidates[cursor], agentRadius,
                        includeFirstCell, cursor, cursor, escapingStart))
                {
                    // Never manufacture a waypoint that the body cannot actually
                    // reach from the current anchor. The caller may retry from
                    // another recovery cell, but an invalid first segment causes a
                    // permanent steer/replan loop at terrain corners.
                    return Array.Empty<Vector2>();
                }
                farthest = cursor;
            }
            result.Add(candidates[farthest]);
            anchor = candidates[farthest];
            cursor = farthest + 1;
        }
        return result.ToArray();
    }

    private bool SegmentIsBodySafe(
        Vector2 anchor,
        Vector2 candidate,
        float agentRadius,
        bool includeFirstCell,
        int cursor,
        int candidateIndex,
        bool allowPlacementEscape) =>
        includeFirstCell && cursor == 0 && candidateIndex == 0
            ? IsContinuousStepClear(anchor, candidate, agentRadius, allowPlacementEscape)
            : IsDirectPathClear(anchor, candidate, agentRadius);

    private bool SegmentAvoidsAdditionalCost(Vector2 start, Vector2 end, float[]? additionalNavigationCosts)
    {
        if (additionalNavigationCosts is null) return true;
        if (additionalNavigationCosts.Length != grid.Width * grid.Height)
        {
            throw new ArgumentException("Additional navigation-cost dimensions do not match the navigation grid.");
        }

        // A* may deliberately bend around a crowded patch. Do not let geometric
        // smoothing erase that decision by drawing a straight segment back
        // through the occupied cells. Endpoints are excluded so an agent can
        // still leave a crowded start or approach a contested destination.
        var distance = Vector2.Distance(start, end);
        var samples = Math.Max(1, (int)MathF.Ceiling(distance / (grid.Transform.CellSize * 0.40f)));
        for (var sample = 1; sample < samples; sample++)
        {
            var position = Vector2.Lerp(start, end, sample / (float)samples);
            if (!grid.TryWorldToCell(position, out var cell)) return false;
            if (additionalNavigationCosts[grid.Transform.Index(cell)] > 0.25f) return false;
        }
        return true;
    }

    private bool SegmentPreservesTerrainCost(Vector2 start, Vector2 end)
    {
        if (terrain.Revision == 0) return true;
        var allowedCost = MathF.Max(terrain.PathCost(start), terrain.PathCost(end)) + 0.001f;
        var distance = Vector2.Distance(start, end);
        var samples = Math.Max(1, (int)MathF.Ceiling(distance / (grid.Transform.CellSize * 0.40f)));
        for (var sample = 1; sample < samples; sample++)
        {
            var position = Vector2.Lerp(start, end, sample / (float)samples);
            if (terrain.PathCost(position) > allowedCost) return false;
        }
        return true;
    }

    private float PointCongestionCost(GridCell cell, Vector2? center)
    {
        if (center is not { } congestionCenter) return 0f;
        var distance = Vector2.Distance(grid.CellCenter(cell), congestionCenter);
        if (distance >= CongestionAvoidanceRadius) return 0f;
        var proximity = 1f - distance / CongestionAvoidanceRadius;
        return proximity * proximity * DetourAvoidanceSeconds;
    }

    private static bool SegmentAvoidsCongestion(Vector2 start, Vector2 end, Vector2? center)
    {
        if (center is not { } congestionCenter) return true;
        var radiusSquared = CongestionAvoidanceRadius * CongestionAvoidanceRadius;
        var startOffset = start - congestionCenter;
        var startDistanceSquared = startOffset.LengthSquared();
        if (startDistanceSquared < radiusSquared)
        {
            var direction = end - start;
            return Vector2.Dot(direction, startOffset) >= 0f &&
                   Vector2.DistanceSquared(end, congestionCenter) > startDistanceSquared;
        }

        if (Vector2.DistanceSquared(end, congestionCenter) < radiusSquared) return false;
        var segment = end - start;
        var lengthSquared = segment.LengthSquared();
        if (lengthSquared < 0.000001f) return startDistanceSquared >= radiusSquared;
        var time = Math.Clamp(Vector2.Dot(congestionCenter - start, segment) / lengthSquared, 0f, 1f);
        var closest = start + segment * time;
        return Vector2.DistanceSquared(closest, congestionCenter) >= radiusSquared;
    }

    private List<GridCell> Reconstruct(int[] cameFrom, int startIndex, int goalIndex)
    {
        var reversed = new List<GridCell>();
        var current = goalIndex;
        while (current != startIndex)
        {
            reversed.Add(grid.Transform.Cell(current));
            current = cameFrom[current];
            if (current < 0) return new List<GridCell>();
        }
        reversed.Add(grid.Transform.Cell(startIndex));
        reversed.Reverse();
        return reversed;
    }

    private static bool SegmentIntersectsAabb(Vector2 start, Vector2 end, Vector2 minimum, Vector2 maximum)
    {
        var direction = end - start;
        var minimumTime = 0f;
        var maximumTime = 1f;
        return ClipAxis(start.X, direction.X, minimum.X, maximum.X, ref minimumTime, ref maximumTime) &&
               ClipAxis(start.Y, direction.Y, minimum.Y, maximum.Y, ref minimumTime, ref maximumTime);
    }

    private static bool ClipAxis(float start, float direction, float minimum, float maximum, ref float minimumTime, ref float maximumTime)
    {
        if (MathF.Abs(direction) < 0.00001f) return start >= minimum && start <= maximum;
        var first = (minimum - start) / direction;
        var second = (maximum - start) / direction;
        if (first > second) (first, second) = (second, first);
        minimumTime = MathF.Max(minimumTime, first);
        maximumTime = MathF.Min(maximumTime, second);
        return minimumTime <= maximumTime;
    }

    /// <summary>Index of the step direction opposite the given one.</summary>
    private static int OppositeDirection(int directionIndex)
    {
        var offset = NeighborOffsets[directionIndex];
        for (var i = 0; i < NeighborOffsets.Length; i++)
        {
            if (NeighborOffsets[i].X == -offset.X && NeighborOffsets[i].Z == -offset.Z) return i;
        }
        return directionIndex;
    }

    /// <summary>
    /// How hard the heuristic pushes, as a multiple of the admissible estimate.
    /// </summary>
    /// <remarks>
    /// <b>One is the textbook answer, and raising it broke two invariants for nothing.</b> Left at one after
    /// the sweep below: at 2.2 the pen-escape and determinism-fingerprint self-tests fail, and the stall does
    /// not improve at any weight. Kept as a named constant rather than deleted because the reasoning is worth
    /// having on the record, and because the day the fallback is fixed this becomes worth re-testing.
    /// <para>
    /// The original note follows, and its conclusion was wrong.
    /// </para>
    /// <b>One is the textbook answer and it cost 1.8 seconds a search on a real village.</b> Measured with
    /// --pathprofile: two cross-map paths closed 1,348,421 cells between them, 94% of the grid, and neither
    /// failed — so the searches were not exhausting an unreachable map, they were barely steering. The reason
    /// is that the estimate prices a cell at the cheapest SURFACE cost that exists while the real step charges
    /// surface, elevation, turning and congestion on top; on sculpted ground the true cost runs several times
    /// the estimate everywhere, which is the definition of a heuristic that has stopped working.
    /// <para>
    /// So this is weighted A*: the returned route may cost up to this factor more than optimal, and in exchange
    /// the search explores something like a corridor instead of half a map. The trade is the right way round
    /// for a game — a body that walks a slightly longer way is invisible, and a second-long freeze is not — but
    /// it IS a trade, and the number is here rather than buried so it can be argued with. Anything above one
    /// forfeits the optimality guarantee the old scale was preserving at that price.
    /// </para>
    /// <para>
    /// <b>And it is worth less than it looks.</b> Swept at 2.2, 3.0, 4.5 and 6.0, the stall window does not
    /// move — every cross-map search still runs out of budget at the same place. So the weak heuristic was not
    /// what made those searches cost 676,000 expansions each; a heuristic that was steering would have found
    /// the goal sooner as the push increased, and this one does not. What is left is that a cross-map route on
    /// a 1,200-cell grid is the wrong question to ask a cell search at all, and this project already built the
    /// right one — the rectangle hierarchy and its flow fields. The weight stays because it is free and 2.2
    /// beat 1.0; the real fix is upstream of here.
    /// </para>
    /// </remarks>
    private const float HeuristicWeight = 1f;

    /// <summary>
    /// Cells one search may close before it gives up and hands back what it has.
    /// </summary>
    /// <remarks>
    /// <b>A ceiling on the worst case, and NOT a cure for the freeze — those turned out to be different
    /// numbers.</b> An expansion costs about 2.6 microseconds here, so this is roughly 650 ms: half of what
    /// the worst measured cross-map search took, and the thing that stops a bigger map taking fifteen seconds
    /// (a 1200 m world is 5.76M cells).
    /// <para>
    /// It is not smaller because the fallback is a truncated route, and truncation costs an invariant. Swept
    /// against the self-tests and --pathprofile:
    /// </para>
    /// <list type="bullet">
    /// <item>30,000 — the stall window drops from 300 ms a tick to 34, and "a group crosses region borders
    /// without swinging" fails.</item>
    /// <item>100,000 — 105 ms a tick, and it still fails.</item>
    /// <item>250,000 — every test passes and the stall is 262 ms a tick, which is barely an improvement.</item>
    /// </list>
    /// <para>
    /// So a bound tight enough to fix the freeze hands bodies partial routes, and a body that re-plans from
    /// the end of a partial route swings where the old one did not. The bound is not the fix; the FALLBACK is.
    /// A body whose search ran out should stay on the cohort's flow field — the coarse layer already holds a
    /// non-swinging answer for that goal — rather than be given a polyline that stops halfway. That is the
    /// next slice, and until it lands this constant is a ceiling and not a solution.
    /// </remarks>
    private const int ExpansionBudget = 250_000;

    private static float Heuristic(GridCell from, GridCell to)
    {
        var dx = Math.Abs(from.X - to.X);
        var dz = Math.Abs(from.Z - to.Z);
        return Math.Max(dx, dz) + (DiagonalCost - 1f) * Math.Min(dx, dz);
    }
}

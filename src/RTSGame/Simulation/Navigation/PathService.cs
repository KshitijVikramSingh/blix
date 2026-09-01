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
        ClimbCalls++;
        if (!grid.HasRelief) return 0f;
        var dx = toX - fromX;
        var dz = toZ - fromZ;
        var cells = MathF.Sqrt(dx * dx + dz * dz);
        if (cells < 0.5f) return 0f;
        // One sample every four cells — two metres at the half-metre grid — which resolves a landform
        // hundreds of metres across many times over, and capped so a leg the width of the map is bounded.
        var samples = Math.Clamp((int)MathF.Ceiling(cells / 4f), 1, 48);
        ClimbSamples += samples;
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
    /// What building one cost field spends its time on, split four ways.
    /// </summary>
    /// <remarks>
    /// <b>Because this term got slower under optimisation and nobody could say why.</b> §118 measured the
    /// field solve at 45–51 ms in Debug and 75–77 in Release, on byte-identical work — two fields, 27,624
    /// climb calls — and the terms are additive in both configs, so it is not a boundary absorbing tile time.
    /// A term that improves when you stop optimising is either a real deoptimisation or a mis-attributed
    /// region, and those want opposite work.
    /// <para>
    /// The four are the four things the constructor does: mark which rectangles hold pressure, lay out the
    /// corner positions, seed from the goal's rectangle, and run the corner Dijkstra.
    /// </para></remarks>
    internal long FieldPressureTicks;

    internal long FieldCornerTicks;

    internal long FieldSeedTicks;

    internal long FieldSearchTicks;

    internal long FieldCorners;

    internal long FieldSettled;

    internal long FieldLegs;

    /// <summary>
    /// Corner-climb lookups that found an answer and those that had to sample the ground.
    /// </summary>
    /// <remarks>
    /// §126: the climb term costs 55 ms in Release and 3 ms in Debug on the same number of legs, which is
    /// eighteen times and cannot be a property of the same work done twice. Either the optimised build is
    /// missing this cache far more often — which would be a correctness question about the key, not a speed
    /// one — or the same hits are somehow dearer. A hit and a miss differ by a height sampling, so counting
    /// them apart is the whole question.
    /// </remarks>
    internal long ClimbCacheHits;

    internal long ClimbCacheMisses;

    /// <summary>
    /// What routing has spent, split into the three places an order's time can go.
    /// </summary>
    /// <remarks>
    /// Cumulative, so a caller measuring one order snapshots and subtracts. Milliseconds rather than ticks
    /// because every consumer is a report, and the counts travel with them: 340 ms over one mesh build and
    /// 340 ms over forty tile fills are different problems wearing one number.
    /// </remarks>
    public (double MeshMs, int MeshBuilds, int MeshCacheHits, int MeshRectangles,
            double TileMs, long TileFills, double FieldMs, int Fields,
            double TileSeedMs, double TileSearchMs, int TileSeedCells,
            long RegionRelaxations, long RegionSteps) RoutingCost =>
        (MeshBuildTicks * 1000.0 / Stopwatch.Frequency, MeshBuilds, MeshCacheHits, MeshRectangles,
         TileFillTicks * 1000.0 / Stopwatch.Frequency, TileRefinements,
         FieldSetupTicks * 1000.0 / Stopwatch.Frequency, FlowFieldBuilds,
         TileSeedTicks * 1000.0 / Stopwatch.Frequency, TileSearchTicks * 1000.0 / Stopwatch.Frequency,
         TileSeedCells, RegionRelaxations, RegionSteps);
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

    /// <summary>
    /// A route for one body, and a record of what asking cost.
    /// </summary>
    /// <remarks>
    /// <b>The reason is required because the count was not enough.</b> §115 established that the click after
    /// a placement change is two route queries and some 60% of the event, and could not say which two: a
    /// counter cannot distinguish a cohort member the shared field refused from a villager whose stored route
    /// died with the navigation revision, and those want opposite fixes. Every request now names itself and is
    /// charged its own expansions, its own milliseconds and its own ending. See <see cref="RouteAttribution"/>.
    /// <para>
    /// The wrapper exists so the attribution cannot be forgotten at one of the seven places the search can
    /// return from. It reads the counters the core keeps and turns their deltas into an outcome.
    /// </para></remarks>
    public PathResult? FindPath(
        Vector2 start,
        Vector2 requestedGoal,
        float agentRadius,
        RouteReason reason,
        Vector2? congestionAvoidanceCenter = null,
        float[]? additionalNavigationCosts = null,
        float agentSpeed = 0f)
    {
        var startTimestamp = Stopwatch.GetTimestamp();
        var expansionsAtEntry = PathExpansions;
        var refusalsAtEntry = SearchesDeniedByOrderBudget;
        var stopsAtEntry = PathBudgetStops;
        var truncationsAtEntry = PathTruncatedToStart;
        var failuresAtEntry = PathFailures;
        var result = FindPathCore(
            start,
            requestedGoal,
            agentRadius,
            reason,
            congestionAvoidanceCenter,
            additionalNavigationCosts,
            agentSpeed,
            out var resolvedGoal,
            out var goalFieldExisted);
        var spent = PathExpansions - expansionsAtEntry;
        // <b>Ordered narrowest cause first, and nullness is not one of the causes.</b> A truncation is also a
        // budget stop and a refusal ran no search at all, so the tests have to run inwards. Nullness was in
        // this chain once, ahead of the budget, and it filed a budget-stopped search whose partial route the
        // smoothing then rejected as "unresolvable" — a label that points at goal resolution and would have
        // cost the next session a wrong afternoon. What the body received is a separate axis.
        var outcome =
            SearchesDeniedByOrderBudget > refusalsAtEntry ? RouteOutcome.Refused
            : PathTruncatedToStart > truncationsAtEntry ? RouteOutcome.Truncated
            : PathFailures > failuresAtEntry ? RouteOutcome.Exhausted
            : PathBudgetStops > stopsAtEntry ? RouteOutcome.Partial
            : spent > 0 ? RouteOutcome.Reached
            : result is not null ? RouteOutcome.Direct
            : RouteOutcome.Unresolvable;
        Routes.Record(
            reason,
            outcome,
            result is not null,
            spent,
            Stopwatch.GetTimestamp() - startTimestamp,
            CellSpan(start, requestedGoal),
            resolvedGoal is { } reached ? grid.Transform.Index(reached) : -1,
            goalFieldExisted);
        return result;
    }

    /// <summary>Chebyshev cells between the two ends of a request, so its size can be read off the log.</summary>
    private int CellSpan(Vector2 start, Vector2 goal) =>
        grid.TryWorldToCell(start, out var from) && grid.TryWorldToCell(goal, out var to)
            ? Math.Max(Math.Abs(from.X - to.X), Math.Abs(from.Z - to.Z))
            : -1;

    /// <summary>What routing was asked for and how it ended, per reason. Diagnostics; never read back.</summary>
    internal readonly RouteAttribution Routes = new();

    private PathResult? FindPathCore(
        Vector2 start,
        Vector2 requestedGoal,
        float agentRadius,
        RouteReason reason,
        Vector2? congestionAvoidanceCenter,
        float[]? additionalNavigationCosts,
        float agentSpeed,
        out GridCell? reportedGoal,
        out bool goalFieldExisted)
    {
        reportedGoal = null;
        goalFieldExisted = false;
        PathQueries++;
        if (!OrderBudgetRemains)
        {
            // The order's pooled allowance is gone. Refusing here is what makes the bound a bound: the
            // twentieth body cannot spend what the first nineteen already have.
            SearchesDeniedByOrderBudget++;
            return null;
        }

        requestedGoal = terrain.ClampPosition(requestedGoal, agentRadius + BodyFootprint.NavigationMargin);
        if (!grid.TryWorldToCell(start, out var startCell))
        {
            PathNoStartCell++;
            return null;
        }

        if (!grid.TryWorldToCell(requestedGoal, out var requestedCell))
        {
            PathNoGoalCell++;
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
        if (pathStartCell is not { } resolvedStartCell)
        {
            PathStartUnresolvable++;
            return null;
        }
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
        if (goalCell is not { } goal)
        {
            PathGoalUnresolvable++;
            return null;
        }
        reportedGoal = goal;
        // Asked before the search runs, because the search does not build fields and asking afterwards would
        // report the state this query left rather than the state it found.
        // <b>Not in a crowd, and not asking for a way round one.</b> Both halves earned their place: the
        // reason list keeps a body that was sent to find a different answer from being steered back to the
        // shared one, and the pressure test keeps a body standing in a jam from being steered at all. The
        // first alone stranded two bodies in the two-exit pen.
        var crowded = grid.TryWorldToCell(start, out var pressureCell) &&
                      congestion.At(pressureCell) >= CrowdedPressure;
        var available = FieldForGoal(goal, agentRadius, agentSpeed);
        var mayGuide = GuidedSearch && !crowded && !RouteReasons.WantsItsOwnAnswer(reason);
        var guide = mayGuide ? available : null;
        // <b>That a field existed, not that it was used.</b> Conflating the two made the report say "0 had a
        // field" the moment the crowd gate started refusing them, which reads as "there was nothing to use"
        // when what happened is "there was, and we declined it on purpose".
        goalFieldExisted = available is not null;
        var cells = FindCellPath(
            resolvedStartCell,
            goal,
            agentRadius,
            congestionAvoidanceCenter,
            additionalNavigationCosts,
            CongestionSpeedScale(agentSpeed),
            guide,
            abandonAt: mayGuide && GuidedRestart && guide is null ? GuideRestartExpansions : int.MaxValue);
        // <b>The search says when it is worth building a field, rather than a distance threshold guessing.</b>
        // A field costs tens of milliseconds and serves every later ask for the same goal, so the question is
        // never "is this far" but "is this search expensive" — and the search itself is the only thing that
        // knows. Measured: thirteen villagers called home from across the map are thirteen asks for ONE goal,
        // 2.23M cells between them, so the first ask paying for a field makes the other twelve nearly free.
        // Bounded by construction: the wasted prefix is GuideRestartExpansions and never more.
        if (cells is null && abandonedToGuide)
        {
            abandonedToGuide = false;
            GuidedRestarts++;
            var built = GetFlowField(goal, agentRadius, congestion.Revision, agentSpeed: agentSpeed);
            goalFieldExisted = true;
            cells = FindCellPath(
                resolvedStartCell,
                goal,
                agentRadius,
                congestionAvoidanceCenter,
                additionalNavigationCosts,
                CongestionSpeedScale(agentSpeed),
                built);
        }
        if (cells is null)
        {
            PathSearchFoundNothing++;
            return null;
        }

        var destination = goal == requestedCell ? requestedGoal : grid.CellCenter(goal);
        var waypoints = SmoothPath(
            start,
            destination,
            cells,
            agentRadius,
            congestionAvoidanceCenter,
            additionalNavigationCosts,
            includeFirstCell: startWasAdjusted || terrain.Revision > 0);
        // <b>Counted, because this is where a quarter of a million cells of work goes in the bin.</b> §120: a
        // player clicked a far corner and twelve searches came back "Partial, NOTHING" — the search had found
        // a route to the furthest cell it reached, the smoothing threw it away, and the body was left with no
        // destination and asked again next tick. Which of the two rejections fired was not recorded anywhere,
        // and they want different fixes: an empty smoothing result is a smoothing bug, and a blocked first
        // step is the §116 footing problem again.
        if (waypoints.Length == 0)
        {
            PathSmoothedToNothing++;
            return null;
        }

        if (!IsInitialBodyStepClear(start, waypoints[0], agentRadius))
        {
            PathFirstStepBlocked++;
            return null;
        }

        return new PathResult(waypoints, destination, cells.ToArray());
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
            GradientRefusedOffGrid++;
            return Vector2.Zero;
        }
        var goal = grid.IsWalkable(requestedGoalCell, agentRadius)
            ? requestedGoalCell
            : FindNearestWalkable(requestedGoalCell, agentRadius);
        if (goal is not { } resolvedGoal)
        {
            GradientRefusedNoGoal++;
            return Vector2.Zero;
        }

        var costs = GetFlowField(
            resolvedGoal, agentRadius, congestionRevision, agentSpeed: agentSpeed);
        var centerCost = costs.CostAt(current);
        if (!float.IsFinite(centerCost))
        {
            // <b>Split, because the tile was filled and the cell still came back infinite.</b> CostAt fills a
            // region's tile on demand, so this is never "nobody has asked yet" — it is a cell the region
            // search could not reach from its seeded perimeter. Two things do that and they are different
            // bugs: a body standing where it does not fit (its own cell is unwalkable at its radius, so no
            // rectangle contains it and nothing can price it), and a body that fits but sits in a pocket the
            // corner graph does not reach into. The first is a placement or depenetration problem; only the
            // second is what FindFieldEntry was built for.
            if (grid.IsWalkable(current, agentRadius))
            {
                GradientRefusedUnpriced++;
            }
            else
            {
                // <b>By how much, measured against the predicate that actually refused it.</b> A shortfall of
                // centimetres and a shortfall of a metre are different situations — a body resting on the
                // clearance boundary, and a body inside something — and only the first is what this turned out
                // to be. The margin is part of the test (IsWalkable wants radius + NavigationMargin), so it is
                // part of the figure: leaving it out understates the overhang by 3.5 cm and quotes a number no
                // predicate in the codebase uses.
                GradientRefusedNoFooting++;
                GradientNoFootingWorstShortfall = MathF.Max(
                    GradientNoFootingWorstShortfall,
                    agentRadius + BodyFootprint.NavigationMargin - grid.Clearance(current));
                GradientNoFootingWorstClearance = MathF.Min(
                    GradientNoFootingWorstClearance, grid.Clearance(current));
            }
            return Vector2.Zero;
        }

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
    /// <summary>
    /// Whether a cost field for this goal already exists, ignoring congestion.
    /// </summary>
    /// <remarks>
    /// <b>Asked before deciding whether the hierarchy can replace a cell search.</b> The trade is entirely
    /// about repetition: a field costs tens of milliseconds to build and nothing to reuse, so routing a solo
    /// body down one is free if its goal has been asked about before and a straight loss if it has not.
    /// "Villagers walk to the same few places" is a claim about this settlement's behaviour and not something
    /// to assume — see §121.
    /// <para>
    /// Congestion is deliberately out of the key: <see cref="latestFields"/> holds the newest field per goal
    /// regardless of congestion revision precisely so the next revision can adopt what is still valid, so a
    /// field that exists at all is one this query could have started from.
    /// </para></remarks>
    internal RectangleFlowField? FieldForGoal(
        GridCell goal,
        float agentRadius,
        float agentSpeed,
        bool chargeTurns = true)
    {
        var speedScale = congestion.LiveCellCount == 0 ? 1f : CongestionSpeedScale(agentSpeed);
        // <b>Read, never built.</b> Going through GetFlowField would key on the current congestion revision
        // and build a fresh field on a miss, which is exactly the cost this exists to avoid: measured, only
        // four asks in twenty-four find a field, so paying to build one per query is a straight loss.
        // Congestion staleness is acceptable in a guide — it steers a search, it does not price the route.
        return latestFields.GetValueOrDefault((
            grid.Transform.Index(goal),
            (int)MathF.Round(agentRadius * 100f),
            (int)MathF.Round(speedScale * 8f),
            grid.Revision,
            chargeTurns));
    }

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
            ChargeFieldClimb,
            congestion,
            CongestionSecondsPerPressure,
            this,
            agentRadius,
            speedScale,
            chargeTurns,
            CornerClimbCache());
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
        var diagonal = from.X != to.X && from.Z != to.Z;
        // One call, four ground reads, the same five predicates — see NavigationGrid.CanTraverseDiagonal. The
        // corner tests were 45% of a region tile's search, spent almost entirely on reading the same four
        // cells over and over.
        return diagonal
            ? grid.CanTraverseDiagonal(from, to, agentRadius)
            : grid.CanTraverse(from, to, agentRadius);
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

    /// <summary>
    /// The nearest position the cohort's field can actually price, within a bounded radius.
    /// </summary>
    /// <remarks>
    /// <b>The replacement for a cross-map A* that a dropped body did not need.</b> §96 measured why bodies
    /// leave shared transit on a real village: not rejected steps, but the field returning no gradient — the
    /// body is standing in a pocket the region tile's perimeter seeding could not reach, which happens
    /// wherever twenty-five thousand tree blockers cut the walkable area into rectangles a perimeter cannot
    /// see into. The old answer was to give up on the field and search the whole map to the final goal, at a
    /// quarter of a million expansions. The field already knows the way from any priced cell, so the only
    /// question worth asking is "where is the nearest one", and that is a few hundred cells of breadth-first
    /// search rather than a map.
    /// <para>
    /// Bounded hard: past this radius the body is somewhere the field genuinely cannot serve and the caller
    /// should do whatever it did before. Half a per cent of the grid, not a fraction of the answer.
    /// </para>
    /// </remarks>
    /// <summary>
    /// The nearest point to a requested target that the cohort can actually reach, and whether it moved.
    /// </summary>
    /// <remarks>
    /// <b>Resolved once for an order rather than once per body, which is the whole of §102's eighteen
    /// seconds.</b> Twenty bodies each discovering that a clearing inside a wood cannot be entered is twenty
    /// full-map searches for one answer; the answer is a property of the order, not of the body.
    /// <para>
    /// Reachability comes from the mesh's connected components, so the test is two array reads rather than a
    /// search: a target in the cohort's own component is taken as asked, and one outside it walks outward from
    /// the target until it meets ground in that component. That walk is bounded — past the bound the honest
    /// answer is "nowhere near it", and the caller decides what to do with that.
    /// </para>
    /// <para>
    /// <b>Best effort by construction.</b> This never refuses: it returns the requested point, a nearer one, or
    /// nothing at all, and the caller can tell which. A body told to walk somewhere it cannot reach should walk
    /// as near as it can get — that is what a person does, and with fog it is the ordinary case rather than the
    /// exception, because a wood is not known to be impassable until somebody has stood at its edge.
    /// </para>
    /// </remarks>
    public (Vector2 Target, bool WasMoved, float Shortfall)? ResolveReachableGoal(
        Vector2 from,
        Vector2 requestedGoal,
        float agentRadius)
    {
        var clamped = terrain.ClampPosition(requestedGoal, agentRadius + BodyFootprint.NavigationMargin);
        if (!grid.TryWorldToCell(from, out var fromCell) ||
            !grid.TryWorldToCell(clamped, out var goalCell))
        {
            return null;
        }

        var (mesh, meshIndex) = Mesh(agentRadius);
        var fromRectangle = meshIndex.RectangleAt(fromCell);
        if (fromRectangle < 0)
        {
            // The anchor is somewhere the decomposition does not cover — a pocket too tight for this radius.
            // Counted apart from an unreachable target because they are different facts: one is about where
            // the cohort is standing and one is about where it was sent, and reporting the first as the second
            // is how "nothing reachable" ended up printed for a target that had been reached twice.
            GoalsAnchorUnplaced++;
            return null;
        }

        var component = mesh.ComponentOf(fromRectangle);
        var goalRectangle = meshIndex.RectangleAt(goalCell);
        if (goalRectangle >= 0 && mesh.ComponentOf(goalRectangle) == component)
        {
            GoalsTakenAsAsked++;
            return (clamped, false, 0f);
        }

        // Outward from the target, nearest first, for ground in the cohort's own island.
        goalWalkStamp ??= new int[grid.Width * grid.Height];
        if (goalWalkStamp.Length != grid.Width * grid.Height)
        {
            goalWalkStamp = new int[grid.Width * grid.Height];
        }

        goalWalkGeneration++;
        var queue = goalWalkQueue;
        queue.Clear();
        queue.Enqueue(goalCell);
        goalWalkStamp[grid.Transform.Index(goalCell)] = goalWalkGeneration;
        var examined = 0;
        while (queue.TryDequeue(out var current))
        {
            if (++examined > ReachableGoalBudget) break;
            var rectangle = meshIndex.RectangleAt(current);
            if (rectangle >= 0 && mesh.ComponentOf(rectangle) == component)
            {
                var landing = grid.CellCenter(current);
                GoalsMovedToReachable++;
                return (landing, true, Vector2.Distance(landing, clamped));
            }

            for (var i = 0; i < 4; i++)
            {
                var offset = NeighborOffsets[i];
                var next = new GridCell(current.X + offset.X, current.Z + offset.Z);
                if (!grid.Contains(next)) continue;
                var index = grid.Transform.Index(next);
                if (goalWalkStamp[index] == goalWalkGeneration) continue;
                goalWalkStamp[index] = goalWalkGeneration;
                queue.Enqueue(next);
            }
        }

        GoalsUnreachable++;
        return null;
    }

    /// <summary>
    /// Cells the outward walk from an unreachable target may examine.
    /// </summary>
    /// <remarks>
    /// Generous, because this runs once per order rather than once per body: forty thousand cells is a hundred
    /// metres of radius on a half-metre grid and a few milliseconds, against the eighteen seconds the twenty
    /// per-body searches it replaces were costing. Past it, the target is not near anything the cohort can
    /// stand on and the caller should say so rather than walk them somewhere arbitrary.
    /// </remarks>
    private const int ReachableGoalBudget = 40_000;

    private int[]? goalWalkStamp;
    private int goalWalkGeneration;
    private readonly Queue<GridCell> goalWalkQueue = new();

    /// <summary>How order goals resolved: as asked, moved to reachable ground, or nowhere near any.</summary>
    public long GoalsTakenAsAsked { get; private set; }

    public long GoalsMovedToReachable { get; private set; }

    public long GoalsUnreachable { get; private set; }

    /// <summary>Resolutions abandoned because the cohort's anchor was not on the decomposition.</summary>
    public long GoalsAnchorUnplaced { get; private set; }

    /// <summary>
    /// Seconds of travel from a position to a goal, by the field's own reckoning.
    /// </summary>
    /// <remarks>
    /// <b>The honest measure of progress, and straight-line distance is not it.</b> A cohort walking round a
    /// river or a wood closes no straight-line distance for as long as the detour lasts, which looks exactly
    /// like a cohort that has stopped approaching — and §102 recorded the halfway order as "moves fifty-one
    /// metres, closes two" on precisely that measure. Cost-to-goal falls whenever the bodies are getting
    /// closer along the route they are actually walking, so the two figures together say which it is.
    /// </remarks>
    public float? CostToGoal(Vector2 position, Vector2 requestedGoal, float agentRadius)
    {
        if (!grid.TryWorldToCell(position, out var cell) ||
            !grid.TryWorldToCell(requestedGoal, out var requestedGoalCell))
        {
            return null;
        }

        var goal = grid.IsWalkable(requestedGoalCell, agentRadius)
            ? requestedGoalCell
            : FindNearestWalkable(requestedGoalCell, agentRadius);
        if (goal is not { } resolved) return null;
        var cost = GetFlowField(resolved, agentRadius, CongestionRevision).CostAt(cell);
        return float.IsFinite(cost) ? cost : null;
    }

    public Vector2? FindFieldEntry(
        Vector2 position,
        Vector2 requestedGoal,
        float agentRadius,
        int congestionRevision = int.MaxValue,
        float agentSpeed = 0f)
    {
        if (!grid.TryWorldToCell(position, out var origin) ||
            !grid.TryWorldToCell(requestedGoal, out var requestedGoalCell))
        {
            return null;
        }

        var goal = grid.IsWalkable(requestedGoalCell, agentRadius)
            ? requestedGoalCell
            : FindNearestWalkable(requestedGoalCell, agentRadius);
        if (goal is not { } resolvedGoal) return null;
        var costs = GetFlowField(resolvedGoal, agentRadius, congestionRevision, agentSpeed: agentSpeed);

        // Breadth-first over cells, nearest out, so the first priced cell found is the nearest one. The
        // visited set is a stamp array rather than a fresh allocation: this runs on the tick.
        fieldEntryStamp ??= new int[grid.Width * grid.Height];
        if (fieldEntryStamp.Length != grid.Width * grid.Height)
        {
            fieldEntryStamp = new int[grid.Width * grid.Height];
        }

        fieldEntryGeneration++;
        var queue = fieldEntryQueue;
        queue.Clear();
        queue.Enqueue(origin);
        fieldEntryStamp[grid.Transform.Index(origin)] = fieldEntryGeneration;
        var examined = 0;
        while (queue.TryDequeue(out var current))
        {
            if (++examined > FieldEntryBudget) return null;
            if (grid.IsWalkable(current, agentRadius) && float.IsFinite(costs.CostAt(current)))
            {
                FieldEntriesFound++;
                return grid.CellCenter(current);
            }

            for (var i = 0; i < 4; i++)
            {
                var offset = NeighborOffsets[i];
                var next = new GridCell(current.X + offset.X, current.Z + offset.Z);
                if (!grid.Contains(next)) continue;
                var index = grid.Transform.Index(next);
                if (fieldEntryStamp[index] == fieldEntryGeneration) continue;
                fieldEntryStamp[index] = fieldEntryGeneration;
                queue.Enqueue(next);
            }
        }

        FieldEntriesMissed++;
        return null;
    }

    /// <summary>
    /// Cells the field-entry search may examine — a few cells' radius, deliberately.
    /// </summary>
    /// <remarks>
    /// <b>Small because the old fallback was doing a second job by accident.</b> A body that dropped out of
    /// transit used to get its own cross-map A*, and in a pen that is what spread the cohort across several
    /// exits: every body solving separately is diversity, expensively bought. Replacing it wholesale with the
    /// shared gradient funnels them, and both pen-distribution self-tests said so.
    /// <para>
    /// So the two cases are separated by distance, which is what actually distinguishes them. A body standing
    /// a cell or two off priced ground is in a pocket the tile's perimeter seeding could not see into, and
    /// stepping onto the field is exactly right — that was the village's freeze, and its entry was one cell
    /// away. A body that would have to walk a long way to find the field is in a different situation, and it
    /// gets the old answer, diversity included.
    /// </para>
    /// </remarks>
    private const int FieldEntryBudget = 400;

    private int[]? fieldEntryStamp;
    private int fieldEntryGeneration;
    private readonly Queue<GridCell> fieldEntryQueue = new();

    /// <summary>How often a dropped body found its way back onto the field, and how often it could not.</summary>
    public long FieldEntriesFound { get; private set; }

    public long FieldEntriesMissed { get; private set; }

    /// <summary>
    /// Why a path request came back with nothing, by cause.
    /// </summary>
    /// <remarks>
    /// <b>Because "they stood in place" is four different bugs.</b> A body given an order and not moving has
    /// been refused a route, and the refusals are not alike: a target off the grid is a control problem, a
    /// target inside solid ground is a resolution problem, a search that exhausted the reachable set is a
    /// connectivity problem, and a search truncated back to where it started is a budget problem. They want
    /// different answers — clamp the click, resolve the goal outward, accept a nearer goal, raise or slice the
    /// budget — and one counter for all of them would tell us which quarter of the time we are wrong.
    /// </remarks>
    public long PathNoStartCell { get; private set; }

    public long PathNoGoalCell { get; private set; }

    public long PathStartUnresolvable { get; private set; }

    public long PathGoalUnresolvable { get; private set; }

    public long PathSearchFoundNothing { get; private set; }

    public long PathTruncatedToStart { get; private set; }

    /// <summary>Routes the search found and the smoothing then discarded, by which rejection fired.</summary>
    /// <remarks>
    /// Both of these are work performed and thrown away, which is the most expensive kind of refusal there
    /// is: the caller cannot tell it from "there is no route", so it asks again.
    /// </remarks>
    public long PathSmoothedToNothing { get; private set; }

    public long PathFirstStepBlocked { get; private set; }

    /// <summary>
    /// Transit-drop and rejoin counters, kept here rather than on the world.
    /// </summary>
    /// <remarks>
    /// The determinism census walks the world's fields and demands each be fingerprinted or argued away; this
    /// service is already argued as derived, so diagnostics that nothing reads back belong here. Public fields
    /// rather than properties because the world increments them.
    /// </remarks>
    /// <summary>
    /// Why the shared field had no direction for a body, by cause.
    /// </summary>
    /// <remarks>
    /// <b>Because a body the field refuses is handed a cross-map search, and this is where that decision is
    /// taken.</b> The three causes are not alike and only one of them is about the field: a position or goal
    /// off the grid is a control problem, an unresolvable goal is a resolution problem, and a body standing on
    /// ground the field never priced is the case §96 built <see cref="FindFieldEntry"/> for. Attributing the
    /// placement click found two <see cref="RouteReason.OrderSlot"/> searches costing 1,232 ms between them
    /// and neither reaching its goal, and the whole of that spend hangs off which of these three it was.
    /// </remarks>
    public long GradientRefusedOffGrid;

    public long GradientRefusedNoGoal;

    /// <summary>Walkable ground the field could not price: a pocket the corner graph does not reach.</summary>
    public long GradientRefusedUnpriced;

    /// <summary>The body's own cell does not admit a body of its radius, so nothing can price it.</summary>
    public long GradientRefusedNoFooting;

    /// <summary>Metres by which the worst such body failed its cell's clearance test.</summary>
    public float GradientNoFootingWorstShortfall;

    /// <summary>The least clearance any refused body was standing in, for the same figure read the other way.</summary>
    public float GradientNoFootingWorstClearance = float.PositiveInfinity;

    public long FlowTransitDropsRejected;

    public long FlowTransitDropsNoGradient;

    public long FieldRejoins;

    /// <summary>How orders ended for the bodies given them. See SimulationWorld.OrderOutcomes.</summary>
    public long OrdersOnTransit;

    public long OrdersOnFieldEntry;

    public long OrdersOnSlotPath;

    public long OrdersRefused;

    /// <summary>The highest local pressure seen at a transit drop, for choosing the ceiling by measurement.</summary>
    public float WorstDropPressure;

    /// <summary>Climb queries and the height samples they cost. See §97.</summary>
    public long ClimbCalls;

    public long ClimbSamples;

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

    /// <summary>
    /// Whether a cell search may steer by the cost field for its goal. <b>Off: it was measured and rejected.</b>
    /// </summary>
    /// <remarks>
    /// <b>Twenty-seven times cheaper and up to five times longer, so it is off.</b> §121 tried the thing §95
    /// had proposed — the flat estimate prices a cell at the cheapest surface cost that exists anywhere, while
    /// the corner graph knows what the ground between here and the goal actually costs — and the speed was
    /// everything hoped for: thirteen villagers called home across the map fell from 2,233,924 expansions and
    /// 741 ms to 82,474 and 113 ms, with the worst tick going 89 to 31.
    /// <para>
    /// The routes were the problem. Measured pair by pair against the search it replaces: <b>1.14x, 1.37x,
    /// 1.79x, 1.89x and 5.02x</b> — a 125 m walk became 627 m. The corner-graph estimate is not an admissible
    /// lower bound and is a large enough overestimate to turn A* into something close to greedy best-first,
    /// which is exactly the shape of that result: fast searches, bad paths. The raid leg failed it
    /// independently and for the same reason — raiders walked so far round that two of twenty-four got home
    /// against thirteen, and one lived 297 s against a 194 s round trip.
    /// </para>
    /// <para>
    /// Kept as a lever rather than deleted, on §84's precedent, because the <em>idea</em> is still right and
    /// only this estimate is wrong: the next attempt should guide with the field's own <c>CostAt</c> — the
    /// tile-filled figure, which is exact where it is filled — rather than the analytic corner-graph one, or
    /// scale the estimate until the fixture's route-quality leg stops complaining. That leg exists now and is
    /// the acceptance test, and it earned its place by catching this after a first version of it compared two
    /// unguided arms and reported a perfect 1.000x five times over.
    /// </para></remarks>
    internal static bool GuidedSearch;

    /// <summary>
    /// Whether a cost field charges climb. A measurement lever, not a game option.
    /// </summary>
    /// <remarks>
    /// §126 needs to know why the corner Dijkstra is slower in Release than in Debug at identical work, and
    /// 94% of its legs resolve to a lookup in the corner-climb dictionary. Turning the charge off removes
    /// those lookups and leaves everything else, which is the cheapest way to ask whether they are the term
    /// that inverts. Routes are wrong with this off — it is for a stopwatch, never for play.
    /// </remarks>
    internal static bool ChargeFieldClimb = true;

    /// <summary>Whether the climb key is looked up once formed. A stopwatch lever; see ChargeFieldClimb.</summary>
    internal static bool LookUpFieldClimb = true;

    /// <summary>
    /// Local pressure above which a body is treated as being in a crowd.
    /// </summary>
    /// <remarks>
    /// <b>Measured in §96 and now read by two rules instead of one.</b> The village's cross-map transit drops
    /// sit at 0.083 — bodies with room, standing near each other because they were ordered together — and a
    /// pen holding fifty against a single-cell gate is an order of magnitude above that. Half sits between the
    /// two with room on both sides.
    /// <para>
    /// It decided one thing: whether a dropped body hops back onto the shared field or solves its own route.
    /// §121 gives it a second, and it is the same judgement — a body in a crowd is not steered by the shared
    /// answer either. Exempting callers by name was tried first and was the wrong cut: it is not <em>who</em>
    /// asks that matters but <em>where the body is standing</em>, and the two-exit pen said so by stranding
    /// two bodies whose reason was on the permitted list.
    /// </para></remarks>
    internal const float CrowdedPressure = 0.5f;

    /// <summary>
    /// Cells an unguided search may close before it is worth building a field and starting again.
    /// </summary>
    /// <remarks>
    /// Roughly seven milliseconds here, and it is a ceiling on waste rather than a tuning dial: a search that
    /// has closed this many cells without finishing is one the flat estimate has stopped steering, and every
    /// cell after it would be spent at the same rate. The alternative was a distance threshold, which guesses
    /// at the same question from the outside and is wrong on any map where the ground is not what the straight
    /// line suggests — which is every map this game generates.
    /// </remarks>
    private const int GuideRestartExpansions = 20_000;

    /// <summary>
    /// Whether an expensive search may build a field and start again. Off with <see cref="GuidedSearch"/>.
    /// </summary>
    /// <remarks>
    /// This is the half that made the guide reach anything: a field only helps where one exists, and measured
    /// across a session only four asks in twenty-four found one. Within a single burst the picture reverses —
    /// thirteen villagers called home are thirteen asks for one goal — so the first ask paying to build it
    /// makes the rest nearly free. The mechanism worked; what it steered was wrong.
    /// </remarks>
    internal static bool GuidedRestart;

    /// <summary>Searches abandoned partway and rerun against a freshly built field.</summary>
    public long GuidedRestarts { get; private set; }

    /// <summary>Guide lookups that the corner graph could price, and those it could not. See Estimate.</summary>
    public long GuidedEstimates { get; private set; }

    public long GuidedFallbacks { get; private set; }

    private bool abandonedToGuide;

    private List<GridCell>? FindCellPath(
        GridCell start,
        GridCell goal,
        float agentRadius,
        Vector2? congestionAvoidanceCenter,
        float[]? additionalNavigationCosts,
        float congestionSpeedScale,
        RectangleFlowField? guide = null,
        int abandonAt = int.MaxValue)
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
        // <b>The estimate the hierarchy already holds, when it holds one.</b> Measured in §121: every search
        // that ran to its 250,000-cell ceiling had a cost field for its goal already built and ignored it,
        // while every cheap search had none. So this costs nothing to obtain — the field is read, never built
        // — and it is the difference between an estimate that prices a cell at the cheapest surface cost that
        // exists anywhere and one that knows what the ground between here and the goal actually costs.
        //
        // Falls back per cell rather than per search: the corner graph cannot price a cell in a pocket it does
        // not reach into, and an infinite estimate there would put that cell behind every other candidate
        // forever. The admissible straight-line figure is the honest answer for those.
        float Estimate(GridCell cell)
        {
            if (guide is null) return Heuristic(cell, goal) * heuristicScale;
            // <b>The analytic figure, and CostAt is not the fix.</b> §122 tried it — the tile-filled cost, exact
            // within a region and the thing transit actually steers by — and the worst route went 5.02x to
            // 4.86x. A tile is seeded from its own perimeter priced by the corner graph, so it inherits that
            // error rather than correcting it, and CostAt fills tiles as a side effect of being asked. Left on
            // the analytic answer, which is cheaper and no less accurate.
            var guided = guide.AnalyticCostAt(cell);
            if (float.IsFinite(guided))
            {
                GuidedEstimates++;
                return guided;
            }

            // <b>Counted, because the two branches are not on one scale.</b> The analytic figure is about what
            // the ground really costs; this one is a weak lower bound and is several times smaller. A cell the
            // corner graph cannot price therefore looks CHEAPER than its priced neighbours, which is an
            // invitation to dive into exactly the pockets the hierarchy could not see into. Whether that is
            // what ruined §121's routes is a question about how often this line runs.
            GuidedFallbacks++;
            return Heuristic(cell, goal) * heuristicScale;
        }

        open.Enqueue(start, Estimate(start));

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
            orderExpansionsSpent++;
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

            // Before the budget test, because this is not a refusal: the caller is being told to ask again
            // with a guide, and the expansions spent so far are the price of finding that out.
            if (PathExpansions - expansionsAtEntry >= abandonAt)
            {
                abandonedToGuide = true;
                return null;
            }

            if (PathExpansions - expansionsAtEntry >= ExpansionBudget || !OrderBudgetRemains)
            {
                // Out of budget with the goal unreached. Handing back the partial route beats both
                // alternatives: a null makes a reachable destination look unreachable and the body gives up,
                // and carrying on takes the frame. The body walks the part that was solved and asks again from
                // there, which is also what it would do if the world had changed under it.
                PathBudgetStops++;
                PathExpansionsWorst = Math.Max(PathExpansionsWorst, PathExpansions - expansionsAtEntry);
                if (bestIndex == startIndex)
                {
                    // The budget ran out before anything closer to the goal than the start was found, so the
                    // only honest answer is none — and a body that gets none stands still, which is the
                    // symptom this counter exists to attribute.
                    PathTruncatedToStart++;
                    return null;
                }

                return Reconstruct(cameFrom, startIndex, bestIndex);
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
                open.Enqueue(next, nextCost + Estimate(next));
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
                    //
                    // <b>But everything already accepted IS reachable, and throwing it away was the §120
                    // bug.</b> Asked for from the chair: *if I click a random spot in the fog I want a
                    // villager to walk up to at least the last reachable point — I won't expect them to
                    // magically know they can't reach something nobody can see.* That answer was being
                    // computed and binned. A truncated search hands back a route to the furthest cell it
                    // reached and then this line discarded the whole thing on one bad step, so the body was
                    // left with no destination, no progress, and asked again next tick: thirty routes found
                    // and dropped in one measured order, 2.7 seconds of searching for nothing, and from the
                    // chair a settlement that hitched and did not move.
                    //
                    // The reasoning above survives intact, because it is about the FIRST segment: with
                    // nothing accepted there is no valid prefix and no route, and that is still the honest
                    // answer. A prefix is not manufactured, it is the part that passed.
                    return result.Count > 0 ? result.ToArray() : Array.Empty<Vector2>();
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
    /// Cells every search issued for ONE order may close between them.
    /// </summary>
    /// <remarks>
    /// <b>Per order, because a budget per search cannot bound the cost of an order that issues twenty.</b>
    /// §102: each body's search stopped politely at its own 250,000 and the tick still took eighteen seconds,
    /// because twenty of them ran. This is the pooled allowance — once it is spent, the remaining bodies get no
    /// search at all and fall back to the cohort's field or to standing where they are, which is the same
    /// best-effort answer the goal resolution gives and costs nothing.
    /// <para>
    /// Reset when a command batch begins, not per tick: the thing being bounded is a player's click.
    /// </para>
    /// </remarks>
    private const int OrderExpansionBudget = 300_000;

    private long orderExpansionsSpent;

    /// <summary>
    /// Opens an allowance for one order, and closes it again when the order has been applied.
    /// </summary>
    /// <remarks>
    /// <b>Scoped to the order, and the first version was not.</b> It opened per command and never closed, so
    /// once one order spent the pool every later search in the game was refused — job walks, congestion
    /// repaths, stuck recovery, all of it. Bodies lost their destinations, the jobs layer reclaimed them two
    /// seconds later, and they walked back to work hundreds of metres from where they had been sent. Reported
    /// from the chair as "some of the group just stalls midway never catching the lead", and the probe found
    /// four hundred and sixty-one denied searches in an order that issues twenty.
    /// <para>
    /// What the freeze actually was is twenty searches in ONE tick, so that is what the bound covers. Ordinary
    /// movement repaths are one body at a time behind cooldowns and were never the problem; they are charged
    /// nothing.
    /// </para>
    /// </remarks>
    public void BeginOrderBudget()
    {
        orderExpansionsSpent = 0;
        orderBudgetActive = true;
    }

    public void EndOrderBudget() => orderBudgetActive = false;

    private bool orderBudgetActive;

    /// <summary>Whether this order still has search left to spend. Unbounded outside an order.</summary>
    private bool OrderBudgetRemains => !orderBudgetActive || orderExpansionsSpent < OrderExpansionBudget;

    /// <summary>Searches refused outright because the order's pooled allowance was gone.</summary>
    public long SearchesDeniedByOrderBudget { get; private set; }

    /// <summary>Cells one search may close before it gives up and hands back what it has.</summary>
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
    private const int DefaultExpansionBudget = 250_000;

    /// <summary>
    /// Cells one search may close, overridable so that a truncated search can be provoked cheaply.
    /// </summary>
    /// <remarks>
    /// <b>A const until §120, and that is why the partial-route path had never been exercised.</b> Reaching it
    /// on the default value needs a body eight hundred cells from its goal on a real map — which is exactly
    /// the situation a player found and no fixture could produce. A settable ceiling makes the same code path
    /// reachable in a thirty-metre world.
    /// <para>
    /// Not a tuning knob: nothing in the game sets it, and the fixture that does says so in its output.
    /// </para></remarks>
    internal static int ExpansionBudgetOverride = DefaultExpansionBudget;

    private static int ExpansionBudget => ExpansionBudgetOverride;

    private static float Heuristic(GridCell from, GridCell to)
    {
        var dx = Math.Abs(from.X - to.X);
        var dz = Math.Abs(from.Z - to.Z);
        return Math.Max(dx, dz) + (DiagonalCost - 1f) * Math.Min(dx, dz);
    }
}

using System.Diagnostics;
using System.Numerics;
using RTSGame.Simulation;
using RTSGame.Simulation.Agents;
using RTSGame.Simulation.Terrain;

namespace RTSGame.Debug;

/// <summary>
/// What relief costs the router, measured before anything is built on top of it.
/// </summary>
/// <remarks>
/// <b>This is the gate on the whole terrain layer, and it is not "does it look like hills".</b> §54 names
/// the risk: the routing hierarchy's 571 ms a tick became 5.8 ms because the partition is
/// <em>rectangles of uniform ground</em>, and two structures decide what "uniform" means. The rectangle
/// mesh merges cells whose traversal cost is equal and whose heights agree to within five centimetres. The
/// navigation grid keeps five numbers for a 64-cell region whose cells agree <em>exactly</em> and a full
/// 4,096-entry chunk otherwise.
/// <para>
/// Continuous relief threatens both. Five centimetres is exactly what a tenth grade climbs across one
/// half-metre cell, so on that reading a hillside is one rectangle per cell and the 571 ms comes straight
/// back; and no region of 64 cells on sloping ground has two cells at the same height, so every region
/// chunks.
/// </para>
/// <para>
/// So this sweeps the amplitude and prints what actually happens to both, plus what it does to route
/// quality and to the tick. Three things have to hold for the layer to be safe to build on: <b>the
/// rectangle count stays sane, the tick time holds, and amplitude zero is bit-for-bit today's ground.</b>
/// The first two are what the numbers below are for; the third is checked outright.
/// </para>
/// </remarks>
internal static class ReliefScenarios
{
    private const int TicksPerSecond = 30;

    /// <summary>
    /// Grade buckets, for describing a map rather than for pricing one.
    /// </summary>
    /// <remarks>
    /// This reporter's own bucketing and nothing else's — the simulation charges slope per edge as
    /// <c>|height change| x ClimbSecondsPerMetre</c>, continuously, and never asks which band a cell is in.
    /// The buckets are here so a map can be described in a line: how much of it is level, how much is
    /// walking uphill, and how much is a scramble.
    /// </remarks>
    private static readonly float[] GradeBands = { 0.06f, 0.15f, 0.27f, 0.42f, float.MaxValue };

    private static int BandOf(float grade)
    {
        for (var band = 0; band < GradeBands.Length; band++)
        {
            if (grade <= GradeBands[band]) return band;
        }

        return GradeBands.Length - 1;
    }

    public static int Run(float extentMeters, float[] amplitudes, uint seed)
    {
        Console.WriteLine(
            $"RTSGame relief — {extentMeters:F0} m, seed {seed}, " +
            $"landform radius {ReliefPlan.DefaultRadiusMetres:F0} m, " +
            $"traversable to a grade of {TerrainMap.MaximumTraversableGrade:F2}");
        Console.WriteLine(
            "  amp (m) | shapes | steepest | closed | chunked | nav MB | raster ms | rects | " +
            "crossings | bands % | route mean | p99 | worst | ms/tick");

        var faults = new List<string>();
        var baseline = new Row();
        foreach (var amplitude in amplitudes)
        {
            var row = Measure(extentMeters, amplitude, seed, faults);
            if (amplitude <= 0.001f) baseline = row;
            Console.WriteLine(
                $"  {amplitude,7:F1} | {row.Shapes,6} | {row.Steepest,8:F2} | " +
                $"{row.ClosedShare * 100f,5:F1}% | {row.Chunked,7} | {row.NavigationBytes / 1048576f,6:F1} | " +
                $"{row.RasterMilliseconds,9:F0} | {row.Rectangles,5:N0} | {row.Crossings,9:N0} | " +
                $"{row.Bands,-18} | {row.MeanRatio,10:F3} | {row.NinetyNinthRatio,5:F2} | " +
                $"{row.WorstRatio,5:F2} | {row.MillisecondsPerTick,7:F2}");
        }

        // <b>Amplitude zero has to be today's ground, and "has to" is the migration plan.</b> Every
        // scenario in the suite is calibrated against flat, so if zero is not exactly flat then the whole
        // suite moves the day this lands and none of §22's or §50's numbers mean anything any more.
        var flat = new SimulationWorld(extentMeters);
        ReliefPlan.For(extentMeters, seed, 0f).Apply(flat.Terrain);
        var untouched = new SimulationWorld(extentMeters);
        var moved = 0;
        var transform = flat.Terrain.Transform;
        for (var z = 0; z <= transform.Height; z++)
        for (var x = 0; x <= transform.Width; x++)
        {
            if (flat.Terrain.VertexHeight(x, z) != untouched.Terrain.VertexHeight(x, z)) moved++;
        }

        Console.WriteLine(
            $"  at amplitude zero: {moved} of {(transform.Width + 1) * (transform.Height + 1):N0} " +
            $"vertices differ from a world nobody generated, rectangles {baseline.Rectangles:N0}");
        if (moved > 0)
        {
            faults.Add(
                $"amplitude zero moved {moved} vertices — it has to be exactly today's ground or every " +
                "scenario in the suite is recalibrated the day this lands");
        }

        foreach (var fault in faults) Console.WriteLine($"  FAULT: {fault}");
        return faults.Count > 0 ? 1 : 0;
    }

    private readonly record struct Row
    {
        public int Shapes { get; init; }
        public float Steepest { get; init; }
        public float ClosedShare { get; init; }
        public int Chunked { get; init; }
        public long NavigationBytes { get; init; }
        public double RasterMilliseconds { get; init; }
        public int Rectangles { get; init; }
        public int Crossings { get; init; }
        public int Walkers { get; init; }
        public string Bands { get; init; }
        public float MeanRatio { get; init; }
        public float NinetyNinthRatio { get; init; }
        public float WorstRatio { get; init; }
        public double MillisecondsPerTick { get; init; }
    }

    private static Row Measure(float extentMeters, float amplitude, uint seed, List<string> faults)
    {
        // <b>The bare map, not the village.</b> What is being measured is what relief alone does to the
        // partition, and eleven thousand trees are the loudest thing on this map — a forest already
        // fragments the mesh, so laying one over the top would hide the signal this exists to find. The
        // village goes on top once the substrate is known to be affordable.
        var world = new SimulationWorld(extentMeters);
        var plan = ReliefPlan.For(extentMeters, seed, amplitude);
        plan.Apply(world.Terrain);

        var clock = Stopwatch.StartNew();
        world.RebuildTerrainNavigation();
        var raster = clock.Elapsed.TotalMilliseconds;

        // How much ground the grades closed. This is the number that says whether an amplitude is a
        // landscape or a wall: relief that shuts a fifth of the map has stopped being terrain and become
        // an obstacle course.
        var closed = 0;
        var cells = 0;
        var grid = world.Navigation.Transform;
        for (var z = 0; z < grid.Height; z += 4)
        for (var x = 0; x < grid.Width; x += 4)
        {
            cells++;
            var at = grid.Origin + new Vector2(x + 0.5f, z + 0.5f) * grid.CellSize;
            if (!world.Terrain.IsBodyTraversable(at, AgentDefaults.Radius)) closed++;
        }

        // <b>What share of the map each slope band holds.</b> Without it the sweep cannot tell "the
        // partition survived because the banding worked" from "the partition survived because the whole map
        // landed in one band and slope is costing nothing at all" — which are the same number and opposite
        // conclusions.
        var bands = new int[GradeBands.Length];
        for (var z = 0; z < grid.Height; z += 2)
        for (var x = 0; x < grid.Width; x += 2)
        {
            var at = grid.Origin + new Vector2(x + 0.5f, z + 0.5f) * grid.CellSize;
            bands[BandOf(world.Terrain.SampleGrade(at))]++;
        }

        var sampled = bands.Sum();
        var mesh = world.RouteMesh(AgentDefaults.Radius);
        var fidelity = world.MeasureRectangleFidelity(Vector2.Zero, AgentDefaults.Radius);

        // A tick with bodies actually routing over it, because a partition that is expensive to search
        // costs nothing until something searches it.
        var walkers = new List<AgentId>();
        for (var i = 0; i < 200 && walkers.Count < 200; i++)
        {
            var angle = i / 200f * MathF.Tau;
            var at = new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * (extentMeters * 0.30f);
            if (!world.Terrain.IsBodyTraversable(at, AgentDefaults.Radius)) continue;
            walkers.Add(world.SpawnAgent(at));
        }

        world.QueueMove(walkers.ToArray(), Vector2.Zero);
        clock.Restart();
        var ticks = 90;
        for (var tick = 0; tick < ticks; tick++) world.Tick((float)SimulationWorld.FixedDeltaSeconds);
        var perTick = clock.Elapsed.TotalMilliseconds / ticks;

        if (amplitude > 0.001f && plan.Landforms.Count == 0)
        {
            faults.Add($"amplitude {amplitude:F1} produced no landforms at all");
        }

        return new Row
        {
            Shapes = plan.Landforms.Count,
            Steepest = plan.SteepestGrade,
            ClosedShare = cells == 0 ? 0f : closed / (float)cells,
            Chunked = world.ChunkedRegions,
            NavigationBytes = world.NavigationBytes,
            RasterMilliseconds = raster,
            Rectangles = mesh.Rectangles,
            Crossings = mesh.Crossings,
            Walkers = walkers.Count,
            Bands = string.Join(
                "/",
                bands.Select(count => $"{(sampled == 0 ? 0f : count * 100f / sampled):F0}")),
            MeanRatio = fidelity.MeanRatio,
            NinetyNinthRatio = fidelity.NinetyNinthRatio,
            WorstRatio = fidelity.WorstRatio,
            MillisecondsPerTick = perTick,
        };
    }
}

using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using RTSGame.Simulation;
using RTSGame.Simulation.Agents;
using RTSGame.Simulation.Navigation;
using RTSGame.Simulation.Spatial;

namespace RTSGame.Debug;

/// <summary>
/// What portal routing costs in route quality, on a map big enough to have regions at all.
/// </summary>
/// <remarks>
/// The tuned 30 m world is a single region, so every existing test passes through the
/// hierarchy without exercising one line of it. Nothing here can be inferred from those
/// numbers, and nothing about the hierarchy's approximation can be judged without a flat
/// search to compare against — which is the only reason <c>BuildReferenceFlowField</c>
/// still exists.
/// <para>
/// Two questions, both answered as ratios against that flat search. How much dearer does
/// the hierarchy think each cell is, and how many cells does it lose entirely. A lost cell
/// is the serious failure: a body standing on one believes it has no route and stops.
/// </para>
/// </remarks>
internal static class RegionRoutingScenarios
{
    /// <summary>Side of the test map, in metres. Six regions across, with room to detour.</summary>
    private const float ExtentMeters = 200f;

    public static int Run(float[]? spans = null)
    {
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
        Console.WriteLine("RTSGame routing fidelity");
        Console.WriteLine(
            $"  {ExtentMeters:F0} m map, walls with gaps, goal in the far corner. " +
            "Ratio is the router's cost over the flat whole-map search it replaced, so 1.000 is");
        Console.WriteLine("  the exact answer and anything below it is a route that does not exist.");
        Console.WriteLine();

        var world = Build();
        var watch = Stopwatch.StartNew();
        var fidelity = world.MeasureRectangleFidelity(
            new Vector2(ExtentMeters * 0.42f, ExtentMeters * 0.42f),
            AgentDefaults.Radius);
        Console.WriteLine(
            $"  clear ground | mean {fidelity.MeanRatio:F4} | p99 {fidelity.NinetyNinthRatio:F4} | " +
            $"worst {fidelity.WorstRatio:F3} at {fidelity.WorstCell.X},{fidelity.WorstCell.Z} | " +
            $"lost {fidelity.UnreachableCells}/{fidelity.ReachableCells}");
        Console.WriteLine(
            $"               | {fidelity.RefinedRegions:N0} rectangles, " +
            $"{fidelity.SettledNodes:N0} corners settled, no cell-level search | " +
            $"measured in {watch.ElapsedMilliseconds} ms");

        // Does a jam reach the field at all? The flat reference charges congestion too, so a
        // router that ignored it would drift below one here — a route the reference thinks is
        // dear and this one thinks is cheap. Immovable bodies are the quickest way to make
        // pressure without waiting for a crowd to develop one.
        var jammed = Build();
        var wall = new List<AgentId>();
        for (var i = 0; i < 40; i++)
        {
            wall.Add(jammed.SpawnAgent(
                new Vector2(ExtentMeters * 0.10f, -ExtentMeters * 0.20f + i * 0.55f),
                maximumSpeed: 0f));
        }

        for (var tick = 0; tick < 40; tick++)
        {
            jammed.Tick((float)SimulationWorld.FixedDeltaSeconds);
        }

        var jammedFidelity = jammed.MeasureRectangleFidelity(
            new Vector2(ExtentMeters * 0.42f, ExtentMeters * 0.42f),
            AgentDefaults.Radius);
        Console.WriteLine(
            $"  with a jam   | mean {jammedFidelity.MeanRatio:F4} | " +
            $"p99 {jammedFidelity.NinetyNinthRatio:F4} | worst {jammedFidelity.WorstRatio:F3} | " +
            $"lost {jammedFidelity.UnreachableCells}/{jammedFidelity.ReachableCells} | " +
            $"live cells {jammed.Congestion.LiveCellCount:N0}");
        Console.WriteLine();
        Console.WriteLine(
            "  A jam must not push the mean below one: the reference charges congestion too, so");
        Console.WriteLine(
            "  drifting under would mean this router cannot see what the reference can.");
        return 0;
    }

    /// <summary>
    /// A map with structure rather than an empty field: three staggered walls, each with
    /// one gap, so a route has to commit to a crossing and the regions disagree about
    /// which one. An open map would flatter the hierarchy — every portal equivalent, every
    /// ratio one — and prove nothing.
    /// </summary>
    internal static SimulationWorld Build()
    {
        var world = new SimulationWorld(ExtentMeters);
        var placement = world.Placement.Transform;
        var width = placement.Width;
        var height = placement.Height;

        for (var wall = 1; wall <= 3; wall++)
        {
            var x = width * wall / 4;
            // Gaps alternate top and bottom, so the shortest route zig-zags across several
            // region borders instead of running straight down one corridor.
            var gap = wall % 2 == 1 ? height / 6 : height * 5 / 6;
            for (var z = 0; z < height; z++)
            {
                if (Math.Abs(z - gap) <= 1) continue;
                world.QueueToggleObstacle(placement.CellCenter(new GridCell(x, z)));
            }
        }

        world.Tick((float)SimulationWorld.FixedDeltaSeconds);
        return world;
    }
}

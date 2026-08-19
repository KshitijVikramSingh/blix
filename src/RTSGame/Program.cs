using Blix.Runtime.Silk;
using RTSGame.Debug;

namespace RTSGame;

public static class Program
{
    public static void Main(string[] args)
    {
        if (args.Contains("--selftest"))
        {
            Environment.Exit(SimulationSelfTests.Run());
        }

        if (args.Contains("--benchmark"))
        {
            Environment.Exit(MovementBenchmarks.Run());
        }

        if (args.Contains("--scale"))
        {
            var extents = ParseFloats(args, "--extents");
            var counts = ParseInts(args, "--agents");
            Environment.Exit(ScaleScenarios.Run(extents, counts));
        }

        if (args.Contains("--worldgen"))
        {
            var genExtent = Value(args, "--extent") is { } size ? float.Parse(size) : 600f;
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var world = new Simulation.SimulationWorld(genExtent);
            Console.WriteLine($"  construct {watch.ElapsedMilliseconds} ms");
            watch.Restart();
            WorldTerrainScenarios.Populate(world, issueGroupMove: false);
            Console.WriteLine($"  populate  {watch.ElapsedMilliseconds} ms");
            watch.Restart();
            world.Tick((float)Simulation.SimulationWorld.FixedDeltaSeconds);
            Console.WriteLine($"  first tick {watch.ElapsedMilliseconds} ms");
            Environment.Exit(0);
        }

        if (args.Contains("--mixedtest"))
        {
            Environment.Exit(MixedBodyScenarios.Run());
        }

        if (args.Contains("--radiisweep"))
        {
            var sweepExtent = Value(args, "--extent") is { } size ? float.Parse(size) : 600f;
            Environment.Exit(BodyRadiusSweep.Run(ParseFloats(args, "--radii"), sweepExtent));
        }

        if (args.Contains("--rectangles"))
        {
            var rectExtent = Value(args, "--extent") is { } size ? float.Parse(size) : 600f;
            var rectWorld = new Simulation.SimulationWorld(rectExtent);
            if (args.Contains("--terrain")) WorldTerrainScenarios.Populate(rectWorld, issueGroupMove: false);
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var decomposition = rectWorld.DecomposeWalkable(Simulation.Agents.AgentDefaults.Radius);
            var elapsed = watch.Elapsed.TotalMilliseconds;
            var cells = rectWorld.Navigation.Width * rectWorld.Navigation.Height;
            var largest = 0;
            long areaSum = 0;
            foreach (var rectangle in decomposition.All)
            {
                largest = Math.Max(largest, rectangle.Area);
                areaSum += rectangle.Area;
            }

            Console.WriteLine(
                $"  {rectWorld.ExtentMeters:F0} m | {(args.Contains("--terrain") ? "ridge, lake and road" : "empty")} | " +
                $"{cells:N0} cells");
            Console.WriteLine(
                $"  rectangles {decomposition.Count:N0} | covering {decomposition.CoveredCells:N0} walkable cells | " +
                $"mean {(decomposition.Count == 0 ? 0 : areaSum / decomposition.Count):N0} cells | " +
                $"largest {largest:N0} | built in {elapsed:F0} ms");
            Console.WriteLine(
                $"  against the fixed partition: {rectWorld.RegionCount:N0} regions of " +
                $"{Simulation.Navigation.RegionPartition.CellsPerRegion:N0} cells");

            var degrees = new int[decomposition.Count];
            var widest = 0;
            for (var r = 0; r < decomposition.Count; r++) degrees[r] = decomposition.CrossingsOf(r).Length;
            foreach (var degree in degrees) widest = Math.Max(widest, degree);
            Array.Sort(degrees);
            var median = degrees.Length == 0 ? 0 : degrees[degrees.Length / 2];
            var ninetyNinth = degrees.Length == 0 ? 0 : degrees[(int)(degrees.Length * 0.99f)];
            Console.WriteLine(
                $"  crossings {decomposition.Crossings.Count:N0} | per rectangle: median {median}, " +
                $"p99 {ninetyNinth}, worst {widest}");
            Environment.Exit(0);
        }

        if (args.Contains("--mapdump"))
        {
            var dumpExtent = Value(args, "--extent") is { } size ? float.Parse(size) : 600f;
            var dump = new Simulation.SimulationWorld(dumpExtent);
            WorldTerrainScenarios.Populate(dump, issueGroupMove: false);
            Console.WriteLine($"  {dump.ExtentMeters:F0} m map, '.'=grass '='=road ':'=rough '~'=mud '#'=impassable");
            Console.WriteLine("  'X' = a standard body cannot stand there");
            const int rows = 46;
            for (var row = 0; row < rows; row++)
            {
                var line = new System.Text.StringBuilder("  ");
                for (var column = 0; column < rows * 2; column++)
                {
                    var world = new System.Numerics.Vector2(
                        (column / (float)(rows * 2 - 1) - 0.5f) * dump.ExtentMeters * 0.99f,
                        (row / (float)(rows - 1) - 0.5f) * dump.ExtentMeters * 0.99f);
                    var glyph = dump.Terrain.SampleSurface(world) switch
                    {
                        Simulation.Terrain.TerrainSurface.Road => '=',
                        Simulation.Terrain.TerrainSurface.Rough => ':',
                        Simulation.Terrain.TerrainSurface.Mud => '~',
                        Simulation.Terrain.TerrainSurface.Impassable => '#',
                        _ => '.',
                    };
                    if (dump.Navigation.TryWorldToCell(world, out var navCell) &&
                        !dump.Navigation.IsWalkable(navCell, Simulation.Agents.AgentDefaults.Radius))
                    {
                        glyph = 'X';
                    }

                    line.Append(glyph);
                }

                Console.WriteLine(line.ToString());
            }

            Environment.Exit(0);
        }

        if (args.Contains("--ordertest"))
        {
            var orderExtent = Value(args, "--extent") is { } size ? float.Parse(size) : 1200f;
            var orderAgents = Value(args, "--agents") is { } count ? int.Parse(count) : 30;
            Environment.Exit(ScaleScenarios.RunOrderDistance(
                orderExtent,
                orderAgents,
                args.Contains("--terrain")));
        }

        if (args.Contains("--routingtest"))
        {
            Environment.Exit(RegionRoutingScenarios.Run(ParseFloats(args, "--spans")));
        }

        if (args.Contains("--arrivaltest"))
        {
            Environment.Exit(SimulationSelfTests.RunSharedDestinationRegression());
        }

        if (args.Contains("--cornerlooptest"))
        {
            Environment.Exit(SimulationSelfTests.RunCornerCircuitDiagnostic());
        }

        if (args.Contains("--cornerlegtest"))
        {
            Environment.Exit(SimulationSelfTests.RunCornerCircuitDiagnostic(maximumLegs: 1));
        }

        if (args.Contains("--cornertwolegtest"))
        {
            Environment.Exit(SimulationSelfTests.RunCornerCircuitDiagnostic(maximumLegs: 2));
        }

        if (args.Contains("--cornerthreelegtest"))
        {
            Environment.Exit(SimulationSelfTests.RunCornerCircuitDiagnostic(maximumLegs: 3));
        }

        if (args.Contains("--terraincornertest"))
        {
            Environment.Exit(SimulationSelfTests.RunTerrainCornerDiagnostic());
        }

        if (args.Contains("--doorwaytest"))
        {
            Environment.Exit(SimulationSelfTests.RunDoorwayContentionDiagnostic());
        }

        if (args.Contains("--gatetest"))
        {
            Environment.Exit(SimulationSelfTests.RunSingleCellGateDiagnostic());
        }

        if (args.Contains("--yieldtest"))
        {
            Environment.Exit(SimulationSelfTests.RunIdleYieldDiagnostic());
        }

        var exitAfterFrames = 0;
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == "--frames" && int.TryParse(args[i + 1], out var count))
            {
                exitAfterFrames = count;
            }
        }

        var traceMovement = args.Contains("--trace-movement");
        var startTerrainLab = args.Contains("--terrain-lab");
        var debugAll = args.Contains("--debug-all");
        var extent = Value(args, "--extent") is { } raw
            ? float.Parse(raw)
            : RtsGameLoop.DefaultWorldExtentMeters;
        var compression = Value(args, "--compression") is { } rate
            ? float.Parse(rate)
            : RtsGameLoop.DefaultCompression;
        var game = new RtsGameLoop(
            exitAfterFrames,
            traceMovement,
            startTerrainLab,
            debugAll,
            extent,
            compression);
        using var window = new Window(game, new WindowOptions("RTSGame — Greybox Kingdom", 1280, 720));
        window.Run();
    }

    /// <summary>Reads a comma-separated list following <paramref name="flag"/>.</summary>
    private static float[]? ParseFloats(string[] args, string flag)
    {
        var raw = Value(args, flag);
        return raw is null ? null : raw.Split(',').Select(float.Parse).ToArray();
    }

    private static int[]? ParseInts(string[] args, string flag)
    {
        var raw = Value(args, flag);
        return raw is null ? null : raw.Split(',').Select(int.Parse).ToArray();
    }

    private static string? Value(string[] args, string flag)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == flag) return args[i + 1];
        }

        return null;
    }
}

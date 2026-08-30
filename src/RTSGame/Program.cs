using Blix.Runtime.Silk;
using RTSGame.Debug;

namespace RTSGame;

public static class Program
{
    public static void Main(string[] args)
    {
        // <b>Every number this program prints is a measurement, so it is printed the same everywhere.</b>
        // Without this the machine's own digit grouping gets in: the forest line read "8,65,710 wood" on
        // this one, which is correct for the local convention and useless in a report you compare against
        // yesterday's. It also means two machines' traces diff cleanly, which is the whole point of having
        // a determinism check.
        System.Globalization.CultureInfo.DefaultThreadCurrentCulture =
            System.Globalization.CultureInfo.InvariantCulture;

        if (args.Contains("--selftest"))
        {
            Environment.Exit(SimulationSelfTests.Run());
        }

        if (args.Contains("--fightbench"))
        {
            Environment.Exit(FightBenchmarks.Run());
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

        if (args.Contains("--congestiontest"))
        {
            Environment.Exit(CongestionSpeedScenarios.Run());
        }

        if (args.Contains("--settlement"))
        {
            var settlementExtent = Value(args, "--extent") is { } size ? float.Parse(size) : 600f;
            var years = Value(args, "--years") is { } span ? float.Parse(span) : 1f;
            var settlementRelief = Value(args, "--relief-amplitude") is { } sa ? float.Parse(sa) : 0f;
            var settlementRegion = Value(args, "--region") is { } sr
                ? Enum.Parse<Simulation.Terrain.Region>(sr, ignoreCase: true)
                : Simulation.Terrain.Region.Downland;
            var settlementArchetype = Value(args, "--archetype") is { } sk
                ? Enum.Parse<Simulation.Terrain.Archetype>(sk, ignoreCase: true)
                : Simulation.Terrain.Archetype.SplitValley;
            var settlementSeed = Value(args, "--mapseed") is { } sm ? uint.Parse(sm) : 0x5EED1234u;
            Environment.Exit(SettlementScenarios.Run(
                settlementExtent,
                years,
                settlementRelief,
                settlementRegion,
                settlementArchetype,
                settlementSeed));
        }

        if (args.Contains("--placementcheck"))
        {
            var pcExtent = Value(args, "--extent") is { } size ? float.Parse(size) : 600f;
            var pcMinutes = Value(args, "--minutes") is { } span ? float.Parse(span) : 3f;
            Environment.Exit(SettlementScenarios.RunPlacementCheck(pcExtent, pcMinutes));
        }

        if (args.Contains("--forestcost"))
        {
            var forestExtent = Value(args, "--extent") is { } size ? float.Parse(size) : 600f;
            Environment.Exit(SettlementScenarios.RunForestCost(forestExtent));
        }

        if (args.Contains("--raidtest"))
        {
            var raidExtent = Value(args, "--extent") is { } size ? float.Parse(size) : 600f;
            var raidMinutes = Value(args, "--minutes") is { } span ? float.Parse(span) : 8f;
            var between = Value(args, "--every") is { } gap ? float.Parse(gap) : 60f;
            var health = Value(args, "--health") is { } hp ? float.Parse(hp) : 0f;
            // <b>One run cannot answer a balance question.</b> A raid is chaotic — change a number and a
            // different body dies first, which reroutes everything after it — so raider health 15, 16 and
            // 18 came out non-monotonic on one sample each. Varying the seed is how a sweep gets samples,
            // and the seed is the only thing that varies: same map, same settlement, same schedule.
            var seed = Value(args, "--seed") is { } s ? uint.Parse(s) : 0x1B873593u;
            Environment.Exit(SettlementScenarios.RunRaids(
                raidExtent, raidMinutes, between, health, seed, args.Contains("--peers")));
        }

        // <b>Every map the panel can ask for, because the panel can ask for more than the gate ever tried.</b>
        if (args.Contains("--mapsweep"))
        {
            var sweepExtent = Value(args, "--extent") is { } size ? float.Parse(size) : 600f;
            var sweepSeed = Value(args, "--mapseed") is { } s ? uint.Parse(s) : 0x5EED1234u;
            Environment.Exit(Debug.MapSweep.Run(sweepExtent, sweepSeed));
        }

        if (args.Contains("--shapes"))
        {
            var shapeExtent = Value(args, "--extent") is { } size ? float.Parse(size) : 600f;
            var shapeAmplitude = Value(args, "--relief-amplitude") is { } amp ? float.Parse(amp) : 28f;
            var shapeSeed = Value(args, "--mapseed") is { } s ? uint.Parse(s) : 0x5EED1234u;
            var shapeRows = Value(args, "--rows") is { } r ? int.Parse(r) : 26;
            Debug.ShapeScenarios.Run(shapeExtent, shapeAmplitude, shapeSeed, shapeRows);
            Environment.Exit(0);
        }

        if (args.Contains("--relief"))
        {
            var reliefExtent = Value(args, "--extent") is { } size ? float.Parse(size) : 600f;
            var reliefSeed = Value(args, "--seed") is { } s ? uint.Parse(s) : 0x5EED1234u;
            var amplitudes = ParseFloats(args, "--amplitudes");
            Environment.Exit(ReliefScenarios.Run(
                reliefExtent,
                amplitudes is { Length: > 0 } given ? given : new[] { 0f, 3f, 6f, 12f, 24f },
                reliefSeed));
        }

        if (args.Contains("--skyprofile"))
        {
            var bearing = Value(args, "--bearing") is { } b ? float.Parse(b) : 37f;
            var seasonality = Value(args, "--seasonality") is { } q ? float.Parse(q) : 1f;
            Environment.Exit(SkyProfile.Run(bearing, seasonality));
        }

        if (args.Contains("--catchment"))
        {
            var catchExtent = Value(args, "--extent") is { } size ? float.Parse(size) : 600f;
            var budget = Value(args, "--budget") is { } seconds ? float.Parse(seconds) : 60f;
            Environment.Exit(CatchmentScenarios.Run(catchExtent, budget));
        }

        if (args.Contains("--jobs"))
        {
            var jobsExtent = Value(args, "--extent") is { } size ? float.Parse(size) : 600f;
            var jobsMinutes = Value(args, "--minutes") is { } span ? int.Parse(span) : 6;
            Environment.Exit(JobScenarios.Run(jobsExtent, jobsMinutes));
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
        var startVillage = args.Contains("--village");
        // <b>The map lab: a large canvas, terrain only, and a window to frame a map out of.</b> Larger than
        // the game's own extent on purpose — a 600 m window cut from a coherent 1800 m landscape is a
        // fragment of somewhere, with a river that comes from off-window and a ridge that carries on past
        // the edge. See the remarks on RtsGameLoop.mapLab.
        var startMapLab = args.Contains("--maplab");
        // The other half of the lab's workflow: a printed pick has to be typeable back in, or it is a note
        // rather than a record.
        var labRegion = Value(args, "--region") is { } regionName
            ? Enum.Parse<Simulation.Terrain.Region>(regionName, ignoreCase: true)
            : (Simulation.Terrain.Region?)null;
        var labArchetype = Value(args, "--archetype") is { } archetypeName
            ? Enum.Parse<Simulation.Terrain.Archetype>(archetypeName, ignoreCase: true)
            : (Simulation.Terrain.Archetype?)null;
        var labSeed = Value(args, "--mapseed") is { } ms ? uint.Parse(ms) : (uint?)null;
        var debugAll = args.Contains("--debug-all");
        // A frame run used to keep accepting the real mouse and keyboard. That made a recorded wide-view
        // sample depend on where the pointer happened to be and on whether somebody touched the wheel while
        // it ran. --perf-run is the sealed version: timings are on, the panel is not rendered, and the game
        // ignores live input until the requested frame count closes it.
        var performanceRun = args.Contains("--perf-run");
        // <b>Fog off has to be sayable, because a village turns it on by itself.</b> --fog only ever added
        // fog to a run that would not have had it, so on a village — the only map the perf matrix measures —
        // the fog ablation the camera envelope needs was not reachable from the command line at all.
        var fogDisabled = args.Contains("--nofog");
        var performanceCamera = Value(args, "--perf-camera") is { } cameraMotion
            ? Enum.Parse<PerformanceCameraMotion>(cameraMotion, ignoreCase: true)
            : PerformanceCameraMotion.Still;
        var performanceHour = Value(args, "--perf-hour") is { } hour ? float.Parse(hour) : -1f;
        // The opt-in half of the vsync question: keep the display's cadence and measure pacing as a player
        // feels it, rather than what the frame cost. See RtsGameLoop.performanceVsync.
        var performanceVsync = args.Contains("--perf-vsync");
        if (performanceHour is < -1f or > 24f)
        {
            throw new ArgumentOutOfRangeException("--perf-hour", performanceHour, "Hour must be between 0 and 24.");
        }
        if (performanceRun)
        {
            Console.WriteLine(
                $"  performance fixture: camera {performanceCamera.ToString().ToLowerInvariant()}, " +
                $"light {(performanceHour >= 0f ? $"{performanceHour:F1}:00" : "live")}, " +
                $"vsync {(performanceVsync ? "on" : "off")}");
        }
        // <b>Timings without the overlays.</b> --debug-all turns on the collider overlay too, which draws a
        // disc per body and per tree — 17 ms of it with five thousand trees in view — so a frame measured
        // that way is measuring the instrument. This asks for the numbers and nothing else.
        var timingsOnly = args.Contains("--timings") || performanceRun;
        var extent = Value(args, "--extent") is { } raw
            ? float.Parse(raw)
            : RtsGameLoop.DefaultWorldExtentMeters;
        var compression = Value(args, "--compression") is { } rate
            ? float.Parse(rate)
            : RtsGameLoop.DefaultCompression;
        var relief = Value(args, "--relief-amplitude") is { } metres ? float.Parse(metres) : 0f;
        // <b>A starting zoom, so a frame can be measured at the standoff somebody is complaining about.</b>
        // Everything the detail radius scales — how much ground is meshed, how many trees are drawn, how far
        // the cover reaches — is a function of the camera's distance, so "it is slow zoomed out" is not
        // reproducible from a run that starts zoomed in.
        var zoom = Value(args, "--zoom") is { } standoff ? float.Parse(standoff) : 0f;
        // <b>A soak for the map roll, because Space could not be pressed from a gate.</b>
        var rollEvery = Value(args, "--roll-every") is { } cadence ? int.Parse(cadence) : 0;
        var game = new RtsGameLoop(
            exitAfterFrames,
            relief,
            zoom,
            traceMovement,
            startTerrainLab,
            debugAll,
            timingsOnly,
            extent,
            compression,
            startVillage,
            startMapLab,
            labRegion,
            labArchetype,
            labSeed,
            rollEvery,
            args.Contains("--treeprofile"),
            args.Contains("--fog"),
            args.Contains("--fogcells"),
            fogDisabled,
            performanceRun,
            performanceCamera,
            performanceHour,
            performanceVsync);
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

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

        // <b>Before every scenario branch, because each of them exits.</b> This sat two hundred lines lower
        // once, after `--selftest` had already run and called Environment.Exit — so the control arm and the
        // arm under test were the same arm, agreed perfectly, and nearly bought a conclusion that the pen
        // failure predated the change. A lever parsed after the branch it is meant to affect is not a lever.
        // <b>Off by default because §121 measured it and rejected it: 27x cheaper, up to 5x longer.</b> The
        // lever stays so the next attempt at steering a cell search by the hierarchy can be measured against
        // the same fixture rather than rebuilt from the plan.
        if (args.Contains("--guided-search"))
        {
            Simulation.Navigation.PathService.GuidedSearch = true;
            Simulation.Navigation.PathService.GuidedRestart = true;
            Console.WriteLine(
                "  cell searches steer by the goal's cost field (--guided-search) — REJECTED in §121, " +
                "routes ran to 5x the flat search's");
        }

        // A stopwatch lever for §126: it removes the corner-climb dictionary lookups from the field solve and
        // leaves the rest. Routes are wrong with it on, which is why it says so.
        if (args.Contains("--field-noclimb"))
        {
            Simulation.Navigation.PathService.ChargeFieldClimb = false;
            Console.WriteLine("  cost fields charge no climb (--field-noclimb) — ROUTES ARE WRONG, measurement only");
        }

        if (args.Contains("--field-climbkey"))
        {
            Simulation.Navigation.PathService.LookUpFieldClimb = false;
            Console.WriteLine("  climb keys are formed and not looked up (--field-climbkey) — measurement only");
        }

        if (args.Contains("--no-knowledge"))
        {
            Simulation.FactionKnowledge.Enabled = false;
            Console.WriteLine("  faction knowledge is not gathered (--no-knowledge) — measurement only");
        }

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

        if (args.Contains("--twovillages"))
        {
            var twoExtent = Value(args, "--extent") is { } size ? float.Parse(size) : 600f;
            var twoYears = Value(args, "--years") is { } span ? float.Parse(span) : 1f;
            var twoRelief = Value(args, "--relief-amplitude") is { } amp ? float.Parse(amp) : 32f;
            var twoSeed = Value(args, "--mapseed") is { } seed ? uint.Parse(seed) : 1592594996u;
            Environment.Exit(TwoSettlementScenarios.Run(
                twoExtent, twoYears, twoRelief, twoSeed,
                args.Contains("--swapfactions"), args.Contains("--onevillage"),
                args.Contains("--dresstwice")));
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

        // <b>What the clocks come to, before anybody argues about what they should come to.</b> §112 spent an
        // afternoon on numbers nobody could read off the running game, and one of them was invented. The
        // overrides price a proposal without editing a constant.
        if (args.Contains("--clocks"))
        {
            var clockCompression = Value(args, "--compression") is { } cc
                ? float.Parse(cc)
                : RtsGameLoop.DefaultCompression;
            var clockExtent = Value(args, "--extent") is { } ce ? float.Parse(ce) : 600f;
            var clockYear = ParseDuration(Value(args, "--year"));
            var clockDays = Value(args, "--days") is { } cd ? int.Parse(cd) : (int?)null;
            Environment.Exit(Debug.ClockReport.Run(clockCompression, clockExtent, clockYear, clockDays));
        }

        if (args.Contains("--orderprobe"))
        {
            var probeExtent = Value(args, "--extent") is { } size ? float.Parse(size) : 600f;
            var probeRelief = Value(args, "--relief-amplitude") is { } probeAmplitude
                ? float.Parse(probeAmplitude)
                : 32f;
            var probeSeconds = Value(args, "--seconds") is { } span ? float.Parse(span) : 40f;
            Environment.Exit(ScaleScenarios.RunOrderProbe(probeExtent, probeRelief, probeSeconds));
        }

        if (args.Contains("--fogclick"))
        {
            var clickExtent = Value(args, "--extent") is { } size ? float.Parse(size) : 600f;
            var clickRelief = Value(args, "--relief-amplitude") is { } amplitude
                ? float.Parse(amplitude)
                : 32f;
            var clickWarmup = Value(args, "--warmup") is { } warm ? int.Parse(warm) : 20;
            var clickWatch = Value(args, "--watch") is { } watch ? int.Parse(watch) : 10;
            var clickRegion = Value(args, "--region") is { } clickRegionName
                ? Enum.Parse<Simulation.Terrain.Region>(clickRegionName, ignoreCase: true)
                : Simulation.Terrain.Region.Downland;
            var clickArchetype = Value(args, "--archetype") is { } clickArchetypeName
                ? Enum.Parse<Simulation.Terrain.Archetype>(clickArchetypeName, ignoreCase: true)
                : Simulation.Terrain.Archetype.YValley;
            var clickSeed = Value(args, "--mapseed") is { } clickSeedText
                ? uint.Parse(clickSeedText)
                : 1592594996u;
            var clickBudget = Value(args, "--path-budget") is { } budgetText
                ? int.Parse(budgetText)
                : 0;
            Environment.Exit(FogClickScenarios.Run(
                clickExtent,
                clickRelief,
                clickWarmup,
                clickWatch,
                clickRegion,
                clickArchetype,
                clickSeed,
                clickBudget));
        }

        if (args.Contains("--pathprofile"))
        {
            var profileExtent = Value(args, "--extent") is { } size ? float.Parse(size) : 600f;
            var profileRelief = Value(args, "--relief-amplitude") is { } amplitude
                ? float.Parse(amplitude)
                : 32f;
            var profileOrders = Value(args, "--orders") is { } count ? int.Parse(count) : 4;
            Environment.Exit(ScaleScenarios.RunPathProfile(profileExtent, profileRelief, profileOrders));
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
        // <b>The display's period in MILLISECONDS, as a fact rather than an inference.</b> A vsync-on run
        // reports its cadence against this; without it the cadence is not reported at all, because a run whose
        // every frame misses cannot discover the deadline it is missing. perf-pacing.sh measures it on a near
        // view and passes it to every case in the matrix.
        //
        // Milliseconds only, and the first version of this accepted either: "above five is hertz, below is
        // milliseconds, since one is unambiguous". Sixteen point seven is above five. The script duly passed a
        // period of 14.18 ms, the fixture read it as 14 Hz, and every case reported a hundred per cent on time
        // against a 70 ms deadline. A unit inferred from a magnitude is a unit waiting to be wrong.
        if (Value(args, "--perf-refresh") is { } refreshText)
        {
            Debug.PerformanceRun.RefreshMilliseconds = double.Parse(refreshText);
            Console.WriteLine(
                $"  performance fixture: refresh {Debug.PerformanceRun.RefreshMilliseconds:F2} ms " +
                $"({1000.0 / Debug.PerformanceRun.RefreshMilliseconds:F1} Hz) as given");
        }
        // The caster ablation: how many cascades are allowed to receive casters at all. Default -1 leaves
        // every cascade alone. See RtsGameLoop.performanceCascadeMask for what it is bounding.
        var performanceCascades = Value(args, "--perf-cascades") is { } casc ? int.Parse(casc) : -1;
        // <b>Off by default: the eye rejected it.</b> §84 measured the coarse-cascade proxy as worth 7-10 ms
        // and left its one unverified claim as "nobody has looked yet". Somebody looked, at 118 m, and the
        // real geometry's tree shadows read plainly better — while the frame was comfortable either way. The
        // machinery stays because it is measured and may be wanted on weaker hardware or a larger map, but it
        // is a switch now and not a default. Both arms in one binary — see RtsGameLoop.shadowProxies.
        var shadowProxies = args.Contains("--shadow-proxy");
        // The old, queue-draining fog upload, kept so the fix has something to be measured against.
        var performanceBlockingUpload = args.Contains("--perf-blocking-upload");
        // <b>Pins the wheel's ceiling.</b> Unset, the camera may stand back as far as the map can be seen
        // from — the 118 m cap was a §73 judgement about a frame that has since changed twice, and §82 put
        // the limits explicitly back in play. Give this a number to hold a measured envelope, including the
        // old one: --zoom-limit 118.
        var zoomLimit = Value(args, "--zoom-limit") is { } limit ? float.Parse(limit) : 0f;
        // The geometry lever. See RtsGameLoop.performanceTierBias: coarser trees, same pixels.
        var tierBias = Value(args, "--perf-tier-bias") is { } bias ? int.Parse(bias) : 0;
        // <b>The fill lever, and it has to be the window because the scene target is sized from it.</b>
        // Fragment cost scales with pixels and geometry does not, so a run at a quarter of the area is the
        // one measurement that can tell the two apart. 1280x720 is what every figure in §83-87 was taken at.
        // <b>MSAA, all the way off if asked.</b> §88 measured the 200-300 m band as fragment-bound, and a 4x
        // resolve of subpixel foliage is the most expensive fragment work in the frame at exactly that
        // standoff. --msaa 1 renders straight into the present's source with no resolve declared at all; 2 and
        // 8 are there because the interesting question is where the look stops being worth the milliseconds,
        // and that is not a yes/no.
        var msaa = Value(args, "--msaa") is { } samples ? int.Parse(samples) : 4;
        // --tree-crowd mid,far (or "max") sets the two LOD thresholds for a run. See RtsGameLoop's remarks:
        // at 12,20 every tree on a village map draws in full, which is one end of the art bracket.
        // The pre-kit trees at every tier — 345-552 triangles against the kit's thousands. See §91.
        var cheapTrees = args.Contains("--cheap-trees");
        (float, float)? treeCrowd = null;
        if (Value(args, "--tree-crowd") is { } crowd)
        {
            if (crowd.Equals("max", StringComparison.OrdinalIgnoreCase))
            {
                treeCrowd = (12f, 20f);
            }
            else
            {
                var parts = crowd.Split(',');
                if (parts.Length != 2)
                {
                    throw new ArgumentException(
                        "--tree-crowd takes 'max' or two numbers: --tree-crowd 12,20", "--tree-crowd");
                }

                treeCrowd = (float.Parse(parts[0]), float.Parse(parts[1]));
            }
        }
        var windowWidth = Value(args, "--width") is { } w ? int.Parse(w) : 1280;
        var windowHeight = Value(args, "--height") is { } h ? int.Parse(h) : 720;
        if (performanceCascades > 3)
        {
            throw new ArgumentOutOfRangeException(
                "--perf-cascades", performanceCascades, "There are three cascades; 0 casts from none.");
        }
        if (performanceHour is < -1f or > 24f)
        {
            throw new ArgumentOutOfRangeException("--perf-hour", performanceHour, "Hour must be between 0 and 24.");
        }
        if (performanceRun)
        {
            Console.WriteLine(
                $"  performance fixture: camera {performanceCamera.ToString().ToLowerInvariant()}, " +
                $"light {(performanceHour >= 0f ? $"{performanceHour:F1}:00" : "live")}, " +
                $"vsync {(performanceVsync ? "on" : "off")}" +
                (performanceCascades >= 0 ? $", casters into {performanceCascades} of 3 cascades" : string.Empty));
        }
        // <b>Timings without the overlays.</b> --debug-all turns on the collider overlay too, which draws a
        // disc per body and per tree — 17 ms of it with five thousand trees in view — so a frame measured
        // that way is measuring the instrument. This asks for the numbers and nothing else.
        // <b>NOT implied by --perf-run any more, and that mistake cost §83 and §84 their absolute numbers.</b>
        // --timings turns on timingDebug, and timingDebug keeps the whole diagnostics PRODUCER alive whether
        // or not a panel is drawn — see the gate in RtsGameLoop.ReportDebug, whose own comment says
        // ReportEconomy alone costs over four milliseconds in a wide Village frame, plus a stopwatch per
        // thirty-second tree and one per undergrowth cluster. A sealed run had all of it, so the frame it
        // reported was the frame plus the instrument, and the same view from the chair ran several times
        // faster. The recorder needs none of it: build phases, node time and staged load are computed
        // unconditionally.
        var timingsOnly = args.Contains("--timings");
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
            performanceVsync,
            performanceCascades,
            shadowProxies,
            performanceBlockingUpload,
            zoomLimit,
            tierBias,
            msaa,
            treeCrowd,
            cheapTrees);
        using var window = new Window(
            game, new WindowOptions("RTSGame — Greybox Kingdom", windowWidth, windowHeight));
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

    /// <summary>
    /// A duration written the way a person would say it: 3h, 90min, 5400s, or bare simulated seconds.
    /// </summary>
    /// <remarks>
    /// <b>Wall units in, simulated seconds out, because that conversion is the one that keeps going wrong.</b>
    /// A year is chosen in hours at the chair and stored in simulated seconds, and every time somebody does
    /// that arithmetic in their head it is a chance to hand back the wrong unit — which §112 did, in a test
    /// about units. So "3h" means three hours in the chair and comes back multiplied by the compression.
    /// </remarks>
    private static float? ParseDuration(string? text, float compression = RtsGameLoop.DefaultCompression)
    {
        if (text is null) return null;
        var trimmed = text.Trim();
        if (trimmed.EndsWith("h", StringComparison.OrdinalIgnoreCase))
        {
            return float.Parse(trimmed[..^1]) * 3600f * compression;
        }

        if (trimmed.EndsWith("min", StringComparison.OrdinalIgnoreCase))
        {
            return float.Parse(trimmed[..^3]) * 60f * compression;
        }

        if (trimmed.EndsWith("s", StringComparison.OrdinalIgnoreCase))
        {
            return float.Parse(trimmed[..^1]) * compression;
        }

        return float.Parse(trimmed);
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

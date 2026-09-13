using System.Globalization;
using System.Linq;
using System.Numerics;
using RTSGame.Simulation;
using RTSGame.Simulation.Agents;
using RTSGame.Simulation.Movement;
using RTSGame.Simulation.Spatial;

namespace RTSGame.Debug;

/// <summary>
/// What happens when bodies of different sizes share a crowd.
/// </summary>
/// <remarks>
/// Every constant in <c>plan-rts.md</c> was tuned against one body, 0.37 m, and §3 of
/// <c>plan-rts-game.md</c> now has a second class at roughly 0.9 m. The pairwise arithmetic is
/// already right nearly everywhere — the velocity solver uses <c>agent.Radius + other.Radius</c>,
/// depenetration queries at <c>agent.Radius + largestRadius</c> — so what is left is the handful of
/// places where a distance was written down as a number instead of as a body.
/// <para>
/// Measured in body-relative units throughout: separation as a fraction of the pair's combined
/// radius, and the approach geometry scaled so that the same scenario at two sizes is the same
/// scenario. An overlap of 10 cm means something quite different to a villager than to a cart, and
/// comparing the raw figures would say the wider bodies behave worse when they are merely wider.
/// </para>
/// </remarks>
internal static class MixedBodyScenarios
{
    private const float Villager = AgentDefaults.Radius;
    /// <summary>The widest body in the roster, which is the cart.</summary>
    /// <remarks>
    /// It was a 0.90 m heavy class, which is retired — a body that wide cannot be routed to a point
    /// beside a building. The widest thing in the world is now the hauler cart at 0.55, and these
    /// scenarios are about mixing sizes rather than about any particular size, so they hold with the
    /// narrower spread and simply have less of it to work with.
    /// </remarks>
    private const float Heavy = 0.55f;
    private static readonly float Step = (float)SimulationWorld.FixedDeltaSeconds;

    private readonly record struct Crossing(
        string Name,
        float CombinedRadius,
        float MinimumSeparation,
        int ContactTicks,
        float ReactionDistance,
        float ClearedSeconds);

    public static int Run()
    {
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
        Console.WriteLine("RTSGame mixed body sizes");
        Console.WriteLine(
            "  Separation is reported over the pair's combined radius, so 1.00 is exactly touching");
        Console.WriteLine(
            "  and anything below it is overlap. Reaction is how far apart they were when the solver");
        Console.WriteLine(
            "  first turned either of them — avoidance that begins under 1.00 begins too late.");
        Console.WriteLine();

        Console.WriteLine(
            $"  head-on crossing | ORCA lookahead {ReciprocalVelocitySolver.NeighborLookahead:F2} m beyond contact");
        Console.WriteLine(
            "    pair                  | combined | closest | contact | reaction | cleared");
        foreach (var crossing in new[]
                 {
                     HeadOn("villager + villager", Villager, Villager),
                     HeadOn("villager + heavy", Villager, Heavy),
                     HeadOn("heavy + heavy", Heavy, Heavy),
                 })
        {
            Print(crossing);
        }

        Console.WriteLine();

        // The same three, with the horizon written as a body rather than as a number. This is the
        // candidate fix measured in place rather than argued for: nothing else changes.
        var original = ReciprocalVelocitySolver.NeighborLookahead;
        var scaled = original * 2f;
        ReciprocalVelocitySolver.NeighborLookahead = scaled;
        Console.WriteLine($"  the same, with the lookahead doubled to {scaled:F2} m");
        Console.WriteLine(
            "    pair                  | combined | closest | contact | reaction | cleared");
        foreach (var crossing in new[]
                 {
                     HeadOn("villager + villager", Villager, Villager),
                     HeadOn("villager + heavy", Villager, Heavy),
                     HeadOn("heavy + heavy", Heavy, Heavy),
                 })
        {
            Print(crossing);
        }

        ReciprocalVelocitySolver.NeighborLookahead = original;
        Console.WriteLine();

        ReportArrival();
        ReportSpeeds();
        ReportTurningCircle();
        ReportShove();
        ReportLine();
        ReportGate();
        return 0;
    }

    private static void Print(Crossing crossing)
    {
        var ratio = crossing.MinimumSeparation / crossing.CombinedRadius;
        Console.WriteLine(
            $"    {crossing.Name,-21} | {crossing.CombinedRadius,6:F2} m | {ratio,6:F3}  | " +
            $"{crossing.ContactTicks,5} t | " +
            $"{(crossing.ReactionDistance < 0f ? "  never" : $"{crossing.ReactionDistance / crossing.CombinedRadius,6:F2}")}   | " +
            $"{(crossing.ClearedSeconds < 0f ? "never" : $"{crossing.ClearedSeconds,4:F1}s")}" +
            (ratio < 0.995f ? "   OVERLAP" : ""));
    }

    /// <summary>
    /// Two bodies walking through each other, offset by a fixed fraction of their own size.
    /// </summary>
    /// <remarks>
    /// The offset scales with the bodies so the two runs are the same problem at two scales: a
    /// third of a combined radius of lateral error to correct before they meet. Given as an
    /// absolute number it would be a near miss for villagers and a head-on for wagons, and the
    /// comparison would measure the setup rather than the solver.
    /// </remarks>
    private static Crossing HeadOn(string name, float first, float second)
    {
        var world = new SimulationWorld();
        var combined = first + second;
        var offset = combined * 0.33f;
        var a = world.SpawnAgent(new Vector2(-6f, offset * 0.5f), radius: first);
        var b = world.SpawnAgent(new Vector2(6f, -offset * 0.5f), radius: second);
        world.QueueMove(new[] { a }, new Vector2(6f, offset * 0.5f));
        world.QueueMove(new[] { b }, new Vector2(-6f, -offset * 0.5f));

        var closest = float.MaxValue;
        var contact = 0;
        var reaction = -1f;
        var cleared = -1f;
        for (var tick = 0; tick < 900; tick++)
        {
            world.Tick(Step);
            ref readonly var left = ref world.Agents.Get(a);
            ref readonly var right = ref world.Agents.Get(b);
            var distance = Vector2.Distance(left.Position, right.Position);
            closest = MathF.Min(closest, distance);
            if (distance < combined - 0.001f) contact++;

            // The first moment either body is steering somewhere other than at its goal. Their
            // goals are straight ahead, so any lateral velocity at all is the solver reacting.
            if (reaction < 0f &&
                (MathF.Abs(left.Velocity.Y) > 0.05f || MathF.Abs(right.Velocity.Y) > 0.05f))
            {
                reaction = distance;
            }

            if (cleared < 0f && !left.HasDestination && !right.HasDestination)
            {
                cleared = (tick + 1) * Step;
            }
        }

        return new Crossing(name, combined, closest, contact, reaction, cleared);
    }

    /// <summary>
    /// How far a settled body is pushed when something much larger walks into it.
    /// </summary>
    /// <remarks>
    /// Depenetration splits a correction evenly between two movable bodies whatever they weigh, so
    /// a wagon and a villager each take half of it. Whether that reads as wrong depends on how far
    /// the villager actually ends up from where it was standing, which is the number here.
    /// </remarks>
    private static void ReportShove()
    {
        Console.WriteLine("  a settled body, walked into");
        Console.WriteLine("    walker    | idle body displaced | idle returns to");

        foreach (var (name, radius) in new[] { ("villager", Villager), ("heavy", Heavy) })
        {
            var world = new SimulationWorld();
            var idle = world.SpawnAgent(new Vector2(0f, 0f), radius: Villager);
            var walker = world.SpawnAgent(new Vector2(-6f, 0f), radius: radius);
            // Let the idle body settle and adopt its hold position before anything arrives.
            for (var tick = 0; tick < 30; tick++) world.Tick(Step);
            var origin = world.Agents.Get(idle).Position;
            world.QueueMove(new[] { walker }, new Vector2(6f, 0f));

            var furthest = 0f;
            for (var tick = 0; tick < 900; tick++)
            {
                world.Tick(Step);
                furthest = MathF.Max(furthest, Vector2.Distance(world.Agents.Get(idle).Position, origin));
            }

            var settled = Vector2.Distance(world.Agents.Get(idle).Position, origin);
            Console.WriteLine(
                $"    {name,-9} | {furthest,10:F2} m      | {settled,6:F2} m from where it stood");
        }

        Console.WriteLine();
    }

    /// <summary>
    /// A mixed column through a gate wide enough for the largest body in it.
    /// </summary>
    /// <remarks>
    /// Two placement cells, which §3 fixes as the gate width that admits everything. The question
    /// is not whether the heavy body fits — the decomposition already says it does — but whether a
    /// queue containing one behaves like a queue.
    /// </remarks>
    private static void ReportGate()
    {
        Console.WriteLine("  a mixed column through a 3 m gate");
        Console.WriteLine("    mix                | arrived | cleared | closest/combined | dead stops");

        var configurations = Enumerable.Range(0, 9)
            .SelectMany(heavies => new[] { (heavies, true), (heavies, false) })
            .Where(entry => entry.heavies > 0 || entry.Item2)
            .ToArray();
        var totalDeadStops = 0;
        var totalCleared = 0f;
        var worstOverall = float.MaxValue;
        foreach (var (heavies, heavyFirst) in configurations)
        {
            var world = new SimulationWorld();
            var placement = world.Placement.Transform;
            for (var z = 0; z < placement.Height; z++)
            {
                if (z == 10 || z == 11) continue;
                world.QueueToggleObstacle(placement.CellCenter(new GridCell(10, z)));
            }

            world.Tick(Step);
            var gate = placement.CellCenter(new GridCell(10, 10));

            var ids = new List<AgentId>();
            for (var i = 0; i < 12; i++)
            {
                var isHeavy = heavyFirst ? i < heavies : i >= 12 - heavies;
                var radius = isHeavy ? Heavy : Villager;
                ids.Add(world.SpawnAgent(
                    new Vector2(gate.X - 5f - i % 3 * 2.2f, gate.Y + (i / 3 - 1.5f) * 2.2f),
                    radius: radius));
            }

            world.QueueMove(ids, new Vector2(gate.X + 5f, gate.Y));

            var worstRatio = float.MaxValue;
            var cleared = -1f;
            for (var tick = 0; tick < 2400; tick++)
            {
                world.Tick(Step);
                for (var i = 0; i < ids.Count; i++)
                for (var j = i + 1; j < ids.Count; j++)
                {
                    ref readonly var first = ref world.Agents.Get(ids[i]);
                    ref readonly var second = ref world.Agents.Get(ids[j]);
                    var combined = first.Radius + second.Radius;
                    var ratio = Vector2.Distance(first.Position, second.Position) / combined;
                    worstRatio = MathF.Min(worstRatio, ratio);
                }

                if (cleared < 0f && ids.All(id => !world.Agents.Get(id).HasDestination))
                {
                    cleared = (tick + 1) * Step;
                }
            }

            var arrived = ids.Count(id => world.Agents.Get(id).Position.X > gate.X + 2f);
            totalDeadStops += (int)world.AvoidanceTerrainDeadStops;
            totalCleared += cleared < 0f ? 60f : cleared;
            worstOverall = MathF.Min(worstOverall, worstRatio);
            Console.WriteLine(
                $"    {heavies,2} heavy, {(heavyFirst ? "leading" : "trailing"),-8} | {arrived,4}/12 | " +
                $"{(cleared < 0f ? "never" : $"{cleared,4:F1}s")}  | {worstRatio,10:F3}       | " +
                $"{world.AvoidanceTerrainDeadStops}");
        }

        Console.WriteLine(
            $"    over {configurations.Length} arrangements | dead stops {totalDeadStops} | " +
            $"cleared {totalCleared:F1}s total | worst separation {worstOverall:F3}");
        Console.WriteLine();
    }

    /// <summary>
    /// Whether a body holds its course while a crowd crosses in front of it.
    /// </summary>
    /// <remarks>
    /// This is the number the correction share exists to move. A wagon that takes half of every
    /// contact with every villager it passes is pushed off its line by the crowd rather than
    /// through it; weighting the share by mass is supposed to show up here and nowhere else.
    /// Reported for both classes so the heavy figure has something to be read against — a body
    /// crossing traffic is deflected whatever it weighs, and the question is how much less.
    /// </remarks>
    private static void ReportLine()
    {
        Console.WriteLine("  holding a line across crossing traffic");
        Console.WriteLine("    body      | worst deviation | at the far side | walked");

        foreach (var (name, radius) in new[] { ("villager", Villager), ("heavy", Heavy) })
        {
            var world = new SimulationWorld();
            var crosser = world.SpawnAgent(new Vector2(-9f, 0f), radius: radius);

            // Two files of villagers walking across the crosser's path, from both sides, so the
            // deflection cannot come from being pushed consistently one way.
            var traffic = new List<AgentId>();
            for (var row = 0; row < 5; row++)
            for (var side = 0; side < 2; side++)
            {
                traffic.Add(world.SpawnAgent(new Vector2(
                    -1.5f + row * 0.95f,
                    (side == 0 ? -1f : 1f) * (5f + row * 0.6f))));
            }

            for (var i = 0; i < traffic.Count; i++)
            {
                var start = world.Agents.Get(traffic[i]).Position;
                world.QueueMove(new[] { traffic[i] }, new Vector2(start.X, -start.Y));
            }

            world.QueueMove(new[] { crosser }, new Vector2(9f, 0f));

            var worst = 0f;
            var walked = 0f;
            var previous = world.Agents.Get(crosser).Position;
            for (var tick = 0; tick < 1200; tick++)
            {
                world.Tick(Step);
                var position = world.Agents.Get(crosser).Position;
                walked += Vector2.Distance(previous, position);
                previous = position;
                worst = MathF.Max(worst, MathF.Abs(position.Y));
            }

            var final = world.Agents.Get(crosser).Position;
            Console.WriteLine(
                $"    {name,-9} | {worst,10:F2} m    | {MathF.Abs(final.Y),10:F2} m   | {walked,5:F1} m");
        }

        Console.WriteLine();
    }

    /// <summary>
    /// How much warning the velocity solve gives, as a time rather than as a distance.
    /// </summary>
    /// <remarks>
    /// The neighbour horizon is written as a distance beyond contact, and the argument for that
    /// number is a reaction *time*: 0.78 m is 0.44 s at walking pace. That argument only held while
    /// every unit walked. A scout closes on another scout at 7 m/s, so the same 0.78 m is 0.11 s —
    /// a quarter of the warning, from the same constant, for the same reason the flat 1.52 m gave
    /// two heavy bodies none. Measured here before deciding whether it wants fixing.
    /// </remarks>
    private static void ReportSpeeds()
    {
        Console.WriteLine("  warning, as a time rather than a distance");
        Console.WriteLine("    pair                  | speed    | reaction | closing | warning");

        foreach (var (name, speed) in new[]
                 {
                     ("hauler cart", UnitType.HaulerCart.MaximumSpeed),
                     ("villager", UnitType.Villager.MaximumSpeed),
                     ("heavy cavalry", UnitType.HeavyCavalry.MaximumSpeed),
                     ("light cavalry", UnitType.LightCavalry.MaximumSpeed),
                 })
        {
            var world = new SimulationWorld();
            const float radius = Villager;
            var combined = radius * 2f;
            var offset = combined * 0.33f;
            var left = world.SpawnAgent(new Vector2(-9f, offset * 0.5f), radius: radius, maximumSpeed: speed);
            var right = world.SpawnAgent(new Vector2(9f, -offset * 0.5f), radius: radius, maximumSpeed: speed);
            world.QueueMove(new[] { left }, new Vector2(9f, offset * 0.5f));
            world.QueueMove(new[] { right }, new Vector2(-9f, -offset * 0.5f));

            var reaction = -1f;
            for (var tick = 0; tick < 1200; tick++)
            {
                world.Tick(Step);
                ref readonly var first = ref world.Agents.Get(left);
                ref readonly var second = ref world.Agents.Get(right);
                if (reaction >= 0f) continue;
                if (MathF.Abs(first.Velocity.Y) > 0.05f || MathF.Abs(second.Velocity.Y) > 0.05f)
                {
                    reaction = Vector2.Distance(first.Position, second.Position);
                }
            }

            // What matters is the gap left to close when the solve first acts, over the rate it is
            // closing at — which is the seconds the body actually has to do something about it.
            var gap = reaction < 0f ? 0f : reaction - combined;
            var closing = speed * 2f;
            Console.WriteLine(
                $"    two at {name,-14} | {speed,4:F2} m/s | " +
                $"{(reaction < 0f ? "  never" : $"{reaction,6:F2} m")} | {closing,4:F1} m/s | " +
                $"{(reaction < 0f ? "   -  " : $"{gap / closing,5:F2} s")}");
        }

        Console.WriteLine();
    }

    /// <summary>
    /// The tightest arc a body actually traces when told to come about at speed.
    /// </summary>
    /// <remarks>
    /// Measured as speed over the rate its heading is swinging, which is the radius of the arc it
    /// is on at that instant, and reported as the tightest one it managed. The claim a turning
    /// circle makes is not that a wagon turns slowly — it is that a wagon changes direction only
    /// by rolling round an arc it cannot tighten, so the radius is the thing to look at.
    /// <para>
    /// The reversal is ordered while the body is still travelling. Ordered after it had arrived,
    /// there is no arc to trace: a stopped body turns on the spot at the pivot floor and the
    /// measurement reads zero for everything, which is what the first version of this did.
    /// </para>
    /// </remarks>
    private static void ReportTurningCircle()
    {
        Console.WriteLine("  coming about at speed");
        Console.WriteLine("    unit           | asked for | tightest arc traced | time to reverse");

        foreach (var type in new[]
                 {
                     UnitType.Villager, UnitType.HaulerCart, UnitType.LightCavalry, UnitType.HeavyCavalry,
                 })
        {
            var world = new SimulationWorld();
            var id = world.SpawnAgent(new Vector2(-13f, 0f), type);
            world.QueueMove(new[] { id }, new Vector2(13f, 0f));

            // Long enough to be at speed, far short of arriving.
            for (var tick = 0; tick < 90; tick++) world.Tick(Step);
            world.QueueMove(new[] { id }, new Vector2(-13f, 0f));

            var tightest = float.MaxValue;
            var reversedAt = -1f;
            var previousHeading = world.Agents.Get(id).Velocity;
            for (var tick = 0; tick < 600; tick++)
            {
                world.Tick(Step);
                ref readonly var agent = ref world.Agents.Get(id);
                var velocity = agent.Velocity;
                var speed = velocity.Length();
                if (speed > 0.3f && previousHeading.LengthSquared() > 0.09f)
                {
                    var from = Vector2.Normalize(previousHeading);
                    var to = velocity / speed;
                    var swing = MathF.Acos(Math.Clamp(Vector2.Dot(from, to), -1f, 1f));
                    // Below a tenth of a degree a tick this is straight-line travel and the
                    // quotient is dominated by float noise rather than by any arc.
                    if (swing > 0.0017f) tightest = MathF.Min(tightest, speed * Step / swing);
                }

                previousHeading = velocity;
                if (reversedAt < 0f && velocity.X < -0.1f) reversedAt = (tick + 1) * Step;
            }

            Console.WriteLine(
                $"    {type.Name,-14} | {(type.HasTurningCircle ? $"{type.TurningRadius,6:F2} m" : "  free"),-9} | " +
                $"{(tightest == float.MaxValue ? "        never turned" : $"{tightest,13:F2} m     ")} | " +
                $"{(reversedAt < 0f ? " never" : $"{reversedAt,5:F1} s")}");
        }

        Console.WriteLine();
    }

    /// <summary>Whether each unit type can settle on an empty point at all.</summary>
    private static void ReportArrival()
    {
        Console.WriteLine("  arriving at an empty point");
        Console.WriteLine("    unit           | arrived | closest approach | speed there");

        foreach (var type in UnitType.All)
        {
            var world = new SimulationWorld();
            var target = new Vector2(6f, 0f);
            var id = world.SpawnAgent(new Vector2(-9f, 0f), type);
            world.QueueMove(new[] { id }, target);

            var arrived = -1f;
            var closest = float.MaxValue;
            var speedThere = 0f;
            for (var tick = 0; tick < 1800; tick++)
            {
                world.Tick(Step);
                ref readonly var agent = ref world.Agents.Get(id);
                var distance = Vector2.Distance(agent.Position, target);
                if (distance < closest)
                {
                    closest = distance;
                    speedThere = agent.Velocity.Length();
                }

                if (agent.HasDestination) continue;
                arrived = (tick + 1) * Step;
                break;
            }

            Console.WriteLine(
                $"    {type.Name,-14} | {(arrived < 0f ? "  never" : $"{arrived,5:F1}s"),-7} | " +
                $"{closest,11:F2} m     | {speedThere,4:F2} m/s");
        }

        Console.WriteLine();
    }
}

using System.Numerics;
using RTSGame.Simulation;
using RTSGame.Simulation.Agents;
using RTSGame.Simulation.Collision;

namespace RTSGame.Debug;

/// <summary>
/// Fighting and chasing, isolated, so a change to either can actually be measured.
/// </summary>
/// <remarks>
/// <b>This exists because five locomotion changes were judged by the wrong instrument and all five
/// verdicts were wrong.</b> §40: <c>--raidtest</c> measures economy, jobs, hauling, threat, pathing and a
/// scripted director together over six minutes, so anything that perturbs timing reroutes the run — and
/// the same unchanged configuration produced 4.4 or 2.4 raiders killed depending on which five seeds were
/// drawn. It is a good gate for "did something break" and useless for "is this better".
/// <para>
/// So these follow the pattern the movement benchmarks already established for crowds: one mechanism per
/// scenario, open ground, no economy, seconds to run, and every number reported rather than summarised. The
/// three cases are the three things this session kept guessing about:
/// </para>
/// <list type="number">
/// <item><b>A ring.</b> N defenders against one raider standing still. How long to kill it, how much of the
/// time anybody is actually in contact, and how many of the N ever land a blow. This is the engagement
/// limit and the concentration question, with nothing else in the frame.</item>
/// <item><b>A chase.</b> One pursuer, one quarry, a speed ratio. What share of the chase is spent
/// <em>inside reach</em> — the number every one of the five rejected hypotheses was really about, and not
/// one of them measured it.</item>
/// <item><b>A cordon.</b> One body trying to cross a ring of enemies standing in its way. Does it get
/// through, how long does it take, how far does it deviate. That is the reported bug — "slid in, got
/// surrounded, walked away" — stated as a measurement.</item>
/// </list>
/// <para>
/// Deterministic and cheap, so a question can be swept across a dozen configurations instead of five.
/// </para>
/// </remarks>
internal static class FightBenchmarks
{
    private const int TicksPerSecond = 30;
    private static readonly FactionId Ours = new(0);
    private static readonly FactionId Theirs = new(1);

    public static int Run()
    {
        Console.WriteLine("RTSGame fight benchmarks — one mechanism per scenario, open ground");

        Console.WriteLine();
        Console.WriteLine("  a ring: N villagers on one stationary raider");
        Console.WriteLine(
            "    N | killed in | contact% | landed/N | most at once | body-s of contact");
        foreach (var count in new[] { 1, 2, 3, 4, 6, 8, 12 })
        {
            Ring(count);
        }

        Console.WriteLine();
        Console.WriteLine("  a chase: one pursuer, one quarry, 40 m of running room");
        Console.WriteLine(
            "    quarry pace | caught | inside reach% | gap at end | closing m/s");
        foreach (var share in new[] { 1.15f, 1.00f, 0.90f, 0.80f, 0.70f, 0.60f })
        {
            Chase(share);
        }

        Console.WriteLine();
        Console.WriteLine("  a chase, swept on the stop distance the chase behaviour asks for");
        Console.WriteLine(
            "    stop at | effective gap | inside reach% | caught a 0.7x quarry in");
        var wasStop = AgentDefaults.ChaseStopMetres;
        foreach (var stop in new[] { 0.95f, 0.60f, 0.30f, 0.0f })
        {
            AgentDefaults.ChaseStopMetres = stop;
            Chase(0.70f, label: $"{stop:F2} m");
        }

        AgentDefaults.ChaseStopMetres = wasStop;

        Console.WriteLine();
        Console.WriteLine("  a cordon: one body crossing a ring of N enemies stood in the way");
        Console.WriteLine(
            "    N | crossed | seconds | detour | held below half pace for");
        foreach (var count in new[] { 2, 4, 6, 8, 12 })
        {
            Cordon(count);
        }

        return 0;
    }

    /// <summary>
    /// N bodies set on one that cannot run, which is the fight with the running taken out.
    /// </summary>
    /// <remarks>
    /// The quarry is given no orders and no speed, so nothing here depends on pursuit: what is left is
    /// purely how many can get at it and how continuously. <c>landed/N</c> is the concentration number and
    /// <c>contact%</c> is the duty cycle — a fight where six are present and three are ever touching is a
    /// different mechanism from one where six touch half the time, and the raid scenario could not tell
    /// them apart.
    /// </remarks>
    private static void Ring(int count)
    {
        var world = new SimulationWorld(120f);
        var victim = world.SpawnAgent(Vector2.Zero, UnitType.Raider, Theirs);
        // Directed and speedless: it is a post to hit, not an opponent, because this scenario is not about
        // whether it can get away.
        ref var quarry = ref world.Agents.Get(victim);
        quarry.Directed = true;
        quarry.MaximumSpeed = 0f;
        // <b>And carrying, which is what makes anybody attack it.</b> The first version of this fixture
        // measured zero contacts at every N, and the fixture was wrong rather than the game: question one
        // asks what I can see that is worth protecting, and an empty field with one thief standing in it
        // contains nothing. A thief with grain on its back is itself the thing worth protecting — §33 — so
        // this is the smallest world in which a defence has any reason to exist.
        quarry.Jobs.Carrying = Simulation.Economy.Resource.Grain;
        quarry.Jobs.CarriedUnits = 20;
        var health = quarry.Health;

        var ring = UnitType.Raider.Radius + UnitType.Villager.Radius + 0.4f;
        var mob = new List<AgentId>();
        for (var i = 0; i < count; i++)
        {
            var angle = i / (float)count * MathF.Tau;
            mob.Add(world.SpawnAgent(
                new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * ring,
                UnitType.Villager,
                Ours));
        }

        var landed = new HashSet<int>();
        var contactTicks = 0;
        var mostAtOnce = 0;
        var contactSeconds = 0f;
        var killedAt = -1f;
        var limit = 60 * TicksPerSecond;
        for (var tick = 1; tick <= limit; tick++)
        {
            world.Tick((float)SimulationWorld.FixedDeltaSeconds);
            var struck = world.Threat.Contacts;
            if (struck > 0) contactTicks++;
            mostAtOnce = Math.Max(mostAtOnce, struck);
            contactSeconds += struck * (float)SimulationWorld.FixedDeltaSeconds;
            foreach (var who in world.Threat.LandedThisTick) landed.Add(who.Value);
            if (!world.Agents.Contains(victim) || !world.Agents.Get(victim).IsAlive)
            {
                killedAt = tick / (float)TicksPerSecond;
                break;
            }
        }

        var elapsed = killedAt > 0f ? killedAt : limit / (float)TicksPerSecond;
        Console.WriteLine(
            $"    {count,2} | {(killedAt > 0f ? $"{killedAt,7:F1} s" : "   never"),9} | " +
            $"{100f * contactTicks / (elapsed * TicksPerSecond),7:F0}% | {landed.Count,4}/{count,-3} | " +
            $"{mostAtOnce,12} | {contactSeconds,17:F1}" +
            (killedAt > 0f ? string.Empty : $"  ({health - world.Agents.Get(victim).Health:F0} of {health:F0} done)"));
    }

    /// <summary>
    /// One body running from another, and the only question that matters: how much of it is spent in reach.
    /// </summary>
    /// <remarks>
    /// A chase that never closes and a chase that closes and loses contact every second are both "did not
    /// catch it", and they want completely different fixes. The quarry runs in a straight line rather than
    /// evading, because evasion is a separate mechanism and mixing them is how the raid scenario stopped
    /// being able to answer anything.
    /// </remarks>
    private static void Chase(float quarryPaceShare, string? label = null)
    {
        var world = new SimulationWorld(240f);
        var quarry = world.SpawnAgent(new Vector2(-40f, 0f), UnitType.Villager, Theirs);
        var hunter = world.SpawnAgent(new Vector2(-41.2f, 0f), UnitType.Villager, Ours);
        ref var runner = ref world.Agents.Get(quarry);
        runner.Directed = true;
        runner.MaximumSpeed = UnitType.Villager.MaximumSpeed * quarryPaceShare;
        // Away down the long axis, and far enough that it never arrives and stops.
        world.QueueMove(new[] { quarry }, new Vector2(100f, 0f));
        world.QueueChase(new[] { hunter }, quarry);

        var reach = MathF.Max(
            (UnitType.Villager.Radius + UnitType.Villager.Radius) *
            Simulation.Threat.ThreatSystem.ReachShare,
            AgentDefaults.ChaseStopMetres + Simulation.Threat.ThreatSystem.ContactSlack);
        var inReach = 0;
        var startGap = 1.2f;
        var caught = -1f;
        var limit = 60 * TicksPerSecond;
        var ticks = 0;
        for (var tick = 1; tick <= limit; tick++)
        {
            world.Tick((float)SimulationWorld.FixedDeltaSeconds);
            ticks = tick;
            if (!world.Agents.Contains(quarry) || !world.Agents.Get(quarry).IsAlive)
            {
                caught = tick / (float)TicksPerSecond;
                break;
            }

            var gap = Vector2.Distance(
                world.Agents.Get(hunter).Position, world.Agents.Get(quarry).Position);
            if (gap <= reach) inReach++;
        }

        var endGap = world.Agents.Contains(quarry) && world.Agents.Get(quarry).IsAlive
            ? Vector2.Distance(world.Agents.Get(hunter).Position, world.Agents.Get(quarry).Position)
            : 0f;
        var seconds = ticks / (float)TicksPerSecond;
        // Positive closes, negative loses ground. The arithmetic prediction is the speed difference; what
        // this reports is what the layer actually delivered, and the two are not the same number.
        var closing = (startGap - endGap) / MathF.Max(0.01f, seconds);
        if (label is not null)
        {
            Console.WriteLine(
                $"    {label,7} | {endGap,13:F2} m | {100f * inReach / ticks,12:F0}% | " +
                $"{(caught > 0f ? $"{caught:F1} s" : "never"),24}");
            return;
        }

        Console.WriteLine(
            $"    {quarryPaceShare * UnitType.Villager.MaximumSpeed,7:F2} m/s | " +
            $"{(caught > 0f ? $"{caught,6:F1} s" : "  no"),8} | {100f * inReach / ticks,12:F0}% | " +
            $"{endGap,7:F2} m | {closing,+8:F3}");
    }

    /// <summary>
    /// A body walking through a wall of enemies, which is the reported bug as a number.
    /// </summary>
    /// <remarks>
    /// The cordon stands still and is given no orders, so this measures one thing: what the movement layer
    /// does when a body's route runs through bodies it is hostile to. A crossing that costs nothing is the
    /// bug; a crossing that costs seconds and a detour is a cordon doing its job.
    /// </remarks>
    private static void Cordon(int count)
    {
        var world = new SimulationWorld(120f);
        var runner = world.SpawnAgent(new Vector2(-12f, 0f), UnitType.Raider, Theirs);
        ref var body = ref world.Agents.Get(runner);
        body.Directed = true;
        // Health out of the way: this is about getting past, not about surviving.
        body.Health = 1e6f;

        // A line across the path, a body's width apart, so there is no clean gap to walk through.
        var span = UnitType.Villager.Radius * 2f * 1.05f;
        for (var i = 0; i < count; i++)
        {
            var offset = (i - (count - 1) * 0.5f) * span;
            world.SpawnAgent(new Vector2(0f, offset), UnitType.Villager, Ours);
        }

        world.QueueMove(new[] { runner }, new Vector2(12f, 0f));
        var walked = 0f;
        var slowTicks = 0;
        var halfPace = UnitType.Raider.MaximumSpeed * 0.5f;
        var previous = world.Agents.Get(runner).Position;
        var crossedAt = -1f;
        var limit = 60 * TicksPerSecond;
        var ticks = 0;
        for (var tick = 1; tick <= limit; tick++)
        {
            world.Tick((float)SimulationWorld.FixedDeltaSeconds);
            ticks = tick;
            ref readonly var it = ref world.Agents.Get(runner);
            walked += Vector2.Distance(it.Position, previous);
            previous = it.Position;
            if (it.Velocity.Length() < halfPace) slowTicks++;
            if (it.Position.X > 4f)
            {
                crossedAt = tick / (float)TicksPerSecond;
                break;
            }
        }

        // Detour: how much further it walked than the straight line it was given.
        var straight = 16f;
        Console.WriteLine(
            $"    {count,2} | {(crossedAt > 0f ? "yes" : " no"),7} | " +
            $"{(crossedAt > 0f ? crossedAt : ticks / (float)TicksPerSecond),7:F1} | " +
            $"{walked / straight,6:F2}x | {slowTicks / (float)TicksPerSecond,6:F1} s");
    }
}

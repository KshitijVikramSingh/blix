using System.Numerics;
using RTSGame.Simulation;
using RTSGame.Simulation.Agents;
using RTSGame.Simulation.Jobs;
using RTSGame.Simulation.Persistence;

namespace RTSGame.Debug;

/// <summary>
/// What a standing arrangement does when nobody is watching it, measured on the map the game
/// is actually played on.
/// </summary>
/// <remarks>
/// The self-tests prove the jobs layer is correct on one unit at a time in a thirty-metre
/// square. This is the other question, and it is the one the design cares about: over minutes,
/// on six hundred metres, does the arrangement keep working — and what does it cost?
/// <para>
/// Two lanes, at the two lengths the design actually predicts. Sixteen units run the
/// catchment: <see cref="CatchmentMetres"/>, which is §6's sixty seconds at hauler pace and the
/// distance a granary is supposed to serve. Sixteen more run the long haul across the ridge,
/// which means every one of them uses the single gap in it, in both directions, continuously —
/// the workload the congestion body terms exist for and the one Session 6 has to settle them
/// against. Sixteen stand posted, which is the cheapest thing the layer does and the shape a
/// garrison will take.
/// </para>
/// <para>
/// An order lands in the middle of it, on the catchment lane, and is then simply let go of —
/// never told to resume. Two numbers come out of that and they are worth keeping apart: how
/// long the order took to carry out, which is a distance, and how long after the unit was
/// standing free it went back to work, which is the only part the jobs layer decides.
/// </para>
/// </remarks>
internal static class JobScenarios
{
    private const int TicksPerSecond = 30;

    /// <summary>
    /// §6's catchment: sixty seconds at hauler pace, 66 m. The length a local shuttle should be.
    /// </summary>
    private const float CatchmentMetres = 66f;

    public static int Run(float extentMeters, int minutes)
    {
        var world = new SimulationWorld(extentMeters);
        WorldTerrainScenarios.Shape(world);
        var extent = world.ExtentMeters;
        var pass = WorldTerrainScenarios.PassCentreOf(extent);

        // A depot south of the ridge, a granary a catchment away from it, and a site north of
        // the ridge — so the long lane has no way through but the pass.
        var depot = new Vector2(pass.X, -0.06f * extent);
        var granary = depot + new Vector2(CatchmentMetres, 0f);
        var site = new Vector2(pass.X + 0.02f * extent, 0.30f * extent);

        var local = Employ(world, 16, depot + new Vector2(0f, -6f), Assignment.Shuttle(depot, granary, 4f));
        var longHaul = Employ(world, 16, depot + new Vector2(0f, -12f), Assignment.Shuttle(depot, site, 4f));
        var postedAt = depot + new Vector2(-10f, 4f);
        var posted = Employ(world, 16, postedAt, Assignment.Hold(postedAt, 8f));

        var rally = depot + new Vector2(-0.05f * extent, -0.04f * extent);
        var totalTicks = TicksPerSecond * 60 * minutes;
        // No order at all unless there is time left afterwards for the settlement to go back to work
        // on its own, which is the whole thing the order is here to demonstrate. A shorter run than
        // that ended with sixteen units still legitimately interrupted and reported it as a fault.
        var recoveryTicks = TicksPerSecond * 150;
        var interruptAt = totalTicks > TicksPerSecond * 180 + recoveryTicks
            ? TicksPerSecond * 180
            : int.MaxValue;

        Console.WriteLine(
            $"RTSGame jobs trace — {extent:F0} m, {world.Agents.Count} units, {minutes} sim minutes");
        Console.WriteLine(
            $"  16 on the catchment ({CatchmentMetres:F0} m), 16 on the long haul " +
            $"({Vector2.Distance(depot, site):F0} m through the pass), 16 posted");
        Console.WriteLine(
            "     time | catchment | long haul | posted | working | interrupted | unreachable | " +
            "jobs ms | tick ms | astar");

        var previous = (Local: 0, Long: 0, Posted: 0, Tick: 0);
        var orderTick = -1;
        // Per unit, because the cohort never does anything simultaneously: they are ordered
        // together and they arrive, are released and go back to work one at a time. Averaging a
        // cohort-wide "all of them are free" moment gives a number that never arrives.
        var freeAt = new int[local.Length];
        Array.Fill(freeAt, -1);
        for (var tick = 1; tick <= totalTicks; tick++)
        {
            if (tick == interruptAt)
            {
                world.QueueMove(local, rally);
                orderTick = tick;
            }

            world.Tick((float)SimulationWorld.FixedDeltaSeconds);

            if (orderTick > 0)
            {
                // <b>One moment per unit now, and it used to be three.</b> The other two — the interrupt
                // expiring on its own, and the first leg finished after it — measured behaviour §106 removed
                // on purpose, so they read NaN on every run. Arrival is still per unit rather than
                // cohort-wide, because the cohort never does anything simultaneously: they are ordered
                // together and they arrive one at a time, and a "they are all there" moment never comes.
                for (var i = 0; i < local.Length; i++)
                {
                    if (freeAt[i] < 0 && !world.Agents.Get(local[i]).HasDestination) freeAt[i] = tick;
                }
            }

            if (tick % (TicksPerSecond * 30) != 0) continue;

            var counts = Census(world);
            var seconds = (tick - previous.Tick) / (float)TicksPerSecond;
            var now = (Local: Legs(world, local), Long: Legs(world, longHaul), Posted: Legs(world, posted));
            Console.WriteLine(
                $"  {tick / (float)TicksPerSecond,6:F0} s | " +
                $"{(now.Local - previous.Local) * 60f / seconds,9:F1} | " +
                $"{(now.Long - previous.Long) * 60f / seconds,9:F1} | " +
                $"{(now.Posted - previous.Posted) * 60f / seconds,6:F1} | " +
                $"{counts.Working,7} | {counts.Interrupted,11} | {counts.Unreachable,11} | " +
                $"{Phase(world, SimulationPhase.Jobs),7:F3} | " +
                $"{Phase(world, SimulationPhase.TotalTick),7:F3} | {world.PathQueries,5}");
            previous = (now.Local, now.Long, now.Posted, tick);
        }

        var final = Census(world);
        Console.WriteLine("  legs per minute above, by lane. Sixteen units on each.");
        // <b>The grace and the resumed walk are gone, because §106 deleted the thing they measured.</b> This
        // trace was written when an order's interrupt counted itself down and the body walked back to work on
        // its own; §106 decided from the chair that an order holds until overridden — reach the target, then
        // idle. So "interrupt expired N s later" was reporting a NaN and "first leg finished" another, and
        // the FAULT line below called sixteen correctly-parked villagers a fault on every run since.
        //
        // A trace that cries wolf gets read as noise and then not read at all, which is worse than no trace:
        // this one was reporting a fault for months and the only thing that noticed was a session that ran it
        // for an unrelated reason.
        Console.WriteLine(orderTick > 0
            ? $"  order at 180 s, {local.Length} units: standing free after " +
              $"{Mean(freeAt, orderTick):F1} s (a distance), and parked there — §106, an order holds"
            : $"  no order issued: {minutes} minutes leaves no room to watch one be carried out");
        Console.WriteLine(
            $"  ending: {final.Working} working, {final.Interrupted} parked under orders, " +
            $"{final.Unreachable} unable to reach their place");

        ReportSave(world);

        // <b>One fault, and being parked is not it.</b> A body that cannot reach its place is broken; a body
        // holding an interrupt after an order is doing what §106 asks. The ordered cohort is expected to be
        // exactly that count, so a mismatch there is worth saying out loud without being a failure — it means
        // somebody left the cohort, and CohortDeparture says why.
        var faults = final.Unreachable;
        if (faults > 0)
        {
            Console.WriteLine($"  FAULT: {final.Unreachable} cannot reach their place");
        }

        if (orderTick > 0 && final.Interrupted != local.Length)
        {
            Console.WriteLine(
                $"  note: {final.Interrupted} parked against {local.Length} ordered — the difference left the " +
                "cohort, and cohortDepartures says by which reason");
        }

        return faults > 0 ? 1 : 0;
    }

    /// <summary>
    /// What saving this settlement costs, measured where a career save would actually be taken.
    /// </summary>
    /// <remarks>
    /// §5 makes the save core loop, so its cost belongs in the trace of the thing being saved rather
    /// than in a microbenchmark of an empty map. The bulk is the ground: heights and surfaces are one
    /// value per cell of the whole world whether anything has happened on them or not, and the
    /// congestion field costs whatever the jams cost. Both are the numbers to watch when Session 6
    /// adds stock and nodes.
    /// </remarks>
    private static void ReportSave(SimulationWorld world)
    {
        using var buffer = new MemoryStream();
        var watch = System.Diagnostics.Stopwatch.StartNew();
        WorldSave.Save(world, buffer);
        var saved = watch.Elapsed.TotalMilliseconds;
        buffer.Position = 0;
        watch.Restart();
        var loaded = WorldSave.Load(buffer);
        var read = watch.Elapsed.TotalMilliseconds;

        world.DropRouteCaches();
        var matches =
            DeterminismCheck.Fingerprint(world, DeterminismCheck.Scope.Full, includeWork: false) ==
            DeterminismCheck.Fingerprint(loaded, DeterminismCheck.Scope.Full, includeWork: false);
        Console.WriteLine(
            $"  save {buffer.Length / 1024f:N0} KB in {saved:F0} ms, loaded back in {read:F0} ms, " +
            $"identical: {matches}");
    }

    private static float Seconds(int ticks) => ticks / (float)TicksPerSecond;

    private static float Mean(int[] ticks, int from) =>
        Mean(ticks, Enumerable.Repeat(from, ticks.Length).ToArray());

    /// <summary>Mean gap between two per-unit moments, over the units that reached both.</summary>
    private static float Mean(int[] ticks, int[] from)
    {
        var total = 0;
        var counted = 0;
        for (var i = 0; i < ticks.Length; i++)
        {
            if (ticks[i] < 0 || from[i] < 0) continue;
            total += ticks[i] - from[i];
            counted++;
        }

        return counted == 0 ? float.NaN : Seconds(total) / counted;
    }

    private static AgentId[] Employ(SimulationWorld world, int count, Vector2 muster, Assignment work)
    {
        var ids = new AgentId[count];
        for (var i = 0; i < count; i++)
        {
            ids[i] = world.SpawnAgent(muster + new Vector2(i % 8 * 1.1f - 4.4f, i / 8 * 1.1f));
        }

        world.QueueAssign(ids, work);
        return ids;
    }

    private readonly record struct JobCensus(int Working, int Interrupted, int Unreachable);

    private static JobCensus Census(SimulationWorld world)
    {
        var working = 0;
        var interrupted = 0;
        var unreachable = 0;
        foreach (ref readonly var agent in world.Agents.All)
        {
            if (!agent.IsAlive || !agent.Jobs.HasAssignment) continue;
            if (agent.Jobs.IsInterrupted) interrupted++;
            else if (agent.Jobs.CannotReachWork) unreachable++;
            else working++;
        }

        return new JobCensus(working, interrupted, unreachable);
    }

    private static int Legs(SimulationWorld world, IEnumerable<AgentId> ids)
    {
        var legs = 0;
        foreach (var id in ids)
        {
            if (world.Agents.Contains(id)) legs += world.Agents.Get(id).Jobs.LegsCompleted;
        }

        return legs;
    }

    /// <summary>
    /// One phase's cost, read out of the same formatted line the overlay uses, so there is no
    /// second accessor into the timings to keep in step with it.
    /// </summary>
    private static double Phase(SimulationWorld world, SimulationPhase phase)
    {
        var label = phase == SimulationPhase.TotalTick ? "total" : phase.ToString().ToLowerInvariant();
        foreach (var column in world.Timings.Format(world.Agents.Count, world.TickNumber).Split('|'))
        {
            var text = column.Trim();
            if (!text.StartsWith(label + " ", StringComparison.Ordinal)) continue;
            var value = text[(label.Length + 1)..].Replace(" ms", string.Empty);
            return double.TryParse(value, out var milliseconds) ? milliseconds : 0.0;
        }

        return 0.0;
    }
}

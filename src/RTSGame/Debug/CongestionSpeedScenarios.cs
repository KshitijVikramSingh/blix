using System.Globalization;
using System.Numerics;
using RTSGame.Simulation;
using RTSGame.Simulation.Agents;
using RTSGame.Simulation.Spatial;
using RTSGame.Simulation.Terrain;

namespace RTSGame.Debug;

/// <summary>
/// Whether a fast unit and a slow one disagree about a jam, which they should.
/// </summary>
/// <remarks>
/// Route cost is seconds at a reference pace and a body's own speed scales every leg of travel
/// equally — which is exactly what lets one field serve every unit. Congestion is the one term that
/// does not scale, because a queue costs whoever is standing in it the same wall clock however fast
/// they would otherwise be moving. So the same jam is a different fraction of a journey depending
/// on who is looking at it, and a field that charges everyone the same for it is telling somebody
/// the wrong thing.
/// <para>
/// Concretely, on a choice between ten reference-seconds of jammed road and a thirty-second clear
/// detour — a dead tie at the reference pace — a scout's real cost is 25.1 s the short way against
/// 15.3 s round, and a cart's is 36.3 s against 48.8 s. They should pick differently. Unscaled they
/// cannot: they read the same number and it says the routes are equal.
/// </para>
/// <para>
/// Measured here rather than argued, because this project has twice paid for a cost term that was
/// reasoned into place — see `plan-rts.md` §1 on barriers-as-cost and §6 on everything else.
/// </para>
/// </remarks>
internal static class CongestionSpeedScenarios
{
    private static readonly float Step = (float)SimulationWorld.FixedDeltaSeconds;

    /// <summary>Cells of gap. Six is 3 m, which admits every body class.</summary>
    private const int GapCells = 6;

    /// <summary>Bodies that never reached the goal, reported under the table rather than in it.</summary>
    private static readonly List<string> Failures = new();

    public static int Run()
    {
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
        Console.WriteLine("RTSGame congestion against unit speed");
        Console.WriteLine(
            "  One wall, two ways through: a near gap on the direct line and a detour some way");
        Console.WriteLine(
            "  along it. The near gap is jammed by a crowd already queueing for it.");
        Console.WriteLine();
        Console.WriteLine(
            "  Swept, because a single geometry proves nothing: at a short detour everybody goes");
        Console.WriteLine(
            "  round and at a long one everybody waits, and the only place the question is live is");
        Console.WriteLine(
            "  where the reference body is close to indifferent. D = detour, J = through the jam.");
        Console.WriteLine();

        var types = new[]
        {
            UnitType.HaulerCart, UnitType.Villager, UnitType.HeavyCavalry, UnitType.LightCavalry,
        };

        Console.Write("    detour |");
        foreach (var type in types) Console.Write($" {type.Name,-14} |");
        Console.WriteLine();
        Console.Write("           |");
        foreach (var type in types) Console.Write($" {type.MaximumSpeed,4:F2} m/s      |");
        Console.WriteLine();

        foreach (var offset in new[] { 2f, 3f, 4f, 5f, 6f, 8f })
        {
            Console.Write($"    {offset,4:F1} m |");
            foreach (var type in types)
            {
                var (crossing, seconds, _) = JamAndRoute(type, offset);
                var route = crossing is not { } z ? "-" : z > offset * 0.5f ? "D" : "J";
                Console.Write(
                    $" {route} {(seconds < 0f ? "  never   " : $"{seconds,6:F1}s   "),-12} |");
            }

            Console.WriteLine();
        }

        Console.WriteLine();
        Console.WriteLine(
            "  A body's own speed cancels out of every leg of travel, so a row where the columns");
        Console.WriteLine(
            "  disagree is the queue being valued differently and nothing else.");

        if (Failures.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine(
                "  Never arrived — a large body cannot reach a goal a crowd of small ones is");
            Console.WriteLine(
                "  standing on. Unrelated to anything above: it reproduces with the scaling off.");
            foreach (var failure in Failures) Console.WriteLine(failure);
        }

        return 0;
    }

    /// <summary>
    /// Jams the near gap with a real queue, then sends one body of this type through the choice.
    /// </summary>
    /// <remarks>
    /// A fresh world per unit type, so the test body never interacts with another test body and the
    /// jam it meets is identical in each run. The queue is made of villagers actually trying to get
    /// through, because the congestion field only accumulates where a body wants to move and is
    /// not moving — a wall of immovable bodies is a wall, not a jam, and deposits nothing.
    /// </remarks>
    private static (float? Crossing, float Seconds, int LiveCells) JamAndRoute(
        UnitType type,
        float detourOffset)
    {
        var world = new SimulationWorld();
        var grid = world.Terrain.Transform;
        var column = grid.Width / 2;
        var nearFrom = grid.Height / 2 - GapCells / 2;
        var detourFrom = nearFrom + (int)(detourOffset / grid.CellSize);

        for (var z = 0; z < grid.Height; z++)
        {
            var open = (z >= nearFrom && z < nearFrom + GapCells) ||
                       (z >= detourFrom && z < detourFrom + GapCells);
            if (open) continue;
            world.Terrain.SetSurface(new GridCell(column, z), TerrainSurface.Impassable);
        }

        world.RebuildTerrainNavigation();
        var wallX = world.Navigation.CellCenter(new GridCell(column, 0)).X;
        var lane = world.Navigation.CellCenter(new GridCell(0, grid.Height / 2)).Y;
        var goal = new Vector2(wallX + 6f, lane);

        // The queue: enough bodies to saturate a 3 m gap and keep it saturated.
        var crowd = new List<AgentId>();
        for (var row = 0; row < 6; row++)
        for (var column2 = 0; column2 < 5; column2++)
        {
            crowd.Add(world.SpawnAgent(
                new Vector2(wallX - 1.2f - column2 * 0.85f, lane - 2f + row * 0.85f),
                UnitType.Villager));
        }

        world.QueueMove(crowd, goal);

        // Let the jam form and register before anything is asked to route around it.
        for (var tick = 0; tick < 150; tick++) world.Tick(Step);
        var liveCells = world.Congestion.LiveCellCount;

        var subject = world.SpawnAgent(new Vector2(wallX - 11f, lane), type);
        world.QueueMove(new[] { subject }, goal);

        float? crossing = null;
        var arrived = -1f;
        for (var tick = 0; tick < 2400; tick++)
        {
            world.Tick(Step);
            ref readonly var agent = ref world.Agents.Get(subject);
            if (crossing is null && agent.Position.X > wallX + 0.5f) crossing = agent.Position.Y - lane;
            if (arrived < 0f && !agent.HasDestination)
            {
                arrived = (tick + 1) * Step;
                break;
            }
        }

        if (arrived < 0f)
        {
            ref readonly var stalled = ref world.Agents.Get(subject);
            // Buffered rather than printed, so one body failing does not tear the table apart.
            Failures.Add(
                $"    {type.Name} at a {detourOffset:F0} m detour: stopped " +
                $"{Vector2.Distance(stalled.Position, goal):F2} m short of the goal, doing " +
                $"{stalled.Velocity.Length():F2} of {stalled.MaximumSpeed:F2} m/s, stuck for " +
                $"{stalled.StuckSeconds:F1} s");
        }

        return (crossing, arrived, liveCells);
    }
}

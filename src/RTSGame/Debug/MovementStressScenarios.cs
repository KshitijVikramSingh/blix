using System.Numerics;
using RTSGame.Simulation;
using RTSGame.Simulation.Agents;
using RTSGame.Simulation.Spatial;

namespace RTSGame.Debug;

internal static class MovementStressScenarios
{
    internal readonly record struct PenEscapeScenario(
        AgentId[] Agents,
        Vector2[] ExitCenters,
        Vector2 PenMinimum,
        Vector2 PenMaximum,
        Vector2 Target,
        AgentId? PrimaryBlocker);

    public static AgentId[] Populate(SimulationWorld world, int count, bool issueGroupMove)
    {
        var columns = count == 30 ? 6 : (int)MathF.Ceiling(MathF.Sqrt(count * 1.15f));
        var rows = (int)MathF.Ceiling(count / (float)columns);
        var radius = count >= 200 ? AgentDefaults.CrowdRadius : AgentDefaults.Radius;
        var spacing = count == 30 ? 1.22f : count <= 50 ? 1.05f : 0.72f;
        var centerX = count == 30 ? 0f : 4f;
        var ids = new AgentId[count];
        var spawned = 0;

        for (var row = 0; row < rows && spawned < count; row++)
        for (var column = 0; column < columns && spawned < count; column++)
        {
            var position = new Vector2(
                centerX + (column - (columns - 1) * 0.5f) * spacing,
                (row - (rows - 1) * 0.5f) * spacing);
            ids[spawned++] = world.SpawnAgent(position, radius: radius);
        }

        if (issueGroupMove) world.QueueMove(ids, new Vector2(-4f, 0f));
        return ids;
    }

    public static PenEscapeScenario PopulatePenEscape(
        SimulationWorld world,
        int count = 30,
        bool issueGroupMove = true,
        int alternateOpeningCount = 3,
        int seed = 17,
        bool blockPrimary = false)
    {
        var walls = new HashSet<GridCell>();
        // The pen was written as placement-cell indices against the 20x20 grid of the tuned
        // 30 m world, where cells 5..14 happen to straddle the origin. On the 400x400 grid of
        // a 600 m world the same indices are fifteen metres from the corner, so the pen sat
        // at the edge of the map while the crowd spawned in the middle of it — visible as a
        // small walled square in the far corner and nothing to escape from anywhere near the
        // units. Offsetting by the grid's own centre puts it back around the origin at any
        // extent, and is exactly zero on the world every threshold here was tuned against.
        var centreX = (world.Placement.Transform.Width - 20) / 2;
        var centreZ = (world.Placement.Transform.Height - 20) / 2;
        var left = 6 + centreX;
        var right = 14 + centreX;
        var bottom = 5 + centreZ;
        var top = 14 + centreZ;
        // The obvious/main opening deliberately points away from the north-east
        // target. Every successful route must first leave in the wrong direction
        // and then wrap around the pen.
        var primaryOpening = new GridCell(left, 7);
        var alternateCandidates = new List<GridCell>();
        for (var z = bottom + 2; z <= top - 2; z++) alternateCandidates.Add(new GridCell(left, z));
        for (var x = left + 2; x <= right - 2; x++) alternateCandidates.Add(new GridCell(x, bottom));
        for (var z = bottom + 2; z <= bottom + 4; z++) alternateCandidates.Add(new GridCell(right, z));
        for (var x = left + 2; x <= left + 4; x++) alternateCandidates.Add(new GridCell(x, top));
        var random = new Random(seed);
        for (var i = alternateCandidates.Count - 1; i > 0; i--)
        {
            var swap = random.Next(i + 1);
            (alternateCandidates[i], alternateCandidates[swap]) =
                (alternateCandidates[swap], alternateCandidates[i]);
        }
        var openings = new List<GridCell> { primaryOpening };
        foreach (var candidate in alternateCandidates)
        {
            if (openings.Count > alternateOpeningCount) break;
            if (openings.Any(existing =>
                    Math.Abs(existing.X - candidate.X) + Math.Abs(existing.Z - candidate.Z) <= 1))
            {
                continue;
            }
            openings.Add(candidate);
        }

        for (var x = left; x <= right; x++)
        {
            walls.Add(new GridCell(x, bottom));
            walls.Add(new GridCell(x, top));
        }
        for (var z = bottom + 1; z < top; z++)
        {
            walls.Add(new GridCell(left, z));
            walls.Add(new GridCell(right, z));
        }
        foreach (var opening in openings) walls.Remove(opening);
        foreach (var wall in walls)
        {
            world.QueueToggleObstacle(world.Placement.Transform.CellCenter(wall));
        }
        world.Tick((float)SimulationWorld.FixedDeltaSeconds);

        var columns = (int)MathF.Ceiling(MathF.Sqrt(count * 1.15f));
        var rows = (int)MathF.Ceiling(count / (float)columns);
        const float spacing = 1.05f;
        var ids = new AgentId[count];
        var spawned = 0;
        for (var row = 0; row < rows && spawned < count; row++)
        for (var column = 0; column < columns && spawned < count; column++)
        {
            ids[spawned++] = world.SpawnAgent(new Vector2(
                0.75f + (column - (columns - 1) * 0.5f) * spacing,
                0f + (row - (rows - 1) * 0.5f) * spacing));
        }

        AgentId? primaryBlocker = null;
        if (blockPrimary)
        {
            primaryBlocker = world.SpawnAgent(
                world.Placement.Transform.CellCenter(primaryOpening),
                maximumSpeed: 0f);
        }

        var target = new Vector2(12.5f, 12.5f);
        if (issueGroupMove) world.QueueMove(ids, target);
        return new PenEscapeScenario(
            ids,
            openings.Select(world.Placement.Transform.CellCenter).ToArray(),
            new Vector2(-6f, -7.5f),
            new Vector2(7.5f, 7.5f),
            target,
            primaryBlocker);
    }
}

using System.Numerics;
using RTSGame.Simulation.Agents;

namespace RTSGame.Simulation.Spatial;

/// <summary>
/// Uniform bucket grid over agent bodies only, rebuilt from the agent array
/// rather than from collider proxies. The collider hash carries four proxies per
/// agent and rebuilds in full whenever any one of them moves, which makes it the
/// wrong structure for the per-tick neighbour sweeps in steering and contact
/// resolution.
/// </summary>
/// <remarks>
/// Query results are deterministic without sorting: buckets are filled in agent
/// index order and cells are visited in a fixed (z, x) order.
/// </remarks>
internal sealed class AgentSpatialIndex
{
    private readonly float cellSize;
    private readonly Dictionary<(int X, int Z), List<int>> buckets = new();

    public AgentSpatialIndex(float cellSize)
    {
        if (cellSize <= 0f) throw new ArgumentOutOfRangeException(nameof(cellSize));
        this.cellSize = cellSize;
    }

    public void Rebuild(ReadOnlySpan<AgentState> agents)
    {
        foreach (var bucket in buckets.Values) bucket.Clear();
        for (var index = 0; index < agents.Length; index++)
        {
            // Removed slots never enter the index, which is what keeps every
            // neighbour sweep built on it — avoidance and contact resolution
            // both — from seeing bodies that are no longer in the world.
            if (!agents[index].IsAlive) continue;
            var key = Bucket(agents[index].Position);
            if (!buckets.TryGetValue(key, out var values)) buckets[key] = values = new List<int>();
            values.Add(index);
        }
    }

    /// <summary>
    /// Collects the indices of every agent whose bucket overlaps the query disc.
    /// This is a broad phase: callers still test the exact distance.
    /// </summary>
    public void Query(Vector2 center, float radius, int excludedIndex, List<int> results)
    {
        results.Clear();
        var minimum = Bucket(center - new Vector2(radius));
        var maximum = Bucket(center + new Vector2(radius));
        for (var z = minimum.Z; z <= maximum.Z; z++)
        for (var x = minimum.X; x <= maximum.X; x++)
        {
            if (!buckets.TryGetValue((x, z), out var values)) continue;
            foreach (var index in values)
            {
                if (index != excludedIndex) results.Add(index);
            }
        }
    }

    private (int X, int Z) Bucket(Vector2 point) =>
        ((int)MathF.Floor(point.X / cellSize), (int)MathF.Floor(point.Y / cellSize));
}

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
/// Buckets are a dense array over the world rather than a dictionary. The map is
/// bounded and small, so the whole grid is a few hundred lists — while a hashed
/// grid charges a tuple hash and a probe for every insert and, worse, for every
/// cell of every query. This structure is swept several times per tick (once by
/// the velocity solve, once per relaxation pass of the contact solve) and each
/// sweep visits tens of cells per body, so that constant was the largest single
/// cost left in the movement phases.
/// <para>
/// Query results are deterministic without sorting: buckets are filled in agent
/// index order and cells are visited in a fixed (z, x) order. Positions outside
/// the grid are clamped into the edge buckets, which can only ever widen the
/// broad phase — every caller re-tests exact distance — and cannot happen for a
/// body the simulation has constrained to the terrain.
/// </para>
/// </remarks>
internal sealed class AgentSpatialIndex
{
    private readonly float cellSize;
    private readonly Vector2 origin;
    private readonly int width;
    private readonly int height;
    private readonly List<int>[] buckets;

    public AgentSpatialIndex(float cellSize, Vector2 minimum, Vector2 maximum)
    {
        if (cellSize <= 0f) throw new ArgumentOutOfRangeException(nameof(cellSize));
        this.cellSize = cellSize;
        origin = minimum;
        var extent = maximum - minimum;
        width = Math.Max(1, (int)MathF.Ceiling(extent.X / cellSize));
        height = Math.Max(1, (int)MathF.Ceiling(extent.Y / cellSize));
        buckets = new List<int>[width * height];
        for (var i = 0; i < buckets.Length; i++) buckets[i] = new List<int>();
    }

    public void Rebuild(ReadOnlySpan<AgentState> agents)
    {
        foreach (var bucket in buckets) bucket.Clear();
        for (var index = 0; index < agents.Length; index++)
        {
            // Removed slots never enter the index, which is what keeps every
            // neighbour sweep built on it — avoidance and contact resolution
            // both — from seeing bodies that are no longer in the world.
            if (!agents[index].IsAlive) continue;
            var (x, z) = Bucket(agents[index].Position);
            buckets[z * width + x].Add(index);
        }
    }

    /// <summary>
    /// Collects the indices of every agent whose bucket overlaps the query disc.
    /// This is a broad phase: callers still test the exact distance.
    /// </summary>
    public void Query(Vector2 center, float radius, int excludedIndex, List<int> results)
    {
        results.Clear();
        var (minimumX, minimumZ) = Bucket(center - new Vector2(radius));
        var (maximumX, maximumZ) = Bucket(center + new Vector2(radius));
        for (var z = minimumZ; z <= maximumZ; z++)
        {
            var row = z * width;
            for (var x = minimumX; x <= maximumX; x++)
            {
                foreach (var index in buckets[row + x])
                {
                    if (index != excludedIndex) results.Add(index);
                }
            }
        }
    }

    private (int X, int Z) Bucket(Vector2 point)
    {
        var local = (point - origin) / cellSize;
        return (
            Math.Clamp((int)MathF.Floor(local.X), 0, width - 1),
            Math.Clamp((int)MathF.Floor(local.Y), 0, height - 1));
    }
}

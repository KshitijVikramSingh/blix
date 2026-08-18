using System.Diagnostics;
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
    // Null means empty. The grid covers the whole map, so on a kilometre world all
    // but a few thousand of these are permanently empty and allocating a list for
    // each of them costs both the allocation and, far worse, a cache miss per bucket
    // on every sweep that clears them.
    private readonly List<int>?[] buckets;
    // Buckets that were written by the last rebuild, so the next one can clear those
    // and only those.
    private int[] occupied = Array.Empty<int>();
    private int occupiedCount;

    public AgentSpatialIndex(float cellSize, Vector2 minimum, Vector2 maximum)
    {
        if (cellSize <= 0f) throw new ArgumentOutOfRangeException(nameof(cellSize));
        this.cellSize = cellSize;
        origin = minimum;
        var extent = maximum - minimum;
        width = Math.Max(1, (int)MathF.Ceiling(extent.X / cellSize));
        height = Math.Max(1, (int)MathF.Ceiling(extent.Y / cellSize));
        buckets = new List<int>?[width * height];
    }

    /// <summary>Stopwatch ticks spent rebuilding since the last reset.</summary>
    /// <remarks>
    /// Measured here rather than at the call sites because the rebuild happens once in
    /// the velocity solve and once per relaxation pass of the contact solve, so its cost
    /// is spread across two phases that both look like steering work. It scales with the
    /// world, not with the crowd — the clear is over every bucket — which is invisible in
    /// a 30 m square and is the whole question on a kilometre one.
    /// </remarks>
    public long RebuildTicks { get; private set; }
    /// <summary>Rebuilds since the last reset.</summary>
    public int Rebuilds { get; private set; }
    /// <summary>Buckets swept by every rebuild, whether or not anything is in them.</summary>
    public int BucketCount => buckets.Length;

    public void ResetRebuildCounters()
    {
        RebuildTicks = 0;
        Rebuilds = 0;
    }

    /// <summary>
    /// Refills the index from the agent array. Every bucket written by the previous
    /// rebuild is emptied; the rest are already empty and are not touched.
    /// </summary>
    /// <remarks>
    /// This used to clear all of them, which is the obvious thing to do on a grid of
    /// four hundred and the dominant per-tick cost on a grid of six hundred and forty
    /// thousand — the sweep is done once by the velocity solve and once per relaxation
    /// pass of the contact solve, and it scales with the map rather than the crowd, so
    /// a hundred idle villagers on a kilometre of empty ground paid the same price as a
    /// full battle. Occupied buckets are bounded by the number of bodies, so the clear
    /// is now bounded by the crowd like everything else in the tick.
    /// <para>
    /// Deliberately not a fit to the agents' bounding box, which was the other
    /// candidate: the box is only small while the army is in one place, and units
    /// spread across their own territory is the normal state of this game rather than
    /// an edge case. Bucket assignment, sweep order and query results are all unchanged
    /// by this — it only stops visiting cells that were already empty.
    /// </para>
    /// </remarks>
    public void Rebuild(ReadOnlySpan<AgentState> agents)
    {
        var start = Stopwatch.GetTimestamp();
        for (var i = 0; i < occupiedCount; i++) buckets[occupied[i]]!.Clear();
        occupiedCount = 0;
        if (occupied.Length < agents.Length) Array.Resize(ref occupied, agents.Length);

        for (var index = 0; index < agents.Length; index++)
        {
            // Removed slots never enter the index, which is what keeps every
            // neighbour sweep built on it — avoidance and contact resolution
            // both — from seeing bodies that are no longer in the world.
            if (!agents[index].IsAlive) continue;
            var (x, z) = Bucket(agents[index].Position);
            var bucket = z * width + x;
            var contents = buckets[bucket];
            if (contents is null)
            {
                buckets[bucket] = contents = new List<int>();
            }

            if (contents.Count == 0) occupied[occupiedCount++] = bucket;
            contents.Add(index);
        }

        Rebuilds++;
        RebuildTicks += Stopwatch.GetTimestamp() - start;
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
                var bucket = buckets[row + x];
                if (bucket is null) continue;
                foreach (var index in bucket)
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

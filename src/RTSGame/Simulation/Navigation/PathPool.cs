using System.Numerics;
using RTSGame.Simulation.Persistence;

namespace RTSGame.Simulation.Navigation;

internal readonly record struct PathHandle(int Value)
{
    public static PathHandle None => new(-1);

    public bool IsValid => Value >= 0;
}

internal sealed class PathPool
{
    private readonly List<Vector2[]?> paths = new();
    // Distance from each waypoint to the end of the route, computed once. Walking
    // the polyline every tick to answer "how far is left" is O(waypoints) per
    // agent per tick, and a smoothed route on sculpted terrain runs to dozens of
    // points — it was the single most expensive thing in the recovery phase.
    private readonly List<float[]?> suffixLengths = new();
    private readonly Stack<int> freeHandles = new();

    public PathHandle Add(Vector2[] points)
    {
        if (points.Length == 0) throw new ArgumentException("A path needs at least one waypoint.", nameof(points));
        var suffix = SuffixesOf(points);

        if (freeHandles.TryPop(out var reused))
        {
            paths[reused] = points;
            suffixLengths[reused] = suffix;
            return new PathHandle(reused);
        }

        paths.Add(points);
        suffixLengths.Add(suffix);
        return new PathHandle(paths.Count - 1);
    }

    /// <summary>Distance from each waypoint to the end, as one pass backwards.</summary>
    private static float[] SuffixesOf(Vector2[] points)
    {
        var suffix = new float[points.Length];
        for (var i = points.Length - 2; i >= 0; i--)
        {
            suffix[i] = suffix[i + 1] + Vector2.Distance(points[i], points[i + 1]);
        }

        return suffix;
    }

    /// <summary>Route distance remaining from <paramref name="waypointIndex"/> onward.</summary>
    public float SuffixLength(PathHandle handle, int waypointIndex)
    {
        if (!handle.IsValid || handle.Value >= suffixLengths.Count ||
            suffixLengths[handle.Value] is not { } suffix)
        {
            return 0f;
        }
        return waypointIndex >= 0 && waypointIndex < suffix.Length ? suffix[waypointIndex] : 0f;
    }

    public ReadOnlySpan<Vector2> Get(PathHandle handle)
    {
        if (!handle.IsValid || handle.Value >= paths.Count || paths[handle.Value] is not { } path)
        {
            throw new ArgumentOutOfRangeException(nameof(handle), "Path handle is no longer valid.");
        }
        return path;
    }

    /// <summary>
    /// The stored routes, and the order freed handles will be handed back out in.
    /// </summary>
    /// <remarks>
    /// The free list is saved even though nothing reads it directly, and this is the clearest example
    /// of why a save manifest is a superset of what the determinism fingerprint reads. The
    /// fingerprint's argument for skipping it is sound — a divergence in the free list can only
    /// matter by handing out a different handle, and handles live on bodies, which are read — but
    /// that argument is about <em>detecting</em> a difference. Reproducing the future needs the list
    /// itself, or the first route planned after a load takes a different handle and the two worlds
    /// part company on the next tick.
    /// <para>
    /// Suffix lengths are not saved. They are a prefix sum over the points, computed once for exactly
    /// the reason they are not stored twice.
    /// </para>
    /// </remarks>
    internal void Write(WorldWriter writer)
    {
        writer.Int(paths.Count);
        foreach (var path in paths)
        {
            if (path is null) writer.Int(-1);
            else writer.Blob<Vector2>(path);
        }

        // Stack.ToArray hands them back top first, which is the order they will be popped in.
        writer.Blob<int>(freeHandles.ToArray());
    }

    internal void Read(WorldReader reader)
    {
        paths.Clear();
        suffixLengths.Clear();
        freeHandles.Clear();
        var count = reader.Int();
        for (var i = 0; i < count; i++)
        {
            var length = reader.Int();
            if (length < 0)
            {
                paths.Add(null);
                suffixLengths.Add(null);
                continue;
            }

            var points = new Vector2[length];
            for (var point = 0; point < length; point++) points[point] = reader.Vector();
            paths.Add(points);
            suffixLengths.Add(SuffixesOf(points));
        }

        var free = reader.Blob<int>();
        // Pushed in reverse so the first one popped is the one that was on top when it was saved.
        for (var i = free.Length - 1; i >= 0; i--) freeHandles.Push(free[i]);
    }

    public void Release(PathHandle handle)
    {
        if (!handle.IsValid || handle.Value >= paths.Count || paths[handle.Value] is null) return;
        paths[handle.Value] = null;
        suffixLengths[handle.Value] = null;
        freeHandles.Push(handle.Value);
    }
}

using System.Numerics;

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
        var suffix = new float[points.Length];
        for (var i = points.Length - 2; i >= 0; i--)
        {
            suffix[i] = suffix[i + 1] + Vector2.Distance(points[i], points[i + 1]);
        }

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

    public void Release(PathHandle handle)
    {
        if (!handle.IsValid || handle.Value >= paths.Count || paths[handle.Value] is null) return;
        paths[handle.Value] = null;
        suffixLengths[handle.Value] = null;
        freeHandles.Push(handle.Value);
    }
}

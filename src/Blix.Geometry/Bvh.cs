using System.Numerics;
using System.Runtime.InteropServices;

namespace Blix.Geometry;

/// <summary>One node of a bounding volume hierarchy, in the 32-byte layout a GPU traverses unchanged.</summary>
/// <remarks>
/// An interior node's children are a pair, <c>Index</c> and <c>Index + 1</c>, so one integer names both. A leaf
/// (<c>Count</c> &gt; 0) names <c>Count</c> primitives starting at <c>Index</c> in its hierarchy's primitive order.
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
public readonly record struct BvhNode(Vector3 Min, uint Index, Vector3 Max, uint Count)
{
    public const int SizeInBytes = 32;

    public bool IsLeaf => Count > 0;

    public Bounds3 Bounds => new(Min, Max);
}

/// <summary>Builds a hierarchy over boxes by the surface-area heuristic, binned.</summary>
/// <remarks>
/// <para>
/// The primitives are whatever the boxes bound: triangles for a mesh's hierarchy (<see cref="TriangleBvh"/>),
/// placements for the one over a scene (<see cref="RayQueryScene"/>). The result is the nodes, root first, and
/// the order the primitives were rearranged into, which a leaf's range indexes.
/// </para>
/// <para>
/// Each split is the cheapest of <see cref="Bins"/> candidate planes per axis over the centroids, by the
/// expected cost of tracing a ray through the two halves (each half's surface area times its count). A node
/// becomes a leaf when that split costs no less than testing everything in it and it is small enough to be
/// one, or when its centroids coincide and no plane can separate them, in which case it is halved by count
/// above <paramref name="maxLeafSize"/> so that no leaf grows without bound.
/// </para>
/// </remarks>
public static class BvhBuilder
{
    public const int Bins = 16;

    /// <summary>What visiting a node costs, in triangle tests: the price a split pays for its two boxes.</summary>
    /// <remarks>
    /// Without it a split always looks cheaper than a leaf (two halves' area times count is never more than the
    /// whole's), so every leaf ends at one primitive and the nodes double: measured on Sponza, 25.2M nodes for
    /// 12.8M triangles.
    /// </remarks>
    public const float TraversalCost = 1f;

    public static (BvhNode[] Nodes, int[] Order) Build(ReadOnlySpan<Bounds3> boxes, int maxLeafSize)
    {
        if (maxLeafSize < 1) throw new ArgumentOutOfRangeException(nameof(maxLeafSize), "a leaf holds at least one primitive.");
        var count = boxes.Length;
        var order = new int[count];
        for (var i = 0; i < count; i++) order[i] = i;
        // Nothing to bound: one empty leaf, which the traversals refuse before visiting.
        if (count == 0) return (new[] { new BvhNode(Vector3.Zero, 0, Vector3.Zero, 0) }, order);

        var centroids = new Vector3[count];
        for (var i = 0; i < count; i++) centroids[i] = (boxes[i].Min + boxes[i].Max) * 0.5f;

        var nodes = new List<BvhNode>(Math.Max(1, 2 * count / maxLeafSize));
        nodes.Add(default);
        var pending = new Stack<(int Node, int Start, int Count)>();
        pending.Push((0, 0, count));
        Span<Bounds3> binBounds = stackalloc Bounds3[Bins];
        Span<int> binCounts = stackalloc int[Bins];
        Span<float> rightArea = stackalloc float[Bins];
        Span<int> rightCount = stackalloc int[Bins];

        while (pending.Count > 0)
        {
            var (node, start, n) = pending.Pop();
            var bounds = Empty;
            var centroidBounds = Empty;
            for (var i = start; i < start + n; i++)
            {
                bounds = Union(bounds, boxes[order[i]]);
                centroidBounds = Union(centroidBounds, centroids[order[i]]);
            }

            var leafCost = Area(bounds) * n;
            var bestCost = float.MaxValue;
            var bestAxis = -1;
            var bestSplit = 0;
            var extent = centroidBounds.Max - centroidBounds.Min;
            for (var axis = 0; axis < 3 && n > 1; axis++)
            {
                var lo = Component(centroidBounds.Min, axis);
                var span = Component(extent, axis);
                if (span <= 0f) continue;
                binBounds.Fill(Empty);
                binCounts.Clear();
                var scale = Bins / span;
                for (var i = start; i < start + n; i++)
                {
                    var b = Math.Min(Bins - 1, (int)((Component(centroids[order[i]], axis) - lo) * scale));
                    binCounts[b]++;
                    binBounds[b] = Union(binBounds[b], boxes[order[i]]);
                }

                // Right-hand sums from the top, then sweep the left side up against them.
                var acc = Empty;
                var accCount = 0;
                for (var b = Bins - 1; b > 0; b--)
                {
                    acc = Union(acc, binBounds[b]);
                    accCount += binCounts[b];
                    rightArea[b] = accCount > 0 ? Area(acc) : 0f;
                    rightCount[b] = accCount;
                }
                acc = Empty;
                accCount = 0;
                for (var b = 0; b < Bins - 1; b++)
                {
                    acc = Union(acc, binBounds[b]);
                    accCount += binCounts[b];
                    if (accCount == 0 || rightCount[b + 1] == 0) continue;
                    var cost = Area(acc) * accCount + rightArea[b + 1] * rightCount[b + 1];
                    if (cost < bestCost) { bestCost = cost; bestAxis = axis; bestSplit = b + 1; }
                }
            }

            int mid;
            if (bestAxis >= 0 && (TraversalCost * Area(bounds) + bestCost < leafCost || n > maxLeafSize))
            {
                var lo = Component(centroidBounds.Min, bestAxis);
                var scale = Bins / Component(extent, bestAxis);
                mid = Partition(order, start, n, i => Math.Min(Bins - 1, (int)((Component(centroids[i], bestAxis) - lo) * scale)) < bestSplit);
                // The binning that chose the split put primitives on both sides; recomputed, it must again.
                if (mid == start || mid == start + n) mid = start + n / 2;
            }
            else if (n > maxLeafSize)
            {
                // Centroids coincide: nothing separates them, so split the run in half rather than make one leaf of it.
                mid = start + n / 2;
            }
            else
            {
                nodes[node] = new BvhNode(bounds.Min, (uint)start, bounds.Max, (uint)n);
                continue;
            }

            var left = nodes.Count;
            nodes.Add(default);
            nodes.Add(default);
            nodes[node] = new BvhNode(bounds.Min, (uint)left, bounds.Max, 0);
            pending.Push((left + 1, mid, start + n - mid));
            pending.Push((left, start, mid - start));
        }

        return (nodes.ToArray(), order);
    }

    /// <summary>The expected cost of a ray through this hierarchy, relative to testing every primitive at its root.</summary>
    /// <remarks>
    /// Each node weighs by its surface area over the root's: <see cref="TraversalCost"/> per box visited, and one per
    /// primitive in a leaf. A measure of the build's quality to compare builds by, not a time.
    /// </remarks>
    public static float SahCost(ReadOnlySpan<BvhNode> nodes)
    {
        var rootArea = Area(nodes[0].Bounds);
        if (rootArea <= 0f) return 0f;
        var cost = 0f;
        foreach (var node in nodes)
        {
            var weight = Area(node.Bounds) / rootArea;
            cost += node.IsLeaf ? weight * node.Count : weight * TraversalCost;
        }
        return cost;
    }

    private static int Partition(int[] order, int start, int count, Func<int, bool> goesLeft)
    {
        var i = start;
        var j = start + count - 1;
        while (i <= j)
        {
            if (goesLeft(order[i]))
            {
                i++;
                continue;
            }
            var swap = order[i];
            order[i] = order[j];
            order[j] = swap;
            j--;
        }
        return i;
    }

    internal static readonly Bounds3 Empty = new(new Vector3(float.MaxValue), new Vector3(float.MinValue));

    internal static Bounds3 Union(in Bounds3 a, in Bounds3 b) => new(Vector3.Min(a.Min, b.Min), Vector3.Max(a.Max, b.Max));

    private static Bounds3 Union(in Bounds3 a, Vector3 p) => new(Vector3.Min(a.Min, p), Vector3.Max(a.Max, p));

    internal static float Area(in Bounds3 b)
    {
        var d = Vector3.Max(b.Max - b.Min, Vector3.Zero);
        return 2f * (d.X * d.Y + d.Y * d.Z + d.Z * d.X);
    }

    internal static float Component(Vector3 v, int axis) => axis == 0 ? v.X : axis == 1 ? v.Y : v.Z;
}

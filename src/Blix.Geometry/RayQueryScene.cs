using System.Numerics;

namespace Blix.Geometry;

/// <summary>A mesh as ray queries take it: positions and a triangle list, in the mesh's own space.</summary>
public sealed class RayMesh
{
    public RayMesh(Vector3[] positions, uint[] indices)
    {
        ArgumentNullException.ThrowIfNull(positions);
        ArgumentNullException.ThrowIfNull(indices);
        if (indices.Length % 3 != 0) throw new ArgumentException($"{indices.Length} indices are not whole triangles.", nameof(indices));
        Positions = positions;
        Indices = indices;
    }

    public Vector3[] Positions { get; }
    public uint[] Indices { get; }
    public int TriangleCount => Indices.Length / 3;

    /// <summary>One triangle by index, through the traversal's own test.</summary>
    public bool Hit(in ShearedRay ray, int triangle, float tMin, float tMax, out float t, out Vector2 barycentrics, out bool frontFace) =>
        RayTests.Triangle(ray,
            Positions[Indices[triangle * 3]], Positions[Indices[triangle * 3 + 1]], Positions[Indices[triangle * 3 + 2]],
            tMin, tMax, out t, out barycentrics, out frontFace);

    public Bounds3 Bounds
    {
        get
        {
            var min = new Vector3(float.MaxValue);
            var max = new Vector3(float.MinValue);
            foreach (var p in Positions) { min = Vector3.Min(min, p); max = Vector3.Max(max, p); }
            return new Bounds3(min, max);
        }
    }
}

/// <summary>Meshes placed in a world, under one hierarchy over regions and instances: the top level of a ray query.</summary>
/// <remarks>
/// <para>
/// A placement is a <see cref="RayMesh"/> and a world matrix in the System.Numerics row-vector form (world =
/// Vector3.Transform(local, matrix)). What the hierarchy holds depends on how a mesh is placed:
/// </para>
/// <list type="bullet">
/// <item>A static placement of a mesh placed nowhere else is merged, in world space, into <b>regions</b>: cells of
/// space, each one <see cref="TriangleBvh"/> over the merged triangles whose centres fall in it, halved until it holds
/// at most the given triangle count. Nothing is shared, so merging costs nothing, and a ray no longer descends into
/// every overlapping placement in turn (measured before regions: 18 placements entered per camera ray on Sponza, 12
/// on Bistro).</item>
/// <item>A mesh placed more than once, and anything marked dynamic, stays an <b>instance</b>: one hierarchy per mesh,
/// shared, and a ray carried into the mesh's space by the placement's inverse, direction unnormalised, so a distance
/// found there is the distance in the world.</item>
/// </list>
/// <para>
/// Either way a hit names the placement and the triangle in its own mesh: a region keeps, per triangle, where it
/// came from. This is the CPU reference, the oracle the GPU traversal (ray_query.glsl) is tested against. A
/// placement that moves rebuilds the scene for now; refitting is for when something moves every frame.
/// </para>
/// </remarks>
public sealed class RayQueryScene
{
    public readonly record struct Instance(RayMesh Mesh, Matrix4x4 World, bool Dynamic = false);

    /// <summary>What the top level holds: a region (world space, with owners) or one instanced placement.</summary>
    internal sealed record Entry(TriangleBvh Bvh, Matrix4x4 WorldToLocal, int Placement, int[]? OwnerPlacement, int[]? OwnerTriangle)
    {
        public bool IsRegion => OwnerPlacement is not null;
    }

    public const int DefaultRegionTriangles = 262144;

    /// <summary>Set on a region triangle's owner when its vertices were swapped to undo a mirroring placement.</summary>
    internal const int MirroredOwner = int.MinValue;

    private readonly Instance[] placements;
    private readonly Entry[] entries;

    private RayQueryScene(Instance[] placements, Entry[] entries, BvhNode[] nodes, int[] order)
    {
        this.placements = placements;
        this.entries = entries;
        Nodes = nodes;
        Order = order;
    }

    public IReadOnlyList<Instance> Instances => placements;
    internal IReadOnlyList<Entry> Entries => entries;
    public BvhNode[] Nodes { get; }
    public int[] Order { get; }

    public int RegionCount => entries.Count(e => e.IsRegion);
    public int InstanceEntryCount => entries.Count(e => !e.IsRegion);
    /// <summary>Hierarchy nodes over every distinct region and instanced mesh.</summary>
    public long MeshNodeCount => DistinctBvhs().Sum(b => (long)b.Nodes.Length);
    public long StoredTriangleCount => DistinctBvhs().Sum(b => (long)b.TriangleCount);

    private IEnumerable<TriangleBvh> DistinctBvhs() => entries.Select(e => e.Bvh).Distinct(ReferenceEqualityComparer.Instance).Cast<TriangleBvh>();

    /// <exception cref="ArgumentException">An instanced placement's matrix has no inverse, so a ray cannot be carried into it.</exception>
    public static RayQueryScene Build(IReadOnlyList<Instance> placements, int regionTriangles = DefaultRegionTriangles)
    {
        ArgumentNullException.ThrowIfNull(placements);
        if (regionTriangles < 1) throw new ArgumentOutOfRangeException(nameof(regionTriangles));
        var all = placements.ToArray();
        var uses = new Dictionary<RayMesh, int>(ReferenceEqualityComparer.Instance);
        foreach (var p in all) uses[p.Mesh] = uses.GetValueOrDefault(p.Mesh) + 1;

        var merged = new List<int>();
        var instanced = new List<int>();
        for (var i = 0; i < all.Length; i++)
        {
            if (!all[i].Dynamic && uses[all[i].Mesh] == 1 && all[i].Mesh.TriangleCount > 0) merged.Add(i);
            else instanced.Add(i);
        }

        // Regions are cells of space, not groups of placements: every merged triangle, carried into the world, goes
        // to the cell its centre falls in, and a cell is halved at the middle of its longest side until it holds at
        // most the budget. A large placement crossing cells is shared out between them, so cells barely overlap
        // (grouping whole placements left Sponza's walls and arcades in all 67 regions at once).
        var worldPositions = new Vector3[all.Length][];
        Parallel.ForEach(merged, i =>
        {
            var source = all[i].Mesh.Positions;
            var carried = new Vector3[source.Length];
            for (var v = 0; v < source.Length; v++) carried[v] = Vector3.Transform(source[v], all[i].World);
            worldPositions[i] = carried;
        });
        var triPlacement = new List<int>();
        var triIndex = new List<int>();
        foreach (var i in merged)
        {
            for (var tri = 0; tri < all[i].Mesh.TriangleCount; tri++) { triPlacement.Add(i); triIndex.Add(tri); }
        }
        var centres = new Vector3[triPlacement.Count];
        Parallel.For(0, centres.Length, k =>
        {
            var p = worldPositions[triPlacement[k]];
            var idx = all[triPlacement[k]].Mesh.Indices;
            var t = triIndex[k];
            centres[k] = (p[idx[t * 3]] + p[idx[t * 3 + 1]] + p[idx[t * 3 + 2]]) / 3f;
        });

        var regions = new List<int[]>();
        var pending = new Stack<int[]>();
        if (centres.Length > 0) pending.Push(Enumerable.Range(0, centres.Length).ToArray());
        while (pending.Count > 0)
        {
            var cell = pending.Pop();
            if (cell.Length <= regionTriangles) { regions.Add(cell); continue; }
            var lo = new Vector3(float.MaxValue);
            var hi = new Vector3(float.MinValue);
            foreach (var k in cell) { lo = Vector3.Min(lo, centres[k]); hi = Vector3.Max(hi, centres[k]); }
            var span = hi - lo;
            var axis = span.X >= span.Y && span.X >= span.Z ? 0 : span.Y >= span.Z ? 1 : 2;
            var middle = BvhBuilder.Component(lo + hi, axis) * 0.5f;
            var left = cell.Where(k => BvhBuilder.Component(centres[k], axis) < middle).ToArray();
            var right = cell.Where(k => BvhBuilder.Component(centres[k], axis) >= middle).ToArray();
            if (left.Length == 0 || right.Length == 0)
            {
                // Every centre on one side of the middle (they coincide): halve by count instead.
                left = cell[..(cell.Length / 2)];
                right = cell[(cell.Length / 2)..];
            }
            pending.Push(right);
            pending.Push(left);
        }

        var regionEntries = new Entry[regions.Count];
        Parallel.For(0, regions.Count, r =>
        {
            var cell = regions[r];
            var positions = new List<Vector3>();
            var indices = new uint[cell.Length * 3];
            var ownerPlacement = new int[cell.Length];
            var ownerTriangle = new int[cell.Length];
            // Each vertex once per cell, however many of the cell's triangles share it.
            var remap = new Dictionary<long, uint>();
            for (var n = 0; n < cell.Length; n++)
            {
                var i = triPlacement[cell[n]];
                var tri = triIndex[cell[n]];
                var mesh = all[i].Mesh;
                // A mirroring placement reverses its triangles' winding in the world. Swapping two vertices restores
                // the authored winding, so facing means the mesh's own side here as for an instance; the owner
                // records the swap (high bit) so the barycentrics can be swapped back.
                var mirrored = all[i].World.GetDeterminant() < 0f;
                for (var c = 0; c < 3; c++)
                {
                    var corner = mirrored && c > 0 ? 3 - c : c;
                    var vertex = mesh.Indices[tri * 3 + corner];
                    var key = ((long)i << 32) | vertex;
                    if (!remap.TryGetValue(key, out var at))
                    {
                        at = (uint)positions.Count;
                        positions.Add(worldPositions[i][vertex]);
                        remap[key] = at;
                    }
                    indices[n * 3 + c] = at;
                }
                ownerPlacement[n] = i;
                ownerTriangle[n] = mirrored ? tri | MirroredOwner : tri;
            }
            regionEntries[r] = new Entry(TriangleBvh.Build(positions.ToArray(), indices), Matrix4x4.Identity, -1, ownerPlacement, ownerTriangle);
        });

        var meshBvhs = new Dictionary<RayMesh, TriangleBvh>(ReferenceEqualityComparer.Instance);
        foreach (var i in instanced) meshBvhs[all[i].Mesh] = null!;
        var distinct = meshBvhs.Keys.ToArray();
        var built = new TriangleBvh[distinct.Length];
        Parallel.For(0, distinct.Length, k => built[k] = TriangleBvh.Build(distinct[k].Positions, distinct[k].Indices));
        for (var k = 0; k < distinct.Length; k++) meshBvhs[distinct[k]] = built[k];

        var entries = new List<Entry>(regionEntries);
        foreach (var i in instanced)
        {
            if (!Matrix4x4.Invert(all[i].World, out var inverse))
            {
                throw new ArgumentException($"placement {i}'s world matrix has no inverse.", nameof(placements));
            }
            entries.Add(new Entry(meshBvhs[all[i].Mesh], inverse, i, null, null));
        }

        var boxes = entries.Select(e => e.IsRegion ? e.Bvh.Bounds : WorldBounds(e.Bvh.Bounds, all[e.Placement].World)).ToArray();
        var (nodes, order) = BvhBuilder.Build(boxes, maxLeafSize: 1);
        return new RayQueryScene(all, entries.ToArray(), nodes, order);
    }

    /// <summary>The nearest hit along the ray in (tMin, tMax), if any.</summary>
    public RayHit? Closest(in Ray ray, float tMin = 0f, float tMax = float.PositiveInfinity)
    {
        if (entries.Length == 0) return null;
        var world = new ShearedRay(ray.Origin, ray.Direction);
        var t = tMax;
        var bestEntry = -1;
        var bestTriangle = 0;
        Vector2 bestBary = default;
        var bestFront = false;
        Span<uint> stack = stackalloc uint[64];
        var top = 0;
        stack[top++] = 0;
        while (top > 0)
        {
            var node = Nodes[stack[--top]];
            // Re-tested on the way out: a hit found since it was pushed may have moved t in front of it.
            if (!RayTests.Box(world, node, tMin, t, out _)) continue;
            if (!node.IsLeaf)
            {
                // Nearer child popped first, so the nearest hit is found early and prunes the rest.
                var hitL = RayTests.Box(world, Nodes[node.Index], tMin, t, out var entryL);
                var hitR = RayTests.Box(world, Nodes[node.Index + 1], tMin, t, out var entryR);
                if (hitL && hitR)
                {
                    var nearIsLeft = entryL <= entryR;
                    stack[top++] = nearIsLeft ? node.Index + 1 : node.Index;
                    stack[top++] = nearIsLeft ? node.Index : node.Index + 1;
                }
                else if (hitL) stack[top++] = node.Index;
                else if (hitR) stack[top++] = node.Index + 1;
                continue;
            }
            for (var k = 0; k < node.Count; k++)
            {
                var e = Order[node.Index + k];
                if (!entries[e].Bvh.Closest(Local(ray, e), tMin, t, out var th, out var tri, out var bc, out var front)) continue;
                t = th;
                bestEntry = e;
                bestTriangle = tri;
                bestBary = bc;
                bestFront = front;
            }
        }
        if (bestEntry < 0) return null;
        var hit = entries[bestEntry];
        // Facing is the mesh's own, as hardware ray queries define it: a mirroring placement mirrors the normals with
        // the triangles, so the authored outside stays the outside. A region keeps that by having restored each
        // mirrored triangle's winding when it merged it, which swapped v1 and v2: their barycentrics swap back here.
        if (!hit.IsRegion) return new RayHit(t, hit.Placement, bestTriangle, bestBary, bestFront);
        var owner = hit.OwnerTriangle![bestTriangle];
        return (owner & MirroredOwner) != 0
            ? new RayHit(t, hit.OwnerPlacement![bestTriangle], owner & ~MirroredOwner, new Vector2(bestBary.Y, bestBary.X), bestFront)
            : new RayHit(t, hit.OwnerPlacement![bestTriangle], owner, bestBary, bestFront);
    }

    /// <summary>Whether anything is hit along the ray in (tMin, tMax).</summary>
    public bool Any(in Ray ray, float tMin = 0f, float tMax = float.PositiveInfinity)
    {
        if (entries.Length == 0) return false;
        var world = new ShearedRay(ray.Origin, ray.Direction);
        Span<uint> stack = stackalloc uint[64];
        var top = 0;
        stack[top++] = 0;
        while (top > 0)
        {
            var node = Nodes[stack[--top]];
            if (!RayTests.Box(world, node, tMin, tMax, out _)) continue;
            if (!node.IsLeaf)
            {
                stack[top++] = node.Index + 1;
                stack[top++] = node.Index;
                continue;
            }
            for (var k = 0; k < node.Count; k++)
            {
                var e = Order[node.Index + k];
                if (entries[e].Bvh.Any(Local(ray, e), tMin, tMax)) return true;
            }
        }
        return false;
    }

    private ShearedRay Local(in Ray ray, int entry) => entries[entry].IsRegion
        ? new ShearedRay(ray.Origin, ray.Direction)
        : new ShearedRay(Vector3.Transform(ray.Origin, entries[entry].WorldToLocal), Vector3.TransformNormal(ray.Direction, entries[entry].WorldToLocal));

    /// <summary>The world box around a mesh-space box under a matrix: its eight corners, carried and enclosed.</summary>
    public static Bounds3 WorldBounds(in Bounds3 local, in Matrix4x4 world)
    {
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        for (var c = 0; c < 8; c++)
        {
            var p = Vector3.Transform(new Vector3(
                (c & 1) == 0 ? local.Min.X : local.Max.X,
                (c & 2) == 0 ? local.Min.Y : local.Max.Y,
                (c & 4) == 0 ? local.Min.Z : local.Max.Z), world);
            min = Vector3.Min(min, p);
            max = Vector3.Max(max, p);
        }
        return new Bounds3(min, max);
    }
}

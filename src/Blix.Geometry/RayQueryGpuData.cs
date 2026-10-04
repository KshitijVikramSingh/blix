using System.Numerics;
using System.Runtime.InteropServices;

namespace Blix.Geometry;

/// <summary>A <see cref="RayQueryScene"/> packed into the six storage blocks Blix.Shaders' ray_query.glsl reads.</summary>
/// <remarks>
/// <para>
/// Each array is one block's contents, std430, in the order the shader declares them; the block names are
/// <see cref="BlockNames"/>. The top level's leaves name entries: a region (world space) or one instanced placement.
/// Each distinct hierarchy is packed once, a mesh placed many times included. Indices are rebased so the GPU follows
/// them without offsets: interior nodes name nodes in the shared node array, leaves name triangles in the shared
/// triangle array, triangles name vertices in the shared position array.
/// </para>
/// <para>
/// A triangle's fourth word is its index in its mesh for an instance, and its row in the owner block for a region:
/// the placement it came from and its index in that placement's mesh, high bit set when the merge swapped v1 and v2
/// to undo a mirror. An entry's matrix is world-to-mesh (identity for a region) in System.Numerics' memory order,
/// which a GLSL mat4 reads as the column form the shader multiplies by.
/// </para>
/// </remarks>
public sealed class RayQueryGpuData
{
    public static readonly string[] BlockNames =
        { "BlixRayTopNodes", "BlixRayInstances", "BlixRayNodes", "BlixRayTriangles", "BlixRayPositions", "BlixRayOwners", "BlixRaySurfaces" };

    /// <summary>A surface word before any bake: white, fully covered (albedo sRGB in the low three bytes, coverage on top).</summary>
    public const uint SolidWhite = 0xFFFFFFFFu;

    /// <summary>Marks a region in <see cref="GpuInstance.Placement"/>.</summary>
    public const uint RegionPlacement = 0xFFFFFFFFu;

    [StructLayout(LayoutKind.Sequential)]
    public readonly record struct GpuInstance(Matrix4x4 WorldToLocal, uint Root, uint Placement, uint TriangleBase, uint Pad1)
    {
        public const int SizeInBytes = 80;
    }

    private RayQueryGpuData(BvhNode[] topNodes, GpuInstance[] instances, BvhNode[] nodes, uint[] triangles, Vector4[] positions, uint[] owners,
        Vector2[] uvs, uint[] rowPlacements, (TriangleBvh Bvh, uint TriangleBase)[] distinct)
    {
        Uvs = uvs;
        RowPlacements = rowPlacements;
        this.distinct = distinct;
        TopNodes = topNodes;
        Instances = instances;
        Nodes = nodes;
        Triangles = triangles;
        Positions = positions;
        Owners = owners;
    }

    public BvhNode[] TopNodes { get; }
    /// <summary>One per entry of the top level, in its order: a region, or an instanced placement.</summary>
    public GpuInstance[] Instances { get; }
    public BvhNode[] Nodes { get; }
    /// <summary>Four per triangle, in leaf order: three rebased vertex indices, then its index (instance) or owner row (region).</summary>
    public uint[] Triangles { get; }
    public Vector4[] Positions { get; }
    /// <summary>Two per region triangle: placement, then triangle index with the high bit for a swapped winding.</summary>
    public uint[] Owners { get; }
    /// <summary>One per position: the texture coordinates a surface bake reads (not part of traversal).</summary>
    public Vector2[] Uvs { get; }
    /// <summary>One per triangle row: the placement it came from, which names its material for a bake.</summary>
    public uint[] RowPlacements { get; }

    /// <summary>The row holding a placement's triangle (its index in that placement's mesh): where its surface word is.</summary>
    public int RowOf(int placement, int triangle) => rowsByPlacement[placement][triangle];

    private int[][] rowsByPlacement = Array.Empty<int[]>();

    private readonly (TriangleBvh Bvh, uint TriangleBase)[] distinct;

    /// <summary>Hand baked surface words (one per triangle row) back to the CPU hierarchies as coverage, so the oracle meets the same triangles.</summary>
    public void ApplyCoverage(ReadOnlySpan<uint> surfaces)
    {
        if (surfaces.Length < RowPlacements.Length) throw new ArgumentException($"{surfaces.Length} surfaces for {RowPlacements.Length} triangles.", nameof(surfaces));
        foreach (var (bvh, triangleBase) in distinct)
        {
            if (bvh.TriangleCount == 0) continue;
            var coverage = new byte[bvh.TriangleCount];
            var solid = true;
            for (var k = 0; k < coverage.Length; k++)
            {
                coverage[k] = (byte)(surfaces[(int)triangleBase + k] >> 24);
                solid &= coverage[k] == 255;
            }
            bvh.Coverage = solid ? null : coverage;
        }
    }

    /// <summary>The seven blocks' bytes, in <see cref="BlockNames"/> order. An empty block is one zero word, since a buffer cannot be empty.</summary>
    public byte[][] Blocks() => new[]
    {
        MemoryMarshal.AsBytes(TopNodes.AsSpan()).ToArray(),
        MemoryMarshal.AsBytes(Instances.AsSpan()).ToArray(),
        MemoryMarshal.AsBytes(Nodes.AsSpan()).ToArray(),
        MemoryMarshal.AsBytes(Triangles.AsSpan()).ToArray(),
        MemoryMarshal.AsBytes(Positions.AsSpan()).ToArray(),
        Owners.Length > 0 ? MemoryMarshal.AsBytes(Owners.AsSpan()).ToArray() : new byte[8],
        MemoryMarshal.AsBytes(Enumerable.Repeat(SolidWhite, Math.Max(1, RowPlacements.Length)).ToArray().AsSpan()).ToArray(),
    };

    public long SizeInBytes =>
        (long)TopNodes.Length * BvhNode.SizeInBytes + (long)Instances.Length * GpuInstance.SizeInBytes
        + (long)Nodes.Length * BvhNode.SizeInBytes + Triangles.Length * 4L + Positions.Length * 16L + Owners.Length * 4L + RowPlacements.Length * 4L;

    /// <exception cref="ArgumentException">The scene places nothing: there is no top level to trace.</exception>
    public static RayQueryGpuData Pack(RayQueryScene scene)
    {
        ArgumentNullException.ThrowIfNull(scene);
        if (scene.Entries.Count == 0) throw new ArgumentException("a ray query scene with no placements has nothing to pack.", nameof(scene));

        var roots = new Dictionary<TriangleBvh, uint>(ReferenceEqualityComparer.Instance);
        var nodes = new List<BvhNode>();
        var triangles = new List<uint>();
        var positions = new List<Vector4>();
        var owners = new List<uint>();
        var uvs = new List<Vector2>();
        var rowPlacements = new List<uint>();
        var triangleBases = new Dictionary<TriangleBvh, uint>(ReferenceEqualityComparer.Instance);
        var instances = new GpuInstance[scene.Entries.Count];
        for (var e = 0; e < instances.Length; e++)
        {
            var entry = scene.Entries[e];
            var mesh = entry.Bvh;
            if (!roots.TryGetValue(mesh, out var root))
            {
                root = (uint)nodes.Count;
                var triangleBase = (uint)(triangles.Count / 4);
                var vertexBase = (uint)positions.Count;
                if (mesh.TriangleCount == 0)
                {
                    // No triangles: a leaf of one zero-area triangle, which the triangle test never hits. The empty
                    // node the builder gives would read as an interior node here, where count 0 means one.
                    nodes.Add(new BvhNode(Vector3.Zero, triangleBase, Vector3.Zero, 1));
                    triangles.AddRange(new[] { vertexBase, vertexBase, vertexBase, 0u });
                    positions.Add(new Vector4(0f, 0f, 0f, 1f));
                    uvs.Add(Vector2.Zero);
                    rowPlacements.Add(entry.IsRegion ? 0u : (uint)entry.Placement);
                }
                else
                {
                    foreach (var n in mesh.Nodes)
                    {
                        nodes.Add(n with { Index = n.IsLeaf ? n.Index + triangleBase : n.Index + root });
                    }
                    foreach (var tri in mesh.Order)
                    {
                        triangles.Add(mesh.Indices[tri * 3] + vertexBase);
                        triangles.Add(mesh.Indices[tri * 3 + 1] + vertexBase);
                        triangles.Add(mesh.Indices[tri * 3 + 2] + vertexBase);
                        if (entry.IsRegion)
                        {
                            triangles.Add((uint)(owners.Count / 2));
                            owners.Add((uint)entry.OwnerPlacement![tri]);
                            owners.Add(unchecked((uint)entry.OwnerTriangle![tri]));
                            rowPlacements.Add((uint)entry.OwnerPlacement![tri]);
                        }
                        else
                        {
                            triangles.Add((uint)tri);
                            rowPlacements.Add((uint)entry.Placement);
                        }
                    }
                    foreach (var p in mesh.Positions) positions.Add(new Vector4(p, 1f));
                    for (var v = 0; v < mesh.Positions.Length; v++) uvs.Add(entry.Uvs is { } entryUvs ? entryUvs[v] : Vector2.Zero);
                }
                roots[mesh] = root;
                triangleBases[mesh] = triangleBase;
            }
            instances[e] = new GpuInstance(entry.WorldToLocal, root, entry.IsRegion ? RegionPlacement : (uint)entry.Placement, triangleBases[mesh], 0);
        }

        // Placement and triangle back to row: a region's rows name their owners; an instance's mesh rows are shared by
        // every placement of it, in its hierarchy's leaf order.
        var rowsByPlacement = new int[scene.Instances.Count][];
        for (var p = 0; p < rowsByPlacement.Length; p++) rowsByPlacement[p] = Array.Empty<int>();
        var shared = new Dictionary<TriangleBvh, int[]>(ReferenceEqualityComparer.Instance);
        for (var e = 0; e < scene.Entries.Count; e++)
        {
            var entry = scene.Entries[e];
            var bvh = entry.Bvh;
            var triangleBase = (int)triangleBases[bvh];
            if (entry.IsRegion)
            {
                for (var pos = 0; pos < bvh.TriangleCount; pos++)
                {
                    var placement = entry.OwnerPlacement![bvh.Order[pos]];
                    var triangle = entry.OwnerTriangle![bvh.Order[pos]] & ~RayQueryScene.MirroredOwner;
                    if (rowsByPlacement[placement].Length == 0) rowsByPlacement[placement] = new int[scene.Instances[placement].Mesh.TriangleCount];
                    rowsByPlacement[placement][triangle] = triangleBase + pos;
                }
            }
            else
            {
                if (!shared.TryGetValue(bvh, out var rows))
                {
                    rows = new int[bvh.TriangleCount];
                    for (var pos = 0; pos < bvh.TriangleCount; pos++) rows[bvh.Order[pos]] = triangleBase + pos;
                    shared[bvh] = rows;
                }
                rowsByPlacement[entry.Placement] = rows;
            }
        }

        var top = scene.Nodes.Select(n => n.IsLeaf ? n with { Index = (uint)scene.Order[n.Index] } : n).ToArray();
        return new RayQueryGpuData(top, instances, nodes.ToArray(), triangles.ToArray(), positions.ToArray(), owners.ToArray(),
            uvs.ToArray(), rowPlacements.ToArray(), triangleBases.Select(kv => (kv.Key, kv.Value)).ToArray()) { rowsByPlacement = rowsByPlacement };
    }
}

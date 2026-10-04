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
        { "BlixRayTopNodes", "BlixRayInstances", "BlixRayNodes", "BlixRayTriangles", "BlixRayPositions", "BlixRayOwners" };

    /// <summary>Marks a region in <see cref="GpuInstance.Placement"/>.</summary>
    public const uint RegionPlacement = 0xFFFFFFFFu;

    [StructLayout(LayoutKind.Sequential)]
    public readonly record struct GpuInstance(Matrix4x4 WorldToLocal, uint Root, uint Placement, uint Pad0, uint Pad1)
    {
        public const int SizeInBytes = 80;
    }

    private RayQueryGpuData(BvhNode[] topNodes, GpuInstance[] instances, BvhNode[] nodes, uint[] triangles, Vector4[] positions, uint[] owners)
    {
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

    /// <summary>The six blocks' bytes, in <see cref="BlockNames"/> order. An empty block is one zero word, since a buffer cannot be empty.</summary>
    public byte[][] Blocks() => new[]
    {
        MemoryMarshal.AsBytes(TopNodes.AsSpan()).ToArray(),
        MemoryMarshal.AsBytes(Instances.AsSpan()).ToArray(),
        MemoryMarshal.AsBytes(Nodes.AsSpan()).ToArray(),
        MemoryMarshal.AsBytes(Triangles.AsSpan()).ToArray(),
        MemoryMarshal.AsBytes(Positions.AsSpan()).ToArray(),
        Owners.Length > 0 ? MemoryMarshal.AsBytes(Owners.AsSpan()).ToArray() : new byte[8],
    };

    public long SizeInBytes =>
        (long)TopNodes.Length * BvhNode.SizeInBytes + (long)Instances.Length * GpuInstance.SizeInBytes
        + (long)Nodes.Length * BvhNode.SizeInBytes + Triangles.Length * 4L + Positions.Length * 16L + Owners.Length * 4L;

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
                        }
                        else
                        {
                            triangles.Add((uint)tri);
                        }
                    }
                    foreach (var p in mesh.Positions) positions.Add(new Vector4(p, 1f));
                }
                roots[mesh] = root;
            }
            instances[e] = new GpuInstance(entry.WorldToLocal, root, entry.IsRegion ? RegionPlacement : (uint)entry.Placement, 0, 0);
        }

        var top = scene.Nodes.Select(n => n.IsLeaf ? n with { Index = (uint)scene.Order[n.Index] } : n).ToArray();
        return new RayQueryGpuData(top, instances, nodes.ToArray(), triangles.ToArray(), positions.ToArray(), owners.ToArray());
    }
}

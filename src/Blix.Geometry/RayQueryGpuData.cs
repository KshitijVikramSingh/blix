using System.Numerics;
using System.Runtime.InteropServices;

namespace Blix.Geometry;

/// <summary>A <see cref="RayQueryScene"/> packed into the five storage blocks Blix.Shaders' ray_query.glsl reads.</summary>
/// <remarks>
/// <para>
/// Each array is one block's contents, std430, in the order the shader declares them; the block names are
/// <see cref="BlockNames"/>. A mesh placed many times is packed once. Indices are rebased so the GPU follows them
/// without offsets: a mesh's interior nodes name nodes in the shared node array, its leaves name triangles in the
/// shared triangle array, and its triangles name vertices in the shared position array. The top level's leaves
/// name their placement directly.
/// </para>
/// <para>
/// An instance's matrix is the world-to-mesh inverse in System.Numerics' memory order, which a GLSL mat4 reads as
/// the column form the shader multiplies by (<c>local = m * world</c>), as every model matrix in this engine is fed.
/// </para>
/// </remarks>
public sealed class RayQueryGpuData
{
    public static readonly string[] BlockNames = { "BlixRayTopNodes", "BlixRayInstances", "BlixRayNodes", "BlixRayTriangles", "BlixRayPositions" };

    [StructLayout(LayoutKind.Sequential)]
    public readonly record struct GpuInstance(Matrix4x4 WorldToLocal, uint Root, uint Pad0, uint Pad1, uint Pad2)
    {
        public const int SizeInBytes = 80;
    }

    private RayQueryGpuData(BvhNode[] topNodes, GpuInstance[] instances, BvhNode[] nodes, uint[] triangles, Vector4[] positions)
    {
        TopNodes = topNodes;
        Instances = instances;
        Nodes = nodes;
        Triangles = triangles;
        Positions = positions;
    }

    public BvhNode[] TopNodes { get; }
    public GpuInstance[] Instances { get; }
    public BvhNode[] Nodes { get; }
    /// <summary>Four per triangle, in leaf order: three rebased vertex indices and the triangle's index in its mesh.</summary>
    public uint[] Triangles { get; }
    public Vector4[] Positions { get; }

    /// <summary>The five blocks' bytes, in <see cref="BlockNames"/> order.</summary>
    public byte[][] Blocks() => new[]
    {
        MemoryMarshal.AsBytes(TopNodes.AsSpan()).ToArray(),
        MemoryMarshal.AsBytes(Instances.AsSpan()).ToArray(),
        MemoryMarshal.AsBytes(Nodes.AsSpan()).ToArray(),
        MemoryMarshal.AsBytes(Triangles.AsSpan()).ToArray(),
        MemoryMarshal.AsBytes(Positions.AsSpan()).ToArray(),
    };

    public long SizeInBytes =>
        (long)TopNodes.Length * BvhNode.SizeInBytes + (long)Instances.Length * GpuInstance.SizeInBytes
        + (long)Nodes.Length * BvhNode.SizeInBytes + Triangles.Length * 4L + Positions.Length * 16L;

    /// <exception cref="ArgumentException">The scene places nothing: there is no top level to trace.</exception>
    public static RayQueryGpuData Pack(RayQueryScene scene)
    {
        ArgumentNullException.ThrowIfNull(scene);
        if (scene.Instances.Count == 0) throw new ArgumentException("a ray query scene with no placements has nothing to pack.", nameof(scene));

        var roots = new Dictionary<TriangleBvh, uint>(ReferenceEqualityComparer.Instance);
        var nodes = new List<BvhNode>();
        var triangles = new List<uint>();
        var positions = new List<Vector4>();
        var instances = new GpuInstance[scene.Instances.Count];
        for (var i = 0; i < instances.Length; i++)
        {
            var mesh = scene.Instances[i].Mesh;
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
                    roots[mesh] = root;
                    Matrix4x4.Invert(scene.Instances[i].World, out var emptyInverse);
                    instances[i] = new GpuInstance(emptyInverse, root, 0, 0, 0);
                    continue;
                }
                foreach (var n in mesh.Nodes)
                {
                    nodes.Add(n with { Index = n.IsLeaf ? n.Index + triangleBase : n.Index + root });
                }
                foreach (var tri in mesh.Order)
                {
                    triangles.Add(mesh.Indices[tri * 3] + vertexBase);
                    triangles.Add(mesh.Indices[tri * 3 + 1] + vertexBase);
                    triangles.Add(mesh.Indices[tri * 3 + 2] + vertexBase);
                    triangles.Add((uint)tri);
                }
                foreach (var p in mesh.Positions) positions.Add(new Vector4(p, 1f));
                roots[mesh] = root;
            }
            Matrix4x4.Invert(scene.Instances[i].World, out var inverse);
            instances[i] = new GpuInstance(inverse, root, 0, 0, 0);
        }

        var top = scene.Nodes.Select(n => n.IsLeaf ? n with { Index = (uint)scene.Order[n.Index] } : n).ToArray();
        return new RayQueryGpuData(top, instances, nodes.ToArray(), triangles.ToArray(), positions.ToArray());
    }
}

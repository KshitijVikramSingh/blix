using System.Numerics;
using Blix.Assets;
using Blix.Diagnostics;
using Blix.Geometry;
using Blix.Graphics;
using Blix.Render;

namespace Blix;

/// <summary>A static glTF model resident on a device, its node hierarchy kept.</summary>
/// <remarks>
/// <b>Residency, and nothing about how it is drawn.</b> Each node keeps its name, parent, local and world
/// transforms and world bounds; each primitive is an uploaded <see cref="Mesh"/> with its glTF material
/// and resolved textures. Which pipeline draws it, what colour stands in for a missing material and how
/// many times it is drawn are the renderer's. The vertex layout is what the import produced
/// (<see cref="AssetImportContext"/>), which is how a renderer states the format it reads.
/// <para>
/// For one mesh an instance can place, use <see cref="GltfNodeModel.Merged"/> and <c>CreateMesh</c>
/// instead: this type is the arrangement that keeps the hierarchy, for tools that pick, outline or pose
/// nodes. Made by <see cref="ResidencyExtensions.CreateModel"/>.
/// </para>
/// </remarks>
public sealed class Model : IDisposable
{
    private readonly IGraphicsDevice device;
    private readonly List<Node> nodes = new();
    private readonly List<Part> parts = new();

    private Model(IGraphicsDevice device, string name)
    {
        this.device = device;
        Name = name;
    }

    /// <summary>A node of the hierarchy, drawable or not.</summary>
    /// <param name="Bounds">World-space bounds of its own primitives' vertices; null for a node without any.</param>
    public sealed record Node(
        int Index, string Name, int ParentIndex, Matrix4x4 Local, Matrix4x4 World,
        int PrimitiveCount, int VertexCount, Bounds3? Bounds);

    /// <summary>One uploaded primitive, in its node's local space.</summary>
    public sealed record Part(int NodeIndex, Mesh Mesh, GltfMaterial? Material, MaterialTextures Textures);

    /// <summary>The name its GPU resources were created under.</summary>
    public string Name { get; }

    public IReadOnlyList<Node> Nodes => nodes;

    public IReadOnlyList<Part> Parts => parts;

    /// <summary>World-space bounds over every node that has geometry; zero-sized for a model without any.</summary>
    public Bounds3 Bounds { get; private set; } = new(Vector3.Zero, Vector3.Zero);

    /// <summary>What the pick pass draws for <paramref name="part"/>, placed by <paramref name="placement"/>.</summary>
    public DebugPickGeometry PickGeometry(Part part, Matrix4x4 placement)
    {
        ArgumentNullException.ThrowIfNull(part);
        return new DebugPickGeometry(
            part.Mesh.VertexBuffer, part.Mesh.Layout, part.Mesh.IndexBuffer, 0, part.Mesh.IndexCount, 0,
            nodes[part.NodeIndex].World * placement);
    }

    internal static Model Load(IGraphicsDevice device, GltfNodeModel source, GltfTextureLoader textures, string name)
    {
        var model = new Model(device, name);
        var world = source.WorldTransforms();
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        for (var i = 0; i < source.Nodes.Length; i++)
        {
            var node = source.Nodes[i];
            var nodeMin = new Vector3(float.MaxValue);
            var nodeMax = new Vector3(float.MinValue);
            var vertices = 0;
            foreach (var primitive in node.Primitives)
            {
                vertices += primitive.Mesh.VertexCount;
                Accumulate(primitive.Mesh, world[i], ref nodeMin, ref nodeMax);
                var mesh = device.CreateMesh(primitive.Mesh, $"{name}.{node.Name}.{model.parts.Count}");
                model.parts.Add(new Part(i, mesh, primitive.Material, textures.Load(primitive.Material)));
            }

            var hasMesh = node.Primitives.Length > 0;
            model.nodes.Add(new Node(
                i, node.Name, node.ParentIndex, node.LocalTransform, world[i], node.Primitives.Length, vertices,
                hasMesh ? new Bounds3(nodeMin, nodeMax) : null));
            if (!hasMesh) continue;
            min = Vector3.Min(min, nodeMin);
            max = Vector3.Max(max, nodeMax);
        }

        if (model.parts.Count > 0) model.Bounds = new Bounds3(min, max);
        return model;
    }

    // Bounds from the vertices moved to world space, not the local box transformed: a rotated box's
    // corners overstate the extent, and a tool frames and outlines on this.
    internal static void Accumulate(MeshData mesh, Matrix4x4 transform, ref Vector3 min, ref Vector3 max)
    {
        var stride = mesh.Layout.Stride;
        if (stride < 12) return;
        for (var v = 0; v < mesh.VertexCount; v++)
        {
            var at = v * stride;
            var point = Vector3.Transform(new Vector3(
                BitConverter.ToSingle(mesh.VertexBytes, at),
                BitConverter.ToSingle(mesh.VertexBytes, at + 4),
                BitConverter.ToSingle(mesh.VertexBytes, at + 8)), transform);
            min = Vector3.Min(min, point);
            max = Vector3.Max(max, point);
        }
    }

    /// <summary>Destroys the uploaded buffers. Textures belong to the loader that resolved them.</summary>
    public void Dispose()
    {
        foreach (var part in parts)
        {
            device.DestroyVertexBuffer(part.Mesh.VertexBuffer);
            device.DestroyIndexBuffer(part.Mesh.IndexBuffer);
        }

        parts.Clear();
    }
}

using System.Numerics;
using Blix.Assets;
using Blix.Graphics;
using Blix.Graphics.Vulkan;

namespace Blix.Labs.Toolchain;

/// <summary>
/// An imported glTF, kept as its authored node hierarchy rather than one fused blob.
/// </summary>
/// <remarks>
/// <b>The hierarchy is the point.</b> A fused mesh renders identically and answers none of the questions
/// an asset actually raises — where is this part's pivot, what does the author think "forward" is, why does
/// the turret rotate about a point six centimetres inside the hull. <c>ImportNodes</c> keeps every node in
/// LOCAL space with a parent index, so the composed world transform is a walk up the chain, and that
/// composed translation IS the rig pivot a game would drive.
/// <para>
/// <c>blix-cook inspect</c> already prints those numbers. This exists so they can be drawn next to the
/// thing they describe — the pattern this project has paid for three times (TankArena's tank rig, the CC0
/// sourcing workflow, the RTS villager) by dialling knobs in an overlay until the model sat right.
/// </para>
/// <para>
/// Primitives arrive in <c>VertexPosition3NormalTexture</c> layout, which is what the lab's lit pipeline
/// already takes — so a real asset needed no shader change to appear.
/// </para>
/// </remarks>
public sealed class LabModel : IDisposable
{
    /// <summary>One drawable piece: a node's primitive, with where it sits and what it looks like.</summary>
    public readonly record struct Part(
        int NodeIndex,
        VertexBufferHandle Vertices,
        IndexBufferHandle Indices,
        int IndexCount,
        Vector3 BaseColour,
        float Metallic,
        float Roughness);

    /// <summary>A node of the authored hierarchy, drawable or not.</summary>
    public readonly record struct Node(
        int Index,
        string Name,
        int ParentIndex,
        Matrix4x4 LocalTransform,
        Matrix4x4 WorldTransform,
        int PrimitiveCount,
        int VertexCount,
        Vector3 BoundsMin,
        Vector3 BoundsMax);

    private VulkanGraphicsDevice device = null!;
    private readonly List<Part> parts = new();
    private readonly List<Node> nodes = new();

    public IReadOnlyList<Part> Parts => parts;

    public IReadOnlyList<Node> Nodes => nodes;

    /// <summary>Assembled bounds across every mesh-bearing node, in model space.</summary>
    public Vector3 BoundsMin { get; private set; } = new(float.MaxValue);

    public Vector3 BoundsMax { get; private set; } = new(float.MinValue);

    public string SourcePath { get; private set; } = string.Empty;

    /// <summary>Largest bounds dimension, for framing a camera on an asset of unknown scale.</summary>
    public float LongestExtent
    {
        get
        {
            if (parts.Count == 0) return 1f;
            var size = BoundsMax - BoundsMin;
            return MathF.Max(size.X, MathF.Max(size.Y, size.Z));
        }
    }

    public static LabModel Load(VulkanGraphicsDevice vk, string path)
    {
        var model = new LabModel { device = vk, SourcePath = path };
        var imported = new GltfStaticImporter().ImportNodes(
            new AssetImportContext(AssetId.Parse("lab"), path));

        var source = imported.Nodes;
        var world = new Matrix4x4[source.Length];
        for (var i = 0; i < source.Length; i++)
        {
            // Row-vector compose: child = local * parent. The same walk blix-cook inspect does,
            // and the reason a part's pivot is its composed TRANSLATION rather than its local one.
            world[i] = source[i].LocalTransform;
            for (var p = source[i].ParentIndex; p >= 0; p = source[p].ParentIndex)
            {
                world[i] *= source[p].LocalTransform;
            }
        }

        for (var i = 0; i < source.Length; i++)
        {
            var node = source[i];
            var min = new Vector3(float.MaxValue);
            var max = new Vector3(float.MinValue);
            var vertices = 0;

            foreach (var primitive in node.Primitives)
            {
                var mesh = primitive.Mesh;
                vertices += mesh.VertexCount;
                Accumulate(mesh, world[i], ref min, ref max);

                var name = $"lab.{Path.GetFileNameWithoutExtension(path)}.{node.Name}.{model.parts.Count}";
                var vb = vk.CreateVertexBuffer(
                    new VertexBufferData(
                        new VertexBufferDescription(mesh.Layout, mesh.VertexCount, GraphicsBufferUsage.Static),
                        mesh.VertexBytes),
                    $"{name}.vb");

                var ib = mesh.Indices32 is { } wide
                    ? vk.CreateIndexBuffer(wide, name: $"{name}.ib")
                    : vk.CreateIndexBuffer(mesh.Indices, name: $"{name}.ib");

                var material = primitive.Material;
                model.parts.Add(new Part(
                    NodeIndex: i,
                    Vertices: vb,
                    Indices: ib,
                    IndexCount: mesh.IndexCount,
                    BaseColour: material is null
                        ? new Vector3(0.7f)
                        : new Vector3(
                            material.BaseColorFactor.X, material.BaseColorFactor.Y, material.BaseColorFactor.Z),
                    Metallic: material?.MetallicFactor ?? 0f,
                    Roughness: material?.RoughnessFactor ?? 0.7f));
            }

            var hasMesh = node.Primitives.Length > 0;
            model.nodes.Add(new Node(
                Index: i,
                Name: node.Name,
                ParentIndex: node.ParentIndex,
                LocalTransform: node.LocalTransform,
                WorldTransform: world[i],
                PrimitiveCount: node.Primitives.Length,
                VertexCount: vertices,
                BoundsMin: hasMesh ? min : Vector3.Zero,
                BoundsMax: hasMesh ? max : Vector3.Zero));

            if (!hasMesh) continue;
            model.BoundsMin = Vector3.Min(model.BoundsMin, min);
            model.BoundsMax = Vector3.Max(model.BoundsMax, max);
        }

        if (model.parts.Count == 0)
        {
            model.BoundsMin = Vector3.Zero;
            model.BoundsMax = Vector3.Zero;
        }

        return model;
    }

    // World-space bounds of one primitive. Walks positions rather than trusting an authored
    // bounds field, because an asset that lies about its extents is exactly the sort of thing
    // a lab exists to catch.
    private static void Accumulate(MeshData mesh, Matrix4x4 world, ref Vector3 min, ref Vector3 max)
    {
        var stride = mesh.Layout.Stride;
        if (stride < 12) return;

        for (var v = 0; v < mesh.VertexCount; v++)
        {
            var offset = v * stride;
            var local = new Vector3(
                BitConverter.ToSingle(mesh.VertexBytes, offset),
                BitConverter.ToSingle(mesh.VertexBytes, offset + 4),
                BitConverter.ToSingle(mesh.VertexBytes, offset + 8));
            var point = Vector3.Transform(local, world);
            min = Vector3.Min(min, point);
            max = Vector3.Max(max, point);
        }
    }

    public void Dispose()
    {
        foreach (var part in parts)
        {
            device.DestroyVertexBuffer(part.Vertices);
            device.DestroyIndexBuffer(part.Indices);
        }

        parts.Clear();
        nodes.Clear();
    }
}

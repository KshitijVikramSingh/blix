using System.Numerics;
using Blix;
using Blix.Graphics.Images;
using Blix.Assets;
using Blix.Graphics;
using Blix.Graphics.Vulkan;

namespace Blix.Tools.Studio;

/// <summary>
/// An imported glTF, kept as its authored node hierarchy rather than one fused blob.
/// </summary>
/// <remarks>
/// The hierarchy is inspectable data, not just a route to fused geometry. <c>ImportNodes</c> retains
/// every local transform and parent index so tools can draw and inspect authored pivots and orientation.
/// <para>
/// Primitives use Studio's static lit layout, while node composition remains explicit and available
/// to callers.
/// </para>
/// </remarks>
public sealed class StudioModel : IDisposable
{
    /// <summary>One drawable piece: a node's primitive, with where it sits and what it looks like.</summary>
    public readonly record struct Part(
        int NodeIndex,
        VertexBufferHandle Vertices,
        IndexBufferHandle Indices,
        int IndexCount,
        Vector3 BaseColour,
        float Metallic,
        float Roughness,
        TextureHandle Albedo,
        /// <summary>
        /// What the material says about its own surface: <c>OPAQUE</c>/<c>MASK</c>/<c>BLEND</c>, the
        /// cutout threshold, and whether the back face is part of the model.
        /// </summary>
        /// <remarks>Studio preserves these authored facts even when a particular asset produces no
        /// visible cutout or back-face difference.</remarks>
        /// <summary>Which TEXCOORD set this part's albedo samples — 0 for almost everything.</summary>
        int AlbedoUvSet = 0,
        /// <summary>The material's <c>baseColorFactor.a</c>, which the cutout test multiplies in.</summary>
        float BaseAlpha = 1f,
        GltfAlphaMode AlphaMode = GltfAlphaMode.Opaque,
        float AlphaCutoff = 0.5f,
        bool DoubleSided = false,
        /// <summary>The material's own name, which is often the only colour information a kit ships.</summary>
        /// <remarks>Callers may use the name for application-owned tint policy when an asset carries
        /// no intrinsic base colour.</remarks>
        string MaterialName = "");

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

    /// <summary>One image the asset actually ships, with enough to label it in a panel.</summary>
    public readonly record struct Image(string Name, TextureHandle Texture, int Width, int Height);

    private VulkanGraphicsDevice device = null!;
    private readonly List<TextureHandle> ownedTextures = new();
    private readonly List<Image> images = new();
    // TextureRegistry keys source identity rather than importer object identity.
    private readonly TextureRegistry uploaded = new();
    private TextureHandle white;
    private readonly List<Part> parts = new();
    private readonly List<Node> nodes = new();

    public IReadOnlyList<Part> Parts => parts;

    public IReadOnlyList<Node> Nodes => nodes;

    /// <summary>The distinct base-colour images this asset uploaded. See StudioRig.Images for why.</summary>
    public IReadOnlyList<Image> Images => images;

    /// <summary>Assembled bounds across every mesh-bearing node, in model space.</summary>
    public Vector3 BoundsMin { get; private set; } = new(float.MaxValue);

    public Vector3 BoundsMax { get; private set; } = new(float.MinValue);

    public string SourcePath { get; private set; } = string.Empty;

    /// <summary>How many distinct base-colour textures were uploaded. Zero means every part is untextured.</summary>
    public int TextureCount => uploaded.ResidentCount;

    /// <summary>Parts whose material carries a real base-colour texture rather than the white stand-in.</summary>
    public int TexturedPartCount { get; private set; }

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

    public static StudioModel Load(VulkanGraphicsDevice vk, string path)
    {
        var model = new StudioModel { device = vk, SourcePath = path };

        // A material without a base-colour texture still samples one, so the shader needs no
        // branch: glTF defines the factor as multiplying the texture, and white is the identity.
        model.white = vk.CreateTexture2D(
            new TextureDescription(1, 1, TextureFormat.Rgba8Srgb, SamplerDescription.LinearRepeat),
            new byte[] { 255, 255, 255, 255 }, "lab.white");
        model.ownedTextures.Add(model.white);

        // includeColour: the stage's static pipeline declares the 36-byte layout, so everything
        // drawn on it must carry a colour — and a kit model's COLOR_0 is baked ambient occlusion
        // that was being thrown away on every piece of scatter.
        var imported = new GltfStaticImporter().ImportNodes(
            new AssetImportContext(AssetId.Parse("lab"), path, includeColour: true));

        var source = imported.Nodes;
        var world = new Matrix4x4[source.Length];
        for (var i = 0; i < source.Length; i++)
        {
            // Row-vector compose: child = local * parent. The same walk blix inspect does,
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
                    Roughness: material?.RoughnessFactor ?? 0.7f,
                    Albedo: model.UploadAlbedo(vk, material?.BaseColorTexture),
                    AlbedoUvSet: material?.BaseColorTexCoord ?? 0,
                    BaseAlpha: material?.BaseColorFactor.W ?? 1f,
                    AlphaMode: material?.AlphaMode ?? GltfAlphaMode.Opaque,
                    AlphaCutoff: material?.AlphaCutoff ?? 0.5f,
                    DoubleSided: material?.DoubleSided ?? false,
                    MaterialName: material?.Name ?? string.Empty));

                if (material?.BaseColorTexture is not null) model.TexturedPartCount++;
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

    // Uploads a material's base-colour texture once, however many primitives share it.
    private TextureHandle UploadAlbedo(VulkanGraphicsDevice vk, GltfTexture? texture)
    {
        if (texture is null) return white;

        // Source textures expose eager mips; cooked textures may keep their mip chain behind a lazy
        // disk handle. Upload the authored chain verbatim because BC formats cannot be GPU-blitted
        // to generate missing levels.
        var mips = texture.MipBytes is { Count: > 0 } eager
            ? eager
            : texture.LazyHandle is { } lazy
                ? Enumerable.Range(0, lazy.MipCount).Select(i => BlixTexReader.ReadMip(lazy, i)).ToArray()
                : null;

        if (mips is null || mips.Count == 0)
        {
            Console.WriteLine($"[lab] albedo '{texture.Name}' has no readable mips — drawing white.");
            return white;
        }

        return uploaded.GetOrAdd(texture, texture.Format, () =>
        {
            var description = new TextureDescription(
                texture.Width, texture.Height, texture.Format, SamplerDescription.LinearRepeat);
            var handle = mips.Count > 1
                ? vk.CreateTexture2DMipped(description, mips, $"lab.albedo.{texture.Name}")
                : vk.CreateTexture2D(description, mips[0], $"lab.albedo.{texture.Name}");

            ownedTextures.Add(handle);
            images.Add(new Image(
                string.IsNullOrEmpty(texture.Name) ? $"albedo {images.Count}" : texture.Name,
                handle, texture.Width, texture.Height));
            return handle;
        });
    }

    // World-space bounds of one primitive. Walks positions rather than trusting an authored
    // bounds field, because an asset that lies about its extents is exactly the sort of thing
    // a viewer exists to catch.
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

        foreach (var texture in ownedTextures) device.DestroyTexture(texture);
        ownedTextures.Clear();
        images.Clear();
        uploaded.Clear();
        parts.Clear();
        nodes.Clear();
    }
}

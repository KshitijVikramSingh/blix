using System.Numerics;
using Blix;
using Blix.Assets;
using Blix.Graphics;
using Blix.Render;

namespace Blix.Tools.Studio;

/// <summary>
/// An imported glTF as Studio draws it: the engine's <see cref="Model"/>, its node hierarchy kept, with
/// Studio's policy applied.
/// </summary>
/// <remarks>
/// <b>Policy over residency.</b> Uploading, the hierarchy, world bounds and textures are the engine's
/// (<see cref="Model"/>). This is what Studio decides on top: the 36-byte colour layout its static
/// pipeline reads (the import asks for COLOR_0), the grey a part without a material is drawn in, that
/// cooked textures are realised at load, the images its panel lists, and the names its resources carry.
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

    // The grey a part with no material is drawn in, and the roughness it assumes: Studio's choice.
    private static readonly Vector3 FallbackColour = new(0.7f);
    private const float FallbackRoughness = 0.7f;

    private Model model = null!;
    private GltfTextureLoader textures = null!;
    private readonly List<Part> parts = new();
    private readonly List<Node> nodes = new();
    private readonly List<Image> images = new();

    /// <summary>The engine model this draws.</summary>
    public Model Model => model;

    public IReadOnlyList<Part> Parts => parts;

    public IReadOnlyList<Node> Nodes => nodes;

    /// <summary>The distinct base-colour images this asset uploaded.</summary>
    public IReadOnlyList<Image> Images => images;

    /// <summary>Assembled bounds across every mesh-bearing node, in model space.</summary>
    public Vector3 BoundsMin => model.Bounds.Min;

    public Vector3 BoundsMax => model.Bounds.Max;

    public string SourcePath { get; private set; } = string.Empty;

    /// <summary>How many distinct base-colour textures were uploaded. Zero means every part is untextured.</summary>
    public int TextureCount => images.Count;

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

    public static StudioModel Load(IGraphicsDevice device, string path)
    {
        // includeColour: the stage's static pipeline declares the 36-byte layout, so everything drawn on
        // it must carry a colour, and COLOR_0 is a base-colour multiplier (conventions §6).
        var imported = new GltfStaticImporter().ImportNodes(
            new AssetImportContext(AssetId.Parse("lab"), path, includeColour: true));

        var studio = new StudioModel { SourcePath = path };
        studio.textures = new GltfTextureLoader(device);
        studio.model = device.CreateModel(imported, studio.textures, $"lab.{Path.GetFileNameWithoutExtension(path)}");
        // Realised now, not streamed: an inspector shows the asset as it is from the first frame.
        studio.textures.Drain(double.PositiveInfinity);

        foreach (var p in studio.model.Parts)
        {
            var m = p.Material;
            studio.parts.Add(new Part(
                p.NodeIndex, p.Mesh.VertexBuffer, p.Mesh.IndexBuffer, p.Mesh.IndexCount,
                m is null ? FallbackColour : new Vector3(m.BaseColorFactor.X, m.BaseColorFactor.Y, m.BaseColorFactor.Z),
                m?.MetallicFactor ?? 0f, m?.RoughnessFactor ?? FallbackRoughness, studio.Albedo(m, p.Textures),
                AlbedoUvSet: m?.BaseColorTexCoord ?? 0,
                BaseAlpha: m?.BaseColorFactor.W ?? 1f,
                AlphaMode: m?.AlphaMode ?? GltfAlphaMode.Opaque,
                AlphaCutoff: m?.AlphaCutoff ?? 0.5f,
                DoubleSided: m?.DoubleSided ?? false,
                MaterialName: m?.Name ?? string.Empty));
            if (m?.BaseColorTexture is not null) studio.TexturedPartCount++;
        }

        foreach (var n in studio.model.Nodes)
        {
            studio.nodes.Add(new Node(
                n.Index, n.Name, n.ParentIndex, n.Local, n.World, n.PrimitiveCount, n.VertexCount,
                n.Bounds?.Min ?? Vector3.Zero, n.Bounds?.Max ?? Vector3.Zero));
        }

        return studio;
    }

    // The resolved albedo, and the image it is, listed once for the images panel in the order met.
    private TextureHandle Albedo(GltfMaterial? material, MaterialTextures resolved)
    {
        if (material?.BaseColorTexture is { } texture && images.All(i => i.Texture != resolved.Albedo))
        {
            images.Add(new Image(
                string.IsNullOrEmpty(texture.Name) ? $"albedo {images.Count}" : texture.Name,
                resolved.Albedo, texture.Width, texture.Height));
        }

        return resolved.Albedo;
    }

    public void Dispose()
    {
        model.Dispose();
        textures.Dispose();
        parts.Clear();
        nodes.Clear();
        images.Clear();
    }
}

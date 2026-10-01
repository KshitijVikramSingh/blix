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
/// (<see cref="Model"/>). This is what Studio decides on top: the complete 60-byte static vertex its
/// static pipeline reads (tangent, both UV sets, COLOR_0), the grey a part without a material is drawn in, that
/// cooked textures are realised at load, and the names its resources carry.
/// </remarks>
internal sealed class StudioModel : IDisposable
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
        /// <summary>Everything beyond base colour and alpha: textures, normal, occlusion, emission.</summary>
        StudioSurface Surface,
        /// <summary>Which TEXCOORD set this part's albedo samples — 0 for almost everything.</summary>
        int AlbedoUvSet = 0,
        /// <summary>The material's <c>baseColorFactor.a</c>, which the cutout test multiplies in.</summary>
        float BaseAlpha = 1f,
        AlphaMode AlphaMode = AlphaMode.Opaque,
        float AlphaCutoff = 0.5f,
        bool DoubleSided = false,
        /// <summary>The material's own name, which is often the only colour information a kit ships.</summary>
        /// <remarks>Callers may use the name for application-owned tint policy when an asset carries
        /// no intrinsic base colour.</remarks>
        string MaterialName = "");

    private Model model = null!;
    private MaterialTextureLoader textures = null!;
    private readonly List<Part> parts = new();

    /// <summary>The engine model this draws.</summary>
    public Model Model => model;

    public IReadOnlyList<Part> Parts => parts;

    internal static StudioModel Load(IGraphicsDevice device, string path)
    {
        // Tangents and colour: the stage's static pipeline reads the complete vertex — the cooked tangent
        // frame for normal maps, and COLOR_0 as a base-colour multiplier (conventions §6). Static: a model
        // view draws a rigged file's skinned meshes at their bind pose.
        // Cooked on open: what Studio shows is the cooked asset, which is what the engine draws.
        var data = ModelData.Load(Blix.Recipes.CookCache.Resolve(path), new ModelNeeds(Tangents: true, Colour: true, Skinned: false));

        var studio = new StudioModel();
        studio.textures = new MaterialTextureLoader(device);
        studio.model = device.CreateModel(data, studio.textures, $"lab.{Path.GetFileNameWithoutExtension(path)}");
        // Realised now, not streamed: an inspector shows the asset as it is from the first frame.
        studio.textures.Drain(double.PositiveInfinity);

        foreach (var p in studio.model.Parts)
        {
            var m = p.Material;
            studio.parts.Add(new Part(
                p.NodeIndex, p.Mesh.VertexBuffer, p.Mesh.IndexBuffer, p.Mesh.IndexCount,
                StudioInspection.BaseColour(m, StudioInspection.ModelFallbackColour),
                m?.MetallicFactor ?? 0f, m?.RoughnessFactor ?? StudioInspection.FallbackRoughness, StudioSurface.Of(m, p.Textures),
                AlbedoUvSet: m?.BaseColorTexCoord ?? 0,
                BaseAlpha: m?.BaseColorFactor.W ?? 1f,
                AlphaMode: m?.AlphaMode ?? AlphaMode.Opaque,
                AlphaCutoff: m?.AlphaCutoff ?? 0.5f,
                DoubleSided: m?.DoubleSided ?? false,
                MaterialName: m?.Name ?? string.Empty));
        }

        return studio;
    }

    public void Dispose()
    {
        model.Dispose();
        textures.Dispose();
        parts.Clear();
    }
}

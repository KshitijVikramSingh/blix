using System.Numerics;
using Blix;
using Blix.Assets;
using Blix.Cooked;
using Blix.Graphics;
using Blix.Render;

namespace Blix.Tools.Studio;

/// <summary>A model as Studio draws it: the engine's <see cref="Model"/>, with Studio's policy applied.</summary>
/// <remarks>
/// <para>
/// <b>One type, as the engine has one.</b> A file with a skin and a file without are the same kind of
/// thing: parts a skin deforms draw through the palette, and every other part draws rigidly — at its
/// node's world, or at the joint that carries it. A static file is one with no skinned parts and no
/// joints, so it takes only the rigid path.
/// </para>
/// <para>
/// <b>Policy over residency.</b> Uploading, skins, clips, palette packing and the bone buffers are the
/// engine's (<see cref="Model"/>, <see cref="BoneBuffers"/>). This type is what Studio decides on top: the
/// instance row its reference pipeline poses, the grey a part without a material is drawn in, that cooked
/// textures are realised at load rather than streamed (an inspector shows full detail at once), and the
/// names its resources carry. How many bones a skin may have is not among them: each skin's palette
/// buffer is sized for its own bones, up to what the device binds.
/// </para>
/// </remarks>
internal sealed class StudioModel : IDisposable
{
    /// <summary>Maximum independently posed bodies packed into each skin's palette buffer.</summary>
    /// <remarks>
    /// Studio needs enough instances to compare independent poses, not crowd-scale storage. Each skin's
    /// buffer is <c>bones * MaxInstances * 64</c> bytes, allocated once per model.
    /// </remarks>
    public const int MaxInstances = 8;

    /// <summary>One drawable piece of the skin: every primitive shares the skeleton and the palette.</summary>
    public readonly record struct SkinnedPart(
        VertexBufferHandle Vertices,
        IndexBufferHandle Indices,
        int IndexCount,
        Vector3 BaseColour,
        float Metallic,
        float Roughness,
        StudioSurface Surface,
        /// <summary>Which of <see cref="Skins"/> poses this part.</summary>
        /// <remarks>Skins may share joints while retaining distinct inverse-bind matrices.</remarks>
        int SkinIndex,
        /// <summary>Which TEXCOORD set this part's albedo samples — 0 for almost everything.</summary>
        int AlbedoUvSet = 0,
        /// <summary>The material's <c>baseColorFactor.a</c>, which the cutout test multiplies in.</summary>
        float BaseAlpha = 1f,
        AlphaMode AlphaMode = AlphaMode.Opaque,
        float AlphaCutoff = 0.5f,
        bool DoubleSided = false,
        /// <summary>The material's own name, which application-owned tint policy keys on.</summary>
        string MaterialName = "");

    /// <summary>A static mesh carried by a joint — a knife in a hand, a cape on a chest.</summary>
    /// <remarks>
    /// Attachments use the standard lit pipeline and a model matrix composed from their joint world
    /// transform; they do not consume the skinned palette as vertex data. Their alpha is the
    /// material's, as a skinned part's is: MASK discards, BLEND draws in the blended group.
    /// </remarks>
    public readonly record struct Attachment(
        string Name,
        string JointName,
        int JointIndex,
        Matrix4x4 LocalTransform,
        VertexBufferHandle Vertices,
        IndexBufferHandle Indices,
        int IndexCount,
        Vector3 BaseColour,
        float Metallic,
        float Roughness,
        StudioSurface Surface,
        /// <summary>Which TEXCOORD set this part's albedo samples — 0 for almost everything.</summary>
        int AlbedoUvSet = 0,
        /// <summary>The material's <c>baseColorFactor.a</c>, which the cutout test multiplies in.</summary>
        float BaseAlpha = 1f,
        AlphaMode AlphaMode = AlphaMode.Opaque,
        float AlphaCutoff = 0.5f,
        /// <summary>The material's <c>doubleSided</c>: drawn unculled when set, back faces culled otherwise.</summary>
        bool DoubleSided = false,
        /// <summary>The material's own name, which application-owned tint policy keys on.</summary>
        string MaterialName = "");

    /// <summary>Static geometry carried by the asset that follows no joint, at its authored world transform.</summary>
    /// <remarks>Excluded from the attachment picker. Its alpha is the material's, as an attachment's is.</remarks>
    public readonly record struct StaticPart(
        string Name,
        Matrix4x4 WorldTransform,
        VertexBufferHandle Vertices,
        IndexBufferHandle Indices,
        int IndexCount,
        Vector3 BaseColour,
        float Metallic,
        float Roughness,
        StudioSurface Surface,
        /// <summary>Which TEXCOORD set this part's albedo samples — 0 for almost everything.</summary>
        int AlbedoUvSet = 0,
        /// <summary>The material's <c>baseColorFactor.a</c>, which the cutout test multiplies in.</summary>
        float BaseAlpha = 1f,
        AlphaMode AlphaMode = AlphaMode.Opaque,
        float AlphaCutoff = 0.5f,
        /// <summary>The material's <c>doubleSided</c>: drawn unculled when set, back faces culled otherwise.</summary>
        bool DoubleSided = false,
        /// <summary>The material's own name, which application-owned tint policy keys on.</summary>
        string MaterialName = "");

    /// <summary>One skin: its own skeleton (bone count, inverse binds) and its palette binding.</summary>
    public sealed record SkinSlot(SkinBinding Skin, MaterialHandle BoneMaterial);

    private Model model = null!;
    private BoneBuffers? bones;
    private MaterialTextureLoader textures = null!;
    private readonly List<SkinnedPart> skinnedParts = new();
    private readonly List<Attachment> attachments = new();
    private readonly List<StaticPart> staticParts = new();
    private readonly List<SkinSlot> skins = new();
    // Per skin: whether its last uploaded palette mirrors (a negative determinant), judged on a bone that
    // weights vertices — the skin's equivalent of a rigid part's world, for choosing its front face.
    private bool[] mirrored = Array.Empty<bool>();
    private int[] probeBone = Array.Empty<int>();
    private int variant = -1;

    /// <summary>Whether <paramref name="skin"/> was last uploaded mirrored, so its front faces are clockwise.</summary>
    /// <remarks>One answer per skin: a rig that mirrors some joints and not others within one primitive cannot be one pipeline.</remarks>
    public bool IsMirrored(int skin) => (uint)skin < (uint)mirrored.Length && mirrored[skin];

    /// <summary>The engine model this draws.</summary>
    public Model Model => model;

    /// <summary>The parts a skin deforms. Empty for a file without a skin.</summary>
    public IReadOnlyList<SkinnedPart> SkinnedParts => skinnedParts;

    /// <summary>Static meshes the asset hangs off joints. Empty for most models.</summary>
    public IReadOnlyList<Attachment> Attachments => attachments;

    /// <summary>Unskinned parts on no joint, at their nodes' world transforms: everything, in a static file.</summary>
    public IReadOnlyList<StaticPart> StaticParts => staticParts;

    /// <summary>Every skin the asset declares. None for a static file, three for tank.glb.</summary>
    public IReadOnlyList<SkinSlot> Skins => skins;

    /// <summary>
    /// Loads the model, its skins and clips when it has them, and the per-frame bone-palette buffers.
    /// </summary>
    /// <param name="skinnedProgram">
    /// The program whose set-3 slot describes the palette buffer: Studio's skinned pipeline.
    /// </param>
    /// <param name="scene">The scene to place; null for the file's default.</param>
    internal static StudioModel Load(IGraphicsDevice device, string path, ShaderProgramHandle skinnedProgram, int? scene = null)
    {
        // Cooked on open: what Studio shows is the cooked asset, which is what the engine draws. Skinned
        // parts skinned; every other part in the complete vertex the static pipeline reads (tangents for
        // normal maps, COLOR_0 as a base-colour multiplier, conventions §6).
        var data = ModelData.Load(Blix.Recipes.CookCache.Resolve(path), new ModelNeeds(Tangents: true, Colour: true, Skinned: true), scene);

        var studio = new StudioModel();
        studio.textures = new MaterialTextureLoader(device);
        studio.model = device.CreateModel(data, studio.textures, $"lab.{Path.GetFileNameWithoutExtension(path)}");
        // Realised now, not streamed: an inspector shows the asset as it is from the first frame.
        studio.textures.Drain(double.PositiveInfinity);
        studio.Rebuild();
        var model = studio.model;

        if (!model.IsSkinned) return studio;

        studio.bones = model.CreateBoneBuffers(skinnedProgram, MaxInstances);
        studio.mirrored = new bool[model.Skins.Count];
        studio.probeBone = model.Skins.Select(skin =>
        {
            for (var j = 0; j < skin.Bones.Count; j++)
            {
                if ((uint)skin.Bones[j] < (uint)model.WeightedBones.Count && model.WeightedBones[skin.Bones[j]]) return j;
            }

            return 0;
        }).ToArray();
        for (var s = 0; s < model.Skins.Count; s++)
        {
            studio.skins.Add(new SkinSlot(model.Skins[s], studio.bones.For(s).Handle));
        }

        return studio;
    }

    /// <summary>The <c>KHR_materials_variants</c> entry the parts are drawn with; -1 for each primitive's own material.</summary>
    public int Variant => variant;

    /// <summary>Draws every part with <paramref name="index"/>'s materials from now on (-1: their own).</summary>
    public void SetVariant(int index)
    {
        if (index < -1 || index >= model.Variants.Count)
        {
            throw new ArgumentOutOfRangeException(
                nameof(index), $"'{model.Name}' has {model.Variants.Count} variant(s); {index} is not one (-1 for none).");
        }

        variant = index;
        Rebuild();
    }

    // Studio's part lists from the engine model: only shown nodes (KHR_node_visibility), a static part or
    // attachment once per instance (EXT_mesh_gpu_instancing, each instance before the node's transform),
    // and every material the chosen variant gives (KHR_materials_variants).
    private void Rebuild()
    {
        skinnedParts.Clear();
        staticParts.Clear();
        attachments.Clear();
        var fallback = model.IsSkinned ? StudioInspection.RigFallbackColour : StudioInspection.ModelFallbackColour;
        Vector3 BaseColourOf(PbrMaterial? m) => StudioInspection.BaseColour(m, fallback);

        foreach (var p in model.SkinnedParts)
        {
            if (!model.Nodes[p.NodeIndex].Shown) continue;
            var (m, tex) = p.MaterialFor(variant);
            skinnedParts.Add(new SkinnedPart(
                p.Mesh.VertexBuffer, p.Mesh.IndexBuffer, p.Mesh.IndexCount, BaseColourOf(m),
                m?.MetallicFactor ?? 0f, m?.RoughnessFactor ?? StudioInspection.FallbackRoughness, StudioSurface.Of(m, tex),
                p.SkinIndex,
                AlbedoUvSet: m?.BaseColorTexCoord ?? 0,
                BaseAlpha: m?.BaseColorFactor.W ?? 1f,
                AlphaMode: m?.AlphaMode ?? AlphaMode.Opaque,
                AlphaCutoff: m?.AlphaCutoff ?? 0.5f,
                DoubleSided: m?.DoubleSided ?? false,
                MaterialName: m?.Name ?? string.Empty));
        }

        foreach (var p in model.StaticParts)
        {
            var node = model.Nodes[p.NodeIndex];
            if (!node.Shown) continue;
            var (m, tex) = p.MaterialFor(variant);
            var siblings = model.StaticParts.Where(o => o.NodeIndex == p.NodeIndex).ToList();
            var name = siblings.Count > 1 ? $"{node.Name}.{siblings.IndexOf(p)}" : node.Name;
            var instance = 0;
            foreach (var world in node.DrawnWorlds)
            {
                staticParts.Add(new StaticPart(
                    node.Instances is null ? name : $"{name}#{instance++}", world, p.Mesh.VertexBuffer, p.Mesh.IndexBuffer, p.Mesh.IndexCount, BaseColourOf(m),
                    m?.MetallicFactor ?? 0f, m?.RoughnessFactor ?? StudioInspection.FallbackRoughness, StudioSurface.Of(m, tex),
                    AlbedoUvSet: m?.BaseColorTexCoord ?? 0,
                    BaseAlpha: m?.BaseColorFactor.W ?? 1f,
                    AlphaMode: m?.AlphaMode ?? AlphaMode.Opaque,
                    AlphaCutoff: m?.AlphaCutoff ?? 0.5f,
                    DoubleSided: m?.DoubleSided ?? false,
                    MaterialName: m?.Name ?? string.Empty));
            }
        }

        foreach (var a in model.Attachments)
        {
            var node = model.Nodes[a.Part.NodeIndex];
            if (!node.Shown) continue;
            var (m, tex) = a.Part.MaterialFor(variant);
            var instance = 0;
            foreach (var local in node.Instances?.Select(i => i * a.Local) ?? new[] { a.Local })
            {
                attachments.Add(new Attachment(
                    node.Instances is null ? a.Name : $"{a.Name}#{instance++}", a.JointName, a.JointIndex, local,
                    a.Part.Mesh.VertexBuffer, a.Part.Mesh.IndexBuffer, a.Part.Mesh.IndexCount,
                    BaseColourOf(m), m?.MetallicFactor ?? 0f, m?.RoughnessFactor ?? StudioInspection.FallbackRoughness,
                    StudioSurface.Of(m, tex),
                    AlbedoUvSet: m?.BaseColorTexCoord ?? 0,
                    BaseAlpha: m?.BaseColorFactor.W ?? 1f,
                    AlphaMode: m?.AlphaMode ?? AlphaMode.Opaque,
                    AlphaCutoff: m?.AlphaCutoff ?? 0.5f,
                    DoubleSided: m?.DoubleSided ?? false,
                    MaterialName: m?.Name ?? string.Empty));
            }
        }
    }

    /// <summary>Copies one skin's live instance palettes into that skin's frame buffer.</summary>
    public void UploadPalettes(BonePaletteSet palettes, int skinIndex)
    {
        (bones ?? throw new InvalidOperationException($"'{model.Name}' has no skin, so there is no palette to upload."))
            .Upload(skinIndex, palettes);
        if (palettes.Count > 0 && (uint)skinIndex < (uint)mirrored.Length)
        {
            mirrored[skinIndex] = palettes.Matrices[probeBone[skinIndex]].GetDeterminant() < 0f;
        }
    }

    public void Dispose()
    {
        bones?.Dispose();
        model.Dispose();
        textures.Dispose();
        skinnedParts.Clear();
        attachments.Clear();
        staticParts.Clear();
    }
}

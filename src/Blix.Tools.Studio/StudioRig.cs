using System.Numerics;
using Blix;
using Blix.Assets;
using Blix.Cooked;
using Blix.Graphics;
using Blix.Render;

namespace Blix.Tools.Studio;

/// <summary>A rigged glTF as Studio draws it: the engine's <see cref="Rig"/>, with Studio's policy applied.</summary>
/// <remarks>
/// <b>Policy over residency.</b> Uploading, skins, clips, palette packing and the bone buffers are the
/// engine's (<see cref="Rig"/>, <see cref="BoneBuffers"/>). This type is what Studio decides on top: the
/// bone and instance caps its reference row allows, the grey a part without a material is drawn in, that
/// cooked textures are realised at load rather than streamed (an inspector shows full detail at once),
/// and the names its resources carry.
/// </remarks>
internal sealed class StudioRig : IDisposable
{
    /// <summary>Matches the fixed bound in <c>studio_skinned.vert</c>.</summary>
    /// <remarks>
    /// Stated to tools as <see cref="StudioRenderer.MaxBones"/>. <see cref="Load"/> checks
    /// every skin before allocating GPU resources; a larger rig is valid engine data but unsupported
    /// by this reference pipeline.
    /// </remarks>
    public const int MaxBones = 128;

    /// <summary>Maximum independently posed bodies packed into each skin's palette buffer.</summary>
    /// <remarks>
    /// Studio needs enough instances to compare independent poses, not crowd-scale storage.
    /// <c>MaxBones * MaxInstances * 64</c> is 64 KB per skin and is allocated once per rig.
    /// </remarks>
    public const int MaxInstances = 8;

    /// <summary>Total matrices in each Studio skin-palette buffer.</summary>
    public const int PaletteMatrixCapacity = MaxBones * MaxInstances;

    /// <summary>One drawable piece of the skin: every primitive shares the skeleton and the palette.</summary>
    public readonly record struct Part(
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
        GltfAlphaMode AlphaMode = GltfAlphaMode.Opaque,
        float AlphaCutoff = 0.5f,
        bool DoubleSided = false,
        /// <summary>The material's own name, which application-owned tint policy keys on.</summary>
        string MaterialName = "");

    /// <summary>A static mesh carried by a joint — a knife in a hand, a cape on a chest.</summary>
    /// <remarks>
    /// Attachments use the standard lit pipeline and a model matrix composed from their joint world
    /// transform; they do not consume the skinned palette as vertex data.
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
        /// <summary>The material's own name, which application-owned tint policy keys on.</summary>
        string MaterialName = "");

    /// <summary>Static geometry carried by the asset that follows no joint, at its authored world transform.</summary>
    /// <remarks>Excluded from the attachment picker.</remarks>
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
        /// <summary>The material's own name, which application-owned tint policy keys on.</summary>
        string MaterialName = "");

    /// <summary>One skin: the skeleton it poses, the frame its meshes were authored in, and its palette binding.</summary>
    public sealed record SkinSlot(Skeleton Skeleton, Matrix4x4 MeshNodeTransform, MaterialHandle BoneMaterial);

    private Rig rig = null!;
    private BoneBuffers bones = null!;
    private GltfTextureLoader textures = null!;
    private readonly List<Part> parts = new();
    private readonly List<Attachment> attachments = new();
    private readonly List<StaticPart> staticParts = new();
    private readonly List<SkinSlot> skins = new();

    /// <summary>The engine rig this draws.</summary>
    public Rig Rig => rig;

    public IReadOnlyList<Part> Parts => parts;

    /// <summary>Static meshes the asset hangs off joints. Empty for most rigs.</summary>
    public IReadOnlyList<Attachment> Attachments => attachments;

    public IReadOnlyList<StaticPart> StaticParts => staticParts;

    /// <summary>The set-3 palette binding for skin 0. Most rigs have exactly one skin.</summary>
    public MaterialHandle BoneMaterial => skins[0].BoneMaterial;

    /// <summary>Every skin the asset declares. One for most rigs, three for tank.glb.</summary>
    public IReadOnlyList<SkinSlot> Skins => skins;

    /// <summary>
    /// Loads the skin, its clips and its skeleton, and creates the per-frame bone-palette buffers.
    /// </summary>
    /// <param name="skinnedProgram">
    /// The program whose set-3 slot describes the palette buffer: Studio's skinned pipeline.
    /// </param>
    internal static StudioRig Load(IGraphicsDevice device, string path, ShaderProgramHandle skinnedProgram)
    {
        var imported = new GltfImporter().Import(new AssetImportContext(AssetId.Parse("lab.rig"), path));
        ValidatePaletteCapacity(path, imported.SkinsOrEmpty);

        var studio = new StudioRig();
        studio.textures = new GltfTextureLoader(device);
        studio.rig = device.CreateRig(imported, studio.textures, $"lab.rig.{Path.GetFileNameWithoutExtension(path)}");
        // Realised now, not streamed: an inspector shows the asset as it is from the first frame.
        studio.textures.Drain(double.PositiveInfinity);
        studio.bones = studio.rig.CreateBoneBuffers(skinnedProgram, MaxInstances);

        foreach (var p in studio.rig.Parts)
        {
            var m = p.Material;
            studio.parts.Add(new Part(
                p.Mesh.VertexBuffer, p.Mesh.IndexBuffer, p.Mesh.IndexCount, BaseColourOf(m),
                m?.MetallicFactor ?? 0f, m?.RoughnessFactor ?? StudioInspection.FallbackRoughness, StudioSurface.Of(m, p.Textures),
                p.SkinIndex,
                AlbedoUvSet: m?.BaseColorTexCoord ?? 0,
                BaseAlpha: m?.BaseColorFactor.W ?? 1f,
                AlphaMode: m?.AlphaMode ?? GltfAlphaMode.Opaque,
                AlphaCutoff: m?.AlphaCutoff ?? 0.5f,
                DoubleSided: m?.DoubleSided ?? false,
                MaterialName: m?.Name ?? string.Empty));
        }

        foreach (var p in studio.rig.StaticParts)
        {
            var m = p.Material;
            studio.staticParts.Add(new StaticPart(
                p.Name, p.World, p.Mesh.VertexBuffer, p.Mesh.IndexBuffer, p.Mesh.IndexCount, BaseColourOf(m),
                m?.MetallicFactor ?? 0f, m?.RoughnessFactor ?? StudioInspection.FallbackRoughness, StudioSurface.Of(m, p.Textures),
                m?.Name ?? string.Empty));
        }

        foreach (var a in studio.rig.Attachments)
        {
            var m = a.Material;
            studio.attachments.Add(new Attachment(
                a.Name, a.JointName, a.JointIndex, a.Local, a.Mesh.VertexBuffer, a.Mesh.IndexBuffer, a.Mesh.IndexCount,
                BaseColourOf(m), m?.MetallicFactor ?? 0f, m?.RoughnessFactor ?? StudioInspection.FallbackRoughness,
                StudioSurface.Of(m, a.Textures), m?.Name ?? string.Empty));
        }

        for (var s = 0; s < studio.rig.Skins.Count; s++)
        {
            studio.skins.Add(new SkinSlot(
                studio.rig.Skins[s].Skeleton, studio.rig.Skins[s].MeshNodeTransform, studio.bones.For(s).Handle));
        }

        return studio;
    }

    /// <summary>Copies one skin's live instance palettes into that skin's frame buffer.</summary>
    public void UploadPalettes(BonePaletteSet palettes, int skinIndex) => bones.Upload(skinIndex, palettes);

    /// <summary>Refuses a rig that cannot fit Studio's maximum instance row.</summary>
    internal static void ValidatePaletteCapacity(string sourcePath, IReadOnlyList<GltfSkinBinding> skins)
    {
        ArgumentNullException.ThrowIfNull(sourcePath);
        ArgumentNullException.ThrowIfNull(skins);
        for (var skin = 0; skin < skins.Count; skin++)
        {
            var count = skins[skin].Skeleton.BoneCount;
            if (checked(count * MaxInstances) <= PaletteMatrixCapacity) continue;
            throw new AssetImportException(
                sourcePath,
                null,
                $"skin {skin} has {count} bones; Studio supports at most {MaxBones} bones across " +
                $"its {MaxInstances}-instance reference row ({PaletteMatrixCapacity} palette matrices)");
        }
    }

    private static Vector3 BaseColourOf(GltfMaterial? m) =>
        StudioInspection.BaseColour(m, StudioInspection.RigFallbackColour);

    public void Dispose()
    {
        bones.Dispose();
        rig.Dispose();
        textures.Dispose();
        parts.Clear();
        attachments.Clear();
        staticParts.Clear();
    }
}

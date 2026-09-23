using System.Numerics;
using System.Runtime.InteropServices;
using Blix;
using Blix.Assets;
using Blix.Cooked;
using Blix.Graphics;
using Blix.Graphics.Images;
using Blix.Graphics.Vulkan;

namespace Blix.Tools.Studio;

/// <summary>
/// A rigged glTF prepared for inspection and rendering in Studio.
/// </summary>
/// <remarks>
/// Rigged assets have a separate representation from <see cref="StudioModel"/>: they carry fused
/// skinned primitives, skeletons, clips, per-skin inverse-bind state, and palette bindings rather
/// than an authored static-node hierarchy. This type owns their GPU residency and bind-time facts.
/// Animation clocks, blending, instance policy, and frame orchestration remain with callers.
/// </remarks>
public sealed class StudioRig : IDisposable
{
    /// <summary>Matches the fixed bound in <c>studio_skinned.vert</c>.</summary>
    /// <remarks>
    /// Public so Studio's UI and conformance suite can describe the limit. <see cref="Load"/> checks
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
        TextureHandle Albedo,
        /// <summary>Which of <see cref="Skins"/> poses this part.</summary>
        /// <remarks>Skins may share joints while retaining distinct inverse-bind matrices.</remarks>
        int SkinIndex,
        /// <summary>
        /// What the material says about its own surface: <c>OPAQUE</c>/<c>MASK</c>/<c>BLEND</c>, the
        /// cutout threshold, and whether the back face is part of the model.
        /// </summary>
        /// <remarks>
        /// The shared Studio pipeline consumes double-sided state now. Alpha mode and cutoff are
        /// retained as authored material facts even when the current sample assets do not exercise
        /// a visible cutout.
        /// </remarks>
        /// <summary>Which TEXCOORD set this part's albedo samples — 0 for almost everything.</summary>
        int AlbedoUvSet = 0,
        /// <summary>The material's <c>baseColorFactor.a</c>, which the cutout test multiplies in.</summary>
        float BaseAlpha = 1f,
        GltfAlphaMode AlphaMode = GltfAlphaMode.Opaque,
        float AlphaCutoff = 0.5f,
        bool DoubleSided = false,
        /// <summary>The material's own name — see <see cref="StudioModel.Part.MaterialName"/>.</summary>
        string MaterialName = "");

    /// <summary>
    /// A static mesh carried by a joint — a knife in a hand, a cape on a chest.
    /// </summary>
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
        TextureHandle Albedo,
        /// <summary>The material's own name — see <see cref="StudioModel.Part.MaterialName"/>.</summary>
        string MaterialName = "");

    /// <summary>One image the asset actually ships, with enough to label it in a panel.</summary>
    public readonly record struct Image(string Name, TextureHandle Texture, int Width, int Height);

    private VulkanGraphicsDevice device = null!;
    private readonly List<Part> parts = new();
    private readonly List<Attachment> attachments = new();
    private readonly List<TextureHandle> ownedTextures = new();
    private readonly List<Image> images = new();
    private MaterialBindings bones = null!;
    private readonly List<SkinSlot> skins = new();
    private readonly List<StaticPart> staticParts = new();
    private readonly List<MaterialBindings> skinBones = new();
    private byte[] palettePayload = Array.Empty<byte>();

    public Skeleton Skeleton { get; private set; } = null!;

    /// <summary>Every clip in the file, ordered by name so two runs of the viewer list them the same way.</summary>
    public IReadOnlyList<AnimationClip> Clips { get; private set; } = Array.Empty<AnimationClip>();

    public IReadOnlyList<Part> Parts => parts;

    /// <summary>Static meshes the asset hangs off joints. Empty for most rigs.</summary>
    public IReadOnlyList<Attachment> Attachments => attachments;

    /// <summary>
    /// Static geometry carried by the asset that follows no joint.
    /// </summary>
    /// <remarks>
    /// Static parts keep their authored world transform and are excluded from the attachment picker.
    /// </remarks>
    public readonly record struct StaticPart(
        string Name,
        Matrix4x4 WorldTransform,
        VertexBufferHandle Vertices,
        IndexBufferHandle Indices,
        int IndexCount,
        Vector3 BaseColour,
        float Metallic,
        float Roughness,
        TextureHandle Albedo,
        /// <summary>The material's own name — see <see cref="StudioModel.Part.MaterialName"/>.</summary>
        string MaterialName = "");

    public IReadOnlyList<StaticPart> StaticParts => staticParts;

    /// <summary>Mesh nodes or attributes the importer declined, with their reasons.</summary>
    /// <remarks>Studio retains these diagnostics so inspection exposes incomplete imports.</remarks>
    public IReadOnlyList<GltfIgnored> Ignored { get; private set; } = Array.Empty<GltfIgnored>();

    /// <summary>Distinct uploaded base-colour images, with labels and dimensions for inspection.</summary>
    public IReadOnlyList<Image> Images => images;

    /// <summary>The skin node's ancestor chain, composed. Goes into uModel BEFORE the user transform.</summary>
    public Matrix4x4 MeshNodeTransform { get; private set; } = Matrix4x4.Identity;

    public string SourcePath { get; private set; } = string.Empty;

    /// <summary>Bounds of the mesh in its REST pose, in mesh-node space. What the camera frames on.</summary>
    public Vector3 BoundsMin { get; private set; } = new(float.MaxValue);

    public Vector3 BoundsMax { get; private set; } = new(float.MinValue);

    public int VertexCount { get; private set; }

    /// <summary>Per bone, whether any vertex carries a non-zero weight for it.</summary>
    /// <remarks>
    /// This is the literal vertex-weight census. Control bones may remain false even though they are
    /// present in the skeleton and palette. Use <see cref="DeformHierarchy"/> for overlay drawing.
    /// </remarks>
    public IReadOnlyList<bool> WeightedBones => weightedBones;

    /// <summary>How many bones some vertex weights. The rest are controls the mesh never sees.</summary>
    public int WeightedBoneCount { get; private set; }

    /// <summary>Weighted bones plus every ancestor needed to draw their chains continuously.</summary>
    /// <remarks>
    /// This is deliberately a superset of <see cref="WeightedBones"/>: unweighted parents can carry
    /// weighted descendants, and omitting them leaves disconnected overlay segments.
    /// </remarks>
    public IReadOnlyList<bool> DeformHierarchy => deformHierarchy;

    /// <summary>How many bones the overlay must draw to show every weighted chain unbroken.</summary>
    public int DeformHierarchyCount { get; private set; }

    private bool[] weightedBones = Array.Empty<bool>();
    private bool[] deformHierarchy = Array.Empty<bool>();

    public float LongestExtent
    {
        get
        {
            if (parts.Count == 0) return 1f;
            var size = BoundsMax - BoundsMin;
            return MathF.Max(size.X, MathF.Max(size.Y, size.Z));
        }
    }

    /// <summary>The set-3 palette binding for skin 0. Most rigs have exactly one skin.</summary>
    public MaterialHandle BoneMaterial => bones.Handle;

    /// <summary>
    /// One skin: the skeleton it poses, the frame its meshes were authored in, and the palette
    /// buffer its parts are drawn against.
    /// </summary>
    public sealed record SkinSlot(Skeleton Skeleton, Matrix4x4 MeshNodeTransform, MaterialHandle BoneMaterial);

    /// <summary>Every skin the asset declares. One for most rigs, three for tank.glb.</summary>
    /// <remarks>Each skin has its own palette binding because its inverse-bind matrices may differ.</remarks>
    public IReadOnlyList<SkinSlot> Skins => skins;

    /// <summary>
    /// Loads the skin, its clips and its skeleton, and creates the per-frame bone-palette buffer.
    /// </summary>
    /// <param name="skinnedProgram">
    /// The program whose set-3 slot describes the palette buffer. Reflected, so the buffer's size comes
    /// from the shader's own declaration rather than from a number restated here.
    /// </param>
    public static StudioRig Load(VulkanGraphicsDevice vk, string path, ShaderProgramHandle skinnedProgram)
    {
        var rig = new StudioRig { device = vk, SourcePath = path };

        var imported = new GltfImporter().Import(new AssetImportContext(AssetId.Parse("lab.rig"), path));
        ValidatePaletteCapacity(path, imported.SkinsOrEmpty);
        rig.Skeleton = imported.Skeleton;

        rig.Ignored = imported.IgnoredOrEmpty;
        rig.MeshNodeTransform = imported.MeshNodeTransform;
        rig.Clips = imported.Animations.OrderBy(c => c.Name, StringComparer.Ordinal).ToArray();
        rig.palettePayload = new byte[PaletteMatrixCapacity * 64];
        rig.weightedBones = SkinningAnalysis.FindWeightedBones(imported.Skeleton, imported.Primitives);
        rig.WeightedBoneCount = rig.weightedBones.Count(b => b);
        rig.deformHierarchy = SkinningAnalysis.IncludeAncestors(imported.Skeleton, rig.weightedBones);
        rig.DeformHierarchyCount = rig.deformHierarchy.Count(b => b);

        var white = vk.CreateTexture2D(
            new TextureDescription(1, 1, TextureFormat.Rgba8Srgb, SamplerDescription.LinearRepeat),
            new byte[] { 255, 255, 255, 255 }, "lab.rig.white");
        rig.ownedTextures.Add(white);

        // TextureRegistry uses source identity rather than GltfTexture object identity so repeated
        // material references share one upload.
        var uploaded = new TextureRegistry();
        for (var i = 0; i < imported.Primitives.Length; i++)
        {
            var primitive = imported.Primitives[i];
            var mesh = primitive.Mesh;
            rig.VertexCount += mesh.VertexCount;
            rig.AccumulateRestBounds(mesh);

            var name = $"lab.rig.{Path.GetFileNameWithoutExtension(path)}.{i}";
            var vb = vk.CreateVertexBuffer(
                new VertexBufferData(
                    new VertexBufferDescription(mesh.Layout, mesh.VertexCount, GraphicsBufferUsage.Static),
                    mesh.VertexBytes),
                $"{name}.vb");
            var ib = mesh.Indices32 is { } wide
                ? vk.CreateIndexBuffer(wide, name: $"{name}.ib")
                : vk.CreateIndexBuffer(mesh.Indices, name: $"{name}.ib");

            var material = primitive.Material;
            rig.parts.Add(new Part(
                SkinIndex: primitive.SkinIndex,
                Vertices: vb,
                Indices: ib,
                IndexCount: mesh.IndexCount,
                BaseColour: material is null
                    ? new Vector3(0.75f)
                    : new Vector3(
                        material.BaseColorFactor.X, material.BaseColorFactor.Y, material.BaseColorFactor.Z),
                Metallic: material?.MetallicFactor ?? 0f,
                Roughness: material?.RoughnessFactor ?? 0.7f,
                Albedo: rig.UploadAlbedo(vk, material?.BaseColorTexture, white, uploaded),
                AlbedoUvSet: material?.BaseColorTexCoord ?? 0,
                BaseAlpha: material?.BaseColorFactor.W ?? 1f,
                AlphaMode: material?.AlphaMode ?? GltfAlphaMode.Opaque,
                AlphaCutoff: material?.AlphaCutoff ?? 0.5f,
                DoubleSided: material?.DoubleSided ?? false,
                MaterialName: material?.Name ?? string.Empty));
        }

        // Static parts and attachments upload beside the skinned parts but do not expand the body's
        // rest bounds; camera framing describes the skinned subject rather than optional equipment.
        for (var i = 0; i < imported.StaticPartsOrEmpty.Length; i++)
        {
            var source = imported.StaticPartsOrEmpty[i];
            for (var p = 0; p < source.Primitives.Length; p++)
            {
                var mesh = source.Primitives[p].Mesh;
                var name = $"lab.rig.{Path.GetFileNameWithoutExtension(path)}.static.{i}.{p}";
                var material = source.Primitives[p].Material;
                rig.staticParts.Add(new StaticPart(
                    Name: source.Primitives.Length > 1 ? $"{source.Name}.{p}" : source.Name,
                    WorldTransform: source.WorldTransform,
                    Vertices: vk.CreateVertexBuffer(
                        new VertexBufferData(
                            new VertexBufferDescription(mesh.Layout, mesh.VertexCount, GraphicsBufferUsage.Static),
                            mesh.VertexBytes),
                        $"{name}.vb"),
                    Indices: mesh.Indices32 is { } staticWide
                        ? vk.CreateIndexBuffer(staticWide, name: $"{name}.ib")
                        : vk.CreateIndexBuffer(mesh.Indices, name: $"{name}.ib"),
                    IndexCount: mesh.IndexCount,
                    BaseColour: material is null
                        ? new Vector3(0.75f)
                        : new Vector3(
                            material.BaseColorFactor.X, material.BaseColorFactor.Y, material.BaseColorFactor.Z),
                    Metallic: material?.MetallicFactor ?? 0f,
                    Roughness: material?.RoughnessFactor ?? 0.7f,
                    Albedo: rig.UploadAlbedo(vk, material?.BaseColorTexture, white, uploaded),
                    MaterialName: material?.Name ?? string.Empty));
            }
        }

        for (var i = 0; i < imported.AttachmentsOrEmpty.Length; i++)
        {
            var source = imported.AttachmentsOrEmpty[i];
            for (var p = 0; p < source.Primitives.Length; p++)
            {
                var mesh = source.Primitives[p].Mesh;
                var name = $"lab.rig.{Path.GetFileNameWithoutExtension(path)}.attach.{i}.{p}";
                var material = source.Primitives[p].Material;
                rig.attachments.Add(new Attachment(
                    Name: source.Primitives.Length > 1 ? $"{source.Name}.{p}" : source.Name,
                    JointName: source.JointName,
                    JointIndex: source.JointIndex,
                    LocalTransform: source.LocalTransform,
                    Vertices: vk.CreateVertexBuffer(
                        new VertexBufferData(
                            new VertexBufferDescription(mesh.Layout, mesh.VertexCount, GraphicsBufferUsage.Static),
                            mesh.VertexBytes),
                        $"{name}.vb"),
                    Indices: mesh.Indices32 is { } attachWide
                        ? vk.CreateIndexBuffer(attachWide, name: $"{name}.ib")
                        : vk.CreateIndexBuffer(mesh.Indices, name: $"{name}.ib"),
                    IndexCount: mesh.IndexCount,
                    BaseColour: material is null
                        ? new Vector3(0.75f)
                        : new Vector3(
                            material.BaseColorFactor.X, material.BaseColorFactor.Y, material.BaseColorFactor.Z),
                    Metallic: material?.MetallicFactor ?? 0f,
                    Roughness: material?.RoughnessFactor ?? 0.7f,
                    Albedo: rig.UploadAlbedo(vk, material?.BaseColorTexture, white, uploaded),
                    MaterialName: material?.Name ?? string.Empty));
            }
        }

        if (rig.parts.Count == 0)
        {
            rig.BoundsMin = Vector3.Zero;
            rig.BoundsMax = Vector3.Zero;
        }

        // Each skin owns a material with one backing buffer per frame slot. Frame-slot separation
        // prevents CPU palette writes from aliasing GPU reads; instance slices separate poses drawn
        // within one frame.
        var imports = imported.SkinsOrEmpty;
        for (var s = 0; s < imports.Length; s++)
        {
            var slotBones = vk.CreateMaterial(
                skinnedProgram, setIndex: 3, framesInFlight: vk.MaxFramesInFlightCount,
                name: $"lab.rig.bones.{s}");
            rig.skinBones.Add(slotBones);
            rig.skins.Add(new SkinSlot(
                imports[s].Skeleton, imports[s].MeshNodeTransform, slotBones.Handle));
        }

        rig.bones = rig.skinBones[0];

        return rig;
    }

    /// <summary>Copies every live instance palette into skin 0's current-frame buffer.</summary>
    /// <remarks>
    /// The 4x4s go up untransposed, exactly as conventions §2 says: GLSL reads std430 column-major,
    /// which is the transpose of the row-vector form the CPU built, so `skin * v` in the shader
    /// computes what `v_row * skin` computes here. There is no transpose in this file and there must
    /// not be one.
    /// Only the live matrix prefix is written; unused instance capacity is not uploaded.
    /// </remarks>
    public void UploadPalettes(BonePaletteSet palettes) => UploadPalettes(palettes, 0);

    /// <summary>
    /// Packs N posed bodies into one palette set per skin, at the stride the shader reads.
    /// </summary>
    /// <remarks>
    /// Palette packing lives here because this type owns the per-skin inverse-bind state. Callers
    /// provide poses and placements without duplicating the skin loop.
    /// </remarks>
    /// <param name="poses">One per body, in instance order.</param>
    /// <param name="placements">Where each body stands. Same length as <paramref name="poses"/>.</param>
    /// <param name="into">One set per skin, reset by this call.</param>
    public void PackPalettes(
        IReadOnlyList<Pose> poses, IReadOnlyList<Matrix4x4> placements, IReadOnlyList<BonePaletteSet> into)
    {
        ArgumentNullException.ThrowIfNull(poses);
        ArgumentNullException.ThrowIfNull(placements);
        ArgumentNullException.ThrowIfNull(into);
        if (poses.Count != placements.Count)
        {
            throw new ArgumentException(
                $"{poses.Count} pose(s) and {placements.Count} placement(s); a body needs both.",
                nameof(placements));
        }

        if (into.Count != Skins.Count)
        {
            throw new ArgumentException(
                $"{into.Count} palette set(s) for {Skins.Count} skin(s). Each skin has its own inverse "
                + "binds, so sharing one set would draw some bodies at another skin's bind pose.",
                nameof(into));
        }

        foreach (var set in into) set.Reset();
        for (var body = 0; body < poses.Count; body++)
        {
            for (var s = 0; s < Skins.Count; s++)
            {
                into[s].Add(Skins[s].Skeleton, poses[body], Skins[s].MeshNodeTransform * placements[body]);
            }
        }
    }

    /// <summary>One palette set per skin, sized for this rig.</summary>
    public BonePaletteSet[] CreatePaletteSets(int instances) =>
        Skins.Select(s => new BonePaletteSet(s.Skeleton.BoneCount, instances)).ToArray();

    /// <summary>Copies one skin's live instance palettes into that skin's frame buffer.</summary>
    /// <remarks>
    /// A shared pose can produce different palette matrices for skins with different inverse binds.
    /// </remarks>
    public void UploadPalettes(BonePaletteSet palettes, int skinIndex)
    {
        ArgumentNullException.ThrowIfNull(palettes);
        if ((uint)skinIndex >= (uint)skinBones.Count)
        {
            throw new ArgumentOutOfRangeException(
                nameof(skinIndex), skinIndex, $"this rig has {skinBones.Count} skin(s).");
        }

        if (palettes.BoneCount != Skins[skinIndex].Skeleton.BoneCount)
        {
            throw new ArgumentException(
                $"Palette set is packed at a stride of {palettes.BoneCount}; this rig has " +
                $"{Skins[skinIndex].Skeleton.BoneCount} bones. The shader multiplies by the stride, " +
                "so a mismatch " +
                $"renders other instances' poses rather than failing.",
                nameof(palettes));
        }

        var live = palettes.LiveMatrixCount;
        if (live > PaletteMatrixCapacity)
        {
            throw new InvalidOperationException(
                $"Studio palette needs {live} matrices ({palettes.BoneCount} bones x " +
                $"{palettes.Count} instances), but its reflected buffer holds {PaletteMatrixCapacity}.");
        }
        for (var i = 0; i < live; i++)
        {
            MemoryMarshal.Write(palettePayload.AsSpan(i * 64, 64), in palettes.Matrices[i]);
        }

        skinBones[skinIndex].WriteBuffer(device.CurrentFrameSlot, 0, palettePayload.AsSpan(0, live * 64));
    }

    /// <summary>Computes each bone's object-space world transform under <paramref name="pose"/>.</summary>
    /// <remarks>Studio convenience wrapper over the skeleton's shared hierarchy walk.</remarks>
    public static void ComputeBoneWorlds(Skeleton skeleton, Pose pose, Matrix4x4[] outWorlds)
    {
        ArgumentNullException.ThrowIfNull(skeleton);
        ArgumentNullException.ThrowIfNull(pose);
        ArgumentNullException.ThrowIfNull(outWorlds);
        skeleton.ComputeBoneWorlds(pose, outWorlds);
    }

    /// <summary>The clip with this name, ignoring an exporter's `Armature|` prefix; null if absent.</summary>
    public AnimationClip? Clip(string name)
    {
        foreach (var clip in Clips)
        {
            if (clip.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) return clip;
            var bar = clip.Name.LastIndexOf('|');
            var bare = bar >= 0 ? clip.Name[(bar + 1)..] : clip.Name;
            if (bare.Equals(name, StringComparison.OrdinalIgnoreCase)) return clip;
        }

        return null;
    }

    private TextureHandle UploadAlbedo(
        VulkanGraphicsDevice vk,
        GltfTexture? texture,
        TextureHandle white,
        TextureRegistry uploaded)
    {
        if (texture is null) return white;

        // Source imports provide eager mip bytes; cooked imports may provide a lazy .blixtex handle.
        // Material upload accepts both representations and realizes lazy mips only at this boundary.
        var mips = texture.MipBytes is { Count: > 0 } eager
            ? eager
            : texture.LazyHandle is { } lazy
                ? Enumerable.Range(0, lazy.MipCount).Select(i => BlixTexReader.ReadMip(lazy, i)).ToArray()
                : null;

        if (mips is null || mips.Count == 0)
        {
            // Reported rather than silently white. A texture that exists and cannot be read is a
            // bug somewhere, and a white stand-in is exactly what hides it.
            Console.WriteLine($"[lab] albedo '{texture.Name}' has no readable mips — drawing white.");
            return white;
        }

        // Preserve a prebuilt mip chain verbatim. In particular, block-compressed textures cannot
        // rely on the renderable-format blit path used to generate mips from a single level.
        return uploaded.GetOrAdd(texture, texture.Format, () =>
        {
            var description = new TextureDescription(
                texture.Width, texture.Height, texture.Format, SamplerDescription.LinearRepeat);
            var handle = mips.Count > 1
                ? vk.CreateTexture2DMipped(description, mips, $"lab.rig.albedo.{texture.Name}")
                : vk.CreateTexture2D(description, mips[0], $"lab.rig.albedo.{texture.Name}");

            ownedTextures.Add(handle);
            images.Add(new Image(
                string.IsNullOrEmpty(texture.Name) ? $"albedo {images.Count}" : texture.Name,
                handle, texture.Width, texture.Height));
            return handle;
        });
    }

    /// <summary>Refuses a rig that cannot fit Studio's maximum instance row.</summary>
    internal static void ValidatePaletteCapacity(
        string sourcePath,
        IReadOnlyList<GltfSkinBinding> skins)
    {
        ArgumentNullException.ThrowIfNull(sourcePath);
        ArgumentNullException.ThrowIfNull(skins);

        for (var skin = 0; skin < skins.Count; skin++)
        {
            var bones = skins[skin].Skeleton.BoneCount;
            var required = checked(bones * MaxInstances);
            if (required <= PaletteMatrixCapacity) continue;

            throw new AssetImportException(
                sourcePath,
                null,
                $"skin {skin} has {bones} bones; Studio supports at most {MaxBones} bones across " +
                $"its {MaxInstances}-instance reference row ({PaletteMatrixCapacity} palette matrices)");
        }
    }

    // Measure rest-pose bounds through MeshNodeTransform, matching the model-space transform used by
    // the draw. The rest palette is identity by construction, so clip selection cannot affect framing.
    private void AccumulateRestBounds(MeshData mesh)
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
            var point = Vector3.Transform(local, MeshNodeTransform);
            BoundsMin = Vector3.Min(BoundsMin, point);
            BoundsMax = Vector3.Max(BoundsMax, point);
        }
    }

    public void Dispose()
    {
        foreach (var part in parts)
        {
            device.DestroyVertexBuffer(part.Vertices);
            device.DestroyIndexBuffer(part.Indices);
        }

        // Attachments own buffers too. Their textures do not need freeing here — they come from the
        // same `uploaded` cache the parts use and are already in ownedTextures, so releasing them
        // again would be a double free.
        foreach (var attachment in attachments)
        {
            device.DestroyVertexBuffer(attachment.Vertices);
            device.DestroyIndexBuffer(attachment.Indices);
        }

        foreach (var texture in ownedTextures) device.DestroyTexture(texture);
        ownedTextures.Clear();
        images.Clear();
        parts.Clear();
        attachments.Clear();
        // Every skin's buffer, not just skin 0's. `bones` aliases skinBones[0], so destroying both
        // would double-free it.
        foreach (var slot in skinBones) device.DestroyMaterial(slot.Handle);
    }
}

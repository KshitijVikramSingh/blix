using System.Numerics;
using System.Runtime.InteropServices;
using Blix;
using Blix.Assets;
using Blix.Graphics;
using Blix.Graphics.Vulkan;

namespace Blix.Tools.Studio;

/// <summary>
/// An imported glTF kept as a SKELETON and its clips, rather than as a node hierarchy.
/// </summary>
/// <remarks>
/// <b>The sibling of <see cref="StudioModel"/>, and deliberately not a mode of it.</b> A static asset raises
/// "where is this part's pivot"; a rigged one raises "is the motion right", and the two are answered by
/// different data out of different importers. <c>GltfStaticImporter.ImportNodes</c> keeps the authored
/// node tree and hands back <c>VertexPosition3NormalTexture</c>; <c>GltfImporter.Import</c> fuses the
/// skin's primitives and hands back <c>Skin4Tangent</c> vertices plus a <see cref="Skeleton"/> and every
/// clip. Making one type serve both would mean a type where half the fields are null.
/// <para>
/// <b>What this owns and what it does not.</b> It owns GPU residency (vertex/index buffers, the albedo,
/// the bone-palette buffer) and the bind-time facts a viewer needs to draw a skeleton. It does NOT own a
/// clock: <see cref="Blix.ClipPlayer"/> does, and a lab that wants two of them for a blend makes two.
/// </para>
/// </remarks>
public sealed class StudioRig : IDisposable
{
    /// <summary>Matches the fixed bound in <c>studio_skinned.vert</c>.</summary>
    /// <remarks>
    /// Public so the probe can check a rig against it without a device. A rig over this draws nothing
    /// useful — the shader would index past its array — and finding that out at load is a message,
    /// while finding it out on the GPU is a hang.
    /// </remarks>
    public const int MaxBones = 128;

    /// <summary>How many independently posed bodies one palette buffer holds.</summary>
    /// <remarks>
    /// <b>The number that closes the gap this rig type used to document.</b> One palette binding
    /// served one pose per frame, so two same-frame draws sharing it both rendered the second — fine
    /// for a lab with one subject and the first thing a game breaks. The buffer is now
    /// <see cref="MaxBones"/> x this, sliced by instance, which is the shape Bulwark and RTSGame
    /// already use for their crowds.
    /// <para>
    /// Eight rather than a crowd: the lab's question is "are these poses independent", which three
    /// bodies answer and three hundred only make slower. <c>MaxBones * MaxInstances * 64</c> = 64 KB,
    /// allocated once per rig and short-written per frame.
    /// </para>
    /// </remarks>
    public const int MaxInstances = 8;

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
        /// <remarks>
        /// <b>Two skins can share every joint and still disagree about the bind pose.</b> tank.glb's
        /// tracks are bound 3.97 from its hull, so drawing a part against another skin's palette
        /// puts it somewhere plausible and wrong.
        /// </remarks>
        int SkinIndex,
        /// <summary>
        /// What the material says about its own surface: <c>OPAQUE</c>/<c>MASK</c>/<c>BLEND</c>, the
        /// cutout threshold, and whether the back face is part of the model.
        /// </summary>
        /// <remarks>
        /// <b><see cref="GltfMaterial"/> has carried these for a long time and nothing on this stage
        /// read them.</b> Of the three, only <c>DoubleSided</c> currently changes a picture in this
        /// tree — measured, not assumed: all 26 MASK materials here have no base-colour texture and
        /// <c>baseAlpha = 1.00</c> against a 0.20 cutoff, so their alpha is 1.0 everywhere and a
        /// faithful cutout would discard nothing. The alpha pair is carried because it is free once
        /// the material is threaded and because the next asset may mean it, NOT because it is
        /// demonstrated here.
        /// </remarks>
        /// <summary>The material's <c>baseColorFactor.a</c>, which the cutout test multiplies in.</summary>
        float BaseAlpha = 1f,
        GltfAlphaMode AlphaMode = GltfAlphaMode.Opaque,
        float AlphaCutoff = 0.5f,
        bool DoubleSided = false);

    /// <summary>
    /// A static mesh carried by a joint — a knife in a hand, a cape on a chest.
    /// </summary>
    /// <remarks>
    /// <b>Uploaded exactly like a <see cref="Part"/>, drawn nothing like one.</b> The geometry is
    /// static, so it goes through the standard lit pipeline with an ordinary model matrix rather
    /// than the skinned one with a palette — which is the whole reason it is a separate record and
    /// not a Part with a flag.
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
        TextureHandle Albedo);

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

    /// <summary>Every clip in the file, ordered by name so two runs of the lab list them the same way.</summary>
    public IReadOnlyList<AnimationClip> Clips { get; private set; } = Array.Empty<AnimationClip>();

    public IReadOnlyList<Part> Parts => parts;

    /// <summary>Static meshes the asset hangs off joints. Empty for most rigs.</summary>
    public IReadOnlyList<Attachment> Attachments => attachments;

    /// <summary>Mesh nodes the import declined, and why. Empty when the file was read whole.</summary>
    /// <remarks>
    /// <b>Kept because a tool that cannot say what it dropped is the fault these records exist to
    /// fix.</b> The importer learned to report skipped nodes and unread attributes, and both landed
    /// in <c>AssetLoadLog</c> — a channel only the test suites drain. So the reports were being
    /// produced and, in the one tool whose entire job is looking at assets, shown to nobody. Holding
    /// them on the rig costs a reference and makes the panel possible.
    /// </remarks>
    /// <summary>
    /// Static geometry the model carries that hangs off no joint — a turret, a gun.
    /// </summary>
    /// <remarks>
    /// <b>Not an <see cref="Attachment"/>, because an attachment follows a joint and this does not.</b>
    /// Reusing that record would need a joint index meaning "no joint" — a contradiction sitting in
    /// a field name — and would drop scenery into the viewer's attachment picker, where the
    /// meaningful act is choosing which weapon a hand holds.
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
        TextureHandle Albedo);

    public IReadOnlyList<StaticPart> StaticParts => staticParts;

    /// <summary>Vertex attributes the file declared that the importer did not read.</summary>
    public IReadOnlyList<GltfIgnored> Ignored { get; private set; } = Array.Empty<GltfIgnored>();

    /// <summary>The distinct base-colour images this asset uploaded, deduped per source texture.</summary>
    /// <remarks>
    /// <b>What an asset viewer could never show.</b> The lab has reported texture COUNTS since it
    /// learned to load a model — "1 image across 12 parts" — and a count is the least interesting
    /// fact about a texture. Which image, at what size, and whether it is the one you meant are all
    /// answerable by looking, and until the UI layer could draw a texture there was nowhere to look.
    /// </remarks>
    public IReadOnlyList<Image> Images => images;

    /// <summary>The skin node's ancestor chain, composed. Goes into uModel BEFORE the user transform.</summary>
    public Matrix4x4 MeshNodeTransform { get; private set; } = Matrix4x4.Identity;

    public string SourcePath { get; private set; } = string.Empty;

    /// <summary>Bounds of the mesh in its REST pose, in mesh-node space. What the camera frames on.</summary>
    public Vector3 BoundsMin { get; private set; } = new(float.MaxValue);

    public Vector3 BoundsMax { get; private set; } = new(float.MinValue);

    public int VertexCount { get; private set; }

    /// <summary>Per bone: does some vertex carry a non-zero weight for it? Literally, with no promotion.</summary>
    /// <remarks>
    /// <b>A finding from looking at the first skeleton the lab drew.</b> The Rogue has 41 bones and the
    /// overlay was unreadable — a star of lines radiating from the feet — which looked like a bug and
    /// was not: most of those bones are IK handles and roll controls (<c>kneeIK.l</c>,
    /// <c>control-heel-roll.r</c>, <c>handIK.l</c>) parented straight to the root, skinning nothing.
    /// They are in the file because an animator posed through them, and they are in the palette because
    /// the exporter had no reason to drop them.
    /// <para>
    /// This is the census: the bones the mesh is actually attached to. It is <em>not</em> what the
    /// overlay filters on — see <see cref="DeformHierarchy"/>, and see why they differ.
    /// </para>
    /// </remarks>
    public IReadOnlyList<bool> WeightedBones => weightedBones;

    /// <summary>How many bones some vertex weights. The rest are controls the mesh never sees.</summary>
    public int WeightedBoneCount { get; private set; }

    /// <summary>Weighted bones <em>plus every ancestor that carries one</em> — what it takes to DRAW the chain.</summary>
    /// <remarks>
    /// <b>A superset of <see cref="WeightedBones"/>, and the distinction is not pedantry.</b> A joint that
    /// no vertex weights can still sit in the middle of a chain that several do — the Rogue's <c>root</c>
    /// is exactly that, weighted by nothing and the parent of everything. Drawing only the weighted set
    /// leaves such a chain as floating segments, which reads as a broken skeleton.
    /// <para>
    /// They were one property once, and the name said "does any vertex weight this" while the value had
    /// silently been promoted up the ancestry. A count reported from it would have meant "bones needed to
    /// draw the deformation ancestry" while claiming to mean "bones the mesh follows" — a number that is
    /// right, labelled with a question it does not answer. Two names, because they are two facts.
    /// </para>
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
    /// <remarks>
    /// <b>A palette buffer each, because a palette matrix is inverse-bind times world.</b> The POSE
    /// can be shared and is — the joints are the same nodes — but the inverse binds belong to the
    /// skin, so the products differ and each needs somewhere to live.
    /// </remarks>
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
        rig.Skeleton = imported.Skeleton;

        rig.Ignored = imported.IgnoredOrEmpty;
        rig.MeshNodeTransform = imported.MeshNodeTransform;
        rig.Clips = imported.Animations.OrderBy(c => c.Name, StringComparer.Ordinal).ToArray();
        rig.palettePayload = new byte[MaxBones * MaxInstances * 64];
        rig.weightedBones = FindWeightedBones(imported.Skeleton, imported.Primitives);
        rig.WeightedBoneCount = rig.weightedBones.Count(b => b);
        rig.deformHierarchy = PromoteToHierarchy(imported.Skeleton, rig.weightedBones);
        rig.DeformHierarchyCount = rig.deformHierarchy.Count(b => b);

        var white = vk.CreateTexture2D(
            new TextureDescription(1, 1, TextureFormat.Rgba8Srgb, SamplerDescription.LinearRepeat),
            new byte[] { 255, 255, 255, 255 }, "lab.rig.white");
        rig.ownedTextures.Add(white);

        var uploaded = new Dictionary<GltfTexture, TextureHandle>();
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
                BaseAlpha: material?.BaseColorFactor.W ?? 1f,
                AlphaMode: material?.AlphaMode ?? GltfAlphaMode.Opaque,
                AlphaCutoff: material?.AlphaCutoff ?? 0.5f,
                DoubleSided: material?.DoubleSided ?? false));
        }

        // <b>Attachments upload beside the parts and are bounded out of the rest bounds.</b> A
        // weapon is not part of the body's silhouette — including a two-handed crossbow in the
        // bounds would push the camera back and shrink the character for every viewer, whether or
        // not anything is drawing it.
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
                    Albedo: rig.UploadAlbedo(vk, material?.BaseColorTexture, white, uploaded)));
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
                    Albedo: rig.UploadAlbedo(vk, material?.BaseColorTexture, white, uploaded)));
            }
        }

        if (rig.parts.Count == 0)
        {
            rig.BoundsMin = Vector3.Zero;
            rig.BoundsMax = Vector3.Zero;
        }

        // <b>framesInFlight, and that is the whole aliasing story.</b> The palette is written at
        // RECORD time and read at Execute — the same lifetime hazard that put seven boxes at the
        // seventh's transform. One buffer per frame slot is what keeps this frame's pose from
        // overwriting the pose the GPU is still reading from the last one.
        //
        // What it does NOT solve: two draws in the SAME frame wanting different poses. They would
        // share this one buffer and both render the second. The lab draws one rig, so the question
        // does not arise here — a second rig gets a second StudioRig and a second material, which is
        // why this is per-rig rather than owned by the renderer.
        // One palette buffer per skin. The single-skin case allocates exactly what it always did.
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

    /// <summary>Copies every live instance's palette into the frame's bone buffer. Once per frame, before recording.</summary>
    /// <remarks>
    /// The 4x4s go up untransposed, exactly as conventions §2 says: GLSL reads std430 column-major,
    /// which is the transpose of the row-vector form the CPU built, so `skin * v` in the shader
    /// computes what `v_row * skin` computes here. There is no transpose in this file and there must
    /// not be one.
    /// <para>
    /// <b>Only the live prefix is sent.</b> The buffer holds eight instances' worth; three 41-bone
    /// rigs are 7,872 bytes of it, and uploading the whole 64 KB to draw three bodies is how an
    /// instance buffer comes to cost more than the draw. A short write is legal and the shader never
    /// reads past <c>instanceCount</c>.
    /// </para>
    /// </remarks>
    public void UploadPalettes(BonePaletteSet palettes) => UploadPalettes(palettes, 0);

    /// <summary>
    /// Packs N posed bodies into one palette set per skin, at the stride the shader reads.
    /// </summary>
    /// <remarks>
    /// <b>Here because this is where the skins live.</b> A palette matrix is inverse-bind times
    /// world, the inverse binds belong to the skin, and this type owns <see cref="Skins"/> — so the
    /// loop over them belongs to it rather than to each caller. It was written twice before that was
    /// true: once in the viewer's animation and once in the capture tool, and when skins became
    /// plural both copies had to learn it separately.
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
    /// <b>Per skin, because a palette is inverse-bind times world and the inverse binds are the
    /// skin's own.</b> The pose behind them is shared — tank.glb's three skins are the same joints
    /// in the same order — so this is N uploads of the same posed hierarchy through N different bind
    /// matrices, not N poses.
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

        var live = Math.Min(palettes.LiveMatrixCount, MaxBones * MaxInstances);
        for (var i = 0; i < live; i++)
        {
            MemoryMarshal.Write(palettePayload.AsSpan(i * 64, 64), in palettes.Matrices[i]);
        }

        skinBones[skinIndex].WriteBuffer(device.CurrentFrameSlot, 0, palettePayload.AsSpan(0, live * 64));
    }

    /// <summary>
    /// Each bone's object-space transform under <paramref name="pose"/> — where to DRAW a bone, not how to skin with one.
    /// </summary>
    /// <remarks>
    /// <b>Not the palette.</b> A palette matrix is `InverseBindPose * world`: it maps a rest-pose vertex
    /// to its posed place, and its translation is a displacement, not a position — drawing a skeleton
    /// from palette translations gives a heap of lines near the origin, which is the first thing anyone
    /// tries and the first thing that looks broken. This is the hierarchy walk's `world` term on its
    /// own, which is where the joint actually is.
    /// </remarks>
    public static void ComputeBoneWorlds(Skeleton skeleton, Pose pose, Matrix4x4[] outWorlds)
    {
        ArgumentNullException.ThrowIfNull(skeleton);
        ArgumentNullException.ThrowIfNull(pose);
        ArgumentNullException.ThrowIfNull(outWorlds);
        if (outWorlds.Length < skeleton.BoneCount)
        {
            throw new ArgumentException(
                $"Need {skeleton.BoneCount} matrices, got {outWorlds.Length}.", nameof(outWorlds));
        }

        for (var i = 0; i < skeleton.BoneCount; i++)
        {
            var local = pose.Locals[i].ToMatrix();
            var parent = skeleton.Bones[i].ParentIndex;
            // Row-vector compose, same direction as Skeleton.ComputeBonePalette's own walk.
            outWorlds[i] = parent < 0 ? local : local * outWorlds[parent];
        }
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
        Dictionary<GltfTexture, TextureHandle> uploaded)
    {
        if (texture is null) return white;
        if (uploaded.TryGetValue(texture, out var existing)) return existing;

        var mip0 = texture.MipBytes is { Count: > 0 } mips ? mips[0] : null;
        if (mip0 is null) return white;

        var handle = vk.CreateTexture2D(
            new TextureDescription(texture.Width, texture.Height, texture.Format, SamplerDescription.LinearRepeat),
            mip0,
            $"lab.rig.albedo.{texture.Name}");
        uploaded[texture] = handle;
        ownedTextures.Add(handle);
        images.Add(new Image(
            string.IsNullOrEmpty(texture.Name) ? $"albedo {images.Count}" : texture.Name,
            handle, texture.Width, texture.Height));
        return handle;
    }

    /// <summary>Which bones some vertex actually weights, read off the vertex data. No device needed.</summary>
    /// <remarks>
    /// <b>Static and deviceless on purpose</b> — the probe answers the same question with no GPU in
    /// sight, and a rig's bone census is a fact about the FILE. Duplicating the weight walk so the
    /// headless tool could have it would be two implementations of one answer, which is how the two
    /// come to disagree.
    /// <para>
    /// Attribute offsets come from the LAYOUT rather than from the 80-byte stride this asset happens
    /// to have: the same file could arrive without tangents one day, and a hard-coded offset would
    /// then read weights out of the middle of a texcoord and report a plausible, wrong answer.
    /// </para>
    /// <para>
    /// Returns the LITERAL set. <see cref="PromoteToHierarchy"/> is the separate step that widens it to
    /// something drawable, and it is separate precisely so a caller has to choose which one it meant.
    /// </para>
    /// </remarks>
    public static bool[] FindWeightedBones(Skeleton skeleton, IReadOnlyList<GltfPrimitive> primitives)
    {
        ArgumentNullException.ThrowIfNull(skeleton);
        ArgumentNullException.ThrowIfNull(primitives);

        var deform = new bool[skeleton.BoneCount];
        foreach (var primitive in primitives)
        {
            var mesh = primitive.Mesh;
            var indexAttribute = Attribute(mesh.Layout, location: 3);
            var weightAttribute = Attribute(mesh.Layout, location: 4);
            if (indexAttribute < 0 || weightAttribute < 0) continue;

            var stride = mesh.Layout.Stride;
            for (var v = 0; v < mesh.VertexCount; v++)
            {
                var at = v * stride;
                for (var j = 0; j < 4; j++)
                {
                    var weight = BitConverter.ToSingle(mesh.VertexBytes, at + weightAttribute + (j * 4));
                    if (weight <= 0f) continue;
                    var bone = (int)BitConverter.ToSingle(mesh.VertexBytes, at + indexAttribute + (j * 4));
                    if (bone >= 0 && bone < deform.Length) deform[bone] = true;
                }
            }
        }

        return deform;
    }

    /// <summary>Widens a weighted set to include every ancestor that carries one.</summary>
    /// <remarks>
    /// A chain drawn without the joints that carry it is a set of floating segments, which is a worse
    /// picture than the cluttered one. The hierarchy-order invariant — a parent always precedes its
    /// child — makes this one backwards pass with no recursion and no visited set.
    /// <para>
    /// Does not mutate its argument: the literal census and the drawable set are both wanted, by
    /// different callers, from the same load.
    /// </para>
    /// </remarks>
    public static bool[] PromoteToHierarchy(Skeleton skeleton, IReadOnlyList<bool> weighted)
    {
        ArgumentNullException.ThrowIfNull(skeleton);
        ArgumentNullException.ThrowIfNull(weighted);

        var hierarchy = new bool[skeleton.BoneCount];
        for (var i = 0; i < hierarchy.Length && i < weighted.Count; i++) hierarchy[i] = weighted[i];

        for (var i = hierarchy.Length - 1; i >= 0; i--)
        {
            if (!hierarchy[i]) continue;
            var parent = skeleton.Bones[i].ParentIndex;
            if (parent >= 0) hierarchy[parent] = true;
        }

        return hierarchy;
    }

    private static int Attribute(VertexLayout layout, int location)
    {
        foreach (var attribute in layout.Attributes)
        {
            if (attribute.Location == location) return attribute.Offset;
        }

        return -1;
    }

    // Bounds of the mesh at REST, in mesh-node space.
    //
    // <b>Through MeshNodeTransform, because that is what the draw goes through.</b> The Rogue's skin
    // node is identity, but the RTS villager's carries a hundredfold scale — measuring in raw vertex
    // space there gives a body a hundred times too small, and the camera frames on empty air. Third
    // time in this project that measuring through a different transform from the drawing was the bug.
    //
    // Rest rather than posed, so the framing describes the asset rather than whichever clip happened
    // to be selected. At rest the bone palette is the identity by construction (BindWorld ×
    // InverseBindPose = I), so no palette appears here — that identity is why it can be left out, not
    // an assumption that skinning does nothing.
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

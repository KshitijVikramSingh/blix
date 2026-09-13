using System.Numerics;
using System.Runtime.InteropServices;
using Blix.Assets;
using Blix.Graphics;
using Blix.Graphics.Vulkan;

namespace Blix.Labs.Toolchain;

/// <summary>
/// An imported glTF kept as a SKELETON and its clips, rather than as a node hierarchy.
/// </summary>
/// <remarks>
/// <b>The sibling of <see cref="LabModel"/>, and deliberately not a mode of it.</b> A static asset raises
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
public sealed class LabRig : IDisposable
{
    /// <summary>Matches the fixed bound in <c>lab_skinned.vert</c>.</summary>
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
        TextureHandle Albedo);

    private VulkanGraphicsDevice device = null!;
    private readonly List<Part> parts = new();
    private readonly List<TextureHandle> ownedTextures = new();
    private MaterialBindings bones = null!;
    private byte[] palettePayload = Array.Empty<byte>();

    public Skeleton Skeleton { get; private set; } = null!;

    /// <summary>Every clip in the file, ordered by name so two runs of the lab list them the same way.</summary>
    public IReadOnlyList<AnimationClip> Clips { get; private set; } = Array.Empty<AnimationClip>();

    public IReadOnlyList<Part> Parts => parts;

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

    /// <summary>The set-3 palette binding, handed to every skinned draw in the frame.</summary>
    public MaterialHandle BoneMaterial => bones.Handle;

    /// <summary>
    /// Loads the skin, its clips and its skeleton, and creates the per-frame bone-palette buffer.
    /// </summary>
    /// <param name="skinnedProgram">
    /// The program whose set-3 slot describes the palette buffer. Reflected, so the buffer's size comes
    /// from the shader's own declaration rather than from a number restated here.
    /// </param>
    public static LabRig Load(VulkanGraphicsDevice vk, string path, ShaderProgramHandle skinnedProgram)
    {
        var rig = new LabRig { device = vk, SourcePath = path };

        var imported = new GltfImporter().Import(new AssetImportContext(AssetId.Parse("lab.rig"), path));
        rig.Skeleton = imported.Skeleton;
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
                Vertices: vb,
                Indices: ib,
                IndexCount: mesh.IndexCount,
                BaseColour: material is null
                    ? new Vector3(0.75f)
                    : new Vector3(
                        material.BaseColorFactor.X, material.BaseColorFactor.Y, material.BaseColorFactor.Z),
                Metallic: material?.MetallicFactor ?? 0f,
                Roughness: material?.RoughnessFactor ?? 0.7f,
                Albedo: rig.UploadAlbedo(vk, material?.BaseColorTexture, white, uploaded)));
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
        // does not arise here — a second rig gets a second LabRig and a second material, which is
        // why this is per-rig rather than owned by the renderer.
        rig.bones = vk.CreateMaterial(
            skinnedProgram, setIndex: 3, framesInFlight: vk.MaxFramesInFlightCount, name: "lab.rig.bones");

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
    public void UploadPalettes(BonePaletteSet palettes)
    {
        ArgumentNullException.ThrowIfNull(palettes);
        if (palettes.BoneCount != Skeleton.BoneCount)
        {
            throw new ArgumentException(
                $"Palette set is packed at a stride of {palettes.BoneCount}; this rig has " +
                $"{Skeleton.BoneCount} bones. The shader multiplies by the stride, so a mismatch " +
                $"renders other instances' poses rather than failing.",
                nameof(palettes));
        }

        var live = Math.Min(palettes.LiveMatrixCount, MaxBones * MaxInstances);
        for (var i = 0; i < live; i++)
        {
            MemoryMarshal.Write(palettePayload.AsSpan(i * 64, 64), in palettes.Matrices[i]);
        }

        bones.WriteBuffer(device.CurrentFrameSlot, 0, palettePayload.AsSpan(0, live * 64));
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

        foreach (var texture in ownedTextures) device.DestroyTexture(texture);
        ownedTextures.Clear();
        parts.Clear();
        device.DestroyMaterial(bones.Handle);
    }
}

using System.Numerics;
using System.Runtime.InteropServices;
using Blix.Assets;
using Blix.Geometry;
using Blix.Graphics;
using Blix.Render;

namespace Blix;

/// <summary>A skinned glTF resident on a device: its parts, skins, clips and joint attachments.</summary>
/// <remarks>
/// <b>Residency, and nothing about how it is drawn or animated.</b> Skinned parts, static parts and
/// attachments are uploaded <see cref="Mesh"/>es with their glTF materials and resolved textures; each skin
/// keeps its skeleton and the frame its meshes were authored in. Clocks, blending and poses are
/// <see cref="ClipPlayer"/> and the pose operations; packing poses into palettes is
/// <see cref="BonePaletteSet"/>; the buffers a skinned shader reads them from are <see cref="BoneBuffers"/>,
/// made against the program that reads them. Made by <see cref="ResidencyExtensions.CreateRig"/>.
/// </remarks>
public sealed class Rig : IDisposable
{
    private readonly IGraphicsDevice device;
    private readonly List<SkinnedPart> parts = new();
    private readonly List<StaticPart> staticParts = new();
    private readonly List<Attachment> attachments = new();
    private readonly List<Skin> skins = new();

    private Rig(IGraphicsDevice device, string name)
    {
        this.device = device;
        Name = name;
    }

    /// <summary>One skin: the skeleton it poses and the frame its meshes were authored in.</summary>
    public sealed record Skin(Skeleton Skeleton, Matrix4x4 MeshNodeTransform);

    /// <summary>A part the palette deforms, drawn against <see cref="SkinIndex"/>'s bones.</summary>
    public sealed record SkinnedPart(Mesh Mesh, int SkinIndex, GltfMaterial? Material, MaterialTextures Textures);

    /// <summary>Geometry the asset carries that follows no joint, at its authored world transform.</summary>
    public sealed record StaticPart(string Name, Matrix4x4 World, Mesh Mesh, GltfMaterial? Material, MaterialTextures Textures);

    /// <summary>A static mesh carried by a joint: placed by the joint's world transform, not skinned.</summary>
    public sealed record Attachment(
        string Name, string JointName, int JointIndex, Matrix4x4 Local, Mesh Mesh, GltfMaterial? Material,
        MaterialTextures Textures);

    public string Name { get; }

    public IReadOnlyList<Skin> Skins => skins;

    /// <summary>Skin 0's skeleton: most rigs have one skin. Multi-skin files read <see cref="Skins"/>.</summary>
    public Skeleton Skeleton => skins[0].Skeleton;

    /// <summary>Skin 0's authoring frame, which goes before a placement.</summary>
    public Matrix4x4 MeshNodeTransform => skins[0].MeshNodeTransform;

    public IReadOnlyList<SkinnedPart> Parts => parts;

    public IReadOnlyList<StaticPart> StaticParts => staticParts;

    public IReadOnlyList<Attachment> Attachments => attachments;

    /// <summary>Every clip in the file, ordered by name so two runs list them the same way.</summary>
    public IReadOnlyList<AnimationClip> Clips { get; private set; } = Array.Empty<AnimationClip>();

    /// <summary>What the importer declined to read, with its reasons.</summary>
    public IReadOnlyList<GltfIgnored> Ignored { get; private set; } = Array.Empty<GltfIgnored>();

    /// <summary>The skinned parts' rest-pose bounds in skin 0's authoring frame; zero-sized without parts.</summary>
    /// <remarks>Static parts and attachments do not widen it: it describes the body, not its equipment.</remarks>
    public Bounds3 RestBounds { get; private set; } = new(Vector3.Zero, Vector3.Zero);

    /// <summary>The largest rest-bounds dimension, for framing an asset of unknown scale; 1 without parts.</summary>
    public float LongestExtent
    {
        get
        {
            if (parts.Count == 0) return 1f;
            var size = RestBounds.Max - RestBounds.Min;
            return MathF.Max(size.X, MathF.Max(size.Y, size.Z));
        }
    }

    /// <summary>Vertices over the skinned parts.</summary>
    public int VertexCount { get; private set; }

    /// <summary>Per bone of skin 0, whether any vertex weights it.</summary>
    public IReadOnlyList<bool> WeightedBones { get; private set; } = Array.Empty<bool>();

    /// <summary>The weighted bones plus every ancestor needed to draw their chains unbroken.</summary>
    public IReadOnlyList<bool> DeformHierarchy { get; private set; } = Array.Empty<bool>();

    /// <summary>The clip with this name, ignoring an exporter's <c>Armature|</c> prefix; null if absent.</summary>
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

    /// <summary>One palette set per skin, sized for <paramref name="instances"/> bodies.</summary>
    public BonePaletteSet[] CreatePaletteSets(int instances) =>
        skins.Select(s => new BonePaletteSet(s.Skeleton.BoneCount, instances)).ToArray();

    /// <summary>Packs N posed bodies into one palette set per skin, each body at its placement.</summary>
    /// <remarks>
    /// Per skin, because each has its own inverse binds: one pose gives different palettes for two skins.
    /// Each body is <c>MeshNodeTransform * placement</c>, so a set holds world-space palettes.
    /// </remarks>
    public void PackPalettes(IReadOnlyList<Pose> poses, IReadOnlyList<Matrix4x4> placements, IReadOnlyList<BonePaletteSet> into)
    {
        ArgumentNullException.ThrowIfNull(poses);
        ArgumentNullException.ThrowIfNull(placements);
        ArgumentNullException.ThrowIfNull(into);
        if (poses.Count != placements.Count)
        {
            throw new ArgumentException(
                $"{poses.Count} pose(s) and {placements.Count} placement(s); a body needs both.", nameof(placements));
        }

        if (into.Count != skins.Count)
        {
            throw new ArgumentException(
                $"{into.Count} palette set(s) for {skins.Count} skin(s). Each skin has its own inverse binds, so " +
                "sharing one set would draw some bodies at another skin's bind pose.", nameof(into));
        }

        foreach (var set in into) set.Reset();
        for (var body = 0; body < poses.Count; body++)
        {
            for (var s = 0; s < skins.Count; s++) into[s].Add(skins[s].Skeleton, poses[body], skins[s].MeshNodeTransform * placements[body]);
        }
    }

    /// <summary>
    /// The buffers a skinned shader reads this rig's palettes from, one per skin, laid out by
    /// <paramref name="program"/>'s set 3 and sized for <paramref name="maxInstances"/> bodies.
    /// </summary>
    /// <remarks>
    /// The one place residency needs a program: descriptor sets take their layout from a shader's
    /// reflection, so a bone buffer exists against the program that will read it. A program without set 3
    /// is refused by the device when the buffers are made. <c>skinning.glsl</c> declares the block every
    /// skinned program reads.
    /// </remarks>
    public BoneBuffers CreateBoneBuffers(ShaderProgramHandle program, int maxInstances)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxInstances, 1);
        var bindings = skins
            .Select((s, i) => device.CreateMaterial(
                program, setIndex: DescriptorSets.Draw, framesInFlight: device.MaxFramesInFlightCount,
                name: $"{Name}.bones.{i}"))
            .ToArray();
        return new BoneBuffers(device, bindings, skins.Select(s => s.Skeleton.BoneCount).ToArray(), maxInstances);
    }

    internal static Rig Load(IGraphicsDevice device, GltfModel source, GltfTextureLoader textures, string name)
    {
        var rig = new Rig(device, name);
        foreach (var skin in source.SkinsOrEmpty) rig.skins.Add(new Skin(skin.Skeleton, skin.MeshNodeTransform));
        rig.Clips = source.Animations.OrderBy(c => c.Name, StringComparer.Ordinal).ToArray();
        rig.Ignored = source.IgnoredOrEmpty;
        var weighted = SkinningAnalysis.FindWeightedBones(source.Skeleton, source.Primitives);
        rig.WeightedBones = weighted;
        rig.DeformHierarchy = SkinningAnalysis.IncludeAncestors(source.Skeleton, weighted);

        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        for (var i = 0; i < source.Primitives.Length; i++)
        {
            var primitive = source.Primitives[i];
            rig.VertexCount += primitive.Mesh.VertexCount;
            Model.Accumulate(primitive.Mesh, source.MeshNodeTransform, ref min, ref max);
            rig.parts.Add(new SkinnedPart(
                device.CreateMesh(primitive.Mesh, $"{name}.{i}"), primitive.SkinIndex, primitive.Material,
                textures.Load(primitive.Material)));
        }

        if (rig.parts.Count > 0) rig.RestBounds = new Bounds3(min, max);

        for (var i = 0; i < source.StaticPartsOrEmpty.Length; i++)
        {
            var part = source.StaticPartsOrEmpty[i];
            for (var p = 0; p < part.Primitives.Length; p++)
            {
                var primitive = part.Primitives[p];
                rig.staticParts.Add(new StaticPart(
                    part.Primitives.Length > 1 ? $"{part.Name}.{p}" : part.Name, part.WorldTransform,
                    device.CreateMesh(primitive.Mesh, $"{name}.static.{i}.{p}"), primitive.Material,
                    textures.Load(primitive.Material)));
            }
        }

        for (var i = 0; i < source.AttachmentsOrEmpty.Length; i++)
        {
            var attachment = source.AttachmentsOrEmpty[i];
            for (var p = 0; p < attachment.Primitives.Length; p++)
            {
                var primitive = attachment.Primitives[p];
                rig.attachments.Add(new Attachment(
                    attachment.Primitives.Length > 1 ? $"{attachment.Name}.{p}" : attachment.Name,
                    attachment.JointName, attachment.JointIndex, attachment.LocalTransform,
                    device.CreateMesh(primitive.Mesh, $"{name}.attach.{i}.{p}"), primitive.Material,
                    textures.Load(primitive.Material)));
            }
        }

        return rig;
    }

    /// <summary>Destroys every uploaded buffer. Textures belong to the loader; bone buffers to their owner.</summary>
    public void Dispose()
    {
        foreach (var mesh in parts.Select(p => p.Mesh).Concat(staticParts.Select(p => p.Mesh)).Concat(attachments.Select(a => a.Mesh)))
        {
            device.DestroyVertexBuffer(mesh.VertexBuffer);
            device.DestroyIndexBuffer(mesh.IndexBuffer);
        }

        parts.Clear();
        staticParts.Clear();
        attachments.Clear();
    }
}

/// <summary>A rig's palette buffers, one per skin, and the copy from palette sets into them.</summary>
/// <remarks>
/// The 4x4s go up untransposed (conventions §2): GLSL reads std430 column-major, the transpose of the
/// row-vector form the CPU built, so <c>skin * v</c> in the shader computes what <c>v_row * skin</c>
/// computes here. Only the live prefix is written each frame, into the current frame slot's buffer.
/// </remarks>
public sealed class BoneBuffers : IDisposable
{
    private readonly IGraphicsDevice device;
    private readonly IMaterialBindings[] bindings;
    private readonly int[] boneCounts;
    private readonly byte[] payload;

    internal BoneBuffers(IGraphicsDevice device, IMaterialBindings[] bindings, int[] boneCounts, int maxInstances)
    {
        this.device = device;
        this.bindings = bindings;
        this.boneCounts = boneCounts;
        MaxInstances = maxInstances;
        payload = new byte[(boneCounts.Length == 0 ? 0 : boneCounts.Max()) * maxInstances * 64];
    }

    /// <summary>How many bodies each skin's buffer holds.</summary>
    public int MaxInstances { get; }

    /// <summary>The per-draw set a skinned part of <paramref name="skin"/> binds.</summary>
    public IMaterialBindings For(int skin) => bindings[skin];

    /// <summary>Copies one skin's live palettes into this frame's buffer.</summary>
    public void Upload(int skin, BonePaletteSet palettes)
    {
        ArgumentNullException.ThrowIfNull(palettes);
        if ((uint)skin >= (uint)bindings.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(skin), skin, $"this rig has {bindings.Length} skin(s).");
        }

        if (palettes.BoneCount != boneCounts[skin])
        {
            throw new ArgumentException(
                $"Palette set is packed at a stride of {palettes.BoneCount}; skin {skin} has {boneCounts[skin]} bones. " +
                "The shader multiplies by the stride, so a mismatch renders other instances' poses rather than failing.",
                nameof(palettes));
        }

        if (palettes.Count > MaxInstances)
        {
            throw new InvalidOperationException(
                $"{palettes.Count} bodies for buffers made for {MaxInstances}; make the bone buffers larger.");
        }

        var live = palettes.LiveMatrixCount;
        for (var i = 0; i < live; i++) MemoryMarshal.Write(payload.AsSpan(i * 64, 64), in palettes.Matrices[i]);
        bindings[skin].WriteBuffer(device.CurrentFrameSlot, 0, payload.AsSpan(0, live * 64));
    }

    public void Dispose()
    {
        foreach (var binding in bindings) device.DestroyMaterial(binding.Handle);
    }
}

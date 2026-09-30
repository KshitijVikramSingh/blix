using System.Numerics;
using Blix.Assets;
using Blix.Diagnostics;
using Blix.Geometry;
using Blix.Graphics;
using Blix.Render;

namespace Blix;

/// <summary>A cooked model resident on a device: its scene graph, uploaded parts, and skins when it has any.</summary>
/// <remarks>
/// <para>
/// <b>Residency, and nothing about how it is drawn.</b> Each node keeps its name, parent, transforms and
/// world bounds; each primitive is an uploaded <see cref="Mesh"/> with its glTF material and resolved
/// textures, in the layout the load asked for (<see cref="ModelNeeds"/>). Which pipeline draws it, what
/// colour stands in for a missing material and how many times it is drawn are the renderer's.
/// </para>
/// <para>
/// <b>One type for static and rigged models.</b> A rig is a model some of whose parts a skin deforms;
/// its skeletons, clips, palette packing and bone buffers are members here, and each refuses by name on
/// a model with no skin. Skinned parts, the static parts beside them and the meshes a joint carries are
/// views over the same uploaded parts. Made by <see cref="ResidencyExtensions.CreateModel"/>.
/// </para>
/// </remarks>
public sealed class Model : IDisposable
{
    private readonly IGraphicsDevice device;
    private readonly List<Node> nodes = new();
    private readonly List<Part> parts = new();
    private readonly List<Skin> skins = new();
    private readonly List<Part> staticParts = new();
    private readonly List<Attachment> attachments = new();

    private Model(IGraphicsDevice device, string name)
    {
        this.device = device;
        Name = name;
    }

    /// <summary>A node of the scene graph, drawable or not.</summary>
    /// <param name="Bounds">World-space bounds of its own primitives' vertices; null for a node without any.</param>
    public sealed record Node(
        int Index, string Name, int ParentIndex, Matrix4x4 Local, Matrix4x4 World,
        int PrimitiveCount, int VertexCount, Bounds3? Bounds);

    /// <summary>One uploaded primitive, in its mesh's space, placed by <see cref="NodeIndex"/>.</summary>
    /// <param name="SkinIndex">The skin that deforms it, or -1 for a static part.</param>
    public sealed record Part(int NodeIndex, Mesh Mesh, PbrMaterial? Material, MaterialTextures Textures, int SkinIndex = -1);

    /// <summary>One skin: the skeleton it poses and where its geometry sits.</summary>
    public sealed record Skin(Skeleton Skeleton, Matrix4x4 SkeletonPlacement);

    /// <summary>A static part a joint carries: placed by the joint's world transform, not skinned.</summary>
    /// <param name="JointIndex">The joint's bone in <paramref name="SkinIndex"/>'s skeleton.</param>
    public sealed record Attachment(string Name, string JointName, int JointIndex, int SkinIndex, Matrix4x4 Local, Part Part);

    /// <summary>The name its GPU resources were created under.</summary>
    public string Name { get; }

    public IReadOnlyList<Node> Nodes => nodes;

    /// <summary>Every uploaded part, static and skinned, in node order.</summary>
    public IReadOnlyList<Part> Parts => parts;

    /// <summary>World-space bounds over every node with geometry; zero-sized for a model without any.</summary>
    public Bounds3 Bounds { get; private set; } = new(Vector3.Zero, Vector3.Zero);

    /// <summary>The largest bounds dimension, for framing an asset of unknown scale; 1 without parts.</summary>
    public float LongestExtent => Longest(parts.Count, Bounds);

    // ── skins ──────────────────────────────────────────────────────────────────────

    /// <summary>The skins, in the order the file met them. Empty for a static model.</summary>
    public IReadOnlyList<Skin> Skins => skins;

    /// <summary>Whether a skin deforms some part.</summary>
    public bool IsSkinned => skins.Count > 0;

    /// <summary>The parts a skin deforms.</summary>
    public IEnumerable<Part> SkinnedParts => parts.Where(p => p.SkinIndex >= 0);

    /// <summary>Unskinned parts on no joint, each at its node's world.</summary>
    public IReadOnlyList<Part> StaticParts => staticParts;

    /// <summary>Unskinned parts a joint carries.</summary>
    public IReadOnlyList<Attachment> Attachments => attachments;

    /// <summary>Skin 0's skeleton: most rigs have one skin. Multi-skin files read <see cref="Skins"/>.</summary>
    public Skeleton Skeleton => RequireSkin().Skeleton;

    /// <summary>Skin 0's placement, which goes before a body's own.</summary>
    public Matrix4x4 SkeletonPlacement => RequireSkin().SkeletonPlacement;

    /// <summary>The source's vertex attributes its cook did not carry, with its reasons.</summary>
    public IReadOnlyList<UnreadAttribute> Ignored { get; private set; } = Array.Empty<UnreadAttribute>();

    /// <summary>Every clip in the file, ordered by name so two runs list them the same way.</summary>
    public IReadOnlyList<AnimationClip> Clips { get; private set; } = Array.Empty<AnimationClip>();

    /// <summary>Where the skinned parts sit at their skins' rest pose, each skin placed by its own; zero-sized without them.</summary>
    /// <remarks>Static parts and attachments do not widen it: it describes the body, not its equipment.</remarks>
    public Bounds3 RestBounds { get; private set; } = new(Vector3.Zero, Vector3.Zero);

    /// <summary>The largest rest-bounds dimension, for framing a body of unknown scale; 1 without skinned parts.</summary>
    public float RestExtent => Longest(SkinnedParts.Count(), RestBounds);

    /// <summary>Vertices over the skinned parts.</summary>
    public int SkinnedVertexCount { get; private set; }

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
    public BonePaletteSet[] CreatePaletteSets(int instances)
    {
        RequireSkin();
        return skins.Select(s => new BonePaletteSet(s.Skeleton.BoneCount, instances)).ToArray();
    }

    /// <summary>Packs N posed bodies into one palette set per skin, each body at its placement.</summary>
    /// <remarks>
    /// Per skin, because each has its own inverse binds: one pose gives different palettes for two skins.
    /// Each body is <c>SkeletonPlacement * placement</c>, so a set holds world-space palettes.
    /// </remarks>
    public void PackPalettes(IReadOnlyList<Pose> poses, IReadOnlyList<Matrix4x4> placements, IReadOnlyList<BonePaletteSet> into)
    {
        ArgumentNullException.ThrowIfNull(poses);
        ArgumentNullException.ThrowIfNull(placements);
        ArgumentNullException.ThrowIfNull(into);
        RequireSkin();
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
            for (var s = 0; s < skins.Count; s++) into[s].Add(skins[s].Skeleton, poses[body], skins[s].SkeletonPlacement * placements[body]);
        }
    }

    /// <summary>
    /// The buffers a skinned shader reads this model's palettes from, one per skin, laid out by
    /// <paramref name="program"/>'s set 3 and sized for <paramref name="maxInstances"/> bodies.
    /// </summary>
    /// <remarks>
    /// The one place residency needs a program: descriptor sets take their layout from a shader's
    /// reflection, so a bone buffer exists against the program that will read it. A program without set 3
    /// is refused by the device when the buffers are made. <c>skinning.glsl</c> declares the block every
    /// skinned program reads, unsized; each skin's buffer is sized here, bones x bodies, so the bone count a
    /// program serves is the device's limit and not a constant in the shader.
    /// </remarks>
    public BoneBuffers CreateBoneBuffers(ShaderProgramHandle program, int maxInstances)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxInstances, 1);
        RequireSkin();
        var bindings = skins
            .Select((s, i) => device.CreateMaterial(
                program, setIndex: DescriptorSets.Draw, framesInFlight: device.MaxFramesInFlightCount,
                name: $"{Name}.bones.{i}",
                blockSizes: new Dictionary<int, int> { [0] = checked(s.Skeleton.BoneCount * maxInstances * 64) }))
            .ToArray();
        return new BoneBuffers(device, bindings, skins.Select(s => s.Skeleton.BoneCount).ToArray(), maxInstances);
    }

    /// <summary>What the pick pass draws for <paramref name="part"/>, placed by <paramref name="placement"/>.</summary>
    public DebugPickGeometry PickGeometry(Part part, Matrix4x4 placement)
    {
        ArgumentNullException.ThrowIfNull(part);
        return new DebugPickGeometry(
            part.Mesh.VertexBuffer, part.Mesh.Layout, part.Mesh.IndexBuffer, 0, part.Mesh.IndexCount, 0,
            nodes[part.NodeIndex].World * placement);
    }

    internal static Model Load(IGraphicsDevice device, ModelData source, MaterialTextureLoader textures, string name)
    {
        var model = new Model(device, name);
        var world = source.World;
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        var partsOfNode = new List<Part>[source.Nodes.Count];

        for (var s = 0; s < source.Skins.Count; s++) model.skins.Add(new Skin(source.Skins[s].Skeleton, source.Placement(s)));

        for (var i = 0; i < source.Nodes.Count; i++)
        {
            var node = source.Nodes[i];
            var nodeMin = new Vector3(float.MaxValue);
            var nodeMax = new Vector3(float.MinValue);
            var vertices = 0;
            var count = 0;
            partsOfNode[i] = new List<Part>();
            if (node.MeshIndex >= 0)
            {
                var mesh = source.Meshes[node.MeshIndex];
                // A skinned part's rest vertices sit where its skin places them; a static one where its node does.
                var placement = mesh.Skinned ? source.Placement(node.SkinIndex) : world[i];
                foreach (var primitive in mesh.Primitives)
                {
                    vertices += primitive.Mesh.VertexCount;
                    Accumulate(primitive.Mesh, placement, ref nodeMin, ref nodeMax);
                    var part = new Part(
                        i, device.CreateMesh(primitive.Mesh, $"{name}.{node.Name}.{model.parts.Count}"), primitive.Material,
                        textures.Load(primitive.Material), mesh.Skinned ? node.SkinIndex : -1);
                    model.parts.Add(part);
                    partsOfNode[i].Add(part);
                    count++;
                }
            }

            var hasMesh = count > 0;
            model.nodes.Add(new Node(
                i, node.Name, node.ParentIndex, node.Local, world[i], count, vertices,
                hasMesh ? new Bounds3(nodeMin, nodeMax) : null));
            if (!hasMesh) continue;
            min = Vector3.Min(min, nodeMin);
            max = Vector3.Max(max, nodeMax);
        }

        if (model.parts.Count > 0) model.Bounds = new Bounds3(min, max);
        model.Ignored = source.Ignored;

        // Equipment and scenery: the unskinned parts, split by whether a joint carries them. For every
        // model, not only a rigged one: in a file with no skin nothing is carried, so every part is static.
        var carried = source.Attachments();
        var attached = carried.Select(a => a.NodeIndex).ToHashSet();
        foreach (var a in carried)
        {
            var nodeParts = partsOfNode[a.NodeIndex];
            for (var p = 0; p < nodeParts.Count; p++)
            {
                model.attachments.Add(new Attachment(
                    nodeParts.Count > 1 ? $"{source.Nodes[a.NodeIndex].Name}.{p}" : source.Nodes[a.NodeIndex].Name,
                    source.Nodes[a.JointNode].Name, a.BoneIndex, a.SkinIndex, a.Local, nodeParts[p]));
            }
        }

        for (var n = 0; n < source.Nodes.Count; n++)
        {
            if (attached.Contains(n)) continue;
            model.staticParts.AddRange(partsOfNode[n].Where(p => p.SkinIndex < 0));
        }

        if (!source.IsRigged) return model;

        model.Clips = source.Clips.OrderBy(c => c.Name, StringComparer.Ordinal).ToArray();
        var skinnedPrimitives = source.Nodes
            .Where(n => n.MeshIndex >= 0 && source.Meshes[n.MeshIndex].Skinned)
            .SelectMany(n => source.Meshes[n.MeshIndex].Primitives)
            .ToArray();
        var weighted = SkinningAnalysis.FindWeightedBones(model.skins[0].Skeleton, skinnedPrimitives.Select(p => p.Mesh));
        model.WeightedBones = weighted;
        model.DeformHierarchy = SkinningAnalysis.IncludeAncestors(model.skins[0].Skeleton, weighted);

        var restMin = new Vector3(float.MaxValue);
        var restMax = new Vector3(float.MinValue);
        foreach (var mesh in source.Nodes.Where(n => n.MeshIndex >= 0).Select(n => source.Meshes[n.MeshIndex]).Where(m => m.Skinned))
        {
            var skin = model.skins[mesh.SkinIndex];
            foreach (var p in mesh.Primitives)
            {
                model.SkinnedVertexCount += p.Mesh.VertexCount;
                SkinningAnalysis.AccumulateRestBounds(skin.Skeleton, skin.SkeletonPlacement, p.Mesh, ref restMin, ref restMax);
            }
        }

        if (skinnedPrimitives.Length > 0) model.RestBounds = new Bounds3(restMin, restMax);

        return model;
    }

    // Bounds from the vertices moved to world space, not the local box transformed: a rotated box's
    // corners overstate the extent, and a tool frames and outlines on this.
    internal static void Accumulate(MeshData mesh, Matrix4x4 transform, ref Vector3 min, ref Vector3 max)
    {
        var stride = mesh.Layout.Stride;
        if (stride < 12) return;
        for (var v = 0; v < mesh.VertexCount; v++)
        {
            var at = v * stride;
            var point = Vector3.Transform(new Vector3(
                BitConverter.ToSingle(mesh.VertexBytes, at),
                BitConverter.ToSingle(mesh.VertexBytes, at + 4),
                BitConverter.ToSingle(mesh.VertexBytes, at + 8)), transform);
            min = Vector3.Min(min, point);
            max = Vector3.Max(max, point);
        }
    }

    private static float Longest(int count, in Bounds3 bounds)
    {
        if (count == 0) return 1f;
        var size = bounds.Max - bounds.Min;
        return MathF.Max(size.X, MathF.Max(size.Y, size.Z));
    }

    private Skin RequireSkin() => skins.Count > 0
        ? skins[0]
        : throw new InvalidOperationException($"'{Name}' has no skin, so it has no skeleton, palettes or bone buffers.");

    /// <summary>Destroys the uploaded buffers. Textures belong to the loader; bone buffers to their owner.</summary>
    public void Dispose()
    {
        foreach (var part in parts)
        {
            device.DestroyVertexBuffer(part.Mesh.VertexBuffer);
            device.DestroyIndexBuffer(part.Mesh.IndexBuffer);
        }

        parts.Clear();
        staticParts.Clear();
        attachments.Clear();
    }
}

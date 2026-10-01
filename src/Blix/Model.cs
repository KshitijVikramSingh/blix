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
    private readonly List<SkinBinding> skins = new();
    private readonly List<Part> staticParts = new();
    private readonly List<Attachment> attachments = new();
    private Skeleton? skeleton;
    private Matrix4x4 skeletonPlacement = Matrix4x4.Identity;
    private Matrix4x4[] worldScratch = Array.Empty<Matrix4x4>();

    private Model(IGraphicsDevice device, string name)
    {
        this.device = device;
        Name = name;
    }

    /// <summary>A node of the scene graph, drawable or not.</summary>
    /// <param name="Bounds">World-space bounds of its own primitives' vertices (every instance); null for a node without any.</param>
    /// <param name="Shown">Whether it and every ancestor are visible (<c>KHR_node_visibility</c>, <see cref="ModelData.IsShown"/>).</param>
    /// <param name="Instances">Its <c>EXT_mesh_gpu_instancing</c> transforms, each before <see cref="World"/>; null when not instanced.</param>
    /// <param name="CameraIndex">The camera it carries (<see cref="Model.Cameras"/>), or -1.</param>
    /// <param name="LightIndex">The light it carries (<see cref="Model.Lights"/>), or -1.</param>
    public sealed record Node(
        int Index, string Name, int ParentIndex, Matrix4x4 Local, Matrix4x4 World,
        int PrimitiveCount, int VertexCount, Bounds3? Bounds,
        bool Shown = true, IReadOnlyList<Matrix4x4>? Instances = null, int CameraIndex = -1, int LightIndex = -1)
    {
        /// <summary>Every world its mesh is drawn at: <see cref="World"/>, or one per instance.</summary>
        public IEnumerable<Matrix4x4> DrawnWorlds => Instances is { } all ? all.Select(i => i * World) : new[] { World };
    }

    /// <summary>One uploaded primitive, in its mesh's space, placed by <see cref="NodeIndex"/>.</summary>
    /// <param name="SkinIndex">The skin that deforms it, or -1 for a static part.</param>
    /// <param name="Variants">Per <see cref="Model.Variants"/> entry, the material that variant gives it, or null to keep its own.</param>
    public sealed record Part(
        int NodeIndex, Mesh Mesh, PbrMaterial? Material, MaterialTextures Textures, int SkinIndex = -1,
        IReadOnlyList<PartMaterial?>? Variants = null)
    {
        /// <summary>The material <paramref name="variant"/> draws it with: its own for -1 or a variant that leaves it.</summary>
        public PartMaterial MaterialFor(int variant) =>
            Variants is { } v && (uint)variant < (uint)v.Count && v[variant] is { } chosen ? chosen : new PartMaterial(Material, Textures);
    }

    /// <summary>A material and its resolved textures.</summary>
    public sealed record PartMaterial(PbrMaterial? Material, MaterialTextures Textures);

    /// <summary>A rigid part an animated node carries — a joint, or any node a clip moves — placed by that node's posed world.</summary>
    /// <param name="JointIndex">That node's bone in <see cref="Model.Skeleton"/>.</param>
    /// <param name="SkinIndex">The first skin with that node as a joint, or -1.</param>
    public sealed record Attachment(string Name, string JointName, int JointIndex, int SkinIndex, Matrix4x4 Local, Part Part);

    /// <summary>The name its GPU resources were created under.</summary>
    public string Name { get; }

    public IReadOnlyList<Node> Nodes => nodes;

    /// <summary>The file's cameras, as authored; a node carries one by <see cref="Node.CameraIndex"/>.</summary>
    public IReadOnlyList<ModelData.Camera> Cameras { get; private set; } = Array.Empty<ModelData.Camera>();

    /// <summary>The file's punctual lights, as authored; nothing here shades with them.</summary>
    public IReadOnlyList<ModelData.Light> Lights { get; private set; } = Array.Empty<ModelData.Light>();

    /// <summary>The <c>KHR_materials_variants</c> names (<see cref="Part.MaterialFor"/>).</summary>
    public IReadOnlyList<string> Variants { get; private set; } = Array.Empty<string>();

    /// <summary>The file's scenes, and the one this model placed (-1 when the file has none).</summary>
    public IReadOnlyList<ModelData.Scene> Scenes { get; private set; } = Array.Empty<ModelData.Scene>();

    /// <inheritdoc cref="ModelData.SceneIndex"/>
    public int SceneIndex { get; private set; } = -1;

    /// <summary>The variant named <paramref name="name"/>, or -1.</summary>
    public int Variant(string name)
    {
        for (var v = 0; v < Variants.Count; v++)
        {
            if (string.Equals(Variants[v], name, StringComparison.OrdinalIgnoreCase)) return v;
        }

        return -1;
    }

    /// <summary>Every uploaded part, static and skinned, in node order.</summary>
    public IReadOnlyList<Part> Parts => parts;

    /// <summary>World-space bounds over every node with geometry; zero-sized for a model without any.</summary>
    public Bounds3 Bounds { get; private set; } = new(Vector3.Zero, Vector3.Zero);

    /// <summary>The largest bounds dimension, for framing an asset of unknown scale; 1 without parts.</summary>
    public float LongestExtent => Longest(parts.Count, Bounds);

    // ── skins ──────────────────────────────────────────────────────────────────────

    /// <summary>The skins, in the order the file met them, each bound to <see cref="Skeleton"/>. Empty for a static model.</summary>
    /// <remarks>
    /// A skin is posed by the model's one animated hierarchy: its palette is gathered from that pose's bone
    /// worlds through its joints and inverse binds, so skins with different joint lists move together.
    /// </remarks>
    public IReadOnlyList<SkinBinding> Skins => skins;

    /// <summary>Whether a skin deforms some part.</summary>
    public bool IsSkinned => skins.Count > 0;

    /// <summary>The parts a skin deforms.</summary>
    public IEnumerable<Part> SkinnedParts => parts.Where(p => p.SkinIndex >= 0);

    /// <summary>Unskinned parts no animated node carries, each at its node's world.</summary>
    public IReadOnlyList<Part> StaticParts => staticParts;

    /// <summary>Unskinned parts an animated node carries (<see cref="Attachment"/>).</summary>
    public IReadOnlyList<Attachment> Attachments => attachments;

    /// <summary>Whether a clip moves anything — a skin's joints or any other node.</summary>
    public bool IsAnimated => Clips.Count > 0;

    /// <summary>
    /// The model's animated hierarchy: every skin's joints and every node a clip moves (<see cref="ModelData.Skeleton"/>).
    /// Poses and clips are against it; for most rigs it is skin 0's skeleton exactly.
    /// </summary>
    public Skeleton Skeleton => skeleton
        ?? throw new InvalidOperationException($"'{Name}' has no skin and no animation, so there is no skeleton to pose.");

    /// <summary>Where the animated hierarchy hangs, which goes before a body's own placement.</summary>
    public Matrix4x4 SkeletonPlacement => skeleton is not null
        ? skeletonPlacement
        : throw new InvalidOperationException($"'{Name}' has no skin and no animation, so there is no skeleton to place.");

    /// <summary>The source's vertex attributes its cook did not carry, with its reasons.</summary>
    public IReadOnlyList<UnreadAttribute> Ignored { get; private set; } = Array.Empty<UnreadAttribute>();

    /// <summary>Every clip in the file, ordered by name so two runs list them the same way.</summary>
    public IReadOnlyList<AnimationClip> Clips { get; private set; } = Array.Empty<AnimationClip>();

    /// <summary>
    /// Where the body sits at rest: the skinned parts at their skins' rest pose, or, for a model with no
    /// skinned parts, its <see cref="Bounds"/>. Static parts and attachments of a skinned model do not
    /// widen it: it describes the body, not its equipment.
    /// </summary>
    /// <remarks>Static parts and attachments do not widen it: it describes the body, not its equipment.</remarks>
    public Bounds3 RestBounds { get; private set; } = new(Vector3.Zero, Vector3.Zero);

    /// <summary>The largest rest-bounds dimension, for framing a body of unknown scale; 1 without skinned parts.</summary>
    public float RestExtent => Longest(parts.Count, RestBounds);

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

    /// <summary>One palette set per skin, sized for <paramref name="instances"/> bodies. None for a model without a skin.</summary>
    public BonePaletteSet[] CreatePaletteSets(int instances)
    {
        _ = Skeleton;
        return skins.Select(s => new BonePaletteSet(s.JointCount, instances)).ToArray();
    }

    /// <summary>Packs N posed bodies into one palette set per skin, each body at its placement.</summary>
    /// <remarks>
    /// Per skin, because each has its own inverse binds: one pose gives different palettes for two skins.
    /// The poses are of <see cref="Skeleton"/>, the one animated hierarchy; each skin gathers its joints
    /// from it (<see cref="SkinBinding.Bones"/>). Each body is <c>SkeletonPlacement * placement</c>, so a set holds
    /// world-space palettes. A model without a skin has no sets, and nothing is packed.
    /// </remarks>
    public void PackPalettes(IReadOnlyList<Pose> poses, IReadOnlyList<Matrix4x4> placements, IReadOnlyList<BonePaletteSet> into)
    {
        ArgumentNullException.ThrowIfNull(poses);
        ArgumentNullException.ThrowIfNull(placements);
        ArgumentNullException.ThrowIfNull(into);
        _ = Skeleton;
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
        if (worldScratch.Length != Skeleton.BoneCount) worldScratch = new Matrix4x4[Skeleton.BoneCount];
        for (var body = 0; body < poses.Count; body++)
        {
            // One pose of the hierarchy per body; every skin gathers its joints from it.
            Skeleton.ComputeBoneWorlds(poses[body], worldScratch);
            for (var s = 0; s < skins.Count; s++)
            {
                into[s].Add(skins[s], worldScratch, skeletonPlacement * placements[body]);
            }
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
                arrayLengths: new Dictionary<int, int> { [0] = checked(s.JointCount * maxInstances) }))
            .ToArray();
        return new BoneBuffers(device, bindings, skins.Select(s => s.JointCount).ToArray(), maxInstances);
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

        foreach (var own in source.Skins) model.skins.Add(own.Binding);
        model.skeleton = source.Skeleton;
        model.skeletonPlacement = source.SkeletonPlacement;

        for (var i = 0; i < source.Nodes.Count; i++)
        {
            var node = source.Nodes[i];
            var nodeMin = new Vector3(float.MaxValue);
            var nodeMax = new Vector3(float.MinValue);
            var vertices = 0;
            var count = 0;
            partsOfNode[i] = new List<Part>();
            // Only the placed scene is uploaded; a hidden node is (visibility can change), and says so.
            if (node.MeshIndex >= 0 && source.IsPlaced(i))
            {
                var mesh = source.Meshes[node.MeshIndex];
                // A skinned part's rest vertices sit where its skin places them; a static one where its node
                // does, once per instance.
                var placements = mesh.Skinned ? new[] { source.Placement(node.SkinIndex) } : source.DrawnWorlds(i).ToArray();
                foreach (var primitive in mesh.Primitives)
                {
                    vertices += primitive.Mesh.VertexCount;
                    foreach (var placement in placements) Accumulate(primitive.Mesh, placement, ref nodeMin, ref nodeMax);
                    var part = new Part(
                        i, device.CreateMesh(primitive.Mesh, $"{name}.{node.Name}.{model.parts.Count}"), primitive.Material,
                        textures.Load(primitive.Material), mesh.Skinned ? node.SkinIndex : -1,
                        primitive.Variants?.Select(v => v is null ? null : new PartMaterial(v.Material, textures.Load(v.Material))).ToArray());
                    model.parts.Add(part);
                    partsOfNode[i].Add(part);
                    count++;
                }
            }

            var hasMesh = count > 0;
            model.nodes.Add(new Node(
                i, node.Name, node.ParentIndex, node.Local, world[i], count, vertices,
                hasMesh ? new Bounds3(nodeMin, nodeMax) : null,
                source.IsShown(i), node.Instances, node.CameraIndex, node.LightIndex));
            // The model's bounds are what it shows: a hidden node's geometry does not frame it.
            if (!hasMesh || !source.IsShown(i)) continue;
            min = Vector3.Min(min, nodeMin);
            max = Vector3.Max(max, nodeMax);
        }

        if (min.X <= max.X) model.Bounds = new Bounds3(min, max);
        model.Ignored = source.Ignored;
        model.Cameras = source.Cameras;
        model.Lights = source.Lights;
        model.Variants = source.Variants;
        model.Scenes = source.Scenes;
        model.SceneIndex = source.SceneIndex;

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

        if (source.Skeleton is not { } hierarchy) return model;

        model.Clips = source.Clips.OrderBy(c => c.Name, StringComparer.Ordinal).ToArray();

        // Weighted bones and rest bounds, per skin, in the hierarchy's bones: the same gather a draw does.
        var weighted = new bool[hierarchy.BoneCount];
        var restWorlds = new Matrix4x4[hierarchy.BoneCount];
        hierarchy.ComputeBoneWorlds(hierarchy.CreateRestPose(), restWorlds);
        var restMin = new Vector3(float.MaxValue);
        var restMax = new Vector3(float.MinValue);
        var anySkinned = false;
        for (var s = 0; s < model.skins.Count; s++)
        {
            var skin = model.skins[s];
            // Skinned vertices only: a load that asked for static geometry has nothing here to bound.
            var meshes = source.SkinnedPrimitives(s).Select(p => p.Mesh).Where(m => m.Layout.Stride >= 80).ToArray();
            var own = SkinningAnalysis.FindWeightedJoints(skin, meshes);
            for (var b = 0; b < own.Length; b++) weighted[skin.Bones[b]] |= own[b];

            var palette = new BonePaletteSet(skin.JointCount, 1);
            palette.Add(skin, restWorlds, source.SkeletonPlacement);
            foreach (var mesh in meshes)
            {
                anySkinned = true;
                model.SkinnedVertexCount += mesh.VertexCount;
                SkinningAnalysis.AccumulateRestBounds(palette.Matrices, mesh, ref restMin, ref restMax);
            }
        }

        model.WeightedBones = weighted;
        model.DeformHierarchy = SkinningAnalysis.IncludeAncestors(hierarchy, weighted);
        model.RestBounds = anySkinned && restMin.X <= restMax.X ? new Bounds3(restMin, restMax) : model.Bounds;

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

    private SkinBinding RequireSkin() => skins.Count > 0
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

using System.Numerics;
using Blix.Assets;
using Blix.Cooked;
using Blix.Graphics;

namespace Blix;

/// <summary>What a load asks of a model's vertices: the layout its pipelines declare.</summary>
/// <param name="Tangents">Carry the tangent frame (normal-mapped static pipelines).</param>
/// <param name="Colour">Carry COLOR_0 and the second texture coordinate.</param>
/// <param name="Skinned">
/// Keep skinned meshes skinned, in the 80-byte layout skinned pipelines read. False reads them as static
/// geometry at their bind pose, which is what a model view of a rigged file draws.
/// </param>
/// <summary>What a <c>KHR_lights_punctual</c> light is.</summary>
public enum LightType
{
    Directional,
    Point,
    Spot,
}

public readonly record struct ModelNeeds(bool Tangents = false, bool Colour = false, bool Skinned = true);

/// <summary>
/// A cooked model in memory: glTF's scene graph as the cook wrote it — nodes, meshes placed by them,
/// skins as joint nodes, clips — with vertices in the layout the load asked for.
/// </summary>
/// <remarks>
/// <para>
/// <b>One type, however the file is used.</b> A rig is a model some of whose nodes have a skin; a
/// static model is one whose nodes have none. Everything a consumer derives — where a skin sits, which
/// meshes ride a joint, the whole scene flattened into world space — is a view here, written once, by
/// the rules the glTF importer has always applied.
/// </para>
/// <para>
/// Reads <c>.blixmesh</c> only: the engine takes cooked models, and a tool opening a source cooks it
/// first.
/// </para>
/// </remarks>
public sealed class ModelData
{
    /// <summary>A node of the scene graph. Mesh, skin, camera and light are -1 where it has none.</summary>
    /// <param name="Visible">Its own <c>KHR_node_visibility</c> flag; whether it is shown also takes its ancestors' (<see cref="IsShown"/>).</param>
    /// <param name="Instances">
    /// <c>EXT_mesh_gpu_instancing</c>: the transform of each instance, applied before the node's world
    /// (row-vector <c>instance * world</c>). Null for an ordinary node; an instanced mesh is never drawn
    /// un-instanced.
    /// </param>
    public sealed record Node(
        string Name, int ParentIndex, Matrix4x4 Local, int MeshIndex, int SkinIndex,
        bool Visible = true, IReadOnlyList<Matrix4x4>? Instances = null, int CameraIndex = -1, int LightIndex = -1);

    /// <summary>One primitive of a mesh, in mesh space, with its material.</summary>
    /// <param name="Variants">
    /// <c>KHR_materials_variants</c>: per variant of the model, the material it gives this primitive, or null
    /// to keep its own. Null when the model has no variants.
    /// </param>
    public sealed record Primitive(MeshData Mesh, PbrMaterial? Material, int MaterialIndex, IReadOnlyList<VariantMaterial?>? Variants = null)
    {
        /// <summary>The material <paramref name="variant"/> gives this primitive; its own for -1 or a variant that leaves it.</summary>
        public VariantMaterial MaterialFor(int variant) =>
            Variants is { } v && (uint)variant < (uint)v.Count && v[variant] is { } chosen ? chosen : new VariantMaterial(MaterialIndex, Material);
    }

    /// <summary>A material by its index in the file, resolved.</summary>
    public sealed record VariantMaterial(int MaterialIndex, PbrMaterial? Material);

    /// <summary>A glTF scene: the root nodes it places.</summary>
    public sealed record Scene(string Name, IReadOnlyList<int> Roots);

    /// <summary>A glTF camera, as authored; the node carrying it looks down its -Z with +Y up.</summary>
    /// <param name="AspectRatio">The authored aspect, or 0 to take the viewport's.</param>
    /// <param name="ZFar">The far plane; infinity for an infinite perspective projection.</param>
    /// <param name="XMag">Orthographic half-width (0 for perspective).</param>
    /// <param name="YMag">Orthographic half-height (0 for perspective).</param>
    public sealed record Camera(
        string Name, bool Orthographic, float YFov, float AspectRatio, float XMag, float YMag, float ZNear, float ZFar);

    /// <summary>A <c>KHR_lights_punctual</c> light, as authored: what it is, not how it is shaded.</summary>
    /// <param name="Intensity">Candela for point and spot, lux for directional.</param>
    /// <param name="Range">Where its influence ends; infinity when the file gives none.</param>
    public sealed record Light(
        string Name, LightType Type, Vector3 Color, float Intensity, float Range, float InnerConeAngle, float OuterConeAngle);

    /// <summary>A mesh: primitives stored once, however many nodes place it.</summary>
    /// <param name="Skinned">Whether its vertices are skinned in this load (per <see cref="ModelNeeds.Skinned"/>).</param>
    public sealed record Mesh(string Name, IReadOnlyList<Primitive> Primitives, int SkinIndex, bool Skinned);

    /// <summary>A skin: its binding into the model's animated <see cref="ModelData.Skeleton"/>, each joint's node, and where its joints hang.</summary>
    /// <param name="Binding">Its joints as bones of <see cref="ModelData.Skeleton"/>, and their inverse binds, in the skin's (parent-first) joint order.</param>
    /// <param name="JointNodes">The scene node of each joint, index for index with the binding's joints.</param>
    /// <param name="Placement">The world of the node its first root joint hangs from (<see cref="JointHierarchy.Placement"/>).</param>
    public sealed record Skin(SkinBinding Binding, IReadOnlyList<int> JointNodes, Matrix4x4 Placement);

    /// <summary>A rigid mesh an animated node carries: its node, the node carrying it, and its transform relative to that.</summary>
    /// <param name="CarrierNode">The nearest animated node at or above it: a skin joint, or any node a clip moves (which need be no skin's joint).</param>
    /// <param name="SkinIndex">The first skin with <paramref name="CarrierNode"/> as a joint, or -1.</param>
    /// <param name="BoneIndex">The carrier's bone in the model's animated <see cref="ModelData.Skeleton"/>: a hierarchy bone, not a skin joint.</param>
    public sealed record Attachment(int NodeIndex, int CarrierNode, int SkinIndex, int BoneIndex, Matrix4x4 Local);

    private Matrix4x4[]? world;
    private readonly bool[] placed;
    private readonly bool[] shown;

    private ModelData(
        IReadOnlyList<Node> nodes, IReadOnlyList<Mesh> meshes, IReadOnlyList<Skin> skins, IReadOnlyList<AnimationClip> clips,
        IReadOnlyList<UnreadAttribute> ignored, string source, Skeleton? skeleton, IReadOnlyList<int> skeletonNodes, Matrix4x4 skeletonPlacement,
        IReadOnlyList<Scene> scenes, int defaultScene, int scene, IReadOnlyList<Camera> cameras, IReadOnlyList<Light> lights,
        IReadOnlyList<string> variants)
    {
        Scenes = scenes;
        DefaultScene = defaultScene;
        SceneIndex = scene;
        Cameras = cameras;
        Lights = lights;
        Variants = variants;
        (placed, shown) = BlixMeshFile.Placement(
            nodes.Select(n => n.ParentIndex).ToArray(), nodes.Select(n => n.Visible).ToArray(), scene < 0 ? null : scenes[scene].Roots.ToArray());
        Skeleton = skeleton;
        SkeletonNodes = skeletonNodes;
        SkeletonPlacement = skeletonPlacement;
        Ignored = ignored;
        Nodes = nodes;
        Meshes = meshes;
        Skins = skins;
        Clips = clips;
        Source = source;
    }

    public IReadOnlyList<Node> Nodes { get; }

    /// <summary>The file's scenes. Empty for a file with none, whose every root node is placed.</summary>
    public IReadOnlyList<Scene> Scenes { get; }

    /// <summary>The scene the file names as its default, or -1 when it has no scenes.</summary>
    public int DefaultScene { get; }

    /// <summary>The scene this load placed (-1: the file has none, so every root is placed).</summary>
    public int SceneIndex { get; }

    /// <summary>The file's cameras; a node carries one by <see cref="Node.CameraIndex"/>.</summary>
    public IReadOnlyList<Camera> Cameras { get; }

    /// <summary>The file's punctual lights; a node carries one by <see cref="Node.LightIndex"/>.</summary>
    public IReadOnlyList<Light> Lights { get; }

    /// <summary>The <c>KHR_materials_variants</c> names, in the file's order (<see cref="Primitive.MaterialFor"/>).</summary>
    public IReadOnlyList<string> Variants { get; }

    /// <summary>Whether node <paramref name="node"/> is in the scene this load placed.</summary>
    /// <remarks>Every view here — flattened, attachments, skinned primitives, a resident model — takes only placed nodes.</remarks>
    public bool IsPlaced(int node) => placed[node];

    /// <summary>Whether node <paramref name="node"/> is placed and it and every ancestor are visible (<c>KHR_node_visibility</c>).</summary>
    /// <remarks>A hidden node's meshes and lights are not drawn; its cameras still work.</remarks>
    public bool IsShown(int node) => shown[node];

    /// <summary>Every world node <paramref name="node"/>'s mesh is drawn at: its world, or one per instance.</summary>
    public IEnumerable<Matrix4x4> DrawnWorlds(int node)
    {
        if (Nodes[node].Instances is not { } instances)
        {
            yield return World[node];
            yield break;
        }

        foreach (var instance in instances) yield return instance * World[node];
    }

    public IReadOnlyList<Mesh> Meshes { get; }

    public IReadOnlyList<Skin> Skins { get; }

    /// <summary>
    /// The model's animated hierarchy: every skin's joints and every node a clip moves, as one skeleton.
    /// Null for a model with neither.
    /// </summary>
    /// <remarks>
    /// <para>
    /// glTF animates nodes, and every skin reads its joints' worlds from the one scene graph; this is the
    /// part of the graph that moves. One pose of it drives every skin (each gathering its joints through
    /// <see cref="SkinBinding.Bones"/>) and every rigid part an animated node carries (<see cref="Attachments"/>).
    /// Static nodes in between are the bones' offsets (<see cref="JointHierarchy"/>).
    /// </para>
    /// <para>
    /// A pose hierarchy only: the inverse binds are each skin's (<see cref="Skin.Binding"/>). When it is exactly
    /// skin 0's joints (one skin, no other node animated, which is most rigs) its bones are in skin 0's joint
    /// order, so bone indices on an ordinary rig do not move; that is a compatibility convention, not a
    /// privilege of skin 0. Otherwise its bones are in node order.
    /// </para>
    /// </remarks>
    public Skeleton? Skeleton { get; }

    /// <summary>The node each <see cref="Skeleton"/> bone is.</summary>
    public IReadOnlyList<int> SkeletonNodes { get; }

    /// <summary>Where the animated hierarchy hangs: goes after every bone world (<see cref="JointHierarchy.Placement"/>).</summary>
    public Matrix4x4 SkeletonPlacement { get; }

    /// <summary>Clips as bone tracks against <see cref="Skeleton"/>: every animated node, joint or not.</summary>
    public IReadOnlyList<AnimationClip> Clips { get; }

    /// <summary>Whether a clip moves anything.</summary>
    public bool IsAnimated => Clips.Count > 0;

    /// <summary>The source's vertex attributes its cook did not carry.</summary>
    public IReadOnlyList<UnreadAttribute> Ignored { get; }

    /// <summary>The cooked file this was read from.</summary>
    public string Source { get; }

    /// <summary>Whether some node's mesh is deformed by a skin.</summary>
    public bool IsRigged => Skins.Count > 0;

    /// <summary>Every node's world transform (row-vector: <c>local * parentWorld</c>).</summary>
    public IReadOnlyList<Matrix4x4> World => world ??= ComposeWorld(Nodes);

    /// <summary>
    /// Where <paramref name="skin"/>'s skeleton hangs in the scene: the world of the node its first root
    /// joint hangs from, or identity for a scene root (<see cref="JointHierarchy"/>).
    /// </summary>
    /// <remarks>
    /// Not the mesh node's world: glTF places a skinned vertex by its joints alone and ignores that
    /// transform. The two agree in most exports; tank.glb is one where they do not.
    /// </remarks>
    public Matrix4x4 Placement(int skin) => Skins[skin].Placement;

    /// <summary>Unskinned mesh nodes at or under an animated node: the rigid parts the pose moves.</summary>
    /// <remarks>
    /// The transform is composed from the node up to the animated node (row-vector: child local, then
    /// parent); identity when the mesh node is itself animated. A mesh node that reaches the root without
    /// meeting one is a static part.
    /// </remarks>
    public IReadOnlyList<Attachment> Attachments()
    {
        var found = new List<Attachment>();
        var boneOfNode = new Dictionary<int, int>();
        for (var b = 0; b < SkeletonNodes.Count; b++) boneOfNode[SkeletonNodes[b]] = b;
        for (var n = 0; n < Nodes.Count; n++)
        {
            if (Nodes[n].MeshIndex < 0 || Nodes[n].SkinIndex >= 0 || !placed[n]) continue;
            var local = Matrix4x4.Identity;
            var carrier = n;
            while (carrier >= 0 && !boneOfNode.ContainsKey(carrier))
            {
                local *= Nodes[carrier].Local;
                carrier = Nodes[carrier].ParentIndex;
            }

            if (carrier < 0) continue;
            var skin = -1;
            for (var s = 0; s < Skins.Count && skin < 0; s++)
            {
                if (Skins[s].JointNodes.Contains(carrier)) skin = s;
            }

            found.Add(new Attachment(n, carrier, skin, boneOfNode[carrier], local));
        }

        return found;
    }

    /// <summary>The primitives <paramref name="skin"/> deforms, in node order: what a skinned draw of it uploads.</summary>
    public IEnumerable<Primitive> SkinnedPrimitives(int skin = 0)
    {
        for (var n = 0; n < Nodes.Count; n++)
        {
            if (Nodes[n].SkinIndex != skin || Nodes[n].MeshIndex < 0 || !placed[n]) continue;
            foreach (var p in Meshes[Nodes[n].MeshIndex].Primitives) yield return p;
        }
    }

    /// <summary>The first node with this name, or -1.</summary>
    public int FindNode(string name)
    {
        for (var n = 0; n < Nodes.Count; n++)
        {
            if (string.Equals(Nodes[n].Name, name, StringComparison.Ordinal)) return n;
        }

        return -1;
    }

    /// <summary>Every primitive a shown node places, moved to that node's world: the scene as one flat list.</summary>
    /// <remarks>
    /// <para>
    /// A skinned mesh read skinned stays in mesh space here; only static vertices are moved. An instanced
    /// node gives one primitive per instance (<see cref="DrawnWorlds"/>). Hidden nodes and nodes outside the
    /// placed scene give none.
    /// </para>
    /// <para>
    /// A mirroring world (negative determinant) reverses the winding, so the front face is still the one
    /// glTF means, and negates the tangent's <c>w</c>, so the bitangent still follows the texture's v.
    /// </para>
    /// </remarks>
    public IEnumerable<(int NodeIndex, Primitive Primitive)> Flattened()
    {
        for (var n = 0; n < Nodes.Count; n++)
        {
            if (Nodes[n].MeshIndex < 0 || !shown[n]) continue;
            var mesh = Meshes[Nodes[n].MeshIndex];
            foreach (var at in DrawnWorlds(n))
            foreach (var p in mesh.Primitives)
            {
                yield return (n, mesh.Skinned || at.IsIdentity ? p : p with { Mesh = Moved(p.Mesh, at) });
            }
        }
    }

    /// <summary>Reads a cooked model, its vertices in the layout <paramref name="needs"/> declares.</summary>
    /// <param name="scene">The scene to place; null for the file's default. Ignored by a file with no scenes.</param>
    public static ModelData Load(string path, ModelNeeds needs = default, int? scene = null)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (!path.EndsWith(".blixmesh", StringComparison.OrdinalIgnoreCase))
        {
            throw new AssetImportException(
                path, null,
                "the engine reads cooked models only — cook it (`blix cook asset`), or open it through a tool, which cooks on open");
        }

        var loadWatch = System.Diagnostics.Stopwatch.StartNew();
        var file = BlixMeshReader.Read(path);
        var cookedDir = Path.GetDirectoryName(Path.GetFullPath(path)) ?? string.Empty;
        var textureCache = new Dictionary<int, TextureData>();
        var materialCache = new Dictionary<int, PbrMaterial>();
        CookedMaterials.LoadImagesFromTable(file.ImageTable, file.MaterialTable, cookedDir, textureCache);

        var staticLayout = CookedVertices.Requested(needs.Tangents, needs.Colour);
        var meshes = file.Meshes.Select(m =>
        {
            var skinned = m.SkinIndex >= 0 && needs.Skinned;
            var layout = skinned ? VertexPosition3NormalTextureSkin4Tangent.Layout : staticLayout;
            var primitives = m.Primitives.Select(p =>
            {
                var (bytes, bounds) = CookedVertices.Repack(p, layout, transform: null, path);
                if (ReferenceEquals(bytes, p.VertexBytes)) bytes = (byte[])bytes.Clone();
                var lods = p.Lods.Select(l => new MeshLod(l.Indices16, l.Indices32, l.Error)).ToArray();
                var lod0 = p.Lods[0];
                return new Primitive(
                    new MeshData(p.Name, bytes, lod0.Indices16 ?? Array.Empty<ushort>(), layout, bounds,
                        Indices32: lod0.Indices32, Lods: lods),
                    CookedMaterials.MaterialFromCooked(file.MaterialTable, p.MaterialIndex, materialCache, textureCache, path),
                    p.MaterialIndex,
                    p.VariantMaterials?.Select(v => v < 0 ? null : new VariantMaterial(
                        v, CookedMaterials.MaterialFromCooked(file.MaterialTable, v, materialCache, textureCache, path))).ToArray());
            }).ToArray();
            return new Mesh(m.Name, primitives, m.SkinIndex, skinned);
        }).ToArray();

        var nodes = file.Nodes.Select(n => new Node(
            n.Name, n.ParentIndex, n.LocalTransform, n.MeshIndex, n.SkinIndex, n.Visible, n.Instances, n.CameraIndex, n.LightIndex)).ToArray();
        var scenes = file.SceneTable.Select(s => new Scene(s.Name, s.Roots)).ToArray();
        if (scene is { } asked && file.SceneTable.Count > 0 && (uint)asked >= (uint)file.SceneTable.Count)
        {
            throw new AssetImportException(path, null, $"scene {asked} was asked for, and the file has {file.SceneTable.Count}");
        }

        var placedScene = file.SceneFor(scene);
        var worlds = ComposeWorld(nodes);
        // Each skin's facts from the file: its joints, their inverse binds, and where they hang. Which bones of
        // the animated hierarchy the joints are is known once that hierarchy is built below.
        var skinFacts = file.SkinTable.Select(s =>
        {
            var joints = s.Bones.Select(b => b.NodeIndex).ToArray();
            var hierarchy = JointHierarchy.Resolve(
                s.Bones.Select(b => b.Name).ToArray(), s.Bones.Select(b => b.ParentIndex).ToArray(), joints,
                n => nodes[n].ParentIndex, n => nodes[n].Local, n => worlds[n]);
            return new SkinFacts(joints, hierarchy, s.Bones.Select(b => b.InverseBindPose).ToArray());
        }).ToArray();

        var (skeleton, skeletonNodes, skeletonPlacement) = AnimatedHierarchy(file, nodes, worlds, skinFacts);
        var boneOfNode = new Dictionary<int, int>();
        for (var b = 0; b < skeletonNodes.Length; b++) boneOfNode[skeletonNodes[b]] = b;
        var skins = skinFacts
            .Select(k => new Skin(
                new SkinBinding(skeleton!, k.Joints.Select(n => boneOfNode[n]).ToArray(), k.InverseBinds),
                k.Joints, k.Hierarchy.Placement))
            .ToArray();

        var clips = file.ClipTable
            .Select(c => new AnimationClip(c.Name, c.Tracks
                .Where(t => boneOfNode.ContainsKey(t.NodeIndex))
                .Select(t => new BoneTrack
                {
                    BoneIndex = boneOfNode[t.NodeIndex],
                    Translation = t.Translation.Length == 0 ? null
                        : new KeyframeVector3Curve(t.Translation.Select(k => new Keyframe<Vector3>(k.Time, k.Value, k.InTangent, k.OutTangent)).ToArray(),
                            Mode(t.TranslationInterpolation)),
                    Rotation = t.Rotation.Length == 0 ? null
                        : new KeyframeQuaternionCurve(t.Rotation.Select(k => new Keyframe<Quaternion>(k.Time, k.Value, k.InTangent, k.OutTangent)).ToArray(),
                            Mode(t.RotationInterpolation)),
                    Scale = t.Scale.Length == 0 ? null
                        : new KeyframeVector3Curve(t.Scale.Select(k => new Keyframe<Vector3>(k.Time, k.Value, k.InTangent, k.OutTangent)).ToArray(),
                            Mode(t.ScaleInterpolation)),
                }).ToArray()))
            .Where(c => c.Tracks.Length > 0)
            .ToArray();

        var ignored = file.IgnoredTable.Select(i => new UnreadAttribute(i.Semantic, i.Primitives)).ToArray();
        if (AssetLoadLog.Enabled)
        {
            AssetLoadLog.Report(new AssetLoadReport(
                SourcePath: path, CookedPath: path, Mode: AssetLoadMode.Cooked,
                Bytes: new FileInfo(path).Length, LoadMs: loadWatch.Elapsed.TotalMilliseconds,
                Recipe: file.Cooked?.Stamp.Recipe));
        }

        return new ModelData(
            nodes, meshes, skins, clips, ignored, path, skeleton, skeletonNodes, skeletonPlacement,
            scenes, file.DefaultScene, placedScene,
            file.CameraTable.Select(c => new Camera(c.Name, c.Orthographic, c.YFov, c.AspectRatio, c.XMag, c.YMag, c.ZNear, c.ZFar)).ToArray(),
            file.LightTable.Select(l => new Light(
                l.Name, l.Type switch { BlixMesh.LightDirectional => LightType.Directional, BlixMesh.LightPoint => LightType.Point, _ => LightType.Spot },
                l.Color, l.Intensity, l.Range, l.InnerConeAngle, l.OuterConeAngle)).ToArray(),
            file.VariantTable);
    }


    private sealed record SkinFacts(int[] Joints, JointHierarchy Hierarchy, Matrix4x4[] InverseBinds);

    // The animated hierarchy: every skin's joints and every tracked node. In skin 0's joint order when that is
    // exactly the set (the compatibility convention), otherwise in node order (the cook writes parents first).
    // No inverse binds: those are each skin's, applied through its binding.
    private static (Skeleton? Skeleton, int[] Nodes, Matrix4x4 Placement) AnimatedHierarchy(
        BlixMeshFile file, IReadOnlyList<Node> nodes, Matrix4x4[] worlds, IReadOnlyList<SkinFacts> skins)
    {
        var members = skins.SelectMany(k => k.Joints)
            .Concat(file.ClipTable.SelectMany(c => c.Tracks).Select(t => t.NodeIndex))
            .ToHashSet();
        if (members.Count == 0) return (null, Array.Empty<int>(), Matrix4x4.Identity);
        if (skins.Count > 0 && members.SetEquals(skins[0].Joints))
        {
            return (new Skeleton(skins[0].Hierarchy.Bones.ToArray()), skins[0].Joints.ToArray(), skins[0].Hierarchy.Placement);
        }

        var order = members.OrderBy(n => n).ToArray();
        var index = new Dictionary<int, int>();
        for (var i = 0; i < order.Length; i++) index[order[i]] = i;
        var parents = new int[order.Length];
        for (var i = 0; i < order.Length; i++)
        {
            var parent = nodes[order[i]].ParentIndex;
            while (parent >= 0 && !index.ContainsKey(parent)) parent = nodes[parent].ParentIndex;
            parents[i] = parent < 0 ? -1 : index[parent];
        }

        var resolved = JointHierarchy.Resolve(
            order.Select(n => nodes[n].Name).ToArray(), parents, order,
            n => nodes[n].ParentIndex, n => nodes[n].Local, n => worlds[n]);
        return (new Skeleton(resolved.Bones.ToArray()), order, resolved.Placement);
    }

    private static Interpolation Mode(BlixMeshInterpolation mode) => mode switch
    {
        BlixMeshInterpolation.Step => Interpolation.Step,
        BlixMeshInterpolation.CubicSpline => Interpolation.CubicSpline,
        _ => Interpolation.Linear,
    };

    private static Matrix4x4[] ComposeWorld(IReadOnlyList<Node> nodes)
    {
        var result = new Matrix4x4[nodes.Count];
        for (var i = 0; i < nodes.Count; i++)
        {
            result[i] = nodes[i].ParentIndex < 0 ? nodes[i].Local : nodes[i].Local * result[nodes[i].ParentIndex];
        }

        return result;
    }

    // Static vertices moved to a world: the one transform every flattening uses (MeshDataExtensions.Transformed),
    // which reverses the winding and negates the tangent's w under a mirror. The source bounds stand for an empty mesh.
    private static MeshData Moved(MeshData mesh, in Matrix4x4 world) =>
        mesh.VertexCount == 0 ? mesh : mesh.Transformed(world);
}

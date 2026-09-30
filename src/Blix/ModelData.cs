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
    /// <summary>A node of the scene graph. Mesh and skin are -1 where it has none.</summary>
    public sealed record Node(string Name, int ParentIndex, Matrix4x4 Local, int MeshIndex, int SkinIndex);

    /// <summary>One primitive of a mesh, in mesh space, with its material.</summary>
    public sealed record Primitive(MeshData Mesh, PbrMaterial? Material, int MaterialIndex);

    /// <summary>A mesh: primitives stored once, however many nodes place it.</summary>
    /// <param name="Skinned">Whether its vertices are skinned in this load (per <see cref="ModelNeeds.Skinned"/>).</param>
    public sealed record Mesh(string Name, IReadOnlyList<Primitive> Primitives, int SkinIndex, bool Skinned);

    /// <summary>A skin: its skeleton, bones in parent-first order, each bone's joint node, and where it hangs.</summary>
    /// <param name="Placement">What goes after every bone world (<see cref="JointHierarchy.Placement"/>).</param>
    public sealed record Skin(Skeleton Skeleton, IReadOnlyList<int> JointNodes, Matrix4x4 Placement);

    /// <summary>A static mesh a joint carries: its node, the joint, and its transform relative to that joint.</summary>
    /// <param name="BoneIndex">The joint's bone in <paramref name="SkinIndex"/>'s skeleton.</param>
    public sealed record Attachment(int NodeIndex, int JointNode, int SkinIndex, int BoneIndex, Matrix4x4 Local);

    private Matrix4x4[]? world;

    private ModelData(
        IReadOnlyList<Node> nodes, IReadOnlyList<Mesh> meshes, IReadOnlyList<Skin> skins, IReadOnlyList<AnimationClip> clips,
        IReadOnlyList<UnreadAttribute> ignored, string source)
    {
        Ignored = ignored;
        Nodes = nodes;
        Meshes = meshes;
        Skins = skins;
        Clips = clips;
        Source = source;
    }

    public IReadOnlyList<Node> Nodes { get; }

    public IReadOnlyList<Mesh> Meshes { get; }

    public IReadOnlyList<Skin> Skins { get; }

    /// <summary>Clips on skin 0's bones; tracks on nodes that are not its joints are not carried.</summary>
    public IReadOnlyList<AnimationClip> Clips { get; }

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

    /// <summary>Unskinned mesh nodes under a joint, each claimed by the first skin whose joints include it.</summary>
    /// <remarks>
    /// The transform is composed from the node up to the joint (row-vector: child local, then parent).
    /// A mesh node that reaches the root without meeting a joint only shares the file, and is not one.
    /// </remarks>
    public IReadOnlyList<Attachment> Attachments()
    {
        var found = new List<Attachment>();
        var claimed = new HashSet<int>();
        for (var s = 0; s < Skins.Count; s++)
        {
            var boneOfJoint = new Dictionary<int, int>();
            for (var b = 0; b < Skins[s].JointNodes.Count; b++) boneOfJoint[Skins[s].JointNodes[b]] = b;

            for (var n = 0; n < Nodes.Count; n++)
            {
                if (Nodes[n].MeshIndex < 0 || Nodes[n].SkinIndex >= 0 || claimed.Contains(n)) continue;
                var local = Nodes[n].Local;
                var ancestor = Nodes[n].ParentIndex;
                while (ancestor >= 0 && !boneOfJoint.ContainsKey(ancestor))
                {
                    local *= Nodes[ancestor].Local;
                    ancestor = Nodes[ancestor].ParentIndex;
                }

                if (ancestor < 0) continue;
                claimed.Add(n);
                found.Add(new Attachment(n, ancestor, s, boneOfJoint[ancestor], local));
            }
        }

        return found;
    }

    /// <summary>The primitives <paramref name="skin"/> deforms, in node order: what a skinned draw of it uploads.</summary>
    public IEnumerable<Primitive> SkinnedPrimitives(int skin = 0)
    {
        for (var n = 0; n < Nodes.Count; n++)
        {
            if (Nodes[n].SkinIndex != skin || Nodes[n].MeshIndex < 0) continue;
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

    /// <summary>Every primitive a node places, moved to that node's world: the scene as one flat list.</summary>
    /// <remarks>A skinned mesh read skinned stays in mesh space here; only static vertices are moved.</remarks>
    public IEnumerable<(int NodeIndex, Primitive Primitive)> Flattened()
    {
        for (var n = 0; n < Nodes.Count; n++)
        {
            if (Nodes[n].MeshIndex < 0) continue;
            var mesh = Meshes[Nodes[n].MeshIndex];
            foreach (var p in mesh.Primitives)
            {
                yield return (n, mesh.Skinned || World[n].IsIdentity ? p : p with { Mesh = Moved(p.Mesh, World[n]) });
            }
        }
    }

    /// <summary>Reads a cooked model, its vertices in the layout <paramref name="needs"/> declares.</summary>
    public static ModelData Load(string path, ModelNeeds needs = default)
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
                    p.MaterialIndex);
            }).ToArray();
            return new Mesh(m.Name, primitives, m.SkinIndex, skinned);
        }).ToArray();

        var nodes = file.Nodes.Select(n => new Node(n.Name, n.ParentIndex, n.LocalTransform, n.MeshIndex, n.SkinIndex)).ToArray();
        var worlds = ComposeWorld(nodes);
        var skins = file.SkinTable.Select(s =>
        {
            var joints = s.Bones.Select(b => b.NodeIndex).ToArray();
            var hierarchy = JointHierarchy.Resolve(
                s.Bones.Select(b => new Bone(b.Name, b.ParentIndex, b.InverseBindPose)).ToArray(), joints,
                n => nodes[n].ParentIndex, n => nodes[n].Local, n => worlds[n]);
            return new Skin(new Skeleton(hierarchy.Bones.ToArray()), joints, hierarchy.Placement);
        }).ToArray();

        var boneOfNode = new Dictionary<int, int>();
        if (skins.Length > 0)
        {
            for (var b = 0; b < skins[0].JointNodes.Count; b++) boneOfNode[skins[0].JointNodes[b]] = b;
        }

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

        return new ModelData(nodes, meshes, skins, clips, ignored, path);
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

    // Static vertices moved to a world transform, with the normal (and tangent, where carried)
    // following. Positions are Float3 at 0 and normals Float3 at 12 in every static layout.
    private static MeshData Moved(MeshData mesh, in Matrix4x4 world)
    {
        var bytes = (byte[])mesh.VertexBytes.Clone();
        var normalMatrix = GraphicsMatrices.NormalMatrix(world);
        var stride = mesh.Layout.Stride;
        var tangentAt = mesh.Layout.Attributes.FirstOrDefault(a => a.Format == VertexAttributeFormat.Float4)?.Offset ?? -1;
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        var floats = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(bytes);
        for (var v = 0; v < mesh.VertexCount; v++)
        {
            var o = v * stride / 4;
            var p = Vector3.Transform(new Vector3(floats[o], floats[o + 1], floats[o + 2]), world);
            floats[o] = p.X; floats[o + 1] = p.Y; floats[o + 2] = p.Z;
            min = Vector3.Min(min, p);
            max = Vector3.Max(max, p);
            var n = Vector3.TransformNormal(new Vector3(floats[o + 3], floats[o + 4], floats[o + 5]), normalMatrix);
            if (n.LengthSquared() > 1e-12f) n = Vector3.Normalize(n);
            floats[o + 3] = n.X; floats[o + 4] = n.Y; floats[o + 5] = n.Z;
            if (tangentAt < 0) continue;
            var t0 = o + (tangentAt / 4);
            var t = Vector3.TransformNormal(new Vector3(floats[t0], floats[t0 + 1], floats[t0 + 2]), world);
            if (t.LengthSquared() > 1e-12f) t = Vector3.Normalize(t);
            floats[t0] = t.X; floats[t0 + 1] = t.Y; floats[t0 + 2] = t.Z;
        }

        return mesh with { VertexBytes = bytes, Bounds = mesh.VertexCount > 0 ? new Geometry.Bounds3(min, max) : mesh.Bounds };
    }
}

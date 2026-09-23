using System.Numerics;
using Blix.Assets;
using Blix.Geometry;
using Blix.Graphics;
using Blix.Graphics.Images;
using SharpGLTF.Schema2;
using Blix.Cooked;

namespace Blix;

// Loads a .glb / .gltf file and decodes it into engine-shaped types: skinned
// `GltfPrimitive[]` (each with vertex layout VertexPosition3NormalTextureSkin4Tangent
// + a `GltfMaterial` carrying baseColor/normal/metallic-roughness textures), a
// `GltfSkinBinding[]` with parent-first skeletons and placement transforms, and one
// `AnimationClip` per glTF animation that touches the shared joint ordering. It also
// preserves joint attachments and independent static parts from the same file.
//
// Current limits:
// - Multiple skins may share one clip set only when their joint ordering agrees.
// - LINEAR interpolation only (STEP / CUBICSPLINE rejected at import).
// - No morph-target weights.
// - At most four skin influences are retained per vertex: the strongest four,
//   renormalised. Mesh indices use UInt16 or UInt32 as required.
//
// Matrices need no conversion at the boundary: glTF / SharpGLTF deliver them in
// System.Numerics row-vector form, which is exactly the engine's convention
// (F-016) — inverse-bind matrices pass through untransposed.
public sealed class GltfImporter : IAssetImporter<GltfModel>
{
    public string Name => "rigged-model.gltf";

    /// <summary>
    /// Reads a rigged glTF, or refuses it by name.
    /// </summary>
    /// <exception cref="AssetImportException">
    /// The file is not a supported rigged glTF. Import refusals carry the source path so tools can
    /// distinguish invalid or unsupported content from faults in the tool itself.
    /// </exception>
    public GltfModel Import(AssetImportContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!File.Exists(context.SourcePath))
        {
            throw new AssetImportException(context.SourcePath, null, "no file there.");
        }
        // Normalize parser failures and the importer's own content refusals into the same
        // path-bearing boundary for callers and diagnostic tools.
        return AssetImportException.Refusing(context.SourcePath, () => ImportCore(context, preferCooked: true));
    }

    /// <summary>
    /// Imports the glTF itself, ignoring any cooked artifact beside it.
    /// </summary>
    /// <remarks>
    /// Recipes must use this entry point so their output is derived from authored source rather
    /// than from a pre-existing cooked sibling. Runtime callers normally use <see cref="Import"/>.
    /// </remarks>
    public GltfModel ImportSource(AssetImportContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return AssetImportException.Refusing(context.SourcePath, () => ImportCore(context, preferCooked: false));
    }

    /// <summary>Rebuilds a rig from its cooked form — skins, clips, attachments and all.</summary>
    /// <remarks>
    /// Bones, clips, attachments, static parts, materials, and image references are reconstructed
    /// entirely from the cooked artifact; the source glTF is not opened.
    /// </remarks>
    private GltfModel ImportCookedRig(AssetImportContext context, string rigPath, BlixMeshFile cooked)
    {
        var loadWatch = System.Diagnostics.Stopwatch.StartNew();
        var cookedDir = Path.GetDirectoryName(Path.GetFullPath(rigPath)) ?? string.Empty;

        var textureCache = new Dictionary<int, GltfTexture>();
        var materialCache = new Dictionary<int, GltfMaterial>();
        GltfShared.LoadImagesFromTable(cooked.ImageTable, cooked.MaterialTable, cookedDir, textureCache);

        GltfPrimitive Rebuild(BlixMeshPrimitive p)
        {
            var lod0 = p.Lods[0];
            return new GltfPrimitive(
                new MeshData(
                    p.Name, p.VertexBytes, lod0.Indices16 ?? Array.Empty<ushort>(),
                    p.Layout, p.Bounds, Indices32: lod0.Indices32),
                GltfShared.MaterialFromCooked(cooked.MaterialTable, p.MaterialIndex, materialCache, textureCache, rigPath),
                SkinIndex: p.SkinIndex,
                MaterialIndex: p.MaterialIndex);
        }

        var bindings = cooked.SkinTable
            .Select(skin => new GltfSkinBinding(
                new Skeleton(skin.Bones
                    .Select(b => new Bone(b.Name, b.ParentIndex, b.InverseBindPose))
                    .ToArray()),
                skin.MeshNodeTransform))
            .ToArray();

        var animations = cooked.ClipTable.Select(RebuildClip).ToArray();

        var attachments = cooked.AttachmentTable
            .Select(a => new GltfAttachment(
                a.Name, a.JointName, a.JointIndex, a.LocalTransform,
                a.Primitives.Select(Rebuild).ToArray(), a.SkinIndex))
            .ToArray();

        var staticParts = cooked.StaticPartTable
            .Select(sp => new GltfStaticPart(
                sp.Name, sp.WorldTransform, sp.Primitives.Select(Rebuild).ToArray()))
            .ToArray();

        if (AssetLoadLog.Enabled)
        {
            AssetLoadLog.Report(new AssetLoadReport(
                SourcePath: context.SourcePath,
                CookedPath: rigPath,
                Mode: AssetLoadMode.Cooked,
                Bytes: SourceLength(rigPath),
                LoadMs: loadWatch.Elapsed.TotalMilliseconds,
                Recipe: cooked.Cooked?.Stamp.Recipe));
        }

        return new GltfModel(
            cooked.Primitives.Select(Rebuild).ToArray(),
            bindings[0].Skeleton, animations, bindings[0].MeshNodeTransform,
            attachments, staticParts, Array.Empty<GltfIgnored>(), bindings);
    }

    /// <summary>
    /// One clip, from the keyframes it was cooked as.
    /// </summary>
    /// <remarks>
    /// An empty channel array means the channel was absent, which is why it maps back to a null
    /// curve rather than an empty one — <c>KeyframeVector3Curve</c> refuses to exist with no keys,
    /// and rightly: a curve with nothing to evaluate is not a curve.
    /// </remarks>
    private static AnimationClip RebuildClip(BlixMeshClip clip) => new(
        clip.Name,
        clip.Tracks.Select(t => new BoneTrack
        {
            BoneIndex = t.BoneIndex,
            Translation = t.Translation.Length == 0
                ? null
                : new KeyframeVector3Curve(
                    t.Translation.Select(k => new Keyframe<Vector3>(k.Time, k.Value)).ToArray()),
            Rotation = t.Rotation.Length == 0
                ? null
                : new KeyframeQuaternionCurve(
                    t.Rotation.Select(k => new Keyframe<Quaternion>(k.Time, k.Value)).ToArray()),
            Scale = t.Scale.Length == 0
                ? null
                : new KeyframeVector3Curve(
                    t.Scale.Select(k => new Keyframe<Vector3>(k.Time, k.Value)).ToArray()),
        }).ToArray());

    private GltfModel ImportCore(AssetImportContext context, bool preferCooked)
    {
        // A rigged .blixmesh is self-contained and loads without opening the source glTF.
        var directRig = Path.GetExtension(context.SourcePath)
            .Equals(".blixmesh", StringComparison.OrdinalIgnoreCase);
        var rigPath = directRig
            ? context.SourcePath
            : Path.ChangeExtension(context.SourcePath, ".blixmesh");
        if (preferCooked && File.Exists(rigPath))
        {
            var cookedRig = BlixMeshReader.Read(rigPath);

            // A .blixmesh with no skins is a STATIC cook sitting beside a rigged source — which is
            // the normal state of any file the static cook reached first. It is not this importer's
            // to read, and saying so beats loading a character with no skeleton.
            if (cookedRig.IsRigged) return ImportCookedRig(context, rigPath, cookedRig);
            if (directRig)
            {
                throw new AssetImportException(
                    context.SourcePath, null,
                    "this .blixmesh carries no skin, so there is no rig in it — " +
                    "load it as a static model instead (GltfStaticImporter)");
            }
        }

        var loadWatch = System.Diagnostics.Stopwatch.StartNew();

        var model = AssetImportException.Refusing(context.SourcePath, () => ModelRoot.Load(context.SourcePath));

        // Group skinned mesh nodes by their owning skin. Encounter order supplies a stable binding
        // index but carries no semantic priority.
        var skinOrder = new List<Skin>();
        var nodesBySkin = new Dictionary<Skin, List<Node>>();
        foreach (var node in model.LogicalNodes)
        {
            if (node.Mesh is null || node.Skin is null) continue;
            if (!nodesBySkin.TryGetValue(node.Skin, out var group))
            {
                group = new List<Node>();
                nodesBySkin[node.Skin] = group;
                skinOrder.Add(node.Skin);
            }

            group.Add(node);
        }

        if (skinOrder.Count == 0)
        {
            // Valid static glTF content is outside this importer's domain rather than a parser fault.
            throw new AssetImportException(
                context.SourcePath, null,
                "no node has both a mesh and a skin, so there is no rig here — " +
                "load it as a static model instead (GltfStaticImporter)");
        }

        // Nodes driven by one skin share one palette and model matrix, so their world matrices must
        // agree. Different skins retain independent placement transforms.
        //
        // Each skin's mesh-node transform is its group's. It is NOT baked into vertices: per the
        // glTF skinning spec the inverse binds map MESH-LOCAL vertices into joint space, so baking
        // it would put the skinning maths in the wrong frame. The renderer composes it at draw time
        //    uModel = userTransform * MeshNodeTransform
        // F-016: engine row-vector form matches SharpGLTF, so no transpose.
        foreach (var group in nodesBySkin.Values)
        {
            var head = group[0];
            foreach (var node in group)
            {
                if (node.WorldMatrix == head.WorldMatrix) continue;
                throw new InvalidOperationException(
                    $"glTF '{context.SourcePath}' has multiple skinned-mesh nodes sharing ONE skin " +
                    $"but with different world matrices. Mesh '{node.Mesh!.Name}' transform diverges " +
                    $"from '{head.Mesh!.Name}'. Per-submesh mesh-node transforms aren't supported; " +
                    $"meshes at different places need different skins, which this importer does read.");
            }
        }

        // Decode every primitive across every skinned-mesh node. Each gets its
        // own MeshData (skinned vertex stream) and the material it references.
        // Textures are deduped across materials via a shared cache so an image
        // referenced by two primitives only decodes once. We pre-decode every
        // unique source image in parallel before walking primitives -- PNG/JPEG
        // decode is the dominant cost for heavy assets. Same pattern as the
        // static importer; see GltfStaticImporter.PreDecodeImages for rationale.
        var textureCache = new Dictionary<int, GltfTexture>();
        var gltfDir = Path.GetDirectoryName(Path.GetFullPath(context.SourcePath)) ?? string.Empty;
        GltfShared.PreDecodeImages(model, textureCache, gltfDir, context.SourcePath);
        var materialCache = new Dictionary<int, GltfMaterial>();
        var primitivesList = new List<GltfPrimitive>();
        var bindings = new List<GltfSkinBinding>();
        var remapsBySkin = new List<int[]>();
        for (var s = 0; s < skinOrder.Count; s++)
        {
            var owner = skinOrder[s];
            var group = nodesBySkin[owner];

            // Each skin gets its own source-to-parent-first joint remap; an index is meaningful
            // only within the skin that supplied it.
            var (skinBones, skinRemap) = BuildSkeletonAndOrdering(owner);
            var skinRemaps = skinRemap;
            bindings.Add(new GltfSkinBinding(new Skeleton(skinBones), group[0].WorldMatrix));
            remapsBySkin.Add(skinRemaps);

            foreach (var node in group)
            {
                var mesh = node.Mesh!;
                for (var i = 0; i < mesh.Primitives.Count; i++)
                {
                    var prim = mesh.Primitives[i];
                    var meshName = $"{mesh.Name ?? "gltf_mesh"}.{i}";
                    var meshData = BuildMeshData(meshName, prim, skinRemap);
                    var material = GltfShared.ExtractMaterial(prim.Material, materialCache, textureCache, context.SourcePath);
                    primitivesList.Add(new GltfPrimitive(
                        meshData, material, SkinIndex: s, MaterialIndex: prim.Material?.LogicalIndex ?? -1));
                }
            }
        }
        var primitives = primitivesList.ToArray();

        // Resolve each attachment against the first skin containing its ancestor joint. The skin
        // index selects the skeleton whose remapped bone index the attachment records; inverse bind
        // matrices are not part of attachment placement.
        var attachments = new List<GltfAttachment>();
        var claimed = new HashSet<string>(StringComparer.Ordinal);
        for (var s = 0; s < skinOrder.Count; s++)
        {
            foreach (var found in CollectAttachments(
                         model, skinOrder[s], remapsBySkin[s], materialCache, textureCache, context.SourcePath))
            {
                if (!claimed.Add(found.Name)) continue;
                attachments.Add(found with { SkinIndex = s });
            }
        }

        // Preserve unskinned mesh nodes that are not joint attachments as independently placed
        // static parts of the rigged model.
        var attached = new HashSet<string>(attachments.Select(a => a.Name), StringComparer.Ordinal);
        var staticParts = new List<GltfStaticPart>();
        foreach (var node in model.LogicalNodes)
        {
            if (node.Mesh is null || node.Skin is not null) continue;
            var name = node.Name ?? node.Mesh.Name ?? "?";
            if (attached.Contains(name)) continue;

            var parts = new List<GltfPrimitive>();
            for (var i = 0; i < node.Mesh.Primitives.Count; i++)
            {
                var prim = node.Mesh.Primitives[i];
                var meshData = GltfStaticImporter.BuildStaticMeshData(
                    $"{name}.{i}", prim, Matrix4x4.Identity, Matrix4x4.Identity, includeColour: true);
                parts.Add(new GltfPrimitive(
                    meshData, GltfShared.ExtractMaterial(prim.Material, materialCache, textureCache, context.SourcePath),
                    MaterialIndex: prim.Material?.LogicalIndex ?? -1));
            }

            if (parts.Count > 0) staticParts.Add(new GltfStaticPart(name, node.WorldMatrix, parts.ToArray()));
        }

        // Filter animations to those that touch a joint; an animation targeting only non-skin nodes
        // (scene camera, light) becomes an empty clip and gets dropped.
        //
        // AnimationClip tracks store bone indices, while glTF channels target nodes. One shared clip
        // set is therefore valid only when every animated skin resolves each joint to the same
        // parent-first index. Geometry-only multi-skin files do not need this restriction.
        var clipsAgree = true;
        for (var s = 1; s < skinOrder.Count && clipsAgree && model.LogicalAnimations.Count > 0; s++)
        {
            var other = skinOrder[s];
            if (other.JointsCount != skinOrder[0].JointsCount) { clipsAgree = false; break; }
            for (var j = 0; j < other.JointsCount; j++)
            {
                if (ReferenceEquals(other.GetJoint(j).Joint, skinOrder[0].GetJoint(j).Joint)
                    && remapsBySkin[s][j] == remapsBySkin[0][j])
                {
                    continue;
                }

                clipsAgree = false;
                break;
            }
        }

        if (!clipsAgree)
        {
            throw new AssetImportException(
                context.SourcePath, null,
                $"this file's {skinOrder.Count} skins order their joints differently, so one set of "
                + "animation clips cannot drive them all — clip tracks are bone indices against a "
                + "single skeleton. Reading the skins is supported; per-skin clips are not yet.");
        }

        var animations = new List<AnimationClip>();
        foreach (var anim in model.LogicalAnimations)
        {
            var clip = BuildAnimationClip(anim, skinOrder[0], remapsBySkin[0]);
            if (clip.Tracks.Length > 0)
            {
                animations.Add(clip);
            }
        }

        // A rig may also carry coloured static parts and attachments. Audit each logical primitive
        // against the layout that consumed it; Distinct inside CollectIgnored folds repeated nodes
        // using the same primitive and layout.
        var ignored = GltfShared.CollectIgnored(model.LogicalNodes
            .Where(node => node.Mesh is not null)
            .SelectMany(node => node.Mesh!.Primitives.Select(primitive =>
                (primitive, node.Skin is null
                    ? GltfShared.VertexFeatures.Colour
                    : GltfShared.VertexFeatures.Skinning | GltfShared.VertexFeatures.Tangents))));

        if (AssetLoadLog.Enabled)
        {
            var warning = ignored.Length == 0
                ? null
                : "ignored " + string.Join(", ",
                    ignored.Select(i => $"{i.Semantic} ({i.Primitives} prim) — {i.Explanation}"));

            AssetLoadLog.Report(new AssetLoadReport(
                SourcePath: context.SourcePath,
                CookedPath: null,
                Mode: AssetLoadMode.Source,
                Bytes: SourceLength(context.SourcePath),
                LoadMs: loadWatch.Elapsed.TotalMilliseconds,
                Warning: warning));
        }

        return new GltfModel(
            primitives, bindings[0].Skeleton, animations.ToArray(), bindings[0].MeshNodeTransform,
            attachments.ToArray(), staticParts.ToArray(), ignored, bindings.ToArray());
    }

    private static long SourceLength(string path)
    {
        try
        {
            return new FileInfo(path) is { Exists: true } f ? f.Length : 0L;
        }
        catch (IOException)
        {
            return 0L;
        }
    }






    // Build the engine-side Bone[] in topo-sorted (parent-first) order, returning
    // the bones AND the old-to-new index mapping (used later to remap vertex joint
    // indices and animation channel targets).

    /// <summary>
    /// Every static mesh node whose ancestor chain reaches a joint of this skin.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Walks up from each mesh node rather than down from each joint because a glTF may
    /// to put a group node between the joint and the mesh and the relationship is still an
    /// attachment. Walking down would need to know how deep to look; walking up terminates at the
    /// first joint or at the root, and there is nothing to guess.
    /// </para>
    /// <para>
    /// <see cref="BuildSkeletonAndOrdering"/> topologically
    /// sorts the skin's joints so parents precede children, so the skin's own index and the
    /// skeleton's are different numbers for the same bone on any rig that was not already sorted.
    /// Recording the raw skin index would therefore attach geometry to the wrong bone.
    /// </para>
    /// <para>
    /// Vertices stay in the node's own space. The static builder bakes a world matrix
    /// into positions, which is right for a prop that never moves and wrong for one carried by a
    /// hand; identity goes in and the placement rides on <see cref="GltfAttachment.LocalTransform"/>
    /// instead, to be composed with the joint's animated transform at draw time.
    /// </para>
    /// </remarks>


    private static GltfAttachment[] CollectAttachments(
        ModelRoot model,
        Skin skin,
        int[] oldToNew,
        Dictionary<int, GltfMaterial> materialCache,
        Dictionary<int, GltfTexture> textureCache,
        string containerPath)
    {
        var jointToSkinIndex = new Dictionary<Node, int>();
        for (var i = 0; i < skin.Joints.Count; i++) jointToSkinIndex[skin.Joints[i]] = i;

        var found = new List<GltfAttachment>();
        foreach (var node in model.LogicalNodes)
        {
            if (node.Mesh is null || node.Skin is not null) continue;

            // Up the chain, composing as we go. Row-vector order (F-016): a child's local is
            // pre-multiplied onto what is already accumulated, matching ComputeBonePalette's
            // world = local * parentWorld recurrence.
            var local = node.LocalMatrix;
            var ancestor = node.VisualParent;
            while (ancestor is not null && !jointToSkinIndex.ContainsKey(ancestor))
            {
                local *= ancestor.LocalMatrix;
                ancestor = ancestor.VisualParent;
            }

            // Reached the root without meeting a joint: a static mesh that simply shares the file.
            // Not an attachment, and quietly adopting it would put scenery in the character's hand.
            if (ancestor is null) continue;

            var primitives = new List<GltfPrimitive>();
            for (var i = 0; i < node.Mesh.Primitives.Count; i++)
            {
                var prim = node.Mesh.Primitives[i];
                var name = $"{node.Name ?? node.Mesh.Name ?? "attachment"}.{i}";
                // Rig attachments always include colour because their current consumer, RigView,
                // uses the coloured static layout. General static imports keep colour opt-in.
                var meshData = GltfStaticImporter.BuildStaticMeshData(
                    name, prim, Matrix4x4.Identity, Matrix4x4.Identity, includeColour: true);
                primitives.Add(new GltfPrimitive(
                    meshData, GltfShared.ExtractMaterial(prim.Material, materialCache, textureCache, containerPath),
                    MaterialIndex: prim.Material?.LogicalIndex ?? -1));
            }

            if (primitives.Count == 0) continue;

            found.Add(new GltfAttachment(
                Name: node.Name ?? node.Mesh.Name ?? $"attachment_{found.Count}",
                JointName: ancestor.Name ?? "?",
                JointIndex: oldToNew[jointToSkinIndex[ancestor]],
                LocalTransform: local,
                Primitives: primitives.ToArray()));
        }

        return found.ToArray();
    }

    private static (Bone[] bones, int[] oldToNew) BuildSkeletonAndOrdering(Skin skin)
    {
        var joints = skin.Joints;
        var ibmList = skin.InverseBindMatrices;
        var n = joints.Count;
        if (ibmList.Count != n)
        {
            throw new InvalidOperationException(
                $"Skin has {n} joints but {ibmList.Count} inverse-bind matrices.");
        }

        // Joint → index lookup for parent resolution.
        var jointToIndex = new Dictionary<Node, int>();
        for (var i = 0; i < n; i++) jointToIndex[joints[i]] = i;

        // For each joint, the parent index in the joints list — walking up the
        // scene-graph VisualParent chain until we find another joint or hit null.
        // Non-joint ancestors get skipped (e.g., the armature root node that's
        // not itself a joint).
        var parentOld = new int[n];
        for (var i = 0; i < n; i++)
        {
            var p = joints[i].VisualParent;
            while (p is not null && !jointToIndex.ContainsKey(p))
            {
                p = p.VisualParent;
            }
            parentOld[i] = p is null ? -1 : jointToIndex[p];
        }

        // Topological sort: parents before children. The recursive Visit visits
        // a node's parent before adding the node — so the output ordering has
        // every parent appearing earlier than its children.
        var visited = new bool[n];
        var orderNewToOld = new List<int>(n);
        void Visit(int i)
        {
            if (visited[i]) return;
            visited[i] = true;
            if (parentOld[i] >= 0) Visit(parentOld[i]);
            orderNewToOld.Add(i);
        }
        for (var i = 0; i < n; i++) Visit(i);

        var oldToNew = new int[n];
        for (var newIdx = 0; newIdx < orderNewToOld.Count; newIdx++)
        {
            oldToNew[orderNewToOld[newIdx]] = newIdx;
        }

        // Emit bones in the new (topo-sorted) order with remapped parent indices.
        // IBMs pass through untransposed: SharpGLTF returns System.Numerics
        // row-vector matrices, which is exactly the engine's convention (F-016).
        var bones = new Bone[n];
        for (var newIdx = 0; newIdx < n; newIdx++)
        {
            var oldIdx = orderNewToOld[newIdx];
            var joint = joints[oldIdx];
            var ibm = ibmList[oldIdx];
            var parentNew = parentOld[oldIdx] >= 0 ? oldToNew[parentOld[oldIdx]] : -1;
            // F-016: SharpGLTF IBM is already row-vector form (matching the
            // engine convention). Pass through without transpose.
            bones[newIdx] = new Bone(joint.Name ?? $"bone_{newIdx}", parentNew, ibm);
        }
        return (bones, oldToNew);
    }

    // Pack the mesh primitive's vertex streams into the engine's skinned vertex
    // layout. POSITION is required; NORMAL / TEXCOORD_0 default to safe values
    // (up-normal, (0, 0)) if absent. JOINTS_0 / WEIGHTS_0 are required — this is
    // a skinned mesh importer; an unrigged glTF should use GltfStaticImporter.
    /// <summary>
    /// The four strongest influences on one vertex, renormalised, out of however many the file gives.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Four is the vertex-layout limit. The layout
    /// carries four bone indices and four weights; widening it to eight costs 32 bytes on every
    /// skinned vertex in every asset, for influences that are almost always negligible. That is an
    /// bandwidth/fidelity tradeoff applied to every skinned asset.
    /// </para>
    /// <para>
    /// The strongest four are retained. glTF does not require influence sets to be sorted, so
    /// "the first four" can discard the influence that actually shapes the vertex and keep three that
    /// barely move it.
    /// </para>
    /// <para>
    /// Retained weights are renormalised. Weights sum to 1
    /// across ALL sets, so keeping a subset leaves them summing to less, and a skinning matrix scaled
    /// by 0.8 drags its vertex a fifth of the way to the origin. Approximate deformation is a
    /// limitation; a collapsing mesh is a bug.
    /// </para>
    /// </remarks>
    private static (Vector4 Joints, Vector4 Weights) SelectInfluences(
        int v,
        IList<Vector4> joints0, IList<Vector4> weights0,
        List<IList<Vector4>> extraJoints, List<IList<Vector4>> extraWeights,
        int[] oldToNew)
    {
        Span<(int Joint, float Weight)> all = stackalloc (int, float)[4 + (extraJoints.Count * 4)];
        var n = 0;
        void Take(Vector4 j, Vector4 w, Span<(int, float)> into, ref int at)
        {
            into[at++] = ((int)j.X, w.X);
            into[at++] = ((int)j.Y, w.Y);
            into[at++] = ((int)j.Z, w.Z);
            into[at++] = ((int)j.W, w.W);
        }

        Take(joints0[v], weights0[v], all, ref n);
        for (var s = 0; s < extraJoints.Count; s++) Take(extraJoints[s][v], extraWeights[s][v], all, ref n);

        // Selection sort for the top four: n is at most a handful, and this keeps ties in the order
        // the file listed them so a re-import gives the same answer.
        for (var i = 0; i < 4 && i < n; i++)
        {
            var best = i;
            for (var k = i + 1; k < n; k++)
            {
                if (all[k].Weight > all[best].Weight) best = k;
            }

            (all[i], all[best]) = (all[best], all[i]);
        }

        var total = 0f;
        for (var i = 0; i < 4 && i < n; i++) total += all[i].Weight;
        var scale = total > 1e-6f ? 1f / total : 0f;

        var idx = Vector4.Zero;
        var wt = Vector4.Zero;
        for (var i = 0; i < 4; i++)
        {
            var (joint, weight) = i < n ? all[i] : (0, 0f);
            var remapped = (float)oldToNew[joint];
            var scaled = weight * scale;
            switch (i)
            {
                case 0: idx.X = remapped; wt.X = scaled; break;
                case 1: idx.Y = remapped; wt.Y = scaled; break;
                case 2: idx.Z = remapped; wt.Z = scaled; break;
                default: idx.W = remapped; wt.W = scaled; break;
            }
        }

        return (idx, wt);
    }

    private static MeshData BuildMeshData(string name, MeshPrimitive primitive, int[] oldToNew)
    {
        var positions = primitive.GetVertexAccessor("POSITION")?.AsVector3Array()
            ?? throw new InvalidOperationException("glTF mesh primitive missing required POSITION accessor.");
        var jointsAcc = primitive.GetVertexAccessor("JOINTS_0")
            ?? throw new InvalidOperationException("glTF mesh primitive missing JOINTS_0 — not a skinned mesh.");
        var weightsAcc = primitive.GetVertexAccessor("WEIGHTS_0")
            ?? throw new InvalidOperationException("glTF mesh primitive missing WEIGHTS_0 — not a skinned mesh.");
        var jointsArray = jointsAcc.AsVector4Array();
        var weightsArray = weightsAcc.AsVector4Array();

        // Gather every complete JOINTS_n/WEIGHTS_n pair before selecting and renormalising the
        // strongest four influences for the engine vertex layout.
        var extraJoints = new List<IList<Vector4>>();
        var extraWeights = new List<IList<Vector4>>();
        for (var set = 1; ; set++)
        {
            var j = primitive.GetVertexAccessor($"JOINTS_{set}");
            var w = primitive.GetVertexAccessor($"WEIGHTS_{set}");
            if (j is null || w is null) break;
            extraJoints.Add(j.AsVector4Array());
            extraWeights.Add(w.AsVector4Array());
        }
        var normals = primitive.GetVertexAccessor("NORMAL")?.AsVector3Array();
        var uvs = primitive.GetVertexAccessor("TEXCOORD_0")?.AsVector2Array();
        // Per-vertex tangents. glTF stores them as vec4 — XYZ is the tangent
        // direction in mesh-local space, W is +1 or -1 indicating the bitangent
        // handedness (the bitangent is `cross(normal, tangent.xyz) * tangent.w`).
        // Missing on most authored assets that weren't baked with MikkTSpace; we
        // emit (0, 0, 0, 0) as a sentinel and let the fragment shader fall back
        // to derivative-based synthesis.
        var tangents = primitive.GetVertexAccessor("TANGENT")?.AsVector4Array();

        var vertexCount = positions.Count;
        var vertices = new VertexPosition3NormalTextureSkin4Tangent[vertexCount];

        // Vertices are kept in mesh-local space (the glTF skinning spec requires
        // it — IBMs map mesh-local to joint-local, so any transform applied before
        // the IBM breaks the math). The mesh node's ancestor transform is exposed
        // on GltfModel for the renderer to compose into uModel.
        var min = positions[0];
        var max = positions[0];
        for (var v = 0; v < vertexCount; v++)
        {
            var p = positions[v];
            min = Vector3.Min(min, p);
            max = Vector3.Max(max, p);

            var n = normals?[v] ?? new Vector3(0.0f, 1.0f, 0.0f);
            // Preserve glTF UVs: glTF coordinates, decoded row order, and Vulkan sampling all use
            // the same top-left image convention on this path.
            var uv = uvs?[v] ?? Vector2.Zero;

            // Remap joint indices through the topo-sort. Each slot is a float that
            // we cast to int, look up, and store back as float (the vertex shader
            // does int(...) at lookup time). Unused slots (weight == 0) still get
            // remapped so the stored index stays within bounds.
            Vector4 newIdx, w;
            if (extraJoints.Count == 0)
            {
                // Preserve authored slot order when all influences already fit the layout.
                var oldIdx = jointsArray[v];
                newIdx = new Vector4(
                    oldToNew[(int)oldIdx.X],
                    oldToNew[(int)oldIdx.Y],
                    oldToNew[(int)oldIdx.Z],
                    oldToNew[(int)oldIdx.W]);
                w = weightsArray[v];
            }
            else
            {
                (newIdx, w) = SelectInfluences(v, jointsArray, weightsArray, extraJoints, extraWeights, oldToNew);
            }

            var t = tangents?[v] ?? Vector4.Zero;

            vertices[v] = new VertexPosition3NormalTextureSkin4Tangent(
                new GraphicsVector3(p.X, p.Y, p.Z),
                new GraphicsVector3(n.X, n.Y, n.Z),
                new GraphicsVector2(uv.X, uv.Y),
                new GraphicsVector4(newIdx.X, newIdx.Y, newIdx.Z, newIdx.W),
                new GraphicsVector4(w.X, w.Y, w.Z, w.W),
                new GraphicsVector4(t.X, t.Y, t.Z, t.W));
        }

        // Indices: glTF supports unsigned byte / short / int. We pick UInt16
        // when the vertex count fits, UInt32 otherwise. Mesh primitives
        // without an index buffer (rare for skinned content) get a
        // sequential index list synthesised.
        var rawIndices = primitive.GetIndices();
        var needsUInt32 = vertexCount > ushort.MaxValue;
        ushort[] indices16;
        uint[]? indices32;
        if (rawIndices is null || rawIndices.Count == 0)
        {
            if (needsUInt32)
            {
                indices16 = Array.Empty<ushort>();
                indices32 = new uint[vertexCount];
                for (var i = 0; i < vertexCount; i++) indices32[i] = (uint)i;
            }
            else
            {
                indices32 = null;
                indices16 = new ushort[vertexCount];
                for (var i = 0; i < vertexCount; i++) indices16[i] = (ushort)i;
            }
        }
        else if (needsUInt32)
        {
            indices16 = Array.Empty<ushort>();
            indices32 = new uint[rawIndices.Count];
            for (var i = 0; i < rawIndices.Count; i++) indices32[i] = rawIndices[i];
        }
        else
        {
            indices32 = null;
            indices16 = new ushort[rawIndices.Count];
            for (var i = 0; i < rawIndices.Count; i++) indices16[i] = (ushort)rawIndices[i];
        }

        var bytes = VertexPosition3NormalTextureSkin4Tangent.Pack(vertices);
        return new MeshData(
            name, bytes, indices16,
            VertexPosition3NormalTextureSkin4Tangent.Layout,
            new Bounds3(min, max),
            Indices32: indices32);
    }

    // Convert one glTF animation into one engine AnimationClip. Channels targeting
    // non-skin nodes are skipped silently; the per-bone PRS tracks group every
    // channel that targets a single joint into a single BoneTrack.
    private static AnimationClip BuildAnimationClip(Animation anim, Skin skin, int[] oldToNew)
    {
        var jointToIndex = new Dictionary<Node, int>();
        for (var i = 0; i < skin.Joints.Count; i++) jointToIndex[skin.Joints[i]] = i;

        var trackBuilders = new Dictionary<int, TrackBuilder>();
        foreach (var channel in anim.Channels)
        {
            if (channel.TargetNode is null) continue;
            if (!jointToIndex.TryGetValue(channel.TargetNode, out var oldIdx)) continue;
            var newIdx = oldToNew[oldIdx];
            if (!trackBuilders.TryGetValue(newIdx, out var builder))
            {
                builder = new TrackBuilder(newIdx);
                trackBuilders[newIdx] = builder;
            }

            switch (channel.TargetNodePath)
            {
                case PropertyPath.translation:
                    builder.Translation = BuildVector3Curve(channel.GetTranslationSampler(), anim.Name, "translation");
                    break;
                case PropertyPath.rotation:
                    builder.Rotation = BuildQuaternionCurve(channel.GetRotationSampler(), anim.Name, "rotation");
                    break;
                case PropertyPath.scale:
                    builder.Scale = BuildVector3Curve(channel.GetScaleSampler(), anim.Name, "scale");
                    break;
                case PropertyPath.weights:
                    // Morph-target weight animations aren't supported. Future
                    // morph-target work extends BoneTrack or adds a parallel
                    // MorphTrack.
                    break;
            }
        }

        var tracks = new BoneTrack[trackBuilders.Count];
        var ti = 0;
        foreach (var b in trackBuilders.Values)
        {
            tracks[ti++] = new BoneTrack
            {
                BoneIndex = b.BoneIndex,
                Translation = b.Translation,
                Rotation = b.Rotation,
                Scale = b.Scale,
            };
        }
        return new AnimationClip(anim.Name ?? "anim", tracks);
    }

    private static KeyframeVector3Curve BuildVector3Curve(IAnimationSampler<Vector3> sampler, string? animName, string channelName)
    {
        if (sampler.InterpolationMode != AnimationInterpolationMode.LINEAR)
        {
            throw new NotSupportedException(
                $"glTF animation '{animName}' channel '{channelName}' uses interpolation mode " +
                $"{sampler.InterpolationMode}; only LINEAR is supported.");
        }
        var keys = new List<Keyframe<Vector3>>();
        foreach (var (time, value) in sampler.GetLinearKeys())
        {
            keys.Add(new Keyframe<Vector3>(time, value));
        }
        return new KeyframeVector3Curve(keys.ToArray());
    }

    private static KeyframeQuaternionCurve BuildQuaternionCurve(IAnimationSampler<Quaternion> sampler, string? animName, string channelName)
    {
        if (sampler.InterpolationMode != AnimationInterpolationMode.LINEAR)
        {
            throw new NotSupportedException(
                $"glTF animation '{animName}' channel '{channelName}' uses interpolation mode " +
                $"{sampler.InterpolationMode}; only LINEAR is supported.");
        }
        var keys = new List<Keyframe<Quaternion>>();
        foreach (var (time, value) in sampler.GetLinearKeys())
        {
            keys.Add(new Keyframe<Quaternion>(time, value));
        }
        return new KeyframeQuaternionCurve(keys.ToArray());
    }

    private sealed class TrackBuilder
    {
        public TrackBuilder(int boneIndex) { BoneIndex = boneIndex; }
        public int BoneIndex { get; }
        public KeyframeVector3Curve?    Translation { get; set; }
        public KeyframeQuaternionCurve? Rotation    { get; set; }
        public KeyframeVector3Curve?    Scale       { get; set; }
    }
}

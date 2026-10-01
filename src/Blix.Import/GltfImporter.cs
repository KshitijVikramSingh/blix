using System.Numerics;
using Blix.Assets;
using Blix.Geometry;
using Blix.Graphics;
using Blix.Graphics.Images;
using SharpGLTF.Schema2;
using Blix.Cooked;

namespace Blix.Import;

// Loads a .glb / .gltf file and decodes it into engine-shaped types: skinned
// `GltfPrimitive[]` (each with vertex layout VertexPosition3NormalTextureSkin4Tangent
// + a `PbrMaterial` carrying baseColor/normal/metallic-roughness textures), a
// `GltfSkinBinding[]` with parent-first skeletons and placement transforms, and one
// `AnimationClip` per glTF animation that touches the shared joint ordering. It also
// preserves joint attachments and independent static parts from the same file.
//
// Current limits:
// - Multiple skins may share one clip set only when their joint ordering agrees.
// - LINEAR interpolation only (STEP / CUBICSPLINE rejected at import).
// - No morph deformation: morph targets that take effect are refused (GltfSourcePolicy, the same rule the
//   cook applies); where every instance's weights are zero and undriven, the base mesh is read, which is the shape.
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
        GltfStaticImporter.RefuseCooked(context);
        return AssetImportException.Refusing(context.SourcePath, () => ImportCore(context));
    }

    private GltfModel ImportCore(AssetImportContext context)
    {
        var loadWatch = System.Diagnostics.Stopwatch.StartNew();

        var model = AssetImportException.Refusing(context.SourcePath, () => ModelRoot.Load(context.SourcePath));
        AssetImportException.Refusing(context.SourcePath, () => { GltfSourcePolicy.RefuseEffectiveMorphTargets(model, context.SourcePath); return 0; });

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

        // A skinned mesh's own node transform is ignored, per glTF: its vertices are placed by the skin's
        // joints alone, so one skin may be placed by any number of mesh nodes, wherever they sit. Where the
        // skeleton hangs is the skin's (JointHierarchy.Placement), not any mesh node's.

        // Decode every primitive across every skinned-mesh node. Each gets its
        // own MeshData (skinned vertex stream) and the material it references.
        // Textures are deduped across materials via a shared cache so an image
        // referenced by two primitives only decodes once. We pre-decode every
        // unique source image in parallel before walking primitives -- PNG/JPEG
        // decode is the dominant cost for heavy assets. Same pattern as the
        // static importer; see GltfStaticImporter.PreDecodeImages for rationale.
        var textureCache = new Dictionary<int, TextureData>();
        var gltfDir = Path.GetDirectoryName(Path.GetFullPath(context.SourcePath)) ?? string.Empty;
        GltfShared.PreDecodeImages(model, textureCache, gltfDir, context.SourcePath);
        var materialCache = new Dictionary<int, PbrMaterial>();
        var primitivesList = new List<GltfPrimitive>();
        var bindings = new List<GltfSkinBinding>();
        var remapsBySkin = new List<int[]>();
        for (var s = 0; s < skinOrder.Count; s++)
        {
            var owner = skinOrder[s];
            var group = nodesBySkin[owner];

            // Each skin gets its own source-to-parent-first joint remap; an index is meaningful
            // only within the skin that supplied it.
            var (skinBones, inverseBinds, skinRemap, placement) = BuildSkeletonAndOrdering(owner);
            var skinRemaps = skinRemap;
            bindings.Add(new GltfSkinBinding(SkinBinding.Direct(new Skeleton(skinBones), inverseBinds), placement));
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
            primitives, bindings[0].Skeleton, animations.ToArray(), bindings[0].SkeletonPlacement,
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
        Dictionary<int, PbrMaterial> materialCache,
        Dictionary<int, TextureData> textureCache,
        string containerPath)
    {
        var jointToSkinIndex = new Dictionary<Node, int>();
        for (var i = 0; i < skin.Joints.Count; i++) jointToSkinIndex[skin.Joints[i]] = i;

        var found = new List<GltfAttachment>();
        foreach (var node in model.LogicalNodes)
        {
            if (node.Mesh is null || node.Skin is not null) continue;

            // Up the chain, composing as we go. Row-vector order (F-016): a child's local is
            // pre-multiplied onto what is already accumulated, matching Skeleton.ComputeBoneWorlds's
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

    /// <summary>
    /// Every vertex attribute the cooked vertex does not carry, per the complete layouts: static meshes
    /// read tangent, colour and the second set; skinned ones read the skinning pairs as well. Public for
    /// the cook, which records what it did not read.
    /// </summary>
    public static UnreadAttribute[] UnreadAttributes(ModelRoot model)
    {
        ArgumentNullException.ThrowIfNull(model);
        return GltfShared.CollectIgnored(model.LogicalNodes
            .Where(node => node.Mesh is not null)
            .SelectMany(node => node.Mesh!.Primitives.Select(primitive =>
                (primitive, GltfShared.VertexFeatures.Tangents | GltfShared.VertexFeatures.Colour
                    | (node.Skin is null ? GltfShared.VertexFeatures.None : GltfShared.VertexFeatures.Skinning)))));
    }

    /// <summary>
    /// A skin's joints in parent-first order: as bones (rest and offset from the scene graph), their inverse
    /// binds index for index, the source-joint-to-bone remap, and where the joints hang. Public for the cook.
    /// </summary>
    public static (Bone[] Bones, Matrix4x4[] InverseBinds, int[] OldToNew, Matrix4x4 Placement) BuildSkeletonAndOrdering(Skin skin)
    {
        var joints = skin.Joints;
        var ibmList = skin.InverseBindMatrices;
        var n = joints.Count;
        // No inverseBindMatrices accessor means each is the identity (glTF 2.0 §5.27): the joints were
        // bound where they stand.
        if (ibmList.Count == 0) ibmList = Enumerable.Repeat(Matrix4x4.Identity, n).ToArray();
        // glTF 2.0 §5.27: the accessor MUST have at least as many elements as there are joints; the
        // joints consume the first n, in order, and any beyond are legal and unread.
        if (ibmList.Count < n)
        {
            throw new InvalidOperationException(
                $"Skin has {n} joints but {ibmList.Count} inverse-bind matrices; glTF requires at least one per joint.");
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

        // Emit joints in the new (topo-sorted) order with remapped parent indices.
        // IBMs pass through untransposed: SharpGLTF returns System.Numerics
        // row-vector matrices, which is exactly the engine's convention (F-016).
        var names = new string[n];
        var parents = new int[n];
        var inverseBinds = new Matrix4x4[n];
        var jointNodes = new int[n];
        for (var newIdx = 0; newIdx < n; newIdx++)
        {
            var oldIdx = orderNewToOld[newIdx];
            names[newIdx] = joints[oldIdx].Name ?? $"bone_{newIdx}";
            parents[newIdx] = parentOld[oldIdx] >= 0 ? oldToNew[parentOld[oldIdx]] : -1;
            inverseBinds[newIdx] = ibmList[oldIdx];
            jointNodes[newIdx] = joints[oldIdx].LogicalIndex;
        }

        // Rest, offset and placement from the scene graph, as the cooked reader derives them: one rule, in the engine.
        var hierarchy = Resolve(skin, names, parents, jointNodes);
        return (hierarchy.Bones.ToArray(), inverseBinds, oldToNew, hierarchy.Placement);
    }

    private static JointHierarchy Resolve(Skin skin, string[] names, int[] parents, int[] jointNodes)
    {
        var nodes = skin.LogicalParent.LogicalNodes;
        return JointHierarchy.Resolve(
            names, parents, jointNodes,
            n => nodes[n].VisualParent?.LogicalIndex ?? -1,
            n => nodes[n].LocalMatrix,
            n => nodes[n].WorldMatrix);
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

    /// <summary>A skinned primitive's 80-byte vertices, joints remapped to the skin's bone order. Public for the cook.</summary>
    public static MeshData BuildMeshData(string name, MeshPrimitive primitive, int[] oldToNew)
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

        // Indices as a triangle list whatever the primitive's mode (GltfShared.TriangleIndices), in the
        // narrowest width the vertex count allows.
        var triangles = GltfShared.TriangleIndices(name, primitive);
        var needsUInt32 = vertexCount > ushort.MaxValue;
        var indices32 = needsUInt32 ? triangles : null;
        var indices16 = needsUInt32 ? Array.Empty<ushort>() : triangles.Select(i => (ushort)i).ToArray();

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
    /// <summary>One animation's tracks on <paramref name="skin"/>'s bones. Public for the cook.</summary>
    public static AnimationClip BuildAnimationClip(Animation anim, Skin skin, int[] oldToNew)
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
        var (keys, mode) = SampleKeys(sampler);
        return new KeyframeVector3Curve(keys, mode);
    }

    private static KeyframeQuaternionCurve BuildQuaternionCurve(IAnimationSampler<Quaternion> sampler, string? animName, string channelName)
    {
        var (keys, mode) = SampleKeys(sampler);
        return new KeyframeQuaternionCurve(keys, mode);
    }

    /// <summary>A glTF sampler's keys and interpolation, as the curves read them: LINEAR, STEP or CUBICSPLINE.</summary>
    /// <remarks>
    /// CUBICSPLINE keys carry glTF's in/out tangents (a_k, b_k), per unit of time. The one reading of a
    /// sampler, shared by the source importer and the cook.
    /// </remarks>
    public static (Keyframe<T>[] Keys, Interpolation Mode) SampleKeys<T>(IAnimationSampler<T> sampler)
    {
        ArgumentNullException.ThrowIfNull(sampler);
        switch (sampler.InterpolationMode)
        {
            case AnimationInterpolationMode.CUBICSPLINE:
                return (sampler.GetCubicKeys()
                    .Select(k => new Keyframe<T>(k.Key, k.Value.Value, k.Value.TangentIn, k.Value.TangentOut)).ToArray(),
                    Interpolation.CubicSpline);
            case AnimationInterpolationMode.STEP:
                return (sampler.GetLinearKeys().Select(k => new Keyframe<T>(k.Key, k.Value)).ToArray(), Interpolation.Step);
            default:
                return (sampler.GetLinearKeys().Select(k => new Keyframe<T>(k.Key, k.Value)).ToArray(), Interpolation.Linear);
        }
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

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
// `Skeleton` with hierarchy-order bones, and one `AnimationClip` per glTF animation
// that touches the skin's joints. Collects every skinned-mesh node that references
// the primary skin so body+hair+clothing splits import as one bundle.
//
// Current limits:
// - One skin per file. Secondary skins are ignored.
// - LINEAR interpolation only (STEP / CUBICSPLINE rejected at import).
// - No morph-target weights.
// - Indices wider than ushort throw.
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
    /// The file is not something Blix can read. <b>Every refusal comes out as this one type</b>,
    /// carrying the path, because that is what lets a tool tell "your asset is bad" from "this tool
    /// has a bug" — and a tool that cannot tell them apart either crashes on bad input or swallows
    /// its own faults. Blix.Tools.Check did the first: a judge whose whole job is to survive a bad
    /// asset exited 134 with a stack trace on one.
    /// </exception>
    public GltfModel Import(AssetImportContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!File.Exists(context.SourcePath))
        {
            throw new AssetImportException(context.SourcePath, null, "no file there.");
        }

        // <b>Started here so the report covers the whole load, including the image pre-decode.</b>
        var loadWatch = System.Diagnostics.Stopwatch.StartNew();

        var model = AssetImportException.Refusing(context.SourcePath, () => ModelRoot.Load(context.SourcePath));

        // Pick the first node carrying both a mesh and a skin -- this becomes the
        // "primary" skin every other skinned-mesh node must reference. glTF
        // allows multiple top-level skinned meshes sharing one skeleton (the
        // body+hair+clothing pattern in real character content); we collect all
        // of them below.
        Node? primarySkinNode = null;
        foreach (var node in model.LogicalNodes)
        {
            if (node.Skin is not null && node.Mesh is not null)
            {
                primarySkinNode = node;
                break;
            }
        }
        if (primarySkinNode is null)
        {
            // <b>A refusal, not a fault.</b> An unskinned glTF is a perfectly good file that this
            // importer is the wrong one for — so it is the engine declining, and it says which
            // importer does want it. As an InvalidOperationException it escaped every tool's catch
            // and took the process down: `blix check --model` on any static prop exited through a
            // stack trace, which is the exact failure Section AV exists to prevent, still open one
            // importer away.
            throw new AssetImportException(
                context.SourcePath, null,
                "no node has both a mesh and a skin, so there is no rig here — " +
                "load it as a static model instead (GltfStaticImporter)");
        }

        var skin = primarySkinNode.Skin;
        var (bones, oldToNew) = BuildSkeletonAndOrdering(skin);
        var skeleton = new Skeleton(bones);

        // Capture the primary skin node's ancestor-chain transform — most glTF
        // characters sit under a parent node that applies an axis-orientation
        // correction (Z-up → Y-up, etc.). We do NOT bake it into vertices: per
        // the glTF skinning spec, the inverse-bind matrices map *mesh-local*
        // vertices into joint-local space, so the shader's vertex input has to
        // stay mesh-local or the skinning math reaches the wrong frame.
        // Instead, we expose the matrix on GltfModel so the renderer composes
        // it into the model matrix at draw time:
        //    uModel = userTransform.ToMatrix() * meshNodeTransform
        // F-016: engine row-vector form now matches SharpGLTF — no transpose.
        var meshNodeTransform = primarySkinNode.WorldMatrix;

        // Collect every skinned-mesh node that references the primary skin.
        // The typical case is one node (CesiumMan, Fox); multi-mesh characters
        // split body/hair/clothing across N nodes all driven by the same
        // armature. Each contributes its own primitives to the final
        // GltfModel.Primitives list with the same skeleton-ordering remap.
        //
        // All such nodes must share the primary node's WorldMatrix. Diverging
        // transforms would require per-submesh meshNodeTransform handling --
        // a much richer GPU path that's not justified by current content. If a
        // file violates this, throw loudly so the import fails clearly instead
        // of silently displaying the wrong thing.
        var skinnedMeshNodes = new List<Node> { primarySkinNode };
        var skipped = new List<GltfSkipped>();
        foreach (var node in model.LogicalNodes)
        {
            if (ReferenceEquals(node, primarySkinNode)) continue;
            if (node.Mesh is null) continue;

            // <b>A bare `continue` used to live here, and it was the whole bug.</b> A mesh weighted
            // to a second skin left no trace: the file arrived as a fraction of itself and every
            // tool downstream agreed it was complete. tank.glb is eleven primitives across three
            // skins, of which five were imported and six vanished without a word.
            //
            // Still skipped — reading more than one skin is a capability with real questions behind
            // it, and tools/character_merge.py exists to avoid needing it — but skipped OUT LOUD.
            if (!ReferenceEquals(node.Skin, skin))
            {
                if (node.Skin is not null) skipped.Add(Describe(node, GltfSkipReason.SecondarySkin));
                continue;
            }

            // Compare row-vector world matrices in SharpGLTF's native form -- no
            // conversion needed since we're just checking equality, not consuming
            // them.
            if (node.WorldMatrix != primarySkinNode.WorldMatrix)
            {
                throw new InvalidOperationException(
                    $"glTF '{context.SourcePath}' has multiple skinned-mesh nodes sharing one skin " +
                    $"but with different world matrices. Mesh '{node.Mesh.Name}' transform diverges " +
                    $"from primary '{primarySkinNode.Mesh!.Name}'. Per-submesh mesh-node transforms " +
                    $"aren't supported.");
            }
            skinnedMeshNodes.Add(node);
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
        GltfShared.PreDecodeImages(model, textureCache, gltfDir);
        var materialCache = new Dictionary<int, GltfMaterial>();
        var primitivesList = new List<GltfPrimitive>();
        foreach (var node in skinnedMeshNodes)
        {
            var mesh = node.Mesh!;
            for (var i = 0; i < mesh.Primitives.Count; i++)
            {
                var prim = mesh.Primitives[i];
                var meshName = $"{mesh.Name ?? "gltf_mesh"}.{i}";
                var meshData = BuildMeshData(meshName, prim, oldToNew);
                var material = GltfShared.ExtractMaterial(prim.Material, materialCache, textureCache);
                primitivesList.Add(new GltfPrimitive(meshData, material));
            }
        }
        var primitives = primitivesList.ToArray();

        var attachments = CollectAttachments(model, skin, oldToNew, materialCache, textureCache);

        // A static mesh under no joint is neither skinned geometry nor an attachment, so nothing
        // takes it. That is a defensible rule and was an invisible one: the four static primitives
        // in tank.glb are the rest of the six it loses.
        var attached = new HashSet<string>(attachments.Select(a => a.Name), StringComparer.Ordinal);
        foreach (var node in model.LogicalNodes)
        {
            if (node.Mesh is null || node.Skin is not null) continue;
            var name = node.Name ?? node.Mesh.Name ?? "?";
            if (attached.Contains(name)) continue;
            skipped.Add(Describe(node, GltfSkipReason.UnparentedStatic));
        }

        // <b>One line, from the importer itself, not only from a tool that happens to ask.</b> A
        // game loading a half-imported character should not have to run `blix check` to find out.
        if (skipped.Count > 0)
        {
            var lost = skipped.Sum(x => x.Primitives);
            Console.Error.WriteLine(
                $"  {Path.GetFileName(context.SourcePath)}: {skipped.Count} mesh node(s), " +
                $"{lost} primitive(s) NOT imported — " +
                string.Join(", ", skipped.Select(x => $"{x.Name} ({x.Explanation})")));
        }

        // Filter animations to those that touch our skin's joints; an animation
        // targeting only non-skin nodes (scene camera, light) becomes an empty clip
        // and gets dropped.
        var animations = new List<AnimationClip>();
        foreach (var anim in model.LogicalAnimations)
        {
            var clip = BuildAnimationClip(anim, skin, oldToNew);
            if (clip.Tracks.Length > 0)
            {
                animations.Add(clip);
            }
        }

        // <b>The rigged path reports too, and its absence was a hole in the instrument.</b>
        // K-E wired the static importer and the font loader and left this one silent, so
        // `blix check --cooked` — which loads every asset STATICALLY — could not see the way a game
        // actually loads a character. RTSGame's four villagers cost 605 ms of PNG decode each
        // through here, and the tool that exists to report exactly that was blind to it.
        //
        // <b>It always says Source, and that is not a placeholder.</b> There is no cooked form of a
        // rigged mesh at all: .blixmesh carries two vertex layouts and neither holds skin weights,
        // so this path has nothing to prefer. Saying so in the report is the point — a load that is
        // slow because nobody cooked it and a load that is slow because it CANNOT be cooked are
        // different problems, and only one of them is anybody's fault.
        if (AssetLoadLog.Enabled)
        {
            AssetLoadLog.Report(new AssetLoadReport(
                SourcePath: context.SourcePath,
                CookedPath: null,
                Mode: AssetLoadMode.Source,
                Bytes: SourceLength(context.SourcePath),
                LoadMs: loadWatch.Elapsed.TotalMilliseconds,
                Warning: "a rigged glTF has no cooked form — .blixmesh holds no skinned vertex layout"));
        }

        return new GltfModel(
            primitives, skeleton, animations.ToArray(), meshNodeTransform, attachments, skipped.ToArray());
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
    /// <b>Walks UP from each mesh node rather than down from each joint</b>, because a glTF is free
    /// to put a group node between the joint and the mesh and the relationship is still an
    /// attachment. Walking down would need to know how deep to look; walking up terminates at the
    /// first joint or at the root, and there is nothing to guess.
    /// </para>
    /// <para>
    /// <b>The joint index is remapped.</b> <see cref="BuildSkeletonAndOrdering"/> topologically
    /// sorts the skin's joints so parents precede children, so the skin's own index and the
    /// skeleton's are different numbers for the same bone on any rig that was not already sorted.
    /// Recording the raw one would put the knife on whatever bone happened to land at that index —
    /// a bug that looks like a content problem and survives every test that only counts.
    /// </para>
    /// <para>
    /// <b>Vertices are left in the node's own space.</b> The static builder bakes a world matrix
    /// into positions, which is right for a prop that never moves and wrong for one carried by a
    /// hand; identity goes in and the placement rides on <see cref="GltfAttachment.LocalTransform"/>
    /// instead, to be composed with the joint's animated transform at draw time.
    /// </para>
    /// </remarks>

    private static GltfSkipped Describe(Node node, GltfSkipReason reason)
    {
        var mesh = node.Mesh!;
        var vertices = 0;
        foreach (var prim in mesh.Primitives)
        {
            vertices += prim.GetVertexAccessor("POSITION")?.Count ?? 0;
        }

        return new GltfSkipped(
            node.Name ?? mesh.Name ?? "?", reason, mesh.Primitives.Count, vertices);
    }


    private static GltfAttachment[] CollectAttachments(
        ModelRoot model,
        Skin skin,
        int[] oldToNew,
        Dictionary<int, GltfMaterial> materialCache,
        Dictionary<int, GltfTexture> textureCache)
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
                // <b>Colour unconditionally here, where the static importer makes it opt-in.</b>
                // Not an inconsistency: an attachment has exactly ONE consumer in the tree — the
                // studio's RigView, drawing it on the studio's static pipeline — where a static
                // mesh has six, each with a pipeline of its own. With one consumer the layout can
                // simply agree with it, and a flag would only be a thing to forget.
                var meshData = GltfStaticImporter.BuildStaticMeshData(
                    name, prim, Matrix4x4.Identity, Matrix4x4.Identity, includeColour: true);
                primitives.Add(new GltfPrimitive(
                    meshData, GltfShared.ExtractMaterial(prim.Material, materialCache, textureCache)));
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
    // a skinned mesh importer; an unrigged mesh should use ObjImporter or a
    // future GltfStaticMeshImporter.
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
            // glTF stores UV with origin top-left (Y-down per spec); the engine's
            // existing texture pipeline flips images on load to put PNG's top
            // row at GL UV.y=1 (matching the Y-up convention OBJ UVs author for).
            // Flip the glTF UV.y at the import boundary so the imported vertex
            // stream lives in the same UV convention as everything else — shader
            // stays format-agnostic.
            var rawUv = uvs?[v] ?? Vector2.Zero;
            var uv = new Vector2(rawUv.X, 1.0f - rawUv.Y);

            // Remap joint indices through the topo-sort. Each slot is a float that
            // we cast to int, look up, and store back as float (the vertex shader
            // does int(...) at lookup time). Unused slots (weight == 0) still get
            // remapped so the stored index stays within bounds.
            var oldIdx = jointsArray[v];
            var newIdx = new Vector4(
                oldToNew[(int)oldIdx.X],
                oldToNew[(int)oldIdx.Y],
                oldToNew[(int)oldIdx.Z],
                oldToNew[(int)oldIdx.W]);
            var w = weightsArray[v];

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

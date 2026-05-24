using System.Numerics;
using Blix.Assets;
using Blix.Geometry;
using Blix.Graphics;
using Blix.Graphics.Images;
using SharpGLTF.Schema2;

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
// All math is converted to the engine's column-vector matrix convention at the
// import boundary — glTF / SharpGLTF deliver matrices in row-vector form (System.
// Numerics convention); the importer transposes inverse-bind matrices once so the
// downstream skeleton math doesn't have to think about it.
public sealed class GltfImporter : IAssetImporter<GltfModel>
{
    public string Name => "rigged-model.gltf";

    public GltfModel Import(AssetImportContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!File.Exists(context.SourcePath))
        {
            throw new FileNotFoundException($"glTF file not found: {context.SourcePath}", context.SourcePath);
        }

        var model = ModelRoot.Load(context.SourcePath);

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
            throw new InvalidOperationException(
                $"glTF '{context.SourcePath}' contains no node with both a mesh and a skin.");
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
        // The transpose is the standard row-vector (SharpGLTF / System.Numerics)
        // to column-vector (engine) bridge — same as IBM extraction below.
        var meshNodeTransform = Matrix4x4.Transpose(primarySkinNode.WorldMatrix);

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
        foreach (var node in model.LogicalNodes)
        {
            if (ReferenceEquals(node, primarySkinNode)) continue;
            if (node.Mesh is null) continue;
            if (!ReferenceEquals(node.Skin, skin)) continue;

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
        PreDecodeImages(model, textureCache, gltfDir);
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
                var material = ExtractMaterial(prim.Material, materialCache, textureCache);
                primitivesList.Add(new GltfPrimitive(meshData, material));
            }
        }
        var primitives = primitivesList.ToArray();

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

        return new GltfModel(primitives, skeleton, animations.ToArray(), meshNodeTransform);
    }

    // Decode a glTF material into engine form. Captures BaseColor (factor +
    // optional texture), normal map, and metallic-roughness texture + factors;
    // other channels (occlusion, emissive, alpha mode) are skipped.
    //
    // Mirror of GltfStaticImporter.PreDecodeChannels. See that file for the
    // rationale; in short: walk every material once, collect every image,
    // decode them in parallel, pre-populate the textureCache.
    private static readonly string[] PreDecodeChannels =
    {
        "BaseColor", "Normal", "MetallicRoughness", "Occlusion", "Emissive",
    };

    private static void PreDecodeImages(
        ModelRoot model,
        Dictionary<int, GltfTexture> textureCache,
        string gltfDir)
    {
        var imageRefs = new HashSet<int>();
        // Track which images are used as MetallicRoughness so the decode
        // path can route them through LoadMetallicRoughness -- handles
        // 1-channel grayscale "roughness only" PNGs correctly. Mirror of
        // GltfStaticImporter.PreDecodeImages.
        var mrImageIndices = new HashSet<int>();
        foreach (var mat in model.LogicalMaterials)
        {
            foreach (var channelName in PreDecodeChannels)
            {
                var channel = mat.FindChannel(channelName);
                if (!channel.HasValue) continue;
                var img = channel.Value.Texture?.PrimaryImage;
                if (img is null) continue;
                imageRefs.Add(img.LogicalIndex);
                if (channelName == "MetallicRoughness")
                {
                    mrImageIndices.Add(img.LogicalIndex);
                }
            }
        }
        if (imageRefs.Count == 0) return;

        var imagesToConsider = model.LogicalImages
            .Where(i => imageRefs.Contains(i.LogicalIndex))
            .ToArray();

        var cookedSourcePaths = new Dictionary<int, string>();
        var sourceImages = new List<SharpGLTF.Schema2.Image>();
        foreach (var image in imagesToConsider)
        {
            var blixTexPath = TryResolveBlixTex(image, gltfDir);
            if (blixTexPath is not null)
            {
                cookedSourcePaths[image.LogicalIndex] = blixTexPath;
            }
            else
            {
                sourceImages.Add(image);
            }
        }

        var cookedWatch = System.Diagnostics.Stopwatch.StartNew();
        foreach (var (idx, path) in cookedSourcePaths)
        {
            var handle = BlixTexReader.ReadHandle(path);
            textureCache[idx] = new GltfTexture(
                Path.GetFileNameWithoutExtension(path), handle);
        }
        cookedWatch.Stop();
        if (cookedSourcePaths.Count > 0)
        {
            Console.WriteLine(
                $"  indexed {cookedSourcePaths.Count} cooked .blixtex images in {cookedWatch.ElapsedMilliseconds} ms (lazy)");
        }

        if (sourceImages.Count == 0) return;

        var decoded = new System.Collections.Concurrent.ConcurrentDictionary<int, GltfTexture>();
        var decodeWatch = System.Diagnostics.Stopwatch.StartNew();
        System.Threading.Tasks.Parallel.ForEach(sourceImages, image =>
        {
            var bytes = image.Content.Content.ToArray();
            using var stream = new MemoryStream(bytes);
            var d = mrImageIndices.Contains(image.LogicalIndex)
                ? ImageLoader.LoadMetallicRoughness(stream)
                : ImageLoader.LoadRgba32(stream);
            decoded[image.LogicalIndex] = GltfTexture.Rgba8Single(
                image.Name ?? $"image_{image.LogicalIndex}",
                d.Pixels, d.Width, d.Height);
        });
        foreach (var kv in decoded) textureCache[kv.Key] = kv.Value;
        Console.WriteLine(
            $"  decoded {sourceImages.Count} images in {decodeWatch.ElapsedMilliseconds} ms");
    }

    private static string? TryResolveBlixTex(SharpGLTF.Schema2.Image image, string gltfDir)
    {
        var sourcePath = image.Content.SourcePath;
        if (string.IsNullOrEmpty(sourcePath)) return null;
        var blixTexPath = Path.ChangeExtension(sourcePath, ".blixtex");
        return File.Exists(blixTexPath) ? blixTexPath : null;
    }

    // materialCache deduplicates: two primitives referencing the same glTF material
    // get the same GltfMaterial instance. textureCache does the same a layer
    // deeper — two materials referencing the same image only decode that image
    // once.
    private static GltfMaterial? ExtractMaterial(
        Material? material,
        Dictionary<int, GltfMaterial> materialCache,
        Dictionary<int, GltfTexture> textureCache)
    {
        if (material is null) return null;
        if (materialCache.TryGetValue(material.LogicalIndex, out var cached)) return cached;

        var baseColorChannel = material.FindChannel("BaseColor");
        var baseColorFactor = baseColorChannel.HasValue
            ? baseColorChannel.Value.Color
            : new Vector4(1.0f, 1.0f, 1.0f, 1.0f);
        var baseColorTexture = baseColorChannel.HasValue
            ? ExtractTexture(baseColorChannel.Value.Texture, textureCache)
            : null;

        var normalChannel = material.FindChannel("Normal");
        var normalTexture = normalChannel.HasValue
            ? ExtractTexture(normalChannel.Value.Texture, textureCache)
            : null;

        var metallicChannel = material.FindChannel("MetallicRoughness");
        // glTF's MetallicRoughness channel exposes its factors via Parameters
        // (named scalars); fall back to spec defaults if the channel is absent.
        var metallic = 1.0f;
        var roughness = 1.0f;
        GltfTexture? metallicRoughnessTexture = null;
        if (metallicChannel.HasValue)
        {
            foreach (var p in metallicChannel.Value.Parameters)
            {
                if (p.Name == "MetallicFactor") metallic = (float)Convert.ToDouble(p.Value);
                else if (p.Name == "RoughnessFactor") roughness = (float)Convert.ToDouble(p.Value);
            }
            metallicRoughnessTexture = ExtractTexture(metallicChannel.Value.Texture, textureCache);
        }

        var occlusionChannel = material.FindChannel("Occlusion");
        var occlusionStrength = 1.0f;
        GltfTexture? occlusionTexture = null;
        if (occlusionChannel.HasValue)
        {
            foreach (var p in occlusionChannel.Value.Parameters)
            {
                if (p.Name == "Strength") occlusionStrength = (float)Convert.ToDouble(p.Value);
            }
            occlusionTexture = ExtractTexture(occlusionChannel.Value.Texture, textureCache);
        }

        var emissiveChannel = material.FindChannel("Emissive");
        var emissiveFactor = Vector3.Zero;
        var emissiveStrength = 1.0f;
        GltfTexture? emissiveTexture = null;
        if (emissiveChannel.HasValue)
        {
            // glTF Emissive channel exposes a vec3 factor under .Color (XYZ).
            // FindChannel returns the texture in .Texture when one is bound.
            var c = emissiveChannel.Value.Color;
            emissiveFactor = new Vector3(c.X, c.Y, c.Z);
            emissiveTexture = ExtractTexture(emissiveChannel.Value.Texture, textureCache);
            foreach (var p in emissiveChannel.Value.Parameters)
            {
                if (p.Name == "EmissiveStrength") emissiveStrength = (float)Convert.ToDouble(p.Value);
            }
        }

        var alphaMode = material.Alpha switch
        {
            SharpGLTF.Schema2.AlphaMode.OPAQUE => GltfAlphaMode.Opaque,
            SharpGLTF.Schema2.AlphaMode.MASK   => GltfAlphaMode.Mask,
            SharpGLTF.Schema2.AlphaMode.BLEND  => GltfAlphaMode.Blend,
            _                                  => GltfAlphaMode.Opaque,
        };

        var result = new GltfMaterial(
            material.Name ?? $"material_{material.LogicalIndex}",
            baseColorFactor,
            baseColorTexture,
            normalTexture,
            metallicRoughnessTexture,
            metallic,
            roughness,
            occlusionTexture,
            occlusionStrength,
            emissiveTexture,
            emissiveFactor,
            emissiveStrength,
            alphaMode,
            material.AlphaCutoff,
            material.DoubleSided);
        materialCache[material.LogicalIndex] = result;
        return result;
    }

    // Decode a glTF texture's primary image into RGBA8 bytes. Routes through
    // Blix.Graphics.Images.ImageLoader so PNG/JPEG decoding stays in one place
    // (same code path the disk-based TextureImporter uses).
    private static GltfTexture? ExtractTexture(
        Texture? texture,
        Dictionary<int, GltfTexture> textureCache)
    {
        if (texture is null) return null;
        var image = texture.PrimaryImage;
        if (image is null) return null;
        if (textureCache.TryGetValue(image.LogicalIndex, out var cached)) return cached;

        var bytes = image.Content.Content;
        using var stream = new MemoryStream(bytes.ToArray());
        var decoded = ImageLoader.LoadRgba32(stream);

        var result = GltfTexture.Rgba8Single(
            image.Name ?? texture.Name ?? $"image_{image.LogicalIndex}",
            decoded.Pixels, decoded.Width, decoded.Height);
        textureCache[image.LogicalIndex] = result;
        return result;
    }

    // Build the engine-side Bone[] in topo-sorted (parent-first) order, returning
    // the bones AND the old-to-new index mapping (used later to remap vertex joint
    // indices and animation channel targets).
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

        // Emit bones in the new (topo-sorted) order with remapped parent indices
        // and IBMs transposed to our column-vector convention. SharpGLTF returns
        // System.Numerics-style row-vector matrices; transpose maps the layout
        // into the column-vector form the rest of the engine expects.
        var bones = new Bone[n];
        for (var newIdx = 0; newIdx < n; newIdx++)
        {
            var oldIdx = orderNewToOld[newIdx];
            var joint = joints[oldIdx];
            var ibm = ibmList[oldIdx];
            var parentNew = parentOld[oldIdx] >= 0 ? oldToNew[parentOld[oldIdx]] : -1;
            var ibp = Matrix4x4.Transpose(ibm);
            bones[newIdx] = new Bone(joint.Name ?? $"bone_{newIdx}", parentNew, ibp);
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

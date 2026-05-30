using System.Numerics;
using Blix.Assets;
using Blix.Geometry;
using Blix.Graphics;
using Blix.Graphics.Images;
using SharpGLTF.Schema2;

namespace Blix;

// Static-mesh sibling of GltfImporter. Loads any .glb/.gltf containing untransformed
// or transformed mesh nodes (no skinning required) and emits a GltfModel whose
// Primitives use the VertexPosition3NormalTexture layout. Skeleton/Animations come
// back empty so existing renderer code can branch on Skeleton.Bones.Length == 0.
//
// Each mesh node's world-space transform is baked into the vertex positions at
// import time so the renderer can draw every primitive with a shared identity
// model matrix. That's the right call for static scene assets like Sponza where
// instancing isn't a goal and the alternative (per-primitive uModel) would force
// the demo to track a transform alongside each Mesh handle.
//
// Materials reuse the GltfMaterial record produced by the rigged importer; the
// extraction logic is duplicated rather than shared because hoisting it would
// pull GltfImporter's private internals into a third file. ~30 lines of dupe is
// the lighter cost.
public sealed class GltfStaticImporter : IAssetImporter<GltfModel>
{
    public string Name => "static-mesh.gltf";

    public GltfModel Import(AssetImportContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!File.Exists(context.SourcePath))
        {
            throw new FileNotFoundException($"glTF file not found: {context.SourcePath}", context.SourcePath);
        }

        // When a cooked .blixmesh sibling exists, the runtime only needs the
        // material descriptors + image URIs from the .gltf -- not the .bin
        // buffer data that SharpGLTF's default ModelRoot.Load eagerly reads
        // and validates against (for ~95% of a big scene's parse time).
        // ReadContext.Create + ValidationMode.Skip + a callback that returns
        // empty bytes for non-.gltf resources skips the buffer reads
        // entirely: 4500ms -> 11ms on Sponza main. Per-accessor reads would
        // fail under this model, but BlixMeshReader.Read replaces them.
        var blixmeshPath = Path.ChangeExtension(context.SourcePath, ".blixmesh");
        var useCookedMesh = File.Exists(blixmeshPath);
        var gltfFullPath = Path.GetFullPath(context.SourcePath);
        var gltfDirInfo = Path.GetDirectoryName(gltfFullPath) ?? string.Empty;
        var gltfFileName = Path.GetFileName(gltfFullPath);
        ModelRoot model;
        if (useCookedMesh)
        {
            var settings = new ReadSettings { Validation = SharpGLTF.Validation.ValidationMode.Skip };
            ArraySegment<byte> Reader(string assetName)
            {
                // SharpGLTF asks for the .gltf JSON first; supply it. Then
                // asks for any external .bin / image files; return empty so
                // the parser stops short of reading them. Material + image
                // metadata stays intact since it all lives in the JSON.
                var full = Path.Combine(gltfDirInfo, assetName);
                if (Path.GetExtension(full).Equals(".gltf", StringComparison.OrdinalIgnoreCase))
                {
                    return new ArraySegment<byte>(File.ReadAllBytes(full));
                }
                return ArraySegment<byte>.Empty;
            }
            model = SharpGLTF.Schema2.ReadContext.Create(Reader)
                .WithSettingsFrom(settings)
                .ReadSchema2(gltfFileName);
        }
        else
        {
            model = ModelRoot.Load(context.SourcePath);
        }

        var textureCache = new Dictionary<int, GltfTexture>();
        var materialCache = new Dictionary<int, GltfMaterial>();
        var primitives = new List<GltfPrimitive>();

        // Parallel texture decode. PNG/JPEG decode via StbImageSharp is the
        // dominant cost for heavy assets (Modern Sponza spends most of its
        // multi-minute load here), and StbImageSharp doesn't share state
        // across calls, so we can decode every unique source image in
        // parallel and pre-populate the textureCache. Cooked .blixtex
        // siblings (see tools/Blix.Tools.Cook + BlixTex format) skip the
        // decode entirely; PreDecodeImages prefers them when present.
        var gltfDir = Path.GetDirectoryName(Path.GetFullPath(context.SourcePath)) ?? string.Empty;
        PreDecodeImages(model, textureCache, gltfDir);

        // Cooked-mesh fast path. The blixmesh sibling was detected above
        // (used to short-circuit ModelRoot.Load's buffer reads); now read
        // its cooked vertex + index bytes instead of walking glTF accessors.
        // Material resolution still uses the lite (JSON-only) model.
        if (useCookedMesh)
        {
            var cooked = BlixMeshReader.Read(blixmeshPath);
            foreach (var p in cooked.Primitives)
            {
                // Heal degenerate UVs in cooked files too (in-place mutation
                // is fine, we own the buffer after BlixMeshReader returns it).
                // Lets us fix asset-level UV corruption without re-running the
                // cook step.
                // UV offset comes from the cooked layout (32-byte → 24,
                // 48-byte tangent → 40), not hardcoded.
                var uvAttr = cooked.Layout.Attributes.First(a => a.Location == 3);
                SanitizePackedUVs(p.VertexBytes, p.VertexCount, stride: cooked.Layout.Stride, uvOffset: uvAttr.Offset, p.Name);
                // LOD0 is the default index buffer; the full chain rides along
                // in Lods for the demo's distance-based selection.
                var lod0 = p.Lods[0];
                var lods = new MeshLod[p.Lods.Count];
                for (var l = 0; l < p.Lods.Count; l++)
                    lods[l] = new MeshLod(p.Lods[l].Indices16, p.Lods[l].Indices32, p.Lods[l].Error);
                var meshData = new MeshData(
                    p.Name,
                    p.VertexBytes,
                    lod0.Indices16 ?? Array.Empty<ushort>(),
                    cooked.Layout,
                    p.Bounds,
                    Indices32: lod0.Indices32,
                    Lods: lods);
                var gltfMat = p.MaterialIndex >= 0 && p.MaterialIndex < model.LogicalMaterials.Count
                    ? model.LogicalMaterials[p.MaterialIndex]
                    : null;
                var material = ExtractMaterial(gltfMat, materialCache, textureCache);
                primitives.Add(new GltfPrimitive(meshData, material));
            }
        }
        else
        {
            foreach (var node in model.LogicalNodes)
            {
                if (node.Mesh is null) continue;
                // F-016: engine is now row-vector form throughout — same as SharpGLTF.
                // No transpose needed; just use the world matrix directly.
                var world = node.WorldMatrix;
                var normalMatrix = ComputeNormalMatrix(world);

                for (var i = 0; i < node.Mesh.Primitives.Count; i++)
                {
                    var prim = node.Mesh.Primitives[i];
                    var meshName = $"{node.Mesh.Name ?? node.Name ?? "gltf_mesh"}.{i}";
                    var meshData = BuildStaticMeshData(meshName, prim, world, normalMatrix, context.FlipTextureV, context.IncludeTangents);
                    var material = ExtractMaterial(prim.Material, materialCache, textureCache);
                    primitives.Add(new GltfPrimitive(meshData, material));
                }
            }
        }

        if (primitives.Count == 0)
        {
            throw new InvalidOperationException(
                $"glTF '{context.SourcePath}' contains no mesh nodes.");
        }

        // Empty skeleton + zero animations. Identity meshNodeTransform — every
        // vertex has already had its node transform baked in.
        return new GltfModel(
            primitives.ToArray(),
            new Skeleton(Array.Empty<Bone>()),
            Array.Empty<AnimationClip>(),
            Matrix4x4.Identity);
    }

    // Cook a .gltf/.glb to its .blixmesh sibling. CPU-only -- no GraphicsDevice
    // required; safe to invoke from the offline cook tool. Walks the same
    // node/primitive structure the runtime importer does, packs vertices via
    // BuildStaticMeshData, and serialises each primitive's
    // (name, materialIndex, bounds, vertexBytes, indices) to disk.
    // Target triangle ratios for the LOD chain (relative to full detail). The
    // chain stops early if a level doesn't reduce or falls below MinLodIndices.
    private static readonly float[] LodRatios = { 0.5f, 0.25f, 0.125f };
    private const int MinLodIndices = 96; // 32 triangles — below this, no point

    // Reduced index list + the world-space geometric error it introduced
    // (max deviation from the original surface, in mesh units). Error drives
    // screen-space-error LOD selection: project it to pixels at the view
    // distance and switch when it's below a pixel threshold.
    public readonly record struct SimplifyResult(uint[] Indices, float WorldError);

    // Simplify callback: (positions xyz tight float[3*vtx], indices, vertexCount,
    // targetRatio) -> reduced index list + its world error, sharing the same
    // vertices. The cook tool supplies a meshoptimizer-backed implementation;
    // when null, the file is written LOD0-only (Blix has no simplifier of its own).
    public delegate SimplifyResult SimplifyFn(float[] positions, uint[] indices, int vertexCount, float targetRatio);

    public static int CookToBlixMesh(
        string gltfPath, string outPath, bool flipTextureV = false, bool includeTangents = false,
        SimplifyFn? simplify = null, int splitTriBudget = 0, bool splitFoliage = true)
    {
        ArgumentNullException.ThrowIfNull(gltfPath);
        ArgumentNullException.ThrowIfNull(outPath);

        var layout = includeTangents
            ? VertexPosition3NormalTangentTexture.Layout
            : VertexPosition3NormalTexture.Layout;
        var model = ModelRoot.Load(gltfPath);
        var primitives = new List<BlixMeshPrimitive>();
        foreach (var node in model.LogicalNodes)
        {
            if (node.Mesh is null) continue;
            // F-016: engine is now row-vector form; matches SharpGLTF.
            var world = node.WorldMatrix;
            var normalMatrix = ComputeNormalMatrix(world);
            for (var i = 0; i < node.Mesh.Primitives.Count; i++)
            {
                var prim = node.Mesh.Primitives[i];
                var meshName = $"{node.Mesh.Name ?? node.Name ?? "gltf_mesh"}.{i}";
                var meshData = BuildStaticMeshData(meshName, prim, world, normalMatrix, flipTextureV, includeTangents);
                var materialIndex = prim.Material?.LogicalIndex ?? BlixMesh.NoMaterial;

                // Spatial split of oversized primitives so per-prim distance LOD
                // gets fine-grained — a huge floor/wall becomes many chunks, the
                // far ones coarsen while the near stay dense. Seam verts are
                // duplicated per chunk + LockBorder-locked (BuildLods) → crack-free
                // across LOD mismatch. Foliage (non-OPAQUE) optionally excluded so
                // the impostor track can own it instead.
                var isFoliage = prim.Material is { Alpha: not SharpGLTF.Schema2.AlphaMode.OPAQUE };
                var doSplit = splitTriBudget > 0 && (splitFoliage || !isFoliage);
                var chunks = doSplit
                    ? SplitPrimitive(meshData, layout.Stride, splitTriBudget)
                    : new List<MeshData> { meshData };

                foreach (var chunk in chunks)
                {
                    primitives.Add(new BlixMeshPrimitive(
                        Name: chunk.Name,
                        MaterialIndex: materialIndex,
                        Bounds: chunk.Bounds,
                        VertexCount: chunk.VertexCount,
                        VertexBytes: chunk.VertexBytes,
                        IndexFormat: chunk.IndexFormat,
                        Lods: BuildLods(chunk, layout.Stride, simplify)));
                }
            }
        }

        BlixMeshWriter.Write(outPath, new BlixMeshFile(layout, primitives));
        return primitives.Count;
    }

    // LOD0 (full) + decimated levels via the injected simplifier. All levels
    // share the primitive's index format (decimated indices reference the same
    // vertex buffer, so a u16 primitive stays u16).
    private static IReadOnlyList<BlixMeshLod> BuildLods(MeshData meshData, int stride, SimplifyFn? simplify)
    {
        // LOD0 is the original surface: zero geometric error.
        var lods = new List<BlixMeshLod> { new(meshData.Indices, meshData.Indices32, Error: 0f) };
        if (simplify is null) return lods;

        var baseIndices = meshData.Indices32 ?? Array.ConvertAll(meshData.Indices, idx => (uint)idx);
        if (baseIndices.Length < MinLodIndices) return lods;

        var positions = new float[meshData.VertexCount * 3];
        for (var v = 0; v < meshData.VertexCount; v++)
        {
            var o = v * stride;
            positions[v * 3 + 0] = BitConverter.ToSingle(meshData.VertexBytes, o);
            positions[v * 3 + 1] = BitConverter.ToSingle(meshData.VertexBytes, o + 4);
            positions[v * 3 + 2] = BitConverter.ToSingle(meshData.VertexBytes, o + 8);
        }

        var prevCount = baseIndices.Length;
        foreach (var ratio in LodRatios)
        {
            var result = simplify(positions, baseIndices, meshData.VertexCount, ratio);
            var reduced = result.Indices;
            if (reduced.Length < MinLodIndices || reduced.Length >= prevCount) break;
            prevCount = reduced.Length;
            lods.Add(meshData.IndexFormat == IndexFormat.UInt32
                ? new BlixMeshLod(null, reduced, result.WorldError)
                : new BlixMeshLod(Array.ConvertAll(reduced, idx => (ushort)idx), null, result.WorldError));
        }
        return lods;
    }

    // Recursively split a primitive's triangles into spatial chunks under
    // triBudget, each a self-contained MeshData (own gathered + reindexed
    // vertices). Median split along the longest centroid axis. Seam vertices
    // are duplicated across chunks — with BuildLods' LockBorder this keeps chunk
    // boundaries watertight even when adjacent chunks pick different LOD levels.
    private static List<MeshData> SplitPrimitive(MeshData mesh, int stride, int triBudget)
    {
        var baseIdx = mesh.Indices32 ?? Array.ConvertAll(mesh.Indices, idx => (uint)idx);
        var triCount = baseIdx.Length / 3;
        if (triCount <= triBudget) return new List<MeshData> { mesh };

        var centroids = new Vector3[triCount];
        for (var t = 0; t < triCount; t++)
        {
            var a = VertexPosition(mesh.VertexBytes, baseIdx[t * 3], stride);
            var b = VertexPosition(mesh.VertexBytes, baseIdx[t * 3 + 1], stride);
            var c = VertexPosition(mesh.VertexBytes, baseIdx[t * 3 + 2], stride);
            centroids[t] = (a + b + c) / 3f;
        }

        var leaves = new List<int[]>();
        var allTris = new int[triCount];
        for (var t = 0; t < triCount; t++) allTris[t] = t;
        SplitTriangles(allTris, centroids, triBudget, leaves);

        var chunks = new List<MeshData>(leaves.Count);
        var chunkIdx = 0;
        foreach (var leaf in leaves)
            chunks.Add(BuildChunk($"{mesh.Name}#{chunkIdx++}", mesh, baseIdx, leaf, stride));
        return chunks;
    }

    private static void SplitTriangles(int[] tris, Vector3[] centroids, int budget, List<int[]> leaves)
    {
        if (tris.Length <= budget) { leaves.Add(tris); return; }
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        foreach (var t in tris) { min = Vector3.Min(min, centroids[t]); max = Vector3.Max(max, centroids[t]); }
        var ext = max - min;
        var axis = ext.X >= ext.Y && ext.X >= ext.Z ? 0 : (ext.Y >= ext.Z ? 1 : 2);
        Array.Sort(tris, (p, q) => Axis(centroids[p], axis).CompareTo(Axis(centroids[q], axis)));
        var mid = tris.Length / 2;
        // Degenerate (centroids coincide along the split axis) — emit whole.
        if (mid == 0 || mid == tris.Length) { leaves.Add(tris); return; }
        SplitTriangles(tris[..mid], centroids, budget, leaves);
        SplitTriangles(tris[mid..], centroids, budget, leaves);
    }

    private static float Axis(Vector3 v, int a) => a == 0 ? v.X : (a == 1 ? v.Y : v.Z);

    private static Vector3 VertexPosition(byte[] vbytes, uint vtx, int stride)
    {
        var o = (int)vtx * stride;
        return new Vector3(
            BitConverter.ToSingle(vbytes, o),
            BitConverter.ToSingle(vbytes, o + 4),
            BitConverter.ToSingle(vbytes, o + 8));
    }

    // Gather the vertices a chunk's triangles reference, reindex compactly, copy
    // their vertex bytes, recompute bounds. Downgrades to u16 indices when the
    // chunk's vertex count fits (split chunks are far smaller than the parent).
    private static MeshData BuildChunk(string name, MeshData mesh, uint[] baseIdx, int[] triIds, int stride)
    {
        var remap = new Dictionary<uint, uint>();
        var newVerts = new List<uint>();
        var newIndices = new uint[triIds.Length * 3];
        var w = 0;
        foreach (var t in triIds)
        {
            for (var k = 0; k < 3; k++)
            {
                var ov = baseIdx[t * 3 + k];
                if (!remap.TryGetValue(ov, out var nv))
                {
                    nv = (uint)newVerts.Count;
                    remap[ov] = nv;
                    newVerts.Add(ov);
                }
                newIndices[w++] = nv;
            }
        }

        var vbytes = new byte[newVerts.Count * stride];
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        for (var v = 0; v < newVerts.Count; v++)
        {
            Array.Copy(mesh.VertexBytes, (int)newVerts[v] * stride, vbytes, v * stride, stride);
            var p = VertexPosition(mesh.VertexBytes, newVerts[v], stride);
            min = Vector3.Min(min, p); max = Vector3.Max(max, p);
        }
        var bounds = new Bounds3(min, max);

        if (newVerts.Count <= ushort.MaxValue + 1)
        {
            var u16 = new ushort[newIndices.Length];
            for (var i = 0; i < newIndices.Length; i++) u16[i] = (ushort)newIndices[i];
            return new MeshData(name, vbytes, u16, mesh.Layout, bounds);
        }
        return new MeshData(name, vbytes, Array.Empty<ushort>(), mesh.Layout, bounds, Indices32: newIndices);
    }

    public static MeshData BuildStaticMeshData(string name, MeshPrimitive primitive, Matrix4x4 world, Matrix4x4 normalMatrix, bool flipTextureV = false, bool includeTangents = false)
    {
        var positions = primitive.GetVertexAccessor("POSITION")?.AsVector3Array()
            ?? throw new InvalidOperationException("glTF mesh primitive missing required POSITION accessor.");
        var normals = primitive.GetVertexAccessor("NORMAL")?.AsVector3Array();
        var uvs = primitive.GetVertexAccessor("TEXCOORD_0")?.AsVector2Array();
        // Authored tangents (glTF vec4: xyz dir + w handedness). MikkTSpace-
        // compatible per spec, so forwarding beats recomputing.
        var tangents = includeTangents ? primitive.GetVertexAccessor("TANGENT")?.AsVector4Array() : null;

        var vertexCount = positions.Count;

        var minB = new Vector3(float.PositiveInfinity);
        var maxB = new Vector3(float.NegativeInfinity);

        // V canonicalisation (V -> 1-V), opt-in per import via FlipTextureV.
        // Bottom-up (OpenGL-authored) sources sample vertically inverted on a
        // top-down (Vulkan / D3D) sampler; baking the flip here is the single
        // chokepoint the runtime importer and CookToBlixMesh share.
        Vector2 BuildUv(int v)
        {
            var uv = uvs is null ? Vector2.Zero : uvs[v];
            return new Vector2(uv.X, flipTextureV ? 1.0f - uv.Y : uv.Y);
        }
        Vector3 BuildNormal(int v) =>
            Vector3.Normalize(GraphicsMatrices.TransformDirection(normalMatrix, normals is null ? Vector3.UnitY : normals[v]));

        byte[] packed;
        VertexLayout layout;
        int uvOffset;
        if (includeTangents)
        {
            var verts = new VertexPosition3NormalTangentTexture[vertexCount];
            for (var v = 0; v < vertexCount; v++)
            {
                var pWorld = GraphicsMatrices.TransformPoint(world, positions[v]);
                var nWorld = BuildNormal(v);
                // Tangent direction transforms by the model's linear part (NOT
                // the inverse-transpose used for normals). Preserve the w sign.
                Vector3 tDir;
                float tSign;
                if (tangents is not null)
                {
                    var t = tangents[v];
                    tDir = Vector3.Normalize(GraphicsMatrices.TransformDirection(world, new Vector3(t.X, t.Y, t.Z)));
                    tSign = t.W < 0f ? -1f : 1f;
                }
                else
                {
                    // No authored tangent — pick any axis perpendicular to N.
                    var up = MathF.Abs(nWorld.Y) > 0.99f ? Vector3.UnitX : Vector3.UnitY;
                    tDir = Vector3.Normalize(Vector3.Cross(up, nWorld));
                    tSign = 1f;
                }
                var uv = BuildUv(v);
                verts[v] = new VertexPosition3NormalTangentTexture(
                    new GraphicsVector3(pWorld.X, pWorld.Y, pWorld.Z),
                    new GraphicsVector3(nWorld.X, nWorld.Y, nWorld.Z),
                    new GraphicsVector4(tDir.X, tDir.Y, tDir.Z, tSign),
                    new GraphicsVector2(uv.X, uv.Y));
                minB = Vector3.Min(minB, pWorld);
                maxB = Vector3.Max(maxB, pWorld);
            }
            packed = VertexPosition3NormalTangentTexture.Pack(verts);
            layout = VertexPosition3NormalTangentTexture.Layout;
            uvOffset = 10 * sizeof(float);
        }
        else
        {
            var verts = new VertexPosition3NormalTexture[vertexCount];
            for (var v = 0; v < vertexCount; v++)
            {
                var pWorld = GraphicsMatrices.TransformPoint(world, positions[v]);
                var nWorld = BuildNormal(v);
                var uv = BuildUv(v);
                verts[v] = new VertexPosition3NormalTexture(
                    new GraphicsVector3(pWorld.X, pWorld.Y, pWorld.Z),
                    new GraphicsVector3(nWorld.X, nWorld.Y, nWorld.Z),
                    new GraphicsVector2(uv.X, uv.Y));
                minB = Vector3.Min(minB, pWorld);
                maxB = Vector3.Max(maxB, pWorld);
            }
            packed = VertexPosition3NormalTexture.Pack(verts);
            layout = VertexPosition3NormalTexture.Layout;
            uvOffset = 6 * sizeof(float);
        }

        var indicesSrc = primitive.GetIndices();
        // Pick the narrowest width that fits. UInt16 covers virtually every
        // authored asset; UInt32 kicks in for large packs like Khronos
        // Sponza Modern's curtains (66k vertices in a single primitive).
        var needsUInt32 = vertexCount > ushort.MaxValue;
        ushort[] indices16;
        uint[]? indices32;
        if (needsUInt32)
        {
            indices16 = Array.Empty<ushort>();
            indices32 = new uint[indicesSrc.Count];
            for (var i = 0; i < indicesSrc.Count; i++)
            {
                indices32[i] = indicesSrc[i];
            }
        }
        else
        {
            indices32 = null;
            indices16 = new ushort[indicesSrc.Count];
            for (var i = 0; i < indicesSrc.Count; i++)
            {
                indices16[i] = (ushort)indicesSrc[i];
            }
        }

        var bounds = vertexCount == 0 ? Bounds3.Empty : new Bounds3(minB, maxB);
        SanitizePackedUVs(packed, vertexCount, layout.Stride, uvOffset, name);
        return new MeshData(
            name,
            packed,
            indices16,
            layout,
            bounds,
            Indices32: indices32);
    }

    // Some authored assets ship one or two vertices with extreme UV values
    // (we've seen -42470 in the Khronos Intel Sponza source). With wrap=
    // Repeat the GPU still tiles, but adjacent triangles' UV interpolation
    // drags across thousands of units, blowing up dFdx/dFdy so the sampler
    // picks the coarsest mip everywhere -> washed-out garbage. Fold any
    // out-of-range vertex back into [0,1) with `frac` so the LOD calc and
    // texture cache stay sane; tiled textures still tile correctly because
    // frac is the same value modulo 1.
    private const float MaxReasonableUV = 100.0f;

    private static int SanitizePackedUVs(byte[] vertexBytes, int vertexCount, int stride, int uvOffset, string ownerName)
    {
        var touched = 0;
        var span = vertexBytes.AsSpan();
        for (var v = 0; v < vertexCount; v++)
        {
            var slot = span.Slice(v * stride + uvOffset, 8);
            var u = System.Runtime.InteropServices.MemoryMarshal.Read<float>(slot);
            var vv = System.Runtime.InteropServices.MemoryMarshal.Read<float>(slot.Slice(4));
            var fix = false;
            if (MathF.Abs(u) > MaxReasonableUV) { u -= MathF.Floor(u); fix = true; }
            if (MathF.Abs(vv) > MaxReasonableUV) { vv -= MathF.Floor(vv); fix = true; }
            if (fix)
            {
                System.Runtime.InteropServices.MemoryMarshal.Write(slot, in u);
                System.Runtime.InteropServices.MemoryMarshal.Write(slot.Slice(4), in vv);
                touched++;
            }
        }
        if (touched > 0)
        {
            Console.WriteLine(
                $"[GltfStaticImporter] sanitized {touched} vertex UV(s) in '{ownerName}' " +
                $"(values exceeded |UV|>{MaxReasonableUV}; folded with frac to [0,1))");
        }
        return touched;
    }

    // PBR channels we sample per material. Match the set ExtractMaterial walks
    // below; if a new channel is added there, mirror it here so the pre-walk
    // catches its image references.
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
        // Track which images are used as MetallicRoughness so we can route
        // them through the channel-aware loader that handles 1-channel
        // grayscale "roughness only" PNGs (Modern Sponza ships those, and
        // the default LoadRgba32 expansion turns them into "matte metal"
        // walls when shader reads .b as metallic). First channel-binding
        // wins -- an image used as both BaseColor and MR somewhere
        // (unlikely but valid in glTF) gets BaseColor's loader.
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

        // Split: images that have a cooked .blixtex sibling go through the
        // fast no-decode reader; the rest run through PNG/JPEG decode in
        // parallel. The cooked sideload is so cheap relative to PNG decode
        // that even sequential reads stay well under decode time.
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
            // Lazy handle: parses just the 32-byte header + per-mip
            // (offset, length) table. The pixel bytes stay on disk until
            // the upload pump pulls them mip-by-mip at GL upload time.
            // This drops the per-pack import-time memory peak from
            // "all mip data for all textures" (multi-GB) to ~hundreds of
            // bytes per texture.
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

    // Returns the absolute path to a cooked .blixtex sibling for the image
    // if one exists, else null. glTF images carry either an embedded byte
    // blob (no source URI) or a file URI; we can only sideload .blixtex
    // for the URI case. SharpGLTF stashes the loaded URI in
    // MemoryImage.SourcePath -- absolute when an external URI was
    // resolved, null for embedded buffer-view images.
    private static string? TryResolveBlixTex(SharpGLTF.Schema2.Image image, string gltfDir)
    {
        var sourcePath = image.Content.SourcePath;
        if (string.IsNullOrEmpty(sourcePath)) return null;
        // SourcePath is absolute when the full ModelRoot.Load resolved URIs
        // against the glTF dir; relative (e.g. "textures/foo.png") under the
        // lite-load path where the buffer reader returns empty for non-.gltf
        // assets. Resolve against gltfDir so File.Exists hits either way.
        if (!Path.IsPathRooted(sourcePath))
        {
            sourcePath = Path.Combine(gltfDir, sourcePath);
        }
        var blixTexPath = Path.ChangeExtension(sourcePath, ".blixtex");
        return File.Exists(blixTexPath) ? blixTexPath : null;
    }

    private static Matrix4x4 ComputeNormalMatrix(Matrix4x4 model)
    {
        // F-016: engine row-vector convention. For a direction transform,
        // GraphicsMatrices.TransformDirection treats the matrix as row-vector
        // and uses the rotation 3x3 (M11-M33). For invariance under non-uniform
        // scale we need the inverse-transpose. In row-vector form that's
        // Transpose(Invert(model)).
        if (!Matrix4x4.Invert(model, out var inverse)) return Matrix4x4.Identity;
        return Matrix4x4.Transpose(inverse);
    }

    private static GltfMaterial? ExtractMaterial(
        SharpGLTF.Schema2.Material? material,
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
            var c = emissiveChannel.Value.Color;
            emissiveFactor = new Vector3(c.X, c.Y, c.Z);
            emissiveTexture = ExtractTexture(emissiveChannel.Value.Texture, textureCache);
            // KHR_materials_emissive_strength surfaces as an "EmissiveStrength"
            // parameter on the channel when present. Absent => default 1.0.
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

        // KHR_materials_transmission: SharpGLTF surfaces it as a "Transmission"
        // channel with a "TransmissionFactor" parameter. Absent => 0 (opaque).
        var transmission = 0.0f;
        var transmissionChannel = material.FindChannel("Transmission");
        if (transmissionChannel.HasValue)
        {
            foreach (var p in transmissionChannel.Value.Parameters)
            {
                if (p.Name == "TransmissionFactor") transmission = (float)Convert.ToDouble(p.Value);
            }
        }

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
            material.DoubleSided,
            transmission);
        materialCache[material.LogicalIndex] = result;
        return result;
    }

    private static GltfTexture? ExtractTexture(
        SharpGLTF.Schema2.Texture? texture,
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
}

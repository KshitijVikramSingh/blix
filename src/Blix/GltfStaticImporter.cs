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
                SanitizePackedUVs(p.VertexBytes, p.VertexCount, p.Name);
                var meshData = new MeshData(
                    p.Name,
                    p.VertexBytes,
                    p.Indices16,
                    cooked.Layout,
                    p.Bounds,
                    Indices32: p.Indices32);
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
                // SharpGLTF's WorldMatrix is row-vector form; transpose to the engine's
                // column-vector convention before consuming it for vertex transforms.
                var worldRowVector = node.WorldMatrix;
                var world = Matrix4x4.Transpose(worldRowVector);
                var normalMatrix = ComputeNormalMatrix(world);

                for (var i = 0; i < node.Mesh.Primitives.Count; i++)
                {
                    var prim = node.Mesh.Primitives[i];
                    var meshName = $"{node.Mesh.Name ?? node.Name ?? "gltf_mesh"}.{i}";
                    var meshData = BuildStaticMeshData(meshName, prim, world, normalMatrix);
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
    public static int CookToBlixMesh(string gltfPath, string outPath)
    {
        ArgumentNullException.ThrowIfNull(gltfPath);
        ArgumentNullException.ThrowIfNull(outPath);

        var model = ModelRoot.Load(gltfPath);
        var primitives = new List<BlixMeshPrimitive>();
        foreach (var node in model.LogicalNodes)
        {
            if (node.Mesh is null) continue;
            var worldRowVector = node.WorldMatrix;
            var world = Matrix4x4.Transpose(worldRowVector);
            var normalMatrix = ComputeNormalMatrix(world);
            for (var i = 0; i < node.Mesh.Primitives.Count; i++)
            {
                var prim = node.Mesh.Primitives[i];
                var meshName = $"{node.Mesh.Name ?? node.Name ?? "gltf_mesh"}.{i}";
                var meshData = BuildStaticMeshData(meshName, prim, world, normalMatrix);
                var materialIndex = prim.Material?.LogicalIndex ?? BlixMesh.NoMaterial;
                primitives.Add(new BlixMeshPrimitive(
                    Name: meshData.Name,
                    MaterialIndex: materialIndex,
                    Bounds: meshData.Bounds,
                    VertexCount: meshData.VertexCount,
                    VertexBytes: meshData.VertexBytes,
                    IndexFormat: meshData.IndexFormat,
                    Indices16: meshData.Indices,
                    Indices32: meshData.Indices32));
            }
        }

        BlixMeshWriter.Write(outPath, new BlixMeshFile(
            VertexPosition3NormalTexture.Layout,
            primitives));
        return primitives.Count;
    }

    public static MeshData BuildStaticMeshData(string name, MeshPrimitive primitive, Matrix4x4 world, Matrix4x4 normalMatrix)
    {
        var positions = primitive.GetVertexAccessor("POSITION")?.AsVector3Array()
            ?? throw new InvalidOperationException("glTF mesh primitive missing required POSITION accessor.");
        var normals = primitive.GetVertexAccessor("NORMAL")?.AsVector3Array();
        var uvs = primitive.GetVertexAccessor("TEXCOORD_0")?.AsVector2Array();

        var vertexCount = positions.Count;
        var vertices = new VertexPosition3NormalTexture[vertexCount];

        var minB = new Vector3(float.PositiveInfinity);
        var maxB = new Vector3(float.NegativeInfinity);

        for (var v = 0; v < vertexCount; v++)
        {
            var pLocal = positions[v];
            var pWorld = GraphicsMatrices.TransformPoint(world, pLocal);
            var nLocal = normals is null ? Vector3.UnitY : normals[v];
            var nWorld = Vector3.Normalize(GraphicsMatrices.TransformDirection(normalMatrix, nLocal));
            var uv = uvs is null ? Vector2.Zero : uvs[v];

            vertices[v] = new VertexPosition3NormalTexture(
                new GraphicsVector3(pWorld.X, pWorld.Y, pWorld.Z),
                new GraphicsVector3(nWorld.X, nWorld.Y, nWorld.Z),
                new GraphicsVector2(uv.X, uv.Y));

            minB = Vector3.Min(minB, pWorld);
            maxB = Vector3.Max(maxB, pWorld);
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
        var packed = VertexPosition3NormalTexture.Pack(vertices);
        SanitizePackedUVs(packed, vertexCount, name);
        return new MeshData(
            name,
            packed,
            indices16,
            VertexPosition3NormalTexture.Layout,
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

    private static int SanitizePackedUVs(byte[] vertexBytes, int vertexCount, string ownerName)
    {
        const int Stride = 32; // VertexPosition3NormalTexture
        const int UvOffset = 24;
        var touched = 0;
        var span = vertexBytes.AsSpan();
        for (var v = 0; v < vertexCount; v++)
        {
            var slot = span.Slice(v * Stride + UvOffset, 8);
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
        // Inverse-transpose. Same convention as GraphicsMatrices.CreateNormalMatrix
        // but consumed for direction transforms rather than a shader uniform.
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

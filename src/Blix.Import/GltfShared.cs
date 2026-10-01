using Blix.Cooked;
using System.Numerics;
using Blix.Assets;
using Blix.Graphics;
using Blix.Graphics.Images;
using SharpGLTF.Schema2;

namespace Blix.Import;

/// <summary>
/// The parts of reading a glTF that have nothing to do with whether it is rigged.
/// </summary>
/// <remarks>
/// <para>
/// Both rigged and static importers use these routines for image discovery, source/cooked texture
/// choice, material extraction, identity, and load reporting.
/// </para>
/// <para>
/// Geometry layout, transforms, skinning, and animation remain in the specialised importers.
/// </para>
/// </remarks>
internal static class GltfShared
{
    /// <summary>A primitive's triangles as a flat index list, whatever its mode says.</summary>
    /// <remarks>
    /// TRIANGLES is read as authored; TRIANGLE_STRIP and TRIANGLE_FAN are unrolled into lists (glTF
    /// 2.0 §3.7.2.1), and a primitive with no index accessor draws its vertices in order. POINTS and the
    /// three LINE modes are refused by name: Blix draws triangles, and reading a line's indices as a
    /// triangle list is the garbage this replaces.
    /// </remarks>
    public static uint[] TriangleIndices(string name, MeshPrimitive primitive)
    {
        if (primitive.DrawPrimitiveType is PrimitiveType.POINTS or PrimitiveType.LINES
            or PrimitiveType.LINE_LOOP or PrimitiveType.LINE_STRIP)
        {
            throw new InvalidOperationException(
                $"glTF primitive '{name}' is {primitive.DrawPrimitiveType}; Blix draws triangles, and a point or line primitive is not read.");
        }

        // The vertex order the mode reads: the index accessor (sparse substitutions applied — an index
        // accessor may itself be sparse), or the vertices in order when there is none.
        var count = primitive.GetVertexAccessor("POSITION")?.Count ?? 0;
        uint[] raw = primitive.IndexAccessor is not { } accessor ? Enumerable.Range(0, count).Select(i => (uint)i).ToArray()
            : accessor.IsSparse ? accessor.AsScalarArray().Select(v => (uint)MathF.Round(v)).ToArray()
            : accessor.AsIndicesArray().ToArray();
        // An index past the vertices reads memory that is not this primitive's: refused, by name. This is
        // also how primitive restart (an all-ones index, which glTF forbids) is caught.
        if (raw.FirstOrDefault(i => i >= count) is var beyond && raw.Any(i => i >= count))
        {
            throw new InvalidOperationException(
                $"glTF primitive '{name}' indexes vertex {beyond} of {count}; glTF forbids an index past the vertices (and primitive restart).");
        }

        // glTF 2.0 §3.7.2.1's counts, refused by name rather than repaired: a list's vertices MUST be
        // divisible by three, a strip's or fan's MUST be at least three. Dropping the remainder would
        // quietly delete geometry the file says is there — and with SharpGLTF's validator out of the way
        // for a lenient file, nothing else would say so.
        var isList = primitive.DrawPrimitiveType is not (PrimitiveType.TRIANGLE_STRIP or PrimitiveType.TRIANGLE_FAN);
        if (isList ? raw.Length == 0 || raw.Length % 3 != 0 : raw.Length < 3)
        {
            throw new InvalidOperationException(
                $"glTF primitive '{name}' has {raw.Length} vertex index(es) for {primitive.DrawPrimitiveType}; glTF requires "
                + (isList ? "a non-zero multiple of three." : "at least three."));
        }

        // glTF 2.0 §3.7.2.1: strip triangle i is (v_i, v_i+1+i%2, v_i+2-i%2) and fan triangle i is
        // (v_i+1, v_i+2, v_0), both keeping the first triangle's winding.
        switch (primitive.DrawPrimitiveType)
        {
            case PrimitiveType.TRIANGLE_STRIP:
            {
                var list = new uint[Math.Max(0, raw.Length - 2) * 3];
                for (var i = 0; i + 2 < raw.Length; i++)
                {
                    list[i * 3] = raw[i];
                    list[i * 3 + 1] = raw[i + 1 + i % 2];
                    list[i * 3 + 2] = raw[i + 2 - i % 2];
                }

                return list;
            }
            case PrimitiveType.TRIANGLE_FAN:
            {
                var list = new uint[Math.Max(0, raw.Length - 2) * 3];
                for (var i = 0; i + 2 < raw.Length; i++)
                {
                    list[i * 3] = raw[i + 1];
                    list[i * 3 + 1] = raw[i + 2];
                    list[i * 3 + 2] = raw[0];
                }

                return list;
            }
            default:
                return raw;
        }
    }

    /// <summary>The material channels worth pre-decoding, in the order a decode pass walks them.</summary>
    internal static readonly string[] PreDecodeChannels =
    {
        "BaseColor",
        "Normal",
        "MetallicRoughness",
        "Occlusion",
        "Emissive",
    };

    internal static void PreDecodeImages(
        ModelRoot model,
        Dictionary<int, TextureData> textureCache,
        string gltfDir,
        string containerPath = "")
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
            // The lazy handle reads metadata and mip locations only. Pixel bytes stay on disk until
            // the upload queue requests each mip, avoiding an all-textures CPU-byte working set.
            var one = System.Diagnostics.Stopwatch.StartNew();
            var handle = BlixTexReader.ReadHandle(path);
            textureCache[idx] = new TextureData(Path.GetFileNameWithoutExtension(path), handle)
            {
                ResourceId = Path.GetFullPath(path),
            };

            if (AssetLoadLog.Enabled)
            {
                AssetLoadLog.Report(new AssetLoadReport(
                    SourcePath: model.LogicalImages[idx].Content.SourcePath ?? $"image[{idx}]",
                    CookedPath: path,
                    Mode: AssetLoadMode.Cooked,
                    Bytes: FileLength(path),
                    LoadMs: one.Elapsed.TotalMilliseconds,
                    Recipe: CookedFile.TryReadHeader(path)?.Stamp.Recipe));
            }
        }
        cookedWatch.Stop();
        if (cookedSourcePaths.Count > 0)
        {
            Console.WriteLine(
                $"  indexed {cookedSourcePaths.Count} cooked .blixtex images in {cookedWatch.ElapsedMilliseconds} ms (lazy)");
        }

        if (sourceImages.Count == 0) return;

        var decoded = new System.Collections.Concurrent.ConcurrentDictionary<int, TextureData>();
        var decodeWatch = System.Diagnostics.Stopwatch.StartNew();
        System.Threading.Tasks.Parallel.ForEach(sourceImages, image =>
        {
            var one = System.Diagnostics.Stopwatch.StartNew();
            var bytes = image.Content.Content.ToArray();
            using var stream = new MemoryStream(bytes);
            var d = mrImageIndices.Contains(image.LogicalIndex)
                ? ImageLoader.LoadMetallicRoughness(stream)
                : ImageLoader.LoadRgba32(stream);
            decoded[image.LogicalIndex] = TextureData.Rgba8Single(
                image.Name ?? $"image_{image.LogicalIndex}",
                d.Pixels, d.Width, d.Height,
                ImageIdentity(image, containerPath, gltfDir));

            // Report source decoding and distinguish embedded images, which cannot have a sibling
            // .blixtex resolved through a source URI.
            if (AssetLoadLog.Enabled)
            {
                var embedded = string.IsNullOrEmpty(image.Content.SourcePath);
                AssetLoadLog.Report(new AssetLoadReport(
                    SourcePath: image.Content.SourcePath ?? $"{image.Name ?? "image"}[{image.LogicalIndex}] (embedded)",
                    CookedPath: null,
                    Mode: AssetLoadMode.Source,
                    Bytes: bytes.LongLength,
                    LoadMs: one.Elapsed.TotalMilliseconds,
                    Warning: embedded
                        ? "embedded in the container — a .blixtex sibling is not reachable for this image"
                        : "no .blixtex sibling — decoded from source"));
            }
        });
        foreach (var kv in decoded) textureCache[kv.Key] = kv.Value;
        Console.WriteLine(
            $"  decoded {sourceImages.Count} images in {decodeWatch.ElapsedMilliseconds} ms");
    }
    private static long FileLength(string path)
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

    // Returns the absolute path to a cooked .blixtex sibling for the image
    // if one exists, else null. glTF images carry either an embedded byte
    // blob (no source URI) or a file URI; we can only sideload .blixtex
    // for the URI case. SharpGLTF stashes the loaded URI in
    // MemoryImage.SourcePath -- absolute when an external URI was
    // resolved, null for embedded buffer-view images.
    internal static string? TryResolveBlixTex(SharpGLTF.Schema2.Image image, string gltfDir)
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
    /// <summary>Vertex channels one concrete import path consumes beyond position/normal/UV0.</summary>
    [Flags]
    internal enum VertexFeatures
    {
        None = 0,
        Tangents = 1 << 0,
        Colour = 1 << 1,
        Skinning = 1 << 2,
    }

    /// <summary>Every attribute the selected vertex path did not consume.</summary>
    /// <remarks>
    /// Collection remains subtractive, so application-specific and future semantics are surfaced
    /// without a prewritten list. The feature set is the actual output layout: tangent and colour
    /// are opt-in on static imports, while the rigged layout always reads tangents and every
    /// contiguous complete JOINTS/WEIGHTS pair before reducing influences to its strongest four.
    /// </remarks>
    internal static UnreadAttribute[] CollectIgnored(ModelRoot model, VertexFeatures features)
    {
        ArgumentNullException.ThrowIfNull(model);
        return CollectIgnored(model.LogicalMeshes.SelectMany(
            mesh => mesh.Primitives.Select(primitive => (primitive, features))));
    }

    /// <summary>
    /// Every attribute not consumed by the feature set paired with its primitive.
    /// </summary>
    /// <remarks>
    /// The per-primitive form is used by rigged files, which may also contain coloured static parts
    /// and attachments. A primitive used by two different output layouts is audited once for each
    /// layout because either copy may drop a channel.
    /// </remarks>
    internal static UnreadAttribute[] CollectIgnored(
        IEnumerable<(MeshPrimitive Primitive, VertexFeatures Features)> reads)
    {
        ArgumentNullException.ThrowIfNull(reads);
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (prim, features) in reads.Distinct())
        {
            foreach (var semantic in prim.VertexAccessors.Keys)
            {
                if (Consumes(prim, semantic, features)) continue;
                counts[semantic] = counts.GetValueOrDefault(semantic) + 1;
            }

            if (prim.MorphTargetsCount > 0)
            {
                counts[UnreadAttribute.MorphTargets] = counts.GetValueOrDefault(UnreadAttribute.MorphTargets) + 1;
            }
        }

        if (counts.Count == 0) return Array.Empty<UnreadAttribute>();

        // Deterministic order, with skin-influence diagnostics first because they are the strongest
        // signal that a static caller may have chosen the wrong importer.
        return counts
            .Select(kv => new UnreadAttribute(kv.Key, kv.Value))
            .OrderByDescending(i => i.Semantic.StartsWith("JOINTS_", StringComparison.Ordinal)
                                 || i.Semantic.StartsWith("WEIGHTS_", StringComparison.Ordinal))
            .ThenBy(i => i.Semantic, StringComparer.Ordinal)
            .ToArray();
    }

    private static bool Consumes(MeshPrimitive primitive, string semantic, VertexFeatures features)
    {
        if (semantic is "POSITION" or "NORMAL" or "TEXCOORD_0") return true;
        if (semantic == "TANGENT") return (features & VertexFeatures.Tangents) != 0;
        if (semantic is "COLOR_0" or "TEXCOORD_1")
            return (features & VertexFeatures.Colour) != 0;

        if ((features & VertexFeatures.Skinning) == 0) return false;
        if (!TryInfluenceSet(semantic, out var set)) return false;

        // BuildMeshData stops at the first incomplete pair, so a later pair is not consumed even
        // when both of its accessors exist.
        for (var i = 0; i <= set; i++)
        {
            if (primitive.GetVertexAccessor($"JOINTS_{i}") is null
                || primitive.GetVertexAccessor($"WEIGHTS_{i}") is null)
                return false;
        }

        return true;
    }

    private static bool TryInfluenceSet(string semantic, out int set)
    {
        set = -1;
        const string joints = "JOINTS_";
        const string weights = "WEIGHTS_";
        var suffix = semantic.StartsWith(joints, StringComparison.Ordinal)
            ? semantic.AsSpan(joints.Length)
            : semantic.StartsWith(weights, StringComparison.Ordinal)
                ? semantic.AsSpan(weights.Length)
                : default;
        return suffix.Length > 0 && int.TryParse(suffix, out set) && set >= 0;
    }

    /// <summary>What a glTF image's pixels ARE, as a string two loads agree on.</summary>
    /// <remarks>
    /// An external image is its own resolved path. An image embedded in a .glb has no path, so it
    /// is named by its container and index — which is exactly as stable, and is why this is a
    /// string rather than a path type.
    /// </remarks>
    private static string ImageIdentity(SharpGLTF.Schema2.Image image, string containerPath, string? gltfDir)
    {
        var uri = image.Content.SourcePath;
        if (!string.IsNullOrEmpty(uri) && !uri.StartsWith("data:", StringComparison.Ordinal))
        {
            var unescaped = Uri.UnescapeDataString(uri);
            return Path.GetFullPath(gltfDir is null ? unescaped : Path.Combine(gltfDir, unescaped));
        }

        // Embedded images are named by container and index, not by directory. Two .glb files
        // side by side both have an image 0, and a dir-scoped name would equate them — which is the
        // one thing an identity must never do.
        //
        // An empty container path yields an identity nobody else can reproduce, so such a texture is
        // simply never shared. That is the honest outcome: a caller that did not say where the
        // pixels came from has not given us an identity, and inventing one would be worse.
        return containerPath.Length == 0
            ? string.Empty
            : $"{Path.GetFullPath(containerPath)}#{image.LogicalIndex}";
    }

    /// <summary>
    /// Builds a material out of a COOKED table rather than out of the glTF, resolving each channel's
    /// image through the cache the pre-decode pass already filled.
    /// </summary>
    /// <remarks>
    /// Factors, alpha state, extension values, and image-table rows come from the cooked material
    /// table. A legacy artifact marked <c>SourceRequiredForImagesOnly</c> may still need source image
    /// bytes, but current self-contained artifacts resolve their own image resources.
    /// <para>
    /// An image index that is not in the cache resolves to null rather than throwing, and the two
    /// passes agree by construction — <see cref="PreDecodeImages"/> walks
    /// <see cref="PreDecodeChannels"/>, which is the same five channels the cook records. A miss
    /// would mean the cooked table and the source have drifted, which the stamp exists to catch
    /// earlier and more usefully than an exception here would.
    /// </para>
    /// </remarks>
    internal static PbrMaterial? ExtractMaterial(
        SharpGLTF.Schema2.Material? material,
        Dictionary<int, PbrMaterial> materialCache,
        Dictionary<int, TextureData> textureCache,
        string containerPath = "")
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
        var baseColorTexCoord = baseColorChannel.HasValue ? baseColorChannel.Value.TextureCoordinate : 0;

        var normalChannel = material.FindChannel("Normal");
        var normalTexture = normalChannel.HasValue
            ? ExtractTexture(normalChannel.Value.Texture, textureCache)
            : null;
        var normalScale = 1.0f;
        if (normalChannel.HasValue)
        {
            foreach (var p in normalChannel.Value.Parameters)
            {
                if (p.Name == "NormalScale") normalScale = (float)Convert.ToDouble(p.Value);
            }
        }

        var metallicChannel = material.FindChannel("MetallicRoughness");
        var metallic = 1.0f;
        var roughness = 1.0f;
        TextureData? metallicRoughnessTexture = null;
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
        TextureData? occlusionTexture = null;
        if (occlusionChannel.HasValue)
        {
            foreach (var p in occlusionChannel.Value.Parameters)
            {
                // SharpGLTF's name, not glTF's "strength": a lookup by the spec's word never matched.
                if (p.Name == "OcclusionStrength") occlusionStrength = (float)Convert.ToDouble(p.Value);
            }
            occlusionTexture = ExtractTexture(occlusionChannel.Value.Texture, textureCache);
        }

        var emissiveChannel = material.FindChannel("Emissive");
        var emissiveFactor = Vector3.Zero;
        var emissiveStrength = 1.0f;
        TextureData? emissiveTexture = null;
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
            SharpGLTF.Schema2.AlphaMode.OPAQUE => AlphaMode.Opaque,
            SharpGLTF.Schema2.AlphaMode.MASK   => AlphaMode.Mask,
            SharpGLTF.Schema2.AlphaMode.BLEND  => AlphaMode.Blend,
            _                                  => AlphaMode.Opaque,
        };

        // Read the KHR_materials_* surface exposed by SharpGLTF by channel and parameter name. A
        // material that declares nothing simply yields no channel and every field keeps its spec
        // default. Those defaults are not zero across the board — IOR is 1.5, attenuation distance
        // is infinite — because absence means "the base model", not "the parameter set to nothing".
        var ext = ExtractMaterialExtensions(material, textureCache);
        var transmission = ext.TransmissionFactor;

        var result = new PbrMaterial(
            PbrMaterial.IdentityOf(containerPath, material.LogicalIndex),
            material.Name ?? $"material_{material.LogicalIndex}",
            baseColorFactor,
            baseColorTexture,
            baseColorTexCoord,
            normalTexture,
            TexCoord(normalChannel),
            normalScale,
            metallicRoughnessTexture,
            TexCoord(metallicChannel),
            metallic,
            roughness,
            occlusionTexture,
            TexCoord(occlusionChannel),
            occlusionStrength,
            emissiveTexture,
            TexCoord(emissiveChannel),
            emissiveFactor,
            emissiveStrength,
            alphaMode,
            material.AlphaCutoff,
            material.DoubleSided,
            transmission,
            ext);
        materialCache[material.LogicalIndex] = result;
        return result;

        static int TexCoord(SharpGLTF.Schema2.MaterialChannel? channel) =>
            channel.HasValue ? channel.Value.TextureCoordinate : 0;
    }

    /// <summary>Reads every <c>KHR_materials_*</c> property SharpGLTF surfaces, by channel and parameter name.</summary>
    /// <remarks>
    /// A material that declares no extension yields no channel, and each
    /// field then keeps the value the SPEC says that absence means — IOR 1.5, attenuation distance
    /// infinite, specular strength 1 — because those describe the base BRDF rather than a parameter
    /// turned off. Writing zeros here would silently author a different material for every asset in
    /// existence that declines to mention these.
    /// </remarks>
    private static PbrMaterialExtensions ExtractMaterialExtensions(
        SharpGLTF.Schema2.Material material,
        Dictionary<int, TextureData> textureCache)
    {
        var e = PbrMaterialExtensions.None;

        float Param(string channel, string name, float fallback)
        {
            var c = material.FindChannel(channel);
            if (!c.HasValue) return fallback;
            foreach (var p in c.Value.Parameters)
                if (p.Name == name) return (float)Convert.ToDouble(p.Value);
            return fallback;
        }
        Vector3 Rgb(string channel, Vector3 fallback)
        {
            var c = material.FindChannel(channel);
            if (!c.HasValue) return fallback;
            var col = c.Value.Color;
            return new Vector3(col.X, col.Y, col.Z);
        }
        TextureData? Tex(string channel)
        {
            var c = material.FindChannel(channel);
            return c.HasValue ? ExtractTexture(c.Value.Texture, textureCache) : null;
        }

        return e with
        {
            TransmissionFactor = Param("Transmission", "TransmissionFactor", e.TransmissionFactor),
            TransmissionTexture = Tex("Transmission"),

            DiffuseTransmissionFactor =
                Param("DiffuseTransmissionFactor", "DiffuseTransmissionFactor", e.DiffuseTransmissionFactor),
            DiffuseTransmissionColorFactor = Rgb("DiffuseTransmissionColor", e.DiffuseTransmissionColorFactor),
            DiffuseTransmissionTexture = Tex("DiffuseTransmissionFactor"),
            DiffuseTransmissionColorTexture = Tex("DiffuseTransmissionColor"),

            SheenColorFactor = Rgb("SheenColor", e.SheenColorFactor),
            SheenRoughnessFactor = Param("SheenRoughness", "RoughnessFactor", e.SheenRoughnessFactor),
            SheenColorTexture = Tex("SheenColor"),
            SheenRoughnessTexture = Tex("SheenRoughness"),

            ThicknessFactor = Param("VolumeThickness", "ThicknessFactor", e.ThicknessFactor),
            AttenuationDistance = Param("VolumeAttenuation", "AttenuationDistance", e.AttenuationDistance),
            AttenuationColor = Rgb("VolumeAttenuation", e.AttenuationColor),
            ThicknessTexture = Tex("VolumeThickness"),

            SpecularFactor = Param("SpecularFactor", "SpecularFactor", e.SpecularFactor),
            SpecularColorFactor = Rgb("SpecularColor", e.SpecularColorFactor),
            SpecularTexture = Tex("SpecularFactor"),
            SpecularColorTexture = Tex("SpecularColor"),

            IndexOfRefraction = material.IndexOfRefraction is var ior && ior > 0f ? ior : e.IndexOfRefraction,

            ClearcoatFactor = Param("ClearCoat", "ClearCoatFactor", e.ClearcoatFactor),
            ClearcoatRoughnessFactor = Param("ClearCoatRoughness", "RoughnessFactor", e.ClearcoatRoughnessFactor),
            ClearcoatNormalScale = Param("ClearCoatNormal", "NormalScale", e.ClearcoatNormalScale),
            ClearcoatTexture = Tex("ClearCoat"),
            ClearcoatRoughnessTexture = Tex("ClearCoatRoughness"),
            ClearcoatNormalTexture = Tex("ClearCoatNormal"),

            IridescenceFactor = Param("Iridescence", "IridescenceFactor", e.IridescenceFactor),
            IridescenceIor = Param("Iridescence", "IndexOfRefraction", e.IridescenceIor),
            IridescenceThicknessMinimum = Param("IridescenceThickness", "Minimum", e.IridescenceThicknessMinimum),
            IridescenceThicknessMaximum = Param("IridescenceThickness", "Maximum", e.IridescenceThicknessMaximum),
            IridescenceTexture = Tex("Iridescence"),
            IridescenceThicknessTexture = Tex("IridescenceThickness"),

            AnisotropyStrength = Param("Anisotropy", "AnisotropyStrength", e.AnisotropyStrength),
            AnisotropyRotation = Param("Anisotropy", "AnisotropyRotation", e.AnisotropyRotation),
            AnisotropyTexture = Tex("Anisotropy"),

            Dispersion = material.Dispersion,
            Unlit = material.Unlit,
        };
    }

    internal static TextureData? ExtractTexture(
        SharpGLTF.Schema2.Texture? texture,
        Dictionary<int, TextureData> textureCache)
    {
        if (texture is null) return null;
        var image = texture.PrimaryImage;
        if (image is null) return null;
        if (textureCache.TryGetValue(image.LogicalIndex, out var cached)) return cached;

        var bytes = image.Content.Content;
        using var stream = new MemoryStream(bytes.ToArray());
        var decoded = ImageLoader.LoadRgba32(stream);

        var result = TextureData.Rgba8Single(
            image.Name ?? texture.Name ?? $"image_{image.LogicalIndex}",
            decoded.Pixels, decoded.Width, decoded.Height,
            ImageIdentity(image, containerPath: string.Empty, gltfDir: null));
        textureCache[image.LogicalIndex] = result;
        return result;
    }
}

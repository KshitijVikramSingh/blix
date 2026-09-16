using Blix.Cooked;
using System.Numerics;
using Blix.Assets;
using Blix.Graphics;
using Blix.Graphics.Images;
using SharpGLTF.Schema2;

namespace Blix;

/// <summary>
/// The parts of reading a glTF that have nothing to do with whether it is rigged.
/// </summary>
/// <remarks>
/// <para>
/// <b>These lived in both importers, and the copies had drifted.</b> The header of
/// GltfStaticImporter recorded the trade at the time — "the extraction logic is duplicated rather
/// than shared because hoisting it would pull GltfImporter's private internals into a third file.
/// ~30 lines of dupe" — and the estimate did not survive: it measured 203 lines against 235, with
/// all four copies differing.
/// </para>
/// <para>
/// <b>None of the differences were decisions.</b> ExtractTexture differed by a type qualification
/// alone. TryResolveBlixTex resolved relative texture paths in one copy and not the other.
/// ExtractMaterial had KHR_materials_transmission and emissive strength on the static side only, so
/// a rigged character with a glass visor lost it silently. PreDecodeImages had the cooked .blixtex
/// fast path on one side, so rigged assets decoded PNGs that had already been cooked. Every one of
/// those is the static importer receiving work the character path was never asked about.
/// </para>
/// <para>
/// So the static copy was the superset in all four cases, and this is it. The static path is
/// unchanged by construction; the rigged path gains transmission, emissive strength, cooked-texture
/// loading and relative-path resolution — none of which it was refusing, all of which it was
/// missing.
/// </para>
/// </remarks>
internal static class GltfShared
{
    /// <summary>The material channels worth pre-decoding, in the order a decode pass walks them.</summary>
    /// <remarks>
    /// This was duplicated too, byte for byte, with a comment on one copy reading "Mirror of
    /// GltfStaticImporter.PreDecodeChannels". A comment that names its own twin is a note saying
    /// the drift has not happened YET.
    /// </remarks>
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
            var one = System.Diagnostics.Stopwatch.StartNew();
            var handle = BlixTexReader.ReadHandle(path);
            textureCache[idx] = new GltfTexture(
                Path.GetFileNameWithoutExtension(path), handle);

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

        var decoded = new System.Collections.Concurrent.ConcurrentDictionary<int, GltfTexture>();
        var decodeWatch = System.Diagnostics.Stopwatch.StartNew();
        System.Threading.Tasks.Parallel.ForEach(sourceImages, image =>
        {
            var one = System.Diagnostics.Stopwatch.StartNew();
            var bytes = image.Content.Content.ToArray();
            using var stream = new MemoryStream(bytes);
            var d = mrImageIndices.Contains(image.LogicalIndex)
                ? ImageLoader.LoadMetallicRoughness(stream)
                : ImageLoader.LoadRgba32(stream);
            decoded[image.LogicalIndex] = GltfTexture.Rgba8Single(
                image.Name ?? $"image_{image.LogicalIndex}",
                d.Pixels, d.Width, d.Height);

            // <b>The silent one.</b> A texture that decodes from PNG on every load, because no
            // .blixtex sibling was found, is indistinguishable from one that did not — and for an
            // image embedded in a .glb it is not even possible to have a sibling, which is a fact
            // about how the asset was authored that nothing could previously report.
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
    /// <summary>The attribute semantics this importer actually reads. Everything else is reported.</summary>
    /// <remarks>
    /// <b>One list, because both importers have the same gap.</b> The rigged path reads JOINTS_0 and
    /// WEIGHTS_0 where the static path does not, but neither reads index 1 of anything — so a single
    /// set is honest for both and a second copy would be a place for them to drift.
    /// </remarks>
    private static readonly HashSet<string> ReadSemantics = new(StringComparer.Ordinal)
    {
        "POSITION", "NORMAL", "TANGENT", "TEXCOORD_0", "COLOR_0", "JOINTS_0", "WEIGHTS_0",
    };

    /// <summary>
    /// Every attribute the file declares that this importer does not read, with how many primitives
    /// carried each.
    /// </summary>
    /// <remarks>
    /// Subtractive on purpose: it asks what the primitive HAS and removes what we read, rather than
    /// looking for a list of names someone thought of. An exporter emitting something nobody here
    /// anticipated is exactly the case worth hearing about, and a hardcoded list is deaf to it.
    /// </remarks>
    internal static GltfIgnored[] CollectIgnored(ModelRoot model)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var mesh in model.LogicalMeshes)
        {
            foreach (var prim in mesh.Primitives)
            {
                foreach (var semantic in prim.VertexAccessors.Keys)
                {
                    if (ReadSemantics.Contains(semantic)) continue;
                    counts[semantic] = counts.GetValueOrDefault(semantic) + 1;
                }

                if (prim.MorphTargetsCount > 0)
                {
                    counts[GltfIgnored.MorphTargets] = counts.GetValueOrDefault(GltfIgnored.MorphTargets) + 1;
                }
            }
        }

        if (counts.Count == 0) return Array.Empty<GltfIgnored>();

        // Ordered so the report reads the same way twice, and so the one that corrupts geometry
        // rather than merely omitting it comes first.
        return counts
            .Select(kv => new GltfIgnored(kv.Key, kv.Value))
            .OrderByDescending(i => i.Semantic.StartsWith("JOINTS_", StringComparison.Ordinal)
                                 || i.Semantic.StartsWith("WEIGHTS_", StringComparison.Ordinal))
            .ThenBy(i => i.Semantic, StringComparer.Ordinal)
            .ToArray();
    }

    internal static GltfMaterial? ExtractMaterial(
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
        var baseColorTexCoord = baseColorChannel.HasValue ? baseColorChannel.Value.TextureCoordinate : 0;

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
            baseColorTexCoord,
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
    internal static GltfTexture? ExtractTexture(
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

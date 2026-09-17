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
            // Lazy handle: parses just the 32-byte header + per-mip
            // (offset, length) table. The pixel bytes stay on disk until
            // the upload pump pulls them mip-by-mip at GL upload time.
            // This drops the per-pack import-time memory peak from
            // "all mip data for all textures" (multi-GB) to ~hundreds of
            // bytes per texture.
            var one = System.Diagnostics.Stopwatch.StartNew();
            var handle = BlixTexReader.ReadHandle(path);
            textureCache[idx] = new GltfTexture(Path.GetFileNameWithoutExtension(path), handle)
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
                d.Pixels, d.Width, d.Height,
                ImageIdentity(image, containerPath, gltfDir));

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

    /// <summary>
    /// Builds a material out of a COOKED table rather than out of the glTF, resolving each channel's
    /// image through the cache the pre-decode pass already filled.
    /// </summary>
    /// <remarks>
    /// <b>This is what stage K-F actually buys, and it is worth stating precisely.</b> Before it,
    /// a cooked mesh still walked <c>model.LogicalMaterials</c> on every load — so "cooked" meant
    /// geometry only, and every factor, alpha mode and texture reference was re-parsed out of the
    /// source each time. Now the values come from the file and the SOURCE is opened for one thing:
    /// image bytes. That is the difference the <c>SourceRequiredForImagesOnly</c> flag records.
    /// <para>
    /// An image index that is not in the cache resolves to null rather than throwing, and the two
    /// passes agree by construction — <see cref="PreDecodeImages"/> walks
    /// <see cref="PreDecodeChannels"/>, which is the same five channels the cook records. A miss
    /// would mean the cooked table and the source have drifted, which the stamp exists to catch
    /// earlier and more usefully than an exception here would.
    /// </para>
    /// </remarks>
    /// <summary>
    /// Loads every image a cooked mesh names, from the cooked mesh's own table — no glTF involved.
    /// </summary>
    /// <remarks>
    /// <b>The counterpart to <see cref="PreDecodeImages"/>, and the reason a cooked asset can be
    /// loaded with its source deleted.</b> That method walks a <c>ModelRoot</c> to discover which
    /// images the materials use and where they live; this reads both off the cooked file, which is
    /// what the image table was added to record.
    /// <para>
    /// <b>The cache is keyed by ROW, matching what a cooked material channel now holds.</b>
    /// <see cref="MaterialFromCooked"/> looks its textures up by that same number, so the two agree
    /// without either of them knowing a glTF logical index exists.
    /// </para>
    /// <para>
    /// A row whose resource is a <c>.blixtex</c> is opened lazily — header and mip table only,
    /// pixels stay on disk. A row still naming its source image is decoded, and reported on the slow
    /// path so <c>blix check --cooked</c> can say so. Metallic-roughness images are routed through
    /// the channel-aware decoder exactly as the glTF path routes them, because a one-channel
    /// roughness PNG expanded by the ordinary loader reads as matte metal.
    /// </para>
    /// </remarks>
    internal static void LoadImagesFromTable(
        IReadOnlyList<BlixMeshImage> images,
        IReadOnlyList<BlixMeshMaterial> materials,
        string cookedDir,
        Dictionary<int, GltfTexture> textureCache)
    {
        if (images.Count == 0) return;

        var metallicRoughnessRows = new HashSet<int>();
        foreach (var m in materials)
        {
            if (m.MetallicRoughnessImage >= 0) metallicRoughnessRows.Add(m.MetallicRoughnessImage);
        }

        var cooked = 0;
        var cookedWatch = System.Diagnostics.Stopwatch.StartNew();
        var decodedCount = 0;

        for (var row = 0; row < images.Count; row++)
        {
            var entry = images[row];
            var path = Path.GetFullPath(Path.Combine(cookedDir, entry.Resource));
            var one = System.Diagnostics.Stopwatch.StartNew();

            if (!File.Exists(path))
            {
                // Reported rather than thrown: one missing texture should not stop an asset from
                // loading, and a model drawn with a channel missing is a thing a person can see and
                // act on. The row still says what was wanted, which is more than the old path could
                // say once the glTF was gone.
                if (AssetLoadLog.Enabled)
                {
                    AssetLoadLog.Report(new AssetLoadReport(
                        SourcePath: entry.Resource, CookedPath: null, Mode: AssetLoadMode.Source,
                        Bytes: 0, LoadMs: 0,
                        Warning: $"image '{entry.Name}' is missing — the cooked mesh names {entry.Resource}"));
                }

                continue;
            }

            if (path.EndsWith(".blixtex", StringComparison.OrdinalIgnoreCase))
            {
                textureCache[row] = new GltfTexture(entry.Name, BlixTexReader.ReadHandle(path))
                {
                    ResourceId = path,
                };
                cooked++;
                if (AssetLoadLog.Enabled)
                {
                    AssetLoadLog.Report(new AssetLoadReport(
                        SourcePath: entry.Resource, CookedPath: path, Mode: AssetLoadMode.Cooked,
                        Bytes: FileLength(path), LoadMs: one.Elapsed.TotalMilliseconds,
                        Recipe: CookedFile.TryReadHeader(path)?.Stamp.Recipe));
                }

                continue;
            }

            using (var stream = File.OpenRead(path))
            {
                var d = metallicRoughnessRows.Contains(row)
                    ? ImageLoader.LoadMetallicRoughness(stream)
                    : ImageLoader.LoadRgba32(stream);
                textureCache[row] = GltfTexture.Rgba8Single(entry.Name, d.Pixels, d.Width, d.Height, path);
            }

            decodedCount++;
            if (AssetLoadLog.Enabled)
            {
                AssetLoadLog.Report(new AssetLoadReport(
                    SourcePath: entry.Resource, CookedPath: null, Mode: AssetLoadMode.Source,
                    Bytes: FileLength(path), LoadMs: one.Elapsed.TotalMilliseconds,
                    Warning: "no .blixtex for this image — decoded from source"));
            }
        }

        cookedWatch.Stop();
        if (cooked > 0)
        {
            Console.WriteLine(
                $"  indexed {cooked} cooked .blixtex images in {cookedWatch.ElapsedMilliseconds} ms (lazy)");
        }

        if (decodedCount > 0) Console.WriteLine($"  decoded {decodedCount} images from source");
    }

    /// <summary>What a material IS, as a string two loads agree on.</summary>
    /// <remarks>
    /// Empty when the caller cannot say which file it came from — never shared, for the same reason
    /// an unnamed texture is not: an identity nobody can reproduce is not an identity.
    /// </remarks>
    private static string MaterialIdentity(string containerPath, int index) =>
        containerPath.Length == 0 ? string.Empty : $"{Path.GetFullPath(containerPath)}#material{index}";

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

        // <b>Embedded: named by its CONTAINER and index, not by the directory.</b> Two .glb files
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

    internal static GltfMaterial? MaterialFromCooked(
        IReadOnlyList<BlixMeshMaterial> materials,
        int index,
        Dictionary<int, GltfMaterial> materialCache,
        Dictionary<int, GltfTexture> textureCache,
        string cookedPath = "")
    {
        if (index < 0 || index >= materials.Count) return null;
        if (materialCache.TryGetValue(index, out var cached)) return cached;

        var m = materials[index];
        var result = new GltfMaterial(
            MaterialIdentity(cookedPath, index),
            m.Name,
            m.BaseColorFactor,
            Texture(m.BaseColorImage),
            m.BaseColorTexCoord,
            Texture(m.NormalImage),
            Texture(m.MetallicRoughnessImage),
            m.MetallicFactor,
            m.RoughnessFactor,
            Texture(m.OcclusionImage),
            m.OcclusionStrength,
            Texture(m.EmissiveImage),
            m.EmissiveFactor,
            m.EmissiveStrength,
            m.AlphaMode switch
            {
                BlixMesh.AlphaMask => GltfAlphaMode.Mask,
                BlixMesh.AlphaBlend => GltfAlphaMode.Blend,
                _ => GltfAlphaMode.Opaque,
            },
            m.AlphaCutoff,
            m.DoubleSided,
            m.TransmissionFactor);

        materialCache[index] = result;
        return result;

        GltfTexture? Texture(int image) =>
            image >= 0 && textureCache.TryGetValue(image, out var t) ? t : null;
    }

    internal static GltfMaterial? ExtractMaterial(
        SharpGLTF.Schema2.Material? material,
        Dictionary<int, GltfMaterial> materialCache,
        Dictionary<int, GltfTexture> textureCache,
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
            MaterialIdentity(containerPath, material.LogicalIndex),
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
            decoded.Pixels, decoded.Width, decoded.Height,
            ImageIdentity(image, containerPath: string.Empty, gltfDir: null));
        textureCache[image.LogicalIndex] = result;
        return result;
    }
}

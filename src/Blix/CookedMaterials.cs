using Blix.Assets;
using Blix.Cooked;
using Blix.Graphics;
using Blix.Graphics.Images;

namespace Blix;

/// <summary>A cooked mesh's materials and images, read from its own tables: no source involved.</summary>
/// <remarks>
/// A cooked file's image table resolved to textures, and its material rows to
/// <see cref="PbrMaterial"/>s. Parsing a source's materials is the cook's (Blix.Recipes).
/// </remarks>
internal static class CookedMaterials
{
    /// <summary>
    /// Loads every image a cooked mesh names, from the cooked mesh's own table — no glTF involved.
    /// </summary>
    /// <remarks>
    /// Image discovery and resource location both come from the cooked image table.
    /// <para>
    /// The cache is keyed by image-table row, matching what a cooked material channel stores.
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
        Dictionary<int, TextureData> textureCache)
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
                // A missing image leaves that material channel unbound and is reported without
                // preventing the rest of the cooked model from loading.
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
                textureCache[row] = new TextureData(entry.Name, BlixTexReader.ReadHandle(path))
                {
                    ResourceId = path,
                    Sampler = entry.Sampler.ToSamplerDescription(),
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
                textureCache[row] = TextureData.Rgba8Single(entry.Name, d.Pixels, d.Width, d.Height, path, entry.Sampler.ToSamplerDescription());
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


    internal static PbrMaterial? MaterialFromCooked(
        IReadOnlyList<BlixMeshMaterial> materials,
        int index,
        Dictionary<int, PbrMaterial> materialCache,
        Dictionary<int, TextureData> textureCache,
        string cookedPath = "")
    {
        if (index < 0 || index >= materials.Count) return null;
        if (materialCache.TryGetValue(index, out var cached)) return cached;

        var m = materials[index];
        var result = new PbrMaterial(
            PbrMaterial.IdentityOf(cookedPath, index),
            m.Name,
            m.BaseColorFactor,
            Texture(m.BaseColorImage),
            m.BaseColorTexCoord,
            Texture(m.NormalImage),
            m.NormalTexCoord,
            m.NormalScale,
            Texture(m.MetallicRoughnessImage),
            m.MetallicRoughnessTexCoord,
            m.MetallicFactor,
            m.RoughnessFactor,
            Texture(m.OcclusionImage),
            m.OcclusionTexCoord,
            m.OcclusionStrength,
            Texture(m.EmissiveImage),
            m.EmissiveTexCoord,
            m.EmissiveFactor,
            m.EmissiveStrength,
            m.AlphaMode switch
            {
                BlixMesh.AlphaMask => AlphaMode.Mask,
                BlixMesh.AlphaBlend => AlphaMode.Blend,
                _ => AlphaMode.Opaque,
            },
            m.AlphaCutoff,
            m.DoubleSided,
            m.TransmissionFactor,
            // Resolve cooked extension image rows through the same texture cache as core material
            // channels so source and cooked material shapes agree.
            CookedExtensions(m.Ext, Texture),
            m.UvTransforms is { } uv ? new PbrUvTransforms(Uv(uv.BaseColor), Uv(uv.Normal), Uv(uv.MetallicRoughness), Uv(uv.Occlusion), Uv(uv.Emissive)) : null,
            m.ExtensionUv?.Select(e => new PbrTextureUv(e.TexCoord, Uv(e.Transform))).ToArray());

        materialCache[index] = result;
        return result;

        TextureData? Texture(int image) =>
            image >= 0 && textureCache.TryGetValue(image, out var t) ? t : null;

        static UvTransform Uv(BlixMeshUvTransform t) => new(t.Offset, t.Rotation, t.Scale);
    }


    /// <summary>The cooked <c>KHR_materials_*</c> block as the engine's, with images resolved.</summary>
    private static PbrMaterialExtensions CookedExtensions(
        Blix.Assets.BlixMaterialExtensions x,
        Func<int, TextureData?> texture) => new(
            TransmissionFactor: x.TransmissionFactor,
            TransmissionTexture: texture(x.TransmissionImage),
            DiffuseTransmissionFactor: x.DiffuseTransmissionFactor,
            DiffuseTransmissionColorFactor: x.DiffuseTransmissionColorFactor,
            DiffuseTransmissionTexture: texture(x.DiffuseTransmissionImage),
            DiffuseTransmissionColorTexture: texture(x.DiffuseTransmissionColorImage),
            SheenColorFactor: x.SheenColorFactor,
            SheenRoughnessFactor: x.SheenRoughnessFactor,
            SheenColorTexture: texture(x.SheenColorImage),
            SheenRoughnessTexture: texture(x.SheenRoughnessImage),
            ThicknessFactor: x.ThicknessFactor,
            AttenuationDistance: x.AttenuationDistance,
            AttenuationColor: x.AttenuationColor,
            ThicknessTexture: texture(x.ThicknessImage),
            SpecularFactor: x.SpecularFactor,
            SpecularColorFactor: x.SpecularColorFactor,
            SpecularTexture: texture(x.SpecularImage),
            SpecularColorTexture: texture(x.SpecularColorImage),
            IndexOfRefraction: x.IndexOfRefraction,
            ClearcoatFactor: x.ClearcoatFactor,
            ClearcoatRoughnessFactor: x.ClearcoatRoughnessFactor,
            ClearcoatNormalScale: x.ClearcoatNormalScale,
            ClearcoatTexture: texture(x.ClearcoatImage),
            ClearcoatRoughnessTexture: texture(x.ClearcoatRoughnessImage),
            ClearcoatNormalTexture: texture(x.ClearcoatNormalImage),
            IridescenceFactor: x.IridescenceFactor,
            IridescenceIor: x.IridescenceIor,
            IridescenceThicknessMinimum: x.IridescenceThicknessMinimum,
            IridescenceThicknessMaximum: x.IridescenceThicknessMaximum,
            IridescenceTexture: texture(x.IridescenceImage),
            IridescenceThicknessTexture: texture(x.IridescenceThicknessImage),
            AnisotropyStrength: x.AnisotropyStrength,
            AnisotropyRotation: x.AnisotropyRotation,
            AnisotropyTexture: texture(x.AnisotropyImage),
            Dispersion: x.Dispersion,
            Unlit: x.Unlit);

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

}

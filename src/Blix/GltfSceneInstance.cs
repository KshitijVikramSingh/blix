using System.Numerics;
using Blix.Geometry;
using Blix.Graphics;
using Blix.Graphics.Images;
using Blix.Render;

namespace Blix;

// Built scene from a GltfModel: every primitive uploaded as a Mesh, every
// glTF material constructed into a runtime Material with the right pipeline
// for its alpha mode and double-sided flag. The result is two flat lists --
// submeshes and unique materials -- plus a world-space bounds covering
// everything drawn.
//
// Why this exists: the Walkthrough demo grew a per-primitive material-build
// loop with hardcoded uniform names and a "tag the marble floor by bounds
// heuristic" detour. Once Sponza Modern + add-ons land we'll have many more
// primitives plus alphaMode and doubleSided that the per-primitive loop
// would have to encode anyway. GltfSceneInstance turns the inline loop into
// "supply N pipelines + a callback that binds scene-wide textures"; the
// demo keeps full control of those without owning the bookkeeping.
//
// Pipeline selection: glTF gives us three alpha modes (OPAQUE / MASK / BLEND)
// times double-sided (off/on) = six variants. The renderer rarely needs all
// six; demos declare the ones they want and the rest fall back to Opaque
// with a warning. Saves demos from having to declare a "doubleSided alpha
// blend" pipeline when they have no curtains.
//
// Material customization runs through `OnMaterialBuilt` -- the place to
// bind shadow maps, IBL probes, BRDF LUT, env cube, etc. that every
// material in the scene shares. Without it GltfSceneInstance would need
// to know about every scene-wide texture, which couples it to the
// scene-renderer it shouldn't know about.
public sealed class GltfSceneInstance
{
    public IReadOnlyList<SubmeshInstance> Submeshes { get; }
    public Bounds3 Bounds { get; }
    public MaterialSet Materials { get; }

    private GltfSceneInstance(SubmeshInstance[] submeshes, Bounds3 bounds, MaterialSet materials)
    {
        Submeshes = submeshes;
        Bounds = bounds;
        Materials = materials;
    }

    public static GltfSceneInstance Build(IGraphicsDevice device, GltfModel model, GltfSceneOptions options)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(options);

        var textureCache = new Dictionary<GltfTexture, TextureHandle>(ReferenceEqualityComparer.Instance);
        var materialCache = new Dictionary<GltfMaterial, Material>(ReferenceEqualityComparer.Instance);
        var submeshes = new SubmeshInstance[model.Primitives.Length];

        var worldMin = new Vector3(float.PositiveInfinity);
        var worldMax = new Vector3(float.NegativeInfinity);
        var fallbackPipelineWarnings = new HashSet<string>();

        for (var i = 0; i < model.Primitives.Length; i++)
        {
            var prim = model.Primitives[i];
            var meshName = $"{options.Prefix}.mesh.{prim.Mesh.Name}";
            var vb = device.CreateVertexBuffer(
                new VertexBufferData(
                    new VertexBufferDescription(prim.Mesh.Layout, prim.Mesh.VertexCount, GraphicsBufferUsage.Static),
                    prim.Mesh.VertexBytes),
                name: $"{meshName}.vb");
            var ib = device.CreateIndexBuffer(prim.Mesh.Indices, name: $"{meshName}.ib");
            var mesh = new Mesh(meshName, vb, ib, prim.Mesh.Indices.Length, prim.Mesh.Bounds);

            var alphaMode = prim.Material?.AlphaMode ?? GltfAlphaMode.Opaque;
            var doubleSided = prim.Material?.DoubleSided ?? false;
            var pipeline = SelectPipeline(options, alphaMode, doubleSided, fallbackPipelineWarnings);

            Material? material;
            if (prim.Material is { } gm)
            {
                if (!materialCache.TryGetValue(gm, out material))
                {
                    material = BuildMaterial(device, gm, pipeline, options, textureCache);
                    options.OnMaterialBuilt?.Invoke(material, gm);
                    materialCache[gm] = material;
                }
            }
            else
            {
                // Unnamed default material -- shared across every prim that
                // imports with a null material reference.
                var defaultKey = new GltfMaterial(
                    Name: $"{options.Prefix}.default",
                    BaseColorFactor: new Vector4(1.0f),
                    BaseColorTexture: null, NormalTexture: null,
                    MetallicRoughnessTexture: null, MetallicFactor: 1.0f, RoughnessFactor: 1.0f,
                    OcclusionTexture: null, OcclusionStrength: 1.0f,
                    EmissiveTexture: null, EmissiveFactor: Vector3.Zero, EmissiveStrength: 1.0f,
                    AlphaMode: GltfAlphaMode.Opaque, AlphaCutoff: 0.5f, DoubleSided: false);
                material = BuildMaterial(device, defaultKey, pipeline, options, textureCache);
                options.OnMaterialBuilt?.Invoke(material, null);
            }

            submeshes[i] = new SubmeshInstance(
                Name: prim.Mesh.Name,
                Mesh: mesh,
                Material: material,
                Source: prim.Material,
                WorldBounds: prim.Mesh.Bounds,
                AlphaMode: alphaMode,
                DoubleSided: doubleSided);

            worldMin = Vector3.Min(worldMin, prim.Mesh.Bounds.Min);
            worldMax = Vector3.Max(worldMax, prim.Mesh.Bounds.Max);
        }

        var bounds = model.Primitives.Length == 0
            ? Bounds3.Empty
            : new Bounds3(worldMin, worldMax);
        var materialSet = new MaterialSet(materialCache.Values.ToArray());
        return new GltfSceneInstance(submeshes, bounds, materialSet);
    }

    private static PipelineHandle SelectPipeline(
        GltfSceneOptions options, GltfAlphaMode mode, bool doubleSided,
        HashSet<string> warnedFor)
    {
        PipelineHandle? requested = (mode, doubleSided) switch
        {
            (GltfAlphaMode.Opaque, false) => options.Opaque,
            (GltfAlphaMode.Opaque, true)  => options.OpaqueDoubleSided,
            (GltfAlphaMode.Mask,   false) => options.AlphaMask,
            (GltfAlphaMode.Mask,   true)  => options.AlphaMaskDoubleSided,
            (GltfAlphaMode.Blend,  false) => options.AlphaBlend,
            (GltfAlphaMode.Blend,  true)  => options.AlphaBlendDoubleSided,
            _ => options.Opaque,
        };

        if (requested.HasValue) return requested.Value;

        var key = $"{mode}/{doubleSided}";
        if (warnedFor.Add(key))
        {
            Console.WriteLine(
                $"[GltfSceneInstance] No pipeline provided for {mode}/doubleSided={doubleSided}; " +
                "falling back to Opaque. Visual artefacts likely on the affected primitives.");
        }
        return options.Opaque;
    }

    private static Material BuildMaterial(
        IGraphicsDevice device, GltfMaterial gm, PipelineHandle pipeline,
        GltfSceneOptions options, Dictionary<GltfTexture, TextureHandle> textureCache)
    {
        var albedo = UploadOrFallback(device, gm.BaseColorTexture, options, textureCache, options.Defaults.WhitePixel, "albedo");
        var normal = UploadOrFallback(device, gm.NormalTexture, options, textureCache, options.Defaults.FlatNormal, "normal");
        var mr = UploadOrFallback(device, gm.MetallicRoughnessTexture, options, textureCache, options.Defaults.NeutralMetallicRoughness, "mr");
        var occlusion = UploadOrFallback(device, gm.OcclusionTexture, options, textureCache, options.Defaults.FullOcclusion, "ao");
        var emissive = UploadOrFallback(device, gm.EmissiveTexture, options, textureCache, options.Defaults.WhitePixel, "emissive");

        var material = new Material($"{options.Prefix}.mat.{gm.Name}", pipeline);
        material.SetUniform("uBaseColorFactor", new Vector4Uniform(gm.BaseColorFactor));
        material.SetUniform("uMetallicFactor", new FloatUniform(gm.MetallicFactor));
        material.SetUniform("uRoughnessFactor", new FloatUniform(gm.RoughnessFactor));
        // Multiply factor by strength so the lit shader only needs one uniform.
        // KHR_materials_emissive_strength defaults to 1.0 when the extension
        // isn't present, so this stays correct for vanilla glTF.
        material.SetUniform("uEmissiveFactor", new Vector3Uniform(gm.EmissiveFactor * gm.EmissiveStrength));
        material.SetUniform("uOcclusionStrength", new FloatUniform(gm.OcclusionStrength));
        material.SetUniform("uAlphaCutoff", new FloatUniform(gm.AlphaCutoff));
        // Existing lit shader uses uNormalScale as a "has normal map" gate.
        material.SetUniform("uNormalScale", new FloatUniform(gm.NormalTexture is null ? 0.0f : 1.0f));
        material.SetUniform("uHasMetallicMap", new FloatUniform(gm.MetallicRoughnessTexture is null ? 0.0f : 1.0f));
        material.SetUniform("uHasOcclusionMap", new FloatUniform(gm.OcclusionTexture is null ? 0.0f : 1.0f));

        material.SetTexture("uAlbedo", albedo, 0);
        material.SetTexture("uNormalMap", normal, 1);
        material.SetTexture("uMetallicRoughness", mr, 2);
        material.SetTexture("uEmissive", emissive, 3);
        material.SetTexture("uOcclusion", occlusion, 15);
        return material;
    }

    private static TextureHandle UploadOrFallback(
        IGraphicsDevice device, GltfTexture? source, GltfSceneOptions options,
        Dictionary<GltfTexture, TextureHandle> cache, TextureHandle fallback, string slotHint)
    {
        if (source is null) return fallback;
        if (cache.TryGetValue(source, out var existing)) return existing;
        var handle = device.CreateTexture2D(
            new TextureDescription(source.Width, source.Height, TextureFormat.Rgba8, options.Sampler),
            source.RgbaPixels,
            name: $"{options.Prefix}.{slotHint}.{source.Name}");
        cache[source] = handle;
        return handle;
    }
}

// One uploaded primitive paired with the runtime Material it draws with and
// the GltfMaterial it was built from (kept for inspection + overrides).
public sealed record SubmeshInstance(
    string Name,
    Mesh Mesh,
    Material Material,
    GltfMaterial? Source,
    Bounds3 WorldBounds,
    GltfAlphaMode AlphaMode,
    bool DoubleSided);

// Flat set of unique runtime materials a scene built. Demos use `Find` to
// look up by name when applying targeted overrides (Sponza's marble floor,
// for example).
public sealed class MaterialSet
{
    private readonly Material[] all;
    private readonly Dictionary<string, Material> byName;

    public MaterialSet(IEnumerable<Material> materials)
    {
        all = materials.ToArray();
        byName = all.ToDictionary(m => m.Name, m => m);
    }

    public IReadOnlyList<Material> All => all;

    public Material? Find(string name) => byName.TryGetValue(name, out var m) ? m : null;
}

// Pipelines + defaults + customisation hook fed into GltfSceneInstance.Build.
// Designed so a minimal demo only has to populate `Opaque` + `Defaults` +
// `Sampler`; richer demos opt into MASK / BLEND / DOUBLE_SIDED pipelines
// only for the variants their scene actually uses.
public sealed class GltfSceneOptions
{
    public required PipelineHandle Opaque { get; init; }
    public PipelineHandle? OpaqueDoubleSided { get; init; }
    public PipelineHandle? AlphaMask { get; init; }
    public PipelineHandle? AlphaMaskDoubleSided { get; init; }
    public PipelineHandle? AlphaBlend { get; init; }
    public PipelineHandle? AlphaBlendDoubleSided { get; init; }

    public required GltfDefaultTextures Defaults { get; init; }
    public required SamplerDescription Sampler { get; init; }

    // Demo hook: invoked once per built Material with the source GltfMaterial
    // (null for primitives with no material reference). Bind any scene-wide
    // textures (shadow maps, IBL probes, BRDF LUT, env cube) here.
    public Action<Material, GltfMaterial?>? OnMaterialBuilt { get; init; }

    public string Prefix { get; init; } = "gltf";
}

// 1x1 default textures the lit pipeline falls back to when a material doesn't
// author a particular slot. Demos build these once and share across scenes.
public sealed record GltfDefaultTextures(
    // White (255,255,255,255). Used for missing albedo (factor alone drives)
    // AND missing emissive (factor alone drives -- glTF spec: absent texture
    // implies all components = 1.0 so the factor isn't zeroed out).
    TextureHandle WhitePixel,
    // (128,128,255,255). Tangent-space "up" -- decoded as (0,0,1) in shader.
    TextureHandle FlatNormal,
    // (255,255,255,255). Default MR sample is 1.0 in every channel so the
    // factor uniforms control the BRDF on their own; the shader's gate
    // uniform (uHasMetallicMap) is what actually disables sampling.
    TextureHandle NeutralMetallicRoughness,
    // (255,255,255,255). AO = 1.0 (no occlusion). Same gate convention.
    TextureHandle FullOcclusion);

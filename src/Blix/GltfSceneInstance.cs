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

        // Memoise the OverrideMaterial result per source ref so each unique
        // GltfMaterial gets one rewritten record reused across every primitive
        // that references it. Without memoisation, two primitives with the
        // same source would each compute (potentially diverging) overrides
        // and the materialCache below would key on the original ref but get
        // mismatched values.
        var overrideCache = options.OverrideMaterial is null
            ? null
            : new Dictionary<GltfMaterial, GltfMaterial>(ReferenceEqualityComparer.Instance);
        var textureCache = new Dictionary<GltfTexture, TextureHandle>(ReferenceEqualityComparer.Instance);
        // Tracks textures currently being uploaded but whose handle hasn't
        // landed yet. Subsequent bindings of the same GltfTexture attach
        // their `apply` callback to the existing upload instead of starting
        // a duplicate one (which would also fail since the CPU mip bytes
        // were released after the first enqueue). The dictionary value is
        // a mutable list because more than two materials can share one
        // texture (e.g. a normal map reused by several wall variants).
        var pendingTextures = new Dictionary<GltfTexture, List<Action<TextureHandle>>>(
            ReferenceEqualityComparer.Instance);
        var materialCache = new Dictionary<GltfMaterial, Material>(ReferenceEqualityComparer.Instance);
        var submeshes = new SubmeshInstance[model.Primitives.Length];

        var worldMin = new Vector3(float.PositiveInfinity);
        var worldMax = new Vector3(float.NegativeInfinity);
        var fallbackPipelineWarnings = new HashSet<string>();

        for (var i = 0; i < model.Primitives.Length; i++)
        {
            var prim = model.Primitives[i];
            // Apply OverrideMaterial once per unique source. The cache key is
            // the ORIGINAL source ref; subsequent lookups for the same source
            // return the already-rewritten record so all later code paths see
            // a consistent alphaMode + doubleSided + cutoff per primitive.
            var sourceMaterial = prim.Material;
            if (sourceMaterial is not null && overrideCache is not null)
            {
                if (!overrideCache.TryGetValue(sourceMaterial, out var rewritten))
                {
                    rewritten = options.OverrideMaterial!(sourceMaterial);
                    overrideCache[sourceMaterial] = rewritten;
                }
                sourceMaterial = rewritten;
            }
            var meshName = $"{options.Prefix}.mesh.{prim.Mesh.Name}";
            var vb = device.CreateVertexBuffer(
                new VertexBufferData(
                    new VertexBufferDescription(prim.Mesh.Layout, prim.Mesh.VertexCount, GraphicsBufferUsage.Static),
                    prim.Mesh.VertexBytes),
                name: $"{meshName}.vb");
            var ib = prim.Mesh.IndexFormat == IndexFormat.UInt32
                ? device.CreateIndexBuffer(prim.Mesh.Indices32!, name: $"{meshName}.ib")
                : device.CreateIndexBuffer(prim.Mesh.Indices, name: $"{meshName}.ib");
            var mesh = new Mesh(meshName, vb, ib, prim.Mesh.IndexCount, prim.Mesh.Bounds);

            var alphaMode = sourceMaterial?.AlphaMode ?? GltfAlphaMode.Opaque;
            var doubleSided = sourceMaterial?.DoubleSided ?? false;
            var pipeline = SelectPipeline(options, alphaMode, doubleSided, fallbackPipelineWarnings);

            Material? material;
            if (sourceMaterial is { } gm)
            {
                // Cache by ORIGINAL prim.Material reference so primitives that
                // share a source (after override) share one runtime Material.
                var cacheKey = prim.Material!;
                if (!materialCache.TryGetValue(cacheKey, out material))
                {
                    material = BuildMaterial(device, gm, pipeline, options, textureCache, pendingTextures);
                    options.OnMaterialBuilt?.Invoke(material, gm);
                    materialCache[cacheKey] = material;
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
                material = BuildMaterial(device, defaultKey, pipeline, options, textureCache, pendingTextures);
                options.OnMaterialBuilt?.Invoke(material, null);
            }

            submeshes[i] = new SubmeshInstance(
                Name: prim.Mesh.Name,
                Mesh: mesh,
                Material: material,
                Source: sourceMaterial,
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

        // Diagnostic dump: surface per-material classification so we can
        // spot misbehaviour like an opaque wall declared alphaMode=BLEND
        // (renders see-through), or a textured material whose albedo
        // import dropped to null (renders as the default white pixel).
        // Gated behind an env var so the regular Walkthrough demo's
        // console doesn't get spammed.
        if (Environment.GetEnvironmentVariable("BLIX_DUMP_MATERIALS") != null)
        {
            Console.WriteLine($"[GltfSceneInstance:{options.Prefix}] {materialCache.Count} materials, {submeshes.Length} submeshes:");
            Console.WriteLine($"  {"name",-50} {"alpha",-7} {"2-side",-7} {"baseFac.a",-10} {"alb",-3} {"nrm",-3} {"mr",-3} {"ao",-3} {"em",-3}");
            foreach (var (gm, _) in materialCache.OrderBy(kv => kv.Key.Name))
            {
                Console.WriteLine(
                    $"  {Trunc(gm.Name, 50),-50} {gm.AlphaMode,-7} {gm.DoubleSided,-7} " +
                    $"{gm.BaseColorFactor.W,-10:0.000} " +
                    $"{(gm.BaseColorTexture is null ? "-" : "Y"),-3} " +
                    $"{(gm.NormalTexture is null ? "-" : "Y"),-3} " +
                    $"{(gm.MetallicRoughnessTexture is null ? "-" : "Y"),-3} " +
                    $"{(gm.OcclusionTexture is null ? "-" : "Y"),-3} " +
                    $"{(gm.EmissiveTexture is null ? "-" : "Y"),-3}");
            }
            var byMode = materialCache.Keys.GroupBy(m => m.AlphaMode)
                .OrderBy(g => g.Key)
                .Select(g => $"{g.Key}={g.Count()}");
            Console.WriteLine($"  By alphaMode: {string.Join(", ", byMode)}");
        }

        return new GltfSceneInstance(submeshes, bounds, materialSet);
    }

    private static string Trunc(string s, int n) => s.Length <= n ? s : s.Substring(0, n - 1) + "~";

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
        GltfSceneOptions options, Dictionary<GltfTexture, TextureHandle> textureCache,
        Dictionary<GltfTexture, List<Action<TextureHandle>>> pendingTextures)
    {
        var material = new Material($"{options.Prefix}.mat.{gm.Name}", pipeline);
        material.SetUniform("uBaseColorFactor", new Vector4Uniform(gm.BaseColorFactor));
        material.SetUniform("uMetallicFactor", new FloatUniform(gm.MetallicFactor));
        material.SetUniform("uRoughnessFactor", new FloatUniform(gm.RoughnessFactor));
        // Multiply factor by strength so the lit shader only needs one uniform.
        // KHR_materials_emissive_strength defaults to 1.0 when the extension
        // isn't present, so this stays correct for vanilla glTF.
        material.SetUniform("uEmissiveFactor", new Vector3Uniform(gm.EmissiveFactor * gm.EmissiveStrength));
        material.SetUniform("uOcclusionStrength", new FloatUniform(gm.OcclusionStrength));
        // Effective cutoff: glTF spec says alphaCutoff is only used when
        // alphaMode == MASK. Force 0 for OPAQUE + BLEND so the lit shader's
        // `if (uAlphaCutoff > 0 && a < cutoff) discard;` never fires --
        // OPAQUE materials with low-alpha textures (Modern Sponza authors
        // some that way) would otherwise discard every fragment and the
        // scene renders empty.
        var effectiveCutoff = gm.AlphaMode == GltfAlphaMode.Mask ? gm.AlphaCutoff : 0.0f;
        material.SetUniform("uAlphaCutoff", new FloatUniform(effectiveCutoff));
        // Existing lit shader uses uNormalScale as a "has normal map" gate.
        material.SetUniform("uNormalScale", new FloatUniform(gm.NormalTexture is null ? 0.0f : 1.0f));
        material.SetUniform("uHasMetallicMap", new FloatUniform(gm.MetallicRoughnessTexture is null ? 0.0f : 1.0f));
        material.SetUniform("uHasOcclusionMap", new FloatUniform(gm.OcclusionTexture is null ? 0.0f : 1.0f));
        // Lit shader uses this to negate the geometric normal on back-faces
        // so doubleSided foliage (leaves, fabric) lights correctly from both
        // sides. The derivative-based cotangentFrame naturally inherits the
        // flip via its cross(dp, N) terms, so no separate TBN handedness
        // adjustment is needed.
        material.SetUniform("uDoubleSided", new FloatUniform(gm.DoubleSided ? 1.0f : 0.0f));

        // Bind defaults FIRST. When an uploader is set, the real textures
        // pop in over the next few frames as Drain runs (see ResourceUploader);
        // until then the lit shader samples the default which keeps the
        // material valid for rendering. When no uploader, uploads happen
        // synchronously here and the final SetTexture wins immediately.
        material.SetTexture("uAlbedo", options.Defaults.WhitePixel, 0);
        material.SetTexture("uNormalMap", options.Defaults.FlatNormal, 1);
        material.SetTexture("uMetallicRoughness", options.Defaults.NeutralMetallicRoughness, 2);
        material.SetTexture("uEmissive", options.Defaults.WhitePixel, 3);
        material.SetTexture("uOcclusion", options.Defaults.FullOcclusion, 15);

        BindMaterialTexture(device, options, textureCache, pendingTextures, gm.BaseColorTexture, "albedo",
            real => material.SetTexture("uAlbedo", real, 0));
        BindMaterialTexture(device, options, textureCache, pendingTextures, gm.NormalTexture, "normal",
            real => material.SetTexture("uNormalMap", real, 1));
        BindMaterialTexture(device, options, textureCache, pendingTextures, gm.MetallicRoughnessTexture, "mr",
            real => material.SetTexture("uMetallicRoughness", real, 2));
        BindMaterialTexture(device, options, textureCache, pendingTextures, gm.EmissiveTexture, "emissive",
            real => material.SetTexture("uEmissive", real, 3));
        BindMaterialTexture(device, options, textureCache, pendingTextures, gm.OcclusionTexture, "ao",
            real => material.SetTexture("uOcclusion", real, 15));

        return material;
    }

    // Per-slot binder. If the source is null, leaves the default placeholder
    // alone (already bound by the caller). If the source has been uploaded
    // before, binds the cached handle immediately. Otherwise either uploads
    // synchronously (no uploader provided) or enqueues for deferred upload
    // (uploader present), invoking apply with the real handle when ready.
    private static void BindMaterialTexture(
        IGraphicsDevice device, GltfSceneOptions options,
        Dictionary<GltfTexture, TextureHandle> textureCache,
        Dictionary<GltfTexture, List<Action<TextureHandle>>> pendingTextures,
        GltfTexture? source, string slotHint,
        Action<TextureHandle> apply)
    {
        if (source is null) return;
        if (textureCache.TryGetValue(source, out var cached))
        {
            apply(cached);
            return;
        }
        // An upload is already in flight for this source -- piggyback on
        // it instead of starting another. Happens when the same image
        // backs more than one glTF material (e.g. a normal map reused
        // across wall variants). Without this, the second BuildMaterial
        // call would crash on the released CPU bytes.
        if (pendingTextures.TryGetValue(source, out var pendingList))
        {
            pendingList.Add(apply);
            return;
        }

        // sRGB-aware format selection. glTF spec: BaseColor + Emissive are
        // sRGB-encoded; Normal + MetallicRoughness + Occlusion are LINEAR.
        // We promote Rgba8 -> Rgba8Srgb for the sRGB slots so GL does sRGB->
        // linear conversion BEFORE filtering (correct), instead of the shader
        // pow(c, 2.2)'ing the already-filtered gamma-space average (wrong on
        // high-contrast textures, producing too-dark mip transitions).
        // Compressed BC7 textures already encode the sRGB flag in their own
        // format enum (Bc7Srgb vs Bc7Unorm) so we pass those through.
        var isSrgbSlot = slotHint == "albedo" || slotHint == "emissive";
        var uploadFormat = source.Format == TextureFormat.Rgba8 && isSrgbSlot
            ? TextureFormat.Rgba8Srgb
            : source.Format;

        if (options.Uploader is { } uploader)
        {
            var subscribers = new List<Action<TextureHandle>> { apply };
            pendingTextures[source] = subscribers;
            var name = $"{options.Prefix}.{slotHint}.{source.Name}";
            void OnUploaded(TextureHandle h)
            {
                textureCache[source] = h;
                pendingTextures.Remove(source);
                foreach (var sub in subscribers) sub(h);
            }

            if (source.LazyHandle is { } lazy)
            {
                // Lazy path: bytes stay on disk until the uploader's pump
                // hits each mip. Captures `lazy` so the mip reads happen
                // at process-time, not enqueue-time.
                uploader.EnqueueLazy(
                    uploadFormat, source.Width, source.Height, source.MipCount,
                    mipReader: level => BlixTexReader.ReadMip(lazy, level),
                    options.Sampler, name, OnUploaded);
                return;
            }

            // Eager path: source.MipBytes is populated (PNG-decode case).
            // After enqueue we drop OUR reference; the uploader's work
            // items hold theirs only until each mip is processed.
            var mipBytes = source.MipBytes
                ?? throw new InvalidOperationException(
                    $"GltfTexture '{source.Name}' has neither lazy handle nor CPU mip bytes.");
            uploader.Enqueue(
                uploadFormat, source.Width, source.Height, mipBytes, options.Sampler,
                name, OnUploaded);
            source.ReleaseCpuMipBytes();
            return;
        }

        // Synchronous path (no uploader). Lazy textures need to materialise
        // bytes here too.
        IReadOnlyList<byte[]> bytesForSync;
        if (source.LazyHandle is { } syncLazy)
        {
            var loaded = new byte[syncLazy.MipCount][];
            for (var i = 0; i < syncLazy.MipCount; i++) loaded[i] = BlixTexReader.ReadMip(syncLazy, i);
            bytesForSync = loaded;
        }
        else
        {
            bytesForSync = source.MipBytes
                ?? throw new InvalidOperationException(
                    $"GltfTexture '{source.Name}' has neither lazy handle nor CPU mip bytes.");
        }
        var handle = device.CreateTexture2DMipped(
            new TextureDescription(source.Width, source.Height, uploadFormat, options.Sampler),
            bytesForSync,
            name: $"{options.Prefix}.{slotHint}.{source.Name}");
        textureCache[source] = handle;
        source.ReleaseCpuMipBytes();
        apply(handle);
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
public sealed record GltfSceneOptions
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

    // Demo hook: rewrite a source GltfMaterial BEFORE pipeline selection and
    // BuildMaterial. Use this to compensate for upstream authoring quirks like
    // foliage tagged alphaMode=BLEND that the runtime should treat as MASK
    // (binary alpha + depth-write so leaves z-sort against themselves without
    // a back-to-front pass). Returns the (possibly rewritten) material. Called
    // at most once per UNIQUE source GltfMaterial -- if multiple primitives
    // reference the same source, the same overridden record is reused so
    // materialCache keying stays stable. Return the input unchanged to opt out.
    public Func<GltfMaterial, GltfMaterial>? OverrideMaterial { get; init; }

    // When set, per-material texture uploads are queued through the uploader
    // instead of running synchronously inside Build. Materials get the
    // default placeholder texture for each slot up front; the uploader
    // swaps in the real texture over the next few frames as Drain processes
    // its queue. Build still returns synchronously with a fully-constructed
    // scene graph -- only the GL upload work is deferred, not the structural
    // work. When null, all uploads happen inline (legacy behaviour).
    public ResourceUploader? Uploader { get; init; }

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

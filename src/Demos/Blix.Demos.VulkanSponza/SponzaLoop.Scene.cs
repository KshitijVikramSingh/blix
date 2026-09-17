using System.Numerics;
using System.Runtime.InteropServices;
using Blix;
using Blix.Assets;
using Blix.Core;
using Blix.Diagnostics;
using Blix.Geometry;
using Blix.Graphics;
using Blix.Graphics.Images;
using Blix.Graphics.Vulkan;
using Blix.Runtime.Silk;

namespace Blix.Demos.VulkanSponza;

internal sealed partial class SponzaLoop
{
    // Background, parallel: each pack gets its own importer (no shared state) and
    // is parsed to CPU geometry/material data. flipTextureV matches the cook's
    // --flip-v (Intel Sponza is bottom-up); includeTangents forwards the glTF
    // TANGENT for the lit TBN. Returns models in pack order (main first).
    private static List<(string Name, GltfModel Model)> ParsePacksParallel(
        List<(string Name, string Path, string AssetId)> packs)
    {
        var parsed = new (string Name, GltfModel Model)?[packs.Count];
        System.Threading.Tasks.Parallel.For(0, packs.Count, i =>
        {
            var p = packs[i];
            try
            {
                var model = new GltfStaticImporter().Import(
                    new AssetImportContext(AssetId.Parse(p.AssetId), p.Path, includeTangents: true));
                parsed[i] = (p.Name, model);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[VulkanSponza] {p.Name} pack failed to parse: {ex.Message}");
            }
        });
        return parsed.Where(r => r.HasValue).Select(r => r!.Value).ToList();
    }

    private void TryFinishLoad()
    {
        if (sceneLoaded) return;
        if (meshLoad.IsFaulted)
        {
            Console.WriteLine($"[VulkanSponza] load failed: {meshLoad.Fault?.Message}");
            sceneLoaded = true; // give up → render the empty clear
            return;
        }

        // Stage primitives within the per-frame budget; false while still parsing
        // off-thread or with more to stage. True once every primitive is staged.
        if (!meshLoad.Drain(LoadBudgetMs, StageDrawable)) return;

        // Everything staged → build the shared buffers + finish.
        ConsolidateBuffers();
        RegisterSelectables();
        Console.WriteLine($"[VulkanSponza] total draws: {opaqueDrawables.Count} opaque/mask, {blendDrawables.Count} blend.");
        LogPrimitiveSizeHistogram();
        UpdateCamera();
        sceneLoaded = true;
    }

    // Build the pickable-primitive set (one entry per drawable, opaque + blend)
    // and register it with the debug system, so click-to-pick + the Selection
    // panel work. Paths are session-stable (bucket + index); nothing persists.
    private void RegisterSelectables()
    {
        var items = new List<(string Path, string Name, Bounds3 Bounds, int LodLevels, float MaxError)>(
            opaqueDrawables.Count + blendDrawables.Count);
        void Add(string bucket, List<Drawable> list)
        {
            for (var i = 0; i < list.Count; i++)
            {
                var d = list[i];
                var maxErr = d.LodErrors.Length > 0 ? d.LodErrors[^1] : 0f;
                items.Add(($"scene/{bucket}/{i}", d.Name, d.Bounds, d.LodIndexCounts.Length, maxErr));
            }
        }
        Add("opaque", opaqueDrawables);
        Add("blend", blendDrawables);
        sceneSelection.Rebuild(items);
        debugSystem?.Register(sceneSelection);

        // Per-drawable LOD margins, default 1.0 (= use the global budget as-is).
        opaqueLodMargins = new float[opaqueDrawables.Count];
        blendLodMargins = new float[blendDrawables.Count];
        System.Array.Fill(opaqueLodMargins, 1f);
        System.Array.Fill(blendLodMargins, 1f);
    }

    // Per-primitive LOD0 triangle-size distribution across all opaque drawables.
    // Sponza's geometry is dominated by a few very large primitives (whole floor
    // slabs / walls), which is exactly what makes per-prim center-distance LOD
    // coarse — one level for a huge prim. This histogram sets the threshold for
    // cook-time spatial splitting (only split prims above the fat bucket) and
    // shows how concentrated the triangle budget is in the tail.
    private void LogPrimitiveSizeHistogram()
    {
        // Buckets by LOD0 triangle count. Tracks prim count + summed tris per
        // bucket so we can see where the budget actually lives (count vs mass).
        var edges = new[] { 1_000, 5_000, 20_000, 50_000, int.MaxValue };
        var labels = new[] { "<1k", "1-5k", "5-20k", "20-50k", ">50k" };
        var counts = new int[edges.Length];
        var tris = new long[edges.Length];
        long totalTris = 0;
        var fattest = new List<int>();
        foreach (var d in opaqueDrawables)
        {
            var t = d.LodIndexCounts[0] / 3;
            totalTris += t;
            fattest.Add(t);
            for (var i = 0; i < edges.Length; i++)
            {
                if (t < edges[i]) { counts[i]++; tris[i] += t; break; }
            }
        }
        fattest.Sort((a, b) => b.CompareTo(a));
        var top = string.Join("/", fattest.Take(5).Select(t => $"{t / 1000.0:0.0}k"));
        Console.WriteLine($"[VulkanSponza] opaque LOD0 tris: {totalTris / 1_000_000.0:0.00}M across {opaqueDrawables.Count} prims; top5={top}");
        for (var i = 0; i < edges.Length; i++)
        {
            var pct = totalTris > 0 ? 100.0 * tris[i] / totalTris : 0;
            Console.WriteLine($"[VulkanSponza]   {labels[i],-7}: {counts[i],4} prims, {tris[i] / 1_000_000.0:0.00}M tris ({pct:0}% of budget)");
        }
    }

    // Stage one primitive (or spatial-split chunk): resolve/upload its material
    // and queue its geometry. Each carries its own cooked LOD index chain and
    // selects a level by screen-space error; geometry is concatenated into the
    // shared VB/IB by ConsolidateBuffers, so a draw is just a sub-range.
    // Materials are cached by GltfMaterial so primitives sharing a material reuse
    // one handle + descriptor set. Called incrementally (time-sliced) during the
    // streaming load — the material's texture read+upload is the bulk of the cost.
    private void StageDrawable(GltfPrimitive prim)
    {
        {
            var pm = prim.Material;
            var mesh = prim.Mesh;
            // Glass/transmissive routes to the blend pipeline regardless of its
            // declared alpha mode (see EffectiveTransmission).
            // Only genuinely transmissive materials (glass) need alpha blending.
            // Cutout foliage authored as BLEND (the cypress, etc.) is treated as
            // MASK so it lands in the opaque bucket → written by the depth
            // pre-pass → early-Z. That kills the layered double-sided foliage
            // overdraw that makes the hero tree fragment-bound at Retina res.
            // Its alphaCutoff (set in BuildMaterial) drives the shader discard.
            var isBlend = EffectiveTransmission(pm) > 0f;
            var material = GetMaterial(pm, out var albedo, out var alphaCutoff, out var baseColorAlpha);
            var alphaMode = isBlend ? GltfAlphaMode.Blend
                : alphaCutoff > 0f ? GltfAlphaMode.Mask
                : (pm?.AlphaMode ?? GltfAlphaMode.Opaque);
            var pipeline = PickPipeline(alphaMode, pm?.DoubleSided ?? false);

            sharedLayout = mesh.Layout; // uniform across packs (all cooked --tangents)
            // Cooked LOD chain, or a single level from the runtime-import indices.
            var lods = mesh.Lods ?? new[]
            {
                mesh.Indices32 is { } i32 ? new MeshLod(null, i32) : new MeshLod(mesh.Indices, null)
            };
            // No GPU buffers yet — stage the CPU bytes; ConsolidateBuffers packs
            // every pack into the shared VB/IB once loading is done.
            staging.Add(new DrawableStaging(
                mesh.VertexBytes, mesh.VertexCount, lods, material, pipeline, mesh.Bounds,
                albedo, alphaCutoff, baseColorAlpha,
                new[] { new ShaderTextureBinding("uAlbedo", albedo, Slot: 0) }, isBlend,
                string.IsNullOrEmpty(mesh.Name) ? "primitive" : mesh.Name));
        }
    }

    // Bundle every staged primitive into the shared buffers, then compose the
    // draw lists/groups over the result. The engine's MeshBundler does the
    // geometry packing (one shared VB + per-width u16/u32 IBs, each primitive a
    // BaseVertex + per-LOD firstIndex sub-range — indices stay primitive-local,
    // vkCmdDrawIndexed's vertexOffset rebases them); the game keeps the
    // material/pipeline binding, the opaque/blend split, and the draw groups.
    // Inputs are pre-sorted by (pipeline, material, index-width) and opaque-then-
    // blend, so each (pipeline, material, width) run is contiguous — one indirect
    // call per group, the two buckets contiguous.
    private void ConsolidateBuffers()
    {
        if (staging.Count == 0) return;

        static bool StagingIsU32(DrawableStaging s) => s.Lods[0].Indices32 is not null;
        static IEnumerable<DrawableStaging> Grouped(IEnumerable<DrawableStaging> src) => src
            .OrderBy(s => s.Pipeline.Id).ThenBy(s => s.Material.Id).ThenBy(s => StagingIsU32(s) ? 1 : 0);
        // OrderBy is stable, so prims keep their relative order within a group.
        var ordered = Grouped(staging.Where(s => !s.IsBlend))
            .Concat(Grouped(staging.Where(s => s.IsBlend)))
            .ToList();

        var bundle = Blix.Render.MeshBundler.Bundle(
            ordered.Select(s => new Blix.Render.MeshGeometryInput(
                s.VertexBytes, s.VertexCount, s.Lods, s.Bounds)).ToList(),
            sharedLayout, vk, "sponza.shared");
        sharedVb = bundle.Vertices;
        sharedIbU16 = bundle.Indices16;
        sharedIbU32 = bundle.Indices32;

        // Attach material/pipeline/etc. to each bundled geometry sub-range; split
        // back into the opaque + blend buckets (bundle order == ordered order).
        for (var i = 0; i < ordered.Count; i++)
        {
            var s = ordered[i];
            var bm = bundle.Meshes[i];
            (s.IsBlend ? blendDrawables : opaqueDrawables).Add(new Drawable(
                bm.IndicesAreU32, bm.BaseVertex, bm.LodFirstIndex, bm.LodIndexCounts, bm.LodErrors,
                s.Material, s.Pipeline, bm.Bounds, s.Albedo, s.AlphaCutoff, s.BaseColorAlpha,
                s.ShadowAlbedoBinding, s.Name));
        }

        // Contiguous (pipeline, material, width) groups over the sorted lists. A
        // material is uniformly mask-or-not, so the group is too (lets the depth
        // pre-pass pick its mask/opaque pipeline + material bind per group).
        static void BuildGroups(List<Drawable> list, List<OpaqueGroup> into)
        {
            into.Clear();
            for (var i = 0; i < list.Count;)
            {
                var d = list[i];
                var j = i + 1;
                while (j < list.Count
                    && list[j].Pipeline == d.Pipeline
                    && list[j].Material == d.Material
                    && list[j].IndicesAreU32 == d.IndicesAreU32) j++;
                into.Add(new OpaqueGroup(d.Pipeline, d.Material, d.IndicesAreU32, d.AlphaCutoff > 0f, i, j - i));
                i = j;
            }
        }
        BuildGroups(opaqueDrawables, opaqueGroups);
        BuildGroups(blendDrawables, blendGroups);

        // One indirect command per drawable, refilled each frame (camera opaque +
        // one per shadow cascade + blend). indirectScratch is sized for the
        // largest list (opaque) and reused for the smaller fills.
        opaqueIndirect = vk.CreateIndirectBuffer(opaqueDrawables.Count, "sponza.opaque.indirect");
        for (var c = 0; c < CascadeCount; c++)
            cascadeIndirect[c] = vk.CreateIndirectBuffer(opaqueDrawables.Count, $"sponza.cascade{c}.indirect");
        if (blendDrawables.Count > 0)
            blendIndirect = vk.CreateIndirectBuffer(blendDrawables.Count, "sponza.blend.indirect");
        indirectScratch = new byte[Math.Max(opaqueDrawables.Count, blendDrawables.Count) * VulkanGraphicsDevice.IndirectCommandStride];
        staging.Clear();
        Console.WriteLine($"[VulkanSponza] bundled geometry: 1 VB ({bundle.VertexCount} verts); {opaqueDrawables.Count} opaque + {blendDrawables.Count} blend draws; {opaqueGroups.Count} opaque indirect groups.");
    }

    private MaterialHandle GetMaterial(GltfMaterial? gm, out TextureHandle albedo, out float alphaCutoff, out float baseColorAlpha)
    {
        // An unidentifiable material is never cached — an identity nobody can reproduce is not one,
        // and a shared empty key would merge two unrelated materials into whichever built first.
        if (gm is not null && gm.ResourceId.Length > 0 && materialCache.TryGetValue(gm.ResourceId, out var c))
        {
            (albedo, alphaCutoff, baseColorAlpha) = (c.Albedo, c.Cutoff, c.Alpha);
            return c.Mat;
        }
        if (gm is null && noMaterialCache is { } nc)
        {
            (albedo, alphaCutoff, baseColorAlpha) = (nc.Albedo, nc.Cutoff, nc.Alpha);
            return nc.Mat;
        }
        var mat = BuildMaterial(gm, out albedo, out alphaCutoff, out baseColorAlpha);
        var entry = (mat, albedo, alphaCutoff, baseColorAlpha);
        if (gm is not null && gm.ResourceId.Length > 0) materialCache[gm.ResourceId] = entry;
        else if (gm is null) noMaterialCache = entry;
        return mat;
    }

    // Transmission for a material, with a demo-level fallback. Intel Sponza
    // authors its glass as an opaque, perfectly-smooth dielectric with NO
    // KHR_materials_transmission — so it renders near-black (4% head-on
    // Fresnel) with only grazing reflections. The asset is missing the
    // metadata, so we tag known glass materials by name and let the generic
    // Fresnel-glass path in lit.frag take over. (Same spirit as the metallic-
    // threshold patch: a demo-level conformance fix over an asset quirk, not a
    // renderer default.) Real assets that ship the extension use it directly.
    private static float EffectiveTransmission(GltfMaterial? m)
    {
        if (m is null) return 0f;
        if (m.TransmissionFactor > 0f) return m.TransmissionFactor;
        return m.Name.ToLowerInvariant().Contains("glass") ? 1.0f : 0f;
    }

    // One engine Material per glTF material. BaseColorFactor / EmissiveFactor /
    // MaterialParams (alphaCutoff, normalScale, roughness, metallic) UBO + the
    // five channel textures (defaults when a channel is absent).
    private MaterialHandle BuildMaterial(
        GltfMaterial? gm, out TextureHandle albedo, out float alphaCutoff, out float baseColorAlpha)
    {
        // Engine resolves the five channel textures (read/decode/upload/dedup/
        // stream + glTF-default fallbacks + per-slot sRGB policy); the game keeps
        // the material UBO write + descriptor binding below.
        var tex = textureLoader.Load(gm);
        albedo = tex.Albedo;

        var baseColorFactor = gm?.BaseColorFactor ?? Vector4.One;
        var emissiveFactor = gm is null ? Vector3.Zero : gm.EmissiveFactor;
        var emissiveStrength = gm?.EmissiveStrength ?? 1.0f;
        // Cutout cutoff. MASK uses its authored cutoff. Non-glass BLEND (foliage
        // authored as blend) is treated as cutout at 0.5 so it can depth-write +
        // early-Z instead of overdrawing. Glass (transmissive) keeps 0 → no
        // discard, true alpha blend.
        alphaCutoff = EffectiveTransmission(gm) > 0f ? 0.0f
            : gm?.AlphaMode == GltfAlphaMode.Mask ? (gm?.AlphaCutoff ?? 0.5f)
            : gm?.AlphaMode == GltfAlphaMode.Blend ? 0.5f
            : 0.0f;
        baseColorAlpha = baseColorFactor.W;
        var normalScale = 1.0f;
        var roughness = gm?.RoughnessFactor ?? 0.8f;
        var metallic = gm?.MetallicFactor ?? 0.0f;
        var transmission = EffectiveTransmission(gm);

        return vk.CreateMaterial(litProgram, name: "sponza.material")
            .SetUniform(binding: 0, "uBaseColorFactor", baseColorFactor)
            .SetUniform(binding: 0, "uEmissiveFactor",
                new Vector4(emissiveFactor.X, emissiveFactor.Y, emissiveFactor.Z, emissiveStrength))
            .SetUniform(binding: 0, "uMaterialParams",
                new Vector4(alphaCutoff, normalScale, roughness, metallic))
            .SetUniform(binding: 0, "uMaterialParams2",
                new Vector4(transmission, 0f, 0f, 0f))
            .SetTexture(binding: 1, tex.Albedo)
            .SetTexture(binding: 2, tex.Normal)
            .SetTexture(binding: 3, tex.Emissive)
            .SetTexture(binding: 4, tex.MetallicRoughness)
            .SetTexture(binding: 5, tex.Occlusion)
            .Handle;
    }

    private PipelineHandle PickPipeline(GltfAlphaMode mode, bool doubleSided) =>
        (mode, doubleSided) switch
        {
            (GltfAlphaMode.Blend, true)  => blendDoubleSidedPipeline,
            (GltfAlphaMode.Blend, false) => blendSolidPipeline,
            (_, true)                    => opaqueDoubleSidedPipeline,
            _                            => opaqueSolidPipeline,
        };
}

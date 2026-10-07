using System.Numerics;
using System.Runtime.InteropServices;
using Blix;
using Blix.Assets;
using Blix.Core;
using Blix.Diagnostics;
using Blix.Geometry;
using Blix.Graphics;
using Blix.Graphics.Images;
using Blix.Runtime.Silk;

namespace Blix.Demos.VulkanSponza;

internal sealed partial class SponzaLoop
{
    // Background, parallel: each cooked pack is read to CPU geometry/material data, with tangents for the
    // lit TBN: each unique primitive once, in mesh space, with every world the scene places it at (or,
    // under --flatten, every placement baked into its own primitive). Returns pack order (main first).
    private static List<(string Name, PlacedPrimitive[] Primitives)> ParsePacksParallel(
        List<(string Name, string Path)> packs, bool flatten)
    {
        var parsed = new (string Name, PlacedPrimitive[] Primitives)?[packs.Count];
        System.Threading.Tasks.Parallel.For(0, packs.Count, i =>
        {
            var p = packs[i];
            try
            {
                var model = ModelData.Load(p.Path, new ModelNeeds(Tangents: true, Skinned: false));
                parsed[i] = (p.Name, flatten ? Flattened(model, i) : Placed(model, i));
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[VulkanSponza] {p.Name} pack failed to parse: {ex.Message}");
            }
        });
        return parsed.Where(r => r.HasValue).Select(r => r!.Value).ToList();
    }

    // The old shape: every placement baked into world-space vertices, each placed once at identity.
    private static PlacedPrimitive[] Flattened(ModelData model, int pack) => model.Flattened()
        .Select((x, i) => new PlacedPrimitive(x.Primitive, new[] { Matrix4x4.Identity }, new[] { new PlacementInstance(pack, -1, i) }))
        .ToArray();

    // Each mesh's primitives once, with every world a shown node draws the mesh at (DrawnWorlds: the
    // node's world, or each of its instances'), in the order the file first places the mesh. A skinned
    // mesh is drawn at its bind pose, unmoved, as Flattened draws it.
    // Each world keeps which instance it is (pack, node, which of the node's draws): merging worlds per mesh is
    // how the scene draws a mesh once for all its placements, and it must not also merge their identities.
    private static PlacedPrimitive[] Placed(ModelData model, int pack)
    {
        var worlds = new Dictionary<int, List<Matrix4x4>>();
        var instances = new Dictionary<int, List<PlacementInstance>>();
        var order = new List<int>();
        for (var n = 0; n < model.Nodes.Count; n++)
        {
            var mesh = model.Nodes[n].MeshIndex;
            if (mesh < 0 || !model.IsShown(n)) continue;
            if (!worlds.TryGetValue(mesh, out var list))
            {
                worlds[mesh] = list = new List<Matrix4x4>();
                instances[mesh] = new List<PlacementInstance>();
                order.Add(mesh);
            }

            var drawn = model.Meshes[mesh].Skinned ? new[] { Matrix4x4.Identity } : model.DrawnWorlds(n).ToArray();
            for (var d = 0; d < drawn.Length; d++)
            {
                list.Add(drawn[d]);
                instances[mesh].Add(new PlacementInstance(pack, n, d));
            }
        }

        return order
            .SelectMany(mesh => model.Meshes[mesh].Primitives.Select(p => new PlacedPrimitive(p, worlds[mesh].ToArray(), instances[mesh].ToArray())))
            .ToArray();
    }

    // A mesh-space box carried to world space: the AABB of its eight transformed corners.
    private static Bounds3 WorldBounds(in Bounds3 mesh, in Matrix4x4 world)
    {
        if (world.IsIdentity) return mesh;
        var min = new Vector3(float.PositiveInfinity);
        var max = new Vector3(float.NegativeInfinity);
        for (var c = 0; c < 8; c++)
        {
            var corner = Vector3.Transform(new Vector3(
                (c & 1) == 0 ? mesh.Min.X : mesh.Max.X,
                (c & 2) == 0 ? mesh.Min.Y : mesh.Max.Y,
                (c & 4) == 0 ? mesh.Min.Z : mesh.Max.Z), world);
            min = Vector3.Min(min, corner);
            max = Vector3.Max(max, corner);
        }

        return new Bounds3(min, max);
    }

    private void FitSceneVolumeToDrawables()
    {
        var all = opaquePlacements.Concat(blendPlacements).ToList();
        if (all.Count == 0) return;
        var min = all[0].Bounds.Min;
        var max = all[0].Bounds.Max;
        foreach (var p in all)
        {
            min = Vector3.Min(min, p.Bounds.Min);
            max = Vector3.Max(max, p.Bounds.Max);
        }

        skyVolumeMin = min;
        skyVolumeSpan = max - min;
        Console.WriteLine($"[VulkanSponza] scene volume (no .blixsky): {min} .. {max}");
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
        // With no baked sky volume, the scene's extent is still the truth caster culling sweeps to and the
        // orbit frames: everything that can cast or receive is a drawable, so their union is that volume.
        // Without this both read a zero box, and offscreen casters lost their shadows.
        if (!skyVolumeLoaded) FitSceneVolumeToDrawables();
        RegisterSelectables();
        BuildCullBuffers();
        Console.WriteLine(
            $"[VulkanSponza] total: {opaqueDrawables.Count} opaque/mask + {blendDrawables.Count} blend unique primitives, "
            + $"placed {opaquePlacements.Count} + {blendPlacements.Count} times{(flatten ? " (--flatten: every placement baked)" : "")}.");
        LogPrimitiveSizeHistogram();
        UpdateCamera();
        sceneLoaded = true;
    }

    // Build the pickable set (one entry per placement, opaque + blend) and register it with the debug
    // system, so click-to-pick + the Selection panel work. Paths are session-stable (bucket + placement
    // index); nothing persists. Per-placement LOD state is sized here too.
    private void RegisterSelectables()
    {
        var items = new List<(string Path, string Name, Bounds3 Bounds, DebugPickGeometry Geometry, int LodLevels, float MaxError)>(
            opaquePlacements.Count + blendPlacements.Count);
        void Add(string bucket, List<Drawable> drawables, List<Placement> placements)
        {
            for (var i = 0; i < placements.Count; i++)
            {
                var p = placements[i];
                var d = drawables[p.Drawable];
                var maxErr = d.LodErrors.Length > 0 ? d.LodErrors[^1] : 0f;
                // The full-detail range out of the shared buffers, at this placement's world: what the
                // pick pass draws to know whether this placement is under the cursor.
                var geometry = new DebugPickGeometry(
                    sharedVb, sharedLayout, SharedIb(d), d.LodFirstIndex[0], d.LodIndexCounts[0], d.BaseVertex,
                    sceneTransforms[p.Transform]);
                items.Add(($"scene/{bucket}/{i}", d.Name, p.Bounds, geometry, d.LodIndexCounts.Length, maxErr));
            }
        }
        Add("opaque", opaqueDrawables, opaquePlacements);
        Add("blend", blendDrawables, blendPlacements);
        sceneSelection.Rebuild(items);

        // Per-placement LOD margins, default 1.0 (= use the global budget as-is).
        opaqueLodMargins = new float[opaquePlacements.Count];
        blendLodMargins = new float[blendPlacements.Count];
        opaqueLodState = new int[opaquePlacements.Count];
        cascadeLodState = new int[CascadeCount][];
        for (var c = 0; c < CascadeCount; c++) cascadeLodState[c] = new int[opaquePlacements.Count];
        blendLodState = new int[blendPlacements.Count];
        System.Array.Fill(opaqueLodMargins, 1f);
        System.Array.Fill(blendLodMargins, 1f);

        // Give cutout foliage four times the global LOD error budget. Its texture-defined silhouette
        // tolerates coarser geometry, and the ivy/tree packs account for a measured 9.85 ms. Select
        // by alpha-cutoff semantics rather than asset naming.
        var foliage = 0;
        for (var i = 0; i < opaquePlacements.Count; i++)
        {
            if (opaqueDrawables[opaquePlacements[i].Drawable].AlphaCutoff <= 0f) continue;
            opaqueLodMargins[i] = FoliageLodMargin;
            foliage++;
        }
        for (var i = 0; i < blendPlacements.Count; i++)
        {
            if (blendDrawables[blendPlacements[i].Drawable].AlphaCutoff <= 0f) continue;
            blendLodMargins[i] = FoliageLodMargin;
        }
        // Derive the measurement orbit from cutout geometry so foliage-focused runs keep their
        // intended subject in view as scene bounds or authored placement change.
        var sum = Vector3.Zero;
        var n = 0;
        foreach (var p in opaquePlacements)
        {
            if (opaqueDrawables[p.Drawable].AlphaCutoff <= 0f) continue;
            sum += (p.Bounds.Min + p.Bounds.Max) * 0.5f;
            n++;
        }
        foliageCentre = n > 0 ? sum / n : Vector3.Zero;
        foliageValid = n > 0;
        Console.WriteLine(
            $"[VulkanSponza]   {foliage:N0} cutout placements start at {FoliageLodMargin:0.#}x the LOD budget"
            + (foliageValid ? $", centred at {foliageCentre}." : "."));
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
        foreach (var p in opaquePlacements)
        {
            var t = opaqueDrawables[p.Drawable].LodIndexCounts[0] / 3;
            totalTris += t;
            fattest.Add(t);
            for (var i = 0; i < edges.Length; i++)
            {
                if (t < edges[i]) { counts[i]++; tris[i] += t; break; }
            }
        }
        fattest.Sort((a, b) => b.CompareTo(a));
        var top = string.Join("/", fattest.Take(5).Select(t => $"{t / 1000.0:0.0}k"));
        Console.WriteLine($"[VulkanSponza] opaque LOD0 tris: {totalTris / 1_000_000.0:0.00}M across {opaquePlacements.Count} placed prims; top5={top}");
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
    // Materials are cached by PbrMaterial so primitives sharing a material reuse
    // one handle + descriptor set. Called incrementally (time-sliced) during the
    // streaming load — the material's texture read+upload is the bulk of the cost.
    private void StageDrawable(PlacedPrimitive placed)
    {
        var prim = placed.Primitive;
        {
            var pm = prim.Material;
            var mesh = prim.Mesh;
            // Only materials with authored transmission use alpha blending.
            // Cutout foliage authored as BLEND (the cypress, etc.) is treated as
            // MASK so it lands in the opaque bucket → written by the depth
            // pre-pass → early-Z. That kills the layered double-sided foliage
            // overdraw that makes the hero tree fragment-bound at Retina res.
            // Its alphaCutoff (set in BuildMaterial) drives the shader discard.
            var isBlend = (pm?.TransmissionFactor ?? 0f) > 0f;
            var material = GetMaterial(pm, out var albedo, out var alphaCutoff, out var baseColorAlpha);
            var alphaMode = isBlend ? AlphaMode.Blend
                : alphaCutoff > 0f ? AlphaMode.Mask
                : (pm?.AlphaMode ?? AlphaMode.Opaque);
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
                new[] { new ShaderTextureBinding("uAlbedo", albedo) }, isBlend,
                string.IsNullOrEmpty(mesh.Name) ? "primitive" : mesh.Name, placed.Worlds, placed.Instances, prim.Source,
                pm?.BaseColorFactor ?? Vector4.One));
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
            sharedLayout, device, "sponza.shared");
        sharedVb = bundle.Vertices;
        sharedIbU16 = bundle.Indices16;
        sharedIbU32 = bundle.Indices32;

        // Attach material/pipeline/etc. to each bundled geometry sub-range; split back into the opaque +
        // blend buckets (bundle order == ordered order). Each drawable's placements follow as one
        // contiguous run of its bucket's placement list, and every placement takes a row of the
        // transform table.
        // Traced GI needs the ray scene only where there is a probe field to inject into.
        var rayMeshes = rayScene || (giTrace && bounceReady) ? BuildRayMeshes(ordered) : null;
        var rayInstances = new List<RayQueryScene.Instance>();
        var nonUniform = 0;
        var surfaceKeys = new Dictionary<SurfaceIdentity, uint>();
        for (var i = 0; i < ordered.Count; i++)
        {
            var s = ordered[i];
            var bm = bundle.Meshes[i];
            var bucket = s.IsBlend ? blendDrawables : opaqueDrawables;
            var placements = s.IsBlend ? blendPlacements : opaquePlacements;
            var start = placements.Count;
            for (var w = 0; w < s.Worlds.Length; w++)
            {
                var world = s.Worlds[w];
                placements.Add(new Placement(bucket.Count, sceneTransforms.Count, WorldBounds(bm.Bounds, world)));
                sceneTransforms.Add(world);
                sceneTransformMaterials.Add((uint)s.Material.Id);
                sceneTransformSurfaceKeys.Add(SurfaceKeyOf(s, w, i, surfaceKeys));
                sceneTransformInstances.Add(s.Instances[w]);
                if (rayMeshes is not null)
                {
                    rayInstances.Add(new RayQueryScene.Instance(rayMeshes[i], world));
                    rayPlacementMaterials.Add(new RayMaterial(s.Albedo, s.BaseColor, s.AlphaCutoff));
                }
                if (!UniformScale(world)) nonUniform++;
            }

            bucket.Add(new Drawable(
                bm.IndicesAreU32, bm.BaseVertex, bm.LodFirstIndex, bm.LodIndexCounts, bm.LodErrors,
                s.Material, s.Pipeline, bm.Bounds, s.Albedo, s.AlphaCutoff, s.BaseColorAlpha,
                s.ShadowAlbedoBinding, s.Name, start, s.Worlds.Length,
                s.Lods.Select(l => l.Clusters ?? (IReadOnlyList<MeshCluster>)Array.Empty<MeshCluster>()).ToArray(),
                s.Pipeline == opaqueDoubleSidedPipeline || s.Pipeline == blendDoubleSidedPipeline));
        }

        SelectMover(rayMeshes is not null ? rayInstances : null);
        if (rayMeshes is not null) BuildRayScene(rayMeshes, rayInstances);
        WriteSurfaceKeyCensus(ordered, surfaceKeys.Count);

        // lit.vert carries normals by the model's linear part, exact for rotation and uniform scale. Said
        // when it is not, rather than shaded wrong in silence.
        if (nonUniform > 0)
        {
            Console.WriteLine($"[VulkanSponza] {nonUniform} placement(s) scale non-uniformly; their normals are approximate.");
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

        // lodSlots indirect commands per drawable per pass, one per LOD level (see FillIndirect), refilled
        // each frame (camera opaque + one per shadow cascade + blend). indirectScratch is sized for the
        // largest list (opaque) and reused for the smaller fills.
        lodSlots = Math.Max(1, opaqueDrawables.Concat(blendDrawables).Select(d => d.LodIndexCounts.Length).DefaultIfEmpty(1).Max());
        staging.Clear();
        Console.WriteLine(
            $"[VulkanSponza] bundled geometry: 1 VB ({bundle.VertexCount} verts) holding {opaqueDrawables.Count} + {blendDrawables.Count} "
            + $"unique primitives, placed {opaquePlacements.Count} + {blendPlacements.Count} times; {opaqueGroups.Count} opaque indirect groups "
            + $"x {lodSlots} LOD slots; {sceneTransforms.Count} transforms ({sceneTransforms.Count * 64 / 1024.0:0.0} KB).");
        // The GPU cull's buffers wait for the LOD margins (RegisterSelectables); see BuildCullBuffers.
        if (gpuCull) return;

        opaqueIndirect = Own(device.CreateIndirectBuffer(opaqueDrawables.Count * lodSlots, "sponza.opaque.indirect"));
        for (var c = 0; c < CascadeCount; c++)
            cascadeIndirect[c] = Own(device.CreateIndirectBuffer(opaqueDrawables.Count * lodSlots, $"sponza.cascade{c}.indirect"));
        if (blendDrawables.Count > 0)
            blendIndirect = Own(device.CreateIndirectBuffer(blendDrawables.Count * lodSlots, "sponza.blend.indirect"));
        indirectScratch = new byte[Math.Max(opaqueDrawables.Count, blendDrawables.Count) * lodSlots * IndirectDraw.RecordStride];

        // Set 3, sized to this scene: the transform table, and room for every pass to see every placement
        // (camera and each cascade over the opaque placements, the camera again over the blend ones).
        var visibleCapacity = SceneListVisibleBase(SceneListBlend) + blendPlacements.Count;
        visibleScratch = new uint[Math.Max(1, visibleCapacity)];
        var instances = device.CreateMaterial(
            litProgram, setIndex: 3, framesInFlight: device.MaxFramesInFlightCount, name: "sponza.instances",
            arrayLengths: new Dictionary<int, int>
            {
                [0] = Math.Max(1, sceneTransforms.Count), [1] = visibleScratch.Length,
                [3] = Math.Max(1, sceneTransformSurfaceKeys.Count), [4] = Math.Max(1, sceneTransforms.Count),
            });
        Own(instances.Handle);
        sceneInstances = instances;
        // Static: written into every frame slot once, never again.
        var transformBytes = MemoryMarshal.AsBytes(CollectionsMarshal.AsSpan(sceneTransforms));
        for (var slot = 0; slot < instances.FramesInFlight; slot++)
        {
            instances.WriteBuffer(slot, 0, transformBytes);
            instances.WriteBuffer(slot, 3, MemoryMarshal.AsBytes(CollectionsMarshal.AsSpan(sceneTransformSurfaceKeys)));
            // Nothing moves yet: the previous transforms are the transforms.
            instances.WriteBuffer(slot, 4, transformBytes);
        }
    }

    // What scene_cull.comp reads, uploaded once, and what it writes, zeroed: every placement (bounds,
    // drawable, transform row, LOD margin), every drawable and its levels, opaque bucket first, then
    // blend; per-(pass, placement) LOD state laid out like the visible list; and the indirect records,
    // one region per pass in the order of SceneListRecordBase.
    private void BuildCullBuffers()
    {
        if (!gpuCull) return;
        var placementCount = opaquePlacements.Count + blendPlacements.Count;
        var drawableCount = opaqueDrawables.Count + blendDrawables.Count;

        var placements = new CullPlacement[Math.Max(1, placementCount)];
        void Placements(List<Placement> list, float[] margins, int offset)
        {
            for (var i = 0; i < list.Count; i++)
            {
                var p = list[i];
                placements[offset + i] = new CullPlacement(
                    new Vector4(p.Bounds.Min, 0f), new Vector4(p.Bounds.Max, 0f),
                    (uint)p.Drawable, (uint)p.Transform, margins[i], 0);
            }
        }
        Placements(opaquePlacements, opaqueLodMargins, 0);
        Placements(blendPlacements, blendLodMargins, opaquePlacements.Count);

        var drawables = new CullDrawable[Math.Max(1, drawableCount)];
        var lods = new CullLod[Math.Max(1, drawableCount * lodSlots)];
        var at = 0;
        foreach (var d in opaqueDrawables.Concat(blendDrawables))
        {
            drawables[at] = new CullDrawable((uint)d.LodIndexCounts.Length, d.BaseVertex, (uint)d.PlacementStart, 0);
            for (var l = 0; l < d.LodIndexCounts.Length; l++)
            {
                lods[at * lodSlots + l] = new CullLod(d.LodErrors[l], (uint)d.LodIndexCounts[l], (uint)d.LodFirstIndex[l], 0);
            }
            at++;
        }

        var visibleCapacity = Math.Max(1, SceneListVisibleBase(SceneListBlend) + blendPlacements.Count);
        var records = Math.Max(1, SceneListRecordBase(SceneListBlend) + blendDrawables.Count * lodSlots);
        cullPlacements = Own(device.CreateGpuBuffer(placements.Length * CullPlacement.Size,
            MemoryMarshal.AsBytes(placements.AsSpan()), "sponza.cull.placements"));
        cullDrawables = Own(device.CreateGpuBuffer(drawables.Length * 16, MemoryMarshal.AsBytes(drawables.AsSpan()), "sponza.cull.drawables"));
        cullLods = Own(device.CreateGpuBuffer(lods.Length * 16, MemoryMarshal.AsBytes(lods.AsSpan()), "sponza.cull.lods"));
        cullState = Own(device.CreateGpuBuffer(visibleCapacity * 4, name: "sponza.cull.state"));
        cullCursor = Own(device.CreateGpuBuffer(records * 4, name: "sponza.cull.cursor"));
        sceneArgs = Own(device.CreateGpuBuffer(records * IndirectDraw.RecordStride, name: "sponza.scene.args"));
        sceneVisible = Own(device.CreateGpuBuffer(visibleCapacity * 4, name: "sponza.scene.visible"));
        sceneTransformBuffer = Own(device.CreateGpuBuffer(Math.Max(1, sceneTransforms.Count) * 64,
            MemoryMarshal.AsBytes(CollectionsMarshal.AsSpan(sceneTransforms)), "sponza.scene.transforms"));
        cullBuffers = new ShaderBufferBinding[]
        {
            new("CullPlacements", cullPlacements), new("CullDrawables", cullDrawables), new("CullLods", cullLods),
            new("CullState", cullState), new("CullCursor", cullCursor), new("SceneArgs", sceneArgs),
            new("SceneVisible", sceneVisible),
        };
        sceneSurfaceKeyBuffer = Own(device.CreateGpuBuffer(Math.Max(1, sceneTransformSurfaceKeys.Count) * 4,
            MemoryMarshal.AsBytes(CollectionsMarshal.AsSpan(sceneTransformSurfaceKeys)), "sponza.scene.surface-keys"));
        // Where each row stood last frame. Nothing moves yet, so it starts (and stays) equal to the transforms; a
        // mover (4e-vi) writes a moved row's old matrix here before its new one goes in.
        scenePreviousTransformBuffer = Own(device.CreateGpuBuffer(Math.Max(1, sceneTransforms.Count) * 64,
            MemoryMarshal.AsBytes(CollectionsMarshal.AsSpan(sceneTransforms)), "sponza.scene.previous-transforms"));
        sceneBuffers = new ShaderBufferBinding[]
        {
            new("SceneTransforms", sceneTransformBuffer), new("SceneVisible", sceneVisible),
            new("SceneSurfaceKeys", sceneSurfaceKeyBuffer), new("ScenePreviousTransforms", scenePreviousTransformBuffer),
        };
        Console.WriteLine(
            $"[VulkanSponza] GPU cull: {placementCount} placements, {drawableCount} drawables x {lodSlots} levels, "
            + $"{records} indirect records, {visibleCapacity} visible slots "
            + $"({(placements.Length * CullPlacement.Size + (drawables.Length + lods.Length) * 16 + visibleCapacity * 8 + records * 24 + sceneTransforms.Count * 64) / 1048576.0:0.0} MB).");
    }

    // scene_cull.comp's records, std430.
    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct CullPlacement(Vector4 BoundsMin, Vector4 BoundsMax, uint Drawable, uint Transform, float Margin, uint Pad)
    {
        public const int Size = 48;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct CullDrawable(uint LodCount, int BaseVertex, uint PlacementStart, uint Pad);

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct CullLod(float Error, uint IndexCount, uint FirstIndex, uint Pad);

    // Whether a world's linear part scales every axis alike (a rotation times a uniform scale).
    private static bool UniformScale(in Matrix4x4 m)
    {
        var x = new Vector3(m.M11, m.M12, m.M13).Length();
        var y = new Vector3(m.M21, m.M22, m.M23).Length();
        var z = new Vector3(m.M31, m.M32, m.M33).Length();
        var lo = MathF.Min(x, MathF.Min(y, z));
        var hi = MathF.Max(x, MathF.Max(y, z));
        return lo > 0f && hi / lo < 1.001f;
    }

    private MaterialHandle GetMaterial(PbrMaterial? gm, out TextureHandle albedo, out float alphaCutoff, out float baseColorAlpha)
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

    // Material semantics come only from authored glTF data. Transmission requires
    // KHR_materials_transmission so raster shading and lighting bakes consume the same surface
    // model; names are labels and never override authored opacity.

    // One engine Material per glTF material. BaseColorFactor / EmissiveFactor /
    // MaterialParams (alphaCutoff, normalScale, roughness, metallic) UBO + the
    // five channel textures (defaults when a channel is absent).
    private MaterialHandle BuildMaterial(
        PbrMaterial? gm, out TextureHandle albedo, out float alphaCutoff, out float baseColorAlpha)
    {
        // Engine resolves the five channel textures (read/decode/upload/dedup/
        // stream + glTF-default fallbacks + per-slot sRGB policy); the game keeps
        // the material UBO write + descriptor binding below.
        var tex = textureLoader.Load(gm);
        albedo = tex.Albedo;

        var baseColorFactor = gm?.BaseColorFactor ?? Vector4.One;
        var emissiveFactor = gm is null ? Vector3.Zero : gm.EmissiveFactor;
        var emissiveStrength = gm?.EmissiveStrength ?? 1.0f;
        // Cutout cutoff. MASK uses its authored cutoff. Non-transmissive BLEND (foliage authored
        // as blend) is treated as cutout at 0.5 so it can depth-write + early-Z instead of
        // overdrawing. Transmissive keeps 0 → no discard, true alpha blend.
        alphaCutoff = (gm?.TransmissionFactor ?? 0f) > 0f ? 0.0f
            : gm?.AlphaMode == AlphaMode.Mask ? (gm?.AlphaCutoff ?? 0.5f)
            : gm?.AlphaMode == AlphaMode.Blend ? 0.5f
            : 0.0f;
        // Measurement switch: a zero cutoff makes PickPipeline choose Opaque everywhere, which is
        // what takes the alpha sample out of the shadow casters and the pre-pass as well as the lit
        // pass. See --no-mask.
        if (forceOpaqueMask) alphaCutoff = 0f;
        baseColorAlpha = baseColorFactor.W;
        var normalScale = gm?.NormalScale ?? 1.0f;
        var roughness = gm?.RoughnessFactor ?? 0.8f;
        var metallic = gm?.MetallicFactor ?? 0.0f;
        var transmission = gm?.TransmissionFactor ?? 0f;
        // Cloth terms come from imported material extensions authored by the patch.
        var ext = gm?.Ext ?? Blix.PbrMaterialExtensions.None;
        var sheen = ext.SheenColorFactor;
        var sheenRoughness = ext.SheenRoughnessFactor;
        var diffuseTransmission = ext.DiffuseTransmissionFactor;
        var diffuseTransmissionColor = ext.DiffuseTransmissionColorFactor;

        return Own(device.CreateMaterial(litProgram, name: "sponza.material")
            .SetUniform(binding: 0, "uBaseColorFactor", baseColorFactor)
            .SetUniform(binding: 0, "uEmissiveFactor",
                new Vector4(emissiveFactor.X, emissiveFactor.Y, emissiveFactor.Z, emissiveStrength))
            .SetUniform(binding: 0, "uMaterialParams",
                new Vector4(alphaCutoff, normalScale, roughness, metallic))
            .SetUniform(binding: 0, "uMaterialParams2",
                // w carries the MSAA sample count, which the depth pre-pass's mask shader needs to
                // build the same coverage mask the lit pass will -- it has no frame block bound, and
                // a spare component here beats duplicating a std140 layout to reach one float.
                //
                // At one sample, carry the cutout policy in the same material channel so depth and
                // lit passes make identical coverage decisions. A negative value selects the plain
                // binary --no-hashed-alpha comparison; a positive value enables hashed coverage.
                new Vector4(transmission, sheenRoughness, diffuseTransmission,
                    MsaaSamples > 1 ? MsaaSamples : (hashedAlpha ? 1f : -1f)))
            .SetUniform(binding: 0, "uSheenColor", new Vector4(sheen.X, sheen.Y, sheen.Z, 0f))
            .SetUniform(binding: 0, "uDiffuseTransmissionColor",
                new Vector4(diffuseTransmissionColor.X, diffuseTransmissionColor.Y,
                            diffuseTransmissionColor.Z, 0f))
            .SetTexture(binding: 1, tex.Albedo)
            .SetTexture(binding: 2, tex.Normal)
            .SetTexture(binding: 3, tex.Emissive)
            .SetTexture(binding: 4, tex.MetallicRoughness)
            .SetTexture(binding: 5, tex.Occlusion)
            .SetTexture(binding: 6, tex.DiffuseTransmissionColor)
            .Handle);
    }

    private PipelineHandle PickPipeline(AlphaMode mode, bool doubleSided) =>
        (mode, doubleSided) switch
        {
            (AlphaMode.Blend, true)  => blendDoubleSidedPipeline,
            (AlphaMode.Blend, false) => blendSolidPipeline,
            (_, true)                    => opaqueDoubleSidedPipeline,
            _                            => opaqueSolidPipeline,
        };

    // A surface's identity (stage 4e): the instance that draws it and the source primitive it was cooked from.
    // Without provenance (a primitive built in memory) the staged primitive itself stands in for its source.
    private const uint StochasticSurface = 0x80000000u;
    // The placement may move (declared dynamic, with a reach: the mover's rows): a fact about the surface, set once
    // the mover is chosen, that TAA reads to know a pixel's shading follows that placement's pose (stage 4f).
    private const uint DynamicSurface = 0x40000000u;

    private readonly record struct SurfaceIdentity(PlacementInstance Instance, int SourceMesh, int SourcePrimitive, int Unsourced);

    // The SurfaceKey of staged primitive s's w-th placement: dense, from 1, assigned in load order, so every cooked
    // chunk of one source primitive drawn by one instance gets the same key and nothing else does. The top bit
    // (StochasticSurface) marks an alpha-tested surface: its coverage is a coin flip per frame under TAA's jitter
    // (a leaf, then the wall behind it), so it does not own a pixel from one frame to the next, and a pixel's key
    // changing there is not a disocclusion.
    private static uint SurfaceKeyOf(DrawableStaging s, int w, int stagingIndex, Dictionary<SurfaceIdentity, uint> keys)
    {
        var identity = s.Source is { } src
            ? new SurfaceIdentity(s.Instances[w], src.Mesh, src.Primitive, -1)
            : new SurfaceIdentity(s.Instances[w], -1, -1, stagingIndex);
        if (!keys.TryGetValue(identity, out var key)) keys[identity] = key = (uint)keys.Count + 1;
        return s.AlphaCutoff > 0f ? key | StochasticSurface : key;
    }

    // What the keys say about the scene: how many surfaces, how many placement rows (cooked chunks x instances)
    // each spans, and how much the cook's split would have fragmented an identity without provenance.
    private void WriteSurfaceKeyCensus(IReadOnlyList<DrawableStaging> ordered, int keyCount)
    {
        var rows = sceneTransformSurfaceKeys.Count;
        var rowsPerKey = sceneTransformSurfaceKeys.GroupBy(k => k).Select(g => g.Count()).OrderBy(c => c).ToArray();
        var instances = ordered.SelectMany(s => s.Instances).Distinct().Count();
        var unsourced = ordered.Count(s => s.Source is null);
        var materials = sceneTransformMaterials.Distinct().Count();
        // Does a key name one place? A key is authoring granularity (source primitive x instance), and one authored
        // primitive can be several disconnected surfaces. Group each key's rows into regions whose world boxes touch
        // (a split's chunks are adjacent, so one connected surface is one region); a key over several regions is a
        // place where only spatial support, not the key, can tell two surfaces apart.
        var rowBounds = new Bounds3[rows];
        foreach (var pl in opaquePlacements.Concat(blendPlacements)) rowBounds[pl.Transform] = pl.Bounds;
        var multiRegion = 0;
        (uint Key, int Rows, int Regions, float Extent) worst = default;
        foreach (var group in Enumerable.Range(0, rows).GroupBy(r => sceneTransformSurfaceKeys[r]))
        {
            var members = group.ToArray();
            if (members.Length < 2) continue;
            var parent = Enumerable.Range(0, members.Length).ToArray();
            int Find(int a) { while (parent[a] != a) a = parent[a] = parent[parent[a]]; return a; }
            for (var a = 0; a < members.Length; a++)
            for (var b = a + 1; b < members.Length; b++)
            {
                var ba = rowBounds[members[a]]; var bb = rowBounds[members[b]];
                const float touch = 0.02f;
                if (ba.Min.X <= bb.Max.X + touch && bb.Min.X <= ba.Max.X + touch && ba.Min.Y <= bb.Max.Y + touch
                    && bb.Min.Y <= ba.Max.Y + touch && ba.Min.Z <= bb.Max.Z + touch && bb.Min.Z <= ba.Max.Z + touch)
                {
                    parent[Find(a)] = Find(b);
                }
            }
            var regions = Enumerable.Range(0, members.Length).Select(Find).Distinct().Count();
            if (regions < 2) continue;
            multiRegion++;
            var lo = members.Select(r => rowBounds[r].Min).Aggregate(Vector3.Min);
            var hi = members.Select(r => rowBounds[r].Max).Aggregate(Vector3.Max);
            if (regions > worst.Regions) worst = (group.Key, members.Length, regions, (hi - lo).Length());
        }
        Console.WriteLine(string.Create(Inv,
            $"[VulkanSponza] surface keys spanning disconnected regions: {multiRegion:N0} of {keyCount:N0}; the most split: {worst.Rows} rows in {worst.Regions} regions over {worst.Extent:0.0} m."));
        Console.WriteLine(string.Create(Inv,
            $"[VulkanSponza] surface keys: {keyCount:N0} surfaces over {rows:N0} placement rows ({instances:N0} instances; rows per surface median {rowsPerKey[rowsPerKey.Length / 2]}, max {rowsPerKey[^1]}); {materials} materials, {unsourced} staged primitive(s) without provenance."));
    }
}

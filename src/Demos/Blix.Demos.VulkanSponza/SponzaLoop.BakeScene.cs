using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using Blix.Geometry;
using Blix.Graphics;
using Blix.Graphics.MetalHWX;

namespace Blix.Demos.VulkanSponza;

// The bake scene: the ray scene at full detail, for the work that bakes the lightmap atlas -- the buried test, the light
// bake and the texels' gathers -- so their rays meet the LOD 0 surfaces the texels sit
// on. The frame's scene is simplified up to rayLodError: its chords pass in front of concave detail (a texel started
// behind its own mesh's coarse surface and read as buried in plain view) and over convex detail, which the bake exists
// to keep. Metal only (--lightmap --transport-texels, frame rays on Metal): packed off the frame's thread from LOD 0
// (~30 s on Sponza), its blocks on the GPU without the software traversal's nodes (Metal builds its own structures),
// baked for albedo and coverage as the frame's scene is, and traced by a tracer of its own.
internal sealed partial class SponzaLoop
{
    private Task<RayQueryGpuData>? bakeSceneTask;
    private RayQueryGpuData? bakeSceneData;
    // The bake scene's placement-and-triangle-to-row map and its row count, kept when its data is let go.
    private (int[][] Map, int Rows)? bakeSceneRowMap;
    private GpuBufferHandle[] bakeSceneBlocks = Array.Empty<GpuBufferHandle>();
    private readonly List<(RayMaterial Material, int First, int Count)> bakeSceneSurfaceRuns = new();
    private ShaderBufferBinding[] bakeSceneSurfaceBuffers = Array.Empty<ShaderBufferBinding>();
    // The bake scene's own alpha atlas (its coordinates are at full detail, so it is planned from its data, not the frame's).
    private RayAlphaAtlas.Plan? bakeSceneAlphaPlan;
    private List<RayMaterial> bakeSceneAlphaMaterials = new();
    private long bakeSceneSurfacesFrame = -1;
    private MetalRayTracer? bakeTracer;
    private bool bakeTracerFailed;
    // Its work done -- the cook's buried test run, no light bake under way (or both read from their caches) -- the bake scene lets go of its ~1.4 GB, and the texels' later gathers (a sun's turn, the
    // rays past the bake's) take the frame's tracer.
    private bool bakeSceneReleased;
    private bool BakeSceneDone => texelsUploaded && !lightBakeActive;
    private int gpuMemoryLoggedFrame = -1;

    // --no-bake-scene: the atlas bakes against the frame's scene (the A/B).
    private bool noBakeScene;
    private bool BakeSceneWanted => !noBakeScene && lightmapEnabled && transportTexels && FrameRaysOnHardware && rayLodError > 0f;
    // The staged drawables the bake scene would be built from, held from load until the transport cook knows its key
    // (DecideBakeScene); then built from, or let go of.
    private List<DrawableStaging>? bakeSceneSource;
    // Every cache the bake scene would fill was found (the buried flags, this sun's light; the lightlet records bake on
    // the frame's tracer): it is not built at all -- ~1.3 GB of GPU memory at the boot's peak and 45-85 s of CPU, every boot, for nothing.
    private volatile bool bakeSceneSkipped;

    // On the cook's thread, with the key: the caches checked, the bake scene started or skipped.
    private void DecideBakeScene(string cacheKey)
    {
        if (Interlocked.Exchange(ref bakeSceneSource, null) is not { } source) return;
        var lightPath = LightBakePathFor(cacheKey, (-Vector3.Normalize(sunDirection), EffectiveSunIrradiance));
        // Under the split light the one light's bake is never read; the lightmap records' stage (the GI bundle's)
        // holds the buried flags with the records.
        var light = lightBakeOff || splitLight || File.Exists(lightPath) || File.Exists(ResolvedPathFor(lightPath));
        var buried = !lightmapEnabled || texelsStageKey is { } tk
            && (File.Exists(BuriedCachePath(tk)) || File.Exists(TransportStagePath(tk, "lightmap-records")));
        // --lightmap-buried-why re-traces buried records against the bake scene: built for it.
        if (light && buried && !lightmapBuriedWhy)
        {
            bakeSceneSkipped = true;
            Console.WriteLine("[VulkanSponza] bake scene: not built -- the buried flags and this sun's light are both cached.");
            return;
        }
        Console.WriteLine($"[VulkanSponza] bake scene: building (missing:{(buried ? "" : " buried flags")}{(light ? "" : " light")}).");
        StartBakeScene(source);
    }

    // The bake scene's tracer once built (null before, or without one).
    private MetalRayTracer? BakeTracer => Volatile.Read(ref bakeTracer);

    private void StartBakeScene(List<DrawableStaging> ordered)
    {
        if (!BakeSceneWanted || ordered.Count == 0) return;
        bakeSceneTask = Task.Run(() =>
        {
            var clock = Stopwatch.StartNew();
            var meshes = BuildRayMeshes(ordered, 0f);
            // One instance per placement, in the frame's scene's order: placement ids, materials and sidedness carry over.
            var instances = new List<RayQueryScene.Instance>();
            for (var i = 0; i < ordered.Count; i++)
                foreach (var world in ordered[i].Worlds) instances.Add(new RayQueryScene.Instance(meshes[i], world));
            var data = RayQueryGpuData.Pack(RayQueryScene.Build(instances, rayRegionTriangles));
            Console.WriteLine(string.Create(Inv,
                $"[VulkanSponza] bake scene: {instances.Count:N0} placements at full detail, {data.Triangles.Length / 4:N0} triangles, packed in {clock.Elapsed.TotalSeconds:0.0} s off the frame."));
            return data;
        });
    }

    // Each frame: the packed scene onto the GPU once it is ready, its surfaces baked once everything is resident, and its
    // tracer built a few frames after (so the bake has run), as the frame tracer is.
    private void UpdateBakeScene()
    {
        if (postLoadFrames % 300 == 0 && postLoadFrames > 0 && gpuMemoryLoggedFrame != postLoadFrames)
        {
            gpuMemoryLoggedFrame = postLoadFrames;
            WriteGpuMemory(string.Create(Inv, $"at post-load frame {postLoadFrames}"));
        }
        if (bakeSceneTask is null || bakeSceneReleased) return;
        if (BakeSceneDone)
        {
            ReleaseBakeScene();
            return;
        }
        if (bakeTracer is not null || bakeTracerFailed) return;
        if (bakeSceneData is null && bakeSceneBlocks.Length == 0)
        {
            if (!bakeSceneTask.IsCompleted) return;
            if (bakeSceneTask.Exception is { } e)
            {
                Console.WriteLine($"[VulkanSponza] bake scene: failed ({e.GetBaseException().Message}); the atlas bakes against the frame's scene.");
                bakeTracerFailed = true;
                return;
            }
            UploadBakeScene(bakeSceneTask.Result);
            return;
        }
        if (bakeSceneSurfacesFrame < 0)
        {
            if (!fullyLoaded || postLoadFrames < device.MaxFramesInFlightCount + 2) return;   // past the last mip's upload (RecordRaySurfaceBake)
            foreach (var (material, first, count) in bakeSceneSurfaceRuns)
                graph.Dispatch(raySurfaceBakePassHandle, new DispatchCommand(raySurfaceBakePipeline, (count + 63) / 64, 1, 1,
                    new ShaderUniform[]
                    {
                        new("uBaseColor", new Vector4Uniform(material.BaseColor)),
                        new("uParams", new Vector4Uniform(new Vector4(first, count, material.AlphaCutoff, material.Clear ? 1f : 0f))),
                        new("uParams2", new Vector4Uniform(new Vector4(material.Transmission.Equals(default(TextureHandle)) ? 0f : 1f,
                        material.TransmissionColor.Equals(default(TextureHandle)) ? 0f : 1f, RayAlphaMip(material), RayStochastic(material) ? 1f : 0f))),
                        new("uDiffuse", new Vector4Uniform(BakeDiffuse(material))),
                    new("uDiffuse2", new Vector4Uniform(new Vector4(material.DiffuseRoughness, 0f, 0f, 0f))),
                    },
                    new[] { new ShaderTextureBinding("uAlbedo", material.Albedo),
                        new ShaderTextureBinding("uTransmission", material.Transmission.Equals(default(TextureHandle)) ? material.Albedo : material.Transmission),
                        new ShaderTextureBinding("uTransmissionColor", material.TransmissionColor.Equals(default(TextureHandle)) ? material.Albedo : material.TransmissionColor),
                        BakeMetallicRoughness(material) },
                    Buffers: bakeSceneSurfaceBuffers));
            RecordRayAlphaBake(bakeSceneAlphaPlan, bakeSceneAlphaMaterials, bakeSceneData!, bakeSceneSurfaceBuffers);
            bakeSceneSurfacesFrame = postLoadFrames;
            return;
        }
        if (postLoadFrames < bakeSceneSurfacesFrame + device.MaxFramesInFlightCount + 1) return;
        var tracer = MetalRayTracer.TryCreate(device, out var reason);
        if (tracer is null)
        {
            Console.WriteLine($"[VulkanSponza] bake scene: no tracer ({reason}); the atlas bakes against the frame's scene.");
            bakeTracerFailed = true;
            return;
        }
        // On the frame tracer's queue: its passes share the frame's timeline (see MetalRayTracer.ShareQueueWith).
        if (frameTracer is { } frame) tracer.ShareQueueWith(frame);
        var data = bakeSceneData;
        var surfaceWords = MemoryMarshal.Cast<byte, uint>(device.ReadGpuBuffer(bakeSceneBlocks[6], 0, data.RowPlacements.Length * 4).AsSpan()).ToArray();
        device.WaitIdle();
        var build = tracer.Build(data, bakeSceneBlocks, surfaceWords);
        Console.WriteLine(string.Create(Inv,
            $"[VulkanSponza] bake scene tracer: {build.SolidTriangles + build.PartialTriangles:N0} triangles ({build.PartialTriangles:N0} partly covered) in {build.Hierarchies} bottom-level structures, built in {build.CpuPrepareMs + build.BlasGpuMs + build.CompactTlasGpuMs:0} ms; {build.CompactedBytes / 1048576.0:0} MB."));
        if (bakeSceneAlphaPlan is not null) tracer.SetAlphaAtlas(data.RowPlacements.Length);   // per-hit alpha, as the frame's tracer (SponzaLoop.RayAlpha.cs)
        WriteGpuMemory("after the bake scene tracer");
        if (bakeSceneAlbedoCheck) WriteBakeSceneAlbedoCheck(data);
        bakeSceneRowMap = (data.RowMap, data.RowPlacements.Length);   // the contact table's rows (GpuContacts) outlive the rest
        bakeSceneData = null;   // the CPU copy is done with: the tracer and the blocks hold what it traces
        Volatile.Write(ref bakeTracer, tracer);
    }

    // --bake-scene-albedo-check: each placement's baked surfaces (mean albedo, mean coverage) in the frame's scene and in
    // the bake scene -- what their tracers' hits read. Investigating the sun nodes' cook on the bake scene, which read
    // Cornell's light 28% dark with the same hits, normals and patches as the frame's tracer.
    private bool bakeSceneAlbedoCheck;

    private void WriteBakeSceneAlbedoCheck(RayQueryGpuData bake)
    {
        if (rayGpuData is null || raySurfaces.Equals(default(GpuBufferHandle))) return;
        device.WaitIdle();
        Dictionary<uint, (Vector3 Albedo, float Coverage, int Rows)> Means(RayQueryGpuData data, GpuBufferHandle surfaces)
        {
            var words = MemoryMarshal.Cast<byte, uint>(device.ReadGpuBuffer(surfaces, 0, data.RowPlacements.Length * 4).AsSpan()).ToArray();
            var sums = new Dictionary<uint, (Vector3, float, int)>();
            for (var row = 0; row < data.RowPlacements.Length; row++)
            {
                var w = words[row];
                var p = data.RowPlacements[row];
                sums.TryGetValue(p, out var s);
                sums[p] = (s.Item1 + new Vector3(w & 255u, (w >> 8) & 255u, (w >> 16) & 255u) / 255f, s.Item2 + RayQueryGpuData.CoverageByte(w) / 255f, s.Item3 + 1);
            }
            return sums.ToDictionary(kv => kv.Key, kv => (kv.Value.Item1 / kv.Value.Item3, kv.Value.Item2 / kv.Value.Item3, kv.Value.Item3));
        }
        var frame = Means(rayGpuData, raySurfaces);
        var baked = Means(bake, bakeSceneBlocks[6]);
        Console.WriteLine("[VulkanSponza] bake scene albedo check: placement, rows, mean sRGB albedo and coverage -- frame scene | bake scene:");
        foreach (var p in frame.Keys.Union(baked.Keys).Order().Take(64))
        {
            frame.TryGetValue(p, out var f);
            baked.TryGetValue(p, out var b);
            Console.WriteLine(string.Create(Inv,
                $"[VulkanSponza]   {p,5} {(p < rayPlacementNames.Count ? rayPlacementNames[(int)p] : "?"),-24} {f.Rows,6} ({f.Albedo.X:0.000} {f.Albedo.Y:0.000} {f.Albedo.Z:0.000}) {f.Coverage:0.00} | {b.Rows,6} ({b.Albedo.X:0.000} {b.Albedo.Y:0.000} {b.Albedo.Z:0.000}) {b.Coverage:0.00}"));
        }
    }

    private void ReleaseBakeScene()
    {
        device.WaitIdle();
        var tracer = BakeTracer;
        Volatile.Write(ref bakeTracer, null);
        tracer?.Dispose();
        foreach (var b in bakeSceneBlocks) Disown(b);
        if (bakeSceneSurfaceBuffers.Length == 5)
        {
            Disown(bakeSceneSurfaceBuffers[1].Buffer);   // the coordinates, rows and shares; triangles and surfaces are blocks
            Disown(bakeSceneSurfaceBuffers[2].Buffer);
            Disown(bakeSceneSurfaceBuffers[4].Buffer);
        }
        bakeSceneBlocks = Array.Empty<GpuBufferHandle>();
        bakeSceneSurfaceBuffers = Array.Empty<ShaderBufferBinding>();
        bakeSceneData = null;
        bakeSceneAlphaPlan = null;
        bakeSceneReleased = true;
        Console.WriteLine($"[VulkanSponza] bake scene: released ({(tracer is null ? "never traced: its work read from the caches" : lightBakeOff ? "the buried test done; no light bake (--no-light-bake)" : "the buried test and the light bake done")}); later gathers take the frame's tracer.");
        WriteGpuMemory("after releasing the bake scene");
    }

    // The blocks Metal's build reads (instances, triangles, positions, owners, surfaces); the software traversal's nodes
    // only as placeholders. And the surface bake's inputs, as BuildRayGpu makes them for the frame's scene.
    private void UploadBakeScene(RayQueryGpuData data)
    {
        bakeSceneData = data;
        var blocks = data.Blocks();
        (bakeSceneAlphaPlan, bakeSceneAlphaMaterials) = PlanRayAlpha(data, ref blocks[6]);
        bakeSceneBlocks = new GpuBufferHandle[blocks.Length];
        long bytes = 0;
        for (var b = 0; b < blocks.Length; b++)
        {
            var needed = b is not (0 or 2);
            var content = needed ? blocks[b] : new byte[16];
            bakeSceneBlocks[b] = Own(device.CreateGpuBuffer(Math.Max(16, content.Length), content, $"sponza.bake-scene.{RayQueryGpuData.BlockNames[b]}"));
            bytes += content.Length;
        }
        var byMaterial = new Dictionary<RayMaterial, List<uint>>();
        for (var row = 0; row < data.RowPlacements.Length; row++)
        {
            var material = rayPlacementMaterials[(int)data.RowPlacements[row]];
            if (!byMaterial.TryGetValue(material, out var rows)) byMaterial[material] = rows = new List<uint>();
            rows.Add((uint)row);
        }
        var bakeRows = new List<uint>(data.RowPlacements.Length);
        foreach (var (material, rows) in byMaterial)
        {
            bakeSceneSurfaceRuns.Add((material, bakeRows.Count, rows.Count));
            bakeRows.AddRange(rows);
        }
        var uvBytes = MemoryMarshal.AsBytes(data.Uvs.AsSpan()).ToArray();
        var bakeRowBytes = MemoryMarshal.AsBytes(CollectionsMarshal.AsSpan(bakeRows)).ToArray();
        bakeSceneSurfaceBuffers = new[]
        {
            new ShaderBufferBinding("BlixRayTriangles", bakeSceneBlocks[3]),
            new ShaderBufferBinding("BlixRayUvs", Own(device.CreateGpuBuffer(Math.Max(16, uvBytes.Length), uvBytes, "sponza.bake-scene.uvs"))),
            new ShaderBufferBinding("BakeRows", Own(device.CreateGpuBuffer(Math.Max(16, bakeRowBytes.Length), bakeRowBytes, "sponza.bake-scene.bake-rows"))),
            new ShaderBufferBinding("BlixRaySurfaces", bakeSceneBlocks[6]),
            // The rows' transmission share (ray_surface_bake.comp): the bake scene's own, unread -- the frame scene's is.
            new ShaderBufferBinding("BlixRayTransmission", Own(device.CreateGpuBuffer(Math.Max(16, data.RowPlacements.Length * 4), name: "sponza.bake-scene.transmission"))),
        };
        bytes += uvBytes.Length + bakeRowBytes.Length;
        Console.WriteLine(string.Create(Inv,
            $"[VulkanSponza] bake scene on the GPU: {bytes / 1048576.0:0} MB ({data.Triangles.Length / 4:N0} triangles, {data.Positions.Length:N0} vertices; no traversal nodes)."));
    }

    // Metal's view of the device's GPU memory (frame and bake tracers share the device).
    private void WriteGpuMemory(string when)
    {
        var tracer = frameTracer ?? BakeTracer;
        if (tracer is null) return;
        var (allocated, recommended) = tracer.Memory;
        Console.WriteLine(string.Create(Inv, $"[VulkanSponza] GPU memory {when}: {allocated:N0} MB allocated of {recommended:N0} MB recommended ({100 * allocated / recommended:0}%)."));
    }

    // The bake scene's tracer for work off the frame's thread (the cook's buried test): waited for while it is wanted
    // and being built; else the frame's (WaitForFrameTracer).
    private MetalRayTracer? WaitForBakeTracer()
    {
        if (!BakeSceneWanted || bakeSceneSkipped) return WaitForFrameTracer();
        if (!transportCookAsync) return BakeTracer;
        var clock = Stopwatch.StartNew();
        while (BakeTracer is null && !Volatile.Read(ref bakeTracerFailed) && !Volatile.Read(ref bakeSceneReleased) && clock.Elapsed.TotalSeconds < 300)
            Thread.Sleep(50);
        return BakeTracer ?? WaitForFrameTracer();
    }
}

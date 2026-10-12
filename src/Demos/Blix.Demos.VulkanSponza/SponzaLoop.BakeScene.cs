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
    private GpuBufferHandle[] bakeSceneBlocks = Array.Empty<GpuBufferHandle>();
    private readonly List<(RayMaterial Material, int First, int Count)> bakeSceneSurfaceRuns = new();
    private ShaderBufferBinding[] bakeSceneSurfaceBuffers = Array.Empty<ShaderBufferBinding>();
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
            if (!fullyLoaded) return;
            foreach (var (material, first, count) in bakeSceneSurfaceRuns)
                graph.Dispatch(raySurfaceBakePassHandle, new DispatchCommand(raySurfaceBakePipeline, (count + 63) / 64, 1, 1,
                    new ShaderUniform[]
                    {
                        new("uBaseColor", new Vector4Uniform(material.BaseColor)),
                        new("uParams", new Vector4Uniform(new Vector4(first, count, material.AlphaCutoff, material.Clear ? 1f : 0f))),
                    },
                    new[] { new ShaderTextureBinding("uAlbedo", material.Albedo) },
                    Buffers: bakeSceneSurfaceBuffers));
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
        WriteGpuMemory("after the bake scene tracer");
        bakeSceneData = null;   // the CPU copy is done with: the tracer and the blocks hold what it traces
        Volatile.Write(ref bakeTracer, tracer);
    }

    private void ReleaseBakeScene()
    {
        device.WaitIdle();
        var tracer = BakeTracer;
        Volatile.Write(ref bakeTracer, null);
        tracer?.Dispose();
        foreach (var b in bakeSceneBlocks) Disown(b);
        if (bakeSceneSurfaceBuffers.Length == 4)
        {
            Disown(bakeSceneSurfaceBuffers[1].Buffer);   // the coordinates and rows; triangles and surfaces are blocks
            Disown(bakeSceneSurfaceBuffers[2].Buffer);
        }
        bakeSceneBlocks = Array.Empty<GpuBufferHandle>();
        bakeSceneSurfaceBuffers = Array.Empty<ShaderBufferBinding>();
        bakeSceneData = null;
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
        if (!BakeSceneWanted) return WaitForFrameTracer();
        if (!transportCookAsync) return BakeTracer;
        var clock = Stopwatch.StartNew();
        while (BakeTracer is null && !Volatile.Read(ref bakeTracerFailed) && !Volatile.Read(ref bakeSceneReleased) && clock.Elapsed.TotalSeconds < 300)
            Thread.Sleep(50);
        return BakeTracer ?? WaitForFrameTracer();
    }
}

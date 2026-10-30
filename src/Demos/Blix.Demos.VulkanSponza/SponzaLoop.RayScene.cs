using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using Blix.Geometry;
using Blix.Graphics;

using Blix.Diagnostics;

namespace Blix.Demos.VulkanSponza;

// The scene under CPU ray-query hierarchies (Blix.Geometry), built at load over every primitive's full-detail
// triangles: regions over the placements of meshes used once, an instance per placement of a mesh used more. Always
// built: RTGI's cook and the frame's traced passes are built from it. Stage 3's measure of whether a load-time build fits these scenes, and what a CPU ray costs in them.
internal sealed partial class SponzaLoop
{
    private RayQueryScene? rayQueries;
    // The scene's diagonal: how far a sun ray may go -- the frame's traced passes need only this of the CPU scene.
    private float rayReach = 1000f;
    // Per ray-scene placement (the transform table's order): the material a surface bake reads.
    // Clear: authored transmission (the panes): no coverage, rays pass (ray_surface_bake.comp).
    private readonly record struct RayMaterial(TextureHandle Albedo, Vector4 BaseColor, float AlphaCutoff, bool Clear, TextureHandle Transmission = default,
        TextureHandle TransmissionColor = default, TextureHandle MetallicRoughness = default, float Roughness = 0.8f, bool Blended = false, float DiffuseRoughness = -1f, float Metallic = 0f, TextureHandle Emissive = default, float PaneStops = 0f, float PaneAlbedo = 0f);
    // Per placement: the material as the loop sees it (overrides applied), for the reference's material table; null where there is none.
    private readonly List<PbrMaterial?> rayPlacementPbr = new();
    private readonly List<RayMaterial> rayPlacementMaterials = new();
    // Per placement (the ray scene's order): whether it is drawn double-sided -- a sheet with no inside.
    private readonly List<bool> rayPlacementDoubleSided = new();
    // Per placement: its material's diffuse transmission (factor times colour; zero: none). The sun's shadow rays go
    // through such a sheet (SponzaLoop.RtShadows.cs RtRowTransmit), and what lands behind it is the sunlit sheet's
    // diffuse glow, not a tinted beam (tried as a beam: a sharp coloured copy of the cloth, 2026-10-15).
    private readonly List<Vector3> rayPlacementTransmission = new();
    private readonly List<float> rayPlacementTransmissionFactor = new();
    // Per placement: --cloth-transmission's curtains (DrawableStaging.Cloth).
    private readonly List<bool> rayPlacementCloth = new();
    // Per placement: its primitive's name (the leak census names what a leak passed).
    private readonly List<string> rayPlacementNames = new();
    private RayQueryGpuData? rayGpuData;
    private GpuBufferHandle raySurfaces;
    // One bake dispatch per material: its albedo, factor and cutoff, and its run of triangle rows in BakeRows.
    private readonly List<(RayMaterial Material, int First, int Count)> raySurfaceBakes = new();
    private ShaderBufferBinding[] raySurfaceBakeBuffers = Array.Empty<ShaderBufferBinding>();
    // Per triangle row, the share of its material's diffuse transmission factor its factor texture keeps (the texture's
    // alpha averaged over the triangle, unorm8; 255 whole, the default), and its colour texture's mean (sRGB, the upper
    // three bytes; white without one): ray_surface_bake.comp writes them.
    private GpuBufferHandle rayTransmissionRows;
    // Per row, the baked specular roughness (low byte) and metallic (next), unorm8: the reference's per-row material means (ray_surface_bake.comp).
    private GpuBufferHandle rayMrRows;
    private uint[]? rowTransmissionWords;
    private bool RowTransmissionWords()
    {
        if (rowTransmissionWords is not null) return true;
        if (rayTransmissionRows.Equals(default(GpuBufferHandle)) || rayGpuData is not { } d || !raySurfacesChecked) return false;
        device.WaitIdle();
        rowTransmissionWords = MemoryMarshal.Cast<byte, uint>(device.ReadGpuBuffer(rayTransmissionRows, 0, d.RowPlacements.Length * 4).AsSpan()).ToArray();
        return true;
    }
    private float RowTransmissionShare(int row) =>
        RowTransmissionWords() && row < rowTransmissionWords!.Length ? (rowTransmissionWords[row] & 255u) / 255f : 1f;
    private Vector3 RowTransmissionColour(int row)
    {
        if (!RowTransmissionWords() || row >= rowTransmissionWords!.Length) return Vector3.One;
        var w = rowTransmissionWords[row];
        static float Linear(uint b) { var c = b / 255f; return c <= 0.04045f ? c / 12.92f : MathF.Pow((c + 0.055f) / 1.055f, 2.4f); }
        return new Vector3(Linear((w >> 8) & 255u), Linear((w >> 16) & 255u), Linear(w >> 24));
    }
    private ShaderInterface raySurfaceBakeInterface = null!;
    private PassHandle raySurfaceBakePassHandle;
    private PipelineHandle raySurfaceBakePipeline;
    private bool raySurfacesBaked;
    // --ray-region-triangles N: the most triangles a region holds before it is split (RayQueryScene).
    private int rayRegionTriangles = RayQueryScene.DefaultRegionTriangles;
    // --ray-lod-error M (a measurement): trace each mesh at its coarsest cooked level whose geometric error, carried into
    // the world by the largest scale any of its placements gives it, stays within M metres. Full detail (0) by default:
    // every ray's origin and lift stand on the surface the rays are traced against. At 2 cm (the old default, 506 MB on
    // Sponza's GPU rather than 781) Crytek Sponza's vaults sat up to 2 cm behind their coarse surface: rays started
    // inside it, and the vaults went black in the bounce (2026-10-23).
    private float rayLodError = 0f;

    // At each mesh's coarsest level within rayLodError (0: full detail).
    private RayMesh[] BuildRayMeshes(List<DrawableStaging> ordered)
    {
        var lodLimit = rayLodError;
        var stride = sharedLayout.Stride;
        var semantics = Blix.Graphics.VertexSemantics.Of(sharedLayout);
        var position = semantics?.Position ?? 0;
        var uv = semantics?.Uv0 ?? -1;
        var meshes = new RayMesh[ordered.Count];
        Parallel.For(0, ordered.Count, i =>
        {
            var s = ordered[i];
            var positions = new Vector3[s.VertexCount];
            var uvs = uv >= 0 ? new Vector2[s.VertexCount] : null;
            for (var v = 0; v < s.VertexCount; v++)
            {
                var at = v * stride + position;
                positions[v] = new Vector3(
                    BitConverter.ToSingle(s.VertexBytes, at), BitConverter.ToSingle(s.VertexBytes, at + 4), BitConverter.ToSingle(s.VertexBytes, at + 8));
                if (uvs is not null) uvs[v] = new Vector2(BitConverter.ToSingle(s.VertexBytes, v * stride + uv), BitConverter.ToSingle(s.VertexBytes, v * stride + uv + 4));
            }
            var scale = s.Worlds.Select(w => MathF.Max(new Vector3(w.M11, w.M12, w.M13).Length(),
                MathF.Max(new Vector3(w.M21, w.M22, w.M23).Length(), new Vector3(w.M31, w.M32, w.M33).Length()))).DefaultIfEmpty(1f).Max();
            var level = 0;
            for (var l = 1; l < s.Lods.Count && lodLimit > 0f; l++)
            {
                if (s.Lods[l].Error * scale <= lodLimit) level = l;
                else break;
            }
            var lod = s.Lods[level];
            meshes[i] = new RayMesh(positions, lod.Indices32 ?? Array.ConvertAll(lod.Indices16!, x => (uint)x), uvs);
        });
        return meshes;
    }

    // Where the ray scene's hierarchies are kept (StartRayScene): the scene's "gi" directory beside its packs when it has
    // one, else the user's cache.
    private static string rayStoreDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache", "blix", "transport");

    private static void ChooseRayStore(string assetsRoot)
    {
        var bundle = Path.Combine(assetsRoot, "gi");
        if (Directory.Exists(bundle)) rayStoreDir = bundle;
    }

    // The ray scene's build and its packing for the GPU, in the background (TryFinishLoad waits on the task, the window
    // going on); FinishRayScene, on the frame thread, uploads it and starts RTGI's cook (SponzaLoop.RtgiCook.cs).
    private Task<(RayQueryScene Scene, RayQueryGpuData? Data, double BuildMs, int Meshes, int Placements)>? rayBuildTask;

    private void StartRayScene(RayMesh[] meshes, List<RayQueryScene.Instance> instances)
    {
        DebugProgress.Begin("ray scene: build and pack", 0, "load");
        var regionTriangles = rayRegionTriangles;
        var storeDir = rayStoreDir;
        rayBuildTask = Task.Run(() =>
        {
            var clock = Stopwatch.StartNew();
            // The hierarchies are the geometry's: kept in the scene's gi directory (or in the user's cache), read
            // back for the same placements instead of built (29.5 s on Sponza, 2026-10-16).
            var key = RayQueryScene.Key(instances, regionTriangles);
            var keyMs = clock.Elapsed.TotalMilliseconds;
            var path = Path.Combine(storeDir, key + ".ray-scene");
            RayQueryScene? scene = null;
            if (File.Exists(path))
            {
                try
                {
                    using var stream = new BufferedStream(File.OpenRead(path), 1 << 22);
                    scene = RayQueryScene.Read(stream, instances, key);
                    if (scene is not null)
                        Console.WriteLine(string.Create(Inv,
                            $"[VulkanSponza] ray scene: read from {path} ({new FileInfo(path).Length / 1048576.0:0} MB, {clock.Elapsed.TotalSeconds:0.0} s; the key {keyMs:0} ms of it)."));
                }
                catch (Exception e) when (e is IOException or EndOfStreamException or InvalidDataException)
                {
                    Console.WriteLine($"[VulkanSponza] ray scene: {path} could not be read ({e.Message}); building it.");
                }
            }
            if (scene is null)
            {
                scene = RayQueryScene.Build(instances, regionTriangles);
                var built = scene;
                // Written beside the boot, once: the next boot reads it.
                _ = Task.Run(() =>
                {
                    try
                    {
                        Directory.CreateDirectory(storeDir);
                        var temp = path + ".partial";
                        using (var stream = new BufferedStream(File.Create(temp), 1 << 22)) built.Write(stream, key);
                        File.Move(temp, path, overwrite: true);
                        Console.WriteLine(string.Create(Inv, $"[VulkanSponza] ray scene: kept at {path} ({new FileInfo(path).Length / 1048576.0:0} MB)."));
                    }
                    catch (IOException e) { Console.WriteLine($"[VulkanSponza] ray scene: not kept ({e.Message})."); }
                });
            }
            var buildMs = clock.Elapsed.TotalMilliseconds;
            var data = instances.Count > 0 ? RayQueryGpuData.Pack(scene) : null;
            if (data is not null && Environment.GetEnvironmentVariable("BLIX_RAYSCENE_HASH") == "1")
                Console.WriteLine("[VulkanSponza] ray scene: packed blocks " + string.Join(" ", data.Blocks().Select(b => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(b))[..12])));
            return (scene, data, buildMs, meshes.Length, instances.Count);
        });
    }

    private void FinishRayScene()
    {
        DebugProgress.End("ray scene: build and pack");
        var (scene, data, buildMs, meshCount, placements) = rayBuildTask!.Result;
        // Not the finished task: it holds the scene and its packing for as long as it lives, and once held 435 MB past the
        // point the scene was let go (2026-10-19). A completed one in its place.
        rayBuildTask = Task.FromResult<(RayQueryScene, RayQueryGpuData?, double, int, int)>(default);
        rayQueries = scene;
        rayReach = (scene.Nodes[0].Bounds.Max - scene.Nodes[0].Bounds.Min).Length();
        var nodes = rayQueries.MeshNodeCount;
        Console.WriteLine(string.Create(Inv,
            $"[VulkanSponza] ray scene: {placements:N0} placements of {meshCount:N0} meshes as {rayQueries.RegionCount:N0} regions (at most {rayRegionTriangles:N0} triangles) and {rayQueries.InstanceEntryCount:N0} instances, {rayQueries.StoredTriangleCount:N0} triangles stored, built in {buildMs:0} ms on {Environment.ProcessorCount} threads in the background; {nodes:N0} nodes ({nodes * BvhNode.SizeInBytes / 1048576.0:0.0} MB), top level {rayQueries.Nodes.Length:N0}."));
        // The GPU scene and RTGI's cook are the diffuse GI's, so both are built whenever there is a scene.
        if (data is not null) BuildRayGpu(data);
        if (placements > 0) StartRtgiCook(scene);
    }

    // The ray scene on the GPU, for whatever traces it (the Metal tracer's build, the GPU reference, RTGI): the packed
    // blocks, and the surface bake's inputs.
    private ShaderBufferBinding[] rayBlockBuffers = Array.Empty<ShaderBufferBinding>();

    // The software traversal's node blocks (TopNodes, Nodes) are needed only by what traces in GLSL (ray_query.glsl): the
    // batch kernels (ray_batch_closest.comp, ray_batch_shadow.comp), which answer the GPU reference's batches when there is
    // no Metal tracer. With frame rays on Metal the nodes are left out (placeholders bound; ~220 MB on Sponza), and a
    // Metal tracer that fails to build brings them back (RestoreRayTraversal).
    private bool rayTraversalDropped;
    private bool SoftwareTraversalNeeded => !FrameRaysOnHardware;

    private void BuildRayGpu(RayQueryGpuData data)
    {
        rayGpuData = data;
        var blocks = data.Blocks();
        (rayAlphaPlan, rayAlphaMaterials) = PlanRayAlpha(data, ref blocks[6]);
        var buffers = new List<ShaderBufferBinding>();
        rayTraversalDropped = !SoftwareTraversalNeeded;
        for (var b = 0; b < blocks.Length; b++)
        {
            var content = rayTraversalDropped && b is 0 or 2 ? new byte[16] : blocks[b];
            buffers.Add(new ShaderBufferBinding(RayQueryGpuData.BlockNames[b],
                Own(device.CreateGpuBuffer(Math.Max(16, content.Length), content, $"sponza.{RayQueryGpuData.BlockNames[b]}"))));
        }
        if (rayTraversalDropped)
            Console.WriteLine(string.Create(Inv,
                $"[VulkanSponza] ray scene: the software traversal's nodes left off the GPU ({(blocks[0].Length + blocks[2].Length) / 1048576.0:0} MB): every trace goes to Metal."));
        raySurfaces = buffers[^1].Buffer;

        // The surface bake's inputs: every triangle row grouped by its placement's material, one run per material.
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
            raySurfaceBakes.Add((material, bakeRows.Count, rows.Count));
            bakeRows.AddRange(rows);
        }
        var uvBytes = MemoryMarshal.AsBytes(data.Uvs.AsSpan()).ToArray();
        var bakeRowBytes = MemoryMarshal.AsBytes(CollectionsMarshal.AsSpan(bakeRows)).ToArray();
        raySurfaceBakeBuffers = new[]
        {
            new ShaderBufferBinding("BlixRayTriangles", buffers[3].Buffer),
            new ShaderBufferBinding("BlixRayUvs", Own(device.CreateGpuBuffer(Math.Max(16, uvBytes.Length), uvBytes, "sponza.ray.uvs"))),
            new ShaderBufferBinding("BakeRows", Own(device.CreateGpuBuffer(Math.Max(16, bakeRowBytes.Length), bakeRowBytes, "sponza.ray.bake-rows"))),
            new ShaderBufferBinding("BlixRaySurfaces", raySurfaces),
            new ShaderBufferBinding("BlixRayTransmission", rayTransmissionRows = Own(device.CreateGpuBuffer(Math.Max(16, data.RowPlacements.Length * 4),
                MemoryMarshal.AsBytes(Enumerable.Repeat(255u, Math.Max(4, data.RowPlacements.Length)).ToArray().AsSpan()), "sponza.ray.transmission"))),
            new ShaderBufferBinding("BlixRayMr", rayMrRows = Own(device.CreateGpuBuffer(Math.Max(16, data.RowPlacements.Length * 4), name: "sponza.ray.mr"))),
        };
        rayBlockBuffers = buffers.ToArray();
        Console.WriteLine(string.Create(Inv,
            $"[VulkanSponza] ray scene on the GPU: {data.SizeInBytes / 1048576.0:0.0} MB ({data.Nodes.Length:N0} mesh nodes, {data.Triangles.Length / 4:N0} triangles, {data.Positions.Length:N0} vertices, {data.Instances.Length:N0} entries)."));
    }

    // The node blocks back on the GPU, for a trace in GLSL after all. Every binding is by name from rayBlockBuffers, so
    // the next frame's dispatches take them.
    private void RestoreRayTraversal(string why)
    {
        if (!rayTraversalDropped || rayGpuData is null) return;
        var blocks = rayGpuData.Blocks();
        foreach (var b in new[] { 0, 2 })
            rayBlockBuffers[b] = new ShaderBufferBinding(RayQueryGpuData.BlockNames[b],
                Own(device.CreateGpuBuffer(Math.Max(16, blocks[b].Length), blocks[b], $"sponza.{RayQueryGpuData.BlockNames[b]}")));
        rayTraversalDropped = false;
        Console.WriteLine($"[VulkanSponza] ray scene: the software traversal's nodes restored ({why}).");
    }

    // Once, on the first frame every texture is resident: one dispatch per material into the surfaces block, declared
    // ahead of every pass that traces.
    // The frame the bake was recorded in, and how many times; a few frames on the surfaces are read back, and a bake that
    // left every word unbaked white is recorded again (the testbeds' surfaces were never baked: albedo 1 everywhere --
    // the GI's hits and the GPU reference all read it, the raster drew the real colours).
    private int raySurfaceBakeFrame = -1, raySurfaceBakeTries;
    private bool raySurfacesChecked;
    // What everything that reads the surfaces waits for: baked, and read back to have been (the frame tracer's coverage,
    // the references and RTGI).
    private bool RaySurfacesReady => raySurfacesChecked;

    private void RecordRaySurfaceBake()
    {
        if (raySurfacesBaked && !raySurfacesChecked && rayGpuData is { } data
            && postLoadFrames >= raySurfaceBakeFrame + device.MaxFramesInFlightCount + 1)
        {
            device.WaitIdle();
            var words = MemoryMarshal.Cast<byte, uint>(device.ReadGpuBuffer(raySurfaces, 0, data.RowPlacements.Length * 4).AsSpan());
            var white = 0;
            foreach (var w in words) if (w == RayQueryGpuData.SolidWhite) white++;
            Console.WriteLine(string.Create(Inv,
                $"[VulkanSponza] ray surfaces: read back after bake {raySurfaceBakeTries} (recorded at post-load frame {raySurfaceBakeFrame}): {white:N0} of {words.Length:N0} words still the unbaked white."));
            // Which diffuse roughness levels the rows were baked at (RayQueryGpuData.SurfaceRoughness): all level 0 is a Lambert bake, or materials without roughness.
            var levels = new int[8];
            foreach (var w in words) levels[w >> 29]++;
            Console.WriteLine(string.Create(Inv, $"[VulkanSponza] ray surfaces: baked at diffuse roughness levels {string.Join(", ", Enumerable.Range(0, 8).Where(i => levels[i] > 0).Select(i => $"{RayQueryGpuData.SurfaceRoughness[i]:0.##} x{levels[i]:N0}"))}."));
            if (white == words.Length && raySurfaceBakeTries < 3) raySurfacesBaked = false;   // record it again
            else
            {
                raySurfacesChecked = true;
                // The alpha atlas read back, for the CPU's traversals (the cooks, the oracle): its per-hit test, as the GPU's.
                if (rayAlphaPlan is { } plan && rayQueries is { } cpuScene)
                    cpuScene.AlphaTrailer = MemoryMarshal.Cast<byte, uint>(device.ReadGpuBuffer(raySurfaces, data.RowPlacements.Length * 4, plan.Words.Length * 4).AsSpan()).ToArray();
            }
        }
        if (raySurfaceBakes.Count == 0 && rayGpuData is not null) raySurfacesChecked = true;   // nothing to bake
        // Frames in flight past the last mip's upload (ResourceUploader queues it with a frame's commands, no wait): baked on
        // the first frame after it, the teacup's factor texture read 51%, 0% and 35% of the factor in three runs alike
        // (2026-10-22) -- and every row's albedo stood on the same race.
        if (raySurfacesBaked || raySurfaceBakes.Count == 0 || !fullyLoaded || postLoadFrames < device.MaxFramesInFlightCount + 2) return;
        raySurfaceBakeFrame = postLoadFrames;
        raySurfaceBakeTries++;
        foreach (var (material, first, count) in raySurfaceBakes)
        {
            graph.Dispatch(raySurfaceBakePassHandle, new DispatchCommand(raySurfaceBakePipeline, (count + 63) / 64, 1, 1,
                new ShaderUniform[]
                {
                    new("uBaseColor", new Vector4Uniform(material.BaseColor)),
                    new("uParams", new Vector4Uniform(new Vector4(first, count, material.AlphaCutoff, material.Clear ? 1f : 0f))),
                    new("uParams2", new Vector4Uniform(new Vector4(material.Transmission.Equals(default(TextureHandle)) ? 0f : 1f,
                        material.TransmissionColor.Equals(default(TextureHandle)) ? 0f : 1f, RayAlphaMip(material), RayStochastic(material) ? 1f : 0f))),
                    new("uDiffuse", new Vector4Uniform(BakeDiffuse(material))),
                    new("uDiffuse2", new Vector4Uniform(new Vector4(material.DiffuseRoughness, material.Metallic, material.Emissive.Equals(default(TextureHandle)) ? 0f : 1f, 0f))),
                    new("uPane", new Vector4Uniform(new Vector4(material.PaneStops, material.PaneAlbedo, 0f, 0f))),
                },
                new[] { new ShaderTextureBinding("uAlbedo", material.Albedo),
                    new ShaderTextureBinding("uTransmission", material.Transmission.Equals(default(TextureHandle)) ? material.Albedo : material.Transmission),
                        new ShaderTextureBinding("uTransmissionColor", material.TransmissionColor.Equals(default(TextureHandle)) ? material.Albedo : material.TransmissionColor),
                        BakeMetallicRoughness(material), BakeEmissive(material) },
                Buffers: raySurfaceBakeBuffers));
        }
        RecordRayAlphaBake(rayAlphaPlan, rayAlphaMaterials, rayGpuData!, raySurfaceBakeBuffers);
        raySurfacesBaked = true;
        Console.WriteLine(string.Create(Inv, $"[VulkanSponza] ray surfaces: baked {rayGpuData!.RowPlacements.Length:N0} triangles' albedo and coverage in {raySurfaceBakes.Count} material dispatches."));
    }
}

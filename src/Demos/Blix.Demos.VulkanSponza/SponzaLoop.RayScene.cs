using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using Blix.Geometry;
using Blix.Graphics;

namespace Blix.Demos.VulkanSponza;

// --ray-scene: the scene under CPU ray-query hierarchies (Blix.Geometry), built at load over every primitive's
// full-detail triangles: regions over the placements of meshes used once, an instance per placement of a mesh
// used more. Stage 3's measure of whether a load-time build fits these scenes, and what a CPU ray costs in them.
internal sealed partial class SponzaLoop
{
    private bool rayScene;
    // The probe injection traces its rays through the ray scene (sky_inject_traced.comp) unless --gi-march asks for
    // the baked occupancy grid's march. --ab trace alternates the two in one process.
    private bool giTrace;
    private PipelineHandle injectTracedPipeline;
    private bool InjectTracedNow => injectTracedPipeline.Id != 0 && rayBlockBuffers.Length > 0 && raySurfacesBaked
        && (abMode == "trace" ? !AbOffPhase : giTrace);
    private RayQueryScene? rayQueries;
    // Per ray-scene placement (the transform table's order): the material a surface bake reads.
    private readonly record struct RayMaterial(TextureHandle Albedo, Vector4 BaseColor, float AlphaCutoff);
    private readonly List<RayMaterial> rayPlacementMaterials = new();
    private RayQueryGpuData? rayGpuData;
    private GpuBufferHandle raySurfaces;
    // One bake dispatch per material: its albedo, factor and cutoff, and its run of triangle rows in BakeRows.
    private readonly List<(RayMaterial Material, int First, int Count)> raySurfaceBakes = new();
    private ShaderBufferBinding[] raySurfaceBakeBuffers = Array.Empty<ShaderBufferBinding>();
    private ShaderInterface raySurfaceBakeInterface = null!;
    private PassHandle raySurfaceBakePassHandle;
    private PipelineHandle raySurfaceBakePipeline;
    private bool raySurfacesBaked;
    // --ray-region-triangles N: the most triangles a region holds before it is split (RayQueryScene).
    private int rayRegionTriangles = RayQueryScene.DefaultRegionTriangles;
    // --ray-lod-error M: trace each mesh at its coarsest cooked level whose geometric error, carried into the world
    // by the largest scale any of its placements gives it, stays within M metres. 0 traces full detail. 2 cm by
    // default: the traced probe field's error against the full-detail reference is unchanged there (Sponza 0.0027,
    // Bistro 0.0050) for 506 MB on the GPU rather than 781 (Sponza).
    private float rayLodError = 0.02f;

    private RayMesh[] BuildRayMeshes(List<DrawableStaging> ordered)
    {
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
            for (var l = 1; l < s.Lods.Count && rayLodError > 0f; l++)
            {
                if (s.Lods[l].Error * scale <= rayLodError) level = l;
                else break;
            }
            var lod = s.Lods[level];
            meshes[i] = new RayMesh(positions, lod.Indices32 ?? Array.ConvertAll(lod.Indices16!, x => (uint)x), uvs);
        });
        return meshes;
    }

    private void BuildRayScene(RayMesh[] meshes, List<RayQueryScene.Instance> instances)
    {
        var clock = Stopwatch.StartNew();
        rayQueries = RayQueryScene.Build(instances, rayRegionTriangles);
        var nodes = rayQueries.MeshNodeCount;
        Console.WriteLine(string.Create(Inv,
            $"[VulkanSponza] ray scene: {instances.Count:N0} placements of {meshes.Length:N0} meshes as {rayQueries.RegionCount:N0} regions (at most {rayRegionTriangles:N0} triangles) and {rayQueries.InstanceEntryCount:N0} instances, {rayQueries.StoredTriangleCount:N0} triangles stored, built in {clock.Elapsed.TotalMilliseconds:0} ms on {Environment.ProcessorCount} threads; {nodes:N0} nodes ({nodes * BvhNode.SizeInBytes / 1048576.0:0.0} MB), top level {rayQueries.Nodes.Length:N0}."));
        if ((rayCheck || giTrace || probeReference) && instances.Count > 0) BuildRayGpu();
        if (rayCheck && instances.Count > 0) BuildRayCheck();
    }

    // --ray-check. The scene packed for ray_query.glsl, and a fixed batch of rays: half from random points in the
    // scene's box in random directions, half aimed at random placements' triangles (half of those at an interior
    // point, half at a vertex or an edge's midpoint), a fifth of them stopped short at a finite tMax. The GPU traces them every frame; the shot holds the last answers
    // against RayQueryScene's, ray for ray.
    private bool rayCheck;
    private ShaderInterface rayCheckInterface = null!;
    private PassHandle rayCheckPassHandle;
    private PipelineHandle rayCheckPipeline;
    private ShaderBufferBinding[] rayCheckBuffers = Array.Empty<ShaderBufferBinding>();
    private GpuBufferHandle rayCheckHits;
    private Ray[] rayCheckRays = Array.Empty<Ray>();
    private float[] rayCheckTMax = Array.Empty<float>();
    private const int RayCheckCount = 65536;
    // --ray-bench <mixed|probe|camera>: closest hit only, on a batch shaped like one use (null: --ray-check's mix
    // with any-hit too). probe: 2,048 points on a grid through the scene, 32 directions each, as GI probes cast;
    // camera: the start view's primary rays.
    private string? rayBench;
    // --ray-probe <ray> <placement> <triangle>: also run that ray against that triangle alone on the GPU.
    private (int Ray, int Instance, int Triangle)? rayCheckProbe;
    // The top-level entry the probe's placement is: it must be an instance, since a region's triangles are renumbered.
    private int rayProbeEntry = -1;

    // The ray scene on the GPU, for whatever traces it (the check, the view, the traced injection): the packed
    // blocks, and the surface bake's inputs.
    private ShaderBufferBinding[] rayBlockBuffers = Array.Empty<ShaderBufferBinding>();

    private void BuildRayGpu()
    {
        var data = RayQueryGpuData.Pack(rayQueries!);
        rayGpuData = data;
        var blocks = data.Blocks();
        var buffers = new List<ShaderBufferBinding>();
        for (var b = 0; b < blocks.Length; b++)
        {
            buffers.Add(new ShaderBufferBinding(RayQueryGpuData.BlockNames[b],
                Own(device.CreateGpuBuffer(Math.Max(16, blocks[b].Length), blocks[b], $"sponza.{RayQueryGpuData.BlockNames[b]}"))));
        }
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
        };
        rayBlockBuffers = buffers.ToArray();
        Console.WriteLine(string.Create(Inv,
            $"[VulkanSponza] ray scene on the GPU: {data.SizeInBytes / 1048576.0:0.0} MB ({data.Nodes.Length:N0} mesh nodes, {data.Triangles.Length / 4:N0} triangles, {data.Positions.Length:N0} vertices, {data.Instances.Length:N0} entries)."));
    }

    private void BuildRayCheck()
    {
        var data = rayGpuData!;
        var buffers = rayBlockBuffers.ToList();
        var rng = new Random(20261004);
        var bounds = rayQueries!.Nodes[0].Bounds;
        Vector3 InBox() => bounds.Min + (bounds.Max - bounds.Min) * new Vector3((float)rng.NextDouble(), (float)rng.NextDouble(), (float)rng.NextDouble());
        Vector3 Direction()
        {
            Vector3 v;
            do v = new Vector3((float)rng.NextDouble() * 2 - 1, (float)rng.NextDouble() * 2 - 1, (float)rng.NextDouble() * 2 - 1);
            while (v.LengthSquared() is < 1e-4f or > 1f);
            return Vector3.Normalize(v);
        }
        rayCheckRays = new Ray[RayCheckCount];
        rayCheckTMax = new float[RayCheckCount];
        var packed = new Vector4[RayCheckCount * 2];
        var instances = rayQueries.Instances;
        Matrix4x4.Invert(viewProj, out var invViewProj);
        for (var i = 0; i < RayCheckCount; i++)
        {
            var origin = InBox();
            var direction = Direction();
            if (rayBench is "probe" or "camera")
            {
                if (rayBench == "probe")
                {
                    // 16 x 8 x 16 grid points, cell centres, each casting 32 random directions.
                    var cell = i / 32;
                    var g = new Vector3(cell % 16, (cell / 16) % 8, cell / 128);
                    origin = bounds.Min + (bounds.Max - bounds.Min) * (g + new Vector3(0.5f)) / new Vector3(16f, 8f, 16f);
                }
                else
                {
                    // 256 x 256 over the view.
                    var ndc = new Vector2((i % 256 + 0.5f) / 256f * 2f - 1f, (i / 256 + 0.5f) / 256f * 2f - 1f);
                    var far = Vector4.Transform(new Vector4(ndc, 1f, 1f), invViewProj);
                    origin = cameraPosition;
                    direction = Vector3.Normalize(new Vector3(far.X, far.Y, far.Z) / far.W - cameraPosition);
                }
                rayCheckRays[i] = new Ray(origin, direction);
                rayCheckTMax[i] = float.PositiveInfinity;
                packed[i * 2] = new Vector4(origin, 0f);
                packed[i * 2 + 1] = new Vector4(direction, float.PositiveInfinity);
                continue;
            }
            if (i % 2 == 1)
            {
                var inst = instances[rng.Next(instances.Count)];
                if (inst.Mesh.TriangleCount > 0)
                {
                    var tri = rng.Next(inst.Mesh.TriangleCount);
                    var a = inst.Mesh.Positions[inst.Mesh.Indices[tri * 3]];
                    var b = inst.Mesh.Positions[inst.Mesh.Indices[tri * 3 + 1]];
                    var c = inst.Mesh.Positions[inst.Mesh.Indices[tri * 3 + 2]];
                    var u = (float)rng.NextDouble();
                    var v = (float)rng.NextDouble() * (1 - u);
                    // Every other aimed ray goes exactly at what triangles share, a vertex or an edge's midpoint:
                    // where a test that is not watertight lets a ray through.
                    var onMesh = (i / 2) % 2 == 0
                        ? a + (b - a) * u + (c - a) * v
                        : (i / 4) % 2 == 0 ? a : (a + b) * 0.5f;
                    var target = Vector3.Transform(onMesh, inst.World);
                    if (Vector3.DistanceSquared(target, origin) > 1e-6f) direction = Vector3.Normalize(target - origin);
                }
            }
            var tMax = i % 5 == 0 ? (bounds.Max - bounds.Min).Length() * 0.1f : float.PositiveInfinity;
            rayCheckRays[i] = new Ray(origin, direction);
            rayCheckTMax[i] = tMax;
            packed[i * 2] = new Vector4(origin, 0f);
            packed[i * 2 + 1] = new Vector4(direction, tMax);
        }
        var raysBuffer = Own(device.CreateGpuBuffer(packed.Length * 16, MemoryMarshal.AsBytes(packed.AsSpan()), "sponza.ray-check.rays"));
        rayCheckHits = Own(device.CreateGpuBuffer(RayCheckCount * 48 + 80, name: "sponza.ray-check.hits"));
        buffers.Add(new ShaderBufferBinding("RayCheckRays", raysBuffer));
        buffers.Add(new ShaderBufferBinding("RayCheckHits", rayCheckHits));
        rayCheckBuffers = buffers.ToArray();
        if (rayCheckProbe is { } probe)
        {
            rayProbeEntry = Array.FindIndex(data.Instances, x => x.Placement == (uint)probe.Instance);
            if (rayProbeEntry < 0)
            {
                Console.WriteLine($"[VulkanSponza] ray probe: placement {probe.Instance} is merged into a region; the probe takes instanced placements only.");
            }
        }
        Console.WriteLine(string.Create(Inv,
            $"[VulkanSponza] ray check: {RayCheckCount:N0} {rayBench ?? "check"} rays."));
    }

    // Once, on the first frame every texture is resident: one dispatch per material into the surfaces block, declared
    // ahead of every pass that traces.
    private void RecordRaySurfaceBake()
    {
        if (raySurfacesBaked || raySurfaceBakes.Count == 0 || !fullyLoaded) return;
        foreach (var (material, first, count) in raySurfaceBakes)
        {
            graph.Dispatch(raySurfaceBakePassHandle, new DispatchCommand(raySurfaceBakePipeline, (count + 63) / 64, 1, 1,
                new ShaderUniform[]
                {
                    new("uBaseColor", new Vector4Uniform(material.BaseColor)),
                    new("uParams", new Vector4Uniform(new Vector4(first, count, material.AlphaCutoff, 0f))),
                },
                new[] { new ShaderTextureBinding("uAlbedo", material.Albedo) },
                Buffers: raySurfaceBakeBuffers));
        }
        raySurfacesBaked = true;
        Console.WriteLine(string.Create(Inv, $"[VulkanSponza] ray surfaces: baked {rayGpuData!.RowPlacements.Length:N0} triangles' albedo and coverage in {raySurfaceBakes.Count} material dispatches."));
    }

    private void RecordRayCheck()
    {
        if (rayCheckBuffers.Length == 0 || !fullyLoaded) return;
        graph.Dispatch(rayCheckPassHandle, new DispatchCommand(rayCheckPipeline, (RayCheckCount + 63) / 64, 1, 1,
            new ShaderUniform[]
            {
                new("uCount", new Vector4Uniform(new Vector4(RayCheckCount, rayBench is null ? 0f : 1f, 0f, 0f))),
                new("uProbe", new Vector4Uniform(rayCheckProbe is { } p && rayProbeEntry >= 0 ? new Vector4(p.Ray + 1, rayProbeEntry, p.Triangle, 0f) : Vector4.Zero)),
            },
            Array.Empty<ShaderTextureBinding>(), Buffers: rayCheckBuffers));
    }

    // The last frame's GPU answers against the CPU's, ray for ray: the same hit or miss, the same placement and
    // triangle, and how far the distance and barycentrics are apart (the GPU may fuse a multiply-add the CPU rounds
    // twice, so they need not match to the bit).
    private void WriteRayCheck()
    {
        if (rayCheckBuffers.Length == 0 || rayQueries is null) return;
        // The baked coverage, back to the CPU hierarchies, so the oracle meets the same partly covered triangles.
        if (rayGpuData is { } gpuData)
        {
            var surfaceWords = MemoryMarshal.Cast<byte, uint>(device.ReadGpuBuffer(raySurfaces, 0, gpuData.RowPlacements.Length * 4).AsSpan()).ToArray();
            gpuData.ApplyCoverage(surfaceWords);
            var partly = surfaceWords.Count(w => (w >> 24) is > 0 and < 255);
            var none = surfaceWords.Count(w => w >> 24 == 0);
            Console.WriteLine(string.Create(Inv, $"[VulkanSponza] ray surfaces: {partly:N0} triangles partly covered, {none:N0} not at all, of {surfaceWords.Length:N0}."));
        }
        var bytes = device.ReadGpuBuffer(rayCheckHits, 0, RayCheckCount * 48 + 80);
        var words = MemoryMarshal.Cast<byte, uint>(bytes.AsSpan()).ToArray();
        int hits = 0, hitMismatch = 0, anyMismatch = 0, sameTriangle = 0, otherTriangle = 0, otherTriangleSameT = 0, faceMismatch = 0;
        double maxRelT = 0, maxBary = 0, maxNormal = 0;
        var relBuckets = new int[4];   // same triangle, relative distance error above 1e-6, 1e-5, 1e-4, 1e-3
        var worst = -1;
        var worstOther = -1;
        for (var i = 0; i < RayCheckCount; i++)
        {
            var o = i * 12;
            var gpuHit = (words[o + 3] & 1u) != 0;
            var gpuAny = (words[o + 3] & 4u) != 0;
            var cpu = rayQueries.Closest(rayCheckRays[i], 0f, rayCheckTMax[i], (uint)i);
            if (rayBench is null && gpuAny != rayQueries.Any(rayCheckRays[i], 0f, rayCheckTMax[i], (uint)i)) anyMismatch++;
            if (gpuHit != cpu.HasValue) { hitMismatch++; continue; }
            if (cpu is not { } c) continue;
            hits++;
            var t = BitConverter.UInt32BitsToSingle(words[o]);
            // Against the size of the numbers involved (how far from the origin the ray starts, plus how far it went):
            // float rounding is relative to that, and a hit a millimetre from its origin has no meaningful relative t.
            var rel = Math.Abs(t - c.T) / Math.Max(1e-6, rayCheckRays[i].Origin.Length() + Math.Abs(c.T));
            if ((int)words[o + 4] == c.Instance && (int)words[o + 5] == c.Triangle)
            {
                sameTriangle++;
                if (rel > maxRelT) worst = i;
                maxRelT = Math.Max(maxRelT, rel);
                for (var k = 0; k < 4; k++) if (rel > Math.Pow(10, -6 + k)) relBuckets[k]++;
                var b = new Vector2(BitConverter.UInt32BitsToSingle(words[o + 1]), BitConverter.UInt32BitsToSingle(words[o + 2]));
                maxBary = Math.Max(maxBary, Vector2.Distance(b, c.Barycentrics));
                if (((words[o + 3] & 2u) != 0) != c.FrontFace) faceMismatch++;
                // The normal against the CPU's: the mesh triangle carried into the world, authored front, unit.
                var mesh = rayQueries.Instances[c.Instance].Mesh;
                var world = rayQueries.Instances[c.Instance].World;
                var q0 = Vector3.Transform(mesh.Positions[mesh.Indices[c.Triangle * 3]], world);
                var q1 = Vector3.Transform(mesh.Positions[mesh.Indices[c.Triangle * 3 + 1]], world);
                var q2 = Vector3.Transform(mesh.Positions[mesh.Indices[c.Triangle * 3 + 2]], world);
                var cpuNormal = Vector3.Normalize(Vector3.Cross(q1 - q0, q2 - q0) * (world.GetDeterminant() < 0f ? -1f : 1f));
                var gpuNormal = new Vector3(BitConverter.UInt32BitsToSingle(words[o + 8]), BitConverter.UInt32BitsToSingle(words[o + 9]), BitConverter.UInt32BitsToSingle(words[o + 10]));
                if (float.IsFinite(cpuNormal.X)) maxNormal = Math.Max(maxNormal, Vector3.Distance(cpuNormal, gpuNormal));
            }
            else
            {
                otherTriangle++;
                // A different triangle at the same distance is a tie the two broke differently (coplanar copies, an
                // edge both own), not a wrong answer.
                if (rel < 1e-4) otherTriangleSameT++;
                else worstOther = i;
            }
        }
        Console.WriteLine(string.Create(Inv,
            $"[VulkanSponza] ray check, GPU against CPU over {RayCheckCount:N0} rays ({hits:N0} hit): hit/miss differ on {hitMismatch}, any-hit on {anyMismatch}; same placement and triangle on {sameTriangle:N0}, another on {otherTriangle} ({otherTriangleSameT} of them at the same distance); distance within {maxRelT:0.0e0} relative, barycentrics within {maxBary:0.0e0}, facing differs on {faceMismatch}, normals within {maxNormal:0.0e0}."));
        Console.WriteLine(string.Create(Inv,
            $"[VulkanSponza] ray check: same-triangle distance error, relative to the coordinates' scale, above 1e-6 / 1e-5 / 1e-4 / 1e-3 on {relBuckets[0]} / {relBuckets[1]} / {relBuckets[2]} / {relBuckets[3]} rays."));
        // What a closest-hit trace did per ray: nodes popped (both levels), placements entered, triangles tested.
        var nodes = new long[RayCheckCount];
        var entered = new long[RayCheckCount];
        var tested = new long[RayCheckCount];
        for (var i = 0; i < RayCheckCount; i++)
        {
            nodes[i] = words[i * 12 + 6];
            entered[i] = words[i * 12 + 3] >> 8;
            tested[i] = words[i * 12 + 7];
        }
        string Spread(long[] v)
        {
            var sorted = v.OrderBy(x => x).ToArray();
            return string.Create(Inv, $"mean {v.Average():0.0}, median {sorted[sorted.Length / 2]}, p95 {sorted[(int)(sorted.Length * 0.95)]}, max {sorted[^1]}");
        }
        Console.WriteLine(string.Create(Inv,
            $"[VulkanSponza] ray work per {rayBench ?? "check"} ray, closest hit: nodes {Spread(nodes)}; placements entered {Spread(entered)}; triangles tested {Spread(tested)}; {100.0 * hits / RayCheckCount:0.0}% hit."));
        if (host.Timing.GpuPassTotals.TryGetValue("ray-check", out var pass) && pass.Samples > 0)
        {
            var traced = rayBench is null ? 2 * RayCheckCount : RayCheckCount;
            Console.WriteLine(string.Create(Inv,
                $"[VulkanSponza] ray {(rayBench is null ? "check" : "bench")}: {pass.MeanMs:0.000} ms per dispatch over {pass.Samples} frames, {traced / pass.MeanMs / 1000.0:0.0} M rays/s{(rayBench is null ? " (closest and any)" : " (closest)")}."));
        }

        if (rayCheckProbe is { } probe && rayProbeEntry >= 0)
        {
            var w = RayCheckCount * 12;
            var inst = rayQueries.Instances[probe.Instance];
            Matrix4x4.Invert(inst.World, out var inv);
            var ray = rayCheckRays[probe.Ray];
            var local = new ShearedRay(Vector3.Transform(ray.Origin, inv), Vector3.TransformNormal(ray.Direction, inv));
            var cpuHit = inst.Mesh.Hit(local, probe.Triangle, 0f, rayCheckTMax[probe.Ray], out var ct, out var cb, out _);
            float F(int k) => BitConverter.UInt32BitsToSingle(words[w + k]);
            var m = inst.Mesh;
            Vector3 P(int k) => m.Positions[m.Indices[probe.Triangle * 3 + k]];
            float C(Vector3 v, int axis) => axis == 0 ? v.X : axis == 1 ? v.Y : v.Z;
            var a = P(0) - local.Origin; var b = P(1) - local.Origin; var c = P(2) - local.Origin;
            float ax = C(a, local.Kx) - local.Sx * C(a, local.Kz), ay = C(a, local.Ky) - local.Sy * C(a, local.Kz);
            float bx = C(b, local.Kx) - local.Sx * C(b, local.Kz), by = C(b, local.Ky) - local.Sy * C(b, local.Kz);
            float cx = C(c, local.Kx) - local.Sx * C(c, local.Kz), cy = C(c, local.Ky) - local.Sy * C(c, local.Kz);
            var k = words[w + 7];
            Console.WriteLine(string.Create(Inv,
                $"[VulkanSponza] ray probe, GPU: u {F(4):R} v {F(5):R} w {F(6):R} k ({k & 15},{(k >> 4) & 15},{(k >> 8) & 15}) shear ({F(8):R}, {F(9):R}, {F(10):R}) origin ({F(12):R}, {F(13):R}, {F(14):R}) direction ({F(16):R}, {F(17):R}, {F(18):R})"));
            Console.WriteLine(string.Create(Inv,
                $"[VulkanSponza] ray probe, CPU: u {cx * by - cy * bx:R} v {ax * cy - ay * cx:R} w {bx * ay - by * ax:R} k ({local.Kx},{local.Ky},{local.Kz}) shear ({local.Sx:R}, {local.Sy:R}, {local.Sz:R}) origin ({local.Origin.X:R}, {local.Origin.Y:R}, {local.Origin.Z:R}) direction ({local.Direction.X:R}, {local.Direction.Y:R}, {local.Direction.Z:R})"));
            Console.WriteLine(string.Create(Inv,
                $"[VulkanSponza] ray probe: ray {probe.Ray} against placement {probe.Instance} triangle {probe.Triangle} alone: GPU found it {(words[w] & 1u) != 0}, hit {(words[w] & 2u) != 0} at t {BitConverter.UInt32BitsToSingle(words[w + 1]):0.######} bary ({BitConverter.UInt32BitsToSingle(words[w + 2]):0.####}, {BitConverter.UInt32BitsToSingle(words[w + 3]):0.####}); CPU hit {cpuHit} at t {ct:0.######} bary {cb}."));
        }
        foreach (var (label, i) in new[] { ("worst same-triangle", worst), ("a different triangle at another distance", worstOther) })
        {
            if (i < 0) continue;
            var c = rayQueries.Closest(rayCheckRays[i], 0f, rayCheckTMax[i], (uint)i)!.Value;
            var o = i * 12;
            var inst = rayQueries.Instances[c.Instance];
            var m = inst.Mesh;
            var p0 = m.Positions[m.Indices[c.Triangle * 3]]; var p1 = m.Positions[m.Indices[c.Triangle * 3 + 1]]; var p2 = m.Positions[m.Indices[c.Triangle * 3 + 2]];
            var area = Vector3.Cross(p1 - p0, p2 - p0).Length() * 0.5f;
            Console.WriteLine(string.Create(Inv,
                $"[VulkanSponza] ray check, {label} (ray {i}): CPU t {c.T:0.######} placement {c.Instance} triangle {c.Triangle} bary {c.Barycentrics}; GPU t {BitConverter.UInt32BitsToSingle(words[o]):0.######} placement {(int)words[o + 4]} triangle {(int)words[o + 5]} bary ({BitConverter.UInt32BitsToSingle(words[o + 1]):0.####}, {BitConverter.UInt32BitsToSingle(words[o + 2]):0.####}); triangle area {area:0.######} m², placement scale det {inst.World.GetDeterminant():0.###}, direction {rayCheckRays[i].Direction}."));
        }
    }

    // --ray-view. The camera's view traced through the same scene (ray_view.comp), half resolution, beside the
    // raster's depth at each pixel's depth sample; read back at the shot.
    private bool rayView;
    private ShaderInterface rayViewInterface = null!;
    private PassHandle rayViewPassHandle;
    private PipelineHandle rayViewPipeline;
    private GpuBufferHandle rayViewOut;
    private (int Width, int Height) rayViewSize;

    // Where in its pixel the depth resolve's sample sits: sample 0 of the standard pattern under MSAA.
    private Vector2 DepthSamplePoint => MsaaSamples switch
    {
        4 => new Vector2(0.375f, 0.125f),
        2 => new Vector2(0.75f, 0.75f),
        _ => new Vector2(0.5f, 0.5f),
    };

    private void RecordRayView(int frameWidth, int frameHeight)
    {
        if (!rayView || rayBlockBuffers.Length == 0 || !fullyLoaded) return;
        var size = (frameWidth / 2, frameHeight / 2);
        if (rayViewSize != size)
        {
            // The view is recorded at the window's size; a shot refuses a resize, so this sizes once in practice.
            rayViewOut = Own(device.CreateGpuBuffer(size.Item1 * size.Item2 * 16, name: "sponza.ray-view"));
            rayViewSize = size;
        }
        Matrix4x4.Invert(viewProjJittered, out var invViewProj);
        Matrix4x4.Invert(cameraProjection, out var invProjection);
        var buffers = rayBlockBuffers.Append(new ShaderBufferBinding("RayViewOut", rayViewOut)).ToArray();
        graph.Dispatch(rayViewPassHandle, new DispatchCommand(rayViewPipeline, (size.Item1 + 7) / 8, (size.Item2 + 7) / 8, 1,
            new ShaderUniform[]
            {
                new("uInvViewProj", new Matrix4x4Uniform(invViewProj)),
                new("uInvProjection", new Matrix4x4Uniform(invProjection)),
                new("uCamera", new Vector4Uniform(new Vector4(cameraPosition, 0f))),
                new("uForward", new Vector4Uniform(new Vector4(Vector3.Normalize(cameraForward), 0f))),
                new("uSize", new Vector4Uniform(new Vector4(size.Item1, size.Item2, frameWidth, frameHeight))),
                new("uSample", new Vector4Uniform(new Vector4(DepthSamplePoint, 0f, 0f))),
            },
            new[] { new ShaderTextureBinding("uSceneDepth", graph.GetDepthTexture(SampleableSceneDepth)) },
            Buffers: buffers));
    }

    // Every pixel classified, and both pictures written: the traced view shaded by its normals, and where the two
    // depths disagree, coloured by why.
    private void WriteRayView(string basePath)
    {
        if (!rayView || rayViewSize.Width == 0 || rayQueries is null) return;
        var (w, h) = rayViewSize;
        var words = MemoryMarshal.Cast<byte, uint>(device.ReadGpuBuffer(rayViewOut, 0, w * h * 16).AsSpan()).ToArray();
        float Traced(int i) => BitConverter.UInt32BitsToSingle(words[i * 4]);
        float Raster(int i) => BitConverter.UInt32BitsToSingle(words[i * 4 + 1]);

        // What each placement is in the raster: alpha-tested (the pre-pass discards its transparent texels; a ray has no
        // alpha test yet) or glass (not in the depth at all). The ray scene's placements are the transform table's rows.
        var masked = new bool[sceneTransforms.Count];
        var glass = new bool[sceneTransforms.Count];
        foreach (var p in opaquePlacements) masked[p.Transform] = opaqueDrawables[p.Drawable].AlphaCutoff > 0f;
        foreach (var p in blendPlacements) glass[p.Transform] = true;

        int agree = 0, bothSky = 0, foliage = 0, glassCount = 0, edge = 0, other = 0, nearAgree = 0;
        var diff = new byte[w * h * 4];
        var shaded = new byte[w * h * 4];
        var light = Vector3.Normalize(new Vector3(0.35f, 0.85f, 0.4f));
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
        {
            var i = y * w + x;
            var traced = Traced(i);
            var raster = Raster(i);
            var placement = words[i * 4 + 2];
            var o = i * 4;
            (byte R, byte G, byte B) colour;
            if (float.IsInfinity(traced) && float.IsInfinity(raster)) { bothSky++; colour = (40, 40, 40); }
            else if (!float.IsInfinity(traced) && !float.IsInfinity(raster) && MathF.Abs(traced - raster) <= 0.01f * raster)
            {
                agree++;
                if (MathF.Abs(traced - raster) <= 0.001f * raster) nearAgree++;
                colour = (0, 110, 0);
            }
            else if (placement != 0xFFFFFFFFu && placement < glass.Length && glass[placement]) { glassCount++; colour = (0, 200, 220); }
            else if (placement != 0xFFFFFFFFu && placement < masked.Length && masked[placement]) { foliage++; colour = (60, 90, 255); }
            else
            {
                // An edge: the raster's depth jumps by more than 5% to a neighbour, so a sub-pixel offset picks a different surface.
                var jump = false;
                for (var dy = -1; dy <= 1 && !jump; dy++)
                for (var dx = -1; dx <= 1 && !jump; dx++)
                {
                    var nx = x + dx; var ny = y + dy;
                    if (nx < 0 || ny < 0 || nx >= w || ny >= h) continue;
                    var r = Raster(ny * w + nx);
                    jump = float.IsInfinity(r) != float.IsInfinity(raster) || MathF.Abs(r - raster) > 0.05f * MathF.Min(r, raster);
                }
                if (jump) { edge++; colour = (230, 200, 0); }
                else { other++; colour = (255, 0, 0); }
            }
            diff[o] = colour.R; diff[o + 1] = colour.G; diff[o + 2] = colour.B; diff[o + 3] = 255;

            if (float.IsInfinity(traced)) { shaded[o] = 70; shaded[o + 1] = 90; shaded[o + 2] = 120; }
            else
            {
                var packedNormal = words[i * 4 + 3];
                float Snorm(int shift) => Math.Clamp((sbyte)((packedNormal >> shift) & 0xFF) / 127f, -1f, 1f);
                var n = new Vector3(Snorm(0), Snorm(8), Snorm(16));
                // Lit by a fixed direction, times how bright the baked albedo is: the surfaces a ray now meets.
                var albedo = (packedNormal >> 24) / 255f;
                var l = (0.25f + 0.75f * Math.Clamp(Vector3.Dot(n, light) * 0.5f + 0.5f, 0f, 1f)) * MathF.Pow(albedo, 1f / 2.2f);
                var v = (byte)Math.Clamp(l * 255f, 0f, 255f);
                shaded[o] = v; shaded[o + 1] = v; shaded[o + 2] = v;
            }
            shaded[o + 3] = 255;
        }

        var total = w * h;
        double Share(int n) => 100.0 * n / total;
        Console.WriteLine(string.Create(Inv,
            $"[VulkanSponza] ray view against the raster, {w}x{h} at each pixel's depth sample: depths agree within 1% on {Share(agree):0.00}% ({Share(nearAgree):0.00}% within 0.1%), both sky {Share(bothSky):0.00}%; disagree at alpha-tested foliage {Share(foliage):0.00}%, glass {Share(glassCount):0.00}%, depth edges {Share(edge):0.00}%, elsewhere {Share(other):0.000}% ({other} pixels)."));
        Blix.Graphics.Images.PngWriter.WriteRgba8(basePath + ".traced.png", shaded, w, h);
        Blix.Graphics.Images.PngWriter.WriteRgba8(basePath + ".traced-vs-raster.png", diff, w, h);
        Console.WriteLine($"[VulkanSponza]   {basePath}.traced.png, {basePath}.traced-vs-raster.png (green agree, blue alpha-tested, cyan glass, yellow depth edge, red elsewhere)");
    }

    // Camera rays through a grid over the view, traced on every core, at the end of a shot: what a CPU ray costs here.
    private void WriteRayBenchmark()
    {
        if (rayQueries is null) return;
        const int W = 320, H = 180;
        Matrix4x4.Invert(viewProj, out var invViewProj);
        var hits = 0;
        long hitCount = 0;
        var rays = new Ray[W * H];
        for (var y = 0; y < H; y++)
        for (var x = 0; x < W; x++)
        {
            var ndc = new Vector2((x + 0.5f) / W * 2f - 1f, (y + 0.5f) / H * 2f - 1f);
            var far = Vector4.Transform(new Vector4(ndc, 1f, 1f), invViewProj);
            var dir = Vector3.Normalize(new Vector3(far.X, far.Y, far.Z) / far.W - cameraPosition);
            rays[y * W + x] = new Ray(cameraPosition, dir);
        }
        var single = Stopwatch.StartNew();
        for (var i = 0; i < 4096; i++) if (rayQueries.Closest(rays[i * 13 % rays.Length]) is not null) hits++;
        var singleRate = 4096 / single.Elapsed.TotalSeconds;
        var all = Stopwatch.StartNew();
        Parallel.For(0, rays.Length, () => 0L, (i, _, n) => rayQueries.Closest(rays[i]) is not null ? n + 1 : n,
            n => Interlocked.Add(ref hitCount, n));
        var allRate = rays.Length / all.Elapsed.TotalSeconds;
        Console.WriteLine(string.Create(Inv, 
            $"[VulkanSponza] ray scene: {W}x{H} camera rays, closest hit: {singleRate / 1e6:0.00} M rays/s on one core, {allRate / 1e6:0.00} M rays/s on {Environment.ProcessorCount}; {100.0 * hitCount / rays.Length:0.0}% hit."));
    }
}

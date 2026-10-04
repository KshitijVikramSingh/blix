using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using Blix.Geometry;
using Blix.Graphics;

namespace Blix.Demos.VulkanSponza;

// --ray-scene: the scene under CPU ray-query hierarchies (Blix.Geometry), built at load. One TriangleBvh per
// unique primitive over its full-detail triangles, one RayQueryScene over every placement. Stage 3's first
// measure: whether a load-time build fits these scenes, and what a CPU ray costs in them.
internal sealed partial class SponzaLoop
{
    private bool rayScene;
    private RayQueryScene? rayQueries;

    private TriangleBvh[] BuildRayMeshes(List<DrawableStaging> ordered)
    {
        var clock = Stopwatch.StartNew();
        var stride = sharedLayout.Stride;
        var position = Blix.Graphics.VertexSemantics.Of(sharedLayout)?.Position ?? 0;
        var meshes = new TriangleBvh[ordered.Count];
        Parallel.For(0, ordered.Count, i =>
        {
            var s = ordered[i];
            var positions = new Vector3[s.VertexCount];
            for (var v = 0; v < s.VertexCount; v++)
            {
                var at = v * stride + position;
                positions[v] = new Vector3(
                    BitConverter.ToSingle(s.VertexBytes, at), BitConverter.ToSingle(s.VertexBytes, at + 4), BitConverter.ToSingle(s.VertexBytes, at + 8));
            }
            var lod0 = s.Lods[0];
            var indices = lod0.Indices32 ?? Array.ConvertAll(lod0.Indices16!, x => (uint)x);
            meshes[i] = TriangleBvh.Build(positions, indices);
        });
        var triangles = meshes.Sum(m => (long)m.TriangleCount);
        var nodes = meshes.Sum(m => (long)m.Nodes.Length);
        var sah = meshes.Where(m => m.TriangleCount > 0).Select(m => BvhBuilder.SahCost(m.Nodes) / m.TriangleCount).DefaultIfEmpty(0f).Average();
        Console.WriteLine(string.Create(Inv, 
            $"[VulkanSponza] ray scene: {meshes.Length} mesh hierarchies over {triangles:N0} triangles in {clock.Elapsed.TotalMilliseconds:0} ms ({Environment.ProcessorCount} threads); {nodes:N0} nodes ({nodes * BvhNode.SizeInBytes / 1048576.0:0.0} MB) + order {triangles * 4 / 1048576.0:0.0} MB; mean SAH cost {sah:0.000} of testing every triangle."));
        return meshes;
    }

    private void BuildRayScene(TriangleBvh[] meshes, List<RayQueryScene.Instance> instances)
    {
        var clock = Stopwatch.StartNew();
        rayQueries = RayQueryScene.Build(instances);
        Console.WriteLine(string.Create(Inv,
            $"[VulkanSponza] ray scene: top level over {instances.Count:N0} placements in {clock.Elapsed.TotalMilliseconds:0} ms, {rayQueries.Nodes.Length:N0} nodes."));
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
    // --ray-probe <ray> <placement> <triangle>: also run that ray against that triangle alone on the GPU.
    private (int Ray, int Instance, int Triangle)? rayCheckProbe;

    private void BuildRayCheck()
    {
        var data = RayQueryGpuData.Pack(rayQueries!);
        var blocks = data.Blocks();
        var buffers = new List<ShaderBufferBinding>();
        for (var b = 0; b < blocks.Length; b++)
        {
            buffers.Add(new ShaderBufferBinding(RayQueryGpuData.BlockNames[b],
                Own(device.CreateGpuBuffer(Math.Max(16, blocks[b].Length), blocks[b], $"sponza.{RayQueryGpuData.BlockNames[b]}"))));
        }

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
        for (var i = 0; i < RayCheckCount; i++)
        {
            var origin = InBox();
            var direction = Direction();
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
        rayCheckHits = Own(device.CreateGpuBuffer(RayCheckCount * 32 + 80, name: "sponza.ray-check.hits"));
        buffers.Add(new ShaderBufferBinding("RayCheckRays", raysBuffer));
        buffers.Add(new ShaderBufferBinding("RayCheckHits", rayCheckHits));
        rayCheckBuffers = buffers.ToArray();
        Console.WriteLine(string.Create(Inv,
            $"[VulkanSponza] ray check: scene packed for the GPU, {data.SizeInBytes / 1048576.0:0.0} MB ({data.Nodes.Length:N0} mesh nodes, {data.Triangles.Length / 4:N0} triangles, {data.Positions.Length:N0} vertices, {data.Instances.Length:N0} placements); {RayCheckCount:N0} rays."));
    }

    private void RecordRayCheck()
    {
        if (rayCheckBuffers.Length == 0 || !fullyLoaded) return;
        graph.Dispatch(rayCheckPassHandle, new DispatchCommand(rayCheckPipeline, (RayCheckCount + 63) / 64, 1, 1,
            new ShaderUniform[]
            {
                new("uCount", new Vector4Uniform(new Vector4(RayCheckCount, 0f, 0f, 0f))),
                new("uProbe", new Vector4Uniform(rayCheckProbe is { } p ? new Vector4(p.Ray + 1, p.Instance, p.Triangle, 0f) : Vector4.Zero)),
            },
            Array.Empty<ShaderTextureBinding>(), Buffers: rayCheckBuffers));
    }

    // The last frame's GPU answers against the CPU's, ray for ray: the same hit or miss, the same placement and
    // triangle, and how far the distance and barycentrics are apart (the GPU may fuse a multiply-add the CPU rounds
    // twice, so they need not match to the bit).
    private void WriteRayCheck()
    {
        if (rayCheckBuffers.Length == 0 || rayQueries is null) return;
        var bytes = device.ReadGpuBuffer(rayCheckHits, 0, RayCheckCount * 32 + 80);
        var words = MemoryMarshal.Cast<byte, uint>(bytes.AsSpan()).ToArray();
        int hits = 0, hitMismatch = 0, anyMismatch = 0, sameTriangle = 0, otherTriangle = 0, otherTriangleSameT = 0, faceMismatch = 0;
        double maxRelT = 0, maxBary = 0;
        var relBuckets = new int[4];   // same triangle, relative distance error above 1e-6, 1e-5, 1e-4, 1e-3
        var worst = -1;
        var worstOther = -1;
        for (var i = 0; i < RayCheckCount; i++)
        {
            var o = i * 8;
            var gpuHit = (words[o + 3] & 1u) != 0;
            var gpuAny = (words[o + 3] & 4u) != 0;
            var cpu = rayQueries.Closest(rayCheckRays[i], 0f, rayCheckTMax[i]);
            if (gpuAny != cpu.HasValue) anyMismatch++;
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
            $"[VulkanSponza] ray check, GPU against CPU over {RayCheckCount:N0} rays ({hits:N0} hit): hit/miss differ on {hitMismatch}, any-hit on {anyMismatch}; same placement and triangle on {sameTriangle:N0}, another on {otherTriangle} ({otherTriangleSameT} of them at the same distance); distance within {maxRelT:0.0e0} relative, barycentrics within {maxBary:0.0e0}, facing differs on {faceMismatch}."));
        Console.WriteLine(string.Create(Inv,
            $"[VulkanSponza] ray check: same-triangle distance error, relative to the coordinates' scale, above 1e-6 / 1e-5 / 1e-4 / 1e-3 on {relBuckets[0]} / {relBuckets[1]} / {relBuckets[2]} / {relBuckets[3]} rays."));
        if (rayCheckProbe is { } probe)
        {
            var w = RayCheckCount * 8;
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
            var c = rayQueries.Closest(rayCheckRays[i], 0f, rayCheckTMax[i])!.Value;
            var o = i * 8;
            var inst = rayQueries.Instances[c.Instance];
            var m = inst.Mesh;
            var p0 = m.Positions[m.Indices[c.Triangle * 3]]; var p1 = m.Positions[m.Indices[c.Triangle * 3 + 1]]; var p2 = m.Positions[m.Indices[c.Triangle * 3 + 2]];
            var area = Vector3.Cross(p1 - p0, p2 - p0).Length() * 0.5f;
            Console.WriteLine(string.Create(Inv,
                $"[VulkanSponza] ray check, {label} (ray {i}): CPU t {c.T:0.######} placement {c.Instance} triangle {c.Triangle} bary {c.Barycentrics}; GPU t {BitConverter.UInt32BitsToSingle(words[o]):0.######} placement {(int)words[o + 4]} triangle {(int)words[o + 5]} bary ({BitConverter.UInt32BitsToSingle(words[o + 1]):0.####}, {BitConverter.UInt32BitsToSingle(words[o + 2]):0.####}); triangle area {area:0.######} m², placement scale det {inst.World.GetDeterminant():0.###}, direction {rayCheckRays[i].Direction}."));
        }
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

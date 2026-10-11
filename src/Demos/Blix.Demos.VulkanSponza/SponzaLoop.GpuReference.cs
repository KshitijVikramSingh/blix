using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;
using Blix.Geometry;
using Blix.Graphics;

namespace Blix.Demos.VulkanSponza;

// --gpu-reference (with --shot): the gate's surface reference path-traced on the GPU as a WAVEFRONT (reference_wave.comp,
// one bounce a frame, every ray through the frame's batches -- SponzaLoop.RayBatch.cs: the software traversal, or Metal's
// hardware under --ray-backend hw|both). --reference-monolithic keeps the first form (reference_points.comp: whole paths
// in one kernel over ray_query.glsl), the wavefront's cross-check. Both take reference_path.glsl's sampling and bounce
// count, as the reference view does. The points are the CPU reference's (WriteSurfaceReference): a grid of camera pixels off
// depth edges, at the surface lifted 1 cm along its face normal. Paths accumulate over the frames before the shot,
// --ref-paths in all, --ref-bounces deep; at the shot the lines are the CPU reference's format (tools/testbeds/gate.py
// reads them), the CPU reference's own -- if --probe-reference also ran -- relabelled, and the two held against each
// other at the same points.
internal sealed partial class SponzaLoop
{
    private bool gpuReference;
    // --gpu-ref-paths N: the GPU reference's own path count (--ref-paths when absent), so it can run far past the CPU's.
    private int? gpuReferencePathsWanted;
    private int GpuReferenceTarget => gpuReferencePathsWanted ?? refPaths;
    private ShaderInterface gpuReferenceInterface = null!;
    private PassHandle gpuReferencePassHandle;
    private PipelineHandle gpuReferencePipeline;
    private List<(int K, float Px, float Py)>? gpuReferencePoints;
    private GpuBufferHandle gpuReferencePointBuffer, gpuReferenceAccum;
    private int gpuReferencePaths;
    private uint gpuReferenceDispatch;
    // The CPU reference's totals by point (WriteSurfaceReference), when it ran too: the cross-check.
    private readonly Dictionary<int, double> cpuSurfaceReference = new();
    private const int GpuReferenceGrid = 24;
    private const int GpuReferencePathsPerThread = 4;
    private const int GpuReferencePathsPerDispatch = 64 * GpuReferencePathsPerThread;

    // The wavefront: lanes per point, each running whole paths one bounce a frame, up to its share of the paths.
    private bool referenceMonolithic;
    private const int ReferenceLanesPerPoint = 256;
    private ShaderInterface referenceWaveInterface = null!;
    private PipelineHandle referenceWavePipeline;
    private PassHandle referenceWaveStepPass, referenceWaveShadePass, referenceWaveResolvePass;
    private RayBatchPasses referenceClosestBatch, referenceShadowBatch;
    private int ReferenceLaneQuota => (GpuReferenceTarget + ReferenceLanesPerPoint - 1) / ReferenceLanesPerPoint;
    // The gate's points as PickGpuReferencePoints packed them (position, normal), and their wave.
    private List<Vector4>? gpuReferencePackedPoints;
    private ReferenceWave? gateWave;

    /// <summary>One reference wavefront: its points (two vec4s each: position, normal), lanesPerPoint lanes a point each
    /// running Quota whole paths one bounce a frame, and the buffers its steps and batches pass between them. The gate's
    /// points have one (gateWave); the transfer capture's texels another (SponzaLoop.TransferFit.cs).</summary>
    private sealed class ReferenceWave
    {
        public GpuBufferHandle Points, Lanes, Sums, Rays, Hits, Origins, Occluded;
        public int PointCount, LanesPerPoint, Quota;
        public uint Dispatch;
        public int Count => PointCount * LanesPerPoint;
    }

    private ReferenceWave CreateReferenceWave(List<Vector4> packed, int lanesPerPoint, int quota, string name)
    {
        var w = new ReferenceWave { PointCount = packed.Count / 2, LanesPerPoint = lanesPerPoint, Quota = quota };
        var lanes = w.Count;
        // Every lane starts with no path: its hit count -1.
        var start = new float[lanes * 16];
        for (var l = 0; l < lanes; l++) start[l * 16 + 3] = -1f;
        w.Points = Own(device.CreateGpuBuffer(packed.Count * 16, MemoryMarshal.AsBytes(CollectionsMarshal.AsSpan(packed)), $"sponza.{name}.points"));
        w.Lanes = Own(device.CreateGpuBuffer(lanes * 64, MemoryMarshal.AsBytes(start.AsSpan()), $"sponza.{name}.lanes"));
        w.Sums = Own(device.CreateGpuBuffer(lanes * 32, new byte[lanes * 32], $"sponza.{name}.lane-sums"));
        w.Rays = Own(device.CreateGpuBuffer(lanes * 32, new byte[lanes * 32], $"sponza.{name}.rays"));
        w.Hits = Own(device.CreateGpuBuffer(lanes * 48, new byte[lanes * 48], $"sponza.{name}.hits"));
        w.Origins = Own(device.CreateGpuBuffer(lanes * 16, new byte[lanes * 16], $"sponza.{name}.shadow-origins"));
        w.Occluded = Own(device.CreateGpuBuffer(lanes * 4, new byte[lanes * 4], $"sponza.{name}.occluded"));
        return w;
    }

    // The frames a wave needs at most: every lane's quota of paths at their longest (bounces + 2 rays each).
    private int ReferenceWaveFrames(ReferenceWave w) => w.Quota * (refBounces + 2) + 4;

    // Each frame: from as many frames before the shot as the paths need, the points picked and their paths dispatched.
    private void RecordGpuReference()
    {
        if (!gpuReference || shotPath is null || shotWritten || rayQueries is not { } scene || rayBlockBuffers.Length == 0 || !raySurfacesBaked) return;
        if (!referenceMonolithic)
        {
            RecordGateReferenceWave(scene);
            return;
        }
        var dispatches = (GpuReferenceTarget + GpuReferencePathsPerDispatch - 1) / GpuReferencePathsPerDispatch;
        if (gpuReferencePoints is null)
        {
            if (postLoadFrames < shotFrame - dispatches - 2) return;
            PickGpuReferencePoints(scene);
        }
        if (gpuReferencePoints!.Count == 0 || gpuReferencePaths >= GpuReferenceTarget) return;
        var declared = gpuReferenceInterface.Slots.Select(sl => sl.Name).Where(nm => nm is not null).ToHashSet();
        graph.Dispatch(gpuReferencePassHandle, new DispatchCommand(gpuReferencePipeline, gpuReferencePoints.Count, 1, 1,
            new ShaderUniform[]
            {
                new("uSunDirection", new Vector4Uniform(new Vector4(sunDirection, 0f))),
                new("uSunIrradiance", new Vector4Uniform(new Vector4(EffectiveSunIrradiance, 1f))),
                new("uParams", new Vector4Uniform(new Vector4(gpuReferenceDispatch + (uint)refSeed * 7919u, refBounces, GpuReferencePathsPerThread, gpuReferencePoints.Count))),
            },
            new[] { new ShaderTextureBinding("uSkyRadiance", envCubeTexture) },
            Buffers: rayBlockBuffers
                .Append(new ShaderBufferBinding("ReferencePointList", gpuReferencePointBuffer))
                .Append(new ShaderBufferBinding("ReferencePointAccum", gpuReferenceAccum))
                .Where(b => declared.Contains(b.Name)).ToArray()));
        gpuReferenceDispatch++;
        gpuReferencePaths += GpuReferencePathsPerDispatch;
    }

    // The gate's wave: started as many frames before the shot as its lanes' quota can take.
    private void RecordGateReferenceWave(RayQueryScene scene)
    {
        if (gateWave is null)
        {
            if (postLoadFrames < shotFrame - ReferenceLaneQuota * (refBounces + 2) - 4) return;
            PickGpuReferencePoints(scene);
            if (gpuReferencePackedPoints is not { Count: > 0 } packed) return;
            gateWave = CreateReferenceWave(packed, ReferenceLanesPerPoint, ReferenceLaneQuota, "reference");
        }
        RecordReferenceWave(gateWave);
    }

    // One bounce of every lane's path: step (the next rays) -> CLOSEST -> shade (shadow rays, the next start) -> SHADOW ->
    // resolve. The batches are the frame's (SponzaLoop.RayBatch.cs): software, or Metal under --ray-backend hw|both.
    private void RecordReferenceWave(ReferenceWave w)
    {
        var count = w.Count;
        if (count == 0) return;
        var frameSeed = w.Dispatch * 0x9E3779B9u + (uint)refSeed * 7919u;
        var declared = referenceWaveInterface.Slots.Select(sl => sl.Name).Where(nm => nm is not null).ToHashSet();
        DispatchCommand Wave(int mode) => new(referenceWavePipeline, (count + 63) / 64, 1, 1,
            new ShaderUniform[]
            {
                new("uSunDirection", new Vector4Uniform(new Vector4(sunDirection, 0f))),
                new("uSunIrradiance", new Vector4Uniform(new Vector4(EffectiveSunIrradiance, 1f))),
                new("uParams", new Vector4Uniform(new Vector4(mode, refBounces, w.Quota, w.LanesPerPoint))),
                new("uCount", new Vector4Uniform(new Vector4(count, w.Dispatch + (uint)refSeed * 7919u, 0f, 0f))),
            },
            new[] { new ShaderTextureBinding("uSkyRadiance", envCubeTexture) },
            Buffers: rayBlockBuffers
                .Append(new ShaderBufferBinding("ReferencePointList", w.Points))
                .Append(new ShaderBufferBinding("ReferenceLanes", w.Lanes))
                .Append(new ShaderBufferBinding("ReferenceLaneSums", w.Sums))
                .Append(new ShaderBufferBinding("BatchRays", w.Rays))
                .Append(new ShaderBufferBinding("BatchHits", w.Hits))
                .Append(new ShaderBufferBinding("BatchOrigins", w.Origins))
                .Append(new ShaderBufferBinding("BatchOccluded", w.Occluded))
                .Where(b => declared.Contains(b.Name)).ToArray());
        graph.Dispatch(referenceWaveStepPass, Wave(0));
        RecordClosestBatch(referenceClosestBatch, w.Rays, w.Hits, count, frameSeed);
        graph.Dispatch(referenceWaveShadePass, Wave(1));
        RecordShadowBatch(referenceShadowBatch, w.Origins, w.Occluded, count, -Vector3.Normalize(sunDirection), frameSeed ^ 0x5bd1e995u);
        graph.Dispatch(referenceWaveResolvePass, Wave(2));
        w.Dispatch++;
    }

    // WriteSurfaceReference's points: the grid's camera pixels whose four neighbours, 2 px away, meet the same depth
    // (within 2%), at the hit lifted 1 cm along the face normal turned toward the camera.
    private void PickGpuReferencePoints(RayQueryScene scene)
    {
        device.TryGetTextureSize(graph.GetColorTexture(incidentHandle), out var iw, out var ih);
        Matrix4x4.Invert(viewProj, out var invViewProj);
        Ray CameraRay(float px, float py)
        {
            var ndc = new Vector2(px / iw * 2f - 1f, py / ih * 2f - 1f);
            var far = Vector4.Transform(new Vector4(ndc, 1f, 1f), invViewProj);
            return new Ray(cameraPosition, Vector3.Normalize(new Vector3(far.X, far.Y, far.Z) / far.W - cameraPosition));
        }
        Vector3 FaceNormal(RayHit hit)
        {
            var inst = scene.Instances[hit.Instance];
            var m = inst.Mesh;
            var a = Vector3.Transform(m.Positions[m.Indices[hit.Triangle * 3]], inst.World);
            var b = Vector3.Transform(m.Positions[m.Indices[hit.Triangle * 3 + 1]], inst.World);
            var c = Vector3.Transform(m.Positions[m.Indices[hit.Triangle * 3 + 2]], inst.World);
            var n = Vector3.Cross(b - a, c - a);
            return n.LengthSquared() > 0f ? Vector3.Normalize(n) : Vector3.UnitY;
        }
        var points = new List<(int K, float Px, float Py)>();
        var packed = new List<Vector4>();
        for (var k = 0; k < GpuReferenceGrid * GpuReferenceGrid; k++)
        {
            var px = (k % GpuReferenceGrid + 0.5f) / GpuReferenceGrid * iw;
            var py = (k / GpuReferenceGrid + 0.5f) / GpuReferenceGrid * ih;
            var ray = CameraRay(px, py);
            if (scene.Closest(ray) is not { } hit) continue;
            var edge = false;
            foreach (var (dx, dy) in new[] { (2f, 0f), (-2f, 0f), (0f, 2f), (0f, -2f) })
                if (scene.Closest(CameraRay(px + dx, py + dy)) is not { } other || MathF.Abs(other.T - hit.T) > 0.02f * hit.T) { edge = true; break; }
            if (edge) continue;
            var n = FaceNormal(hit);
            if (Vector3.Dot(n, ray.Direction) > 0f) n = -n;
            points.Add((k, px, py));
            packed.Add(new Vector4(ray.PointAt(hit.T) + n * 0.01f, 0f));
            packed.Add(new Vector4(n, 0f));
        }
        gpuReferencePoints = points;
        gpuReferencePackedPoints = packed;
        if (points.Count == 0) return;
        gpuReferencePointBuffer = Own(device.CreateGpuBuffer(packed.Count * 16, MemoryMarshal.AsBytes(CollectionsMarshal.AsSpan(packed)), "sponza.reference.points"));
        gpuReferenceAccum = Own(device.CreateGpuBuffer(points.Count * 16, new byte[points.Count * 16], "sponza.reference.points-accum"));
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"[VulkanSponza] GPU surface reference: {points.Count} of {GpuReferenceGrid * GpuReferenceGrid} pixels off depth edges, {GpuReferenceTarget} paths x {refBounces} bounces, from post-load frame {postLoadFrames}."));
    }

    // The wavefront's lanes summed per point: (sum of pi x radiance, paths).
    private Vector4[] ReferenceWaveSums(ReferenceWave w)
    {
        var sums = MemoryMarshal.Cast<byte, Vector4>(device.ReadGpuBuffer(w.Sums, 0, w.Count * 32).AsSpan()).ToArray();
        var perPoint = new Vector4[w.PointCount];
        for (var p = 0; p < w.PointCount; p++)
            for (var k = 0; k < w.LanesPerPoint; k++) perPoint[p] += sums[(p * w.LanesPerPoint + k) * 2 + 1];
        return perPoint;
    }

    // At the shot: the gate's lines from the GPU reference, and the CPU's held against it where both ran.
    private void WriteGpuSurfaceReference()
    {
        if (!gpuReference || gpuReferencePoints is not { Count: > 0 } points) return;
        var accum = referenceMonolithic
            ? MemoryMarshal.Cast<byte, Vector4>(device.ReadGpuBuffer(gpuReferenceAccum, 0, points.Count * 16).AsSpan()).ToArray()
            : ReferenceWaveSums(gateWave!);
        var incident = device.ReadTexture(graph.GetColorTexture(incidentHandle), out var iw, out var ih, out _);
        static double Lum(Vector3 v) => 0.2126 * v.X + 0.7152 * v.Y + 0.0722 * v.Z;
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"[VulkanSponza] surface reference on the GPU ({(referenceMonolithic ? "monolithic" : FrameRaysOnHardware && frameTracer is not null ? "wavefront, Metal" : "wavefront, software")}; {points.Count} pixels off depth edges, {(referenceMonolithic ? gpuReferencePaths : (int)Math.Round(accum.Average(a => a.W)))} paths x {refBounces} bounces):"));
        if (!referenceMonolithic && FrameRaysOnHardware && frameTracer is not null)
        {
            var trace = frameTracer.TraceStats;
            var shadow = frameTracer.ShadowStats;
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"[VulkanSponza] reference wavefront on Metal: closest {trace.Dispatches:N0} batches, {trace.MeanGpuMs:0.000} ms mean, {trace.Failures} failed; shadow {shadow.Dispatches:N0} batches, {shadow.MeanGpuMs:0.000} ms mean, {shadow.Failures} failed ({gateWave?.Count ?? 0:N0} rays a batch)."));
        }
        var pairs = new List<(double Cpu, double Gpu)>();
        for (var i = 0; i < points.Count; i++)
        {
            var (k, px, py) = points[i];
            var a = accum[i];
            var reference = a.W > 0f ? Lum(new Vector3(a.X, a.Y, a.Z) / a.W) : double.NaN;
            var o = (Math.Clamp((int)py, 0, ih - 1) * iw + Math.Clamp((int)px, 0, iw - 1)) * 8;
            var field = Lum(new Vector3((float)BitConverter.ToHalf(incident, o), (float)BitConverter.ToHalf(incident, o + 2), (float)BitConverter.ToHalf(incident, o + 4)));
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"    surface  at {px / iw:0.0000},{py / ih:0.0000}  gpu  total {field:0.00000} ref {reference:0.00000}"));
            if (cpuSurfaceReference.TryGetValue(k, out var cpu)) pairs.Add((cpu, reference));
        }
        if (pairs.Count == 0) return;
        var mean = pairs.Average(p => p.Cpu);
        var diffs = pairs.Select(p => Math.Abs(p.Gpu - p.Cpu) / Math.Max(mean, 1e-9)).OrderBy(v => v).ToArray();
        var ratios = pairs.Where(p => p.Cpu > 0.02 * mean).Select(p => p.Gpu / p.Cpu).OrderBy(v => v).ToArray();
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"[VulkanSponza] GPU reference against the CPU's at {pairs.Count} points: energy {pairs.Sum(p => p.Gpu) / pairs.Sum(p => p.Cpu):0.000}, median ratio {(ratios.Length > 0 ? ratios[ratios.Length / 2] : double.NaN):0.000}, |diff|/mean median {diffs[diffs.Length / 2]:0.000} p90 {diffs[(int)(diffs.Length * 0.9)]:0.000}."));
    }
}

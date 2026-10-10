using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;
using Blix.Geometry;
using Blix.Graphics;

namespace Blix.Demos.VulkanSponza;

// --gpu-reference (with --shot): the gate's surface reference path-traced on the GPU (reference_points.comp, sharing
// reference_path.glsl with the reference view), through ray_query.glsl -- so a hardware backend beneath it speeds it
// the same as everything else. The points are the CPU reference's (WriteSurfaceReference): a grid of camera pixels off
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

    // Each frame: from as many frames before the shot as the paths need, the points picked and their paths dispatched.
    private void RecordGpuReference()
    {
        if (!gpuReference || shotPath is null || shotWritten || rayQueries is not { } scene || rayBlockBuffers.Length == 0 || !raySurfacesBaked) return;
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
        if (points.Count == 0) return;
        gpuReferencePointBuffer = Own(device.CreateGpuBuffer(packed.Count * 16, MemoryMarshal.AsBytes(CollectionsMarshal.AsSpan(packed)), "sponza.reference.points"));
        gpuReferenceAccum = Own(device.CreateGpuBuffer(points.Count * 16, new byte[points.Count * 16], "sponza.reference.points-accum"));
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"[VulkanSponza] GPU surface reference: {points.Count} of {GpuReferenceGrid * GpuReferenceGrid} pixels off depth edges, {GpuReferenceTarget} paths x {refBounces} bounces, from post-load frame {postLoadFrames}."));
    }

    // At the shot: the gate's lines from the GPU reference, and the CPU's held against it where both ran.
    private void WriteGpuSurfaceReference()
    {
        if (!gpuReference || gpuReferencePoints is not { Count: > 0 } points) return;
        var accum = MemoryMarshal.Cast<byte, Vector4>(device.ReadGpuBuffer(gpuReferenceAccum, 0, points.Count * 16).AsSpan()).ToArray();
        var incident = device.ReadTexture(graph.GetColorTexture(incidentHandle), out var iw, out var ih, out _);
        static double Lum(Vector3 v) => 0.2126 * v.X + 0.7152 * v.Y + 0.0722 * v.Z;
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"[VulkanSponza] surface reference on the GPU ({points.Count} pixels off depth edges, {gpuReferencePaths} paths x {refBounces} bounces):"));
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

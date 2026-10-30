using System.Numerics;
using System.Runtime.InteropServices;
using Blix.Graphics;
using Blix.Graphics.MetalHWX;

namespace Blix.Demos.VulkanSponza;

// The two batch operations a ray consumer writes its rays for: GLSL under MoltenVK cannot reach Metal's intersector, so a
// consumer writes its rays to a buffer, a batch answers them, and a later pass reads the answers -- a wavefront.
//
//   CLOSEST  rays (two vec4s: origin, tMin; direction, tMax) -> answers (three uvec4s: t, barycentrics, flags;
//            placement, triangle; world normal, linear albedo)
//   SHADOW   origins (vec4: position, tMax; tMax <= 0 no ray) toward one direction -> a uint each, 1 where occluded
//
// Each is answered by Metal's hardware (an external pass in the frame: MetalRayTracer.EncodeTrace / EncodeShadow) once
// the frame's tracer exists (EnsureFrameTracer), or, where it failed to build, by the software traversal
// (ray_batch_closest.comp / ray_batch_shadow.comp over ray_query.glsl), in the same formats and with the same coverage
// seeds (the base plus the ray's index), so a consumer reads either.
internal sealed partial class SponzaLoop
{
    private ShaderInterface batchClosestInterface = null!, batchShadowInterface = null!;
    private PipelineHandle batchClosestPipeline, batchShadowPipeline;
    // Frame rays go to Metal unless its tracer failed to build (rtShadows off then: RecordRtShadows).
    private bool FrameRaysOnHardware => rtShadows;

    /// <summary>One consumer's batch: a compute pass for the software traversal and an external pass for Metal, declared
    /// where the consumer needs the answers; a frame dispatches one of the two.</summary>
    private readonly record struct RayBatchPasses(PassHandle Software, PassHandle Hardware);

    private RayBatchPasses DeclareClosestBatch(string name) =>
        new(graph.ComputePass(name).Shader(batchClosestInterface).Handle, graph.ExternalPass(name + "-metal"));

    private RayBatchPasses DeclareShadowBatch(string name) =>
        new(graph.ComputePass(name).Shader(batchShadowInterface).Handle, graph.ExternalPass(name + "-metal"));

    private void CreateRayBatchPipelines(string shaderDir)
    {
        batchClosestPipeline = Own(device.CreateComputePipeline(Own(device.CreateComputeShaderProgramFromSpv(
            File.ReadAllBytes(Path.Combine(shaderDir, "ray_batch_closest.comp.spv")), batchClosestInterface, "ray_batch_closest")), "ray_batch_closest"));
        batchShadowPipeline = Own(device.CreateComputePipeline(Own(device.CreateComputeShaderProgramFromSpv(
            File.ReadAllBytes(Path.Combine(shaderDir, "ray_batch_shadow.comp.spv")), batchShadowInterface, "ray_batch_shadow")), "ray_batch_shadow"));
    }

    // At the shot: what the frame tracer's in-frame batches cost on Metal, from their own GPU times (closest-hit batches;
    // the deferred shadow batches -- the traced screen shadows count in a slot of their own, RtShadowStatSlot).
    private void WriteFrameTracerStats()
    {
        if (frameTracer is null) return;
        var trace = frameTracer.TraceStats;
        var shadow = frameTracer.ShadowStatsOf(0);
        Console.WriteLine(string.Create(Inv,
            $"[VulkanSponza] metal tracer, in-frame batches: closest {trace.Dispatches:N0} ({trace.MeanGpuMs:0.000} ms mean, {trace.Failures} failed), shadow batches {shadow.Dispatches:N0} ({shadow.MeanGpuMs:0.000} ms mean, {shadow.Failures} failed)."));
    }

    // The seed travels as its bits in a float (a vec4 uniform): exact, which a float's value would not be past 2^24.
    private static float SeedBits(uint seed) => BitConverter.UInt32BitsToSingle(seed);

    // With frame rays on Metal, the frame's tracer answers, else the software kernel.
    // limit (Metal only): a uint the frame wrote before the batch, its live count; the software kernel traces all count.
    // deferred: the answers are read on a later frame, so the batch can wait for the frame's tracer group
    // (SponzaLoop.TracerGroup.cs) instead of a trip to Metal of its own.
    private void RecordClosestBatch(RayBatchPasses passes, GpuBufferHandle rays, GpuBufferHandle hits, int count, uint seedBase,
        GpuBufferHandle? limit = null, bool deferred = false)
    {
        if (count <= 0) return;
        if (FrameRaysOnHardware && (EnsureFrameTracer() ? frameTracer : null) is { } tracer)
        {
            if (deferred && TracerGroupsOn)
                DeferTracerBatch((sync, t) => t.EncodeTrace(sync, rays, 0, hits, 0, count, seedBase, limit));
            else graph.External(passes.Hardware, sync => tracer.EncodeTrace(sync, rays, 0, hits, 0, count, seedBase, limit));
            return;
        }
        var declared = batchClosestInterface.Slots.Select(sl => sl.Name).Where(nm => nm is not null).ToHashSet();
        graph.Dispatch(passes.Software, new DispatchCommand(batchClosestPipeline, (count + 63) / 64, 1, 1,
            new ShaderUniform[] { new("uBatch", new Vector4Uniform(new Vector4(count, SeedBits(seedBase), 0f, 0f))) },
            Array.Empty<ShaderTextureBinding>(),
            Buffers: rayBlockBuffers.Append(new ShaderBufferBinding("BatchRays", rays)).Append(new ShaderBufferBinding("BatchHits", hits))
                .Where(b => declared.Contains(b.Name)).ToArray()));
    }

    private void RecordShadowBatch(RayBatchPasses passes, GpuBufferHandle origins, GpuBufferHandle occluded, int count, Vector3 towardLight, uint seedBase,
        GpuBufferHandle? limit = null, bool deferred = false, GpuBufferHandle? directions = null)
    {
        if (count <= 0) return;
        if (FrameRaysOnHardware && (EnsureFrameTracer() ? frameTracer : null) is { } tracer)
        {
            if (deferred && TracerGroupsOn)
                DeferTracerBatch((sync, t) => t.EncodeShadow(sync, origins, occluded, 0, count, towardLight, seedBase, limit: limit, directions: directions));
            else graph.External(passes.Hardware, sync => tracer.EncodeShadow(sync, origins, occluded, 0, count, towardLight, seedBase, limit: limit, directions: directions));
            return;
        }
        var declared = batchShadowInterface.Slots.Select(sl => sl.Name).Where(nm => nm is not null).ToHashSet();
        graph.Dispatch(passes.Software, new DispatchCommand(batchShadowPipeline, (count + 63) / 64, 1, 1,
            new ShaderUniform[]
            {
                new("uShadow", new Vector4Uniform(new Vector4(towardLight, count))),
                new("uSeed", new Vector4Uniform(new Vector4(SeedBits(seedBase), 0f, 0f, 0f))),
            },
            Array.Empty<ShaderTextureBinding>(),
            Buffers: rayBlockBuffers.Append(new ShaderBufferBinding("BatchOrigins", origins)).Append(new ShaderBufferBinding("BatchOccluded", occluded))
                .Where(b => declared.Contains(b.Name)).ToArray()));
    }
}

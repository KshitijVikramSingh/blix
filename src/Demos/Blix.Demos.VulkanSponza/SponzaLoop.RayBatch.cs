using System.Numerics;
using Blix.Graphics;

namespace Blix.Demos.VulkanSponza;

// The two batch operations every ray consumer migrates onto (plan.md, stage 5, hardware rays): GLSL under MoltenVK
// cannot reach Metal's intersector, so a consumer writes its rays to a buffer, a batch answers them, and a later pass
// reads the answers -- a wavefront.
//
//   CLOSEST  rays (two vec4s: origin, tMin; direction, tMax) -> answers (three uvec4s: t, barycentrics, flags;
//            placement, triangle; world normal, linear albedo) -- ray_check.comp's formats
//   SHADOW   origins (vec4: position, tMax; tMax <= 0 no ray) toward one direction -> a uint each, 1 where occluded
//
// Each is answered by the software traversal (ray_batch_closest.comp / ray_batch_shadow.comp over ray_query.glsl) or by
// Metal's hardware (an external pass in the frame: MetalRayTracer.EncodeTrace / EncodeShadow), in the same formats and
// with the same coverage seeds (the base plus the ray's index), so a consumer reads either. Frame work goes to Metal
// under --ray-backend hw|both, once its structures exist (EnsureFrameTracer); the software kernels otherwise, and they
// stay the reference behaviour.
internal sealed partial class SponzaLoop
{
    private ShaderInterface batchClosestInterface = null!, batchShadowInterface = null!;
    private PipelineHandle batchClosestPipeline, batchShadowPipeline;
    private bool FrameRaysOnHardware => rayBackend is "hw" or "both";

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

    // The seed travels as its bits in a float (a vec4 uniform): exact, which a float's value would not be past 2^24.
    private static float SeedBits(uint seed) => BitConverter.UInt32BitsToSingle(seed);

    private void RecordClosestBatch(RayBatchPasses passes, GpuBufferHandle rays, GpuBufferHandle hits, int count, uint seedBase)
    {
        if (count <= 0) return;
        if (FrameRaysOnHardware && EnsureFrameTracer())
        {
            var tracer = frameTracer!;
            graph.External(passes.Hardware, sync => tracer.EncodeTrace(sync, rays, 0, hits, 0, count, seedBase));
            return;
        }
        var declared = batchClosestInterface.Slots.Select(sl => sl.Name).Where(nm => nm is not null).ToHashSet();
        graph.Dispatch(passes.Software, new DispatchCommand(batchClosestPipeline, (count + 63) / 64, 1, 1,
            new ShaderUniform[] { new("uBatch", new Vector4Uniform(new Vector4(count, SeedBits(seedBase), 0f, 0f))) },
            Array.Empty<ShaderTextureBinding>(),
            Buffers: rayBlockBuffers.Append(new ShaderBufferBinding("BatchRays", rays)).Append(new ShaderBufferBinding("BatchHits", hits))
                .Where(b => declared.Contains(b.Name)).ToArray()));
    }

    private void RecordShadowBatch(RayBatchPasses passes, GpuBufferHandle origins, GpuBufferHandle occluded, int count, Vector3 towardLight, uint seedBase)
    {
        if (count <= 0) return;
        if (FrameRaysOnHardware && EnsureFrameTracer())
        {
            var tracer = frameTracer!;
            graph.External(passes.Hardware, sync => tracer.EncodeShadow(sync, origins, occluded, 0, count, towardLight, seedBase));
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

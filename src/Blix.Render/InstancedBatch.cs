using System.Numerics;
using Blix.Graphics;

namespace Blix.Render;

// L3 — instanced-draw ergonomics. Given an already-built mesh + pipeline +
// InstanceBuffer (all caller-owned), stages per-instance data CPU-side via
// Begin/Add/End and records a single instanced draw. It owns NO GPU resources and
// no material — the pipeline (and thus the shader, vertex layout, push-constant
// layout, lighting/fog/whatever) is entirely the caller's. The batch only knows
// how to accumulate {model, tint} records and bind the buffer for one draw.
//
// Construct one per (mesh, pipeline, buffer). The push-constant payload handed to
// Begin must match whatever the pipeline's shader declares.
public sealed class InstancedBatch
{
    private readonly Mesh mesh;
    private readonly PipelineHandle pipeline;
    private readonly InstanceBuffer buffer;
    private readonly InstanceData[] staging;

    private byte[] pushConstants = Array.Empty<byte>();
    private int count;
    private bool inBatch;

    public InstancedBatch(Mesh mesh, PipelineHandle pipeline, InstanceBuffer buffer)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        ArgumentNullException.ThrowIfNull(buffer);
        this.mesh = mesh;
        this.pipeline = pipeline;
        this.buffer = buffer;
        staging = new InstanceData[buffer.Capacity];
    }

    // Instances accumulated since Begin.
    public int Count => count;

    // Begin a batch. `pushConstants` is whatever the pipeline's shader declares
    // (e.g. a view-projection matrix, optionally + camera/fog); the batch just
    // forwards the bytes to the draw.
    public void Begin(ReadOnlySpan<byte> pushConstants)
    {
        if (inBatch)
        {
            throw new InvalidOperationException("InstancedBatch.Begin called while a batch is already active. Call End first.");
        }
        if (this.pushConstants.Length != pushConstants.Length)
        {
            this.pushConstants = new byte[pushConstants.Length];
        }
        pushConstants.CopyTo(this.pushConstants);
        count = 0;
        inBatch = true;
    }

    public void Add(Matrix4x4 model, Vector4 tint)
    {
        if (!inBatch)
        {
            throw new InvalidOperationException("InstancedBatch.Add called outside Begin/End.");
        }
        if (count >= staging.Length)
        {
            throw new InvalidOperationException($"InstancedBatch exceeded {staging.Length} instances in a single Begin/End pair.");
        }
        staging[count++] = new InstanceData(model, tint);
    }

    // Bulk replacement of the batch contents. Clears any prior Add'd instances.
    public void SetInstances(ReadOnlySpan<InstanceData> instances)
    {
        if (!inBatch)
        {
            throw new InvalidOperationException("InstancedBatch.SetInstances called outside Begin/End.");
        }
        if (instances.Length > staging.Length)
        {
            throw new InvalidOperationException($"InstancedBatch.SetInstances given {instances.Length} instances; max is {staging.Length}.");
        }
        instances.CopyTo(staging);
        count = instances.Length;
    }

    // Records the instanced draw. `textures` are frame-global samplers the pipeline's
    // shader declares (e.g. a shadow map) — forwarded verbatim, mirroring
    // ParticleBatch.Draw; the batch stays agnostic to what they mean. Null = none.
    public void End(RenderPassBuilder pass, IReadOnlyList<ShaderTextureBinding>? textures = null)
    {
        ArgumentNullException.ThrowIfNull(pass);
        if (!inBatch)
        {
            throw new InvalidOperationException("InstancedBatch.End called without a matching Begin.");
        }
        inBatch = false;
        if (count == 0) return;

        buffer.Write(staging.AsSpan(0, count));
        pass.DrawIndexedInstanced(
            mesh.VertexBuffer,
            mesh.IndexBuffer,
            pipeline,
            mesh.IndexCount,
            instanceCount: count,
            Array.Empty<ShaderUniform>(),
            textures ?? Array.Empty<ShaderTextureBinding>(),
            perDrawMaterial: buffer.Material,
            pushConstants: pushConstants);
    }
}

using System.Numerics;
using System.Runtime.InteropServices;
using Blix.Graphics;
using Blix.Graphics.Vulkan;

namespace Blix.Render;

// One instance's GPU payload: world transform + tint. 80 bytes, std430-compatible
// (mat4 @0 size 64, vec4 @64 size 16; array stride 80). Layout must stay
// byte-identical to the `Instance` struct in instanced.vert.
[StructLayout(LayoutKind.Sequential)]
public struct InstanceData
{
    public Matrix4x4 Model;
    public Vector4 Tint;

    public InstanceData(Matrix4x4 model, Vector4 tint)
    {
        Model = model;
        Tint = tint;
    }
}

// Instanced mesh batcher on the Vulkan backend. Draws one mesh (the VB/IB passed
// at construction) many times in a single vkCmdDrawIndexed(instanceCount=N); each
// instance's transform + tint live in a set-3 storage buffer indexed by
// gl_InstanceIndex. Shape mirrors SpriteBatch: owns its shader (instanced.vert/
// frag, shipped as SPIR-V next to the assembly) and a depth-tested, back-face-
// culling pipeline baked against `renderTarget` (null = swapchain) for render-pass
// compatibility — so create one batch per (mesh, target) you draw.
//
// The per-instance SSBO reuses the exact bone-palette mechanism: a
// framesInFlight-replicated MaterialBindings at set 3, written into the current
// frame slot each End and bound via the draw's PerDrawMaterial. Because the
// instanced props this serves are not skinned, set 3 is free for instance data;
// a skinned + instanced mesh would collide on set 3 and is out of scope.
public sealed class InstancedBatch : IDisposable
{
    // 16384 * 80 bytes ≈ 1.3 MB per frame slot — well under maxStorageBufferRange.
    public const int MaxInstances = 16384;
    private const int InstanceStride = 80;

    // set 3 / binding 0 = readonly per-instance SSBO (Vertex); 64-byte vertex push
    // constant = view-projection. Exposed so a RenderGraph GraphicsPass can declare
    // it via .Shader(InstancedBatch.Interface) (the graph validator requires every
    // graphics pass to declare its shader interfaces).
    public static ShaderInterface Interface { get; } = new(
        Slots: new[]
        {
            new DescriptorSetSlot(
                3, 0, ShaderResourceType.StorageBuffer, ShaderStages.Vertex,
                BlockLayout: new UniformBlockLayout(
                    TotalSize: MaxInstances * InstanceStride,
                    Members: new[]
                    {
                        new UniformBlockMember("instances", 0, MaxInstances * InstanceStride, ElementStride: InstanceStride),
                    })),
        },
        PushConstants: new[] { new PushConstantRange(ShaderStages.Vertex, 0, 64) });

    // The instanced shader consumes only position + normal, but meshes are packed
    // as VertexPosition3NormalTexture (stride 32). Declaring just the two consumed
    // attributes — at the full stride so the buffer still reads correctly — avoids
    // a "vertex attribute not consumed by vertex shader" validation warning.
    private static readonly VertexLayout MeshLayout = new(
        Stride: VertexPosition3NormalTexture.Layout.Stride,
        Attributes: new[]
        {
            new VertexAttribute(Location: 0, VertexAttributeFormat.Float3, Offset: 0),
            new VertexAttribute(Location: 1, VertexAttributeFormat.Float3, Offset: 3 * sizeof(float)),
        });

    private readonly VulkanGraphicsDevice device;
    private readonly VertexBufferHandle vertexBuffer;
    private readonly IndexBufferHandle indexBuffer;
    private readonly int indexCount;
    private readonly ShaderProgramHandle shader;
    private readonly PipelineHandle pipeline;
    private readonly MaterialBindings instanceSsbo; // set 3, framesInFlight-replicated
    private readonly byte[] payload = new byte[MaxInstances * InstanceStride];
    private readonly byte[] pushConstants = new byte[64]; // one mat4 (view-projection)

    private int count;
    private bool inBatch;
    private bool disposed;

    // mesh: the geometry replicated per instance. Its vertex layout must be
    // VertexPosition3NormalTexture (instanced.vert consumes position + normal;
    // uv is ignored). renderTarget: surface this batch's pipeline is baked
    // against (null = swapchain).
    public InstancedBatch(VulkanGraphicsDevice device, Mesh mesh, RenderSurfaceHandle? renderTarget = null)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(mesh);
        this.device = device;
        vertexBuffer = mesh.VertexBuffer;
        indexBuffer = mesh.IndexBuffer;
        indexCount = mesh.IndexCount;

        var shaderDir = Path.Combine(AppContext.BaseDirectory, "Shaders");
        var vertSpv = File.ReadAllBytes(Path.Combine(shaderDir, "instanced.vert.spv"));
        var fragSpv = File.ReadAllBytes(Path.Combine(shaderDir, "instanced.frag.spv"));
        shader = device.CreateShaderProgramFromSpv(vertSpv, fragSpv, Interface, "instanced");

        pipeline = device.CreatePipeline(
            new PipelineDescription(
                shader,
                MeshLayout,
                PrimitiveTopology.Triangles,
                DepthState.LessEqualWrite,
                RasterizerState.BackFaceCulling,
                ColorBlends: [BlendState.Disabled],
                RenderTarget: renderTarget),
            name: "instanced");

        instanceSsbo = device.CreateMaterial(
            shader, setIndex: 3, framesInFlight: device.MaxFramesInFlightCount, name: "instanced.ssbo");
    }

    // Current instance count accumulated since Begin.
    public int Count => count;

    public void Begin(Matrix4x4 viewProjection)
    {
        if (inBatch)
        {
            throw new InvalidOperationException("InstancedBatch.Begin called while a batch is already active. Call End first.");
        }
        // Raw write: GLSL reads the push bytes column-major, so the row-vector
        // matrix lands as its transpose and `uViewProj * v_col` equals the
        // engine's row-vector product (same convention as SpriteBatch).
        MemoryMarshal.Write(pushConstants.AsSpan(0, 64), in viewProjection);
        count = 0;
        inBatch = true;
    }

    public void Add(Matrix4x4 model, Vector4 tint)
    {
        if (!inBatch)
        {
            throw new InvalidOperationException("InstancedBatch.Add called outside Begin/End.");
        }
        if (count >= MaxInstances)
        {
            throw new InvalidOperationException($"InstancedBatch exceeded {MaxInstances} instances in a single Begin/End pair.");
        }
        var inst = new InstanceData(model, tint);
        MemoryMarshal.Write(payload.AsSpan(count * InstanceStride, InstanceStride), in inst);
        count++;
    }

    // Bulk replacement of the batch contents. Clears any prior Add'd instances.
    public void SetInstances(ReadOnlySpan<InstanceData> instances)
    {
        if (!inBatch)
        {
            throw new InvalidOperationException("InstancedBatch.SetInstances called outside Begin/End.");
        }
        if (instances.Length > MaxInstances)
        {
            throw new InvalidOperationException($"InstancedBatch.SetInstances given {instances.Length} instances; max is {MaxInstances}.");
        }
        var dst = MemoryMarshal.Cast<byte, InstanceData>(payload.AsSpan(0, instances.Length * InstanceStride));
        instances.CopyTo(dst);
        count = instances.Length;
    }

    public void End(RenderPassBuilder pass)
    {
        ArgumentNullException.ThrowIfNull(pass);
        if (!inBatch)
        {
            throw new InvalidOperationException("InstancedBatch.End called without a matching Begin.");
        }
        if (count == 0)
        {
            inBatch = false;
            return;
        }

        // WriteBuffer requires the full BlockLayout.TotalSize. The draw's
        // instanceCount bounds the GPU to `count` instances, so the stale tail of
        // the buffer is never read. Write the current frame slot only, then record
        // the draw — the replicated slots keep per-frame writes off in-flight work.
        instanceSsbo.WriteBuffer(device.CurrentFrameSlot, binding: 0, payload);

        pass.DrawIndexedInstanced(
            vertexBuffer,
            indexBuffer,
            pipeline,
            indexCount,
            instanceCount: count,
            Array.Empty<ShaderUniform>(),
            Array.Empty<ShaderTextureBinding>(),
            perDrawMaterial: instanceSsbo.Handle,
            pushConstants: pushConstants);

        inBatch = false;
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        device.DestroyMaterial(instanceSsbo.Handle);
        device.DestroyPipeline(pipeline);
        device.DestroyShaderProgram(shader);
    }
}

using System.Numerics;
using System.Runtime.InteropServices;
using Blix.Graphics;
using Blix.Graphics.Vulkan;

namespace Blix.Render;

// One instance's GPU payload: world transform + tint. 80 bytes, std430-compatible
// (mat4 @0 size 64, vec4 @64 size 16; array stride 80). Layout must stay
// byte-identical to the `Instance` struct any InstancedBatch shader declares.
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

// Instanced mesh batcher on the Vulkan backend: draws one mesh (the VB/IB passed
// at construction) many times in a single vkCmdDrawIndexed(instanceCount=N), each
// instance pulling its transform + tint from a set-3 storage buffer indexed by
// gl_InstanceIndex.
//
// This class owns ONLY the instancing mechanism — the per-instance SSBO (reusing
// the bone-palette MaterialBindings path, framesInFlight-replicated, bound via the
// draw's PerDrawMaterial) and the instanced draw. The *material* is the caller's:
//
//   • Default constructor → a turnkey built-in shader (instanced.vert/frag:
//     transform + tint + flat sun-shade), push = mat4 view-projection. Good for
//     quick/unlit use; Begin(viewProjection) packs it for you.
//   • Custom constructor → you supply a ShaderProgramHandle whose interface
//     declares InstanceSlot at set 3 and whatever push-constant range it needs.
//     You pack the push bytes and pass them to Begin(ReadOnlySpan<byte>). Lighting,
//     fog, shadows, texturing — all live in your shader, not here.
//
// The per-instance props this serves are not skinned, so set 3 is free for the
// instance SSBO; a skinned + instanced mesh would collide on set 3 (out of scope).
public sealed class InstancedBatch : IDisposable
{
    // 16384 * 80 bytes ≈ 1.3 MB per frame slot — well under maxStorageBufferRange.
    public const int MaxInstances = 16384;
    public const int InstanceStride = 80;

    // The set-3 / binding-0 readonly storage-buffer slot every InstancedBatch shader
    // must declare. Custom shaders compose this into their own ShaderInterface so the
    // engine and the shader agree on where per-instance data lives.
    public static DescriptorSetSlot InstanceSlot { get; } = new(
        3, 0, ShaderResourceType.StorageBuffer, ShaderStages.Vertex,
        BlockLayout: new UniformBlockLayout(
            TotalSize: MaxInstances * InstanceStride,
            Members: new[]
            {
                new UniformBlockMember("instances", 0, MaxInstances * InstanceStride, ElementStride: InstanceStride),
            }));

    // The built-in shader's interface: the instance SSBO + a 64-byte vertex push
    // (view-projection). Exposed so a RenderGraph GraphicsPass can declare it via
    // .Shader(InstancedBatch.Interface).
    public static ShaderInterface Interface { get; } = new(
        Slots: new[] { InstanceSlot },
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
    private readonly bool ownsShader;
    private readonly PipelineHandle pipeline;
    private readonly MaterialBindings instanceSsbo; // set 3, framesInFlight-replicated
    private readonly byte[] payload = new byte[MaxInstances * InstanceStride];
    private readonly byte[] pushConstants;
    private readonly int pushConstantBytes;

    private int count;
    private bool inBatch;
    private bool disposed;

    // Turnkey: the built-in instanced.vert/frag shader (transform + tint + flat
    // sun-shade), push = mat4 view-projection via Begin(viewProjection).
    public InstancedBatch(VulkanGraphicsDevice device, Mesh mesh, RenderSurfaceHandle? renderTarget = null)
        : this(device, mesh, LoadDefaultShader(device), pushConstantBytes: 64, renderTarget, ownsShader: true)
    {
    }

    // Custom material: `shader` must declare InstanceSlot at set 3 and a push range
    // of `pushConstantBytes`. You pack the push bytes and pass them to
    // Begin(ReadOnlySpan<byte>). The shader is NOT owned — dispose it yourself.
    public InstancedBatch(VulkanGraphicsDevice device, Mesh mesh, ShaderProgramHandle shader, int pushConstantBytes, RenderSurfaceHandle? renderTarget = null)
        : this(device, mesh, shader, pushConstantBytes, renderTarget, ownsShader: false)
    {
    }

    private InstancedBatch(VulkanGraphicsDevice device, Mesh mesh, ShaderProgramHandle shader, int pushConstantBytes, RenderSurfaceHandle? renderTarget, bool ownsShader)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(mesh);
        if (pushConstantBytes <= 0) throw new ArgumentOutOfRangeException(nameof(pushConstantBytes));
        this.device = device;
        this.shader = shader;
        this.ownsShader = ownsShader;
        this.pushConstantBytes = pushConstantBytes;
        pushConstants = new byte[pushConstantBytes];
        vertexBuffer = mesh.VertexBuffer;
        indexBuffer = mesh.IndexBuffer;
        indexCount = mesh.IndexCount;

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
            shader, setIndex: InstanceSlot.Set, framesInFlight: device.MaxFramesInFlightCount, name: "instanced.ssbo");
    }

    private static ShaderProgramHandle LoadDefaultShader(VulkanGraphicsDevice device)
    {
        var shaderDir = Path.Combine(AppContext.BaseDirectory, "Shaders");
        var vertSpv = File.ReadAllBytes(Path.Combine(shaderDir, "instanced.vert.spv"));
        var fragSpv = File.ReadAllBytes(Path.Combine(shaderDir, "instanced.frag.spv"));
        return device.CreateShaderProgramFromSpv(vertSpv, fragSpv, Interface, "instanced");
    }

    // Current instance count accumulated since Begin.
    public int Count => count;

    // Begin for the built-in shader: packs the view-projection into the 64-byte push.
    public void Begin(Matrix4x4 viewProjection)
    {
        if (pushConstantBytes != 64)
        {
            throw new InvalidOperationException(
                "Begin(viewProjection) is for the built-in shader (64-byte push). A custom-shader batch must use Begin(ReadOnlySpan<byte>).");
        }
        BeginInternal();
        // Raw write: GLSL reads the push bytes column-major, so the row-vector
        // matrix lands as its transpose and `uViewProj * v_col` equals the
        // engine's row-vector product (same convention as SpriteBatch).
        MemoryMarshal.Write(pushConstants.AsSpan(0, 64), in viewProjection);
    }

    // Begin for a custom shader: the caller supplies the full push-constant payload
    // (its length must equal the pushConstantBytes the batch was created with).
    public void Begin(ReadOnlySpan<byte> pushConstantPayload)
    {
        if (pushConstantPayload.Length != pushConstantBytes)
        {
            throw new ArgumentException(
                $"InstancedBatch expects {pushConstantBytes} push-constant bytes, got {pushConstantPayload.Length}.", nameof(pushConstantPayload));
        }
        BeginInternal();
        pushConstantPayload.CopyTo(pushConstants);
    }

    private void BeginInternal()
    {
        if (inBatch)
        {
            throw new InvalidOperationException("InstancedBatch.Begin called while a batch is already active. Call End first.");
        }
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
        if (ownsShader) device.DestroyShaderProgram(shader);
    }
}

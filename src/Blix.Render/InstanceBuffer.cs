using System.Numerics;
using System.Runtime.InteropServices;
using Blix.Graphics;
using Blix.Graphics.Vulkan;

namespace Blix.Render;

// One instance's GPU payload: world transform + tint. 80 bytes, std430 (mat4 @0
// size 64, vec4 @64 size 16; array stride 80). Must stay byte-identical to the
// `Instance` struct any instanced shader declares.
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

// L1 — per-instance storage buffer (data layer). Owns a frames-in-flight-
// replicated set-3 SSBO of InstanceData and nothing else: no mesh, no shader, no
// pipeline, no push constants. Any instanced shader that declares
// InstanceBuffer.Slot at set 3 can be drawn with it. Write() uploads the current
// frame's instances; bind Material as a draw's PerDrawMaterial.
//
// One InstanceBuffer holds one instance set — give each independently-written
// batch its own buffer so per-frame writes don't clobber each other.
public sealed class InstanceBuffer : IDisposable
{
    // 16384 * 80 bytes ≈ 1.3 MB per frame slot — well under maxStorageBufferRange.
    public const int MaxInstances = 16384;
    public const int Stride = 80;

    // The set-3 / binding-0 readonly storage-buffer slot instanced shaders must
    // declare. Compose it into your shader's ShaderInterface so the engine and the
    // shader agree on where per-instance data lives.
    public static DescriptorSetSlot Slot { get; } = new(
        3, 0, ShaderResourceType.StorageBuffer, ShaderStages.Vertex,
        BlockLayout: new UniformBlockLayout(
            TotalSize: MaxInstances * Stride,
            Members: new[]
            {
                new UniformBlockMember("instances", 0, MaxInstances * Stride, ElementStride: Stride),
            }));

    private readonly VulkanGraphicsDevice device;
    private readonly MaterialBindings ssbo;
    private readonly byte[] scratch = new byte[MaxInstances * Stride];
    private bool disposed;

    public MaterialHandle Material => ssbo.Handle;
    public int Capacity => MaxInstances;

    // `shader` is used only to allocate the set-3 descriptor layout and must declare
    // Slot at set 3. The resulting descriptor set is layout-compatible with any
    // pipeline whose set 3 matches (i.e. any shader composing the same Slot), so the
    // buffer isn't tied to that one pipeline.
    public InstanceBuffer(VulkanGraphicsDevice device, ShaderProgramHandle shader, string? name = null)
    {
        ArgumentNullException.ThrowIfNull(device);
        this.device = device;
        ssbo = device.CreateMaterial(
            shader, setIndex: Slot.Set, framesInFlight: device.MaxFramesInFlightCount, name: name ?? "instances");
    }

    // Upload `instances` into the current frame's slot. The whole block is written
    // (WriteBuffer requires the full size); the stale tail past instances.Length is
    // never read because the instanced draw is bounded to that count.
    public void Write(ReadOnlySpan<InstanceData> instances)
    {
        if (instances.Length > MaxInstances)
        {
            throw new ArgumentException(
                $"InstanceBuffer holds at most {MaxInstances} instances; got {instances.Length}.", nameof(instances));
        }
        var dst = MemoryMarshal.Cast<byte, InstanceData>(scratch.AsSpan(0, instances.Length * Stride));
        instances.CopyTo(dst);
        ssbo.WriteBuffer(device.CurrentFrameSlot, binding: Slot.Binding, scratch);
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        device.DestroyMaterial(ssbo.Handle);
    }
}

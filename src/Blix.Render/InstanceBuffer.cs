using System.Numerics;
using System.Runtime.InteropServices;
using Blix.Graphics;

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

    /// <summary>Bytes the whole set-3 block occupies: every instance slot, filled or not.</summary>
    public const int BlockSize = MaxInstances * Stride;

    /// <summary>
    /// Give a reflected interface the instance block's length.
    /// </summary>
    /// <remarks>
    /// The shader declares the block unsized -- <c>InstanceData instances[]</c> -- so reflection
    /// reports it with a block size of zero, which is the truth: how many instances there are is
    /// this class's decision and not the shader's. Composing it here means a caller derives the
    /// set, the binding, the stage and the stride from the shader and states only the count, in
    /// the one place that already owns it.
    /// </remarks>
    /// <exception cref="ArgumentException">The shader's instance stride is not <see cref="Stride"/>: this class packs that many bytes per instance.</exception>
    public static ShaderInterface Size(ShaderInterface reflected)
    {
        var declared = reflected.Slots.FirstOrDefault(s => s.Set == Slot.Set && s.Binding == Slot.Binding)?.BlockLayout?.RuntimeArray;
        if (declared is { } array && array.ElementStride != Stride)
        {
            throw new ArgumentException(
                $"the shader's instance array has a {array.ElementStride}-byte stride; InstanceBuffer packs {Stride} bytes per instance.",
                nameof(reflected));
        }

        return reflected.WithArrayLength(Slot.Set, Slot.Binding, MaxInstances);
    }

    private readonly IGraphicsDevice device;
    private readonly IMaterialBindings ssbo;
    private readonly byte[] scratch = new byte[MaxInstances * Stride];
    private bool disposed;

    public MaterialHandle Material => ssbo.Handle;
    public int Capacity => MaxInstances;

    // `shader` is used only to allocate the set-3 descriptor layout and must declare
    // Slot at set 3. The resulting descriptor set is layout-compatible with any
    // pipeline whose set 3 matches (i.e. any shader composing the same Slot), so the
    // buffer isn't tied to that one pipeline.
    public InstanceBuffer(IGraphicsDevice device, ShaderProgramHandle shader, string? name = null)
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
        var live = instances.Length * Stride;
        var dst = MemoryMarshal.Cast<byte, InstanceData>(scratch.AsSpan(0, live));
        instances.CopyTo(dst);
        // <b>Only the live instances, not the whole capacity.</b> This uploaded all 16,384 slots however many
        // were in use, so a batch drawing one instance copied 1.31 MB every frame. The ground coats are one
        // instance each and there are 85 of them on a hilly map: 3.0 ms a frame, all of it memcpy of stale
        // tail. The shader reads instanceCount entries and never looks further, so the tail was never data —
        // it was just being paid for.
        ssbo.WriteBuffer(device.CurrentFrameSlot, binding: Slot.Binding, scratch.AsSpan(0, live));
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        device.DestroyMaterial(ssbo.Handle);
    }
}

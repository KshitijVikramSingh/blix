using Silk.NET.Vulkan;

namespace Blix.Graphics.Vulkan;

// Per-material indirect-draw support: a host-visible buffer of
// VkDrawIndexedIndirectCommand structs, replicated per frame-in-flight. The
// caller rewrites the current frame's slot each frame (CPU cull + LOD pick →
// one command per object), then issues one vkCmdDrawIndexedIndirect per
// material group instead of a draw per object — collapsing per-draw encode
// (the cost the shared-buffer Stage 0 left as the next lever).
public sealed partial class VulkanGraphicsDevice
{
    // VkDrawIndexedIndirectCommand: indexCount, instanceCount, firstIndex,
    // vertexOffset, firstInstance — five uint32s.
    public const int IndirectCommandStride = 20;

    // One MORE slot than frames-in-flight. The indirect buffer is filled in
    // OnRender — BEFORE Execute's vkWaitForFences — so if it were only
    // MaxFramesInFlight slots, the slot written for frame N is the same physical
    // buffer the GPU is still reading for frame N-MaxFramesInFlight (up to
    // MaxFramesInFlight-1 frames can be in flight). That race corrupted commands
    // and flashed geometry black on camera movement (GPU falls behind). With one
    // extra slot, the slot written at N was last read at N-(MFIF+1), which is
    // guaranteed complete — no wait, no race, full pipelining.
    private const int IndirectSlots = MaxFramesInFlight + 1;
    // Independent ring index (NOT currentFrame, which only cycles MaxFramesInFlight);
    // advanced once per presented frame, in lockstep with currentFrame.
    private int indirectSlot;

    private sealed class VkIndirectBuffer
    {
        public VkBufferEntry[] PerSlot = System.Array.Empty<VkBufferEntry>();
    }

    private readonly Dictionary<int, VkIndirectBuffer> indirectBufferTable = new();

    internal void AdvanceIndirectSlot() => indirectSlot = (indirectSlot + 1) % IndirectSlots;

    // Allocate a ring-buffered (IndirectSlots) indirect buffer sized for
    // maxDrawCommands. Host-visible + coherent so the per-frame fill is a plain
    // memcpy with no staging.
    public IndirectBufferHandle CreateIndirectBuffer(int maxDrawCommands, string? name = null)
    {
        ThrowIfDisposed();
        var bytes = maxDrawCommands * IndirectCommandStride;
        var perSlot = new VkBufferEntry[IndirectSlots];
        for (var i = 0; i < IndirectSlots; i++)
        {
            perSlot[i] = CreateHostVisibleBuffer(
                new byte[bytes], BufferUsageFlags.IndirectBufferBit, $"{name ?? "indirect"}.slot{i}");
        }
        var id = nextResourceId++;
        indirectBufferTable[id] = new VkIndirectBuffer { PerSlot = perSlot };
        return new IndirectBufferHandle(id);
    }

    // Overwrite this frame's ring slot. Called during OnRender; the matching read
    // (GetIndirectBuffer) uses the same indirectSlot, which advances only after
    // the frame is submitted — so write and read agree within a frame, and the
    // ring's extra slot means no in-flight frame is reading this buffer.
    public void WriteIndirectCommands(IndirectBufferHandle handle, ReadOnlySpan<byte> commands)
    {
        if (!indirectBufferTable.TryGetValue(handle.Id, out var ib))
        {
            throw new InvalidOperationException($"Unknown indirect buffer handle {handle.Id}.");
        }
        UploadToHostVisibleBuffer(ib.PerSlot[indirectSlot].Memory, commands, 0);
    }

    // Read uses the same ring slot the fill wrote this frame.
    internal VkBufferEntry GetIndirectBuffer(IndirectBufferHandle h)
        => indirectBufferTable[h.Id].PerSlot[indirectSlot];

    private void DestroyIndirectBuffers()
    {
        foreach (var ib in indirectBufferTable.Values)
        {
            foreach (var e in ib.PerSlot) DestroyVkBufferEntry(e);
        }
        indirectBufferTable.Clear();
    }
}

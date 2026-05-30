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

    private sealed class VkIndirectBuffer
    {
        public VkBufferEntry[] PerFrame = System.Array.Empty<VkBufferEntry>();
    }

    private readonly Dictionary<int, VkIndirectBuffer> indirectBufferTable = new();

    // Allocate a frames-in-flight-replicated indirect buffer sized for
    // maxDrawCommands. Host-visible + coherent so the per-frame fill is a plain
    // memcpy with no staging.
    public IndirectBufferHandle CreateIndirectBuffer(int maxDrawCommands, string? name = null)
    {
        ThrowIfDisposed();
        var bytes = maxDrawCommands * IndirectCommandStride;
        var perFrame = new VkBufferEntry[MaxFramesInFlight];
        for (var i = 0; i < MaxFramesInFlight; i++)
        {
            perFrame[i] = CreateHostVisibleBuffer(
                new byte[bytes], BufferUsageFlags.IndirectBufferBit, $"{name ?? "indirect"}.frame{i}");
        }
        var id = nextResourceId++;
        indirectBufferTable[id] = new VkIndirectBuffer { PerFrame = perFrame };
        return new IndirectBufferHandle(id);
    }

    // Overwrite the CURRENT frame slot's commands. Call during OnRender, before
    // Execute: CurrentFrameSlot is the slot the upcoming Execute records against,
    // and the slot it last used (MaxFramesInFlight ago) has been waited on, so
    // there's no GPU read/write race. `commands` is the packed
    // VkDrawIndexedIndirectCommand bytes for the whole buffer.
    public void WriteIndirectCommands(IndirectBufferHandle handle, ReadOnlySpan<byte> commands)
    {
        if (!indirectBufferTable.TryGetValue(handle.Id, out var ib))
        {
            throw new InvalidOperationException($"Unknown indirect buffer handle {handle.Id}.");
        }
        UploadToHostVisibleBuffer(ib.PerFrame[currentFrame].Memory, commands, 0);
    }

    internal VkBufferEntry GetIndirectBuffer(IndirectBufferHandle h, int frameSlot)
        => indirectBufferTable[h.Id].PerFrame[frameSlot];

    private void DestroyIndirectBuffers()
    {
        foreach (var ib in indirectBufferTable.Values)
        {
            foreach (var e in ib.PerFrame) DestroyVkBufferEntry(e);
        }
        indirectBufferTable.Clear();
    }
}

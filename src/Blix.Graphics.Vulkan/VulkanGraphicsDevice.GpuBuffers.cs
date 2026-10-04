using Silk.NET.Vulkan;
using VkBuffer = Silk.NET.Vulkan.Buffer;

namespace Blix.Graphics.Vulkan;

// GPU buffers: device-local storage that compute reads and writes, draws read, and indirect draws take their
// arguments from (IGraphicsDevice.CreateGpuBuffer). One copy, never a ring: after the initial upload only the GPU
// writes one, and every dispatch that binds one is fenced on both sides (TranslateComputePass), so frames in flight
// order through the queue rather than through copies.
public sealed partial class VulkanGraphicsDevice
{
    private readonly Dictionary<int, VkBufferEntry> gpuBufferTable = new();

    public unsafe GpuBufferHandle CreateGpuBuffer(int sizeBytes, ReadOnlySpan<byte> initial = default, string? name = null)
    {
        ThrowIfDisposed();
        if (sizeBytes <= 0 || initial.Length > sizeBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(sizeBytes),
                $"a GPU buffer needs a positive size at least its initial contents' ({initial.Length} bytes); got {sizeBytes}.");
        }

        var label = name ?? "gpu-buffer";
        var ci = new BufferCreateInfo
        {
            SType = StructureType.BufferCreateInfo,
            Size = (ulong)sizeBytes,
            Usage = BufferUsageFlags.StorageBufferBit | BufferUsageFlags.IndirectBufferBit
                | BufferUsageFlags.TransferDstBit | BufferUsageFlags.TransferSrcBit,
            SharingMode = SharingMode.Exclusive,
        };
        VkBuffer buffer;
        ThrowIfNotSuccess(Vk.CreateBuffer(Device, in ci, null, &buffer), $"vkCreateBuffer({label})");
        Vk.GetBufferMemoryRequirements(Device, buffer, out var req);
        var ai = new MemoryAllocateInfo
        {
            SType = StructureType.MemoryAllocateInfo,
            AllocationSize = req.Size,
            MemoryTypeIndex = FindMemoryTypeIndex(req.MemoryTypeBits, MemoryPropertyFlags.DeviceLocalBit),
        };
        DeviceMemory memory;
        ThrowIfNotSuccess(Vk.AllocateMemory(Device, in ai, null, &memory), $"vkAllocateMemory({label})");
        ThrowIfNotSuccess(Vk.BindBufferMemory(Device, buffer, memory, 0), $"vkBindBufferMemory({label})");
        var entry = new VkBufferEntry { Buffer = buffer, Memory = memory, Size = (ulong)sizeBytes, Name = label };

        // Zeros everywhere, then the initial contents over the start: one submission, waited on, at load.
        VkBufferEntry? staging = initial.Length > 0
            ? CreateHostVisibleBuffer(initial, BufferUsageFlags.TransferSrcBit, $"{label}.staging")
            : null;
        var cmd = BeginSingleTimeCommands();
        Vk.CmdFillBuffer(cmd, buffer, 0, Vk.WholeSize, 0u);
        if (staging is { } source)
        {
            var fillDone = new MemoryBarrier
            {
                SType = StructureType.MemoryBarrier,
                SrcAccessMask = AccessFlags.TransferWriteBit,
                DstAccessMask = AccessFlags.TransferWriteBit,
            };
            Vk.CmdPipelineBarrier(cmd, PipelineStageFlags.TransferBit, PipelineStageFlags.TransferBit, 0, 1, &fillDone, 0, null, 0, null);
            var region = new BufferCopy { SrcOffset = 0, DstOffset = 0, Size = (ulong)initial.Length };
            Vk.CmdCopyBuffer(cmd, source.Buffer, buffer, 1, &region);
        }

        EndSingleTimeCommands(cmd);
        if (staging is { } done) DestroyVkBufferEntry(done);

        var id = nextResourceId++;
        gpuBufferTable[id] = entry;
        return new GpuBufferHandle(id);
    }

    public void DestroyGpuBuffer(GpuBufferHandle handle)
    {
        if (!gpuBufferTable.Remove(handle.Id, out var entry)) return;
        DestroyVkBufferEntry(entry);
    }

    public unsafe byte[] ReadGpuBuffer(GpuBufferHandle handle, int offset, int length)
    {
        ThrowIfDisposed();
        var entry = GetGpuBuffer(handle);
        if (offset < 0 || length <= 0 || (ulong)offset + (ulong)length > entry.Size)
        {
            throw new ArgumentOutOfRangeException(nameof(length),
                $"{length} bytes from {offset} is outside GPU buffer '{entry.Name}' ({entry.Size} bytes).");
        }

        var staging = CreateHostVisibleBuffer(new byte[length], BufferUsageFlags.TransferDstBit, $"{entry.Name}.readback");
        var cmd = BeginSingleTimeCommands();
        // Everything submitted before this, compute and draws alike, made visible to the copy: a pipeline barrier's
        // first scope is every earlier command on the queue, not only this command buffer's.
        var written = new MemoryBarrier
        {
            SType = StructureType.MemoryBarrier,
            SrcAccessMask = AccessFlags.ShaderWriteBit | AccessFlags.TransferWriteBit,
            DstAccessMask = AccessFlags.TransferReadBit,
        };
        Vk.CmdPipelineBarrier(cmd, PipelineStageFlags.AllCommandsBit, PipelineStageFlags.TransferBit, 0, 1, &written, 0, null, 0, null);
        var region = new BufferCopy { SrcOffset = (ulong)offset, DstOffset = 0, Size = (ulong)length };
        Vk.CmdCopyBuffer(cmd, entry.Buffer, staging.Buffer, 1, &region);
        EndSingleTimeCommands(cmd);

        var bytes = new byte[length];
        void* mapped;
        ThrowIfNotSuccess(Vk.MapMemory(Device, staging.Memory, 0, staging.Size, 0, &mapped), "vkMapMemory(gpu-buffer readback)");
        new ReadOnlySpan<byte>(mapped, length).CopyTo(bytes);
        Vk.UnmapMemory(Device, staging.Memory);
        DestroyVkBufferEntry(staging);
        return bytes;
    }

    internal VkBufferEntry GetGpuBuffer(GpuBufferHandle handle) =>
        gpuBufferTable.TryGetValue(handle.Id, out var entry)
            ? entry
            : throw new InvalidOperationException($"Unknown GPU buffer handle {handle.Id}.");

    private void DestroyAllGpuBuffers()
    {
        foreach (var entry in gpuBufferTable.Values) DestroyVkBufferEntry(entry);
        gpuBufferTable.Clear();
    }

    // A dispatch that binds GPU buffers is fenced on both sides, by memory barriers over all buffers (the graph
    // tracks images only, so this is the dispatch's own, like the storage-image transitions beside it). Before:
    // every earlier read or write of one, by an indirect fetch, a vertex, fragment or compute shader, this frame or
    // an earlier one still in flight on the queue. After: this dispatch's writes, visible to all of those.
    private const PipelineStageFlags GpuBufferReaders = PipelineStageFlags.DrawIndirectBit | PipelineStageFlags.VertexShaderBit
        | PipelineStageFlags.FragmentShaderBit | PipelineStageFlags.ComputeShaderBit;
    private const AccessFlags GpuBufferAccess = AccessFlags.IndirectCommandReadBit | AccessFlags.ShaderReadBit | AccessFlags.ShaderWriteBit;

    private unsafe void FenceGpuBuffers(CommandBuffer cmd, bool beforeDispatch)
    {
        var barrier = new MemoryBarrier
        {
            SType = StructureType.MemoryBarrier,
            SrcAccessMask = beforeDispatch ? GpuBufferAccess : AccessFlags.ShaderWriteBit,
            DstAccessMask = beforeDispatch ? AccessFlags.ShaderReadBit | AccessFlags.ShaderWriteBit : GpuBufferAccess,
        };
        Vk.CmdPipelineBarrier(cmd,
            beforeDispatch ? GpuBufferReaders : PipelineStageFlags.ComputeShaderBit,
            beforeDispatch ? PipelineStageFlags.ComputeShaderBit : GpuBufferReaders,
            0, 1, &barrier, 0, null, 0, null);
    }
}

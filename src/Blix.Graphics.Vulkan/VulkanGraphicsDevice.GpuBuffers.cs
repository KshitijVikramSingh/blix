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
        // Exportable as an MTLBuffer wherever the device can export at all: a GPU buffer is what a native Metal
        // pass reads and writes beside Vulkan (VulkanGraphicsDevice.MetalObjects), and the request has to be
        // made when the memory is allocated, not when the buffer is exported.
        var metalExport = new ExportMetalObjectCreateInfoEXT
        {
            SType = StructureType.ExportMetalObjectCreateInfoExt,
            ExportObjectType = ExportMetalObjectTypeFlagsEXT.ExportMetalObjectTypeMetalBufferBitExt,
        };
        var ai = new MemoryAllocateInfo
        {
            SType = StructureType.MemoryAllocateInfo,
            PNext = MetalObjectsEnabled ? &metalExport : null,
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

    // A storage buffer in host-visible, coherent memory (device-local too where there is such a type: Apple's unified
    // memory has one), mapped for its life: the CPU reads it in place (ReadMappedGpuBuffer). A sweep read a slice's sums
    // back with ReadGpuBuffer -- the device idled, a copy submitted and waited on -- every slice (2026-10-16).
    public unsafe GpuBufferHandle CreateReadableGpuBuffer(int sizeBytes, string? name = null)
    {
        ThrowIfDisposed();
        if (sizeBytes <= 0) throw new ArgumentOutOfRangeException(nameof(sizeBytes), $"a GPU buffer needs a positive size; got {sizeBytes}.");
        var label = name ?? "gpu-buffer.readable";
        var ci = new BufferCreateInfo
        {
            SType = StructureType.BufferCreateInfo,
            Size = (ulong)sizeBytes,
            Usage = BufferUsageFlags.StorageBufferBit | BufferUsageFlags.TransferDstBit | BufferUsageFlags.TransferSrcBit,
            SharingMode = SharingMode.Exclusive,
        };
        VkBuffer buffer;
        ThrowIfNotSuccess(Vk.CreateBuffer(Device, in ci, null, &buffer), $"vkCreateBuffer({label})");
        Vk.GetBufferMemoryRequirements(Device, buffer, out var req);
        const MemoryPropertyFlags host = MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit;
        uint type;
        try { type = FindMemoryTypeIndex(req.MemoryTypeBits, host | MemoryPropertyFlags.DeviceLocalBit); }
        catch (InvalidOperationException) { type = FindMemoryTypeIndex(req.MemoryTypeBits, host); }
        var ai = new MemoryAllocateInfo { SType = StructureType.MemoryAllocateInfo, AllocationSize = req.Size, MemoryTypeIndex = type };
        DeviceMemory memory;
        ThrowIfNotSuccess(Vk.AllocateMemory(Device, in ai, null, &memory), $"vkAllocateMemory({label})");
        ThrowIfNotSuccess(Vk.BindBufferMemory(Device, buffer, memory, 0), $"vkBindBufferMemory({label})");
        void* mapped;
        ThrowIfNotSuccess(Vk.MapMemory(Device, memory, 0, Vk.WholeSize, 0, &mapped), $"vkMapMemory({label})");
        new Span<byte>(mapped, sizeBytes).Clear();
        var id = nextResourceId++;
        gpuBufferTable[id] = new VkBufferEntry { Buffer = buffer, Memory = memory, Size = (ulong)sizeBytes, Name = label, Mapped = (nint)mapped };
        return new GpuBufferHandle(id);
    }

    public unsafe void ReadMappedGpuBuffer(GpuBufferHandle handle, int offset, Span<byte> destination)
    {
        ThrowIfDisposed();
        var entry = GetGpuBuffer(handle);
        if (entry.Mapped == 0)
        {
            ReadGpuBuffer(handle, offset, destination.Length).CopyTo(destination);
            return;
        }
        if (offset < 0 || (ulong)offset + (ulong)destination.Length > entry.Size)
            throw new ArgumentOutOfRangeException(nameof(offset), $"{destination.Length} bytes from {offset} is outside GPU buffer '{entry.Name}' ({entry.Size} bytes).");
        new ReadOnlySpan<byte>((byte*)entry.Mapped + offset, destination.Length).CopyTo(destination);
    }

    public void DestroyGpuBuffer(GpuBufferHandle handle)
    {
        if (!gpuBufferTable.Remove(handle.Id, out var entry)) return;
        DestroyVkBufferEntry(entry);
    }

    // IGraphicsDevice.DispatchNow: the dispatches recorded as a compute pass's are (RecordComputeDispatch), into the frame
    // slot the next frame will use. Safe because the device is idle first -- no frame in flight still reads that slot's
    // uniform buffers or transient sets -- and this returns only when the GPU is done, before the next frame rewinds
    // and resets them.
    public void DispatchNow(IReadOnlyList<DispatchCommand> dispatches, string name = "dispatch-now")
    {
        ThrowIfDisposed();
        if (dispatches.Count == 0) return;
        ThrowIfNotSuccess(Vk.DeviceWaitIdle(Device), "vkDeviceWaitIdle(dispatch-now)");
        // The uniform blocks these dispatches write are read by this submission alone, done when it returns: given back to
        // the arena after, as they were before. A cook's thousands of DispatchNow calls between two frames each took a
        // slice the next frame's rewind would have freed, and Sponza's sky visibility (16 K of them) ran the slot out
        // (2026-10-21).
        var arenaMark = uniformArenaBump.Length > 0 ? uniformArenaBump[arenaSlot].Used : 0;
        var cmd = BeginSingleTimeCommands();
        foreach (var d in dispatches) RecordComputeDispatch(cmd, d, name, currentFrame);
        EndSingleTimeCommands(cmd);
        ReleaseUniformBlocksPast(arenaMark);
    }

    public unsafe void WriteGpuBuffer(GpuBufferHandle handle, int offset, ReadOnlySpan<byte> data)
    {
        ThrowIfDisposed();
        var entry = GetGpuBuffer(handle);
        if (offset < 0 || data.Length <= 0 || (ulong)offset + (ulong)data.Length > entry.Size)
        {
            throw new ArgumentOutOfRangeException(nameof(data),
                $"{data.Length} bytes at {offset} is outside GPU buffer '{entry.Name}' ({entry.Size} bytes).");
        }

        var staging = CreateHostVisibleBuffer(data, BufferUsageFlags.TransferSrcBit, $"{entry.Name}.write");
        var cmd = BeginSingleTimeCommands();
        // Every earlier command's reads and writes of it done before the copy; the copy's write seen by every later one.
        var before = new MemoryBarrier
        {
            SType = StructureType.MemoryBarrier,
            SrcAccessMask = AccessFlags.ShaderReadBit | AccessFlags.ShaderWriteBit | AccessFlags.TransferReadBit | AccessFlags.TransferWriteBit,
            DstAccessMask = AccessFlags.TransferWriteBit,
        };
        Vk.CmdPipelineBarrier(cmd, PipelineStageFlags.AllCommandsBit, PipelineStageFlags.TransferBit, 0, 1, &before, 0, null, 0, null);
        var region = new BufferCopy { SrcOffset = 0, DstOffset = (ulong)offset, Size = (ulong)data.Length };
        Vk.CmdCopyBuffer(cmd, staging.Buffer, entry.Buffer, 1, &region);
        var after = new MemoryBarrier
        {
            SType = StructureType.MemoryBarrier,
            SrcAccessMask = AccessFlags.TransferWriteBit,
            DstAccessMask = AccessFlags.ShaderReadBit | AccessFlags.ShaderWriteBit | AccessFlags.TransferReadBit,
        };
        Vk.CmdPipelineBarrier(cmd, PipelineStageFlags.TransferBit, PipelineStageFlags.AllCommandsBit, 0, 1, &after, 0, null, 0, null);
        EndSingleTimeCommands(cmd);
        DestroyVkBufferEntry(staging);
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

    // After a graphics pass whose draws wrote GPU buffers (RenderPassDescription.WritesGpuBuffers): those writes,
    // from the vertex or fragment stage, visible to every later reader -- an indirect fetch, a vertex, fragment or
    // compute shader.
    private unsafe void FenceGraphicsBufferWrites(CommandBuffer cmd)
    {
        var barrier = new MemoryBarrier
        {
            SType = StructureType.MemoryBarrier,
            SrcAccessMask = AccessFlags.ShaderWriteBit,
            DstAccessMask = GpuBufferAccess,
        };
        Vk.CmdPipelineBarrier(cmd, PipelineStageFlags.VertexShaderBit | PipelineStageFlags.FragmentShaderBit, GpuBufferReaders,
            0, 1, &barrier, 0, null, 0, null);
    }

    // indirect: the dispatch also fetches its group counts from a GPU buffer (DispatchCommand.IndirectArgs), at the
    // draw-indirect stage, so that fetch is in the second scope too.
    private unsafe void FenceGpuBuffers(CommandBuffer cmd, bool beforeDispatch, bool indirect = false)
    {
        var barrier = new MemoryBarrier
        {
            SType = StructureType.MemoryBarrier,
            SrcAccessMask = beforeDispatch ? GpuBufferAccess : AccessFlags.ShaderWriteBit,
            DstAccessMask = beforeDispatch
                ? AccessFlags.ShaderReadBit | AccessFlags.ShaderWriteBit | (indirect ? AccessFlags.IndirectCommandReadBit : 0)
                : GpuBufferAccess,
        };
        Vk.CmdPipelineBarrier(cmd,
            beforeDispatch ? GpuBufferReaders : PipelineStageFlags.ComputeShaderBit,
            beforeDispatch ? PipelineStageFlags.ComputeShaderBit | (indirect ? PipelineStageFlags.DrawIndirectBit : 0) : GpuBufferReaders,
            0, 1, &barrier, 0, null, 0, null);
    }
}

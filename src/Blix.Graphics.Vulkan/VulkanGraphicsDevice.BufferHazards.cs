using Silk.NET.Vulkan;

namespace Blix.Graphics.Vulkan;

// GPU-buffer ordering between dispatches, by the hazards there are rather than around every dispatch. Until 2026-10-26
// a dispatch binding GPU buffers was fenced on both sides by a barrier over all buffers (FenceGpuBuffers): two global
// barriers a dispatch whatever it read or wrote, and the Xcode capture of Crytek Sponza's frame showed one dispatch per
// Metal compute encoder. Now, within a run of dispatches -- nothing else recorded between them -- a dispatch waits only
// when it reads or writes a buffer an earlier dispatch of the run wrote, or writes one it read (the block's readonly
// or writeonly, from reflection: DescriptorSetSlot.Access; a block declaring neither counts both). The run's writes
// are made visible to every reader (indirect fetches, vertex, fragment and compute shaders) when it ends, before
// whatever comes next: EndDispatchRun, at each non-compute pass, the frame's end, DispatchNow's end and an isolated
// pass's flush. The first dispatch of a run keeps the full barrier before it, as before: it may follow a draw that read
// or wrote what it touches.
//
// Crytek Sponza under --rtgi (2026-10-27, two runs an arm, a quiet GPU): 9.6 barriers a frame before every dispatch and
// 9.6 after became 9.2 before (0.4 skipped) and none after; the training's GPU time 3.85 / 4.07 ms -> 2.51 / 2.79, the
// frame's GPU time unchanged (22.8-23.2 ms: the RTGI chain's dispatches depend on each other, and the frame waits on the
// Metal tracer and the screen passes beside it).
//
// With synchronization2 the barriers are vkCmdPipelineBarrier2's, else vkCmdPipelineBarrier's.
// BLIX_BUFFER_BARRIERS=all: the old fencing (both sides of every dispatch), to compare.
public sealed partial class VulkanGraphicsDevice
{
    private readonly bool bufferBarriersAll = Environment.GetEnvironmentVariable("BLIX_BUFFER_BARRIERS") == "all";
    private CommandBuffer dispatchRunCmd;
    private readonly HashSet<int> runWrites = new();
    private readonly HashSet<int> runReads = new();
    private readonly List<int> dispatchReads = new(), dispatchWrites = new();
    private long bufferBarriersEmitted, bufferBarriersSkipped;

    /// <summary>Barriers between dispatches emitted and avoided since the last call, for the profile.</summary>
    public (long Emitted, long Skipped) TakeBufferBarrierCounts()
    {
        var r = (bufferBarriersEmitted, bufferBarriersSkipped);
        bufferBarriersEmitted = bufferBarriersSkipped = 0;
        return r;
    }

    // Before a dispatch that binds GPU buffers (or reads its group counts from one).
    private void OrderDispatchBuffers(CommandBuffer cmd, VkShaderProgramEntry prog, DispatchCommand d)
    {
        dispatchReads.Clear();
        dispatchWrites.Clear();
        if (d.Buffers is { } buffers)
        {
            foreach (var b in buffers)
            {
                var access = BufferAccess.ReadWrite;
                foreach (var s in prog.Interface.Slots)
                {
                    if (s.Type == ShaderResourceType.StorageBuffer && string.Equals(s.Name, b.Name, StringComparison.Ordinal))
                    {
                        access = s.Access;
                        break;
                    }
                }
                if (access != BufferAccess.WriteOnly) dispatchReads.Add(b.Buffer.Id);
                if (access != BufferAccess.ReadOnly) dispatchWrites.Add(b.Buffer.Id);
            }
        }
        var indirect = d.IndirectArgs is not null;
        if (d.IndirectArgs is { } args) dispatchReads.Add(args.Id);

        if (bufferBarriersAll || dispatchRunCmd.Handle != cmd.Handle)
        {
            // A run starts: everything earlier on the queue, whatever stage, before this dispatch. (A run left open on
            // another command buffer was ended by the flush points before that buffer ended.)
            FenceGpuBuffers(cmd, beforeDispatch: true, indirect);
            bufferBarriersEmitted++;
            runWrites.Clear();
            runReads.Clear();
            dispatchRunCmd = bufferBarriersAll ? default : cmd;
        }
        else
        {
            var hazard = false;
            foreach (var id in dispatchReads) hazard |= runWrites.Contains(id);
            foreach (var id in dispatchWrites) hazard |= runWrites.Contains(id) || runReads.Contains(id);
            if (hazard)
            {
                ComputeToComputeBarrier(cmd, indirect);
                bufferBarriersEmitted++;
                runWrites.Clear();
                runReads.Clear();
            }
            else bufferBarriersSkipped++;
        }
        foreach (var id in dispatchReads) runReads.Add(id);
        foreach (var id in dispatchWrites) runWrites.Add(id);
    }

    // After the dispatch: under BLIX_BUFFER_BARRIERS=all, its writes visible at once, as before.
    private void AfterDispatchBuffers(CommandBuffer cmd)
    {
        if (bufferBarriersAll) FenceGpuBuffers(cmd, beforeDispatch: false);
    }

    // The run's writes visible to every later reader, and the next dispatch starting a run.
    private void EndDispatchRun(CommandBuffer cmd)
    {
        if (dispatchRunCmd.Handle == 0) return;
        if (dispatchRunCmd.Handle == cmd.Handle && runWrites.Count > 0) FenceGpuBuffers(cmd, beforeDispatch: false);
        dispatchRunCmd = default;
        runWrites.Clear();
        runReads.Clear();
    }

    private unsafe void ComputeToComputeBarrier(CommandBuffer cmd, bool indirect)
    {
        if ((Features & GraphicsFeatures.Synchronization2) != 0)
        {
            var barrier = new MemoryBarrier2
            {
                SType = StructureType.MemoryBarrier2,
                SrcStageMask = PipelineStageFlags2.ComputeShaderBit,
                SrcAccessMask = AccessFlags2.ShaderStorageWriteBit | AccessFlags2.ShaderStorageReadBit,
                DstStageMask = PipelineStageFlags2.ComputeShaderBit | (indirect ? PipelineStageFlags2.DrawIndirectBit : 0),
                DstAccessMask = AccessFlags2.ShaderStorageReadBit | AccessFlags2.ShaderStorageWriteBit
                    | (indirect ? AccessFlags2.IndirectCommandReadBit : 0),
            };
            var dependency = new DependencyInfo
            {
                SType = StructureType.DependencyInfo,
                MemoryBarrierCount = 1,
                PMemoryBarriers = &barrier,
            };
            Vk.CmdPipelineBarrier2(cmd, &dependency);
            return;
        }
        var legacy = new MemoryBarrier
        {
            SType = StructureType.MemoryBarrier,
            SrcAccessMask = AccessFlags.ShaderWriteBit | AccessFlags.ShaderReadBit,
            DstAccessMask = AccessFlags.ShaderReadBit | AccessFlags.ShaderWriteBit | (indirect ? AccessFlags.IndirectCommandReadBit : 0),
        };
        Vk.CmdPipelineBarrier(cmd, PipelineStageFlags.ComputeShaderBit,
            PipelineStageFlags.ComputeShaderBit | (indirect ? PipelineStageFlags.DrawIndirectBit : 0), 0, 1, &legacy, 0, null, 0, null);
    }
}

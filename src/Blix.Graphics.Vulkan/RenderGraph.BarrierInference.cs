using Silk.NET.Vulkan;

namespace Blix.Graphics.Vulkan;

// Barrier inference for cross-pass resource reads.
//
// Render passes perform attachment layout transitions. Sampled Read edges also
// need a global memory dependency: a consumer can gather neighbours, or read a
// full-size depth target while drawing a quarter-size air pass. A framebuffer-local
// subpass dependency alone is not sufficient for those reads on tile GPUs.

// One resource's dependency scopes. Attachment render passes establish the
// layouts; sampled-read scopes are combined into a global vkCmdPipelineBarrier.
internal sealed record BarrierOp(
    int ResourceId,
    ImageLayout OldLayout,
    ImageLayout NewLayout,
    PipelineStageFlags SrcStage,
    AccessFlags SrcAccess,
    PipelineStageFlags DstStage,
    AccessFlags DstAccess);

internal static class BarrierInference
{
    // Returns a per-pass list of pre-pass barriers. Pass id → barriers
    // that must be emitted via vkCmdPipelineBarrier BEFORE that graphics
    // pass's render-pass-begin.
    //
    // Layouts already rest in ShaderReadOnlyOptimal. The backend combines these
    // scopes into a global memory barrier before a graphics consumer, avoiding
    // stale image handles after a graph resize. Compute dispatches retain their
    // storage-image transitions in TranslateComputePass.
    public static Dictionary<int, List<BarrierOp>> Infer(RenderGraph graph)
    {
        var result = new Dictionary<int, List<BarrierOp>>(graph.PassOrder.Count);
        foreach (var passId in graph.PassOrder)
        {
            var barriers = new List<BarrierOp>();
            result[passId] = barriers;
            if (!graph.GraphicsPasses.TryGetValue(passId, out var pass)) continue;
            foreach (var read in pass.Reads.DistinctBy(r => r.Resource.Id))
            {
                var depth = graph.Resources[read.Resource.Id].Kind != GraphResourceKind.ColorTarget;
                var attachmentStage = depth
                    ? PipelineStageFlags.EarlyFragmentTestsBit | PipelineStageFlags.LateFragmentTestsBit
                    : PipelineStageFlags.ColorAttachmentOutputBit;
                var attachmentAccess = depth
                    ? AccessFlags.DepthStencilAttachmentWriteBit
                    : AccessFlags.ColorAttachmentWriteBit;
                barriers.Add(new BarrierOp(read.Resource.Id,
                    ImageLayout.ShaderReadOnlyOptimal, ImageLayout.ShaderReadOnlyOptimal,
                    attachmentStage | PipelineStageFlags.ComputeShaderBit,
                    attachmentAccess | AccessFlags.ShaderWriteBit,
                    PipelineStageFlags.FragmentShaderBit, AccessFlags.ShaderReadBit));
            }
        }
        return result;
    }
}

using Silk.NET.Vulkan;

namespace Blix.Graphics.Vulkan;

// Barrier inference for cross-pass resource reads.
//
// Graphics-only graphs need no explicit barriers: each pass's render-pass
// subpass-dependency pair already handles the cross-pass layout transition
// AND memory-availability barrier (color/depth → ShaderReadOnlyOptimal,
// ColorAttachmentWrite → ShaderRead). When compute passes start writing,
// inference will emit vkCmdPipelineBarrier ops between compute writes and
// downstream graphics reads (and vice versa) — compute lives outside the
// render-pass dependency system.

// One image-memory barrier op. Caller emits via vkCmdPipelineBarrier
// after resolving ResourceId → VkImage from the graph's BackendResources.
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
    // that must be emitted via vkCmdPipelineBarrier BEFORE that pass's
    // render-pass-begin (graphics) or dispatch (compute).
    //
    // For v1 graphics-only graphs: every pass maps to an empty list.
    // The subpass dependencies baked into each VkRenderPass cover the
    // cross-pass barriers. See file header for rationale.
    public static Dictionary<int, List<BarrierOp>> Infer(RenderGraph graph)
    {
        var result = new Dictionary<int, List<BarrierOp>>(graph.PassOrder.Count);
        foreach (var passId in graph.PassOrder)
        {
            result[passId] = new List<BarrierOp>();
        }
        // Step 8 (compute Execute) fills this loop body with real
        // BarrierOp emission. Until then, graphics-only Read edges are
        // covered by per-pass subpass dependencies.
        return result;
    }
}

using Silk.NET.Vulkan;

namespace Blix.Graphics.Vulkan;

// Barrier inference for cross-pass resource reads (Vector B VB.iv).
//
// Reduced-scope v1: graphics-only graphs need NO explicit barriers
// between passes. The per-pass VkRenderPass subpass-dependency pair
// (built in VB.iii's CreateGraphicsPassRenderPass) already covers
// the cross-pass memory + layout barrier:
//
//   - Color attachments finalLayout = SHADER_READ_ONLY_OPTIMAL +
//     subpass dep 0→External with ColorAttachmentWrite → ShaderRead
//   - Depth attachments same posture for downstream shadow-map sampling
//
// So when pass A's color target is read by pass B's fragment shader,
// A's render pass end handles both the layout transition AND the
// memory-availability barrier. No vkCmdPipelineBarrier between A and
// B needed.
//
// This file ships the data shape + pure inference function so the
// contract is locked. The function returns empty barrier lists for v1
// graphs. When step 8 lights up ComputePass.Execute, compute writes
// won't be covered by subpass deps (compute is outside the render-pass
// system) — at that point InferBarriers grows to emit explicit
// vkCmdPipelineBarrier ops between compute writes and downstream
// graphics reads (and between graphics writes and downstream compute
// reads).
//
// VB.v will read the per-pass barrier lists and emit vkCmdPipelineBarrier
// before each pass's render-pass-begin. For v1 the lists are empty so
// the emit loop is a no-op.

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

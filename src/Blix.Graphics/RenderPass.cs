namespace Blix.Graphics;

public sealed record RenderPass(
    string Name,
    RenderPassDescription Description,
    IReadOnlyList<RenderCommand> Commands);

public sealed record RenderPassDescription(
    RenderSurfaceHandle Target,
    IReadOnlyList<GraphicsColor?> ClearColors,
    bool ClearDepth,
    // Compute pass: no framebuffer/render pass. The pass's commands are
    // DispatchCommands; the backend records them outside any render pass
    // with the needed storage-image barriers.
    bool Compute = false,

    // <b>Draw OVER an off-screen target rather than clearing it.</b> Asked for explicitly, never
    // inferred: the first attempt read "empty ClearColors" as the request, which is true of the
    // swapchain overlay path and NOT true of a depth-only pass, whose colour list is empty because it
    // has no colour attachments at all. That silently stopped a shadow map clearing — it accumulated
    // stale depth and the ground went black under a shadow that was never lifted.
    //
    // Honoured only where the target's owner supplied a load-form render pass; a surface without one
    // still clears, which is what every surface did before.
    bool LoadExisting = false,

    // Compute pass: the attachment outputs of earlier graphics passes it samples (RenderGraph.ComputeAttachmentReads).
    // The backend orders each explicitly (attachment write -> compute read) before the first dispatch, instead of
    // relying on the producer's subpass dependency to have named the compute stage.
    IReadOnlyList<TextureHandle>? AttachmentReads = null,

    // Native Metal work in place of dispatches (RenderGraph.External): the device splits the frame's submission
    // here and calls this with the timeline values to wait for and signal (ExternalWorkSync). Set on a Compute pass
    // with no commands.
    Action<ExternalWorkSync>? External = null,

    // An external pass whose result is first needed later (RenderGraph.ExternalJoin): the frame continues past it
    // without waiting, so independent work overlaps the Metal work, and waits at the join.
    bool ExternalJoinLater = false,

    // A join (RenderGraph.ExternalJoin): the name of the external pass whose work everything after this waits for.
    string? ExternalJoin = null,

    // An external pass only async compute reads (RenderGraph.ExternalPass's async): the frame goes on without waiting, the
    // next async run and the frame's end wait for it, on a timeline of its own.
    bool ExternalAsync = false,

    // Graphics pass: its draws write GPU buffers (a fragment shader appending to a list, say), fenced after the
    // render pass ends so a later dispatch, an indirect fetch or a draw sees them (GraphicsPassBuilder.WritesGpuBuffers).
    // Without it only dispatches fence GPU buffers.
    bool WritesGpuBuffers = false,

    // Compute pass on the device's second queue (ComputePassBuilder.Async): a run of them goes out beside the passes after
    // it, the frame's last submission waiting for it.
    bool Async = false,

    // Waits for the previous frame's async compute (GraphicsPassBuilder.AfterAsync / ComputePassBuilder.AfterAsync): frames
    // chain on their main queue's work, so this marks the first pass reading what the last frame's async run writes.
    bool AfterAsync = false);

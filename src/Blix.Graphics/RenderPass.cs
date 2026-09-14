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
    bool LoadExisting = false);

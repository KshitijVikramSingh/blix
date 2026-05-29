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
    // DispatchCommands; the Vulkan backend records them outside any render pass
    // with the needed storage-image barriers. GL backend rejects compute passes.
    bool Compute = false);

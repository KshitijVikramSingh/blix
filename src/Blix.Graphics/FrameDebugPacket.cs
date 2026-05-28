namespace Blix.Graphics;

public sealed record FrameDebugPacket(
    int TotalPasses,
    int TotalDraws,
    IReadOnlyList<FrameDebugPass> Passes);

public sealed record FrameDebugPass(
    string Name,
    RenderSurfaceHandle Target,
    int Width,
    int Height,
    bool ClearedColor,
    bool ClearedDepth,
    IReadOnlyList<FrameDebugDraw> Draws,
    // Human-readable name of the render target (e.g. "swapchain"). Optional so
    // the GL backend, which doesn't populate it, keeps compiling.
    string TargetName = "");

public sealed record FrameDebugDraw(
    PipelineHandle Pipeline,
    VertexBufferHandle VertexBuffer,
    IndexBufferHandle IndexBuffer,
    int IndexCount,
    IReadOnlyList<string> UniformNames,
    IReadOnlyList<FrameDebugTexture> Textures,
    // Shader-pipeline inspection fields. Optional (default empty) so any backend
    // that doesn't populate them — and existing callers — keep compiling. The
    // Vulkan backend fills these from each DrawIndexedCommand so the overlay's
    // Pipeline tab can show what's actually fed to the shader this frame.
    string Shader = "",
    IReadOnlyList<FrameDebugUniform>? Uniforms = null,
    IReadOnlyList<float>? PushConstants = null);

// One name->formatted-value pair captured from a draw's bound uniforms.
public sealed record FrameDebugUniform(string Name, string Value);

public sealed record FrameDebugTexture(
    string Name,
    int Slot,
    TextureHandle Texture,
    // Resolved resource name of the bound texture (e.g. "hdr", "ibl.font").
    string Resource = "");

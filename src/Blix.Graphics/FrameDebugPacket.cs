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
    IReadOnlyList<FrameDebugDraw> Draws);

public sealed record FrameDebugDraw(
    PipelineHandle Pipeline,
    VertexBufferHandle VertexBuffer,
    IndexBufferHandle IndexBuffer,
    int IndexCount,
    IReadOnlyList<string> UniformNames,
    IReadOnlyList<FrameDebugTexture> Textures);

public sealed record FrameDebugTexture(string Name, int Slot, TextureHandle Texture);

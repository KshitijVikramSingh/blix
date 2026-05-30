namespace Blix.Graphics;

public sealed record PipelineDescription(
    ShaderProgramHandle ShaderProgram,
    VertexLayout VertexLayout,
    PrimitiveTopology Topology,
    DepthState Depth,
    RasterizerState Rasterizer,
    IReadOnlyList<BlendState> ColorBlends,
    // Vulkan-only: target render surface for this pipeline's render-pass
    // compatibility. Null → swapchain (default). Vulkan render-pass
    // compatibility requires matching attachment formats, so a pipeline
    // baked against the swapchain (BGRA8) cannot be drawn into an
    // offscreen Rgba16F surface — that needs its own pipeline. GL backend
    // ignores this field.
    RenderSurfaceHandle? RenderTarget = null,
    // Vulkan: enable alpha-to-coverage — the fragment's output alpha is
    // converted to an MSAA coverage mask, giving antialiased alpha-cutout
    // edges (foliage) without the cost/order-dependence of blending. No effect
    // without MSAA. GL backend ignores it. Default false preserves behavior.
    bool AlphaToCoverage = false)
{
    public PipelineDescription(
        ShaderProgramHandle shaderProgram,
        VertexLayout vertexLayout,
        PrimitiveTopology topology,
        DepthState depth,
        RasterizerState rasterizer,
        BlendState blend)
        : this(shaderProgram, vertexLayout, topology, depth, rasterizer, [blend])
    {
    }
}

public sealed record DepthState(
    bool Enabled,
    bool WriteEnabled,
    DepthCompare Compare)
{
    public static DepthState Disabled { get; } = new(
        Enabled: false,
        WriteEnabled: false,
        DepthCompare.Less);

    public static DepthState LessEqualWrite { get; } = new(
        Enabled: true,
        WriteEnabled: true,
        DepthCompare.LessEqual);

    // Skybox-style depth: test against far plane (depth=1) so any scene geometry
    // already drawn occludes the sky, but the sky itself doesn't write depth.
    public static DepthState LessEqualNoWrite { get; } = new(
        Enabled: true,
        WriteEnabled: false,
        DepthCompare.LessEqual);
}

public enum DepthCompare
{
    Less = 0,
    LessEqual
}

public sealed record RasterizerState(
    CullMode CullMode,
    FrontFace FrontFace)
{
    public static RasterizerState NoCulling { get; } = new(
        CullMode.None,
        FrontFace.CounterClockwise);

    public static RasterizerState BackFaceCulling { get; } = new(
        CullMode.Back,
        FrontFace.CounterClockwise);
}

public enum CullMode
{
    None = 0,
    Back,
    Front
}

public enum FrontFace
{
    CounterClockwise = 0,
    Clockwise
}

public enum BlendMode
{
    Alpha = 0,    // SrcAlpha, OneMinusSrcAlpha -- standard transparency
    Additive,     // One, One -- emissive layering, bloom accumulation
}

public sealed record BlendState(bool Enabled, BlendMode Mode = BlendMode.Alpha)
{
    public static BlendState Disabled { get; } = new(Enabled: false);
    public static BlendState AlphaBlend { get; } = new(Enabled: true, BlendMode.Alpha);
    public static BlendState Additive { get; } = new(Enabled: true, BlendMode.Additive);
}

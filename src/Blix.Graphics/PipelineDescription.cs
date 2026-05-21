namespace Blix.Graphics;

public sealed record PipelineDescription(
    ShaderProgramHandle ShaderProgram,
    VertexLayout VertexLayout,
    PrimitiveTopology Topology,
    DepthState Depth,
    RasterizerState Rasterizer,
    IReadOnlyList<BlendState> ColorBlends)
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

public sealed record BlendState(bool Enabled)
{
    public static BlendState Disabled { get; } = new(Enabled: false);
    public static BlendState AlphaBlend { get; } = new(Enabled: true);
}

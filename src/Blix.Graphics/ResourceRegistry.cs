namespace Blix.Graphics;

public enum TextureKind
{
    UserUploaded = 0,
    RenderSurfaceColor,
    RenderSurfaceDepth
}

public sealed record VertexBufferEntry(
    VertexBufferHandle Handle,
    string Name,
    int VertexCount,
    int Stride);

public sealed record IndexBufferEntry(
    IndexBufferHandle Handle,
    string Name,
    int IndexCount);

public sealed record TextureEntry(
    TextureHandle Handle,
    string Name,
    int Width,
    int Height,
    TextureFormat Format,
    TextureKind Kind);

public sealed record ShaderProgramEntry(
    ShaderProgramHandle Handle,
    string Name);

public sealed record PipelineEntry(
    PipelineHandle Handle,
    string Name,
    ShaderProgramHandle ShaderProgram,
    PrimitiveTopology Topology);

public sealed record RenderSurfaceEntry(
    RenderSurfaceHandle Handle,
    string Name,
    int Width,
    int Height,
    IReadOnlyList<TextureHandle> ColorAttachments,
    TextureHandle? DepthTexture);

public sealed class ResourceRegistrySnapshot
{
    private readonly Dictionary<int, VertexBufferEntry> vertexBuffers;
    private readonly Dictionary<int, IndexBufferEntry> indexBuffers;
    private readonly Dictionary<int, TextureEntry> textures;
    private readonly Dictionary<int, ShaderProgramEntry> shaderPrograms;
    private readonly Dictionary<int, PipelineEntry> pipelines;
    private readonly Dictionary<int, RenderSurfaceEntry> renderSurfaces;

    public ResourceRegistrySnapshot(
        IReadOnlyList<VertexBufferEntry> vertexBufferEntries,
        IReadOnlyList<IndexBufferEntry> indexBufferEntries,
        IReadOnlyList<TextureEntry> textureEntries,
        IReadOnlyList<ShaderProgramEntry> shaderProgramEntries,
        IReadOnlyList<PipelineEntry> pipelineEntries,
        IReadOnlyList<RenderSurfaceEntry> renderSurfaceEntries)
    {
        VertexBuffers = vertexBufferEntries;
        IndexBuffers = indexBufferEntries;
        Textures = textureEntries;
        ShaderPrograms = shaderProgramEntries;
        Pipelines = pipelineEntries;
        RenderSurfaces = renderSurfaceEntries;

        vertexBuffers = vertexBufferEntries.ToDictionary(entry => entry.Handle.Id);
        indexBuffers = indexBufferEntries.ToDictionary(entry => entry.Handle.Id);
        textures = textureEntries.ToDictionary(entry => entry.Handle.Id);
        shaderPrograms = shaderProgramEntries.ToDictionary(entry => entry.Handle.Id);
        pipelines = pipelineEntries.ToDictionary(entry => entry.Handle.Id);
        renderSurfaces = renderSurfaceEntries.ToDictionary(entry => entry.Handle.Id);
    }

    public IReadOnlyList<VertexBufferEntry> VertexBuffers { get; }
    public IReadOnlyList<IndexBufferEntry> IndexBuffers { get; }
    public IReadOnlyList<TextureEntry> Textures { get; }
    public IReadOnlyList<ShaderProgramEntry> ShaderPrograms { get; }
    public IReadOnlyList<PipelineEntry> Pipelines { get; }
    public IReadOnlyList<RenderSurfaceEntry> RenderSurfaces { get; }

    public VertexBufferEntry? FindVertexBuffer(VertexBufferHandle handle) =>
        vertexBuffers.TryGetValue(handle.Id, out var entry) ? entry : null;

    public IndexBufferEntry? FindIndexBuffer(IndexBufferHandle handle) =>
        indexBuffers.TryGetValue(handle.Id, out var entry) ? entry : null;

    public TextureEntry? FindTexture(TextureHandle handle) =>
        textures.TryGetValue(handle.Id, out var entry) ? entry : null;

    public ShaderProgramEntry? FindShaderProgram(ShaderProgramHandle handle) =>
        shaderPrograms.TryGetValue(handle.Id, out var entry) ? entry : null;

    public PipelineEntry? FindPipeline(PipelineHandle handle) =>
        pipelines.TryGetValue(handle.Id, out var entry) ? entry : null;

    public RenderSurfaceEntry? FindRenderSurface(RenderSurfaceHandle handle) =>
        renderSurfaces.TryGetValue(handle.Id, out var entry) ? entry : null;
}

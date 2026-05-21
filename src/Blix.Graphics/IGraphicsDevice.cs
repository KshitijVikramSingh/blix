namespace Blix.Graphics;

public interface IGraphicsDevice : IDisposable
{
    GraphicsDeviceInfo Info { get; }

    void SetDefaultRenderSurfaceSize(int width, int height);

    VertexBufferHandle CreateVertexBuffer(VertexBufferData data, string? name = null);

    void UpdateVertexBuffer(VertexBufferHandle handle, ReadOnlySpan<byte> bytes, int byteOffset = 0);

    void DestroyVertexBuffer(VertexBufferHandle handle);

    IndexBufferHandle CreateIndexBuffer(
        IReadOnlyList<ushort> indices,
        GraphicsBufferUsage usage = GraphicsBufferUsage.Static,
        string? name = null);

    void DestroyIndexBuffer(IndexBufferHandle handle);

    ShaderProgramHandle CreateShaderProgram(ShaderSources sources);

    ShaderProgramHandle CreateShaderProgram(ShaderProgramDescription description);

    void DestroyShaderProgram(ShaderProgramHandle handle);

    PipelineHandle CreatePipeline(PipelineDescription description, string? name = null);

    void DestroyPipeline(PipelineHandle handle);

    TextureHandle CreateTexture2D(TextureDescription description, ReadOnlySpan<byte> pixels, string? name = null);

    // Creates a cubemap texture from six square face images, laid out in GL face order:
    // [+X, -X, +Y, -Y, +Z, -Z]. Each face is RGBA8, face*4 bytes long. The resulting
    // texture is sampled in shaders via a samplerCube uniform with a 3D direction.
    TextureHandle CreateTextureCube(int faceSize, ReadOnlySpan<byte> faces, SamplerDescription sampler, string? name = null);

    // Creates an empty depth-format cubemap intended as a shadow-map render target.
    // Each face gets its own depth attachment via DepthCubeFace render surfaces.
    // The returned handle is sampled in shaders as samplerCubeShadow (the sampler
    // descriptor should have Compare = true) -- texture(uShadowCube, vec4(dir, ref))
    // does the hardware PCF compare in cubemap space.
    TextureHandle CreateTextureCubeDepth(int faceSize, SamplerDescription sampler, string? name = null);

    // HDR cubemap with Rgba16F (half-float) storage, uploaded from caller-supplied
    // Half data laid out [+X, -X, +Y, -Y, +Z, -Z] face order, 4 channels per pixel
    // (R, G, B, A). Lets the env map carry brightness > 1.0 -- the prerequisite for
    // PBR specular reflecting a bright sun, IBL diffuse irradiance with proper
    // dynamic range, and pre-tonemap input that isn't already clipped.
    TextureHandle CreateTextureCubeHdr(int faceSize, ReadOnlySpan<Half> faces, SamplerDescription sampler, string? name = null);

    void DestroyTexture(TextureHandle handle);

    RenderSurface CreateRenderSurface(RenderSurfaceDescription description);

    void DestroyRenderSurface(RenderSurfaceHandle handle);

    ResourceRegistrySnapshot SnapshotResources();
}

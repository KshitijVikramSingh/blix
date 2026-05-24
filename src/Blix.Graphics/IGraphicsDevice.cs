namespace Blix.Graphics;

// Snapshot of device-level diagnostic counters surfaced to UI / overlays /
// CI. Frame-scoped values (FrameErrorCount, LastError*) reset at the start
// of each Execute() so a HUD line that reads "GL errors: 3" reflects the
// frame currently on screen — not the all-time total.
public readonly record struct GraphicsDeviceDiagnostics(
    int FrameErrorCount,
    string LastErrorContext,
    string LastErrorMessage);

public interface IGraphicsDevice : IDisposable
{
    GraphicsDeviceInfo Info { get; }

    GraphicsDeviceDiagnostics DiagnosticsSnapshot { get; }

    void SetDefaultRenderSurfaceSize(int width, int height);

    VertexBufferHandle CreateVertexBuffer(VertexBufferData data, string? name = null);

    void UpdateVertexBuffer(VertexBufferHandle handle, ReadOnlySpan<byte> bytes, int byteOffset = 0);

    void DestroyVertexBuffer(VertexBufferHandle handle);

    IndexBufferHandle CreateIndexBuffer(
        IReadOnlyList<ushort> indices,
        GraphicsBufferUsage usage = GraphicsBufferUsage.Static,
        string? name = null);

    // 32-bit index buffer. Use for meshes with >65535 vertices in a single
    // primitive (large authored scenes -- some Sponza Modern packs hit this).
    // The backend records the format per handle; DrawElements picks the
    // matching GL element type at draw time without caller involvement.
    IndexBufferHandle CreateIndexBuffer(
        IReadOnlyList<uint> indices,
        GraphicsBufferUsage usage = GraphicsBufferUsage.Static,
        string? name = null);

    void DestroyIndexBuffer(IndexBufferHandle handle);

    ShaderProgramHandle CreateShaderProgram(ShaderSources sources);

    ShaderProgramHandle CreateShaderProgram(ShaderProgramDescription description);

    void DestroyShaderProgram(ShaderProgramHandle handle);

    PipelineHandle CreatePipeline(PipelineDescription description, string? name = null);

    void DestroyPipeline(PipelineHandle handle);

    TextureHandle CreateTexture2D(TextureDescription description, ReadOnlySpan<byte> pixels, string? name = null);

    // Multi-mip texture upload. Each entry of `mipBytes` is one mip level's
    // packed pixel/block data; mip 0 (full size) first, then half, quarter,
    // etc. Required for compressed formats (BC7/BC5/BC6h) since GL can't
    // glGenerateMipmap on a compressed texture -- mips must be pre-baked
    // at cook time. Uncompressed formats accept this path too if the caller
    // wants pre-baked mips instead of GL-side generation.
    //
    // When mipBytes.Count == 1, this collapses to the single-mip case --
    // identical to CreateTexture2D for uncompressed; for compressed it
    // uploads just mip 0 and the GL sampler's MAX_LEVEL gets clamped to 0.
    TextureHandle CreateTexture2DMipped(
        TextureDescription description,
        IReadOnlyList<byte[]> mipBytes,
        string? name = null);

    // Uploads bytes to a specific mip level of an existing texture. Pairs
    // with CreateTexture2DMipped used in "create with smallest mip, then
    // stream the larger mips in over multiple frames" flows. The texture
    // must already exist; mipLevel must be < the level count derivable
    // from the texture's recorded width/height (mip0 = full size).
    //
    // Also widens the GL sampler's MIN_FILTER to a mipmap mode once 2+
    // levels have been uploaded so trilinear sampling actually picks up
    // the new finer mip. BASE_LEVEL is adjusted so sampling sees the
    // highest-quality mip available so far.
    void UploadTextureMip(TextureHandle handle, int mipLevel, ReadOnlySpan<byte> bytes);

    // Allocates a multi-mip texture with all storage reserved but no real
    // pixel data uploaded. The smallest mip is set as BASE_LEVEL = MAX_LEVEL
    // initially so samplers return from it (returns garbage until something
    // is uploaded to that level via UploadTextureMip). Used by the streamed-
    // upload path -- ResourceUploader allocates upfront, then drips real
    // mip data in over multiple frames.
    TextureHandle AllocateTexture2DMips(
        TextureDescription description,
        int mipCount,
        string? name = null);

    // Creates a 3D texture from a contiguous voxel array. Data layout is
    // x-major within rows, y-major within slices, z-major across slices --
    // i.e. voxel (x, y, z) is at byte offset (z*H*W + y*W + x) * bytesPerVoxel
    // for an unpacked single-channel format. Intended use is volumetric
    // rendering: ray-marched volume passes sample with a sampler3D uniform.
    TextureHandle CreateTexture3D(
        int width, int height, int depth,
        TextureFormat format,
        SamplerDescription sampler,
        ReadOnlySpan<byte> pixels,
        string? name = null);

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

    // HDR cubemap with caller-supplied mip chain. Used by the PBR specular
    // prefilter: each mip is pre-integrated at a specific roughness (mip K
    // has roughness K/(N-1)), so sampling with textureLod(R, roughness * (N-1))
    // returns the GGX-convolved env for that surface roughness -- the "split
    // sum" approximation's prefiltered colour term. mipFaces[k] must be
    // 6 * size * size * 4 Half values where size = baseFaceSize >> k.
    TextureHandle CreateTextureCubeHdrMipped(
        int baseFaceSize,
        IReadOnlyList<Half[]> mipFaces,
        SamplerDescription sampler,
        string? name = null);

    void DestroyTexture(TextureHandle handle);

    RenderSurface CreateRenderSurface(RenderSurfaceDescription description);

    void DestroyRenderSurface(RenderSurfaceHandle handle);

    ResourceRegistrySnapshot SnapshotResources();
}

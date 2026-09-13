namespace Blix.Graphics;

// Snapshot of device-level diagnostic counters surfaced to UI / overlays /
// CI. Frame-scoped values (FrameErrorCount, LastError*) reset at the start
// of each Execute() so a HUD line that reads "errors: 3" reflects the
// frame currently on screen — not the all-time total.
public readonly record struct GraphicsDeviceDiagnostics(
    int FrameErrorCount,
    string LastErrorContext,
    string LastErrorMessage,
    // Per-frame transient vertex arena gauges (bytes). Used = current ring slot's
    // bump usage this frame; HighWater = all-time per-slot peak; Capacity = per-slot
    // size. Surfaced in the debug overlay so per-frame upload traffic is visible.
    int TransientArenaBytesUsed = 0,
    int TransientArenaHighWaterBytes = 0,
    int TransientArenaCapacityBytes = 0,
    // Pipeline cache (GetOrCreatePipeline): distinct cached pipelines, plus
    // cumulative cache hits/misses since device creation. Hits climbing while
    // Distinct stays flat means variant/shared-state pipelines are being reused
    // rather than rebuilt.
    int PipelineCacheCount = 0,
    int PipelineCacheHits = 0,
    int PipelineCacheMisses = 0);

public interface IGraphicsDevice : IDisposable
{
    GraphicsDeviceInfo Info { get; }

    GraphicsDeviceDiagnostics DiagnosticsSnapshot { get; }

    void SetDefaultRenderSurfaceSize(int width, int height);

    VertexBufferHandle CreateVertexBuffer(VertexBufferData data, string? name = null);

    void UpdateVertexBuffer(VertexBufferHandle handle, ReadOnlySpan<byte> bytes, int byteOffset = 0);

    void DestroyVertexBuffer(VertexBufferHandle handle);

    // Sub-allocate a per-frame transient vertex slice and copy `data` into it. The
    // slice is valid only for the current frame (its backing ring slot is recycled
    // a few frames later). Bind the returned slice's buffer at its ByteOffset and
    // draw the matching index buffer with base-0 indices — the offset is aligned to
    // vertexStride so firstVertex = 0 addresses the slice. This is the race-free
    // replacement for "create one Dynamic vertex buffer and UpdateVertexBuffer it
    // every frame", which collided with in-flight GPU reads.
    TransientVertexSlice AllocVertices(ReadOnlySpan<byte> data, int vertexStride, string? name = null);

    IndexBufferHandle CreateIndexBuffer(
        IReadOnlyList<ushort> indices,
        GraphicsBufferUsage usage = GraphicsBufferUsage.Static,
        string? name = null);

    // 32-bit index buffer. Use for meshes with >65535 vertices in a single
    // primitive (large authored scenes -- some Sponza Modern packs hit this).
    // The backend records the format per handle; the draw path picks the
    // matching VkIndexType at draw time without caller involvement.
    IndexBufferHandle CreateIndexBuffer(
        IReadOnlyList<uint> indices,
        GraphicsBufferUsage usage = GraphicsBufferUsage.Static,
        string? name = null);

    void DestroyIndexBuffer(IndexBufferHandle handle);

    // Shader-program *creation* is backend-specific: the Vulkan backend builds
    // programs from compiled SPIR-V (VulkanGraphicsDevice.CreateShaderProgramFromSpv),
    // so it isn't on the backend-neutral contract. Destruction is generic.
    void DestroyShaderProgram(ShaderProgramHandle handle);

    PipelineHandle CreatePipeline(PipelineDescription description, string? name = null);

    // Cached, opt-in variant of CreatePipeline: returns the SAME handle for a
    // structurally-equal description (deduping the VkPipeline + its layout). Cached
    // pipelines are device-owned and live until teardown, so callers must NOT
    // DestroyPipeline a handle obtained here while it may still be in use elsewhere.
    // Use for pipelines whose description recurs (variant permutations, shared
    // blend modes); use CreatePipeline when you want sole ownership + explicit destroy.
    PipelineHandle GetOrCreatePipeline(PipelineDescription description, string? name = null);

    void DestroyPipeline(PipelineHandle handle);

    TextureHandle CreateTexture2D(TextureDescription description, ReadOnlySpan<byte> pixels, string? name = null);

    // Multi-mip texture upload. Each entry of `mipBytes` is one mip level's
    // packed pixel/block data; mip 0 (full size) first, then half, quarter,
    // etc. Required for compressed formats (BC7/BC5/BC6h) since their mips
    // can't be generated on the GPU -- they must be pre-baked at cook time.
    // Uncompressed formats accept this path too if the caller wants pre-baked
    // mips instead of runtime generation.
    //
    // When mipBytes.Count == 1, this collapses to the single-mip case --
    // identical to CreateTexture2D for uncompressed; for compressed it
    // uploads just mip 0.
    TextureHandle CreateTexture2DMipped(
        TextureDescription description,
        IReadOnlyList<byte[]> mipBytes,
        string? name = null);

    // Uploads bytes to a specific mip level of an existing texture. Pairs
    // with AllocateTexture2DMips in "allocate the chain, then stream the
    // levels in over multiple frames" flows. The texture must already exist;
    // mipLevel must be < the level count derivable from the texture's
    // recorded width/height (mip0 = full size). A level's contents are
    // undefined until it has been uploaded.
    void UploadTextureMip(TextureHandle handle, int mipLevel, ReadOnlySpan<byte> bytes);

    // The same upload for data that changes EVERY frame, recorded with the
    // frame's own commands instead of submitted on its own and waited for.
    //
    // UploadTextureMip ends in a full queue drain, which is what makes it safe
    // to sample on the caller's next line and ruinous in a frame loop: CPU and
    // GPU stop overlapping, so the frame costs CPU + GPU rather than
    // max(CPU, GPU). Measured on a per-frame fog mask, the drain was 20 ms of a
    // 28 ms frame, and the frames that happened to skip the upload ran at 7.
    //
    // The bytes are copied into a staging ring immediately, so the caller's
    // buffer is free on return; the copy lands before the frame's first render
    // pass. The contents are NOT resident until that frame executes, so anything
    // that must read the data back — or sample it in the same breath — wants
    // UploadTextureMip instead.
    void QueueTextureUpload(TextureHandle handle, int mipLevel, ReadOnlySpan<byte> bytes);

    // Allocates a multi-mip texture with all storage reserved but no pixel
    // data uploaded -- every level's contents are undefined until filled via
    // UploadTextureMip. Used by the streamed-upload path: ResourceUploader
    // allocates upfront, then drips real mip data in over multiple frames,
    // smallest level first.
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

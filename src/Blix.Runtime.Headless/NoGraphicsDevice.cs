using Blix.Graphics;

namespace Blix.Runtime.Headless;

/// <summary>
/// The device a headless loop is handed: it describes itself, and refuses to make anything.
/// </summary>
/// <remarks>
/// <para>
/// <b>Loud rather than null.</b> A loop that creates a buffer in a headless run has asked for
/// something that cannot exist here. A null device would answer with a NullReferenceException from
/// somewhere inside the loop; this answers with the call that was made and why it cannot be.
/// A loop that casts it to a backend device fails at the cast, which is just as plain.
/// </para>
/// <para>
/// <b>Destroying is refused too,</b> because there is nothing it could be destroying. Only what a
/// host itself calls, sizing the default surface and taking an inventory, is allowed.
/// </para>
/// </remarks>
public sealed class NoGraphicsDevice : IGraphicsDevice
{
    /// <inheritdoc />
    public GraphicsDeviceInfo Info { get; } = new("Blix", "headless (no device)", "0", "none");

    /// <inheritdoc />
    public GraphicsDeviceDiagnostics DiagnosticsSnapshot => new(0, string.Empty, string.Empty);

    /// <inheritdoc />
    public void SetDefaultRenderSurfaceSize(int width, int height) { }

    /// <inheritdoc />
    public ResourceRegistrySnapshot SnapshotResources() => new([], [], [], [], [], []);

    /// <inheritdoc />
    public void Dispose() { }

    /// <inheritdoc />
    public VertexBufferHandle CreateVertexBuffer(VertexBufferData data, string? name = null) => throw Refuse();

    /// <inheritdoc />
    public void UpdateVertexBuffer(VertexBufferHandle handle, ReadOnlySpan<byte> bytes, int byteOffset = 0) => throw Refuse();

    /// <inheritdoc />
    public void DestroyVertexBuffer(VertexBufferHandle handle) => throw Refuse();

    /// <inheritdoc />
    public TransientVertexSlice AllocVertices(ReadOnlySpan<byte> data, int vertexStride, string? name = null) => throw Refuse();

    /// <inheritdoc />
    public IndexBufferHandle CreateIndexBuffer(
        IReadOnlyList<ushort> indices, GraphicsBufferUsage usage = GraphicsBufferUsage.Static, string? name = null) => throw Refuse();

    /// <inheritdoc />
    public IndexBufferHandle CreateIndexBuffer(
        IReadOnlyList<uint> indices, GraphicsBufferUsage usage = GraphicsBufferUsage.Static, string? name = null) => throw Refuse();

    /// <inheritdoc />
    public void DestroyIndexBuffer(IndexBufferHandle handle) => throw Refuse();

    public ShaderProgramHandle CreateShaderProgramFromSpv(
        byte[] vertexSpv, byte[] fragmentSpv, ShaderInterface shaderInterface, string? name = null) => throw Refuse();

    public ShaderProgramHandle CreateComputeShaderProgramFromSpv(
        byte[] computeSpv, ShaderInterface shaderInterface, string? name = null) => throw Refuse();

    public PipelineHandle CreateComputePipeline(ShaderProgramHandle program, string? name = null) => throw Refuse();

    public IMaterialBindings CreateMaterial(
        ShaderProgramHandle program, int setIndex = DescriptorSets.Material, int framesInFlight = 1, string? name = null) => throw Refuse();

    public void DestroyMaterial(MaterialHandle handle) => throw Refuse();

    public TextureHandle CreateTextureCube(
        int faceSize, TextureFormat format, int mipCount, ReadOnlySpan<byte> data, SamplerDescription sampler, string name) => throw Refuse();

    public TextureHandle CreateStorageTexture2D(
        int width, int height, TextureFormat format, SamplerDescription sampler, string? name = null) => throw Refuse();

    public TextureHandle CreateStorageTexture3D(
        int width, int height, int depth, TextureFormat format, SamplerDescription sampler, string? name = null) => throw Refuse();

    public byte[] ReadTexture(TextureHandle handle, out int width, out int height, out TextureFormat format) => throw Refuse();

    public bool TryGetTextureSize(TextureHandle handle, out int width, out int height) => throw Refuse();

    /// <inheritdoc />
    /// <remarks>Nothing was submitted, so nothing is waited for.</remarks>
    public void WaitIdle() { }

    /// <inheritdoc />
    /// <remarks>One, the frame being stepped: a loop keeping per-slot copies keeps one here.</remarks>
    public int MaxFramesInFlightCount => 1;

    public int CurrentFrameSlot => 0;

    /// <inheritdoc />
    /// <remarks>There is no display to wait for; setting it changes nothing.</remarks>
    public bool VsyncEnabled { get => false; set { } }

    public int MaxMsaaSamples => 1;

    /// <inheritdoc />
    public void DestroyShaderProgram(ShaderProgramHandle handle) => throw Refuse();

    /// <inheritdoc />
    public PipelineHandle CreatePipeline(PipelineDescription description, string? name = null) => throw Refuse();

    /// <inheritdoc />
    public PipelineHandle GetOrCreatePipeline(PipelineDescription description, string? name = null) => throw Refuse();

    /// <inheritdoc />
    public void DestroyPipeline(PipelineHandle handle) => throw Refuse();

    /// <inheritdoc />
    public TextureHandle CreateTexture2D(TextureDescription description, ReadOnlySpan<byte> pixels, string? name = null) => throw Refuse();

    /// <inheritdoc />
    public TextureHandle CreateTexture2DMipped(
        TextureDescription description, IReadOnlyList<byte[]> mipBytes, string? name = null) => throw Refuse();

    /// <inheritdoc />
    public void UploadTextureMip(TextureHandle handle, int mipLevel, ReadOnlySpan<byte> bytes) => throw Refuse();

    /// <inheritdoc />
    public void QueueTextureUpload(TextureHandle handle, int mipLevel, ReadOnlySpan<byte> bytes) => throw Refuse();

    /// <inheritdoc />
    public TextureHandle AllocateTexture2DMips(TextureDescription description, int mipCount, string? name = null) => throw Refuse();

    /// <inheritdoc />
    public TextureHandle CreateTexture3D(
        int width, int height, int depth, TextureFormat format, SamplerDescription sampler,
        ReadOnlySpan<byte> pixels, string? name = null) => throw Refuse();

    /// <inheritdoc />
    public TextureHandle CreateTextureCubeHdr(
        int faceSize, ReadOnlySpan<Half> faces, SamplerDescription sampler, string? name = null) => throw Refuse();

    /// <inheritdoc />
    public TextureHandle CreateTextureCubeHdrMipped(
        int baseFaceSize, IReadOnlyList<Half[]> mipFaces, SamplerDescription sampler, string? name = null) => throw Refuse();

    /// <inheritdoc />
    public void DestroyTexture(TextureHandle handle) => throw Refuse();

    /// <inheritdoc />
    public RenderSurface CreateRenderSurface(RenderSurfaceDescription description) => throw Refuse();

    /// <inheritdoc />
    public void DestroyRenderSurface(RenderSurfaceHandle handle) => throw Refuse();

    private static InvalidOperationException Refuse([System.Runtime.CompilerServices.CallerMemberName] string call = "") =>
        new($"{call} needs a graphics device, and a headless run has none. Create GPU resources only " +
            "when the loop runs in a window, or keep this loop's headless path free of them.");
}

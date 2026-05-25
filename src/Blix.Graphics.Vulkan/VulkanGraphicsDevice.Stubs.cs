namespace Blix.Graphics.Vulkan;

// IGraphicsDevice methods we haven't lit up yet. Buffers, shader programs,
// and pipelines moved out to VulkanGraphicsDevice.Resources.cs as they came
// online. What remains: textures + render surfaces (their own follow-up
// push), and the GLSL-text CreateShaderProgram path (deferred until we
// link libshaderc for runtime compilation; demos use pre-compiled SPIR-V
// via CreateShaderProgramFromSpv).
public sealed partial class VulkanGraphicsDevice
{
    private const string NotYet = "Vulkan backend: this resource path is not implemented yet.";

    public ShaderProgramHandle CreateShaderProgram(ShaderSources sources) =>
        throw new NotImplementedException(
            NotYet + " Use CreateShaderProgramFromSpv(byte[] vertSpv, byte[] fragSpv) until runtime GLSL→SPIR-V compilation is wired.");

    public ShaderProgramHandle CreateShaderProgram(ShaderProgramDescription description) =>
        throw new NotImplementedException(
            NotYet + " Use CreateShaderProgramFromSpv(byte[] vertSpv, byte[] fragSpv) until runtime GLSL→SPIR-V compilation is wired.");

    public TextureHandle CreateTexture2DMipped(
        TextureDescription description,
        IReadOnlyList<byte[]> mipBytes,
        string? name = null) =>
        throw new NotImplementedException(NotYet);

    public void UploadTextureMip(TextureHandle handle, int mipLevel, ReadOnlySpan<byte> bytes) =>
        throw new NotImplementedException(NotYet);

    public TextureHandle AllocateTexture2DMips(
        TextureDescription description,
        int mipCount,
        string? name = null) =>
        throw new NotImplementedException(NotYet);

    public TextureHandle CreateTexture3D(
        int width, int height, int depth,
        TextureFormat format,
        SamplerDescription sampler,
        ReadOnlySpan<byte> pixels,
        string? name = null) =>
        throw new NotImplementedException(NotYet);

    public TextureHandle CreateTextureCube(int faceSize, ReadOnlySpan<byte> faces, SamplerDescription sampler, string? name = null) =>
        throw new NotImplementedException(NotYet);

    public TextureHandle CreateTextureCubeDepth(int faceSize, SamplerDescription sampler, string? name = null) =>
        throw new NotImplementedException(NotYet);

    public TextureHandle CreateTextureCubeHdr(int faceSize, ReadOnlySpan<Half> faces, SamplerDescription sampler, string? name = null) =>
        throw new NotImplementedException(NotYet);

    public TextureHandle CreateTextureCubeHdrMipped(
        int baseFaceSize,
        IReadOnlyList<Half[]> mipFaces,
        SamplerDescription sampler,
        string? name = null) =>
        throw new NotImplementedException(NotYet);

    public RenderSurface CreateRenderSurface(RenderSurfaceDescription description) =>
        throw new NotImplementedException(NotYet);

    public void DestroyRenderSurface(RenderSurfaceHandle handle) =>
        throw new NotImplementedException(NotYet);
}

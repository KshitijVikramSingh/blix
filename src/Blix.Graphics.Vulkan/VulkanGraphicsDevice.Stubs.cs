namespace Blix.Graphics.Vulkan;

// IGraphicsDevice methods not yet lit up on the Vulkan backend.
// CreateShaderProgram(...) needs runtime GLSL→SPIR-V (libshaderc);
// callers should use CreateShaderProgramFromSpv. Texture creation
// variants land alongside their first consumer.
public sealed partial class VulkanGraphicsDevice
{
    private const string NotYet = "Vulkan backend: this resource path is not implemented yet.";

    public ShaderProgramHandle CreateShaderProgram(ShaderSources sources) =>
        throw new NotImplementedException(
            NotYet + " Use CreateShaderProgramFromSpv(byte[] vertSpv, byte[] fragSpv) until runtime GLSL→SPIR-V compilation is wired.");

    public ShaderProgramHandle CreateShaderProgram(ShaderProgramDescription description) =>
        throw new NotImplementedException(
            NotYet + " Use CreateShaderProgramFromSpv(byte[] vertSpv, byte[] fragSpv) until runtime GLSL→SPIR-V compilation is wired.");

    // CreateTexture2DMipped is implemented in VulkanGraphicsDevice.Textures.cs
    // (cooked .blixtex mip-chain upload, incl. BC formats).

    public void UploadTextureMip(TextureHandle handle, int mipLevel, ReadOnlySpan<byte> bytes) =>
        throw new NotImplementedException(NotYet);

    public TextureHandle AllocateTexture2DMips(
        TextureDescription description,
        int mipCount,
        string? name = null) =>
        throw new NotImplementedException(NotYet);

    // CreateTexture3D / CreateStorageTexture3D are implemented in
    // VulkanGraphicsDevice.Textures.cs (Type3D images).

    public TextureHandle CreateTextureCube(int faceSize, ReadOnlySpan<byte> faces, SamplerDescription sampler, string? name = null) =>
        throw new NotImplementedException(NotYet);

    public TextureHandle CreateTextureCubeDepth(int faceSize, SamplerDescription sampler, string? name = null) =>
        throw new NotImplementedException(NotYet);

    // CreateTextureCubeHdr / CreateTextureCubeHdrMipped are implemented in
    // VulkanGraphicsDevice.Textures.cs (real RGBA16F cube uploads for cooked
    // .blixprobe IBL).
}

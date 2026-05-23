namespace Blix.Graphics;

public sealed record TextureDescription(
    int Width,
    int Height,
    TextureFormat Format,
    SamplerDescription Sampler);

public enum TextureFormat
{
    Rgba8 = 0,
    Depth24,
    // 16-bit floating-point per channel. Allows values >1 in render targets, which is
    // the prerequisite for HDR pipelines (bloom bright-pass, tone mapping). Not
    // user-uploadable from byte arrays; create via render-surface attachments.
    Rgba16F,
    // Single-channel 8-bit unsigned, GL_R8. Used by volumetric textures storing a
    // density scalar per voxel; sampled in GLSL as the .r component of a vec4.
    R8
}

public sealed record SamplerDescription(
    TextureFilter MinFilter,
    TextureFilter MagFilter,
    TextureWrap WrapU,
    TextureWrap WrapV,
    bool GenerateMipmaps,
    // Compare flips the texture into depth-comparison mode (GL_COMPARE_REF_TO_TEXTURE
    // with GL_LEQUAL). Required for sampler2DShadow lookups — the GPU then performs a
    // free 2x2 bilinear PCF compare on each texture() call. Only meaningful on depth
    // textures; ignored for color textures.
    bool Compare = false,
    // Wrap mode for the third (W) axis. Only consumed by 3D textures via
    // CreateTexture3D; ignored by 2D/Cube paths. Defaults to ClampToEdge so
    // existing 2D-only call sites stay unaffected.
    TextureWrap WrapW = TextureWrap.ClampToEdge)
{
    public static SamplerDescription PixelatedRepeat { get; } = new(
        TextureFilter.Nearest,
        TextureFilter.Nearest,
        TextureWrap.Repeat,
        TextureWrap.Repeat,
        GenerateMipmaps: false);

    public static SamplerDescription LinearClamp { get; } = new(
        TextureFilter.Linear,
        TextureFilter.Linear,
        TextureWrap.ClampToEdge,
        TextureWrap.ClampToEdge,
        GenerateMipmaps: false);

    public static SamplerDescription LinearRepeat { get; } = new(
        TextureFilter.Linear,
        TextureFilter.Linear,
        TextureWrap.Repeat,
        TextureWrap.Repeat,
        GenerateMipmaps: false);

    // ClampToEdge + linear + mipmaps enabled. Used for IBL env cubemaps where
    // textureLod(uEnvMap, R, roughness * (mipCount-1)) needs the mip chain to
    // produce roughness-driven blur for specular reflection.
    public static SamplerDescription LinearClampMipmap { get; } = new(
        TextureFilter.Linear,
        TextureFilter.Linear,
        TextureWrap.ClampToEdge,
        TextureWrap.ClampToEdge,
        GenerateMipmaps: true);
}

public enum TextureFilter
{
    Nearest = 0,
    Linear
}

public enum TextureWrap
{
    Repeat = 0,
    ClampToEdge
}

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
    // Packed 32-bit unsigned-float HDR: 11 bits R, 11 bits G, 10 bits B, no alpha
    // (VK_FORMAT_B10G11R11_UFLOAT_PACK32 / GL_R11F_G11F_B10F). Half the bandwidth +
    // tile footprint of Rgba16F, which matters most for MSAA scene-colour targets on
    // TBDR GPUs (the on-chip tile holds samples × bytes-per-pixel). Trades fp16's
    // 10-bit mantissa for 5/5/5-bit mantissas and drops alpha — fine for an opaque
    // HDR radiance target sampled .rgb by tonemap; unsigned, so radiance only (no
    // negatives). Render-surface attachment only, not byte-uploadable.
    R11G11B10F,
    // Single-channel 8-bit unsigned, GL_R8. Used by volumetric textures storing a
    // density scalar per voxel; sampled in GLSL as the .r component of a vec4.
    R8,
    // Same byte layout as Rgba8 but uploaded with GL_SRGB8_ALPHA8 internal format
    // so the GPU does sRGB->linear conversion at sample time. This makes bilinear/
    // trilinear filtering and mip pyramid sampling operate in linear space (the
    // physically correct behaviour); without it, filtering averages gamma-encoded
    // bytes and produces too-dark results, especially at mip transitions on high-
    // contrast textures. Use for glTF BaseColor + Emissive sources (sRGB per spec).
    Rgba8Srgb,
    // BC7 (BPTC) compressed RGBA, sRGB-encoded. The workhorse compressed colour
    // format -- 8 bits per pixel (4x compression vs Rgba8), high quality.
    // Pixel data is stored as 16-byte blocks per 4x4 texel region. Width +
    // height should be multiples of 4; smaller edge-mips pad up to 4.
    Bc7Srgb,
    // BC7 (BPTC) compressed RGBA, linear-space. For non-colour data like
    // metallic-roughness, occlusion, or roughness-only packed channels.
    Bc7Unorm,
    // BC5 (RGTC2) compressed two-channel signed/unsigned. The canonical normal-
    // map format: stores X + Y, reconstructs Z = sqrt(1 - x*x - y*y) in shader.
    // 8 bpp (4x compression vs Rgba8); higher quality than BC7 for normals
    // because the encoder isn't trying to preserve a B channel.
    Bc5Unorm,
    // BC6h (BPTC) compressed RGB half-float, unsigned. For HDR sources
    // (env probes, baked irradiance). 8 bpp; preserves >1.0 values that BC7
    // would clamp.
    Bc6hUf16,
}

// Helpers for the compressed-format family. Centralised so the backend +
// cook tool + .blixtex format agree on byte-layout math.
public static class TextureFormatExtensions
{
    public static bool IsCompressed(this TextureFormat format) => format switch
    {
        TextureFormat.Bc7Srgb or TextureFormat.Bc7Unorm
            or TextureFormat.Bc5Unorm or TextureFormat.Bc6hUf16 => true,
        _ => false,
    };

    // Bytes required to store one mip level at the given size. For
    // uncompressed formats: width*height*bpp. For BCn: 16 bytes per 4x4
    // block, padded -- so a 1x1 mip still costs 16 bytes.
    public static int MipByteCount(this TextureFormat format, int width, int height) => format switch
    {
        TextureFormat.Rgba8 => width * height * 4,
        TextureFormat.Rgba8Srgb => width * height * 4,
        TextureFormat.Rgba16F => width * height * 8,
        TextureFormat.R11G11B10F => width * height * 4,
        TextureFormat.R8 => width * height,
        // BC7 / BC5 / BC6h are all 16 bytes per 4x4 block.
        TextureFormat.Bc7Srgb or TextureFormat.Bc7Unorm
            or TextureFormat.Bc5Unorm or TextureFormat.Bc6hUf16
                => ((width + 3) / 4) * ((height + 3) / 4) * 16,
        TextureFormat.Depth24 => throw new ArgumentException(
            "Depth formats don't have a fixed mip byte count.", nameof(format)),
        _ => throw new ArgumentOutOfRangeException(nameof(format), format, null),
    };

    // Total bytes for a full mip chain of `mipCount` levels starting at
    // width×height, each level halving (floored, min 1) in both dimensions —
    // the standard pyramid the upload path allocates. Single-layer (2D); cube
    // and 3D callers scale by face count / depth. Used by the diagnostics
    // snapshot to report per-texture footprint.
    public static long TextureByteCount(this TextureFormat format, int width, int height, int mipCount)
    {
        if (mipCount < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(mipCount), "Mip count must be at least 1.");
        }

        long total = 0;
        var w = width;
        var h = height;
        for (var level = 0; level < mipCount; level++)
        {
            total += format.MipByteCount(w, h);
            w = Math.Max(1, w / 2);
            h = Math.Max(1, h / 2);
        }
        return total;
    }
}

public sealed record SamplerDescription(
    TextureFilter MinFilter,
    TextureFilter MagFilter,
    TextureWrap WrapU,
    TextureWrap WrapV,
    bool GenerateMipmaps,
    // Compare puts the sampler in depth-comparison mode (Vulkan compareEnable with
    // compareOp = LessOrEqual). Required for sampler2DShadow lookups — the GPU then
    // performs a free 2x2 bilinear PCF compare on each texture() call. Only meaningful
    // on depth textures; ignored for color textures.
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

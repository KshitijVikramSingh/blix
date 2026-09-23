using Blix.Graphics;
using Blix.Graphics.Images;
using Blix.Render;

namespace Blix;

// The five resolved GPU textures for a glTF material (engine defaults where a
// slot is absent). The caller binds these into its own descriptor set.
public readonly record struct MaterialTextures(
    TextureHandle Albedo,
    TextureHandle Normal,
    TextureHandle MetallicRoughness,
    TextureHandle Emissive,
    TextureHandle Occlusion);

// Engine glTF texture loader — the asset-pipeline half of material handling:
// read/decode/upload/dedup/stream a material's textures into GPU handles. Owns
//   - the per-slot sRGB-vs-linear format policy (glTF spec: BaseColor/Emissive
//     are sRGB; Normal/MR/Occlusion are linear),
//   - cooked-`.blixtex` (streamed, mip-chained) vs decoded-PNG routing,
//   - per-source dedup,
//   - standard glTF default fallbacks (white / flat-normal / black / neutral),
//   - a budgeted mip-upload queue (ResourceUploader) — drive Drain() per frame.
//
// It does NOT build the material UBO or descriptor set — binding is the
// renderer's, and (post SPIR-V reflection) the UBO layout is the game's. The
// caller does `loader.Load(gltfMaterial)` → writes its UBO from the material's
// scalar factors → binds the returned handles.
public sealed class GltfTextureLoader
{
    private readonly IGraphicsDevice device;
    private readonly ResourceUploader uploader;

    // The registry keys resource identity together with channel format so cross-import sharing
    // preserves sRGB/linear distinctions.
    private readonly TextureRegistry registry;

    /// <summary>What this loader has resident — distinct textures, their bytes, and uploads avoided.</summary>
    public TextureRegistry Registry => registry;

    private readonly TextureHandle fallbackAlbedo;   // 1×1 white sRGB  → BaseColorFactor drives
    private readonly TextureHandle flatNormal;       // 1×1 (128,128,255) linear → tangent "up"
    private readonly TextureHandle blackEmissive;    // 1×1 black sRGB  → no glow
    private readonly TextureHandle defaultMr;        // 1×1 white linear → factors pass through
    private readonly TextureHandle defaultAo;        // 1×1 white linear → no occlusion

    /// <param name="shared">
    /// A registry to share with other owners, or null to keep one of this loader's own.
    /// </param>
    /// <remarks>
    /// Sharing is explicit because the caller also chooses the associated lifetime scope. Two
    /// loaders given the same registry reuse identified textures; null creates a private scope.
    /// </remarks>
    public GltfTextureLoader(IGraphicsDevice device, TextureRegistry? shared = null)
    {
        ArgumentNullException.ThrowIfNull(device);
        this.device = device;
        registry = shared ?? new TextureRegistry();
        uploader = new ResourceUploader(device);

        fallbackAlbedo = device.CreateTexture2D(
            new TextureDescription(1, 1, TextureFormat.Rgba8Srgb, SamplerDescription.LinearRepeat),
            new byte[] { 255, 255, 255, 255 }, "gltf.default.albedo");
        flatNormal = device.CreateTexture2D(
            new TextureDescription(1, 1, TextureFormat.Rgba8, SamplerDescription.LinearRepeat),
            new byte[] { 128, 128, 255, 255 }, "gltf.default.normal");
        blackEmissive = device.CreateTexture2D(
            new TextureDescription(1, 1, TextureFormat.Rgba8Srgb, SamplerDescription.LinearRepeat),
            new byte[] { 0, 0, 0, 255 }, "gltf.default.emissive");
        defaultMr = device.CreateTexture2D(
            new TextureDescription(1, 1, TextureFormat.Rgba8, SamplerDescription.LinearRepeat),
            new byte[] { 255, 255, 255, 255 }, "gltf.default.mr");
        defaultAo = device.CreateTexture2D(
            new TextureDescription(1, 1, TextureFormat.Rgba8, SamplerDescription.LinearRepeat),
            new byte[] { 255, 255, 255, 255 }, "gltf.default.ao");
    }

    // The upload queue: drive Drain(budgetMs) each frame; PendingCount == 0
    // when every texture's mips have landed (callers gate their full-detail
    // render path on this).
    public int PendingCount => uploader.PendingCount;
    public void Drain(double budgetMillis) => uploader.Drain(budgetMillis);

    // Resolve a material's five textures to GPU handles (defaults where absent),
    // deduplicated by resource identity and format, with budgeted cooked-mip uploads.
    public MaterialTextures Load(GltfMaterial? material) => new(
        Resolve(material?.BaseColorTexture, fallbackAlbedo, TextureFormat.Rgba8Srgb, "albedo"),
        Resolve(material?.NormalTexture, flatNormal, TextureFormat.Rgba8, "normal"),
        Resolve(material?.MetallicRoughnessTexture, defaultMr, TextureFormat.Rgba8, "mr"),
        Resolve(material?.EmissiveTexture, blackEmissive, TextureFormat.Rgba8Srgb, "emissive"),
        Resolve(material?.OcclusionTexture, defaultAo, TextureFormat.Rgba8, "ao"));

    private TextureHandle Resolve(
        GltfTexture? tex,
        TextureHandle fallback,
        TextureFormat uploadFormat,
        string channelTag)
    {
        if (tex is null) return fallback;

        return registry.GetOrAdd(tex, uploadFormat, () => Upload(tex, uploadFormat, channelTag, fallback));
    }

    private TextureHandle Upload(
        GltfTexture tex, TextureFormat uploadFormat, string channelTag, TextureHandle fallback)
    {
        var label = $"gltf.{channelTag}.{tex.Name}";
        TextureHandle handle;
        if (tex.LazyHandle is { } lazy)
        {
            // Cooked .blixtex: pre-baked BC (or Rgba8) mip chain on disk. tex.Format
            // already encodes the sRGB choice (BC7Srgb albedo vs BC7Unorm MR, BC5
            // normal), so use it directly. Allocate the chain now (materials bind a
            // stable handle immediately) and queue per-mip uploads across later drains. Callers must
            // not sample undefined levels; current applications hold a flat preview until the queue
            // drains. One file open per texture (not per mip) — see
            // CreateBufferedMipReader — which matters on a high-open-latency volume.
            handle = device.AllocateTexture2DMips(
                new TextureDescription(tex.Width, tex.Height, tex.Format, SamplerDescription.LinearRepeat),
                tex.MipCount, label);
            uploader.EnqueueInto(
                handle, tex.Format, tex.Width, tex.Height, tex.MipCount,
                BlixTexReader.CreateBufferedMipReader(lazy));
        }
        else if (tex.MipBytes is { Count: > 0 } mips)
        {
            // Eager path: decoded-PNG (single Rgba8 mip — blit-generates the chain)
            // or an eager .blixtex read.
            handle = tex.MipCount > 1
                ? device.CreateTexture2DMipped(
                    new TextureDescription(tex.Width, tex.Height, tex.Format, SamplerDescription.LinearRepeat), mips, label)
                : device.CreateTexture2D(
                    new TextureDescription(tex.Width, tex.Height, uploadFormat, SamplerDescription.LinearRepeat), mips[0], label);
        }
        else
        {
            // No pixels at all — the channel's neutral stand-in. Registered under this texture's
            // key like any other result, so a second material naming the same empty texture does
            // not re-derive it.
            return fallback;
        }

        return handle;
    }
}

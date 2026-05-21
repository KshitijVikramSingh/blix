namespace Blix.Graphics;

public sealed record RenderSurface(
    RenderSurfaceHandle Handle,
    IReadOnlyList<TextureHandle> ColorAttachments,
    TextureHandle? DepthTexture);

public sealed record RenderSurfaceDescription(
    string Name,
    RenderSurfaceSize Size,
    IReadOnlyList<ColorAttachmentDescription> ColorAttachments,
    DepthAttachmentDescription? Depth);

public sealed record ColorAttachmentDescription(
    TextureFormat Format,
    SamplerDescription Sampler);

public abstract record DepthAttachmentDescription;

public sealed record DepthRenderbuffer : DepthAttachmentDescription;

public sealed record DepthTexture(SamplerDescription Sampler) : DepthAttachmentDescription;

// Attaches one face of an existing depth cubemap as this surface's depth target.
// The cubemap is created via CreateTextureCubeDepth; the surface references it
// rather than owning its lifetime. Six surfaces -- one per face -- collectively
// render a full omnidirectional depth map for point-light shadow casting.
//
// Face indices follow the OpenGL cubemap convention:
//   0 = +X, 1 = -X, 2 = +Y, 3 = -Y, 4 = +Z, 5 = -Z.
public sealed record DepthCubeFace(TextureHandle CubeMap, int Face) : DepthAttachmentDescription;

public abstract record RenderSurfaceSize;

public sealed record FixedRenderSurfaceSize(int Width, int Height) : RenderSurfaceSize;

public sealed record MatchDefaultRenderSurfaceSize(float Scale = 1.0f) : RenderSurfaceSize;

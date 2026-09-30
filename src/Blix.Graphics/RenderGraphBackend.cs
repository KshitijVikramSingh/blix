namespace Blix.Graphics;

/// <summary>A device that can realise a render graph.</summary>
/// <remarks>
/// Internal to the engine on purpose: a backend implements it, and an application never names it. The
/// application's side is <c>new RenderGraph(device)</c> with whatever <see cref="IGraphicsDevice"/> it
/// was given, and a device that cannot do this refuses there, by name.
/// </remarks>
internal interface IRenderGraphDevice
{
    IRenderGraphBackend CreateRenderGraphBackend(RenderGraph graph);
}

/// <summary>What realises one graph on one device: its images, passes and their surfaces.</summary>
/// <remarks>
/// The graph asks it for exactly two things per frame, a graphics pass's surface and a resource's
/// sampleable texture, both engine handles. Everything it allocates behind those is its own.
/// </remarks>
internal interface IRenderGraphBackend : IDisposable
{
    /// <summary>Allocates everything the graph declared. Called once, after validation.</summary>
    void Compile();

    /// <summary>The surface a graphics pass draws into; a compute pass reports an empty handle.</summary>
    bool TryGetPassSurface(int passId, out RenderSurfaceHandle surface);

    /// <summary>A target's sampleable texture, or null for one that cannot be sampled (a multisampled target).</summary>
    bool TryGetSampleable(int resourceId, out TextureHandle? texture);

    /// <summary>Advances whenever swapchain-sized targets were reallocated.</summary>
    ulong MatchSwapchainResourceGeneration { get; }
}

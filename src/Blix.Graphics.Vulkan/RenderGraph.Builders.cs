namespace Blix.Graphics.Vulkan;

// Fluent builders for graph pass declarations. Thin proxies — each
// chained call mutates the underlying entry and returns this. After
// Compile() the graph freezes; builders held past that point throw.
//
// Graphics and compute passes are both recorded per frame and replayed in
// declaration order. The builders describe topology only; graph.Pass and
// graph.Dispatch supply that frame's work after Compile freezes it.

public sealed class GraphicsPassBuilder
{
    private readonly RenderGraph graph;
    private readonly GraphicsPassEntry entry;

    internal GraphicsPassBuilder(RenderGraph graph, GraphicsPassEntry entry)
    {
        this.graph = graph;
        this.entry = entry;
    }

    public PassHandle Handle => entry.Handle;
    public string Name => entry.Name;

    // View can be whole-image (implicit GraphResourceHandle conversion) or
    // a sub-resource (e.g. cube.Face(i)). LoadOp/StoreOp follow Vulkan.
    public GraphicsPassBuilder Target(TextureView view, LoadOp load, StoreOp store)
    {
        EnsureMutable();
        entry.ColorTargets.Add(new ColorAttachmentBinding(view, load, store));
        return this;
    }

    public GraphicsPassBuilder Target(GraphResourceHandle handle, LoadOp load, StoreOp store) =>
        Target((TextureView)handle, load, store);

    // MSAA resolve destination for the color target at the same index. The
    // pass renders into its (multisample) Target and resolves into this 1×
    // target on store; downstream passes sample the resolve target.
    public GraphicsPassBuilder ResolveColor(TextureView view)
    {
        EnsureMutable();
        entry.ResolveTargets.Add(view);
        return this;
    }

    public GraphicsPassBuilder ResolveColor(GraphResourceHandle handle) =>
        ResolveColor((TextureView)handle);

    /// <summary>
    /// Resolve this pass's multisampled DEPTH into a 1x target, so something can sample it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A multisampled attachment is not sampleable, and that is what this is for.</b> A pass that
    /// multisamples its depth and also needs that depth readable afterwards — to carry it to the
    /// swapchain, to drive a depth-based effect — has no way to get at it otherwise. The studio
    /// stage is the case that asked: its present pass writes gl_FragDepth from the scene's depth so
    /// debug gizmos depth-test against the scene, and turning MSAA on broke exactly that.
    /// </para>
    /// <para>
    /// <b>Asking for this builds the pass with vkCreateRenderPass2.</b> Depth resolve is a structure
    /// chained onto VkSubpassDescription2 and has no equivalent in the original call, so a pass that
    /// wants it takes a second construction path. Every pass that does NOT ask keeps the original
    /// one, byte for byte — this is an addition, not a migration, because the graph is shared by two
    /// games and a rewrite of pass creation is not a thing to do as the tail of a feature.
    /// </para>
    /// <para>
    /// The resolve mode is SAMPLE_ZERO. Averaging depth is meaningless across a silhouette — the
    /// mean of a near sample and a far one is a surface that is not there — and min/max are not
    /// portable. Sample zero is what a depth-forward wants: one real sample of the real geometry.
    /// </para>
    /// </remarks>
    public GraphicsPassBuilder ResolveDepth(TextureView view)
    {
        EnsureMutable();
        entry.DepthResolveTarget = view;
        return this;
    }

    /// <inheritdoc cref="ResolveDepth(TextureView)"/>
    public GraphicsPassBuilder ResolveDepth(GraphResourceHandle handle) =>
        ResolveDepth((TextureView)handle);

    // Declare the depth attachment for this pass. At most one per pass;
    // a second call replaces the previous declaration.
    public GraphicsPassBuilder Depth(TextureView view, LoadOp load, StoreOp store)
    {
        EnsureMutable();
        entry.Depth = new DepthAttachmentBinding(view, load, store);
        return this;
    }

    public GraphicsPassBuilder Depth(GraphResourceHandle handle, LoadOp load, StoreOp store) =>
        Depth((TextureView)handle, load, store);

    // Read edge — the view must reference a resource declared by an
    // earlier pass (validated at Compile time).
    public GraphicsPassBuilder Read(TextureView view)
    {
        EnsureMutable();
        entry.Reads.Add(view);
        return this;
    }

    public GraphicsPassBuilder Read(GraphResourceHandle handle) =>
        Read((TextureView)handle);

    /// <summary>A read of what the PREVIOUS frame wrote into this resource.</summary>
    /// <remarks>
    /// <b>The one read that legitimately precedes its writer.</b> Temporal accumulation samples the
    /// last frame's result and then overwrites it, so the pass that produces the data is declared
    /// after the pass that consumes it and the ordering check would reject the pair. This declares
    /// the read — the layout transition and the barrier are exactly as real as any other — and
    /// exempts only the ordering assertion.
    ///
    /// Two obligations come with it, and neither is the graph's to enforce. The first frame reads
    /// undefined contents, so a caller must gate the blend until it has written once. And the
    /// resource must not be resized or re-created underneath the history without the caller
    /// invalidating it, for the same reason.
    /// </remarks>
    public GraphicsPassBuilder ReadHistory(GraphResourceHandle handle)
    {
        EnsureMutable();
        entry.Reads.Add((TextureView)handle);
        entry.HistoryReads.Add(handle.Id);
        return this;
    }

    // Closed set of shaders this pass supports. Drawing with one not in
    // the list is rejected at draw time.
    public GraphicsPassBuilder Shader(params ShaderInterface[] shaders)
    {
        EnsureMutable();
        ArgumentNullException.ThrowIfNull(shaders);
        foreach (var s in shaders)
        {
            ArgumentNullException.ThrowIfNull(s);
            entry.Shaders.Add(s);
        }
        return this;
    }

    private void EnsureMutable()
    {
        if (graph.IsCompiled)
        {
            throw new InvalidOperationException(
                $"GraphicsPassBuilder for '{entry.Name}' was used after RenderGraph.Compile(). The topology is frozen.");
        }
    }
}

public sealed class ComputePassBuilder
{
    private readonly RenderGraph graph;
    private readonly ComputePassEntry entry;

    internal ComputePassBuilder(RenderGraph graph, ComputePassEntry entry)
    {
        this.graph = graph;
        this.entry = entry;
    }

    public PassHandle Handle => entry.Handle;
    public string Name => entry.Name;

    // Compute pass declares its reads (sampled images / readonly storage)
    // and writes (storage images). Barrier inference treats writes
    // analogously to color attachments in the GraphicsPass path.
    public ComputePassBuilder Read(TextureView view)
    {
        EnsureMutable();
        entry.Reads.Add(view);
        return this;
    }

    /// <summary>A read of what the PREVIOUS frame wrote; see GraphicsPassBuilder.ReadHistory.</summary>
    public ComputePassBuilder ReadHistory(GraphResourceHandle handle)
    {
        EnsureMutable();
        entry.Reads.Add((TextureView)handle);
        entry.HistoryReads.Add(handle.Id);
        return this;
    }

    public ComputePassBuilder Read(GraphResourceHandle handle) =>
        Read((TextureView)handle);

    public ComputePassBuilder Write(TextureView view)
    {
        EnsureMutable();
        entry.Writes.Add(view);
        return this;
    }

    public ComputePassBuilder Write(GraphResourceHandle handle) =>
        Write((TextureView)handle);

    public ComputePassBuilder Shader(ShaderInterface shader)
    {
        EnsureMutable();
        ArgumentNullException.ThrowIfNull(shader);
        entry.Shader = shader;
        return this;
    }

    private void EnsureMutable()
    {
        if (graph.IsCompiled)
        {
            throw new InvalidOperationException(
                $"ComputePassBuilder for '{entry.Name}' was used after RenderGraph.Compile(). The topology is frozen.");
        }
    }
}

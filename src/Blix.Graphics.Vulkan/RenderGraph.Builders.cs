namespace Blix.Graphics.Vulkan;

// Fluent builders for graph pass declarations. Builders are thin proxies
// over the graph's mutable pass entries — each fluent call mutates the
// underlying entry and returns `this` for chaining. Once Compile() runs,
// the graph freezes; builders held past Compile() throw if used.
//
// Compute-pass.Dispatch() throws NotImplementedException at execute time
// (the throw lands in VB.v when Execute is wired). Declaration is
// supported in v1 so the API + tests are stable; execution is the part
// deferred to step 8 (froxel fog).

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

    // Declare a color render-target attachment for this pass. View may
    // be a whole-image (implicit from GraphResourceHandle) or a sub-resource
    // view (e.g. cube.Face(i)). LoadOp / StoreOp follow Vulkan attachment
    // op semantics; see LoadOp / StoreOp enums in TextureView.cs.
    public GraphicsPassBuilder Target(TextureView view, LoadOp load, StoreOp store)
    {
        EnsureMutable();
        entry.ColorTargets.Add(new ColorAttachmentBinding(view, load, store));
        return this;
    }

    public GraphicsPassBuilder Target(GraphResourceHandle handle, LoadOp load, StoreOp store) =>
        Target((TextureView)handle, load, store);

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

    // Declare a Read edge — this pass samples / reads the view as a
    // shader input. Drives barrier inference in VB.iv. Compile() (VB.ii)
    // validates that the view's resource was declared by an earlier
    // pass in declaration order.
    public GraphicsPassBuilder Read(TextureView view)
    {
        EnsureMutable();
        entry.Reads.Add(view);
        return this;
    }

    public GraphicsPassBuilder Read(GraphResourceHandle handle) =>
        Read((TextureView)handle);

    // Declare the closed set of shader interfaces this pass supports.
    // Compile() validates set-1 layout compatibility across all listed
    // shaders (per Q-008 disposition). Drawing with a shader not listed
    // here is rejected at draw time.
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

namespace Blix.Graphics.Vulkan;

// Fluent builders for graph pass declarations. Thin proxies — each
// chained call mutates the underlying entry and returns this. After
// Compile() the graph freezes; builders held past that point throw.
//
// ComputePass.Dispatch() throws at execute time today — declaration is
// stable, execution will light up when there's a real consumer.

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

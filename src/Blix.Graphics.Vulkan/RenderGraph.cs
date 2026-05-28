namespace Blix.Graphics.Vulkan;

// Declarative render-pass topology baked once, then re-executed per frame.
// Compile() freezes the graph; Execute() captures per-pass scopes and
// dispatches them through the imperative command list.
//
// Pass state is stored as mutable class instances; fluent builders mutate
// them in place until Compile() flips IsCompiled.
public sealed partial class RenderGraph
{
    // Null only in the test ctor — validation runs without a device, but
    // backend compile/execute will reject null at the call site.
    internal VulkanGraphicsDevice? Device { get; }

    // Single id counter so resource and pass handles never collide.
    internal Dictionary<int, GraphResourceEntry> Resources { get; } = new();
    internal Dictionary<int, GraphicsPassEntry> GraphicsPasses { get; } = new();
    internal Dictionary<int, ComputePassEntry> ComputePasses { get; } = new();

    // Passes execute in declaration order. Read edges only validate
    // reachability — they do not reorder.
    internal List<int> PassOrder { get; } = new();

    private int nextId = 1;
    internal bool IsCompiled { get; private set; }

    // Populated at Compile time. Empty for graphics-only graphs (subpass
    // deps cover cross-pass memory + layout); compute reads/writes add
    // explicit vkCmdPipelineBarrier emissions.
    internal Dictionary<int, List<BarrierOp>> PerPassBarriers { get; private set; } =
        new Dictionary<int, List<BarrierOp>>();

    // Per-pass scopes captured by graph.Pass(); replayed in graph.Execute()
    // and cleared at the start of every Execute so any pass not re-declared
    // this frame simply doesn't render.
    private readonly Dictionary<int, (Action<Blix.Graphics.RenderPassBuilder> Scope, Blix.Graphics.GraphicsColor? ClearColor)> recordedScopes = new();

    public RenderGraph(VulkanGraphicsDevice device)
    {
        ArgumentNullException.ThrowIfNull(device);
        Device = device;
        Device.SwapchainRecreated += OnSwapchainRecreated;
    }

    // Test-only ctor — validation paths don't need a device.
    internal RenderGraph()
    {
        Device = null;
    }

    // --- Resource factories -----------------------------------------------

    // Persistent color render-target resource managed by the graph.
    public GraphResourceHandle ColorTarget(string name, TextureFormat format, GraphSize size)
    {
        EnsureNotCompiled(nameof(ColorTarget));
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(size);

        var id = nextId++;
        Resources[id] = new GraphResourceEntry(
            new GraphResourceHandle(id), name, GraphResourceKind.ColorTarget, format, size);
        return new GraphResourceHandle(id);
    }

    // Persistent depth render-target resource (2D).
    public GraphResourceHandle DepthTarget(string name, GraphSize size)
    {
        EnsureNotCompiled(nameof(DepthTarget));
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(size);

        var id = nextId++;
        Resources[id] = new GraphResourceEntry(
            new GraphResourceHandle(id), name, GraphResourceKind.DepthTarget, null, size);
        return new GraphResourceHandle(id);
    }

    // Depth cubemap. Six faces; faceSize is the edge length per face.
    // The returned DepthCubeHandle exposes .Face(int) → TextureView for
    // per-face render-target declarations (point-light shadow passes).
    public DepthCubeHandle DepthCube(string name, int faceSize)
    {
        EnsureNotCompiled(nameof(DepthCube));
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (faceSize <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(faceSize), faceSize, "DepthCube faceSize must be positive.");
        }

        var id = nextId++;
        Resources[id] = new GraphResourceEntry(
            new GraphResourceHandle(id), name, GraphResourceKind.DepthCube, null,
            new FixedGraphSize(faceSize, faceSize));
        return new DepthCubeHandle(id);
    }

    // --- Pass factories ---------------------------------------------------

    public GraphicsPassBuilder GraphicsPass(string name)
    {
        EnsureNotCompiled(nameof(GraphicsPass));
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var id = nextId++;
        var entry = new GraphicsPassEntry { Handle = new PassHandle(id), Name = name };
        GraphicsPasses[id] = entry;
        PassOrder.Add(id);
        return new GraphicsPassBuilder(this, entry);
    }

    public ComputePassBuilder ComputePass(string name)
    {
        EnsureNotCompiled(nameof(ComputePass));
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var id = nextId++;
        var entry = new ComputePassEntry { Handle = new PassHandle(id), Name = name };
        ComputePasses[id] = entry;
        PassOrder.Add(id);
        return new ComputePassBuilder(this, entry);
    }

    // --- Compile + Execute -------------------------------------------------

    // Validates → allocates backend resources → infers barriers → freezes.
    // Throws InvalidOperationException with a specific reason on failure;
    // IsCompiled stays false so the caller can fix the graph and retry.
    public void Compile()
    {
        EnsureNotCompiled(nameof(Compile));
        RenderGraphValidation.Validate(this);
        // Test-mode graphs (parameterless ctor) skip backend allocation.
        CompileBackend();
        PerPassBarriers = BarrierInference.Infer(this);
        IsCompiled = true;
    }

    // Synthetic RenderSurfaceHandle for a graph pass; pass it to
    // PipelineDescription.RenderTarget. Pipelines created against a graph
    // pass survive size-only resizes (render-pass compat is preserved).
    public RenderSurfaceHandle GetPassSurface(PassHandle pass)
    {
        if (!IsCompiled)
        {
            throw new InvalidOperationException(
                "RenderGraph.GetPassSurface called before Compile. Pass render passes don't exist until the graph has been compiled.");
        }
        if (!BackendPasses.TryGetValue(pass.Id, out var bp))
        {
            throw new InvalidOperationException(
                $"Pass handle id {pass.Id} not found in this graph.");
        }
        if (bp.SurfaceHandle.Id == 0)
        {
            throw new InvalidOperationException(
                $"Pass id {pass.Id} is a compute pass; compute passes don't have render surfaces.");
        }
        return bp.SurfaceHandle;
    }

    // Sampleable TextureHandle for a graph-managed color target. Available
    // only after Compile() — the underlying VkImage doesn't exist until then.
    public TextureHandle GetColorTexture(GraphResourceHandle resource)
    {
        if (!IsCompiled)
        {
            throw new InvalidOperationException(
                "RenderGraph.GetColorTexture called before Compile. Color attachments don't exist as sampleable textures until the graph has been compiled.");
        }
        if (!BackendResources.TryGetValue(resource.Id, out var br))
        {
            throw new InvalidOperationException(
                $"Graph resource id {resource.Id} has no allocated backend. Was it created via this graph's ColorTarget factory?");
        }
        if (Resources[resource.Id].Kind != GraphResourceKind.ColorTarget)
        {
            throw new InvalidOperationException(
                $"Graph resource id {resource.Id} is not a ColorTarget. Use GetDepthTexture for depth resources.");
        }
        if (br.SampleableHandle is not { } th)
        {
            throw new InvalidOperationException(
                $"Graph resource id {resource.Id} has no sampleable handle. Was the resource registered correctly?");
        }
        return th;
    }

    // Sampleable TextureHandle for a graph-managed depth target. Final
    // layout is ShaderReadOnlyOptimal so a downstream pass can read via
    // sampler2D. Default sampler is LinearClamp — consumers do manual
    // depth comparison in the shader (hardware PCF samplerShadow would
    // need an overload taking a SamplerDescription).
    public TextureHandle GetDepthTexture(GraphResourceHandle resource)
    {
        if (!IsCompiled)
        {
            throw new InvalidOperationException(
                "RenderGraph.GetDepthTexture called before Compile. Depth attachments don't exist as sampleable textures until the graph has been compiled.");
        }
        if (!BackendResources.TryGetValue(resource.Id, out var br))
        {
            throw new InvalidOperationException(
                $"Graph resource id {resource.Id} has no allocated backend. Was it created via this graph's DepthTarget factory?");
        }
        if (Resources[resource.Id].Kind != GraphResourceKind.DepthTarget)
        {
            throw new InvalidOperationException(
                $"Graph resource id {resource.Id} is not a DepthTarget. Use GetColorTexture for color resources or GetDepthCubeTexture for depth cubes.");
        }
        if (br.SampleableHandle is not { } th)
        {
            throw new InvalidOperationException(
                $"Graph resource id {resource.Id} has no sampleable handle. Was the resource registered correctly?");
        }
        return th;
    }

    // Sampleable samplerCube TextureHandle for a graph-managed depth cube
    // (point-light shadow: six face passes write linear distance, then a
    // later pass samples the cube by light→fragment direction).
    public TextureHandle GetDepthCubeTexture(DepthCubeHandle cube)
    {
        if (!IsCompiled)
        {
            throw new InvalidOperationException(
                "RenderGraph.GetDepthCubeTexture called before Compile.");
        }
        if (!BackendResources.TryGetValue(cube.Id, out var br))
        {
            throw new InvalidOperationException(
                $"Graph resource id {cube.Id} has no allocated backend. Was it created via this graph's DepthCube factory?");
        }
        if (Resources[cube.Id].Kind != GraphResourceKind.DepthCube)
        {
            throw new InvalidOperationException(
                $"Graph resource id {cube.Id} is not a DepthCube.");
        }
        if (br.SampleableHandle is not { } th)
        {
            throw new InvalidOperationException(
                $"Graph resource id {cube.Id} has no sampleable handle. Was the resource registered correctly?");
        }
        return th;
    }

    public void Pass(
        PassHandle handle,
        Action<Blix.Graphics.RenderPassBuilder> scope,
        Blix.Graphics.GraphicsColor? clearColor = null)
    {
        ArgumentNullException.ThrowIfNull(scope);
        if (!IsCompiled)
        {
            throw new InvalidOperationException(
                "RenderGraph.Pass called before Compile. Call graph.Compile() once at engine init, then graph.Pass(...) each frame.");
        }
        recordedScopes[handle.Id] = (scope, clearColor);
    }

    public void Execute(Blix.Graphics.RenderCommandList commandList)
    {
        ArgumentNullException.ThrowIfNull(commandList);
        if (!IsCompiled)
        {
            throw new InvalidOperationException(
                "RenderGraph.Execute called before Compile. Call graph.Compile() once at engine init.");
        }
        if (Device is null) return; // Test-mode graph; nothing to execute.

        foreach (var passId in PassOrder)
        {
            if (!recordedScopes.TryGetValue(passId, out var recorded)) continue;
            if (!BackendPasses.TryGetValue(passId, out var bpass)) continue;
            if (bpass.SurfaceHandle.Id == 0) continue; // compute passes — skip

            // ClearColors[]: Clear LoadOp → user override (first slot) or
            // black; Load/DontCare → null.
            if (!GraphicsPasses.TryGetValue(passId, out var gpass)) continue;
            var clearColors = new Blix.Graphics.GraphicsColor?[gpass.ColorTargets.Count];
            for (var i = 0; i < gpass.ColorTargets.Count; i++)
            {
                if (gpass.ColorTargets[i].Load == LoadOp.Clear)
                {
                    clearColors[i] = i == 0 && recorded.ClearColor is { } c
                        ? c
                        : new Blix.Graphics.GraphicsColor(0f, 0f, 0f, 1f);
                }
                else
                {
                    clearColors[i] = null;
                }
            }
            var clearDepth = gpass.Depth is { } d && d.Load == LoadOp.Clear;

            var desc = new Blix.Graphics.RenderPassDescription(
                Target: bpass.SurfaceHandle,
                ClearColors: clearColors,
                ClearDepth: clearDepth);
            commandList.Pass(gpass.Name, desc, recorded.Scope);
        }

        recordedScopes.Clear();
    }

    private void EnsureNotCompiled(string operation)
    {
        if (IsCompiled)
        {
            throw new InvalidOperationException(
                $"RenderGraph.{operation} called after Compile. The graph topology is frozen post-Compile; construct a new graph to change it.");
        }
    }
}

// --- Internal bookkeeping types ---------------------------------------

internal enum GraphResourceKind
{
    ColorTarget,
    DepthTarget,
    DepthCube,
}

internal sealed record GraphResourceEntry(
    GraphResourceHandle Handle,
    string Name,
    GraphResourceKind Kind,
    TextureFormat? Format,    // null for depth resources
    GraphSize Size);

internal sealed record ColorAttachmentBinding(TextureView View, LoadOp Load, StoreOp Store);
internal sealed record DepthAttachmentBinding(TextureView View, LoadOp Load, StoreOp Store);

// Mutable; builders fill these in over multiple fluent calls.
internal sealed class GraphicsPassEntry
{
    public PassHandle Handle;
    public string Name = string.Empty;
    public List<ColorAttachmentBinding> ColorTargets { get; } = new();
    public DepthAttachmentBinding? Depth { get; set; }
    public List<TextureView> Reads { get; } = new();
    public List<ShaderInterface> Shaders { get; } = new();
}

internal sealed class ComputePassEntry
{
    public PassHandle Handle;
    public string Name = string.Empty;
    public List<TextureView> Reads { get; } = new();
    public List<TextureView> Writes { get; } = new();
    public ShaderInterface? Shader { get; set; }
}

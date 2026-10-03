namespace Blix.Graphics;

// Declarative render-pass topology baked once, then re-executed per frame.
// Compile() freezes the graph; Execute() captures per-pass scopes and
// dispatches them through the imperative command list.
//
// Pass state is stored as mutable class instances; fluent builders mutate
// them in place until Compile() flips IsCompiled.
//
// <b>The graph is the engine's; what realises it is the device's.</b> Declaring
// targets and passes, validating them, and replaying each frame's scopes are the
// same for any device, so they live here and name no backend. Allocating the
// images, building render passes and framebuffers, and reallocating on resize is
// the backend's, which the device supplies through IRenderGraphDevice. It used
// to be one class split across files, so the graph's constructor took the Vulkan
// device and every program that declared a pass had to cast to get one.
public sealed partial class RenderGraph : IDisposable
{
    // Null only in the test ctor — validation runs without a device, and so
    // without a backend to allocate anything.
    internal IRenderGraphBackend? Backend { get; }

    // Single id counter so resource and pass handles never collide.
    internal Dictionary<int, GraphResourceEntry> Resources { get; } = new();
    internal Dictionary<int, GraphicsPassEntry> GraphicsPasses { get; } = new();
    internal Dictionary<int, ComputePassEntry> ComputePasses { get; } = new();

    // Passes execute in declaration order. Read edges only validate
    // reachability — they do not reorder.
    internal List<int> PassOrder { get; } = new();

    private int nextId = 1;
    internal bool IsCompiled { get; private set; }

    /// <summary>
    /// Changes after the graph has reallocated its <see cref="MatchSwapchainGraphSize"/>
    /// resources for a new swapchain. Applications that accumulate temporal data in
    /// graph-owned targets can cache this value and invalidate that history when it changes.
    /// </summary>
    public ulong MatchSwapchainResourceGeneration => Backend?.MatchSwapchainResourceGeneration ?? 0;

    // Per-pass scopes captured by graph.Pass(); replayed in graph.Execute()
    // and cleared at the start of every Execute so any pass not re-declared
    // this frame simply doesn't render.
    private readonly Dictionary<int, (Action<Blix.Graphics.RenderPassBuilder> Scope, Blix.Graphics.GraphicsColor? ClearColor)> recordedScopes = new();
    // Per-frame compute dispatches keyed by ComputePass handle. Executed in
    // PassOrder (declaration order) alongside graphics passes, so a compute
    // pass declared before a graphics pass that samples its output runs first.
    // A pass's dispatches run in the order they were recorded.
    private readonly Dictionary<int, List<Blix.Graphics.DispatchCommand>> recordedDispatches = new();

    public RenderGraph(IGraphicsDevice device)
    {
        ArgumentNullException.ThrowIfNull(device);
        Backend = device is IRenderGraphDevice realises
            ? realises.CreateRenderGraphBackend(this)
            : throw new NotSupportedException(
                $"A render graph needs a device that can realise it, and {device.Info.Renderer} cannot. " +
                "A program with no GPU (the headless host) declares no graph; give it a path that draws nothing.");
    }

    // Test-only ctor — validation paths don't need a device.
    internal RenderGraph()
    {
        Backend = null;
    }

    /// <summary>Frees what the backend allocated for this graph.</summary>
    public void Dispose() => Backend?.Dispose();

    // --- Resource factories -----------------------------------------------

    // Persistent color render-target resource managed by the graph. samples>1
    // makes it an MSAA attachment — render into it, then resolve into a 1×
    // target via GraphicsPassBuilder.ResolveColor. MSAA targets aren't
    // sampleable (GetColorTexture throws); sample the resolve target instead.
    public GraphResourceHandle ColorTarget(string name, TextureFormat format, GraphSize size, int samples = 1)
    {
        EnsureNotCompiled(nameof(ColorTarget));
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(size);

        var id = nextId++;
        Resources[id] = new GraphResourceEntry(
            new GraphResourceHandle(id), name, GraphResourceKind.ColorTarget, format, size, samples);
        return new GraphResourceHandle(id);
    }

    // Persistent depth render-target resource (2D). samples>1 = MSAA depth
    // (matches the pass's MSAA colour; depth isn't resolved).
    public GraphResourceHandle DepthTarget(string name, GraphSize size, int samples = 1)
    {
        EnsureNotCompiled(nameof(DepthTarget));
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(size);

        var id = nextId++;
        Resources[id] = new GraphResourceEntry(
            new GraphResourceHandle(id), name, GraphResourceKind.DepthTarget, null, size, samples);
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
        // Test-mode graphs (parameterless ctor) have no backend to allocate.
        Backend?.Compile();
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
        if (Backend is null || !Backend.TryGetPassSurface(pass.Id, out var surface))
        {
            throw new InvalidOperationException(
                $"Pass handle id {pass.Id} not found in this graph.");
        }
        if (surface.Id == 0)
        {
            throw new InvalidOperationException(
                $"Pass id {pass.Id} is a compute pass; compute passes don't have render surfaces.");
        }
        return surface;
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
        if (Backend is null || !Backend.TryGetSampleable(resource.Id, out var sampleable))
        {
            throw new InvalidOperationException(
                $"Graph resource id {resource.Id} has no allocated backend. Was it created via this graph's ColorTarget factory?");
        }
        if (Resources[resource.Id].Kind != GraphResourceKind.ColorTarget)
        {
            throw new InvalidOperationException(
                $"Graph resource id {resource.Id} is not a ColorTarget. Use GetDepthTexture for depth resources.");
        }
        if (sampleable is not { } th)
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
        if (Backend is null || !Backend.TryGetSampleable(resource.Id, out var sampleable))
        {
            throw new InvalidOperationException(
                $"Graph resource id {resource.Id} has no allocated backend. Was it created via this graph's DepthTarget factory?");
        }
        if (Resources[resource.Id].Kind != GraphResourceKind.DepthTarget)
        {
            throw new InvalidOperationException(
                $"Graph resource id {resource.Id} is not a DepthTarget. Use GetColorTexture for color resources or GetDepthCubeTexture for depth cubes.");
        }
        if (sampleable is not { } th)
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
        if (Backend is null || !Backend.TryGetSampleable(cube.Id, out var sampleable))
        {
            throw new InvalidOperationException(
                $"Graph resource id {cube.Id} has no allocated backend. Was it created via this graph's DepthCube factory?");
        }
        if (Resources[cube.Id].Kind != GraphResourceKind.DepthCube)
        {
            throw new InvalidOperationException(
                $"Graph resource id {cube.Id} is not a DepthCube.");
        }
        if (sampleable is not { } th)
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

    // Record a dispatch into a compute pass for this frame. The pipeline must be a
    // compute pipeline whose program matches the ComputePass's declared Shader
    // interface. Emitted at Execute time as a RenderCommandList.ComputePass in
    // declaration order. Dispatches recorded into one pass in a frame run in the
    // order recorded, each fenced against the last where they bind GPU buffers,
    // which is what lets one pass reset, count and then scatter.
    public void Dispatch(PassHandle handle, Blix.Graphics.DispatchCommand dispatch)
    {
        ArgumentNullException.ThrowIfNull(dispatch);
        if (!IsCompiled)
        {
            throw new InvalidOperationException(
                "RenderGraph.Dispatch called before Compile.");
        }
        if (!ComputePasses.ContainsKey(handle.Id))
        {
            throw new InvalidOperationException(
                $"RenderGraph.Dispatch targets pass {handle.Id}, which is not a ComputePass. Declare it with graph.ComputePass(...).");
        }
        if (!recordedDispatches.TryGetValue(handle.Id, out var list))
        {
            list = new List<Blix.Graphics.DispatchCommand>();
            recordedDispatches[handle.Id] = list;
        }
        list.Add(dispatch);
    }

    public void Execute(Blix.Graphics.RenderCommandList commandList)
    {
        ArgumentNullException.ThrowIfNull(commandList);
        if (!IsCompiled)
        {
            throw new InvalidOperationException(
                "RenderGraph.Execute called before Compile. Call graph.Compile() once at engine init.");
        }
        if (Backend is null) return; // Test-mode graph; nothing to execute.

        foreach (var passId in PassOrder)
        {
            // Compute pass: emit its recorded dispatches (if any) in order. The
            // Vulkan backend records it outside a render pass with the storage
            // barriers; a graphics pass declared after it samples the result.
            if (ComputePasses.TryGetValue(passId, out var cpass))
            {
                if (recordedDispatches.TryGetValue(passId, out var dispatches))
                {
                    commandList.ComputePass(cpass.Name, dispatches);
                }
                continue;
            }

            if (!recordedScopes.TryGetValue(passId, out var recorded)) continue;
            if (!Backend.TryGetPassSurface(passId, out var surface)) continue;
            if (surface.Id == 0) continue; // safety: non-graphics

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
                Target: surface,
                ClearColors: clearColors,
                ClearDepth: clearDepth);
            commandList.Pass(gpass.Name, desc, recorded.Scope);
        }

        recordedScopes.Clear();
        recordedDispatches.Clear();
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
    GraphSize Size,
    int Samples = 1);         // >1 = MSAA attachment (not sampleable; resolve into a 1× target)

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

    // <b>Reads that are satisfied by the PREVIOUS frame, not this one.</b> A read edge normally has
    // to follow the write that produces it, which is the check that catches a resource bound but
    // never declared. A temporal pass inverts that by definition: it samples what the last frame
    // left before this frame overwrites it, so the write it depends on is always "later" in
    // declaration order and always already done. Recorded separately so the barrier and the layout
    // transition still happen — the read is real — while the ordering check knows to skip it.
    public HashSet<int> HistoryReads { get; } = new();
    public List<ShaderInterface> Shaders { get; } = new();
    // Single-sample resolve destinations for MSAA color targets, parallel to
    // ColorTargets (resolve i ← color i). Empty when the pass isn't MSAA.
    public List<TextureView> ResolveTargets { get; } = new();

    /// <summary>
    /// A 1x depth target this pass resolves its multisampled depth into, or null.
    /// </summary>
    /// <remarks>
    /// <b>Separate from <see cref="ResolveTargets"/> because depth resolve is a different Vulkan
    /// feature, not another entry in the same list.</b> Colour resolve is a field on
    /// VkSubpassDescription; depth resolve is a structure chained onto VkSubpassDescription2, which
    /// means the pass has to be built with vkCreateRenderPass2. A pass that asks for this therefore
    /// takes a different construction path — see CreateGraphicsPassRenderPass.
    /// </remarks>
    public TextureView? DepthResolveTarget { get; set; }
}

internal sealed class ComputePassEntry
{
    public PassHandle Handle;
    public string Name = string.Empty;
    public List<TextureView> Reads { get; } = new();

    /// <summary>Cross-frame reads; see the note on GraphicsPassEntry.HistoryReads.</summary>
    public HashSet<int> HistoryReads { get; } = new();
    public List<TextureView> Writes { get; } = new();
    public ShaderInterface? Shader { get; set; }
}

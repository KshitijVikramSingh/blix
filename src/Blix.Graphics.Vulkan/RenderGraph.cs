namespace Blix.Graphics.Vulkan;

// Reduced-scope render graph (Vector B per docs/vector-b-plan.md).
// Declarative pass topology baked at engine init; per-frame Execute
// supplies parameters and triggers draws. Backend compiles to existing
// VkRenderPass + VkFramebuffer + descriptor-set machinery from Vector A
// and step 4.
//
// VB.i scope (this file): public type + factory shape. Compile() and
// Execute() are no-op stubs; their bodies land in VB.ii (validation),
// VB.iii (backend compile), VB.iv (barrier inference), VB.v (execute).
//
// Pass state is stored as mutable class instances inside the graph;
// fluent builders mutate them in place. Compile() freezes the topology;
// after that, declared pass and resource lists are immutable.
public sealed partial class RenderGraph
{
    // Backend access for VB.iii compile time (allocate VkImage etc.).
    // Nullable internally because validation (VB.ii) doesn't need the
    // device — only backend resource allocation (VB.iii+) does. Tests
    // construct with the internal parameterless ctor; production callers
    // must use the public one which enforces non-null.
    internal VulkanGraphicsDevice? Device { get; }

    // Resource + pass tables. Ids are allocated from a single counter so
    // resource and pass handles never collide.
    internal Dictionary<int, GraphResourceEntry> Resources { get; } = new();
    internal Dictionary<int, GraphicsPassEntry> GraphicsPasses { get; } = new();
    internal Dictionary<int, ComputePassEntry> ComputePasses { get; } = new();

    // Declaration-order preservation: passes execute in the order they
    // were declared (per D4 / D9 — declaration order = execution order;
    // Read edges validate only, not reorder).
    internal List<int> PassOrder { get; } = new();

    private int nextId = 1;
    internal bool IsCompiled { get; private set; }

    // Per-pass pre-pass barrier lists populated by VB.iv InferBarriers
    // at Compile time. Empty lists for v1 graphics-only graphs (subpass
    // deps cover cross-pass memory + layout). VB.v emits via
    // vkCmdPipelineBarrier before each pass's render-pass-begin.
    internal Dictionary<int, List<BarrierOp>> PerPassBarriers { get; private set; } =
        new Dictionary<int, List<BarrierOp>>();

    // VB.v.c — Per-pass user-supplied scopes captured by graph.Pass().
    // Replayed at graph.Execute() time via commandList.Pass(...). Cleared
    // at the start of each Execute so passes that aren't redeclared this
    // frame don't render (graphics-pass conditional skipping comes for
    // free).
    private readonly Dictionary<int, (Action<Blix.Graphics.RenderPassBuilder> Scope, Blix.Graphics.GraphicsColor? ClearColor)> recordedScopes = new();

    public RenderGraph(VulkanGraphicsDevice device)
    {
        ArgumentNullException.ThrowIfNull(device);
        Device = device;
        Device.SwapchainRecreated += OnSwapchainRecreated;
    }

    // Test-only constructor. Validation logic doesn't touch the device;
    // backend allocation (VB.iii) will reject a null device at the point
    // it actually needs one.
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

    // --- Compile + Execute (VB.i stubs) -----------------------------------

    // Runs pure-function validation (VB.ii — see
    // RenderGraph.Validate.cs) and then flips IsCompiled.
    // VB.iii will add backend resource allocation here; VB.iv builds the
    // barrier inference table; VB.vi hooks resize handling.
    //
    // On validation failure, IsCompiled stays false so the caller can fix
    // the graph and retry. The thrown InvalidOperationException carries a
    // named-entity reason ("Duplicate pass name 'foo'", etc.).
    public void Compile()
    {
        EnsureNotCompiled(nameof(Compile));
        RenderGraphValidation.Validate(this);
        // Backend phase: allocates VkImage / VkRenderPass / VkFramebuffer
        // per declared resource + pass. Test-mode graphs (constructed via
        // the internal parameterless ctor) skip this and stay validation-only.
        CompileBackend();
        // Barrier inference (VB.iv). Pure function on the pass list; for
        // v1 graphics-only graphs returns empty per-pass barrier lists
        // (subpass deps cover the cross-pass memory + layout barrier).
        // Real BarrierOp emission lands when ComputePass.Execute does in step 8.
        PerPassBarriers = BarrierInference.Infer(this);
        IsCompiled = true;
    }

    // VB.v.c — Capture a per-frame scope for the given pass. Replayed at
    // Execute() time. Each frame the user calls graph.Pass(handle, scope)
    // for the passes they want to draw; passes without a recorded scope
    // this frame skip rendering (graphics-pass-as-conditional fall-out).
    //
    // clearColor: optional override for the first color attachment when
    // its declared LoadOp is Clear. Subsequent color attachments + depth
    // use defaults (black + 1.0) for VB.v. Per-attachment clear values
    // come when ShaderLab port needs them.
    // Returns the synthetic RenderSurfaceHandle for a graph pass, suitable
    // for use as PipelineDescription.RenderTarget. Pipelines created
    // against a graph pass's render pass survive graph resize (render-pass
    // compat unchanged on size-only changes).
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

    // Returns the sampleable TextureHandle for a graph-managed color
    // target. Use this to pass the color attachment of pass A as an
    // input to pass B via ShaderTextureBinding. Available only after
    // Compile() — the underlying VkImage doesn't exist until then.
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

    // Returns the sampleable TextureHandle for a graph-managed depth
    // target. The shadow-map sampling path: a depth pass writes the
    // resource at ShaderReadOnlyOptimal finalLayout (handled by the
    // graph's render pass setup), and a subsequent pass reads it via
    // sampler2D / ShaderTextureBinding. Available only after Compile().
    //
    // The default sampler is LinearClamp (ClampToEdge). For hardware PCF
    // (samplerShadow with comparison mode) a future overload will accept
    // a SamplerDescription; current consumers do manual depth comparison
    // in the shader, which works with the linear sampler.
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

    // Returns the sampleable TextureHandle (samplerCube) for a graph-managed
    // depth cube. The point-light shadow path: 6 depth passes write each
    // cube face (linear distance from the light), then a later pass samples
    // the whole cube with the light→fragment direction. Available only after
    // Compile().
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

        // Iterate declared pass order. Each pass with a recorded scope
        // emits a commandList.Pass(...) call routed at its synthetic
        // RenderSurfaceHandle so the existing Execute path resolves the
        // graph's VkRenderPass + Framebuffer.
        foreach (var passId in PassOrder)
        {
            if (!recordedScopes.TryGetValue(passId, out var recorded)) continue;
            if (!BackendPasses.TryGetValue(passId, out var bpass)) continue;
            if (bpass.SurfaceHandle.Id == 0) continue; // compute passes — skip in v1

            // Compose ClearColors[] from declared LoadOp on each color
            // target. For Clear LoadOp, use user override (first slot) or
            // black default. For Load/DontCare, null entry.
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

        // Per-frame scopes don't persist across frames — passes that
        // aren't redeclared next frame fall out of execution.
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

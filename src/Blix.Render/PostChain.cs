using Blix.Graphics;
using Blix.Graphics.Vulkan;

namespace Blix.Render;

// One stage of a PostChain: a fullscreen image->image pass that reads the previous
// stage's output (stage 0 reads the chain's input) through the sampler named
// InputName, and writes a fresh target of the given Format/Size.
public sealed record PostStage(
    string Name,
    TextureFormat Format,
    GraphSize Size,
    ShaderInterface Interface,
    string InputName);

// A linear chain of fullscreen post-process passes over auto-managed intermediate
// targets -- the orchestration layer above FullscreenPass. It declares one color
// target + one graphics pass per stage into the caller's RenderGraph, wiring each
// stage's Read edge to the previous stage's target, and records the per-frame
// fullscreen draws. Bloom is three stages (bright -> blurH -> blurV); the same
// shape covers any sample-input -> fullscreen-pass -> write-output sequence.
//
// It owns ONLY the chain plumbing: intermediate targets, pass topology, and the
// draw recording. Like FullscreenPass/InstancedBatch, the CALLER brings the
// pipelines (and thus the shaders, via BuildPipelines) and the per-stage push
// bytes (via Record). And it deliberately STOPS at a texture: the chain produces
// Output, and compositing it back (exposure, intensity, tonemap) is the caller's
// own present pass -- which is what lets a scene opt out of the chain entirely and
// keep its own tonemap policy.
//
// Lifecycle is three calls, dictated by RenderGraph: passes/targets must be
// declared before Compile(), but render surfaces and sampleable textures only
// exist after it.
//   1. ctor                         -- declare targets + passes (before Compile)
//   2. BuildPipelines(factory)      -- after graph.Compile(): caller builds each
//                                       stage's pipeline against its surface
//   3. Record(pushForStage)         -- per frame: record every stage's draw
public sealed class PostChain : IDisposable
{
    private readonly RenderGraph graph;
    private readonly FullscreenPass fullscreen;
    private readonly GraphResourceHandle input;       // the chain's external input (stage 0 reads this)
    private readonly PostStage[] stages;
    private readonly GraphResourceHandle[] targets;   // one output target per stage
    private readonly PassHandle[] passes;
    private readonly PipelineHandle[] pipelines;
    private bool pipelinesBuilt;
    private bool disposed;

    public PostChain(
        VulkanGraphicsDevice device,
        RenderGraph graph,
        GraphResourceHandle input,
        IReadOnlyList<PostStage> stages,
        string? name = null)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(stages);
        if (stages.Count == 0) throw new ArgumentException("PostChain needs at least one stage.", nameof(stages));

        this.graph = graph;
        this.input = input;
        this.stages = stages.ToArray();
        fullscreen = new FullscreenPass(device, $"{name ?? "postchain"}.fullscreen");

        var n = this.stages.Length;
        targets = new GraphResourceHandle[n];
        passes = new PassHandle[n];
        pipelines = new PipelineHandle[n];

        var prev = input;
        for (var i = 0; i < n; i++)
        {
            var stage = this.stages[i];
            targets[i] = graph.ColorTarget(stage.Name, stage.Format, stage.Size);
            passes[i] = graph.GraphicsPass(stage.Name)
                .Target(targets[i], LoadOp.Clear, StoreOp.Store)
                .Read(prev)
                .Shader(stage.Interface)
                .Handle;
            prev = targets[i];
        }
    }

    // The last stage's color target. Caller samples it via graph.GetColorTexture
    // (after Compile) and composites it in its own present pass.
    public GraphResourceHandle Output => targets[^1];

    // After graph.Compile(): build each stage's pipeline. The factory is the
    // caller's -- it brings the shader program and blend/raster state, built
    // against the given surface. Caller-supplies-pipelines, same as FullscreenPass.
    public void BuildPipelines(Func<PostStage, RenderSurfaceHandle, PipelineHandle> build)
    {
        ArgumentNullException.ThrowIfNull(build);
        for (var i = 0; i < stages.Length; i++)
            pipelines[i] = build(stages[i], graph.GetPassSurface(passes[i]));
        pipelinesBuilt = true;
    }

    // Per frame: record every stage's fullscreen draw into the graph. The input
    // texture for each stage is resolved from the wiring; pushForStage(index, stage)
    // supplies that stage's push-constant bytes (null = none) -- e.g. the bright
    // stage's threshold, each blur's texel step.
    public void Record(Func<int, PostStage, byte[]?> pushForStage)
    {
        ArgumentNullException.ThrowIfNull(pushForStage);
        if (!pipelinesBuilt)
            throw new InvalidOperationException("PostChain.Record called before BuildPipelines.");

        for (var i = 0; i < stages.Length; i++)
        {
            var stage = stages[i];
            // Stage 0 reads the chain input; every later stage reads the previous target.
            var inputTex = graph.GetColorTexture(i == 0 ? input : targets[i - 1]);
            var binding = new ShaderTextureBinding(stage.InputName, inputTex, Slot: 0);
            var push = pushForStage(i, stage);
            var pipeline = pipelines[i];
            graph.Pass(passes[i], scope => fullscreen.Draw(scope, pipeline, new[] { binding }, push));
        }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        // The intermediate targets are graph-owned (freed with the graph); only the
        // internal fullscreen-triangle buffers are ours.
        fullscreen.Dispose();
    }
}

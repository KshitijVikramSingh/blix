using System.Numerics;
using System.Runtime.InteropServices;
using Blix.Diagnostics;
using Blix.Graphics;
using Blix.Graphics.Images;
using Blix.Graphics.Vulkan;
using Blix.Render;

namespace Blix.Tools.Studio;

/// <summary>
/// The Studio reference renderer: cascade casting, optional pre-pass, HDR lighting, inspection
/// viewport, and presentation.
/// </summary>
/// <remarks>
/// Every binding is reflected from build-generated SPIR-V sidecars and merged per program, so the
/// reference pipeline cannot drift from the shader interface it executes.
/// <para>
/// This is an optional reference composition, not a universal default renderer. Engine layers own
/// techniques and shared shading vocabulary; Studio owns which techniques are active and their authored
/// settings through <see cref="StudioLook"/>.
/// </para>
/// </remarks>
public sealed class StudioRenderer : IDisposable
{
    /// <summary>Square shadow map, matching the texel size the lit shader offsets by.</summary>
    public const int ShadowMapSize = 2048;

    /// <summary>Cascades in the sun's shadow. Fixed — see blix_sun_shadow_cascaded for why.</summary>
    public const int CascadeCount = 3;

    /// <summary>Bytes the lit pass pushes. <see cref="StudioPush.LitBytes"/> is the definition.</summary>
    /// <remarks>Forwarded for public compatibility; <see cref="StudioPush"/> is the sole size owner.</remarks>
    public const int LitPushBytes = StudioPush.LitBytes;

    /// <summary>Bytes the caster pushes: the model matrix, and what it takes to cut out.</summary>
    public const int CasterPushBytes = StudioPush.CasterBytes;

    /// <summary>Bytes the SKINNED caster pushes: a vec4 carrying the bone stride and the cutout.</summary>
    /// <remarks>
    /// Smaller than the unskinned caster's, not larger. That one pushes a mat4 because it has to place
    /// its object; a skinned instance's placement is already baked into its palette.
    /// </remarks>
    public const int SkinnedCasterPushBytes = StudioPush.SkinnedCasterBytes;

    private const int PushBytes = LitPushBytes;

    private VulkanGraphicsDevice device = null!;
    private FullscreenPass fullscreen = null!;

    private RenderGraph graph = null!;
    private GraphResourceHandle sceneColourTarget;
    private GraphResourceHandle sceneDepthTarget;
    private GraphResourceHandle viewportColourTarget;
    private GraphResourceHandle viewportDepthTarget;
    private PassHandle litPass;
    private PassHandle viewportPass;

    private ShaderProgramHandle litProgram;
    private ShaderProgramHandle shadowProgram;
    private ShaderProgramHandle presentProgram;
    private ShaderProgramHandle skinnedProgram;
    private ShaderProgramHandle skinnedShadowProgram;

    private PipelineHandle litPipeline;
    private PipelineHandle shadowPipeline;
    private PipelineHandle presentPipeline;
    private PipelineHandle skinnedPipeline;

    // The same program and layout as skinnedPipeline with the culling turned off, for a rig whose
    // material says doubleSided. Two pipelines rather than one, because face culling is pipeline
    // state in Vulkan and cannot be pushed per draw.
    private PipelineHandle skinnedDoubleSidedPipeline;

    // BLEND needs its own pipeline because blending is Vulkan pipeline state. It is depth-tested
    // but not depth-writing, so a transparent surface does not hide what is
    // behind it, and never culled, because a single-sided blend material shows its own far side
    // through itself.
    private PipelineHandle blendPipeline;
    private PipelineHandle skinnedBlendPipeline;
    private PipelineHandle skinnedShadowPipeline;

    private VertexBufferHandle cubeVertices;
    private IndexBufferHandle cubeIndices;
    private VertexBufferHandle groundVertices;
    private IndexBufferHandle groundIndices;
    private int cubeIndexCount;
    private int groundIndexCount;

    // Reusing one scratch buffer is safe because DrawIndexedCommand copies push data when recorded.
    // Every draw on the lit pipeline must bind every texture its shader declares. Studio's
    // own ground and boxes went through the same pipeline passing only the shadow map, so binding 1
    // was left unwritten and validation reported uAlbedo "used in draw but never updated" — which
    // reads like a model-loading bug and is not one. White is the identity for a base-colour factor.
    private readonly GraphResourceHandle[] cascadeTargets = new GraphResourceHandle[CascadeCount];
    private readonly PassHandle[] cascadePasses = new PassHandle[CascadeCount];
    private GraphResourceHandle sceneColourMsaaTarget;
    private GraphResourceHandle sceneDepthResolveTarget;

    /// <summary>The scene depth something can sample: the resolve target under MSAA, else the one.</summary>
    private GraphResourceHandle SampleableSceneDepth =>
        msaaSamplesUsed > 1 ? sceneDepthResolveTarget : sceneDepthTarget;

    private int msaaSamplesUsed = 1;
    private PassHandle prePass;
    private PipelineHandle prePassPipeline;
    private PipelineHandle prePassSkinnedPipeline;
    private readonly Matrix4x4[] cascadeViewProjection = new Matrix4x4[CascadeCount];
    private readonly float[] cascadeSplit = new float[CascadeCount];
    private readonly float[] cascadeSide = new float[CascadeCount];

    /// <summary>Each cascade's box width in metres, for reporting metres per texel.</summary>
    public IReadOnlyList<float> CascadeSideMetres => cascadeSide;

    /// <summary>Each cascade's far bound along the view, in metres.</summary>
    public IReadOnlyList<float> CascadeSplits => cascadeSplit;

    private TextureHandle whiteTexture;

    // ── the environment, baked once ──────────────────────────────────────────────────────────
    // STRUCTURAL, per StudioLook: baked from the sun as it stood when the graph was built, and
    // never rebaked. Moving --sun-azimuth afterwards moves the DIRECT light and leaves the
    // environment where it was, so the two can disagree — which is why the baked direction is
    // printed beside the live one rather than left to be discovered.
    private TextureHandle irradianceTexture;
    private TextureHandle prefilteredTexture;
    private TextureHandle brdfLutTexture;

    // EnvironmentBaker may alias the environment, irradiance, and prefiltered handles. Keep unique
    // ownership here so teardown destroys each underlying texture exactly once.
    private readonly List<TextureHandle> ownedEnvironmentTextures = new();

    private bool iblActive;
    private float envMipCeiling;
    private Vector3 bakedSunDirection;
    private double bakeMilliseconds;

    /// <summary>The sun the environment was baked from. Equal to the live one until one moves.</summary>
    public Vector3 BakedSunDirection => bakedSunDirection;

    /// <summary>Whether a real probe is bound, rather than the 1x1 stand-in.</summary>
    public bool ImageBasedLightingActive => iblActive;

    /// <summary>What the bake cost, in milliseconds. Printed by --debug.</summary>
    public double BakeMilliseconds => bakeMilliseconds;

    /// <summary>
    /// The stage's graph, for a tool recording a pass of its own.
    /// </summary>
    /// <remarks>
    /// Tools record their declared extension passes directly before Render. Execution follows graph
    /// declaration order, and scopes are cleared after each execution.
    /// </remarks>
    public RenderGraph Graph => graph;

    private readonly byte[] pushScratch = new byte[PushBytes];
    private readonly byte[] casterScratch = new byte[StudioPush.CasterBytes];
    private readonly byte[] casterPushScratch = new byte[CasterPushBytes];
    private readonly byte[] skinnedCasterPushScratch = new byte[SkinnedCasterPushBytes];

    // The caster only needs the model matrix, and the reflected interface says so — 64
    // bytes against the lit pass's 96. Pushing the larger block at it is rejected by the
    // device with the sizes named, which is the binding model earning its keep: a
    // hand-declared interface would have shrugged and corrupted the tail.

    // ── authored look ────────────────────────────────────────────────────────────────────────

    /// <summary>The house style this stage draws with. Owned here; a caller adjusts it in place.</summary>
    /// <remarks>Set structural members before <see cref="Load"/>; live members may change per frame.</remarks>
    public StudioLook Look { get; } = new();

    /// <param name="extend">
    /// Optional construction-time declaration of passes that append after Studio lighting and
    /// before presentation.
    /// </param>
    public void Load(VulkanGraphicsDevice vk, string shaderDirectory, Action<StudioGraph>? extend = null)
    {
        device = vk;
        fullscreen = new FullscreenPass(vk, "lab.present");

        ShaderInterface Reflect(params string[] stages) =>
            ShaderReflection.MergeStages(
                stages.Select(s => ShaderReflection.Load(
                    Path.Combine(shaderDirectory, s + ".spv.refl.json"))).ToArray());

        var shadowInterface = Reflect("studio_shadow.vert", "studio_shadow.frag");
        var litInterface = Reflect("studio_lit.vert", "studio_lit.frag");
        var presentInterface = Reflect("studio_present.vert", "studio_present.frag");

        // The skinned pair reuses the unskinned FRAGMENT stages, so these differ from the two above
        // by exactly one thing: a set-3 storage buffer the vertex stage reads. That is what makes
        // the bone palette's size a reflected fact rather than a constant restated in C# — the
        // hazard Blix.Test.Studio catches where Studio still has a hand-written number.
        var skinnedInterface = Reflect("studio_skinned.vert", "studio_lit.frag");
        var skinnedShadowInterface = Reflect("studio_skinned_shadow.vert", "studio_skinned_shadow.frag");

        // The graph owns colour, depth-only, resolve, and execution dependencies.
        graph = new RenderGraph(vk);
        var fullSize = new MatchSwapchainGraphSize(1.0f);
        // Three maps preserve contact-shadow resolution near the subject while retaining reach.
        for (var c = 0; c < CascadeCount; c++)
        {
            cascadeTargets[c] = graph.DepthTarget(
                $"lab-shadow-{c}", new FixedGraphSize(Look.ShadowMapSize, Look.ShadowMapSize));
        }
        sceneColourTarget = graph.ColorTarget("lab-hdr", TextureFormat.Rgba16F, fullSize);

        // MSAA changes target sample counts, not the scene-recording path. Colour and depth must
        // agree, and the request is clamped before native render-pass creation.
        var samples = Math.Clamp(Look.MsaaSamples, 1, vk.MaxMsaaSamples);
        if (samples != Look.MsaaSamples)
        {
            Console.WriteLine(
                $"msaa: {Look.MsaaSamples}x asked, {samples}x used — this device supports at most " +
                $"{vk.MaxMsaaSamples}x for colour and depth together.");
        }

        if (samples > 1)
        {
            sceneColourMsaaTarget = graph.ColorTarget(
                "lab-hdr-msaa", TextureFormat.Rgba16F, fullSize, samples: samples);
        }

        sceneDepthTarget = graph.DepthTarget("lab-scene-depth", fullSize, samples: samples);

        // Presentation samples scene depth for downstream gizmo depth testing, so MSAA needs a 1x
        // resolve target. At one sample the original depth target is already sampleable.
        if (samples > 1)
        {
            sceneDepthResolveTarget = graph.DepthTarget("lab-scene-depth-1x", fullSize);
        }

        // The panel is a true second camera with its own half-size colour and depth targets. Matching
        // scene formats keeps the render passes pipeline-compatible. It carries untonemapped HDR into
        // ImGui, so values over 1 may clip until the panel earns a dedicated presentation pass.
        var halfSize = new MatchSwapchainGraphSize(0.5f);
        viewportColourTarget = graph.ColorTarget("lab-viewport", TextureFormat.Rgba16F, halfSize);
        viewportDepthTarget = graph.DepthTarget("lab-viewport-depth", halfSize);

        // Each cascade redraws every caster. This is affordable for Studio's intended small subjects;
        // larger scenes should measure caster cost explicitly.
        for (var c = 0; c < CascadeCount; c++)
        {
            cascadePasses[c] = graph.GraphicsPass($"lab.shadow.{c}")
                .Depth(cascadeTargets[c], LoadOp.Clear, StoreOp.Store)
                .Shader(shadowInterface)
                .Handle;
        }

        // The optional pre-pass reuses caster shaders with the camera matrix and writes the depth
        // buffer later loaded by the lit pass.
        if (Look.DepthPrePass)
        {
            prePass = graph.GraphicsPass("lab.prepass")
                .Depth(sceneDepthTarget, LoadOp.Clear, StoreOp.Store)
                .Shader(shadowInterface)
                .Handle;
        }

        // Read() is the edge that makes the ordering a fact rather than a convention: the
        // lit pass samples what the caster pass wrote, and the graph knows it.
        var litBuilder = graph.GraphicsPass("lab.lit");
        litBuilder = samples > 1
            ? litBuilder.Target(sceneColourMsaaTarget, LoadOp.Clear, StoreOp.Store).ResolveColor(sceneColourTarget)
            : litBuilder.Target(sceneColourTarget, LoadOp.Clear, StoreOp.Store);
        msaaSamplesUsed = samples;
        if (samples > 1) litBuilder = litBuilder.ResolveDepth(sceneDepthResolveTarget);
        litPass = litBuilder
            // Loads what the pre-pass laid down, or clears it itself. The lit pipelines still WRITE
            // depth either way: with a pre-pass those writes are redundant rather than wrong, and
            // leaving them on is what lets the panel's viewport — which has no pre-pass of its own —
            // keep using the same pipelines instead of needing a twin family.
            .Depth(sceneDepthTarget, Look.DepthPrePass ? LoadOp.Load : LoadOp.Clear, StoreOp.Store)
            .Read(cascadeTargets[0])
            .Read(cascadeTargets[1])
            .Read(cascadeTargets[2])
            .Shader(litInterface)
            .Handle;

        // The viewport reuses the lit interface and pipelines. Per-draw uniform storage keeps its
        // camera independent, while compatible attachment formats let one pipeline serve both passes.
        viewportPass = graph.GraphicsPass("lab.viewport")
            .Target(viewportColourTarget, LoadOp.Clear, StoreOp.Store)
            .Depth(viewportDepthTarget, LoadOp.Clear, StoreOp.Store)
            .Read(cascadeTargets[0])
            .Read(cascadeTargets[1])
            .Read(cascadeTargets[2])
            .Shader(litInterface)
            .Handle;

        // The skinned pipelines draw INTO the same two passes rather than into passes of their own.
        // A rig and a box are the same lighting question with different vertex plumbing, and giving
        // the rig its own pass would mean a second clear, a second sort order, and two places to fix
        // the next time the sun moves.

        // Everything the stage owns exists; nothing is compiled. The one window a tool has, and a
        // delegate rather than a property because the window is invisible: after the stage has
        // declared its passes, before Compile freezes the shape. A one-shot call that hands you the
        // graph is scoping; it is not the stage running your code.
        extend?.Invoke(new StudioGraph(
            graph, sceneColourTarget, sceneDepthTarget, cascadeTargets[0], litInterface, shadowInterface));

        graph.Compile();

                byte[] Spv(string stage) => File.ReadAllBytes(Path.Combine(shaderDirectory, stage + ".spv"));

        shadowProgram = vk.CreateShaderProgramFromSpv(
            Spv("studio_shadow.vert"), Spv("studio_shadow.frag"), shadowInterface, "lab.shadow");
        litProgram = vk.CreateShaderProgramFromSpv(
            Spv("studio_lit.vert"), Spv("studio_lit.frag"), litInterface, "lab.lit");
        presentProgram = vk.CreateShaderProgramFromSpv(
            Spv("studio_present.vert"), Spv("studio_present.frag"), presentInterface, "lab.present");
        skinnedProgram = vk.CreateShaderProgramFromSpv(
            Spv("studio_skinned.vert"), Spv("studio_lit.frag"), skinnedInterface, "lab.skinned");
        skinnedShadowProgram = vk.CreateShaderProgramFromSpv(
            Spv("studio_skinned_shadow.vert"), Spv("studio_skinned_shadow.frag"), skinnedShadowInterface, "lab.skinned.shadow");


        shadowPipeline = vk.CreatePipeline(new PipelineDescription(
            shadowProgram,
            VertexPosition3NormalTexture2Color.Layout,
            PrimitiveTopology.Triangles,
            DepthState.LessEqualWrite,
            RasterizerState.NoCulling,
            Array.Empty<BlendState>(),
            RenderTarget: graph.GetPassSurface(cascadePasses[0])), "lab.shadow");

        if (Look.DepthPrePass)
        {
            // Its own pipelines rather than the caster's: those are baked against a 2048-square
            // depth-only surface and this one is the scene's. Two more, which is the cost of the
            // feature stated plainly — this stage is at ten pipelines now, and that number is the
            // thing to watch as the house style grows.
            prePassPipeline = vk.CreatePipeline(new PipelineDescription(
                shadowProgram,
                VertexPosition3NormalTexture2Color.Layout,
                PrimitiveTopology.Triangles,
                DepthState.LessEqualWrite,
                RasterizerState.NoCulling,
                Array.Empty<BlendState>(),
                RenderTarget: graph.GetPassSurface(prePass)), "lab.prepass");
        }

        litPipeline = vk.CreatePipeline(new PipelineDescription(
            litProgram,
            VertexPosition3NormalTexture2Color.Layout,
            PrimitiveTopology.Triangles,
            DepthState.LessEqualWrite,
            RasterizerState.NoCulling,
            new[] { BlendState.Disabled },
            RenderTarget: graph.GetPassSurface(litPass)), "lab.lit");

        // Closed, single-sided rig parts use back-face culling. This reduces fill and makes an
        // inside-out import visible as missing surfaces.
        skinnedPipeline = vk.CreatePipeline(new PipelineDescription(
            skinnedProgram,
            VertexPosition3NormalTextureSkin4Tangent.Layout,
            PrimitiveTopology.Triangles,
            DepthState.LessEqualWrite,
            RasterizerState.BackFaceCulling,
            new[] { BlendState.Disabled },
            RenderTarget: graph.GetPassSurface(litPass)), "lab.skinned");

        // Blended and double-sided materials use uncullled variants; closed single-sided parts use
        // the pipeline above. RigView selects the authored material case per part.
        blendPipeline = vk.CreatePipeline(new PipelineDescription(
            litProgram,
            VertexPosition3NormalTexture2Color.Layout,
            PrimitiveTopology.Triangles,
            DepthState.LessEqualNoWrite,
            RasterizerState.NoCulling,
            new[] { BlendState.AlphaBlend },
            RenderTarget: graph.GetPassSurface(litPass)), "lab.lit.blend");

        skinnedBlendPipeline = vk.CreatePipeline(new PipelineDescription(
            skinnedProgram,
            VertexPosition3NormalTextureSkin4Tangent.Layout,
            PrimitiveTopology.Triangles,
            DepthState.LessEqualNoWrite,
            RasterizerState.NoCulling,
            new[] { BlendState.AlphaBlend },
            RenderTarget: graph.GetPassSurface(litPass)), "lab.skinned.blend");

        skinnedDoubleSidedPipeline = vk.CreatePipeline(new PipelineDescription(
            skinnedProgram,
            VertexPosition3NormalTextureSkin4Tangent.Layout,
            PrimitiveTopology.Triangles,
            DepthState.LessEqualWrite,
            RasterizerState.NoCulling,
            new[] { BlendState.Disabled },
            RenderTarget: graph.GetPassSurface(litPass)), "lab.skinned.doublesided");

        // The caster does NOT cull: a one-sided shadow from a back-face-culled caster loses the far
        // side of a limb, and a character's own silhouette is mostly far sides.
        skinnedShadowPipeline = vk.CreatePipeline(new PipelineDescription(
            skinnedShadowProgram,
            VertexPosition3NormalTextureSkin4Tangent.Layout,
            PrimitiveTopology.Triangles,
            DepthState.LessEqualWrite,
            RasterizerState.NoCulling,
            Array.Empty<BlendState>(),
            RenderTarget: graph.GetPassSurface(cascadePasses[0])), "lab.skinned.shadow");

        if (Look.DepthPrePass)
        {
            prePassSkinnedPipeline = vk.CreatePipeline(new PipelineDescription(
                skinnedShadowProgram,
                VertexPosition3NormalTextureSkin4Tangent.Layout,
                PrimitiveTopology.Triangles,
                DepthState.LessEqualWrite,
                RasterizerState.NoCulling,
                Array.Empty<BlendState>(),
                RenderTarget: graph.GetPassSurface(prePass)), "lab.prepass.skinned");
        }

        // FullscreenPass.Layout, not a vertex format: studio_present.vert builds its triangle from
        // gl_VertexIndex and declares no inputs at all, so any attribute here is a promise the shader
        // does not keep — and the validation layers said so on every run.
        presentPipeline = vk.CreatePipeline(new PipelineDescription(
            presentProgram,
            FullscreenPass.Layout,
            PrimitiveTopology.Triangles,
            // Writes depth, always passes. The blit has nothing to depth-test against; it is
            // carrying the scene's depth onto the swapchain so that whatever draws next — the
            // runtime's debug pass — can.
            // LessEqual rather than Always, which the enum does not have: the swapchain depth is
            // cleared to 1.0 and every carried value is at most that, so the test never rejects.
            new DepthState(Enabled: true, WriteEnabled: true, DepthCompare.LessEqual),
            RasterizerState.NoCulling,
            BlendState.Disabled), "lab.present");

        whiteTexture = vk.CreateTexture2D(
            new TextureDescription(1, 1, TextureFormat.Rgba8Srgb, SamplerDescription.LinearRepeat),
            new byte[] { 255, 255, 255, 255 }, "lab.white");

        BakeEnvironment(vk);

        // From here a structural change cannot take effect, so say so rather than accept it quietly.
        Look.SealStructural();

        var (cv, ci) = StudioGeometry.Cube();
        // Widened to white. The stage's own furniture has no authored colour and does not want
        // one; it rides the same 36-byte layout so that ONE pipeline draws the ground, the boxes,
        // a model and an attachment — which is why this arc adds no pipeline variant at all.
        cubeVertices = vk.CreateVertexBuffer(
            VertexPosition3NormalTexture2Color.CreateBufferData(VertexPosition3NormalTexture2Color.From(cv)), "lab.cube.vb");
        cubeIndices = vk.CreateIndexBuffer(ci, name: "lab.cube.ib");
        cubeIndexCount = ci.Length;

        var (gv, gi) = StudioGeometry.Ground();
        groundVertices = vk.CreateVertexBuffer(
            VertexPosition3NormalTexture2Color.CreateBufferData(VertexPosition3NormalTexture2Color.From(gv)), "lab.ground.vb");
        groundIndices = vk.CreateIndexBuffer(gi, name: "lab.ground.ib");
        groundIndexCount = gi.Length;
    }

    /// <summary>The HDR colour the scene is lit into, before tonemapping. For anything that wants to sample it.</summary>
    public TextureHandle SceneColour => graph.GetColorTexture(sceneColourTarget);

    /// <summary>
    /// The surface the lit pass draws into, for a view that wants to draw alongside the scene.
    /// </summary>
    /// <remarks>
    /// Debug geometry aimed at this rather than at the swapchain lands IN the picture the capture tool
    /// reads back — which is the difference between a screenshot of a scene and a screenshot of what the
    /// engine thinks is in it.
    /// </remarks>
    public RenderSurfaceHandle SceneSurface => graph.GetPassSurface(litPass);

    /// <summary>The sun's depth buffer. Exposed so a tool can look at what the caster pass produced.</summary>
    /// <summary>The NEAREST cascade's depth, which is the one worth looking at in a panel.</summary>
    public TextureHandle ShadowDepth => graph.GetDepthTexture(cascadeTargets[0]);

    /// <summary>One cascade's depth, near to far.</summary>
    public TextureHandle CascadeDepth(int cascade) =>
        graph.GetDepthTexture(cascadeTargets[Math.Clamp(cascade, 0, CascadeCount - 1)]);

    /// <summary>The program the bone-palette material must be created against.</summary>
    /// <remarks>
    /// A <c>MaterialBindings</c> takes its descriptor layout from a program's reflected interface, so a
    /// rig cannot build its set-3 buffer until it knows which program will read it. Handing the program
    /// out is what keeps the buffer's size a fact from the shader rather than a constant agreed between
    /// two files that can drift apart.
    /// </remarks>
    public ShaderProgramHandle SkinnedProgram => skinnedProgram;

    /// <summary>The panel viewport's colour, already rendered. Register it with the host to show it.</summary>
    public TextureHandle ViewportColour => graph.GetColorTexture(viewportColourTarget);

    /// <summary>The surface the viewport draws into, so debug geometry can land in the panel's picture too.</summary>
    public RenderSurfaceHandle ViewportSurface => graph.GetPassSurface(viewportPass);

    /// <param name="rigInstances">
    /// How many instances of <paramref name="rig"/> to draw, and how many palettes the caller has
    /// already written into its bone buffer. Zero draws nothing; one is the ordinary case and takes
    /// the same path as eight.
    /// </param>
    public void Render(
        RenderCommandList commandList,
        Matrix4x4 viewProjection,
        Vector3 cameraPosition,
        IReadOnlyList<IStudioView>? views = null,
        Matrix4x4? viewportViewProjection = null,
        Vector3 viewportCameraPosition = default)
    {
        views ??= Array.Empty<IStudioView>();
        FitCascades();

        // Pass 1 — the sun's depth, ONCE PER CASCADE. Every caster is drawn three times; see the
        // pass declaration for why that cost is accepted here and where it stops being affordable.
        for (var c = 0; c < CascadeCount; c++)
        {
            var lightViewProjection = cascadeViewProjection[c];
            graph.Pass(cascadePasses[c], scope =>
            {
                var uniforms = new ShaderUniform[]
                {
                    new("uLightViewProjection", new Matrix4x4Uniform(lightViewProjection)),
                };

                // No furniture in the caster pass at all: the only furniture left is the ground, and
                // the ground casts nothing onto itself worth the fill.

                var draw = new StudioDraw(
                    scope, StudioPass.Shadow, uniforms, Array.Empty<ShaderTextureBinding>(),
                    shadowPipeline, skinnedShadowPipeline, whiteTexture,
                    // The caster pass never culls, so both are the same handle here.
                    SkinnedDoubleSidedPipeline: skinnedShadowPipeline);
                foreach (var view in views) view.Draw(draw);
            });
        }

        // Pass 1b — the camera's own depth, so the lit pass shades fewer fragments.
        if (Look.DepthPrePass)
        {
            graph.Pass(prePass, scope =>
            {
                var uniforms = new ShaderUniform[]
                {
                    // The camera where the light goes. That is the whole of the difference.
                    new("uLightViewProjection", new Matrix4x4Uniform(viewProjection)),
                };

                // The ground included, unlike the caster passes: it is the largest thing on screen
                // and therefore the one whose fragments are most worth not shading twice.
                if (Look.Ground)
                {
                    DrawGround(scope, prePassPipeline, uniforms, Array.Empty<ShaderTextureBinding>(), depthOnly: true);
                }

                var draw = new StudioDraw(
                    scope, StudioPass.Shadow, uniforms, Array.Empty<ShaderTextureBinding>(),
                    prePassPipeline, prePassSkinnedPipeline, whiteTexture,
                    SkinnedDoubleSidedPipeline: prePassSkinnedPipeline);
                foreach (var view in views) view.Draw(draw);
            });
        }

        // Pass 2 — light it into HDR, sampling the depth the caster passes just wrote.
        var shadowTexture = graph.GetDepthTexture(cascadeTargets[0]);
        graph.Pass(litPass, scope =>
        {
            var uniforms = new ShaderUniform[]
            {
                new("uViewProjection", new Matrix4x4Uniform(viewProjection)),
                new("uCascadeVP0", new Matrix4x4Uniform(cascadeViewProjection[0])),
                new("uCascadeVP1", new Matrix4x4Uniform(cascadeViewProjection[1])),
                new("uCascadeVP2", new Matrix4x4Uniform(cascadeViewProjection[2])),
                // World metres per shadow texel, per cascade.
                new("uCascadeTexels", new Vector4Uniform(new Vector4(
                    cascadeSide[0] / Look.ShadowMapSize,
                    cascadeSide[1] / Look.ShadowMapSize,
                    cascadeSide[2] / Look.ShadowMapSize,
                    Look.ShowCascades ? 1f : 0f))),
                new("uCameraPosition", new Vector4Uniform(new Vector4(cameraPosition, 1f))),
                new("uSunDirection", new Vector4Uniform(new Vector4(Look.SunDirection, 0f))),
                new("uSunColour", new Vector4Uniform(new Vector4(Look.SunColour, Look.AmbientStrength))),
                new("uEnvironment", new Vector4Uniform(new Vector4(envMipCeiling, iblActive ? 1f : 0f, 0f, 0f))),
            };
            var textures = EnvironmentTextures(shadowTexture);

            // FURNITURE, and it stays the stage's: a tool does not choose whether the stage has a
            // floor. That is part of what makes it a stage rather than a blank device.
            if (Look.Ground) DrawGround(scope, litPipeline, uniforms, textures);

            var draw = new StudioDraw(
                scope, StudioPass.Lit, uniforms, textures, litPipeline, skinnedPipeline, whiteTexture,
                SkinnedDoubleSidedPipeline: skinnedDoubleSidedPipeline,
                BlendPipeline: blendPipeline, SkinnedBlendPipeline: skinnedBlendPipeline);
            foreach (var view in views) view.Draw(draw);
        });

        // Pass 2b — the SAME scene from a second camera, into the panel's target. Same content,
        // same shadow map, same pipelines; only the view-projection differs. That is what makes it
        // a view and not a second renderer, and it is why every draw below is the same call the
        // lit pass makes rather than a parallel implementation that could drift from it.
        if (viewportViewProjection is { } panelViewProjection)
        {
            graph.Pass(viewportPass, scope =>
            {
                var uniforms = new ShaderUniform[]
                {
                    new("uViewProjection", new Matrix4x4Uniform(panelViewProjection)),
                    new("uCascadeVP0", new Matrix4x4Uniform(cascadeViewProjection[0])),
                new("uCascadeVP1", new Matrix4x4Uniform(cascadeViewProjection[1])),
                new("uCascadeVP2", new Matrix4x4Uniform(cascadeViewProjection[2])),
                // World metres per shadow texel, per cascade.
                new("uCascadeTexels", new Vector4Uniform(new Vector4(
                    cascadeSide[0] / Look.ShadowMapSize,
                    cascadeSide[1] / Look.ShadowMapSize,
                    cascadeSide[2] / Look.ShadowMapSize,
                    Look.ShowCascades ? 1f : 0f))),
                    new("uCameraPosition", new Vector4Uniform(new Vector4(viewportCameraPosition, 1f))),
                    new("uSunDirection", new Vector4Uniform(new Vector4(Look.SunDirection, 0f))),
                    new("uSunColour", new Vector4Uniform(new Vector4(Look.SunColour, Look.AmbientStrength))),
                    new("uEnvironment", new Vector4Uniform(new Vector4(envMipCeiling, iblActive ? 1f : 0f, 0f, 0f))),
                };
                var textures = EnvironmentTextures(shadowTexture);

                if (Look.Ground) DrawGround(scope, litPipeline, uniforms, textures);

                // Record the same views through the second camera; content has one draw description.
                var draw = new StudioDraw(
                    scope, StudioPass.Lit, uniforms, textures, litPipeline, skinnedPipeline, whiteTexture,
                    SkinnedDoubleSidedPipeline: skinnedDoubleSidedPipeline,
                    BlendPipeline: blendPipeline, SkinnedBlendPipeline: skinnedBlendPipeline);
                foreach (var view in views) view.Draw(draw);
            });
        }

        graph.Execute(commandList);

        // Pass 3 — exposure + tonemap onto the swapchain.
        var present = new ShaderUniform[]
        {
            new("uParams", new Vector4Uniform(new Vector4(Look.Exposure, Look.TonemapMode, 0f, 0f))),
        };
        commandList.Pass(
            "lab.present",
            new RenderPassDescription(
                Target: RenderSurfaceHandle.Default,
                ClearColors: new GraphicsColor?[] { new GraphicsColor(0, 0, 0, 1) },
                ClearDepth: true),
            pass => fullscreen.Draw(
                pass, presentPipeline,
                new[]
                {
                    new ShaderTextureBinding("uScene", graph.GetColorTexture(sceneColourTarget), Slot: 0),
                    new ShaderTextureBinding("uSceneDepth", graph.GetDepthTexture(SampleableSceneDepth), Slot: 1),
                },
                pushConstants: null,
                uniforms: present));
    }


    /// <summary>
    /// The floor: one lit quad, no shadow of its own, a fixed slate grey.
    /// </summary>
    /// <param name="depthOnly">
    /// Use the caster-sized push block and cutout binding required by the depth-only pipeline.
    /// </param>
    private void DrawGround(
        RenderPassBuilder pass,
        PipelineHandle pipeline,
        ShaderUniform[] uniforms,
        ShaderTextureBinding[] textures,
        bool depthOnly = false)
    {
        var push = depthOnly ? casterScratch : pushScratch;
        StudioPush.Matrix(Matrix4x4.Identity, push);
        if (depthOnly) StudioPush.CasterCutout(push, alphaCutoff: 0f, baseAlpha: 1f);
        else StudioPush.Material(push, GroundColour, metallic: 0f, roughness: 0.9f);

        pass.DrawIndexed(
            vertexBuffer: groundVertices,
            indexBuffer: groundIndices,
            pipeline: pipeline,
            indexCount: groundIndexCount,
            uniforms: uniforms,
            // Lit draws append ground albedo to the pass bindings. The caster declares albedo at
            // slot 0 as well so MASK geometry can discard consistently.
            textures: depthOnly
                ? new[] { new ShaderTextureBinding("uAlbedo", whiteTexture, Slot: 0) }
                : AppendAlbedo(textures, whiteTexture),
            pushConstants: push);
    }

    /// <summary>
    /// Bakes the house style's environment from its own sun — no asset, no HDR, no download.
    /// </summary>
    /// <remarks>
    /// The authored sun provides a self-contained procedural environment. With IBL disabled, 1x1
    /// identity textures still satisfy the reflected bindings while the shader uses flat ambient.
    /// </remarks>
    private void BakeEnvironment(VulkanGraphicsDevice vk)
    {
        bakedSunDirection = Look.SunDirection;

        if (!Look.ImageBasedLighting)
        {
            iblActive = false;
            envMipCeiling = 0f;
            var grey = new byte[6 * 4];
            for (var i = 0; i < grey.Length; i++) grey[i] = 128;
            irradianceTexture = vk.CreateTextureCube(
                1, TextureFormat.Rgba8, 1, grey, SamplerDescription.LinearClamp, "lab.ibl.off.cube");
            prefilteredTexture = irradianceTexture;
            brdfLutTexture = vk.CreateTexture2D(
                new TextureDescription(1, 1, TextureFormat.Rgba8, SamplerDescription.LinearClamp),
                new byte[] { 255, 255, 255, 255 }, "lab.ibl.off.brdf");
            Own(irradianceTexture, brdfLutTexture);
            return;
        }

        var watch = System.Diagnostics.Stopwatch.StartNew();
        var probe = EnvironmentBaker.Bake(
            vk,
            new EnvironmentProfile
            {
                Source = new ProceduralEnvironmentSource(Look.SunDirection),
                SpecularPrefilterBaseSize = Look.EnvFaceSize,
                SpecularPrefilterMipCount = Look.EnvMipCount,
            },
            "lab.ibl");

        // Cached beside the binary. The table is the same numbers on every run, and the Studio baseline
        // alone launches this tool twenty-three times.
        brdfLutTexture = EnvironmentBaker.BakeBrdfLut(
            vk, Look.BrdfLutSize, "lab.ibl.brdf",
            Path.Combine(AppContext.BaseDirectory, "brdf-cache"));
        watch.Stop();
        bakeMilliseconds = watch.Elapsed.TotalMilliseconds;

        irradianceTexture = probe.DiffuseIrradiance;
        prefilteredTexture = probe.PrefilteredSpecular;
        Own(probe.EnvCubemap, probe.DiffuseIrradiance, probe.PrefilteredSpecular, brdfLutTexture);

        // From the BAKE, not from the look: if the baker returns fewer mips than were asked for,
        // the ceiling the shader samples to has to be the one that exists. A LOD above the top mip
        // clamps silently and makes every rough surface read the same level.
        envMipCeiling = MathF.Max(0f, probe.PrefilteredSpecularMipCount - 1);
        iblActive = true;
    }

    /// <summary>
    /// Fits the three cascades as concentric boxes around the stage's content.
    /// </summary>
    /// <remarks>
    /// Studio is an origin-centred turntable, so concentric content boxes retain 1.8–4.1x more
    /// subject resolution than measured frustum slices from the default orbit. The split lambda
    /// still controls how strongly the radii concentrate near the subject.
    /// </remarks>
    private void FitCascades()
    {
        // The innermost box is sized to the SUBJECT — a model is normalised to about three units on
        // this stage — and the outermost to the ground the camera can orbit around.
        Span<float> radii = stackalloc float[CascadeCount];
        GraphicsMatrices.CascadeSplits(
            MathF.Max(1f, Look.ShadowSubjectRadius),
            MathF.Max(2f, Look.ShadowDistance * 0.5f),
            Look.CascadeSplitLambda,
            radii);

        for (var c = 0; c < CascadeCount; c++)
        {
            var side = radii[c] * 2f;
            cascadeSide[c] = side;
            cascadeSplit[c] = radii[c];

            // Snapped on the LIGHT's axes — the grid the texels are on. Snapping in world XZ, the
            // obvious version, snaps to a grid the texels are not aligned with unless the sun
            // happens to be axis-aligned, so the crawl it is meant to stop only partly stops.
            var toLight = Vector3.Normalize(Look.SunDirection);
            var up = MathF.Abs(toLight.Y) > 0.99f ? Vector3.UnitZ : Vector3.UnitY;
            var forward = -toLight;
            var axisRight = Vector3.Normalize(Vector3.Cross(up, forward));
            var axisUp = Vector3.Cross(forward, axisRight);
            var texel = side / Look.ShadowMapSize;
            var centre =
                axisRight * (MathF.Round(Vector3.Dot(Vector3.Zero, axisRight) / texel) * texel) +
                axisUp * (MathF.Round(Vector3.Dot(Vector3.Zero, axisUp) / texel) * texel);

            var away = radii[c] + 20f;
            cascadeViewProjection[c] = GraphicsMatrices.SunShadowViewProjection(
                Look.SunDirection, centre, away, side, 0.05f, away * 2f + side);
        }
    }

    private void Own(params TextureHandle[] textures)
    {
        foreach (var t in textures)
        {
            if (!ownedEnvironmentTextures.Contains(t)) ownedEnvironmentTextures.Add(t);
        }
    }

    private static ShaderTextureBinding[] AppendAlbedo(ShaderTextureBinding[] pass, TextureHandle albedo)
    {
        var all = new ShaderTextureBinding[pass.Length + 1];
        pass.CopyTo(all, 0);
        all[^1] = new ShaderTextureBinding("uAlbedo", albedo, Slot: 1);
        return all;
    }

    /// <summary>What every lit draw binds, before its own albedo. The stage's one answer.</summary>
    private ShaderTextureBinding[] EnvironmentTextures(TextureHandle _) => new[]
    {
        new ShaderTextureBinding("uCascade0", graph.GetDepthTexture(cascadeTargets[0]), Slot: 0),
        new ShaderTextureBinding("uCascade1", graph.GetDepthTexture(cascadeTargets[1]), Slot: 5),
        new ShaderTextureBinding("uCascade2", graph.GetDepthTexture(cascadeTargets[2]), Slot: 6),
        new ShaderTextureBinding("uIrradiance", irradianceTexture, Slot: 2),
        new ShaderTextureBinding("uPrefilteredEnv", prefilteredTexture, Slot: 3),
        new ShaderTextureBinding("uBrdfLut", brdfLutTexture, Slot: 4),
    };

    private static readonly Vector3 GroundColour = new(0.22f, 0.23f, 0.26f);



    /// <summary>
    /// Releases everything this renderer made.
    /// </summary>
    /// <remarks>The graph releases graph-owned resources; this type releases its pipelines, programs,
    /// buffers, and uniquely owned environment textures.</remarks>
    public void Dispose()
    {
        // The graph owns render passes, framebuffers, and offscreen images created below the device's
        // tracked resource tables, so it must release them explicitly.
        graph?.Dispose();
        fullscreen?.Dispose();
        if (device is null) return;

        device.DestroyPipeline(litPipeline);
        device.DestroyPipeline(shadowPipeline);
        device.DestroyPipeline(presentPipeline);
        device.DestroyPipeline(skinnedPipeline);
        device.DestroyPipeline(skinnedDoubleSidedPipeline);
        device.DestroyPipeline(blendPipeline);
        device.DestroyPipeline(skinnedBlendPipeline);
        device.DestroyPipeline(skinnedShadowPipeline);
        device.DestroyShaderProgram(litProgram);
        device.DestroyShaderProgram(shadowProgram);
        device.DestroyShaderProgram(presentProgram);
        device.DestroyShaderProgram(skinnedProgram);
        device.DestroyShaderProgram(skinnedShadowProgram);
        device.DestroyVertexBuffer(cubeVertices);
        device.DestroyIndexBuffer(cubeIndices);
        device.DestroyVertexBuffer(groundVertices);
        device.DestroyIndexBuffer(groundIndices);
        device.DestroyTexture(whiteTexture);
        foreach (var t in ownedEnvironmentTextures) device.DestroyTexture(t);
        ownedEnvironmentTextures.Clear();
    }
}

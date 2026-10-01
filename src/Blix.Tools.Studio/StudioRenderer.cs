using System.Numerics;
using System.Runtime.InteropServices;
using Blix.Diagnostics;
using Blix.Core;
using Blix.Graphics;
using Blix.Graphics.Images;
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
    /// <summary>The most bones a skin may have for the stage's skinned pipeline.</summary>
    public const int MaxBones = StudioRig.MaxBones;

    /// <summary>The most bodies one rig's draw can pose independently (the reference row).</summary>
    public const int MaxInstances = StudioRig.MaxInstances;

    // Every asset loaded through the stage, with what Studio keeps about it. Released with the stage.
    private readonly StudioAssets assets = new();

    /// <summary>Loads a model to draw skinned: the engine model, made in the stage's formats.</summary>
    /// <remarks>
    /// The stage applies its own policy to it (the vertex format its skinned pipeline reads, the bone and
    /// instance caps, its bone buffers, its fallback materials) and keeps that to itself; the model
    /// returned is the engine's, and what a tool reads. Owned by the stage: <see cref="Unload"/> releases
    /// it, and so does disposing the stage. Call after <see cref="Load"/>. Refused for a file with no skin.
    /// </remarks>
    public Model LoadRig(string path) => assets.Add(StudioRig.Load(device, path, skinnedProgram));

    /// <summary>Loads a model to draw static: its node hierarchy kept, a rigged file's meshes at bind pose.</summary>
    /// <remarks>Owned by the stage, as <see cref="LoadRig"/>. Call after <see cref="Load"/>.</remarks>
    public Model LoadModel(string path) => assets.Add(StudioModel.Load(device, path));

    /// <summary>Releases a model this stage loaded: its buffers, bone buffers and textures.</summary>
    public void Unload(Model model) => assets.Remove(model);

    /// <summary>Copies one skin's posed palettes into the buffer the stage's skinned pipeline reads this frame.</summary>
    public void UploadPalettes(Model rig, BonePaletteSet palettes, int skin = 0) => assets.RigFor(rig).UploadPalettes(palettes, skin);

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

    private IGraphicsDevice device = null!;
    private FullscreenPass fullscreen = null!;

    private RenderGraph graph = null!;
    private GraphResourceHandle sceneColourTarget;
    private GraphResourceHandle sceneDepthTarget;
    private GraphResourceHandle viewportColourTarget;
    private GraphResourceHandle viewportDepthTarget;
    private PassHandle litPass;
    private PassHandle viewportPass;

    // The viewport's own copies of the lit-family pipelines. A pipeline is compatible only with a
    // render pass identical to its own but for layouts and load ops, and the viewport's differs
    // from the lit pass in sample count (the lit pass may be MSAA) and in its subpass dependencies
    // (the lit pass also waits on a depth pre-pass and on compute). Sharing one set was written
    // when neither was true, and every panel draw failed validation once they were.
    private PipelineHandle viewportLitPipeline;
    private PipelineHandle viewportSkinnedPipeline;
    private PipelineHandle viewportBlendPipeline;
    private PipelineHandle viewportSkinnedBlendPipeline;
    private PipelineHandle viewportSkinnedDoubleSidedPipeline;

    private ShaderProgramHandle litProgram;
    private ShaderProgramHandle shadowProgram;
    private ShaderProgramHandle presentProgram;
    private ShaderProgramHandle skyProgram;
    private ShaderProgramHandle skinnedProgram;
    private ShaderProgramHandle skinnedShadowProgram;

    private PipelineHandle litPipeline;
    private PipelineHandle shadowPipeline;
    private PipelineHandle presentPipeline;
    private PipelineHandle skyPipeline;
    private PipelineHandle viewportSkyPipeline;
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
    // What the sky pass draws: the baked environment cube itself, or the flat grey cube when the
    // environment is off. Either way it is the thing the lit shader's ambient comes from.
    private TextureHandle skyTexture;

    // <b>The sky follows the sun and its own settings, once they have stopped moving.</b> The environment is baked from
    // the sun's direction, so a sun dragged on the panel used to move the light and the shadows and
    // leave the sky where it was baked. Rebaking on every tick of a drag would make dragging worse
    // (the RTS's map lab learned the same about slow operations), so the rebake waits until the sun
    // has held still for a moment.
    private const double SunSettleSeconds = 0.25;
    private readonly System.Diagnostics.Stopwatch sunStill = System.Diagnostics.Stopwatch.StartNew();
    private Vector3 sunSeen;
    private ProceduralSkyLook skySeen;
    private ProceduralSkyLook bakedSky;
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
    public void Load(IGraphicsDevice device, Action<StudioGraph>? extend = null)
    {
        // Its own shaders, staged beside the application by the reference, as SpriteBatch and the
        // runtime's ImGui find theirs. The caller used to be told to pass this path, which is how
        // every program standing on the stage came to write it out by hand.
        var shaderDirectory = AppFiles.Shaders;
        this.device = device;
        fullscreen = new FullscreenPass(device, "lab.present");

        ShaderInterface Reflect(params string[] stages) =>
            ShaderReflection.ForProgram(shaderDirectory, stages);

        var shadowInterface = Reflect("studio_shadow.vert", "studio_shadow.frag");
        var litInterface = Reflect("studio_lit.vert", "studio_lit.frag");
        var presentInterface = Reflect("studio_present.vert", "studio_present.frag");
        var skyInterface = Reflect("studio_sky.vert", "studio_sky.frag");

        // The skinned pair reuses the unskinned FRAGMENT stages, so these differ from the two above
        // by exactly one thing: a set-3 storage buffer the vertex stage reads. That is what makes
        // the bone palette's size a reflected fact rather than a constant restated in C# — the
        // hazard Blix.Test.Studio catches where Studio still has a hand-written number.
        var skinnedInterface = Reflect("studio_skinned.vert", "studio_lit.frag");
        var skinnedShadowInterface = Reflect("studio_skinned_shadow.vert", "studio_skinned_shadow.frag");

        // The graph owns colour, depth-only, resolve, and execution dependencies.
        graph = new RenderGraph(device);
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
        var samples = Math.Clamp(Look.MsaaSamples, 1, device.MaxMsaaSamples);
        if (samples != Look.MsaaSamples)
        {
            Console.WriteLine(
                $"msaa: {Look.MsaaSamples}x asked, {samples}x used — this device supports at most " +
                $"{device.MaxMsaaSamples}x for colour and depth together.");
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

        // The panel is a true second camera with its own half-size colour and depth targets. It
        // carries untonemapped HDR into
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
            // depth either way: with a pre-pass those writes are redundant rather than wrong, and it
            // keeps the lit family's description identical to the viewport's, which has no pre-pass.
            .Depth(sceneDepthTarget, Look.DepthPrePass ? LoadOp.Load : LoadOp.Clear, StoreOp.Store)
            .Read(cascadeTargets[0])
            .Read(cascadeTargets[1])
            .Read(cascadeTargets[2])
            .Shader(litInterface)
            .Handle;

        // The viewport reuses the lit interface, with pipelines of its own built against this pass.
        // Per-draw uniform storage keeps its camera independent.
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

        shadowProgram = device.CreateShaderProgramFromSpv(
            Spv("studio_shadow.vert"), Spv("studio_shadow.frag"), shadowInterface, "lab.shadow");
        litProgram = device.CreateShaderProgramFromSpv(
            Spv("studio_lit.vert"), Spv("studio_lit.frag"), litInterface, "lab.lit");
        presentProgram = device.CreateShaderProgramFromSpv(
            Spv("studio_present.vert"), Spv("studio_present.frag"), presentInterface, "lab.present");
        skyProgram = device.CreateShaderProgramFromSpv(
            Spv("studio_sky.vert"), Spv("studio_sky.frag"), skyInterface, "lab.sky");
        skinnedProgram = device.CreateShaderProgramFromSpv(
            Spv("studio_skinned.vert"), Spv("studio_lit.frag"), skinnedInterface, "lab.skinned");
        skinnedShadowProgram = device.CreateShaderProgramFromSpv(
            Spv("studio_skinned_shadow.vert"), Spv("studio_skinned_shadow.frag"), skinnedShadowInterface, "lab.skinned.shadow");


        shadowPipeline = device.CreatePipeline(new PipelineDescription(
            shadowProgram,
            VertexPosition3NormalTangentTexture2Color.Layout,
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
            prePassPipeline = device.CreatePipeline(new PipelineDescription(
                shadowProgram,
                VertexPosition3NormalTangentTexture2Color.Layout,
                PrimitiveTopology.Triangles,
                DepthState.LessEqualWrite,
                RasterizerState.NoCulling,
                Array.Empty<BlendState>(),
                RenderTarget: graph.GetPassSurface(prePass)), "lab.prepass");
        }

        litPipeline = device.CreatePipeline(new PipelineDescription(
            litProgram,
            VertexPosition3NormalTangentTexture2Color.Layout,
            PrimitiveTopology.Triangles,
            DepthState.LessEqualWrite,
            RasterizerState.NoCulling,
            new[] { BlendState.Disabled },
            RenderTarget: graph.GetPassSurface(litPass)), "lab.lit");

        // Depth-tested at the far plane and never written: drawn first, it fills what the pre-pass
        // left at 1.0 and nothing else, and it cannot occlude anything drawn after it.
        skyPipeline = device.CreatePipeline(new PipelineDescription(
            skyProgram, FullscreenPass.Layout, PrimitiveTopology.Triangles,
            new DepthState(Enabled: true, WriteEnabled: false, DepthCompare.LessEqual),
            RasterizerState.NoCulling, new[] { BlendState.Disabled },
            RenderTarget: graph.GetPassSurface(litPass)), "lab.sky");

        // Closed, single-sided rig parts use back-face culling. This reduces fill and makes an
        // inside-out import visible as missing surfaces.
        skinnedPipeline = device.CreatePipeline(new PipelineDescription(
            skinnedProgram,
            VertexPosition3NormalTextureSkin4Tangent.Layout,
            PrimitiveTopology.Triangles,
            DepthState.LessEqualWrite,
            RasterizerState.BackFaceCulling,
            new[] { BlendState.Disabled },
            RenderTarget: graph.GetPassSurface(litPass)), "lab.skinned");

        // Blended and double-sided materials use uncullled variants; closed single-sided parts use
        // the pipeline above. RigView selects the authored material case per part.
        blendPipeline = device.CreatePipeline(new PipelineDescription(
            litProgram,
            VertexPosition3NormalTangentTexture2Color.Layout,
            PrimitiveTopology.Triangles,
            DepthState.LessEqualNoWrite,
            RasterizerState.NoCulling,
            new[] { BlendState.AlphaBlend },
            RenderTarget: graph.GetPassSurface(litPass)), "lab.lit.blend");

        skinnedBlendPipeline = device.CreatePipeline(new PipelineDescription(
            skinnedProgram,
            VertexPosition3NormalTextureSkin4Tangent.Layout,
            PrimitiveTopology.Triangles,
            DepthState.LessEqualNoWrite,
            RasterizerState.NoCulling,
            new[] { BlendState.AlphaBlend },
            RenderTarget: graph.GetPassSurface(litPass)), "lab.skinned.blend");

        skinnedDoubleSidedPipeline = device.CreatePipeline(new PipelineDescription(
            skinnedProgram,
            VertexPosition3NormalTextureSkin4Tangent.Layout,
            PrimitiveTopology.Triangles,
            DepthState.LessEqualWrite,
            RasterizerState.NoCulling,
            new[] { BlendState.Disabled },
            RenderTarget: graph.GetPassSurface(litPass)), "lab.skinned.doublesided");

        var viewportSurface = graph.GetPassSurface(viewportPass);
        viewportLitPipeline = device.CreatePipeline(new PipelineDescription(
            litProgram, VertexPosition3NormalTangentTexture2Color.Layout, PrimitiveTopology.Triangles,
            DepthState.LessEqualWrite, RasterizerState.NoCulling, new[] { BlendState.Disabled },
            RenderTarget: viewportSurface), "lab.viewport.lit");
        viewportSkyPipeline = device.CreatePipeline(new PipelineDescription(
            skyProgram, FullscreenPass.Layout, PrimitiveTopology.Triangles,
            new DepthState(Enabled: true, WriteEnabled: false, DepthCompare.LessEqual),
            RasterizerState.NoCulling, new[] { BlendState.Disabled },
            RenderTarget: viewportSurface), "lab.viewport.sky");
        viewportSkinnedPipeline = device.CreatePipeline(new PipelineDescription(
            skinnedProgram, VertexPosition3NormalTextureSkin4Tangent.Layout, PrimitiveTopology.Triangles,
            DepthState.LessEqualWrite, RasterizerState.BackFaceCulling, new[] { BlendState.Disabled },
            RenderTarget: viewportSurface), "lab.viewport.skinned");
        viewportBlendPipeline = device.CreatePipeline(new PipelineDescription(
            litProgram, VertexPosition3NormalTangentTexture2Color.Layout, PrimitiveTopology.Triangles,
            DepthState.LessEqualNoWrite, RasterizerState.NoCulling, new[] { BlendState.AlphaBlend },
            RenderTarget: viewportSurface), "lab.viewport.lit.blend");
        viewportSkinnedBlendPipeline = device.CreatePipeline(new PipelineDescription(
            skinnedProgram, VertexPosition3NormalTextureSkin4Tangent.Layout, PrimitiveTopology.Triangles,
            DepthState.LessEqualNoWrite, RasterizerState.NoCulling, new[] { BlendState.AlphaBlend },
            RenderTarget: viewportSurface), "lab.viewport.skinned.blend");
        viewportSkinnedDoubleSidedPipeline = device.CreatePipeline(new PipelineDescription(
            skinnedProgram, VertexPosition3NormalTextureSkin4Tangent.Layout, PrimitiveTopology.Triangles,
            DepthState.LessEqualWrite, RasterizerState.NoCulling, new[] { BlendState.Disabled },
            RenderTarget: viewportSurface), "lab.viewport.skinned.doublesided");

        // The caster does NOT cull: a one-sided shadow from a back-face-culled caster loses the far
        // side of a limb, and a character's own silhouette is mostly far sides.
        skinnedShadowPipeline = device.CreatePipeline(new PipelineDescription(
            skinnedShadowProgram,
            VertexPosition3NormalTextureSkin4Tangent.Layout,
            PrimitiveTopology.Triangles,
            DepthState.LessEqualWrite,
            RasterizerState.NoCulling,
            Array.Empty<BlendState>(),
            RenderTarget: graph.GetPassSurface(cascadePasses[0])), "lab.skinned.shadow");

        if (Look.DepthPrePass)
        {
            prePassSkinnedPipeline = device.CreatePipeline(new PipelineDescription(
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
        presentPipeline = device.CreatePipeline(new PipelineDescription(
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

        whiteTexture = device.CreateTexture2D(
            new TextureDescription(1, 1, TextureFormat.Rgba8Srgb, SamplerDescription.LinearRepeat),
            new byte[] { 255, 255, 255, 255 }, "lab.white");

        BakeEnvironment(device);

        // From here a structural change cannot take effect, so say so rather than accept it quietly.
        Look.SealStructural();

        var (cv, ci) = StudioGeometry.Cube();
        // The stage's own furniture rides the complete static vertex a cooked model arrives in, so ONE
        // pipeline draws the ground, the boxes, a model and an attachment.
        cubeVertices = device.CreateVertexBuffer(VertexPosition3NormalTangentTexture2Color.CreateBufferData(cv), "lab.cube.vb");
        cubeIndices = device.CreateIndexBuffer(ci, name: "lab.cube.ib");
        cubeIndexCount = ci.Length;

        // As far as the camera can see, so its edge is a horizon against the sky rather than a
        // corner in the middle of the frame. The camera's far plane is 120 m.
        var (gv, gi) = StudioGeometry.Ground(extent: 120f);
        groundVertices = device.CreateVertexBuffer(VertexPosition3NormalTangentTexture2Color.CreateBufferData(gv), "lab.ground.vb");
        groundIndices = device.CreateIndexBuffer(gi, name: "lab.ground.ib");
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
    /// A <c>IMaterialBindings</c> takes its descriptor layout from a program's reflected interface, so a
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
        FollowEnvironment();
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
                    SkinnedDoubleSidedPipeline: skinnedShadowPipeline) { Assets = assets };
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
                    SkinnedDoubleSidedPipeline: prePassSkinnedPipeline) { Assets = assets };
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

            // First, because blended surfaces write no depth: a sky drawn after them would paint over
            // glass. With the pre-pass, geometry depth is already here and the sky only fills the rest.
            DrawSky(scope, skyPipeline, viewProjection, cameraPosition);

            // FURNITURE, and it stays the stage's: a tool does not choose whether the stage has a
            // floor. That is part of what makes it a stage rather than a blank device.
            if (Look.Ground) DrawGround(scope, litPipeline, uniforms, textures);

            var draw = new StudioDraw(
                scope, StudioPass.Lit, uniforms, textures, litPipeline, skinnedPipeline, whiteTexture,
                SkinnedDoubleSidedPipeline: skinnedDoubleSidedPipeline,
                BlendPipeline: blendPipeline, SkinnedBlendPipeline: skinnedBlendPipeline) { Assets = assets };
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

                DrawSky(scope, viewportSkyPipeline, panelViewProjection, viewportCameraPosition);
                if (Look.Ground) DrawGround(scope, viewportLitPipeline, uniforms, textures);

                // Record the same views through the second camera; content has one draw description,
                // with the pipelines built for this pass.
                var draw = new StudioDraw(
                    scope, StudioPass.Lit, uniforms, textures, viewportLitPipeline, viewportSkinnedPipeline, whiteTexture,
                    SkinnedDoubleSidedPipeline: viewportSkinnedDoubleSidedPipeline,
                    BlendPipeline: viewportBlendPipeline, SkinnedBlendPipeline: viewportSkinnedBlendPipeline) { Assets = assets };
                foreach (var view in views) view.Draw(draw);
            });
        }

        graph.Execute(commandList);

        // Pass 3 — exposure + tonemap onto the swapchain.
        var present = new ShaderUniform[]
        {
            new("uParams", new Vector4Uniform(new Vector4(Look.Exposure, (float)Look.TonemapMode, 0f, 0f))),
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
                    new ShaderTextureBinding("uScene", graph.GetColorTexture(sceneColourTarget)),
                    new ShaderTextureBinding("uSceneDepth", graph.GetDepthTexture(SampleableSceneDepth)),
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
                ? new[] { new ShaderTextureBinding("uAlbedo", whiteTexture) }
                : StudioDraw.Append(textures, whiteTexture, whiteTexture, whiteTexture, whiteTexture, whiteTexture),
            pushConstants: push);
    }

    /// <summary>
    /// Bakes the house style's environment from its own sun — no asset, no HDR, no download.
    /// </summary>
    /// <remarks>
    /// The authored sun provides a self-contained procedural environment. With IBL disabled, 1x1
    /// identity textures still satisfy the reflected bindings while the shader uses flat ambient.
    /// </remarks>
    private void BakeEnvironment(IGraphicsDevice device)
    {
        bakedSunDirection = Look.SunDirection;
        bakedSky = Look.Sky;

        if (!Look.ImageBasedLighting)
        {
            iblActive = false;
            envMipCeiling = 0f;
            var grey = new byte[6 * 4];
            for (var i = 0; i < grey.Length; i++) grey[i] = 128;
            irradianceTexture = device.CreateTextureCube(
                1, TextureFormat.Rgba8, 1, grey, SamplerDescription.LinearClamp, "lab.ibl.off.cube");
            prefilteredTexture = irradianceTexture;
            skyTexture = irradianceTexture;
            brdfLutTexture = device.CreateTexture2D(
                new TextureDescription(1, 1, TextureFormat.Rgba8, SamplerDescription.LinearClamp),
                new byte[] { 255, 255, 255, 255 }, "lab.ibl.off.brdf");
            Own(irradianceTexture, brdfLutTexture);
            return;
        }

        var watch = System.Diagnostics.Stopwatch.StartNew();
        var probe = EnvironmentBaker.Bake(
            device,
            new EnvironmentProfile
            {
                // NEGATED, because the two sides disagree about which way a sun direction points: the
                // stage's is TOWARD the sun (the lit shader's L), the source's is FROM the sun into the
                // scene. Passed as it was, the baker drew the sun below the horizon and, reading a high
                // sun as a set one, warmed the whole horizon with its dusk tint: the "yellow-white" sky.
                Source = new ProceduralEnvironmentSource(-Look.SunDirection, Look.Sky),
                SpecularPrefilterBaseSize = Look.EnvFaceSize,
                SpecularPrefilterMipCount = Look.EnvMipCount,
            },
            "lab.ibl");

        // Cached beside the binary. The table is the same numbers on every run, and the Studio baseline
        // alone launches this tool twenty-three times.
        brdfLutTexture = EnvironmentBaker.BakeBrdfLut(
            device, Look.BrdfLutSize, "lab.ibl.brdf",
            Path.Combine(AppContext.BaseDirectory, "brdf-cache"));
        watch.Stop();
        bakeMilliseconds = watch.Elapsed.TotalMilliseconds;

        irradianceTexture = probe.DiffuseIrradiance;
        prefilteredTexture = probe.PrefilteredSpecular;
        skyTexture = probe.EnvCubemap;
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

    /// <summary>The environment behind everything, through one camera.</summary>
    private void DrawSky(RenderPassBuilder pass, PipelineHandle pipeline, Matrix4x4 viewProjection, Vector3 cameraPosition)
    {
        if (!Matrix4x4.Invert(viewProjection, out var inverse)) return;
        fullscreen.Draw(
            pass, pipeline,
            new[] { new ShaderTextureBinding("uSky", skyTexture) },
            uniforms: new ShaderUniform[]
            {
                new("uInverseViewProjection", new Matrix4x4Uniform(inverse)),
                new("uCameraPosition", new Vector4Uniform(new Vector4(cameraPosition, 1f))),
            });
    }

    /// <summary>Rebakes the environment once the sun and the sky settings have held still somewhere they were not baked at.</summary>
    /// <remarks>
    /// Waits for the GPU first: the old textures are bound by frames that may still be in flight, and
    /// the device frees a texture the moment it is asked to. It happens once per settled change, so
    /// the wait is not paid while dragging.
    /// </remarks>
    private void FollowEnvironment()
    {
        if (!Look.ImageBasedLighting) return;
        var sun = Look.SunDirection;
        var sky = Look.Sky;
        if (sun != sunSeen || sky != skySeen)
        {
            sunSeen = sun;
            skySeen = sky;
            sunStill.Restart();
            return;
        }

        if ((sun == bakedSunDirection && sky == bakedSky) || sunStill.Elapsed.TotalSeconds < SunSettleSeconds) return;

        device.WaitIdle();
        foreach (var t in ownedEnvironmentTextures) device.DestroyTexture(t);
        ownedEnvironmentTextures.Clear();
        BakeEnvironment(device);
    }

    private void Own(params TextureHandle[] textures)
    {
        foreach (var t in textures)
        {
            if (!ownedEnvironmentTextures.Contains(t)) ownedEnvironmentTextures.Add(t);
        }
    }

    /// <summary>What every lit draw binds, before its own material. The stage's one answer.</summary>
    private ShaderTextureBinding[] EnvironmentTextures(TextureHandle _) => new[]
    {
        new ShaderTextureBinding("uCascade0", graph.GetDepthTexture(cascadeTargets[0])),
        new ShaderTextureBinding("uCascade1", graph.GetDepthTexture(cascadeTargets[1])),
        new ShaderTextureBinding("uCascade2", graph.GetDepthTexture(cascadeTargets[2])),
        new ShaderTextureBinding("uIrradiance", irradianceTexture),
        new ShaderTextureBinding("uPrefilteredEnv", prefilteredTexture),
        new ShaderTextureBinding("uBrdfLut", brdfLutTexture),
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
        assets.Dispose();
        if (device is null) return;

        device.DestroyPipeline(litPipeline);
        device.DestroyPipeline(shadowPipeline);
        device.DestroyPipeline(presentPipeline);
        device.DestroyPipeline(skinnedPipeline);
        device.DestroyPipeline(skinnedDoubleSidedPipeline);
        device.DestroyPipeline(blendPipeline);
        device.DestroyPipeline(skinnedBlendPipeline);
        device.DestroyPipeline(skinnedShadowPipeline);
        device.DestroyPipeline(viewportLitPipeline);
        device.DestroyPipeline(viewportSkinnedPipeline);
        device.DestroyPipeline(viewportBlendPipeline);
        device.DestroyPipeline(viewportSkinnedBlendPipeline);
        device.DestroyPipeline(viewportSkinnedDoubleSidedPipeline);
        device.DestroyShaderProgram(litProgram);
        device.DestroyShaderProgram(shadowProgram);
        device.DestroyShaderProgram(presentProgram);
        device.DestroyPipeline(skyPipeline);
        device.DestroyPipeline(viewportSkyPipeline);
        device.DestroyShaderProgram(skyProgram);
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

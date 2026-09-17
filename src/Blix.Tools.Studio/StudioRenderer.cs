using System.Numerics;
using System.Runtime.InteropServices;
using Blix.Diagnostics;
using Blix.Graphics;
using Blix.Graphics.Images;
using Blix.Graphics.Vulkan;
using Blix.Render;

namespace Blix.Tools.Studio;

/// <summary>
/// The lab's three passes: cast, light, present.
/// </summary>
/// <remarks>
/// <b>Reflected, not hand-declared.</b> Every binding below is read out of the compiled SPIR-V at build
/// time (spirv-cross sidecars next to each <c>.spv</c>) and merged per program. The older demos declare a
/// <see cref="ShaderInterface"/> by hand, which is fine at their size and does not scale: the interface
/// has to be restated every time a shader gains a binding, and nothing checks the restatement against the
/// shader it claims to describe. A lab meant to grow starts on the path that cannot drift.
/// <para>
/// <b>Not here YET:</b> cascades, texel snapping, bloom, IBL, MSAA, a depth pre-pass. This used to say
/// "deliberately not here", on the grounds that a lab which grew them would be claiming to be a renderer
/// — and that was the right call while this stage was only a lab. It is now also where Blix's house style
/// lives (see <see cref="StudioLook"/>), which is a different job: the reference look is the answer to
/// "what does Blix think this asset should look like", and a reference that is out-rendered by every demo
/// answers it badly. So these arrive here rather than being kept out, one at a time, each earning its
/// place in <see cref="StudioLook"/> as a declared value rather than a constant.
/// <para>
/// What has NOT changed is where the technique lives. A capability belongs in the engine and its shading
/// vocabulary in <c>Blix.Shaders</c>; what this stage owns is the COMPOSITION — which of them are on, and
/// at what settings. That is the same rule that sent the NdotV fix into the shared <c>pbr.glsl</c> rather
/// than into this file.
/// </para>
/// </para>
/// </remarks>
public sealed class StudioRenderer : IDisposable
{
    /// <summary>Square shadow map, matching the texel size the lit shader offsets by.</summary>
    public const int ShadowMapSize = 2048;

    /// <summary>Cascades in the sun's shadow. Fixed — see blix_sun_shadow_cascaded for why.</summary>
    public const int CascadeCount = 3;

    /// <summary>Bytes the lit pass pushes. <see cref="StudioPush.LitBytes"/> is the definition.</summary>
    /// <remarks>
    /// <b>These were separate numbers, and they drifted.</b> This file declared its own 96, 64 and 16
    /// beside <see cref="StudioPush"/>'s, with a comment calling the lit one "the one number in this
    /// file that can silently disagree with the SPIR-V". It disagreed with <see cref="StudioPush"/>
    /// instead, the moment a second UV set widened the block there and not here: every draw from this
    /// file pushed 96 bytes into a pipeline declaring 112.
    ///
    /// Forwarding rather than deleting, because the names are public and a tool checks them against
    /// what the shader declares — which is exactly the check that should keep working.
    /// </remarks>
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

    // <b>Blending is pipeline state, which is the whole reason BLEND costs a pipeline and MASK does
    // not.</b> Depth-tested but not depth-WRITING, so a transparent surface does not hide what is
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

    // <b>One scratch buffer, reused across draws — which is safe now and was not.</b>
    // This started as a pool of one array per recorded draw, because a recorded command
    // held the caller's array by reference and read it at Execute: seven objects rendered
    // at the seventh's transform, six apparently missing, draw counts perfectly healthy.
    //
    // The workaround is gone because the API stopped needing it. DrawIndexedCommand copies
    // its push payload at record time, so a renderer may pack into one buffer per draw
    // exactly as the obvious code does. Keeping the pool would have left a local remedy
    // standing in for an engine contract, and the next renderer would have had to
    // rediscover it.
    // <b>Every draw on the lit pipeline must bind every texture its shader declares.</b> The lab's
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

    // <b>What this renderer must destroy, deduplicated — because the probe ALIASES.</b>
    // EnvironmentBaker's procedural path returns ONE cube and assigns it to EnvCubemap,
    // DiffuseIrradiance and PrefilteredSpecular alike, so destroying "the irradiance" and "the
    // prefiltered env" is destroying the same texture twice. That double free is a SIGSEGV in
    // teardown — intermittent, after the frame is captured and the PNG is on disk, so the run looks
    // successful right up until the process dies and the exit code says 139.
    // Kept as a set rather than by reasoning about which fields alias today: that is the baker's
    // business and it may differ per source.
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
    /// <b>Recording needs no help from here, which is why there is no per-frame hook.</b>
    /// RenderGraph.Pass stores a scope against a pass HANDLE, and Execute walks PassOrder —
    /// declaration order — so when a tool records is irrelevant to when its pass runs. It calls this
    /// itself, any time before Render, and its pass runs after the stage's because that is when it
    /// was declared. Scopes are cleared at Execute, so it re-records each frame like everything else.
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

    // ── the look ─────────────────────────────────────────────────────────────────────────────
    //
    // <b>These moved to StudioLook, and the comment that used to sit here argued against it.</b> The
    // old argument — that gathering them dissolves once they are sorted by what READS them, since the
    // sun is the lit pass's, the shadow extent is the caster's and the exposure is the present pass's
    // — is true, and it answers a different question than the one that matters. Sorted by who DECIDES
    // them they are one artifact: Blix's house style, which a tool takes wholesale and a lab overrides
    // in part. TuneAttribute.Group carries the by-pass grouping into the panel, so nothing was lost by
    // letting the by-author grouping own the type.
    //
    // They lived on a StudioScene before that, because SetSunDirection needed somewhere to sit.
    // "Scene" promised a graph this deliberately does not have, and once the light moved out and the
    // ring of boxes turned out never to be drawn, there was nothing left in it.

    /// <summary>The house style this stage draws with. Owned here; a caller adjusts it in place.</summary>
    /// <remarks>
    /// <b>Owned rather than taken, and structural members are the reason the distinction is quiet.</b>
    /// A per-frame value can be changed whenever. A <see cref="TuneAttribute.Structural"/> one is read
    /// when the graph is built, so the window for setting it is between constructing this renderer and
    /// the first frame — the same window rung four's <c>extend</c> hook uses, and the same window the
    /// command line already runs in.
    /// </remarks>
    public StudioLook Look { get; } = new();

    /// <param name="extend">
    /// <b>Rung four: a tool adding a pass of its own.</b> Called with the stage's graph and targets
    /// after they exist and BEFORE <c>Compile()</c>, which is the only window in which a pass can be
    /// declared at all — so a selection outline, a pre-pass or an id buffer is a delegate rather
    /// than a fork of this file.
    /// <para>
    /// It is here on one prediction, recorded as one: <b>tooling asks to extend a graph before it
    /// asks to replace one.</b> If that turns out false this is one parameter to remove.
    /// </para>
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
        // hazard the probe exists to catch, in the one place the lab still had a hand-written number.
        var skinnedInterface = Reflect("studio_skinned.vert", "studio_lit.frag");
        var skinnedShadowInterface = Reflect("studio_skinned_shadow.vert", "studio_skinned_shadow.frag");

        // <b>A render graph, not hand-built surfaces.</b> The first cut of this used
        // CreateRenderSurface directly and failed on the first run: "RenderSurface needs at
        // least one color attachment" — which a shadow map does not have and should not be
        // made to pretend to. Depth-only targets live on the graph, which is the layer that
        // knows a pass can want depth and nothing else.
        graph = new RenderGraph(vk);
        var fullSize = new MatchSwapchainGraphSize(1.0f);
        // <b>THREE maps, near to far.</b> One box sized to cover everything spends its texels on
        // air: at the stage's default framing a single 2048 map over the visible ground is ~9 mm a
        // texel, and a contact shadow under a foot is the thing that resolution decides.
        for (var c = 0; c < CascadeCount; c++)
        {
            cascadeTargets[c] = graph.DepthTarget(
                $"lab-shadow-{c}", new FixedGraphSize(Look.ShadowMapSize, Look.ShadowMapSize));
        }
        sceneColourTarget = graph.ColorTarget("lab-hdr", TextureFormat.Rgba16F, fullSize);

        // <b>MSAA is a second colour target and a resolve, and nothing else knows.</b> The pass
        // builder takes its surface from whichever target it is given, so switching the target is
        // the whole of switching MSAA — the same two lines RTSGame reaches for, and the reason there
        // is no second code path here. The scene DEPTH has to match the colour's sample count or the
        // render pass is invalid, which is why it is threaded even when MSAA is off.
        // Clamped to what the device can actually do, and said out loud. This laptop's Metal backend
        // stops at 4x, and asking for 8 is a native assertion rather than an error anything can catch.
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

        // <b>A 1x depth for the present pass to SAMPLE.</b> A multisampled attachment is not
        // sampleable, and this stage's present pass carries scene depth across to the swapchain so
        // debug gizmos depth-test against the scene. GraphicsPassBuilder.ResolveDepth exists because
        // of this; before it, turning MSAA on failed at the first frame with "graph resource id 6
        // has no sampleable handle".
        if (samples > 1)
        {
            sceneDepthResolveTarget = graph.DepthTarget("lab-scene-depth-1x", fullSize);
        }

        // <b>A SECOND camera on the same scene, not a mirror of the first.</b> Showing the main
        // scene target in a panel would be a picture of the picture — it proves a texture can be
        // drawn (stage A did that) and nothing about views. A viewport is only a view if it can
        // look somewhere else, so this is its own target, its own camera and its own depth.
        //
        // <b>The SAME formats as the scene target, deliberately.</b> Two render passes whose
        // attachments match in format and sample count are render-pass COMPATIBLE, so a pipeline
        // baked against one is legal in the other — which means the viewport needs no pipelines of
        // its own. Give it an Rgba8 target instead and every lit and skinned pipeline would need a
        // twin, for a picture that is the same picture from a different chair.
        //
        // What that costs: the viewport holds HDR radiance with no tonemap, because the curve lives
        // in the present pass and a panel has no present pass — ImGui samples a texture and draws
        // it. Anything over 1.0 therefore clips. Accepted for now and written down; the fix is a
        // fragment stage that tonemaps, and it is not worth two pipeline families until the clipping
        // is actually in the way.
        //
        // Half the swapchain's size. A panel is a fraction of the window, the scene is drawn twice
        // to fill both, and paying full resolution for the smaller of the two is the kind of cost
        // that is invisible until a frame budget is tight.
        var halfSize = new MatchSwapchainGraphSize(0.5f);
        viewportColourTarget = graph.ColorTarget("lab-viewport", TextureFormat.Rgba16F, halfSize);
        viewportDepthTarget = graph.DepthTarget("lab-viewport-depth", halfSize);

        // <b>Three passes, and the caster geometry is drawn in every one of them.</b> That is the
        // cost cascades actually have and it is worth saying out loud rather than discovering: this
        // stage now redraws its casters 3x. It draws a handful of objects, so it is affordable here;
        // it is the first thing to look at if this stage ever gets a scene.
        for (var c = 0; c < CascadeCount; c++)
        {
            cascadePasses[c] = graph.GraphicsPass($"lab.shadow.{c}")
                .Depth(cascadeTargets[c], LoadOp.Clear, StoreOp.Store)
                .Shader(shadowInterface)
                .Handle;
        }

        // <b>Depth first, into the same buffer the lit pass then tests against.</b> The caster
        // shaders already do exactly this job — transform by a matrix, discard on a cutout — so the
        // pre-pass is those shaders with the CAMERA's view-projection where the light's goes, and
        // views need no new code path: to a view this is another StudioPass.Shadow.
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

        // Same shader interface as the lit pass, the same Read edge on the shadow map, and the same
        // PIPELINES — the viewport is the lit pass pointed somewhere else, which is exactly what
        // makes it a view rather than a second renderer.
        //
        // <b>It needed its own programs until the engine grew dynamic uniform offsets.</b> A program
        // used to own one uniform buffer per frame slot, so two passes sharing one shared the buffer
        // and the last uViewProjection written won for both — two cameras, one picture. The fix was
        // a duplicate program; the real fix was per-draw uniform storage, and now that it exists the
        // duplicate is gone. Two render passes whose attachments match are render-pass compatible,
        // so one pipeline serves both.
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

        // <b>Back-face culling, unlike everything else in this lab.</b> The boxes and the ground are
        // drawn with NoCulling so a camera inside one still shows something; a character is a closed
        // manifold whose interior is never the subject, and culling it halves the fill on the pass
        // that already costs the most. It also makes an inside-out rig — inverted bind matrices, a
        // mirrored import — visible as holes rather than as a mesh that merely looks odd.
        skinnedPipeline = vk.CreatePipeline(new PipelineDescription(
            skinnedProgram,
            VertexPosition3NormalTextureSkin4Tangent.Layout,
            PrimitiveTopology.Triangles,
            DepthState.LessEqualWrite,
            RasterizerState.BackFaceCulling,
            new[] { BlendState.Disabled },
            RenderTarget: graph.GetPassSurface(litPass)), "lab.skinned");

        // <b>Its twin, for the materials that say they have two sides.</b> Every material on all
        // three rigged assets in this tree is doubleSided — the Rogue's single material, the
        // peasant's four including MI_Hair_1 at 646 verts, the ranger's three — and all of them were
        // being drawn by the pipeline above, which culls. A closed body does not notice, so the
        // comment above stays true for the case it describes; what it missed is that a rig is not
        // only a closed body. See StudioViews.RigView for what honouring it actually moves.
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
                // WORLD metres per shadow texel, per cascade — each box's own side over the map side.
                // This used to be 1/ShadowMapSize three times, a UV quantity standing in for a length,
                // and the acne offset it fed was three millimetres wide as a result.
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
                // WORLD metres per shadow texel, per cascade — each box's own side over the map side.
                // This used to be 1/ShadowMapSize three times, a UV quantity standing in for a length,
                // and the acne offset it fed was three millimetres wide as a result.
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

                // The SAME views, from the second camera. That is what makes it a view rather
                // than a second renderer — and now that a view is an interface, a tool's own
                // contribution appears in the panel for free, which it never did before.
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
    /// <remarks>
    /// The last of what used to be a scene. There were seven boxes beside it at varied roughness —
    /// a good lighting subject and a terrible backdrop, as their own comment said — and both tools
    /// replaced them with ground-only the moment they loaded anything, so they were never once
    /// drawn. If look development wants a test subject again it arrives as a view, which is what
    /// rung two is for.
    /// </remarks>
    /// <param name="depthOnly">
    /// <b>The pre-pass draws this through a CASTER pipeline, which declares a smaller push block.</b>
    /// The lit block is 112 bytes and a caster's is 80, and pushing the larger at the smaller is
    /// rejected by the device with both sizes named — which is the binding model earning its keep,
    /// and how this was found the first time the ground went down the pre-pass.
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
            // The same array every other lit draw gets, plus this one's albedo. It used to be
            // `textures[0]` and a white texture, which was exactly right while the pass bound one
            // texture and one short of correct the moment it bound four.
            //
            // The caster shader declares an albedo too — at slot 0, so it can cut out — and every
            // draw on a pipeline must bind every texture that shader declares.
            textures: depthOnly
                ? new[] { new ShaderTextureBinding("uAlbedo", whiteTexture, Slot: 0) }
                : AppendAlbedo(textures, whiteTexture),
            pushConstants: push);
    }

    /// <summary>
    /// Bakes the house style's environment from its own sun — no asset, no HDR, no download.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Procedural, and that is the whole reason the stage can have IBL at all.</b> The engine's
    /// baker takes either an HDR equirect or a sun direction, and a tool that must open any model on
    /// any machine cannot depend on shipping an environment map. So the sun the house style already
    /// declares bakes its own sky, and the look stays self-contained.
    /// </para>
    /// <para>
    /// <b>The stand-in is 1x1 and the shader branches on it.</b> With IBL off there is still a cube
    /// and a LUT bound, because every draw on a pipeline must bind every texture its shader
    /// declares; what changes is a flag that sends the fragment down the flat-ambient path, rather
    /// than integrating one texel through the split-sum to arrive at a worse version of the same
    /// answer.
    /// </para>
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

        // Cached beside the binary. The table is the same numbers on every run, and the lab baseline
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
    /// <para>
    /// <b>Around the CONTENT, not around slices of the view frustum, and that was measured rather
    /// than chosen.</b> The textbook fit slices the frustum by depth, which assumes a camera
    /// standing among what it looks at. This stage's camera orbits its subject from eleven metres
    /// out, so the frustum where the subject stands is about twenty metres wide and any slice
    /// containing it must be at least that big. Fitted that way, at every split ratio tried, the
    /// subject landed in a cascade between 1.8x and 4.1x COARSER than the single origin-fitted box
    /// the cascades replaced — 15.6 to 36.1 mm a texel against 8.8. Nothing in the picture said so;
    /// the shadows were all present and merely soft.
    /// </para>
    /// <para>
    /// So the boxes are concentric on the stage's origin, which is where its ground and its subject
    /// both are, and the shader picks the first one that CONTAINS the fragment. That is the same
    /// conclusion RTSGame reached from the other direction, and it is a fact about this stage rather
    /// than about cascades: a turntable knows where its content is, and a fit that ignores that is
    /// spending resolution on the space between the camera and the thing.
    /// </para>
    /// <para>
    /// The radii follow the same practical split curve the frustum scheme uses, so
    /// <see cref="StudioLook.CascadeSplitLambda"/> still means what it means — 0 spaces them evenly,
    /// 1 concentrates them near the subject.
    /// </para>
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
    /// <remarks>
    /// It used to release only the fullscreen pass, leaving three pipelines, three programs and four
    /// buffers behind — nine objects, which is exactly what <c>BLIX_VK_VALIDATE=1</c> reported at device
    /// teardown. Nothing else notices a leak in a process that is about to exit, which is why the
    /// validation layers are the only thing that ever will.
    /// <para>
    /// The graph's own resources are the graph's; the surfaces here are its targets, not ours.
    /// </para>
    /// </remarks>
    public void Dispose()
    {
        // The graph owns render passes, framebuffers and offscreen images created through raw
        // Vulkan calls, which the device's own tables know nothing about — so it must be told to
        // let go. TankArena disposes its graph and says why; this one did not, which is what the
        // leaked-object count was.
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

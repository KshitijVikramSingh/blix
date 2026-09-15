using System.Numerics;
using System.Runtime.InteropServices;
using Blix.Diagnostics;
using Blix.Graphics;
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
/// <b>Deliberately not here:</b> cascades, texel snapping, bloom, IBL, MSAA, a depth pre-pass. Those exist
/// in TankArena and VulkanSponza because those earned them. A lab that grew them by default would be
/// claiming to be a renderer, and would stop being readable at exactly the point it became useful.
/// </para>
/// </remarks>
public sealed class StudioRenderer : IDisposable, ITunable
{
    /// <summary>Square shadow map, matching the texel size the lit shader offsets by.</summary>
    public const int ShadowMapSize = 2048;

    /// <summary>Bytes the lit pass pushes: mat4 model (64) + vec4 colour (16) + vec4 material (16).</summary>
    /// <remarks>
    /// Public so a tool can check it against what the shader actually declares. The one number in this
    /// file that can silently disagree with the SPIR-V — and did, on the first run, when the caster was
    /// handed the lit pass's block.
    /// </remarks>
    public const int LitPushBytes = 96;

    /// <summary>Bytes the caster pushes: the model matrix, and nothing else.</summary>
    public const int CasterPushBytes = 64;

    /// <summary>Bytes the SKINNED caster pushes: a vec4 whose x is the per-instance bone stride.</summary>
    /// <remarks>
    /// Smaller than the unskinned caster's, not larger. That one pushes a mat4 because it has to place
    /// its object; a skinned instance's placement is already baked into its palette, so all this stage
    /// needs is the stride. <c>studio_shadow.frag</c> declares no push block, so unlike the lit pair this
    /// block was free to be exactly what the stage uses rather than shaped to match a fragment stage.
    /// </remarks>
    public const int SkinnedCasterPushBytes = 16;

    private const int PushBytes = LitPushBytes;

    private VulkanGraphicsDevice device = null!;
    private FullscreenPass fullscreen = null!;

    private RenderGraph graph = null!;
    private GraphResourceHandle shadowTarget;
    private GraphResourceHandle sceneColourTarget;
    private GraphResourceHandle sceneDepthTarget;
    private GraphResourceHandle viewportColourTarget;
    private GraphResourceHandle viewportDepthTarget;
    private PassHandle shadowPass;
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
    private TextureHandle whiteTexture;

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
    private readonly byte[] casterPushScratch = new byte[CasterPushBytes];
    private readonly byte[] skinnedCasterPushScratch = new byte[SkinnedCasterPushBytes];

    // The caster only needs the model matrix, and the reflected interface says so — 64
    // bytes against the lit pass's 96. Pushing the larger block at it is rejected by the
    // device with the sizes named, which is the binding model earning its keep: a
    // hand-declared interface would have shrugged and corrupted the tail.

    // ── the look, declared, and it is not a thing of its own ─────────────────────────────────
    //
    // <b>There is no StudioLook, and the reason is worth keeping.</b> Gathering these into one
    // "look" object was the obvious move and it dissolved the moment they were sorted by what reads
    // them: the sun and the ambient are the LIT pass's, the shadow extent is the SHADOW pass's, and
    // the exposure and tonemap are the PRESENT pass's. That is not one concept, it is three sets of
    // pass parameters — and parameters belong with what consumes them, which is this.
    //
    // They lived on a StudioScene because SetSunDirection needed somewhere to sit. "Scene" promised
    // a graph this deliberately does not have, and once the light moved here and the ring of boxes
    // turned out never to be drawn, there was nothing left in it.

    /// <summary>Degrees around Y, from +Z toward +X.</summary>
    [Tune(0, 360)] public float SunAzimuth { get; set; } = 52.125f;

    /// <summary>Degrees above the horizon. Not 90: straight down has no stable up vector.</summary>
    [Tune(0, 89)] public float SunElevation { get; set; } = 54.526f;

    /// <summary>Scales the sun's tint. One is the light this stage was authored under.</summary>
    [Tune(0, 3)] public float SunIntensity { get; set; } = 1f;

    /// <summary>Flat stand-in for image-based lighting, which this stage does not carry.</summary>
    [Tune(0, 0.5f)] public float AmbientStrength { get; set; } = 0.06f;

    /// <summary>Half-width of the sun's orthographic box, in metres.</summary>
    /// <remarks>
    /// A knob because it is a trade every subject settles differently: too wide and a small rig gets
    /// a few texels of shadow map, too narrow and a large one is cut off at the edge of the light.
    /// </remarks>
    [Tune(2, 40)] public float ShadowExtent { get; set; } = 9f;

    /// <summary>Whether the floor is drawn. Off is how you look at a thing against nothing.</summary>
    /// <remarks>
    /// It is a lit mesh rather than a gizmo, and it has to be: a shadow needs something to land on.
    /// The GRID over it is a gizmo and always was — <c>debug.Draw.Grid</c>, engine-native, drawn by
    /// the tool. The two were never one thing; they only ever looked like one.
    /// </remarks>
    [Tune] public bool Ground { get; set; } = true;

    /// <summary>Exposure applied before tonemapping.</summary>
    [Tune(0, 4)] public float Exposure { get; set; } = 1.0f;

    /// <summary>0 = ACES, 1 = AgX, 2 = Reinhard, 3 = neutral. Matches blix_tonemap.</summary>
    [Tune(0, 3)] public float TonemapMode { get; set; }

    /// <summary>Direction TOWARD the sun. Derived from the two angles.</summary>
    public Vector3 SunDirection { get; private set; } = Vector3.Normalize(new Vector3(0.45f, 0.8f, 0.35f));

    /// <summary>Derived: the tint this stage was authored with, scaled by <see cref="SunIntensity"/>.</summary>
    public Vector3 SunColour { get; private set; } = new(3.2f, 3.05f, 2.75f);

    private static readonly Vector3 SunTint = new(3.2f, 3.05f, 2.75f);

    /// <summary>
    /// Derives the look once, so the declared angles and the derived vector agree from frame one.
    /// </summary>
    /// <remarks>
    /// <b>Without this the two disagreed silently.</b> The vector fields carry the hardcoded
    /// direction this stage was authored with, and nothing recomputed them until something MOVED —
    /// so a run with no flags lit the scene from the old vector while the panel showed angles that
    /// did not produce it. The capture is what caught it: it came back matching the picture from
    /// before the angles existed, byte for byte, which is exactly what "the knob is ignored" looks
    /// like when the default happens to be close.
    /// </remarks>
    public StudioRenderer() => Recompute();

    /// <summary>A declared value moved. Recompute what is derived from it.</summary>
    public void OnChanged(TunableChange change) => Recompute();

    /// <summary>Derive the sun's vectors from its angles. Also run once at construction.</summary>
    public void Recompute()
    {
        var elevation = SunElevation * (MathF.PI / 180f);
        var azimuth = SunAzimuth * (MathF.PI / 180f);
        var horizontal = MathF.Cos(elevation);

        SunDirection = Vector3.Normalize(new Vector3(
            horizontal * MathF.Sin(azimuth),
            MathF.Sin(elevation),
            horizontal * MathF.Cos(azimuth)));

        SunColour = SunTint * SunIntensity;
    }

    /// <summary>
    /// A sun view-projection that covers the stage, for the caster pass.
    /// </summary>
    /// <remarks>
    /// An orthographic box aimed down the sun direction at the origin. No cascades and no texel
    /// snapping — both belong to a renderer that has earned them (TankArena and Sponza have), and a
    /// tooling stage that grew them by default would be quietly claiming to be one.
    /// </remarks>
    public Matrix4x4 SunViewProjection(float? extent = null, float depth = 30f)
    {
        var box = extent ?? ShadowExtent;
        var eye = SunDirection * (depth * 0.5f);
        var up = MathF.Abs(Vector3.Dot(SunDirection, Vector3.UnitY)) > 0.95f ? Vector3.UnitZ : Vector3.UnitY;
        var view = Matrix4x4.CreateLookAt(eye, Vector3.Zero, up);
        var projection = GraphicsMatrices.CreateOrthographicVulkan(box * 2f, box * 2f, 0.1f, depth);
        return view * projection;
    }

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
        var skinnedShadowInterface = Reflect("studio_skinned_shadow.vert", "studio_shadow.frag");

        // <b>A render graph, not hand-built surfaces.</b> The first cut of this used
        // CreateRenderSurface directly and failed on the first run: "RenderSurface needs at
        // least one color attachment" — which a shadow map does not have and should not be
        // made to pretend to. Depth-only targets live on the graph, which is the layer that
        // knows a pass can want depth and nothing else.
        graph = new RenderGraph(vk);
        var fullSize = new MatchSwapchainGraphSize(1.0f);
        shadowTarget = graph.DepthTarget("lab-shadow", new FixedGraphSize(ShadowMapSize, ShadowMapSize));
        sceneColourTarget = graph.ColorTarget("lab-hdr", TextureFormat.Rgba16F, fullSize);
        sceneDepthTarget = graph.DepthTarget("lab-scene-depth", fullSize);

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

        shadowPass = graph.GraphicsPass("lab.shadow")
            .Depth(shadowTarget, LoadOp.Clear, StoreOp.Store)
            .Shader(shadowInterface)
            .Handle;

        // Read() is the edge that makes the ordering a fact rather than a convention: the
        // lit pass samples what the caster pass wrote, and the graph knows it.
        litPass = graph.GraphicsPass("lab.lit")
            .Target(sceneColourTarget, LoadOp.Clear, StoreOp.Store)
            .Depth(sceneDepthTarget, LoadOp.Clear, StoreOp.Store)
            .Read(shadowTarget)
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
            .Read(shadowTarget)
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
            graph, sceneColourTarget, sceneDepthTarget, shadowTarget, litInterface, shadowInterface));

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
            Spv("studio_skinned_shadow.vert"), Spv("studio_shadow.frag"), skinnedShadowInterface, "lab.skinned.shadow");


        shadowPipeline = vk.CreatePipeline(new PipelineDescription(
            shadowProgram,
            VertexPosition3NormalTexture.Layout,
            PrimitiveTopology.Triangles,
            DepthState.LessEqualWrite,
            RasterizerState.NoCulling,
            Array.Empty<BlendState>(),
            RenderTarget: graph.GetPassSurface(shadowPass)), "lab.shadow");

        litPipeline = vk.CreatePipeline(new PipelineDescription(
            litProgram,
            VertexPosition3NormalTexture.Layout,
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

        // The caster does NOT cull: a one-sided shadow from a back-face-culled caster loses the far
        // side of a limb, and a character's own silhouette is mostly far sides.
        skinnedShadowPipeline = vk.CreatePipeline(new PipelineDescription(
            skinnedShadowProgram,
            VertexPosition3NormalTextureSkin4Tangent.Layout,
            PrimitiveTopology.Triangles,
            DepthState.LessEqualWrite,
            RasterizerState.NoCulling,
            Array.Empty<BlendState>(),
            RenderTarget: graph.GetPassSurface(shadowPass)), "lab.skinned.shadow");

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

        var (cv, ci) = StudioGeometry.Cube();
        cubeVertices = vk.CreateVertexBuffer(VertexPosition3NormalTexture.CreateBufferData(cv), "lab.cube.vb");
        cubeIndices = vk.CreateIndexBuffer(ci, name: "lab.cube.ib");
        cubeIndexCount = ci.Length;

        var (gv, gi) = StudioGeometry.Ground();
        groundVertices = vk.CreateVertexBuffer(VertexPosition3NormalTexture.CreateBufferData(gv), "lab.ground.vb");
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
    public TextureHandle ShadowDepth => graph.GetDepthTexture(shadowTarget);

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
        var sunViewProjection = SunViewProjection();

        // Pass 1 — the sun's depth. No colour attachment at all, which is the thing the
        // raw surface path could not express.
        graph.Pass(shadowPass, scope =>
        {
            var uniforms = new ShaderUniform[]
            {
                new("uLightViewProjection", new Matrix4x4Uniform(sunViewProjection)),
            };

            // No furniture in the caster pass at all: the only furniture left is the ground, and
            // the ground casts nothing onto itself worth the fill.

            var draw = new StudioDraw(
                scope, StudioPass.Shadow, uniforms, Array.Empty<ShaderTextureBinding>(),
                shadowPipeline, skinnedShadowPipeline, whiteTexture);
            foreach (var view in views) view.Draw(draw);
        });

        // Pass 2 — light it into HDR, sampling the depth the caster pass just wrote.
        var shadowTexture = graph.GetDepthTexture(shadowTarget);
        graph.Pass(litPass, scope =>
        {
            var uniforms = new ShaderUniform[]
            {
                new("uViewProjection", new Matrix4x4Uniform(viewProjection)),
                new("uSunViewProjection", new Matrix4x4Uniform(sunViewProjection)),
                new("uCameraPosition", new Vector4Uniform(new Vector4(cameraPosition, 1f))),
                new("uSunDirection", new Vector4Uniform(new Vector4(SunDirection, 0f))),
                new("uSunColour", new Vector4Uniform(new Vector4(SunColour, AmbientStrength))),
            };
            var textures = new[] { new ShaderTextureBinding("uSunShadowMap", shadowTexture, Slot: 0) };

            // FURNITURE, and it stays the stage's: a tool does not choose whether the stage has a
            // floor. That is part of what makes it a stage rather than a blank device.
            if (Ground) DrawGround(scope, litPipeline, uniforms, textures);

            var draw = new StudioDraw(
                scope, StudioPass.Lit, uniforms, textures, litPipeline, skinnedPipeline, whiteTexture);
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
                    new("uSunViewProjection", new Matrix4x4Uniform(sunViewProjection)),
                    new("uCameraPosition", new Vector4Uniform(new Vector4(viewportCameraPosition, 1f))),
                    new("uSunDirection", new Vector4Uniform(new Vector4(SunDirection, 0f))),
                    new("uSunColour", new Vector4Uniform(new Vector4(SunColour, AmbientStrength))),
                };
                var textures = new[] { new ShaderTextureBinding("uSunShadowMap", shadowTexture, Slot: 0) };

                if (Ground) DrawGround(scope, litPipeline, uniforms, textures);

                // The SAME views, from the second camera. That is what makes it a view rather
                // than a second renderer — and now that a view is an interface, a tool's own
                // contribution appears in the panel for free, which it never did before.
                var draw = new StudioDraw(
                    scope, StudioPass.Lit, uniforms, textures, litPipeline, skinnedPipeline, whiteTexture);
                foreach (var view in views) view.Draw(draw);
            });
        }

        graph.Execute(commandList);

        // Pass 3 — exposure + tonemap onto the swapchain.
        var present = new ShaderUniform[]
        {
            new("uParams", new Vector4Uniform(new Vector4(Exposure, TonemapMode, 0f, 0f))),
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
                    new ShaderTextureBinding("uSceneDepth", graph.GetDepthTexture(sceneDepthTarget), Slot: 1),
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
    private void DrawGround(
        RenderPassBuilder pass,
        PipelineHandle pipeline,
        ShaderUniform[] uniforms,
        ShaderTextureBinding[] textures)
    {
        StudioPush.Matrix(Matrix4x4.Identity, pushScratch);
        StudioPush.Material(pushScratch, GroundColour, metallic: 0f, roughness: 0.9f);

        pass.DrawIndexed(
            vertexBuffer: groundVertices,
            indexBuffer: groundIndices,
            pipeline: pipeline,
            indexCount: groundIndexCount,
            uniforms: uniforms,
            textures: new[] { textures[0], new ShaderTextureBinding("uAlbedo", whiteTexture, Slot: 1) },
            pushConstants: pushScratch);
    }

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
    }
}

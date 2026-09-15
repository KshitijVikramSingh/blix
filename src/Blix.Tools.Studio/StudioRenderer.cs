using System.Numerics;
using System.Runtime.InteropServices;
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
public sealed class StudioRenderer : IDisposable
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

    private readonly byte[] pushScratch = new byte[PushBytes];
    private readonly byte[] casterPushScratch = new byte[CasterPushBytes];
    private readonly byte[] skinnedCasterPushScratch = new byte[SkinnedCasterPushBytes];

    // The caster only needs the model matrix, and the reflected interface says so — 64
    // bytes against the lit pass's 96. Pushing the larger block at it is rejected by the
    // device with the sizes named, which is the binding model earning its keep: a
    // hand-declared interface would have shrugged and corrupted the tail.

    /// <summary>Exposure applied before tonemapping.</summary>
    public float Exposure { get; set; } = 1.0f;

    /// <summary>0 = ACES, 1 = AgX, 2 = Reinhard, 3 = neutral. Matches blix_tonemap.</summary>
    public float TonemapMode { get; set; }

    public void Load(VulkanGraphicsDevice vk, string shaderDirectory)
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
        StudioScene scene,
        Matrix4x4 viewProjection,
        Vector3 cameraPosition,
        StudioModel? model = null,
        Matrix4x4 modelTransform = default,
        StudioRig? rig = null,
        int rigInstances = 1,
        Matrix4x4? viewportViewProjection = null,
        Vector3 viewportCameraPosition = default)
    {
        if (modelTransform == default) modelTransform = Matrix4x4.Identity;

        var sunViewProjection = scene.SunViewProjection();

        // Pass 1 — the sun's depth. No colour attachment at all, which is the thing the
        // raw surface path could not express.
        graph.Pass(shadowPass, scope =>
        {
            var uniforms = new ShaderUniform[]
            {
                new("uLightViewProjection", new Matrix4x4Uniform(sunViewProjection)),
            };

            foreach (var item in scene.Objects)
            {
                // The ground casts nothing onto itself worth the fill.
                if (item.IsGround) continue;
                DrawObject(scope, item, shadowPipeline, uniforms, Array.Empty<ShaderTextureBinding>(), casterOnly: true);
            }

            DrawModelParts(
                scope, model, modelTransform, shadowPipeline, uniforms,
                Array.Empty<ShaderTextureBinding>(), casterOnly: true);

            DrawRigParts(
                scope, rig, rigInstances, skinnedShadowPipeline, uniforms,
                Array.Empty<ShaderTextureBinding>(), casterOnly: true);
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
                new("uSunDirection", new Vector4Uniform(new Vector4(scene.SunDirection, 0f))),
                new("uSunColour", new Vector4Uniform(new Vector4(scene.SunColour, scene.AmbientStrength))),
            };
            var textures = new[] { new ShaderTextureBinding("uSunShadowMap", shadowTexture, Slot: 0) };

            foreach (var item in scene.Objects)
            {
                DrawObject(scope, item, litPipeline, uniforms, textures);
            }

            DrawModelParts(scope, model, modelTransform, litPipeline, uniforms, textures, casterOnly: false);
            DrawRigParts(scope, rig, rigInstances, skinnedPipeline, uniforms, textures, casterOnly: false);
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
                    new("uSunDirection", new Vector4Uniform(new Vector4(scene.SunDirection, 0f))),
                    new("uSunColour", new Vector4Uniform(new Vector4(scene.SunColour, scene.AmbientStrength))),
                };
                var textures = new[] { new ShaderTextureBinding("uSunShadowMap", shadowTexture, Slot: 0) };

                foreach (var item in scene.Objects)
                {
                    DrawObject(scope, item, litPipeline, uniforms, textures);
                }

                DrawModelParts(
                    scope, model, modelTransform, litPipeline, uniforms, textures, casterOnly: false);
                DrawRigParts(
                    scope, rig, rigInstances, skinnedPipeline, uniforms, textures, casterOnly: false);
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

    // Each node's primitives, placed by that node's COMPOSED world transform — the same walk up
    // the parent chain blix-cook inspect does. Drawn per part rather than fused, because a fused
    // mesh cannot answer where any part's pivot is, which is the question an asset raises.
    private void DrawModelParts(
        RenderPassBuilder pass,
        StudioModel? model,
        Matrix4x4 modelTransform,
        PipelineHandle pipeline,
        ShaderUniform[] uniforms,
        ShaderTextureBinding[] textures,
        bool casterOnly)
    {
        if (model is null) return;

        for (var index = 0; index < model.Parts.Count; index++)
        {
            var part = model.Parts[index];
            var node = model.Nodes[part.NodeIndex];
            var push = casterOnly ? casterPushScratch : pushScratch;
            PackMatrix(node.WorldTransform * modelTransform, push);
            if (!casterOnly)
            {
                var floats = MemoryMarshal.Cast<byte, float>(push.AsSpan());
                floats[16] = part.BaseColour.X;
                floats[17] = part.BaseColour.Y;
                floats[18] = part.BaseColour.Z;
                floats[19] = 1f;
                floats[20] = part.Metallic;
                floats[21] = part.Roughness;
            }

            // The caster samples nothing — its shader declares no textures at all, and it is
            // handed an empty list. The lit draw gets a FRESH array per part: push payloads are
            // copied at record time, texture lists are still retained by reference, so a shared
            // array would give every draw the last part's albedo — the aliasing that stacked
            // seven boxes, wearing a different hat.
            if (casterOnly)
            {
                pass.DrawIndexed(
                    vertexBuffer: part.Vertices,
                    indexBuffer: part.Indices,
                    pipeline: pipeline,
                    indexCount: part.IndexCount,
                    uniforms: uniforms,
                    textures: textures,
                    pushConstants: push);
            }
            else
            {
                pass.DrawIndexed(
                    vertexBuffer: part.Vertices,
                    indexBuffer: part.Indices,
                    pipeline: pipeline,
                    indexCount: part.IndexCount,
                    uniforms: uniforms,
                    textures: new[]
                    {
                        textures[0],
                        new ShaderTextureBinding("uAlbedo", part.Albedo, Slot: 1),
                    },
                    pushConstants: push);
            }
        }
    }

    // The rig's primitives, drawn ONCE each for N instances out of one sliced palette.
    //
    // The contrast with DrawModelParts is the whole difference between a static asset and a rigged
    // one: there, each part is placed by its node's composed world matrix, because the hierarchy IS
    // the articulation. Here the hierarchy lives in the palette and every primitive of an instance
    // reads the same slice — a skin that placed its parts individually would tear along their seams.
    //
    // <b>This is where the one-palette-per-frame gap was.</b> A descriptor set's BUFFER is not copied
    // at record time the way a push payload is, so two draws in one frame sharing one palette binding
    // both read whatever it held at Execute — the second pose, twice. The fix is not more bindings
    // but a wider one: every instance's matrices in a single buffer at a known stride, indexed by
    // gl_InstanceIndex. Bulwark and RTSGame both reached the same shape; BonePaletteSet is the
    // stride contract they were each restating.
    //
    // uModel goes up as IDENTITY. Each instance's placement is baked into its own palette slice,
    // because a per-draw push constant cannot vary per instance.
    private void DrawRigParts(
        RenderPassBuilder pass,
        StudioRig? rig,
        int instances,
        PipelineHandle pipeline,
        ShaderUniform[] uniforms,
        ShaderTextureBinding[] textures,
        bool casterOnly)
    {
        if (rig is null || instances <= 0) return;

        var stride = (float)rig.Skeleton.BoneCount;
        foreach (var part in rig.Parts)
        {
            byte[] push;
            if (casterOnly)
            {
                push = skinnedCasterPushScratch;
                var casterFloats = MemoryMarshal.Cast<byte, float>(push.AsSpan());
                casterFloats[0] = stride;
                casterFloats[1] = 0f;
                casterFloats[2] = 0f;
                casterFloats[3] = 0f;
            }
            else
            {
                push = pushScratch;
                PackMatrix(Matrix4x4.Identity, push);
                var floats = MemoryMarshal.Cast<byte, float>(push.AsSpan());
                floats[16] = part.BaseColour.X;
                floats[17] = part.BaseColour.Y;
                floats[18] = part.BaseColour.Z;
                floats[19] = 1f;
                floats[20] = part.Metallic;
                floats[21] = part.Roughness;
                // z, where the shader reads the per-instance stride. See studio_skinned.vert for why it
                // rides in a material slot rather than in a block of its own.
                floats[22] = stride;
                floats[23] = 0f;
            }

            pass.DrawIndexedInstanced(
                vertexBuffer: part.Vertices,
                indexBuffer: part.Indices,
                pipeline: pipeline,
                indexCount: part.IndexCount,
                instanceCount: instances,
                uniforms: uniforms,
                // A fresh array per part for the same reason DrawModelParts builds one: texture
                // lists are retained by reference, so a shared array gives every draw the last
                // part's albedo.
                textures: casterOnly
                    ? textures
                    : new[] { textures[0], new ShaderTextureBinding("uAlbedo", part.Albedo, Slot: 1) },
                perDrawMaterial: rig.BoneMaterial,
                pushConstants: push);
        }
    }

    private static void PackMatrix(Matrix4x4 m, byte[] target)
    {
        var floats = MemoryMarshal.Cast<byte, float>(target.AsSpan());
        floats[0] = m.M11; floats[1] = m.M12; floats[2] = m.M13; floats[3] = m.M14;
        floats[4] = m.M21; floats[5] = m.M22; floats[6] = m.M23; floats[7] = m.M24;
        floats[8] = m.M31; floats[9] = m.M32; floats[10] = m.M33; floats[11] = m.M34;
        floats[12] = m.M41; floats[13] = m.M42; floats[14] = m.M43; floats[15] = m.M44;
    }

    private void DrawObject(
        RenderPassBuilder pass,
        in StudioObject item,
        PipelineHandle pipeline,
        ShaderUniform[] uniforms,
        ShaderTextureBinding[] textures,
        bool casterOnly = false)
    {
        var push = casterOnly ? casterPushScratch : pushScratch;
        PackPush(item, push);
        var bindings = casterOnly
            ? textures
            : new[] { textures[0], new ShaderTextureBinding("uAlbedo", whiteTexture, Slot: 1) };
        pass.DrawIndexed(
            vertexBuffer: item.IsGround ? groundVertices : cubeVertices,
            indexBuffer: item.IsGround ? groundIndices : cubeIndices,
            pipeline: pipeline,
            indexCount: item.IsGround ? groundIndexCount : cubeIndexCount,
            uniforms: uniforms,
            textures: bindings,
            pushConstants: push);
    }

    // mat4 model, vec4 base colour, vec4 (metallic, roughness, _, _) — 96 bytes, inside the
    // 128-byte floor every Vulkan implementation guarantees, which is why there is no
    // per-object descriptor set in this lab at all.
    private static void PackPush(in StudioObject item, byte[] target)
    {
        var floats = MemoryMarshal.Cast<byte, float>(target.AsSpan());
        var m = item.Model;
        floats[0] = m.M11; floats[1] = m.M12; floats[2] = m.M13; floats[3] = m.M14;
        floats[4] = m.M21; floats[5] = m.M22; floats[6] = m.M23; floats[7] = m.M24;
        floats[8] = m.M31; floats[9] = m.M32; floats[10] = m.M33; floats[11] = m.M34;
        floats[12] = m.M41; floats[13] = m.M42; floats[14] = m.M43; floats[15] = m.M44;
        if (floats.Length < 24) return;   // the caster's 64-byte block is the model matrix only
        floats[16] = item.BaseColour.X; floats[17] = item.BaseColour.Y; floats[18] = item.BaseColour.Z; floats[19] = 1f;
        floats[20] = item.Metallic; floats[21] = item.Roughness; floats[22] = 0f; floats[23] = 0f;
    }

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

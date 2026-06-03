using System.Numerics;
using System.Runtime.InteropServices;
using Blix;
using Blix.Core;
using Blix.Diagnostics;
using Blix.Graphics;
using Blix.Graphics.Primitives;
using Blix.Graphics.Vulkan;
using Blix.Render;
using Blix.Runtime.Silk;

namespace Blix.Demos.VulkanParticles;

// ParticleBatch showcase — three CPU-simulated effects (a spark FOUNTAIN with drifting
// SMOKE, a periodic EXPLOSION, a swirling VORTEX) riding the per-frame transient vertex
// arena, rendered through an HDR bloom pipeline with soft-particle depth fade.
//
// Pass graph (RenderGraph, baked once): depth pre-pass → scene (opaque ground+blocks,
// then soft particles into an Rgba16F HDR target) → bloom bright/blurH/blurV → present
// (composite + ACES tonemap → swapchain). The pre-pass produces a sampleable depth so
// the particle pass can dissolve billboards into geometry (a colour target is single-
// writer and a pass can't sample its own depth attachment).
//
// ParticleBatch stays a pure geometry primitive: the demo brings the soft pipeline, the
// push (viewProj + near/far/fadeDist + per-effect sharpness), and the scene-depth
// binding. Additive sparks/explosion/vortex share one cached pipeline; smoke uses a
// premultiplied-alpha pipeline (same shader, premult output) so it composites as soft
// translucent smoke rather than a dark blob.
//
// Interactive: drag to orbit, wheel/Q-E to zoom, arrows/WASD to look, Space toggles
// auto-orbit, R resets. Pass --debug to enable the tuning + diagnostics overlay (` then
// shows/hides it). Auto-exits after --frames N (validation gate: BLIX_VK_VALIDATE=1,
// grep [vk-ERR ]/[vk-WARN ]).
public static class Program
{
    public static void Main(string[] args)
    {
        var exitAfterFrames = 0;
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == "--frames" && int.TryParse(args[i + 1], out var n)) exitAfterFrames = n;
        }
        var overlay = args.Contains("--debug");   // opt into the tuning + diagnostics overlay

        var loop = new ParticlesLoop(exitAfterFrames, overlay);
        using var window = new Window(loop, new WindowOptions("Blix — Particles (soft + bloom)", 1280, 720));
        window.Run();
    }
}

// Live-tunable post/effect knobs — reflected into the overlay via [Tune] and mutated in
// place, so reading a field each frame picks up the slider value.
internal sealed class ParticleSettings
{
    [Tune(0.2f, 3.0f)] public float Exposure = 1.25f;
    [Tune(0.0f, 3.0f)] public float Bloom = 1.4f;
    [Tune(0.2f, 2.0f)] public float Threshold = 0.85f;
    [Tune(0.1f, 2.0f)] public float FadeDist = 0.55f;
    [Tune(0.5f, 6.0f)] public float SparkSharpness = 3.4f;
    [Tune(0.5f, 4.0f)] public float SmokeSharpness = 1.1f;
    [Tune]             public bool ShowGizmos = false;
}

internal sealed class ParticlesLoop : IGameLoop, IInputHandler, IDebuggable, IDisposable
{
    // Effect anchor points, consolidated so the whole scene frames in one view.
    private static readonly Vector3 FountainAt = new(0f, 0f, 0f);
    private static readonly Vector3 ExplosionAt = new(-4.5f, 2.2f, 0f);
    private static readonly Vector3 VortexAt = new(4.5f, 0f, 0f);

    private const float ExplosionInterval = 1.8f;
    private const float NearPlane = 0.1f;
    private const float FarPlane = 200f;
    private const float BloomScale = 0.25f;

    private readonly int exitAfterFrames;
    private readonly bool overlayEnabled;        // --debug: enables the ` overlay
    private readonly Random rng = new(12345);   // deterministic emission
    private readonly ParticleSettings fx = new();
    private ObjectTunables tunables = null!;
    private VulkanGraphicsDevice vk = null!;
    private IRenderHost host = null!;

    // One drawable effect = a batch + its pipeline + sort flag + radial sharpness + its
    // OWN push buffer (each draw's push bytes are read at submit, so buffers can't alias).
    private sealed class Effect
    {
        public Effect(ParticleBatch batch, bool sort, bool additive)
        {
            Batch = batch;
            Sort = sort;
            Additive = additive;
        }
        public ParticleBatch Batch { get; }
        public bool Sort { get; }
        public bool Additive { get; }                 // picks pipeline + which sharpness knob
        public PipelineHandle Pipeline { get; set; }
        public readonly byte[] Push = new byte[80];   // viewProj(64)+near+far+fade+sharpness
    }

    private ParticleBatch sparks = null!, explosion = null!, vortex = null!, smoke = null!;
    private Effect[] effects = null!;

    // Render graph + targets.
    private RenderGraph graph = null!;
    private GraphResourceHandle hdrHandle, sceneDepthHandle, sceneDepthBHandle;
    private GraphResourceHandle bloomBrightHandle, bloomBlurHHandle, bloomBlurVHandle;
    private PassHandle depthPrepassHandle, scenePassHandle;
    private PassHandle bloomBrightPassHandle, bloomBlurHPassHandle, bloomBlurVPassHandle;

    // Pipelines.
    private PipelineHandle depthOnlyPipeline, opaquePipeline;
    private PipelineHandle additivePipeline, premultPipeline;
    private PipelineHandle bloomBrightPipeline, bloomBlurPipeline, presentPipeline;

    // Opaque backdrop geometry (drawn in both the depth pre-pass and the scene pass).
    private readonly List<OpaqueMesh> opaques = new();
    private VertexBufferHandle fullscreenVB;
    private IndexBufferHandle fullscreenIB;

    // Scratch push buffers (reused; sizes match the shader push ranges).
    private readonly byte[] depthPush = new byte[128];     // model + viewProj
    private readonly byte[] opaquePush = new byte[144];    // model + viewProj + albedo
    private readonly byte[] presentPush = new byte[8];     // exposure + bloomIntensity
    private readonly byte[] bloomBrightPush = new byte[4]; // threshold
    private readonly ShaderTextureBinding[] particleTextures = new ShaderTextureBinding[1];

    // Orbit camera state.
    private float camYaw = 0.7f, camPitch = 0.30f, camRadius = 10.5f;
    private bool autoOrbit = true, dragging;
    private static readonly Vector3 CamTarget = new(0f, 2.2f, 0f);
    private Matrix4x4 viewProj;
    private Vector3 camRight, camUp, camPos;
    private float aspect = 16f / 9f;

    private float explosionTimer = ExplosionInterval;   // burst on the very first update
    private float vortexAngle;
    private int frameCount;

    private readonly record struct OpaqueMesh(
        VertexBufferHandle VB, IndexBufferHandle IB, int IndexCount, Matrix4x4 Model, Vector4 Albedo);

    public ParticlesLoop(int exitAfterFrames, bool overlayEnabled)
    {
        this.exitAfterFrames = exitAfterFrames;
        this.overlayEnabled = overlayEnabled;
    }

    public string DebugName => "particles";

    public void OnLoad(IRenderHost host, IGraphicsDevice graphicsDevice)
    {
        this.host = host;
        vk = (VulkanGraphicsDevice)graphicsDevice;
        var shaderDir = Path.Combine(AppContext.BaseDirectory, "Shaders");

        // --- Render graph ------------------------------------------------
        graph = new RenderGraph(vk);
        var fullSize = new MatchSwapchainGraphSize(1.0f);
        var bloomSize = new MatchSwapchainGraphSize(BloomScale);
        hdrHandle = graph.ColorTarget("hdr", TextureFormat.Rgba16F, fullSize);
        sceneDepthHandle = graph.DepthTarget("scene-depth", fullSize);   // sampled, single-sample
        sceneDepthBHandle = graph.DepthTarget("scene-depthB", fullSize); // scene-pass occlusion
        bloomBrightHandle = graph.ColorTarget("bloom-bright", TextureFormat.Rgba16F, bloomSize);
        bloomBlurHHandle = graph.ColorTarget("bloom-blurH", TextureFormat.Rgba16F, bloomSize);
        bloomBlurVHandle = graph.ColorTarget("bloom-blurV", TextureFormat.Rgba16F, bloomSize);

        // --- Shader interfaces -------------------------------------------
        var depthInterface = new ShaderInterface(
            Slots: Array.Empty<DescriptorSetSlot>(),
            PushConstants: new[] { new PushConstantRange(ShaderStages.Vertex, 0, 128) });
        var opaqueInterface = new ShaderInterface(
            Slots: Array.Empty<DescriptorSetSlot>(),
            PushConstants: new[] { new PushConstantRange(ShaderStages.Vertex | ShaderStages.Fragment, 0, 144) });
        // Soft particles: one read-only sampler (scene depth, set 0 binding 0) + an 80-byte
        // viewProj/near/far/fadeDist/sharpness push.
        var softInterface = new ShaderInterface(
            Slots: new[] { new DescriptorSetSlot(0, 0, ShaderResourceType.SampledImage, ShaderStages.Fragment) },
            PushConstants: new[] { new PushConstantRange(ShaderStages.Vertex | ShaderStages.Fragment, 0, 80) });
        var bloomBrightInterface = new ShaderInterface(
            Slots: new[] { new DescriptorSetSlot(0, 0, ShaderResourceType.SampledImage, ShaderStages.Fragment) },
            PushConstants: new[] { new PushConstantRange(ShaderStages.Fragment, 0, 4) });
        var bloomBlurInterface = new ShaderInterface(
            Slots: new[] { new DescriptorSetSlot(0, 0, ShaderResourceType.SampledImage, ShaderStages.Fragment) },
            PushConstants: new[] { new PushConstantRange(ShaderStages.Fragment, 0, 8) });
        var presentInterface = new ShaderInterface(
            Slots: new[]
            {
                new DescriptorSetSlot(0, 0, ShaderResourceType.SampledImage, ShaderStages.Fragment),
                new DescriptorSetSlot(0, 1, ShaderResourceType.SampledImage, ShaderStages.Fragment),
            },
            PushConstants: new[] { new PushConstantRange(ShaderStages.Fragment, 0, 8) });

        // --- Declare passes (execute in declaration order) ---------------
        depthPrepassHandle = graph.GraphicsPass("depth-prepass")
            .Depth(sceneDepthHandle, LoadOp.Clear, StoreOp.Store)
            .Shader(depthInterface)
            .Handle;
        scenePassHandle = graph.GraphicsPass("scene")
            .Target(hdrHandle, LoadOp.Clear, StoreOp.Store)
            .Depth(sceneDepthBHandle, LoadOp.Clear, StoreOp.Store)
            .Read(sceneDepthHandle)
            .Shader(opaqueInterface, softInterface)
            .Handle;
        bloomBrightPassHandle = graph.GraphicsPass("bloom-bright")
            .Target(bloomBrightHandle, LoadOp.Clear, StoreOp.Store)
            .Read(hdrHandle)
            .Shader(bloomBrightInterface)
            .Handle;
        bloomBlurHPassHandle = graph.GraphicsPass("bloom-blurH")
            .Target(bloomBlurHHandle, LoadOp.Clear, StoreOp.Store)
            .Read(bloomBrightHandle)
            .Shader(bloomBlurInterface)
            .Handle;
        bloomBlurVPassHandle = graph.GraphicsPass("bloom-blurV")
            .Target(bloomBlurVHandle, LoadOp.Clear, StoreOp.Store)
            .Read(bloomBlurHHandle)
            .Shader(bloomBlurInterface)
            .Handle;
        graph.Compile();

        // --- Pipelines (after Compile; target the pass surfaces) ---------
        PipelineHandle Pipe(string vert, string frag, ShaderInterface iface, string name,
            VertexLayout layout, DepthState depth, BlendState[] blends, PassHandle pass)
        {
            var program = vk.CreateShaderProgramFromSpv(
                File.ReadAllBytes(Path.Combine(shaderDir, vert)),
                File.ReadAllBytes(Path.Combine(shaderDir, frag)),
                iface, name);
            return vk.CreatePipeline(new PipelineDescription(
                program, layout, PrimitiveTopology.Triangles, depth,
                RasterizerState.NoCulling, blends,
                RenderTarget: graph.GetPassSurface(pass)), name);
        }

        depthOnlyPipeline = Pipe("depth_only.vert.spv", "depth_only.frag.spv", depthInterface, "depth_only",
            VertexPosition3NormalTexture.Layout, DepthState.LessEqualWrite, Array.Empty<BlendState>(), depthPrepassHandle);
        opaquePipeline = Pipe("opaque.vert.spv", "opaque.frag.spv", opaqueInterface, "opaque",
            VertexPosition3NormalTexture.Layout, DepthState.LessEqualWrite, new[] { BlendState.Disabled }, scenePassHandle);

        // Soft particle pipelines: two blend states over one soft shader, both via
        // GetOrCreatePipeline. Additive is shared by sparks/explosion/vortex; premultiplied-
        // alpha draws the smoke (the shader outputs premultiplied colour).
        var softProgram = vk.CreateShaderProgramFromSpv(
            File.ReadAllBytes(Path.Combine(shaderDir, "particle_soft.vert.spv")),
            File.ReadAllBytes(Path.Combine(shaderDir, "particle_soft.frag.spv")),
            softInterface, "particle_soft");
        PipelineDescription SoftDesc(BlendState blend) => new(
            softProgram, ParticleBatch.VertexLayoutDescription, PrimitiveTopology.Triangles,
            DepthState.Disabled, RasterizerState.NoCulling, new[] { blend },
            RenderTarget: graph.GetPassSurface(scenePassHandle));
        additivePipeline = vk.GetOrCreatePipeline(SoftDesc(BlendState.Additive), "particle.soft.additive");
        premultPipeline = vk.GetOrCreatePipeline(SoftDesc(BlendState.PremultipliedAlpha), "particle.soft.premult");

        bloomBrightPipeline = Pipe("present.vert.spv", "bloom_bright.frag.spv", bloomBrightInterface, "bloom_bright",
            VertexPosition3NormalTexture.Layout, DepthState.Disabled, new[] { BlendState.Disabled }, bloomBrightPassHandle);
        bloomBlurPipeline = Pipe("present.vert.spv", "bloom_blur.frag.spv", bloomBlurInterface, "bloom_blur",
            VertexPosition3NormalTexture.Layout, DepthState.Disabled, new[] { BlendState.Disabled }, bloomBlurHPassHandle);

        // Present targets the swapchain (default render target).
        var presentProgram = vk.CreateShaderProgramFromSpv(
            File.ReadAllBytes(Path.Combine(shaderDir, "present.vert.spv")),
            File.ReadAllBytes(Path.Combine(shaderDir, "present.frag.spv")),
            presentInterface, "present");
        presentPipeline = vk.CreatePipeline(new PipelineDescription(
            presentProgram, VertexPosition3NormalTexture.Layout, PrimitiveTopology.Triangles,
            DepthState.Disabled, RasterizerState.NoCulling, BlendState.Disabled), "present");

        // --- Geometry + batches ------------------------------------------
        BuildOpaqueGeometry();
        var dummy = new VertexPosition3NormalTexture[]
        {
            new(new GraphicsVector3(0, 0, 0), new GraphicsVector3(0, 0, 1), new GraphicsVector2(0, 0)),
            new(new GraphicsVector3(0, 0, 0), new GraphicsVector3(0, 0, 1), new GraphicsVector2(0, 0)),
            new(new GraphicsVector3(0, 0, 0), new GraphicsVector3(0, 0, 1), new GraphicsVector2(0, 0)),
        };
        fullscreenVB = vk.CreateVertexBuffer(VertexPosition3NormalTexture.CreateBufferData(dummy), "fullscreen.vb");
        fullscreenIB = vk.CreateIndexBuffer(new ushort[] { 0, 1, 2 }, name: "fullscreen.ib");

        sparks = new ParticleBatch(vk, 8192, "sparks");
        explosion = new ParticleBatch(vk, 6144, "explosion");
        vortex = new ParticleBatch(vk, 6144, "vortex");
        smoke = new ParticleBatch(vk, 2048, "smoke");
        effects = new[]
        {
            new Effect(smoke, sort: true, additive: false) { Pipeline = premultPipeline },
            new Effect(sparks, sort: false, additive: true) { Pipeline = additivePipeline },
            new Effect(explosion, sort: false, additive: true) { Pipeline = additivePipeline },
            new Effect(vortex, sort: false, additive: true) { Pipeline = additivePipeline },
        };

        tunables = new ObjectTunables(fx);
        aspect = host.LogicalSize.Width / (float)host.LogicalSize.Height;
        BuildCamera();
    }

    // Ground plane + blocks placed so each effect intersects a surface: the explosion
    // bursts into a wide block, the vortex swirls around a tall pillar, the fountain/smoke
    // fade against a backdrop wall + the gridded floor.
    private void BuildOpaqueGeometry()
    {
        const float g = 22f;
        var groundVerts = new VertexPosition3NormalTexture[]
        {
            new(new GraphicsVector3(-g, 0, -g), new GraphicsVector3(0, 1, 0), new GraphicsVector2(0, 0)),
            new(new GraphicsVector3( g, 0, -g), new GraphicsVector3(0, 1, 0), new GraphicsVector2(1, 0)),
            new(new GraphicsVector3( g, 0,  g), new GraphicsVector3(0, 1, 0), new GraphicsVector2(1, 1)),
            new(new GraphicsVector3(-g, 0,  g), new GraphicsVector3(0, 1, 0), new GraphicsVector2(0, 1)),
        };
        var groundVB = vk.CreateVertexBuffer(VertexPosition3NormalTexture.CreateBufferData(groundVerts), "ground.vb");
        var groundIB = vk.CreateIndexBuffer(new ushort[] { 0, 1, 2, 0, 2, 3 }, name: "ground.ib");
        opaques.Add(new OpaqueMesh(groundVB, groundIB, 6, Matrix4x4.Identity, new Vector4(0.06f, 0.07f, 0.10f, 1f)));

        var cubeVB = vk.CreateVertexBuffer(VertexPosition3NormalTexture.CreateBufferData(Cube.Vertices), "cube.vb");
        var cubeIB = vk.CreateIndexBuffer(Cube.Indices, name: "cube.ib");
        Matrix4x4 Block(float sx, float sy, float sz, Vector3 at) =>
            Matrix4x4.CreateScale(sx, sy, sz) * Matrix4x4.CreateTranslation(at);

        opaques.Add(new OpaqueMesh(cubeVB, cubeIB, Cube.Indices.Length,
            Block(2.2f, 2.0f, 2.2f, new Vector3(-4.5f, 1.0f, 0f)), new Vector4(0.30f, 0.27f, 0.24f, 1f)));   // explosion block
        opaques.Add(new OpaqueMesh(cubeVB, cubeIB, Cube.Indices.Length,
            Block(0.9f, 4.0f, 0.9f, new Vector3(4.5f, 2.0f, 0f)), new Vector4(0.24f, 0.27f, 0.33f, 1f)));    // vortex pillar
        opaques.Add(new OpaqueMesh(cubeVB, cubeIB, Cube.Indices.Length,
            Block(3.6f, 2.4f, 0.7f, new Vector3(0f, 1.2f, -2.2f)), new Vector4(0.22f, 0.24f, 0.28f, 1f)));   // fountain backdrop
        opaques.Add(new OpaqueMesh(cubeVB, cubeIB, Cube.Indices.Length,
            Block(0.8f, 1.0f, 0.8f, new Vector3(-1.8f, 0.5f, 2.0f)), new Vector4(0.26f, 0.26f, 0.30f, 1f))); // foreground step
        opaques.Add(new OpaqueMesh(cubeVB, cubeIB, Cube.Indices.Length,
            Block(1.0f, 0.6f, 1.0f, new Vector3(2.2f, 0.3f, 2.4f)), new Vector4(0.28f, 0.26f, 0.24f, 1f)));  // foreground step
    }

    public void OnResize(int width, int height)
    {
        if (height > 0) aspect = width / (float)height;
    }

    public void OnUpdate(Time time)
    {
        var dt = MathF.Min((float)time.Delta, 1f / 30f);   // clamp the first big frame
        if (autoOrbit) camYaw += dt * 0.25f;
        BuildCamera();

        EmitFountain();
        EmitSmoke();
        EmitVortex(dt);
        explosionTimer += dt;
        if (explosionTimer >= ExplosionInterval)
        {
            explosionTimer -= ExplosionInterval;
            EmitExplosion();
        }

        sparks.Update(dt, new Vector3(0f, -9.0f, 0f), drag: 0.4f);
        explosion.Update(dt, new Vector3(0f, -7.0f, 0f), drag: 1.2f);
        vortex.Update(dt, new Vector3(0f, 0.6f, 0f), drag: 1.6f);
        smoke.Update(dt, new Vector3(0f, 0.5f, 0f), drag: 0.3f);
    }

    // Warm spark cone: white-hot core (pushed well above 1.0 so it blooms) cooling to a
    // dim ember, shrinking as it cools.
    private void EmitFountain()
    {
        for (var i = 0; i < 48; i++)
        {
            var vel = new Vector3(Spread() * 1.7f, 5.5f + (float)rng.NextDouble() * 3.0f, Spread() * 1.7f);
            var heat = 0.7f + (float)rng.NextDouble() * 0.3f;
            sparks.Emit(
                position: FountainAt + new Vector3(Spread() * 0.12f, 0.2f, Spread() * 0.12f),
                velocity: vel,
                startColor: new Vector4(5.0f, 3.4f * heat, 1.4f * heat, 1f),
                endColor: new Vector4(1.4f, 0.20f, 0.04f, 0f),
                startSize: 0.10f, endSize: 0.02f,
                life: 1.1f + (float)rng.NextDouble() * 0.8f);
        }
    }

    // Translucent smoke that swells and thins as it rises. Sub-1.0 (premultiplied-alpha)
    // so it reads as soft grey smoke, not a bloom source.
    private void EmitSmoke()
    {
        for (var i = 0; i < 8; i++)
        {
            smoke.Emit(
                position: FountainAt + new Vector3(Spread() * 0.3f, 0.7f, Spread() * 0.3f),
                velocity: new Vector3(Spread() * 0.4f, 1.1f + (float)rng.NextDouble(), Spread() * 0.4f),
                startColor: new Vector4(0.75f, 0.76f, 0.82f, 0.40f),
                endColor: new Vector4(0.40f, 0.41f, 0.46f, 0f),
                startSize: 0.4f, endSize: 1.8f,
                life: 2.4f + (float)rng.NextDouble() * 1.4f);
        }
    }

    // A radial burst: white flash (strong bloom pop) cooling through orange to red.
    private void EmitExplosion()
    {
        for (var i = 0; i < 480; i++)
        {
            var z = (float)rng.NextDouble() * 2f - 1f;
            var a = (float)rng.NextDouble() * MathF.PI * 2f;
            var r = MathF.Sqrt(1f - z * z);
            var dir = new Vector3(r * MathF.Cos(a), z, r * MathF.Sin(a));
            var speed = 5.0f + (float)rng.NextDouble() * 6.0f;
            explosion.Emit(
                position: ExplosionAt,
                velocity: dir * speed,
                startColor: new Vector4(6.0f, 4.2f, 2.6f, 1f),
                endColor: new Vector4(1.6f, 0.25f, 0.05f, 0f),
                startSize: 0.18f, endSize: 0.03f,
                life: 0.7f + (float)rng.NextDouble() * 0.7f);
        }
    }

    // A ring launched tangentially around the Y axis; drag curls it into a swirl. Hue
    // cycles cyan → magenta over life (boosted for bloom).
    private void EmitVortex(float dt)
    {
        vortexAngle += dt * 9f;
        for (var i = 0; i < 36; i++)
        {
            var a = vortexAngle + i * (MathF.PI * 2f / 36f);
            var radial = new Vector3(MathF.Cos(a), 0f, MathF.Sin(a));
            var tangent = new Vector3(-MathF.Sin(a), 0f, MathF.Cos(a));
            var spawn = VortexAt + radial * 1.5f + new Vector3(0f, (float)rng.NextDouble() * 0.4f, 0f);
            var vel = tangent * 4.8f + new Vector3(0f, 2.0f + (float)rng.NextDouble(), 0f) - radial * 0.6f;
            vortex.Emit(
                position: spawn,
                velocity: vel,
                startColor: new Vector4(0.5f, 2.6f, 4.0f, 1f),
                endColor: new Vector4(3.2f, 0.5f, 3.4f, 0f),
                startSize: 0.11f, endSize: 0.02f,
                life: 1.4f + (float)rng.NextDouble() * 0.8f);
        }
    }

    private float Spread() => ((float)rng.NextDouble() - 0.5f) * 2f;

    private void BuildCamera()
    {
        camPitch = Math.Clamp(camPitch, -0.2f, 1.45f);
        camRadius = Math.Clamp(camRadius, 4f, 30f);
        var cosP = MathF.Cos(camPitch);
        camPos = CamTarget + new Vector3(
            camRadius * cosP * MathF.Sin(camYaw),
            camRadius * MathF.Sin(camPitch),
            camRadius * cosP * MathF.Cos(camYaw));
        var view = Matrix4x4.CreateLookAt(camPos, CamTarget, Vector3.UnitY);
        var proj = GraphicsMatrices.CreatePerspectiveVulkan(MathF.PI / 3f, aspect, NearPlane, FarPlane);
        viewProj = view * proj;

        var forward = Vector3.Normalize(CamTarget - camPos);
        camRight = Vector3.Normalize(Vector3.Cross(forward, Vector3.UnitY));
        camUp = Vector3.Cross(camRight, forward);
    }

    public void OnRender(Time time, RenderFrameContext frame, RenderCommandList commandList)
    {
        frameCount++;

        // 1) Depth pre-pass: opaque depth → sampleable scene-depth.
        graph.Pass(depthPrepassHandle, scope =>
        {
            foreach (var m in opaques)
            {
                var model = m.Model;
                MemoryMarshal.Write(depthPush.AsSpan(0, 64), in model);
                MemoryMarshal.Write(depthPush.AsSpan(64, 64), in viewProj);
                scope.DrawIndexed(
                    m.VB, m.IB, depthOnlyPipeline, m.IndexCount,
                    Array.Empty<ShaderUniform>(), Array.Empty<ShaderTextureBinding>(), depthPush);
            }
        });

        // 2) Scene: opaque backdrop (depth-tested) then soft particles into HDR.
        var sceneDepthTex = graph.GetDepthTexture(sceneDepthHandle);
        particleTextures[0] = new ShaderTextureBinding("uSceneDepth", sceneDepthTex, Slot: 0);
        foreach (var e in effects) PackEffectPush(e);
        graph.Pass(scenePassHandle, scope =>
        {
            foreach (var m in opaques)
            {
                var model = m.Model;
                MemoryMarshal.Write(opaquePush.AsSpan(0, 64), in model);
                MemoryMarshal.Write(opaquePush.AsSpan(64, 64), in viewProj);
                var albedo = m.Albedo;
                MemoryMarshal.Write(opaquePush.AsSpan(128, 16), in albedo);
                scope.DrawIndexed(
                    m.VB, m.IB, opaquePipeline, m.IndexCount,
                    Array.Empty<ShaderUniform>(), Array.Empty<ShaderTextureBinding>(), opaquePush);
            }
            // Premultiplied-alpha smoke first (depth-sorted), additive effects over it.
            foreach (var e in effects)
                e.Batch.Draw(scope, e.Pipeline, camRight, camUp, camPos, e.Sort, e.Push, particleTextures);
        }, clearColor: new GraphicsColor(0.015f, 0.02f, 0.035f, 1f));

        // 3) Bloom chain: bright(hdr) → blurH → blurV at quarter res.
        var threshold = fx.Threshold;
        MemoryMarshal.Write(bloomBrightPush.AsSpan(0, 4), in threshold);
        var bloomW = MathF.Max(1f, frame.Width * BloomScale);
        var bloomH = MathF.Max(1f, frame.Height * BloomScale);
        RecordFullscreen(bloomBrightPassHandle, bloomBrightPipeline,
            new ShaderTextureBinding("uHdr", graph.GetColorTexture(hdrHandle), Slot: 0), bloomBrightPush);
        RecordFullscreen(bloomBlurHPassHandle, bloomBlurPipeline,
            new ShaderTextureBinding("uSrc", graph.GetColorTexture(bloomBrightHandle), Slot: 0),
            Vec2Bytes(1f / bloomW, 0f));
        RecordFullscreen(bloomBlurVPassHandle, bloomBlurPipeline,
            new ShaderTextureBinding("uSrc", graph.GetColorTexture(bloomBlurHHandle), Slot: 0),
            Vec2Bytes(0f, 1f / bloomH));

        graph.Execute(commandList);

        // 4) Present: composite hdr + bloom, exposure + ACES tonemap → swapchain.
        var hdrTex = graph.GetColorTexture(hdrHandle);
        var bloomTex = graph.GetColorTexture(bloomBlurVHandle);
        var exposure = fx.Exposure;
        var bloomI = fx.Bloom;
        MemoryMarshal.Write(presentPush.AsSpan(0, 4), in exposure);
        MemoryMarshal.Write(presentPush.AsSpan(4, 4), in bloomI);
        commandList.Pass(
            "present",
            new RenderPassDescription(
                Target: RenderSurfaceHandle.Default,
                ClearColors: new GraphicsColor?[] { new GraphicsColor(0, 0, 0, 1) },
                ClearDepth: true),
            pass =>
            {
                pass.DrawIndexed(
                    vertexBuffer: fullscreenVB, indexBuffer: fullscreenIB,
                    pipeline: presentPipeline, indexCount: 3,
                    uniforms: Array.Empty<ShaderUniform>(),
                    textures: new[]
                    {
                        new ShaderTextureBinding("uHdr", hdrTex, Slot: 0),
                        new ShaderTextureBinding("uBloom", bloomTex, Slot: 1),
                    },
                    pushConstants: presentPush);
            });

        if (exitAfterFrames > 0 && frameCount >= exitAfterFrames)
        {
            host.RequestClose();
        }
    }

    // Pack one effect's soft push: viewProj (0..64), near (64), far (68), fadeDist (72),
    // sharpness (76). Each effect owns its buffer so the per-draw bytes don't alias.
    private void PackEffectPush(Effect e)
    {
        var near = NearPlane;
        var far = FarPlane;
        var fade = fx.FadeDist;
        var sharp = e.Additive ? fx.SparkSharpness : fx.SmokeSharpness;
        MemoryMarshal.Write(e.Push.AsSpan(0, 64), in viewProj);
        MemoryMarshal.Write(e.Push.AsSpan(64, 4), in near);
        MemoryMarshal.Write(e.Push.AsSpan(68, 4), in far);
        MemoryMarshal.Write(e.Push.AsSpan(72, 4), in fade);
        MemoryMarshal.Write(e.Push.AsSpan(76, 4), in sharp);
    }

    private void RecordFullscreen(PassHandle pass, PipelineHandle pipeline,
        ShaderTextureBinding input, byte[]? push)
    {
        graph.Pass(pass, scope =>
        {
            if (push is null)
            {
                scope.DrawIndexed(fullscreenVB, fullscreenIB, pipeline, indexCount: 3,
                    Array.Empty<ShaderUniform>(), new[] { input });
            }
            else
            {
                scope.DrawIndexed(fullscreenVB, fullscreenIB, pipeline, indexCount: 3,
                    Array.Empty<ShaderUniform>(), new[] { input }, push);
            }
        });
    }

    private static byte[] Vec2Bytes(float x, float y)
    {
        var b = new byte[8];
        MemoryMarshal.Write(b.AsSpan(0, 4), in x);
        MemoryMarshal.Write(b.AsSpan(4, 4), in y);
        return b;
    }

    // --- Diagnostics overlay (` toggles) -----------------------------------
    public void Debug(DebugContext debug)
    {
        // The overlay only renders when State.Enabled (the backtick key toggles
        // State.ShowOverlay, not this). Gate it on --debug; when off, emit nothing.
        debug.State.Enabled = overlayEnabled;
        if (!overlayEnabled) return;

        debug.Draw.ViewProjection = viewProj;
        tunables.BuildControls(debug);

        using (debug.Scope("particles"))
        {
            debug.Values.Value("live total", sparks.Count + explosion.Count + vortex.Count + smoke.Count);
            debug.Values.Value("sparks", sparks.Count);
            debug.Values.Value("explosion", explosion.Count);
            debug.Values.Value("vortex", vortex.Count);
            debug.Values.Value("smoke", smoke.Count);
        }
        using (debug.Scope("camera"))
        {
            debug.Values.Value("auto-orbit", autoOrbit);
            debug.Values.Value("radius", camRadius);
        }

        if (fx.ShowGizmos)
        {
            var c = new GraphicsColor(0.95f, 0.8f, 0.25f, 0.9f);
            debug.Draw.Sphere("fountain", FountainAt + new Vector3(0, 0.2f, 0), 0.4f, c);
            debug.Draw.Sphere("explosion", ExplosionAt, 0.5f, c);
            debug.Draw.Sphere("vortex", VortexAt, 1.5f, c);
        }
    }

    // --- Input -------------------------------------------------------------
    public void OnKeyDown(Key key)
    {
        switch (key)
        {
            case Key.Escape: host.RequestClose(); break;
            case Key.Space: autoOrbit = !autoOrbit; break;
            case Key.R: camYaw = 0.7f; camPitch = 0.30f; camRadius = 10.5f; autoOrbit = true; break;
            case Key.Left: case Key.A: camYaw -= 0.08f; autoOrbit = false; break;
            case Key.Right: case Key.D: camYaw += 0.08f; autoOrbit = false; break;
            case Key.Up: case Key.W: camPitch += 0.06f; autoOrbit = false; break;
            case Key.Down: case Key.S: camPitch -= 0.06f; autoOrbit = false; break;
            case Key.Q: camRadius += 0.8f; break;
            case Key.E: camRadius -= 0.8f; break;
        }
    }

    public void OnMouseDown(MouseButton button)
    {
        if (button == MouseButton.Left) { dragging = true; autoOrbit = false; }
    }

    public void OnMouseUp(MouseButton button)
    {
        if (button == MouseButton.Left) dragging = false;
    }

    public void OnMouseMove(float x, float y, float deltaX, float deltaY)
    {
        if (!dragging) return;
        camYaw += deltaX * 0.006f;
        camPitch += deltaY * 0.006f;
    }

    public void OnMouseWheel(float offsetX, float offsetY)
    {
        camRadius -= offsetY * 0.9f;
    }

    // Window.Dispose disposes the loop after WaitIdle and before device teardown — the
    // safe point to free the graph's render passes + offscreen images (not in the
    // device's auto-freed tables). Batch buffers + pipelines live in device tables.
    public void Dispose() => graph?.Dispose();
}

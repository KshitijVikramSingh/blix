using System.Numerics;
using Blix;
using Blix.Core;
using Blix.Graphics;
using Blix.Graphics.Vulkan;
using Blix.Render;
using Blix.Runtime.Silk;

namespace Blix.Demos.VulkanParticles;

// ParticleBatch showcase + proof gate: three CPU-simulated effects — a spark
// FOUNTAIN with drifting SMOKE, a periodic EXPLOSION burst, and a swirling VORTEX —
// all riding the per-frame transient vertex arena (each expands its live particles
// into billboards and uploads one arena slice per frame; no persistent VB).
//
// Two pipelines over ONE shader, differing only by blend state, both via
// GetOrCreatePipeline: the additive pipeline is SHARED by sparks + explosion +
// vortex (three batches, one cached handle — proving pipeline reuse), the alpha
// pipeline draws the depth-sorted smoke. Colour + size ramp over each particle's
// life (sparks cool white-hot → ember, smoke swells and thins).
//
// Auto-exits after --frames N (> frames-in-flight). Correctness: run under
// BLIX_VK_VALIDATE=1, grep [vk-ERR]/[vk-WARN] (screenshots don't work here).
public static class Program
{
    public static void Main(string[] args)
    {
        var exitAfterFrames = 0;
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == "--frames" && int.TryParse(args[i + 1], out var n)) exitAfterFrames = n;
        }

        var loop = new ParticlesLoop(exitAfterFrames);
        using var window = new Window(loop, new WindowOptions("Blix — Particles (fountain · explosion · vortex)", 1280, 720));
        window.Run();
    }
}

internal sealed class ParticlesLoop : IGameLoop, IInputHandler
{
    // Effect anchor points, spread along X so all three read in one orbit.
    private static readonly Vector3 FountainAt = new(0f, 0f, 0f);
    private static readonly Vector3 ExplosionAt = new(-7f, 2.5f, 0f);
    private static readonly Vector3 VortexAt = new(7f, 0f, 0f);

    private const float ExplosionInterval = 2.2f;

    private readonly int exitAfterFrames;
    private readonly Random rng = new(12345);   // deterministic emission
    private VulkanGraphicsDevice vk = null!;
    private IRenderHost host = null!;

    // Three additive batches (sparks, explosion, vortex) + one alpha batch (smoke).
    private ParticleBatch sparks = null!;
    private ParticleBatch explosion = null!;
    private ParticleBatch vortex = null!;
    private ParticleBatch smoke = null!;
    private PipelineHandle additivePipeline;
    private PipelineHandle alphaPipeline;

    private Matrix4x4 viewProj;
    private Vector3 camRight, camUp, camPos;
    private float aspect = 16f / 9f;
    private float explosionTimer = ExplosionInterval;   // burst on the very first update
    private float vortexAngle;
    private int frameCount;

    public ParticlesLoop(int exitAfterFrames) => this.exitAfterFrames = exitAfterFrames;

    public void OnLoad(IRenderHost host, IGraphicsDevice graphicsDevice)
    {
        this.host = host;
        vk = (VulkanGraphicsDevice)graphicsDevice;

        // Descriptor-less: vertices carry colour, the only binding is a view-proj
        // push constant — so particles belong in the arena, not MaterialBindings.
        var iface = new ShaderInterface(
            Slots: Array.Empty<DescriptorSetSlot>(),
            PushConstants: new[] { new PushConstantRange(ShaderStages.Vertex, 0, 64) });
        var shaderDir = Path.Combine(AppContext.BaseDirectory, "Shaders");
        var shader = vk.CreateShaderProgramFromSpv(
            File.ReadAllBytes(Path.Combine(shaderDir, "particle.vert.spv")),
            File.ReadAllBytes(Path.Combine(shaderDir, "particle.frag.spv")),
            iface, "particle");

        // Two pipelines over one shader, differing ONLY by blend state, both via
        // GetOrCreatePipeline (two distinct PipelineKey entries). The additive handle
        // is reused by three batches — same description → same cached handle.
        PipelineDescription Desc(BlendState blend) => new(
            shader, ParticleBatch.VertexLayoutDescription, PrimitiveTopology.Triangles,
            DepthState.Disabled, RasterizerState.NoCulling, new[] { blend });
        additivePipeline = vk.GetOrCreatePipeline(Desc(BlendState.Additive), "particle.additive");
        alphaPipeline = vk.GetOrCreatePipeline(Desc(BlendState.AlphaBlend), "particle.alpha");

        sparks = new ParticleBatch(vk, 4096, "sparks");
        explosion = new ParticleBatch(vk, 4096, "explosion");
        vortex = new ParticleBatch(vk, 4096, "vortex");
        smoke = new ParticleBatch(vk, 2048, "smoke");

        aspect = host.LogicalSize.Width / (float)host.LogicalSize.Height;
        BuildCamera(0f);
    }

    public void OnResize(int width, int height)
    {
        if (height > 0) aspect = width / (float)height;
    }

    public void OnUpdate(Time time)
    {
        BuildCamera((float)time.Total);
        var dt = MathF.Min((float)time.Delta, 1f / 30f);   // clamp the first big frame

        EmitFountain();
        EmitSmoke();
        EmitVortex(dt);
        explosionTimer += dt;
        if (explosionTimer >= ExplosionInterval)
        {
            explosionTimer -= ExplosionInterval;
            EmitExplosion();
        }

        // Each effect integrates with its own forces.
        sparks.Update(dt, new Vector3(0f, -9.0f, 0f), drag: 0.4f);    // gravity-pulled, slight air drag
        explosion.Update(dt, new Vector3(0f, -7.0f, 0f), drag: 1.2f); // burst slows fast
        vortex.Update(dt, new Vector3(0f, 0.6f, 0f), drag: 1.6f);     // buoyant + heavy drag → curls inward
        smoke.Update(dt, new Vector3(0f, 0.5f, 0f), drag: 0.3f);      // buoyant, lazy
    }

    // Warm spark cone from the fountain base: white-hot core cooling to a dim ember,
    // shrinking as it cools.
    private void EmitFountain()
    {
        for (var i = 0; i < 36; i++)
        {
            var vel = new Vector3(Spread() * 1.6f, 5.0f + (float)rng.NextDouble() * 2.5f, Spread() * 1.6f);
            var heat = 0.7f + (float)rng.NextDouble() * 0.3f;
            sparks.Emit(
                position: FountainAt + new Vector3(Spread() * 0.15f, 0.2f, Spread() * 0.15f),
                velocity: vel,
                startColor: new Vector4(1f, 0.95f * heat, 0.55f * heat, 1f),
                endColor: new Vector4(0.85f, 0.18f, 0.04f, 0f),
                startSize: 0.12f, endSize: 0.03f,
                life: 1.1f + (float)rng.NextDouble() * 0.8f);
        }
    }

    // Translucent grey smoke that swells and thins as it rises off the fountain.
    private void EmitSmoke()
    {
        for (var i = 0; i < 10; i++)
        {
            smoke.Emit(
                position: FountainAt + new Vector3(Spread() * 0.3f, 0.6f, Spread() * 0.3f),
                velocity: new Vector3(Spread() * 0.4f, 1.0f + (float)rng.NextDouble(), Spread() * 0.4f),
                startColor: new Vector4(0.55f, 0.55f, 0.6f, 0.32f),
                endColor: new Vector4(0.28f, 0.28f, 0.32f, 0f),
                startSize: 0.45f, endSize: 1.7f,
                life: 2.6f + (float)rng.NextDouble() * 1.4f);
        }
    }

    // A radial burst in every direction — white flash cooling through orange to red.
    private void EmitExplosion()
    {
        for (var i = 0; i < 360; i++)
        {
            // Roughly uniform direction on the sphere.
            var z = (float)rng.NextDouble() * 2f - 1f;
            var a = (float)rng.NextDouble() * MathF.PI * 2f;
            var r = MathF.Sqrt(1f - z * z);
            var dir = new Vector3(r * MathF.Cos(a), z, r * MathF.Sin(a));
            var speed = 4.5f + (float)rng.NextDouble() * 5.5f;
            explosion.Emit(
                position: ExplosionAt,
                velocity: dir * speed,
                startColor: new Vector4(1f, 0.95f, 0.8f, 1f),
                endColor: new Vector4(0.9f, 0.2f, 0.04f, 0f),
                startSize: 0.22f, endSize: 0.04f,
                life: 0.7f + (float)rng.NextDouble() * 0.7f);
        }
    }

    // A ring of particles launched tangentially around the Y axis; drag curls them
    // inward into a swirl. Hue cycles cyan → magenta over life.
    private void EmitVortex(float dt)
    {
        vortexAngle += dt * 9f;   // spin the emission point around the ring
        for (var i = 0; i < 28; i++)
        {
            var a = vortexAngle + i * (MathF.PI * 2f / 28f);
            var radial = new Vector3(MathF.Cos(a), 0f, MathF.Sin(a));
            var tangent = new Vector3(-MathF.Sin(a), 0f, MathF.Cos(a));
            var spawn = VortexAt + radial * 1.6f + new Vector3(0f, (float)rng.NextDouble() * 0.4f, 0f);
            var vel = tangent * 4.5f + new Vector3(0f, 1.8f + (float)rng.NextDouble(), 0f) - radial * 0.6f;
            vortex.Emit(
                position: spawn,
                velocity: vel,
                startColor: new Vector4(0.2f, 0.8f, 1f, 1f),
                endColor: new Vector4(0.9f, 0.2f, 1f, 0f),
                startSize: 0.13f, endSize: 0.03f,
                life: 1.4f + (float)rng.NextDouble() * 0.8f);
        }
    }

    private float Spread() => ((float)rng.NextDouble() - 0.5f) * 2f;

    private void BuildCamera(float t)
    {
        var camX = MathF.Sin(t * 0.2f) * 10f;
        var camZ = 16f + MathF.Cos(t * 0.2f) * 3f;
        camPos = new Vector3(camX, 5.5f, camZ);
        var target = new Vector3(0f, 2.5f, 0f);
        var view = Matrix4x4.CreateLookAt(camPos, target, Vector3.UnitY);
        var proj = GraphicsMatrices.CreatePerspectiveVulkan(MathF.PI / 3f, aspect, 0.1f, 300f);
        viewProj = view * proj;

        var forward = Vector3.Normalize(target - camPos);
        camRight = Vector3.Normalize(Vector3.Cross(forward, Vector3.UnitY));
        camUp = Vector3.Cross(camRight, forward);
    }

    public void OnRender(Time time, RenderFrameContext frame, RenderCommandList commandList)
    {
        frameCount++;
        commandList.Pass(
            "particles",
            new RenderPassDescription(
                Target: RenderSurfaceHandle.Default,
                ClearColors: new GraphicsColor?[] { new GraphicsColor(0.02f, 0.02f, 0.04f, 1f) },
                ClearDepth: true),
            pass =>
            {
                // Alpha smoke first (depth-sorted back-to-front), additive effects
                // over it (order-independent). Four arena slices + two cached
                // pipelines (additive shared by three batches) in one frame.
                smoke.Draw(pass, alphaPipeline, viewProj, camRight, camUp, camPos, sortByDepth: true);
                sparks.Draw(pass, additivePipeline, viewProj, camRight, camUp, camPos, sortByDepth: false);
                explosion.Draw(pass, additivePipeline, viewProj, camRight, camUp, camPos, sortByDepth: false);
                vortex.Draw(pass, additivePipeline, viewProj, camRight, camUp, camPos, sortByDepth: false);
            });

        if (exitAfterFrames > 0 && frameCount >= exitAfterFrames)
        {
            host.RequestClose();
        }
    }

    // No OnUnload disposal — RequestClose fires Closing before the final recorded
    // draw executes; device.Dispose (waits idle + frees every table) does teardown.

    public void OnKeyDown(Key key)
    {
        if (key == Key.Escape) host.RequestClose();
    }
}

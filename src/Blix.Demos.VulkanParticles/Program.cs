using System.Numerics;
using System.Runtime.InteropServices;
using Blix;
using Blix.Core;
using Blix.Graphics;
using Blix.Graphics.Vulkan;
using Blix.Render;
using Blix.Runtime.Silk;

namespace Blix.Demos.VulkanParticles;

// ParticleBatch proof gate + showcase: a CPU-simulated particle fountain that is
// the first NEW consumer of the per-frame transient vertex arena. Every frame each
// system expands its live particles into camera-facing billboards and uploads them
// as one arena slice (no persistent vertex buffer). Two systems exercise two
// pipelines built over ONE shader, differing only by blend state — additive sparks
// and alpha-blended smoke — both obtained via GetOrCreatePipeline, so they prove
// the pipeline cache distinguishes blend state (two distinct cache entries).
//
// Auto-exits after --frames N (> frames-in-flight, so every arena ring slot is
// written/read). Correctness: run under BLIX_VK_VALIDATE=1, grep [vk-ERR]/[vk-WARN].
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
        using var window = new Window(loop, new WindowOptions("Blix — Particle Fountain", 1280, 720));
        window.Run();
    }
}

internal sealed class ParticlesLoop : IGameLoop, IInputHandler
{
    private const int MaxSparks = 4096;
    private const int MaxSmoke = 2048;

    private readonly int exitAfterFrames;
    private readonly Random rng = new(12345);   // deterministic emission
    private VulkanGraphicsDevice vk = null!;
    private IRenderHost host = null!;

    private ParticleBatch sparks = null!;     // additive
    private ParticleBatch smoke = null!;      // alpha
    private PipelineHandle additivePipeline;
    private PipelineHandle alphaPipeline;

    private Matrix4x4 viewProj;
    private Vector3 camRight, camUp, camPos;
    private float aspect = 16f / 9f;
    private int frameCount;

    public ParticlesLoop(int exitAfterFrames) => this.exitAfterFrames = exitAfterFrames;

    public void OnLoad(IRenderHost host, IGraphicsDevice graphicsDevice)
    {
        this.host = host;
        vk = (VulkanGraphicsDevice)graphicsDevice;

        // One shader; the only binding is a view-projection push constant — the
        // particle vertices carry their own colour, so this is descriptor-less and
        // belongs in the arena.
        var iface = new ShaderInterface(
            Slots: Array.Empty<DescriptorSetSlot>(),
            PushConstants: new[] { new PushConstantRange(ShaderStages.Vertex, 0, 64) });
        var shaderDir = Path.Combine(AppContext.BaseDirectory, "Shaders");
        var shader = vk.CreateShaderProgramFromSpv(
            File.ReadAllBytes(Path.Combine(shaderDir, "particle.vert.spv")),
            File.ReadAllBytes(Path.Combine(shaderDir, "particle.frag.spv")),
            iface, "particle");

        // Two pipelines over the one shader, differing ONLY by blend state. Built
        // through GetOrCreatePipeline so they intern as two distinct cache entries
        // (the PipelineKey compares ColorBlends by value). Depth disabled — particles
        // neither test nor write depth.
        PipelineDescription Desc(BlendState blend) => new(
            shader, ParticleBatch.VertexLayoutDescription, PrimitiveTopology.Triangles,
            DepthState.Disabled, RasterizerState.NoCulling, new[] { blend });
        additivePipeline = vk.GetOrCreatePipeline(Desc(BlendState.Additive), "particle.additive");
        alphaPipeline = vk.GetOrCreatePipeline(Desc(BlendState.AlphaBlend), "particle.alpha");

        sparks = new ParticleBatch(vk, MaxSparks, "sparks");
        smoke = new ParticleBatch(vk, MaxSmoke, "smoke");

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
        var dt = MathF.Min((float)time.Delta, 1f / 30f);   // clamp first big frame

        // Emit sparks: a burst from the origin with an upward cone + spread.
        for (var i = 0; i < 40; i++)
        {
            var dir = new Vector3(Spread(), 3.5f + (float)rng.NextDouble() * 2.5f, Spread());
            var heat = 0.6f + (float)rng.NextDouble() * 0.4f;
            sparks.Emit(
                position: new Vector3(Spread() * 0.2f, 0.2f, Spread() * 0.2f),
                velocity: dir * 3.5f,
                color: new Vector4(1f, heat, 0.25f * heat, 1f),   // warm spark
                life: 1.2f + (float)rng.NextDouble() * 0.8f,
                size: 0.10f + (float)rng.NextDouble() * 0.06f);
        }
        // Emit smoke: slower, drifting up, translucent grey.
        for (var i = 0; i < 12; i++)
        {
            smoke.Emit(
                position: new Vector3(Spread() * 0.4f, 0.5f, Spread() * 0.4f),
                velocity: new Vector3(Spread() * 0.4f, 1.2f + (float)rng.NextDouble(), Spread() * 0.4f),
                color: new Vector4(0.55f, 0.55f, 0.6f, 0.35f),
                life: 2.5f + (float)rng.NextDouble() * 1.5f,
                size: 0.6f + (float)rng.NextDouble() * 0.5f);
        }

        sparks.Update(dt, new Vector3(0f, -6f, 0f));   // gravity-pulled sparks
        smoke.Update(dt, new Vector3(0f, 0.4f, 0f));    // buoyant smoke
    }

    private float Spread() => ((float)rng.NextDouble() - 0.5f) * 2f;

    private void BuildCamera(float t)
    {
        var camX = MathF.Sin(t * 0.25f) * 8f;
        var camZ = 10f + MathF.Cos(t * 0.25f) * 2f;
        camPos = new Vector3(camX, 4.5f, camZ);
        var target = new Vector3(0f, 2.5f, 0f);
        var view = Matrix4x4.CreateLookAt(camPos, target, Vector3.UnitY);
        var proj = GraphicsMatrices.CreatePerspectiveVulkan(MathF.PI / 3f, aspect, 0.1f, 200f);
        viewProj = view * proj;

        // Camera right/up in world space, to orient the billboards.
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
                // Smoke first (alpha, depth-sorted back-to-front), sparks over it
                // (additive, no sort). Two arena slices + two cached pipelines in
                // one frame.
                smoke.Draw(pass, alphaPipeline, viewProj, camRight, camUp, camPos, sortByDepth: true);
                sparks.Draw(pass, additivePipeline, viewProj, camRight, camUp, camPos, sortByDepth: false);
            });

        if (exitAfterFrames > 0 && frameCount >= exitAfterFrames)
        {
            host.RequestClose();
        }
    }

    // No OnUnload disposal — same rationale as VulkanInstanced: RequestClose fires
    // Closing before the final recorded draw executes, so device.Dispose (which
    // waits idle + frees every table) does teardown.

    public void OnKeyDown(Key key)
    {
        if (key == Key.Escape) host.RequestClose();
    }
}

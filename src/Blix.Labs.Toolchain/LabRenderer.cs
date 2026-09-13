using System.Numerics;
using System.Runtime.InteropServices;
using Blix.Graphics;
using Blix.Graphics.Vulkan;
using Blix.Render;

namespace Blix.Labs.Toolchain;

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
public sealed class LabRenderer : IDisposable
{
    /// <summary>Square shadow map, matching the texel size the lit shader offsets by.</summary>
    public const int ShadowMapSize = 2048;

    private const int PushBytes = 96;   // mat4 model (64) + vec4 colour (16) + vec4 material (16)

    private VulkanGraphicsDevice device = null!;
    private FullscreenPass fullscreen = null!;

    private RenderGraph graph = null!;
    private GraphResourceHandle shadowTarget;
    private GraphResourceHandle sceneColourTarget;
    private GraphResourceHandle sceneDepthTarget;
    private PassHandle shadowPass;
    private PassHandle litPass;

    private ShaderProgramHandle litProgram;
    private ShaderProgramHandle shadowProgram;
    private ShaderProgramHandle presentProgram;
    private PipelineHandle litPipeline;
    private PipelineHandle shadowPipeline;
    private PipelineHandle presentPipeline;

    private VertexBufferHandle cubeVertices;
    private IndexBufferHandle cubeIndices;
    private VertexBufferHandle groundVertices;
    private IndexBufferHandle groundIndices;
    private int cubeIndexCount;
    private int groundIndexCount;

    private readonly byte[] pushScratch = new byte[PushBytes];

    // The caster only needs the model matrix, and the reflected interface says so — 64
    // bytes against the lit pass's 96. Pushing the larger block at it is rejected by the
    // device with the sizes named, which is the binding model earning its keep: a
    // hand-declared interface would have shrugged and corrupted the tail.
    private readonly byte[] casterPushScratch = new byte[64];

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

        var shadowInterface = Reflect("lab_shadow.vert", "lab_shadow.frag");
        var litInterface = Reflect("lab_lit.vert", "lab_lit.frag");
        var presentInterface = Reflect("lab_present.vert", "lab_present.frag");

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

        graph.Compile();

                byte[] Spv(string stage) => File.ReadAllBytes(Path.Combine(shaderDirectory, stage + ".spv"));

        shadowProgram = vk.CreateShaderProgramFromSpv(
            Spv("lab_shadow.vert"), Spv("lab_shadow.frag"), shadowInterface, "lab.shadow");
        litProgram = vk.CreateShaderProgramFromSpv(
            Spv("lab_lit.vert"), Spv("lab_lit.frag"), litInterface, "lab.lit");
        presentProgram = vk.CreateShaderProgramFromSpv(
            Spv("lab_present.vert"), Spv("lab_present.frag"), presentInterface, "lab.present");

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

        presentPipeline = vk.CreatePipeline(new PipelineDescription(
            presentProgram,
            VertexPosition3Texture.Layout,
            PrimitiveTopology.Triangles,
            DepthState.Disabled,
            RasterizerState.NoCulling,
            BlendState.Disabled), "lab.present");

        var (cv, ci) = LabGeometry.Cube();
        cubeVertices = vk.CreateVertexBuffer(VertexPosition3NormalTexture.CreateBufferData(cv), "lab.cube.vb");
        cubeIndices = vk.CreateIndexBuffer(ci, name: "lab.cube.ib");
        cubeIndexCount = ci.Length;

        var (gv, gi) = LabGeometry.Ground();
        groundVertices = vk.CreateVertexBuffer(VertexPosition3NormalTexture.CreateBufferData(gv), "lab.ground.vb");
        groundIndices = vk.CreateIndexBuffer(gi, name: "lab.ground.ib");
        groundIndexCount = gi.Length;
    }

    /// <summary>The HDR colour the scene is lit into, before tonemapping. For anything that wants to sample it.</summary>
    public TextureHandle SceneColour => graph.GetColorTexture(sceneColourTarget);

    /// <summary>The sun's depth buffer. Exposed so a tool can look at what the caster pass produced.</summary>
    public TextureHandle ShadowDepth => graph.GetDepthTexture(shadowTarget);

    public void Render(
        RenderCommandList commandList, LabScene scene, Matrix4x4 viewProjection, Vector3 cameraPosition)
    {
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
        });

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
                new[] { new ShaderTextureBinding("uScene", graph.GetColorTexture(sceneColourTarget), Slot: 0) },
                pushConstants: null,
                uniforms: present));
    }

    private void DrawObject(
        RenderPassBuilder pass,
        in LabObject item,
        PipelineHandle pipeline,
        ShaderUniform[] uniforms,
        ShaderTextureBinding[] textures,
        bool casterOnly = false)
    {
        PackPush(item);
        if (casterOnly) Array.Copy(pushScratch, casterPushScratch, casterPushScratch.Length);
        pass.DrawIndexed(
            vertexBuffer: item.IsGround ? groundVertices : cubeVertices,
            indexBuffer: item.IsGround ? groundIndices : cubeIndices,
            pipeline: pipeline,
            indexCount: item.IsGround ? groundIndexCount : cubeIndexCount,
            uniforms: uniforms,
            textures: textures,
            pushConstants: casterOnly ? casterPushScratch : pushScratch);
    }

    // mat4 model, vec4 base colour, vec4 (metallic, roughness, _, _) — 96 bytes, inside the
    // 128-byte floor every Vulkan implementation guarantees, which is why there is no
    // per-object descriptor set in this lab at all.
    private void PackPush(in LabObject item)
    {
        var floats = MemoryMarshal.Cast<byte, float>(pushScratch.AsSpan());
        var m = item.Model;
        floats[0] = m.M11; floats[1] = m.M12; floats[2] = m.M13; floats[3] = m.M14;
        floats[4] = m.M21; floats[5] = m.M22; floats[6] = m.M23; floats[7] = m.M24;
        floats[8] = m.M31; floats[9] = m.M32; floats[10] = m.M33; floats[11] = m.M34;
        floats[12] = m.M41; floats[13] = m.M42; floats[14] = m.M43; floats[15] = m.M44;
        floats[16] = item.BaseColour.X; floats[17] = item.BaseColour.Y; floats[18] = item.BaseColour.Z; floats[19] = 1f;
        floats[20] = item.Metallic; floats[21] = item.Roughness; floats[22] = 0f; floats[23] = 0f;
    }

    public void Dispose()
    {
        fullscreen?.Dispose();
    }
}

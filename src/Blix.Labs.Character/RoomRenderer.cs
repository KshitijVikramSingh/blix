using System.Numerics;
using System.Runtime.InteropServices;
using Blix.Graphics;
using Blix.Graphics.Vulkan;
using Blix.Render;

namespace Blix.Labs.Character;

/// <summary>
/// Three passes over a room that never moves: cast, light, present.
/// </summary>
/// <remarks>
/// <para>
/// <b>No model matrices anywhere.</b> The room's vertices are already in world space because that
/// is what <see cref="Blix.Geometry.TriangleMesh3D"/> holds, so the vertex stage transforms by the
/// view-projection alone. That removes the one place the picture and the collider could disagree —
/// and it is why this is not the toolchain lab's renderer with a different scene in it. That one
/// draws a list of objects each with its own transform; this draws one buffer, once.
/// </para>
/// <para>
/// <b>Deliberately not here:</b> cascades, bloom, IBL, MSAA, a depth pre-pass, instancing. The
/// subject of this lab is what a body does against a surface. A renderer that grew features by
/// default would be claiming to be one.
/// </para>
/// </remarks>
public sealed class RoomRenderer : IDisposable
{
    public const int ShadowMapSize = 2048;

    /// <summary>Bytes the lit pass pushes: vec4 base colour + vec4 material.</summary>
    /// <remarks>
    /// Public so the probe can check it against what the SPIR-V declares. It is the one number in
    /// this file that can silently disagree with the shader it describes — the toolchain lab's
    /// equivalent did, on its first run, and the device caught it at draw time, which is late.
    /// </remarks>
    public const int LitPushBytes = 32;

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

    private VertexBufferHandle roomVertices;
    private IndexBufferHandle roomIndices;
    private int roomIndexCount;

    // Reused across draws, which is safe for exactly one reason: a recorded command COPIES its push
    // payload (see RenderCommand.cs). The uniform lists below are rebuilt per pass rather than
    // reused, because those are not copied — the prologue's fingerprint would say so if they were.
    private readonly byte[] pushScratch = new byte[LitPushBytes];

    /// <summary>Direction TOWARD the sun.</summary>
    public Vector3 SunDirection { get; set; } = Vector3.Normalize(new Vector3(0.42f, 0.78f, 0.30f));

    public Vector3 SunColour { get; set; } = new(3.1f, 3.0f, 2.8f);

    public float AmbientStrength { get; set; } = 0.10f;

    public float Exposure { get; set; } = 1.0f;

    public float TonemapMode { get; set; }

    /// <summary>0 shows each part's own colour, 1 shades every surface by its slope.</summary>
    public float SlopeTint { get; set; }

    public TextureHandle SceneColour => graph.GetColorTexture(sceneColourTarget);

    public RenderSurfaceHandle SceneSurface => graph.GetPassSurface(litPass);

    public TextureHandle ShadowDepth => graph.GetDepthTexture(shadowTarget);

    public void Load(VulkanGraphicsDevice vk, string shaderDirectory, Room room)
    {
        device = vk;
        fullscreen = new FullscreenPass(vk, "room.present");

        ShaderInterface Reflect(params string[] stages) =>
            ShaderReflection.MergeStages(
                stages.Select(s => ShaderReflection.Load(
                    Path.Combine(shaderDirectory, s + ".spv.refl.json"))).ToArray());

        var shadowInterface = Reflect("room_shadow.vert", "room_shadow.frag");
        var litInterface = Reflect("room_lit.vert", "room_lit.frag");
        var presentInterface = Reflect("room_present.vert", "room_present.frag");

        graph = new RenderGraph(vk);
        var fullSize = new MatchSwapchainGraphSize(1.0f);
        shadowTarget = graph.DepthTarget("room-shadow", new FixedGraphSize(ShadowMapSize, ShadowMapSize));
        sceneColourTarget = graph.ColorTarget("room-hdr", TextureFormat.Rgba16F, fullSize);
        sceneDepthTarget = graph.DepthTarget("room-scene-depth", fullSize);

        shadowPass = graph.GraphicsPass("room.shadow")
            .Depth(shadowTarget, LoadOp.Clear, StoreOp.Store)
            .Shader(shadowInterface)
            .Handle;

        litPass = graph.GraphicsPass("room.lit")
            .Target(sceneColourTarget, LoadOp.Clear, StoreOp.Store)
            .Depth(sceneDepthTarget, LoadOp.Clear, StoreOp.Store)
            .Read(shadowTarget)
            .Shader(litInterface)
            .Handle;

        graph.Compile();

        byte[] Spv(string stage) => File.ReadAllBytes(Path.Combine(shaderDirectory, stage + ".spv"));

        shadowProgram = vk.CreateShaderProgramFromSpv(
            Spv("room_shadow.vert"), Spv("room_shadow.frag"), shadowInterface, "room.shadow");
        litProgram = vk.CreateShaderProgramFromSpv(
            Spv("room_lit.vert"), Spv("room_lit.frag"), litInterface, "room.lit");
        presentProgram = vk.CreateShaderProgramFromSpv(
            Spv("room_present.vert"), Spv("room_present.frag"), presentInterface, "room.present");

        // BACK-FACE CULLING on the lit pass, which the toolchain lab's scene deliberately does not
        // do. Every solid here is closed and the probe says so, so an interior face is never the
        // subject — and culling makes a wrongly-wound face show up as a hole rather than as a
        // surface that merely lights oddly. That is the same fault the probe checks arithmetically;
        // this is the version a person sees without running anything.
        litPipeline = vk.CreatePipeline(new PipelineDescription(
            litProgram,
            VertexPosition3NormalTexture.Layout,
            PrimitiveTopology.Triangles,
            DepthState.LessEqualWrite,
            RasterizerState.BackFaceCulling,
            new[] { BlendState.Disabled },
            RenderTarget: graph.GetPassSurface(litPass)), "room.lit");

        // The caster does NOT cull: a shadow cast by front faces alone loses the far side of every
        // solid, and a room's shadows are mostly far sides.
        shadowPipeline = vk.CreatePipeline(new PipelineDescription(
            shadowProgram,
            VertexPosition3NormalTexture.Layout,
            PrimitiveTopology.Triangles,
            DepthState.LessEqualWrite,
            RasterizerState.NoCulling,
            Array.Empty<BlendState>(),
            RenderTarget: graph.GetPassSurface(shadowPass)), "room.shadow");

        presentPipeline = vk.CreatePipeline(new PipelineDescription(
            presentProgram,
            FullscreenPass.Layout,
            PrimitiveTopology.Triangles,
            new DepthState(Enabled: true, WriteEnabled: true, DepthCompare.LessEqual),
            RasterizerState.NoCulling,
            BlendState.Disabled), "room.present");

        // ONE vertex buffer for the whole room, and an index buffer that is simply 0..n-1. The
        // geometry is flat-shaded, so adjacent faces share no normal and therefore no vertex; an
        // index buffer buys nothing here except the ability to draw a sub-range, which is exactly
        // what the per-part draws below need it for.
        roomVertices = vk.CreateVertexBuffer(
            VertexPosition3NormalTexture.CreateBufferData(room.Vertices), "room.vb");

        var indices = new ushort[room.Vertices.Length];
        for (var i = 0; i < indices.Length; i++) indices[i] = (ushort)i;
        roomIndices = vk.CreateIndexBuffer(indices, name: "room.ib");
        roomIndexCount = indices.Length;
    }

    /// <summary>An orthographic sun box that covers the whole hall.</summary>
    /// <remarks>
    /// Sized from the hall rather than guessed: the diagonal of a 28 x 18 m floor is 33 m, so an
    /// extent short of 17 clips the corners into permanent shadow — which reads as a lighting bug
    /// and is a projection one. No cascades and no texel snapping; those belong to a renderer that
    /// earned them.
    /// </remarks>
    public Matrix4x4 SunViewProjection()
    {
        const float extent = 18f;
        const float depth = 70f;
        var eye = SunDirection * (depth * 0.5f);
        var up = MathF.Abs(Vector3.Dot(SunDirection, Vector3.UnitY)) > 0.95f ? Vector3.UnitZ : Vector3.UnitY;
        var view = Matrix4x4.CreateLookAt(eye, Vector3.Zero, up);
        var projection = GraphicsMatrices.CreateOrthographicVulkan(extent * 2f, extent * 2f, 0.1f, depth);
        return view * projection;
    }

    public void Render(RenderCommandList commandList, Room room, Matrix4x4 viewProjection, Vector3 cameraPosition)
    {
        var sunViewProjection = SunViewProjection();

        // ONE draw for the whole room. The caster needs no per-part anything — it writes depth, and
        // the room's colour is not depth.
        graph.Pass(shadowPass, scope =>
        {
            scope.DrawIndexed(
                vertexBuffer: roomVertices,
                indexBuffer: roomIndices,
                pipeline: shadowPipeline,
                indexCount: roomIndexCount,
                indexOffset: 0,
                uniforms: new ShaderUniform[]
                {
                    new("uLightViewProjection", new Matrix4x4Uniform(sunViewProjection)),
                },
                textures: Array.Empty<ShaderTextureBinding>());
        });

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
            var textures = new[] { new ShaderTextureBinding("uSunShadowMap", ShadowDepth, Slot: 0) };

            // One draw per PART, so a part is a thing the frame's draw counts can see. The room is
            // one buffer and the parts are ranges of it, which is what makes "the collider is the
            // drawn geometry" true at the level of a single triangle rather than of a mesh.
            foreach (var part in room.Parts)
            {
                PackPush(part, pushScratch);
                scope.DrawIndexed(
                    vertexBuffer: roomVertices,
                    indexBuffer: roomIndices,
                    pipeline: litPipeline,
                    indexCount: part.TriangleCount * 3,
                    uniforms: uniforms,
                    textures: textures,
                    pushConstants: pushScratch,
                    indexOffset: part.FirstTriangle * 3);
            }
        });

        graph.Execute(commandList);

        commandList.Pass(
            "room.present",
            new RenderPassDescription(
                Target: RenderSurfaceHandle.Default,
                ClearColors: new GraphicsColor?[] { new GraphicsColor(0, 0, 0, 1) },
                ClearDepth: true),
            pass => fullscreen.Draw(
                pass, presentPipeline,
                new[]
                {
                    new ShaderTextureBinding("uScene", SceneColour, Slot: 0),
                    new ShaderTextureBinding("uSceneDepth", graph.GetDepthTexture(sceneDepthTarget), Slot: 1),
                },
                pushConstants: null,
                uniforms: new ShaderUniform[]
                {
                    new("uParams", new Vector4Uniform(new Vector4(Exposure, TonemapMode, 0f, 0f))),
                }));
    }

    private void PackPush(RoomPart part, byte[] target)
    {
        var floats = MemoryMarshal.Cast<byte, float>(target.AsSpan());
        floats[0] = part.Colour.X; floats[1] = part.Colour.Y; floats[2] = part.Colour.Z; floats[3] = 1f;
        floats[4] = 0f;            // metallic — nothing in a physics room is metal
        floats[5] = 0.85f;         // roughness: matte, so shape reads from shading rather than highlights
        floats[6] = SlopeTint;
        floats[7] = 0f;
    }

    public void Dispose()
    {
        // The graph owns render passes, framebuffers and images made through raw Vulkan calls that
        // the device's tables know nothing about, so it has to be told to let go — the toolchain
        // lab leaked nine objects by not doing this, and only BLIX_VK_VALIDATE ever noticed.
        graph?.Dispose();
        fullscreen?.Dispose();
        if (device is null) return;

        device.DestroyPipeline(litPipeline);
        device.DestroyPipeline(shadowPipeline);
        device.DestroyPipeline(presentPipeline);
        device.DestroyShaderProgram(litProgram);
        device.DestroyShaderProgram(shadowProgram);
        device.DestroyShaderProgram(presentProgram);
        device.DestroyVertexBuffer(roomVertices);
        device.DestroyIndexBuffer(roomIndices);
    }
}

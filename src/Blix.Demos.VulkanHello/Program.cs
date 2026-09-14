using System.Numerics;
using Blix;
using Blix.Core;
using Blix.Diagnostics;
using Blix.Graphics;
using Blix.Graphics.Vulkan;
using Blix.Render;
using Blix.Runtime.Silk;
using ImGuiNET;

namespace Blix.Demos.VulkanHello;

// Spinning lit cube on the Vulkan backend. First 3D content on this
// backend, exercising depth attachment + descriptor sets + per-frame UBO
// + name-keyed ShaderUniform writes routed through a UniformBlockLayout.
//
// The narrowest known-good Vulkan call site: it keeps the name-keyed
// ShaderUniform("uModel", ...) API at the call site, so it doubles as the
// simplest reference when something further up the stack breaks.
//
// ── Executable spec for (engine primitives this demo proves) ──
//   • The narrowest known-good Vulkan path: depth attachment + descriptor sets +
//     per-frame UBO + name-keyed ShaderUniform routed through a UniformBlockLayout
//   • Offscreen pass → FullscreenPass present
// ── Intentionally owns (stays local) ──
//   • nothing gameplay — this is the minimal reference call site; keep it minimal
public static class Program
{
    public static void Main(string[] args)
    {
        var loop = new HelloLoop();
        // Through FromArgs so the host's shared arguments actually reach it. This demo used to
        // construct its options directly, which silently ignored --frames: every "bounded" run of
        // it was really an unbounded one that something else killed, and a killed process never
        // tears down, so it never reported a leak either. A comparison against it was measuring
        // nothing.
        var options = WindowOptions.FromArgs(args, WindowOptions.Default with
        {
            Title = "Blix — Vulkan Cube",
            Width = 1280,
            Height = 720,
        });
        using var window = new Window(loop, options);
        window.Run();
    }
}

internal sealed class HelloLoop : IGameLoop, IDebuggable, IUiSource
{
    public string DebugName => "vulkan-cube";

    private VertexBufferHandle vertexBuffer;
    private IndexBufferHandle indexBuffer;
    private ShaderProgramHandle shaderProgram;
    private PipelineHandle pipeline;
    private TextureHandle albedoTexture;
    private MaterialHandle whiteMaterial;
    private MaterialHandle redMaterial;

    // Step 4 — offscreen pass + present pass.
    private RenderSurfaceHandle offscreenSurface;
    private TextureHandle offscreenColor;
    private ShaderProgramHandle presentShaderProgram;
    private PipelineHandle presentPipeline;
    private FullscreenPass fullscreen = null!;

    private int frameCount;
    private Matrix4x4 viewProj;

    // Push-constant payload reused across frames. Two separate arrays so the
    // record-then-translate flow doesn't see one draw's bytes mutated into
    // the other's. 64 bytes each (one mat4 uModel).
    private readonly byte[] leftPushBytes = new byte[64];
    private readonly byte[] rightPushBytes = new byte[64];

    // State surfaced through IDebuggable — see Debug() below.
    private GraphicsDeviceInfo? gpuInfo;
    private Vector3 cameraPosition;
    private Vector3 cameraTarget;
    private float fovYRadians;
    private float currentRotY;
    private float currentRotX;
    private Matrix4x4 leftModel;
    private Matrix4x4 rightModel;
    private static readonly Vector3 LightDirection = Vector3.Normalize(new Vector3(0.55f, 1.0f, 0.45f));
    private const float CubeSeparation = 1.1f; // ±X distance from origin

    public void OnLoad(IRenderHost host, IGraphicsDevice graphicsDevice)
    {
        host.SetTitle("Blix — Vulkan Cube");
        var vk = (VulkanGraphicsDevice)graphicsDevice;
        gpuInfo = graphicsDevice.Info;

        var (vertices, indices) = BuildCube();
        var vertexData = VertexPosition3Texture.CreateBufferData(vertices);
        vertexBuffer = vk.CreateVertexBuffer(vertexData, "cube.vb");
        indexBuffer = vk.CreateIndexBuffer(indices, name: "cube.ib");

        // Procedural checkerboard albedo with red/green UV-corner markers so
        // texture orientation is visually verifiable: red marker = UV (0,0)
        // origin, green = UV (1,0). 256×256 RGBA8 sRGB.
        var checker = BuildUvAwareCheckerboard(size: 256, cellCount: 8);
        albedoTexture = vk.CreateTexture2D(
            new TextureDescription(256, 256, TextureFormat.Rgba8Srgb, SamplerDescription.LinearRepeat),
            checker,
            "cube.albedo");

        // Per-frame UBO layout — just uViewProjection now. uModel moved to
        // push constants per the 2e per-draw lifetime tier.
        var uniformLayout = new UniformBlockLayout(
            TotalSize: 64,
            Members: new[] { new UniformBlockMember("uViewProjection", Offset: 0, Size: 64) });

        // Per-material tint UBO layout — vec4 at offset 0, total 16 bytes.
        var tintLayout = new UniformBlockLayout(
            TotalSize: 16,
            Members: new[] { new UniformBlockMember("uTint", Offset: 0, Size: 16) });

        // Declared binding contract:
        //   set 0 binding 0  per-frame   Frame { mat4 uViewProjection }
        //   set 2 binding 0  per-material CubeMaterial { vec4 uTint }
        //   set 2 binding 1  per-material sampler2D uAlbedo
        //   push constants   per-draw    PushConstants { mat4 uModel } (vertex stage)
        // Set 1 is unused (no per-pass data on the cube); the pipeline layout
        // carries an empty layout for it to keep set indices contiguous.
        var cubeInterface = new ShaderInterface(
            Slots: new[]
            {
                new DescriptorSetSlot(
                    Set: 0, Binding: 0,
                    Type: ShaderResourceType.UniformBuffer,
                    Stages: ShaderStages.Vertex | ShaderStages.Fragment,
                    BlockLayout: uniformLayout),
                new DescriptorSetSlot(
                    Set: 2, Binding: 0,
                    Type: ShaderResourceType.UniformBuffer,
                    Stages: ShaderStages.Fragment,
                    BlockLayout: tintLayout),
                new DescriptorSetSlot(
                    Set: 2, Binding: 1,
                    Type: ShaderResourceType.SampledImage,
                    Stages: ShaderStages.Fragment),
            },
            PushConstants: new[]
            {
                new PushConstantRange(ShaderStages.Vertex, Offset: 0, Size: 64),
            });

        // Offscreen render target — half-resolution Rgba16F with a depth
        // attachment so the cubes' DepthState.LessEqualWrite still
        // depth-tests. The half-res upscale at present time is the visible
        // proof that an intermediate buffer is in the pipeline.
        var surface = vk.CreateRenderSurface(new RenderSurfaceDescription(
            Name: "offscreen",
            Size: new MatchDefaultRenderSurfaceSize(Scale: 0.5f),
            ColorAttachments: new[]
            {
                new ColorAttachmentDescription(TextureFormat.Rgba16F, SamplerDescription.LinearClamp),
            },
            Depth: new DepthRenderbuffer()));
        offscreenSurface = surface.Handle;
        offscreenColor = surface.ColorAttachments[0];

        var shaderDir = Path.Combine(AppContext.BaseDirectory, "Shaders");
        var vertSpv = File.ReadAllBytes(Path.Combine(shaderDir, "cube.vert.spv"));
        var fragSpv = File.ReadAllBytes(Path.Combine(shaderDir, "cube.frag.spv"));
        shaderProgram = vk.CreateShaderProgramFromSpv(vertSpv, fragSpv, cubeInterface, "cube");

        // Cube pipeline targets the offscreen surface — different attachment
        // formats (Rgba16F + D32 vs swapchain BGRA + D32) require a render-
        // pass-compatible pipeline, so we bake against the surface's pass.
        pipeline = vk.CreatePipeline(new PipelineDescription(
            shaderProgram,
            VertexPosition3Texture.Layout,
            PrimitiveTopology.Triangles,
            DepthState.LessEqualWrite,
            RasterizerState.NoCulling,
            new[] { BlendState.Disabled },
            RenderTarget: offscreenSurface), "cube");

        // Two materials sharing the same shader interface and same texture,
        // different tint. The cube switches between them every 2 seconds
        // (see OnRender) to visually validate that the descriptor-set
        // carrier handles multi-material lookup.
        whiteMaterial = vk.CreateMaterial(shaderProgram, name: "cube.white")
            .SetUniform(binding: 0, "uTint", new Vector4(1.0f, 1.0f, 1.0f, 1.0f))
            .SetTexture(binding: 1, albedoTexture)
            .Handle;

        redMaterial = vk.CreateMaterial(shaderProgram, name: "cube.red")
            .SetUniform(binding: 0, "uTint", new Vector4(1.0f, 0.45f, 0.35f, 1.0f))
            .SetTexture(binding: 1, albedoTexture)
            .Handle;

        // --- Present pipeline ---------------------------------------------
        // Fullscreen-quad sampler that reads offscreenColor and writes the
        // swapchain. Interface: one SampledImage at set 0 binding 0 — uses
        // the inline ShaderTextureBinding path (set 0 is within the
        // single-set assumption documented in Cleanup C).
        var presentInterface = new ShaderInterface(new[]
        {
            new DescriptorSetSlot(
                Set: 0, Binding: 0,
                Type: ShaderResourceType.SampledImage,
                Stages: ShaderStages.Fragment),
        });
        var presentVertSpv = File.ReadAllBytes(Path.Combine(shaderDir, "present.vert.spv"));
        var presentFragSpv = File.ReadAllBytes(Path.Combine(shaderDir, "present.frag.spv"));
        presentShaderProgram = vk.CreateShaderProgramFromSpv(presentVertSpv, presentFragSpv, presentInterface, "present");

        // Present pipeline targets the swapchain (RenderTarget defaults
        // to null → DefaultRenderPass). No depth test — fullscreen quad.
        presentPipeline = vk.CreatePipeline(new PipelineDescription(
            presentShaderProgram,
            VertexPosition3Texture.Layout,
            PrimitiveTopology.Triangles,
            DepthState.Disabled,
            RasterizerState.NoCulling,
            BlendState.Disabled), "present");

        // Fullscreen triangle for the present pass (positions synthesised from
        // gl_VertexIndex in present.vert; the buffer is never sampled).
        fullscreen = new FullscreenPass(vk, "present");

        // Camera pulled back + raised so both cubes fit. Two cubes at
        // x = ±CubeSeparation; camera at (3.5, 1.9, 4.5) looking at origin.
        var aspect = host.LogicalSize.Width / (float)host.LogicalSize.Height;
        cameraPosition = new Vector3(3.5f, 1.9f, 4.5f);
        cameraTarget = Vector3.Zero;
        fovYRadians = MathF.PI / 3f;
        var view = Matrix4x4.CreateLookAt(cameraPosition, cameraTarget, Vector3.UnitY);
        var proj = GraphicsMatrices.CreatePerspectiveVulkan(fovYRadians, aspect, nearPlane: 0.1f, farPlane: 100f);
        viewProj = view * proj;
    }

    public void OnRender(Time time, RenderFrameContext frame, RenderCommandList commandList)
    {
        frameCount++;
        currentRotY = (float)time.Total * 0.8f;
        currentRotX = (float)time.Total * 0.3f;

        // Two cubes — left spins +Y, right spins -Y, both share the X axis
        // wobble. Counter-rotation gives each cube a distinct visual
        // signature so it's obvious the push-constant uModel is per-draw.
        leftModel = Matrix4x4.CreateRotationY(currentRotY)
                  * Matrix4x4.CreateRotationX(currentRotX)
                  * Matrix4x4.CreateTranslation(new Vector3(-CubeSeparation, 0, 0));
        rightModel = Matrix4x4.CreateRotationY(-currentRotY)
                   * Matrix4x4.CreateRotationX(currentRotX)
                   * Matrix4x4.CreateTranslation(new Vector3(CubeSeparation, 0, 0));

        // Pack each model matrix into its own 64-byte push-constant buffer.
        // The buffers are pre-allocated fields; the record-then-translate
        // flow captures the byte[] reference, so we must NOT reuse the same
        // array for both draws this frame.
        PackMatrix(leftModel, leftPushBytes);
        PackMatrix(rightModel, rightPushBytes);

        var perFrame = new ShaderUniform[]
        {
            new("uViewProjection", new Matrix4x4Uniform(viewProj)),
        };

        // Pass 1 — render both cubes into the half-resolution offscreen
        // Rgba16F surface. Surface's render pass auto-transitions the color
        // attachment to SHADER_READ_ONLY_OPTIMAL at end-of-pass.
        commandList.Pass(
            "cube-offscreen",
            new RenderPassDescription(
                Target: offscreenSurface,
                ClearColors: new GraphicsColor?[] { new GraphicsColor(0.06f, 0.08f, 0.12f, 1.0f) },
                ClearDepth: true),
            pass =>
            {
                pass.DrawIndexed(
                    vertexBuffer: vertexBuffer,
                    indexBuffer: indexBuffer,
                    pipeline: pipeline,
                    indexCount: 36,
                    uniforms: perFrame,
                    textures: Array.Empty<ShaderTextureBinding>(),
                    material: whiteMaterial,
                    pushConstants: leftPushBytes);

                pass.DrawIndexed(
                    vertexBuffer: vertexBuffer,
                    indexBuffer: indexBuffer,
                    pipeline: pipeline,
                    indexCount: 36,
                    uniforms: perFrame,
                    textures: Array.Empty<ShaderTextureBinding>(),
                    material: redMaterial,
                    pushConstants: rightPushBytes);
            });

        // Pass 2 — fullscreen quad samples offscreenColor and writes the
        // swapchain. Half-res → full-res upscale gives a slight softening
        // which is the visible proof the intermediate buffer is real.
        commandList.Pass(
            "present",
            new RenderPassDescription(
                Target: RenderSurfaceHandle.Default,
                ClearColors: new GraphicsColor?[] { new GraphicsColor(0, 0, 0, 1) },
                ClearDepth: true),
            pass => fullscreen.Draw(
                pass, presentPipeline,
                new[] { new ShaderTextureBinding("uOffscreen", offscreenColor, Slot: 0) }));
    }

    private static void PackMatrix(Matrix4x4 m, byte[] target)
    {
        System.Runtime.InteropServices.MemoryMarshal.Write(target, in m);
    }

    public void Debug(DebugContext debug)
    {
        debug.Values.Value("frame", frameCount);

        using (debug.Scope("light"))
        {
            debug.Values.Value("direction", LightDirection);
        }
        using (debug.Scope("cubes"))
        {
            debug.Values.Value("count", 2);
            debug.Values.Value("rotY-rad", currentRotY);
            debug.Values.Value("rotX-rad", currentRotX);
            debug.Values.Value("separation", CubeSeparation);
            debug.Values.Value("vertices-each", 24);
            debug.Values.Value("triangles-each", 12);
        }
        using (debug.Scope("camera"))
        {
            debug.Values.Value("position", cameraPosition);
            debug.Values.Value("target", cameraTarget);
            debug.Values.Value("fovY-rad", fovYRadians);
        }
        if (gpuInfo is { } info)
        {
            using (debug.Scope("gpu"))
            {
                debug.Values.Value("vendor", info.Vendor);
                debug.Values.Value("renderer", info.Renderer);
                debug.Values.Value("version", info.Version);
            }
        }

        debug.Stats.Gauge("triangles", 24);
        debug.Stats.Gauge("vertices", 48);

        // === Visual debug overlays ===
        // ViewProjection is what the Vulkan-side line drawer uses to project
        // these world-space coordinates onto the swapchain. Must match the
        // camera's matrix or the overlay floats away from the cube.
        // Every primitive below belongs to this view. Scoped rather than assigned: the old
        // per-channel matrix meant a frame could only ever be one world seen one way.
        using var view = debug.Draw.In("main", viewProj);

        // World axes at origin (X red, Y green, Z blue). One meter long.
        // Confirms which way each axis goes after the Vulkan +Y-down flip
        // (Y should point UP visually even though +Y is DOWN in clip space).
        const float axisLen = 1.0f;
        debug.Draw.Line("axis/x", Vector3.Zero, Vector3.UnitX * axisLen, new GraphicsColor(0.95f, 0.30f, 0.30f, 1f));
        debug.Draw.Line("axis/y", Vector3.Zero, Vector3.UnitY * axisLen, new GraphicsColor(0.30f, 0.95f, 0.40f, 1f));
        debug.Draw.Line("axis/z", Vector3.Zero, Vector3.UnitZ * axisLen, new GraphicsColor(0.40f, 0.55f, 0.95f, 1f));

        // Light direction arrow: tail at "where the sun is" (origin + lightDir
        // scaled out), head at cube origin. Visually represents "light travels
        // in this direction and lands on the cube." Soft yellow.
        var lightSource = LightDirection * 2.0f;
        debug.Draw.Arrow("light/direction", lightSource, Vector3.Zero, new GraphicsColor(1.0f, 0.95f, 0.45f, 1f));

        // One world-space OBB per cube, tracking each cube's rotation +
        // translation. Slightly inflated (0.51 instead of 0.50) so the
        // wireframe doesn't z-fight or get visually swallowed.
        var leftObb = Matrix4x4.CreateScale(0.51f) * leftModel;
        debug.Draw.Obb("cube/left/obb", leftObb, new GraphicsColor(1f, 1f, 1f, 0.85f));
        var rightObb = Matrix4x4.CreateScale(0.51f) * rightModel;
        debug.Draw.Obb("cube/right/obb", rightObb, new GraphicsColor(1f, 1f, 1f, 0.85f));

        // <b>The one primitive with a memory.</b> A corner of each cube, remembered for two seconds, so a
        // spinning cube draws the arc its corner has just travelled. Every other call in this method
        // describes this instant; these two describe the recent past, which nothing in diagnostics could
        // express before — a draw command could not outlive the frame that made it.
        //
        // Trails are also the cheapest possible demonstration that the two axes compose: the points are
        // remembered per path and drawn into whichever view is in scope.
        if (!trailsOn) return;
        var corner = new Vector3(0.5f, 0.5f, 0.5f);
        var leftCorner = Vector3.Transform(corner, leftModel);
        var rightCorner = Vector3.Transform(corner, rightModel);
        debug.Draw.Trail(
            "cube/left/corner", leftCorner, new GraphicsColor(1f, 0.55f, 0.2f, 1f), trailSeconds);
        debug.Draw.Trail(
            "cube/right/corner", rightCorner, new GraphicsColor(0.3f, 0.9f, 1f, 1f), trailSeconds);

    }

    // --- IUiSource ----------------------------------------------------------
    //
    // An application's own panel, drawn into the same ImGui frame as the diagnostics overlay rather than
    // instead of it. Before this, the only interface a Blix application could have WAS the overlay: ImGui
    // existed only for an IDebuggable loop and the frame was hard-wired to the diagnostics panels.
    //
    // The text field is here deliberately. ImGui has always reported WantCaptureKeyboard and Blix never
    // read it, so typing used to reach the game as well as the field — invisible while the overlay was
    // the only UI, because it has hardly any text fields.

    private bool trailsOn = true;
    private float trailSeconds = 2f;
    private string note = "type here — keys must not reach the game";

    public string UiName => "hello";

    public void DrawUi()
    {
        ImGui.SetNextWindowSize(new Vector2(340, 150), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowPos(new Vector2(20, 220), ImGuiCond.FirstUseEver);
        if (!ImGui.Begin("Hello"))
        {
            ImGui.End();
            return;
        }

        ImGui.Checkbox("corner trails", ref trailsOn);
        ImGui.SliderFloat("seconds", ref trailSeconds, 0.25f, 8f);
        ImGui.InputText("note", ref note, 128);
        ImGui.TextDisabled($"{(trailsOn ? "tracing" : "off")} · {trailSeconds:0.0}s");
        ImGui.End();
    }

    private static (VertexPosition3Texture[] Vertices, ushort[] Indices) BuildCube()
    {
        // 24 vertices = 4 per face. Per-face UV mapping wraps the full
        // texture onto each face. UV (0,0) is top-left of the texture; the
        // vertex order matches the index buffer's triangulation:
        //   v0 = lower-left  → UV (0, 1)
        //   v1 = lower-right → UV (1, 1)
        //   v2 = upper-right → UV (1, 0)
        //   v3 = upper-left  → UV (0, 0)
        VertexPosition3Texture V(float x, float y, float z, float u, float v) =>
            new(new GraphicsVector3(x, y, z), new GraphicsVector2(u, v));

        var vertices = new VertexPosition3Texture[]
        {
            // -Z face
            V(-0.5f, -0.5f, -0.5f, 0, 1), V( 0.5f, -0.5f, -0.5f, 1, 1),
            V( 0.5f,  0.5f, -0.5f, 1, 0), V(-0.5f,  0.5f, -0.5f, 0, 0),
            // +Z face
            V(-0.5f, -0.5f,  0.5f, 0, 1), V( 0.5f, -0.5f,  0.5f, 1, 1),
            V( 0.5f,  0.5f,  0.5f, 1, 0), V(-0.5f,  0.5f,  0.5f, 0, 0),
            // -X face
            V(-0.5f, -0.5f, -0.5f, 0, 1), V(-0.5f, -0.5f,  0.5f, 1, 1),
            V(-0.5f,  0.5f,  0.5f, 1, 0), V(-0.5f,  0.5f, -0.5f, 0, 0),
            // +X face
            V( 0.5f, -0.5f, -0.5f, 0, 1), V( 0.5f,  0.5f, -0.5f, 0, 0),
            V( 0.5f,  0.5f,  0.5f, 1, 0), V( 0.5f, -0.5f,  0.5f, 1, 1),
            // -Y face
            V(-0.5f, -0.5f, -0.5f, 0, 1), V( 0.5f, -0.5f, -0.5f, 1, 1),
            V( 0.5f, -0.5f,  0.5f, 1, 0), V(-0.5f, -0.5f,  0.5f, 0, 0),
            // +Y face
            V(-0.5f,  0.5f, -0.5f, 0, 1), V(-0.5f,  0.5f,  0.5f, 0, 0),
            V( 0.5f,  0.5f,  0.5f, 1, 0), V( 0.5f,  0.5f, -0.5f, 1, 1),
        };
        var indices = new ushort[]
        {
            0,  1,  2,   0,  2,  3,    // -Z
            4,  5,  6,   4,  6,  7,    // +Z
            8,  9, 10,   8, 10, 11,    // -X
            12, 13, 14,  12, 14, 15,   // +X
            16, 17, 18,  16, 18, 19,   // -Y
            20, 21, 22,  20, 22, 23,   // +Y
        };
        return (vertices, indices);
    }

    // Procedural texture: greyscale checkerboard with two coloured corner
    // markers so UV orientation is visible. Red at the texture's (0,0)
    // pixel (UV origin), green at the (size-1, 0) pixel (UV (1,0) end).
    // Row order is top-down — pixel (x, y) where y=0 is the top row, so the
    // red marker lands in the first scanline written.
    private static byte[] BuildUvAwareCheckerboard(int size, int cellCount)
    {
        var data = new byte[size * size * 4];
        var cellSize = size / cellCount;
        var markerSize = size / 16;
        for (var y = 0; y < size; y++)
        for (var x = 0; x < size; x++)
        {
            var dark = ((x / cellSize) + (y / cellSize)) % 2 == 0;
            var idx = (y * size + x) * 4;
            var c = dark ? (byte)40 : (byte)220;
            data[idx]     = c;
            data[idx + 1] = c;
            data[idx + 2] = c;
            data[idx + 3] = 255;
            // UV (0,0) corner marker — red
            if (x < markerSize && y < markerSize)
            {
                data[idx] = 220; data[idx + 1] = 50; data[idx + 2] = 50;
            }
            // UV (1,0) corner marker — green
            else if (x >= size - markerSize && y < markerSize)
            {
                data[idx] = 50; data[idx + 1] = 220; data[idx + 2] = 50;
            }
        }
        return data;
    }

}

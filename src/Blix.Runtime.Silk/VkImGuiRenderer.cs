using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Blix.Diagnostics;
using Blix.Diagnostics.Overlay;
using Blix.Graphics;
using Blix.Graphics.Vulkan;
using ImGuiNET;

namespace Blix.Runtime.Silk;

// Vulkan-native ImGui backend. Owns the ImGui context, the font-atlas texture,
// and a fullscreen-overlay pipeline; renders the shared DebugOverlayUi panels
// onto the swapchain.
//
// Shape mirrors VkLineDrawer: pre-compiled SPIR-V from the lib's Shaders/ dir,
// dynamic host-visible vertex/index buffers re-uploaded each frame, drawn into
// the overlay render pass the Window appends after the scene. ImGui's many
// per-widget draw commands map to DrawIndexedCommands carrying per-cmd index/
// vertex offsets into the one shared buffer pair, a clip-rect scissor, and the
// scale/translate push constant.
public sealed class VkImGuiRenderer : IDisposable
{
    // ImDrawVert: vec2 pos, vec2 uv, uint32 packed RGBA = 20 bytes.
    private const int VertexStride = 20;
    // Generous ceilings for the diagnostics overlay (a few thousand verts at
    // most). Overflowing cmd-lists are dropped rather than crashing.
    private const int MaxVertices = 96 * 1024;
    private const int MaxIndices = 192 * 1024;

    private readonly VulkanGraphicsDevice device;
    private readonly DebugOverlayUi ui = new();
    private readonly VertexBufferHandle vertexBuffer;
    private readonly IndexBufferHandle indexBuffer;
    private readonly ShaderProgramHandle shader;
    private readonly PipelineHandle pipeline;
    private readonly TextureHandle fontTexture;

    // Reused per frame so the overlay doesn't allocate two big arrays each
    // render. Sized to the max buffers above.
    private readonly byte[] vtxScratch = new byte[MaxVertices * VertexStride];
    private readonly byte[] idxScratch = new byte[MaxIndices * sizeof(ushort)];

    private bool disposed;

    public VkImGuiRenderer(VulkanGraphicsDevice device)
    {
        ArgumentNullException.ThrowIfNull(device);
        this.device = device;

        ImGui.CreateContext();
        ImGui.StyleColorsDark();
        var io = ImGui.GetIO();
        // We honor per-cmd VtxOffset, so let ImGui keep large vertex buffers in
        // one list instead of splitting at 64k.
        io.BackendFlags |= ImGuiBackendFlags.RendererHasVtxOffset;

        fontTexture = CreateFontTexture();

        // Dynamic vertex + index buffers, pre-sized; re-uploaded each frame.
        var layout = new VertexLayout(VertexStride, new[]
        {
            new VertexAttribute(Location: 0, VertexAttributeFormat.Float2, Offset: 0),
            new VertexAttribute(Location: 1, VertexAttributeFormat.Float2, Offset: 8),
            new VertexAttribute(Location: 2, VertexAttributeFormat.UByte4Norm, Offset: 16),
        });
        var emptyVtx = new byte[MaxVertices * VertexStride];
        vertexBuffer = device.CreateVertexBuffer(
            new VertexBufferData(
                new VertexBufferDescription(layout, MaxVertices, GraphicsBufferUsage.Dynamic),
                emptyVtx),
            name: "imgui.vb");
        indexBuffer = device.CreateIndexBuffer(
            new ushort[MaxIndices], GraphicsBufferUsage.Dynamic, name: "imgui.ib");

        var shaderDir = Path.Combine(AppContext.BaseDirectory, "Shaders");
        var vertSpv = File.ReadAllBytes(Path.Combine(shaderDir, "imgui.vert.spv"));
        var fragSpv = File.ReadAllBytes(Path.Combine(shaderDir, "imgui.frag.spv"));
        var imguiInterface = new ShaderInterface(
            Slots: new[]
            {
                new DescriptorSetSlot(0, 0, ShaderResourceType.SampledImage, ShaderStages.Fragment),
            },
            PushConstants: new[] { new PushConstantRange(ShaderStages.Vertex, 0, 16) });
        shader = device.CreateShaderProgramFromSpv(vertSpv, fragSpv, imguiInterface, "imgui");

        pipeline = device.CreatePipeline(new PipelineDescription(
            shader,
            layout,
            PrimitiveTopology.Triangles,
            DepthState.Disabled,
            RasterizerState.NoCulling,
            BlendState.AlphaBlend), "imgui");
    }

    public bool WantCaptureMouse => ImGui.GetIO().WantCaptureMouse;
    public bool WantCaptureKeyboard => ImGui.GetIO().WantCaptureKeyboard;

    /// <summary>
    /// Opens an ImGui frame, lets <paramref name="content"/> fill it, and closes it.
    /// </summary>
    /// <remarks>
    /// <b>This used to call the diagnostics overlay directly</b>, which is what made the overlay the only
    /// interface a Blix application could have. There were two near-identical copies of the IO setup below
    /// — one that drew the overlay panels and one that drew the perf HUD — differing only in what happened
    /// between NewFrame and Render. That difference is now the caller's, and this is a renderer again
    /// rather than a renderer of one particular thing.
    /// </remarks>
    public void BeginFrame(
        int windowWidth, int windowHeight,
        int framebufferWidth, int framebufferHeight,
        float deltaTime,
        Vector2 mousePos, bool mouseLeft, bool mouseRight, bool mouseMiddle, float wheel,
        Action content)
    {
        ArgumentNullException.ThrowIfNull(content);

        var logicalW = Math.Max(windowWidth, 1);
        var logicalH = Math.Max(windowHeight, 1);
        var io = ImGui.GetIO();
        io.DisplaySize = new Vector2(logicalW, logicalH);
        io.DisplayFramebufferScale = new Vector2(
            Math.Max(framebufferWidth, 1) / (float)logicalW,
            Math.Max(framebufferHeight, 1) / (float)logicalH);
        io.DeltaTime = deltaTime > 0.0f ? deltaTime : 1.0f / 60.0f;
        io.MousePos = mousePos;
        io.MouseDown[0] = mouseLeft;
        io.MouseDown[1] = mouseRight;
        io.MouseDown[2] = mouseMiddle;
        if (wheel != 0f) io.MouseWheel += wheel;

        ImGui.NewFrame();
        content();
        ImGui.Render();
    }

    /// <summary>Lays out the diagnostics panels. One possible frame content among several now.</summary>
    public void LayoutDiagnostics(DebugSystem debugSystem) => ui.Layout(debugSystem);

    /// <summary>
    /// Draws the minimal FPS readout into the foreground draw list.
    /// </summary>
    /// <remarks>
    /// Deliberately not the panels: the point of the HUD is a measurement the overlay's own cost does not
    /// skew.
    /// </remarks>
    public void DrawPerfHudText(string text)
    {
        var dl = ImGui.GetForegroundDrawList();
        var font = ImGui.GetFont();
        const float size = 22f;
        var pos = new Vector2(10f, 8f);
        // 1px drop shadow for legibility over any scene colour (ABGR packing).
        dl.AddText(font, size, pos + new Vector2(1.5f, 1.5f), 0xFF000000u, text);
        dl.AddText(font, size, pos, 0xFFFFFFFFu, text);
    }

    // Record the current frame's ImGui draw data into the given overlay pass.
    // Reads ImGui.GetDrawData() (valid until the next BeginFrame).
    public unsafe void Submit(RenderPassBuilder pass)
    {
        var drawData = ImGui.GetDrawData();
        if (drawData.CmdListsCount == 0) return;

        var fbScale = drawData.FramebufferScale;
        var fbWidth = drawData.DisplaySize.X * fbScale.X;
        var fbHeight = drawData.DisplaySize.Y * fbScale.Y;
        if (fbWidth <= 0f || fbHeight <= 0f) return;

        // Concatenate every cmd-list's vertices/indices into the shared buffers,
        // tracking running element bases so each draw can index its slice.
        var vtxBase = 0;
        var idxBase = 0;
        var listVtxBase = new int[drawData.CmdListsCount];
        var listIdxBase = new int[drawData.CmdListsCount];
        for (var i = 0; i < drawData.CmdListsCount; i++)
        {
            var list = drawData.CmdLists[i];
            var vtxCount = list.VtxBuffer.Size;
            var idxCount = list.IdxBuffer.Size;
            if (vtxBase + vtxCount > MaxVertices || idxBase + idxCount > MaxIndices)
            {
                // Out of room — drop the rest of the overlay this frame rather
                // than overrun. The ceilings are generous; hitting this means
                // the UI grew unexpectedly large.
                break;
            }
            listVtxBase[i] = vtxBase;
            listIdxBase[i] = idxBase;
            Marshal.Copy(list.VtxBuffer.Data, vtxScratch, vtxBase * VertexStride, vtxCount * VertexStride);
            Marshal.Copy(list.IdxBuffer.Data, idxScratch, idxBase * sizeof(ushort), idxCount * sizeof(ushort));
            vtxBase += vtxCount;
            idxBase += idxCount;
        }
        if (vtxBase == 0) return;

        device.UpdateVertexBuffer(vertexBuffer, vtxScratch.AsSpan(0, vtxBase * VertexStride));
        device.UpdateIndexBuffer(indexBuffer, idxScratch.AsSpan(0, idxBase * sizeof(ushort)));

        // scale/translate push constant: map display rect -> clip space.
        var scale = new Vector2(2.0f / drawData.DisplaySize.X, 2.0f / drawData.DisplaySize.Y);
        var translate = new Vector2(
            -1.0f - drawData.DisplayPos.X * scale.X,
            -1.0f - drawData.DisplayPos.Y * scale.Y);
        var push = new byte[16];
        MemoryMarshal.Write(push.AsSpan(0, 8), in scale);
        MemoryMarshal.Write(push.AsSpan(8, 8), in translate);

        var fontBinding = new[] { new ShaderTextureBinding("uFont", fontTexture, Slot: 0) };
        var clipOff = drawData.DisplayPos;

        for (var i = 0; i < drawData.CmdListsCount; i++)
        {
            var list = drawData.CmdLists[i];
            // Lists past the overflow break weren't copied — stop there.
            if (i > 0 && listVtxBase[i] == 0 && listIdxBase[i] == 0) break;

            for (var c = 0; c < list.CmdBuffer.Size; c++)
            {
                var cmd = list.CmdBuffer[c];
                if (cmd.UserCallback != IntPtr.Zero) continue; // unused by our panels
                if (cmd.ElemCount == 0) continue;

                // Clip rect (display coords) -> framebuffer pixel scissor.
                var clipMinX = (cmd.ClipRect.X - clipOff.X) * fbScale.X;
                var clipMinY = (cmd.ClipRect.Y - clipOff.Y) * fbScale.Y;
                var clipMaxX = (cmd.ClipRect.Z - clipOff.X) * fbScale.X;
                var clipMaxY = (cmd.ClipRect.W - clipOff.Y) * fbScale.Y;
                clipMinX = Math.Clamp(clipMinX, 0f, fbWidth);
                clipMinY = Math.Clamp(clipMinY, 0f, fbHeight);
                clipMaxX = Math.Clamp(clipMaxX, 0f, fbWidth);
                clipMaxY = Math.Clamp(clipMaxY, 0f, fbHeight);
                if (clipMaxX <= clipMinX || clipMaxY <= clipMinY) continue;

                var scissor = new ScissorRect(
                    (int)clipMinX, (int)clipMinY,
                    (int)(clipMaxX - clipMinX), (int)(clipMaxY - clipMinY));

                pass.DrawIndexed(
                    vertexBuffer: vertexBuffer,
                    indexBuffer: indexBuffer,
                    pipeline: pipeline,
                    indexCount: (int)cmd.ElemCount,
                    indexOffset: listIdxBase[i] + (int)cmd.IdxOffset,
                    vertexOffset: listVtxBase[i] + (int)cmd.VtxOffset,
                    textures: fontBinding,
                    pushConstants: push,
                    scissor: scissor);
            }
        }
    }

    private unsafe TextureHandle CreateFontTexture()
    {
        var io = ImGui.GetIO();
        io.Fonts.GetTexDataAsRGBA32(out IntPtr pixels, out var width, out var height, out _);
        var byteCount = width * height * 4;
        var managed = new byte[byteCount];
        Marshal.Copy(pixels, managed, 0, byteCount);
        // Linear (non-sRGB) RGBA8: the atlas is white RGB + coverage alpha; the
        // shader handles the sRGB color conversion for the vertex colors.
        var handle = device.CreateTexture2D(
            new TextureDescription(width, height, TextureFormat.Rgba8, SamplerDescription.LinearClamp),
            managed, "imgui.font");
        io.Fonts.ClearTexData();
        return handle;
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        device.DestroyPipeline(pipeline);
        device.DestroyShaderProgram(shader);
        device.DestroyVertexBuffer(vertexBuffer);
        device.DestroyIndexBuffer(indexBuffer);
        device.DestroyTexture(fontTexture);
        ImGui.DestroyContext();
    }
}

using Blix.Diagnostics;
using ImGuiNET;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using OpenTK.Windowing.GraphicsLibraryFramework;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace Blix.Runtime.OpenTK;

internal sealed class ImGuiOverlayRenderer : IDisposable
{
    private readonly int vertexArray;
    private readonly int vertexBuffer;
    private readonly int indexBuffer;
    private readonly int shader;
    private readonly int fontTexture;
    private readonly int projectionLocation;
    private bool disposed;

    public ImGuiOverlayRenderer()
    {
        ImGui.CreateContext();
        ImGui.StyleColorsDark();

        shader = CreateShader();
        projectionLocation = GL.GetUniformLocation(shader, "projection_matrix");

        vertexArray = GL.GenVertexArray();
        vertexBuffer = GL.GenBuffer();
        indexBuffer = GL.GenBuffer();

        GL.BindVertexArray(vertexArray);
        GL.BindBuffer(BufferTarget.ArrayBuffer, vertexBuffer);
        GL.BindBuffer(BufferTarget.ElementArrayBuffer, indexBuffer);

        GL.EnableVertexAttribArray(0);
        GL.VertexAttribPointer(0, 2, VertexAttribPointerType.Float, false, Unsafe.SizeOf<ImDrawVert>(), 0);
        GL.EnableVertexAttribArray(1);
        GL.VertexAttribPointer(1, 2, VertexAttribPointerType.Float, false, Unsafe.SizeOf<ImDrawVert>(), 8);
        GL.EnableVertexAttribArray(2);
        GL.VertexAttribPointer(2, 4, VertexAttribPointerType.UnsignedByte, true, Unsafe.SizeOf<ImDrawVert>(), 16);

        GL.BindVertexArray(0);

        fontTexture = CreateFontTexture();
    }

    public bool WantsMouseCapture => ImGui.GetIO().WantCaptureMouse;

    public void RenderOverlay(Vector2i windowSize, Vector2i framebufferSize, float deltaTime, MouseState mouseState, DebugSystem debugSystem)
    {
        var logicalWidth = Math.Max(windowSize.X, 1);
        var logicalHeight = Math.Max(windowSize.Y, 1);
        var framebufferWidth = Math.Max(framebufferSize.X, 1);
        var framebufferHeight = Math.Max(framebufferSize.Y, 1);

        var io = ImGui.GetIO();
        io.DisplaySize = new System.Numerics.Vector2(logicalWidth, logicalHeight);
        io.DisplayFramebufferScale = new System.Numerics.Vector2(
            framebufferWidth / (float)logicalWidth,
            framebufferHeight / (float)logicalHeight);
        io.DeltaTime = deltaTime > 0.0f ? deltaTime : 1.0f / 60.0f;
        io.MousePos = new System.Numerics.Vector2(mouseState.Position.X, mouseState.Position.Y);
        io.MouseDown[0] = mouseState.IsButtonDown(MouseButton.Left);
        io.MouseDown[1] = mouseState.IsButtonDown(MouseButton.Right);
        io.MouseDown[2] = mouseState.IsButtonDown(MouseButton.Middle);

        ImGui.NewFrame();
        DrawDiagnostics(debugSystem);
        ImGui.Render();

        RenderDrawData(ImGui.GetDrawData(), framebufferSize);
    }

    private static void DrawDiagnostics(DebugSystem debugSystem)
    {
        var debug = debugSystem.Current;
        if (debug is null)
        {
            return;
        }

        ImGui.SetNextWindowPos(new System.Numerics.Vector2(16.0f, 16.0f), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowBgAlpha(0.82f);
        ImGui.Begin(
            "Diagnostics",
            ImGuiWindowFlags.AlwaysAutoResize |
            ImGuiWindowFlags.NoSavedSettings);

        if (ImGui.CollapsingHeader("Controls", ImGuiTreeNodeFlags.DefaultOpen))
        {
            DrawControls(debugSystem, debug.ControlEntries);
        }

        if (ImGui.CollapsingHeader("State", ImGuiTreeNodeFlags.DefaultOpen))
        {
            DrawValues(debug.ValueEntries);
        }

        ImGui.End();
    }

    private static void DrawControls(DebugSystem debugSystem, IReadOnlyList<DebugControlEntry> entries)
    {
        foreach (var group in entries.GroupBy(entry => entry.Scope).OrderBy(group => group.Key, StringComparer.Ordinal))
        {
            var label = string.IsNullOrWhiteSpace(group.Key) ? "Global" : group.Key;
            if (!ImGui.TreeNodeEx(label, ImGuiTreeNodeFlags.DefaultOpen))
            {
                continue;
            }

            foreach (var entry in group)
            {
                DrawControl(debugSystem, entry);
            }

            ImGui.TreePop();
        }
    }

    private static void DrawControl(DebugSystem debugSystem, DebugControlEntry entry)
    {
        ImGui.PushID(entry.Path);
        switch (entry.Kind)
        {
            case DebugControlKind.Boolean:
            {
                var value = (bool)entry.Value;
                if (ImGui.Checkbox(entry.Name, ref value))
                {
                    debugSystem.SetControlValue(entry.Path, value);
                }

                break;
            }

            case DebugControlKind.Float:
            {
                var value = (float)entry.Value;
                if (ImGui.SliderFloat(entry.Name, ref value, entry.Min, entry.Max, "%.2f"))
                {
                    debugSystem.SetControlValue(entry.Path, value);
                }

                break;
            }

            case DebugControlKind.Enum:
            {
                var value = (int)entry.Value;
                var options = entry.Options ?? Array.Empty<string>();
                if (ImGui.Combo(entry.Name, ref value, options.ToArray(), options.Count))
                {
                    debugSystem.SetControlValue(entry.Path, value);
                }

                break;
            }

            case DebugControlKind.Button:
            {
                if (ImGui.Button(entry.Name))
                {
                    debugSystem.SetControlValue(entry.Path, true);
                }

                break;
            }
        }

        ImGui.PopID();
    }

    private static void DrawValues(IReadOnlyList<DebugValueEntry> entries)
    {
        foreach (var group in entries.GroupBy(entry => entry.Scope).OrderBy(group => group.Key, StringComparer.Ordinal))
        {
            var label = string.IsNullOrWhiteSpace(group.Key) ? "Global" : group.Key;
            if (!ImGui.TreeNodeEx(label, ImGuiTreeNodeFlags.DefaultOpen))
            {
                continue;
            }

            foreach (var entry in group)
            {
                ImGui.TextUnformatted($"{entry.Name}: {entry.Value}");
            }

            ImGui.TreePop();
        }
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        GL.DeleteTexture(fontTexture);
        GL.DeleteBuffer(indexBuffer);
        GL.DeleteBuffer(vertexBuffer);
        GL.DeleteVertexArray(vertexArray);
        GL.DeleteProgram(shader);
        ImGui.DestroyContext();
    }

    private int CreateFontTexture()
    {
        var io = ImGui.GetIO();
        io.Fonts.GetTexDataAsRGBA32(out IntPtr pixels, out var width, out var height, out _);

        var texture = GL.GenTexture();
        GL.BindTexture(TextureTarget.Texture2D, texture);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
        GL.TexImage2D(
            TextureTarget.Texture2D,
            level: 0,
            PixelInternalFormat.Rgba,
            width,
            height,
            border: 0,
            PixelFormat.Rgba,
            PixelType.UnsignedByte,
            pixels);

        io.Fonts.SetTexID((IntPtr)texture);
        io.Fonts.ClearTexData();
        return texture;
    }

    private unsafe void RenderDrawData(ImDrawDataPtr drawData, Vector2i framebufferSize)
    {
        var framebufferWidth = Math.Max(framebufferSize.X, 1);
        var framebufferHeight = Math.Max(framebufferSize.Y, 1);
        if (drawData.CmdListsCount == 0)
        {
            return;
        }

        drawData.ScaleClipRects(ImGui.GetIO().DisplayFramebufferScale);

        GL.Enable(EnableCap.Blend);
        GL.BlendEquation(BlendEquationMode.FuncAdd);
        GL.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
        GL.Disable(EnableCap.CullFace);
        GL.Disable(EnableCap.DepthTest);
        GL.Enable(EnableCap.ScissorTest);
        GL.ActiveTexture(TextureUnit.Texture0);

        GL.UseProgram(shader);
        GL.Uniform1(GL.GetUniformLocation(shader, "fontTexture"), 0);
        var projection = CreateProjection(drawData.DisplaySize.X, drawData.DisplaySize.Y);
        GL.UniformMatrix4(projectionLocation, false, ref projection);
        GL.BindVertexArray(vertexArray);

        for (var i = 0; i < drawData.CmdListsCount; i++)
        {
            var commandList = drawData.CmdLists[i];
            GL.BindBuffer(BufferTarget.ArrayBuffer, vertexBuffer);
            GL.BufferData(
                BufferTarget.ArrayBuffer,
                commandList.VtxBuffer.Size * Unsafe.SizeOf<ImDrawVert>(),
                commandList.VtxBuffer.Data,
                BufferUsageHint.StreamDraw);

            GL.BindBuffer(BufferTarget.ElementArrayBuffer, indexBuffer);
            GL.BufferData(
                BufferTarget.ElementArrayBuffer,
                commandList.IdxBuffer.Size * sizeof(ushort),
                commandList.IdxBuffer.Data,
                BufferUsageHint.StreamDraw);

            for (var commandIndex = 0; commandIndex < commandList.CmdBuffer.Size; commandIndex++)
            {
                var command = commandList.CmdBuffer[commandIndex];
                if (command.UserCallback != IntPtr.Zero)
                {
                    continue;
                }

                var clip = command.ClipRect;
                if (clip.X >= framebufferWidth || clip.Y >= framebufferHeight || clip.Z < 0.0f || clip.W < 0.0f)
                {
                    continue;
                }

                GL.BindTexture(TextureTarget.Texture2D, (int)command.TextureId);
                GL.Scissor(
                    (int)clip.X,
                    framebufferHeight - (int)clip.W,
                    (int)(clip.Z - clip.X),
                    (int)(clip.W - clip.Y));
                GL.DrawElementsBaseVertex(
                    PrimitiveType.Triangles,
                    (int)command.ElemCount,
                    DrawElementsType.UnsignedShort,
                    (IntPtr)(command.IdxOffset * sizeof(ushort)),
                    (int)command.VtxOffset);
            }
        }

        GL.Disable(EnableCap.ScissorTest);
        GL.BindVertexArray(0);
        GL.UseProgram(0);
    }

    private static Matrix4 CreateProjection(float width, float height)
    {
        return new Matrix4(
            2.0f / width, 0.0f, 0.0f, 0.0f,
            0.0f, 2.0f / -height, 0.0f, 0.0f,
            0.0f, 0.0f, -1.0f, 0.0f,
            -1.0f, 1.0f, 0.0f, 1.0f);
    }

    private static int CreateShader()
    {
        const string vertexSource = """
            #version 410 core

            layout(location = 0) in vec2 in_position;
            layout(location = 1) in vec2 in_texCoord;
            layout(location = 2) in vec4 in_color;

            uniform mat4 projection_matrix;

            out vec2 frag_uv;
            out vec4 frag_color;

            void main()
            {
                frag_uv = in_texCoord;
                frag_color = in_color;
                gl_Position = projection_matrix * vec4(in_position, 0.0, 1.0);
            }
            """;

        const string fragmentSource = """
            #version 410 core

            in vec2 frag_uv;
            in vec4 frag_color;

            uniform sampler2D fontTexture;

            out vec4 output_color;

            void main()
            {
                output_color = frag_color * texture(fontTexture, frag_uv);
            }
            """;

        var vertexShader = CompileShader(ShaderType.VertexShader, vertexSource);
        var fragmentShader = CompileShader(ShaderType.FragmentShader, fragmentSource);

        var program = GL.CreateProgram();
        GL.AttachShader(program, vertexShader);
        GL.AttachShader(program, fragmentShader);
        GL.LinkProgram(program);
        GL.GetProgram(program, GetProgramParameterName.LinkStatus, out var linked);
        if (linked == 0)
        {
            throw new InvalidOperationException(GL.GetProgramInfoLog(program));
        }

        GL.DetachShader(program, vertexShader);
        GL.DetachShader(program, fragmentShader);
        GL.DeleteShader(vertexShader);
        GL.DeleteShader(fragmentShader);
        return program;
    }

    private static int CompileShader(ShaderType type, string source)
    {
        var shader = GL.CreateShader(type);
        GL.ShaderSource(shader, source);
        GL.CompileShader(shader);
        GL.GetShader(shader, ShaderParameter.CompileStatus, out var compiled);
        if (compiled == 0)
        {
            throw new InvalidOperationException(GL.GetShaderInfoLog(shader));
        }

        return shader;
    }
}

using Blix.Graphics;
using OpenTK.Graphics.OpenGL4;

namespace Blix.Graphics.OpenGL;

public sealed partial class OpenGLGraphicsDevice
{
    public FrameDebugPacket Execute(RenderCommandList commandList)
    {
        ThrowIfDisposed();

        var passPackets = new List<FrameDebugPass>(commandList.Passes.Count);
        var totalDraws = 0;

        foreach (var pass in commandList.Passes)
        {
            var passPacket = ExecutePass(pass);
            passPackets.Add(passPacket);
            totalDraws += passPacket.Draws.Count;
        }

        return new FrameDebugPacket(
            TotalPasses: passPackets.Count,
            TotalDraws: totalDraws,
            Passes: passPackets);
    }

    private FrameDebugPass ExecutePass(RenderPass pass)
    {
        var (width, height) = BindRenderSurface(pass.Description.Target);
        ApplyPassDescription(pass.Description, currentColorAttachmentCount);

        var draws = new List<FrameDebugDraw>(pass.Commands.Count);

        foreach (var command in pass.Commands)
        {
            draws.Add(ExecuteCommand(command));
        }

        return new FrameDebugPass(
            pass.Name,
            pass.Description.Target,
            width,
            height,
            ClearedColor: AnyColorClear(pass.Description.ClearColors),
            ClearedDepth: pass.Description.ClearDepth,
            Draws: draws);
    }

    private static bool AnyColorClear(IReadOnlyList<GraphicsColor?> colors)
    {
        for (var i = 0; i < colors.Count; i++)
        {
            if (colors[i] is not null)
            {
                return true;
            }
        }

        return false;
    }

    private FrameDebugDraw ExecuteCommand(RenderCommand command)
    {
        return command switch
        {
            DrawIndexedCommand draw => ExecuteDrawIndexed(draw),
            _ => throw new NotSupportedException($"Unsupported render command: {command.GetType().Name}")
        };
    }

    private FrameDebugDraw ExecuteDrawIndexed(DrawIndexedCommand command)
    {
        if (!vertexBuffers.TryGetValue(command.VertexBuffer.Id, out var vertexBuffer))
        {
            throw new InvalidOperationException($"Unknown vertex buffer handle: {command.VertexBuffer.Id}");
        }

        if (!indexBuffers.TryGetValue(command.IndexBuffer.Id, out var indexBuffer))
        {
            throw new InvalidOperationException($"Unknown index buffer handle: {command.IndexBuffer.Id}");
        }

        if (!pipelines.TryGetValue(command.Pipeline.Id, out var pipeline))
        {
            throw new InvalidOperationException($"Unknown pipeline handle: {command.Pipeline.Id}");
        }

        if (!shaderPrograms.TryGetValue(pipeline.ShaderProgram.Id, out var shaderProgram))
        {
            throw new InvalidOperationException($"Unknown shader program handle: {pipeline.ShaderProgram.Id}");
        }

        if (command.IndexCount <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(command),
                $"DrawIndexed requires a positive IndexCount, got {command.IndexCount}.");
        }

        if (command.IndexOffset < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(command),
                $"DrawIndexed IndexOffset must be non-negative, got {command.IndexOffset}.");
        }

        if (command.IndexOffset + command.IndexCount > indexBuffer.Count)
        {
            throw new InvalidOperationException(
                $"DrawIndexed requested indices [{command.IndexOffset}, {command.IndexOffset + command.IndexCount}) but index buffer {command.IndexBuffer.Id} only holds {indexBuffer.Count}.");
        }

        ApplyPipelineState(pipeline);
        GL.UseProgram(shaderProgram.ProgramId);
        ApplyUniforms(shaderProgram, command.Uniforms);
        ApplyTextureBindings(shaderProgram, command.Textures);
        GL.BindVertexArray(vertexArray);
        GL.BindBuffer(BufferTarget.ArrayBuffer, vertexBuffer.Buffer);
        BindVertexLayout(pipeline.VertexLayout);
        GL.BindBuffer(BufferTarget.ElementArrayBuffer, indexBuffer.Buffer);
        // DrawElements byte-offset is element-offset * sizeof(element); the
        // element type + element width come from the buffer's recorded format.
        var (elementType, elementBytes) = indexBuffer.Format == IndexFormat.UInt32
            ? (DrawElementsType.UnsignedInt, sizeof(uint))
            : (DrawElementsType.UnsignedShort, sizeof(ushort));
        GL.DrawElements(MapPrimitiveTopology(pipeline.Topology), command.IndexCount, elementType, command.IndexOffset * elementBytes);
        GL.BindBuffer(BufferTarget.ElementArrayBuffer, 0);
        GL.BindBuffer(BufferTarget.ArrayBuffer, 0);
        GL.BindVertexArray(0);

        return BuildDrawDebug(command);
    }

    private (int Width, int Height) BindRenderSurface(RenderSurfaceHandle handle)
    {
        if (handle == RenderSurfaceHandle.Default)
        {
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
            GL.DrawBuffer(DrawBufferMode.Back);
            GL.ReadBuffer(ReadBufferMode.Back);
            GL.Viewport(0, 0, defaultSurfaceWidth, defaultSurfaceHeight);
            currentColorAttachmentCount = 1;
            return (defaultSurfaceWidth, defaultSurfaceHeight);
        }

        if (!renderSurfaces.TryGetValue(handle.Id, out var surface))
        {
            throw new InvalidOperationException($"Unknown render surface handle: {handle.Id}");
        }

        surface = EnsureRenderSurfaceResource(handle.Id, surface);
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, surface.Framebuffer);

        if (surface.DrawBuffers.Length == 0)
        {
            GL.DrawBuffer(DrawBufferMode.None);
            GL.ReadBuffer(ReadBufferMode.None);
        }
        else
        {
            GL.DrawBuffers(surface.DrawBuffers.Length, surface.DrawBuffers);
            GL.ReadBuffer(ReadBufferMode.ColorAttachment0);
        }

        GL.Viewport(0, 0, surface.Width, surface.Height);
        currentColorAttachmentCount = surface.ColorTextures.Count;
        return (surface.Width, surface.Height);
    }

    private static void ApplyPassDescription(RenderPassDescription description, int activeColorAttachmentCount)
    {
        var clears = description.ClearColors;
        var broadcast = clears.Count == 1 && activeColorAttachmentCount > 0;
        var attachmentLimit = broadcast ? activeColorAttachmentCount : clears.Count;

        for (var i = 0; i < attachmentLimit; i++)
        {
            var sourceIndex = broadcast ? 0 : i;

            if (clears[sourceIndex] is not { } color)
            {
                continue;
            }

            var components = new[] { color.Red, color.Green, color.Blue, color.Alpha };
            GL.ClearBuffer(ClearBuffer.Color, i, components);
        }

        if (description.ClearDepth)
        {
            // Depth clears must not inherit a previous pipeline's disabled depth write mask.
            GL.DepthMask(true);
            var depthValue = new[] { 1.0f };
            GL.ClearBuffer(ClearBuffer.Depth, 0, depthValue);
        }
    }

    private static FrameDebugDraw BuildDrawDebug(DrawIndexedCommand command)
    {
        var uniformNames = new string[command.Uniforms.Count];

        for (var i = 0; i < command.Uniforms.Count; i++)
        {
            uniformNames[i] = command.Uniforms[i].Name;
        }

        var textures = new FrameDebugTexture[command.Textures.Count];

        for (var i = 0; i < command.Textures.Count; i++)
        {
            var binding = command.Textures[i];
            textures[i] = new FrameDebugTexture(binding.Name, binding.Slot, binding.Texture);
        }

        return new FrameDebugDraw(
            command.Pipeline,
            command.VertexBuffer,
            command.IndexBuffer,
            command.IndexCount,
            uniformNames,
            textures);
    }
}

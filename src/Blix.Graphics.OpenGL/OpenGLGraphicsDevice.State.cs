using Blix.Graphics;
using OpenTK.Graphics.OpenGL4;

namespace Blix.Graphics.OpenGL;

public sealed partial class OpenGLGraphicsDevice
{
    private void ApplyPipelineState(PipelineResource pipeline)
    {
        ApplyDepthState(pipeline.Depth);
        ApplyRasterizerState(pipeline.Rasterizer);
        ApplyBlendStates(pipeline.ColorBlends, currentColorAttachmentCount);
    }

    private static void ApplyDepthState(DepthState state)
    {
        if (state.Enabled)
        {
            GL.Enable(EnableCap.DepthTest);
            GL.DepthFunc(MapDepthCompare(state.Compare));
        }
        else
        {
            GL.Disable(EnableCap.DepthTest);
        }

        GL.DepthMask(state.WriteEnabled);
    }

    private static void ApplyRasterizerState(RasterizerState state)
    {
        GL.FrontFace(MapFrontFace(state.FrontFace));

        if (state.CullMode == CullMode.None)
        {
            GL.Disable(EnableCap.CullFace);
            return;
        }

        GL.Enable(EnableCap.CullFace);
        GL.CullFace(MapCullMode(state.CullMode));
    }

    private static void ApplyBlendStates(IReadOnlyList<BlendState> blends, int activeAttachmentCount)
    {
        for (var i = 0; i < activeAttachmentCount; i++)
        {
            var state = i < blends.Count ? blends[i] : BlendState.Disabled;

            if (state.Enabled)
            {
                GL.Enable(IndexedEnableCap.Blend, (uint)i);
                GL.BlendFunc(i, BlendingFactorSrc.SrcAlpha, BlendingFactorDest.OneMinusSrcAlpha);
            }
            else
            {
                GL.Disable(IndexedEnableCap.Blend, (uint)i);
            }
        }
    }

    private void ApplyUniforms(ShaderProgramResource program, IReadOnlyList<ShaderUniform> uniforms)
    {
        foreach (var uniform in uniforms)
        {
            var location = program.GetUniformLocation(uniform.Name);

            if (location < 0)
            {
                continue;
            }

            ApplyUniform(location, uniform.Value);
        }
    }

    private void ApplyUniform(int location, ShaderUniformValue value)
    {
        switch (value)
        {
            case Matrix4x4Uniform matrix:
                WriteColumnMajor(matrix.Value, matrixUploadBuffer);
                GL.UniformMatrix4(location, 1, transpose: false, matrixUploadBuffer);
                break;

            case Matrix4x4ArrayUniform array:
                if (array.Value.Length > MatrixUploadBufferMatrices)
                {
                    throw new InvalidOperationException(
                        $"Matrix4x4ArrayUniform of size {array.Value.Length} exceeds device upload " +
                        $"buffer capacity ({MatrixUploadBufferMatrices}). Increase " +
                        $"MatrixUploadBufferMatrices or split the upload.");
                }
                for (var i = 0; i < array.Value.Length; i++)
                {
                    WriteColumnMajor(array.Value[i], matrixUploadBuffer.AsSpan(i * 16, 16));
                }
                GL.UniformMatrix4(location, array.Value.Length, transpose: false, matrixUploadBuffer);
                break;

            case Vector4Uniform v4:
                GL.Uniform4(location, v4.Value.X, v4.Value.Y, v4.Value.Z, v4.Value.W);
                break;

            case Vector3Uniform vector:
                GL.Uniform3(location, vector.Value.X, vector.Value.Y, vector.Value.Z);
                break;

            case Vector2Uniform v2:
                GL.Uniform2(location, v2.Value.X, v2.Value.Y);
                break;

            case FloatUniform f:
                GL.Uniform1(location, f.Value);
                break;

            default:
                throw new NotSupportedException($"Unsupported uniform value: {value.GetType().Name}");
        }
    }

    private void ApplyTextureBindings(ShaderProgramResource program, IReadOnlyList<ShaderTextureBinding> bindings)
    {
        foreach (var binding in bindings)
        {
            if (binding.Slot < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(bindings), "Texture binding slot must not be negative.");
            }

            if (!textures.TryGetValue(binding.Texture.Id, out var texture))
            {
                throw new InvalidOperationException($"Unknown texture handle: {binding.Texture.Id}");
            }

            var location = program.GetUniformLocation(binding.Name);

            if (location < 0)
            {
                continue;
            }

            GL.ActiveTexture(TextureUnit.Texture0 + binding.Slot);
            // Use the texture's own target (Texture2D for normal textures, TextureCubeMap
            // for cubemaps) so samplerCube uniforms read correctly.
            GL.BindTexture(texture.Target, texture.Texture);
            GL.Uniform1(location, binding.Slot);
        }
    }

    private static void BindVertexLayout(VertexLayout layout)
    {
        foreach (var attribute in layout.Attributes)
        {
            GL.VertexAttribPointer(
                attribute.Location,
                GetComponentCount(attribute.Format),
                VertexAttribPointerType.Float,
                false,
                layout.Stride,
                attribute.Offset);
            GL.EnableVertexAttribArray(attribute.Location);
        }
    }

    private static void WriteColumnMajor(System.Numerics.Matrix4x4 m, Span<float> dst)
    {
        dst[0] = m.M11; dst[1] = m.M21; dst[2] = m.M31; dst[3] = m.M41;
        dst[4] = m.M12; dst[5] = m.M22; dst[6] = m.M32; dst[7] = m.M42;
        dst[8] = m.M13; dst[9] = m.M23; dst[10] = m.M33; dst[11] = m.M43;
        dst[12] = m.M14; dst[13] = m.M24; dst[14] = m.M34; dst[15] = m.M44;
    }
}

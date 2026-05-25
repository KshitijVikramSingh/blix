using Blix.Graphics;
using OpenTK.Graphics.OpenGL4;

namespace Blix.Graphics.OpenGL;

public sealed partial class OpenGLGraphicsDevice
{
    // Debug-only global override: when set, every ApplyRasterizerState call
    // forces CullMode.None regardless of the pipeline's authored setting.
    // Lets a demo flip back-face culling off live (without rebuilding the
    // pipeline graph each frame) to A/B test whether missing geometry is
    // genuinely culled or caused by something else.
    public static bool DebugForceDisableCullFace { get; set; } = false;

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

        if (state.CullMode == CullMode.None || DebugForceDisableCullFace)
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
                switch (state.Mode)
                {
                    case BlendMode.Additive:
                        GL.BlendFunc(i, BlendingFactorSrc.One, BlendingFactorDest.One);
                        break;
                    case BlendMode.Alpha:
                    default:
                        GL.BlendFunc(i, BlendingFactorSrc.SrcAlpha, BlendingFactorDest.OneMinusSrcAlpha);
                        break;
                }
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
                // Engine convention is .NET row-vector form (F-016). Direct
                // memcpy of .NET's row-major bytes with transpose:false tells
                // GL "interpret these bytes as column-major" — which, applied
                // to .NET row-major storage, naturally reinterprets the
                // matrix as its TRANSPOSE = column-vector form in GLSL, which
                // is what `M * v_col` expects. Same operation Vulkan does
                // implicitly via std140 column-major reading of the UBO.
                WriteMatrixBytes(matrix.Value, matrixUploadBuffer);
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
                    WriteMatrixBytes(array.Value[i], matrixUploadBuffer.AsSpan(i * 16, 16));
                }
                GL.UniformMatrix4(location, array.Value.Length, transpose: false, matrixUploadBuffer);
                break;

            case Vector4Uniform v4:
                GL.Uniform4(location, v4.Value.X, v4.Value.Y, v4.Value.Z, v4.Value.W);
                break;

            case Vector3Uniform vector:
                GL.Uniform3(location, vector.Value.X, vector.Value.Y, vector.Value.Z);
                break;

            case Vector3ArrayUniform v3a:
            {
                // Flatten to a contiguous float[] — OpenTK's Uniform3 array overload
                // takes the *vec3 count*, not the float count, and reads 3*count floats.
                var flat = new float[v3a.Value.Length * 3];
                for (var i = 0; i < v3a.Value.Length; i++)
                {
                    flat[i * 3 + 0] = v3a.Value[i].X;
                    flat[i * 3 + 1] = v3a.Value[i].Y;
                    flat[i * 3 + 2] = v3a.Value[i].Z;
                }
                GL.Uniform3(location, v3a.Value.Length, flat);
                break;
            }

            case Vector2Uniform v2:
                GL.Uniform2(location, v2.Value.X, v2.Value.Y);
                break;

            case FloatUniform f:
                GL.Uniform1(location, f.Value);
                break;

            case FloatArrayUniform fa:
                GL.Uniform1(location, fa.Value.Length, fa.Value);
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

    // Copies .NET Matrix4x4's raw row-major bytes into the upload buffer
    // unchanged. Combined with GL.UniformMatrix4(transpose: false), GL
    // interprets the bytes as column-major from the GPU's perspective —
    // applied to .NET's row-major storage, that's the same byte-level
    // operation as taking the transpose, which converts the engine's
    // row-vector form into the column-vector form GLSL's `M * v_col`
    // expects. Symmetric with the Vulkan backend's direct UBO memcpy
    // (where GLSL's std140 column-major reading does the same job).
    private static void WriteMatrixBytes(System.Numerics.Matrix4x4 m, Span<float> dst)
    {
        var src = System.Runtime.InteropServices.MemoryMarshal.CreateReadOnlySpan(ref m, 1);
        var floats = System.Runtime.InteropServices.MemoryMarshal.Cast<System.Numerics.Matrix4x4, float>(src);
        floats.CopyTo(dst);
    }
}

using Blix.Graphics;
using OpenTK.Graphics.OpenGL4;

namespace Blix.Graphics.OpenGL;

public sealed partial class OpenGLGraphicsDevice
{
    private static int GetBytesPerPixel(TextureFormat format)
    {
        return format switch
        {
            TextureFormat.Rgba8 => 4,
            TextureFormat.R8 => 1,
            // Depth24 / Rgba16F are not user-uploadable; surfaces create them through
            // CreateRenderSurface as empty attachments.
            _ => throw new NotSupportedException($"Texture format {format} is not byte-uploadable.")
        };
    }

    private static int GetComponentCount(VertexAttributeFormat format)
    {
        return format switch
        {
            VertexAttributeFormat.Float2 => 2,
            VertexAttributeFormat.Float3 => 3,
            VertexAttributeFormat.Float4 => 4,
            _ => throw new NotSupportedException($"Unsupported vertex attribute format: {format}")
        };
    }

    private static BufferUsageHint MapBufferUsage(GraphicsBufferUsage usage)
    {
        return usage switch
        {
            GraphicsBufferUsage.Static => BufferUsageHint.StaticDraw,
            GraphicsBufferUsage.Dynamic => BufferUsageHint.DynamicDraw,
            GraphicsBufferUsage.Stream => BufferUsageHint.StreamDraw,
            _ => throw new NotSupportedException($"Unsupported buffer usage: {usage}")
        };
    }

    private static OpenTK.Graphics.OpenGL4.PrimitiveType MapPrimitiveTopology(Blix.Graphics.PrimitiveTopology topology)
    {
        return topology switch
        {
            Blix.Graphics.PrimitiveTopology.Triangles => OpenTK.Graphics.OpenGL4.PrimitiveType.Triangles,
            Blix.Graphics.PrimitiveTopology.Lines => OpenTK.Graphics.OpenGL4.PrimitiveType.Lines,
            _ => throw new NotSupportedException($"Unsupported primitive topology: {topology}")
        };
    }

    private static DepthFunction MapDepthCompare(DepthCompare compare)
    {
        return compare switch
        {
            DepthCompare.Less => DepthFunction.Less,
            DepthCompare.LessEqual => DepthFunction.Lequal,
            _ => throw new NotSupportedException($"Unsupported depth compare: {compare}")
        };
    }

    private static TriangleFace MapCullMode(CullMode cullMode)
    {
        return cullMode switch
        {
            CullMode.Back => TriangleFace.Back,
            CullMode.Front => TriangleFace.Front,
            _ => throw new NotSupportedException($"Unsupported cull mode: {cullMode}")
        };
    }

    private static OpenTK.Graphics.OpenGL4.FrontFaceDirection MapFrontFace(Blix.Graphics.FrontFace frontFace)
    {
        return frontFace switch
        {
            Blix.Graphics.FrontFace.CounterClockwise => OpenTK.Graphics.OpenGL4.FrontFaceDirection.Ccw,
            Blix.Graphics.FrontFace.Clockwise => OpenTK.Graphics.OpenGL4.FrontFaceDirection.Cw,
            _ => throw new NotSupportedException($"Unsupported front face: {frontFace}")
        };
    }

    private static TextureMinFilter MapMinFilter(TextureFilter filter, bool generateMipmaps)
    {
        return (filter, generateMipmaps) switch
        {
            (TextureFilter.Nearest, false) => TextureMinFilter.Nearest,
            (TextureFilter.Linear, false) => TextureMinFilter.Linear,
            (TextureFilter.Nearest, true) => TextureMinFilter.NearestMipmapNearest,
            (TextureFilter.Linear, true) => TextureMinFilter.LinearMipmapLinear,
            _ => throw new NotSupportedException($"Unsupported texture min filter: {filter}")
        };
    }

    private static TextureMagFilter MapMagFilter(TextureFilter filter)
    {
        return filter switch
        {
            TextureFilter.Nearest => TextureMagFilter.Nearest,
            TextureFilter.Linear => TextureMagFilter.Linear,
            _ => throw new NotSupportedException($"Unsupported texture mag filter: {filter}")
        };
    }

    private static TextureWrapMode MapTextureWrap(TextureWrap wrap)
    {
        return wrap switch
        {
            TextureWrap.Repeat => TextureWrapMode.Repeat,
            TextureWrap.ClampToEdge => TextureWrapMode.ClampToEdge,
            _ => throw new NotSupportedException($"Unsupported texture wrap: {wrap}")
        };
    }

    private static PixelInternalFormat MapPixelInternalFormat(TextureFormat format)
    {
        return format switch
        {
            TextureFormat.Rgba8 => PixelInternalFormat.Rgba8,
            TextureFormat.Depth24 => PixelInternalFormat.DepthComponent24,
            TextureFormat.Rgba16F => PixelInternalFormat.Rgba16f,
            TextureFormat.R8 => PixelInternalFormat.R8,
            _ => throw new NotSupportedException($"Unsupported texture format: {format}")
        };
    }

    private static PixelFormat MapPixelFormat(TextureFormat format)
    {
        return format switch
        {
            TextureFormat.Rgba8 => PixelFormat.Rgba,
            TextureFormat.Depth24 => PixelFormat.DepthComponent,
            TextureFormat.Rgba16F => PixelFormat.Rgba,
            TextureFormat.R8 => PixelFormat.Red,
            _ => throw new NotSupportedException($"Unsupported texture format: {format}")
        };
    }

    private static PixelType MapPixelType(TextureFormat format)
    {
        // Type tag used at TexImage2D upload time. For Rgba16F we use HalfFloat so the
        // empty initial upload (IntPtr.Zero data) doesn't pretend the storage is bytes.
        return format switch
        {
            TextureFormat.Rgba8 => PixelType.UnsignedByte,
            TextureFormat.Depth24 => PixelType.UnsignedInt,
            TextureFormat.Rgba16F => PixelType.HalfFloat,
            TextureFormat.R8 => PixelType.UnsignedByte,
            _ => throw new NotSupportedException($"Unsupported texture format: {format}")
        };
    }

    private static ShaderType MapShaderStage(ShaderStage stage)
    {
        return stage switch
        {
            ShaderStage.Vertex => ShaderType.VertexShader,
            ShaderStage.Fragment => ShaderType.FragmentShader,
            _ => throw new NotSupportedException($"Unsupported shader stage: {stage}")
        };
    }
}

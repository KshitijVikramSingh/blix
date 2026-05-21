using Blix.Graphics;
using OpenTK.Graphics.OpenGL4;

namespace Blix.Graphics.OpenGL;

public sealed partial class OpenGLGraphicsDevice : IGraphicsDevice, IRenderer
{
    private readonly Dictionary<int, VertexBufferResource> vertexBuffers = [];
    private readonly Dictionary<int, IndexBufferResource> indexBuffers = [];
    private readonly Dictionary<int, ShaderProgramResource> shaderPrograms = [];
    private readonly Dictionary<int, PipelineResource> pipelines = [];
    private readonly Dictionary<int, TextureResource> textures = [];
    private readonly Dictionary<int, RenderSurfaceResource> renderSurfaces = [];
    // Sized to handle both single-matrix and array uploads. 64 mat4 = 1024 floats =
    // 4 KB — covers the skinning bone-palette max without per-frame allocation.
    // Single-matrix uniforms still upload from the first 16 floats; OpenGL reads only
    // as many as the `count` parameter to UniformMatrix4 specifies.
    private const int MatrixUploadBufferMatrices = 64;
    private readonly float[] matrixUploadBuffer = new float[MatrixUploadBufferMatrices * 16];
    private readonly bool debugLabelsSupported;
    private readonly int vertexArray;
    private int defaultSurfaceWidth = 1;
    private int defaultSurfaceHeight = 1;
    private int currentColorAttachmentCount = 1;
    private bool disposed;
    private int nextHandle = 1;

    public OpenGLGraphicsDevice()
    {
        vertexArray = GL.GenVertexArray();
        Info = new GraphicsDeviceInfo(
            GetString(StringName.Vendor),
            GetString(StringName.Renderer),
            GetString(StringName.Version),
            GetString(StringName.ShadingLanguageVersion));
        debugLabelsSupported = DetectExtension("GL_KHR_debug");
    }

    public GraphicsDeviceInfo Info { get; }

    public void SetDefaultRenderSurfaceSize(int width, int height)
    {
        ThrowIfDisposed();
        var nextWidth = Math.Max(width, 1);
        var nextHeight = Math.Max(height, 1);

        if (defaultSurfaceWidth == nextWidth && defaultSurfaceHeight == nextHeight)
        {
            return;
        }

        defaultSurfaceWidth = nextWidth;
        defaultSurfaceHeight = nextHeight;

        foreach (var (id, surface) in renderSurfaces.ToArray())
        {
            if (surface.Description.Size is MatchDefaultRenderSurfaceSize)
            {
                renderSurfaces[id] = surface with { Dirty = true };
            }
        }
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        foreach (var resource in vertexBuffers.Values)
        {
            GL.DeleteBuffer(resource.Buffer);
        }

        foreach (var program in shaderPrograms.Values)
        {
            GL.DeleteProgram(program.ProgramId);
        }

        foreach (var resource in indexBuffers.Values)
        {
            GL.DeleteBuffer(resource.Buffer);
        }

        foreach (var resource in textures.Values)
        {
            GL.DeleteTexture(resource.Texture);
        }

        foreach (var resource in renderSurfaces.Values)
        {
            DeleteRenderSurfaceResource(resource);
        }

        GL.DeleteVertexArray(vertexArray);
        vertexBuffers.Clear();
        indexBuffers.Clear();
        shaderPrograms.Clear();
        pipelines.Clear();
        textures.Clear();
        renderSurfaces.Clear();
        disposed = true;
    }

    private int NextHandle()
    {
        return nextHandle++;
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
    }

    private static string GetString(StringName name)
    {
        return GL.GetString(name) ?? "unknown";
    }

    private static bool DetectExtension(string name)
    {
        var count = GL.GetInteger(GetPName.NumExtensions);

        for (var i = 0; i < count; i++)
        {
            if (string.Equals(GL.GetString(StringNameIndexed.Extensions, i), name, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private void ApplyDebugLabel(ObjectLabelIdentifier kind, int glName, string label)
    {
        if (!debugLabelsSupported || string.IsNullOrEmpty(label))
        {
            return;
        }

        GL.ObjectLabel(kind, glName, label.Length, label);
    }
}

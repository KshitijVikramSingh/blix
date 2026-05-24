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
    // Capability flags used by Stage 2's debug callback + pass-group plumbing.
    // KHR_debug is the modern path; ARB_debug_output is the older one Apple GL
    // 4.1 ships. We pick whichever is available at init.
    private readonly bool khrDebugSupported;
    private readonly bool arbDebugOutputSupported;
    // Keep the callback delegate alive — OpenTK passes it to native code and
    // does NOT pin it. If we let the GC collect, the next driver invocation
    // SIGSEGVs through a dangling function pointer.
    private DebugProc? debugProcDelegate;
    private OpenTK.Graphics.OpenGL.DebugProcArb? debugProcArbDelegate;
    private readonly int vertexArray;
    private int defaultSurfaceWidth = 1;
    private int defaultSurfaceHeight = 1;
    private int currentColorAttachmentCount = 1;
    private bool disposed;
    private int nextHandle = 1;

    // Per-frame error counter, surfaced to the debug HUD. Reset at the start
    // of every Execute() call so a HUD line that says "GL errors: 7" means
    // "this frame's command list produced 7 errors", not "all-time total".
    private int frameErrorCount;
    private string lastErrorContext = string.Empty;
    private string lastErrorMessage = string.Empty;

    public DiagnosticsMode Diagnostics { get; }

    public int FrameErrorCount => frameErrorCount;

    public string LastErrorContext => lastErrorContext;

    public string LastErrorMessage => lastErrorMessage;

    public GraphicsDeviceDiagnostics DiagnosticsSnapshot =>
        new(frameErrorCount, lastErrorContext, lastErrorMessage);

    public OpenGLGraphicsDevice()
        : this(ResolveDefaultDiagnosticsMode())
    {
    }

    public OpenGLGraphicsDevice(DiagnosticsMode diagnosticsMode)
    {
        // BLIX_GL_STRICT=1 in the environment hard-overrides the caller's
        // choice to ThrowOnFirst. Lets the user flip strict mode on for any
        // demo without recompiling — match the convention used by other
        // GL strict-mode env vars (e.g., Mesa's MESA_DEBUG, NVIDIA's __GL_DEBUG).
        Diagnostics = ResolveStrictOverride(diagnosticsMode);

        vertexArray = GL.GenVertexArray();
        Info = new GraphicsDeviceInfo(
            GetString(StringName.Vendor),
            GetString(StringName.Renderer),
            GetString(StringName.Version),
            GetString(StringName.ShadingLanguageVersion));
        khrDebugSupported = DetectExtension("GL_KHR_debug");
        arbDebugOutputSupported = DetectExtension("GL_ARB_debug_output");
        // Object labels (glObjectLabel) come from KHR_debug specifically.
        // ARB_debug_output is the older Apple-GL-4.1 path and does NOT include
        // glObjectLabel, so label calls stay gated on khrDebugSupported.
        debugLabelsSupported = khrDebugSupported;

        if (Diagnostics != DiagnosticsMode.Off)
        {
            InstallDebugCallback();
        }
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

    private static DiagnosticsMode ResolveDefaultDiagnosticsMode()
    {
        // Debug builds default to WarnOnly: surfacing silent failures early
        // is the explicit goal of this subsystem. Release builds default to
        // Off so glGetError pumping doesn't show up in profiles.
#if DEBUG
        return DiagnosticsMode.WarnOnly;
#else
        return DiagnosticsMode.Off;
#endif
    }

    private static DiagnosticsMode ResolveStrictOverride(DiagnosticsMode requested)
    {
        var strict = Environment.GetEnvironmentVariable("BLIX_GL_STRICT");
        if (!string.IsNullOrEmpty(strict) && (strict == "1" || strict.Equals("true", StringComparison.OrdinalIgnoreCase)))
        {
            return DiagnosticsMode.ThrowOnFirst;
        }
        return requested;
    }

    // Records the most-recent error observation so the HUD can surface it.
    // Called from both the per-pass glGetError pump and the async debug
    // callback so either path keeps the HUD line current.
    private void RecordError(string contextLabel, string message)
    {
        frameErrorCount++;
        lastErrorContext = contextLabel;
        lastErrorMessage = message;
    }

    // Install the GL debug-message callback. Prefers KHR_debug (sync via
    // glDebugMessageControl + DebugOutputSynchronous). Falls back to
    // ARB_debug_output on Apple GL 4.1 where KHR_debug isn't exposed.
    private void InstallDebugCallback()
    {
        if (khrDebugSupported)
        {
            debugProcDelegate = OnDebugMessage;
            GL.Enable(EnableCap.DebugOutput);
            GL.Enable(EnableCap.DebugOutputSynchronous);
            GL.DebugMessageCallback(debugProcDelegate, IntPtr.Zero);
            // Empty filter = "all messages enabled". The driver still filters
            // by severity at its end; we ask for everything and let the
            // callback decide what to surface based on our policy.
            GL.DebugMessageControl(DebugSourceControl.DontCare, DebugTypeControl.DontCare, DebugSeverityControl.DontCare, 0, (int[]?)null, true);
            return;
        }

        if (arbDebugOutputSupported)
        {
            debugProcArbDelegate = OnArbDebugMessage;
            // ARB_debug_output uses GL_DEBUG_OUTPUT_SYNCHRONOUS_ARB (same numeric
            // value as the KHR equivalent). OpenTK exposes the Arb-side entry
            // point under GL.Arb.
            OpenTK.Graphics.OpenGL.GL.Arb.DebugMessageCallback(debugProcArbDelegate, IntPtr.Zero);
        }
    }

    // KHR_debug callback. Source/type/severity decoded through GLDiagnostics so
    // the wire format matches the GL_CAPS spelling used in driver docs.
    private void OnDebugMessage(DebugSource source, DebugType type, int id, DebugSeverity severity, int length, IntPtr message, IntPtr userParam)
    {
        var text = System.Runtime.InteropServices.Marshal.PtrToStringAnsi(message, length) ?? string.Empty;
        var formatted = $"[GL/{GLDiagnostics.DecodeSource(source)}/{GLDiagnostics.DecodeType(type)}/{GLDiagnostics.DecodeSeverity(severity)}] id={id}: {text}";
        DispatchDebugCallback(type, severity, formatted);
    }

    // ARB_debug_output callback. OpenTK 4.9 declares DebugProcArb using the same
    // KHR-style enums as DebugProc, but in the OpenTK.Graphics.OpenGL (not OpenGL4)
    // namespace — re-cast to the OpenGL4 form so the same decoders work.
    private void OnArbDebugMessage(OpenTK.Graphics.OpenGL.DebugSource source, OpenTK.Graphics.OpenGL.DebugType type, int id, OpenTK.Graphics.OpenGL.DebugSeverity severity, int length, IntPtr message, IntPtr userParam)
    {
        var text = System.Runtime.InteropServices.Marshal.PtrToStringAnsi(message, length) ?? string.Empty;
        var khrSource = (DebugSource)(int)source;
        var khrType = (DebugType)(int)type;
        var khrSeverity = (DebugSeverity)(int)severity;
        var formatted = $"[GL/ARB/{GLDiagnostics.DecodeSource(khrSource)}/{GLDiagnostics.DecodeType(khrType)}/{GLDiagnostics.DecodeSeverity(khrSeverity)}] id={id}: {text}";
        DispatchDebugCallback(khrType, khrSeverity, formatted);
    }

    // Shared callback dispatcher: applies the DiagnosticsMode policy. Errors
    // (type=Error OR severity=High) drive the throw decision in ThrowOnFirst;
    // notification-severity chatter is dropped even in WarnOnly so the console
    // signal-to-noise stays high.
    private void DispatchDebugCallback(DebugType type, DebugSeverity severity, string formatted)
    {
        if (severity == DebugSeverity.DebugSeverityNotification)
        {
            return;
        }

        var isError = type == DebugType.DebugTypeError || severity == DebugSeverity.DebugSeverityHigh;
        if (isError)
        {
            RecordError("callback", formatted);
        }

        switch (Diagnostics)
        {
            case DiagnosticsMode.Off:
                return;
            case DiagnosticsMode.WarnOnly:
                Console.WriteLine(formatted);
                return;
            case DiagnosticsMode.ThrowOnFirst:
                Console.WriteLine(formatted);
                if (isError)
                {
                    throw new InvalidOperationException(formatted);
                }
                return;
        }
    }
}

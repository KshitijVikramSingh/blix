using OpenTK.Graphics.OpenGL4;

namespace Blix.Graphics.OpenGL;

// What the device does when a GL error or debug-callback message is observed.
// Picked at device init (defaults vary by build flavour) and overridable at
// runtime via the BLIX_GL_STRICT env var — see OpenGLGraphicsDevice ctor.
//
// Off          — no glGetError pumping, no debug callback attached. Release default.
// WarnOnly     — log each observation to Console with pass-level context. Debug default.
// ThrowOnFirst — throw InvalidOperationException at the first non-NoError. Useful
//                for CI / strict-mode local runs that want failures to bubble up
//                immediately so we can fix the silent-failure bug class.
public enum DiagnosticsMode
{
    Off,
    WarnOnly,
    ThrowOnFirst,
}

internal static class GLDiagnostics
{
    // Drains the GL error queue. Apple's GL 4.1 driver coalesces some error
    // bits, so a single offending call can still leave more than one entry
    // queued — pump in a loop until NoError is reported. Returns the number of
    // errors observed; the policy callback fires once per error with the
    // decoded enum name attached.
    //
    // Caller owns deciding what context (pass label, etc.) to surface; this
    // function is intentionally pass-agnostic so the same code can serve the
    // per-pass pump in Execute.cs and any future hot-path checks.
    public static int Drain(DiagnosticsMode mode, string contextLabel, Action<string>? onError = null)
    {
        if (mode == DiagnosticsMode.Off)
        {
            return 0;
        }

        var count = 0;
        // Cap the drain loop in case a misconfigured driver returns the same
        // error every call (we've seen this in macOS-corner-case bugs). 16
        // iterations is enough to find the first 3-4 errors we care about
        // without livelocking.
        for (var guard = 0; guard < 16; guard++)
        {
            var code = GL.GetError();
            if (code == ErrorCode.NoError)
            {
                break;
            }

            count++;
            var name = DecodeError(code);
            var message = $"[GL] {contextLabel}: {name} (0x{(int)code:X4})";

            switch (mode)
            {
                case DiagnosticsMode.WarnOnly:
                    Console.WriteLine(message);
                    onError?.Invoke(message);
                    break;
                case DiagnosticsMode.ThrowOnFirst:
                    onError?.Invoke(message);
                    throw new InvalidOperationException(message);
            }
        }

        return count;
    }

    // Human-readable name for a GL error enum. OpenTK's ErrorCode.ToString()
    // already returns reasonable PascalCase names; we re-spell them in the
    // GL_ALL_CAPS form because that's what driver docs / Apple's GL traces /
    // RenderDoc tags use, which makes Googling the exact symbol easier.
    public static string DecodeError(ErrorCode code) => code switch
    {
        ErrorCode.NoError => "GL_NO_ERROR",
        ErrorCode.InvalidEnum => "GL_INVALID_ENUM",
        ErrorCode.InvalidValue => "GL_INVALID_VALUE",
        ErrorCode.InvalidOperation => "GL_INVALID_OPERATION",
        ErrorCode.OutOfMemory => "GL_OUT_OF_MEMORY",
        ErrorCode.InvalidFramebufferOperation => "GL_INVALID_FRAMEBUFFER_OPERATION",
        ErrorCode.ContextLost => "GL_CONTEXT_LOST",
        ErrorCode.TableTooLarge => "GL_TABLE_TOO_LARGE",
        _ => $"GL_UNKNOWN(0x{(int)code:X4})",
    };

    // KHR_debug source/type/severity decoders. The callback gets each of these
    // as an int; the user sees a string in their console. We keep this in one
    // place so the messages look consistent whether they came from glGetError
    // pumping or from the asynchronous debug callback.
    public static string DecodeSource(DebugSource source) => source switch
    {
        DebugSource.DebugSourceApi => "API",
        DebugSource.DebugSourceWindowSystem => "WINDOW",
        DebugSource.DebugSourceShaderCompiler => "SHADER",
        DebugSource.DebugSourceThirdParty => "THIRD_PARTY",
        DebugSource.DebugSourceApplication => "APP",
        DebugSource.DebugSourceOther => "OTHER",
        _ => source.ToString(),
    };

    public static string DecodeType(DebugType type) => type switch
    {
        DebugType.DebugTypeError => "ERROR",
        DebugType.DebugTypeDeprecatedBehavior => "DEPRECATED",
        DebugType.DebugTypeUndefinedBehavior => "UB",
        DebugType.DebugTypePortability => "PORTABILITY",
        DebugType.DebugTypePerformance => "PERF",
        DebugType.DebugTypeMarker => "MARKER",
        DebugType.DebugTypePushGroup => "PUSH",
        DebugType.DebugTypePopGroup => "POP",
        DebugType.DebugTypeOther => "OTHER",
        _ => type.ToString(),
    };

    public static string DecodeSeverity(DebugSeverity severity) => severity switch
    {
        DebugSeverity.DebugSeverityHigh => "HIGH",
        DebugSeverity.DebugSeverityMedium => "MED",
        DebugSeverity.DebugSeverityLow => "LOW",
        DebugSeverity.DebugSeverityNotification => "NOTE",
        _ => severity.ToString(),
    };
}

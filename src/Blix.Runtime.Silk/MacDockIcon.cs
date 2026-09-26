using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Blix.Graphics.Images;
using Silk.NET.Core;

namespace Blix.Runtime.Silk;

/// <summary>
/// Sets the macOS dock icon, which is the only icon a macOS application has.
/// </summary>
/// <remarks>
/// <b>Why this exists beside <c>SetWindowIcon</c>.</b> GLFW has no window icon on macOS: windows
/// there carry no icon in their title bar, so <c>glfwSetWindowIcon</c> is documented to do nothing
/// and returns without error. The icon a person actually sees is the dock tile, and that normally
/// comes from an application bundle's <c>.icns</c> &mdash; which a <c>dotnet</c> apphost run from a
/// terminal does not have, so it shows the generic executable icon instead.
/// <para>
/// <c>NSApplication.setApplicationIconImage:</c> overrides the dock tile at runtime, which is the
/// one path that works for a binary launched the way Blix applications are launched. Everything
/// here is guarded: a failure costs an icon, never a start-up.
/// </para>
/// </remarks>
[SupportedOSPlatform("macos")]
internal static class MacDockIcon
{
    private const string Objc = "/usr/lib/libobjc.A.dylib";

    [DllImport(Objc, CharSet = CharSet.Ansi)]
    private static extern IntPtr objc_getClass(string name);

    [DllImport(Objc, CharSet = CharSet.Ansi)]
    private static extern IntPtr sel_registerName(string name);

    [DllImport(Objc, EntryPoint = "objc_msgSend")]
    private static extern IntPtr Send(IntPtr receiver, IntPtr selector);

    [DllImport(Objc, EntryPoint = "objc_msgSend")]
    private static extern IntPtr Send(IntPtr receiver, IntPtr selector, IntPtr arg);

    [DllImport(Objc, EntryPoint = "objc_msgSend")]
    private static extern IntPtr Send(IntPtr receiver, IntPtr selector, IntPtr bytes, nuint length);

    /// <summary>
    /// Replaces the dock tile with <paramref name="image"/>. Returns false if anything was missing,
    /// which is the expected answer when AppKit is not loaded.
    /// </summary>
    /// <remarks>
    /// Must run on the main thread; it is called during window construction, which is where Blix
    /// already requires the caller to be. Every argument below is pointer-sized, so the calls use
    /// the simplest possible message-send shape rather than a hand-written struct-passing one.
    /// </remarks>
    internal static bool TrySet(RawImage image)
    {
        try
        {
            var nsData = objc_getClass("NSData");
            var nsImage = objc_getClass("NSImage");
            var nsApplication = objc_getClass("NSApplication");

            // AppKit is linked by the windowing backend. If it somehow is not, these come back
            // null and there is no dock to talk to.
            if (nsData == IntPtr.Zero || nsImage == IntPtr.Zero || nsApplication == IntPtr.Zero) return false;

            var png = PngWriter.EncodeRgba8(image.Pixels.Span, image.Width, image.Height);

            IntPtr data;
            unsafe
            {
                fixed (byte* bytes = png)
                {
                    // dataWithBytes:length: copies, so the pin only has to outlive this call.
                    data = Send(nsData, sel_registerName("dataWithBytes:length:"), (IntPtr)bytes, (nuint)png.Length);
                }
            }

            if (data == IntPtr.Zero) return false;

            var allocated = Send(nsImage, sel_registerName("alloc"));
            if (allocated == IntPtr.Zero) return false;

            var icon = Send(allocated, sel_registerName("initWithData:"), data);
            if (icon == IntPtr.Zero) return false;

            // sharedApplication creates NSApp if the backend has not already.
            var app = Send(nsApplication, sel_registerName("sharedApplication"));
            if (app == IntPtr.Zero) return false;

            Send(app, sel_registerName("setApplicationIconImage:"), icon);
            return true;
        }
        catch (DllNotFoundException)
        {
            return false;
        }
        catch (EntryPointNotFoundException)
        {
            return false;
        }
    }
}

using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Silk.NET.Windowing;

namespace Blix.Runtime.Silk;

/// <summary>
/// Extended dynamic range on macOS: whether the window's Metal layer is asking for it, and how much headroom the display
/// is currently granting.
/// </summary>
/// <remarks>
/// <b>Why this exists.</b> An HDR swapchain (RGBA16F in extended-linear sRGB) only lights the panel above SDR white if
/// the window's <c>CAMetalLayer</c> has <c>wantsExtendedDynamicRangeContent</c> set; otherwise values above 1.0 clip to
/// white however correct the surface format is. Whether MoltenVK sets it, for the colour space asked for, is not
/// something the Vulkan API tells you, so this looks at the layer and turns it on, and reports what it found.
/// <para>
/// Headroom is the ratio the display can currently exceed SDR white by (<c>NSScreen</c>'s
/// <c>maximumExtendedDynamicRangeColorComponentValue</c>): it reads 1.0 until something on screen is using EDR, and it
/// moves with the brightness setting. The double-returning message send is the arm64 form of <c>objc_msgSend</c> (x86-64
/// needs <c>objc_msgSend_fpret</c>), and the build here is Apple silicon; elsewhere headroom reads 1.0 rather than risk it.
/// Every call is guarded: a failure costs a log line, never a start-up.
/// </para>
/// </remarks>
[SupportedOSPlatform("macos")]
internal static class MacEdr
{
    private const string Objc = "/usr/lib/libobjc.A.dylib";

    [DllImport(Objc, CharSet = CharSet.Ansi)]
    private static extern IntPtr objc_getClass(string name);

    [DllImport(Objc, CharSet = CharSet.Ansi)]
    private static extern IntPtr sel_registerName(string name);

    [DllImport(Objc)]
    private static extern IntPtr object_getClass(IntPtr obj);

    [DllImport(Objc)]
    private static extern IntPtr class_getName(IntPtr cls);

    [DllImport(Objc, EntryPoint = "objc_msgSend")]
    private static extern IntPtr Send(IntPtr receiver, IntPtr selector);

    [DllImport(Objc, EntryPoint = "objc_msgSend")]
    private static extern byte SendByte(IntPtr receiver, IntPtr selector);

    [DllImport(Objc, EntryPoint = "objc_msgSend")]
    private static extern void SendBool(IntPtr receiver, IntPtr selector, byte arg);

    [DllImport(Objc, EntryPoint = "objc_msgSend")]
    private static extern nuint SendUInt(IntPtr receiver, IntPtr selector);

    [DllImport(Objc, EntryPoint = "objc_msgSend")]
    private static extern double SendDouble(IntPtr receiver, IntPtr selector);

    private static IntPtr Sel(string name) => sel_registerName(name);

    private static string ClassName(IntPtr obj) =>
        obj == IntPtr.Zero ? "(null)" : Marshal.PtrToStringAnsi(class_getName(object_getClass(obj))) ?? "(unnamed)";

    private static string Describe(IntPtr obj)
    {
        if (obj == IntPtr.Zero) return "(null)";
        var text = Send(Send(obj, Sel("description")), Sel("UTF8String"));
        return text == IntPtr.Zero ? ClassName(obj) : Marshal.PtrToStringUTF8(text) ?? ClassName(obj);
    }

    /// <summary>The window's Metal layer, or zero if the window is not what this expects.</summary>
    private static IntPtr MetalLayer(IWindow window)
    {
        var cocoa = window.Native?.Cocoa;
        if (cocoa is null || cocoa.Value == IntPtr.Zero) return IntPtr.Zero;
        var view = Send(cocoa.Value, Sel("contentView"));
        if (view == IntPtr.Zero) return IntPtr.Zero;
        var layer = Send(view, Sel("layer"));
        return ClassName(layer).Contains("CAMetalLayer", StringComparison.Ordinal) ? layer : IntPtr.Zero;
    }

    /// <summary>
    /// Turns <c>wantsExtendedDynamicRangeContent</c> on for the window's Metal layer and describes what it found, for the log.
    /// </summary>
    internal static string EnableExtendedRange(IWindow window)
    {
        try
        {
            var layer = MetalLayer(window);
            if (layer == IntPtr.Zero) return "EDR: the window's layer is not a CAMetalLayer (or has no Cocoa handle); nothing set.";
            var before = SendByte(layer, Sel("wantsExtendedDynamicRangeContent")) != 0;
            SendBool(layer, Sel("setWantsExtendedDynamicRangeContent:"), 1);
            var after = SendByte(layer, Sel("wantsExtendedDynamicRangeContent")) != 0;
            var pixelFormat = SendUInt(layer, Sel("pixelFormat"));  // MTLPixelFormat: 115 is RGBA16Float, 80 BGRA8Unorm
            var colorSpace = Describe(Send(layer, Sel("colorspace")));
            return $"EDR: layer wantsExtendedDynamicRangeContent {before} -> {after}; pixelFormat {pixelFormat}; colorspace {colorSpace}; " +
                   $"headroom now {Headroom():F2}, potential {PotentialHeadroom():F2}";
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or InvalidOperationException)
        {
            return $"EDR: could not reach the layer ({e.GetType().Name}).";
        }
    }

    /// <summary>The headroom the main display is granting now: 1.0 means none.</summary>
    internal static double Headroom() => ReadScreen("maximumExtendedDynamicRangeColorComponentValue");

    /// <summary>The most headroom the display could grant (it depends on the brightness setting).</summary>
    internal static double PotentialHeadroom() => ReadScreen("maximumPotentialExtendedDynamicRangeColorComponentValue");

    private static double ReadScreen(string property)
    {
        if (RuntimeInformation.ProcessArchitecture != Architecture.Arm64) return 1.0;
        try
        {
            var screen = Send(objc_getClass("NSScreen"), Sel("mainScreen"));
            return screen == IntPtr.Zero ? 1.0 : SendDouble(screen, Sel(property));
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            return 1.0;
        }
    }
}

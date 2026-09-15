using System.Runtime.InteropServices;

namespace Blix.Recipes;

// P/Invoke into vendored bc7enc (third_party/bc7enc + blix_bc7.cpp wrapper,
// built to libblix_bc7.dylib next to the cook by the BuildBc7 MSBuild target).
// Cook-time only — turns RGBA8 mips into raw BC7 blocks. Replaces the slow
// managed BCnEncoder.Net path (minutes per 4K texture) so BC7 cooking is fast
// enough to be the default. Falls back to BCnEncoder.Net when the dylib is
// unavailable (non-macOS, or clang build skipped).
public static unsafe class Bc7Native
{
    private const string Lib = "blix_bc7";

    static Bc7Native()
    {
        NativeLibrary.SetDllImportResolver(typeof(Bc7Native).Assembly, (name, asm, search) =>
        {
            if (name != Lib) return IntPtr.Zero;
            return NativeLibrary.Load(LibPath);
        });
    }

    private static string LibPath => Path.Combine(AppContext.BaseDirectory, "libblix_bc7.dylib");

    // True when the native encoder is present and loadable. Probed once; the
    // cook uses this to decide between the fast native path and the managed
    // fallback.
    private static readonly bool available = ProbeAvailable();
    public static bool Available => available;

    private static bool ProbeAvailable()
    {
        try
        {
            if (!File.Exists(LibPath)) return false;
            Init();
            return true;
        }
        catch
        {
            return false;
        }
    }

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, EntryPoint = "blix_bc7_init")]
    private static extern void blix_bc7_init();

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, EntryPoint = "blix_bc7_encode")]
    private static extern void blix_bc7_encode(
        byte* src, int width, int height,
        byte* dst, int perceptual, int quality, int numThreads);

    public static void Init() => blix_bc7_init();

    // Encode a tightly-packed RGBA8 image (R first) to raw BC7 blocks. perceptual
    // selects YCbCr weights (sRGB color maps) vs linear (normal / data maps).
    // quality: 0 fastest, 1 balanced, 2 high. numThreads <= 1 keeps it single-
    // threaded (the cook already parallelises across textures).
    public static byte[] EncodeImage(
        byte[] rgba, int width, int height, bool perceptual, int quality, int numThreads = 1)
    {
        ArgumentNullException.ThrowIfNull(rgba);
        var expected = checked(width * height * 4);
        if (rgba.Length < expected)
        {
            throw new ArgumentException(
                $"RGBA buffer is {rgba.Length} bytes; expected at least {expected} for {width}x{height}.", nameof(rgba));
        }
        var blocksX = (width + 3) / 4;
        var blocksY = (height + 3) / 4;
        var dst = new byte[checked(blocksX * blocksY * 16)];
        fixed (byte* src = rgba)
        fixed (byte* outp = dst)
        {
            blix_bc7_encode(src, width, height, outp, perceptual ? 1 : 0, quality, numThreads);
        }
        return dst;
    }
}

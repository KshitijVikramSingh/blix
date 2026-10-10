using System.Runtime.InteropServices;

namespace Blix.Graphics.MetalHWX;

/// <summary>native/blix_hwx.mm's C entry points, and the resolver that finds the library beside this assembly.</summary>
/// <remarks>The struct layouts are the native ones (static_assert'ed there) and must change with them.</remarks>
internal static unsafe class HwxNative
{
    public const string Library = "blix_hwx";

    public static string LibraryPath => Path.Combine(AppContext.BaseDirectory, "libblix_hwx.dylib");

    static HwxNative()
    {
        // By absolute path: default probing does not reliably find an app-local dylib by bare name on macOS.
        NativeLibrary.SetDllImportResolver(typeof(HwxNative).Assembly, (name, _, _) =>
            name == Library ? NativeLibrary.Load(LibraryPath) : IntPtr.Zero);
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Blas
    {
        public uint PrimBase;
        public uint SolidCount;
        public uint PartialCount;
        public uint Pad;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Entry
    {
        public fixed float Transform[12];
        public uint Blas;
        public fixed uint Pad[3];
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct BuildStats
    {
        public double BlasGpuMs;
        public double CompactTlasGpuMs;
        public ulong BlasBytes;
        public ulong CompactedBytes;
        public ulong TlasBytes;
        public ulong ScratchBytes;
        public ulong GeometryBytes;
        public uint BlasCount;
        public uint GeometryCount;
    }

    [DllImport(Library)] public static extern nint hwx_create(nint mtlDevice, byte* error, int errorLength);
    [DllImport(Library)] public static extern void hwx_destroy(nint ctx);
    [DllImport(Library)] public static extern nint hwx_error(nint ctx);

    [DllImport(Library)]
    public static extern int hwx_build(nint ctx, nint positions, uint vertexCount, nint instances, nint triangles, nint owners, nint surfaces,
        uint* indices, uint* primRows, ulong primCount, Blas* blas, uint blasCount, Entry* entries, uint entryCount, BuildStats* stats);

    [DllImport(Library)]
    public static extern int hwx_encode_shadow(nint ctx, nint sharedEvent, ulong waitValue, ulong signalValue,
        nint origins, ulong originsOffset, nint occluded, ulong occludedOffset, uint count, float* direction, uint seedBase);

    [DllImport(Library)]
    public static extern void hwx_shadow_stats(nint ctx, ulong* dispatches, ulong* totalGpuNs, ulong* lastGpuNs, ulong* failures);

    [DllImport(Library)]
    public static extern int hwx_trace(nint ctx, nint rays, nint hits, uint count, uint seedBase, uint closestOnly, uint repeats, double* gpuMs);
}

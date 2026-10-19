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
        nint origins, ulong originsOffset, nint occluded, ulong occludedOffset, uint count, float* direction, uint seedBase, nint skyRays,
        uint alphaTest, uint statSlot, nint limit, ulong limitOffset, nint directions, nint pixelOf, uint throughAt);

    [DllImport(Library)]
    public static extern void hwx_shadow_stats(nint ctx, uint slot, ulong* dispatches, ulong* totalGpuNs, ulong* lastGpuNs, ulong* failures);

    [DllImport(Library)]
    public static extern int hwx_encode_trace(nint ctx, nint sharedEvent, ulong waitValue, ulong signalValue,
        nint rays, ulong raysOffset, nint hits, ulong hitsOffset, uint count, uint seedBase, nint limit, ulong limitOffset);

    [DllImport(Library)]
    public static extern int hwx_group_begin(nint ctx, nint sharedEvent, ulong waitValue);

    [DllImport(Library)]
    public static extern int hwx_group_end(nint ctx, nint sharedEvent, ulong signalValue);

    [DllImport(Library)]
    public static extern void hwx_trace_stats(nint ctx, ulong* dispatches, ulong* totalGpuNs, ulong* lastGpuNs, ulong* failures);

    [DllImport(Library)]
    public static extern int hwx_set_transmit(nint ctx, uint* rowTransmit, uint rowCount);

    [DllImport(Library)]
    public static extern int hwx_set_alpha(nint ctx, nint* textures, float* cutoffs, float* alphaFactors, uint materialCount,
        uint* rowMaterial, uint rowCount, nint uvs, float mip);

    [DllImport(Library)]
    public static extern int hwx_shadow_host(nint ctx, float* origins, uint count, float* direction, uint seedBase, byte* occluded, double* gpuMs);

    [DllImport(Library)]
    public static extern void hwx_share_queue(nint ctx, nint from);

    [DllImport(Library)]
    public static extern void hwx_use_queue(nint ctx, nint queue);

    [DllImport(Library)]
    public static extern void hwx_memory(nint ctx, ulong* allocated, ulong* recommended);

    [DllImport(Library)]
    public static extern int hwx_candidates_setup(nint ctx, float* patches, uint patchCount, uint* cellStart, uint cells, uint* cellItems, uint items);

    [DllImport(Library)]
    public static extern int hwx_candidates(nint ctx, float* queries, uint count, void* parameters, int* candidates, uint* triedNearest, double* gpuMs);

    [DllImport(Library)]
    public static extern int hwx_contact_trace(nint ctx, float* rays, uint count, uint seed, uint* contactStart, uint contactRows, float* contactTris,
        uint contactCount, uint* answers, double* gpuMs);

    [DllImport(Library)]
    public static extern int hwx_trace_host(nint ctx, float* rays, uint count, uint seedBase, uint closestOnly, uint* hits, double* gpuMs);

    [DllImport(Library)]
    public static extern int hwx_buried(nint ctx, float* records, uint count, uint tests, uint threshold, uint seed, float reach,
        float lift, uint enclosedTests, byte* doubleSided, uint placementCount, uint* contactStart, uint contactRows, float* contactTris,
        uint contactCount, byte* buried, double* gpuMs);

    [DllImport(Library)]
    public static extern int hwx_lightlet_dense(nint ctx, float* points, uint count, float* dirs, uint dirCount, uint words, uint seed,
        float lift, float reach, uint* bits, double* gpuMs);

    [DllImport(Library)]
    public static extern int hwx_lightlet_rings(nint ctx, float* points, uint pointCount, uint* pairs, uint pairCount, float* nodeDirs,
        uint nodeCount, float* tanRadii, uint radii, uint azimuths, uint seed, float lift, float reach, uint* bits, double* gpuMs);

    [DllImport(Library)]
    public static extern int hwx_trace(nint ctx, nint rays, nint hits, uint count, uint seedBase, uint closestOnly, uint repeats, double* gpuMs, float* sun, float reach);
}

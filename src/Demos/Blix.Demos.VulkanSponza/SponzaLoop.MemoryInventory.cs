using System.Globalization;

namespace Blix.Demos.VulkanSponza;

// Where the memory goes: the device's resources by size (the largest buffers, the textures by format, the geometry),
// and the managed heap -- logged when a cook lands and from the memory panel's button. Sponza's process stood at ~16 GB
// on a 16 GB machine whose GPU memory is the same RAM (2026-10-13): this says what to cut.
internal sealed partial class SponzaLoop
{
    private void LogMemoryInventory(string when)
    {
        var snap = device.SnapshotResources();
        static string Mb(long bytes) => (bytes / 1048576.0).ToString("N0", CultureInfo.InvariantCulture);
        var buffers = snap.GpuBuffers.Sum(b => b.SizeBytes);
        var textures = snap.Textures.Sum(t => t.ByteSize);
        var vertices = snap.VertexBuffers.Sum(v => v.ByteSize);
        var indices = snap.IndexBuffers.Sum(i => i.ByteSize);
        var gc = GC.GetGCMemoryInfo();
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"[VulkanSponza] memory, {when}: GPU buffers {Mb(buffers)} MB in {snap.GpuBuffers.Count:N0}, textures {Mb(textures)} MB in {snap.Textures.Count:N0}, vertices {Mb(vertices)} MB, indices {Mb(indices)} MB; managed heap {Mb(GC.GetTotalMemory(false))} MB (committed {Mb(gc.TotalCommittedBytes)} MB)."));
        foreach (var b in snap.GpuBuffers.OrderByDescending(b => b.SizeBytes).Where((b, i) => i < 20 || b.SizeBytes >= 16 << 20))
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"    {Mb(b.SizeBytes),7} MB  buffer  {b.Name}"));
        foreach (var g in snap.Textures.GroupBy(t => t.Format).OrderByDescending(g => g.Sum(t => t.ByteSize)).Take(8))
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"    {Mb(g.Sum(t => t.ByteSize)),7} MB  textures {g.Key} ({g.Count():N0})"));
        foreach (var t in snap.Textures.OrderByDescending(t => t.ByteSize).Take(6))
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"    {Mb(t.ByteSize),7} MB  texture {t.Name} {t.Width}x{t.Height} {t.Format}"));
        // The cook's CPU structures, by their counts (a tuple of int and float is 8 bytes; an array's header aside).
        var couplingPairs = transportSolve?.Couplings.Sum(c => (long)(c?.Length ?? 0)) ?? 0;
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"    cook on the CPU: couplings {couplingPairs:N0} pairs (~{Mb(couplingPairs * 8 + (transportSolve?.Couplings.Length ?? 0) * 32L)} MB as arrays a patch); ray scene {rayQueries?.MeshNodeCount ?? 0:N0} nodes (~{Mb((rayQueries?.MeshNodeCount ?? 0) * 32L)} MB) and {rayQueries?.StoredTriangleCount ?? 0:N0} triangles."));
        LogHeapHolders();
    }
}

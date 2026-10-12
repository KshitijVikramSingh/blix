using System.Globalization;
using System.Text.RegularExpressions;

namespace Blix.Demos.VulkanSponza;

// --memory-report: at the shot, what the GPU holds, by resource -- the device's snapshot (textures, vertex, index and
// GPU buffers) grouped by name with per-slot numbers folded (mask0, mask1 -> mask), material textures together. What it
// cannot see: the Metal tracers' own structures (their sizes are in the log when built) and the CPU's copies.
internal sealed partial class SponzaLoop
{
    private bool memoryReport;

    private void WriteMemoryReport()
    {
        if (!memoryReport) return;
        var snap = device.SnapshotResources();
        var groups = new Dictionary<string, (long Bytes, int Count)>();
        void Add(string kind, string name, long bytes)
        {
            var key = kind + " " + Regex.Replace(name ?? "?", @"\d+$", "");
            groups.TryGetValue(key, out var g);
            groups[key] = (g.Bytes + bytes, g.Count + 1);
        }
        foreach (var t in snap.Textures)
        {
            // Material textures carry their file's name: one line for all of them.
            var name = t.Name ?? "?";
            var material = name.Contains('/') || name.EndsWith(".blixtex", StringComparison.Ordinal) || name.EndsWith(".png", StringComparison.Ordinal);
            Add("texture", material ? "(material textures)" : name, t.ByteSize);
        }
        foreach (var v in snap.VertexBuffers) Add("vertex", v.Name ?? "?", v.ByteSize);
        foreach (var i in snap.IndexBuffers) Add("index", i.Name ?? "?", i.ByteSize);
        foreach (var b in snap.GpuBuffers) Add("buffer", b.Name ?? "?", b.SizeBytes);
        var total = groups.Values.Sum(g => g.Bytes);
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"[VulkanSponza] memory report: {total / 1048576.0:N0} MB in {groups.Values.Sum(g => g.Count):N0} Vulkan resources (Metal tracers' structures not included):"));
        foreach (var kind in new[] { "texture", "vertex", "index", "buffer" })
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"[VulkanSponza]   {kind,-8} {groups.Where(g => g.Key.StartsWith(kind + " ", StringComparison.Ordinal)).Sum(g => g.Value.Bytes) / 1048576.0,8:N0} MB"));
        foreach (var (key, g) in groups.OrderByDescending(g => g.Value.Bytes).Take(45))
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"[VulkanSponza]   {g.Bytes / 1048576.0,8:N1} MB  x{g.Count,-4} {key}"));
    }
}

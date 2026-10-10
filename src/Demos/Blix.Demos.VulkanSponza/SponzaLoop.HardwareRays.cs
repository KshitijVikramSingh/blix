using System.Runtime.InteropServices;
using Blix.Geometry;
using Blix.Graphics;
using Blix.Graphics.MetalHWX;

namespace Blix.Demos.VulkanSponza;

// --ray-backend hw|both: the ray check's (or bench's) batch answered by Apple's ray tracing hardware at the shot, on
// the same packed scene, the same rays and the same baked coverage as the software traversal, and held against the
// same CPU oracle (CompareRayCheck). Built and traced once, off the frame: Vulkan idle, Metal waited on.
internal sealed partial class SponzaLoop
{
    // sw (default): the software traversal only. hw: Metal only. both: each reported against the CPU.
    private string rayBackend = "sw";
    // Dispatches timed after the one whose answers are checked.
    private const int HardwareRayRepeats = 20;

    private void WriteHardwareRayCheck(uint[]? surfaceWords)
    {
        if (rayGpuData is null || surfaceWords is null) return;
        using var tracer = MetalRayTracer.TryCreate(device, out var reason);
        if (tracer is null)
        {
            Console.WriteLine($"[VulkanSponza] ray check (metal): unavailable, {reason}");
            return;
        }

        device.WaitIdle();
        var blocks = RayQueryGpuData.BlockNames.Select(name => rayBlockBuffers.First(b => b.Name == name).Buffer).ToArray();
        var build = tracer.Build(rayGpuData, blocks, surfaceWords);
        Console.WriteLine(string.Create(Inv,
            $"[VulkanSponza] ray scene (metal): {build.Hierarchies:N0} bottom-level structures ({build.Geometries:N0} geometries) under {build.Entries:N0} instances; {build.SolidTriangles:N0} solid and {build.PartialTriangles:N0} partly covered triangles, {build.UncoveredTriangles:N0} uncovered left out, {build.DuplicateRows:N0} duplicate rows dropped."));
        Console.WriteLine(string.Create(Inv,
            $"[VulkanSponza] ray scene (metal): prepared in {build.CpuPrepareMs:0} ms on the CPU (classify {build.CpuClassifyMs:0}, pack {build.CpuPackMs:0}), bottom level built in {build.BlasGpuMs:0.0} ms GPU, compacted with the top level in {build.CompactTlasGpuMs:0.0} ms; {build.BlasBytes / 1048576.0:0.0} MB -> {build.CompactedBytes / 1048576.0:0.0} MB compacted, top level {build.TlasBytes / 1024.0:0.0} KB, geometry {build.GeometryBytes / 1048576.0:0.0} MB, scratch {build.ScratchBytes / 1048576.0:0.0} MB."));

        var rays = rayCheckBuffers.First(b => b.Name == "RayCheckRays").Buffer;
        var hits = device.CreateGpuBuffer(RayCheckDispatchCount * 48 + 80, name: "sponza.ray-check.hits-metal");
        try
        {
            var closestOnly = rayBench is not null;
            var ms = tracer.Trace(rays, hits, RayCheckDispatchCount, seedBase: 0, closestOnly, repeats: 1 + HardwareRayRepeats);
            var words = MemoryMarshal.Cast<byte, uint>(device.ReadGpuBuffer(hits, 0, RayCheckCount * 48).AsSpan()).ToArray();
            CompareRayCheck("metal", words);

            var timed = ms.Skip(1).Order().ToArray();
            var median = timed[timed.Length / 2];
            var traced = closestOnly ? RayCheckDispatchCount : 2 * RayCheckDispatchCount;
            Console.WriteLine(string.Create(Inv,
                $"[VulkanSponza] ray {(closestOnly ? "bench" : "check")} (metal): {median:0.000} ms per dispatch (median of {timed.Length}, {timed[0]:0.000}-{timed[^1]:0.000}), {traced / median / 1000.0:0.0} M rays/s{(closestOnly ? " (closest)" : " (closest and any)")}."));
        }
        finally
        {
            device.DestroyGpuBuffer(hits);
        }
    }
}

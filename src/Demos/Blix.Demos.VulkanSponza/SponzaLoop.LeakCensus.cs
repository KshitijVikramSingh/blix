using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;
using Blix.Geometry;
using Blix.Graphics.Images;

namespace Blix.Demos.VulkanSponza;

// --ray-leak-census (at the shot, with --rt-shadows): do the traced shadows' rays leak through the scene -- pass where
// the geometry is closed? The judge is the CPU ray scene (RayQueryScene): watertight by construction, bit-exact with the
// GPU's software traversal, and the same geometry the frame's Metal tracer holds (the same LOD error). A ray Metal let
// through that the CPU finds blocked by a SOLID surface (no alpha cutoff: every triangle covers, no coin) is a LEAK.
// One blocked only by alpha-tested surfaces (leaves, whose coverage coins Metal and the CPU flip with different seeds)
// is counted apart: two coins, not a crack.
// Candidates, from the last frame's traced rays (their origins and directions, as Metal traced them): every lit ray
// (every 13th at ray LOD error 0, whose CPU scene is slow), and among them the isolated ones -- lit by Metal with all 8
// neighbours shadowed, what a crack between triangles looks like on screen.
// Writes <shot>.leaks.png (red: a solid leak; blue: blocked by leaves only; yellow: isolated and lit on the CPU too).
internal sealed partial class SponzaLoop
{
    private bool rayLeakCensus;

    private void WriteLeakCensus(string basePath)
    {
        if (!rayLeakCensus) return;
        if (frameTracer is null || rayQueries is null || rtShadowOrigins.Length == 0)
        {
            Console.WriteLine("[VulkanSponza] leak census: needs --rt-shadows, the frame tracer and the ray scene; skipped.");
            return;
        }
        device.WaitIdle();
        var slot = device.CurrentFrameSlot;
        var (w, h) = rtShadowSize;
        var rays = w * h;
        var origins = MemoryMarshal.Cast<byte, Vector4>(device.ReadGpuBuffer(rtShadowOrigins[slot], 0, rays * 16).AsSpan()).ToArray();
        var words = MemoryMarshal.Cast<byte, uint>(device.ReadGpuBuffer(rtShadowMasks[slot], 16, rays * 4).AsSpan()).ToArray();
        // Per-ray directions only under the disc's jitter (PenumbraFilter off); filtered, every ray went to the sun's centre.
        var perRay = SunDiscTangent > 0f && !(shadows.PenumbraFilter && !halfShadowsNow);
        var directions = perRay ? MemoryMarshal.Cast<byte, Vector4>(device.ReadGpuBuffer(rtShadowDirections[slot], 0, rays * 16).AsSpan()).ToArray() : null;
        var towardSun = -Vector3.Normalize(sunDirection);

        bool Traced(int i) => origins[i].W > 0f;
        bool MetalLit(int i) => (words[i] & 1u) == 0u;
        var stride = rayLodError > 0f ? 1 : 13;
        var isolated = new List<int>();
        var sample = new List<int>();
        var tracedCount = 0;
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
        {
            var i = y * w + x;
            if (!Traced(i)) continue;
            tracedCount++;
            if (!MetalLit(i)) continue;
            if (i % stride == 0) sample.Add(i);
            if (x == 0 || y == 0 || x == w - 1 || y == h - 1) continue;
            var alone = true;
            for (var dy = -1; dy <= 1 && alone; dy++)
            for (var dx = -1; dx <= 1 && alone; dx++)
            {
                if (dx == 0 && dy == 0) continue;
                var j = (y + dy) * w + x + dx;
                alone = Traced(j) && !MetalLit(j);
            }
            if (alone) isolated.Add(i);
        }

        // The CPU's answer: 0 open, 1 blocked by alpha-tested surfaces only, 2 a solid surface in the way. Walks the
        // ray's hits nearest first; a coin can only drop an alpha-tested triangle, never a solid one.
        var scene = rayQueries;
        int CpuBlocked(int i)
        {
            var o = origins[i];
            var d = perRay ? Vector3.Normalize(new Vector3(directions![i].X, directions[i].Y, directions[i].Z)) : towardSun;
            var ray = new Ray(new Vector3(o.X, o.Y, o.Z), d);
            var result = 0;
            var t0 = 0f;
            for (var steps = 0; steps < 256; steps++)
            {
                if (scene.Closest(ray, t0, o.W, 0x9E3779B9u ^ (uint)i) is not { } hit) return result;
                var m = rayPlacementMaterials[hit.Instance];
                if (m.AlphaCutoff <= 0f && !m.Clear) return 2;
                if (!m.Clear) result = 1;
                t0 = hit.T + 1e-4f + hit.T * 1e-6f;
            }
            return result;
        }
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var isolatedAnswers = new int[isolated.Count];
        Parallel.For(0, isolated.Count, k => isolatedAnswers[k] = CpuBlocked(isolated[k]));
        var sampleAnswers = new int[sample.Count];
        Parallel.For(0, sample.Count, k => sampleAnswers[k] = CpuBlocked(sample[k]));
        int Count(int[] a, int v) => a.Count(x => x == v);
        var lit = 0;
        for (var i = 0; i < rays; i++) if (Traced(i) && MetalLit(i)) lit++;
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"[VulkanSponza] leak census (ray LOD error {rayLodError * 100:0.##} cm, {(perRay ? "per-ray disc directions" : "the sun's direction")}): {tracedCount:N0} rays traced, {lit:N0} lit by Metal; " +
            $"checked {sample.Count:N0} lit (every {stride}): SOLID blocker {Count(sampleAnswers, 2):N0} ({100.0 * Count(sampleAnswers, 2) / Math.Max(sample.Count, 1):0.000}%), leaves only {Count(sampleAnswers, 1):N0} ({100.0 * Count(sampleAnswers, 1) / Math.Max(sample.Count, 1):0.00}%); " +
            $"isolated lit {isolated.Count:N0}: solid {Count(isolatedAnswers, 2):N0}, leaves only {Count(isolatedAnswers, 1):N0} ({clock.Elapsed.TotalSeconds:0.0} s on the CPU)."));
        // The control: Metal-shadowed rays, every 50th -- the CPU should find nearly all of them blocked (solid or
        // leaves); a large open share would mean the judge is not seeing what Metal sees.
        var control = new List<int>();
        for (var i = 0; i < rays; i += 50) if (Traced(i) && !MetalLit(i)) control.Add(i);
        var controlAnswers = new int[control.Count];
        Parallel.For(0, control.Count, k => controlAnswers[k] = CpuBlocked(control[k]));
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"[VulkanSponza]   control, {control.Count:N0} Metal-shadowed rays: the CPU finds solid {100.0 * Count(controlAnswers, 2) / Math.Max(control.Count, 1):0.00}%, leaves only {100.0 * Count(controlAnswers, 1) / Math.Max(control.Count, 1):0.00}%, open {100.0 * Count(controlAnswers, 0) / Math.Max(control.Count, 1):0.00}%."));
        var leaks = new List<int>();
        for (var k = 0; k < sample.Count; k++) if (sampleAnswers[k] == 2) leaks.Add(sample[k]);
        foreach (var i in leaks.Take(12))
        {
            var o = origins[i];
            var d = perRay ? directions![i] : new Vector4(towardSun, 0f);
            var first = scene.Closest(new Ray(new Vector3(o.X, o.Y, o.Z), Vector3.Normalize(new Vector3(d.X, d.Y, d.Z))), 0f, o.W, 0x9E3779B9u ^ (uint)i);
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"[VulkanSponza]   leak at pixel ({i % w}, {i / w}) origin ({o.X:0.###}, {o.Y:0.###}, {o.Z:0.###}): first CPU hit t {first?.T:0.####} placement {first?.Instance} ({(first is { } f ? rayPlacementNames[f.Instance] : "-")}) triangle {first?.Triangle} bary {first?.Barycentrics}"));
        }

        var rgba = new byte[rays * 4];
        for (var i = 0; i < rays; i++)
        {
            byte v = Traced(i) ? (MetalLit(i) ? (byte)160 : (byte)40) : (byte)0;
            rgba[i * 4] = rgba[i * 4 + 1] = rgba[i * 4 + 2] = v;
            rgba[i * 4 + 3] = 255;
        }
        void Mark(int i, byte r, byte g, byte b) { rgba[i * 4] = r; rgba[i * 4 + 1] = g; rgba[i * 4 + 2] = b; }
        for (var k = 0; k < isolated.Count; k++) if (isolatedAnswers[k] == 0) Mark(isolated[k], 255, 220, 0);
        for (var k = 0; k < sample.Count; k++) if (sampleAnswers[k] == 1) Mark(sample[k], 0, 90, 255);
        foreach (var i in leaks) Mark(i, 255, 0, 0);
        PngWriter.WriteRgba8(basePath + ".leaks.png", rgba, w, h);
        Console.WriteLine($"[VulkanSponza]   {basePath}.leaks.png  (red: a solid leak; blue: blocked by leaves only; yellow: isolated, lit on the CPU too; light grey lit, dark grey shadowed)");
    }
}

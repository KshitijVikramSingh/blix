using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;
using Blix.Core;

namespace Blix.Demos.VulkanSponza;

// --ref-view STRIDE: the reference for what the camera sees, per pixel. The gate's reference measures the light ARRIVING
// at a point (the incident field's claim); a surface that lets light through shows its far side, which that cannot
// judge -- and the reference view (G, reference_trace.comp) treats every surface as opaque. So a grid of camera pixels,
// one every STRIDE, each a wave point that is a camera (reference_wave.comp: its first ray along the pixel, the path's
// first hit the surface it shows): its sum is the radiance the pixel sees through the material, by the spec's rules
// (the base lobe 1 - T, the sheet's transmission T c, the sun on either side) and every light that reaches it. The lit
// pass's view 26 ("Diffuse response", --viz 26) is the same quantity from the renderer; the view dump writes both
// (reference_view.f32x4), and tools/gi-model/view_reference.py holds them against each other. --ref-view-lanes N (64)
// and --ref-view-paths N (1,024) a point; --ref-bounces as the gate's. Not with --gpu-reference (they share the wave's
// passes). The camera at the wave's start: a moved view invalidates it.
// --ref-view-records N: N lightmap records more, as SURFACE points (cosine paths from the record, along its normal): the
// indirect light arriving there, the GI's resolved light's claim, record by record -- three quarters on surfaces that let
// light through (|w| 5: both faces, the far side's record among them, which no camera point can see), the rest opaque
// (the two's units held against each other). The dump lists them (reference_records.u32) after the grid's points.
internal sealed partial class SponzaLoop
{
    private int refViewStride;
    private int refViewLanes = 64, refViewPaths = 1024;
    private ReferenceWave? viewWave;
    private int refViewW, refViewH;
    private int refViewRecords;
    private readonly List<uint> refViewRecordIds = new();

    private void ReadViewReferenceArgs(AppArgs args)
    {
        refViewStride = Math.Max(0, args.Int("ref-view") ?? 0);
        if (args.Int("ref-view-lanes") is { } l) refViewLanes = Math.Max(1, l);
        if (args.Int("ref-view-paths") is { } p) refViewPaths = Math.Max(1, p);
        refViewRecords = Math.Max(0, args.Int("ref-view-records") ?? 0);
    }

    private Vector4[]? cachedViewSums;
    private string? viewCacheFile;

    private void RecordViewReference()
    {
        if (refViewStride <= 0 || gpuReference || rayBlockBuffers.Length == 0 || !RaySurfacesReady) return;
        if (viewWave is null)
        {
            if (postLoadFrames < 4 || (FrameRaysOnHardware && frameTracer is null)) return;
            device.TryGetTextureSize(graph.GetColorTexture(incidentHandle), out var iw, out var ih);
            Matrix4x4.Invert(viewProj, out var invViewProj);
            var packed = new List<Vector4>();
            refViewW = (iw + refViewStride - 1) / refViewStride;
            refViewH = (ih + refViewStride - 1) / refViewStride;
            for (var gy = 0; gy < refViewH; gy++)
            for (var gx = 0; gx < refViewW; gx++)
            {
                // The pixel's centre (the grid point's pixel, (int)px, (int)py, as the dump reads it).
                float px = gx * refViewStride + 0.5f, py = gy * refViewStride + 0.5f;
                var ndc = new Vector2(px / iw * 2f - 1f, py / ih * 2f - 1f);
                var far = Vector4.Transform(new Vector4(ndc, 1f, 1f), invViewProj);
                var dir = Vector3.Normalize(new Vector3(far.X, far.Y, far.Z) / far.W - cameraPosition);
                packed.Add(new Vector4(cameraPosition, 0f));
                packed.Add(new Vector4(dir, 1f));
            }
            if (refViewRecords > 0 && texelCount > lightmapFirstRecord)
            {
                var first = lightmapFirstRecord;
                var count = texelCount - first;
                var recs = MemoryMarshal.Cast<byte, Vector4>(device.ReadGpuBuffer(texelBuffer, checked(first * 32), checked(count * 32)).AsSpan()).ToArray();
                var through = new List<int>();
                var opaque = new List<int>();
                for (var i = 0; i < count; i++) (MathF.Abs(recs[2 * i].W) > 4.5f ? through : opaque).Add(i);
                void Pick(List<int> from, int want)
                {
                    if (from.Count == 0 || want <= 0) return;
                    var step = Math.Max(1, from.Count / want) | 1;   // odd: a pair's records lie side by side, and both faces are wanted
                    for (var k = 0; k < from.Count && want-- > 0; k += step)
                    {
                        var i = from[k];
                        var n = Vector3.Normalize(new Vector3(recs[2 * i + 1].X, recs[2 * i + 1].Y, recs[2 * i + 1].Z));
                        packed.Add(new Vector4(new Vector3(recs[2 * i].X, recs[2 * i].Y, recs[2 * i].Z) + n * 2e-3f, 0f));
                        packed.Add(new Vector4(n, 0f));
                        refViewRecordIds.Add((uint)(first + i));
                    }
                }
                // BLIX_REF_RECORDS_FILE: the records named there (one id a line, up to N), not an even pick -- a model's texels.
                if (Environment.GetEnvironmentVariable("BLIX_REF_RECORDS_FILE") is { Length: > 0 } listFile)
                    foreach (var line in File.ReadLines(listFile))
                    {
                        if (refViewRecordIds.Count >= refViewRecords || !int.TryParse(line.Trim(), out var id) || id < first || id >= texelCount) continue;
                        var i = id - first;
                        var n = Vector3.Normalize(new Vector3(recs[2 * i + 1].X, recs[2 * i + 1].Y, recs[2 * i + 1].Z));
                        packed.Add(new Vector4(new Vector3(recs[2 * i].X, recs[2 * i].Y, recs[2 * i].Z) + n * 2e-3f, 0f));
                        packed.Add(new Vector4(n, 0f));
                        refViewRecordIds.Add((uint)id);
                    }
                else
                {
                    Pick(through, through.Count > 0 ? refViewRecords * 3 / 4 : 0);
                    Pick(opaque, refViewRecords - refViewRecordIds.Count);
                }
                Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"[VulkanSponza] view reference: {refViewRecordIds.Count:N0} lightmap records as surface points ({through.Count:N0} records let light through)."));
            }
            var quota = (refViewPaths + refViewLanes - 1) / refViewLanes;
            viewWave = CreateReferenceWave(packed, refViewLanes, quota, "view-reference");
            // BLIX_REF_CACHE (SponzaLoop.GpuReference.cs): the camera paths' sums read back when the same points, sun, sky
            // and rules were traced before -- the wave then counts as done.
            if (referenceCacheDir is not null)
            {
                viewCacheFile = Path.Combine(referenceCacheDir, "view-" + ReferenceCacheKey(packed) + $"-{refViewLanes}x{quota}.ref");
                if (File.Exists(viewCacheFile))
                {
                    var sums = MemoryMarshal.Cast<byte, Vector4>(File.ReadAllBytes(viewCacheFile)).ToArray();
                    if (sums.Length == viewWave.PointCount) { cachedViewSums = sums; viewWave.Dispatch = (uint)ReferenceWaveFrames(viewWave); Console.WriteLine($"[VulkanSponza] view reference: read from the cache ({viewCacheFile}), not traced."); }
                }
            }
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"[VulkanSponza] view reference: {refViewW} x {refViewH} camera points (every {refViewStride} px of {iw} x {ih}), {refViewLanes} lanes a point, {quota * refViewLanes:N0} paths each, {refBounces} bounces; done in ~{ReferenceWaveFrames(viewWave)} frames."));
        }
        if (viewWave.Dispatch < ReferenceWaveFrames(viewWave)) RecordReferenceWave(viewWave);
    }
}

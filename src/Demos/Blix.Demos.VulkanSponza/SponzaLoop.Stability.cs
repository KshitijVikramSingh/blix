using Blix.Graphics;
using Blix.Graphics.Images;

namespace Blix.Demos.VulkanSponza;

// --stability K: does the lighting settle? Over the K frames before the shot, every 4th pixel's luminance in the
// incident-light field and in the final HDR image is read back each frame, and the shot reports how much each pixel
// moves: its temporal coefficient of variation (std / mean over the K frames) and its mean frame-to-frame step. A
// heat map of the incident field's variation is written beside the shot. History counters say whether probes KEEP
// their past; this says whether what is shaded actually holds still.
internal sealed partial class SponzaLoop
{
    private int stabilityFrames;
    private readonly List<float[]> stabilityIncident = new();
    private readonly List<float[]> stabilityScene = new();
    // What is presented: the TAA-resolved image (empty with TAA off). The last completed frame wrote
    // taaHandles[taaWrite]: this is sampled after the current frame flipped the pair.
    private readonly List<float[]> stabilityResolved = new();
    private (int W, int H) stabilityGrid;
    private const int StabilityStride = 4;

    // Called each frame before the shot: reads what the last completed frame left in both targets.
    private void SampleStability()
    {
        if (stabilityFrames <= 0 || shotPath is null || shotWritten || !fullyLoaded) return;
        if (postLoadFrames < shotFrame - stabilityFrames || postLoadFrames >= shotFrame) return;
        var incident = device.ReadTexture(graph.GetColorTexture(incidentHandle), out var iw, out var ih, out _);
        var scene = device.ReadTexture(graph.GetColorTexture(hdrHandle), out var sw, out var sh, out _);
        var resolvedFormat = TextureFormat.R11G11B10F;
        var resolved = render.Taa > 0f ? device.ReadTexture(graph.GetColorTexture(taaHandles[taaWrite]), out _, out _, out resolvedFormat) : null;
        var gw = sw / StabilityStride;
        var gh = sh / StabilityStride;
        stabilityGrid = (gw, gh);
        var inc = new float[gw * gh];
        var sce = new float[gw * gh];
        var res = new float[gw * gh];
        for (var y = 0; y < gh; y++)
        for (var x = 0; x < gw; x++)
        {
            var px = x * StabilityStride;
            var py = y * StabilityStride;
            var ix = Math.Min(iw - 1, px * iw / sw);
            var iy = Math.Min(ih - 1, py * ih / sh);
            var o = (iy * iw + ix) * 8;
            inc[y * gw + x] = Lum((float)BitConverter.ToHalf(incident, o), (float)BitConverter.ToHalf(incident, o + 2), (float)BitConverter.ToHalf(incident, o + 4));
            var packed = BitConverter.ToUInt32(scene, (py * sw + px) * 4);
            sce[y * gw + x] = Lum(UnpackFloat(packed & 0x7FF, 6), UnpackFloat((packed >> 11) & 0x7FF, 6), UnpackFloat((packed >> 22) & 0x3FF, 5));
            if (resolved is not null)
            {
                if (resolvedFormat == TextureFormat.Rgba16F)
                {
                    var o16 = (py * sw + px) * 8;
                    res[y * gw + x] = Lum((float)BitConverter.ToHalf(resolved, o16), (float)BitConverter.ToHalf(resolved, o16 + 2), (float)BitConverter.ToHalf(resolved, o16 + 4));
                }
                else
                {
                    var rp = BitConverter.ToUInt32(resolved, (py * sw + px) * 4);
                    res[y * gw + x] = Lum(UnpackFloat(rp & 0x7FF, 6), UnpackFloat((rp >> 11) & 0x7FF, 6), UnpackFloat((rp >> 22) & 0x3FF, 5));
                }
            }
        }
        stabilityIncident.Add(inc);
        stabilityScene.Add(sce);
        if (resolved is not null) stabilityResolved.Add(res);
        static float Lum(float r, float g, float b) => 0.2126f * r + 0.7152f * g + 0.0722f * b;
    }

    private void WriteStability(string basePath)
    {
        if (stabilityIncident.Count < 2) return;
        // Accuracy of what is presented: against the K jittered frames' average, which is the view supersampled
        // (each frame's jitter samples a different point of the pixel). A resolve can be steady by blurring; this
        // says how far each presented frame is from the image it should converge to. Errors over the mean
        // (relative), median pixel, averaged over the frames.
        if (taaCountRejection && !taaStats.Equals(default(GpuBufferHandle)))
        {
            var counts = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(device.ReadGpuBuffer(taaStats, 0, 8).AsSpan()).ToArray();
            Console.WriteLine(string.Create(Inv,
                $"[VulkanSponza] TAA rejection over the run: history clipped at {100.0 * counts[0] / Math.Max(1u, counts[1]):0.0}% of {counts[1]:N0} resolved pixels"));
        }
        // Lag: each presented frame against the same frame's raw lit image, median pixel, averaged over the frames.
        // With a moving camera this is what history drags along (trails at disocclusions, smear); with a still one
        // it is mostly the jitter TAA averages away. Comparative only: the same scene and path, one setting apart.
        if (stabilityResolved.Count == stabilityScene.Count && stabilityScene.Count >= 1)
        {
            var lags = new List<double>();
            for (var i = 0; i < stabilityScene[0].Length; i++)
            {
                double e = 0, n = 0;
                for (var k = 0; k < stabilityScene.Count; k++)
                {
                    if (stabilityScene[k][i] < 1e-4f) continue;
                    e += Math.Abs(stabilityResolved[k][i] - stabilityScene[k][i]) / stabilityScene[k][i];
                    n++;
                }
                if (n > 0) lags.Add(e / n);
            }
            lags.Sort();
            Console.WriteLine(string.Create(Inv,
                $"[VulkanSponza] lag over {stabilityScene.Count} frames: presented against the same frame's raw image, median {100 * lags[lags.Count / 2]:0.00}%, p90 {100 * lags[lags.Count * 9 / 10]:0.00}%, p99 {100 * lags[lags.Count * 99 / 100]:0.00}%"));
        }
        if (stabilityResolved.Count == stabilityScene.Count && stabilityScene.Count >= 2)
        {
            var (gw, gh) = stabilityGrid;
            var reference = new double[gw * gh];
            foreach (var f in stabilityScene) for (var i = 0; i < reference.Length; i++) reference[i] += f[i];
            for (var i = 0; i < reference.Length; i++) reference[i] /= stabilityScene.Count;
            double MedianError(List<float[]> frames)
            {
                var errors = new List<double>();
                for (var i = 0; i < reference.Length; i++)
                {
                    if (reference[i] < 1e-5) continue;
                    double e = 0;
                    foreach (var f in frames) e += Math.Abs(f[i] - reference[i]);
                    errors.Add(e / frames.Count / reference[i]);
                }
                errors.Sort();
                return errors.Count == 0 ? double.NaN : errors[errors.Count / 2];
            }
            // Bias alone: the presented frames' own average against the supersample (scatter averages away).
            var biasErrors = new List<double>();
            for (var i = 0; i < reference.Length; i++)
            {
                if (reference[i] < 1e-5) continue;
                double mean = 0;
                foreach (var f in stabilityResolved) mean += f[i];
                biasErrors.Add(Math.Abs(mean / stabilityResolved.Count - reference[i]) / reference[i]);
            }
            biasErrors.Sort();
            Console.WriteLine(string.Create(Inv,
                $"[VulkanSponza] stability accuracy against the {stabilityScene.Count}-frame supersample: presented median error {100 * MedianError(stabilityResolved):0.00}%, of which bias (its own average's error) {100 * biasErrors[biasErrors.Count / 2]:0.00}%; one jittered frame (no TAA) {100 * MedianError(stabilityScene):0.00}%"));
        }
        foreach (var (name, frames, map) in new[] { ("incident light", stabilityIncident, true), ("lit image before TAA", stabilityScene, false), ("presented image (TAA-resolved)", stabilityResolved, true) })
        {
            if (frames.Count < 2) continue;
            var (gw, gh) = stabilityGrid;
            var cv = new List<double>();
            var step = new List<double>();
            var cvMap = new double[gw * gh];
            for (var i = 0; i < gw * gh; i++)
            {
                double sum = 0, sum2 = 0, steps = 0;
                for (var k = 0; k < frames.Count; k++)
                {
                    var v = frames[k][i];
                    sum += v; sum2 += v * v;
                    if (k > 0) steps += Math.Abs(v - frames[k - 1][i]);
                }
                var mean = sum / frames.Count;
                if (mean < 1e-5) continue;
                var std = Math.Sqrt(Math.Max(0, sum2 / frames.Count - mean * mean));
                cv.Add(std / mean);
                step.Add(steps / (frames.Count - 1) / mean);
                cvMap[i] = std / mean;
            }
            cv.Sort(); step.Sort();
            string Q(List<double> v) => string.Create(Inv,
                $"median {100 * v[v.Count / 2]:0.00}%, p90 {100 * v[v.Count * 9 / 10]:0.00}%, p99 {100 * v[v.Count * 99 / 100]:0.00}%");
            Console.WriteLine(string.Create(Inv,
                $"[VulkanSponza] stability over {frames.Count} still frames, {name} ({cv.Count} pixels): variation (std/mean) {Q(cv)}; frame-to-frame step {Q(step)}; pixels varying over 2%: {100.0 * cv.Count(c => c > 0.02) / cv.Count:0.0}%"));
            if (!map) continue;
            // Heat map: black steady, white at 10% variation or more.
            var rgba = new byte[gw * gh * 4];
            for (var i = 0; i < gw * gh; i++)
            {
                var b = (byte)Math.Clamp(cvMap[i] / 0.10 * 255.0, 0, 255);
                rgba[i * 4] = b; rgba[i * 4 + 1] = b; rgba[i * 4 + 2] = b; rgba[i * 4 + 3] = 255;
            }
            PngWriter.WriteRgba8(basePath + (name.StartsWith("presented") ? ".stability-presented.png" : ".stability.png"), rgba, gw, gh);
        }
    }
}

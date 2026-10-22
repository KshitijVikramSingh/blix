using System.Globalization;
using System.Numerics;
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
internal sealed partial class SponzaLoop
{
    private int refViewStride;
    private int refViewLanes = 64, refViewPaths = 1024;
    private ReferenceWave? viewWave;
    private int refViewW, refViewH;

    private void ReadViewReferenceArgs(AppArgs args)
    {
        refViewStride = Math.Max(0, args.Int("ref-view") ?? 0);
        if (args.Int("ref-view-lanes") is { } l) refViewLanes = Math.Max(1, l);
        if (args.Int("ref-view-paths") is { } p) refViewPaths = Math.Max(1, p);
    }

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
            var quota = (refViewPaths + refViewLanes - 1) / refViewLanes;
            viewWave = CreateReferenceWave(packed, refViewLanes, quota, "view-reference");
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"[VulkanSponza] view reference: {refViewW} x {refViewH} camera points (every {refViewStride} px of {iw} x {ih}), {refViewLanes} lanes a point, {quota * refViewLanes:N0} paths each, {refBounces} bounces; done in ~{ReferenceWaveFrames(viewWave)} frames."));
        }
        if (viewWave.Dispatch < ReferenceWaveFrames(viewWave)) RecordReferenceWave(viewWave);
    }
}

using System.Numerics;
using Blix;
using Blix.Core;

namespace Blix.Demos.VulkanSponza;

// --ray-panes: the GI's rays meet a transmitting pane (KHR_materials_transmission) as the glass it is, on average. Without it a pane has no coverage and every ray passes it whole, so a
// room behind a window is lit by the sky through it as if the window were not there. With it the pane's rows carry a coverage -- the share of rays the glass does not pass, by the
// hemispherical mean of its Fresnel F (Schlick over the cosine-weighted hemisphere: f0 + (1 - f0) / 21) and what it absorbs of the rest ((1 - F)(1 - T luminance(base colour))) -- and the
// partly covered rows' coin (ray_query.glsl's blix_covered) stops that share of the rays. A ray that stops reflects diffusely what the glass reflects of it (F over the share: the
// albedo of the word), and the rest is absorbed. So the light through the pane is (1 - F) T luminance(base): intensity as the BSDF reference has it, in grey (the tint is not kept; a
// per-channel pass needs the rays to carry a colour). The reflection is diffuse where the glass's is specular, which the GI can take: it only has to be the right amount.
//
// Off by default: it makes pane rows "cutouts" to whatever looks at coverage (the RTGI cook's census sees them as partly covered), and the GPU reference's cache key
// records it. The gate (and tools/testbeds/bsdf.sh) hold the GI to the reference, which has the panes (--ref-bsdf); a scene with a window is where it shows.
internal sealed partial class SponzaLoop
{
    private bool rayPanes;

    private void ReadRayPaneArgs(AppArgs args) => rayPanes = args.Flag("ray-panes");

    // The share of rays a pane stops and the albedo of what stops, for a transmitting material (0, 0 with --ray-panes off or no transmission).
    private float RayPaneStops(PbrMaterial? material, bool clear, out float albedo)
    {
        albedo = 0f;
        if (!rayPanes || !clear || material is null) return 0f;
        var e = material.Ext;
        var t = Math.Clamp(Math.Max(e.TransmissionFactor, material.TransmissionFactor), 0f, 1f);
        if (t <= 0f) return 0f;
        var f0 = ((e.IndexOfRefraction - 1f) / (e.IndexOfRefraction + 1f)) * ((e.IndexOfRefraction - 1f) / (e.IndexOfRefraction + 1f)) * e.SpecularFactor;
        var f = f0 + (e.SpecularFactor - f0) / 21f;
        var tint = material.BaseColorFactor;
        var lum = 0.2126f * tint.X + 0.7152f * tint.Y + 0.0722f * tint.Z;
        var stops = Math.Clamp(f + (1f - f) * (1f - t * lum), 0f, 1f);
        if (stops <= 0.001f) return 0f;
        albedo = Math.Clamp(f / stops, 0f, 1f);
        return stops;
    }
}

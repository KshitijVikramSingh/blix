using Blix.Core;

namespace Blix.Demos.VulkanSponza;

// The diffuse lobe of the lit shader: EON (the energy-preserving Oren-Nayar, Blix.Shaders/diffuse_eon.glsl) by default, in place of the fixed Lambert
// lobe (albedo / pi) it used to have. The lit shader applies it to the sun (a known light direction: the full f_EON), and to every irradiance it
// receives (the incident field's bounce, the sky's irradiance cube): there it multiplies E / pi by EON's albedo toward the eye, which is the exact
// answer for a cosine-distributed field and the only one an irradiance (a quantity with no direction) can ask for.
//
// What it does NOT change: the transport. The caches and the reference path still bounce light with albedo / pi at each hit; a cached irradiance holds no
// direction to evaluate a lobe against, so a bounce there is Lambertian by construction, and the lobe applies where the light is finally SEEN.
//
// glTF has one roughness and no diffuse roughness, so the diffuse roughness r is the material's roughness times --diffuse-roughness-scale
// (default 1). Per-material overrides belong to the cook (a material patch), not to this loop.
//
//   --diffuse lambert              the old fixed Lambert lobe, for comparison
//   --diffuse-roughness-scale x    r = roughness * x, clamped to 0..1 (default 1; 0 is Lambert whatever the model)
//   --ab diffuse                   alternate EON (on phase) and Lambert (off phase) in-process, like the other --ab modes
internal sealed partial class SponzaLoop
{
    private bool diffuseEon = true;
    private float diffuseRoughnessScale = 1f;

    private bool DiffuseEonNow => diffuseEon && !(abMode == "diffuse" && AbOffPhase);

    private void ReadDiffuseArgs(AppArgs args)
    {
        if (args.String("diffuse") is { } model)
        {
            if (model is "lambert") diffuseEon = false;
            else if (model is "eon") diffuseEon = true;
            else throw new ArgumentException($"--diffuse takes eon or lambert, not '{model}'");
        }
        if (args.Float("diffuse-roughness-scale") is { } scale) diffuseRoughnessScale = Math.Clamp(scale, 0f, 4f);
    }
}

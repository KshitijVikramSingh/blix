using System.Numerics;
using Blix.Core;
using Blix.Graphics;

namespace Blix.Demos.VulkanSponza;

// The diffuse lobe of the lit shader: EON (the energy-preserving Oren-Nayar, Blix.Shaders/diffuse_eon.glsl), ON by default, in place of the fixed Lambert lobe
// (albedo / pi). It was opt-in on the material track while the camera-path reference still shaded Lambert and materials.sh held the GI to it; that reference
// and materials.sh are gone, and the GPU reference (reference_wave.comp, the gate's judge) shades EON from the same baked words, so the default is the lobe
// both model. The lit shader applies it to the sun (a known light direction: the full f_EON), and to every irradiance it receives (RTGI's incident field):
// there it multiplies E / pi by EON's albedo toward the eye, which is the exact answer for a cosine-distributed field and the only one an irradiance (a
// quantity with no direction) can ask for.
//
// The transport conserves what the roughness loses. A bounce in RTGI's cache or the reference holds no direction to evaluate a lobe against at the hit it
// shades from the word, so it bounces with a single albedo per hit, and that albedo is baked into the rays' surface word (ray_surface_bake.comp): EON's
// HEMISPHERICAL albedo at the material's diffuse roughness (rho <E> + rho_ms (1 - <E>): the fraction of the light that arrives from anywhere and leaves
// whichever way; below rho for a rough grey, rho itself for a white one), and the roughness level it was baked at (the word's top three bits), from which
// the reference recovers the base albedo and shades its hits with f_EON itself. Lambert's is rho, so with --diffuse lambert the word is the base colour as
// before. Not modelled in RTGI's cache: the lobe's shape at a hit (a sun-lit hit reflects its sun with Lambert's cosine, not EON's flatter one).
// The bake runs once at load, so --diffuse is read at start and --ab diffuse changes only the lit shader, not the rays; the GPU reference's cache key
// records the lobe (SponzaLoop.GpuReference.cs ReferenceMaterialKey).
//
// glTF has one roughness and no diffuse roughness, so the diffuse roughness r is the material's roughness times --diffuse-roughness-scale
// (default 1), or --material name:diffuse-roughness=r.
//
//   --diffuse eon                  the EON lobe (the default)
//   --diffuse lambert              the old fixed Lambert lobe
//   --diffuse-roughness-scale x    r = roughness * x, clamped to 0..1 (default 1; 0 is Lambert whatever the model)
//   --ab diffuse                   alternate EON (on phase) and Lambert (off phase) in-process, like the other --ab modes
internal sealed partial class SponzaLoop
{
    private bool diffuseEon = true;
    private float diffuseRoughnessScale = 1f;

    // ray_surface_bake.comp's inputs: x 1 stores EON's hemispherical albedo (0: the base colour), y the roughness scale, z the material's roughness factor,
    // w 1 when it has a metallic-roughness texture (green times the factor is its roughness).
    private Vector4 BakeDiffuse(RayMaterial material) =>
        new(diffuseEon ? 1f : 0f, diffuseRoughnessScale, material.Roughness, material.MetallicRoughness.Equals(default(TextureHandle)) ? 0f : 1f);

    // The bake binds a texture whether or not the material has one (a binding cannot be absent); without one, the albedo stands in and w says to ignore it.
    private static ShaderTextureBinding BakeMetallicRoughness(RayMaterial material) =>
        new("uMetallicRoughness", material.MetallicRoughness.Equals(default(TextureHandle)) ? material.Albedo : material.MetallicRoughness);

    // And the emissive texture the same way (uDiffuse2.z says whether it is one).
    private static ShaderTextureBinding BakeEmissive(RayMaterial material) =>
        new("uEmissive", material.Emissive.Equals(default(TextureHandle)) ? material.Albedo : material.Emissive);

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

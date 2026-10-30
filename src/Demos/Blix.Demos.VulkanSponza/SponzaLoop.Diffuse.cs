using System.Numerics;
using Blix.Core;
using Blix.Graphics;

namespace Blix.Demos.VulkanSponza;

// The diffuse lobe of the lit shader: EON (the energy-preserving Oren-Nayar, Blix.Shaders/diffuse_eon.glsl), OPT-IN for now (--diffuse eon), in place of
// the fixed Lambert lobe (albedo / pi) it has by default. It is opt-in because the camera-path reference (reference_wave.comp) still shades with Lambert and
// tools/testbeds/materials.sh holds the GI to it: with EON on, the Khronos plant's thin sheets read 8% bright against it (ratchet 5%), so the default stays
// the lobe the reference models until the reference evaluates EON too. The lit shader applies it to the sun (a known light direction: the full f_EON), and to every irradiance it
// receives (the incident field's bounce, the sky's irradiance cube): there it multiplies E / pi by EON's albedo toward the eye, which is the exact
// answer for a cosine-distributed field and the only one an irradiance (a quantity with no direction) can ask for.
//
// The transport conserves what the roughness loses. A patch-to-patch bounce (the patch solve, a gather's hits, the probes, both references) holds no
// direction to evaluate a lobe against, so it bounces with a single albedo / pi per hit, and that albedo is baked into the rays' surface word
// (ray_surface_bake.comp): EON's HEMISPHERICAL albedo at the material's diffuse roughness (rho <E> + rho_ms (1 - <E>): the fraction of the light that
// arrives from anywhere and leaves whichever way; below rho for a rough grey, rho itself for a white one). Lambert's is rho, so with --diffuse lambert the
// word is the base colour as before. The view-dependent part (EON's albedo toward THIS eye) is applied where the light is finally seen, in the lit
// shader. Not modelled: the lobe's shape at a hit (a sun-lit hit reflects its sun with Lambert's cosine, not EON's flatter one).
// The bake runs once at load, so --diffuse is read at start and --ab diffuse changes only the lit shader, not the transport; the transport cache's key
// includes the baked words, so the two modes keep separate caches.
//
// glTF has one roughness and no diffuse roughness, so the diffuse roughness r is the material's roughness times --diffuse-roughness-scale
// (default 1). Per-material overrides belong to the cook (a material patch), not to this loop.
//
//   --diffuse eon                  the EON lobe (default: the fixed Lambert lobe)
//   --diffuse lambert              the old fixed Lambert lobe
//   --diffuse-roughness-scale x    r = roughness * x, clamped to 0..1 (default 1; 0 is Lambert whatever the model)
//   --ab diffuse                   alternate EON (on phase) and Lambert (off phase) in-process, like the other --ab modes
internal sealed partial class SponzaLoop
{
    private bool diffuseEon;
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

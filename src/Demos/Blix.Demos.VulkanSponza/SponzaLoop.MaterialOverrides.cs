using System.Globalization;
using System.Numerics;
using Blix;
using Blix.Core;

namespace Blix.Demos.VulkanSponza;

// Per-material overrides, from the command line, applied to the imported material before anything reads it: the lit shader, the rays' surface bake,
// the transport and both references then all see the one material. For testing what a material parameter does against the reference, and for
// the references' own checks (a pane at ior 1.45, a leaf at diffuse roughness 0.4) on any scene, without re-cooking it.
//
//   --material "<name>:key=value,key=value"     repeatable; <name> is a case-insensitive part of the material's name ("*": every material)
//
// keys (all optional; a later --material overrides an earlier one for the keys it names):
//   roughness, metallic       the factors (the textures still multiply them)
//   base=r/g/b                the base colour factor (linear)
//   ior                       KHR_materials_ior
//   specular                  KHR_materials_specular factor
//   transmission              KHR_materials_transmission factor (0..1)
//   thickness                 KHR_materials_volume thickness in metres: 0 is a THIN-WALLED sheet (the transmitted light passes straight on), above 0
//                             a SOLID (the transmitted light refracts at the surface and is attenuated inside)
//   att-distance, att=r/g/b   the volume's attenuation distance and colour (Beer-Lambert)
//   dt                        KHR_materials_diffuse_transmission factor
//   clearcoat, clearcoat-roughness            KHR_materials_clearcoat
//   sheen=r/g/b, sheen-roughness              KHR_materials_sheen
//   iridescence, iridescence-ior, iridescence-thickness   KHR_materials_iridescence (the thickness in nm: both ends of its range)
//   anisotropy, anisotropy-rotation           KHR_materials_anisotropy (strength; the rotation in radians)
//   emissive=r/g/b                            the emitted radiance factor (the textures still multiply it; the reference reads the factor only)
//   diffuse-roughness         the diffuse lobe's (EON) roughness, 0..1, in place of the material's roughness times --diffuse-roughness-scale
//
// glTF has no diffuse roughness, so that key is not a PbrMaterial field: it travels beside the material (DrawableStaging.DiffuseRoughness, RayMaterial,
// the lit shader's uMaterialParams3.x, the surface bake's uDiffuse2.x, the reference's material table) and is -1 where nothing overrides it.
internal sealed partial class SponzaLoop
{
    private sealed record MaterialOverride(string Pattern, Dictionary<string, string> Keys);

    private readonly List<MaterialOverride> materialOverrides = new();

    private void ReadMaterialOverrideArgs(AppArgs args)
    {
        foreach (var spec in args.All("material"))
        {
            var colon = spec.IndexOf(':');
            if (colon <= 0) throw new ArgumentException($"--material takes \"name:key=value,...\", not '{spec}'");
            var keys = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in spec[(colon + 1)..].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var eq = pair.IndexOf('=');
                if (eq <= 0) throw new ArgumentException($"--material '{spec}': '{pair}' is not key=value");
                var key = pair[..eq].Trim().ToLowerInvariant();
                if (key is not ("roughness" or "metallic" or "base" or "ior" or "specular" or "transmission" or "thickness" or "att-distance" or "att" or "dt" or "diffuse-roughness"
                    or "clearcoat" or "clearcoat-roughness" or "sheen" or "sheen-roughness" or "iridescence" or "iridescence-ior" or "iridescence-thickness"
                    or "anisotropy" or "anisotropy-rotation" or "emissive"))
                    throw new ArgumentException($"--material '{spec}': unknown key '{key}'");
                keys[key] = pair[(eq + 1)..].Trim();
            }
            materialOverrides.Add(new MaterialOverride(spec[..colon].Trim(), keys));
        }
    }

    // The diffuse roughness override for a material that has already had its overrides applied (its name still names it), or -1.
    private float DiffuseRoughnessOf(PbrMaterial? material)
    {
        ApplyMaterialOverrides(material, out var r);
        return r;
    }

    private static float OverrideFloat(string text) => float.Parse(text, CultureInfo.InvariantCulture);

    private static Vector3 OverrideColour(string text)
    {
        var p = text.Split('/');
        if (p.Length != 3) throw new ArgumentException($"--material: a colour is r/g/b, not '{text}'");
        return new Vector3(OverrideFloat(p[0]), OverrideFloat(p[1]), OverrideFloat(p[2]));
    }

    // The material with every override that names it applied; diffuseRoughness is -1 unless one sets it.
    private PbrMaterial? ApplyMaterialOverrides(PbrMaterial? material, out float diffuseRoughness)
    {
        diffuseRoughness = -1f;
        if (material is null || materialOverrides.Count == 0) return material;
        var result = material;
        foreach (var o in materialOverrides)
        {
            if (o.Pattern != "*" && !material.Name.Contains(o.Pattern, StringComparison.OrdinalIgnoreCase)) continue;
            var ext = result.Ext;
            foreach (var (key, text) in o.Keys)
            {
                switch (key)
                {
                    case "roughness": result = result with { RoughnessFactor = Math.Clamp(OverrideFloat(text), 0f, 1f) }; break;
                    case "metallic": result = result with { MetallicFactor = Math.Clamp(OverrideFloat(text), 0f, 1f) }; break;
                    case "base":
                        var c = OverrideColour(text);
                        result = result with { BaseColorFactor = new Vector4(c, result.BaseColorFactor.W) };
                        break;
                    case "ior": ext = ext with { IndexOfRefraction = Math.Max(1f, OverrideFloat(text)) }; break;
                    case "specular": ext = ext with { SpecularFactor = Math.Clamp(OverrideFloat(text), 0f, 1f) }; break;
                    case "transmission":
                        var t = Math.Clamp(OverrideFloat(text), 0f, 1f);
                        ext = ext with { TransmissionFactor = t };
                        result = result with { TransmissionFactor = t };
                        break;
                    case "thickness": ext = ext with { ThicknessFactor = Math.Max(0f, OverrideFloat(text)) }; break;
                    case "att-distance": ext = ext with { AttenuationDistance = OverrideFloat(text) <= 0f ? float.PositiveInfinity : OverrideFloat(text) }; break;
                    case "att": ext = ext with { AttenuationColor = OverrideColour(text) }; break;
                    case "clearcoat": ext = ext with { ClearcoatFactor = Math.Clamp(OverrideFloat(text), 0f, 1f) }; break;
                    case "clearcoat-roughness": ext = ext with { ClearcoatRoughnessFactor = Math.Clamp(OverrideFloat(text), 0f, 1f) }; break;
                    case "sheen": ext = ext with { SheenColorFactor = OverrideColour(text) }; break;
                    case "sheen-roughness": ext = ext with { SheenRoughnessFactor = Math.Clamp(OverrideFloat(text), 0f, 1f) }; break;
                    case "iridescence": ext = ext with { IridescenceFactor = Math.Clamp(OverrideFloat(text), 0f, 1f) }; break;
                    case "iridescence-ior": ext = ext with { IridescenceIor = Math.Max(1f, OverrideFloat(text)) }; break;
                    case "iridescence-thickness":
                        ext = ext with { IridescenceThicknessMinimum = Math.Max(0f, OverrideFloat(text)), IridescenceThicknessMaximum = Math.Max(0f, OverrideFloat(text)) };
                        break;
                    case "anisotropy": ext = ext with { AnisotropyStrength = Math.Clamp(OverrideFloat(text), 0f, 1f) }; break;
                    case "anisotropy-rotation": ext = ext with { AnisotropyRotation = OverrideFloat(text) }; break;
                    case "emissive": result = result with { EmissiveFactor = OverrideColour(text) }; break;
                    case "dt": ext = ext with { DiffuseTransmissionFactor = Math.Clamp(OverrideFloat(text), 0f, 1f) }; break;
                    case "diffuse-roughness": diffuseRoughness = Math.Clamp(OverrideFloat(text), 0f, 1f); break;
                }
            }
            result = result with { Extensions = ext };
        }
        return result;
    }
}

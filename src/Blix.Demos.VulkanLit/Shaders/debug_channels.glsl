// Per-pixel debug-channel selector for the lit shader. Indexed by
// ShaderChannelLabels in Program.cs:
//   0  Shaded             — handled by caller (no override)
//   1  Albedo
//   2  World normal       — including normal-map perturbation
//   3  Geometric normal   — vertex normal only
//   4  Roughness
//   5  Metallic
//   6  NdotV
//   7  Ambient (IBL)
//   8  Specular (IBL)
//   9  Diffuse (IBL)
//   10 Sun shadow
//   11 UVs
//
// Caller is responsible for guarding on mode > 0; channels still pass
// through present's exposure + ACES tonemap, so 0..1 channels read
// close-to-true but not raw.
vec3 selectDebugChannel(
    int mode,
    vec3 albedo,
    vec3 worldN,
    vec3 geomN,
    float roughness,
    float metallic,
    float NdotV,
    vec3 ambient,
    vec3 specularIBL,
    vec3 diffuseIBLContribution,
    float sunShadow,
    vec2 uv)
{
    if (mode == 1)       return albedo;
    else if (mode == 2)  return worldN * 0.5 + 0.5;
    else if (mode == 3)  return geomN * 0.5 + 0.5;
    else if (mode == 4)  return vec3(roughness);
    else if (mode == 5)  return vec3(metallic);
    else if (mode == 6)  return vec3(NdotV);
    else if (mode == 7)  return ambient;
    else if (mode == 8)  return specularIBL;
    else if (mode == 9)  return diffuseIBLContribution;
    else if (mode == 10) return vec3(sunShadow);
    else if (mode == 11) return vec3(uv, 0.0);
    return vec3(0.0);
}

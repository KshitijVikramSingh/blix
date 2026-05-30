#version 450

// Lit fragment shader — Cook-Torrance split-sum IBL on top of a Lambert N·L
// sun term, with cascaded shadows, a Fresnel-glass branch, and froxel-fog
// composite.
//
//   set 0 binding 0 : per-frame UBO (viewProj, sun, IBL strength, camera, fog)
//   set 1 binding 0 : samplerCube uIrradiance      (diffuse IBL)
//   set 1 binding 1 : samplerCube uPrefilteredEnv  (specular IBL; mip = roughness LOD)
//   set 1 binding 2 : sampler2D   uBrdfLut         (split-sum BRDF integration)
//   set 1 binding 3 : sampler2D   uCascadeShadowMaps[3]
//   set 1 binding 4 : sampler3D   uFroxelGrid      (volumetric fog)
//   set 2 binding 0 : per-material UBO (BaseColorFactor, EmissiveFactor,
//                                       MaterialParams = alphaCutoff/normalScale/
//                                       roughness/metallic, MaterialParams2 = transmission)
//   set 2 binding 1 : albedo  (sRGB)
//   set 2 binding 2 : normal  (linear; tangent-space)
//   set 2 binding 3 : emissive (sRGB)
//   set 2 binding 4 : metallic-roughness (linear; G=rough, B=metal)
//   set 2 binding 5 : occlusion (linear; R=AO)
//
// Roughness/metallic are sampled from the MR texture × per-material factors.

layout(set = 0, binding = 0) uniform Frame {
    mat4  uViewProjection;
    vec3  uSunDirection;
    float uSunIntensity;
    vec3  uAmbientColor;   // unused; kept for layout compat
    float uIblIntensity;
    vec3  uCameraPos;
    float uEnvMipCount;
    vec3  uCameraForward;          // unit camera forward (for view-depth cascade pick)
    float uShadowStrength;         // 0 = sun shadows off, 1 = on
    mat4  uCascadeViewProj[3];     // light view-proj per cascade
    vec4  uCascadeSplits;          // .xyz = far view-depth bound of cascades 0,1,2
    vec4  uShadowParams;           // .x = visualizeCascades (0/1)
    vec4  uCascadeBias;            // .xyz = per-cascade base depth bias (NDC units)
    vec4  uShaderParams;           // x=metallicThreshold, y=normalStrength, z=biasSlopeScale
    vec4  uFog;                    // x=screenW, y=screenH, z=fogFar, w=enabled(0/1)
} frame;

layout(set = 1, binding = 0) uniform samplerCube uIrradiance;
layout(set = 1, binding = 1) uniform samplerCube uPrefilteredEnv;
layout(set = 1, binding = 2) uniform sampler2D   uBrdfLut;
// Cascaded sun shadow maps (one depth target per cascade, near→far). Sampled
// with manual depth comparison + 3×3 PCF; cascade chosen by the fragment's
// view-space depth. GLSL forbids non-uniform dynamic indexing of a sampler
// array, so the picker dispatches with constant indices (MoltenVK-safe).
layout(set = 1, binding = 3) uniform sampler2D   uCascadeShadowMaps[3];
#define CASCADE_COUNT 3
// Froxel volumetric fog grid: (xy) = screen UV, z = world distance / fogFar.
// .rgb = integrated in-scattering to that distance, .a = transmittance.
layout(set = 1, binding = 4) uniform sampler3D   uFroxelGrid;

layout(set = 2, binding = 0) uniform Material {
    vec4 uBaseColorFactor;
    vec4 uEmissiveFactor;
    vec4 uMaterialParams;  // x=alphaCutoff, y=normalScale, z=roughness, w=metallic
    vec4 uMaterialParams2; // x=transmission (KHR_materials_transmission)
} mat;

layout(set = 2, binding = 1) uniform sampler2D uAlbedo;
layout(set = 2, binding = 2) uniform sampler2D uNormalMap;
layout(set = 2, binding = 3) uniform sampler2D uEmissive;
// Metallic-roughness, LINEAR encoded:
//   G = roughness, B = metallic.
// R can carry AO when an asset packs ORM into one texture, but Sponza
// Modern ships AO as a separate OcclusionTexture and leaves MR.R empty —
// so we sample AO from its own dedicated binding below instead of MR.R.
layout(set = 2, binding = 4) uniform sampler2D uMetallicRoughness;
// Ambient occlusion (R channel, linear). Default texture is white so
// materials without an OcclusionTexture get no extra attenuation.
layout(set = 2, binding = 5) uniform sampler2D uOcclusion;

layout(location = 0) in vec3 vNormalWorld;
layout(location = 1) in vec2 vUv;
layout(location = 2) in vec3 vWorldPos;
layout(location = 3) in vec3 vTangentWorld;   // world-space tangent (forwarded glTF TANGENT.xyz)
layout(location = 4) in float vTangentSign;   // glTF TANGENT.w handedness

layout(location = 0) out vec4 outColor;

// Roughness-aware Fresnel-Schlick that softens edges as surfaces roughen
// (otherwise rough metals over-glow at grazing angles where the split-sum
// approximation breaks down).
vec3 fresnelSchlickRoughness(float cosTheta, vec3 F0, float roughness) {
    return F0 + (max(vec3(1.0 - roughness), F0) - F0) * pow(clamp(1.0 - cosTheta, 0.0, 1.0), 5.0);
}

// --- Cascaded sun shadows -----------------------------------------------
// Rotated Vogel-disk PCF: 8 evenly-spread taps on a unit disk, rotated per
// pixel by an interleaved-gradient-noise angle. The per-pixel rotation turns
// the old hard 3×3 grid into a fine dither the eye reads as a smooth penumbra
// (and hides the low-res 512² far cascade). 8 taps keeps it near the previous
// 9-tap cost; 16 was ~3× the lit-pass time. current/d are Vulkan NDC depth in
// [0,1]; the ortho projection is linear in z so a constant bias is a constant
// world-space offset.
const int   PCF_TAPS   = 8;
const float PCF_RADIUS = 2.5;   // texels; larger = softer penumbra

// Interleaved gradient noise (Jimenez) -> a [0,1) value per pixel.
float interleavedGradientNoise(vec2 p) {
    return fract(52.9829189 * fract(dot(p, vec2(0.06711056, 0.00583715))));
}

float pcfCascade(sampler2D map, vec2 uv, float current, float bias) {
    vec2 texel = 1.0 / vec2(textureSize(map, 0));
    float phi = interleavedGradientNoise(gl_FragCoord.xy) * 6.28318530718;
    float sum = 0.0;
    for (int i = 0; i < PCF_TAPS; i++) {
        // Vogel (sunflower) disk: even coverage, no precomputed table.
        float r = sqrt((float(i) + 0.5) / float(PCF_TAPS));
        float theta = float(i) * 2.39996323 + phi;   // golden angle
        vec2 off = r * vec2(cos(theta), sin(theta)) * texel * PCF_RADIUS;
        float d = texture(map, uv + off).r;
        sum += (current - bias > d) ? 0.0 : 1.0;
    }
    return sum / float(PCF_TAPS);
}

// Constant-index dispatch (see binding-3 comment): non-uniform dynamic
// sampler-array indexing isn't portable, so branch on the cascade index.
float samplePickedCascade(int idx, vec2 uv, float current, float bias) {
    if (idx == 0)      return pcfCascade(uCascadeShadowMaps[0], uv, current, bias);
    else if (idx == 1) return pcfCascade(uCascadeShadowMaps[1], uv, current, bias);
    else               return pcfCascade(uCascadeShadowMaps[2], uv, current, bias);
}

// Returns 1.0 = lit, 0.0 = shadowed. cascadeOut reports which cascade was
// sampled (-1 = none/out of range) for the debug visualisation.
float sunShadowFactor(float NdotL, out int cascadeOut) {
    cascadeOut = -1;
    if (frame.uShadowStrength <= 0.0) return 1.0;

    // Linear view-space depth = projection of (frag - eye) onto camera fwd.
    float viewDepth = dot(vWorldPos - frame.uCameraPos, frame.uCameraForward);

    // First cascade whose far bound contains this fragment.
    int idx = -1;
    for (int i = 0; i < CASCADE_COUNT; i++) {
        if (viewDepth <= frame.uCascadeSplits[i]) { idx = i; break; }
    }
    if (idx < 0) return 1.0;   // beyond the last cascade — leave fully lit

    vec4 proj = frame.uCascadeViewProj[idx] * vec4(vWorldPos, 1.0);
    vec3 ndc = proj.xyz / proj.w;
    vec2 uv = ndc.xy * 0.5 + 0.5;
    float current = ndc.z;
    if (uv.x < 0.0 || uv.x > 1.0 || uv.y < 0.0 || uv.y > 1.0 ||
        current < 0.0 || current > 1.0) {
        return 1.0;
    }
    cascadeOut = idx;

    // Depth bias. The base term is computed on the CPU per cascade from that
    // cascade's actual world-space texel size ÷ ortho depth range, so it's
    // geometrically grounded rather than a magic constant — it stays constant
    // in NDC across cascades even though each covers a very different world
    // extent. The slope term widens it toward grazing angles (where a single
    // shadow texel spans more depth across the surface → more self-shadow
    // acne). Capped so steep grazing surfaces don't peter-pan.
    float slope = clamp(1.0 - NdotL, 0.0, 1.0);
    float bias = frame.uCascadeBias[idx] * (1.0 + slope * frame.uShaderParams.z);
    return samplePickedCascade(idx, uv, current, bias);
}

const vec3 kCascadeTint[3] = vec3[3](
    vec3(1.0, 0.35, 0.35),   // cascade 0 — red
    vec3(0.35, 1.0, 0.35),   // cascade 1 — green
    vec3(0.4, 0.5, 1.0));    // cascade 2 — blue

void main() {
    // UVs arrive in the correct top-down origin already: the Sponza assets are
    // imported with AssetImportContext.FlipTextureV, which bakes the V-flip
    // into the vertex buffer at load. Nothing to do here.
    vec2 uv = vUv;

    // --- Albedo + alpha test --------------------------------------------
    vec4 sampled = texture(uAlbedo, uv);
    vec4 albedo4 = sampled * mat.uBaseColorFactor;
    float alphaCutoff = mat.uMaterialParams.x;
    // Alpha-to-coverage edge for cutout materials (alphaCutoff > 0): sharpen the
    // sampled alpha to ~1px around the cutoff via its screen-space derivative,
    // so the lit pipeline's alpha-to-coverage turns it into a smooth MSAA leaf
    // silhouette instead of a hard binary edge. Fully-outside texels discard.
    // Opaque (alphaCutoff == 0) keeps coverage 1 → alpha-to-coverage is a no-op.
    float coverage = 1.0;
    if (alphaCutoff > 0.0) {
        coverage = clamp((albedo4.a - alphaCutoff) / max(fwidth(albedo4.a), 1e-5) + 0.5, 0.0, 1.0);
        if (coverage <= 0.0) discard;
    }
    vec3 albedo = albedo4.rgb;

    // --- Normal map (real per-vertex TBN) -------------------------------
    // Geometric normal, flipped on back faces so two-sided geometry (cypress
    // leaf cards, curtains) lights from the inside. Single-sided geometry is
    // back-face culled, so the flip is a no-op there.
    vec3 N = normalize(vNormalWorld);
    if (!gl_FrontFacing) N = -N;
    // TBN from the forwarded glTF tangent. Gram-Schmidt re-orthonormalize the
    // tangent against N (removes interpolation drift); bitangent handedness
    // from TANGENT.w. This replaces the old screen-space-derivative frame,
    // which swirled on sculpted / mirrored-UV geometry (lavabo, lion heads).
    vec3 T = normalize(vTangentWorld - N * dot(N, vTangentWorld));
    vec3 B = cross(N, T) * vTangentSign;
    // Reconstruct Z from XY. Cooked normals are BC5 (2-channel RG, blue
    // dropped), so the sampled .z is meaningless — derive it from the
    // unit-length constraint. This is also correct for RGBA8 normal maps
    // (their stored Z ≈ sqrt(1 - x² - y²)), so it works for both paths.
    float normalScale = mat.uMaterialParams.y * frame.uShaderParams.y;
    vec2 nxy = (texture(uNormalMap, uv).xy * 2.0 - 1.0) * normalScale;
    float nz = sqrt(max(0.0, 1.0 - dot(nxy, nxy)));
    // Default normal map is flat (0,0,1), so untextured materials keep N.
    N = normalize(mat3(T, B, N) * vec3(nxy, nz));

    // --- PBR scalars (factor × texture) ---------------------------------
    // MR.G = roughness, MR.B = metallic. AO comes from its own sampler.
    vec3 mrSample = texture(uMetallicRoughness, uv).rgb;
    float ao        = texture(uOcclusion, uv).r;
    float roughness = clamp(mat.uMaterialParams.z * mrSample.g, 0.04, 1.0);
    float metallic  = clamp(mat.uMaterialParams.w * mrSample.b, 0.0, 1.0);

    // Metalness noise-gate (asset conformance, not a global look hack).
    // Sponza Modern leaves a stray ~0.35 metalness on dielectric stone/brick
    // (its metallic channel doubled as a specular-intensity dial under the
    // authoring pipeline); read as real glTF metalness it mixes albedo into F0
    // and dulls the diffuse. The gate treats metalness below uShaderParams.x
    // as noise -> 0, but passes values at/above through UNCHANGED — unlike the
    // old binary step() it no longer slams genuine partial metals to fully
    // metal. Threshold 0 trusts the glTF verbatim (the standard); the default
    // (0.5) keeps Sponza's stone clean. Live-tunable: Material -> Metallic
    // threshold.
    metallic = metallic >= frame.uShaderParams.x ? metallic : 0.0;
    vec3 F0 = mix(vec3(0.04), albedo, metallic);

    vec3 V = normalize(frame.uCameraPos - vWorldPos);
    float NdotV = max(dot(N, V), 0.0);
    vec3 R = reflect(-V, N);

    // --- Transmissive glass (KHR_materials_transmission) ----------------
    // Shade as Fresnel glass: the reflected fraction (dielectric Fresnel,
    // F0 = 0.04, ramping to 1 at grazing) becomes the blend opacity, so the
    // environment reflection composites over the scene behind:
    //     result = envReflection * F + background * (1 - F)
    // The src colour is the un-weighted environment reflection; the blend
    // multiplies it by alpha = F, giving the Fresnel split. Transmission opens
    // the head-on view to the scene behind instead of reading near-black.
    // (No refraction/absorption tint yet — that's the KHR transmission pass.)
    float transmission = mat.uMaterialParams2.x;
    if (transmission > 0.0) {
        float lod = roughness * (frame.uEnvMipCount - 1.0);
        vec3 envRefl = textureLod(uPrefilteredEnv, R, lod).rgb * frame.uIblIntensity;
        float fresnel = 0.04 + 0.96 * pow(clamp(1.0 - NdotV, 0.0, 1.0), 5.0);
        float glassAlpha = mix(albedo4.a, fresnel, transmission);
        // Physically clean glass is ~96% transparent head-on, which reads as
        // "no glass at all". Lift it by a tunable floor (uShaderParams.w) so
        // the panes keep a faint reflective sheen straight-on. Grazing angles
        // already saturate to opaque, so this only affects the head-on view.
        glassAlpha = max(glassAlpha, frame.uShaderParams.w);
        outColor = vec4(envRefl, glassAlpha);
        return;
    }

    // --- Direct sun (Lambert) + cascaded shadow -------------------------
    vec3 L = -normalize(frame.uSunDirection);
    float NdotL = max(dot(N, L), 0.0);
    int shadowCascade;
    float sunShadow = sunShadowFactor(NdotL, shadowCascade);
    vec3 direct = albedo * NdotL * frame.uSunIntensity * sunShadow;

    // --- IBL: split-sum diffuse + specular ------------------------------
    // Diffuse: irradiance cube × albedo, modulated by (1 - F) and (1 - metallic)
    // (metallics have no diffuse contribution).
    vec3 F = fresnelSchlickRoughness(NdotV, F0, roughness);
    vec3 kS = F;
    vec3 kD = (vec3(1.0) - kS) * (1.0 - metallic);

    vec3 irradiance = texture(uIrradiance, N).rgb;
    vec3 diffuseIBL = irradiance * albedo;

    // Specular: prefiltered env at LOD = roughness × (mipCount - 1), times
    // the BRDF LUT integration (split-sum approximation of the specular term).
    float lod = roughness * (frame.uEnvMipCount - 1.0);
    vec3 prefiltered = textureLod(uPrefilteredEnv, R, lod).rgb;
    vec2 envBrdf = texture(uBrdfLut, vec2(NdotV, roughness)).rg;
    vec3 specularIBL = prefiltered * (F * envBrdf.x + envBrdf.y);

    // AO attenuates the indirect contribution only (per glTF spec).
    vec3 ambient = (kD * diffuseIBL + specularIBL) * frame.uIblIntensity * ao;

    // --- Emissive ------------------------------------------------------
    vec3 emissive = texture(uEmissive, uv).rgb * mat.uEmissiveFactor.rgb * mat.uEmissiveFactor.a;

    vec3 color = direct + ambient + emissive;

    // Debug: tint by which cascade shadowed this fragment (red/green/blue,
    // near→far). Helps confirm split placement + texel-snap stability.
    if (frame.uShadowParams.x > 0.5 && shadowCascade >= 0) {
        color = mix(color, kCascadeTint[shadowCascade] * (0.5 + 0.5 * NdotL * sunShadow), 0.4);
    }

    // --- Froxel fog composite -------------------------------------------
    // Sample the pre-integrated scattering grid at this fragment's screen UV
    // and radial distance, then apply: lit*transmittance + in-scatter. The
    // grid is filled by the froxel compute pass earlier this frame.
    if (frame.uFog.w > 0.5) {
        vec2 fuv = gl_FragCoord.xy / frame.uFog.xy;
        float dist = length(vWorldPos - frame.uCameraPos);
        float w = clamp(dist / frame.uFog.z, 0.0, 1.0);
        vec4 fog = texture(uFroxelGrid, vec3(fuv, w));
        color = color * fog.a + fog.rgb;
    }

    // Output coverage (not raw alpha) so alpha-to-coverage gets the sharpened
    // cutout edge; opaque keeps coverage 1.0 → fully covered.
    outColor = vec4(color, coverage);
}

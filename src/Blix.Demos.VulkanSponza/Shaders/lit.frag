#version 450

// Scaffold lit fragment shader — Cook-Torrance split-sum IBL on top of a
// Lambert N·L sun term.
//
//   set 0 binding 0 : per-frame UBO (viewProj, sun, IBL strength, camera)
//   set 1 binding 0 : samplerCube uIrradiance   (diffuse IBL)
//   set 1 binding 1 : samplerCube uPrefilteredEnv (specular IBL, mip chain
//                                                  = roughness-LOD stand-in)
//   set 1 binding 2 : sampler2D   uBrdfLut      (split-sum BRDF integration)
//   set 2 binding 0 : per-material UBO (BaseColorFactor, EmissiveFactor,
//                                       MaterialParams = alphaCutoff,
//                                       normalScale, roughness, metallic)
//   set 2 binding 1 : albedo  (sRGB)
//   set 2 binding 2 : normal  (linear; tangent-space)
//   set 2 binding 3 : emissive (sRGB)
//
// IBL replaces the prior hemispherical-ambient stand-in. Specular response
// uses the per-material roughness + metallic FACTORS today; metallic-
// roughness TEXTURE sampling lands in the next push (same shader, just
// multiplies texture × factor and replaces the constant).

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

layout(set = 2, binding = 0) uniform Material {
    vec4 uBaseColorFactor;
    vec4 uEmissiveFactor;
    vec4 uMaterialParams;  // x=alphaCutoff, y=normalScale, z=roughness, w=metallic
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

layout(location = 0) out vec4 outColor;

// Screen-space-derivative TBN — the engine's static-mesh vertex layout
// doesn't carry per-vertex tangents, so we synthesise the tangent basis
// from world-position + UV derivatives. Reference: Christian Schüler,
// "Followup: Normal Mapping Without Precomputed Tangents."
vec3 perturbNormal(vec3 N, vec3 worldPos, vec2 uv, vec3 tangentNormal) {
    vec3 dp1 = dFdx(worldPos);
    vec3 dp2 = dFdy(worldPos);
    vec2 duv1 = dFdx(uv);
    vec2 duv2 = dFdy(uv);
    vec3 dp2perp = cross(dp2, N);
    vec3 dp1perp = cross(N, dp1);
    vec3 T = dp2perp * duv1.x + dp1perp * duv2.x;
    vec3 B = dp2perp * duv1.y + dp1perp * duv2.y;
    float invMax = inversesqrt(max(dot(T, T), dot(B, B)));
    mat3 TBN = mat3(T * invMax, B * invMax, N);
    return normalize(TBN * tangentNormal);
}

// Roughness-aware Fresnel-Schlick that softens edges as surfaces roughen
// (otherwise rough metals over-glow at grazing angles where the split-sum
// approximation breaks down).
vec3 fresnelSchlickRoughness(float cosTheta, vec3 F0, float roughness) {
    return F0 + (max(vec3(1.0 - roughness), F0) - F0) * pow(clamp(1.0 - cosTheta, 0.0, 1.0), 5.0);
}

// --- Cascaded sun shadows -----------------------------------------------
// 3×3 PCF average on one cascade. current/s are Vulkan NDC depth in [0,1];
// the ortho projection is linear in z so a constant bias maps to a constant
// world-space offset.
float pcfCascade(sampler2D map, vec2 uv, float current, float bias) {
    vec2 texel = 1.0 / vec2(textureSize(map, 0));
    float sum = 0.0;
    for (int x = -1; x <= 1; x++)
    for (int y = -1; y <= 1; y++) {
        float s = texture(map, uv + vec2(x, y) * texel).r;
        sum += (current - bias > s) ? 0.0 : 1.0;
    }
    return sum / 9.0;
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
    if (alphaCutoff > 0.0 && albedo4.a < alphaCutoff) discard;
    vec3 albedo = albedo4.rgb;

    // --- Normal map ----------------------------------------------------
    // Screen-space TBN normal mapping is great on tileable wall surfaces
    // (large UV span, well-defined UV gradient per fragment) but breaks
    // on sculpted geometry with mirrored or compressed UV islands — the
    // dFdx/dFdy of UV degenerate, the synthesized tangent flips sign
    // across triangle boundaries, and the perturbed normal swirls. The
    // lavabo / lion-head fountains and other ornamental geometry hit
    // this. Proper fix is MikkT per-vertex tangents (engine change).
    // Scaffold fix: smoothly fade out normal mapping when UV derivatives
    // are tiny — surfaces where the screen-space TBN can't be trusted
    // fall back to the geometric normal.
    vec3 N = normalize(vNormalWorld);
    // Two-sided lighting: on a back-facing fragment the geometric normal
    // points away from the viewer, so flip it to face front. Single-sided
    // geometry is back-face culled (gl_FrontFacing always true → no-op);
    // this only fires on double-sided surfaces — the cypress-tree leaf cards
    // and curtains — whose back faces were shading as N·L<0 and reading as
    // dark/"inverted" foliage. Flip before normal mapping so the synthesized
    // tangent frame mirrors consistently on the back side.
    if (!gl_FrontFacing) N = -N;
    vec2 duv1 = dFdx(uv);
    vec2 duv2 = dFdy(uv);
    float uvDensity = length(duv1) + length(duv2);
    float normalMix = smoothstep(0.005, 0.02, uvDensity);
    if (normalMix > 0.0) {
        vec3 tangentN = texture(uNormalMap, uv).xyz * 2.0 - 1.0;
        float normalScale = mat.uMaterialParams.y * frame.uShaderParams.y;
        tangentN.xy *= normalScale;
        tangentN = normalize(tangentN);
        vec3 perturbed = perturbNormal(N, vWorldPos, uv, tangentN);
        N = normalize(mix(N, perturbed, normalMix));
    }

    // --- PBR scalars (factor × texture) ---------------------------------
    // MR.G = roughness, MR.B = metallic. AO comes from its own sampler.
    vec3 mrSample = texture(uMetallicRoughness, uv).rgb;
    float ao        = texture(uOcclusion, uv).r;
    float roughness = clamp(mat.uMaterialParams.z * mrSample.g, 0.04, 1.0);
    float metallic  = clamp(mat.uMaterialParams.w * mrSample.b, 0.0, 1.0);

    // Sponza-specific compensation: most of Sponza Modern's "metalness"
    // textures cap at ~0.35 for stone/brick/columns (authored under a
    // pipeline where the metallic channel doubled as specular-intensity).
    // Treating those values as real glTF metalness mixes albedo into F0
    // partially and crushes diffuse — surfaces darken to grey/black. A
    // step at 0.5 keeps the one genuinely-metallic asset (the iron door)
    // metallic while zero-ing the stone baseline. NOT a generic fix; it's
    // a scaffold-level patch over an asset quirk. Threshold is live-tunable
    // from the overlay (Material → Metallic threshold); 1.0 = all dielectric.
    metallic = step(frame.uShaderParams.x, metallic);
    vec3 F0 = mix(vec3(0.04), albedo, metallic);

    vec3 V = normalize(frame.uCameraPos - vWorldPos);
    float NdotV = max(dot(N, V), 0.0);
    vec3 R = reflect(-V, N);

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

    outColor = vec4(color, albedo4.a);
}

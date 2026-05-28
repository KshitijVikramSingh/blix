// Image-based lighting bits that are specific to the ambient term — kept
// separate from brdf.glsl because IBL uses a roughness-aware Fresnel and
// a fixed prefilter LOD ceiling that direct lighting doesn't need.
//
// Sébastien Lagarde's roughness-aware Schlick approximation. Compresses
// Fresnel rim at high roughness so the IBL ambient doesn't pop a sharp
// edge that the prefiltered env can't possibly justify.
vec3 fresnelSchlickRoughness(float cosTheta, vec3 F0, float roughness) {
    vec3 r = max(vec3(1.0 - roughness), F0);
    return F0 + (r - F0) * pow(clamp(1.0 - cosTheta, 0.0, 1.0), 5.0);
}

// Top mip index of the prefiltered specular env cube. EnvMips - 1 in the
// C# bake; sampling roughness * MAX_REFLECTION_LOD picks the right
// pre-integrated GGX response per draw.
const float MAX_REFLECTION_LOD = 6.0;

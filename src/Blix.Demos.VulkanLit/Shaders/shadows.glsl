// Shadow-map sampling helpers. Two flavours:
//   sampleShadow      — 2D map with a perspective/ortho projection (sun + spot).
//                       3×3 PCF average kills single-tap shimmer under motion.
//   samplePointShadow — depth cube (omnidirectional point light). Disk PCF
//                       across 20 fixed offsets, radius grows with distance.
//
// Returns 1.0 = fully lit, 0.0 = fully shadowed; values in between are PCF
// fractions. NdotL biases the depth comparison so grazing angles don't
// shadow-acne.

float sampleShadow(sampler2D map, vec4 coord, float NdotL) {
    vec3 ndc = coord.xyz / coord.w;
    vec2 uv = ndc.xy * 0.5 + 0.5;
    float current = ndc.z;
    if (uv.x < 0.0 || uv.x > 1.0 || uv.y < 0.0 || uv.y > 1.0 ||
        current < 0.0 || current > 1.0) {
        return 1.0;
    }
    float bias = mix(0.005, 0.0005, NdotL);
    vec2 texel = 1.0 / vec2(textureSize(map, 0));
    float sum = 0.0;
    for (int x = -1; x <= 1; x++)
    for (int y = -1; y <= 1; y++) {
        float s = texture(map, uv + vec2(x, y) * texel).r;
        sum += (current - bias > s) ? 0.0 : 1.0;
    }
    return sum / 9.0;
}

const vec3 kCubeOffsets[20] = vec3[](
    vec3( 1, 1, 1), vec3( 1,-1, 1), vec3(-1,-1, 1), vec3(-1, 1, 1),
    vec3( 1, 1,-1), vec3( 1,-1,-1), vec3(-1,-1,-1), vec3(-1, 1,-1),
    vec3( 1, 1, 0), vec3( 1,-1, 0), vec3(-1,-1, 0), vec3(-1, 1, 0),
    vec3( 1, 0, 1), vec3(-1, 0, 1), vec3( 1, 0,-1), vec3(-1, 0,-1),
    vec3( 0, 1, 1), vec3( 0,-1, 1), vec3( 0,-1,-1), vec3( 0, 1,-1));

float samplePointShadow(samplerCube cube, vec3 fromLight, float currentNorm, float dist) {
    float bias = 0.02;
    float radius = (1.0 + dist * 0.1) * 0.01;
    float sum = 0.0;
    for (int i = 0; i < 20; i++) {
        float s = texture(cube, fromLight + kCubeOffsets[i] * radius).r;
        sum += (currentNorm - bias > s) ? 0.0 : 1.0;
    }
    return sum / 20.0;
}

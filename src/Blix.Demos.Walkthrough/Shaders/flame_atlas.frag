#version 410 core

// Atlas-driven flame. Samples Unity Labs Paris's CC-licensed "Flame02
// temperature" flipbook (see fire_atlas.LICENSE.txt) and maps each pixel's
// grayscale temperature value through a blackbody-ish colour ramp.
//
// Why temperature data instead of pre-coloured fire?
//   * Lets the engine pick the hue at runtime: amber lantern, blue gas
//     burner, arcane green torch -- all from one source asset.
//   * Preserves the simulation's energy/density structure independent of
//     which colour LUT we apply, so a future bloom/post pass can use the
//     raw temperature as an emissive source.
//
// Frame-to-frame blending: floor(t) gives the current frame, fract(t) the
// blend factor to the next. Smooth playback at any render fps regardless of
// uAtlasFps.

in vec2 vUv;
layout(location = 0) out vec4 fragColor;
layout(location = 1) out vec4 fragMaterial;

uniform sampler2D uFlameAtlas;
uniform float uAtlasCols;       // 16
uniform float uAtlasRows;       // 4
uniform float uAtlasFrames;     // total frames played in the loop. 16 stays
                                // in row 0; 64 cycles row-major across rows.
uniform float uAtlasFps;
uniform float uTime;
uniform vec3  uFlameColor;      // tint multiplier on the colour ramp
uniform float uFlameIntensity;
uniform float uExposure;
uniform float uFramePhase;      // per-flame offset, in frames

// Read one frame's temperature scalar at the given quad-uv (v=0 at base,
// v=1 at tip). Composes two coordinate flips:
//   1. Sprite-sheet row order: frame 0 is top-left of the original PNG.
//   2. ImageLoader.LoadRgba32's stbi_set_flip_vertically_on_load, which
//      stores the texture upside-down in GPU memory.
// The expression below maps quad-(0..1) v into the matching post-flip
// GPU-v for the requested frame's cell.
float sampleTemperature(float frameIdx, vec2 quadUv)
{
    float idx = mod(frameIdx, uAtlasFrames);
    float col = mod(idx, uAtlasCols);
    float row = floor(idx / uAtlasCols);

    float u = (col + quadUv.x) / uAtlasCols;
    float v = 1.0 - (row + 1.0 - quadUv.y) / uAtlasRows;
    return texture(uFlameAtlas, vec2(u, v)).r;
}

// Blackbody-inspired ramp from a temperature scalar in [0..1] to RGB.
// Bands chosen by eye, with the tail going HDR (>1.0) so ACES will halo
// the white-hot tip naturally. Multiplied by uFlameColor downstream, so
// the caller's tint biases the whole palette.
vec3 temperatureToFireColor(float t)
{
    t = clamp(t, 0.0, 1.0);
    vec3 c = vec3(0.0);
    c += vec3(0.65, 0.05, 0.00) * smoothstep(0.05, 0.30, t);
    c += vec3(0.80, 0.30, 0.05) * smoothstep(0.25, 0.55, t);
    c += vec3(0.70, 0.55, 0.10) * smoothstep(0.50, 0.80, t);
    c += vec3(0.55, 0.50, 0.30) * smoothstep(0.75, 0.95, t);
    // Super-hot core: pushes past 1.0 so ACES bloomy-rolloff kicks in
    c += vec3(0.80, 0.70, 0.45) * smoothstep(0.88, 1.00, t);
    return c;
}

void main()
{
    float t = uTime * uAtlasFps + uFramePhase;
    float idx = floor(t);
    float blend = fract(t);

    float ta = sampleTemperature(idx,       vUv);
    float tb = sampleTemperature(idx + 1.0, vUv);
    float temperature = mix(ta, tb, blend);

    if (temperature < 0.04) discard;

    vec3 col = temperatureToFireColor(temperature)
             * uFlameColor
             * uFlameIntensity
             * uExposure;

    // Soft alpha from the temperature itself: feathers the silhouette edge
    // so the quad never reads as a hard rectangle. The atlas has clean
    // black backgrounds, so smoothstep on the raw temperature is enough.
    float alpha = smoothstep(0.04, 0.25, temperature);

    // Quad-edge fades. Some atlas frames have the flame reaching all the way
    // to the cell top (or close to the side), which on the billboard reads
    // as a hard horizontal line at the quad boundary. Fading alpha near the
    // four edges of the quad in UV space kills the rectangle tell for free.
    float topFade    = smoothstep(1.00, 0.85, vUv.y);
    float bottomFade = smoothstep(0.00, 0.04, vUv.y);
    float horizFade  = smoothstep(0.00, 0.06, vUv.x) * smoothstep(1.00, 0.94, vUv.x);
    alpha *= topFade * bottomFade * horizFade;

    if (alpha < 0.005) discard;
    fragColor = vec4(col, alpha);
    fragMaterial = vec4(1.0, 0.0, 0.0, alpha);
}

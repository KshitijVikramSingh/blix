#version 450

// Y2K cyber/vapor CRT post-FX, single fullscreen pass (ported from the GL Pong).
//
//   uScene    - offscreen game render (Rgba8) at 2x the on-screen play rect.
//   uPlayRect - (left, bottom, right, top) of the play area in screen UVs.
//   uTime     - seconds since start; drives scanline drift + grain.
//   uTexel    - 1.0 / sceneTextureSize for blur taps.
//   uShake    - vec2 UV offset for screen shake; play rect only (bezel stays still).
//   uWinFlash - rgb = winner side colour, a = intensity 0..1 (decays on victory).
//
// Params ride a fragment push constant instead of a UBO — fewer than 64 bytes,
// no descriptor set needed alongside the set-0 combined sampler.

layout(set = 0, binding = 0) uniform sampler2D uScene;

layout(push_constant) uniform Push {
    vec4 uPlayRect;
    vec2 uTexel;
    vec2 uShake;
    vec4 uWinFlash;
    float uTime;
} pc;

layout(location = 0) in vec2 vScreenUV;
layout(location = 0) out vec4 fragColor;

// The swapchain is B8G8R8A8_SRGB and hardware-encodes linear->sRGB on write.
// This grade runs in display space (ported from the GL build, which presented
// without a second encode), so convert back to linear before output — the
// swapchain's encode then reproduces the authored values instead of
// double-brightening them (which blew out the bright text + paddles).
vec3 srgbToLinear(vec3 c)
{
    return mix(c / 12.92, pow((c + 0.055) / 1.055, vec3(2.4)), step(0.04045, c));
}

vec3 sampleGlow(vec2 uv, float radius)
{
    vec2 d = pc.uTexel * radius;
    vec3 sum = vec3(0.0);
    sum += texture(uScene, uv + vec2(-d.x, -d.y)).rgb;
    sum += texture(uScene, uv + vec2( 0.0, -d.y)).rgb;
    sum += texture(uScene, uv + vec2( d.x, -d.y)).rgb;
    sum += texture(uScene, uv + vec2(-d.x,  0.0)).rgb;
    sum += texture(uScene, uv + vec2( d.x,  0.0)).rgb;
    sum += texture(uScene, uv + vec2(-d.x,  d.y)).rgb;
    sum += texture(uScene, uv + vec2( 0.0,  d.y)).rgb;
    sum += texture(uScene, uv + vec2( d.x,  d.y)).rgb;
    return sum / 8.0;
}

void main()
{
    vec2 uv = vScreenUV;
    vec4 playRect = pc.uPlayRect;
    bool inside = uv.x >= playRect.x && uv.x <= playRect.z
               && uv.y >= playRect.y && uv.y <= playRect.w;

    if (!inside)
    {
        vec2 center = vec2(0.5 * (playRect.x + playRect.z),
                           0.5 * (playRect.y + playRect.w));
        float rad = length(uv - center);
        vec3 indigo = vec3(0.025, 0.020, 0.055);
        vec3 magenta = vec3(0.075, 0.025, 0.080);
        vec3 col0 = mix(magenta, indigo, smoothstep(0.0, 0.9, rad));
        fragColor = vec4(col0, 1.0);
        return;
    }

    vec2 playUV = vec2(
        (uv.x - playRect.x) / (playRect.z - playRect.x),
        (uv.y - playRect.y) / (playRect.w - playRect.y));
    playUV += pc.uShake;
    vec2 centered = playUV * 2.0 - 1.0;

    // Luminance-gated chromatic aberration (bright paddles stop fringing).
    vec3 baseRgb = texture(uScene, playUV).rgb;
    float lum = dot(baseRgb, vec3(0.299, 0.587, 0.114));
    float caGate = 1.0 - smoothstep(0.30, 0.85, lum);
    float caAmount = length(centered) * 0.005 * caGate;
    vec2 caDir = centered;
    float r = texture(uScene, playUV + caDir * caAmount).r;
    float g = baseRgb.g;
    float b = texture(uScene, playUV - caDir * caAmount).b;
    vec3 col = vec3(r, g, b);

    // Dual-radius bloom: tight ring + wide halo.
    vec3 glow1 = sampleGlow(playUV, 5.0);
    vec3 glow2 = sampleGlow(playUV, 14.0);
    float g1Mag = max(max(glow1.r, glow1.g), glow1.b);
    float g2Mag = max(max(glow2.r, glow2.g), glow2.b);
    float key1 = smoothstep(0.20, 0.80, g1Mag);
    float key2 = smoothstep(0.10, 0.60, g2Mag);
    float flashKey = pc.uWinFlash.a * pc.uWinFlash.a;
    col += glow1 * key1 * (1.15 + flashKey * 1.5);
    col += glow2 * key2 * (0.65 + flashKey * 1.2);

    // Magenta lift in shadows, cyan gain in highlights.
    vec3 lift = vec3(0.06, 0.00, 0.10);
    vec3 gain = vec3(0.05, 0.20, 0.25);
    col = col * (1.0 + gain) + lift * (1.0 - smoothstep(0.0, 0.6, col));

    // Animated scanlines.
    float scanY = playUV.y * 600.0 + pc.uTime * 30.0;
    float scan = 0.92 + 0.08 * sin(scanY);
    col *= scan;

    // Square-distance vignette.
    float vig = 1.0 - dot(centered, centered) * 0.22;
    col *= vig;

    // Win-flash side-colour tint.
    col = mix(col, col + pc.uWinFlash.rgb, flashKey * 0.45);

    // Hash grain so flat areas don't band.
    float grain = fract(sin(dot(uv.xy + pc.uTime, vec2(12.9898, 78.233))) * 43758.5453);
    col += (grain - 0.5) * 0.025;

    fragColor = vec4(srgbToLinear(clamp(col, 0.0, 1.0)), 1.0);
}

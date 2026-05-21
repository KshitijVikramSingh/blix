#version 410 core

// Y2K cyber/vapor post-FX, single fullscreen pass.
//
// Inputs:
//   uScene    - offscreen game render (Rgba8) at 2x the on-screen play rect.
//   uPlayRect - (left, bottom, right, top) of the play area in GL screen UVs.
//   uTime     - seconds since start; drives scanline drift + grain.
//   uTexel    - 1.0 / sceneTextureSize for blur taps.
//   uShake    - vec2 UV offset for screen shake; sampled inside the play rect
//               only (bezel stays still).
//
// CA is gated by inverse luminance so already-bright pixels (white paddles)
// don't fringe; dim playfield pixels still get the chromatic shimmer.
// Bloom is dual-radius (tight + wide) for a real "halo" instead of a thin
// ring around the sources.

in vec2 vScreenUV;
out vec4 fragColor;

uniform sampler2D uScene;
uniform vec4 uPlayRect;
uniform float uTime;
uniform vec2 uTexel;
uniform vec2 uShake;
// rgb = winner's side colour; a = intensity 0..1 (decays from 1 on victory).
// Drives a brief whole-screen tint + extra bloom that sells the moment of win
// before the overlay text settles.
uniform vec4 uWinFlash;

vec3 sampleGlow(vec2 uv, float radius)
{
    vec2 d = uTexel * radius;
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
    bool inside = uv.x >= uPlayRect.x && uv.x <= uPlayRect.z
               && uv.y >= uPlayRect.y && uv.y <= uPlayRect.w;

    if (!inside)
    {
        vec2 center = vec2(0.5 * (uPlayRect.x + uPlayRect.z),
                           0.5 * (uPlayRect.y + uPlayRect.w));
        float r = length(uv - center);
        vec3 indigo = vec3(0.025, 0.020, 0.055);
        vec3 magenta = vec3(0.075, 0.025, 0.080);
        vec3 col = mix(magenta, indigo, smoothstep(0.0, 0.9, r));
        fragColor = vec4(col, 1.0);
        return;
    }

    // Play UV with screen-shake offset (UV-space; clamped magnitude in C#).
    vec2 playUV = vec2(
        (uv.x - uPlayRect.x) / (uPlayRect.z - uPlayRect.x),
        (uv.y - uPlayRect.y) / (uPlayRect.w - uPlayRect.y));
    playUV += uShake;
    vec2 centered = playUV * 2.0 - 1.0;

    // Base sample. Luminance gates CA so the white paddles stop fringing while
    // the dark playfield + colour text keep the chromatic shimmer.
    vec3 baseRgb = texture(uScene, playUV).rgb;
    float lum = dot(baseRgb, vec3(0.299, 0.587, 0.114));
    float caGate = 1.0 - smoothstep(0.30, 0.85, lum);
    float caAmount = length(centered) * 0.005 * caGate;
    vec2 caDir = centered;
    float r = texture(uScene, playUV + caDir * caAmount).r;
    float g = baseRgb.g;
    float b = texture(uScene, playUV - caDir * caAmount).b;
    vec3 col = vec3(r, g, b);

    // Dual-radius bloom. Tight 5px gives the "glow ring," wide 14px gives the
    // soft outer halo. Summed contributions add up to a real halo instead of a
    // hairline outline. Threshold is gentle so the score text (~30% alpha) is
    // still pulled in.
    vec3 glow1 = sampleGlow(playUV, 5.0);
    vec3 glow2 = sampleGlow(playUV, 14.0);
    float g1Mag = max(max(glow1.r, glow1.g), glow1.b);
    float g2Mag = max(max(glow2.r, glow2.g), glow2.b);
    float key1 = smoothstep(0.20, 0.80, g1Mag);
    float key2 = smoothstep(0.10, 0.60, g2Mag);
    // Win flash boosts bloom contribution: bright pixels (paddles, ball, score
    // text) bloom dramatically harder during the flash so the play rect reads
    // as "ignited" briefly. Square the alpha so the bulk of the pulse happens
    // in the first ~third of the flash window.
    float flashKey = uWinFlash.a * uWinFlash.a;
    col += glow1 * key1 * (1.15 + flashKey * 1.5);
    col += glow2 * key2 * (0.65 + flashKey * 1.2);

    // Subtle magenta lift in shadows, cyan gain in highlights. Monotonic, so
    // contrast doesn't crush.
    vec3 lift = vec3(0.06, 0.00, 0.10);
    vec3 gain = vec3(0.05, 0.20, 0.25);
    col = col * (1.0 + gain) + lift * (1.0 - smoothstep(0.0, 0.6, col));

    // Animated scanlines.
    float scanY = playUV.y * 600.0 + uTime * 30.0;
    float scan = 0.92 + 0.08 * sin(scanY);
    col *= scan;

    // Square-distance vignette.
    float vig = 1.0 - dot(centered, centered) * 0.22;
    col *= vig;

    // Win flash side-colour tint. Sits on top of the graded colour so the cyan
    // (P1) or magenta (P2) read is unambiguous despite the existing grade.
    col = mix(col, col + uWinFlash.rgb, flashKey * 0.45);

    // Hash-based grain so flat areas don't band.
    float grain = fract(sin(dot(uv.xy + uTime, vec2(12.9898, 78.233))) * 43758.5453);
    col += (grain - 0.5) * 0.025;

    fragColor = vec4(col, 1.0);
}

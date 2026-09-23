#version 450

// Ground-Truth Ambient Occlusion (Jimenez et al. 2016) — a horizon search over the
// depth buffer that answers, per pixel, *how much of the sky this point can actually
// see*, and *which way the opening faces*.
//
// This supplies ambient visibility independently of directional sun visibility.
//
//   out .rgb  bent normal, WORLD space: the average unoccluded direction. The IBL
//             diffuse lookup uses this instead of the geometric normal, so a surface in
//             a corner gathers light from the opening rather than from the wall.
//   out .a    visibility in [0,1]. Multiplied into indirect light. Not raised to a
//             power, not scaled by a strength dial — the integral already answers the
//             question, and a knob on top of it would only be a way to disagree with it.
//
// Inputs are depth only because occlusion is geometric. Normal-map detail must not invent
// occluders, so the geometric normal is reconstructed from neighbouring depth.

#include "fullscreen.glsl"

layout(location = 0) in vec2 vUv;
layout(location = 0) out vec4 outAmbient;

layout(set = 0, binding = 0) uniform Gtao {
    mat4  uInvProjection;   // clip -> view
    mat4  uInvView;         // view -> world (for the bent normal)
    vec4  uTarget;          // xy = size in pixels, zw = 1/size
    // x = world-space radius of the search, y = projection scale (pixels per view-space unit at
    // unit depth), z = debug channel (0 off), w unused.
    vec4  uParams;
    // xy = view-ray scale: multiply NDC by this and append -1 to get the direction whose product
    // with LINEAR depth is the view-space position. y carries the sign of Vulkan's Y-flip, so the
    // reconstruction cannot forget it the way the slice basis once did. z = the distance at or
    // beyond which a texel is background. w unused.
    vec4  uRay;
    // x = history weight 0..1, y = this frame's slice rotation offset, z = show rejection.
    vec4  uTemporal;
    mat4  uPrevViewProj;
} g;

// The Hi-Z pyramid, six levels, each carrying min/max LINEAR view depth. GTAO reads .r — the
// NEAREST surface in a region is its strongest occluder, and over-stating occlusion is the safe
// direction for a visibility term.
//
// Six named lookups behind a branch rather than a computed index, for the same portability reason
// shadow.glsl gives for its three cascades: indexing a sampler array with a runtime value needs
// shaderSampledImageArrayDynamicIndexing, which is not guaranteed.
#define HIZ_LEVELS 6
#define uRayXY() (g.uRay.xy)
layout(set = 0, binding = 1) uniform sampler2D uHiZ[HIZ_LEVELS];
// The denoised target still contains the previous frame when this earlier pass reads it, so GTAO
// history needs no second target or parity swap.
layout(set = 0, binding = 2) uniform sampler2D uHistory;

#define PI     3.14159265359
#define HALF_PI 1.57079632679

// Two hemisphere slices with four radial steps. Spatial denoise and temporal accumulation carry
// smoothness; steps retain horizon reach while neighbouring rotated slices provide azimuthal
// coverage. These stay compile-time constants because dynamic loop bounds added 3.4 ms by blocking
// unrolling on the measured backend.
const int SLICES = 2;
const int STEPS  = 4;

// Screen-space safety limit for the world-space radius. Hi-Z moves wider steps to coarser levels,
// allowing a 256-pixel cap without scattered full-resolution fetches.
#define MAX_RADIUS_PIXELS 256.0

// Nearest linear view depth at a uv, from a chosen pyramid level.
float hiZDepth(int level, vec2 uv) {
    if (level <= 0) return textureLod(uHiZ[0], uv, 0.0).r;
    if (level == 1) return textureLod(uHiZ[1], uv, 0.0).r;
    if (level == 2) return textureLod(uHiZ[2], uv, 0.0).r;
    if (level == 3) return textureLod(uHiZ[3], uv, 0.0).r;
    if (level == 4) return textureLod(uHiZ[4], uv, 0.0).r;
    return textureLod(uHiZ[HIZ_LEVELS - 1], uv, 0.0).r;
}

// View-space position, as a ray times a length.
//
// Hi-Z already stores linear depth, so position is the interpolated view ray times a length. uRay.xy
// includes the projection's Y flip.
vec3 viewPositionAt(vec2 uv, int level) {
    float z = hiZDepth(level, uv);
    return vec3((uv * 2.0 - 1.0) * uRayXY(), -1.0) * z;
}

// The geometric normal, from the depth of the four neighbours.
//
// Use central differences on smooth depth and the nearer one-sided difference at an edge. Central
// differences avoid grazing-surface bias; one-sided differences avoid normals spanning silhouettes.
vec3 reconstructNormal(vec2 uv, vec3 P) {
    vec2 texel = g.uTarget.zw;
    vec3 right = viewPositionAt(uv + vec2(texel.x, 0.0), 0) - P;
    vec3 left  = P - viewPositionAt(uv - vec2(texel.x, 0.0), 0);
    vec3 down  = viewPositionAt(uv + vec2(0.0, texel.y), 0) - P;
    vec3 up    = P - viewPositionAt(uv - vec2(0.0, texel.y), 0);

    // Relative, because a 1 cm depth step is an edge at 1 m and noise at 100 m.
    float tolerance = 0.02 * abs(P.z);
    vec3 dx = abs(right.z - left.z) < tolerance
        ? (right + left) * 0.5
        : (abs(right.z) < abs(left.z) ? right : left);
    vec3 dy = abs(down.z - up.z) < tolerance
        ? (down + up) * 0.5
        : (abs(down.z) < abs(up.z) ? down : up);

    vec3 N = normalize(cross(dx, dy));
    // Orient toward the eye. Which way the cross product points depends on the handedness
    // of the projection; which way the surface faces does not.
    return dot(N, -P) < 0.0 ? -N : N;
}

void main() {
    // The linear-depth pyramid reports the far-plane distance where the pre-pass drew nothing.
    if (hiZDepth(0, vUv) >= g.uRay.z) {
        outAmbient = vec4(normalize((g.uInvView * vec4(0.0, 0.0, 1.0, 0.0)).xyz), 1.0);
        return;
    }

    vec3 P = viewPositionAt(vUv, 0);
    vec3 N = reconstructNormal(vUv, P);
    vec3 V = normalize(-P);

    float worldRadius = g.uParams.x;
    // The search radius in pixels: a fixed world radius subtends fewer pixels the further
    // away it is, which is what keeps the occlusion the same size on the wall whether you
    // are standing at it or across the atrium. Clamped so a surface at the near plane does
    // not turn into a full-screen gather.
    float radiusPixels = min(worldRadius * g.uParams.y / max(-P.z, 1e-3), MAX_RADIUS_PIXELS);
    if (radiusPixels < 1.0) {
        outAmbient = vec4(normalize((g.uInvView * vec4(N, 0.0)).xyz), 1.0);
        return;
    }

    // Interleaved gradient noise, the same rotation shadow.glsl uses and for the same
    // reason: eight slices at a shared angle lie down as eight visible bands, and turning
    // the set by a different angle at every pixel converts that structure into noise the
    // eye reads as grain rather than as geometry.
    //
    // Add a frame rotation so temporal accumulation combines independent estimates rather than
    // repeatedly averaging the same azimuths.
    float ign = fract(52.9829189 * fract(dot(gl_FragCoord.xy, vec2(0.06711056, 0.00583715))));
    float sliceRotation = ign * PI + g.uTemporal.y;
    // Use an independent R2 low-discrepancy sequence for radial offsets; deriving this from IGN
    // would correlate slice rotation and step placement into visible stipple.
    float stepOffset = fract(dot(gl_FragCoord.xy, vec2(0.75487766624669276, 0.56984029099805327)));

    float visibility = 0.0;
    float visibilitySq = 0.0;   // for the per-pixel spread the history is clamped against
    vec3  bentNormal = vec3(0.0);
    float projectedLengthSum = 0.0;
    float arcSum = 0.0;

    for (int s = 0; s < SLICES; ++s) {
        float phi = (float(s) / float(SLICES)) * PI + sliceRotation;
        vec2 direction = vec2(cos(phi), sin(phi));

        // The slice plane contains V and this screen direction. View-space X/Y align with screen
        // X/Y up to the projection scale, so the screen direction lifts to view space as
        // (direction.x, -direction.y, 0) — the standard approximation, and it is exact for the axis
        // the horizon angles are measured in.
        //
        // Vulkan clip Y is down, so screen-down maps to negative view-space Y.
        vec3 sliceDir = vec3(direction.x, -direction.y, 0.0);
        vec3 axis = normalize(cross(sliceDir, V));
        vec3 projectedN = N - axis * dot(N, axis);
        float projectedLength = length(projectedN);
        if (projectedLength < 1e-4) continue;

        // In-plane basis: V, and the perpendicular the horizon angles open toward.
        vec3 tangent = cross(V, axis);
        float cosN = clamp(dot(projectedN, V) / projectedLength, -1.0, 1.0);
        float n = sign(dot(projectedN, tangent)) * acos(cosN);

        // March both ways from the pixel, keeping the highest horizon found.
        float cosHorizon1 = -1.0;   // toward -direction
        float cosHorizon2 = -1.0;   // toward +direction
        for (int t = 0; t < STEPS; ++t) {
            // Squared spacing: samples bunch near the pixel, where contact lives and where
            // a linear march wastes most of its taps on empty space.
            float fraction = (float(t) + stepOffset) / float(STEPS);
            // Keep each step at least one texel beyond the preceding sample; sub-texel deltas
            // normalise noise into a full-strength false horizon.
            float stepPixels = max(fraction * fraction * radiusPixels, float(t) + 1.0);
            vec2 offset = direction * stepPixels * g.uTarget.zw;

            // The level whose texels are about as wide as this step is long. A step of one texel
            // reads level 0; every doubling of the stride moves one level coarser, so the number of
            // texels a search touches stays roughly constant however wide it gets.
            int level = clamp(int(floor(log2(max(stepPixels, 1.0)))), 0, HIZ_LEVELS - 1);

            vec3 s1 = viewPositionAt(vUv - offset, level) - P;
            vec3 s2 = viewPositionAt(vUv + offset, level) - P;

            float d1 = length(s1);
            float d2 = length(s2);
            // Fade a horizon out as it approaches the radius, so a wall entering the search
            // does not switch occlusion on across a hard line as the camera moves.
            float w1 = clamp(1.0 - (d1 / worldRadius), 0.0, 1.0);
            float w2 = clamp(1.0 - (d2 / worldRadius), 0.0, 1.0);

            if (d1 > 1e-5) cosHorizon1 = max(cosHorizon1, mix(-1.0, dot(s1 / d1, V), w1));
            if (d2 > 1e-5) cosHorizon2 = max(cosHorizon2, mix(-1.0, dot(s2 / d2, V), w2));
        }

        // Horizons as angles either side of V, clamped to the hemisphere around the
        // projected normal — beyond that is behind the surface and occludes nothing.
        float h1 = n + max(-acos(clamp(cosHorizon1, -1.0, 1.0)) - n, -HALF_PI);
        float h2 = n + min( acos(clamp(cosHorizon2, -1.0, 1.0)) - n,  HALF_PI);

        // The closed-form cosine-weighted arc integral. This is what makes it
        // ground-truth rather than a heuristic: it is the visibility integral over the
        // slice, solved, not a falloff curve fitted until it looked like shade.
        float arc = 0.25 * (-cos(2.0 * h1 - n) + cos(n) + 2.0 * h1 * sin(n))
                  + 0.25 * (-cos(2.0 * h2 - n) + cos(n) + 2.0 * h2 * sin(n));
        float sliceVisibility = projectedLength * arc;
        visibility += sliceVisibility;
        visibilitySq += sliceVisibility * sliceVisibility;
        projectedLengthSum += projectedLength;
        arcSum += projectedLength * arc;

        // The bisector of the unoccluded arc is where this slice's light comes from.
        float bent = (h1 + h2) * 0.5;
        bentNormal += (V * cos(bent) + tangent * sin(bent)) * projectedLength;
    }

    // Slice variance bounds acceptable history before the spatial neighbourhood exists.
    float sliceMean = visibility / float(SLICES);
    float sliceVar = max(visibilitySq / float(SLICES) - sliceMean * sliceMean, 0.0);
    float sliceSd = sqrt(sliceVar);

    visibility = clamp(visibility / float(SLICES), 0.0, 1.0);

    // --- temporal ------------------------------------------------------------
    float refused = 1.0;
    if (g.uTemporal.x > 0.0) {
        vec3 worldPos = (g.uInvView * vec4(P, 1.0)).xyz;
        vec4 clipPrev = g.uPrevViewProj * vec4(worldPos, 1.0);
        if (clipPrev.w > 1e-4) {
            vec2 uvPrev = (clipPrev.xy / clipPrev.w) * 0.5 + 0.5;
            if (all(greaterThanEqual(uvPrev, vec2(0.0))) && all(lessThanEqual(uvPrev, vec2(1.0)))) {
                float history = texture(uHistory, uvPrev).a;
                // Refused outright off-screen, and bounded by this pixel's own spread everywhere
                // else — so a disocclusion, where history disagrees by far more than the estimator's
                // noise, is pulled back to something this frame would have accepted rather than
                // blended in whole. The present frame keeps the answer; history only makes it quiet.
                float tol = max(sliceSd * 2.0, 0.02);
                float bounded = clamp(history, visibility - tol, visibility + tol);
                refused = abs(bounded - history) > 1e-4 ? 1.0 : 0.0;
                visibility = mix(visibility, bounded, clamp(g.uTemporal.x, 0.0, 1.0));
            }
        }
    }

    // If every slice was degenerate the sum is zero and normalize() would produce NaN,
    // which spreads through the IBL lookup and paints black pixels that no amount of
    // staring at the AO buffer explains.
    vec3 bentView = length(bentNormal) > 1e-5 ? normalize(bentNormal) : N;
    vec3 bentWorld = normalize((g.uInvView * vec4(bentView, 0.0)).xyz);

    outAmbient = vec4(bentWorld, visibility);
    // Bright where history was refused or clamped back — the disocclusions and the screen edge.
    if (g.uTemporal.z > 0.5) outAmbient = vec4(vec3(refused), visibility);

    // Debug channels, written into rgb so the capture's bent-normal PNG carries them. The point is
    // to see the INPUTS: once a surface is heavily occluded the bent normal is the bisector of
    // whatever arc survived, so reading the reconstructed normal off it is circular.
    //   1 = reconstructed geometric normal, world space
    //   2 = mean |projected normal| per slice  (collapses -> visibility collapses)
    //   3 = mean unoccluded-arc fraction
    if (g.uParams.z > 0.5) {
        if (g.uParams.z < 1.5) {
            outAmbient = vec4(normalize((g.uInvView * vec4(N, 0.0)).xyz) * 0.5 + 0.5, visibility);
        } else if (g.uParams.z < 2.5) {
            outAmbient = vec4(vec3(projectedLengthSum / float(SLICES)), visibility);
        } else {
            outAmbient = vec4(vec3(arcSum / max(projectedLengthSum, 1e-4)), visibility);
        }
    }
}

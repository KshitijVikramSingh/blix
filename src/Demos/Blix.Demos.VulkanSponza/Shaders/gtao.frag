#version 450

// Ground-Truth Ambient Occlusion (Jimenez et al. 2016) — a horizon search over the
// depth buffer that answers, per pixel, *how much of the sky this point can actually
// see*, and *which way the opening faces*.
//
// <b>This exists because the lighting model has no ambient visibility term at all.</b>
// lit.frag used to fake one as `0.60 + 0.40 * sunShadow`, which is wrong in kind: sun
// visibility is not ambient visibility. A crevice facing away from the sun but open to
// the sky was darkened; one in full sun but sealed shut was not. Deleting it left the
// honest baseline — flat, bright shadowed areas — and this is the term that was missing.
//
//   out .rgb  bent normal, WORLD space: the average unoccluded direction. The IBL
//             diffuse lookup uses this instead of the geometric normal, so a surface in
//             a corner gathers light from the opening rather than from the wall.
//   out .a    visibility in [0,1]. Multiplied into indirect light. Not raised to a
//             power, not scaled by a strength dial — the integral already answers the
//             question, and a knob on top of it would only be a way to disagree with it.
//
// <b>Inputs are depth ONLY, and that is deliberate rather than a shortcut.</b> Occlusion
// is a question about SPACE. Feeding it the normal-mapped normal makes the horizon search
// answer a question about a texture instead — a flat wall with a brick normal map would
// grow occlusion in mortar lines that occlude nothing. The normal is reconstructed from
// the depth of the neighbours, which is the geometry, which is what casts.

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

#define PI     3.14159265359
#define HALF_PI 1.57079632679

// Slices of the hemisphere, and steps along each — 24 taps, down from 8x8 = 64.
//
// <b>Sized against the denoise, not on its own.</b> Eight-by-eight was chosen to be stable per
// pixel without a temporal accumulator, which is the right instinct and the wrong place to spend
// it: the bilateral pass downstream already integrates nine independent neighbours, so each pixel
// only has to be unbiased, not quiet. Measured at 10.86 ms/frame interleaved — the single largest
// shading term in the renderer, larger than the sun shadows — for a near-field correction.
//
// Slices cut harder than steps because azimuthal error is what the spatial denoise fixes best:
// neighbouring pixels rotate their slice sets differently, so nine neighbours already sample many
// more than four directions between them. Steps are the radial resolution of a single horizon, and
// no amount of neighbour-averaging recovers a horizon that was never found.
const int SLICES = 4;
const int STEPS  = 6;

// <b>A bandwidth limit, not a look control.</b> The world radius decides how far occlusion reaches;
// this decides how far the SEARCH is allowed to wander in screen space before the cost stops being
// worth it. At Retina resolution 0.8 m subtends hundreds of pixels on near geometry, and 128
// dependent depth fetches scattered over a disc that size miss cache on nearly every tap — measured
// at 175 ms/frame against a 50-66 ms baseline. Capping it costs near-field AO on surfaces right
// against the camera and nothing anywhere else.
//
// The principled fix is a depth mip chain, sampling a coarser level as the step radius grows, so a
// wide search costs the same as a narrow one. That is the Hi-Z pyramid, and it is the next pillar
// rather than a detail to smuggle in here.
// <b>Raised from 48 now that the pyramid pays for width.</b> The cap existed because a wide search
// meant more scattered fetches into one full-resolution image — measured at 175 ms/frame before it
// went in. Sampling a coarser level as the step grows makes a distant tap cost the same as a near
// one, so the limit can go back to being about what occlusion MEANS rather than what it costs.
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
// <b>No matrix, and no depth buffer.</b> This was an inverse-projection multiply per tap against a
// nonlinear depth buffer — a matrix product to undo a division, done 128 times a pixel. The pyramid
// already holds LINEAR depth, so a position is the pixel's view ray scaled by it: two multiplies.
// The ray is linear in NDC for a perspective projection, so uRay.xy carries it whole, Y-flip
// included.
vec3 viewPositionAt(vec2 uv, int level) {
    float z = hiZDepth(level, uv);
    return vec3((uv * 2.0 - 1.0) * uRayXY(), -1.0) * z;
}

// The geometric normal, from the depth of the four neighbours.
//
// <b>Central differences where the surface is smooth, one-sided only at a depth edge.</b> The
// one-sided version alone — take whichever neighbour is nearer in depth — is the standard remedy
// for silhouettes, where a fixed pair straddles the edge and invents a normal halfway between the
// foreground and whatever is behind it, producing a dark fringe around every object.
//
// But it is BIASED on a smooth surface seen at a grazing angle, and a floor is exactly that. Depth
// along the screen is strongly curved there, so a one-sided difference leans toward the view by
// tens of degrees. That is not cosmetic: the horizon search clamps its answer to the hemisphere of
// this normal, and a normal leaning toward the eye withdraws the clamp precisely where the grazing
// geometry needs it. The measured result was a floor at visibility ~0 — not contact darkening, an
// open, sky-facing floor reading as fully enclosed.
//
// Second difference tells which case this is. |right.z - left.z| is the curvature of depth across
// the pixel: small means the three samples are collinear and a central difference is exact, large
// means an edge runs through them and the nearer side is the honest answer.
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
    // Sky: the pre-pass clears depth to 1 and does not draw the skybox, so an untouched texel
    // is background. Tested on the raw depth rather than on a reconstructed distance — the
    // cleared value is exact, and a distance threshold near a 200 m far plane is not.
    // Background: the pyramid reports the far plane where nothing was drawn. Tested as a distance
    // now rather than against a raw depth of exactly 1, because the value here is metres.
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
    // eye reads as grain rather than as geometry. Spatial only — no frame counter — so
    // the image is the same whether or not there was a frame before it.
    float ign = fract(52.9829189 * fract(dot(gl_FragCoord.xy, vec2(0.06711056, 0.00583715))));
    float sliceRotation = ign * PI;          // slices span pi; each covers both directions
    // <b>A different sequence, not a rescaling of the first.</b> The step offset was fract(ign*7),
    // which is a function of the rotation — so two pixels that turned their slices alike also
    // marched alike, and the pair of them agreed on an answer that a third pixel disagreed with in
    // the same way. That is what put a stipple on flat walls. R2 (Roberts' low-discrepancy
    // sequence) is independent of the IGN above and spreads evenly over the plane.
    float stepOffset = fract(dot(gl_FragCoord.xy, vec2(0.75487766624669276, 0.56984029099805327)));

    float visibility = 0.0;
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
        // <b>NEGATED Y, and leaving it out cost the floor entirely.</b> Vulkan's clip space is
        // Y-down and this projection bakes that flip in, so increasing uv.y — marching DOWN the
        // screen — is view-space MINUS Y. Lifting the screen direction as (direction, 0) therefore
        // builds a tangent pointing opposite to the direction actually being marched, and h1/h2 are
        // assigned to the wrong sides of the view vector.
        //
        // The error scales with how much of a surface's normal lies along view-space Y. A wall has
        // almost none and looked perfect; the floor is entirely Y and came back uniformly occluded,
        // a flat black plane with a hard edge at the wall bases. It was also indifferent to the
        // search radius, because an inverted axis is an angular mistake and no distance fixes it.
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
            // <b>At least one texel further out each step.</b> Squared spacing bunches the early
            // steps near the pixel, and when radiusPixels is small — which is what distance does to
            // it — the first few land INSIDE the centre texel. That delta is a near-zero vector
            // whose normalised direction is noise, and the distance falloff hands it full weight
            // precisely because it is close. Every one of those is a horizon at a random angle,
            // sampled at maximum strength, and it is why occlusion grew with distance while being
            // almost indifferent to the search radius.
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
        visibility += projectedLength * arc;
        projectedLengthSum += projectedLength;
        arcSum += projectedLength * arc;

        // The bisector of the unoccluded arc is where this slice's light comes from.
        float bent = (h1 + h2) * 0.5;
        bentNormal += (V * cos(bent) + tangent * sin(bent)) * projectedLength;
    }

    visibility = clamp(visibility / float(SLICES), 0.0, 1.0);

    // If every slice was degenerate the sum is zero and normalize() would produce NaN,
    // which spreads through the IBL lookup and paints black pixels that no amount of
    // staring at the AO buffer explains.
    vec3 bentView = length(bentNormal) > 1e-5 ? normalize(bentNormal) : N;
    vec3 bentWorld = normalize((g.uInvView * vec4(bentView, 0.0)).xyz);

    outAmbient = vec4(bentWorld, visibility);

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

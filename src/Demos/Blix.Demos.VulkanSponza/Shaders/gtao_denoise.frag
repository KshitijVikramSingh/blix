#version 450

// Edge-aware spatial denoise AND upsample for the ambient-visibility buffer.
//
// The raw buffer is half the framebuffer; this pass runs at full resolution and reconstructs from
// it. Both jobs are the same operation — gather neighbours, keep the ones on this surface — so they
// are one pass rather than two, and a joint bilateral upsample is strictly better than blurring at
// half res and then stretching the result across the silhouettes it just smeared.
//
// The taps step across the HALF-res grid (uTarget is the AO buffer's size) while the depth guiding
// them is read at FULL res, which is what makes this an upsample rather than a blur: each output
// pixel asks its own depth which of the coarse neighbours belong to it.
//
// <b>This is not optional polish; an 8-slice estimate is noisy by construction.</b> GTAO samples a
// few directions per pixel and rotates them per pixel, so on a surface whose true occlusion is
// smooth, neighbouring pixels return a few different discrete answers. Unfiltered, that reads as a
// stipple on flat walls and as cell-shading on anything softly curved — a curtain fold steps
// between values instead of sweeping through them.
//
// <b>Spatial, and that distinction is the house rule rather than a detail.</b> The usual fix is to
// accumulate over frames, which makes the picture depend on the ones before it — the thing this
// renderer refuses. Averaging a NEIGHBOURHOOD costs the same information-theoretically (16 nearby
// estimates of the same quantity) and costs nothing in history: throw every previous frame away and
// this one is still correct.
//
// Edge-aware, because the one thing a blur must not do is average across a silhouette. Two pixels
// belong to the same surface when their depths agree RELATIVE to how far away they are — a 5 cm
// step is a different surface at 1 m and the same surface at 50 m — so the test is on relative
// depth, not absolute.

#include "fullscreen.glsl"

layout(location = 0) in vec2 vUv;
layout(location = 0) out vec4 outAmbient;

layout(set = 0, binding = 0) uniform Denoise {
    mat4 uInvProjection;
    vec4 uTarget;      // xy = size in pixels, zw = 1/size
} d;

layout(set = 0, binding = 1) uniform sampler2D uAmbientRaw;
layout(set = 0, binding = 2) uniform sampler2D uSceneDepth;

// View-space depth (positive, metres) — the scale the tolerance below is relative to.
float viewDepth(vec2 uv) {
    float raw = texture(uSceneDepth, uv).r;
    vec4 view = d.uInvProjection * vec4(uv * 2.0 - 1.0, raw, 1.0);
    return -(view.z / view.w);
}

void main() {
    vec4  centre = texture(uAmbientRaw, vUv);
    float centreDepth = viewDepth(vUv);

    // 3x3. The kernel is deliberately small: the noise it removes is per-pixel, so nine independent
    // neighbours already integrate most of it, and a wide blur would start erasing the contact
    // darkening that is the entire point of the term. 5x5 was measurably not worth its cost.
    vec4  sum = vec4(0.0);
    float weightSum = 0.0;

    for (int y = -1; y <= 1; ++y) {
        for (int x = -1; x <= 1; ++x) {
            vec2 at = vUv + vec2(float(x), float(y)) * d.uTarget.zw;
            float sampleDepth = viewDepth(at);

            // <b>A smooth weight, because a binary one draws the grid it samples on.</b> This was
            // `abs(dz) < tolerance ? 1 : 0`, and with the source at half resolution that admits a
            // different set of coarse taps for each full-res pixel — switching on the half-res grid,
            // which is a checkerboard painted over every flat surface. Nothing about the surface
            // changes at those boundaries; only the kernel did.
            //
            // Relative depth, with a floor so near-camera geometry does not get an absurdly tight
            // tolerance: a 5 cm step is a different surface at 1 m and the same one at 50 m.
            float tolerance = max(0.02 * centreDepth, 0.01);
            float dz = (sampleDepth - centreDepth) / tolerance;
            float depthWeight = exp2(-dz * dz);
            // Spatial falloff as well, so the kernel has no hard edge of its own.
            //
            // <b>0.5, and tightening it does not buy detail.</b> Swept to 1.5 and 4.0 chasing curtain
            // folds: high-frequency content on the cloth rose 0.00800 -> 0.01693, but it rose on flat
            // stone by the same factor, leaving the signal-to-noise ratio flat at 1.44 / 1.39 / 1.45.
            // Sharpening a filter amplifies what is under it; it does not separate structure from
            // noise. Full-resolution AO is worse by the same measure (1.11) for four times the cost.
            float spatialWeight = exp2(-0.5 * float(x * x + y * y));
            float weight = depthWeight * spatialWeight;

            sum += texture(uAmbientRaw, at) * weight;
            weightSum += weight;
        }
    }

    // weightSum is at least 1 — the centre tap always matches itself — so this cannot divide by
    // zero, and a pixel whose every neighbour is a different surface simply keeps its own answer.
    outAmbient = weightSum > 0.0 ? sum / weightSum : centre;
}

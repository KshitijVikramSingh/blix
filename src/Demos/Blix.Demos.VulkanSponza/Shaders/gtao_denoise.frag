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
// The low-tap GTAO estimate is noisy by construction. This spatial pass supplies a stable current
// frame before temporal accumulation adds longer-term convergence.
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

// ── A lever this pass does not yet pull ──────────────────────────────────────
// The kernel is a fixed 3x3 with a depth-similarity weight, and that is exactly wrong for
// alpha-cutout foliage. A canopy fills the depth buffer with thousands of tiny disconnected
// silhouettes, so nearly every neighbour fails the depth test and those pixels keep their RAW
// per-pixel noise — measured at full resolution, canopy speckle rose 74% while stone was unchanged,
// which is the "flowing leaf edges" a viewer sees in motion. Half resolution hides it by averaging
// leaves away below the texel, which is why half res is the default.
//
// The signal to fix it is already built and already bound: the Hi-Z pyramid carries max MINUS min
// per region, which is depth COMPLEXITY — large in a canopy, small on cloth, stone and floor. A
// kernel that widened where complexity is high would gather more neighbours precisely where it is
// currently starving, without bleeding across the clean silhouettes where it currently behaves.
//
// Not built, because half resolution makes the problem moot and the term is not the frame's cost.
// It becomes worth building when ambient visibility runs at full resolution — and that is the
// checkable condition: full-res AO whose canopy speckle stays within ~10% of the half-res figure.
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

            // Smooth relative-depth weighting avoids exposing the half-resolution sampling grid.
            // The floor prevents an impractically tight tolerance near the camera.
            float tolerance = max(0.02 * centreDepth, 0.01);
            float dz = (sampleDepth - centreDepth) / tolerance;
            float depthWeight = exp2(-dz * dz);
            // Spatial falloff as well, so the kernel has no hard edge of its own.
            //
            // The 0.5 falloff retained the best measured signal-to-noise ratio; tightening amplified
            // cloth detail and stone noise together.
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

// Screen probes (--gi-screen-probes): what screen_probe.comp writes and incident_clipmap.frag reads.
//
// One probe per 16x16 pixel tile, placed on the visible surface. Its rays run full length through the ray scene: a
// hit brings that surface's light back (its sun and the probe clipmap's irradiance there, in its albedo), an escape
// the sky. Why full length: the surface reference measured the clipmap's own answer 2.5x too bright in a quarter of
// Sponza's pixels (concavities darker than the air beside them), and a gather whose short rays read the field where
// they stopped kept the error, because irradiance standing in for the light along one ray is 1.9x too bright there
// even when true. Rays that run to a surface leave the field to answer only for light that surface receives.
//
// SCREEN PROBES STORE RADIANCE; WORLD PROBES (the clipmap) STORE IRRADIANCE. That split is why this works: the
// clipmap answers only for light arriving at a surface a ray hit, which irradiance describes; the light arriving
// along each of this probe's rays stays radiance until the very end, when a pixel convolves it with its own
// normal. Keep it one-way: nothing here feeds the clipmap, and nothing reads a screen probe as irradiance.
//
// A probe holds its incoming radiance projected to second-order spherical harmonics, accumulated over frames;
// irradiance for a normal near the probe's follows from the cosine lobe's convolution (Ramamoorthi and Hanrahan),
// within a few percent for irradiance whatever the radiance. Directions below its horizon are never sampled.
//
// Tiles are a budget, not the representation: a tile's header names its probes in a pool (first, count), so a
// tile can later hold two (a depth discontinuity), split, or none, without the readers changing. Today every
// tile holds at most one, at pool index = tile index.
#ifndef SCREEN_PROBE_GLSL
#define SCREEN_PROBE_GLSL

#define SCREEN_PROBE_TILE 32
// The most probes a tile header may name; readers loop to this bound.
#define SCREEN_PROBE_MAX_PER_TILE 2

// A tile's header: x first probe in the pool, y how many, z flags (none yet), w unused.
#define ScreenProbeTile uvec4

struct ScreenProbe {
    vec4 position;      // xyz world, w view depth
    vec4 normal;        // xyz the geometric normal it was placed on, w frames accumulated
    uvec4 identity;     // x the surface's SurfaceKey (stage 4e: source primitive x instance), y frames the dynamic
                        // part has accumulated, zw unused
    vec4 radiance[9];   // incoming radiance, rgb per spherical-harmonic coefficient: the STATIC part (stage 4f),
                        // over the directions whose paths cross no moving geometry's reach
    vec4 dynamicRadiance[9];   // the part over directions whose paths do, on a short history of its own; a reader
                               // wants the sum (the filter writes it into radiance, this zero)
};

void screenProbeBasis(vec3 d, out float y[9]) {
    y[0] = 0.282095;
    y[1] = 0.488603 * d.y;
    y[2] = 0.488603 * d.z;
    y[3] = 0.488603 * d.x;
    y[4] = 1.092548 * d.x * d.y;
    y[5] = 1.092548 * d.y * d.z;
    y[6] = 0.315392 * (3.0 * d.z * d.z - 1.0);
    y[7] = 1.092548 * d.x * d.z;
    y[8] = 0.546274 * (d.x * d.x - d.y * d.y);
}

// Irradiance for a normal from radiance coefficients: each band scaled by the clamped cosine's (pi, 2pi/3, pi/4).
vec3 screenProbeIrradiance(vec4 radiance[9], vec3 n) {
    float y[9];
    screenProbeBasis(n, y);
    const float a0 = 3.14159265, a1 = 2.0943951, a2 = 0.785398163;
    vec3 e = a0 * radiance[0].rgb * y[0];
    for (int i = 1; i < 4; ++i) e += a1 * radiance[i].rgb * y[i];
    for (int i = 4; i < 9; ++i) e += a2 * radiance[i].rgb * y[i];
    return max(e, vec3(0.0));
}

#endif

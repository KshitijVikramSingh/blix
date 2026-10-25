// The references' one path tracer: reference_trace.comp (each pixel, the reference view) and reference_points.comp (the
// gate's points) both take their paths from here, so the two cannot drift apart. Every ray goes through ray_query.glsl,
// so a hardware backend beneath it speeds both alike.
//
// A path from a surface point: cosine-sampled bounces, each hit lit by the sun through a shadow ray and re-radiated in
// its albedo; an escape brings the sky. `bounces` counts as the CPU reference (WriteSurfaceReference) has always counted
// it, which the GI gate's history was measured with: the sun at hits 1 to bounces + 1, the sky escaping after up to
// bounces + 1 hits (an earlier copy here stopped one short of both, and read ~4% low on the Cornell box). The sky is
// the includer's uSkyRadiance (the sun-free environment blurred a mip, as the texels' rays take it). Returns the radiance arriving along the first direction: irradiance is
// pi times its mean over paths. Sun bounce, sky bounce and direct sky are all in it -- the CPU reference's three terms.
//
// The diffuse lobe is EON (diffuse_eon.glsl) wherever the surface was baked with a diffuse roughness: a hit's word holds the albedo a bounce keeps (EON's
// hemispherical albedo) and the roughness level it was baked at (hit.roughness), from which the base albedo comes back exactly. Each hit is then shaded
// with the BRDF itself, not an albedo: the sun with f_EON(toSun, toPrevious) cos, and the next direction, cosine-sampled about the normal AT THE HIT
// (so the BRDF can be weighed against the direction actually taken), carries pi f_EON(next, toPrevious) -- the estimator, not an expectation over
// directions that would treat the incoming light as isotropic. At roughness 0 (a Lambert bake) both are the old albedo / pi and albedo, exactly.
//
// The includer declares: ray_query.glsl (included before this) and `samplerCube uSkyRadiance`.
#ifndef SPONZA_REFERENCE_PATH_GLSL
#define SPONZA_REFERENCE_PATH_GLSL

#include "diffuse_eon.glsl"

#ifndef PI
#define PI 3.14159265359
#endif

float referenceRand(inout uint s) { s = blix_pcg(s); return float(s & 0xFFFFFFu) / 16777216.0; }

vec3 referenceCosineAround(vec3 n, float u1, float u2) {
    float r = sqrt(u1);
    float phi = 2.0 * PI * u2;
    vec3 t = normalize(abs(n.y) < 0.999 ? cross(vec3(0.0, 1.0, 0.0), n) : cross(vec3(1.0, 0.0, 0.0), n));
    vec3 b = cross(n, t);
    return normalize(t * (r * cos(phi)) + b * (r * sin(phi)) + n * sqrt(max(0.0, 1.0 - u1)));
}

vec3 referencePath(vec3 origin, vec3 normal, int bounces, vec3 toSun, vec3 sunIrradiance, inout uint seed) {
    vec3 throughput = vec3(1.0);
    vec3 radiance = vec3(0.0);
    vec3 dir = referenceCosineAround(normal, referenceRand(seed), referenceRand(seed));
    for (int bounce = 0; bounce <= bounces + 1; ++bounce) {
        BlixRayHit hit;
        if (!blix_traceClosest(origin, dir, 0.0, uintBitsToFloat(0x7F800000u), seed, hit)) {
            radiance += throughput * textureLod(uSkyRadiance, dir, 1.0).rgb;
            break;
        }
        if (bounce == bounces + 1) break;   // a hit past the last one lit: only its escape would have counted
        vec3 hn = dot(hit.normal, dir) > 0.0 ? -hit.normal : hit.normal;
        vec3 hp = origin + dir * hit.t;
        float ndl = dot(hn, toSun);
        vec3 wo = -dir;
        vec3 base = blix_eonBaseFromHemispherical(hit.albedo, hit.roughness);
        if (ndl > 0.0 && !blix_traceAny(hp + hn * 0.01, toSun, 0.0, uintBitsToFloat(0x7F800000u), blix_pcg(seed ^ 0x5bd1e995u)))
            radiance += throughput * blix_eonBrdf(base, hit.roughness, ndl, dot(hn, wo), dot(toSun, wo)) * sunIrradiance * ndl;
        seed = blix_pcg(seed);
        vec3 next = referenceCosineAround(hn, referenceRand(seed), referenceRand(seed));
        throughput *= PI * blix_eonBrdf(base, hit.roughness, dot(hn, next), dot(hn, wo), dot(next, wo));
        origin = hp + hn * 0.01;
        dir = next;
    }
    return radiance;
}

#endif

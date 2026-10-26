// --rtgi path guiding (SponzaLoop.Rtgi.cs): a small table of where each coarse cell's light comes from. A guide cell is a
// cell two levels up from a point's (rtgi_cache.glsl's levels) and the side its surface faces, learnt by every training
// sample of the points in it (rtgi_train.comp) -- shared over a neighbourhood, it fills fast -- as 32 bins over the
// hemisphere about the side's axis, equal in solid angle (4 rings in the cosine x 8 sectors), each the light that arrives
// through it (rtgi_guide_resolve.comp); the rays' pass draws a bin by its light and a direction uniformly within it
// (rtgi_rays.comp). The includer defines GUIDE_KEYS(i).
//
//   guideKeys[g]            the key (0: empty)
//   guideAccum[33g .. +32]  this frame's light per bin (fixed point, RTGI_SCALE), then the samples counted
//   guideDist[9g .. +8]     the bins' running means (8 vec4s), then (samples held, their sum, -, -)

#define GUIDE_PROBES 8u
#define GUIDE_RINGS 4u
#define GUIDE_SECTORS 8u
#define GUIDE_BINS 32u
#define GUIDE_BIN_SOLID_ANGLE (6.28318530718 / 32.0)

uint guideHashOf(uint x) {
    x ^= x >> 16; x *= 0x7feb352du;
    x ^= x >> 15; x *= 0x846ca68bu;
    x ^= x >> 16;
    return x;
}

// The guide cell's two hashes (slot, key) for cell c at level l, side s, in a table of mask + 1 slots.
uvec2 guideHash(ivec3 c, uint l, uint s, uint mask) {
    uint a = guideHashOf(uint(c.x) * 0x2C1B3C6Du ^ guideHashOf(uint(c.y) * 0x297A2D39u ^ guideHashOf(uint(c.z) * 0x8CB92BA7u ^ (l * 6u + s + 0x51ED270Bu))));
    uint b = guideHashOf(a ^ 0x7F4A7C15u ^ uint(c.z) * 0x9E3779B9u) | 1u;
    return uvec2(a & mask, b);
}

uint guideFind(uvec2 h, uint mask) {
    for (uint k = 0u; k < GUIDE_PROBES; k++) {
        uint g = (h.x + k) & mask;
        if (GUIDE_KEYS(g) == h.y) return g;
    }
    return 0xFFFFFFFFu;
}

// The side's frame: z its outward axis, t and b the two others.
void guideFrame(uint side, out vec3 t, out vec3 b, out vec3 z) {
    uint axis = side / 2u;
    float sgn = (side & 1u) == 0u ? 1.0 : -1.0;
    z = axis == 0u ? vec3(sgn, 0.0, 0.0) : axis == 1u ? vec3(0.0, sgn, 0.0) : vec3(0.0, 0.0, sgn);
    t = axis == 0u ? vec3(0.0, 1.0, 0.0) : vec3(1.0, 0.0, 0.0);
    b = axis == 2u ? vec3(0.0, 1.0, 0.0) : vec3(0.0, 0.0, 1.0);
}

// The bin direction d falls in, -1 below the side's hemisphere.
int guideBin(vec3 d, uint side) {
    vec3 t, b, z;
    guideFrame(side, t, b, z);
    float c = dot(d, z);
    if (c <= 0.0) return -1;
    uint ring = min(uint(c * float(GUIDE_RINGS)), GUIDE_RINGS - 1u);
    float phi = atan(dot(d, b), dot(d, t));
    uint sector = min(uint((phi + 3.14159265359) / 6.28318530718 * float(GUIDE_SECTORS)), GUIDE_SECTORS - 1u);
    return int(ring * GUIDE_SECTORS + sector);
}

// A direction uniform within bin k (u1, u2 in [0, 1)).
vec3 guideSample(uint k, uint side, float u1, float u2) {
    vec3 t, b, z;
    guideFrame(side, t, b, z);
    float c = (float(k / GUIDE_SECTORS) + u1) / float(GUIDE_RINGS);
    float phi = (float(k % GUIDE_SECTORS) + u2) / float(GUIDE_SECTORS) * 6.28318530718 - 3.14159265359;
    float s = sqrt(max(0.0, 1.0 - c * c));
    return normalize(t * (cos(phi) * s) + b * (sin(phi) * s) + z * c);
}

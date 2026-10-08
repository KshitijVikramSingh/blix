// Ray queries, traced in software: the GPU half of Blix.Geometry's RayQueryScene, test for test.
//
// blix_traceClosest and blix_traceAny answer what VK_KHR_ray_query commits (distance, placement, triangle,
// barycentrics of v1 and v2, facing in the mesh's own space), so a hardware backend can stand behind the
// same two functions. The data is RayQueryScene's, packed by RayQueryGpuData into seven storage blocks the
// host binds by name; this file declares them at BLIX_RAY_SET, bindings BLIX_RAY_BINDING to +6:
//
//   BlixRayTopNodes   the hierarchy over entries; a leaf's index is the entry itself
//   BlixRayInstances  per entry (a region in world space, or one instanced placement): world-to-mesh matrix
//                     (column form: local = m * world; identity for a region), its root node, its placement
//                     (0xFFFFFFFF for a region)
//   BlixRayNodes      every distinct hierarchy, end to end; an interior index names the left child of an
//                     adjacent pair, a leaf's the first of its triangles in BlixRayTriangles
//   BlixRayTriangles  in leaf order: the three vertex indices into BlixRayPositions, then the triangle's index
//                     in its mesh (an instance) or its row in BlixRayOwners (a region)
//   BlixRayPositions  every hierarchy's vertex positions, end to end
//   BlixRayOwners     per region triangle: its placement, and its index in that placement's mesh with the high
//                     bit set when the merge swapped v1 and v2 to undo a mirror
//   BlixRaySurfaces   per triangle row: its albedo (sRGB, low three bytes) and how much of it is there (top byte,
//                     255 solid), baked from its material; a partly covered triangle is met by chance
//
// A trace takes a seed: the caller's name for the ray, which with the entry and the triangle decides whether a
// partly covered triangle is met (RayTests.Covered, the same integer hash). Over many rays a leaf card is met in
// proportion to how much of it the material's alpha keeps.
//
// The tests are the CPU's (RayTests): the watertight triangle test without a double fallback, and the slab
// test with Ize's widened exit. Each traversal keeps a stack of BLIX_RAY_STACK entries per level, the depth the
// CPU's 64 also bounds (Test.Graphics BV.1 checks hierarchies stay within it).
#ifndef BLIX_RAY_QUERY_GLSL
#define BLIX_RAY_QUERY_GLSL

#ifndef BLIX_RAY_SET
#define BLIX_RAY_SET 0
#endif
#ifndef BLIX_RAY_BINDING
#define BLIX_RAY_BINDING 0
#endif
#ifndef BLIX_RAY_STACK
#define BLIX_RAY_STACK 64
#endif

// BLIX_RAY_STATS: count what each trace does (nodes popped, placements entered, triangles tested) into
// blix_rayStats, private to the invocation, for an instrument to write out. Off, it costs nothing.
#ifdef BLIX_RAY_STATS
uvec3 blix_rayStats = uvec3(0u);
#define BLIX_RAY_COUNT(i) blix_rayStats[i]++
#else
#define BLIX_RAY_COUNT(i)
#endif

struct BlixBvhNode { vec3 lo; uint index; vec3 hi; uint count; };
struct BlixRayInstance { mat4 worldToLocal; uvec4 info; };   // info.x root node, y placement (0xFFFFFFFF: a region), z first triangle row

layout(std430, set = BLIX_RAY_SET, binding = BLIX_RAY_BINDING + 0) readonly buffer BlixRayTopNodes { BlixBvhNode blix_topNodes[]; };
layout(std430, set = BLIX_RAY_SET, binding = BLIX_RAY_BINDING + 1) readonly buffer BlixRayInstances { BlixRayInstance blix_instances[]; };
layout(std430, set = BLIX_RAY_SET, binding = BLIX_RAY_BINDING + 2) readonly buffer BlixRayNodes { BlixBvhNode blix_nodes[]; };
layout(std430, set = BLIX_RAY_SET, binding = BLIX_RAY_BINDING + 3) readonly buffer BlixRayTriangles { uvec4 blix_triangles[]; };
layout(std430, set = BLIX_RAY_SET, binding = BLIX_RAY_BINDING + 4) readonly buffer BlixRayPositions { vec4 blix_positions[]; };
layout(std430, set = BLIX_RAY_SET, binding = BLIX_RAY_BINDING + 5) readonly buffer BlixRayOwners { uvec2 blix_owners[]; };
layout(std430, set = BLIX_RAY_SET, binding = BLIX_RAY_BINDING + 6) readonly buffer BlixRaySurfaces { uint blix_surfaces[]; };

// RayTests.Pcg and RayTests.Covered.
uint blix_pcg(uint v) {
    uint state = v * 747796405u + 2891336453u;
    uint word = ((state >> ((state >> 28u) + 4u)) ^ state) * 277803737u;
    return (word >> 22u) ^ word;
}

// The coin is keyed on the triangle's identity in the scene (its placement and its index in that placement's mesh),
// so a triangle referenced twice flips one coin: an instance's is its placement and the triangle's own index (the
// triangle record's w); a region's comes from the owner row (w), mirror bit masked. Solid rows never ask.
bool blix_covered(uint row, uint seed, uint entryIndex, uint record) {
    uint coverage = blix_surfaces[row] >> 24u;
    if (coverage == 255u) return true;
    if (coverage == 0u) return false;
    uint placement = blix_instances[entryIndex].info.y;
    uint triangle = record;
    if (placement == 0xFFFFFFFFu) {
        uvec2 owner = blix_owners[record];
        placement = owner.x;
        triangle = owner.y & 0x7FFFFFFFu;
    }
    return (blix_pcg(seed + blix_pcg(placement * 0x9E3779B9u + triangle)) >> 24u) < coverage;
}

struct BlixRayHit {
    float t;
    uint instance;
    uint triangle;
    vec2 barycentrics;
    bool frontFace;
    // World-space geometric normal of the triangle hit, unit length, on its authored front (the side frontFace
    // names): what shading a hit needs and rayQuery leaves to the caller.
    vec3 normal;
    // Linear albedo baked for the triangle hit (its material's texture over the triangle, times its factor).
    vec3 albedo;
};

// RayTests.BoxExitWidening: 1 + 2 * gamma(3), gamma(n) = n * 2^-24 / (1 - n * 2^-24).
const float BLIX_BOX_EXIT_WIDENING = 1.0 + 2.0 * (3.0 * 5.9604645e-8 / (1.0 - 3.0 * 5.9604645e-8));

// ShearedRay: the ray's dominant axis and the shear that aligns the triangle test with it.
struct BlixShearedRay { vec3 origin; vec3 direction; vec3 invDirection; ivec3 k; vec3 s; };

BlixShearedRay blix_shear(vec3 origin, vec3 direction) {
    BlixShearedRay r;
    r.origin = origin;
    r.direction = direction;
    r.invDirection = 1.0 / direction;
    vec3 a = abs(direction);
    int kz = a.x >= a.y ? (a.x >= a.z ? 0 : 2) : (a.y >= a.z ? 1 : 2);
    int kx = (kz + 1) % 3;
    int ky = (kx + 1) % 3;
    if (direction[kz] < 0.0) { int swap = kx; kx = ky; ky = swap; }
    r.k = ivec3(kx, ky, kz);
    r.s = vec3(direction[kx] / direction[kz], direction[ky] / direction[kz], 1.0 / direction[kz]);
    return r;
}

bool blix_rayBox(BlixShearedRay r, BlixBvhNode node, float tMin, float tMax, out float entry) {
    vec3 t0 = (node.lo - r.origin) * r.invDirection;
    vec3 t1 = (node.hi - r.origin) * r.invDirection;
    vec3 near = min(t0, t1);
    vec3 far = max(t0, t1);
    entry = max(max(near.x, near.y), max(near.z, tMin));
    float exit = min(min(far.x, far.y), min(far.z, tMax)) * BLIX_BOX_EXIT_WIDENING;
    return entry <= exit;
}

bool blix_rayTriangle(BlixShearedRay r, vec3 v0, vec3 v1, vec3 v2, float tMin, float tMax,
                      out float t, out vec2 barycentrics, out bool frontFace) {
    t = 0.0;
    barycentrics = vec2(0.0);
    frontFace = false;
    // precise throughout: nothing fused, every product rounded where the CPU rounds it. Fused, the two triangles on
    // an edge compute its function in opposite orders and stop being exact negatives, so a ray through the edge
    // can pass between them (measured: a vertex-aimed ray on the city, --ray-check); and the shear and the rest
    // drift from the CPU oracle's answer on near-edge hits.
    precise vec3 a = v0 - r.origin;
    precise vec3 b = v1 - r.origin;
    precise vec3 c = v2 - r.origin;
    float az = a[r.k.z], bz = b[r.k.z], cz = c[r.k.z];
    precise float ax = a[r.k.x] - r.s.x * az, ay = a[r.k.y] - r.s.y * az;
    precise float bx = b[r.k.x] - r.s.x * bz, by = b[r.k.y] - r.s.y * bz;
    precise float cx = c[r.k.x] - r.s.x * cz, cy = c[r.k.y] - r.s.y * cz;
    precise float u = cx * by - cy * bx;
    precise float v = ax * cy - ay * cx;
    precise float w = bx * ay - by * ax;
    if ((u < 0.0 || v < 0.0 || w < 0.0) && (u > 0.0 || v > 0.0 || w > 0.0)) return false;
    float det = u + v + w;
    if (det == 0.0) return false;
    precise float tScaled = u * r.s.z * az + v * r.s.z * bz + w * r.s.z * cz;
    float inv = 1.0 / det;
    t = tScaled * inv;
    if (!(t > tMin && t < tMax)) return false;
    barycentrics = vec2(v * inv, w * inv);
    frontFace = dot(cross(v1 - v0, v2 - v0), r.direction) < 0.0;
    return true;
}

// One mesh's hierarchy from its root, nearest first, shrinking tMax as hits are found (TriangleBvh.Closest).
bool blix_traceMesh(BlixShearedRay r, uint root, float tMin, inout float tMax, uint seed, uint entryIndex, uint triangleBase,
                    out uint triangle, out vec2 barycentrics, out bool frontFace, out uint row) {
    triangle = 0xFFFFFFFFu;
    row = 0u;
    barycentrics = vec2(0.0);
    frontFace = false;
    float entry;
    if (!blix_rayBox(r, blix_nodes[root], tMin, tMax, entry)) return false;
    uint stack[BLIX_RAY_STACK];
    int top = 0;
    stack[top++] = root;
    while (top > 0) {
        BlixBvhNode node = blix_nodes[stack[--top]];
        BLIX_RAY_COUNT(0);
        if (node.count > 0u) {
            for (uint k = 0u; k < node.count; ++k) {
                BLIX_RAY_COUNT(2);
                uvec4 tri = blix_triangles[node.index + k];
                float th; vec2 bc; bool front;
                if (blix_rayTriangle(r, blix_positions[tri.x].xyz, blix_positions[tri.y].xyz, blix_positions[tri.z].xyz,
                                     tMin, tMax, th, bc, front)
                    && blix_covered(node.index + k, seed, entryIndex, tri.w)) {
                    tMax = th;
                    triangle = tri.w;
                    barycentrics = bc;
                    frontFace = front;
                    row = node.index + k;
                }
            }
            continue;
        }
        float entryL, entryR;
        bool hitL = blix_rayBox(r, blix_nodes[node.index], tMin, tMax, entryL);
        bool hitR = blix_rayBox(r, blix_nodes[node.index + 1u], tMin, tMax, entryR);
        if (hitL && hitR) {
            bool nearIsLeft = entryL <= entryR;
            stack[top++] = nearIsLeft ? node.index + 1u : node.index;
            stack[top++] = nearIsLeft ? node.index : node.index + 1u;
        } else if (hitL) {
            stack[top++] = node.index;
        } else if (hitR) {
            stack[top++] = node.index + 1u;
        }
    }
    return triangle != 0xFFFFFFFFu;
}

bool blix_anyInMesh(BlixShearedRay r, uint root, float tMin, float tMax, uint seed, uint entryIndex, uint triangleBase) {
    uint stack[BLIX_RAY_STACK];
    int top = 0;
    stack[top++] = root;
    while (top > 0) {
        BlixBvhNode node = blix_nodes[stack[--top]];
        float entry;
        if (!blix_rayBox(r, node, tMin, tMax, entry)) continue;
        if (node.count == 0u) {
            stack[top++] = node.index + 1u;
            stack[top++] = node.index;
            continue;
        }
        for (uint k = 0u; k < node.count; ++k) {
            uvec4 tri = blix_triangles[node.index + k];
            float th; vec2 bc; bool front;
            if (blix_rayTriangle(r, blix_positions[tri.x].xyz, blix_positions[tri.y].xyz, blix_positions[tri.z].xyz,
                                 tMin, tMax, th, bc, front)
                && blix_covered(node.index + k, seed, entryIndex, tri.w)) return true;
        }
    }
    return false;
}

// Into a placement's space, rounded as System.Numerics' Vector3.Transform rounds it (x, then y, then z, then the
// translation, nothing fused): far from the origin a fused version lands the ray a rounding step away, which on a
// grazing hit is the difference between two triangles.
BlixShearedRay blix_toInstance(vec3 origin, vec3 direction, uint instance) {
    mat4 m = blix_instances[instance].worldToLocal;
    precise vec3 o = m[0].xyz * origin.x + m[1].xyz * origin.y + m[2].xyz * origin.z + m[3].xyz;
    precise vec3 d = m[0].xyz * direction.x + m[1].xyz * direction.y + m[2].xyz * direction.z;
    return blix_shear(o, d);
}

// The nearest hit in (tMin, tMax) over every placement (RayQueryScene.Closest).
bool blix_traceClosest(vec3 origin, vec3 direction, float tMin, float tMax, uint seed, out BlixRayHit hit) {
    hit.t = tMax;
    hit.instance = 0xFFFFFFFFu;
    hit.triangle = 0xFFFFFFFFu;
    hit.barycentrics = vec2(0.0);
    hit.frontFace = false;
    hit.normal = vec3(0.0);
    hit.albedo = vec3(0.0);
    uint hitRow = 0u;
    BlixShearedRay world = blix_shear(origin, direction);
    uint stack[BLIX_RAY_STACK];
    int top = 0;
    stack[top++] = 0u;
    while (top > 0) {
        BlixBvhNode node = blix_topNodes[stack[--top]];
        BLIX_RAY_COUNT(0);
        float entry;
        if (!blix_rayBox(world, node, tMin, hit.t, entry)) continue;
        if (node.count == 0u) {
            float entryL, entryR;
            bool hitL = blix_rayBox(world, blix_topNodes[node.index], tMin, hit.t, entryL);
            bool hitR = blix_rayBox(world, blix_topNodes[node.index + 1u], tMin, hit.t, entryR);
            if (hitL && hitR) {
                bool nearIsLeft = entryL <= entryR;
                stack[top++] = nearIsLeft ? node.index + 1u : node.index;
                stack[top++] = nearIsLeft ? node.index : node.index + 1u;
            } else if (hitL) {
                stack[top++] = node.index;
            } else if (hitR) {
                stack[top++] = node.index + 1u;
            }
            continue;
        }
        uint instance = node.index;
        BLIX_RAY_COUNT(1);
        uint triangle; vec2 bc; bool front; uint row;
        float t = hit.t;
        if (blix_traceMesh(blix_toInstance(origin, direction, instance), blix_instances[instance].info.x, tMin, t, seed, instance,
                           blix_instances[instance].info.z, triangle, bc, front, row)) {
            hit.t = t;
            hit.instance = instance;   // the entry, until resolved below
            hit.triangle = triangle;
            hit.barycentrics = bc;
            hit.frontFace = front;
            hitRow = row;
        }
    }
    if (hit.instance == 0xFFFFFFFFu) return false;
    // The normal, from the triangle as stored: a region's is in the world already (re-wound if its placement
    // mirrored); an instance's is carried out of mesh space by the inverse transpose of its world matrix, which for
    // the world-to-mesh matrix stored here is its transpose.
    uvec4 tri = blix_triangles[hitRow];
    vec3 p0 = blix_positions[tri.x].xyz;
    vec3 n = cross(blix_positions[tri.y].xyz - p0, blix_positions[tri.z].xyz - p0);
    hit.normal = normalize(transpose(mat3(blix_instances[hit.instance].worldToLocal)) * n);
    // sRGB-encoded albedo bytes back to linear (the bake stores them encoded for precision in the darks).
    vec3 srgb = unpackUnorm4x8(blix_surfaces[hitRow]).rgb;
    hit.albedo = mix(srgb / 12.92, pow((srgb + 0.055) / 1.055, vec3(2.4)), step(vec3(0.04045), srgb));
    // Resolve the entry to the placement: an instance is one; a region's owner row names it (RayQueryScene.Closest).
    uint placement = blix_instances[hit.instance].info.y;
    if (placement != 0xFFFFFFFFu) {
        hit.instance = placement;
    } else {
        uvec2 owner = blix_owners[hit.triangle];
        hit.instance = owner.x;
        hit.triangle = owner.y & 0x7FFFFFFFu;
        if ((owner.y & 0x80000000u) != 0u) hit.barycentrics = hit.barycentrics.yx;
    }
    return true;
}

// Whether anything is hit in (tMin, tMax) (RayQueryScene.Any).
bool blix_traceAny(vec3 origin, vec3 direction, float tMin, float tMax, uint seed) {
    BlixShearedRay world = blix_shear(origin, direction);
    uint stack[BLIX_RAY_STACK];
    int top = 0;
    stack[top++] = 0u;
    while (top > 0) {
        BlixBvhNode node = blix_topNodes[stack[--top]];
        float entry;
        if (!blix_rayBox(world, node, tMin, tMax, entry)) continue;
        if (node.count == 0u) {
            stack[top++] = node.index + 1u;
            stack[top++] = node.index;
            continue;
        }
        uint instance = node.index;
        if (blix_anyInMesh(blix_toInstance(origin, direction, instance), blix_instances[instance].info.x, tMin, tMax, seed, instance,
                           blix_instances[instance].info.z)) return true;
    }
    return false;
}

#endif

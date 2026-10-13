// blix_hwx: Metal hardware ray tracing over a ray scene whose blocks live in Vulkan (MoltenVK) memory.
//
// The caller (Blix.Graphics.MetalHWX) hands over MTLBuffers exported from Vulkan GPU buffers through
// VK_EXT_metal_objects: the packed ray scene's Instances, Triangles, Positions, Owners and Surfaces blocks
// (RayQueryGpuData), and the ray and hit buffers of a trace. This library builds one bottom-level acceleration
// structure per distinct hierarchy straight from Positions, a top-level one with one instance per entry, and traces
// with Metal's intersector, answering in ray_check.comp's record so the CPU oracle can hold it ray for ray.
//
// Coverage (RayTests.Covered): a hierarchy's solid triangles are an opaque geometry; its partly covered ones a
// non-opaque geometry whose intersection function flips ray_query.glsl's coin; uncovered triangles are left out.
//
// Synchronisation: hwx_build and hwx_trace wait for their own GPU work before returning, and the caller makes sure
// Vulkan is idle on the buffers involved before calling. hwx_encode_shadow is the in-frame path: it waits and
// signals on the device's frame timeline (an MTLSharedEvent) on the GPU, and returns without waiting.
#import <Foundation/Foundation.h>
#import <Metal/Metal.h>

#include <atomic>
#include <cstdint>
#include <string>
#include <vector>

#define HWX_EXPORT extern "C" __attribute__((visibility("default")))

// One distinct hierarchy: its primitives in the index/row arrays, solid first, then partly covered.
struct HwxBlas {
    uint32_t primBase;
    uint32_t solidCount;
    uint32_t partialCount;
    uint32_t pad;
};

// One top-level entry: its world matrix as Metal's packed 4x3 (columns: the linear part, then the translation)
// and the hierarchy it places.
struct HwxEntry {
    float transform[12];
    uint32_t blas;
    uint32_t pad[3];
};

struct HwxBuildStats {
    double blasGpuMs;
    double compactTlasGpuMs;
    uint64_t blasBytes;
    uint64_t compactedBytes;
    uint64_t tlasBytes;
    uint64_t scratchBytes;
    uint64_t geometryBytes;  // index + primitive-row buffers this library owns
    uint32_t blasCount;
    uint32_t geometryCount;
};

static_assert(sizeof(HwxBlas) == 16 && sizeof(HwxEntry) == 64, "layouts shared with C#");

static NSString* const kSource = @R"(
#include <metal_stdlib>
using namespace metal;
using namespace metal::raytracing;

struct BlixRayInstance { float4x4 worldToLocal; uint4 info; };   // info.y: placement, 0xFFFFFFFF for a region

// RayTests.Pcg, as ray_query.glsl spells it.
static uint blix_pcg(uint v) {
    uint state = v * 747796405u + 2891336453u;
    uint word = ((state >> ((state >> 28u) + 4u)) ^ state) * 277803737u;
    return (word >> 22u) ^ word;
}

// A Metal primitive back to its packed triangle row: entryRows[entry] = (first primitive's slot, geometry 0's count).
static uint blix_row(device const uint* primRows, device const uint2* entryRows, uint instance, uint geometry, uint primitive) {
    uint2 e = entryRows[instance];
    return primRows[e.x + (geometry != 0u ? e.y : 0u) + primitive];
}

// A material's alpha test, for hwx_set_alpha: its albedo texture, cutoff and the factor its alpha is multiplied by.
struct BlixAlphaMaterial { texture2d<float> albedo; float cutoff; float alphaFactor; };
struct BlixAlphaParams { uint enabled; float mip; uint pad1; uint pad2; };   // mip: the level the alpha is read at

// A partly covered triangle, met or not. With alpha (the table's own switch, hwx_set_alpha), the material's alpha at
// the point hit, as the raster's alpha test decides it: what a leaf card's shadow should be. Otherwise blix_covered:
// met when the ray's coin, keyed on the triangle's identity in the scene (placement, and index in that placement's
// mesh), lands under its coverage, which is what the software traversal and the CPU oracle do.
[[intersection(triangle, triangle_data, instancing)]]
bool blix_coverage(uint primitive [[primitive_id]], uint geometry [[geometry_id]], uint instance [[instance_id]],
                   float2 bary [[barycentric_coord]],
                   ray_data uint& seed [[payload]],
                   device const uint* primRows [[buffer(0)]], device const uint2* entryRows [[buffer(1)]],
                   device const BlixRayInstance* instances [[buffer(2)]], device const uint4* triangles [[buffer(3)]],
                   device const uint2* owners [[buffer(4)]], device const uint* surfaces [[buffer(5)]],
                   device const BlixAlphaParams& alpha [[buffer(6)]], device const uint* rowMaterial [[buffer(7)]],
                   device const BlixAlphaMaterial* materials [[buffer(8)]], device const float2* uvs [[buffer(9)]])
{
    uint row = blix_row(primRows, entryRows, instance, geometry, primitive);
    if (alpha.enabled != 0u) {
        uint m = rowMaterial[row];
        if (m != 0xFFFFFFFFu) {
            uint4 tri = triangles[row];
            float2 uv = uvs[tri.x] * (1.0f - bary.x - bary.y) + uvs[tri.y] * bary.x + uvs[tri.z] * bary.y;
            constexpr sampler s(filter::linear, mip_filter::nearest, address::repeat);
            float a = materials[m].albedo.sample(s, uv, level(alpha.mip)).a * materials[m].alphaFactor;
            return a >= materials[m].cutoff;
        }
    }
    uint coverage = surfaces[row] >> 24u;
    if (coverage == 255u) return true;
    if (coverage == 0u) return false;
    uint placement = instances[instance].info.y;
    uint triangle = triangles[row].w;
    if (placement == 0xFFFFFFFFu) {
        uint2 owner = owners[triangle];
        placement = owner.x;
        triangle = owner.y & 0x7FFFFFFFu;
    }
    return (blix_pcg(seed + blix_pcg(placement * 0x9E3779B9u + triangle)) >> 24u) < coverage;
}

// flags (in-frame batches): 1 the live count is limit[0] (a wavefront's pass wrote it), the threads past it idle.
struct Params { uint count; uint seedBase; uint closestOnly; uint flags; };

// flags: 1 the live count is limit[0], as Params'; 2 each ray's own direction (directions[i].xyz), not `direction`;
// 4 packed: ray i is origins[2i] (origin, tMax) and origins[2i + 1] (direction), its answer written to occluded[pixelOf[i]].
struct ShadowParams { float4 direction; uint count; uint seedBase; uint sky; uint flags; };

// Sun shadows: one ray per origin, every one toward the same direction. An origin is (position, tMax); tMax 0 means
// no ray, answered unoccluded; tMax < 0 no ray, answered OCCLUDED (the caller knew: a baked record, say). Writes 1 where anything, met as coverage (or the alpha test, by the table bound) allows,
// is in the way, 0 where nothing is, as the software batch (ray_batch_shadow.comp) answers.
//
// With p.sky, each ray also has a sky ray (skyRays, two float4s: origin and tMax, tMax 0 for none, sky seen; then its
// direction), and the word gains 2 where it meets anything: "does this point see the sky that way".
kernel void blix_shadow(device const float4* origins [[buffer(0)]],
                        device uint* occluded [[buffer(1)]],
                        instance_acceleration_structure tlas [[buffer(2)]],
                        intersection_function_table<triangle_data, instancing> table [[buffer(3)]],
                        constant ShadowParams& p [[buffer(4)]],
                        device const float4* skyRays [[buffer(5)]],
                        device const uint* limit [[buffer(6)]],
                        device const float4* directions [[buffer(7)]],
                        device const uint* pixelOf [[buffer(8)]],
                        uint i [[thread_position_in_grid]])
{
    if (i >= p.count) return;
    if ((p.flags & 1u) != 0u && i >= limit[0]) return;
    bool packed = (p.flags & 4u) != 0u;
    float4 o = packed ? origins[2u * i] : origins[i];
    uint word = 0u;
    intersector<triangle_data, instancing> isect;
    isect.assume_geometry_type(geometry_type::triangle);
    isect.accept_any_intersection(true);
    if (o.w > 0.0f) {
        uint seed = p.seedBase + i;
        float3 direction = packed ? origins[2u * i + 1u].xyz : (p.flags & 2u) != 0u ? directions[i].xyz : p.direction.xyz;
        auto res = isect.intersect(ray(o.xyz, direction, 0.0f, o.w), tlas, 0xFFu, table, seed);
        // Occluded, and how far along the ray it met something (the upper 16 bits, a half): with per-ray directions the
        // caller sizes a penumbra from it. Any hit, not the nearest -- an estimate, as a penumbra filter needs.
        if (res.type != intersection_type::none)
            word |= 1u | (uint(as_type<ushort>(half(min(res.distance, 60000.0f)))) << 16);
    }
    else if (o.w < 0.0f) word |= 1u;
    if (p.sky != 0u) {
        float4 s = skyRays[i * 2u];
        if (s.w > 0.0f) {
            uint seed = p.seedBase + i + 0x68E31DA4u;
            auto res = isect.intersect(ray(s.xyz, skyRays[i * 2u + 1u].xyz, 0.0f, s.w), tlas, 0xFFu, table, seed);
            if (res.type != intersection_type::none) word |= 2u;
        }
    }
    occluded[packed ? pixelOf[i] : i] = word;
}

// ray_check.comp's contract: each ray two float4s (origin, tMin; direction, tMax), its seed seedBase + its index;
// each answer three uint4s (t, barycentrics, flags: 1 hit, 2 front face, 4 any hit; placement, triangle, 0, 0;
// world normal, linear albedo packed).
kernel void blix_trace(device const float4* rays [[buffer(0)]],
                       device uint4* hits [[buffer(1)]],
                       instance_acceleration_structure tlas [[buffer(2)]],
                       intersection_function_table<triangle_data, instancing> table [[buffer(3)]],
                       constant Params& p [[buffer(4)]],
                       device const uint* primRows [[buffer(5)]],
                       device const uint2* entryRows [[buffer(6)]],
                       device const BlixRayInstance* instances [[buffer(7)]],
                       device const uint4* triangles [[buffer(8)]],
                       device const float4* positions [[buffer(9)]],
                       device const uint2* owners [[buffer(10)]],
                       device const uint* surfaces [[buffer(11)]],
                       device const uint* limit [[buffer(12)]],
                       uint i [[thread_position_in_grid]])
{
    if (i >= p.count) return;
    if ((p.flags & 1u) != 0u && i >= limit[0]) return;
    float4 o = rays[i * 2u];
    float4 d = rays[i * 2u + 1u];
    uint seed = p.seedBase + i;
    ray r(o.xyz, d.xyz, o.w, d.w);

    intersector<triangle_data, instancing> isect;
    isect.assume_geometry_type(geometry_type::triangle);
    auto res = isect.intersect(r, tlas, 0xFFu, table, seed);
    bool found = res.type == intersection_type::triangle;

    float t = d.w;
    float2 bary = float2(0.0f);
    bool front = false;
    uint placement = 0xFFFFFFFFu, triangle = 0xFFFFFFFFu;
    float3 n = float3(0.0f);
    uint albedo = 0u;
    if (found) {
        uint row = blix_row(primRows, entryRows, res.instance_id, res.geometry_id, res.primitive_id);
        t = res.distance;
        bary = res.triangle_barycentric_coord;
        front = res.triangle_front_facing;
        uint4 tri = triangles[row];
        float3 p0 = positions[tri.x].xyz;
        float3 nn = cross(positions[tri.y].xyz - p0, positions[tri.z].xyz - p0);
        float4x4 m = instances[res.instance_id].worldToLocal;
        n = normalize(transpose(float3x3(m[0].xyz, m[1].xyz, m[2].xyz)) * nn);
        float3 srgb = unpack_unorm4x8_to_float(surfaces[row]).rgb;
        float3 lin = select(pow((srgb + 0.055f) / 1.055f, float3(2.4f)), srgb / 12.92f, srgb < float3(0.04045f));
        albedo = pack_float_to_unorm4x8(float4(lin, 0.0f));
        placement = instances[res.instance_id].info.y;
        triangle = tri.w;
        if (placement == 0xFFFFFFFFu) {
            uint2 owner = owners[tri.w];
            placement = owner.x;
            triangle = owner.y & 0x7FFFFFFFu;
            if ((owner.y & 0x80000000u) != 0u) bary = bary.yx;
        }
    }

    bool any = found;
    if (p.closestOnly == 0u) {
        isect.accept_any_intersection(true);
        uint anySeed = seed;
        any = isect.intersect(r, tlas, 0xFFu, table, anySeed).type != intersection_type::none;
    }

    uint flags = (found ? 1u : 0u) | (front ? 2u : 0u) | (any ? 4u : 0u);
    hits[i * 3u] = uint4(as_type<uint>(t), as_type<uint>(bary.x), as_type<uint>(bary.y), flags);
    hits[i * 3u + 1u] = uint4(placement, triangle, 0u, 0u);
    hits[i * 3u + 2u] = uint4(as_type<uint>(n.x), as_type<uint>(n.y), as_type<uint>(n.z), albedo);
}

// The lightmap's buried test (hwx_buried): per record (two float4s: position, normal), `tests` cosine rays from just
// `lift` off it, each as far as `reach`; buried when `threshold` of them first meet a single-sided triangle's back face. A
// double-sided placement's back face (doubleSided[placement] != 0) is the far side of a sheet with no inside, and does
// not count. Also buried when enclosed: three quarters of `enclosedTests` more cosine rays, at any distance, meet such
// a back face first -- a face pressed flat against a solid (a floor under a block, a wall against the next) looks into
// it, and the solid's far faces lie further than `reach`. Not all of them: where that solid touches another, a ray
// reaching the contact meets two coplanar faces, and about half the time the other's front face answers.
struct BuriedParams { uint count; uint tests; uint threshold; uint seed; float reach; float lift; uint enclosedTests; uint contactRows; };

// A ray's first hit for the buried test, with a coplanar contact counted as the back face it also is -- as the demo's
// ClosestThroughContacts, from the same table: where two solids touch, their faces in one plane tie, and the closest
// hit reports either. A front-facing hit is a contact when its point lies inside one of its row's partners (coplanar,
// facing the other way: contactStart[row]..contactStart[row + 1] index contactTris, three float4s each, the first's w
// the partner's placement), within 2 mm of their edges. A row that has partners but none holding the point is re-asked
// along four directions jittered by a millirad, a back face at the same distance winning; a row with none never is.
// Returns 0 nothing, 1 a front face, 2 a back face; `placement` the face's (for the double-sided test).
struct BuriedScene {
    device const uint* primRows; device const uint2* entryRows; device const BlixRayInstance* instances;
    device const uint4* triangles; device const uint2* owners; device const uint* contactStart; device const float4* contactTris;
    uint contactRows;
};

inline uint blix_hit_placement(BuriedScene sc, uint instance, uint geometry, uint primitive, thread uint& row)
{
    row = blix_row(sc.primRows, sc.entryRows, instance, geometry, primitive);
    uint placement = sc.instances[instance].info.y;
    if (placement == 0xFFFFFFFFu) placement = sc.owners[sc.triangles[row].w].x;
    return placement;
}

inline bool blix_inside_triangle(float3 p, float3 a, float3 b, float3 c)
{
    float3 n = cross(b - a, c - a);
    float len = length(n);
    if (len < 1e-12f) return false;
    n /= len;
    if (dot(p - a, normalize(cross(b - a, n))) > 2e-3f) return false;
    if (dot(p - b, normalize(cross(c - b, n))) > 2e-3f) return false;
    if (dot(p - c, normalize(cross(a - c, n))) > 2e-3f) return false;
    return true;
}

inline uint blix_buried_hit(float3 o, float3 d, float tMax, instance_acceleration_structure tlas,
                            intersection_function_table<triangle_data, instancing> table, uint seed,
                            BuriedScene sc, thread uint& placement)
{
    intersector<triangle_data, instancing> isect;
    isect.assume_geometry_type(geometry_type::triangle);
    uint s0 = seed;
    auto res = isect.intersect(ray(o, d, 0.0f, tMax), tlas, 0xFFu, table, s0);
    if (res.type != intersection_type::triangle) return 0u;
    uint row;
    placement = blix_hit_placement(sc, res.instance_id, res.geometry_id, res.primitive_id, row);
    if (!res.triangle_front_facing) return 2u;
    if (row + 1u >= sc.contactRows) return 1u;
    uint first = sc.contactStart[row], last = sc.contactStart[row + 1u];
    if (first == last) return 1u;
    float3 p = o + d * res.distance;
    for (uint k = first; k < last; ++k) {
        float4 a = sc.contactTris[k * 3u];
        if (blix_inside_triangle(p, a.xyz, sc.contactTris[k * 3u + 1u].xyz, sc.contactTris[k * 3u + 2u].xyz)) {
            placement = uint(a.w);
            return 2u;
        }
    }
    float3 t = normalize(abs(d.y) < 0.9f ? cross(d, float3(0.0f, 1.0f, 0.0f)) : cross(d, float3(1.0f, 0.0f, 0.0f)));
    float3 b = cross(d, t);
    float near = 1e-3f + 1e-4f * res.distance;
    for (uint k = 0u; k < 4u; ++k) {
        float angle = float(k) * 1.5707963f;
        float3 j = normalize(d + (t * cos(angle) + b * sin(angle)) * 1e-3f);
        uint s1 = seed;
        auto again = isect.intersect(ray(o, j, 0.0f, res.distance + 2.0f * near), tlas, 0xFFu, table, s1);
        if (again.type == intersection_type::triangle && !again.triangle_front_facing && abs(again.distance - res.distance) <= near) {
            uint againRow;
            placement = blix_hit_placement(sc, again.instance_id, again.geometry_id, again.primitive_id, againRow);
            return 2u;
        }
    }
    return 1u;
}

kernel void blix_buried(device const float4* records [[buffer(0)]],
                        device uchar* buried [[buffer(1)]],
                        instance_acceleration_structure tlas [[buffer(2)]],
                        intersection_function_table<triangle_data, instancing> table [[buffer(3)]],
                        constant BuriedParams& p [[buffer(4)]],
                        device const uchar* doubleSided [[buffer(5)]],
                        device const uint* primRows [[buffer(6)]],
                        device const uint2* entryRows [[buffer(7)]],
                        device const BlixRayInstance* instances [[buffer(8)]],
                        device const uint4* triangles [[buffer(9)]],
                        device const uint2* owners [[buffer(10)]],
                        device const uint* contactStart [[buffer(11)]],
                        device const float4* contactTris [[buffer(12)]],
                        uint i [[thread_position_in_grid]])
{
    if (i >= p.count) return;
    BuriedScene sc = { primRows, entryRows, instances, triangles, owners, contactStart, contactTris, p.contactRows };
    float3 n = records[i * 2u + 1u].xyz;
    if (dot(n, n) < 1e-12f) { buried[i] = 0; return; }
    n = normalize(n);
    float3 o = records[i * 2u].xyz + n * p.lift;
    float3 t = normalize(abs(n.y) < 0.999f ? cross(float3(0.0f, 1.0f, 0.0f), n) : cross(float3(1.0f, 0.0f, 0.0f), n));
    float3 b = cross(n, t);
    uint back = 0u;
    for (uint k = 0u; k < p.tests && back < p.threshold; ++k) {
        uint h = blix_pcg(p.seed + i * p.tests + k);
        float u1 = float(h & 0xFFFFu) / 65536.0f, u2 = float(h >> 16) / 65536.0f;
        float r = sqrt(u1), phi = 6.28318530718f * u2;
        float3 d = t * (r * cos(phi)) + b * (r * sin(phi)) + n * sqrt(max(0.0f, 1.0f - u1));
        uint placement;
        if (blix_buried_hit(o, d, p.reach, tlas, table, blix_pcg(h), sc, placement) != 2u) continue;
        if (doubleSided[placement] == 0u) back++;
    }
    uint escaped = 0u;
    bool enclosed = back < p.threshold && p.enclosedTests > 0u;
    for (uint k = 0u; k < p.enclosedTests && enclosed; ++k) {
        uint h = blix_pcg(~(p.seed + i * p.enclosedTests + k));
        float u1 = float(h & 0xFFFFu) / 65536.0f, u2 = float(h >> 16) / 65536.0f;
        float r = sqrt(u1), phi = 6.28318530718f * u2;
        float3 d = t * (r * cos(phi)) + b * (r * sin(phi)) + n * sqrt(max(0.0f, 1.0f - u1));
        uint placement;
        bool into = blix_buried_hit(o, d, INFINITY, tlas, table, blix_pcg(h), sc, placement) == 2u && doubleSided[placement] == 0u;
        if (!into) escaped++;
        enclosed = escaped * 4u <= p.enclosedTests;
    }
    buried[i] = back >= p.threshold || enclosed ? 1u : 0u;
}

// Lightlets (VulkanSponza, SponzaLoop.LightletBake): visibility of surface points toward directions, as bits (1: the point
// faces the direction and nothing is in the way). One coverage coin seed for the whole bake, so a partly covered
// triangle is opaque or clear the same way in every direction from a point: visibility over directions is coherent.
struct LightletParams { uint count; uint dirs; uint words; uint seed; float lift; float reach; uint radii; uint azimuths; };

static bool blix_lightlet_visible(float3 position, float3 n, float3 d, constant LightletParams& p,
                                  instance_acceleration_structure tlas,
                                  intersection_function_table<triangle_data, instancing> table)
{
    if (dot(n, d) <= 0.0f) return false;
    intersector<triangle_data, instancing> isect;
    isect.assume_geometry_type(geometry_type::triangle);
    isect.accept_any_intersection(true);
    uint seed = p.seed;
    return isect.intersect(ray(position + n * p.lift, d, 0.0f, p.reach), tlas, 0xFFu, table, seed).type == intersection_type::none;
}

// Every point x every direction of a table: thread (direction, point); `words` uints of bits a point.
kernel void blix_lightlet_dense(device const float4* points [[buffer(0)]],
                                device atomic_uint* bits [[buffer(1)]],
                                instance_acceleration_structure tlas [[buffer(2)]],
                                intersection_function_table<triangle_data, instancing> table [[buffer(3)]],
                                constant LightletParams& p [[buffer(4)]],
                                device const float4* dirs [[buffer(5)]],
                                uint2 gid [[thread_position_in_grid]])
{
    if (gid.x >= p.dirs || gid.y >= p.count) return;
    float3 n = points[gid.y * 2u + 1u].xyz;
    if (dot(n, n) < 1e-12f) return;
    if (blix_lightlet_visible(points[gid.y * 2u].xyz, normalize(n), dirs[gid.x].xyz, p, tlas, table))
        atomic_fetch_or_explicit(&bits[gid.y * p.words + gid.x / 32u], 1u << (gid.x % 32u), memory_order_relaxed);
}

// Rings around a node direction for listed (point, node) pairs: thread (ring ray, pair); radii x azimuths rays a pair,
// ring r at angular tangent tanRadii[r], azimuth a at a * 2 pi / azimuths + r * 0.27 around the node, the basis as the
// host builds it; two uints of bits a pair (up to 64 rays).
kernel void blix_lightlet_rings(device const float4* points [[buffer(0)]],
                                device atomic_uint* bits [[buffer(1)]],
                                instance_acceleration_structure tlas [[buffer(2)]],
                                intersection_function_table<triangle_data, instancing> table [[buffer(3)]],
                                constant LightletParams& p [[buffer(4)]],
                                device const float4* nodeDirs [[buffer(5)]],
                                device const uint2* pairs [[buffer(6)]],
                                device const float* tanRadii [[buffer(7)]],
                                uint2 gid [[thread_position_in_grid]])
{
    uint rays = p.radii * p.azimuths;
    if (gid.x >= rays || gid.y >= p.count) return;
    uint2 pair = pairs[gid.y];
    float3 n = points[pair.x * 2u + 1u].xyz;
    if (dot(n, n) < 1e-12f) return;
    float3 dn = nodeDirs[pair.y].xyz;
    float3 u = normalize(abs(dn.y) < 0.999f ? cross(dn, float3(0.0f, 1.0f, 0.0f)) : cross(dn, float3(1.0f, 0.0f, 0.0f)));
    float3 v = cross(dn, u);
    uint r = gid.x / p.azimuths, a = gid.x % p.azimuths;
    float ang = float(a) * 6.28318530718f / float(p.azimuths) + float(r) * 0.27f;
    float3 d = normalize(dn + (u * cos(ang) + v * sin(ang)) * tanRadii[r]);
    if (blix_lightlet_visible(points[pair.x * 2u].xyz, normalize(n), d, p, tlas, table))
        atomic_fetch_or_explicit(&bits[gid.y * 2u + gid.x / 32u], 1u << (gid.x % 32u), memory_order_relaxed);
}
)";

struct HwxContext {
    id<MTLDevice> device;
    id<MTLCommandQueue> queue;
    id<MTLCommandQueue> ownQueue;   // the one it was created with (hwx_use_queue(ctx, NULL) returns to it)
    id<MTLComputePipelineState> pso;
    id<MTLIntersectionFunctionTable> table;
    id<MTLComputePipelineState> shadowPso;
    id<MTLComputePipelineState> buriedPso;            // blix_buried, with its own table
    id<MTLIntersectionFunctionTable> buriedTable;
    id<MTLComputePipelineState> lightletDensePso, lightletRingsPso;   // the lightlet bake, each with its own table
    id<MTLIntersectionFunctionTable> lightletDenseTable, lightletRingsTable;
    id<MTLIntersectionFunctionTable> shadowTable;       // the coverage coin, as the software batch
    id<MTLIntersectionFunctionTable> shadowAlphaTable;  // the alpha test (hwx_set_alpha), asked for per dispatch
    id<MTLCommandBuffer> lastShadow;  // waited on at destroy: its completion handler reads this context
    id<MTLCommandBuffer> lastTrace;   // likewise, for hwx_encode_trace
    // By the caller's stats slot (hwx_encode_shadow), so a frame's kinds of shadow dispatch are read apart.
    static constexpr uint32_t kShadowSlots = 4;
    std::atomic<uint64_t> shadowDispatches[kShadowSlots]{}, shadowGpuNs[kShadowSlots]{}, shadowLastGpuNs[kShadowSlots]{}, shadowFailures[kShadowSlots]{};
    std::atomic<uint64_t> traceDispatches{0}, traceGpuNs{0}, traceLastGpuNs{0}, traceFailures{0};
    // The alpha test's inputs (hwx_set_alpha), bound to both tables: on for the shadow table, off for the trace's, so
    // the ray check keeps the software traversal's coverage.
    id<MTLBuffer> alphaOff, alphaOn, rowMaterial, materials, uvs;
    std::vector<id<MTLTexture>> alphaTextures;
    std::vector<id<MTLAccelerationStructure>> blas;  // compacted; indexed by HwxEntry.blas
    id<MTLAccelerationStructure> tlas;
    id<MTLBuffer> instanceDescriptors;
    id<MTLBuffer> indices, primRows, entryRows;
    // Exported from Vulkan, retained here for as long as the scene is.
    id<MTLBuffer> positions, instances, triangles, owners, surfaces;
    std::string error;
};

static int Fail(HwxContext* ctx, const std::string& message) {
    ctx->error = message;
    return 1;
}

static bool Completed(HwxContext* ctx, id<MTLCommandBuffer> cb, const char* what) {
    if (cb.status == MTLCommandBufferStatusCompleted) return true;
    ctx->error = std::string(what) + " failed: " + (cb.error ? cb.error.localizedDescription.UTF8String : "unknown error");
    return false;
}

HWX_EXPORT const char* hwx_error(HwxContext* ctx) { return ctx ? ctx->error.c_str() : "no context"; }

// Null on failure, with the reason in `err`.
HWX_EXPORT HwxContext* hwx_create(void* mtlDevice, char* err, int errLength) {
    @autoreleasepool {
        auto report = [&](NSString* message) {
            if (err && errLength > 0) snprintf(err, errLength, "%s", message.UTF8String);
            return (HwxContext*)nullptr;
        };
        id<MTLDevice> device = (__bridge id<MTLDevice>)mtlDevice;
        if (!device) return report(@"no MTLDevice");
        if (!device.supportsRaytracing) return report([NSString stringWithFormat:@"%@ has no Metal ray tracing", device.name]);

        NSError* error = nil;
        MTLCompileOptions* options = [MTLCompileOptions new];
        options.languageVersion = MTLLanguageVersion3_0;   // argument buffers holding textures by MTLResourceID
        id<MTLLibrary> library = [device newLibraryWithSource:kSource options:options error:&error];
        if (!library) return report([@"trace kernel: " stringByAppendingString:error.localizedDescription]);
        id<MTLFunction> coverage = [library newFunctionWithName:@"blix_coverage"];
        // A pipeline and its own intersection function table (a table belongs to the pipeline it was made from).
        auto pipeline = [&](NSString* kernel, id<MTLIntersectionFunctionTable> __strong* table) -> id<MTLComputePipelineState> {
            MTLLinkedFunctions* linked = [MTLLinkedFunctions linkedFunctions];
            linked.functions = @[ coverage ];
            MTLComputePipelineDescriptor* pd = [MTLComputePipelineDescriptor new];
            pd.computeFunction = [library newFunctionWithName:kernel];
            pd.linkedFunctions = linked;
            id<MTLComputePipelineState> pso = [device newComputePipelineStateWithDescriptor:pd options:MTLPipelineOptionNone
                                                                                reflection:nil error:&error];
            if (!pso) return nil;
            MTLIntersectionFunctionTableDescriptor* td = [MTLIntersectionFunctionTableDescriptor new];
            td.functionCount = 1;
            *table = [pso newIntersectionFunctionTableWithDescriptor:td];
            [*table setFunction:[pso functionHandleWithFunction:coverage] atIndex:0];
            return pso;
        };
        id<MTLIntersectionFunctionTable> table, shadowTable;
        id<MTLComputePipelineState> pso = pipeline(@"blix_trace", &table);
        if (!pso) return report([@"trace pipeline: " stringByAppendingString:error.localizedDescription]);
        id<MTLComputePipelineState> shadowPso = pipeline(@"blix_shadow", &shadowTable);
        if (!shadowPso) return report([@"shadow pipeline: " stringByAppendingString:error.localizedDescription]);
        id<MTLIntersectionFunctionTable> buriedTable;
        id<MTLComputePipelineState> buriedPso = pipeline(@"blix_buried", &buriedTable);
        if (!buriedPso) return report([@"buried-test pipeline: " stringByAppendingString:error.localizedDescription]);
        id<MTLIntersectionFunctionTable> lightletDenseTable, lightletRingsTable;
        id<MTLComputePipelineState> lightletDensePso = pipeline(@"blix_lightlet_dense", &lightletDenseTable);
        if (!lightletDensePso) return report([@"lightlet pipeline: " stringByAppendingString:error.localizedDescription]);
        id<MTLComputePipelineState> lightletRingsPso = pipeline(@"blix_lightlet_rings", &lightletRingsTable);
        if (!lightletRingsPso) return report([@"lightlet rings pipeline: " stringByAppendingString:error.localizedDescription]);
        MTLIntersectionFunctionTableDescriptor* alphaTd = [MTLIntersectionFunctionTableDescriptor new];
        alphaTd.functionCount = 1;
        id<MTLIntersectionFunctionTable> shadowAlphaTable = [shadowPso newIntersectionFunctionTableWithDescriptor:alphaTd];
        [shadowAlphaTable setFunction:[shadowPso functionHandleWithFunction:coverage] atIndex:0];

        auto* ctx = new HwxContext();
        ctx->device = device;
        ctx->queue = [device newCommandQueue];
        ctx->ownQueue = ctx->queue;
        ctx->queue.label = @"blix-hwx";
        ctx->pso = pso;
        ctx->table = table;
        ctx->shadowPso = shadowPso;
        ctx->shadowTable = shadowTable;
        ctx->shadowAlphaTable = shadowAlphaTable;
        ctx->buriedPso = buriedPso;
        ctx->buriedTable = buriedTable;
        ctx->lightletDensePso = lightletDensePso;
        ctx->lightletDenseTable = lightletDenseTable;
        ctx->lightletRingsPso = lightletRingsPso;
        ctx->lightletRingsTable = lightletRingsTable;
        return ctx;
    }
}

HWX_EXPORT void hwx_destroy(HwxContext* ctx) {
    if (!ctx) return;
    if (ctx->lastShadow) [ctx->lastShadow waitUntilCompleted];  // its handler writes into ctx
    if (ctx->lastTrace) [ctx->lastTrace waitUntilCompleted];
    delete ctx;
}

HWX_EXPORT int hwx_build(HwxContext* ctx,
                         void* positions, uint32_t vertexCount, void* instances, void* triangles, void* owners, void* surfaces,
                         const uint32_t* indices, const uint32_t* primRows, uint64_t primCount,
                         const HwxBlas* blas, uint32_t blasCount, const HwxEntry* entries, uint32_t entryCount,
                         HwxBuildStats* stats) {
    @autoreleasepool {
        id<MTLDevice> device = ctx->device;
        *stats = {};
        ctx->positions = (__bridge id<MTLBuffer>)positions;
        ctx->instances = (__bridge id<MTLBuffer>)instances;
        ctx->triangles = (__bridge id<MTLBuffer>)triangles;
        ctx->owners = (__bridge id<MTLBuffer>)owners;
        ctx->surfaces = (__bridge id<MTLBuffer>)surfaces;
        if (!ctx->positions || !ctx->instances || !ctx->triangles || !ctx->owners || !ctx->surfaces)
            return Fail(ctx, "a ray scene block was not exported");
        if (ctx->positions.length < uint64_t(vertexCount) * 16) return Fail(ctx, "Positions is smaller than its vertex count");

        uint64_t primBytes = std::max<uint64_t>(primCount, 1);
        ctx->indices = [device newBufferWithBytes:indices length:primBytes * 12 options:MTLResourceStorageModeShared];
        ctx->primRows = [device newBufferWithBytes:primRows length:primBytes * 4 options:MTLResourceStorageModeShared];
        std::vector<uint32_t> entryRows(std::max<uint32_t>(entryCount, 1) * 2u, 0u);
        for (uint32_t e = 0; e < entryCount; ++e) {
            const HwxBlas& b = blas[entries[e].blas];
            entryRows[e * 2] = b.primBase;
            entryRows[e * 2 + 1] = b.solidCount > 0 ? b.solidCount : b.partialCount;
        }
        ctx->entryRows = [device newBufferWithBytes:entryRows.data() length:entryRows.size() * 4 options:MTLResourceStorageModeShared];
        stats->geometryBytes = ctx->indices.length + ctx->primRows.length + ctx->entryRows.length;
        // The alpha test, off until hwx_set_alpha gives it materials (its buffers bound anyway: the function names them).
        uint32_t off[4] = {0, 0, 0, 0};
        ctx->alphaOff = [device newBufferWithBytes:off length:sizeof(off) options:MTLResourceStorageModeShared];
        ctx->alphaOn = nil;
        ctx->rowMaterial = ctx->materials = ctx->uvs = ctx->alphaOff;
        for (id<MTLIntersectionFunctionTable> table : {ctx->table, ctx->shadowTable, ctx->shadowAlphaTable, ctx->buriedTable,
                                                       ctx->lightletDenseTable, ctx->lightletRingsTable}) {
            [table setBuffer:ctx->primRows offset:0 atIndex:0];
            [table setBuffer:ctx->entryRows offset:0 atIndex:1];
            [table setBuffer:ctx->instances offset:0 atIndex:2];
            [table setBuffer:ctx->triangles offset:0 atIndex:3];
            [table setBuffer:ctx->owners offset:0 atIndex:4];
            [table setBuffer:ctx->surfaces offset:0 atIndex:5];
            [table setBuffer:ctx->alphaOff offset:0 atIndex:6];
            [table setBuffer:ctx->alphaOff offset:0 atIndex:7];
            [table setBuffer:ctx->alphaOff offset:0 atIndex:8];
            [table setBuffer:ctx->alphaOff offset:0 atIndex:9];
        }

        // Bottom level: one structure per hierarchy, built uncompacted, then compacted below.
        auto geometry = [&](uint32_t firstPrim, uint32_t count, bool opaque) {
            MTLAccelerationStructureTriangleGeometryDescriptor* g = [MTLAccelerationStructureTriangleGeometryDescriptor descriptor];
            g.vertexBuffer = ctx->positions;
            g.vertexBufferOffset = 0;
            g.vertexStride = 16;
            g.vertexFormat = MTLAttributeFormatFloat3;
            g.indexBuffer = ctx->indices;
            g.indexBufferOffset = uint64_t(firstPrim) * 12;
            g.indexType = MTLIndexTypeUInt32;
            g.triangleCount = count;
            g.opaque = opaque;
            g.intersectionFunctionTableOffset = 0;
            return g;
        };
        std::vector<MTLPrimitiveAccelerationStructureDescriptor*> descriptors(blasCount);
        std::vector<id<MTLAccelerationStructure>> built(blasCount);
        uint64_t maxScratch = 0;
        for (uint32_t b = 0; b < blasCount; ++b) {
            NSMutableArray* geometries = [NSMutableArray array];
            if (blas[b].solidCount > 0) [geometries addObject:geometry(blas[b].primBase, blas[b].solidCount, true)];
            if (blas[b].partialCount > 0)
                [geometries addObject:geometry(blas[b].primBase + blas[b].solidCount, blas[b].partialCount, false)];
            if (geometries.count == 0) {
                // Nothing left to meet (every triangle uncovered): one degenerate triangle that no ray hits.
                [geometries addObject:geometry(0, 0, true)];
            }
            stats->geometryCount += (uint32_t)geometries.count;
            MTLPrimitiveAccelerationStructureDescriptor* d = [MTLPrimitiveAccelerationStructureDescriptor descriptor];
            d.geometryDescriptors = geometries;
            descriptors[b] = d;
            MTLAccelerationStructureSizes sizes = [device accelerationStructureSizesWithDescriptor:d];
            built[b] = [device newAccelerationStructureWithSize:sizes.accelerationStructureSize];
            if (!built[b]) return Fail(ctx, "out of memory for a bottom-level acceleration structure");
            stats->blasBytes += sizes.accelerationStructureSize;
            maxScratch = std::max<uint64_t>(maxScratch, sizes.buildScratchBufferSize);
        }
        stats->blasCount = blasCount;
        stats->scratchBytes = maxScratch;
        // One scratch, reused: each build is its own encoder, and the scratch is a tracked resource, so Metal orders them.
        id<MTLBuffer> scratch = [device newBufferWithLength:std::max<uint64_t>(maxScratch, 16) options:MTLResourceStorageModePrivate];
        id<MTLBuffer> compactedSizes = [device newBufferWithLength:std::max<uint32_t>(blasCount, 1) * 4 options:MTLResourceStorageModeShared];
        id<MTLCommandBuffer> cb = [ctx->queue commandBuffer];
        for (uint32_t b = 0; b < blasCount; ++b) {
            id<MTLAccelerationStructureCommandEncoder> enc = [cb accelerationStructureCommandEncoder];
            [enc buildAccelerationStructure:built[b] descriptor:descriptors[b] scratchBuffer:scratch scratchBufferOffset:0];
            [enc writeCompactedAccelerationStructureSize:built[b] toBuffer:compactedSizes offset:b * 4];
            [enc endEncoding];
        }
        [cb commit];
        [cb waitUntilCompleted];
        if (!Completed(ctx, cb, "bottom-level build")) return 1;
        stats->blasGpuMs = (cb.GPUEndTime - cb.GPUStartTime) * 1e3;
        scratch = nil;

        // Compact, then the top level over the compacted structures, one entry per instance.
        const uint32_t* sizes = (const uint32_t*)compactedSizes.contents;
        ctx->blas.assign(blasCount, nil);
        for (uint32_t b = 0; b < blasCount; ++b) {
            ctx->blas[b] = [device newAccelerationStructureWithSize:sizes[b]];
            if (!ctx->blas[b]) return Fail(ctx, "out of memory for a compacted acceleration structure");
            stats->compactedBytes += sizes[b];
        }
        ctx->instanceDescriptors = [device newBufferWithLength:sizeof(MTLAccelerationStructureInstanceDescriptor) * std::max<uint32_t>(entryCount, 1)
                                                       options:MTLResourceStorageModeShared];
        auto* desc = (MTLAccelerationStructureInstanceDescriptor*)ctx->instanceDescriptors.contents;
        for (uint32_t e = 0; e < entryCount; ++e) {
            desc[e] = {};
            for (int c = 0; c < 4; ++c)
                desc[e].transformationMatrix.columns[c] = MTLPackedFloat3Make(entries[e].transform[c * 3], entries[e].transform[c * 3 + 1],
                                                                              entries[e].transform[c * 3 + 2]);
            desc[e].options = MTLAccelerationStructureInstanceOptionNone;  // per-geometry opacity decides
            desc[e].mask = 0xFF;
            desc[e].intersectionFunctionTableOffset = 0;
            desc[e].accelerationStructureIndex = entries[e].blas;
        }
        NSMutableArray* referenced = [NSMutableArray arrayWithCapacity:blasCount];
        for (auto& b : ctx->blas) [referenced addObject:b];
        MTLInstanceAccelerationStructureDescriptor* td = [MTLInstanceAccelerationStructureDescriptor descriptor];
        td.instancedAccelerationStructures = referenced;
        td.instanceCount = entryCount;
        td.instanceDescriptorBuffer = ctx->instanceDescriptors;
        td.instanceDescriptorType = MTLAccelerationStructureInstanceDescriptorTypeDefault;
        MTLAccelerationStructureSizes tlasSizes = [device accelerationStructureSizesWithDescriptor:td];
        ctx->tlas = [device newAccelerationStructureWithSize:tlasSizes.accelerationStructureSize];
        stats->tlasBytes = tlasSizes.accelerationStructureSize;
        id<MTLBuffer> tlasScratch = [device newBufferWithLength:std::max<uint64_t>(tlasSizes.buildScratchBufferSize, 16)
                                                        options:MTLResourceStorageModePrivate];
        cb = [ctx->queue commandBuffer];
        id<MTLAccelerationStructureCommandEncoder> enc = [cb accelerationStructureCommandEncoder];
        for (uint32_t b = 0; b < blasCount; ++b) [enc copyAndCompactAccelerationStructure:built[b] toAccelerationStructure:ctx->blas[b]];
        [enc endEncoding];
        enc = [cb accelerationStructureCommandEncoder];  // its own encoder: the top level reads the compacted structures
        [enc buildAccelerationStructure:ctx->tlas descriptor:td scratchBuffer:tlasScratch scratchBufferOffset:0];
        [enc endEncoding];
        [cb commit];
        [cb waitUntilCompleted];
        if (!Completed(ctx, cb, "compaction and top-level build")) return 1;
        stats->compactTlasGpuMs = (cb.GPUEndTime - cb.GPUStartTime) * 1e3;
        return 0;
    }
}

// `repeats` dispatches, each its own command buffer, waited on; gpuMs[k] is the k-th one's GPU time.
HWX_EXPORT int hwx_trace(HwxContext* ctx, void* rays, void* hits, uint32_t count, uint32_t seedBase, uint32_t closestOnly,
                         uint32_t repeats, double* gpuMs) {
    @autoreleasepool {
        if (!ctx->tlas) return Fail(ctx, "trace before build");
        id<MTLBuffer> rayBuffer = (__bridge id<MTLBuffer>)rays;
        id<MTLBuffer> hitBuffer = (__bridge id<MTLBuffer>)hits;
        if (rayBuffer.length < uint64_t(count) * 32) return Fail(ctx, "the ray buffer is smaller than its count");
        if (hitBuffer.length < uint64_t(count) * 48) return Fail(ctx, "the hit buffer is smaller than its count");
        std::vector<id<MTLCommandBuffer>> cbs;
        for (uint32_t k = 0; k < std::max(repeats, 1u); ++k) {
            id<MTLCommandBuffer> cb = [ctx->queue commandBuffer];
            id<MTLComputeCommandEncoder> enc = [cb computeCommandEncoder];
            [enc setComputePipelineState:ctx->pso];
            [enc setBuffer:rayBuffer offset:0 atIndex:0];
            [enc setBuffer:hitBuffer offset:0 atIndex:1];
            [enc setAccelerationStructure:ctx->tlas atBufferIndex:2];
            [enc setIntersectionFunctionTable:ctx->table atBufferIndex:3];
            uint32_t params[4] = {count, seedBase, closestOnly, 0};
            [enc setBytes:params length:sizeof(params) atIndex:4];
            [enc setBuffer:ctx->primRows offset:0 atIndex:5];
            [enc setBuffer:ctx->entryRows offset:0 atIndex:6];
            [enc setBuffer:ctx->instances offset:0 atIndex:7];
            [enc setBuffer:ctx->triangles offset:0 atIndex:8];
            [enc setBuffer:ctx->positions offset:0 atIndex:9];
            [enc setBuffer:ctx->owners offset:0 atIndex:10];
            [enc setBuffer:ctx->surfaces offset:0 atIndex:11];
            [enc setBuffer:rayBuffer offset:0 atIndex:12];   // the limit's slot, unread (flags 0)
            // Reached only through the top level and the function table, so not resident unless named here.
            [enc useResources:ctx->blas.data() count:ctx->blas.size() usage:MTLResourceUsageRead];
            id<MTLResource> alphaBuffers[] = {ctx->alphaOff, ctx->rowMaterial, ctx->materials, ctx->uvs};
            [enc useResources:alphaBuffers count:4 usage:MTLResourceUsageRead];
            [enc dispatchThreads:MTLSizeMake(count, 1, 1) threadsPerThreadgroup:MTLSizeMake(64, 1, 1)];
            [enc endEncoding];
            [cb commit];
            cbs.push_back(cb);
        }
        for (size_t k = 0; k < cbs.size(); ++k) {
            [cbs[k] waitUntilCompleted];
            if (!Completed(ctx, cbs[k], "trace")) return 1;
            if (gpuMs) gpuMs[k] = (cbs[k].GPUEndTime - cbs[k].GPUStartTime) * 1e3;
        }
        return 0;
    }
}

// Sun shadows inside a frame: waits for `waitValue` on the frame timeline, traces `count` shadow rays toward
// `direction` (origins: float4 position + tMax at originsOffset; answers: one uint each at occludedOffset), and with
// `skyRays` (two float4s each: origin + tMax, direction; or null) a sky ray per ray too, then signals `signalValue`. Returns at once. The signal is encoded whatever happens, since a frame waits for it: on a bad
// argument the work is skipped (and counted in hwx_shadow_stats' failures), never the signal.
// `limit` (or null): a uint at limitOffset the GPU wrote earlier in the frame, the live count -- the rays past it are not
// traced (and their answers not written). `directions` (or null): a float4 per ray, its own direction (xyz), in place
// of `direction` -- each ray toward its own point of the sun's disc.
HWX_EXPORT int hwx_encode_shadow(HwxContext* ctx, void* sharedEvent, uint64_t waitValue, uint64_t signalValue,
                                 void* origins, uint64_t originsOffset, void* occluded, uint64_t occludedOffset,
                                 uint32_t count, const float* direction, uint32_t seedBase, void* skyRays, uint32_t alphaTest,
                                 uint32_t statSlot, void* limit, uint64_t limitOffset, void* directions, void* pixelOf) {
    @autoreleasepool {
        id<MTLSharedEvent> event = (__bridge id<MTLSharedEvent>)sharedEvent;
        id<MTLBuffer> originBuffer = (__bridge id<MTLBuffer>)origins;
        id<MTLBuffer> occludedBuffer = (__bridge id<MTLBuffer>)occluded;
        id<MTLBuffer> skyBuffer = (__bridge id<MTLBuffer>)skyRays;
        id<MTLBuffer> limitBuffer = (__bridge id<MTLBuffer>)limit;
        id<MTLBuffer> directionBuffer = (__bridge id<MTLBuffer>)directions;
        id<MTLBuffer> pixelBuffer = (__bridge id<MTLBuffer>)pixelOf;
        id<MTLCommandBuffer> cb = [ctx->queue commandBuffer];
        cb.label = @"blix-hwx shadow";
        [cb encodeWaitForEvent:event value:waitValue];
        int result = 0;
        if (!ctx->tlas) result = Fail(ctx, "shadow trace before build");
        else if (!originBuffer || originBuffer.length < originsOffset + uint64_t(count) * (pixelBuffer ? 32 : 16)) result = Fail(ctx, "the origin buffer is smaller than its count");
        else if (!occludedBuffer || occludedBuffer.length < occludedOffset + uint64_t(count) * 4) result = Fail(ctx, "the occlusion buffer is smaller than its count");
        else if (pixelBuffer && (pixelBuffer.length < uint64_t(count) * 4 || skyBuffer)) result = Fail(ctx, "packed rays need a pixel per ray and no sky rays");
        else if (skyBuffer && skyBuffer.length < uint64_t(count) * 32) result = Fail(ctx, "the sky ray buffer is smaller than its count");
        else if (limitBuffer && limitBuffer.length < limitOffset + 4) result = Fail(ctx, "the limit buffer is smaller than its offset");
        else if (directionBuffer && directionBuffer.length < uint64_t(count) * 16) result = Fail(ctx, "the direction buffer is smaller than its count");
        if (result == 0 && count > 0) {
            id<MTLComputeCommandEncoder> enc = [cb computeCommandEncoder];
            [enc setComputePipelineState:ctx->shadowPso];
            [enc setBuffer:originBuffer offset:originsOffset atIndex:0];
            [enc setBuffer:occludedBuffer offset:occludedOffset atIndex:1];
            [enc setAccelerationStructure:ctx->tlas atBufferIndex:2];
            // The alpha test only where asked and set up; otherwise the coin, as the software batch meets cut-outs.
            bool alpha = alphaTest != 0 && ctx->alphaOn != nil;
            [enc setIntersectionFunctionTable:alpha ? ctx->shadowAlphaTable : ctx->shadowTable atBufferIndex:3];
            struct { float direction[4]; uint32_t count, seedBase, sky, flags; } params = {
                {direction[0], direction[1], direction[2], 0.0f}, count, seedBase, skyBuffer ? 1u : 0u,
                (limitBuffer ? 1u : 0u) | (directionBuffer ? 2u : 0u) | (pixelBuffer ? 4u : 0u)};
            [enc setBytes:&params length:sizeof(params) atIndex:4];
            [enc setBuffer:skyBuffer ? skyBuffer : originBuffer offset:skyBuffer ? 0 : originsOffset atIndex:5];
            // Unread without their flags; bound anyway, as every argument must be.
            [enc setBuffer:limitBuffer ? limitBuffer : originBuffer offset:limitBuffer ? limitOffset : originsOffset atIndex:6];
            [enc setBuffer:directionBuffer ? directionBuffer : originBuffer offset:directionBuffer ? 0 : originsOffset atIndex:7];
            [enc setBuffer:pixelBuffer ? pixelBuffer : originBuffer offset:pixelBuffer ? 0 : originsOffset atIndex:8];
            // Reached only through the top level and the function table, so not resident unless named here.
            [enc useResources:ctx->blas.data() count:ctx->blas.size() usage:MTLResourceUsageRead];
            id<MTLResource> tableBuffers[] = {ctx->primRows, ctx->entryRows, ctx->instances, ctx->triangles, ctx->owners, ctx->surfaces,
                                              alpha ? ctx->alphaOn : ctx->alphaOff, ctx->rowMaterial, ctx->materials, ctx->uvs};
            [enc useResources:tableBuffers count:10 usage:MTLResourceUsageRead];
            // The materials' textures are reached through the argument buffer alone.
            if (alpha && !ctx->alphaTextures.empty())
                [enc useResources:ctx->alphaTextures.data() count:ctx->alphaTextures.size() usage:MTLResourceUsageRead];
            [enc dispatchThreads:MTLSizeMake(count, 1, 1) threadsPerThreadgroup:MTLSizeMake(64, 1, 1)];
            [enc endEncoding];
        }
        [cb encodeSignalEvent:event value:signalValue];
        HwxContext* c = ctx;
        bool skipped = result != 0;
        uint32_t slot = std::min(statSlot, HwxContext::kShadowSlots - 1);
        [cb addCompletedHandler:^(id<MTLCommandBuffer> done) {
            if (skipped || done.status != MTLCommandBufferStatusCompleted) { c->shadowFailures[slot]++; return; }
            uint64_t ns = uint64_t((done.GPUEndTime - done.GPUStartTime) * 1e9);
            c->shadowLastGpuNs[slot] = ns;
            c->shadowGpuNs[slot] += ns;
            c->shadowDispatches[slot]++;
        }];
        [cb commit];
        ctx->lastShadow = cb;
        return result;
    }
}

// Closest hits inside a frame -- a wavefront's batch (ray_check.comp's records, as hwx_trace answers them, the any-hit
// trace skipped): waits for `waitValue` on the frame timeline, traces `count` rays (two float4 each from raysOffset) into
// `hits` (three uint4 each from hitsOffset), signals `signalValue`. Returns at once. As hwx_encode_shadow, the signal is
// encoded whatever happens, since a frame waits for it; a skipped or failed dispatch is counted (hwx_trace_stats).
// `limit` (or null): the live count at limitOffset, as hwx_encode_shadow's.
HWX_EXPORT int hwx_encode_trace(HwxContext* ctx, void* sharedEvent, uint64_t waitValue, uint64_t signalValue,
                                void* rays, uint64_t raysOffset, void* hits, uint64_t hitsOffset,
                                uint32_t count, uint32_t seedBase, void* limit, uint64_t limitOffset) {
    @autoreleasepool {
        id<MTLSharedEvent> event = (__bridge id<MTLSharedEvent>)sharedEvent;
        id<MTLBuffer> rayBuffer = (__bridge id<MTLBuffer>)rays;
        id<MTLBuffer> hitBuffer = (__bridge id<MTLBuffer>)hits;
        id<MTLBuffer> limitBuffer = (__bridge id<MTLBuffer>)limit;
        id<MTLCommandBuffer> cb = [ctx->queue commandBuffer];
        cb.label = @"blix-hwx trace";
        [cb encodeWaitForEvent:event value:waitValue];
        int result = 0;
        if (!ctx->tlas) result = Fail(ctx, "trace before build");
        else if (!rayBuffer || rayBuffer.length < raysOffset + uint64_t(count) * 32) result = Fail(ctx, "the ray buffer is smaller than its count");
        else if (!hitBuffer || hitBuffer.length < hitsOffset + uint64_t(count) * 48) result = Fail(ctx, "the hit buffer is smaller than its count");
        else if (limitBuffer && limitBuffer.length < limitOffset + 4) result = Fail(ctx, "the limit buffer is smaller than its offset");
        if (result == 0 && count > 0) {
            id<MTLComputeCommandEncoder> enc = [cb computeCommandEncoder];
            [enc setComputePipelineState:ctx->pso];
            [enc setBuffer:rayBuffer offset:raysOffset atIndex:0];
            [enc setBuffer:hitBuffer offset:hitsOffset atIndex:1];
            [enc setAccelerationStructure:ctx->tlas atBufferIndex:2];
            [enc setIntersectionFunctionTable:ctx->table atBufferIndex:3];
            uint32_t params[4] = {count, seedBase, 1u, limitBuffer ? 1u : 0u};
            [enc setBytes:params length:sizeof(params) atIndex:4];
            [enc setBuffer:limitBuffer ? limitBuffer : rayBuffer offset:limitBuffer ? limitOffset : raysOffset atIndex:12];
            [enc setBuffer:ctx->primRows offset:0 atIndex:5];
            [enc setBuffer:ctx->entryRows offset:0 atIndex:6];
            [enc setBuffer:ctx->instances offset:0 atIndex:7];
            [enc setBuffer:ctx->triangles offset:0 atIndex:8];
            [enc setBuffer:ctx->positions offset:0 atIndex:9];
            [enc setBuffer:ctx->owners offset:0 atIndex:10];
            [enc setBuffer:ctx->surfaces offset:0 atIndex:11];
            [enc useResources:ctx->blas.data() count:ctx->blas.size() usage:MTLResourceUsageRead];
            // The trace table's alpha-test buffers (off for it, but named by the function, so resident).
            id<MTLResource> alphaBuffers[] = {ctx->alphaOff, ctx->rowMaterial, ctx->materials, ctx->uvs};
            [enc useResources:alphaBuffers count:4 usage:MTLResourceUsageRead];
            [enc dispatchThreads:MTLSizeMake(count, 1, 1) threadsPerThreadgroup:MTLSizeMake(64, 1, 1)];
            [enc endEncoding];
        }
        [cb encodeSignalEvent:event value:signalValue];
        HwxContext* c = ctx;
        bool skipped = result != 0;
        [cb addCompletedHandler:^(id<MTLCommandBuffer> done) {
            if (skipped || done.status != MTLCommandBufferStatusCompleted) { c->traceFailures++; return; }
            uint64_t ns = uint64_t((done.GPUEndTime - done.GPUStartTime) * 1e9);
            c->traceLastGpuNs = ns;
            c->traceGpuNs += ns;
            c->traceDispatches++;
        }];
        [cb commit];
        ctx->lastTrace = cb;
        return result;
    }
}

HWX_EXPORT void hwx_trace_stats(HwxContext* ctx, uint64_t* dispatches, uint64_t* totalGpuNs, uint64_t* lastGpuNs, uint64_t* failures) {
    *dispatches = ctx->traceDispatches;
    *totalGpuNs = ctx->traceGpuNs;
    *lastGpuNs = ctx->traceLastGpuNs;
    *failures = ctx->traceFailures;
}

// What the shadow dispatches of one stats slot (0xFFFFFFFF: all) have cost so far, from their command buffers' GPU
// times; `lastGpuNs` is the slot's last (all: the largest slot's last).
HWX_EXPORT void hwx_shadow_stats(HwxContext* ctx, uint32_t slot, uint64_t* dispatches, uint64_t* totalGpuNs, uint64_t* lastGpuNs,
                                 uint64_t* failures) {
    *dispatches = *totalGpuNs = *lastGpuNs = *failures = 0;
    for (uint32_t k = 0; k < HwxContext::kShadowSlots; ++k) {
        if (slot != 0xFFFFFFFFu && slot != k) continue;
        *dispatches += ctx->shadowDispatches[k];
        *totalGpuNs += ctx->shadowGpuNs[k];
        *lastGpuNs = std::max<uint64_t>(*lastGpuNs, ctx->shadowLastGpuNs[k]);
        *failures += ctx->shadowFailures[k];
    }
}

// The shadow trace's alpha test: per material its albedo texture (an MTLTexture exported from the device), alpha cutoff
// and alpha factor; per packed triangle row its material (0xFFFFFFFF: none, the coverage coin decides); and the scene's
// texture coordinates, one float2 per position (an exported buffer). From now on the shadow pass meets a partly covered
// triangle where its material's alpha passes at the point hit; the ray check keeps the coin.
HWX_EXPORT int hwx_set_alpha(HwxContext* ctx, void* const* textures, const float* cutoffs, const float* alphaFactors,
                             uint32_t materialCount, const uint32_t* rowMaterial, uint32_t rowCount, void* uvs, float mip) {
    @autoreleasepool {
        id<MTLDevice> device = ctx->device;
        id<MTLBuffer> uvBuffer = (__bridge id<MTLBuffer>)uvs;
        if (!uvBuffer) return Fail(ctx, "no texture coordinate buffer");
        ctx->alphaTextures.clear();
        struct Material { MTLResourceID albedo; float cutoff; float alphaFactor; };
        static_assert(sizeof(Material) == 16, "BlixAlphaMaterial");
        std::vector<Material> materials(std::max<uint32_t>(materialCount, 1));
        for (uint32_t m = 0; m < materialCount; ++m) {
            id<MTLTexture> texture = (__bridge id<MTLTexture>)textures[m];
            if (!texture) return Fail(ctx, "a material has no texture");
            ctx->alphaTextures.push_back(texture);
            materials[m] = {texture.gpuResourceID, cutoffs[m], alphaFactors[m]};
        }
        ctx->materials = [device newBufferWithBytes:materials.data() length:materials.size() * sizeof(Material) options:MTLResourceStorageModeShared];
        ctx->rowMaterial = [device newBufferWithBytes:rowMaterial length:std::max<uint32_t>(rowCount, 1) * 4 options:MTLResourceStorageModeShared];
        ctx->uvs = uvBuffer;
        struct { uint32_t enabled; float mip; uint32_t pad1, pad2; } on = {1, mip, 0, 0};
        ctx->alphaOn = [device newBufferWithBytes:&on length:sizeof(on) options:MTLResourceStorageModeShared];
        // The lightlet bake's tables take the alpha test too: its sun records must answer as the traced shadows do.
        for (id<MTLIntersectionFunctionTable> table : {ctx->table, ctx->shadowTable, ctx->shadowAlphaTable, ctx->lightletDenseTable, ctx->lightletRingsTable}) {
            bool alpha = table == ctx->shadowAlphaTable || table == ctx->lightletDenseTable || table == ctx->lightletRingsTable;
            [table setBuffer:alpha ? ctx->alphaOn : ctx->alphaOff offset:0 atIndex:6];
            [table setBuffer:ctx->rowMaterial offset:0 atIndex:7];
            [table setBuffer:ctx->materials offset:0 atIndex:8];
            [table setBuffer:ctx->uvs offset:0 atIndex:9];
        }
        return 0;
    }
}

// The lightmap's buried test (blix_buried) over `count` records in host memory (two float4s each), into one byte each
// (1: buried). In chunks, each waited for; callable off the frame's thread (its own command buffers on the queue).
HWX_EXPORT int hwx_buried(HwxContext* ctx, const float* records, uint32_t count, uint32_t tests, uint32_t threshold, uint32_t seed,
                          float reach, float lift, uint32_t enclosedTests, const uint8_t* doubleSided, uint32_t placementCount,
                          const uint32_t* contactStart, uint32_t contactRows, const float* contactTris, uint32_t contactCount,
                          uint8_t* buried, double* gpuMs) {
    @autoreleasepool {
        if (!ctx->tlas) return Fail(ctx, "buried test before build");
        id<MTLDevice> device = ctx->device;
        id<MTLBuffer> sided = [device newBufferWithBytes:doubleSided length:std::max<uint32_t>(placementCount, 1) options:MTLResourceStorageModeShared];
        // The contact table (the demo's GpuContacts): none, one empty row.
        uint32_t noStart[2] = {0, 0};
        float noTris[12] = {0};
        id<MTLBuffer> starts = [device newBufferWithBytes:(contactRows > 0 ? contactStart : noStart) length:uint64_t(std::max<uint32_t>(contactRows, 2)) * 4 options:MTLResourceStorageModeShared];
        id<MTLBuffer> tris = [device newBufferWithBytes:(contactCount > 0 ? contactTris : noTris) length:uint64_t(std::max<uint32_t>(contactCount, 1)) * 48 options:MTLResourceStorageModeShared];
        const uint32_t chunk = 1u << 20;
        if (gpuMs) *gpuMs = 0.0;
        for (uint32_t first = 0; first < count; first += chunk) {
            uint32_t n = std::min(chunk, count - first);
            id<MTLBuffer> in = [device newBufferWithBytes:records + uint64_t(first) * 8 length:uint64_t(n) * 32 options:MTLResourceStorageModeShared];
            id<MTLBuffer> out = [device newBufferWithLength:std::max<uint32_t>(n, 4) options:MTLResourceStorageModeShared];
            id<MTLCommandBuffer> cb = [ctx->queue commandBuffer];
            cb.label = @"blix-hwx buried";
            id<MTLComputeCommandEncoder> enc = [cb computeCommandEncoder];
            [enc setComputePipelineState:ctx->buriedPso];
            [enc setBuffer:in offset:0 atIndex:0];
            [enc setBuffer:out offset:0 atIndex:1];
            [enc setAccelerationStructure:ctx->tlas atBufferIndex:2];
            [enc setIntersectionFunctionTable:ctx->buriedTable atBufferIndex:3];
            struct { uint32_t count, tests, threshold, seed; float reach, lift; uint32_t enclosedTests, contactRows; } params =
                {n, tests, threshold, seed + first * tests, reach, lift, enclosedTests, contactRows > 0 ? contactRows : 2u};
            [enc setBytes:&params length:sizeof(params) atIndex:4];
            [enc setBuffer:sided offset:0 atIndex:5];
            [enc setBuffer:ctx->primRows offset:0 atIndex:6];
            [enc setBuffer:ctx->entryRows offset:0 atIndex:7];
            [enc setBuffer:ctx->instances offset:0 atIndex:8];
            [enc setBuffer:ctx->triangles offset:0 atIndex:9];
            [enc setBuffer:ctx->owners offset:0 atIndex:10];
            [enc setBuffer:starts offset:0 atIndex:11];
            [enc setBuffer:tris offset:0 atIndex:12];
            [enc useResources:ctx->blas.data() count:ctx->blas.size() usage:MTLResourceUsageRead];
            id<MTLResource> tableBuffers[] = {ctx->surfaces, ctx->alphaOff, ctx->rowMaterial, ctx->materials, ctx->uvs};
            [enc useResources:tableBuffers count:5 usage:MTLResourceUsageRead];
            [enc dispatchThreads:MTLSizeMake(n, 1, 1) threadsPerThreadgroup:MTLSizeMake(64, 1, 1)];
            [enc endEncoding];
            [cb commit];
            [cb waitUntilCompleted];
            if (!Completed(ctx, cb, "buried test")) return 1;
            if (gpuMs) *gpuMs += (cb.GPUEndTime - cb.GPUStartTime) * 1e3;
            memcpy(buried + first, out.contents, n);
        }
        return 0;
    }
}

// The lightlet bake's two passes (blix_lightlet_dense / _rings) over host memory, in chunks, each waited for; callable off
// the frame's thread, as hwx_buried. Points: two float4s each (position, normal). Bits are OR-ed into `bits`, which the
// caller zeroes: dense, `words` uints a point (words >= ceil(dirCount / 32)); rings, two uints a pair.
static int LightletDispatch(HwxContext* ctx, id<MTLComputePipelineState> pso, id<MTLIntersectionFunctionTable> table, const char* what,
                            id<MTLBuffer> points, id<MTLBuffer> out, const void* params, uint32_t paramBytes,
                            id<MTLBuffer> b5, id<MTLBuffer> b6, id<MTLBuffer> b7, MTLSize grid, double* gpuMs) {
    id<MTLCommandBuffer> cb = [ctx->queue commandBuffer];
    cb.label = [NSString stringWithUTF8String:what];
    id<MTLComputeCommandEncoder> enc = [cb computeCommandEncoder];
    [enc setComputePipelineState:pso];
    [enc setBuffer:points offset:0 atIndex:0];
    [enc setBuffer:out offset:0 atIndex:1];
    [enc setAccelerationStructure:ctx->tlas atBufferIndex:2];
    [enc setIntersectionFunctionTable:table atBufferIndex:3];
    [enc setBytes:params length:paramBytes atIndex:4];
    [enc setBuffer:b5 offset:0 atIndex:5];
    if (b6) [enc setBuffer:b6 offset:0 atIndex:6];
    if (b7) [enc setBuffer:b7 offset:0 atIndex:7];
    [enc useResources:ctx->blas.data() count:ctx->blas.size() usage:MTLResourceUsageRead];
    id<MTLResource> tableBuffers[] = {ctx->primRows, ctx->entryRows, ctx->instances, ctx->triangles, ctx->owners, ctx->surfaces,
                                      ctx->alphaOn ? ctx->alphaOn : ctx->alphaOff, ctx->rowMaterial, ctx->materials, ctx->uvs};
    [enc useResources:tableBuffers count:10 usage:MTLResourceUsageRead];
    // The materials' textures are reached through the argument buffer alone.
    if (ctx->alphaOn && !ctx->alphaTextures.empty())
        [enc useResources:ctx->alphaTextures.data() count:ctx->alphaTextures.size() usage:MTLResourceUsageRead];
    [enc dispatchThreads:grid threadsPerThreadgroup:MTLSizeMake(64, 1, 1)];
    [enc endEncoding];
    [cb commit];
    [cb waitUntilCompleted];
    if (!Completed(ctx, cb, what)) return 1;
    if (gpuMs) *gpuMs += (cb.GPUEndTime - cb.GPUStartTime) * 1e3;
    return 0;
}

HWX_EXPORT int hwx_lightlet_dense(HwxContext* ctx, const float* points, uint32_t count, const float* dirs, uint32_t dirCount,
                                  uint32_t words, uint32_t seed, float lift, float reach, uint32_t* bits, double* gpuMs) {
    @autoreleasepool {
        if (!ctx->tlas) return Fail(ctx, "lightlet bake before build");
        if (words * 32u < dirCount) return Fail(ctx, "lightlet bits narrower than the direction table");
        id<MTLDevice> device = ctx->device;
        id<MTLBuffer> dirBuffer = [device newBufferWithBytes:dirs length:uint64_t(std::max<uint32_t>(dirCount, 1)) * 16 options:MTLResourceStorageModeShared];
        const uint32_t chunk = std::max<uint32_t>(1u, (1u << 27) / std::max<uint32_t>(dirCount, 1));
        if (gpuMs) *gpuMs = 0.0;
        for (uint32_t first = 0; first < count; first += chunk) {
            uint32_t n = std::min(chunk, count - first);
            id<MTLBuffer> in = [device newBufferWithBytes:points + uint64_t(first) * 8 length:uint64_t(n) * 32 options:MTLResourceStorageModeShared];
            id<MTLBuffer> out = [device newBufferWithLength:uint64_t(n) * words * 4 options:MTLResourceStorageModeShared];
            memset(out.contents, 0, out.length);
            struct { uint32_t count, dirs, words, seed; float lift, reach; uint32_t radii, azimuths; } params = {n, dirCount, words, seed, lift, reach, 0, 0};
            if (LightletDispatch(ctx, ctx->lightletDensePso, ctx->lightletDenseTable, "blix-hwx lightlet dense", in, out, &params, sizeof(params),
                                 dirBuffer, nil, nil, MTLSizeMake(dirCount, n, 1), gpuMs)) return 1;
            memcpy(bits + uint64_t(first) * words, out.contents, uint64_t(n) * words * 4);
        }
        return 0;
    }
}

HWX_EXPORT int hwx_lightlet_rings(HwxContext* ctx, const float* points, uint32_t pointCount, const uint32_t* pairs, uint32_t pairCount,
                                  const float* nodeDirs, uint32_t nodeCount, const float* tanRadii, uint32_t radii, uint32_t azimuths,
                                  uint32_t seed, float lift, float reach, uint32_t* bits, double* gpuMs) {
    @autoreleasepool {
        if (!ctx->tlas) return Fail(ctx, "lightlet bake before build");
        if (radii * azimuths > 64u) return Fail(ctx, "lightlet rings hold at most 64 rays a pair");
        id<MTLDevice> device = ctx->device;
        id<MTLBuffer> in = [device newBufferWithBytes:points length:uint64_t(std::max<uint32_t>(pointCount, 1)) * 32 options:MTLResourceStorageModeShared];
        id<MTLBuffer> nodes = [device newBufferWithBytes:nodeDirs length:uint64_t(std::max<uint32_t>(nodeCount, 1)) * 16 options:MTLResourceStorageModeShared];
        id<MTLBuffer> tans = [device newBufferWithBytes:tanRadii length:std::max<uint32_t>(radii, 1) * 4 options:MTLResourceStorageModeShared];
        const uint32_t chunk = 1u << 21;
        if (gpuMs) *gpuMs = 0.0;
        for (uint32_t first = 0; first < pairCount; first += chunk) {
            uint32_t n = std::min(chunk, pairCount - first);
            id<MTLBuffer> list = [device newBufferWithBytes:pairs + uint64_t(first) * 2 length:uint64_t(n) * 8 options:MTLResourceStorageModeShared];
            id<MTLBuffer> out = [device newBufferWithLength:uint64_t(n) * 8 options:MTLResourceStorageModeShared];
            memset(out.contents, 0, out.length);
            struct { uint32_t count, dirs, words, seed; float lift, reach; uint32_t radii, azimuths; } params = {n, 0, 2, seed, lift, reach, radii, azimuths};
            if (LightletDispatch(ctx, ctx->lightletRingsPso, ctx->lightletRingsTable, "blix-hwx lightlet rings", in, out, &params, sizeof(params),
                                 nodes, list, tans, MTLSizeMake(radii * azimuths, n, 1), gpuMs)) return 1;
            memcpy(bits + uint64_t(first) * 2, out.contents, uint64_t(n) * 8);
        }
        return 0;
    }
}

// The device's GPU memory: what Metal has allocated in all (every client of the device, Vulkan's included), and the
// working set it recommends staying under before it pages.
HWX_EXPORT void hwx_memory(HwxContext* ctx, uint64_t* allocated, uint64_t* recommended) {
    *allocated = ctx->device.currentAllocatedSize;
    *recommended = ctx->device.recommendedMaxWorkingSetSize;
}

// Shadow rays from host memory toward one direction (blix_shadow, the coverage coin): `count` origins, float4 each
// (position, tMax; 0 or less: no ray), one byte each out (1: something in the way). In chunks, each waited for;
// callable off the frame's thread, as hwx_buried.
HWX_EXPORT int hwx_shadow_host(HwxContext* ctx, const float* origins, uint32_t count, const float* direction, uint32_t seedBase,
                               uint8_t* occluded, double* gpuMs) {
    @autoreleasepool {
        if (!ctx->tlas) return Fail(ctx, "shadow trace before build");
        id<MTLDevice> device = ctx->device;
        const uint32_t chunk = 1u << 21;
        if (gpuMs) *gpuMs = 0.0;
        for (uint32_t first = 0; first < count; first += chunk) {
            uint32_t n = std::min(chunk, count - first);
            id<MTLBuffer> in = [device newBufferWithBytes:origins + uint64_t(first) * 4 length:uint64_t(n) * 16 options:MTLResourceStorageModeShared];
            id<MTLBuffer> out = [device newBufferWithLength:uint64_t(n) * 4 options:MTLResourceStorageModeShared];
            id<MTLCommandBuffer> cb = [ctx->queue commandBuffer];
            cb.label = @"blix-hwx host shadow";
            id<MTLComputeCommandEncoder> enc = [cb computeCommandEncoder];
            [enc setComputePipelineState:ctx->shadowPso];
            [enc setBuffer:in offset:0 atIndex:0];
            [enc setBuffer:out offset:0 atIndex:1];
            [enc setAccelerationStructure:ctx->tlas atBufferIndex:2];
            [enc setIntersectionFunctionTable:ctx->shadowTable atBufferIndex:3];
            struct { float direction[4]; uint32_t count, seedBase, sky, pad1; } params =
                {{direction[0], direction[1], direction[2], 0.0f}, n, seedBase + first, 0u, 0u};
            [enc setBytes:&params length:sizeof(params) atIndex:4];
            [enc setBuffer:in offset:0 atIndex:5];
            [enc setBuffer:in offset:0 atIndex:6];   // the limit's and directions' slots, unread (flags 0)
            [enc setBuffer:in offset:0 atIndex:7];
            [enc useResources:ctx->blas.data() count:ctx->blas.size() usage:MTLResourceUsageRead];
            id<MTLResource> tableBuffers[] = {ctx->primRows, ctx->entryRows, ctx->instances, ctx->triangles, ctx->owners, ctx->surfaces,
                                              ctx->alphaOff, ctx->rowMaterial, ctx->materials, ctx->uvs};
            [enc useResources:tableBuffers count:10 usage:MTLResourceUsageRead];
            [enc dispatchThreads:MTLSizeMake(n, 1, 1) threadsPerThreadgroup:MTLSizeMake(64, 1, 1)];
            [enc endEncoding];
            [cb commit];
            [cb waitUntilCompleted];
            if (!Completed(ctx, cb, "host shadow trace")) return 1;
            if (gpuMs) *gpuMs += (cb.GPUEndTime - cb.GPUStartTime) * 1e3;
            const uint32_t* words = (const uint32_t*)out.contents;
            for (uint32_t k = 0; k < n; ++k) occluded[first + k] = (words[k] & 1u) ? 1 : 0;
        }
        return 0;
    }
}

// From now on `ctx` submits on `from`'s command queue: two tracers whose in-frame passes wait on and signal one frame
// timeline stay on one serial queue, in the order the frame encodes them (a second queue's waits and signals
// interleaved with the first's lost the device to the GPU watchdog).
HWX_EXPORT void hwx_share_queue(HwxContext* ctx, HwxContext* from) {
    if (ctx && from && ctx != from) ctx->queue = from->queue;
}

// From now on `ctx` submits on `queue` (an id<MTLCommandQueue>, retained here): the Vulkan device's own queue
// (VK_EXT_metal_objects), so the frame's Vulkan work and this tracer's run in commit order on one queue rather than
// synchronising across two. NULL: back to its own queue.
HWX_EXPORT void hwx_use_queue(HwxContext* ctx, void* queue) {
    if (ctx) ctx->queue = queue ? (__bridge id<MTLCommandQueue>)queue : ctx->ownQueue;
}

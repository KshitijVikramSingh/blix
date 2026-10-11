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
struct BlixAlphaParams { uint enabled; uint pad0; uint pad1; uint pad2; };

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
            float a = materials[m].albedo.sample(s, uv, level(0.0f)).a * materials[m].alphaFactor;
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

struct Params { uint count; uint seedBase; uint closestOnly; uint pad; };

struct ShadowParams { float4 direction; uint count; uint seedBase; uint sky; uint pad1; };

// Sun shadows: one ray per origin, every one toward the same direction. An origin is (position, tMax); tMax <= 0 means
// no ray, answered unoccluded. Writes 1 where anything, met as coverage (or the alpha test, by the table bound) allows,
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
                        uint i [[thread_position_in_grid]])
{
    if (i >= p.count) return;
    float4 o = origins[i];
    uint word = 0u;
    intersector<triangle_data, instancing> isect;
    isect.assume_geometry_type(geometry_type::triangle);
    isect.accept_any_intersection(true);
    if (o.w > 0.0f) {
        uint seed = p.seedBase + i;
        auto res = isect.intersect(ray(o.xyz, p.direction.xyz, 0.0f, o.w), tlas, 0xFFu, table, seed);
        if (res.type != intersection_type::none) word |= 1u;
    }
    if (p.sky != 0u) {
        float4 s = skyRays[i * 2u];
        if (s.w > 0.0f) {
            uint seed = p.seedBase + i + 0x68E31DA4u;
            auto res = isect.intersect(ray(s.xyz, skyRays[i * 2u + 1u].xyz, 0.0f, s.w), tlas, 0xFFu, table, seed);
            if (res.type != intersection_type::none) word |= 2u;
        }
    }
    occluded[i] = word;
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
                       uint i [[thread_position_in_grid]])
{
    if (i >= p.count) return;
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
)";

struct HwxContext {
    id<MTLDevice> device;
    id<MTLCommandQueue> queue;
    id<MTLComputePipelineState> pso;
    id<MTLIntersectionFunctionTable> table;
    id<MTLComputePipelineState> shadowPso;
    id<MTLIntersectionFunctionTable> shadowTable;       // the coverage coin, as the software batch
    id<MTLIntersectionFunctionTable> shadowAlphaTable;  // the alpha test (hwx_set_alpha), asked for per dispatch
    id<MTLCommandBuffer> lastShadow;  // waited on at destroy: its completion handler reads this context
    id<MTLCommandBuffer> lastTrace;   // likewise, for hwx_encode_trace
    std::atomic<uint64_t> shadowDispatches{0}, shadowGpuNs{0}, shadowLastGpuNs{0}, shadowFailures{0};
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
        MTLIntersectionFunctionTableDescriptor* alphaTd = [MTLIntersectionFunctionTableDescriptor new];
        alphaTd.functionCount = 1;
        id<MTLIntersectionFunctionTable> shadowAlphaTable = [shadowPso newIntersectionFunctionTableWithDescriptor:alphaTd];
        [shadowAlphaTable setFunction:[shadowPso functionHandleWithFunction:coverage] atIndex:0];

        auto* ctx = new HwxContext();
        ctx->device = device;
        ctx->queue = [device newCommandQueue];
        ctx->queue.label = @"blix-hwx";
        ctx->pso = pso;
        ctx->table = table;
        ctx->shadowPso = shadowPso;
        ctx->shadowTable = shadowTable;
        ctx->shadowAlphaTable = shadowAlphaTable;
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
        for (id<MTLIntersectionFunctionTable> table : {ctx->table, ctx->shadowTable, ctx->shadowAlphaTable}) {
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
HWX_EXPORT int hwx_encode_shadow(HwxContext* ctx, void* sharedEvent, uint64_t waitValue, uint64_t signalValue,
                                 void* origins, uint64_t originsOffset, void* occluded, uint64_t occludedOffset,
                                 uint32_t count, const float* direction, uint32_t seedBase, void* skyRays, uint32_t alphaTest) {
    @autoreleasepool {
        id<MTLSharedEvent> event = (__bridge id<MTLSharedEvent>)sharedEvent;
        id<MTLBuffer> originBuffer = (__bridge id<MTLBuffer>)origins;
        id<MTLBuffer> occludedBuffer = (__bridge id<MTLBuffer>)occluded;
        id<MTLBuffer> skyBuffer = (__bridge id<MTLBuffer>)skyRays;
        id<MTLCommandBuffer> cb = [ctx->queue commandBuffer];
        cb.label = @"blix-hwx shadow";
        [cb encodeWaitForEvent:event value:waitValue];
        int result = 0;
        if (!ctx->tlas) result = Fail(ctx, "shadow trace before build");
        else if (!originBuffer || originBuffer.length < originsOffset + uint64_t(count) * 16) result = Fail(ctx, "the origin buffer is smaller than its count");
        else if (!occludedBuffer || occludedBuffer.length < occludedOffset + uint64_t(count) * 4) result = Fail(ctx, "the occlusion buffer is smaller than its count");
        else if (skyBuffer && skyBuffer.length < uint64_t(count) * 32) result = Fail(ctx, "the sky ray buffer is smaller than its count");
        if (result == 0 && count > 0) {
            id<MTLComputeCommandEncoder> enc = [cb computeCommandEncoder];
            [enc setComputePipelineState:ctx->shadowPso];
            [enc setBuffer:originBuffer offset:originsOffset atIndex:0];
            [enc setBuffer:occludedBuffer offset:occludedOffset atIndex:1];
            [enc setAccelerationStructure:ctx->tlas atBufferIndex:2];
            // The alpha test only where asked and set up; otherwise the coin, as the software batch meets cut-outs.
            bool alpha = alphaTest != 0 && ctx->alphaOn != nil;
            [enc setIntersectionFunctionTable:alpha ? ctx->shadowAlphaTable : ctx->shadowTable atBufferIndex:3];
            struct { float direction[4]; uint32_t count, seedBase, sky, pad1; } params = {
                {direction[0], direction[1], direction[2], 0.0f}, count, seedBase, skyBuffer ? 1u : 0u, 0};
            [enc setBytes:&params length:sizeof(params) atIndex:4];
            [enc setBuffer:skyBuffer ? skyBuffer : originBuffer offset:skyBuffer ? 0 : originsOffset atIndex:5];
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
        [cb addCompletedHandler:^(id<MTLCommandBuffer> done) {
            if (skipped || done.status != MTLCommandBufferStatusCompleted) { c->shadowFailures++; return; }
            uint64_t ns = uint64_t((done.GPUEndTime - done.GPUStartTime) * 1e9);
            c->shadowLastGpuNs = ns;
            c->shadowGpuNs += ns;
            c->shadowDispatches++;
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
HWX_EXPORT int hwx_encode_trace(HwxContext* ctx, void* sharedEvent, uint64_t waitValue, uint64_t signalValue,
                                void* rays, uint64_t raysOffset, void* hits, uint64_t hitsOffset,
                                uint32_t count, uint32_t seedBase) {
    @autoreleasepool {
        id<MTLSharedEvent> event = (__bridge id<MTLSharedEvent>)sharedEvent;
        id<MTLBuffer> rayBuffer = (__bridge id<MTLBuffer>)rays;
        id<MTLBuffer> hitBuffer = (__bridge id<MTLBuffer>)hits;
        id<MTLCommandBuffer> cb = [ctx->queue commandBuffer];
        cb.label = @"blix-hwx trace";
        [cb encodeWaitForEvent:event value:waitValue];
        int result = 0;
        if (!ctx->tlas) result = Fail(ctx, "trace before build");
        else if (!rayBuffer || rayBuffer.length < raysOffset + uint64_t(count) * 32) result = Fail(ctx, "the ray buffer is smaller than its count");
        else if (!hitBuffer || hitBuffer.length < hitsOffset + uint64_t(count) * 48) result = Fail(ctx, "the hit buffer is smaller than its count");
        if (result == 0 && count > 0) {
            id<MTLComputeCommandEncoder> enc = [cb computeCommandEncoder];
            [enc setComputePipelineState:ctx->pso];
            [enc setBuffer:rayBuffer offset:raysOffset atIndex:0];
            [enc setBuffer:hitBuffer offset:hitsOffset atIndex:1];
            [enc setAccelerationStructure:ctx->tlas atBufferIndex:2];
            [enc setIntersectionFunctionTable:ctx->table atBufferIndex:3];
            uint32_t params[4] = {count, seedBase, 1u, 0u};
            [enc setBytes:params length:sizeof(params) atIndex:4];
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

// What the shadow dispatches have cost so far, from their command buffers' GPU times.
HWX_EXPORT void hwx_shadow_stats(HwxContext* ctx, uint64_t* dispatches, uint64_t* totalGpuNs, uint64_t* lastGpuNs, uint64_t* failures) {
    *dispatches = ctx->shadowDispatches;
    *totalGpuNs = ctx->shadowGpuNs;
    *lastGpuNs = ctx->shadowLastGpuNs;
    *failures = ctx->shadowFailures;
}

// The shadow trace's alpha test: per material its albedo texture (an MTLTexture exported from the device), alpha cutoff
// and alpha factor; per packed triangle row its material (0xFFFFFFFF: none, the coverage coin decides); and the scene's
// texture coordinates, one float2 per position (an exported buffer). From now on the shadow pass meets a partly covered
// triangle where its material's alpha passes at the point hit; the ray check keeps the coin.
HWX_EXPORT int hwx_set_alpha(HwxContext* ctx, void* const* textures, const float* cutoffs, const float* alphaFactors,
                             uint32_t materialCount, const uint32_t* rowMaterial, uint32_t rowCount, void* uvs) {
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
        uint32_t on[4] = {1, 0, 0, 0};
        ctx->alphaOn = [device newBufferWithBytes:on length:sizeof(on) options:MTLResourceStorageModeShared];
        for (id<MTLIntersectionFunctionTable> table : {ctx->table, ctx->shadowTable, ctx->shadowAlphaTable}) {
            [table setBuffer:table == ctx->shadowAlphaTable ? ctx->alphaOn : ctx->alphaOff offset:0 atIndex:6];
            [table setBuffer:ctx->rowMaterial offset:0 atIndex:7];
            [table setBuffer:ctx->materials offset:0 atIndex:8];
            [table setBuffer:ctx->uvs offset:0 atIndex:9];
        }
        return 0;
    }
}

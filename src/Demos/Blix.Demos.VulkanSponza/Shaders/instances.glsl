// The scene's placements, on set 3: one draw per unique primitive and LOD level, instanced over the
// placements of it that this pass can see.
//
//   binding 0  every placement's world matrix, written once at load (the static half).
//   binding 1  this frame's visible placement indices, every pass's list end to end; a sub-draw's
//              firstInstance is where its run starts, so gl_InstanceIndex lands inside it.
//   binding 2  unused (was each row's material, screen probes' identity before the SurfaceKey; kept free so
//              the bindings after it do not move).
//   binding 3  every placement's SurfaceKey, by the same row: one per (instance, source primitive), shared by
//              every cooked chunk of one surface (stage 4e).
//   binding 4  every placement's previous world matrix, by the same row: where it stood last frame (equal to
//              binding 0 for anything that has not moved), for the pre-pass's velocity.
//   binding 5  every VERTEX's lightmap texel, in the shared vertex buffer's order (--lightmap; zero where a
//              primitive has none): read by gl_VertexIndex, which includes the draw's base vertex.
//
// Every vertex shader that draws scene geometry declares exactly this, so the one set-3 material
// binds to all of their pipelines (an identical set layout is what makes that legal).
#ifndef SPONZA_INSTANCES_GLSL
#define SPONZA_INSTANCES_GLSL

layout(std430, set = 3, binding = 0) readonly buffer SceneTransforms { mat4 transforms[]; };
layout(std430, set = 3, binding = 1) readonly buffer SceneVisible { uint visible[]; };

layout(std430, set = 3, binding = 3) readonly buffer SceneSurfaceKeys { uint surfaceKeyOf[]; };
layout(std430, set = 3, binding = 4) readonly buffer ScenePreviousTransforms { mat4 previousTransforms[]; };

layout(std430, set = 3, binding = 5) readonly buffer SceneLightmapTexels { vec2 lightmapTexelOf[]; };

mat4 instanceWorld() { return transforms[visible[gl_InstanceIndex]]; }
vec2 vertexLightmapTexel() { return lightmapTexelOf[gl_VertexIndex]; }
uint instanceSurfaceKey() { return surfaceKeyOf[visible[gl_InstanceIndex]]; }
mat4 instancePreviousWorld() { return previousTransforms[visible[gl_InstanceIndex]]; }

#endif

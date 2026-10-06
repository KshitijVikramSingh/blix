// The scene's placements, on set 3: one draw per unique primitive and LOD level, instanced over the
// placements of it that this pass can see.
//
//   binding 0  every placement's world matrix, written once at load (the static half).
//   binding 1  this frame's visible placement indices, every pass's list end to end; a sub-draw's
//              firstInstance is where its run starts, so gl_InstanceIndex lands inside it.
//   binding 2  every placement's material id, by the same row (static): screen probes' interim identity, until
//              they read the SurfaceKey target (stage 4e-v).
//   binding 3  every placement's SurfaceKey, by the same row: one per (instance, source primitive), shared by
//              every cooked chunk of one surface (stage 4e).
//   binding 4  every placement's previous world matrix, by the same row: where it stood last frame (equal to
//              binding 0 for anything that has not moved), for the pre-pass's velocity.
//
// Every vertex shader that draws scene geometry declares exactly this, so the one set-3 material
// binds to all of their pipelines (an identical set layout is what makes that legal).
#ifndef SPONZA_INSTANCES_GLSL
#define SPONZA_INSTANCES_GLSL

layout(std430, set = 3, binding = 0) readonly buffer SceneTransforms { mat4 transforms[]; };
layout(std430, set = 3, binding = 1) readonly buffer SceneVisible { uint visible[]; };

layout(std430, set = 3, binding = 2) readonly buffer SceneMaterials { uint materialOf[]; };
layout(std430, set = 3, binding = 3) readonly buffer SceneSurfaceKeys { uint surfaceKeyOf[]; };
layout(std430, set = 3, binding = 4) readonly buffer ScenePreviousTransforms { mat4 previousTransforms[]; };

mat4 instanceWorld() { return transforms[visible[gl_InstanceIndex]]; }
uint instanceMaterial() { return materialOf[visible[gl_InstanceIndex]]; }
uint instanceSurfaceKey() { return surfaceKeyOf[visible[gl_InstanceIndex]]; }
mat4 instancePreviousWorld() { return previousTransforms[visible[gl_InstanceIndex]]; }

#endif

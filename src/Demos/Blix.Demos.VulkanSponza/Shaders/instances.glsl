// The scene's placements, on set 3: one draw per unique primitive and LOD level, instanced over the
// placements of it that this pass can see.
//
//   binding 0  every placement's world matrix, written once at load (the static half).
//   binding 1  this frame's visible placement indices, every pass's list end to end; a sub-draw's
//              firstInstance is where its run starts, so gl_InstanceIndex lands inside it.
//   binding 2  every placement's material id, by the same row (static): a surface's identity across the many
//              placements the cook splits one wall into.
//
// Every vertex shader that draws scene geometry declares exactly this, so the one set-3 material
// binds to all of their pipelines (an identical set layout is what makes that legal).
#ifndef SPONZA_INSTANCES_GLSL
#define SPONZA_INSTANCES_GLSL

layout(std430, set = 3, binding = 0) readonly buffer SceneTransforms { mat4 transforms[]; };
layout(std430, set = 3, binding = 1) readonly buffer SceneVisible { uint visible[]; };

layout(std430, set = 3, binding = 2) readonly buffer SceneMaterials { uint materialOf[]; };

mat4 instanceWorld() { return transforms[visible[gl_InstanceIndex]]; }
uint instanceMaterial() { return materialOf[visible[gl_InstanceIndex]]; }

#endif

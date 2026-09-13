#version 450

// Instanced depth-only shadow caster. Reads each instance's model from the InstanceBuffer
// (set 3) and transforms it by the sun's shadow view-projection. No colour output — the
// depth attachment captures gl_Position.z/w.
//
// <b>It leans in the wind, because the thing it is casting for does.</b> This shader used to transform the
// instance and stop, while world.vert displaced the same geometry downwind — so a swaying tree had a still
// shadow. It reads as the shadow being detached from the trunk, and it shimmers, because the tree moves
// against a shadow that does not. The lean is shared rather than copied: see Shaders/lean.glsl.

layout(location = 0) in vec3 inPosition;
layout(location = 1) in vec3 inNormal;

struct Instance {
    mat4 model;
    vec4 tint;
};

layout(set = 3, binding = 0, std430) readonly buffer Instances {
    Instance instances[];
};

layout(push_constant) uniform Push {
    mat4 uShadowViewProj;
    // The same four numbers world.vert gets, and they have to be the same four: a caster leaning on a
    // different clock or a different bearing from its receiver is worse than one that does not lean at all,
    // because the error moves.
    vec4 uWind;
};

// The material classes — Shaders/materials.glsl. Needed for isPlant, which is what decides whether a thing
// bends at all; the class rides in the instance's tint alpha.
#include "materials.glsl"
// The wind lean, shared with world.vert — Shaders/lean.glsl.
#include "lean.glsl"

void main() {
    Instance inst = instances[gl_InstanceIndex];
    vec4 world = inst.model * vec4(inPosition, 1.0);
    // The instance's own origin is the last column, since the matrix arrives transposed. That is where the
    // plant is rooted, whatever the mesh's own pivot happens to be — same reading world.vert makes.
    world.xyz = blix_rts_lean(world.xyz, inst.model[3].y, inst.tint.a, uWind);
    gl_Position = uShadowViewProj * world;
}

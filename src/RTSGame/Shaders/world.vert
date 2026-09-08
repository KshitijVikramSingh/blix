#version 450

// Instanced world vertex shader. Per-instance transform/tint from the InstanceBuffer
// (set 3). Push = view-projection + camera position + sun direction + the sun's shadow
// view-projection + fog range, shared with the fragment stage. Matrices arrive as raw
// row-major System.Numerics bytes read column-major in GLSL, which is a transpose, so
// M * v matches the engine's row-vector product.

layout(location = 0) in vec3 inPosition;
layout(location = 1) in vec3 inNormal;
// x is how much of this ground class covers this corner, for terrain; for everything else these are the
// asset's own texture coordinates, which nothing reads. See the note on meshLayout in RtsGameLoop: the two
// floats were already in every vertex buffer, unbound.
layout(location = 2) in vec2 inGround;

struct Instance {
    mat4 model;
    vec4 tint;
};

layout(set = 3, binding = 0, std430) readonly buffer Instances {
    Instance instances[];
};

// The push block, shared with world.frag and the skinned pair — Shaders/world_push.glsl.
#include "world_push.glsl"

layout(location = 0) out vec3 vNormal;
layout(location = 1) out vec4 vTint;
layout(location = 2) out vec3 vWorldPos;
layout(location = 3) out vec2 vGround;

// <b>The light-space position used to be computed here, and cascades are why it moved.</b> Offsetting along
// the normal and projecting in the vertex stage was cheaper and smoother — the offset is a property of the
// surface, so interpolating an already-offset position beats offsetting per pixel. But the offset is scaled
// by how many metres a shadow texel covers, and with three boxes of three widths on three maps that is three
// different numbers; a vertex cannot know which one applies, because which cascade a fragment falls in is a
// fact about the fragment. So the fragment stage now does both, from vWorldPos and vNormal, which it already
// had. What that costs is a normalize and a matrix multiply per pixel; what it buys is a far cascade whose
// bias is scaled to its own texels instead of to the near cascade's.
// The material classes — Shaders/materials.glsl, shared with the fragment stage.
#include "materials.glsl"
// The wind lean, shared with shadow_caster.vert so a leaning tree and its shadow agree on where
// the tree is — Shaders/lean.glsl.
#include "lean.glsl"


void main() {
    Instance inst = instances[gl_InstanceIndex];
    vec4 world = inst.model * vec4(inPosition, 1.0);
    // The instance's own origin is the last column, since the matrix arrives transposed — see the note at
    // the top of this file. That is where the plant is rooted, whatever the mesh's own pivot happens to be.
    world.xyz = blix_rts_lean(world.xyz, inst.model[3].y, inst.tint.a, uWind);
    gl_Position = uViewProjection * world;
    vec3 normal = normalize(mat3(inst.model) * inNormal);
    vNormal = normal;
    vTint = inst.tint;
    vWorldPos = world.xyz;
    vGround = inGround;
}

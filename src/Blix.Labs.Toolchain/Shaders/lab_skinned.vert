#version 450

// lab_lit.vert with a bone palette in front of it. Pairs with lab_lit.frag unchanged —
// the fragment stage never learns that the vertices moved, which is the whole reason
// skinning is a vertex-stage concern and not a material one.

layout(location = 0) in vec3 aPosition;
layout(location = 1) in vec3 aNormal;
layout(location = 2) in vec2 aTexCoord;
layout(location = 3) in vec4 aBoneIndices;
layout(location = 4) in vec4 aBoneWeights;
layout(location = 5) in vec4 aTangent;      // declared so the Skin4Tangent layout binds; unused here

layout(set = 0, binding = 0) uniform Frame {
    mat4 uViewProjection;
    mat4 uSunViewProjection;
    vec4 uCameraPosition;
    vec4 uSunDirection;
    vec4 uSunColour;
};

// The bone palette, at set 3 — the engine's per-draw set, written once per frame slot
// through a MaterialBindings rather than inline like a texture. Set 1 is where the lit
// pass's inline textures land and set 2 is the per-material set; a palette is neither.
//
// FIXED SIZE, and that is not a style choice. spirv-cross reflects a runtime-sized
// `mat4 m[]` as block_size 0 with an array count of 0, so the sidecar the lab builds its
// binding model from would describe a zero-byte buffer and the material would allocate
// one. A literal bound reflects as 8192 bytes with a 64-byte element stride, which is
// what MaterialBindings needs to size the buffer — and a short write is legal, so a
// 41-bone rig uploads 2,624 bytes and the tail is simply never read.
layout(std430, set = 3, binding = 0) readonly buffer Bones {
    mat4 m[128];
} bones;

layout(push_constant) uniform Push {
    mat4 uModel;
    vec4 uBaseColour;
    vec4 uMaterial;       // x = metallic, y = roughness
};

layout(location = 0) out vec3 vWorld;
layout(location = 1) out vec3 vNormal;
layout(location = 2) out vec2 vUv;

void main()
{
    // The linear-blend skinning sum. Weights come normalised out of the importer; a rig
    // whose weights do not sum to one shrinks toward the origin, which is a thing the lab's
    // skeleton overlay makes visible (mesh drifts, bones do not).
    mat4 skin = bones.m[int(aBoneIndices.x)] * aBoneWeights.x
              + bones.m[int(aBoneIndices.y)] * aBoneWeights.y
              + bones.m[int(aBoneIndices.z)] * aBoneWeights.z
              + bones.m[int(aBoneIndices.w)] * aBoneWeights.w;

    vec4 posed = skin * vec4(aPosition, 1.0);
    vec4 world = uModel * posed;
    vWorld = world.xyz;

    // Through the skin matrix as well as the model — a bone's rotation turns its normals.
    // Uniform scale only, in this lab as in the unskinned path, so the upper 3x3 suffices.
    vNormal = normalize(mat3(uModel) * (mat3(skin) * aNormal));

    vUv = aTexCoord;
    gl_Position = uViewProjection * world;
}

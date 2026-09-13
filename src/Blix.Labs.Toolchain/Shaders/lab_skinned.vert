#version 450

// lab_lit.vert with a bone palette in front of it, and N of them. Pairs with lab_lit.frag
// unchanged — the fragment stage never learns that the vertices moved, which is the whole
// reason skinning is a vertex-stage concern and not a material one.
//
// <b>Always instanced, even for one body.</b> There is no separate single-rig shader: drawing
// one rig is drawing an instance count of one, through this same line of code. A second,
// simpler path for the common case is how "it works with one and breaks with three" becomes
// possible, and the lab exists to make the three-body case visible rather than to special-case
// around it.

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

// The palettes, at set 3 — the engine's per-draw set, written once per frame slot through a
// MaterialBindings rather than inline like a texture. Set 1 is where the lit pass's inline
// textures land and set 2 is the per-material set; a palette is neither.
//
// FIXED SIZE, and that is not a style choice. spirv-cross reflects a runtime-sized
// `mat4 m[]` as block_size 0 with an array count of 0, so the sidecar the lab builds its
// binding model from would describe a zero-byte buffer and the material would allocate one.
// A literal bound reflects with a real size and a 64-byte element stride, which is what
// MaterialBindings needs — and a short write is legal, so three 41-bone rigs upload 7,872
// bytes and the tail is simply never read.
//
// 1024 = LabRig.MaxBones (128) x LabRig.MaxInstances (8). A literal, because the build's
// SPIR-V target does not pass -D — so the number does live in two files, and the probe is what
// makes that safe: it reads the reflected block size back and exits non-zero the moment it
// stops matching the C# constants. Bulwark hard-codes `#define BONE_COUNT 15` in two shaders
// and throws at load if the asset disagrees; the throw exists because nothing checks earlier.
layout(std430, set = 3, binding = 0) readonly buffer Bones {
    mat4 m[1024];
} bones;

layout(push_constant) uniform Push {
    mat4 uModel;
    vec4 uBaseColour;
    vec4 uMaterial;       // x = metallic, y = roughness, z = bones per instance
};

layout(location = 0) out vec3 vWorld;
layout(location = 1) out vec3 vNormal;
layout(location = 2) out vec2 vUv;

void main()
{
    // <b>The stride comes from the draw, not from a #define.</b> uMaterial.z carries the RIG's
    // bone count, so a shader compiled once serves a 15-bone robot and a 41-bone rogue, and the
    // packing on the CPU cannot disagree with the reading here — the number travels with the data.
    // That is the one thing this does differently from the two consumers it copies from.
    //
    // It rides in uMaterial's spare .z because the push block has to stay byte-identical to the
    // one lab_lit.frag declares. Give this stage a wider block and the two stages reflect
    // different push ranges; the emit path sums range sizes and would then expect 208 bytes for a
    // 112-byte payload and refuse the draw. The alternative — a second fragment shader existing
    // for one float — is worse than a documented use of a slot the frag ignores.
    int base = gl_InstanceIndex * int(uMaterial.z);

    // The linear-blend skinning sum. Weights come normalised out of the importer; a rig whose
    // weights do not sum to one shrinks toward the origin, which is a thing the lab's skeleton
    // overlay makes visible (mesh drifts, bones do not).
    mat4 skin = bones.m[base + int(aBoneIndices.x)] * aBoneWeights.x
              + bones.m[base + int(aBoneIndices.y)] * aBoneWeights.y
              + bones.m[base + int(aBoneIndices.z)] * aBoneWeights.z
              + bones.m[base + int(aBoneIndices.w)] * aBoneWeights.w;

    // <b>uModel is identity on every instanced draw, and the multiply stays.</b> Each instance's
    // placement is baked into its palette on the CPU (Bulwark's shape — a world-space palette and
    // no instance buffer), because a per-draw push constant cannot vary per instance and there is
    // no compose order that lets a shared uModel sit between the skin and a per-instance placement.
    //
    // Kept rather than deleted so this stage still reflects the whole 96-byte push block that
    // lab_lit.frag declares. Drop it and glslc strips uModel from the vertex stage's reflection,
    // the two stages report different push ranges, and the emit path — which sums range sizes —
    // expects 128 bytes for a 96-byte payload and refuses the draw. One dead multiply per vertex
    // against a binding model that stays honest.
    vec4 posed = skin * vec4(aPosition, 1.0);
    vec4 world = uModel * posed;
    vWorld = world.xyz;

    // Through the skin matrix as well as the model — a bone's rotation turns its normals.
    // Uniform scale only, in this lab as in the unskinned path, so the upper 3x3 suffices.
    vNormal = normalize(mat3(uModel) * (mat3(skin) * aNormal));

    vUv = aTexCoord;
    gl_Position = uViewProjection * world;
}

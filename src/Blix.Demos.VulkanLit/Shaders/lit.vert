#version 450

// Set 0 = per-frame: camera + sun + ambient + sun shadow VP. std140-packed:
//   uViewProjection   mat4  offset 0   (size 64)
//   uSunDirection     vec3  offset 64  (size 12)
//   uSunIntensity     float offset 76  (size 4, packs into vec3 slot)
//   uAmbientColor     vec3  offset 80  (size 12)
//   uAmbientIntensity float offset 92  (size 4, packs into vec3 slot)
//   uSunShadowVP      mat4  offset 96  (size 64)
// TotalSize 160.
layout(set = 0, binding = 0) uniform Frame {
    mat4 uViewProjection;
    vec3 uSunDirection;
    float uSunIntensity;
    vec3 uAmbientColor;
    float uAmbientIntensity;
    mat4 uSunShadowVP;
} frame;

// Per-draw model matrix via push constants.
layout(push_constant) uniform PushConstants {
    mat4 uModel;
} pc;

layout(location = 0) in vec3 inPosition;
layout(location = 1) in vec3 inNormal;
layout(location = 2) in vec2 inUv;

layout(location = 0) out vec3 vNormal;
layout(location = 1) out vec2 vUv;
layout(location = 2) out vec4 vShadowCoord;

void main() {
    vec4 world = pc.uModel * vec4(inPosition, 1.0);
    gl_Position = frame.uViewProjection * world;

    // Transform normal via the upper-left 3x3 of the model matrix. The
    // lit demo only uses uniform scales + rotations, so this is correct
    // without inverse-transpose.
    vNormal = mat3(pc.uModel) * inNormal;
    vUv = inUv;

    // Light-space clip-space position. Interpolated to the fragment and
    // divided by w there to get NDC, then mapped to [0,1] for shadow-map
    // sampling.
    vShadowCoord = frame.uSunShadowVP * world;
}

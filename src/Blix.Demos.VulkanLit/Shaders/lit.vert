#version 450

// Per-frame UBO. std140, vec4-packed for the light blocks to avoid the
// vec3+float padding fragility. Layout (offsets in bytes):
//   uViewProjection    mat4  0    (64)
//   uSunDirection      vec3  64   (12)
//   uSunIntensity      float 76   (4)
//   uAmbientColor      vec3  80   (12)
//   uAmbientIntensity  float 92   (4)
//   uSunShadowVP       mat4  96   (64)
//   uSpotViewProj      mat4  160  (64)
//   uSpotPosRange      vec4  224  (xyz = position, w = range)
//   uSpotDirCosInner   vec4  240  (xyz = direction, w = cos(inner cone))
//   uSpotColorCosOuter vec4  256  (xyz = color*intensity, w = cos(outer cone))
// TotalSize 272.
layout(set = 0, binding = 0) uniform Frame {
    mat4 uViewProjection;
    vec3 uSunDirection;
    float uSunIntensity;
    vec3 uAmbientColor;
    float uAmbientIntensity;
    mat4 uSunShadowVP;
    mat4 uSpot0ViewProj;
    vec4 uSpot0PosRange;
    vec4 uSpot0DirCosInner;
    vec4 uSpot0ColorCosOuter;
    mat4 uSpot1ViewProj;
    vec4 uSpot1PosRange;
    vec4 uSpot1DirCosInner;
    vec4 uSpot1ColorCosOuter;
    vec4 uPointPosFar;
    vec4 uPointColorRange;
    vec4 uLightEnable;
} frame;

layout(push_constant) uniform PushConstants {
    mat4 uModel;
} pc;

layout(location = 0) in vec3 inPosition;
layout(location = 1) in vec3 inNormal;
layout(location = 2) in vec2 inUv;

layout(location = 0) out vec3 vNormal;
layout(location = 1) out vec2 vUv;
layout(location = 2) out vec4 vSunShadowCoord;
layout(location = 3) out vec3 vWorldPos;

void main() {
    vec4 world = pc.uModel * vec4(inPosition, 1.0);
    gl_Position = frame.uViewProjection * world;

    vNormal = mat3(pc.uModel) * inNormal;
    vUv = inUv;
    vWorldPos = world.xyz;
    vSunShadowCoord = frame.uSunShadowVP * world;
}

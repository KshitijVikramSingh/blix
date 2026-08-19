#version 450

// Instanced world vertex shader. Per-instance transform/tint from the InstanceBuffer
// (set 3). Push = view-projection + camera position + sun direction + the sun's shadow
// view-projection + fog range, shared with the fragment stage. Matrices arrive as raw
// row-major System.Numerics bytes read column-major in GLSL, which is a transpose, so
// M * v matches the engine's row-vector product.

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
    mat4 uViewProjection;
    vec4 uCamPos;
    vec4 uSunDir;
    mat4 uSunShadowVP;
    vec4 uFog;      // x = start (m), y = end (m), z = strength
    vec4 uShadow;   // x = texel as a fraction of the map, y = map size (m), z = penumbra, w = offset
    vec4 uLight;    // x = sun intensity, y = ambient scale, z = terminator wrap
};

layout(location = 0) out vec3 vNormal;
layout(location = 1) out vec4 vTint;
layout(location = 2) out vec3 vWorldPos;
layout(location = 3) out vec4 vSunShadowCoord;

// The normal-offset technique — src/Blix.Shaders/shadow.glsl. Applied here rather than in
// the fragment stage on purpose: the offset is a property of the surface, so interpolating
// the already-offset light-space position across a triangle is both cheaper and smoother
// than offsetting per pixel.
#include "shadow.glsl"

void main() {
    Instance inst = instances[gl_InstanceIndex];
    vec4 world = inst.model * vec4(inPosition, 1.0);
    gl_Position = uViewProjection * world;
    vec3 normal = normalize(mat3(inst.model) * inNormal);
    vNormal = normal;
    vTint = inst.tint;
    vWorldPos = world.xyz;

    float ndotl = max(dot(normal, normalize(uSunDir.xyz)), 0.0);
    vec3 offset = blix_shadow_normal_offset(
        world.xyz, normal, ndotl, uShadow.x * uShadow.y, uShadow.w);
    vSunShadowCoord = uSunShadowVP * vec4(offset, 1.0);
}

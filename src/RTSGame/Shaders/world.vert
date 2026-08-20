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
    // Declared here although the vertex stage never reads it. A push-constant block is one layout shared by
    // every stage of a pipeline, so a member added to the fragment shader alone leaves the two disagreeing
    // about how big the block is — and the mismatch surfaces as a draw-time payload-length error rather than
    // as a compile error, which is a long way from the line that caused it.
    vec4 uHaze;     // x = desaturation with distance, y = how much haze glows toward the sun
    // Declared but unread here, for the reason above: one block, one layout, every stage.
    vec4 uSunTint;
    vec4 uSkyAmbient;
    vec4 uGroundAmbient;
    vec4 uHazeAway;
    vec4 uHazeToward;
    // x = how far a plant leans at a metre up, y = the clock in simulated seconds, z = how fast the gusts
    // come, w = spare. Read here and nowhere else, which is the first thing in this block the vertex stage
    // owns rather than tolerates.
    vec4 uWind;
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
// The material classes — Shaders/materials.glsl, shared with the fragment stage.
#include "materials.glsl"

// <b>Wind, as a lean rather than as an animation.</b> Nothing in this scene has ever moved except the
// people, which is most of why a still frame of it reads as a diorama: a wood with eleven thousand trees in
// it and not a leaf stirring is uncanny in a way no amount of shading fixes.
//
// Three decisions keep it restrained, which is the whole brief:
//
// It hinges at the ground. The displacement is scaled by how far the vertex is above its own instance's
// origin, so a trunk barely moves, a canopy leans, and nothing detaches from the earth it is planted in.
// Height enters as a square root rather than linearly, because a tree is stiffer than a blade of grass —
// linear would put the same degree of tilt on an eight-metre pine as on a hand of wheat, which at a
// believable amplitude for the pine is imperceptible on the wheat and at a believable one for the wheat
// throws the pine across the map.
//
// It is out of phase with itself. The phase carries a world-space term, so neighbours lean at different
// moments and a wood ripples instead of pulsing. Two frequencies at right angles, so a leaning plant
// describes a slow figure of eight rather than sliding along a line.
//
// And it gusts, on a slow spatial wave, because steady wind reads as a machine. The gust never reaches
// zero — dead calm in one patch while the next one moves is a stranger artefact than a little wind
// everywhere.
vec3 blix_rts_lean(vec3 worldPos, float baseY, float carried) {
    if (!isPlant(carried) || uWind.x <= 0.0) return worldPos;
    float above = max(worldPos.y - baseY, 0.0);
    float t = uWind.y;
    // A slow travelling wave, so a gust arrives somewhere before it arrives everywhere.
    float gust = 0.55 + 0.45 * sin(t * uWind.z + (worldPos.x + worldPos.z * 0.7) * 0.02);
    float phase = t * 1.7 + worldPos.x * 0.23 + worldPos.z * 0.19;
    float amount = uWind.x * gust * sqrt(above);
    return worldPos + vec3(sin(phase), 0.0, cos(phase * 0.83)) * amount;
}

void main() {
    Instance inst = instances[gl_InstanceIndex];
    vec4 world = inst.model * vec4(inPosition, 1.0);
    // The instance's own origin is the last column, since the matrix arrives transposed — see the note at
    // the top of this file. That is where the plant is rooted, whatever the mesh's own pivot happens to be.
    world.xyz = blix_rts_lean(world.xyz, inst.model[3].y, inst.tint.a);
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

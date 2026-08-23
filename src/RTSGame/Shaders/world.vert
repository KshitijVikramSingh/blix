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
    // come, w = the wind's bearing in radians. Read here and nowhere else, which is the first thing in this
    // block the vertex stage owns rather than tolerates — and the bearing is shared with the smoke, so a
    // plume and the trees it drifts past agree about which way the wind is going.
    vec4 uWind;
    // Declared but unread here: one block, one layout, every stage.
    vec4 uHearth;
    vec4 uHearths[12];
    // <b>The other two cascades, appended rather than inserted.</b> The block's tail is a variable-length
    // array of hearth lights, so anything new goes after it: putting the matrices in the middle would move
    // every offset below them in RtsGameLoop for no gain. Cascade 0 is uSunShadowVP above, which keeps its
    // name and its place — see the note on CascadeBlockOffset.
    mat4 uCascade1VP;
    mat4 uCascade2VP;
    // xyz = each cascade's box width in metres; xyz = one texel as a fraction of its own map. Both per
    // cascade, because the bias is measured in texels and the three maps are neither the same width nor the
    // same resolution. uCascadeSide.w turns the debug tint on.
    vec4 uCascadeSide;
    vec4 uCascadeTexel;
    // xyz = how far along the view each cascade reaches, in metres. This is what picks the cascade; the boxes
    // only get to veto. See the note in RtsGameLoop on why containment alone does not work.
    vec4 uCascadeSplit;
    // xyz = the camera's unit forward, the axis those distances are measured along.
    vec4 uCameraAhead;
};

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
// <b>It leans downwind, and only breathes across it.</b> The first version put equal amplitude on two
// axes at right angles, which makes a crown orbit — and an orbit reads as swinging rather than as weather,
// because nothing in wind goes round. Reported immediately as violent, and it was the shape rather than the
// size: most of the displacement is now a steady lean along the wind's own bearing, and what oscillates is
// a fraction of it plus a smaller sway across. A tree that is mostly just *leaning* looks windy while
// hardly moving at all, which is the cheapest calm there is.
//
// It is out of phase with itself. The phase carries a world-space term, so neighbours lean at different
// moments and a wood ripples instead of pulsing.
//
// It gusts, on a slow spatial wave, because steady wind reads as a machine. The gust never reaches zero —
// dead calm in one patch while the next one moves is a stranger artefact than a little wind everywhere.
//
// And nothing may travel more than a fraction of its own height, which is what the square root alone did
// not give. Root-of-height makes small plants move relatively *more* than tall ones — 8 cm on a 60 cm tuft
// is a seventh of it, and a whole field of grass shifting by a seventh shimmers. The cap is the rule a
// person would state if asked: a plant bends, it does not walk.
vec3 blix_rts_lean(vec3 worldPos, float baseY, float carried) {
    if (!isPlant(carried) || uWind.x <= 0.0) return worldPos;
    float above = max(worldPos.y - baseY, 0.0);
    float t = uWind.y;
    // A slow travelling wave, so a gust arrives somewhere before it arrives everywhere.
    float gust = 0.55 + 0.45 * sin(t * uWind.z + (worldPos.x + worldPos.z * 0.7) * 0.02);
    float phase = t * 0.8 + worldPos.x * 0.23 + worldPos.z * 0.19;
    float amount = min(uWind.x * gust * sqrt(above), above * 0.07);
    vec2 downwind = vec2(sin(uWind.w), cos(uWind.w));
    vec2 across = vec2(downwind.y, -downwind.x);
    vec2 offset = downwind * (0.62 + 0.38 * sin(phase)) + across * (0.22 * cos(phase * 0.83));
    return worldPos + vec3(offset.x, 0.0, offset.y) * amount;
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
    vGround = inGround;
}

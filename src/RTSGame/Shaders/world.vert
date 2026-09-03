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
    // Declared and unread here, like uWind in the fragment stage: one block, one layout, every stage. See
    // world.frag for what the four components are.
    vec4 uScouted;
    vec4 uVeil;
    vec4 uVeilAir;
    vec4 uVeilDeep;
    // x = how much sky water hands back at a grazing angle, y = glint gain, z = how opaque deep water gets,
    // w = how far up the shore the film reaches, in wadeable depths. All four are dials in the look panel —
    // see LookSettings, and see §148 for why they stopped being constants.
    vec4 uWater;
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

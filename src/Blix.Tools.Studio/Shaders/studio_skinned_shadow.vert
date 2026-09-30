#version 450

// The caster's half of skinning, instanced. A rig that casts its REST silhouette while its
// mesh walks is the classic symptom of forgetting this one, and it is easy to miss because
// the lit pass looks perfect — the shadow is the only thing that disagrees.
//
// Same set-3 layout and the same per-instance stride as studio_skinned.vert, so both passes bind
// ONE palette material in a frame and read the same poses out of it. Two layouts here would be
// two chances for the shadow to disagree with the body.

layout(location = 0) in vec3 aPosition;
layout(location = 1) in vec3 aNormal;
layout(location = 2) in vec2 aTexCoord;
layout(location = 3) in vec4 aBoneIndices;
layout(location = 4) in vec4 aBoneWeights;
layout(location = 5) in vec4 aTangent;

layout(set = 0, binding = 0) uniform ShadowFrame {
    mat4 uLightViewProjection;
};

// The palettes: the engine's bone block at set 3 (skinning.glsl), unsized. Each rig's buffer is sized
// by Model.CreateBoneBuffers for its own bones x Studio's instance row, so this shader has no bone cap.
#include "skinning.glsl"

// <b>Sixteen bytes, and no model matrix.</b> The unskinned caster pushes a mat4 because it has
// to place its object; this one does not, because each instance's placement is already baked
// into its palette. What it does need is the per-instance stride, and studio_shadow.frag declares
// no push block at all — so unlike the lit pair, this block is free to be exactly what the
// stage uses rather than shaped to match a fragment stage.
//
// The stride is NOT smuggled into a spare matrix element. A matrix element that secretly holds
// a count is a lie about what the value is, and the next reader deserves better than finding an
// integer in M14.
layout(push_constant) uniform Push {
    // x = bones per instance, y = alpha cutoff (0 = never), z = baseColorFactor.a.
    // Still ONE vec4 and still sixteen bytes: the cutout rides in components that were already
    // being pushed and ignored, so the caster's payload does not grow.
    vec4 uSkin;
};

layout(location = 0) out vec2 vUv;

void main()
{
    int base = gl_InstanceIndex * int(uSkin.x);

    mat4 skin = blix_skin(aBoneIndices, aBoneWeights, base);

    vUv = aTexCoord;

    // World-space palette, so there is nothing between the skin and the sun's projection.
    gl_Position = uLightViewProjection * (skin * vec4(aPosition, 1.0));
}

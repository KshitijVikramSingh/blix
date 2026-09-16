#version 450

// The skinned caster's twin of studio_shadow.frag, and it exists only because the two casters push
// DIFFERENT blocks: a static caster pushes a model matrix and this one pushes a palette stride,
// because the bones already carry the placement. A fragment stage reads whatever its vertex stage's
// block declares, so one shared frag cannot serve both layouts.
layout(set = 1, binding = 0) uniform sampler2D uAlbedo;

layout(push_constant) uniform Push {
    vec4 uSkin;           // x = bones per instance, y = alpha cutoff (0 = never), z = baseColorFactor.a
};

layout(location = 0) in vec2 vUv;

void main()
{
    if (uSkin.y > 0.0 && texture(uAlbedo, vUv).a * uSkin.z < uSkin.y) discard;
}

#version 450

// The depth attachment is the whole output — except when a fragment should not be there at all.
//
// <b>A caster that cannot discard casts a solid rectangle for a leaf.</b> This stage was empty, and
// that was correct while nothing could be cut out. Once the lit pass learned MASK, a surface could
// vanish and keep its shadow: the generator's Material_AlphaMask_03 sets a cutoff of 1.1, which
// discards every fragment of the plane, and the plane's shadow stayed on the ground.
//
// The albedo is bound in set 1 binding 0 rather than 1: set 1 binding 0 is the shadow MAP in the lit
// pass, and this pass has no shadow map to bind. Every draw on this pipeline must bind something
// here, including the ground — a pipeline whose shader declares a texture that some draw leaves
// unbound is a SIGSEGV with no managed exception, named only by the validation layers.
layout(set = 1, binding = 0) uniform sampler2D uAlbedo;

layout(push_constant) uniform Push {
    mat4 uModel;
    vec4 uCutout;         // x = alpha cutoff (0 = never), y = baseColorFactor.a
};

layout(location = 0) in vec2 vUv;

void main()
{
    if (uCutout.x > 0.0 && texture(uAlbedo, vUv).a * uCutout.y < uCutout.x) discard;
}

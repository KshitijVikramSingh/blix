#version 410 core

// Writes linear distance-to-light (normalised to [0..1] by uPointLightFarPlane)
// into gl_FragDepth so the cube depth attachment stores comparable values
// regardless of which face we're rendering. The lit shader's samplerCubeShadow
// lookup compares its reference (also linear) against this, dodging the
// per-face projection-depth bookkeeping that would otherwise be required.

in vec3 vWorldPosition;
in vec2 vTexCoord;

uniform vec3  uPointLightPosition;
uniform float uPointLightFarPlane;
uniform sampler2D uAlbedo;
uniform vec4 uBaseColorFactor;
uniform float uAlphaCutoff;

void main()
{
    // Alpha-cutout discard so foliage casts leaf-shape shadows from point
    // lights too (not just sun cascades). uAlphaCutoff = 0 for OPAQUE/BLEND
    // materials so the test is free for solid geometry.
    if (uAlphaCutoff > 0.0)
    {
        vec2 uv = vec2(vTexCoord.x, 1.0 - vTexCoord.y);
        float a = texture(uAlbedo, uv).a * uBaseColorFactor.a;
        if (a < uAlphaCutoff) discard;
    }
    float d = length(vWorldPosition - uPointLightPosition);
    gl_FragDepth = clamp(d / uPointLightFarPlane, 0.0, 1.0);
}

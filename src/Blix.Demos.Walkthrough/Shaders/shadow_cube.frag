#version 410 core

// Writes linear distance-to-light (normalised to [0..1] by uPointLightFarPlane)
// into gl_FragDepth so the cube depth attachment stores comparable values
// regardless of which face we're rendering. The lit shader's samplerCubeShadow
// lookup compares its reference (also linear) against this, dodging the
// per-face projection-depth bookkeeping that would otherwise be required.

in vec3 vWorldPosition;

uniform vec3  uPointLightPosition;
uniform float uPointLightFarPlane;

void main()
{
    float d = length(vWorldPosition - uPointLightPosition);
    gl_FragDepth = clamp(d / uPointLightFarPlane, 0.0, 1.0);
}

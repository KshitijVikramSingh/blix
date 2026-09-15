#version 450

// Fullscreen-triangle sky. The three vertices are supplied at NDC corners
// (-1,-1)/(3,-1)/(-1,3) at the far plane (z=1), so the triangle covers the whole
// screen. The interpolated NDC is handed to the fragment stage, which unprojects
// it to a per-pixel world-space view ray.
layout(location = 0) in vec3 inPos;   // NDC corner (z = 1, far)
layout(location = 0) out vec2 vNdc;

void main()
{
    vNdc = inPos.xy;
    gl_Position = vec4(inPos, 1.0);
}

#version 410 core

// Bounding-box vertex shader for the volumetric flame. The mesh is a unit
// cube in local [-0.5, 0.5] coords; this transforms each vertex to world
// space (centered on uVolumeCenter, scaled by uVolumeSize) so the fragment
// shader can ray-march from the camera through the visible box surface.
//
// vWorldPos is passed through purely to give the frag shader one point on
// the ray it needs to march -- the entry/exit points are recomputed there
// via ray-AABB intersection, but having a fragment to *shade* at all is
// what makes the rasteriser run the volume shader for the covered pixels.

layout (location = 0) in vec3 aPosition;

uniform mat4  uView;
uniform mat4  uProjection;
uniform vec3  uVolumeCenter;
uniform float uVolumeSize;

out vec3 vWorldPos;

void main()
{
    vec3 wp = uVolumeCenter + aPosition * uVolumeSize;
    vWorldPos = wp;
    gl_Position = uProjection * uView * vec4(wp, 1.0);
}

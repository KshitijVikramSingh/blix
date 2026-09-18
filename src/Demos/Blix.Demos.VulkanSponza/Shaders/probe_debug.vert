#version 450
// One camera-facing quad per probe, positions synthesised — no mesh, no vertex buffer read.
//
// Impostor spheres rather than a sphere MESH, because the thing being inspected is a
// direction-dependent value: an impostor gives an exact silhouette and an analytic normal at every
// fragment for four vertices, where a tessellated sphere would spend hundreds of triangles per
// probe to approximate the same normal worse. 41,472 probes makes that difference structural.
layout(set = 0, binding = 0) uniform Frame {
    mat4 uViewProj;
    vec4 uCameraPos;
    vec4 uProbeMin;     // xyz volume min, w probe radius in metres
    vec4 uProbeSpan;    // xyz volume span, w unused
    vec4 uProbeDims;    // xyz probe counts, w unused
    vec4 uProbeMode;
    vec4 uBounceDims;
} f;

layout(location = 0) out vec3 vProbeCentre;
layout(location = 1) out vec2 vQuad;      // [-1,1] across the impostor
layout(location = 2) out vec3 vProbeUvw;  // where to sample the volumes

void main() {
    ivec3 dims = ivec3(f.uProbeDims.xyz);
    int i = gl_InstanceIndex;
    ivec3 p = ivec3(i % dims.x, (i / dims.x) % dims.y, i / (dims.x * dims.y));

    vec3 centre = f.uProbeMin.xyz + (vec3(p) + 0.5) * f.uProbeSpan.xyz / vec3(dims);
    vProbeCentre = centre;
    vProbeUvw = (vec3(p) + 0.5) / vec3(dims);

    // Quad corners from the index, billboarded against the camera.
    vec2 corner = vec2((gl_VertexIndex & 1) == 0 ? -1.0 : 1.0,
                       (gl_VertexIndex & 2) == 0 ? -1.0 : 1.0);
    vQuad = corner;

    vec3 toEye = normalize(f.uCameraPos.xyz - centre);
    vec3 right = normalize(cross(abs(toEye.y) < 0.99 ? vec3(0, 1, 0) : vec3(1, 0, 0), toEye));
    vec3 up = cross(toEye, right);

    vec3 world = centre + (right * corner.x + up * corner.y) * f.uProbeMin.w;
    gl_Position = f.uViewProj * vec4(world, 1.0);
}

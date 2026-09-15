#version 450

// Billboard particle vertex shader. The CPU has already expanded each particle
// into a camera-facing quad (ParticleBatch), so this just projects the world-space
// corner and passes colour + UV through. Descriptor-less — the only input beyond
// the vertex stream is the view-projection push constant.

layout(location = 0) in vec3 inPosition;
layout(location = 1) in vec4 inColor;
layout(location = 2) in vec2 inUv;

layout(push_constant) uniform Push {
    mat4 uViewProjection;
};

layout(location = 0) out vec4 vColor;
layout(location = 1) out vec2 vUv;

void main() {
    gl_Position = uViewProjection * vec4(inPosition, 1.0);
    vColor = inColor;
    vUv = inUv;
}

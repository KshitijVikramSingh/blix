#version 450

// Planar shadow fragment: a flat dark, alpha-blended fill. Paired with cube.vert
// (instanced) whose per-instance model has been flattened onto the ground plane
// along the sun direction, so each caster projects a dark silhouette. Alpha is the
// per-instance tint alpha (shadow strength).
layout(location = 1) in vec4 vTint;
layout(location = 0) out vec4 outColor;

void main() {
    outColor = vec4(0.0, 0.0, 0.0, vTint.a);
}

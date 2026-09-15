#version 450

// Soft round particle: a radial falloff from the quad centre, premultiplied into
// rgb so the SAME shader reads well under both blend modes — additive (One,One)
// adds the falloff-scaled colour, alpha (SrcAlpha,1-SrcAlpha) gets a fading edge.
// No texture: the look is procedural, keeping the demo asset-free.

layout(location = 0) in vec4 vColor;
layout(location = 1) in vec2 vUv;

layout(location = 0) out vec4 outColor;

void main() {
    vec2 d = vUv * 2.0 - 1.0;      // [-1,1] across the quad
    float r2 = dot(d, d);
    float a = clamp(1.0 - r2, 0.0, 1.0);
    a *= a;                        // tighter, softer core
    float alpha = vColor.a * a;
    outColor = vec4(vColor.rgb * alpha, alpha);
}

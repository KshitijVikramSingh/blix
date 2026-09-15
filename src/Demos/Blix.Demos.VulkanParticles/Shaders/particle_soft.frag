#version 450

// Soft particle: a procedural radial billboard plus a depth-aware fade. The radial
// shape is parameterized by uSharpness — high (sparks) gives a tight bright core that
// blooms; low (smoke) gives a broad soft puff. Output is PREMULTIPLIED (rgb already
// scaled by alpha) so the SAME shader serves the additive (One,One) and premultiplied-
// alpha (One,1-SrcA) layers.
//
// The fade samples the opaque scene depth (written by the pre-pass), linearizes it and
// this fragment's own depth to view-space distance, and fades the billboard out as it
// nears the surface behind it — which also culls it where it's behind (distance goes
// negative → 0). Turns the hard billboard/geometry intersection into a smooth dissolve.

layout(set = 0, binding = 0) uniform sampler2D uSceneDepth;

layout(push_constant) uniform Push {
    mat4 uViewProjection;   // 0..64 (vertex stage)
    float uNear;            // 64
    float uFar;             // 68
    float uFadeDist;        // 72 — world-space distance over which the fade ramps
    float uSharpness;       // 76 — radial falloff exponent (high = tight core)
};

layout(location = 0) in vec4 vColor;
layout(location = 1) in vec2 vUv;

layout(location = 0) out vec4 outColor;

// NDC depth [0,1] → positive view-space distance, for the engine's Vulkan perspective
// (GraphicsMatrices.CreatePerspectiveVulkan, near→0 far→1): L = n*f / (f + z*(n - f)).
float linearize(float zNdc) {
    return (uNear * uFar) / (uFar + zNdc * (uNear - uFar));
}

void main() {
    vec2 d = vUv * 2.0 - 1.0;      // [-1,1] across the quad
    float r2 = dot(d, d);
    float disc = clamp(1.0 - r2, 0.0, 1.0);
    // Falloff shaped by uSharpness, plus a small hot centre so bright cores read crisp.
    float a = pow(disc, uSharpness);
    a += 0.6 * pow(disc, uSharpness * 4.0 + 4.0);
    a = clamp(a, 0.0, 1.0);

    // Screen-space lookup into the full-res scene depth.
    vec2 uv = gl_FragCoord.xy / vec2(textureSize(uSceneDepth, 0));
    float sceneL = linearize(texture(uSceneDepth, uv).r);
    float particleL = linearize(gl_FragCoord.z);
    float soft = clamp((sceneL - particleL) / max(uFadeDist, 1e-4), 0.0, 1.0);

    float alpha = vColor.a * a * soft;
    outColor = vec4(vColor.rgb * alpha, alpha);
}

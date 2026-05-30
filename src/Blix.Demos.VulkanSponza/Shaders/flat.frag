#version 450

// Flat preview fragment stage for the streamed-load phase. Paired with lit.vert
// (reuses litInterface). Outputs a simple normal-shaded grey so the scene's
// geometry is readable while textures stream in — NO material textures sampled
// (they're allocated but their mips haven't uploaded yet), NO shadows, NO IBL.
// The full lit shader takes over once every texture has finished uploading.

layout(location = 0) in vec3 vNormalWorld;

layout(location = 0) out vec4 outColor;

void main()
{
    vec3 n = normalize(vNormalWorld);
    // Cheap hemispheric-ish term off a fixed direction: enough to read the
    // shape, deliberately flat (no real lighting).
    float l = clamp(dot(n, normalize(vec3(0.35, 0.85, 0.4))) * 0.5 + 0.5, 0.0, 1.0);
    outColor = vec4(vec3(0.12 + 0.6 * l), 1.0);
}

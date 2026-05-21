#version 410 core

in vec3 worldDirection;
uniform samplerCube uSkybox;
// Runtime slider from the host. Multiplies the linear sky color so the user can dim
// the apparent sky without regenerating the cubemap. cube.frag's env-reflection sample
// reads the same uniform so reflections stay visually consistent.
uniform float uSkyboxIntensity;

layout (location = 0) out vec4 fragColor;
layout (location = 1) out vec4 fragLuminance;
layout (location = 2) out vec4 fragNormal;

void main()
{
    // Pass O.3: cubemap is Rgba16F with linear HDR values (sun can reach > 1).
    // Sample directly -- no sRGB decode -- and multiply by the runtime intensity
    // slider. The HDR scene buffer downstream is linear, so the values flow into
    // the tone mapper unchanged.
    vec3 color = texture(uSkybox, normalize(worldDirection)).rgb * uSkyboxIntensity;

    fragColor = vec4(color, 1.0);
    float luma = dot(color, vec3(0.2126, 0.7152, 0.0722));
    fragLuminance = vec4(vec3(luma), 1.0);
    fragNormal = vec4(0.5, 0.5, 1.0, 1.0);
}

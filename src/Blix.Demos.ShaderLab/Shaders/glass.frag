#version 410 core

in vec3 worldNormal;
in vec3 worldPositionOut;
in vec3 viewNormal;
in vec3 viewPositionOut;
layout (location = 0) out vec4 fragColor;
layout (location = 1) out vec4 fragLuminance;
layout (location = 2) out vec4 fragNormal;

// Scene snapshot taken right before the glass pass. Sampled at the (screen-space)
// position of where the refracted ray would land, giving a believable "see-through-
// with-distortion" effect without ray tracing.
uniform sampler2D uScene;
// Same environment cubemap the lit pass uses for reflections. Glass picks up bright
// highlights from the sky at grazing angles via Fresnel.
uniform samplerCube uEnvMap;
uniform vec3 uCameraPosition;
uniform float uSkyboxIntensity;

// Dielectric tuning knobs. uIor: index of refraction (1.5 = window glass, 1.33 = water,
// 2.4 = diamond). uThickness: how much the refraction direction shoves the screen-space
// UV - bigger = more dramatic refraction warp but more sampling artifacts at edges.
// uTint: a subtle multiplicative color on the refracted color so the glass reads as
// glass rather than plain transparency. uF0: base reflectance at normal incidence
// (~0.04 for water/glass).
uniform float uIor;
uniform float uThickness;
uniform vec3 uTint;
uniform float uF0;

void main()
{
    vec3 N = normalize(worldNormal);
    vec3 V = normalize(uCameraPosition - worldPositionOut);
    vec3 viewN = normalize(viewNormal);
    vec3 viewV = normalize(-viewPositionOut);
    float NdotV = max(dot(N, V), 0.0);

    // Schlick approximation of the Fresnel term: tiny at head-on, ramps to ~1 at
    // grazing angles. This is what makes glass look like glass - edges go reflective,
    // centers stay see-through.
    float fresnel = uF0 + (1.0 - uF0) * pow(1.0 - NdotV, 5.0);

    // Refraction direction in world space. GLSL's refract() applies Snell's law and
    // returns zero on total internal reflection - fall back to a pure reflection there.
    vec3 Rrefract = refract(-V, N, 1.0 / uIor);
    if (dot(Rrefract, Rrefract) < 1e-4)
    {
        Rrefract = reflect(-V, N);
    }

    vec3 viewRefract = refract(-viewV, viewN, 1.0 / uIor);
    if (dot(viewRefract, viewRefract) < 1e-4)
    {
        viewRefract = reflect(-viewV, viewN);
    }

    // Cheap screen-space refraction: offset the on-screen UV by the lateral component
    // of the refraction direction. Not physically correct (ignores depth and the back
    // surface), but plenty convincing for a single forward-facing pass and avoids ray
    // tracing the geometry. The offset is scaled by uThickness; clamping to [0,1] is
    // done by the LinearClamp sampler.
    vec2 screenUv = gl_FragCoord.xy / vec2(textureSize(uScene, 0));
    // Offset in view space, not world space. The old world-XY offset made distortion
    // slide in scene axes instead of screen axes, which could put floor samples on the
    // wrong apparent side of the rotating glass.
    vec2 refractionOffset = viewRefract.xy * uThickness;
    vec3 refractedColor = texture(uScene, screenUv + refractionOffset).rgb;
    refractedColor *= uTint;

    // Environment reflection (cubemap + sRGB decode, scaled by the runtime skybox
    // brightness slider so the glass dims/brightens with the rest of the scene).
    vec3 Rreflect = reflect(-V, N);
    vec3 envSrgb = texture(uEnvMap, Rreflect).rgb;
    vec3 reflectedColor = pow(envSrgb, vec3(2.2)) * uSkyboxIntensity;

    vec3 color = mix(refractedColor, reflectedColor, fresnel);

    fragColor = vec4(color, 1.0);
    float luma = dot(color, vec3(0.2126, 0.7152, 0.0722));
    fragLuminance = vec4(vec3(luma), 1.0);
    fragNormal = vec4(N * 0.5 + 0.5, 1.0);
}

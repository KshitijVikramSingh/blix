#version 450

// Analytic procedural sky. Reconstructs a world-space view ray per pixel from the inverse
// view-projection, then shades a zenith-to-horizon gradient plus a sun disc and glow.
// Drawn first with depth disabled so the world draws over it.
//
// It matters more than a backdrop usually does here: an RTS camera looking down at a plain
// sees the sky only in the top strip of the screen, and that strip is the whole of the
// horizon reference. A flat clear colour is why an untextured plain reads as a diagram.

layout(location = 0) in vec2 vNdc;
layout(location = 0) out vec4 outColor;

layout(push_constant) uniform Push {
    mat4 uInvViewProj;
    vec4 uCamPos;     // xyz = camera world position
    vec4 uSunDir;     // xyz = direction TOWARD the sun (normalized)
    // <b>The sky's own colours, per frame.</b> They were two constants, which is a fine sky and only one
    // sky — and this game runs through a year in ninety minutes. See Rendering/Atmosphere.cs.
    vec4 uZenith;
    vec4 uHorizon;
};

void main() {
    vec4 farPoint = uInvViewProj * vec4(vNdc, 1.0, 1.0);
    vec3 world = farPoint.xyz / farPoint.w;
    vec3 ray = normalize(world - uCamPos.xyz);

    vec3 zenith  = uZenith.rgb;
    vec3 horizon = uHorizon.rgb;
    // Below the horizon is the ground seen through air, so it borrows from the horizon rather than being
    // its own colour — which keeps a night sky from having a daylit floor under it.
    vec3 ground  = horizon * 0.45;

    vec3 col = ray.y >= 0.0
        ? mix(horizon, zenith, pow(clamp(ray.y, 0.0, 1.0), 0.50))
        : mix(horizon, ground, clamp(-ray.y * 3.0, 0.0, 1.0));

    // How bright the sun's own contribution is, from how bright the sky is: at night the zenith is a
    // fortieth of its daylight value, and a full-strength disc hanging in it would be a hole in the screen.
    float daylight = clamp(dot(zenith, vec3(0.3333)) * 4.0, 0.06, 1.0);
    float s = max(dot(ray, normalize(uSunDir.xyz)), 0.0);
    col += vec3(1.00, 0.95, 0.80) * pow(s, 160.0) * 4.0 * daylight;   // disc
    col += horizon * pow(s, 6.0)  * 0.70 * daylight;                  // glow, in the sky's own colour
    col += horizon * pow(s, 1.3)  * 0.16 * daylight;                  // broad warmth

    outColor = vec4(col, 1.0);
}

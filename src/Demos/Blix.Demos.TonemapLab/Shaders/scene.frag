#version 450

// A procedural panoramic world, in scene-linear Rec.709 where 1.0 = 250 nits (the lab's anchor). It exists to give the
// tone mapper and the exposure model the things they have to survive, at real luminance ratios:
//   - a sky from ~3500 nits overhead to ~9000 at the haze, and a sun disc at 1e6 nits (4000 scene units: inside RGBA16F)
//   - a ground at ~2500 nits, a tunnel mouth (about 6.6 EV darker than what is around it; the --tunnel-ev slider) behind the starting view
//   - street lamps (small, 4000 nits), flashing retroreflective signs (small, 1e5 nits)
// The sky, sun and ground follow the world level (EV) so the same scene can be noon or night; the lamps and signs do not,
// which is what makes them dominate at night and what a meter has to learn to ignore.

// An environment map (a cube made from a Poly Haven equirect), when uUseHdri is set; otherwise the procedural sky and ground
// below. uDress keeps the tunnel, lamps and signs on top of an HDRI (they are always on over the procedural world).
layout(set = 0, binding = 0) uniform samplerCube uHdri;

layout(push_constant) uniform Push {
    float uYaw;
    float uPitch;
    float uTanHalfFov;
    float uAspect;
    float uTime;
    float uSunElevation;
    float uSunAzimuth;
    float uWorldEv;
    float uLampNits;
    float uSignNits;
    float uBlurYaw;     // radians the view sweeps during the shutter (0 = no motion blur)
    float uBlurPitch;
    float uTunnelEv;    // the tunnel interior's level relative to the world, in EV
    int uSigns;
    int uUseHdri;
    int uDress;
    int uBlurSamples;
} pc;

layout(location = 0) in vec2 vUv;
layout(location = 0) out vec4 outColor;

const float SCENE_UNIT_NITS = 250.0;
const float PI = 3.14159265;

float angDiff(float a, float b) { return abs(mod(a - b + PI, 2.0 * PI) - PI); }

float gSky;   // 1 where the ray hit sky: written to alpha for the white balance meter, which discounts it

// A lamp is a bright core with a soft shoulder and a faint wide skirt, not a flat disc: the rays a real bulb sends into the lens
// fall off smoothly, and a flat clipped disc gives the glare nothing to work with and reads as a sticker. `rad` is the core's
// radius in radians; the profile is 1 at the centre.
float lampProfile(float r, float rad)
{
    if (r > rad * 24.0) return 0.0;
    return exp(-2.0 * r * r / (rad * rad)) + 0.03 * exp(-r / (rad * 3.0));
}

vec3 world(vec3 d)
{
    gSky = d.y >= 0.0 ? 1.0 : 0.0;
    float k = exp2(pc.uWorldEv);
    float el = pc.uSunElevation;
    vec3 sunDir = normalize(vec3(cos(el) * sin(pc.uSunAzimuth), sin(el), cos(el) * cos(pc.uSunAzimuth)));
    float low = 1.0 - smoothstep(0.0, 0.6, sin(el));            // 0 high sun .. 1 on the horizon
    vec3 sunCol = mix(vec3(1.00, 0.96, 0.90), vec3(1.00, 0.55, 0.25), low);
    vec3 skyCol = mix(vec3(0.45, 0.62, 1.00), vec3(0.78, 0.56, 0.52), low * 0.6);
    float daylight = max(sin(el) * 0.8 + 0.2, 0.05);

    vec3 nits;
    if (pc.uUseHdri != 0)
    {
        // Scene units already (the loader scales the map to the noon level and clamps to half-float range); the world level
        // moves it like the procedural world, so a dusk run works on a photograph too.
        nits = textureLod(uHdri, d, 0.0).rgb * k * SCENE_UNIT_NITS;
    }
    else if (d.y >= 0.0)
    {
        float haze = (1.0 - d.y) * (1.0 - d.y);
        nits = skyCol * mix(3500.0, 9000.0, haze) * k;
        float cosS = dot(d, sunDir);
        nits += sunCol * 1500.0 * pow(max(cosS, 0.0), 24.0) * k;                       // aureole
        nits += sunCol * 1.0e6 * smoothstep(cos(0.03), cos(0.025), cosS) * k;          // the disc
    }
    else
    {
        vec2 g = d.xz / max(-d.y, 0.02);
        float chk = mod(floor(g.x * 0.5) + floor(g.y * 0.5), 2.0);
        float fade = smoothstep(0.0, 0.05, -d.y);
        vec3 ground = vec3(mix(2200.0, 3000.0, chk)) * daylight * mix(vec3(1.0, 0.97, 0.90), sunCol, 0.4);
        vec3 haze = skyCol * 7000.0;
        nits = mix(haze, ground, fade) * k;
    }

    float az = atan(d.x, d.z);
    float elv = asin(clamp(d.y, -1.0, 1.0));
    if (pc.uUseHdri != 0 && pc.uDress == 0) return nits / SCENE_UNIT_NITS;

    // The tunnel, behind the starting view: a sector wide enough that looking into it fills the screen (an interior fills
    // your view; a small dark rectangle left the meter reading the bright world around it and moved it by half a stop).
    // uTunnelEv (about 6.6) EV below whatever is around it, with a row of lamps along it.
    if (angDiff(az, PI) < 1.25 && elv > -0.9 && elv < 1.1)
    {
        nits *= exp2(pc.uTunnelEv);
        gSky = 0.0;
        for (int i = 0; i < 5; ++i)
        {
            vec2 c = vec2(PI + (float(i) - 2.0) * 0.35, 0.25);
            nits += vec3(1.0, 0.82, 0.55) * 800.0 * lampProfile(length(vec2(angDiff(az, c.x), elv - c.y)), 0.012);
        }
    }

    // Street lamps: small, warm, not scaled by the world level.
    for (int i = 0; i < 5; ++i)
    {
        vec2 c = vec2(0.5 + float(i) * 0.55, 0.04 + 0.015 * float(i % 2));
        nits += vec3(1.0, 0.82, 0.55) * pc.uLampNits * lampProfile(length(vec2(angDiff(az, c.x), elv - c.y)), 0.014);
    }

    // Retroreflective signs: two small rectangles that flash for 1.5 s in every 3.
    if (pc.uSigns != 0 && fract(pc.uTime / 3.0) < 0.5)
    {
        if (angDiff(az, 2.2) < 0.05 && abs(elv - 0.10) < 0.025) nits += vec3(0.9, 1.0, 0.9) * pc.uSignNits;
        if (angDiff(az, -1.1) < 0.04 && abs(elv - 0.08) < 0.02) nits += vec3(1.0, 0.9, 0.7) * pc.uSignNits;
    }
    return nits / SCENE_UNIT_NITS;
}

// Motion blur is the mean of the world over the shutter, in scene-linear light BEFORE any tone map: a lamp 1000 times brighter
// than its surroundings is still brighter than them when smeared across a tenth of the frame, which averaging display
// values would not give (lab/tonemap/tmlab/motionblur.py has the arithmetic and its spec).
vec3 viewRay(float yaw, float pitch, vec2 ndc)
{
    vec3 f = vec3(cos(pitch) * sin(yaw), sin(pitch), cos(pitch) * cos(yaw));
    vec3 r = normalize(cross(vec3(0.0, 1.0, 0.0), f));
    vec3 u = cross(f, r);
    return normalize(f + r * (ndc.x * pc.uAspect * pc.uTanHalfFov) + u * (-ndc.y * pc.uTanHalfFov));
}

void main()
{
    vec2 ndc = vUv * 2.0 - 1.0;
    int n = max(pc.uBlurSamples, 1);
    vec3 sum = vec3(0.0);
    float sky = 0.0;
    for (int i = 0; i < n; ++i)
    {
        float t = n == 1 ? 0.0 : (float(i) + 0.5) / float(n) - 0.5;
        sum += min(world(viewRay(pc.uYaw + t * pc.uBlurYaw, pc.uPitch + t * pc.uBlurPitch, ndc)), vec3(60000.0));   // RGBA16F tops out at 65504
        sky += gSky;
    }
    outColor = vec4(sum / float(n), sky / float(n));
}

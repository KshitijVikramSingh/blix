#version 450

// Daylight world shading for the greybox kingdom: warm directional sun gated by a single
// sun shadow map, hemispheric ambient (sky above, warm ground bounce below), and aerial
// perspective toward the horizon. Output is HDR-linear; the present pass tonemaps.
//
// The values here are chosen against a tonemap rather than against the screen. A sun of
// 2.35 and an ambient of 0.30 puts a lit mid-grey surface a little above 1.0 and a shaded
// one near 0.15, which is roughly a two-and-a-half stop separation — enough that a shadow
// reads as shade rather than as a darker shade of the same paint. Written straight to the
// swapchain those numbers would clip; that is the point of having somewhere to put them.

layout(location = 0) in vec3 vNormal;
layout(location = 1) in vec4 vTint;
layout(location = 2) in vec3 vWorldPos;
layout(location = 3) in vec4 vSunShadowCoord;

layout(location = 0) out vec4 outColor;

layout(set = 0, binding = 0) uniform sampler2D uSunShadowMap;
// Where people have been walking. One channel, three metres a texel, smooth-sampled — see
// RtsGameLoop.AdvanceWear for what fills it and why it is not simulation state.
layout(set = 0, binding = 1) uniform sampler2D uWear;
// Where the settlement's own light falls. One channel, about a metre a texel, smooth-sampled — see
// RtsGameLoop.AdvanceHearths. Like the wear, it is an observational field the renderer keeps to itself:
// no decision reads it and a loaded save rebuilds it from where the buildings are.
layout(set = 0, binding = 2) uniform sampler2D uHearthLight;

layout(push_constant) uniform Push {
    mat4 uViewProjection;
    vec4 uCamPos;
    vec4 uSunDir;
    mat4 uSunShadowVP;
    vec4 uFog;      // x = start (m), y = end (m), z = strength
    vec4 uShadow;   // x = texel as a fraction of the map, y = map size (m), z = penumbra, w = offset
    vec4 uLight;    // x = sun intensity, y = ambient scale, z = terminator wrap,
                    // w = how chromatic green is allowed to be under this sun (1 = untouched)
    vec4 uHaze;     // x = desaturation with distance, y = haze glow toward the sun,
                    // z = map extent (m), w = how much wear shows
    // <b>The palette, which used to be constants in this file.</b> A year looked like one afternoon
    // because these were decisions taken once at compile time; they come from Rendering/Atmosphere.cs now,
    // which derives them from the date and where the sun is in its cycle.
    vec4 uSunTint;
    vec4 uSkyAmbient;
    vec4 uGroundAmbient;
    vec4 uHazeAway;
    vec4 uHazeToward;
    // Declared but unread here, for the reason the vertex stage gives: one block, one layout, every stage.
    vec4 uWind;
    vec4 uHearth;   // x = how far into the night it is, y = how much light spills on the ground,
                    // z = how brightly a window burns
};

// The hues stay here and the intensities do not. A colour is a decision about what kind of
// day it is and reads the same at any exposure; a magnitude is a dial nobody can measure, so
// it arrives on a slider — see RTSGame/Debug/LookTuning.cs.
// Two haze colours rather than one, because air lit from behind and air lit from in front are not the
// same colour. Away from the sun it is the cold scatter of the sky; toward it, the warm glow of the same
// air with the sun behind it. One dot product picks between them, which is the cheapest half of real
// atmospheric scattering and most of what makes a low sun read as a time of day. Both now arrive per
// frame, so dusk is a colour the whole scene shares rather than a curve applied to it.

// Shared sun-shadow technique — src/Blix.Shaders/shadow.glsl.
#include "shadow.glsl"
// Value noise and fbm — src/Blix.Shaders/noise.glsl.
#include "noise.glsl"

// The material classes — Shaders/materials.glsl, shared with the vertex stage.
#include "materials.glsl"

// The colour of a wood fire seen at night, which is a hue and therefore stays in the shader — the
// intensities are on sliders and these are not. Deep amber rather than the orange a torch is usually drawn
// as: a hearth seen through a small window is mostly the red end of what the fire is doing, and the tell of
// a fake one is that it is the colour of a traffic cone.
const vec3 kHearthColor = vec3(1.00, 0.52, 0.20);

void main() {
    vec3 n = normalize(vNormal);
    float sunDot = dot(n, normalize(uSunDir.xyz));
    float ndotl = max(sunDot, 0.0);
    float shadow = blix_sun_shadow_soft(
        uSunShadowMap, vSunShadowCoord, ndotl, uShadow.x, uShadow.z, gl_FragCoord.xy);

    // A wrapped terminator. Straight N.L puts a hard line across every curved surface at
    // exactly the angle the sun grazes it, which on low-poly geometry lands on a facet
    // boundary and reads as a crease; wrapping softens the turn without lighting anything
    // that faces away.
    float wrapped = max((sunDot + uLight.z) / (1.0 + uLight.z), 0.0);

    float surface = vTint.a;
    vec3 albedo = vTint.rgb;

    vec3 toFragment = vWorldPos - uCamPos.xyz;
    float distance = length(toFragment);
    float haze = smoothstep(uFog.x, uFog.y, distance) * uFog.z;

    // <b>A lit window is not a surface the sun falls on.</b> It is a hole with a fire behind it, so it
    // takes no ambient, no shadow and no terminator — only the night, which is what decides whether
    // anybody has lit it. Still hazed, because a window a hundred metres off is behind the same air as
    // everything else, and a light that ignores distance is the thing that makes a night scene read as a
    // sprite layer over a photograph.
    if (isClass(surface, kEmber)) {
        vec3 glow = albedo * uHearth.z * uHearth.x;
        float towardSunlit = max(dot(normalize(toFragment), normalize(uSunDir.xyz)), 0.0);
        vec3 hazeLit = mix(uHazeAway.rgb, uHazeToward.rgb, towardSunlit * uHaze.y);
        outColor = vec4(mix(glow, hazeLit, haze), vTint.a);
        return;
    }

    // <b>Land, not a lit plane with a tint on it.</b> The single biggest prototype signal left: real ground
    // varies in hue and value over tens of metres — drier here, greener there, a little warmer where the sun
    // has been on it — and ours was one flat colour per block. Two octaves in world space at 40 m and 120 m,
    // deliberately broad: this is not detail, and a repeating grass texture would fight the low-poly art
    // rather than help it. Kept restrained enough to read as land rather than as camouflage.
    if (isClass(surface, kTerrain)) {
        float broad = blix_vnoise2(vWorldPos.xz * 0.0085);
        float fine  = blix_vnoise2(vWorldPos.xz * 0.025 + 37.0);
        float macro = broad * 0.68 + fine * 0.32;
        // Toward dry straw where the noise is high and cool moss where it is low, around the block's own
        // colour rather than replacing it — so a road stays a road and mud stays mud.
        vec3 dry  = albedo * vec3(1.16, 1.10, 0.86);
        vec3 damp = albedo * vec3(0.86, 0.96, 0.90);
        albedo = mix(damp, dry, smoothstep(0.25, 0.78, macro));

        // <b>And where people walk, the grass goes.</b> The map is centred on the origin, so the lookup
        // needs no second uniform. Toward bare trodden earth rather than simply darker: a path is a
        // different material, not shaded grass, and the giveaway of a fake one is that it stays green.
        vec2 wearUv = vWorldPos.xz / uHaze.z + 0.5;
        float trodden = texture(uWear, wearUv).r * uHaze.w;
        // Eased so the edges of a path feather instead of ending, since footfall does.
        trodden = smoothstep(0.05, 0.85, trodden);
        vec3 bare = vec3(0.20, 0.15, 0.10);
        albedo = mix(albedo, bare, trodden * 0.8);
    }

    // <b>Leaves are not plastic.</b> Foliage is most of the screen and was shaded exactly like a roof tile.
    // Two cheap corrections: a wider terminator, because a canopy is a thousand leaves and has no single
    // facet normal worth respecting; and light coming through from behind, which is the thing that actually
    // says "leaf" and costs one dot product.
    float wrapExtra = 0.0;
    float through = 0.0;
    if (isPlant(surface)) {
        wrapExtra = 0.35;
        // Backlight: how much the sun is behind this surface from where we stand.
        vec3 toEye = normalize(uCamPos.xyz - vWorldPos);
        through = pow(max(dot(-toEye, normalize(uSunDir.xyz)), 0.0), 2.0) * 0.55;
    }

    // <b>A high sun does not make grass greener.</b> Reported as midday reading over-saturated, and green
    // is the specific offender because canopy and ground are most of the screen — so their chroma is the
    // frame's mood rather than one hue among many. Pulled toward a warm grey of the same luminance, which
    // takes the colour out without taking the light out: real grass at noon goes to straw and olive.
    //
    // Here and not in the present pass because the present pass cannot tell a green roof from a green
    // field, and pulling the whole frame's saturation to fix the field drains the earth and the tiles too.
    // The amount arrives per frame from Atmosphere.MiddayGreenDrop against the sun's height, so it is
    // strongest at a summer noon and effectively absent all winter.
    if (uLight.w < 0.999 && (isPlant(surface) || isClass(surface, kTerrain))) {
        float value = dot(albedo, vec3(0.2126, 0.7152, 0.0722));
        albedo = mix(vec3(value) * vec3(1.04, 1.00, 0.92), albedo, uLight.w);
    }

    float wrapWide = max((sunDot + uLight.z + wrapExtra) / (1.0 + uLight.z + wrapExtra), 0.0);

    // Sky above, warm bounce below: a settlement is read from above, so the roofs and the
    // ground are the two surfaces that have to separate from each other.
    vec3 ambient = mix(uGroundAmbient.rgb, uSkyAmbient.rgb, n.y * 0.5 + 0.5) * uLight.y;

    // <b>Shade is a colour, not a subtraction.</b> Cast shadow only removed the sun term, so shadowed
    // ground was the same paint at lower value — which is most of what reads as flat. The light that
    // reaches shade is sky light, so shade goes cool, and that separation is mood rather than exposure.
    vec3 shadeLift = uSkyAmbient.rgb * 1.6 * (1.0 - shadow) * 0.09 * uLight.y;

    // A gentle top-face bias. A settlement is read from above, so upward faces are the ones carrying the
    // silhouette, and a touch more light on them makes roofs pop without tipping into cartoon.
    float upFace = 1.0 + max(n.y, 0.0) * 0.10;

    // <b>What the settlement lights of itself.</b> Sampled by world position out of a field the renderer
    // splats the hearths into, so it costs one texture read whatever the village's size and needs no light
    // list, no loop and no bound on how many fires there are. Multiplied by the night, so it is absent by
    // day rather than washing the ground out at noon.
    //
    // The height falloff is the honest approximation here: it fades with height above <em>zero</em> rather
    // than above the ground, which is exact while the village is flat and wants revisiting the day relief
    // is generated. Without it a lit doorway would put its pool on the roof as well.
    float spill = texture(uHearthLight, vWorldPos.xz / uHaze.z + 0.5).r *
                  uHearth.y * uHearth.x * exp(-max(vWorldPos.y, 0.0) / 3.0);

    vec3 lit = albedo * (ambient + shadeLift + uSunTint.rgb * uLight.x * wrapWide * shadow * upFace);
    lit += albedo * kHearthColor * spill;
    // Light through the leaf, added rather than multiplied: it is the sun arriving by another route.
    lit += albedo * uSunTint.rgb * uLight.x * through * shadow;

    float fog = haze;

    // <b>Distance takes colour before it takes value.</b> A straight mix toward one colour fades a scene
    // evenly, which flattens it — a far forest went pale and stayed just as green. Air scatters short
    // wavelengths and drains saturation first, which is why a distant hillside reads grey-blue rather than
    // as bright green seen through milk, and why draining it here makes the settlement stand out of its own
    // landscape without touching the settlement.
    float drained = fog * uHaze.x;
    float luma = dot(lit, vec3(0.2126, 0.7152, 0.0722));
    lit = mix(lit, vec3(luma), drained);

    // Which way the haze is lit. Looking toward the sun the air between here and there glows; looking away
    // it is the cold scatter of the sky.
    float towardSun = max(dot(normalize(toFragment), normalize(uSunDir.xyz)), 0.0);
    vec3 hazeColor = mix(uHazeAway.rgb, uHazeToward.rgb, towardSun * uHaze.y);

    outColor = vec4(mix(lit, hazeColor, fog), vTint.a);
}

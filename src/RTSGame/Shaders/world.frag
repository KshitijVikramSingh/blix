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
layout(location = 4) in vec2 vGround;

layout(location = 0) out vec4 outColor;

layout(set = 0, binding = 0) uniform sampler2D uSunShadowMap;
// Where people have been walking. One channel, three metres a texel, smooth-sampled — see
// RtsGameLoop.AdvanceWear for what fills it and why it is not simulation state.
layout(set = 0, binding = 1) uniform sampler2D uWear;

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
    vec4 uHearth;   // x = how far into the night it is, y = how far a hearth reaches (m),
                    // z = how brightly the fire itself shows, w = how many fires are lit
    // <b>The settlement's own fires, as point lights rather than as a field.</b> Position and strength; the
    // colour is shared and stays below with the other hues. Twelve of them, nearest first — see
    // Hearths.CollectLights for why that cannot pop.
    vec4 uHearths[12];
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
// intensities are on sliders and these are not.
//
// <b>Three times too yellow, and the third time it was not the colour's fault.</b> The first two were: the
// instinct is to reach for the colour of a flame, and a flame is nearly white at its middle — but what leaves
// a room through a doorway is not the flame, it is what the flame has bounced off, and hot coals and the
// underside of a thatch are much further into the red than the fire that made them.
//
// What was left after that is the <em>curve</em>. A saturated warm colour driven past one clips its red
// channel first, and once red is pinned every further increase in brightness arrives as green — so a hot
// enough ember goes orange, then amber, then yellow, then white, and ACES skews warm hues that way even
// before the clip. The colour was correct and the thing standing in a doorway was still a light bulb.
// Deepened once more, and the fix that actually mattered is the hue-preserving rolloff below.
const vec3 kHearthColor = vec3(1.00, 0.29, 0.075);

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
        outColor = vec4(mix(glow, hazeLit, haze), 1.0);
        return;
    }

    // <b>The surface of water, which is a surface and not a colour on the ground.</b> Water had been painted
    // into the terrain's own albedo, and everything wrong with it followed from that: a lake was a wet-looking
    // patch of sloping hillside instead of a level plane, a channel had no banks because a per-cell colour has
    // no edges, and the whole lot read as "seeping into ravines and laying low and still".
    //
    // Three things make it read as water, and only the first is about colour.
    //
    // <b>It is flat and it is above the bed</b>, which the geometry does — the mesh sits at the water level, so
    // a lake is level by construction and a shore is simply where that level meets the ground. Nobody draws a
    // shoreline.
    //
    // <b>It is translucent, and by depth.</b> vGround.x is opacity and it goes to zero as the water thins, so
    // the bed shows through at the margin and the edge fades out instead of ending. That gradient is the shore.
    //
    // <b>And it moves.</b> Two crossed waves on the clock the wind already carries, at a sixteenth of the
    // albedo — far too little to see as motion in a still frame and enough that the surface is not dead. The
    // frequencies are deliberately close and not harmonic, so the interference never repeats on screen.
    if (isWater(surface)) {
        float depth = clamp(vGround.y, 0.0, 1.0);
        // Toward a third of the shallow colour: deep water is not a darker shade of shallow water, it is the
        // same water with less bed showing through it, and the bed is what most of the brightness was.
        vec3 body = albedo * mix(1.0, 0.34, depth);
        float t = uWind.y;
        float ripple =
            sin(vWorldPos.x * 0.83 + t * 1.10) * sin(vWorldPos.z * 0.61 - t * 0.87) +
            0.5 * sin(vWorldPos.x * 0.31 - t * 0.63) * sin(vWorldPos.z * 0.37 + t * 0.71);
        body *= 1.0 + 0.062 * ripple;
        // Lit as the flat, upward-facing thing it is, and computed here rather than borrowed from the block
        // below — that one is derived from the interpolated normal and does not exist yet at this point in the
        // shader. For a surface whose normal is straight up the hemispheric mix collapses to the sky term.
        vec3 wet = uSkyAmbient.rgb * uLight.y
            + uSunTint.rgb * uLight.x * max(normalize(uSunDir.xyz).y, 0.0);
        vec3 litWater = body * wet;
        float wetFog = smoothstep(uFog.x, uFog.y, length(vWorldPos - uCamPos.xyz)) * uFog.z;
        vec3 wetHaze = mix(uHazeAway.rgb, uHazeToward.rgb, 0.5);
        outColor = vec4(mix(litWater, wetHaze, wetFog), clamp(vGround.x, 0.0, 1.0));
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

    // <b>What the settlement lights of itself, and the whole point of it being a light rather than a
    // field is the dot product.</b> A field had no direction, so it brightened the ground, the wall and the
    // barrel beside it by the same amount — and light with no direction is a stain rather than a source.
    // With a position, the wall facing the doorway is lit and the one facing away is not.
    //
    // The falloff is the inverse square with a metre of softening, which is what stops a fragment a
    // handspan from the fire going to white, multiplied by a smooth window that reaches zero at the reach.
    // The window is what makes twelve lights safe: a fire dropped from the set was already contributing
    // nothing at the distance it was dropped.
    //
    // A generous wrap, because a hearth is not a point. What escapes a doorway has bounced around a room
    // first, so it arrives as a wide soft source, and straight N.L on a wide source puts a hard terminator
    // where there is none.
    vec3 hearth = vec3(0.0);
    float reach = max(uHearth.y, 0.5);
    int fires = int(uHearth.w);
    for (int i = 0; i < fires; i++) {
        vec3 toFire = uHearths[i].xyz - vWorldPos;
        float away = dot(toFire, toFire);
        float window = clamp(1.0 - away / (reach * reach), 0.0, 1.0);
        float falloff = window * window / (1.0 + away);
        float facing = (max(dot(n, normalize(toFire)), 0.0) + 0.35) / 1.35;
        hearth += kHearthColor * (uHearths[i].w * falloff * facing);
    }

    // <b>Rolled off by brightness, not per channel, which is what keeps a fire the colour of a fire.</b>
    // Dividing each channel by itself compresses the biggest one hardest — and the biggest one here is red,
    // so a per-channel knee walks an ember toward yellow exactly as clipping does. Dividing the whole
    // vector by a function of its own luminance leaves the ratios between the channels untouched: it gets
    // dimmer, never blonder. Which is also just true of embers.
    float fire = dot(hearth, vec3(0.2126, 0.7152, 0.0722));
    hearth /= 1.0 + fire * 0.62;

    vec3 lit = albedo * (ambient + shadeLift + uSunTint.rgb * uLight.x * wrapWide * shadow * upFace);
    lit += albedo * hearth * uHearth.x;
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

    // <b>Alpha is coverage, and it used to be the material class.</b> Carrying the class in the fourth
    // channel was free while every pipeline here had blending disabled — and it stopped being free the
    // moment the ground grew a blended coat, because kTerrain is 0.05 and would have drawn every transition
    // at five per cent. Terrain reports how much of this class covers the pixel; everything else is opaque
    // and says so.
    float coverage = isTerrain(surface) ? clamp(vGround.x, 0.0, 1.0) : 1.0;
    outColor = vec4(mix(lit, hazeColor, fog), coverage);
}

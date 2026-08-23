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
layout(location = 3) in vec2 vGround;

layout(location = 0) out vec4 outColor;

layout(set = 0, binding = 0) uniform sampler2D uSunShadowMap;
// Where people have been walking. One channel, three metres a texel, smooth-sampled — see
// RtsGameLoop.AdvanceWear for what fills it and why it is not simulation state.
layout(set = 0, binding = 1) uniform sampler2D uWear;
// The two further cascades. uSunShadowMap above is cascade 0 — the sharp one, covering whatever is nearest —
// and these two carry the middle distance and the far. Binding 1 stays the wear texture rather than being
// renumbered to sit beside its siblings; a binding that works and a shader that agrees with its interface are
// worth more than a tidy numbering.
layout(set = 0, binding = 2) uniform sampler2D uCascade1Map;
layout(set = 0, binding = 3) uniform sampler2D uCascade2Map;
// What the player has scouted, and what they are watching. Red is explored, green is watched, one texel per
// ten-metre cell, linear-filtered so the boundary is a ten-metre ramp rather than a staircase — see
// Rendering/FogOfWar.cs, which owns the masks and is deliberately not simulation state. Named for the map so
// it cannot be confused with uScouted below, which carries the dials.
layout(set = 0, binding = 4) uniform sampler2D uScoutedMap;

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
    // <b>The other two cascades, appended rather than inserted.</b> The block's tail is a variable-length
    // array of hearth lights, so anything new goes after it: putting the matrices in the middle would move
    // every offset below them in RtsGameLoop for no gain. Cascade 0 is uSunShadowVP above, which keeps its
    // name and its place — see the note on CascadeBlockOffset.
    mat4 uCascade1VP;
    mat4 uCascade2VP;
    // xyz = each cascade's box width in metres; xyz = one texel as a fraction of its own map. Both per
    // cascade, because the bias is measured in texels and the three maps are neither the same width nor the
    // same resolution. uCascadeSide.w turns the debug tint on.
    vec4 uCascadeSide;
    vec4 uCascadeTexel;
    // xyz = how far along the view each cascade reaches, in metres. This is what picks the cascade; the boxes
    // only get to veto. See the note in RtsGameLoop on why containment alone does not work.
    vec4 uCascadeSplit;
    // xyz = the camera's unit forward, the axis those distances are measured along.
    vec4 uCameraAhead;
    // x = one over how much ground the fog grid spans. <b>Two different lengths are involved and mixing them
    // is the whole trap.</b> The grid is a ceiling plus one cell, so it spans more than the map: the divisor
    // is that span, and the offset that centres it is the map extent (uHaze.z). Using the span for both — the
    // first version of this — slides the fog half a cell sideways, which is invisible as an offset and shows
    // up as fog that disagrees with the cell overlay by one row at the edges.
    // y = how thick the memory layer is, z = how thick the deep bank over unknown ground is, w = how much
    // colour drains from ground nobody is watching. <b>Two densities and not one ramp</b> — see rts_veil.
    vec4 uScouted;
    // The cloud the veil is made of. x = one over a billow's size in metres, y = how much the noise thins and
    // thickens it, z = drift in metres per second of the wind's clock, w = how bright it is.
    vec4 uVeil;
    // How that cloud is lit and how it is pulled about. x = how strongly its colour follows the sun's bearing
    // rather than the sky's, y = how much it glows looked at against the sun, z = how far it is drawn out
    // downwind (smaller is longer), w = how much the gust makes it breathe.
    vec4 uVeilAir;
    // The deep bank's own four: x = brightness, y = what share of the directional colouring it takes,
    // z = how solid the shared cloud reads under it, w = how sharply it retreats from ground already known.
    vec4 uVeilDeep;
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

// <b>Which of the three maps has this fragment, decided by whether it is inside the box rather than by how
// far away it is.</b> A depth split would be the usual answer and needs the splits passed down and kept in
// step with the fit; containment needs nothing passed down and cannot disagree with the fit, because the fit
// is what it tests. The margin keeps the soft filter's own taps inside the map — a fragment right at the edge
// of cascade 0 reads texels that do not exist, and the artefact is a bright fringe along a line that moves
// with the camera.
//
// Returns false when the fragment is outside this box, in which case `lit` is untouched and the caller tries
// the next one out. Past the last one, nothing is shadowed — which is correct rather than a fallback: the
// cascades cover the shadowed depth, and past it the scene is fog anyway.
bool rts_cascade(
        sampler2D map, mat4 lightVP, float side, float texel,
        vec3 normal, float ndotl, vec2 pixel, out float lit) {
    // The normal offset in this cascade's own texels — see the note in world.vert on why it is here.
    vec3 offset = blix_shadow_normal_offset(vWorldPos, normal, ndotl, side * texel, uShadow.w);
    vec4 coord = lightVP * vec4(offset, 1.0);
    vec3 ndc = coord.xyz / coord.w;
    vec2 uv = ndc.xy * 0.5 + 0.5;
    float margin = uShadow.z * texel + 0.002;
    lit = 1.0;
    if (any(lessThan(uv, vec2(margin))) || any(greaterThan(uv, vec2(1.0 - margin)))
            || ndc.z < 0.0 || ndc.z > 1.0) {
        return false;
    }
    lit = blix_sun_shadow_soft(map, coord, ndotl, texel, uShadow.z, pixel);
    return true;
}

// Which cascade the last call to rts_sun_shadow read, or 3 for none. Only the debug tint uses it — see
// ShadowCascades.ShowSelection for why looking at the boxes is not the same as looking at the choice.
int gCascade = 3;

// One cascade by index. A sampler cannot be indexed out of an array portably, so the branch is written out —
// the same shape SponzaLoop's shader uses, and for the same reason.
bool rts_cascade_at(int cascade, vec3 normal, float ndotl, vec2 pixel, out float lit) {
    if (cascade == 0) {
        return rts_cascade(uSunShadowMap, uSunShadowVP, uCascadeSide.x, uCascadeTexel.x,
                           normal, ndotl, pixel, lit);
    }
    if (cascade == 1) {
        return rts_cascade(uCascade1Map, uCascade1VP, uCascadeSide.y, uCascadeTexel.y,
                           normal, ndotl, pixel, lit);
    }
    return rts_cascade(uCascade2Map, uCascade2VP, uCascadeSide.z, uCascadeTexel.z,
                       normal, ndotl, pixel, lit);
}

float rts_sun_shadow(vec3 normal, float ndotl, vec2 pixel) {
    // How far along the view this fragment is, which is what the splits are in.
    float viewDepth = dot(vWorldPos - uCamPos.xyz, uCameraAhead.xyz);
    int first = viewDepth <= uCascadeSplit.x ? 0 : (viewDepth <= uCascadeSplit.y ? 1 : 2);

    // <b>Outward from the chosen one, never inward.</b> The band a fragment falls in names the cascade whose
    // texels are sized for it; if that box happens not to contain the fragment — near a split, or on ground
    // that rises out of the slice its distance implies — the next box out is bigger and may. Falling inward
    // would be wrong in both directions: a nearer box is smaller, so it is less likely to contain anything,
    // and if it did the fragment would be sampling a map fitted to a slice it is not in.
    float lit;
    for (int c = first; c < 3; c++) {
        if (rts_cascade_at(c, normal, ndotl, pixel, lit)) {
            gCascade = c;
            return lit;
        }
    }

    gCascade = 3;
    return 1.0;
}

// The tint, matching ShadowCascades.TintOf so a box and the pixels it shaded are the same colour. Grey for
// ground no cascade claimed, which is the case worth spotting.
vec3 rts_cascade_tint(int cascade) {
    if (cascade == 0) return vec3(1.0, 0.35, 0.35);
    if (cascade == 1) return vec3(0.35, 1.0, 0.40);
    if (cascade == 2) return vec3(0.40, 0.55, 1.0);
    return vec3(0.55);
}

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

// <b>What the player knows, as weather rather than as a filter.</b> The first version multiplied the finished
// pixel by a constant per tier, and from the chair it read as too strong and as obviously a post-process: an
// even sheet has no wisps, so nothing about it says "cloud" and everything says "the renderer stopped". This
// draws two layers of value-noise cloud instead, rolling downwind, thick over ground never scouted and thin
// over ground merely unwatched.
//
// <b>Mixed toward light rather than multiplied toward black.</b> That is the substantive change and the
// reason it reads softer at the same coverage: an overcast you look at is brighter than the ground under it,
// so hiding something under cloud should raise its value and kill its contrast, not lower both. Multiplying
// could only ever make a darker version of the same picture, which is why more of it looked like less
// weather.
//
// <b>Lit by the sky, not painted grey.</b> A constant would be the same overcast at noon and at midnight;
// scaling uSkyAmbient means dusk and night come free and the cloud can never out-glow the light on it.
//
// Called from two places, which is the whole reason it is a function: water returns early with its own
// colour, and the first cut applied the veil only after that return — so every lake on the map sat in clear
// view in the middle of unexplored ground, which is exactly the information the fog exists to withhold.
vec3 rts_veil(vec3 shown, vec3 worldPos) {
    // Offset by the map extent, scaled by one over the grid span. Derived rather than asserted: cell i covers
    // world [i*c - e/2, (i+1)*c - e/2) and centres at (i+0.5)*c - e/2, so a texel-centre lookup wants
    // uv = (world + e/2) / (cells*c). Both denominators are e for the wear texture above, which is why this
    // looks like it does not need deriving. RtsGameLoop asserts this agrees with the masks, per cell.
    // <b>Sampled in wind-aligned coordinates, so the cloud can be a shape that has a direction.</b> Along the
    // wind and across it, with the along axis compressed — which stretches what comes out of the noise
    // downwind. Isotropic noise that merely translates reads as a texture sliding over the map; a bank pulled
    // out along its own motion reads as weather sweeping across it, and that is the whole difference.
    vec2 heading = vec2(cos(uWind.w), sin(uWind.w));
    vec2 across = vec2(-heading.y, heading.x);
    float travelled = uWind.y * uVeil.z;
    vec2 alongAcross = vec2(dot(worldPos.xz, heading) + travelled, dot(worldPos.xz, across));
    vec2 p = vec2(alongAcross.x * uVeilAir.z, alongAcross.y) * uVeil.x;

    // Two octaves at different sizes, the finer one carried further downwind than the broad one, which is
    // what puts a parallax between the layers instead of scaling one of them. Both on the wind's own clock, so
    // the cloud, the canopy and the water's ripples agree about the weather rather than holding three
    // opinions about it.
    float cloud =
        blix_fbm2(p) * 0.62 +
        blix_fbm2(p * 2.30 + vec2(travelled * uVeil.x * uVeilAir.z * 1.35, 0.0)) * 0.38;

    // <b>The gust, in bands running across the wind.</b> A single global pulse would make the whole map
    // breathe in unison, which nothing does; a wave travelling along the wind thickens one band while the next
    // is thinning, and that is what reads as rolling. On uWind.z because that is the gust rate the canopy
    // already sways to.
    cloud *= 1.0 + uVeilAir.w * sin(uWind.y * uWind.z * 0.55 - alongAcross.x * uVeil.x * 1.7);

    // <b>The boundary is displaced before it is read, which is what makes it seep rather than step.</b>
    // Thinning a veil with noise varies how thick it is and leaves the <em>shape</em> of its edge exactly
    // where the mask put it — so a ten-metre grid stays legible as a grid however much the density wobbles,
    // which is what "blocky" meant. Warping the lookup instead moves the edge itself: the same fog, asked
    // about a point a few metres off, and the answer wanders in and out of the cells in fingers.
    //
    // Derived from the wispiness rather than given a dial of its own, and at a fraction of a billow, because
    // the two are one statement — how ragged is this cloud — and a second slider would only let them
    // disagree. A warp approaching a full billow tears holes through to unscouted ground.
    // Displaced in world axes, from noise sampled in the wind's, so the fingers it tears also lie downwind.
    vec2 warpAmount = vec2(
        blix_fbm2(p * 1.7 + vec2(11.3, 4.1)) - 0.5,
        blix_fbm2(p * 1.7 + vec2(2.7, 19.6)) - 0.5) * uVeil.y * 0.9 / uVeil.x;
    vec2 warp = heading * warpAmount.x + across * warpAmount.y;

    vec2 scoutUv = (worldPos.xz + warp + uHaze.z * 0.5) * uScouted.x;
    vec2 scouted = texture(uScoutedMap, scoutUv).rg;
    // <b>Smoothed again on the way out, because a linear ramp has a corner in it.</b> The mask is blurred on
    // the CPU and the sampler interpolates it linearly, and linear interpolation is continuous in value but
    // not in slope — and it is the slope the eye reads. Two applications of the cubic ease flatten those
    // corners at both ends of the ramp, which is the difference between a soft edge and a faceted one.
    scouted = scouted * scouted * (3.0 - 2.0 * scouted);
    scouted = scouted * scouted * (3.0 - 2.0 * scouted);

    // <b>Two layers, composited, rather than one number lerped through three tiers.</b> The ramp version
    // could not be tuned: unknown and remembered shared a scalar, so the only way to make unscouted ground
    // properly opaque was to drag the whole ramp and pay for it in visibility on ground the player had
    // already scouted — which is a real cost, because the memory tier is most of the frame.
    //
    // Split, each layer answers one question and the answers do not have to be traded off. The memory layer
    // lives on ground that is known and unwatched; the deep bank lives where nothing is known. On fully known
    // ground the deep term is <em>identically zero</em>, so its density is free to go as high as it likes.
    //
    // The blur that keeps the boundary soft is the one place they still meet, and uVeilDeep.w is what that
    // costs: an exponent above one concentrates the bank in genuinely unscouted ground and pulls it back off
    // the fringe of the known.
    float known = scouted.r;
    float watched = scouted.g;
    float memory = known * (1.0 - watched) * uScouted.y;
    float deep = pow(1.0 - known, uVeilDeep.w) * uScouted.z;
    if (memory + deep <= 0.002) return shown;

    // The noise thins and thickens both layers on top of the warp: the warp decides where the edge is, this
    // decides how solid the middle is. At zero wispiness it is a flat sheet of exactly the two densities,
    // which keeps them in charge of how much is hidden.
    float wisp = mix(1.0 - uVeil.y, 1.0 + uVeil.y, cloud);
    float memoryDensity = clamp(memory * wisp, 0.0, 1.0);
    // <b>The same cloud, curved rather than a second field.</b> Below one it fills the thin parts in while
    // leaving the thick ones, so the bank reads as a mass that has texture instead of as the mist turned up.
    // One weather system: two independent noise fields drifting over each other never resolve into a sky.
    float deepDensity = clamp(deep * pow(clamp(wisp, 0.0, 2.0), uVeilDeep.z), 0.0, 1.0);

    // <b>Lit as the same air the distance haze is made of.</b> It was mixing toward flat sky ambient, which
    // is why it sat on top of the scene instead of in it: the one thing every other bit of atmosphere in this
    // shader does is pick between a cold scatter away from the sun and a warm glow toward it, and the veil was
    // the only air on screen with no opinion about where the sun was. Sharing the pair also means Atmosphere.cs
    // drives it — so the fog is the right colour for the season and the hour without a second palette, and it
    // cannot disagree with the haze standing next to it.
    vec3 toFragment = worldPos - uCamPos.xyz;
    float towardSun = max(dot(normalize(toFragment), normalize(uSunDir.xyz)), 0.0);
    vec3 veilColor = mix(uHazeAway.rgb, uHazeToward.rgb, towardSun * uVeilAir.x);
    // Forward scatter, which is the most recognisable thing fog does with light: a bank between the eye and a
    // low sun is brighter than the lit ground beside it. Tight, so it is a glow about the sun's bearing rather
    // than a general lift — a broad one only washes the veil out and loses the shape of the cloud.
    veilColor += uSunTint.rgb * uLight.x * uVeilAir.y * pow(towardSun, 6.0);

    // The deep bank keeps only a share of that directional colouring, and the physics is the reason rather
    // than the taste: light that has been scattered many times has forgotten which way it came from, so a
    // thick bank is more uniform than a thin one. At a full share it reads as coloured glass.
    vec3 deepColor = mix(
        vec3(dot(veilColor, vec3(0.2126, 0.7152, 0.0722))),
        veilColor,
        uVeilDeep.y);

    // Colour before value, the same order the aerial perspective uses: a scene that only loses saturation
    // still reads as itself, and this map carries its season in hue.
    float luma = dot(shown, vec3(0.2126, 0.7152, 0.0722));
    shown = mix(shown, vec3(luma), clamp(memoryDensity + deepDensity, 0.0, 1.0) * uScouted.w);
    // Memory first, then the bank over the top of it — which is the ordering the whole split is for. Layered
    // this way the bank hides the mist it covers instead of averaging with it, and neither one's dial reaches
    // into the other's ground.
    shown = mix(shown, veilColor * uVeil.w, memoryDensity);
    return mix(shown, deepColor * uVeilDeep.x, deepDensity);
}

void main() {
    vec3 n = normalize(vNormal);
    float sunDot = dot(n, normalize(uSunDir.xyz));
    float ndotl = max(sunDot, 0.0);
    float shadow = rts_sun_shadow(n, ndotl, gl_FragCoord.xy);

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
        // Veiled like everything else, and this is the leak that mattered most of the three: a lit window is
        // the single most legible thing on a night map, so a settlement in unexplored ground would have
        // announced itself as a row of bright dots on black. Water gave away terrain; this gives away people.
        outColor = vec4(rts_veil(mix(glow, hazeLit, haze), vWorldPos), 1.0);
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
        outColor = vec4(
            rts_veil(mix(litWater, wetHaze, wetFog), vWorldPos),
            clamp(vGround.x, 0.0, 1.0));
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
    // <b>It replaces the colour, it does not multiply it.</b> Multiplying was the first version and it failed
    // as a diagnostic: red over grass is a brown that reads as soil, green over grass is grass, and the answer
    // to "is cascade 0 being picked at all" was a judgement call about a hue. A debug view whose output can be
    // mistaken for the scene is not telling you anything. So the hue is the cascade and nothing else, and the
    // only thing kept from the lighting is the shadow term — which is what makes it possible to see that a
    // band's shadows are sharp while looking at which band it is.
    //
    // Applied after the fog for the same reason it is not mixed: the far cascade must not fade toward the same
    // grey that means no cascade at all.
    // After the aerial perspective rather than before it, and the ordering is a claim: haze is a fact about
    // air and distance so it belongs to the scene, while the veil is a fact about the player and belongs to
    // the image of it. Applied first, the haze would be veiled and then added back, and unexplored ground
    // would glow with the colour of air nobody can see through.
    vec3 shown = rts_veil(mix(lit, hazeColor, fog), vWorldPos);

    if (uCascadeSide.w > 0.5) shown = rts_cascade_tint(gCascade) * (0.30 + 0.70 * shadow);
    outColor = vec4(shown, coverage);
}

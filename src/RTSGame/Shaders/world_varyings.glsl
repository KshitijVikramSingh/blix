// <b>What every world fragment stage has in common: its inputs.</b> Split out so the plain and the
// skinned stages can each declare their own extra bindings before pulling in the shading itself — the
// skinned one needs an albedo sampler, and a sampler cannot be declared after the code that reads it.
//
// Include this, then declare anything extra, then define rts_surface_tint(), then include world_shade.glsl.
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

// The push block, shared with the other world stages — Shaders/world_push.glsl.
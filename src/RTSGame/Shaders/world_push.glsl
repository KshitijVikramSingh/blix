// <b>The world push block, declared once.</b> Twenty-seven members that four shader stages must agree
// about byte-for-byte, because a push-constant block is one layout shared by every stage of a pipeline: a
// member added to one declaration and not another surfaces as a draw-time payload-length error, a long way
// from the line that caused it. world.vert and world.frag carried identical copies of this; the skinned
// pair would have made four. One file, four includers, and the offsets in RtsGameLoop have one thing to
// stay in step with.
//
// Not every stage reads every member, and that is fine — the layout is what must match, not the usage. The
// commentary here is written from whichever stage actually consumes the value.
//
// Do NOT reorder: the tail is a variable-length hearth array, so anything new goes AFTER it or every offset
// below it moves.

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
    // x = how far a plant leans at a metre up, y = the clock in simulated seconds, z = how fast the gusts
    // come, w = the wind's bearing in radians. Read by the vertex stages (world.vert, the skinned pair and
    // shadow_caster.vert) so a leaning tree and its shadow agree; the bearing is shared with the smoke, so a
    // plume and the trees it drifts past agree about which way the wind is going.
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
    // x = how much sky water hands back at a grazing angle, y = glint gain, z = how opaque deep water gets,
    // w = how far up the shore the film reaches, in wadeable depths. All four are dials in the look panel —
    // see LookSettings, and see §148 for why they stopped being constants.
    vec4 uWater;
    // x = how many bone matrices each skinned body owns, which is how the skinned stages find their slice of
    // the palette: a body's matrices start at gl_InstanceIndex * x. Appended after the tail like the cascade
    // block was, so not one existing offset moves. Zero for every unskinned draw, which read this never.
    vec4 uSkin;
};

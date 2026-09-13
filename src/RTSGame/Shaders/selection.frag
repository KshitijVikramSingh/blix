#version 450

// A plate with an edge: a faint even wash inside, a brighter band at the rim.
//
// The wash says "this ground belongs to the thing" and the rim says where the thing ends. A flat disc of
// one alpha reads as a stain and a bare ring reads as a decal somebody forgot to fill — and against ground
// that is itself mottled, the two together are what stays legible. Nothing here is hard-coded to a colour:
// the instance carries it, so hover and selected are the same shader at different strengths.

layout(location = 0) in float vRadius;
layout(location = 1) in vec4 vTint;

layout(location = 0) out vec4 outColor;

void main() {
    float r = clamp(vRadius, 0.0, 1.0);
    if (r > 1.0) discard;

    // Softened at the very edge so the circle is not a staircase, and eased in from the middle so the wash
    // is strongest where the object is rather than uniform across the disc.
    float outer = 1.0 - smoothstep(0.93, 1.0, r);

    // <b>The ring carries it and the fill only tints.</b> Two wrong versions got here: first a fill so weak it
    // was multiplied down to a quarter and vanished, then — over-correcting — a fill raised to 0.58 *and* an
    // alpha of 0.95, on top of a wash that brightens outward with a band added over it. That made the whole
    // disc 0.55 to 0.95 opaque: a solid cyan blob with the ground hidden and no ring to see, because
    // everything was the ring.
    //
    // The shape wanted is a thin bright edge over a faint tint. So the fill is low and flat, and the band is
    // the only thing near full strength.
    float fill = 0.20 * outer;
    float rim = smoothstep(0.66, 0.88, r) * outer;

    float alpha = clamp(fill + rim * 0.95, 0.0, 1.0) * vTint.a;
    outColor = vec4(vTint.rgb, alpha);
}

#pragma once

// Octahedral mapping: a direction on the sphere <-> a point on a square.
//
// <b>Why a square and not four spherical-harmonic coefficients.</b> L1 SH is a single smooth lobe
// over the whole sphere, so a source subtending twenty degrees and one subtending ninety produce the
// same SHAPE at different magnitudes — it has no vocabulary for "that direction, narrowly". Measured
// on Sponza's cypress: teal curtains several metres away washed the lower half of the tree, because
// L1 could represent their presence but not their extent.
//
// An octahedral map is a discretisation instead of a projection. Sharpness is then a question of
// resolution rather than of basis order, and the area distortion is mild and even — which is the
// property that makes it the standard choice over cube faces for this, since a cube face costs six
// separate images and a seam at every edge.
//
// The mapping is its own inverse in structure: fold the sphere onto an octahedron, unfold that to a
// square, and the |x| + |y| + |z| = 1 normalisation is the whole of it.

// Direction -> [-1,1]^2.
vec2 blix_octEncode(vec3 dir) {
    vec3 d = dir / (abs(dir.x) + abs(dir.y) + abs(dir.z));
    // The lower hemisphere folds outward into the corners, which is what fills the square.
    vec2 uv = d.z >= 0.0
        ? d.xy
        : (1.0 - abs(d.yx)) * vec2(d.x >= 0.0 ? 1.0 : -1.0, d.y >= 0.0 ? 1.0 : -1.0);
    return uv;
}

// [-1,1]^2 -> direction.
vec3 blix_octDecode(vec2 uv) {
    vec3 d = vec3(uv.x, uv.y, 1.0 - abs(uv.x) - abs(uv.y));
    if (d.z < 0.0) {
        d.xy = (1.0 - abs(d.yx)) * vec2(d.x >= 0.0 ? 1.0 : -1.0, d.y >= 0.0 ? 1.0 : -1.0);
    }
    return normalize(d);
}

// <b>The border is not decoration.</b> A tile is sampled bilinearly, so a texel on the edge needs a
// neighbour beyond the edge — and on an octahedral map the neighbour across an edge is the texel
// diagonally opposite, not the one next to it. Without the ring, every probe shows a seam along the
// octahedron's fold, and the seam moves when the camera does, which reads as flickering rather than
// as a mapping error.
//
// Given a border texel's coordinate inside a tile of side `size` (border included), returns the
// INTERIOR coordinate whose value it must copy.
ivec2 blix_octBorderSource(ivec2 c, int size) {
    int last = size - 1;
    int inner = size - 2;               // interior side length
    bool left = c.x == 0, right = c.x == last;
    bool bottom = c.y == 0, top = c.y == last;

    // Corners take the diagonally opposite interior texel.
    if ((left || right) && (bottom || top)) {
        return ivec2(left ? 1 : inner, bottom ? 1 : inner);
    }
    // Edges mirror along the perpendicular axis, which is the fold.
    if (left || right)  return ivec2(left ? 1 : inner, inner + 1 - c.y);
    if (bottom || top)  return ivec2(inner + 1 - c.x, bottom ? 1 : inner);
    return c;
}

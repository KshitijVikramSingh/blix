// A texel's record, as every shader reads it (SponzaLoop.Texels.cs writes it): its position (w: its weight -- 4 an atlas
// texel's, -5 a double-sided pair's side, |w| < 2 buried -- or a hashed texel's surface) and its normal (w: an atlas
// texel's coordinate, packed, or a hashed texel's level). The including shader declares the buffer (Texels, texels[];
// TEXEL_ARRAY names another) before this.
#ifndef TEXEL_RECORD_GLSL
#define TEXEL_RECORD_GLSL
#ifndef TEXEL_ARRAY
#define TEXEL_ARRAY texels
#endif
#define TEXEL_POSITION(i) TEXEL_ARRAY[2u * uint(i)]
#define TEXEL_NORMAL(i) TEXEL_ARRAY[2u * uint(i) + 1u]
#endif

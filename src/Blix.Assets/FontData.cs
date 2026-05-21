namespace Blix.Assets;

// One per-size bitmap atlas for a font. Sizes are baked independently — the
// engine layer uploads one TextureHandle per size and SpriteBatch already
// partitions draws by texture, so rendering at multiple sizes in a single
// Begin/End pair stays one quad list per size and one draw per size.
//
// The atlas is single-channel coverage (alpha). The Blix.Render layer
// expands it to RGBA8 on upload so the existing sprite pipeline can sample it
// without a new shader.
public sealed record FontSizeData(
    float PixelSize,
    int AtlasWidth,
    int AtlasHeight,
    byte[] AlphaPixels,
    float Ascent,
    float Descent,
    float LineGap,
    IReadOnlyDictionary<char, FontGlyph> Glyphs)
{
    // ascent - descent (descent is negative in font-space) is glyph box; lineGap
    // is the extra inter-line breathing room. Sum is the y-step between consecutive
    // baselines for normal-spaced text.
    public float LineHeight => Ascent - Descent + LineGap;
}

// One glyph's location in the atlas + per-glyph metrics, in pixels at the
// size's native pixel height.
//   AtlasX/Y + AtlasW/H: pixel rect in the atlas (top-left origin).
//   OffsetX/Y: offset from the current pen position (cursor at baseline) to the
//              top-left of the glyph quad. OffsetY is typically negative because
//              the glyph top is above the baseline.
//   Advance:   how far to advance the pen after drawing this glyph.
public readonly record struct FontGlyph(
    int AtlasX,
    int AtlasY,
    int AtlasW,
    int AtlasH,
    float OffsetX,
    float OffsetY,
    float Advance);

// CPU-side font: one entry per requested pixel size. Each entry owns its own
// atlas bitmap. Blix.Render's Font wraps this by uploading each size's atlas
// to a TextureHandle.
public sealed record FontData(
    string Name,
    IReadOnlyList<FontSizeData> Sizes);

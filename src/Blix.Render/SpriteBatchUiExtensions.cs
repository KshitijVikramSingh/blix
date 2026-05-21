using System.Numerics;
using Blix.Graphics;

namespace Blix.Render;

// UI helpers built on SpriteBatch. The renderer itself stays primitive — these
// methods just emit textured quads via the existing Draw path. The expected
// view-projection is the screen-space ortho from
// GraphicsMatrices.CreateOrthographicOffCenter(0, width, height, 0, -1, 1), i.e.
// origin at top-left of the framebuffer with Y growing down.
public static class SpriteBatchUiExtensions
{
    // Draws a single line of text at `position` in screen-space pixels.
    // `position` marks the top-left of the text's em-box: the baseline sits at
    // `position.Y + size.Ascent` (where `size` is the chosen FontSize, after
    // accounting for the request-vs-baked-size scale factor). Newlines wrap to
    // the next baseline at `LineHeight * scale`.
    //
    // Missing glyphs (anything outside the baked ASCII range or unmapped chars
    // like tab) are silently skipped after consuming a space-width advance, so
    // unsupported chars look like a blank space rather than throwing.
    public static Vector2 DrawText(
        this SpriteBatch batch,
        Font font,
        float pixelSize,
        string text,
        Vector2 position,
        GraphicsColor color,
        float depth = 0.0f,
        // HiDPI / retina: caller passes dpiScale = framebufferWidth / logicalWidth.
        // The ortho projection runs in logical units (so positions/sizes are in
        // points), but the atlas needs to be at the physical-pixel resolution to
        // render crisp. We pick the baked size closest to pixelSize*dpiScale and
        // shrink each glyph quad by scale<1 so it covers pixelSize logical units
        // on screen — that resolves to (pixelSize*dpiScale) physical pixels, a 1:1
        // mapping to the atlas glyph that was baked at that size.
        float dpiScale = 1.0f)
    {
        ArgumentNullException.ThrowIfNull(batch);
        ArgumentNullException.ThrowIfNull(font);
        ArgumentNullException.ThrowIfNull(text);

        if (text.Length == 0)
        {
            return position;
        }

        var size = font.NearestSize(pixelSize * dpiScale);
        var scale = pixelSize / size.PixelSize;
        var atlas = size.Atlas;

        var startX = position.X;
        // Pen sits at the baseline; offset down from the requested top-left by
        // the scaled ascent so the top of the tallest glyph aligns with position.Y.
        var penX = startX;
        var penY = position.Y + size.Ascent * scale;
        var lineStep = size.LineHeight * scale;

        Vector2 lastAdvance = position;

        foreach (var c in text)
        {
            if (c == '\n')
            {
                penX = startX;
                penY += lineStep;
                continue;
            }

            if (!size.Glyphs.TryGetValue(c, out var glyph))
            {
                // Treat unknown chars as space-width gaps using the space glyph's
                // advance when available, otherwise a quarter of the size.
                penX += size.Glyphs.TryGetValue(' ', out var space)
                    ? space.Advance * scale
                    : size.PixelSize * 0.25f;
                continue;
            }

            if (glyph.AtlasW > 0 && glyph.AtlasH > 0)
            {
                var quadX = penX + glyph.OffsetX * scale;
                var quadY = penY + glyph.OffsetY * scale;
                var quadW = glyph.AtlasW * scale;
                var quadH = glyph.AtlasH * scale;

                batch.Draw(
                    atlas,
                    new Vector2(quadX, quadY),
                    new Vector2(quadW, quadH),
                    new Rect(glyph.AtlasX, glyph.AtlasY, glyph.AtlasW, glyph.AtlasH),
                    color,
                    depth,
                    flipV: false);
            }

            penX += glyph.Advance * scale;
            lastAdvance = new Vector2(penX, penY - size.Ascent * scale);
        }

        return lastAdvance;
    }

    // Returns the bounding box of `text` rendered at `pixelSize` — width is the
    // pen advance across all chars on the widest line, height is line-step times
    // the line count. Layout matches DrawText so the result can be used for
    // alignment without a dry-run.
    public static Vector2 MeasureText(Font font, float pixelSize, string text, float dpiScale = 1.0f)
    {
        ArgumentNullException.ThrowIfNull(font);
        ArgumentNullException.ThrowIfNull(text);

        if (text.Length == 0)
        {
            return Vector2.Zero;
        }

        var size = font.NearestSize(pixelSize * dpiScale);
        var scale = pixelSize / size.PixelSize;

        var lineWidth = 0.0f;
        var maxLineWidth = 0.0f;
        var lineCount = 1;
        foreach (var c in text)
        {
            if (c == '\n')
            {
                maxLineWidth = MathF.Max(maxLineWidth, lineWidth);
                lineWidth = 0.0f;
                lineCount++;
                continue;
            }

            if (size.Glyphs.TryGetValue(c, out var glyph))
            {
                lineWidth += glyph.Advance * scale;
            }
            else if (size.Glyphs.TryGetValue(' ', out var space))
            {
                lineWidth += space.Advance * scale;
            }
            else
            {
                lineWidth += size.PixelSize * 0.25f;
            }
        }
        maxLineWidth = MathF.Max(maxLineWidth, lineWidth);

        return new Vector2(maxLineWidth, lineCount * size.LineHeight * scale);
    }

    // Draws a solid filled rect using the supplied 1x1 white texture for tinting
    // via vertex color. Convenience wrapper around batch.Draw with no source rect.
    public static void DrawSolidRect(
        this SpriteBatch batch,
        TextureHandle whitePixel,
        Rect rect,
        GraphicsColor color,
        float depth = 0.0f)
    {
        ArgumentNullException.ThrowIfNull(batch);
        batch.Draw(
            whitePixel,
            new Vector2(rect.X, rect.Y),
            new Vector2(rect.Width, rect.Height),
            sourceRect: null,
            color: color,
            depth: depth,
            flipV: false);
    }

    // 9-slice: stretches a source texture across `destination` while keeping the
    // four corner regions (size `borders`) at their original pixel size. Edges
    // stretch along one axis and the centre patch stretches in both. Total 9 quads.
    // `borders` are in source-texture pixels measured from each edge.
    public static void DrawNineSlice(
        this SpriteBatch batch,
        TextureHandle texture,
        int textureWidth,
        int textureHeight,
        NineSliceBorders borders,
        Rect destination,
        GraphicsColor color,
        float depth = 0.0f)
    {
        ArgumentNullException.ThrowIfNull(batch);
        if (borders.Left + borders.Right > textureWidth)
        {
            throw new ArgumentException("Horizontal border total exceeds texture width.", nameof(borders));
        }
        if (borders.Top + borders.Bottom > textureHeight)
        {
            throw new ArgumentException("Vertical border total exceeds texture height.", nameof(borders));
        }

        // Source rows/cols in pixel coords (top-left origin).
        var sx0 = 0.0f;
        var sx1 = borders.Left;
        var sx2 = textureWidth - borders.Right;
        var sx3 = (float)textureWidth;
        var sy0 = 0.0f;
        var sy1 = borders.Top;
        var sy2 = textureHeight - borders.Bottom;
        var sy3 = (float)textureHeight;

        // Destination rows/cols in screen-space pixels.
        var dx0 = destination.X;
        var dx1 = destination.X + borders.Left;
        var dx2 = destination.X + destination.Width - borders.Right;
        var dx3 = destination.X + destination.Width;
        var dy0 = destination.Y;
        var dy1 = destination.Y + borders.Top;
        var dy2 = destination.Y + destination.Height - borders.Bottom;
        var dy3 = destination.Y + destination.Height;

        // 9 patches: rows top/middle/bottom × cols left/middle/right.
        Emit(batch, texture, sx0, sy0, sx1, sy1, dx0, dy0, dx1, dy1, color, depth);
        Emit(batch, texture, sx1, sy0, sx2, sy1, dx1, dy0, dx2, dy1, color, depth);
        Emit(batch, texture, sx2, sy0, sx3, sy1, dx2, dy0, dx3, dy1, color, depth);

        Emit(batch, texture, sx0, sy1, sx1, sy2, dx0, dy1, dx1, dy2, color, depth);
        Emit(batch, texture, sx1, sy1, sx2, sy2, dx1, dy1, dx2, dy2, color, depth);
        Emit(batch, texture, sx2, sy1, sx3, sy2, dx2, dy1, dx3, dy2, color, depth);

        Emit(batch, texture, sx0, sy2, sx1, sy3, dx0, dy2, dx1, dy3, color, depth);
        Emit(batch, texture, sx1, sy2, sx2, sy3, dx1, dy2, dx2, dy3, color, depth);
        Emit(batch, texture, sx2, sy2, sx3, sy3, dx2, dy2, dx3, dy3, color, depth);
    }

    private static void Emit(
        SpriteBatch batch, TextureHandle texture,
        float sx0, float sy0, float sx1, float sy1,
        float dx0, float dy0, float dx1, float dy1,
        GraphicsColor color, float depth)
    {
        var srcW = sx1 - sx0;
        var srcH = sy1 - sy0;
        var dstW = dx1 - dx0;
        var dstH = dy1 - dy0;
        if (srcW <= 0.0f || srcH <= 0.0f || dstW <= 0.0f || dstH <= 0.0f) return;

        batch.Draw(
            texture,
            new Vector2(dx0, dy0),
            new Vector2(dstW, dstH),
            new Rect(sx0, sy0, srcW, srcH),
            color,
            depth,
            flipV: false);
    }
}

public readonly record struct NineSliceBorders(float Left, float Top, float Right, float Bottom);

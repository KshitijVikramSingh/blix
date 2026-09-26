using Silk.NET.Core;

namespace Blix.Runtime.Silk;

/// <summary>
/// The Blix mark, rasterised on demand for window icons.
/// </summary>
/// <remarks>
/// <b>Drawn rather than shipped.</b> The mark is three flat-shaded quads, so there is nothing here
/// a PNG would buy. Embedding one would mean an asset to keep in step with the same artwork in
/// <c>website/assets</c>, a decoder dependency in the host purely for a 32-pixel image, and a fixed
/// set of sizes chosen in advance. Generating it means the host answers whatever size the platform
/// asks for, at whatever scale factor, and the geometry below is the whole definition.
/// <para>
/// The coordinates are the same 46-unit grid as <c>website/assets/icon.svg</c>; if one changes the
/// other should. Faces are lit from one direction, matching how the shape is drawn on the site.
/// </para>
/// </remarks>
public static class BlixMark
{
    // The mark is authored 62 wide and 46 tall. A square icon letterboxes it rather than
    // redrawing it: shortening the bracket arms to fit a 46-wide canvas leaves almost nothing
    // between them for the box, and the deep arms are what make it read as a bracket at all.
    private const float Grid = 62f;
    private const float OriginY = -8f;      // centres the 46-tall mark in a 62-tall square

    private static readonly (float X, float Y)[] Top =
        [(31f, 10f), (44f, 16.5f), (31f, 23f), (18f, 16.5f)];

    private static readonly (float X, float Y)[] Left =
        [(18f, 16.5f), (31f, 23f), (31f, 36f), (18f, 29.5f)];

    private static readonly (float X, float Y)[] Right =
        [(44f, 16.5f), (31f, 23f), (31f, 36f), (44f, 29.5f)];

    // The brackets, as the six rectangles a 7-wide stroke on "M17 4 H4 V42 H17" traces out,
    // mirrored. Axis-aligned, so they need no polygon test.
    private static readonly (float X0, float Y0, float X1, float Y1)[] Brackets =
    [
        (0.5f,  0.5f, 7.5f,  45.5f),    // left stem
        (0.5f,  0.5f, 17f,   7.5f),     // left top arm
        (0.5f,  38.5f, 17f,  45.5f),    // left bottom arm
        (54.5f, 0.5f, 61.5f, 45.5f),    // right stem
        (45f,   0.5f, 61.5f, 7.5f),     // right top arm
        (45f,   38.5f, 61.5f, 45.5f),   // right bottom arm
    ];

    // Ember, and the same two derived faces the site uses.
    private static readonly (byte R, byte G, byte B) TopColour = (0xe8, 0x41, 0x17);
    private static readonly (byte R, byte G, byte B) LeftColour = (0xa7, 0x2f, 0x11);
    private static readonly (byte R, byte G, byte B) RightColour = (0xee, 0x6f, 0x4f);

    /// <summary>Sizes a window manager is likely to ask for, smallest first.</summary>
    private static readonly int[] DefaultSizes = [16, 24, 32, 48, 64, 128];

    /// <summary>The size macOS wants for a dock tile, rendered rather than upscaled from 128.</summary>
    private const int DockSize = 256;

    // The dock tile: paper, with the platform's usual rounded square. Only the dock gets it.
    // A background helps a large icon sit among its neighbours and hurts a small one, because it
    // shrinks the mark inside its own border - the same reason the site's 16px favicon has none.
    private const float TileRadius = 0.225f;    // of the side, the platform convention
    private const float TileInset = 0.12f;      // margin between the mark and the tile edge
    private static readonly (byte R, byte G, byte B) TileColour = (0xe8, 0xea, 0xec);

    // A second ramp, for the mark on paper. The dark-band one cannot be reused: it is built so the
    // LIT face is brightest, which is right against a dark surface and backwards against a light
    // one - there the lit face is the closest to the background and the box loses its third side.
    // These hold the same order and sit the whole ramp far enough below paper to read.
    private static readonly (byte R, byte G, byte B) TileTop = (0xba, 0x34, 0x12);
    private static readonly (byte R, byte G, byte B) TileLeft = (0x83, 0x23, 0x0b);
    private static readonly (byte R, byte G, byte B) TileRight = (0xe3, 0x44, 0x1c);

    // Rendering the whole set costs ~7 ms and the dock tile another ~12 ms, measured on an M4.
    // That is not much, but it is the same answer every time and it would otherwise be paid on
    // every window a process opens. A benign race just renders twice and keeps one.
    private static IReadOnlyList<RawImage>? windowIcons;
    private static RawImage? dockIcon;

    /// <summary>
    /// The mark at the sizes a window manager picks from. The platform chooses one and ignores the
    /// rest, so offering several costs a few kilobytes and avoids it scaling one badly.
    /// </summary>
    public static IReadOnlyList<RawImage> WindowIcons() => windowIcons ??= RenderSet();

    /// <summary>The mark on a paper tile, at the size macOS uses for a dock icon.</summary>
    public static RawImage DockIcon() => dockIcon ??= RenderTile(DockSize);

    private static RawImage[] RenderSet()
    {
        var icons = new RawImage[DefaultSizes.Length];
        for (var i = 0; i < DefaultSizes.Length; i++) icons[i] = Render(DefaultSizes[i]);
        return icons;
    }

    /// <summary>
    /// The mark at <paramref name="size"/> square, as straight (non-premultiplied) RGBA8.
    /// </summary>
    /// <remarks>
    /// Supersampled 4x4 and box-filtered down, because at 16 pixels the diagonals are most of the
    /// shape and an aliased edge is the whole difference between a box and a smudge.
    /// </remarks>
    public static RawImage Render(int size)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(size, 1);

        const int Samples = 4;                 // per axis
        const int PerPixel = Samples * Samples;
        var scale = Grid / size;
        var pixels = new byte[size * size * 4];

        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                int r = 0, g = 0, b = 0, covered = 0;

                for (var sy = 0; sy < Samples; sy++)
                {
                    for (var sx = 0; sx < Samples; sx++)
                    {
                        // Sample at the centre of each sub-pixel cell, in grid units.
                        var gx = (x + (sx + 0.5f) / Samples) * scale;
                        var gy = ((y + (sy + 0.5f) / Samples) * scale) + OriginY;

                        // The three faces tile the hexagon without overlapping, so the first hit
                        // is the only hit; shared edges fall to whichever is tested first and the
                        // supersampling hides the seam.
                        (byte R, byte G, byte B) hit;
                        if (InBracket(gx, gy)) hit = TopColour;
                        else if (Inside(Top, gx, gy)) hit = TopColour;
                        else if (Inside(Left, gx, gy)) hit = LeftColour;
                        else if (Inside(Right, gx, gy)) hit = RightColour;
                        else continue;

                        r += hit.R; g += hit.G; b += hit.B; covered++;
                    }
                }

                if (covered == 0) continue;    // leave it transparent

                var i = (y * size + x) * 4;
                pixels[i + 0] = (byte)(r / covered);
                pixels[i + 1] = (byte)(g / covered);
                pixels[i + 2] = (byte)(b / covered);
                pixels[i + 3] = (byte)(covered * 255 / PerPixel);
            }
        }

        return new RawImage(size, size, pixels);
    }

    private static bool InBracket(float px, float py)
    {
        foreach (var (x0, y0, x1, y1) in Brackets)
        {
            if (px >= x0 && px <= x1 && py >= y0 && py <= y1) return true;
        }

        return false;
    }

    /// <summary>
    /// The mark centred on an opaque rounded-square tile, for a dock or a home screen.
    /// </summary>
    private static RawImage RenderTile(int size)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(size, 1);

        const int Samples = 4;
        const int PerPixel = Samples * Samples;
        var pixels = new byte[size * size * 4];

        // Fit the mark's 61x45 of ink inside the tile's safe area and centre it there.
        var avail = Grid * (1f - (TileInset * 2f));
        var fit = Math.Min(avail / 61f, avail / 45f);
        var radius = Grid * TileRadius;
        var scale = Grid / size;

        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                int r = 0, g = 0, b = 0, covered = 0;

                for (var sy = 0; sy < Samples; sy++)
                {
                    for (var sx = 0; sx < Samples; sx++)
                    {
                        var gx = (x + ((sx + 0.5f) / Samples)) * scale;
                        var gy = (y + ((sy + 0.5f) / Samples)) * scale;
                        if (!InRoundedSquare(gx, gy, radius)) continue;

                        // Back out of the tile's frame into the mark's own coordinates.
                        var mx = ((gx - ((Grid / 2f) - (31f * fit))) / fit);
                        var my = ((gy - ((Grid / 2f) - (23f * fit))) / fit);

                        var hit = TileColour;
                        if (InBracket(mx, my)) hit = TileTop;
                        else if (Inside(Top, mx, my)) hit = TileTop;
                        else if (Inside(Left, mx, my)) hit = TileLeft;
                        else if (Inside(Right, mx, my)) hit = TileRight;

                        r += hit.R; g += hit.G; b += hit.B; covered++;
                    }
                }

                if (covered == 0) continue;

                var i = (y * size + x) * 4;
                pixels[i + 0] = (byte)(r / covered);
                pixels[i + 1] = (byte)(g / covered);
                pixels[i + 2] = (byte)(b / covered);
                pixels[i + 3] = (byte)(covered * 255 / PerPixel);
            }
        }

        return new RawImage(size, size, pixels);
    }

    /// <summary>Point test for a rounded square filling the whole grid.</summary>
    private static bool InRoundedSquare(float px, float py, float radius)
    {
        if (px < 0f || py < 0f || px > Grid || py > Grid) return false;

        // Only the four corner boxes need the distance test.
        var cx = px < radius ? radius : (px > Grid - radius ? Grid - radius : px);
        var cy = py < radius ? radius : (py > Grid - radius ? Grid - radius : py);
        if (cx == px || cy == py) return true;

        var dx = px - cx;
        var dy = py - cy;
        return (dx * dx) + (dy * dy) <= radius * radius;
    }

    /// <summary>Winding test for a convex polygon wound consistently in one direction.</summary>
    private static bool Inside((float X, float Y)[] poly, float px, float py)
    {
        var sign = 0;

        for (var i = 0; i < poly.Length; i++)
        {
            var a = poly[i];
            var b = poly[(i + 1) % poly.Length];
            var cross = ((b.X - a.X) * (py - a.Y)) - ((b.Y - a.Y) * (px - a.X));

            if (cross == 0f) continue;         // exactly on the edge: let a neighbour claim it
            var s = cross > 0f ? 1 : -1;
            if (sign == 0) sign = s;
            else if (sign != s) return false;
        }

        return sign != 0;
    }
}

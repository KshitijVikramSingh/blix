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
    // The 46-unit grid the mark is authored on. The box spans 6..40 horizontally and 6..39
    // vertically, so roughly an eighth of the frame is margin on every side - which is what keeps
    // it from crowding its neighbours in a dock or a tab strip.
    private const float Grid = 46f;

    private static readonly (float X, float Y)[] Top =
        [(23f, 6f), (40f, 14.5f), (23f, 23f), (6f, 14.5f)];

    private static readonly (float X, float Y)[] Left =
        [(6f, 14.5f), (23f, 23f), (23f, 39f), (6f, 30.5f)];

    private static readonly (float X, float Y)[] Right =
        [(40f, 14.5f), (23f, 23f), (23f, 39f), (40f, 30.5f)];

    // Ember, and the same two derived faces the site uses.
    private static readonly (byte R, byte G, byte B) TopColour = (0xcb, 0x45, 0x26);
    private static readonly (byte R, byte G, byte B) LeftColour = (0x8f, 0x2d, 0x17);
    private static readonly (byte R, byte G, byte B) RightColour = (0xe2, 0x68, 0x3f);

    /// <summary>Sizes a window manager is likely to ask for, smallest first.</summary>
    private static readonly int[] DefaultSizes = [16, 24, 32, 48, 64, 128];

    /// <summary>The size macOS wants for a dock tile, rendered rather than upscaled from 128.</summary>
    private const int DockSize = 256;

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

    /// <summary>The mark at the size macOS uses for a dock tile.</summary>
    public static RawImage DockIcon() => dockIcon ??= Render(DockSize);

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
                        var gy = (y + (sy + 0.5f) / Samples) * scale;

                        // The three faces tile the hexagon without overlapping, so the first hit
                        // is the only hit; shared edges fall to whichever is tested first and the
                        // supersampling hides the seam.
                        (byte R, byte G, byte B) hit;
                        if (Inside(Top, gx, gy)) hit = TopColour;
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

using Blix.Assets;
using Blix.Graphics;

namespace Blix.Render;

// GPU-side font. One TextureHandle per baked pixel size. Multi-size selection
// happens in DrawText by picking the FontSize whose PixelSize is closest to the
// requested render size; the caller can scale the quads to hit the exact
// requested size, at the cost of some bilinear blurring when scaled.
//
// The atlas is single-channel coverage in source, but the engine's only
// uploadable color format is Rgba8 (see TextureFormat). On upload we expand
// alpha to RGBA = (255, 255, 255, a) so the existing sprite shader (which
// multiplies sampled rgba by vertex color) tints text via vertex color.
public sealed class Font
{
    public string Name { get; }
    public IReadOnlyList<FontSize> Sizes { get; }

    private Font(string name, IReadOnlyList<FontSize> sizes)
    {
        Name = name;
        Sizes = sizes;
    }

    public static Font Upload(IGraphicsDevice device, FontData data)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(data);

        var uploaded = new List<FontSize>(data.Sizes.Count);
        foreach (var size in data.Sizes)
        {
            var rgba = ExpandAlphaToRgba(size.AlphaPixels);
            var description = new TextureDescription(
                size.AtlasWidth,
                size.AtlasHeight,
                TextureFormat.Rgba8,
                SamplerDescription.LinearClamp);
            // Single mip on purpose: CreateTexture2D auto-generates a full mip
            // chain for blittable color formats (right for repeating world
            // textures to kill moire, wrong for a glyph atlas — trilinear mip
            // sampling muddies downscaled text). One level via the mipped path
            // keeps glyphs crisp; the baker already provides several PixelSizes
            // for size selection, so per-texture mips add nothing here.
            var atlas = device.CreateTexture2DMipped(description, new[] { rgba }, name: $"{data.Name}.{size.PixelSize}px");
            uploaded.Add(new FontSize(size, atlas));
        }

        return new Font(data.Name, uploaded);
    }

    // Pick the baked size whose pixel height is closest to the requested size.
    // Ties (equidistant) go to the larger size so downscaling is preferred over
    // upscaling — downscaled bitmap text looks crisper than blown-up text.
    public FontSize NearestSize(float requestedPixelSize)
    {
        if (Sizes.Count == 0)
        {
            throw new InvalidOperationException("Font has no baked sizes.");
        }

        var best = Sizes[0];
        var bestDistance = MathF.Abs(best.PixelSize - requestedPixelSize);
        for (var i = 1; i < Sizes.Count; i++)
        {
            var candidate = Sizes[i];
            var distance = MathF.Abs(candidate.PixelSize - requestedPixelSize);
            if (distance < bestDistance || (distance == bestDistance && candidate.PixelSize > best.PixelSize))
            {
                best = candidate;
                bestDistance = distance;
            }
        }
        return best;
    }

    private static byte[] ExpandAlphaToRgba(byte[] alpha)
    {
        var rgba = new byte[alpha.Length * 4];
        for (var i = 0; i < alpha.Length; i++)
        {
            var j = i * 4;
            rgba[j + 0] = 255;
            rgba[j + 1] = 255;
            rgba[j + 2] = 255;
            rgba[j + 3] = alpha[i];
        }
        return rgba;
    }
}

// One baked size of a font: a texture + the glyph metric table that describes
// where each ASCII codepoint sits in that texture.
public sealed class FontSize
{
    public float PixelSize { get; }
    public int AtlasWidth { get; }
    public int AtlasHeight { get; }
    public TextureHandle Atlas { get; }
    public float Ascent { get; }
    public float Descent { get; }
    public float LineGap { get; }
    public float LineHeight => Ascent - Descent + LineGap;
    public IReadOnlyDictionary<char, FontGlyph> Glyphs { get; }

    internal FontSize(FontSizeData data, TextureHandle atlas)
    {
        PixelSize = data.PixelSize;
        AtlasWidth = data.AtlasWidth;
        AtlasHeight = data.AtlasHeight;
        Atlas = atlas;
        Ascent = data.Ascent;
        Descent = data.Descent;
        LineGap = data.LineGap;
        Glyphs = data.Glyphs;
    }
}

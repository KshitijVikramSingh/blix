using System.Text.Json;
using StbTrueTypeSharp;
using static StbTrueTypeSharp.StbTrueType;

namespace Blix.Assets;

// Bakes a TTF into one or more pixel-size atlases via stb_truetype's
// BakeFontBitmap. v0 covers ASCII 32..126 (printable codepoints + space). Each
// size gets its own atlas because:
//   - BakeFontBitmap is single-size only, and packing across sizes would
//     require a custom shelf packer.
//   - SpriteBatch already partitions draws by texture id, so one Begin/End can
//     mix sizes with one draw call per size that actually got used.
//
// Importer input is a font.json spec file (not the raw TTF) so per-font baking
// configuration (which sizes to bake) lives as content-author data alongside
// the source file rather than being hand-coded at the call site. Manifest entry:
//
//   { "id": "fonts/roboto", "importer": "font.json",
//     "source": "fonts/Roboto-Regular.font.json" }
//
// The font.json file references its TTF by a path relative to itself:
//
//   { "ttf": "Roboto-Regular.ttf", "sizes": [14, 20, 28, 40, 56] }
//
// Atlas allocation: starts at 128x128 and doubles until BakeFontBitmap reports
// every glyph fit. The packer is shelf-style and may return a positive count
// indicating "this many fit"; we treat anything less than the full glyph count
// as failure and retry at the next size up.
public sealed class FontImporter : IAssetImporter<FontData>
{
    private const int FirstCodepoint = 32;
    private const int CodepointCount = 95; // 32..126 inclusive

    private const int MinAtlasSize = 128;
    private const int MaxAtlasSize = 4096;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public string Name => "font.json";

    public FontData Import(AssetImportContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!File.Exists(context.SourcePath))
        {
            throw new FileNotFoundException($"Font spec not found: {context.SourcePath}", context.SourcePath);
        }

        FontSpec? spec;
        try
        {
            using var stream = File.OpenRead(context.SourcePath);
            spec = JsonSerializer.Deserialize<FontSpec>(stream, JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new AssetImportException(context.SourcePath, null, $"invalid font.json: {ex.Message}", ex);
        }

        if (spec is null || string.IsNullOrWhiteSpace(spec.Ttf))
        {
            throw new AssetImportException(context.SourcePath, null, "font.json missing 'ttf' string.");
        }

        if (spec.Sizes is null || spec.Sizes.Length == 0)
        {
            throw new AssetImportException(context.SourcePath, null, "font.json missing non-empty 'sizes' array.");
        }

        // TTF path resolves relative to the spec file's directory.
        var specDir = Path.GetDirectoryName(Path.GetFullPath(context.SourcePath))
            ?? throw new AssetImportException(context.SourcePath, null, "cannot resolve font.json directory.");
        var ttfPath = Path.Combine(specDir, spec.Ttf);

        if (!File.Exists(ttfPath))
        {
            throw new AssetImportException(context.SourcePath, null, $"TTF file not found: {ttfPath}");
        }

        var ttf = File.ReadAllBytes(ttfPath);
        var name = Path.GetFileNameWithoutExtension(ttfPath);

        var sizes = new List<FontSizeData>(spec.Sizes.Length);
        foreach (var pixelSize in spec.Sizes)
        {
            if (pixelSize <= 0.0f)
            {
                throw new AssetImportException(context.SourcePath, null, $"font size must be positive, got {pixelSize}.");
            }
            sizes.Add(BakeOneSize(ttf, pixelSize));
        }

        return new FontData(name, sizes);
    }

    private static FontSizeData BakeOneSize(byte[] ttf, float pixelSize)
    {
        var chardata = new stbtt_bakedchar[CodepointCount];

        var atlasSide = MinAtlasSize;
        byte[]? pixels = null;
        while (atlasSide <= MaxAtlasSize)
        {
            pixels = new byte[atlasSide * atlasSide];

            // stbtt_BakeFontBitmap's int return encodes:
            //   > 0  : first unused row of the bitmap (== bitmap height if fully packed).
            //          Could still be partial — some chars may not have fit even though
            //          rows remain. The reliable signal is "every chardata entry is filled".
            //   < 0  : -N chars fit, the rest didn't. Always a fit failure.
            //   == 0 : nothing fit.
            // The managed Boolean wrapper just collapses this to "result > 0", which
            // wrongly reports success for partial bakes. Call the pointer overload and
            // inspect chardata directly to detect a true full pack.
            int result;
            unsafe
            {
                fixed (byte* ttfPtr = ttf)
                fixed (byte* pixelPtr = pixels)
                fixed (stbtt_bakedchar* charPtr = chardata)
                {
                    result = stbtt_BakeFontBitmap(
                        ttfPtr, 0, pixelSize, pixelPtr, atlasSide, atlasSide,
                        FirstCodepoint, CodepointCount, charPtr);
                }
            }

            // True success: positive return AND every renderable glyph got a non-empty
            // box. The space glyph (codepoint 32) is intentionally empty — skip it.
            var allFit = result > 0;
            if (allFit)
            {
                for (var i = 1; i < CodepointCount; i++)
                {
                    if (chardata[i].x1 == chardata[i].x0 && chardata[i].y1 == chardata[i].y0)
                    {
                        allFit = false;
                        break;
                    }
                }
            }

            if (allFit)
            {
                break;
            }

            atlasSide *= 2;
            pixels = null;
        }

        if (pixels is null)
        {
            throw new InvalidOperationException($"Failed to bake font at size {pixelSize}px — {CodepointCount} glyphs did not fit in {MaxAtlasSize}x{MaxAtlasSize} atlas.");
        }

        float ascent, descent, lineGap;
        unsafe
        {
            fixed (byte* ttfPtr = ttf)
            {
                // index 0 = first font in collection. size param is the pixel height
                // we'll match against on render. Sign convention matches stb's:
                // ascent > 0, descent < 0 (below baseline), lineGap >= 0.
                stbtt_GetScaledFontVMetrics(ttfPtr, 0, pixelSize, &ascent, &descent, &lineGap);
            }
        }

        var glyphs = new Dictionary<char, FontGlyph>(CodepointCount);
        for (var i = 0; i < CodepointCount; i++)
        {
            var c = (char)(FirstCodepoint + i);
            var bc = chardata[i];
            glyphs[c] = new FontGlyph(
                AtlasX: bc.x0,
                AtlasY: bc.y0,
                AtlasW: bc.x1 - bc.x0,
                AtlasH: bc.y1 - bc.y0,
                OffsetX: bc.xoff,
                OffsetY: bc.yoff,
                Advance: bc.xadvance);
        }

        return new FontSizeData(
            PixelSize: pixelSize,
            AtlasWidth: atlasSide,
            AtlasHeight: atlasSide,
            AlphaPixels: pixels,
            Ascent: ascent,
            Descent: descent,
            LineGap: lineGap,
            Glyphs: glyphs);
    }

    private sealed class FontSpec
    {
        public string? Ttf { get; set; }
        public float[]? Sizes { get; set; }
    }
}

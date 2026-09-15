using Blix.Assets;
using Blix.Cooked;

namespace Blix.Recipes;

/// <summary>
/// A <c>.font.json</c> spec to a baked <c>.blixfont</c> atlas: Blix's fourth recipe.
/// </summary>
/// <remarks>
/// <para>
/// <b>This one exists to answer a question about the substrate, not about fonts.</b> The claim the
/// cook arc rests on is that a recipe costs the recipe — that declaring, discovering, stamping,
/// covering, re-cooking and reporting all arrive for free, and what you write is only the decision
/// about your own content. Three recipes written together prove nothing about that; a fourth,
/// written afterwards by following the same pattern, is the test.
/// </para>
/// <para>
/// <b>And it is the clean case for the three-way split.</b> The capability — rasterising a TTF into
/// coverage bitmaps — already existed in the engine as <see cref="FontImporter"/> and is untouched.
/// The format, <c>BlixFont</c>, is engine too, because the runtime reads it. What is here is only
/// the decision: this spec, these settings, that format. It calls the engine and writes the engine's
/// format and contains no rasterising of its own.
/// </para>
/// <para>
/// <b>What it removes.</b> <c>.font.json</c> has always been a recipe with no cook — it declares a
/// source TTF and the pixel sizes to bake, which is exactly a recipe's shape, and then the atlas is
/// rasterised at load, on every launch, forever. stb_truetype's BakeFontBitmap walks every glyph at
/// every requested size and retries at doubled atlas dimensions until they fit.
/// </para>
/// </remarks>
public static class FontRecipe
{
    /// <summary>Bakes one spec into one atlas file. No console output: the driver owns reporting.</summary>
    public static FontData CookOne(string specPath, string outPath)
    {
        ArgumentNullException.ThrowIfNull(specPath);
        ArgumentNullException.ThrowIfNull(outPath);

        // Rasterised by the engine's own importer — the capability, unchanged. Asking it for the
        // source path explicitly rather than letting it find a cooked sibling, because a recipe
        // that read its own output would cook the same file forever.
        var font = new FontImporter().ImportSource(
            new AssetImportContext(AssetId.Parse("cook/font"), specPath));

        var stamp = CookStamp.Of(
            BlixFont.ShippedRecipe, BlixFont.ShippedRecipeVersion, specPath, outPath,
            $"sizes={string.Join(',', font.Sizes.Select(s => s.PixelSize.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)))}");

        BlixFontWriter.Write(outPath, font, stamp);
        return font;
    }

    /// <summary>The uniform entry point the index finds and <c>blix cook</c> calls.</summary>
    [Recipe(BlixFont.ShippedRecipe,
        Produces = ".blixfont",
        Consumes = ".json",
        Version = BlixFont.ShippedRecipeVersion,
        Summary = "a .font.json spec to a baked glyph atlas")]
    public static CookOutcome Cook(CookRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        // <b>Consumes ".json" and then narrows, which is worth saying out loud.</b> The declared
        // extension is what a build rule and `cook status` match on, and this recipe wants
        // `*.font.json` — a compound extension the file system does not model. So it claims .json
        // and declines anything that is not a font spec, rather than claiming an extension it
        // cannot honour. A recipe that silently cooked every .json it met would be worse.
        if (!request.SourcePath.EndsWith(".font.json", StringComparison.OrdinalIgnoreCase))
        {
            return CookOutcome.Skipped("not a .font.json spec");
        }

        var font = CookOne(request.SourcePath, request.OutputPath);
        var bytes = font.Sizes.Sum(s => (long)s.AlphaPixels.Length);
        return CookOutcome.Written(
            $"{font.Sizes.Count} size(s), {font.Sizes.Sum(s => s.Glyphs.Count)} glyphs, {bytes / 1024.0:0.0} KB of coverage");
    }
}

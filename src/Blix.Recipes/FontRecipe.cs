using Blix.Assets;
using Blix.Cooked;

namespace Blix.Recipes;

/// <summary>
/// A <c>.font.json</c> spec to a baked <c>.blixfont</c> atlas.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="FontImporter"/> owns TTF rasterisation and <c>BlixFont</c> owns the runtime format.
/// This recipe binds one declaration and its requested sizes to that capability and format.
/// </para>
/// <para>
/// Cooking moves glyph rasterisation and atlas-growth retries out of application startup. Runtime
/// loading prefers the resulting sibling and falls back to this same source capability when absent.
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

        // Discovery works on simple extensions, so this recipe claims .json and explicitly narrows
        // that set to the compound .font.json convention.
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

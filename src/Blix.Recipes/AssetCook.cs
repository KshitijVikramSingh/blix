using System.Diagnostics;
using Blix.Cooked;

namespace Blix.Recipes;

/// <summary>
/// Cooks one asset and exactly the images it references into a tree of its own — what
/// <c>blix cook asset</c> runs, and what a tool that opens a raw model cooks on open.
/// </summary>
/// <remarks>
/// Follows material image references rather than sweeping the source directory. Textures are cooked
/// before the mesh so its image table records the destination artifacts, and the result stands
/// alone: no source file is needed to load it. Refusals throw <see cref="InvalidDataException"/>
/// before anything is written.
/// </remarks>
public static class AssetCook
{
    /// <summary>What a cook produced.</summary>
    public sealed record Result(string MeshPath, int Primitives, int Images, long SourceBytes, long CookedBytes, TimeSpan TextureTime);

    /// <summary>Cooks <paramref name="entry"/>'s source as its project configuration decides.</summary>
    public static Result Cook(CookConfig.Entry entry, string outDir, Action<string>? log = null)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return Cook(entry.Source, outDir, entry.FlipTextureV, entry.SplitTriBudget, entry.SplitFoliage,
            entry.Materials, log, entry.SplitMaxExtent);
    }

    public static Result Cook(
        string source, string outDir, bool flipTextureV = false, int splitTriBudget = 0, bool splitFoliage = true,
        MaterialPatch? patch = null, Action<string>? log = null, float splitMaxExtent = MeshRecipe.DefaultSplitMaxExtent)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(outDir);
        if (!File.Exists(source)) throw new FileNotFoundException($"No file at {source}.", source);

        var sourceDir = Path.GetDirectoryName(Path.GetFullPath(source)) ?? ".";
        var outputRoot = Path.GetFullPath(outDir);

        // One cooked file per image AS USED: an image read in two roles or two conventions is two
        // references with two cooked names (MeshRecipe.ImageVariants), so nothing here is ambiguous.
        var references = MeshRecipe.ReferencedImages(source, patch);
        log?.Invoke($"  {references.Count} referenced image(s)");

        var problems = new List<string>();
        foreach (var reference in references)
        {
            var sourcePath = Path.GetFullPath(Path.Combine(sourceDir, reference.Uri));
            var outputPath = Path.GetFullPath(Path.Combine(outputRoot, reference.CookedUri));
            if (!IsWithin(sourceDir, sourcePath) || !IsWithin(outputRoot, outputPath))
                problems.Add($"outside tree: {reference.Uri} does not remain inside both source and output roots");
            else if (!File.Exists(sourcePath))
                problems.Add($"missing: {reference.Uri}");
        }

        foreach (var collision in references.GroupBy(r => r.CookedUri, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
        {
            problems.Add(
                $"output collision: {string.Join(", ", collision.Select(r => $"{r.Uri} as {r.Role}"))} "
                + $"all map to {collision.Key}");
        }

        if (problems.Count > 0) throw new InvalidDataException(string.Join(Environment.NewLine, problems));

        Directory.CreateDirectory(outputRoot);
        long sourceBytes = 0, cookedBytes = 0;
        var sw = Stopwatch.StartNew();
        Parallel.ForEach(references, reference =>
        {
            var from = Path.Combine(sourceDir, reference.Uri);
            var to = Path.Combine(outputRoot, reference.CookedUri);
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            TextureRecipe.CookOne(from, to, out var inLen, out var outLen, reference.Role, reference.FlipGreen);
            Interlocked.Add(ref sourceBytes, inLen);
            Interlocked.Add(ref cookedBytes, outLen);
        });
        var textureTime = sw.Elapsed;

        // The shared shipped-mesh path, so asset trees and direct mesh cooks get the same LOD and
        // splitting policy.
        var meshOut = Path.Combine(outputRoot, Path.GetFileNameWithoutExtension(source) + ".blixmesh");
        var count = MeshRecipe.CookShipped(
            source, meshOut,
            flipTextureV: flipTextureV,
            splitTriBudget: splitTriBudget,
            splitFoliage: splitFoliage,
            splitMaxExtent: splitMaxExtent,
            patch: patch,
            log: log);

        return new Result(meshOut, count, references.Count, sourceBytes, cookedBytes, textureTime);
    }

    private static bool IsWithin(string root, string path)
    {
        var relative = Path.GetRelativePath(Path.GetFullPath(root), Path.GetFullPath(path));
        return relative != ".."
            && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            && !Path.IsPathRooted(relative);
    }
}

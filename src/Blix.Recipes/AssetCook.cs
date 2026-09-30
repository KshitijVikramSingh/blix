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

    public static Result Cook(
        string source, string outDir, bool flipTextureV = false, int splitTriBudget = 0, bool splitFoliage = true,
        MaterialPatch? patch = null, Action<string>? log = null, bool staticOnly = false)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(outDir);
        if (!File.Exists(source)) throw new FileNotFoundException($"No file at {source}.", source);

        var sourceDir = Path.GetDirectoryName(Path.GetFullPath(source)) ?? ".";
        var outputRoot = Path.GetFullPath(outDir);

        var references = MeshRecipe.ReferencedImages(source, patch);
        var byUri = references
            .GroupBy(reference => reference.Uri, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        log?.Invoke($"  {byUri.Length} referenced image(s)");

        var problems = new List<string>();
        foreach (var group in byUri)
        {
            var roles = group.Select(reference => (reference.Role, reference.FlipGreen)).Distinct().ToArray();
            if (roles.Length > 1)
            {
                problems.Add(
                    $"ambiguous: {group.Key} is used as {string.Join(" and ", roles)}; one "
                    + ".blixtex cannot preserve both material-channel roles or conventions");
            }

            var sourcePath = Path.GetFullPath(Path.Combine(sourceDir, group.Key));
            var outputPath = Path.GetFullPath(Path.Combine(outputRoot, Path.ChangeExtension(group.Key, ".blixtex")));
            if (!IsWithin(sourceDir, sourcePath) || !IsWithin(outputRoot, outputPath))
                problems.Add($"outside tree: {group.Key} does not remain inside both source and output roots");
            else if (!File.Exists(sourcePath))
                problems.Add($"missing: {group.Key}");
        }

        foreach (var collision in byUri.GroupBy(
                     group => Path.ChangeExtension(group.Key, ".blixtex"),
                     StringComparer.OrdinalIgnoreCase).Where(group => group.Count() > 1))
        {
            problems.Add(
                $"output collision: {string.Join(", ", collision.Select(group => group.Key))} "
                + $"all map to {collision.Key}");
        }

        if (problems.Count > 0) throw new InvalidDataException(string.Join(Environment.NewLine, problems));

        Directory.CreateDirectory(outputRoot);
        long sourceBytes = 0, cookedBytes = 0;
        var sw = Stopwatch.StartNew();
        Parallel.ForEach(byUri, group =>
        {
            var reference = group.Single();
            var from = Path.Combine(sourceDir, reference.Uri);
            var to = Path.Combine(outputRoot, Path.ChangeExtension(reference.Uri, ".blixtex"));
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
            patch: patch,
            log: log,
            staticOnly: staticOnly);

        return new Result(meshOut, count, byUri.Length, sourceBytes, cookedBytes, textureTime);
    }

    private static bool IsWithin(string root, string path)
    {
        var relative = Path.GetRelativePath(Path.GetFullPath(root), Path.GetFullPath(path));
        return relative != ".."
            && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            && !Path.IsPathRooted(relative);
    }
}

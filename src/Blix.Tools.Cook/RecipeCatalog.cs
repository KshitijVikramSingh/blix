using System.Reflection;
using System.Text.Json;
using Blix.Cooked;

namespace Blix.Tools.Cook;

/// <summary>
/// Every recipe this cook can run: Blix's own, plus the ones other projects declare.
/// </summary>
/// <remarks>
/// Discovery reads build-generated <c>*.blixapps.json</c> indexes and loads only assemblies that
/// declare recipes. Blix's built-ins are always present; project recipes join them after their
/// declaring assembly has been built.
/// <para>A recipe needed during a consuming project's pre-compile cook belongs in a referenced
/// cooking project so build order makes the assembly available on a clean build.</para>
/// </remarks>
internal static class RecipeCatalog
{
    private static FoundRecipe[]? cached;

    /// <summary>Blix's own recipes plus every one found in an index under <see cref="Environment.CurrentDirectory"/>.</summary>
    internal static FoundRecipe[] All() => cached ??= Build();

    private static FoundRecipe[] Build()
    {
        // Built-ins are direct dependencies, so the cook remains useful outside an indexed tree.
        var found = new List<FoundRecipe>(BlixRecipes.Find(typeof(Blix.Recipes.MeshRecipe).Assembly));
        var seen = new HashSet<string>(found.Select(r => r.Id), StringComparer.Ordinal);

        foreach (var (index, file) in Indexes())
        {
            // Most indexes contain apps only; do not load assemblies that declare no recipes.
            if (index.Recipes is not { Length: > 0 }) continue;
            if (index.Recipes.All(r => seen.Contains(r.Id))) continue;

            var assemblyPath = Path.Combine(file.DirectoryName!, index.Assembly);
            if (!File.Exists(assemblyPath)) continue;

            Assembly assembly;
            try
            {
                assembly = Assembly.LoadFrom(assemblyPath);
            }
            catch (Exception e) when (e is BadImageFormatException or FileLoadException or IOException)
            {
                Console.Error.WriteLine($"blix cook: {assemblyPath} declares recipes but would not load — {e.Message}");
                continue;
            }

            foreach (var recipe in BlixRecipes.Find(assembly))
            {
                // Recipe IDs are provenance. Built-ins then discovery order win deterministically;
                // every shadowed declaration is reported.
                if (!seen.Add(recipe.Id))
                {
                    Console.Error.WriteLine(
                        $"blix cook: '{recipe.Id}' in {assembly.GetName().Name} is shadowed — that id is already taken.");
                    continue;
                }

                found.Add(recipe);
            }
        }

        return found.OrderBy(r => r.Id, StringComparer.Ordinal).ToArray();
    }

    private static IEnumerable<(Index Index, FileInfo File)> Indexes()
    {
        var root = ProjectRoot();
        IEnumerable<FileInfo> files;
        try
        {
            files = root.EnumerateFiles("*.blixapps.json", SearchOption.AllDirectories);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            yield break;
        }

        foreach (var file in files)
        {
            Index? index = null;
            try
            {
                index = JsonSerializer.Deserialize<Index>(File.ReadAllText(file.FullName), JsonOptions);
            }
            catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
            {
                // One unreadable index does not hide recipes from every other built assembly.
                Console.Error.WriteLine($"blix cook: {file.FullName} is not readable as an index — ignoring it.");
            }

            if (index is not null) yield return (index, file);
        }
    }

    /// <summary>
    /// The tree to search: the outermost solution or repository at or above here, else here.
    /// </summary>
    /// <remarks>
    /// Uses the outermost repository or solution so project-owned recipes elsewhere in the same
    /// checkout remain discoverable. Outside a repository, only the working tree and built-ins are
    /// considered.
    /// </remarks>
    private static DirectoryInfo ProjectRoot()
    {
        var from = new DirectoryInfo(Environment.CurrentDirectory);
        DirectoryInfo? found = null;

        for (var dir = from; dir is not null; dir = dir.Parent)
        {
            if (dir.EnumerateFiles("*.sln").Any() || Directory.Exists(Path.Combine(dir.FullName, ".git")))
            {
                found = dir;
            }
        }

        return found ?? from;
    }

    /// <summary>What to print when an id resolves to nothing — the cause, not only the symptom.</summary>
    internal static string UnknownRecipe(string id) =>
        $"no recipe '{id}'. Known: {string.Join(", ", All().Select(r => r.Id))}." +
        Environment.NewLine +
        "       A recipe must be compiled before cooking starts. Put project-owned recipes in a " +
        "referenced cooking project so they are available on a clean build.";

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    /// <summary>Only the parts of the app index a cook needs.</summary>
    private sealed record Index(string Assembly, IndexedRecipe[]? Recipes);

    private sealed record IndexedRecipe(string Id);
}

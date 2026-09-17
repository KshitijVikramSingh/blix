using System.Reflection;
using System.Text.Json;
using Blix.Cooked;

namespace Blix.Tools.Cook;

/// <summary>
/// Every recipe this cook can run: Blix's own, plus the ones other projects declare.
/// </summary>
/// <remarks>
/// <b>This exists because the build rule already promised it and the cook could not keep the
/// promise.</b> <c>Directory.Build.targets</c> says, of a project writing its own recipe, "a
/// project's own recipe needs no new target — it adds BlixCook directly … which is the whole
/// point: Blix's recipes and a project's take the same path." The build rule accepted the item and
/// the index recorded the recipe; only the cook was hardwired, resolving every id against
/// <c>Blix.Recipes</c> alone. RTSGame's <c>omsh</c> was declared, indexed and unreachable —
/// <c>no recipe 'omsh'. Known: fnt1, gmsh, gpro, gtex</c>.
/// <para>
/// <b>Discovery is by the index, not by scanning assemblies.</b> <c>*.blixapps.json</c> already
/// carries each recipe and names the assembly beside it, and <c>Blix.Cli</c> already finds apps
/// exactly this way — so a project becomes cookable by being built, with nothing to register. An
/// index whose <c>recipes</c> array is empty is never loaded, which is why walking a whole tree
/// costs a directory walk and not an assembly load per project.
/// </para>
/// <para>
/// <b>Build order is the one thing this cannot paper over.</b> A recipe must be compiled before
/// the build that cooks with it, the same way an MSBuild task assembly must; <c>BlixCookAssets</c>
/// runs before its own project's compile. So a recipe in a project that is REFERENCED by the one
/// cooking is ready on the first build, and a recipe in the cooking project's own assembly is
/// ready on the build after it was written. That is stated rather than worked around, because the
/// alternative is the cook silently running a stale recipe.
/// </para>
/// </remarks>
internal static class RecipeCatalog
{
    private static FoundRecipe[]? cached;

    /// <summary>Blix's own recipes plus every one found in an index under <see cref="Environment.CurrentDirectory"/>.</summary>
    internal static FoundRecipe[] All() => cached ??= Build();

    private static FoundRecipe[] Build()
    {
        // Blix's own come first and always, from a reference rather than a file: the cook is
        // useless without them, and making them discovered too would mean a cook that works only
        // from inside a built tree.
        var found = new List<FoundRecipe>(BlixRecipes.Find(typeof(Blix.Recipes.MeshRecipe).Assembly));
        var seen = new HashSet<string>(found.Select(r => r.Id), StringComparer.Ordinal);

        foreach (var (index, file) in Indexes())
        {
            // Nothing to gain by loading an assembly that declared no recipe, and a great deal to
            // lose: every test suite and tool in this tree publishes an index.
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
                // <b>First wins, and it is reported.</b> A recipe id is stamped into every file it
                // writes, so two recipes sharing one makes a cooked file's provenance ambiguous —
                // the same reason the indexer refuses a clash inside one assembly. Across
                // assemblies it cannot be refused at build time, so it is resolved in a fixed
                // order (Blix's own, then discovery order) and said out loud, rather than left to
                // depend on which directory was walked first.
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
                // Matches Blix.Cli's stance on the same file: an unreadable index is reported and
                // stepped over, never fatal.
                Console.Error.WriteLine($"blix cook: {file.FullName} is not readable as an index — ignoring it.");
            }

            if (index is not null) yield return (index, file);
        }
    }

    /// <summary>
    /// The tree to search: the outermost solution or repository at or above here, else here.
    /// </summary>
    /// <remarks>
    /// OUTERMOST rather than nearest, and no <c>blix.project</c> step — see the type remarks for
    /// why this deliberately differs from <c>Blix.Cli</c>'s otherwise identical walk. The fallback
    /// is the working directory rather than an error: a cook invoked outside any repository still
    /// has Blix's own recipes and is useful, where the launcher genuinely has nothing to do.
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
        "       A recipe must be COMPILED before the build that cooks with it, so a project cannot " +
        "use a recipe declared in its own assembly — put it in a project this one references, the " +
        "way RTSGame.Cooking is referenced by RTSGame.";

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    /// <summary>Only the parts of the app index a cook needs.</summary>
    private sealed record Index(string Assembly, IndexedRecipe[]? Recipes);

    private sealed record IndexedRecipe(string Id);
}

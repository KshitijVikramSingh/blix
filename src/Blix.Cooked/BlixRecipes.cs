using System.Reflection;

namespace Blix.Cooked;

/// <summary>A recipe, as found at run time.</summary>
public sealed record FoundRecipe(
    string Id, string Produces, string[] Consumes, string Summary, uint Version, MethodInfo Method)
{
    /// <summary>True when this recipe accepts a source file with that extension.</summary>
    public bool Accepts(string path) =>
        Consumes.Any(e => string.Equals(Path.GetExtension(path), e, StringComparison.OrdinalIgnoreCase));

    /// <summary>The path this recipe would write for that source, as a sibling.</summary>
    public string OutputFor(string sourcePath) => Path.ChangeExtension(sourcePath, Produces);

    /// <summary>Runs it.</summary>
    public CookOutcome Cook(CookRequest request) => (CookOutcome)Method.Invoke(null, new object[] { request })!;
}

/// <summary>
/// Finds the recipes an assembly declares, at run time.
/// </summary>
/// <remarks>
/// <b>The second reader of one truth.</b> <c>Blix.Tools.Apps</c> reads the same attributes out of
/// ECMA-335 metadata at build time and writes the index; this reads them off a loaded assembly.
/// They agree because they read the same declarations — and "because" is a claim, so the suite
/// checks it. If the two ever disagree, <c>blix cook</c> would be listing recipes that cannot be
/// run, or running ones it never listed.
/// </remarks>
public static class BlixRecipes
{
    /// <summary>Every recipe declared in <paramref name="assembly"/>, ordered by id.</summary>
    public static FoundRecipe[] Find(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);

        return assembly.GetTypes()
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Static))
            .Select(m => (Method: m, Attribute: m.GetCustomAttribute<RecipeAttribute>()))
            .Where(x => x.Attribute is not null)
            .Select(x => new FoundRecipe(
                x.Attribute!.Id,
                x.Attribute.Produces,
                x.Attribute.Consumes.Split(';', StringSplitOptions.RemoveEmptyEntries),
                x.Attribute.Summary,
                x.Attribute.Version,
                x.Method))
            .OrderBy(r => r.Id, StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>The recipe with that id, or null.</summary>
    public static FoundRecipe? ById(Assembly assembly, string id) =>
        Find(assembly).FirstOrDefault(r => string.Equals(r.Id, id, StringComparison.Ordinal));

    /// <summary>
    /// The recipe that claims this source file, or null when none does.
    /// </summary>
    /// <remarks>
    /// Returns null rather than guessing when two recipes accept the same extension. Two recipes
    /// wanting the same source is a real possibility — a project may want its own mesh cook — and
    /// silently picking one would make which cook ran depend on assembly load order.
    /// </remarks>
    public static FoundRecipe? For(Assembly assembly, string sourcePath)
    {
        ArgumentNullException.ThrowIfNull(sourcePath);
        var matches = Find(assembly).Where(r => r.Accepts(sourcePath)).ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }
}

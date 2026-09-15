namespace Blix.Cooked;

/// <summary>
/// Declares a cooking recipe: this source format, through this transformation, becomes that
/// engine-native one.
/// </summary>
/// <remarks>
/// <para>
/// <b>For finding, not for constraining.</b> There is no base class, no interface and no contract
/// beyond a signature the build errors on — the same deal <c>[BlixApp]</c> makes one layer up. A
/// recipe is a plain static method; this only lets the index, <c>blix cook</c> and a coverage
/// report discover that it exists without loading the assembly it lives in.
/// </para>
/// <para>
/// <b>And it lives here, in a project with no dependencies</b>, so declaring a recipe costs a
/// reference to <c>Blix.Cooked</c> and nothing else. A project cooking its own content does not
/// take a graphics device to say so.
/// </para>
/// <para>
/// <b>What a recipe is NOT.</b> It is not the format — the byte layout, the reader, the writer and
/// the preamble belong to the engine, because the runtime reads them. It is not the capability
/// either: BC7 encoding, GGX prefiltering and mesh simplification are reusable work a recipe calls.
/// A recipe is the third thing, the decision — <em>this source, these settings, that format</em> —
/// and it is the only one of the three that is not engine code. That split is what keeps Blix's own
/// recipes and a project's own the same kind of thing rather than one being blessed by living in
/// the engine.
/// </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
public sealed class RecipeAttribute : Attribute
{
    /// <param name="id">
    /// Four ASCII characters, stamped into every file this recipe writes. Four because it rides in
    /// the preamble as a 4cc, and readable because "who made this file" should be answerable by
    /// looking rather than by cross-referencing a table.
    /// </param>
    public RecipeAttribute(string id)
    {
        Id = id;
    }

    /// <summary>The 4cc stamped into everything this produces.</summary>
    public string Id { get; }

    /// <summary>The extension this writes, including the dot.</summary>
    public string Produces { get; init; } = "";

    /// <summary>Semicolon-separated source extensions this accepts, including dots.</summary>
    public string Consumes { get; init; } = "";

    /// <summary>One line, for <c>blix cook</c> and the index.</summary>
    public string Summary { get; init; } = "";

    /// <summary>
    /// Bumped whenever this recipe would produce different bytes from the same source and settings.
    /// </summary>
    /// <remarks>
    /// Recorded in every file it writes, which is what lets a re-cook be told from a rewrite — and
    /// what lets a coverage report say "cooked, but by an older recipe than the one you have".
    /// </remarks>
    public uint Version { get; init; } = 1;
}

/// <summary>One file to cook, and where to put it.</summary>
/// <remarks>
/// <b>Options are strings because they end up in the stamp as strings.</b> A recipe parses what it
/// understands and records what it used — and recording the parsed form rather than the raw input
/// is deliberate, so a default that changes between versions shows up as a different stamp rather
/// than as an identical one.
/// </remarks>
public sealed record CookRequest(
    string SourcePath,
    string OutputPath,
    IReadOnlyDictionary<string, string>? Options = null)
{
    /// <summary>Reads a flag, defaulting when absent.</summary>
    public bool Flag(string name, bool fallback = false) =>
        Options is not null && Options.TryGetValue(name, out var v)
            ? v is not ("0" or "false" or "no")
            : fallback;

    /// <summary>Reads a number, defaulting when absent or unparseable.</summary>
    public int Number(string name, int fallback = 0) =>
        Options is not null && Options.TryGetValue(name, out var v) && int.TryParse(v, out var n) ? n : fallback;

    /// <summary>Reads a float, defaulting when absent or unparseable.</summary>
    public float Real(string name, float fallback = 0f) =>
        Options is not null && Options.TryGetValue(name, out var v)
        && float.TryParse(v, System.Globalization.NumberStyles.Float,
                          System.Globalization.CultureInfo.InvariantCulture, out var n)
            ? n : fallback;
}

/// <summary>What a recipe did.</summary>
/// <param name="Wrote">False when the output was already current and nothing was written.</param>
/// <param name="Detail">One line for the log — what came out, and anything worth knowing about it.</param>
public readonly record struct CookOutcome(bool Wrote, string Detail)
{
    public static CookOutcome Skipped(string detail) => new(false, detail);

    public static CookOutcome Written(string detail) => new(true, detail);
}

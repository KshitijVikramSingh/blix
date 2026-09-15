namespace Blix.Cooked;

/// <summary>Properties a cooked artifact declares about itself.</summary>
[Flags]
public enum CookedFlags : uint
{
    None = 0,

    /// <summary>
    /// The source file is still needed at load time; this artifact is an optimisation, not a
    /// replacement.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Set on <c>.blixmesh</c> today, and that is a debt rather than a design.</b> The cook
    /// replaces geometry only — every material factor, texture reference and alpha mode is still
    /// parsed out of the sibling glTF on every load, in full. So a cooked mesh is not a thing you
    /// can ship on its own, not a thing you can open on its own, and not a thing you can move
    /// without moving the glTF beside it.
    /// </para>
    /// <para>
    /// It is a flag rather than a comment so that the debt is <em>visible to a tool</em>: a coverage
    /// report can say which artifacts still pin their sources, and stage K-F of the cook arc is
    /// finished exactly when this stops being set on a mesh.
    /// </para>
    /// </remarks>
    SourceRequired = 1 << 0,
}

/// <summary>
/// What a recipe records about a cook: who did it, to what, with which settings.
/// </summary>
/// <remarks>
/// <para>
/// <b>A recipe cannot write a cooked file without one of these</b> — every writer takes it, and the
/// writer (which belongs to the format, in the engine) lays the preamble down itself. That is the
/// whole enforcement mechanism, and it is deliberately not an interface: an interface can be
/// implemented wrongly, and a source generator would generate what a required parameter already
/// forces.
/// </para>
/// <para>
/// <b>Why <see cref="Parameters"/> exists at all.</b> <c>--flip-v</c>, <c>--split</c> and
/// <c>--no-split-foliage</c> each change the bytes a cook emits and none of them was recorded
/// anywhere, so the cooked half of this tree could not be reproduced from the tree. Recording the
/// settings verbatim is what makes "cook it again and compare" a test that can be written.
/// </para>
/// <para>
/// <b>Why three source fields and not one.</b> A modification time alone is what the loader has
/// today, and a <c>git checkout</c> rewrites it — which is why 29 cooked files in this tree have
/// timestamps that may mean staleness or may mean nothing. Size costs eight bytes and catches what
/// a rewritten mtime hides; <see cref="SourceHash"/> is the one that actually settles it, left
/// optional because not every recipe has the bytes to hand cheaply.
/// </para>
/// </remarks>
/// <param name="Recipe">Four ASCII characters naming the recipe that produced this.</param>
/// <param name="RecipeVersion">The recipe's own version. Bumped when its output would differ.</param>
/// <param name="SourcePath">What it was made from, as the cook saw it. Informational.</param>
/// <param name="SourceTicks">Source last-write time in UTC ticks, or 0 when unknown.</param>
/// <param name="SourceSize">Source length in bytes, or 0 when unknown.</param>
/// <param name="SourceHash">Content hash of the source, or 0 when not computed.</param>
/// <param name="Parameters">The recipe's settings, verbatim, in whatever form it chose.</param>
/// <param name="Flags">What this artifact declares about itself.</param>
public readonly record struct CookStamp(
    string Recipe,
    uint RecipeVersion,
    string SourcePath,
    long SourceTicks,
    long SourceSize,
    ulong SourceHash,
    string Parameters,
    CookedFlags Flags)
{
    /// <summary>A recipe id is exactly four ASCII characters, so it fits a 4cc in the preamble.</summary>
    public const int RecipeIdLength = 4;

    /// <summary>
    /// Builds a stamp, reading the source's size and modification time off disk.
    /// </summary>
    /// <remarks>
    /// The ordinary way to make one. A recipe that has the source bytes in hand may set
    /// <see cref="SourceHash"/> afterwards with <c>stamp with { SourceHash = … }</c>.
    /// </remarks>
    public static CookStamp Of(
        string recipe,
        uint recipeVersion,
        string sourcePath,
        string parameters = "",
        CookedFlags flags = CookedFlags.None)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(recipe);
        ArgumentNullException.ThrowIfNull(sourcePath);

        if (recipe.Length != RecipeIdLength)
        {
            throw new ArgumentException(
                $"Recipe id '{recipe}' must be exactly {RecipeIdLength} characters; it is stored as a 4cc.",
                nameof(recipe));
        }

        long ticks = 0, size = 0;
        try
        {
            var info = new FileInfo(sourcePath);
            if (info.Exists)
            {
                ticks = info.LastWriteTimeUtc.Ticks;
                size = info.Length;
            }
        }
        catch (IOException)
        {
            // A source we cannot stat still cooks; it just records nothing about staleness, which
            // is exactly what the zeroes mean. Failing the cook here would make an unreadable
            // timestamp fatal to a transformation that does not need one.
        }

        return new CookStamp(recipe, recipeVersion, sourcePath, ticks, size, 0UL, parameters ?? "", flags);
    }

    /// <summary>True when this artifact still needs its source file present at load.</summary>
    public bool SourceRequired => (Flags & CookedFlags.SourceRequired) != 0;

    /// <summary>
    /// Whether the source on disk still looks like the one this was cooked from.
    /// </summary>
    /// <remarks>
    /// <b>Reports; does not judge.</b> Returns <c>null</c> when the stamp recorded nothing to
    /// compare against — which is a third answer and not a "yes", and flattening it to one would
    /// reproduce the exact bug this arc exists to fix: a loader whose entire staleness check was
    /// <c>File.Exists</c> and which therefore could not tell a current file from a stale one.
    /// </remarks>
    public bool? MatchesSourceOnDisk(string sourcePath)
    {
        ArgumentNullException.ThrowIfNull(sourcePath);
        if (SourceTicks == 0 && SourceSize == 0) return null;

        try
        {
            var info = new FileInfo(sourcePath);
            if (!info.Exists) return false;
            if (SourceSize != 0 && info.Length != SourceSize) return false;
            if (SourceTicks != 0 && info.LastWriteTimeUtc.Ticks != SourceTicks) return false;
            return true;
        }
        catch (IOException)
        {
            return null;
        }
    }
}

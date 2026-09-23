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
    /// It is a flag rather than a comment so that the debt is <em>visible to a tool</em>: a coverage
    /// report can say which artifacts still pin their sources, and which pin them for what.
    /// </para>
    /// </remarks>
    SourceRequired = 1 << 0,

    /// <summary>
    /// The only thing still wanted from the source is image BYTES. Everything else this artifact
    /// describes, it describes itself. Always accompanies <see cref="SourceRequired"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Distinguishes an artifact that carries all model/material data but still needs source image
    /// bytes from one that depends on its source more generally.
    /// </para>
    /// <para>
    /// Image grouping and addressing remain project policy—atlas, array, palette, or pool—so the
    /// common format records that boundary rather than choosing one.
    /// </para>
    /// </remarks>
    SourceRequiredForImagesOnly = 1 << 1,
}

/// <summary>
/// What a recipe records about a cook: who did it, to what, with which settings.
/// </summary>
/// <remarks>
/// <para>Every cooked writer requires a stamp and writes it through the common preamble.</para>
/// <para>
/// <see cref="Parameters"/> records the recipe's normalized settings so byte-affecting choices such
/// as UV flipping or mesh splitting are reproducible and comparable.
/// </para>
/// <para>
/// Source time and size support the current freshness report. <see cref="SourceHash"/> reserves a
/// stronger content identity, but current recipes leave it zero and freshness does not consult it.
/// </para>
/// </remarks>
/// <param name="Recipe">Four ASCII characters naming the recipe that produced this.</param>
/// <param name="RecipeVersion">The recipe's own version. Bumped when its output would differ.</param>
/// <param name="SourcePath">
/// What it was made from, relative to this artifact's own directory and with forward
/// slashes — so it means the same thing on every machine. Usually just a filename.
/// </param>
/// <param name="SourceTicks">Source last-write time in UTC ticks, or 0 when unknown.</param>
/// <param name="SourceSize">Source length in bytes, or 0 when unknown.</param>
/// <param name="SourceHash">Content hash of the source, or 0 when not computed.</param>
/// <param name="Parameters">The recipe's normalized settings in its canonical string form.</param>
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
    /// <see cref="SourceHash"/> afterwards with <c>stamp with { SourceHash = … }</c>; current shipped
    /// recipes do not do so.
    /// </remarks>
    /// <param name="outputPath">
    /// Where the cooked file is going. The recorded source path is stored relative to this,
    /// which for the ordinary sibling case is just a filename.
    /// </param>
    /// <remarks>
    /// Relative paths keep committed output machine-independent and avoid embedding developer
    /// directories. Forward slashes make the recorded relationship platform-independent.
    /// </remarks>
    public static CookStamp Of(
        string recipe,
        uint recipeVersion,
        string sourcePath,
        string outputPath,
        string parameters = "",
        CookedFlags flags = CookedFlags.None)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(recipe);
        ArgumentNullException.ThrowIfNull(sourcePath);
        ArgumentNullException.ThrowIfNull(outputPath);

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

        string recorded;
        try
        {
            var from = Path.GetDirectoryName(Path.GetFullPath(outputPath));
            recorded = string.IsNullOrEmpty(from)
                ? Path.GetFileName(sourcePath)
                : Path.GetRelativePath(from, Path.GetFullPath(sourcePath));
        }
        catch (ArgumentException)
        {
            recorded = Path.GetFileName(sourcePath);
        }

        // Forward slashes, so a file cooked on macOS and one cooked on Windows record the same
        // string for the same relationship.
        recorded = recorded.Replace('\\', '/');

        return new CookStamp(recipe, recipeVersion, recorded, ticks, size, 0UL, parameters ?? "", flags);
    }

    /// <summary>True when this artifact still needs its source file present at load.</summary>
    public bool SourceRequired => (Flags & CookedFlags.SourceRequired) != 0;

    /// <summary>Whether the producer identity and recorded source metadata still match.</summary>
    /// <remarks>
    /// Recipe-specific code compares <see cref="Parameters"/> as well, because only the recipe can
    /// construct its normalized settings. Keeping the common half here prevents drivers from
    /// falling back to output timestamps or format-version checks.
    /// </remarks>
    public bool MatchesProducerAndSource(string recipe, uint recipeVersion, string sourcePath) =>
        string.Equals(Recipe, recipe, StringComparison.Ordinal)
        && RecipeVersion == recipeVersion
        && MatchesSourceOnDisk(sourcePath) == true;

    /// <summary>
    /// Whether the source on disk still looks like the one this was cooked from.
    /// </summary>
    /// <remarks>
    /// Reports metadata agreement; it does not decide whether a loader may use the artifact.
    /// Returns <c>null</c> when the stamp recorded no comparable time or size.
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

namespace Blix.Cooked;

/// <summary>
/// Reads any Blix cooked artifact's provenance, without knowing which format it is.
/// </summary>
/// <remarks>
/// <para>
/// Uses only the common preamble, so callers need no graphics device or format-specific reader.
/// </para>
/// </remarks>
public static class CookedFile
{
    /// <summary>Reads just the preamble. Cheap: one open and one short read.</summary>
    /// <exception cref="AssetImportException">The file is not a Blix cooked artifact.</exception>
    public static CookedHeader ReadHeader(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        using var stream = File.OpenRead(path);
        return CookPreamble.Read(stream, path);
    }

    /// <summary>
    /// Reads the preamble, or null when the file is missing or is not a cooked artifact.
    /// </summary>
    /// <remarks>
    /// For walking a tree, where "this is not one of ours" is an ordinary answer rather than a
    /// refusal. A tool that reports on a directory should not stop at the first stray file.
    /// </remarks>
    public static CookedHeader? TryReadHeader(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        try
        {
            return ReadHeader(path);
        }
        catch (AssetImportException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    /// <summary>What a cooked artifact's source looks like now, relative to when it was cooked.</summary>
    public enum Freshness
    {
        /// <summary>The stamp recorded nothing to compare against.</summary>
        Unknown,

        /// <summary>The source is gone.</summary>
        SourceMissing,

        /// <summary>The source still matches what this was cooked from.</summary>
        Current,

        /// <summary>The source has changed since; this artifact is stale.</summary>
        Stale,
    }

    /// <summary>
    /// Compares a cooked artifact against a source on disk.
    /// </summary>
    /// <remarks>
    /// Reports freshness without deciding whether a loader may use the artifact. Timestamp changes
    /// can reflect a checkout rather than changed content, and current stamps do not provide a
    /// content-verified decision.
    /// </remarks>
    public static Freshness Compare(in CookedHeader header, string sourcePath)
    {
        ArgumentNullException.ThrowIfNull(sourcePath);
        return header.Stamp.MatchesSourceOnDisk(sourcePath) switch
        {
            true => Freshness.Current,
            false when !File.Exists(sourcePath) => Freshness.SourceMissing,
            false => Freshness.Stale,
            null => Freshness.Unknown,
        };
    }
}

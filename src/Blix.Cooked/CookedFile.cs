namespace Blix.Cooked;

/// <summary>
/// Reads any Blix cooked artifact's provenance, without knowing which format it is.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is what the shared preamble is for.</b> Before it, answering "what made this file, from
/// what, and is it current" meant knowing in advance whether you were holding a
/// <c>.blixmesh</c>, a <c>.blixtex</c> or a <c>.blixprobe</c>, and then calling a different reader
/// with a different header layout and a differently-sized version field. So nothing ever asked —
/// the one type that could have reported it, <c>AssetLoadReport</c>, had four states and zero
/// emitters.
/// </para>
/// <para>
/// A tool built on this needs no graphics device, no format knowledge, and no list of extensions
/// to keep up to date. A format added tomorrow is readable by it on the day it exists.
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
    /// <b>Reports; does not refuse.</b> A loader that rejected a stale file would turn every
    /// <c>git checkout</c> into a build break, because a checkout rewrites modification times
    /// without changing a byte. Making staleness <em>visible</em> costs nothing and is what was
    /// actually missing — the loader's entire check was <c>File.Exists</c>, so a cooked file older
    /// than its source was preferred over the source, silently, for as long as it sat there.
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

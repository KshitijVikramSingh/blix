using System.Text;

namespace Blix.Cooked;

/// <summary>
/// The block every Blix cooked artifact begins with, and the only way to write or read one.
/// </summary>
/// <remarks>
/// <para>
/// <b>Three formats existed before this and none of them knew it was one of three.</b>
/// <c>.blixtex</c> announced itself as <c>"BLIX"</c> while the other two used <c>BLX*</c>; its
/// version was a <c>ushort</c> where theirs were <c>uint</c>. The consequence was not cosmetic — it
/// meant no single function could read any Blix cooked file's magic and version, which is the most
/// basic thing a family of formats gives you, and it is why nothing in the tree could report on
/// cooked output without knowing in advance what it was looking at.
/// </para>
/// <para>
/// So the preamble is fixed, identical across formats, and self-describing:
/// <see cref="PreambleBytes"/> sits at offset 8 so twelve bytes are enough to learn what a file is,
/// what version it is, and where its own header starts. A reader that understands none of the three
/// formats can still report a file's provenance.
/// </para>
/// <code>
///   offset  size  field
///   ------------------------------------------------------------------
///   0       4     magic           4cc, per format — "BLXM" / "BLXT" / "BLXP"
///   4       4     formatVersion   u32, one width for every format
///   8       4     preambleBytes   u32, where the format's own header starts
///   12      4     recipe          4cc, which recipe produced this
///   16      4     recipeVersion   u32
///   20      4     flags           u32, CookedFlags
///   24      8     sourceTicks     i64, source last-write UTC ticks (0 = unknown)
///   32      8     sourceSize      i64, source byte length (0 = unknown)
///   40      8     sourceHash      u64, content hash (0 = not computed)
///   48      4     sourcePathLen   u32
///   52      4     paramsLen       u32
///   ------- 56 bytes fixed -------
///   56      ..    sourcePath  UTF-8
///           ..    parameters  UTF-8
///           ..    zero padding to a 4-byte boundary
///   ------- preambleBytes -------
///   the format's own header follows
/// </code>
/// <para>
/// Little-endian throughout, matching all three formats as they already were.
/// </para>
/// </remarks>
public static class CookPreamble
{
    /// <summary>Bytes before the variable-length tail. Twelve of them locate everything else.</summary>
    public const int FixedBytes = 56;

    /// <summary>Offset of <c>preambleBytes</c>, so a reader can seek past a format it does not know.</summary>
    public const int PreambleBytesOffset = 8;

    /// <summary>Writes the preamble and returns where the format's own header begins.</summary>
    public static int Write(Stream stream, uint magic, uint formatVersion, in CookStamp stamp)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (stamp.Recipe is not { Length: CookStamp.RecipeIdLength })
        {
            throw new ArgumentException(
                $"Recipe id must be exactly {CookStamp.RecipeIdLength} characters; got '{stamp.Recipe}'.",
                nameof(stamp));
        }

        var sourceBytes = Encoding.UTF8.GetBytes(stamp.SourcePath ?? "");
        var paramBytes = Encoding.UTF8.GetBytes(stamp.Parameters ?? "");
        var unpadded = FixedBytes + sourceBytes.Length + paramBytes.Length;
        var padding = (4 - (unpadded % 4)) % 4;
        var total = unpadded + padding;

        var w = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        w.Write(magic);
        w.Write(formatVersion);
        w.Write((uint)total);
        w.Write(FourCc(stamp.Recipe));
        w.Write(stamp.RecipeVersion);
        w.Write((uint)stamp.Flags);
        w.Write(stamp.SourceTicks);
        w.Write(stamp.SourceSize);
        w.Write(stamp.SourceHash);
        w.Write((uint)sourceBytes.Length);
        w.Write((uint)paramBytes.Length);
        w.Write(sourceBytes);
        w.Write(paramBytes);
        for (var i = 0; i < padding; i++) w.Write((byte)0);
        w.Flush();

        return total;
    }

    /// <summary>
    /// Reads the preamble, leaving <paramref name="stream"/> positioned at the format's own header.
    /// </summary>
    /// <remarks>
    /// Refuses through <see cref="AssetImportException"/> rather than a parser exception, because a
    /// bad cooked file is the engine declining an asset — the same sentence a bad glTF gets, from
    /// the same type, so a tool catches one thing and survives both.
    /// </remarks>
    public static CookedHeader Read(Stream stream, string path)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(path);

        var start = stream.Position;
        var fixedBuf = new byte[FixedBytes];
        try
        {
            stream.ReadExactly(fixedBuf, 0, FixedBytes);
        }
        catch (EndOfStreamException)
        {
            throw new AssetImportException(
                path, null, $"not a Blix cooked file — shorter than the {FixedBytes}-byte preamble");
        }

        var magic = BitConverter.ToUInt32(fixedBuf, 0);
        var formatVersion = BitConverter.ToUInt32(fixedBuf, 4);
        var preambleBytes = BitConverter.ToUInt32(fixedBuf, 8);
        var recipe = FromFourCc(BitConverter.ToUInt32(fixedBuf, 12));
        var recipeVersion = BitConverter.ToUInt32(fixedBuf, 16);
        var flags = (CookedFlags)BitConverter.ToUInt32(fixedBuf, 20);
        var sourceTicks = BitConverter.ToInt64(fixedBuf, 24);
        var sourceSize = BitConverter.ToInt64(fixedBuf, 32);
        var sourceHash = BitConverter.ToUInt64(fixedBuf, 40);
        var sourceLen = BitConverter.ToUInt32(fixedBuf, 48);
        var paramLen = BitConverter.ToUInt32(fixedBuf, 52);

        if (preambleBytes < FixedBytes || preambleBytes > int.MaxValue ||
            (long)sourceLen + paramLen > preambleBytes - FixedBytes)
        {
            throw new AssetImportException(
                path, null,
                $"not a Blix cooked file — its preamble claims {preambleBytes} bytes, which does not fit its own fields");
        }

        var tail = new byte[preambleBytes - FixedBytes];
        try
        {
            stream.ReadExactly(tail, 0, tail.Length);
        }
        catch (EndOfStreamException)
        {
            throw new AssetImportException(path, null, "truncated Blix cooked file — the preamble runs past the end");
        }

        var sourcePath = Encoding.UTF8.GetString(tail, 0, (int)sourceLen);
        var parameters = Encoding.UTF8.GetString(tail, (int)sourceLen, (int)paramLen);

        stream.Position = start + preambleBytes;
        return new CookedHeader(
            magic,
            formatVersion,
            (int)preambleBytes,
            new CookStamp(recipe, recipeVersion, sourcePath, sourceTicks, sourceSize, sourceHash, parameters, flags));
    }

    internal static uint FourCc(string s) =>
        (uint)(s[0] | (s[1] << 8) | (s[2] << 16) | (s[3] << 24));

    internal static string FromFourCc(uint v) =>
        new(new[] { (char)(v & 0xFF), (char)((v >> 8) & 0xFF), (char)((v >> 16) & 0xFF), (char)((v >> 24) & 0xFF) });

    /// <summary>Renders a 4cc for a message, so a mismatch reads as text rather than as hex.</summary>
    public static string Describe(uint magic)
    {
        var s = FromFourCc(magic);
        foreach (var c in s)
        {
            if (c is < ' ' or > '~') return $"0x{magic:X8}";
        }

        return $"'{s}'";
    }
}

/// <summary>A cooked artifact's preamble, as read.</summary>
/// <param name="Magic">The format's 4cc.</param>
/// <param name="FormatVersion">The format's version.</param>
/// <param name="PreambleBytes">Where the format's own header starts.</param>
/// <param name="Stamp">What the recipe recorded.</param>
public readonly record struct CookedHeader(uint Magic, uint FormatVersion, int PreambleBytes, CookStamp Stamp)
{
    /// <summary>
    /// Refuses unless this is the expected format at the expected version.
    /// </summary>
    /// <remarks>
    /// Both halves matter and they say different things: a wrong magic means <em>this is not that
    /// kind of file</em>, and a wrong version means <em>it is, and it is old</em> — which is
    /// actionable, so the message says which recipe to re-run.
    /// </remarks>
    public CookedHeader Require(uint magic, uint formatVersion, string path, string extension)
    {
        if (Magic != magic)
        {
            throw new AssetImportException(
                path, null,
                $"not a {extension} file — its magic is {CookPreamble.Describe(Magic)}, expected {CookPreamble.Describe(magic)}");
        }

        if (FormatVersion != formatVersion)
        {
            throw new AssetImportException(
                path, null,
                $"{extension} version {FormatVersion}, but this Blix reads version {formatVersion} — re-cook it");
        }

        return this;
    }
}

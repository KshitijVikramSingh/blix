using Blix.Cooked;
using Blix.Graphics;

namespace Blix.Graphics.Images;

// Engine-native binary texture container. Runtime loading reads a 28-byte format header and an
// eight-byte extent per mip, then copies already encoded mip bytes into GPU staging memory.
//
// File layout (little-endian), v3:
//
//   The shared Blix cooked preamble first -- see Blix.Cooked/CookPreamble.cs.
//   Then this format's own header:
//
//   offset  size  field           (relative to the end of the preamble)
//   ---------------------------------
//   0       4     kind        = 1 (Texture2D)
//   4       4     format      (TextureFormat enum value)
//   8       4     width
//   12      4     height
//   16      4     mipCount
//   20      4     dataOffset  (absolute, from the start of the FILE)
//   24      4     flags       (bit 0 = sRGB; bit 1 = normal-map)
//   --------- 28 bytes (format header) ---------
//           8*N   mip table (per mip: uint32 byteOffset relative to file
//                 start, uint32 byteLength); mipCount entries
//   --------- dataOffset ---------
//   packed mip data: mip 0 first, then mip 1, etc.
//
// The reader accepts v3 only. Earlier layouts must be recooked.
public static class BlixTex
{
    public const uint Magic = 0x54584C42; // "BLXT" little-endian

    /// <summary>The recipe id the shipped texture cook stamps.</summary>
    public const string ShippedRecipe = "gtex";

    /// <summary>
    /// The shipped texture transformation version. It covers role-aware BC5/BC7 encoding,
    /// unflipped decode, alpha-weighted mip colour, cutout-coverage preservation, and complete
    /// role/encoder provenance.
    /// </summary>
    public const uint ShippedRecipeVersion = 6;
    public const uint Version3 = 3;
    public const uint KindTexture2D = 1;
    public const int HeaderSize = 28;
    public const int MipEntrySize = 8;

    [Flags]
    public enum Flags : uint
    {
        None = 0,
        Srgb = 1 << 0,
        NormalMap = 1 << 1,
    }
}

// Loaded BlixTex contents. MipBytes[i] is the i-th mip level's packed
// pixel/block data, ready for direct upload via
// IGraphicsDevice.CreateTexture2DMipped.
public sealed record BlixTexImage(
    int Width,
    int Height,
    TextureFormat Format,
    IReadOnlyList<byte[]> MipBytes,
    BlixTex.Flags Flags);

public static class BlixTexWriter
{
    /// <param name="stamp">See <c>BlixMeshWriter.Write</c> — required, for the same reason.</param>
    public static void Write(string path, BlixTexImage image, in CookStamp stamp)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(image);
        if (image.MipBytes.Count == 0)
        {
            throw new ArgumentException("Image must have at least one mip.", nameof(image));
        }
        using var stream = File.Create(path);
        var preambleBytes = CookPreamble.Write(stream, BlixTex.Magic, BlixTex.Version3, stamp);
        using var writer = new BinaryWriter(stream);

        var mipCount = (uint)image.MipBytes.Count;
        // Absolute, so a mip extent stays a straight seek even though the preamble in front of it
        // is variable-length.
        var dataOffset = (uint)(preambleBytes + BlixTex.HeaderSize + mipCount * BlixTex.MipEntrySize);

        // Format header.
        writer.Write(BlixTex.KindTexture2D);
        writer.Write((uint)image.Format);
        writer.Write((uint)image.Width);
        writer.Write((uint)image.Height);
        writer.Write(mipCount);
        writer.Write(dataOffset);
        writer.Write((uint)image.Flags);
        if (stream.Position != preambleBytes + BlixTex.HeaderSize)
        {
            throw new InvalidOperationException(
                $"BlixTex format header was {stream.Position - preambleBytes} bytes; expected {BlixTex.HeaderSize}.");
        }

        // Mip table.
        var cursor = dataOffset;
        foreach (var mip in image.MipBytes)
        {
            writer.Write(cursor);
            writer.Write((uint)mip.Length);
            cursor += (uint)mip.Length;
        }

        // Mip data.
        foreach (var mip in image.MipBytes)
        {
            writer.Write(mip);
        }
    }
}

// Header and mip extents for deferred reads. No pixel data is retained by this handle.
public sealed record BlixTexLazyHandle(
    string Path,
    TextureFormat Format,
    int Width,
    int Height,
    int MipCount,
    BlixTex.Flags Flags,
    IReadOnlyList<(int Offset, int Length)> MipExtents,
    CookedHeader? Cooked = null);

public static class BlixTexReader
{
    // Parses only the preamble, format header, and mip table. Individual mip bytes remain on disk.
    public static BlixTexLazyHandle ReadHandle(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        using var stream = File.OpenRead(path);
        var header = CookPreamble.Read(stream, path).Require(BlixTex.Magic, BlixTex.Version3, path, ".blixtex");

        return AssetImportException.Refusing(path, () =>
        {
            var headerBuf = new byte[BlixTex.HeaderSize];
            stream.ReadExactly(headerBuf, 0, BlixTex.HeaderSize);
            var kind = BitConverter.ToUInt32(headerBuf, 0);
            if (kind != BlixTex.KindTexture2D)
            {
                throw new AssetImportException(
                    path, null, $"a .blixtex of kind {kind}; this Blix reads Texture2D (1) only");
            }
            var format = (TextureFormat)BitConverter.ToUInt32(headerBuf, 4);
            var width = (int)BitConverter.ToUInt32(headerBuf, 8);
            var height = (int)BitConverter.ToUInt32(headerBuf, 12);
            var mipCount = (int)BitConverter.ToUInt32(headerBuf, 16);
            var dataOffset = (int)BitConverter.ToUInt32(headerBuf, 20);
            var flags = (BlixTex.Flags)BitConverter.ToUInt32(headerBuf, 24);

            // Read mip table.
            var tableSize = mipCount * BlixTex.MipEntrySize;
            var tableBuf = new byte[tableSize];
            stream.ReadExactly(tableBuf, 0, tableSize);
            var extents = new (int Offset, int Length)[mipCount];
            for (var i = 0; i < mipCount; i++)
            {
                var off = (int)BitConverter.ToUInt32(tableBuf, i * BlixTex.MipEntrySize);
                var len = (int)BitConverter.ToUInt32(tableBuf, i * BlixTex.MipEntrySize + 4);
                extents[i] = (off, len);
            }

            return new BlixTexLazyHandle(path, format, width, height, mipCount, flags, extents, header);
        }, ".blixtex");
    }

    // Reads one mip's bytes from disk. Cheap (single file open + seek +
    // read). Use the lazy handle's MipExtents[level] for the byte range
    // -- this method validates it against the file's length.
    public static byte[] ReadMip(BlixTexLazyHandle handle, int mipLevel)
    {
        ArgumentNullException.ThrowIfNull(handle);
        if (mipLevel < 0 || mipLevel >= handle.MipCount)
        {
            throw new ArgumentOutOfRangeException(nameof(mipLevel));
        }
        var (offset, length) = handle.MipExtents[mipLevel];
        var buf = new byte[length];
        using var stream = File.OpenRead(handle.Path);
        stream.Seek(offset, SeekOrigin.Begin);
        stream.ReadExactly(buf, 0, length);
        return buf;
    }

    // Keeps one file open while the progressive uploader requests mips from smallest to finest.
    // Serving mip 0 closes it because the descending upload contract makes that the final read.
    // Random or ascending callers should use ReadMip or ReadAllMips instead.
    public static Func<int, byte[]> CreateBufferedMipReader(BlixTexLazyHandle handle)
    {
        ArgumentNullException.ThrowIfNull(handle);
        FileStream? stream = null;
        return level =>
        {
            if (level < 0 || level >= handle.MipCount)
            {
                throw new ArgumentOutOfRangeException(nameof(level));
            }
            stream ??= File.OpenRead(handle.Path);
            var (offset, length) = handle.MipExtents[level];
            var buf = new byte[length];
            stream.Seek(offset, SeekOrigin.Begin);
            stream.ReadExactly(buf, 0, length);
            if (level == 0)
            {
                // Finest mip served last: release the handle now rather than
                // waiting for the closure to be GC'd.
                stream.Dispose();
                stream = null;
            }
            return buf;
        };
    }

    // Reads every mip in one file open (vs ReadMip's open-per-mip). For the
    // synchronous "load all mips now" path; the streamed path uses
    // CreateBufferedMipReader instead.
    public static byte[][] ReadAllMips(BlixTexLazyHandle handle)
    {
        ArgumentNullException.ThrowIfNull(handle);
        using var stream = File.OpenRead(handle.Path);
        var mips = new byte[handle.MipCount][];
        for (var level = 0; level < handle.MipCount; level++)
        {
            var (offset, length) = handle.MipExtents[level];
            var buf = new byte[length];
            stream.Seek(offset, SeekOrigin.Begin);
            stream.ReadExactly(buf, 0, length);
            mips[level] = buf;
        }
        return mips;
    }

    /// <summary>Reads a whole cooked texture, mips and all.</summary>
    /// <remarks>
    /// Header validation and parsing stay centralized in <see cref="ReadHandle"/>; this method
    /// selects the eager all-mips read strategy.
    /// </remarks>
    public static BlixTexImage Read(string path)
    {
        var handle = ReadHandle(path);
        return new BlixTexImage(handle.Width, handle.Height, handle.Format, ReadAllMips(handle), handle.Flags);
    }
}

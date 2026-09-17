using Blix.Cooked;
using Blix.Graphics;

namespace Blix.Graphics.Images;

// Engine-native binary texture container. Cooked from PNG/JPEG (or HDR) at
// offline cook time; loaded at runtime with no decode -- just a single
// 32-byte header read + an 8-byte-per-mip table lookup + a memcpy of each
// mip's bytes into a GPU staging buffer. Skips ~1-2 seconds of stb_image
// decode per 4K texture, plus enables BCn-compressed formats that GL can't
// generate mips for at upload time.
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
// v1 was a single-mip Rgba8-only variant with no mip table; v2 added the table.
//
// v3 is where this format stopped being its own island. It announced itself as
// "BLIX" while .blixmesh and .blixprobe used BLX*, and its version was a ushort
// where theirs were uint -- so no single function could read any Blix cooked
// file's magic and version, which is the most basic thing a family of formats
// gives you. Magic is now "BLXT" and the version is a uint like everyone
// else's, in the shared preamble. Re-cook to migrate; the cook is fast.
public static class BlixTex
{
    public const uint Magic = 0x54584C42; // "BLXT" little-endian

    /// <summary>The recipe id the shipped texture cook stamps.</summary>
    public const string ShippedRecipe = "gtex";

    /// <summary>The texture cook's own version — see BlixMesh.MeshRecipeVersion for why.</summary>
    // v2: normal maps cook to BC5 rather than BC7. NOT smaller — BC5, BC7 and BC6h are all 16
    // bytes per 4x4 block, as TextureFormatExtensions.MipByteCount says in one line. What changes
    // is how those bits are spent: BC5 gives two channels a BC4-style endpoint pair each, where
    // BC7 divides one block across three or four. A tangent-space normal only needs XY, so this is
    // strictly more precision for the same bytes. Bumping this re-cooks every texture in the tree.
    // v3: images are no longer flipped on decode. The loader had carried a GL-era y-flip, so every
    // cooked texture held upside-down pixels; removing it changes the bytes of every .blixtex.
    public const uint ShippedRecipeVersion = 3;
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

// Lightweight handle returned by BlixTexReader.ReadHandle for the lazy
// upload path -- just header info + the mip table's (offset, length)
// pairs. No pixel data loaded yet. Use ReadMip(handle, level) to pull one
// mip's bytes at upload time. Cheap to keep around (~hundreds of bytes
// per texture) so the importer can hold them while the actual mip bytes
// stay on disk.
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
    // Lazy read: parses just the header + mip table. The returned handle
    // can be used to read individual mips on demand without loading the
    // pixel data into memory upfront. Used by the streamed-upload path
    // to keep peak load-time memory low -- only the mips actively being
    // uploaded sit in RAM.
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

    // Returns a mip reader that opens the .blixtex ONCE -- on the first mip
    // request -- and serves every subsequent mip by seeking within that same
    // open handle, instead of re-opening the file per mip like ReadMip does.
    //
    // Why this exists: the streamed-upload path requests mips one at a time,
    // smallest (mipCount-1) first down to finest (0). ReadMip's open-per-call
    // pattern means MipCount opens per texture; across a scene that's thousands
    // of file opens. On a volume with high per-open latency but ample bandwidth
    // (e.g. an external SSD: ~1.3ms/open vs ~0.01ms internal, but multi-GB/s
    // sequential), those opens dominate load time and starve the per-frame
    // upload budget. Collapsing to one open per texture removes that cost while
    // keeping per-mip granularity for the GPU uploads.
    //
    // The handle is closed once the finest mip (level 0) has been served -- the
    // upload queue enqueues levels descending to 0, so 0 is always last. RAM
    // stays at one open FileStream (no whole-file buffering); since the upload
    // queue drains a texture's mips contiguously, only one stream is open at a
    // time. Intended for the streamed (descending) path; for an ascending or
    // random read of all mips, use ReadAllMips.
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
    /// <b>Goes through <see cref="ReadHandle"/> rather than parsing the header a second time.</b>
    /// It used to be its own full parser over <c>File.ReadAllBytes</c> — a second copy of the same
    /// offsets, the same magic check and the same version check — and the v3 preamble change is
    /// what surfaced it: the two copies had to be edited together, which is the definition of the
    /// problem. One parser, two read strategies.
    /// </remarks>
    public static BlixTexImage Read(string path)
    {
        var handle = ReadHandle(path);
        return new BlixTexImage(handle.Width, handle.Height, handle.Format, ReadAllMips(handle), handle.Flags);
    }
}

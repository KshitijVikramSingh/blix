using Blix.Graphics;

namespace Blix.Graphics.Images;

// Engine-native binary texture container. Cooked from PNG/JPEG (or HDR) at
// offline cook time; loaded at runtime with no decode -- just a single
// 32-byte header read + an 8-byte-per-mip table lookup + a memcpy of each
// mip's bytes into a GPU staging buffer. Skips ~1-2 seconds of stb_image
// decode per 4K texture, plus enables BCn-compressed formats that GL can't
// generate mips for at upload time.
//
// File layout (little-endian), v2:
//
//   offset  size  field
//   ---------------------------------
//   0       4     magic       = "BLIX"
//   4       2     version     = 2
//   6       2     kind        = 1 (Texture2D)
//   8       4     format      (TextureFormat enum value)
//   12      4     width
//   16      4     height
//   20      4     mipCount
//   24      4     dataOffset  (= 32 + mipCount * 8 for v2)
//   28      4     flags       (bit 0 = sRGB; bit 1 = normal-map)
//   --------- 32 bytes (header) ---------
//   32      8*N   mip table (per mip: uint32 byteOffset relative to file
//                 start, uint32 byteLength); mipCount entries
//   --------- header + table (dataOffset bytes) ---------
//   dataOffset ...  packed mip data: mip 0 first, then mip 1, etc.
//
// v1 was a single-mip Rgba8-only variant with no mip table; v2 supersedes
// it. Re-cook to migrate; the cook is fast.
public static class BlixTex
{
    public const uint Magic = 0x58494C42;
    public const ushort Version2 = 2;
    public const ushort KindTexture2D = 1;
    public const int HeaderSize = 32;
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
    public static void Write(string path, BlixTexImage image)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(image);
        if (image.MipBytes.Count == 0)
        {
            throw new ArgumentException("Image must have at least one mip.", nameof(image));
        }
        using var stream = File.Create(path);
        using var writer = new BinaryWriter(stream);

        var mipCount = (uint)image.MipBytes.Count;
        var dataOffset = (uint)(BlixTex.HeaderSize + mipCount * BlixTex.MipEntrySize);

        // Header.
        writer.Write(BlixTex.Magic);
        writer.Write(BlixTex.Version2);
        writer.Write(BlixTex.KindTexture2D);
        writer.Write((uint)image.Format);
        writer.Write((uint)image.Width);
        writer.Write((uint)image.Height);
        writer.Write(mipCount);
        writer.Write(dataOffset);
        writer.Write((uint)image.Flags);
        if (stream.Position != BlixTex.HeaderSize)
        {
            throw new InvalidOperationException(
                $"BlixTex header was {stream.Position} bytes; expected {BlixTex.HeaderSize}.");
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
    IReadOnlyList<(int Offset, int Length)> MipExtents);

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
        if (stream.Length < BlixTex.HeaderSize)
        {
            throw new InvalidDataException($"BlixTex file '{path}' is shorter than the 32-byte header.");
        }

        // Read header.
        var headerBuf = new byte[BlixTex.HeaderSize];
        stream.ReadExactly(headerBuf, 0, BlixTex.HeaderSize);
        var magic = BitConverter.ToUInt32(headerBuf, 0);
        if (magic != BlixTex.Magic)
        {
            throw new InvalidDataException(
                $"BlixTex file '{path}' has wrong magic 0x{magic:X8}; expected 0x{BlixTex.Magic:X8}.");
        }
        var version = BitConverter.ToUInt16(headerBuf, 4);
        if (version != BlixTex.Version2)
        {
            throw new InvalidDataException(
                $"BlixTex file '{path}' has unsupported version {version}; re-cook to v{BlixTex.Version2}.");
        }
        var kind = BitConverter.ToUInt16(headerBuf, 6);
        if (kind != BlixTex.KindTexture2D)
        {
            throw new InvalidDataException(
                $"BlixTex file '{path}' has kind {kind}; only Texture2D (1) supported.");
        }
        var format = (TextureFormat)BitConverter.ToUInt32(headerBuf, 8);
        var width = (int)BitConverter.ToUInt32(headerBuf, 12);
        var height = (int)BitConverter.ToUInt32(headerBuf, 16);
        var mipCount = (int)BitConverter.ToUInt32(headerBuf, 20);
        var dataOffset = (int)BitConverter.ToUInt32(headerBuf, 24);
        var flags = (BlixTex.Flags)BitConverter.ToUInt32(headerBuf, 28);

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

        return new BlixTexLazyHandle(path, format, width, height, mipCount, flags, extents);
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

    public static BlixTexImage Read(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length < BlixTex.HeaderSize)
        {
            throw new InvalidDataException($"BlixTex file '{path}' is shorter than the 32-byte header.");
        }

        var magic = BitConverter.ToUInt32(bytes, 0);
        if (magic != BlixTex.Magic)
        {
            throw new InvalidDataException(
                $"BlixTex file '{path}' has wrong magic 0x{magic:X8}; expected 0x{BlixTex.Magic:X8}.");
        }
        var version = BitConverter.ToUInt16(bytes, 4);
        if (version != BlixTex.Version2)
        {
            throw new InvalidDataException(
                $"BlixTex file '{path}' has unsupported version {version}; runtime supports {BlixTex.Version2}. " +
                "Re-cook with the current tool to migrate.");
        }
        var kind = BitConverter.ToUInt16(bytes, 6);
        if (kind != BlixTex.KindTexture2D)
        {
            throw new InvalidDataException(
                $"BlixTex file '{path}' has kind {kind}; only Texture2D (1) supported.");
        }
        var format = (TextureFormat)BitConverter.ToUInt32(bytes, 8);
        var width = (int)BitConverter.ToUInt32(bytes, 12);
        var height = (int)BitConverter.ToUInt32(bytes, 16);
        var mipCount = (int)BitConverter.ToUInt32(bytes, 20);
        var dataOffset = (int)BitConverter.ToUInt32(bytes, 24);
        var flags = (BlixTex.Flags)BitConverter.ToUInt32(bytes, 28);

        if (mipCount <= 0)
        {
            throw new InvalidDataException($"BlixTex file '{path}' has invalid mipCount {mipCount}.");
        }
        var expectedTableEnd = BlixTex.HeaderSize + mipCount * BlixTex.MipEntrySize;
        if (dataOffset < expectedTableEnd || dataOffset > bytes.Length)
        {
            throw new InvalidDataException(
                $"BlixTex file '{path}' has invalid dataOffset {dataOffset} (table-end={expectedTableEnd}, fileLen={bytes.Length}).");
        }

        var mips = new byte[mipCount][];
        for (var i = 0; i < mipCount; i++)
        {
            var entryOffset = BlixTex.HeaderSize + i * BlixTex.MipEntrySize;
            var mipOffset = (int)BitConverter.ToUInt32(bytes, entryOffset);
            var mipLength = (int)BitConverter.ToUInt32(bytes, entryOffset + 4);
            if (mipOffset < dataOffset || mipOffset + mipLength > bytes.Length)
            {
                throw new InvalidDataException(
                    $"BlixTex file '{path}' has invalid mip {i} entry (offset={mipOffset}, length={mipLength}).");
            }
            var mipBytes = new byte[mipLength];
            Array.Copy(bytes, mipOffset, mipBytes, 0, mipLength);
            mips[i] = mipBytes;
        }
        return new BlixTexImage(width, height, format, mips, flags);
    }
}

namespace Blix.Graphics.Images;

/// <summary>
/// Writes 8-bit RGBA pixels as a PNG.
/// </summary>
/// <remarks>
/// <b>Hand-rolled, and small on purpose.</b> StbImageSharp — already here — only decodes; encoding would
/// have meant another package for the sake of one file format used by tools. A PNG is a signature, three
/// chunks and a zlib stream, and zlib permits <em>stored</em> (uncompressed) blocks, so the whole encoder
/// is a CRC, an Adler sum and some length prefixes. The files are larger than a real deflate would make
/// them; they are screenshots, not assets.
/// <para>
/// PNG specifically, rather than a raw dump, because PNG is what a person and a reading tool can both open.
/// A readback nobody can look at answers nothing — which was the point of adding one.
/// </para>
/// </remarks>
public static class PngWriter
{
    /// <summary>Encodes tightly-packed RGBA8 rows, top row first.</summary>
    public static byte[] EncodeRgba8(ReadOnlySpan<byte> pixels, int width, int height)
    {
        if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        var expected = checked(width * height * 4);
        if (pixels.Length < expected)
        {
            throw new ArgumentException($"Expected {expected} bytes for {width}x{height} RGBA8.", nameof(pixels));
        }

        // Each scanline is prefixed with its filter byte. Filter 0 (None) keeps the encoder
        // trivial and costs only size.
        var raw = new byte[checked((width * 4 + 1) * height)];
        for (var y = 0; y < height; y++)
        {
            var src = y * width * 4;
            var dst = y * (width * 4 + 1);
            raw[dst] = 0;
            pixels.Slice(src, width * 4).CopyTo(raw.AsSpan(dst + 1, width * 4));
        }

        using var output = new MemoryStream();
        output.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });

        var ihdr = new byte[13];
        WriteBigEndian(ihdr, 0, (uint)width);
        WriteBigEndian(ihdr, 4, (uint)height);
        ihdr[8] = 8;    // bit depth
        ihdr[9] = 6;    // colour type 6 = RGBA
        ihdr[10] = 0;   // deflate
        ihdr[11] = 0;   // adaptive filtering
        ihdr[12] = 0;   // no interlace
        WriteChunk(output, "IHDR", ihdr);
        WriteChunk(output, "IDAT", ZlibStored(raw));
        WriteChunk(output, "IEND", Array.Empty<byte>());

        return output.ToArray();
    }

    /// <summary>Encodes and writes to disk, creating the directory if needed.</summary>
    public static void WriteRgba8(string path, ReadOnlySpan<byte> pixels, int width, int height)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        File.WriteAllBytes(path, EncodeRgba8(pixels, width, height));
    }

    // A zlib stream whose deflate payload is stored blocks: no compression, no tables, no
    // Huffman. Every decoder accepts it because the format requires it.
    private static byte[] ZlibStored(byte[] data)
    {
        using var stream = new MemoryStream();
        stream.WriteByte(0x78);   // CM=8, CINFO=7
        stream.WriteByte(0x01);   // FCHECK so the header is a multiple of 31, no dictionary

        const int max = 65535;
        var offset = 0;
        do
        {
            var length = Math.Min(max, data.Length - offset);
            var final = offset + length >= data.Length;
            stream.WriteByte((byte)(final ? 1 : 0));
            stream.WriteByte((byte)(length & 0xFF));
            stream.WriteByte((byte)((length >> 8) & 0xFF));
            stream.WriteByte((byte)(~length & 0xFF));
            stream.WriteByte((byte)((~length >> 8) & 0xFF));
            stream.Write(data, offset, length);
            offset += length;
        }
        while (offset < data.Length);

        var adler = Adler32(data);
        stream.WriteByte((byte)(adler >> 24));
        stream.WriteByte((byte)(adler >> 16));
        stream.WriteByte((byte)(adler >> 8));
        stream.WriteByte((byte)adler);
        return stream.ToArray();
    }

    private static void WriteChunk(Stream output, string type, byte[] data)
    {
        var header = new byte[4];
        WriteBigEndian(header, 0, (uint)data.Length);
        output.Write(header);

        var typed = new byte[4 + data.Length];
        for (var i = 0; i < 4; i++) typed[i] = (byte)type[i];
        data.CopyTo(typed, 4);
        output.Write(typed);

        var crc = new byte[4];
        WriteBigEndian(crc, 0, Crc32(typed));
        output.Write(crc);
    }

    private static void WriteBigEndian(byte[] target, int offset, uint value)
    {
        target[offset] = (byte)(value >> 24);
        target[offset + 1] = (byte)(value >> 16);
        target[offset + 2] = (byte)(value >> 8);
        target[offset + 3] = (byte)value;
    }

    private static uint Adler32(ReadOnlySpan<byte> data)
    {
        uint a = 1, b = 0;
        foreach (var value in data)
        {
            a = (a + value) % 65521;
            b = (b + a) % 65521;
        }

        return (b << 16) | a;
    }

    private static readonly uint[] CrcTable = BuildCrcTable();

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            var c = n;
            for (var k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            table[n] = c;
        }

        return table;
    }

    private static uint Crc32(ReadOnlySpan<byte> data)
    {
        var c = 0xFFFFFFFFu;
        foreach (var value in data) c = CrcTable[(c ^ value) & 0xFF] ^ (c >> 8);
        return c ^ 0xFFFFFFFFu;
    }
}

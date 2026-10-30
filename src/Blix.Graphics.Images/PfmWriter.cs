using System.Text;

namespace Blix.Graphics.Images;

/// <summary>
/// Writes linear float RGB as a PFM (Portable Float Map): the lossless counterpart to <see cref="PngWriter"/>.
/// </summary>
/// <remarks>
/// <b>Why a PFM.</b> A capture's tonemap is an offline choice (Blix.Tools.Shot's header says so), but the PNG
/// it writes has already made it, in 8 bits. Anything that studies the curve itself, fitting one for instance,
/// needs the radiance. A PFM is a three-line text header and raw little-endian float32, so it is as small to
/// write as the PNG and loads with one numpy call, with no decoder dependency on either side.
/// <para>
/// Rows are written bottom to top, as the format has them. Scale is -1 for little-endian.
/// </para>
/// </remarks>
public static class PfmWriter
{
    /// <summary>Tightly-packed RGB float32 rows, top row first, written to <paramref name="path"/>.</summary>
    public static void WriteRgb(string path, ReadOnlySpan<float> rgb, int width, int height)
    {
        if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        var expected = checked(width * height * 3);
        if (rgb.Length < expected)
        {
            throw new ArgumentException($"Expected {expected} floats for {width}x{height} RGB.", nameof(rgb));
        }

        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        using var output = File.Create(path);
        output.Write(Encoding.ASCII.GetBytes($"PF\n{width} {height}\n-1.0\n"));

        var row = new byte[width * 3 * sizeof(float)];
        for (var y = height - 1; y >= 0; y--)
        {
            Buffer.BlockCopy(rgb[(y * width * 3)..(y * width * 3 + width * 3)].ToArray(), 0, row, 0, row.Length);
            output.Write(row);
        }
    }
}

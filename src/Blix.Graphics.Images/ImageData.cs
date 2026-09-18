namespace Blix.Graphics.Images;

public sealed record ImageData(
    int Width,
    int Height,
    Blix.Graphics.TextureFormat Format,
    byte[] Pixels);

// Floating-point image. Pixels is RGBA stored as 4*Width*Height floats laid
// out [r0,g0,b0,a0, r1,g1,b1,a1, ...]. Used for HDR sources (Radiance .hdr
// and similar) whose dynamic range would clip in Rgba8.
public sealed record HdrImageData(
    int Width,
    int Height,
    float[] Pixels);

/// <summary>
/// Turning an equirectangular sky about the vertical axis.
/// </summary>
/// <remarks>
/// <para>
/// An equirect's horizontal axis IS azimuth, so a yaw rotation is a whole-pixel roll — no
/// resampling, no interpolation, and, most importantly, no tilt: the horizon stays where it is.
/// Rotating in ELEVATION has no such form; it would tip the ground into the sky, which is why the
/// only axis offered here is the one that is free.
/// </para>
/// <para>
/// This exists because a scene's sun is authored and an HDR's sun is wherever the photographer
/// stood. Aligning the two by moving the scene's sun throws away a lighting decision; aligning them
/// by turning the sky costs nothing, provided the elevations already agree.
/// </para>
/// </remarks>
public static class EquirectYaw
{
    /// <summary>Rolls <paramref name="src"/> so its content turns by <paramref name="degrees"/> about +Y.</summary>
    public static HdrImageData Rotate(HdrImageData src, float degrees)
    {
        ArgumentNullException.ThrowIfNull(src);
        var shift = (int)MathF.Round(degrees / 360f * src.Width);
        shift = ((shift % src.Width) + src.Width) % src.Width;
        if (shift == 0) return src;

        var dst = new float[src.Pixels.Length];
        for (var y = 0; y < src.Height; y++)
        {
            var row = y * src.Width * 4;
            for (var x = 0; x < src.Width; x++)
            {
                var sx = (x + shift) % src.Width;
                Array.Copy(src.Pixels, row + sx * 4, dst, row + x * 4, 4);
            }
        }
        return new HdrImageData(src.Width, src.Height, dst);
    }
}

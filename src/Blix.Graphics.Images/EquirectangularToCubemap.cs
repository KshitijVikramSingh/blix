using System.Numerics;

namespace Blix.Graphics.Images;

// Converts an equirectangular HDR image (the canonical "panoramic photo of
// the sky" layout: 2:1 aspect, theta along width, phi along height) into the
// six-face cubemap layout the engine's CreateTextureCubeHdr API consumes.
//
// CPU-side, one-time at load. No GPU plumbing or render passes needed --
// trades a bit of latency at startup for much simpler code than the
// blit-six-faces approach. For a 512px-per-face output the conversion takes
// ~50ms in Debug.
public static class EquirectangularToCubemap
{
    // Sampling direction for each cube face's pixel (u, v) in [0, 1]^2. Layout
    // matches GL_TEXTURE_CUBE_MAP_POSITIVE_X .. NEGATIVE_Z (the +X/-X/+Y/-Y/+Z/-Z
    // order CreateTextureCubeHdr expects). The 2 * uv - 1 puts the centre of the
    // face at the centre of the cube's outward axis.
    private static Vector3 SampleDirection(int face, float u, float v)
    {
        // OpenGL cubemap convention has the v axis going DOWNWARD when looking at
        // the face from outside, which is why every face's Y term subtracts v
        // instead of adding it.
        float a = 2.0f * u - 1.0f;
        float b = 2.0f * v - 1.0f;
        return face switch
        {
            0 => new Vector3( 1.0f, -b, -a),  // +X
            1 => new Vector3(-1.0f, -b,  a),  // -X
            2 => new Vector3( a,  1.0f,  b),  // +Y
            3 => new Vector3( a, -1.0f, -b),  // -Y
            4 => new Vector3( a, -b,  1.0f),  // +Z
            5 => new Vector3(-a, -b, -1.0f),  // -Z
            _ => throw new ArgumentOutOfRangeException(nameof(face)),
        };
    }

    // Sample the equirect image bilinearly at (sphericalU, sphericalV) where both
    // are in [0, 1]. Equirect convention: u = (atan2(z, x) / (2*pi)) + 0.5,
    // v = acos(y) / pi (with y up). Out-of-range u wraps; v clamps.
    private static Vector4 SampleEquirect(HdrImageData src, float u, float v)
    {
        // Wrap u, clamp v.
        u = u - MathF.Floor(u);
        v = Math.Clamp(v, 0.0f, 0.99999f);

        float fx = u * src.Width;
        float fy = v * src.Height;
        int x0 = (int)MathF.Floor(fx);
        int y0 = (int)MathF.Floor(fy);
        int x1 = (x0 + 1) % src.Width;          // wrap u
        int y1 = Math.Min(y0 + 1, src.Height - 1);
        float tx = fx - x0;
        float ty = fy - y0;

        return BilinearLerp(
            FetchTexel(src, x0, y0),
            FetchTexel(src, x1, y0),
            FetchTexel(src, x0, y1),
            FetchTexel(src, x1, y1),
            tx, ty);
    }

    private static Vector4 FetchTexel(HdrImageData src, int x, int y)
    {
        int i = (y * src.Width + x) * 4;
        return new Vector4(
            src.Pixels[i + 0],
            src.Pixels[i + 1],
            src.Pixels[i + 2],
            src.Pixels[i + 3]);
    }

    private static Vector4 BilinearLerp(Vector4 a, Vector4 b, Vector4 c, Vector4 d, float tx, float ty)
    {
        var ab = Vector4.Lerp(a, b, tx);
        var cd = Vector4.Lerp(c, d, tx);
        return Vector4.Lerp(ab, cd, ty);
    }

    // Returns 6 * faceSize * faceSize * 4 Half values in the [+X, -X, +Y, -Y, +Z, -Z]
    // face order CreateTextureCubeHdr consumes. The 4 channels are RGBA; alpha is
    // forced to 1.0 since equirect sources have no alpha channel of interest.
    public static Half[] Convert(HdrImageData src, int faceSize)
    {
        ArgumentNullException.ThrowIfNull(src);
        if (faceSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(faceSize), "Cube face size must be > 0.");
        }

        var pixelsPerFace = faceSize * faceSize * 4;
        var output = new Half[pixelsPerFace * 6];

        for (int face = 0; face < 6; face++)
        {
            int faceOffset = face * pixelsPerFace;
            for (int j = 0; j < faceSize; j++)
            {
                // Sample at pixel centres (+0.5) so the conversion has half-texel
                // alignment with the cube face grid.
                float v = (j + 0.5f) / faceSize;
                for (int i = 0; i < faceSize; i++)
                {
                    float u = (i + 0.5f) / faceSize;
                    var dir = Vector3.Normalize(SampleDirection(face, u, v));

                    // Direction -> spherical -> equirect (u, v).
                    // theta = atan2(z, x) in [-pi, pi]; equirect_u in [0, 1].
                    // phi   = acos(y)     in [0, pi];   equirect_v in [0, 1].
                    float theta = MathF.Atan2(dir.Z, dir.X);
                    float phi   = MathF.Acos(Math.Clamp(dir.Y, -1.0f, 1.0f));
                    float eu = (theta / (2.0f * MathF.PI)) + 0.5f;
                    float ev = phi / MathF.PI;

                    var rgba = SampleEquirect(src, eu, ev);
                    int idx = faceOffset + (j * faceSize + i) * 4;
                    output[idx + 0] = (Half)rgba.X;
                    output[idx + 1] = (Half)rgba.Y;
                    output[idx + 2] = (Half)rgba.Z;
                    output[idx + 3] = (Half)1.0f;
                }
            }
        }

        return output;
    }
}

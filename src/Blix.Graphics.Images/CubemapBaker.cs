using System.Numerics;

namespace Blix.Graphics.Images;

// CPU-bakes a 6-face HDR procedural sky cubemap. Output is a flat Half[] in
// the face order CreateTextureCubeHdr expects (+X, -X, +Y, -Y, +Z, -Z), each
// face packed as faceSize*faceSize*4 Halfs (Rgba16F).
//
// Why CPU-side: the engine doesn't have a colour-attachment cube-face target
// (only depth-cube-face for shadow rendering). Baking at startup is fast --
// a 256-face cube is ~390K texels, each evaluating a closed-form sky
// function in a few math ops. Tens of ms, once.
//
// Used by EnvironmentBaker when the EnvironmentProfile selects the procedural
// source. Demos that want a different procedural sky can substitute their
// own baker that produces Half[] in the same face order.
public static class CubemapBaker
{
    public static Half[] BakeSky(int faceSize, Vector3 sunDirection)
    {
        var halfsPerFace = faceSize * faceSize * 4;
        var pixels = new Half[halfsPerFace * 6];
        for (var face = 0; face < 6; face++)
        {
            var faceOffset = face * halfsPerFace;
            for (var t = 0; t < faceSize; t++)
            {
                for (var s = 0; s < faceSize; s++)
                {
                    // Convert face texel (s, t) to a world-space direction. OpenGL's
                    // cubemap face convention; matches CreateTextureCubeHdr's input.
                    var u = 2.0f * (s + 0.5f) / faceSize - 1.0f;
                    var v = 1.0f - 2.0f * (t + 0.5f) / faceSize;
                    var dir = face switch
                    {
                        0 => new Vector3( 1,  v, -u),
                        1 => new Vector3(-1,  v,  u),
                        2 => new Vector3( u,  1, -v),
                        3 => new Vector3( u, -1,  v),
                        4 => new Vector3( u,  v,  1),
                        _ => new Vector3(-u,  v, -1),
                    };
                    dir = Vector3.Normalize(dir);
                    var color = SampleHdrSky(dir, sunDirection);
                    var index = faceOffset + (t * faceSize + s) * 4;
                    pixels[index + 0] = (Half)color.X;
                    pixels[index + 1] = (Half)color.Y;
                    pixels[index + 2] = (Half)color.Z;
                    pixels[index + 3] = (Half)1.0f;
                }
            }
        }
        return pixels;
    }

    // HDR sky in linear space. Sun spot peaks ~12x reference white (enough to
    // punch through tone mapping); horizon warms when sun is low for dawn/dusk.
    // Designed to match the rendered backdrop AND drive IBL faithfully — same
    // values in both roles is the point.
    private static Vector3 SampleHdrSky(Vector3 dir, Vector3 sunDir)
    {
        var zenith = new Vector3(0.10f, 0.20f, 0.50f);
        var horizon = new Vector3(0.55f, 0.60f, 0.72f);
        var ground = new Vector3(0.10f, 0.08f, 0.06f);

        Vector3 col;
        if (dir.Y >= 0.0f)
        {
            var t = Smoothstep(0.0f, 1.0f, dir.Y);
            col = Vector3.Lerp(horizon, zenith, t);
        }
        else
        {
            var t = Smoothstep(0.0f, 0.3f, -dir.Y);
            col = Vector3.Lerp(horizon, ground, t);
        }

        // Warm horizon tinting when sun is low.
        var sunAlt = Math.Clamp(-sunDir.Y, 0.0f, 1.0f);
        var horizonBoost = (1.0f - sunAlt) * Smoothstep(0.25f, -0.05f, dir.Y);
        col = Vector3.Lerp(col, new Vector3(0.95f, 0.55f, 0.25f), horizonBoost * 0.45f);

        // Sun: tight high-intensity disc + softer halo. Punches into HDR
        // without clipping because storage is half-float, not Rgba8.
        var sunCos = MathF.Max(Vector3.Dot(dir, -sunDir), 0.0f);
        var sunSpot = MathF.Pow(sunCos, 320.0f) * 14.0f;
        var sunGlow = MathF.Pow(sunCos, 8.0f) * 1.6f;
        col += new Vector3(1.0f, 0.94f, 0.82f) * (sunSpot + sunGlow);

        return col;
    }

    private static float Smoothstep(float a, float b, float x)
    {
        var t = Math.Clamp((x - a) / (b - a), 0.0f, 1.0f);
        return t * t * (3.0f - 2.0f * t);
    }
}

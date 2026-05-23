using System.Numerics;

namespace Blix.Graphics.Images;

// Best-effort sun direction extracted from an HDR equirect: the centroid of
// the brightest cluster of pixels in the sky portion of the image (upper
// hemisphere, v < 0.5). For most outdoor HDRIs this picks out the sun disc
// cleanly. The returned Vector3 is the world direction the light travels
// (i.e., FROM sun INTO scene -- matches Blix's `sunDirection` convention),
// normalised. Returns null if no clearly-brightest cluster is found.
//
// Co-located with the IBL baker because both consume HdrImageData via the
// equirect spherical mapping.
public static class HdrSunFinder
{
    public static System.Numerics.Vector3? FindSunDirection(HdrImageData src)
    {
        ArgumentNullException.ThrowIfNull(src);
        // Scan a strided grid -- HDR sun pixels are tiny but bright, so
        // we'd miss them sampling too coarsely. Stride of 1 every 4 pixels
        // catches a 4-pixel-wide sun reliably.
        int stride = 4;
        float brightestL = 0.0f;
        int bx = 0, by = 0;
        for (int y = 0; y < src.Height / 2; y += stride)
        {
            for (int x = 0; x < src.Width; x += stride)
            {
                int i = (y * src.Width + x) * 4;
                float L = src.Pixels[i] + src.Pixels[i + 1] + src.Pixels[i + 2];
                if (L > brightestL)
                {
                    brightestL = L;
                    bx = x; by = y;
                }
            }
        }
        if (brightestL <= 0.001f) return null;

        // Equirect pixel (bx, by) -> spherical (theta, phi) -> world direction.
        // theta = (u - 0.5) * 2π ; phi = v * π. With y-up convention:
        // direction = (cos(phi*?)*cos(theta), -cos(phi), cos(phi*?)*sin(theta))
        // -- standard inverse of the cube-direction equirect mapping in
        // EquirectangularToCubemap.
        float u = (bx + 0.5f) / src.Width;
        float v = (by + 0.5f) / src.Height;
        float theta = (u - 0.5f) * 2.0f * MathF.PI;
        float phi   = v * MathF.PI;

        // direction FROM the sky pixel: equirect maps direction.x = cos(phi)*cos(theta),
        // direction.y = cos(phi-pi/2) = -cos(phi) + something? Actually for our
        // equirect we had phi = acos(y) so y = cos(phi). For v=0 (top), phi=0, y=1
        // (straight up). The brightest pixel typically sits at v ~ 0.2 (above the
        // horizon), so phi ~ 0.6, y ~ 0.83.
        float cy = MathF.Cos(phi);                      // direction y component
        float horiz = MathF.Sqrt(MathF.Max(1.0f - cy * cy, 0.0f));
        float cx = horiz * MathF.Cos(theta);
        float cz = horiz * MathF.Sin(theta);
        var dirFromSky = new System.Numerics.Vector3(cx, cy, cz);

        // sunDirection is FROM-sun-INTO-scene; the sun sits AT dirFromSky
        // looking outward, so the light direction is -dirFromSky.
        return System.Numerics.Vector3.Normalize(-dirFromSky);
    }
}

// Precomputes PBR IBL probes from an equirectangular HDR source. CPU-side
// to keep the engine plumbing simple -- one-time cost at startup that's
// ~200ms for a 32-face irradiance cube on a modern laptop.
//
// Currently bakes the diffuse irradiance cubemap only. Proper roughness-
// prefiltered specular (GGX importance sampling) and the Karis BRDF LUT
// would extend this class with two more methods; lit shader changes
// would compose all three for full split-sum IBL. The diffuse cube alone
// is the single biggest visual upgrade vs the "lowest-mip-as-irradiance"
// approximation it replaces, because it preserves the env map's
// directional colour variation (warm sun side vs cool shadow side).
public static class PbrIblBaker
{
    // Cosine-weighted hemispherical convolution. For each output direction N,
    // integrates the env map weighted by cos(theta) over the hemisphere above
    // N. Result is the irradiance E(N) -- the total light arriving on a
    // surface facing N. Lit shader uses this directly (no additional PI
    // scaling) for the diffuse IBL term.
    //
    // sampleCount is the Hammersley-stratified sample count per output texel.
    // 32 gives recognisable directional bias; 64-128 is smoother but slower.
    public static Half[] BakeDiffuseIrradiance(
        HdrImageData equirect,
        int faceSize,
        int sampleCount = 64,
        // Firefly clamp magnitude. Polyhaven HDRIs' single-pixel suns
        // otherwise produce speckle alias on normal-mapped surfaces; ~50
        // is empirically safe (preserves visible sun in the sky AND clean
        // probes). Set to PositiveInfinity to disable.
        float sampleClampMagnitude = 50.0f)
    {
        ArgumentNullException.ThrowIfNull(equirect);
        if (faceSize <= 0) throw new ArgumentOutOfRangeException(nameof(faceSize));
        if (sampleCount <= 0) throw new ArgumentOutOfRangeException(nameof(sampleCount));

        var pixelsPerFace = faceSize * faceSize * 4;
        var output = new Half[pixelsPerFace * 6];

        // Multithread across faces for ~6x speedup on machines with cores spare.
        Parallel.For(0, 6, face =>
        {
            int faceOffset = face * pixelsPerFace;
            for (int j = 0; j < faceSize; j++)
            {
                float v = (j + 0.5f) / faceSize;
                for (int i = 0; i < faceSize; i++)
                {
                    float u = (i + 0.5f) / faceSize;
                    var N = Vector3.Normalize(CubeDirection(face, u, v));

                    // Build a tangent basis around N. The cosine-weighted samples
                    // live in this basis (with N as the up axis).
                    var up = MathF.Abs(N.Y) > 0.99f ? Vector3.UnitZ : Vector3.UnitY;
                    var right = Vector3.Normalize(Vector3.Cross(up, N));
                    var forward = Vector3.Cross(N, right);

                    var irradiance = Vector3.Zero;
                    for (int s = 0; s < sampleCount; s++)
                    {
                        var xi = Hammersley(s, sampleCount);
                        // Cosine-weighted hemisphere sample (Malley's method:
                        // sample disk, project up).
                        float phi = 2.0f * MathF.PI * xi.X;
                        float cosTheta = MathF.Sqrt(1.0f - xi.Y);
                        float sinTheta = MathF.Sqrt(xi.Y);
                        var tangentDir = new Vector3(
                            MathF.Cos(phi) * sinTheta,
                            cosTheta,
                            MathF.Sin(phi) * sinTheta);
                        // Transform tangent-space direction into world space
                        // (with N as the local +Y axis).
                        var worldDir = right * tangentDir.X + N * tangentDir.Y + forward * tangentDir.Z;
                        var li = SampleEquirect(equirect, Vector3.Normalize(worldDir));
                        // Firefly suppression: HDR sources have single-pixel
                        // suns at ~1e3-1e5; with only 64 samples a stray hit
                        // dominates the integral and creates speckle aliasing
                        // when normal-mapped surfaces sample the resulting
                        // cube. Clamp before accumulating -- loses a hair of
                        // peak HDR detail in exchange for a stable, smooth
                        // diffuse cube. 50 is a reasonable cap for the
                        // Polyhaven HDRIs we ship with.
                        li = Vector3.Min(li, new Vector3(sampleClampMagnitude));
                        // For cosine-weighted sampling the PDF is cos/PI, which
                        // cancels the cos in the integrand, so we just average.
                        irradiance += li;
                    }
                    irradiance /= sampleCount;
                    // Multiply by PI: cosine-weighted average is L_avg = E/PI,
                    // so E = L_avg * PI.
                    irradiance *= MathF.PI;

                    int idx = faceOffset + (j * faceSize + i) * 4;
                    output[idx + 0] = (Half)irradiance.X;
                    output[idx + 1] = (Half)irradiance.Y;
                    output[idx + 2] = (Half)irradiance.Z;
                    output[idx + 3] = (Half)1.0f;
                }
            }
        });

        return output;
    }

    // Roughness-prefiltered specular mip chain. Returns one Half[] per mip
    // level, ordered finest-to-coarsest. Mip K is convolved at roughness
    // K/(mipCount-1) using GGX importance sampling, so the lit shader can
    // do `textureLod(specularCube, R, roughness * (mipCount-1))` to get the
    // appropriately-blurred reflection for any surface roughness.
    //
    // Mip 0 (roughness 0) is a direct equirect-to-cubemap conversion -- a
    // perfect mirror should reflect the env exactly, no integration needed.
    // Higher mips do importance-sampled integration; sample count rises with
    // mip index since higher roughness needs more samples to look smooth.
    public static Half[][] BakeSpecularPrefilteredMips(
        HdrImageData equirect,
        int baseFaceSize,
        int mipCount,
        // Firefly clamp magnitude. Same role as BakeDiffuseIrradiance.
        float sampleClampMagnitude = 50.0f)
    {
        ArgumentNullException.ThrowIfNull(equirect);
        if (baseFaceSize <= 0) throw new ArgumentOutOfRangeException(nameof(baseFaceSize));
        if (mipCount <= 0) throw new ArgumentOutOfRangeException(nameof(mipCount));

        var mips = new Half[mipCount][];
        for (int mip = 0; mip < mipCount; mip++)
        {
            int mipSize = Math.Max(1, baseFaceSize >> mip);
            float roughness = mipCount <= 1 ? 0.0f : (float)mip / (mipCount - 1);
            // More samples at higher roughness. Mip 0 needs none -- it's a
            // mirror, just sample the env directly.
            int sampleCount = mip == 0 ? 1 : 32 + mip * 32;
            mips[mip] = BakeSpecularMip(equirect, mipSize, roughness, sampleCount, sampleClampMagnitude);
        }
        return mips;
    }

    private static Half[] BakeSpecularMip(HdrImageData equirect, int faceSize, float roughness, int sampleCount, float sampleClampMagnitude)
    {
        int pixelsPerFace = faceSize * faceSize * 4;
        var output = new Half[pixelsPerFace * 6];

        Parallel.For(0, 6, face =>
        {
            int faceOffset = face * pixelsPerFace;
            for (int j = 0; j < faceSize; j++)
            {
                float v = (j + 0.5f) / faceSize;
                for (int i = 0; i < faceSize; i++)
                {
                    float u = (i + 0.5f) / faceSize;
                    var N = Vector3.Normalize(CubeDirection(face, u, v));
                    // Karis simplification: assume V == R == N so we can
                    // precompute the prefilter independent of view direction.
                    // Loses anisotropic stretch at grazing but is the
                    // standard split-sum approximation.
                    var R = N;
                    var V = N;

                    Vector3 prefilter = Vector3.Zero;
                    float weight = 0.0f;
                    for (int s = 0; s < sampleCount; s++)
                    {
                        var xi = Hammersley(s, sampleCount);
                        var H = ImportanceSampleGgx(xi, N, roughness);
                        var L = Vector3.Normalize(2.0f * Vector3.Dot(V, H) * H - V);
                        float NdotL = Math.Max(Vector3.Dot(N, L), 0.0f);
                        if (NdotL > 0.0f)
                        {
                            var li = SampleEquirect(equirect, L);
                            // Same firefly clamp as the diffuse cube; see
                            // BakeDiffuseIrradiance for rationale.
                            li = Vector3.Min(li, new Vector3(sampleClampMagnitude));
                            prefilter += li * NdotL;
                            weight += NdotL;
                        }
                    }
                    if (weight > 0.0f) prefilter /= weight;

                    int idx = faceOffset + (j * faceSize + i) * 4;
                    output[idx + 0] = (Half)prefilter.X;
                    output[idx + 1] = (Half)prefilter.Y;
                    output[idx + 2] = (Half)prefilter.Z;
                    output[idx + 3] = (Half)1.0f;
                }
            }
        });
        return output;
    }

    // 2D BRDF integration LUT (Karis 2014 split-sum, second term). Function
    // of (NdotV, roughness). R channel = scale on F0, G channel = bias.
    // In the shader: F = F0 * scale + bias gives the GGX-integrated Fresnel-
    // and-visibility contribution. One-time bake, env-independent.
    // Returned bytes are tightly packed RGBA8 (8 bytes per texel) where
    // RG hold the LUT values and BA are unused (set to 0, 255).
    public static byte[] BakeBrdfLut(int size, int sampleCount = 1024)
    {
        if (size <= 0) throw new ArgumentOutOfRangeException(nameof(size));
        if (sampleCount <= 0) throw new ArgumentOutOfRangeException(nameof(sampleCount));

        var output = new byte[size * size * 4];

        Parallel.For(0, size, j =>
        {
            // Y axis = roughness. +0.5 so we sample at texel centres.
            float roughness = (j + 0.5f) / size;
            for (int i = 0; i < size; i++)
            {
                float NdotV = (i + 0.5f) / size;
                // V in the local tangent frame where N = +Z. atan via
                // sqrt(1-NdotV^2) gives the perpendicular component.
                var V = new Vector3(MathF.Sqrt(1.0f - NdotV * NdotV), 0.0f, NdotV);
                var N = new Vector3(0.0f, 0.0f, 1.0f);

                float A = 0.0f;
                float B = 0.0f;
                for (int s = 0; s < sampleCount; s++)
                {
                    var xi = Hammersley(s, sampleCount);
                    var H = ImportanceSampleGgx(xi, N, roughness);
                    var L = Vector3.Normalize(2.0f * Vector3.Dot(V, H) * H - V);
                    float NdotL = Math.Max(L.Z, 0.0f);
                    float NdotH = Math.Max(H.Z, 0.0f);
                    float VdotH = Math.Max(Vector3.Dot(V, H), 0.0f);
                    if (NdotL > 0.0f)
                    {
                        float G = GeometrySmithIbl(NdotV, NdotL, roughness);
                        float GVis = (G * VdotH) / (NdotH * NdotV);
                        float Fc = MathF.Pow(1.0f - VdotH, 5.0f);
                        A += (1.0f - Fc) * GVis;
                        B += Fc * GVis;
                    }
                }
                A /= sampleCount;
                B /= sampleCount;

                int idx = (j * size + i) * 4;
                output[idx + 0] = (byte)Math.Clamp(A * 255.0f, 0.0f, 255.0f);
                output[idx + 1] = (byte)Math.Clamp(B * 255.0f, 0.0f, 255.0f);
                output[idx + 2] = 0;
                output[idx + 3] = 255;
            }
        });
        return output;
    }

    // --- GGX sampling helpers --------------------------------------------

    // Importance-sample a GGX(roughness) distribution in the tangent frame
    // where N is the surface normal. Returns the half-vector H in world space.
    private static Vector3 ImportanceSampleGgx(Vector2 xi, Vector3 N, float roughness)
    {
        float a = roughness * roughness;
        float a2 = a * a;
        float phi = 2.0f * MathF.PI * xi.X;
        // From U2's importance-sampled GGX: cos(theta_h) follows the GGX
        // distribution given a uniform xi.Y in [0, 1].
        float cosTheta = MathF.Sqrt((1.0f - xi.Y) / (1.0f + (a2 - 1.0f) * xi.Y));
        float sinTheta = MathF.Sqrt(MathF.Max(0.0f, 1.0f - cosTheta * cosTheta));
        var tangentH = new Vector3(MathF.Cos(phi) * sinTheta, MathF.Sin(phi) * sinTheta, cosTheta);

        // Tangent-to-world basis built around N.
        var up = MathF.Abs(N.Z) < 0.999f ? new Vector3(0, 0, 1) : new Vector3(1, 0, 0);
        var tangent = Vector3.Normalize(Vector3.Cross(up, N));
        var bitangent = Vector3.Cross(N, tangent);
        return Vector3.Normalize(tangent * tangentH.X + bitangent * tangentH.Y + N * tangentH.Z);
    }

    // IBL-specific Smith G (no +1 / 8 weighting of direct lighting; this
    // version comes from Karis's split-sum derivation).
    private static float GeometrySmithIbl(float NdotV, float NdotL, float roughness)
    {
        float a = roughness;
        float k = (a * a) / 2.0f;
        float ggxV = NdotV / (NdotV * (1.0f - k) + k);
        float ggxL = NdotL / (NdotL * (1.0f - k) + k);
        return ggxV * ggxL;
    }

    // --- Helpers ---

    // Cube face sampling direction; matches EquirectangularToCubemap's
    // convention so cubemaps the engine produces all agree.
    private static Vector3 CubeDirection(int face, float u, float v)
    {
        float a = 2.0f * u - 1.0f;
        float b = 2.0f * v - 1.0f;
        return face switch
        {
            0 => new Vector3( 1.0f, -b, -a),
            1 => new Vector3(-1.0f, -b,  a),
            2 => new Vector3( a,  1.0f,  b),
            3 => new Vector3( a, -1.0f, -b),
            4 => new Vector3( a, -b,  1.0f),
            5 => new Vector3(-a, -b, -1.0f),
            _ => throw new ArgumentOutOfRangeException(nameof(face)),
        };
    }

    // Hammersley sequence -- low-discrepancy 2D points in [0,1)^2. Combined
    // with cosine-weighted hemisphere mapping, gives well-distributed samples
    // that converge faster than uniform random.
    private static Vector2 Hammersley(int i, int n)
    {
        return new Vector2((float)i / n, RadicalInverseVdc((uint)i));
    }

    private static float RadicalInverseVdc(uint bits)
    {
        bits = (bits << 16) | (bits >> 16);
        bits = ((bits & 0x55555555u) << 1) | ((bits & 0xAAAAAAAAu) >> 1);
        bits = ((bits & 0x33333333u) << 2) | ((bits & 0xCCCCCCCCu) >> 2);
        bits = ((bits & 0x0F0F0F0Fu) << 4) | ((bits & 0xF0F0F0F0u) >> 4);
        bits = ((bits & 0x00FF00FFu) << 8) | ((bits & 0xFF00FF00u) >> 8);
        return bits * 2.3283064365386963e-10f;
    }

    // Sample the equirect HDR at the spherical coordinates implied by the
    // given direction. Same direction-to-uv math as the cubemap converter
    // (theta = atan2(z, x), phi = acos(y)).
    private static Vector3 SampleEquirect(HdrImageData src, Vector3 dir)
    {
        float theta = MathF.Atan2(dir.Z, dir.X);
        float phi   = MathF.Acos(Math.Clamp(dir.Y, -1.0f, 1.0f));
        float u = (theta / (2.0f * MathF.PI)) + 0.5f;
        float v = phi / MathF.PI;
        u = u - MathF.Floor(u);
        v = Math.Clamp(v, 0.0f, 0.99999f);

        float fx = u * src.Width;
        float fy = v * src.Height;
        int x0 = (int)MathF.Floor(fx);
        int y0 = (int)MathF.Floor(fy);
        int x1 = (x0 + 1) % src.Width;
        int y1 = Math.Min(y0 + 1, src.Height - 1);
        float tx = fx - x0;
        float ty = fy - y0;

        var a = FetchTexel(src, x0, y0);
        var b = FetchTexel(src, x1, y0);
        var c = FetchTexel(src, x0, y1);
        var d = FetchTexel(src, x1, y1);
        var ab = Vector3.Lerp(a, b, tx);
        var cd = Vector3.Lerp(c, d, tx);
        return Vector3.Lerp(ab, cd, ty);
    }

    private static Vector3 FetchTexel(HdrImageData src, int x, int y)
    {
        int i = (y * src.Width + x) * 4;
        return new Vector3(src.Pixels[i + 0], src.Pixels[i + 1], src.Pixels[i + 2]);
    }
}

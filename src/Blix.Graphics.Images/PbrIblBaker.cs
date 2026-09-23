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
/// <summary>What the sun in an HDR actually is: where it points, and how much light it delivers.</summary>
/// <param name="Direction">FROM the sun INTO the scene, matching the engine's light convention.</param>
/// <param name="Irradiance">
/// Per-channel irradiance on a surface facing the sun, in the HDR's own units — the integral of
/// radiance over the sun's solid angle.
/// </param>
/// <param name="CosAngularRadius">
/// The cosine of the disc's angular radius, which is what lets the bake remove it.
/// </param>
public readonly record struct HdrSun(
    System.Numerics.Vector3 Direction,
    System.Numerics.Vector3 Irradiance,
    float CosAngularRadius);

public static class HdrSunFinder
{
    /// <summary>
    /// The sun's direction AND the energy it carries, in the source's own units.
    /// </summary>
    /// <remarks>
    /// Direction and energy come from the same capture as the IBL, so a directional light needs no
    /// independent scale against that environment. Energy is reported as irradiance: radiance
    /// integrated over the disc's solid angle. Unlike a peak pixel, it is stable across source
    /// resolutions.
    /// </remarks>
    public static HdrSun? FindSun(HdrImageData src)
    {
        ArgumentNullException.ThrowIfNull(src);

        if (FindSunDirection(src) is not { } direction) return null;

        // The disc is every pixel within a small angle of the peak, which is how a sun is found
        // without assuming its size: HDRIs differ, and a fixed pixel radius would take a sliver of a
        // large sun and a chunk of sky around a small one.
        const float AngularRadius = 0.03f;          // ~1.7°, a little over the real sun's 0.53°
        var cosRadius = MathF.Cos(AngularRadius);

        // Solid angle of one equirect texel: dω = (2π/W)(π/H)·sin(phi). The sin term is why a naive
        // pixel sum over-weights the poles, where texels cover almost no sky.
        var dPhi = MathF.PI / src.Height;
        var dTheta = 2.0f * MathF.PI / src.Width;

        var irradiance = System.Numerics.Vector3.Zero;
        for (var y = 0; y < src.Height; y++)
        {
            var phi = (y + 0.5f) / src.Height * MathF.PI;
            var sinPhi = MathF.Sin(phi);
            var solidAngle = dTheta * dPhi * sinPhi;
            if (solidAngle <= 0.0f) continue;

            for (var x = 0; x < src.Width; x++)
            {
                var toPixel = -EquirectDirection((x + 0.5f) / src.Width, (y + 0.5f) / src.Height);
                var cosine = System.Numerics.Vector3.Dot(toPixel, direction);
                if (cosine < cosRadius) continue;

                var i = (y * src.Width + x) * 4;
                // Weighted by cos(theta) as well as solid angle: irradiance is what lands on a
                // surface facing the sun, not the raw radiance sum.
                var w = solidAngle * cosine;
                irradiance += new System.Numerics.Vector3(
                    src.Pixels[i] * w, src.Pixels[i + 1] * w, src.Pixels[i + 2] * w);
            }
        }

        return new HdrSun(direction, irradiance, cosRadius);
    }

    /// <summary>
    /// The same sky with the sun's disc replaced by the sky immediately around it.
    /// </summary>
    /// <remarks>
    /// Lighting integrals use this version so a separate directional light does not count the same
    /// sun twice. The visible environment keeps the disc. Filling from the surrounding annulus also
    /// avoids introducing a dark spot into filtered reflections.
    /// </remarks>
    public static HdrImageData WithoutSun(HdrImageData src, HdrSun sun)
    {
        ArgumentNullException.ThrowIfNull(src);

        // The annulus: just outside the disc, out to twice its angular radius. Wide enough to
        // average over sky rather than over the sun's own bloom, narrow enough to stay local.
        var cosOuter = MathF.Cos(MathF.Acos(sun.CosAngularRadius) * 2.0f);

        var fill = System.Numerics.Vector3.Zero;
        var fillCount = 0;
        var inside = new List<int>();

        for (var y = 0; y < src.Height; y++)
        {
            for (var x = 0; x < src.Width; x++)
            {
                var toPixel = -EquirectDirection((x + 0.5f) / src.Width, (y + 0.5f) / src.Height);
                var cosine = System.Numerics.Vector3.Dot(toPixel, sun.Direction);
                var i = (y * src.Width + x) * 4;

                if (cosine >= sun.CosAngularRadius) { inside.Add(i); continue; }
                if (cosine < cosOuter) continue;

                fill += new System.Numerics.Vector3(src.Pixels[i], src.Pixels[i + 1], src.Pixels[i + 2]);
                fillCount++;
            }
        }

        if (inside.Count == 0) return src;

        var mean = fillCount > 0 ? fill / fillCount : System.Numerics.Vector3.Zero;
        var pixels = (float[])src.Pixels.Clone();
        foreach (var i in inside)
        {
            pixels[i] = mean.X; pixels[i + 1] = mean.Y; pixels[i + 2] = mean.Z;
        }

        return src with { Pixels = pixels };
    }

    /// <summary>The world direction an equirect pixel looks toward.</summary>
    internal static System.Numerics.Vector3 EquirectDirection(float u, float v)
    {
        var theta = (u - 0.5f) * 2.0f * MathF.PI;
        var phi = v * MathF.PI;
        var cy = MathF.Cos(phi);
        var horiz = MathF.Sqrt(MathF.Max(1.0f - cy * cy, 0.0f));
        return System.Numerics.Vector3.Normalize(
            new System.Numerics.Vector3(horiz * MathF.Cos(theta), cy, horiz * MathF.Sin(theta)));
    }

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

    // --- Sheen: the Charlie lobe's own prefilter and albedo table ---------
    // Charlie concentrates energy toward the horizon rather than distributing microfacets like
    // GGX, so sheen needs its own convolution to retain the grazing rim of cloth.

    /// <summary>Prefilters the environment with the Charlie distribution, one mip per roughness.</summary>
    public static Half[][] BakeSheenPrefilteredMips(
        HdrImageData equirect, int baseFaceSize, int mipCount, float sampleClampMagnitude = 50.0f)
    {
        ArgumentNullException.ThrowIfNull(equirect);
        if (baseFaceSize <= 0) throw new ArgumentOutOfRangeException(nameof(baseFaceSize));
        if (mipCount <= 0) throw new ArgumentOutOfRangeException(nameof(mipCount));

        var mips = new Half[mipCount][];
        for (var mip = 0; mip < mipCount; mip++)
        {
            var mipSize = Math.Max(1, baseFaceSize >> mip);
            var roughness = mipCount <= 1 ? 0.0f : (float)mip / (mipCount - 1);
            // Unlike GGX there is no mirror case at mip 0: Charlie at roughness 0 is still a lobe,
            // just a very tight one at the horizon, so every mip integrates.
            var sampleCount = 64 + mip * 64;
            mips[mip] = BakeSheenMip(equirect, mipSize, Math.Max(roughness, 0.07f), sampleCount, sampleClampMagnitude);
        }
        return mips;
    }

    private static Half[] BakeSheenMip(
        HdrImageData equirect, int faceSize, float roughness, int sampleCount, float sampleClampMagnitude)
    {
        var pixelsPerFace = faceSize * faceSize * 4;
        var output = new Half[pixelsPerFace * 6];

        Parallel.For(0, 6, face =>
        {
            var faceOffset = face * pixelsPerFace;
            for (var j = 0; j < faceSize; j++)
            {
                var v = (j + 0.5f) / faceSize;
                for (var i = 0; i < faceSize; i++)
                {
                    var u = (i + 0.5f) / faceSize;
                    var N = Vector3.Normalize(CubeDirection(face, u, v));
                    var V = N;   // the same split-sum assumption the GGX prefilter makes

                    // Sample incoming directions directly. Reusing the GGX half-vector reflection
                    // scheme sends Charlie's horizon-weighted samples below the surface.
                    var tangentZ = MathF.Abs(N.Z) < 0.999f ? new Vector3(0, 0, 1) : new Vector3(1, 0, 0);
                    var tangent = Vector3.Normalize(Vector3.Cross(tangentZ, N));
                    var bitangent = Vector3.Cross(N, tangent);

                    var prefilter = Vector3.Zero;
                    var weight = 0.0f;
                    for (var s = 0; s < sampleCount; s++)
                    {
                        var xi = Hammersley(s, sampleCount);
                        // Cosine-distributed L over the hemisphere about N.
                        var cosTheta = MathF.Sqrt(Math.Max(0.0f, 1.0f - xi.Y));
                        var sinTheta = MathF.Sqrt(Math.Max(0.0f, 1.0f - cosTheta * cosTheta));
                        var phi = 2.0f * MathF.PI * xi.X;
                        var lLocal = new Vector3(sinTheta * MathF.Cos(phi), sinTheta * MathF.Sin(phi), cosTheta);
                        var L = Vector3.Normalize(tangent * lLocal.X + bitangent * lLocal.Y + N * lLocal.Z);

                        var NdotL = Math.Max(Vector3.Dot(N, L), 0.0f);
                        if (NdotL <= 0.0f) continue;
                        var H = Vector3.Normalize(L + V);
                        var NdotH = Math.Max(Vector3.Dot(N, H), 0.0f);
                        var NdotV = Math.Max(Vector3.Dot(N, V), 0.0f);

                        // The lobe itself is the weight, which is what makes this a convolution
                        // with Charlie rather than a cosine blur wearing its name.
                        var w = CharlieDistribution(NdotH, roughness) * AshikhminVisibility(NdotL, NdotV) * NdotL;
                        if (w <= 0.0f) continue;

                        var li = SampleEquirect(equirect, L);
                        li = Vector3.Min(li, new Vector3(sampleClampMagnitude));
                        prefilter += li * w;
                        weight += w;
                    }
                    if (weight > 0.0f) prefilter /= weight;

                    var idx = faceOffset + (j * faceSize + i) * 4;
                    output[idx + 0] = (Half)prefilter.X;
                    output[idx + 1] = (Half)prefilter.Y;
                    output[idx + 2] = (Half)prefilter.Z;
                    output[idx + 3] = (Half)1.0f;
                }
            }
        });
        return output;
    }

    /// <summary>
    /// The Charlie lobe's directional albedo E(NdotV, roughness), as an RGBA8 table in R.
    /// </summary>
    /// <remarks>
    /// The table is numerically integrated from the same distribution used by the prefilter rather
    /// than fitted independently. Uniform hemisphere sampling is intentional: the integrand already
    /// contains the distribution, so importance-sampling it would cancel the term being measured.
    /// </remarks>
    public static byte[] BakeSheenLut(int size, int sampleCount = 1024)
    {
        if (size <= 0) throw new ArgumentOutOfRangeException(nameof(size));
        if (sampleCount <= 0) throw new ArgumentOutOfRangeException(nameof(sampleCount));

        var output = new byte[size * size * 4];
        Parallel.For(0, size, j =>
        {
            var roughness = (j + 0.5f) / size;      // Y axis = sheen roughness
            for (var i = 0; i < size; i++)
            {
                var NdotV = (i + 0.5f) / size;
                var V = new Vector3(MathF.Sqrt(1.0f - NdotV * NdotV), 0.0f, NdotV);

                var sum = 0.0f;
                for (var s = 0; s < sampleCount; s++)
                {
                    var xi = Hammersley(s, sampleCount);
                    // Uniform on the hemisphere: pdf = 1/(2*pi), so the estimator carries 2*pi/N.
                    var cosTheta = xi.Y;
                    var sinTheta = MathF.Sqrt(Math.Max(0.0f, 1.0f - cosTheta * cosTheta));
                    var phi = 2.0f * MathF.PI * xi.X;
                    var L = new Vector3(sinTheta * MathF.Cos(phi), sinTheta * MathF.Sin(phi), cosTheta);

                    var H = Vector3.Normalize(L + V);
                    var NdotL = Math.Max(L.Z, 0.0f);
                    var NdotH = Math.Max(H.Z, 0.0f);
                    if (NdotL <= 0.0f) continue;
                    sum += CharlieDistribution(NdotH, roughness) * AshikhminVisibility(NdotL, NdotV) * NdotL;
                }
                var e = sum * (2.0f * MathF.PI) / sampleCount;

                var idx = (j * size + i) * 4;
                output[idx + 0] = (byte)Math.Clamp(e * 255.0f, 0.0f, 255.0f);
                output[idx + 1] = 0;
                output[idx + 2] = 0;
                output[idx + 3] = 255;
            }
        });
        return output;
    }

    // The same two terms sheen.glsl evaluates, kept in step with it by name so a change to one is
    // an obvious prompt to check the other. A LUT baked from a different lobe than the shader uses
    // is a disagreement no picture makes obvious.
    private static float CharlieDistribution(float NdotH, float roughness)
    {
        var alpha = Math.Max(roughness * roughness, 1e-4f);
        var invAlpha = 1.0f / alpha;
        var sin2 = Math.Max(1.0f - NdotH * NdotH, 1e-7f);
        return (2.0f + invAlpha) * MathF.Pow(sin2, invAlpha * 0.5f) / (2.0f * MathF.PI);
    }

    private static float AshikhminVisibility(float NdotL, float NdotV) =>
        Math.Clamp(1.0f / (4.0f * (NdotL + NdotV - NdotL * NdotV)), 0.0f, 1.0f);

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

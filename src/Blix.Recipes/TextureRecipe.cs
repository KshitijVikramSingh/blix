using System.Diagnostics;
using BCnEncoder.Encoder;
using BCnEncoder.Shared;
using Blix.Cooked;
using Blix.Graphics;
using Blix.Graphics.Images;
using Microsoft.Toolkit.HighPerformance;

namespace Blix.Recipes;

/// <summary>What a texture is FOR, which is what decides how it is encoded.</summary>
/// <remarks>
/// Role is usage metadata rather than a property of the pixels. Callers should provide it when a
/// material channel supplies that knowledge; standalone recipe calls fall back to filename
/// classification. The resolved role, format, flags, encoder, and quality are recorded in the
/// stamp.
/// </remarks>
public enum TextureRole { BaseColor, Normal, Emissive, MetallicRoughness, Linear }

/// <summary>
/// PNG/JPEG to <c>.blixtex</c>: mip chain, BCn encode by role, one file at a time.
/// </summary>
/// <remarks>
/// The recipe transforms one file. Tree traversal, parallel scheduling, and progress reporting are
/// driver responsibilities.
/// </remarks>
public static class TextureRecipe
{
    private readonly record struct EncodingPlan(
        TextureRole Role,
        TextureFormat Format,
        BlixTex.Flags Flags,
        bool Compressed,
        bool NativeBc7,
        int NativeQuality,
        string Backend,
        string Quality)
    {
        public string StampPrefix =>
            $"role={Role} format={Format} flags={Flags} encoder={Backend} quality={Quality}";
    }

    /// <summary>Whether a texture artifact matches today's recipe, source, role and encoder.</summary>
    public static bool IsCurrent(string source, string destination, TextureRole? role = null)
    {
        var header = CookedFile.TryReadHeader(destination);
        if (header is not { Magic: BlixTex.Magic, FormatVersion: BlixTex.Version3 }) return false;
        if (!header.Value.Stamp.MatchesProducerAndSource(
                BlixTex.ShippedRecipe, BlixTex.ShippedRecipeVersion, source))
            return false;

        var prefix = ResolveEncoding(source, role).StampPrefix + " mips=";
        var parameters = header.Value.Stamp.Parameters;
        if (!parameters.StartsWith(prefix, StringComparison.Ordinal)) return false;
        return int.TryParse(parameters.AsSpan(prefix.Length), out var mipCount) && mipCount > 0;
    }

    /// <param name="role">
    /// The role to cook as, when the CALLER knows it. Null means classify by filename.
    /// </param>
    /// <remarks>
    /// Filename classification is only a fallback. Extracted or generically named images can lose
    /// their colour-space role, so callers that know the material channel must pass it explicitly.
    /// <para>
    /// The mesh cook always knows the role, because it finds each image ON a material channel. A
    /// heuristic is the right answer when nothing knows better and the wrong one when something
    /// does.
    /// </para>
    /// </remarks>
    public static void CookOne(
        string source, string destination, out long sourceLen, out long destLen, TextureRole? role = null)
    {
        var verbose = Environment.GetEnvironmentVariable("BLIX_COOK_VERBOSE") != null;
        var plan = ResolveEncoding(source, role);
        var name = Path.GetFileName(source);
        sourceLen = new FileInfo(source).Length;
        using var probe = File.OpenRead(source);
        if (probe.Length == 0)
        {
            throw new InvalidDataException("source file is zero bytes (likely an APFS sparse-copy artifact; re-run tools/setup-sponza-modern.sh)");
        }
        probe.Close();

        using (var stream = File.OpenRead(source))
        {
            var decodeSw = Stopwatch.StartNew();
            // Preserve the canonical ORM meaning for grayscale roughness sources. A generic RGBA
            // expansion would copy roughness into B and make the same value read as metallic.
            var image = plan.Role == TextureRole.MetallicRoughness
                ? ImageLoader.LoadMetallicRoughness(stream)
                : ImageLoader.LoadRgba32(stream);
            if (verbose) Console.WriteLine($"\r    decoded {name} {image.Width}x{image.Height} in {decodeSw.ElapsedMilliseconds} ms");

            var mipSw = Stopwatch.StartNew();
            var mipsRgba = GenerateMipsBoxFilter(image.Pixels, image.Width, image.Height, minDim: 4);
            if (verbose) Console.WriteLine($"\r    mipped  {name} {mipsRgba.Count} levels in {mipSw.ElapsedMilliseconds} ms");

            byte[][] encodedMips;
            if (plan.NativeBc7)
            {
                // Use perceptual weighting for sRGB colour and linear weighting for data. Encoding
                // is single-threaded here because tree drivers parallelize across textures.
                var perceptual = (plan.Flags & BlixTex.Flags.Srgb) != 0;
                encodedMips = new byte[mipsRgba.Count][];
                for (var i = 0; i < mipsRgba.Count; i++)
                {
                    var (pixels, w, h) = mipsRgba[i];
                    var encodeSw = Stopwatch.StartNew();
                    encodedMips[i] = Blix.Recipes.Bc7Native.EncodeImage(
                        pixels, w, h, perceptual, plan.NativeQuality, numThreads: 1);
                    if (verbose) Console.WriteLine($"\r    bc7(native q{plan.NativeQuality}) {name} mip{i} {w}x{h} -> {encodedMips[i].Length} bytes in {encodeSw.ElapsedMilliseconds} ms");
                }
            }
            else if (plan.Compressed)
            {
                // Managed fallback for non-BC7 targets or an explicitly forced BC cook without the
                // native encoder. Driver-level parallelism owns core saturation.
                var encoder = new BcEncoder
                {
                    Options = { IsParallel = false },
                    OutputOptions =
                    {
                        GenerateMipMaps = false,
                        Quality = CompressionQuality.Fast,
                        Format = ToBcFormat(plan.Format),
                        FileFormat = OutputFileFormat.Dds,
                    },
                };
                encodedMips = new byte[mipsRgba.Count][];
                for (var i = 0; i < mipsRgba.Count; i++)
                {
                    var (pixels, w, h) = mipsRgba[i];
                    var encodeSw = Stopwatch.StartNew();
                    encodedMips[i] = EncodeMip(encoder, pixels, w, h);
                    if (verbose) Console.WriteLine($"\r    encoded {name} mip{i} {w}x{h} -> {encodedMips[i].Length} bytes in {encodeSw.ElapsedMilliseconds} ms");
                }
            }
            else
            {
                // Rgba8 multi-mip: the mip data is the pre-filtered RGBA bytes
                // as-is. No compression cost; runtime gets pre-baked mips
                // instead of generating the chain during GPU upload.
                encodedMips = new byte[mipsRgba.Count][];
                for (var i = 0; i < mipsRgba.Count; i++) encodedMips[i] = mipsRgba[i].Pixels;
            }

            // Record the semantic input and the concrete backend. Two machines can resolve the
            // same role to different bytes when native BC7 availability or quality differs.
            var texStamp = CookStamp.Of(
                BlixTex.ShippedRecipe, BlixTex.ShippedRecipeVersion, source, destination,
                $"{plan.StampPrefix} mips={encodedMips.Length}");

            BlixTexWriter.Write(destination, new BlixTexImage(
                image.Width, image.Height, plan.Format, encodedMips, plan.Flags), texStamp);
        }
        destLen = new FileInfo(destination).Length;
    }

    // Native BC7 quality knob: 0 fastest, 1 balanced (default), 2 high. Set
    // BLIX_BC7_QUALITY to override. Higher = slower cook, better quality.
    private static int Bc7Quality() =>
        int.TryParse(Environment.GetEnvironmentVariable("BLIX_BC7_QUALITY"), out var q)
            ? Math.Clamp(q, 0, 2)
            : 1;

    private static EncodingPlan ResolveEncoding(string source, TextureRole? role)
    {
        var resolvedRole = role ?? ClassifyRole(source);
        var (bcFormat, flags) = PickFormat(resolvedRole);

        // Prefer BC encoding when the native encoder is available. Falling back to RGBA8 avoids
        // making the much slower managed encoder an accidental default. BLIX_COOK_FORMAT can force
        // bc7 or rgba8 for controlled cooks.
        var formatEnv = Environment.GetEnvironmentVariable("BLIX_COOK_FORMAT");
        var compressed = formatEnv is not null
            ? formatEnv.Equals("bc7", StringComparison.OrdinalIgnoreCase)
            : Blix.Recipes.Bc7Native.Available;
        if (!compressed)
        {
            return new EncodingPlan(
                resolvedRole, TextureFormat.Rgba8, flags, false, false, 0, "raw", "none");
        }

        var native = (bcFormat is TextureFormat.Bc7Srgb or TextureFormat.Bc7Unorm)
            && Blix.Recipes.Bc7Native.Available;
        var nativeQuality = native ? Bc7Quality() : 0;
        return new EncodingPlan(
            resolvedRole,
            bcFormat,
            flags,
            true,
            native,
            nativeQuality,
            native ? "bc7-native" : "bcnencoder-managed",
            native
                ? nativeQuality.ToString(System.Globalization.CultureInfo.InvariantCulture)
                : CompressionQuality.Fast.ToString());
    }

    // Picks a BCn format + flags based on the heuristic role classification.
    private static (TextureFormat Format, BlixTex.Flags Flags) PickFormat(TextureRole role) => role switch
    {
        TextureRole.BaseColor          => (TextureFormat.Bc7Srgb, BlixTex.Flags.Srgb),
        TextureRole.Emissive           => (TextureFormat.Bc7Srgb, BlixTex.Flags.Srgb),
        // BC5 spends the same 128 bits per 4x4 block as BC7 on the two independent tangent-normal
        // channels. Shaders reconstruct Z from X/Y for both source and cooked paths.
        TextureRole.Normal             => (TextureFormat.Bc5Unorm, BlixTex.Flags.NormalMap),
        TextureRole.MetallicRoughness  => (TextureFormat.Bc7Unorm, BlixTex.Flags.None),
        TextureRole.Linear             => (TextureFormat.Bc7Unorm, BlixTex.Flags.None),
        _                              => (TextureFormat.Bc7Unorm, BlixTex.Flags.None),
    };

    // Metallic-roughness stays BC7 because its useful channels are G and B. BC5 would require a
    // remap to R/G and make source and cooked textures require different shader swizzles.
    private static CompressionFormat ToBcFormat(TextureFormat fmt) => fmt switch
    {
        TextureFormat.Bc7Srgb  => CompressionFormat.Bc7,  // sRGB selection lives on the GL internal format side
        TextureFormat.Bc7Unorm => CompressionFormat.Bc7,
        TextureFormat.Bc5Unorm => CompressionFormat.Bc5,
        _ => throw new NotSupportedException($"Unsupported BC format target: {fmt}"),
    };

    private static byte[] EncodeMip(BcEncoder encoder, byte[] rgbaPixels, int width, int height)
    {
        // BCnEncoder takes a ReadOnlyMemory2D<ColorRgba32>. Reinterpret the
        // byte[] as a span of ColorRgba32 (same layout: R, G, B, A bytes) and
        // build a 2D view.
        if (rgbaPixels.Length != width * height * 4)
        {
            throw new ArgumentException($"Mip data size mismatch: {rgbaPixels.Length} bytes != {width * height * 4}.");
        }
        var colors = new ColorRgba32[width * height];
        for (var i = 0; i < colors.Length; i++)
        {
            colors[i] = new ColorRgba32(
                rgbaPixels[i * 4 + 0],
                rgbaPixels[i * 4 + 1],
                rgbaPixels[i * 4 + 2],
                rgbaPixels[i * 4 + 3]);
        }
        var memory2D = new ReadOnlyMemory2D<ColorRgba32>(colors, height, width);
        // EncodeToRawBytes returns one byte[] per mip. We've already pre-generated
        // the mip chain ourselves (encoder.OutputOptions.GenerateMipMaps = false),
        // so the result is a single-entry array; take [0].
        return encoder.EncodeToRawBytes(memory2D)[0];
    }

    // CPU mip chain via box filter (average of 2x2 pixels). Stops when the
    // smaller dimension hits `minDim`. Returns mip 0 (the original) first.

    /// <summary>Scales each mip's alpha so the fraction above the cutoff matches the base level.</summary>
    /// <remarks>
    /// Textures with no partial alpha are untouched: if every texel is 0 or 255 the coverage already
    /// matches at every level and the search returns a scale of 1.
    /// </remarks>
    private static void RescaleAlphaForCoverage(List<(byte[] Pixels, int Width, int Height)> mips)
    {
        const int cutoff = 128;
        static double Coverage(byte[] px, double scale)
        {
            var n = 0;
            for (var i = 3; i < px.Length; i += 4)
                if (px[i] * scale >= cutoff) n++;
            return n / (double)(px.Length / 4);
        }

        var target = Coverage(mips[0].Pixels, 1.0);
        if (target <= 0.0) return;

        for (var m = 1; m < mips.Count; m++)
        {
            var px = mips[m].Pixels;
            if (Math.Abs(Coverage(px, 1.0) - target) < 0.001) continue;

            // Coverage rises monotonically with the scale, so bisect it.
            double lo = 0.0, hi = 64.0, scale = 1.0;
            for (var it = 0; it < 24; it++)
            {
                scale = 0.5 * (lo + hi);
                if (Coverage(px, scale) < target) lo = scale; else hi = scale;
            }
            if (Math.Abs(scale - 1.0) < 0.01) continue;
            for (var i = 3; i < px.Length; i += 4)
                px[i] = (byte)Math.Clamp((int)Math.Round(px[i] * scale), 0, 255);
        }
    }

    private static List<(byte[] Pixels, int Width, int Height)> GenerateMipsBoxFilter(
        byte[] basePixels, int baseW, int baseH, int minDim)
    {
        var mips = new List<(byte[], int, int)> { (basePixels, baseW, baseH) };
        var current = basePixels;
        var w = baseW;
        var h = baseH;
        while (w > minDim && h > minDim)
        {
            var nw = Math.Max(minDim, w / 2);
            var nh = Math.Max(minDim, h / 2);
            // Skip generating a mip that would equal the previous one.
            if (nw == w && nh == h) break;
            var next = new byte[nw * nh * 4];
            var srcW = w;
            for (var y = 0; y < nh; y++)
            {
                for (var x = 0; x < nw; x++)
                {
                    var sx = x * 2;
                    var sy = y * 2;
                    var sx1 = Math.Min(sx + 1, w - 1);
                    var sy1 = Math.Min(sy + 1, h - 1);
                    // Weight RGB by alpha so transparent padding does not tint cutout edges. Alpha
                    // itself remains a plain box average. Opaque images reduce to the ordinary box
                    // filter because every colour weight is equal.
                    var i0 = (sy * srcW + sx) * 4;
                    var i1 = (sy * srcW + sx1) * 4;
                    var i2 = (sy1 * srcW + sx) * 4;
                    var i3 = (sy1 * srcW + sx1) * 4;
                    int a0 = current[i0 + 3], a1 = current[i1 + 3];
                    int a2 = current[i2 + 3], a3 = current[i3 + 3];
                    var alphaSum = a0 + a1 + a2 + a3;
                    for (var c = 0; c < 3; c++)
                    {
                        var weighted = alphaSum > 0
                            ? (current[i0 + c] * a0 + current[i1 + c] * a1
                             + current[i2 + c] * a2 + current[i3 + c] * a3 + alphaSum / 2) / alphaSum
                            // Fully transparent everywhere: no colour is "there" to preserve, so the
                            // plain average keeps whatever the source held rather than inventing black.
                            : (current[i0 + c] + current[i1 + c] + current[i2 + c] + current[i3 + c] + 2) / 4;
                        next[(y * nw + x) * 4 + c] = (byte)weighted;
                    }
                    next[(y * nw + x) * 4 + 3] = (byte)((alphaSum + 2) / 4);
                }
            }
            mips.Add((next, nw, nh));
            current = next;
            w = nw;
            h = nh;
        }
        // A box filter preserves mean alpha but can collapse the fraction above the cutout threshold,
        // making distant foliage fade or disappear. Scale each mip to match base-level coverage;
        // bisection works because coverage is monotone in that scale.
        RescaleAlphaForCoverage(mips);
        return mips;
    }

    private static TextureRole ClassifyRole(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path).ToLowerInvariant();
        if (name.Contains("basecolor") || name.Contains("albedo") || name.Contains("diffuse"))
            return TextureRole.BaseColor;
        if (name.Contains("emiss"))
            return TextureRole.Emissive;
        // Check data-channel names before normals so compound names such as normal_roughness retain
        // their metallic/roughness interpretation.
        if (name.Contains("roughness") || name.Contains("metalness")
            || name.Contains("metallic") || name.Contains("metalrough")
            || name.Contains("metal_rough"))
            return TextureRole.MetallicRoughness;
        if (name.Contains("normal") || name.EndsWith("_n") || name.EndsWith(".n"))
            return TextureRole.Normal;
        return TextureRole.Linear;
    }

    /// <summary>The uniform entry point the index finds and <c>blix cook</c> calls.</summary>
    [Recipe(BlixTex.ShippedRecipe,
        Produces = ".blixtex",
        Consumes = ".png;.jpg;.jpeg",
        Version = BlixTex.ShippedRecipeVersion,
        Summary = "PNG/JPEG to a mipped BC7/BC5 .blixtex")]
    public static CookOutcome Cook(CookRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        CookOne(request.SourcePath, request.OutputPath, out var sourceLen, out var destLen);
        return CookOutcome.Written($"{sourceLen / 1024.0:0.0} KB -> {destLen / 1024.0:0.0} KB");
    }
}

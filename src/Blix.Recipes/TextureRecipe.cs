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
/// Not a property of the image — a normal map and an albedo can be the same pixels — so it is
/// inferred from the filename and is the one cook setting that is guessed rather than given. That
/// guess is now recorded in the stamp, which is what makes a wrong one visible instead of
/// mysterious.
/// </remarks>
public enum TextureRole { BaseColor, Normal, Emissive, MetallicRoughness, Linear }

/// <summary>
/// PNG/JPEG to <c>.blixtex</c>: mip chain, BCn encode by role, one file at a time.
/// </summary>
/// <remarks>
/// <b>Moved out of the cook tool's <c>Program.cs</c> so that all three of Blix's recipes are the
/// same kind of thing.</b> It was never engine code — unlike the mesh recipe — but it was welded to
/// a CLI, so it could only be invoked by a person typing a directory. The split that matters is
/// driver from recipe: walking a tree, printing progress and parallelising stay with the tool;
/// turning one source file into one cooked file lives here, where a build rule can reach it.
/// </remarks>
public static class TextureRecipe
{
    /// <param name="role">
    /// The role to cook as, when the CALLER knows it. Null means classify by filename.
    /// </param>
    /// <remarks>
    /// <b>An explicit role exists because filename sniffing silently destroyed a texture.</b>
    /// Classification reads the file's NAME, which works for an authored tree where a base colour
    /// map is called "*_BaseColor.png". It fails completely for an image extracted out of a .glb,
    /// whose name this cook invents: Rogue's base colour arrived as "rogue_texture", matched no
    /// rule, fell through to Linear, and was written Bc7Unorm instead of Bc7Srgb. Nothing errored.
    /// The character simply rendered blown out, and the lab baseline caught it as a 9% pixel change
    /// with a mean delta of 90 — which a person spotted as "blown out" before any of this was
    /// measured.
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
        // Cook format selection. BC7 is 4x smaller than Rgba8 on disk + in GPU
        // memory and is the DEFAULT when the fast native encoder (Bc7Native, the
        // vendored bc7enc) is available — it BC7-encodes a 4K texture in a second
        // or so. Without the native lib we fall back to Rgba8 rather than the
        // managed BCnEncoder.Net path (minutes per 4K texture). Override:
        //   BLIX_COOK_FORMAT=bc7   force BC7 (managed fallback if no native lib)
        //   BLIX_COOK_FORMAT=rgba8 force uncompressed Rgba8
        var formatEnv = Environment.GetEnvironmentVariable("BLIX_COOK_FORMAT");
        var bcMode = formatEnv is not null
            ? formatEnv.Equals("bc7", StringComparison.OrdinalIgnoreCase)
            : Blix.Recipes.Bc7Native.Available;
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
            var resolvedRole = role ?? ClassifyRole(source);
            // MR textures need channel-aware loading: 1-channel grayscale
            // PNGs (Modern Sponza's "*_Roughness.png") get expanded by stb to
            // (Y, Y, Y, 255), which the shader would then read as
            // metallic = roughness. LoadMetallicRoughness detects the
            // grayscale source and zeroes the B channel so the cooked
            // .blixtex stores (255, Y, 0, 255) -- the canonical ORM layout.
            var image = resolvedRole == TextureRole.MetallicRoughness
                ? ImageLoader.LoadMetallicRoughness(stream)
                : ImageLoader.LoadRgba32(stream);
            if (verbose) Console.WriteLine($"\r    decoded {name} {image.Width}x{image.Height} in {decodeSw.ElapsedMilliseconds} ms");
            var (bcFormat, flags) = PickFormat(resolvedRole);
            var format = bcMode ? bcFormat : TextureFormat.Rgba8;

            var mipSw = Stopwatch.StartNew();
            var mipsRgba = GenerateMipsBoxFilter(image.Pixels, image.Width, image.Height, minDim: 4);
            if (verbose) Console.WriteLine($"\r    mipped  {name} {mipsRgba.Count} levels in {mipSw.ElapsedMilliseconds} ms");

            byte[][] encodedMips;
            if (bcMode && bcFormat is TextureFormat.Bc7Srgb or TextureFormat.Bc7Unorm && Blix.Recipes.Bc7Native.Available)
            {
                // Fast native BC7 (bc7enc). Perceptual YCbCr weighting for sRGB
                // color maps; linear weighting for normal/data maps. Single-threaded
                // per texture -- the outer Parallel.ForEach over textures already
                // saturates cores.
                var perceptual = (flags & BlixTex.Flags.Srgb) != 0;
                var quality = Bc7Quality();
                encodedMips = new byte[mipsRgba.Count][];
                for (var i = 0; i < mipsRgba.Count; i++)
                {
                    var (pixels, w, h) = mipsRgba[i];
                    var encodeSw = Stopwatch.StartNew();
                    encodedMips[i] = Blix.Recipes.Bc7Native.EncodeImage(pixels, w, h, perceptual, quality, numThreads: 1);
                    if (verbose) Console.WriteLine($"\r    bc7(native q{quality}) {name} mip{i} {w}x{h} -> {encodedMips[i].Length} bytes in {encodeSw.ElapsedMilliseconds} ms");
                }
            }
            else if (bcMode)
            {
                // Managed fallback (no native lib, or a non-BC7 target). Encoder-
                // internal parallelism is OFF -- the outer Parallel.ForEach handles
                // cores. Slow (minutes per 4K texture); only hit when Bc7Native is
                // unavailable.
                var encoder = new BcEncoder
                {
                    Options = { IsParallel = false },
                    OutputOptions =
                    {
                        GenerateMipMaps = false,
                        Quality = CompressionQuality.Fast,
                        Format = ToBcFormat(bcFormat),
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
                // instead of glGenerateMipmap-at-upload, which is still a
                // measurable win on large textures.
                encodedMips = new byte[mipsRgba.Count][];
                for (var i = 0; i < mipsRgba.Count; i++) encodedMips[i] = mipsRgba[i].Pixels;
            }

            // The role is what picks BC7sRGB vs BC5 vs BC7Unorm vs Rgba8, so it is the setting that
            // decides the bytes and it goes in the stamp. `flags` rides along because sRGB and
            // normal-map are read back out of the file, and recording the input beside the output is
            // what makes a mismatch visible rather than a mystery.
            var texStamp = CookStamp.Of(
                BlixTex.ShippedRecipe, BlixTex.ShippedRecipeVersion, source, destination,
                $"format={format} flags={flags} mips={encodedMips.Length}");

            BlixTexWriter.Write(destination, new BlixTexImage(
                image.Width, image.Height, format, encodedMips, flags), texStamp);
        }
        destLen = new FileInfo(destination).Length;
    }

    // Native BC7 quality knob: 0 fastest, 1 balanced (default), 2 high. Set
    // BLIX_BC7_QUALITY to override. Higher = slower cook, better quality.
    private static int Bc7Quality() =>
        int.TryParse(Environment.GetEnvironmentVariable("BLIX_BC7_QUALITY"), out var q)
            ? Math.Clamp(q, 0, 2)
            : 1;

    // Picks a BCn format + flags based on the heuristic role classification.
    private static (TextureFormat Format, BlixTex.Flags Flags) PickFormat(TextureRole role) => role switch
    {
        TextureRole.BaseColor          => (TextureFormat.Bc7Srgb, BlixTex.Flags.Srgb),
        TextureRole.Emissive           => (TextureFormat.Bc7Srgb, BlixTex.Flags.Srgb),
        // <b>BC5 for normals, and it buys PRECISION rather than bytes.</b> That distinction was got
        // wrong out loud on the way here, so it is written down: BC5, BC7 and BC6h are all 16 bytes
        // per 4x4 block — the engine's own MipByteCount says so in one line — and switching a 4K
        // normal map between them moves nothing on disk. What moves is how the block is spent. BC5
        // gives two channels a BC4-style endpoint pair each; BC7 divides the same 128 bits across
        // three or four. A tangent-space normal is unit length, so Z is not information at all —
        // it is sqrt(1 - x² - y²) — and the two channels that ARE information get the whole block.
        //
        // The size lever is elsewhere and is not this: a single-channel BC4 is 8 bytes per block,
        // and resolution is a bigger one still. Neither is a normal-map question.
        //
        // The shader side of this landed long before the cook did, and each side's comment was
        // waiting on the other: lit.frag has said "Cooked normals are BC5 (2-channel RG, blue
        // dropped)" and reconstructed Z for some time, while this line said BC5 "requires shader
        // changes that haven't landed yet". Nothing was blocked; nobody checked.
        //
        // Reconstruction is correct for BOTH paths, which is what makes this need no branch and no
        // flag: an RGBA8 normal map's stored Z already equals sqrt(1 - x² - y²), so a shader that
        // derives it reads source and cooked identically.
        TextureRole.Normal             => (TextureFormat.Bc5Unorm, BlixTex.Flags.NormalMap),
        TextureRole.MetallicRoughness  => (TextureFormat.Bc7Unorm, BlixTex.Flags.None),
        TextureRole.Linear             => (TextureFormat.Bc7Unorm, BlixTex.Flags.None),
        _                              => (TextureFormat.Bc7Unorm, BlixTex.Flags.None),
    };

    // Metallic-roughness stays BC7, and for a sharper reason than inertia. Its two useful channels
    // are G and B, while BC5's two arrive as R and G — so cooking it to BC5 means REMAPPING, after
    // which a cooked MR texture and a source PNG no longer mean the same thing at the same swizzle
    // and every shader sampling one needs to know which it got. Normals need no such divergence,
    // because reconstructing Z is correct for both. That is what makes normals the textbook case
    // and MR a separate decision with its own flag, not a line to change beside this one.
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
                    for (var c = 0; c < 4; c++)
                    {
                        var a = current[(sy * srcW + sx) * 4 + c];
                        var b = current[(sy * srcW + sx1) * 4 + c];
                        var d = current[(sy1 * srcW + sx) * 4 + c];
                        var e = current[(sy1 * srcW + sx1) * 4 + c];
                        next[(y * nw + x) * 4 + c] = (byte)((a + b + d + e + 2) / 4);
                    }
                }
            }
            mips.Add((next, nw, nh));
            current = next;
            w = nw;
            h = nh;
        }
        return mips;
    }

    private static TextureRole ClassifyRole(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path).ToLowerInvariant();
        if (name.Contains("basecolor") || name.Contains("albedo") || name.Contains("diffuse"))
            return TextureRole.BaseColor;
        if (name.Contains("emiss"))
            return TextureRole.Emissive;
        // MR detection. Modern Sponza names them "<material>_Roughness.png" or
        // "<material>_Roughness<material>_Metalness.png" (the concatenation
        // pattern is how their exporter joins the two original maps). Check
        // for roughness/metalness/metallic/metalrough as substrings -- comes
        // BEFORE the normal check so "normal_roughness.png" doesn't get mis-
        // classified as normal.
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

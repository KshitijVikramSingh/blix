using System.Diagnostics;
using BCnEncoder.Encoder;
using BCnEncoder.Shared;
using Blix.Graphics;
using Blix.Graphics.Images;
using Microsoft.Toolkit.HighPerformance;

// Blix asset cooker (CLI).
//
//   blix-cook textures <dir>
//     Walks <dir> recursively. For every .png / .jpg / .jpeg, generates a
//     CPU mip chain, picks a BCn format based on the file's role (BaseColor
//     -> BC7sRGB, Normal -> BC5, MR/Occlusion -> BC7Unorm, Emissive
//     -> BC7sRGB), encodes each mip, and writes a side-by-side .blixtex.
//     Existing .blixtex files older than their source are re-cooked.
//
// Runtime reads .blixtex with no decode + no compression cost; the cost
// is paid here, once.

if (args.Length < 1)
{
    PrintUsage();
    return 1;
}

return args[0] switch
{
    "textures" => CookTextures(args),
    "probe" => CookProbe(args),
    "mesh" => CookMesh(args),
    "help" or "-h" or "--help" => Help(),
    _ => UnknownVerb(args[0]),
};

static void PrintUsage()
{
    Console.WriteLine("Usage:");
    Console.WriteLine("  blix-cook textures <directory>");
    Console.WriteLine("    Cook every .png/.jpg/.jpeg under <directory> (recursive)");
    Console.WriteLine("    into a multi-mip BC7/BC5 .blixtex sibling.");
    Console.WriteLine("  blix-cook probe <hdr-path> [--env-face=256] [--irr-face=32]");
    Console.WriteLine("                            [--prefilter-base=128] [--prefilter-mips=5]");
    Console.WriteLine("                            [--brdf-size=256] [--clamp=50]");
    Console.WriteLine("    Bake equirect-to-cube + diffuse irradiance + GGX prefilter");
    Console.WriteLine("    + BRDF LUT and write a sibling .blixprobe.");
    Console.WriteLine("  blix-cook mesh <path>");
    Console.WriteLine("    Cook a .gltf/.glb (file) or every .gltf/.glb under a directory");
    Console.WriteLine("    (recursive) into a sibling .blixmesh. Materials remain in the");
    Console.WriteLine("    .gltf -- only the per-primitive vertex/index data is cooked.");
}

static int CookMesh(string[] args)
{
    if (args.Length < 2)
    {
        Console.Error.WriteLine("Usage: blix-cook mesh <gltf-or-directory>");
        return 1;
    }
    var target = args[1];

    string[] sources;
    if (Directory.Exists(target))
    {
        sources = Directory
            .EnumerateFiles(target, "*.*", SearchOption.AllDirectories)
            .Where(p =>
            {
                var ext = Path.GetExtension(p).ToLowerInvariant();
                return ext is ".gltf" or ".glb";
            })
            .ToArray();
        Console.WriteLine($"Found {sources.Length} glTF source(s) under {target}");
    }
    else if (File.Exists(target))
    {
        sources = new[] { target };
    }
    else
    {
        Console.Error.WriteLine($"Path not found: {target}");
        return 1;
    }

    if (sources.Length == 0) return 0;

    foreach (var src in sources)
    {
        var outPath = Path.ChangeExtension(src, ".blixmesh");
        // Skip if .blixmesh is newer than its .gltf source.
        if (File.Exists(outPath))
        {
            var srcTime = File.GetLastWriteTimeUtc(src);
            var outTime = File.GetLastWriteTimeUtc(outPath);
            if (outTime > srcTime)
            {
                Console.WriteLine($"  up-to-date: {outPath}");
                continue;
            }
        }
        var sw = Stopwatch.StartNew();
        var count = Blix.GltfStaticImporter.CookToBlixMesh(src, outPath);
        var size = new FileInfo(outPath).Length;
        Console.WriteLine($"  cooked {Path.GetFileName(src)} -> {Path.GetFileName(outPath)} ({count} primitives, {size / 1024.0 / 1024.0:0.00} MB) in {sw.ElapsedMilliseconds} ms");
    }
    return 0;
}

static int CookProbe(string[] args)
{
    if (args.Length < 2)
    {
        Console.Error.WriteLine("Usage: blix-cook probe <hdr-path> [options]");
        return 1;
    }
    var hdrPath = args[1];
    if (!File.Exists(hdrPath))
    {
        Console.Error.WriteLine($"HDR not found: {hdrPath}");
        return 1;
    }

    int envFace = 256, irrFace = 32, prefilterBase = 128, prefilterMips = 5, brdfSize = 256;
    float clamp = 50.0f;
    foreach (var a in args.Skip(2))
    {
        if (a.StartsWith("--env-face=")) envFace = int.Parse(a.AsSpan("--env-face=".Length));
        else if (a.StartsWith("--irr-face=")) irrFace = int.Parse(a.AsSpan("--irr-face=".Length));
        else if (a.StartsWith("--prefilter-base=")) prefilterBase = int.Parse(a.AsSpan("--prefilter-base=".Length));
        else if (a.StartsWith("--prefilter-mips=")) prefilterMips = int.Parse(a.AsSpan("--prefilter-mips=".Length));
        else if (a.StartsWith("--brdf-size=")) brdfSize = int.Parse(a.AsSpan("--brdf-size=".Length));
        else if (a.StartsWith("--clamp=")) clamp = float.Parse(a.AsSpan("--clamp=".Length));
        else { Console.Error.WriteLine($"Unknown option: {a}"); return 1; }
    }

    var outPath = Path.ChangeExtension(hdrPath, ".blixprobe");
    Console.WriteLine($"Cooking probe: {hdrPath} -> {outPath}");
    Console.WriteLine($"  env={envFace} irr={irrFace} prefilter={prefilterBase}/{prefilterMips} brdf={brdfSize} clamp={clamp}");

    var sw = Stopwatch.StartNew();
    var hdr = ImageLoader.LoadRgba32F(hdrPath);
    Console.WriteLine($"  hdr loaded ({hdr.Width}x{hdr.Height}) in {sw.ElapsedMilliseconds} ms");
    sw.Restart();
    var profile = new EnvironmentProfile
    {
        Source = new HdrEnvironmentSource(hdr),
        EnvCubeFaceSize = envFace,
        IrradianceFaceSize = irrFace,
        SpecularPrefilterBaseSize = prefilterBase,
        SpecularPrefilterMipCount = prefilterMips,
        SampleClampMagnitude = clamp,
    };
    var data = EnvironmentBaker.CookHdrProbeData(profile, brdfSize);
    Console.WriteLine($"  baked in {sw.ElapsedMilliseconds} ms");
    sw.Restart();
    BlixProbeWriter.Write(outPath, data);
    var size = new FileInfo(outPath).Length;
    Console.WriteLine($"  wrote {outPath} ({size / 1024.0 / 1024.0:0.00} MB) in {sw.ElapsedMilliseconds} ms");
    return 0;
}

static int Help()
{
    PrintUsage();
    return 0;
}

static int UnknownVerb(string verb)
{
    Console.Error.WriteLine($"Unknown verb '{verb}'.");
    PrintUsage();
    return 1;
}

static int CookTextures(string[] args)
{
    if (args.Length < 2)
    {
        Console.Error.WriteLine("Usage: blix-cook textures <directory>");
        return 1;
    }
    var root = args[1];
    if (!Directory.Exists(root))
    {
        Console.Error.WriteLine($"Directory not found: {root}");
        return 1;
    }

    var sources = Directory
        .EnumerateFiles(root, "*.*", SearchOption.AllDirectories)
        .Where(p =>
        {
            var ext = Path.GetExtension(p).ToLowerInvariant();
            return ext is ".png" or ".jpg" or ".jpeg";
        })
        .ToArray();

    Console.WriteLine($"Found {sources.Length} source images under {root}");
    if (sources.Length == 0) return 0;

    var totalWatch = Stopwatch.StartNew();
    var cookedCount = 0;
    var skippedCount = 0;
    var failedCount = 0;
    var doneCount = 0;
    var sourceBytes = 0L;
    var cookedBytes = 0L;
    var failures = new System.Collections.Concurrent.ConcurrentBag<string>();

    // Live progress: tick every 250ms while the cook runs. Prints to a
    // single rewriting line via \r so the terminal stays clean. Final
    // summary lands as a separate line below.
    var stopFlag = false;
    var progressTask = Task.Run(() =>
    {
        var lastDoneCount = -1;
        while (!Volatile.Read(ref stopFlag))
        {
            var doneNow = Volatile.Read(ref doneCount);
            if (doneNow != lastDoneCount)
            {
                var elapsed = totalWatch.Elapsed.TotalSeconds;
                var rate = doneNow > 0 ? doneNow / Math.Max(elapsed, 0.001) : 0.0;
                var remaining = sources.Length - doneNow;
                var etaSeconds = rate > 0.001 ? remaining / rate : 0.0;
                var pct = doneNow * 100.0 / sources.Length;
                Console.Write(
                    $"\r  [{doneNow}/{sources.Length}] {pct,5:0.0}% " +
                    $"({rate,4:0.0}/s, {Mb(Volatile.Read(ref cookedBytes))} MB out, " +
                    $"ETA {FormatDuration(etaSeconds)})   ");
                lastDoneCount = doneNow;
            }
            Thread.Sleep(250);
        }
    });

    // Diagnostic mode: serial + per-stage timings. Switch back to
    // Parallel.ForEach once we know the cook isn't hanging on a specific
    // image / decode / encode step.
    var serial = Environment.GetEnvironmentVariable("BLIX_COOK_SERIAL") != null;
    var verbose = Environment.GetEnvironmentVariable("BLIX_COOK_VERBOSE") != null;
    Action<string> traceLine = msg =>
    {
        if (verbose) lock (sources) { Console.WriteLine($"\r{msg}"); }
    };

    void ProcessOne(string source)
    {
        var destination = Path.ChangeExtension(source, ".blixtex");
        if (File.Exists(destination))
        {
            var sourceWrite = File.GetLastWriteTimeUtc(source);
            var destWrite = File.GetLastWriteTimeUtc(destination);
            if (destWrite >= sourceWrite)
            {
                Interlocked.Increment(ref skippedCount);
                Interlocked.Increment(ref doneCount);
                return;
            }
        }
        var name = Path.GetFileName(source);
        traceLine($"  START {name}");
        var sw = Stopwatch.StartNew();
        try
        {
            CookOne(source, destination, out var srcLen, out var dstLen);
            Interlocked.Increment(ref cookedCount);
            Interlocked.Add(ref sourceBytes, srcLen);
            Interlocked.Add(ref cookedBytes, dstLen);
            traceLine($"  DONE  {name} in {sw.ElapsedMilliseconds} ms");
        }
        catch (Exception ex)
        {
            failures.Add($"  FAILED {source}: {ex.GetType().Name}: {ex.Message}");
            Interlocked.Increment(ref failedCount);
            traceLine($"  FAIL  {name}: {ex.Message}");
        }
        finally
        {
            Interlocked.Increment(ref doneCount);
        }
    }

    if (serial)
    {
        foreach (var source in sources) ProcessOne(source);
    }
    else
    {
        Parallel.ForEach(sources, ProcessOne);
    }

    Volatile.Write(ref stopFlag, true);
    progressTask.Wait();
    Console.WriteLine();  // newline after the rolling-update line

    foreach (var msg in failures) Console.Error.WriteLine(msg);

    totalWatch.Stop();
    Console.WriteLine($"Cooked {cookedCount} (skipped {skippedCount}, failed {failedCount}) in {FormatDuration(totalWatch.Elapsed.TotalSeconds)}");
    Console.WriteLine($"  source bytes: {Mb(sourceBytes)} MB");
    Console.WriteLine($"  cooked bytes: {Mb(cookedBytes)} MB");
    if (sourceBytes > 0)
    {
        var ratio = cookedBytes / (double)sourceBytes;
        Console.WriteLine($"  compression ratio: {ratio:0.00}x (cooked / source)");
    }
    return failedCount > 0 ? 2 : 0;

    static string Mb(long bytes) => (bytes / 1024.0 / 1024.0).ToString("0.0");
}

static string FormatDuration(double seconds)
{
    if (seconds < 60) return $"{seconds:0}s";
    var minutes = (int)(seconds / 60);
    var remaining = (int)(seconds - minutes * 60);
    return $"{minutes}m{remaining:00}s";
}

static void CookOne(string source, string destination, out long sourceLen, out long destLen)
{
    var verbose = Environment.GetEnvironmentVariable("BLIX_COOK_VERBOSE") != null;
    // Cook format selection. Default Rgba8 multi-mip -- fast cook, larger
    // files. Set BLIX_COOK_FORMAT=bc7 to BC7-encode for ~4x smaller GPU
    // memory + disk; the BCnEncoder.Net encoder is slow (multiple minutes
    // per 4K texture), so this is opt-in until a faster encoder lands.
    var bcMode = string.Equals(
        Environment.GetEnvironmentVariable("BLIX_COOK_FORMAT"), "bc7",
        StringComparison.OrdinalIgnoreCase);
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
        var image = ImageLoader.LoadRgba32(stream);
        if (verbose) Console.WriteLine($"\r    decoded {name} {image.Width}x{image.Height} in {decodeSw.ElapsedMilliseconds} ms");
        var role = ClassifyRole(source);
        var (bcFormat, flags) = PickFormat(role);
        var format = bcMode ? bcFormat : TextureFormat.Rgba8;

        var mipSw = Stopwatch.StartNew();
        var mipsRgba = GenerateMipsBoxFilter(image.Pixels, image.Width, image.Height, minDim: 4);
        if (verbose) Console.WriteLine($"\r    mipped  {name} {mipsRgba.Count} levels in {mipSw.ElapsedMilliseconds} ms");

        byte[][] encodedMips;
        if (bcMode)
        {
            // Compress each mip with the chosen BCn variant. Encoder-internal
            // parallelism is OFF -- the outer Parallel.ForEach handles cores.
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

        BlixTexWriter.Write(destination, new BlixTexImage(
            image.Width, image.Height, format, encodedMips, flags));
    }
    destLen = new FileInfo(destination).Length;
}

// Picks a BCn format + flags based on the heuristic role classification.
static (TextureFormat Format, BlixTex.Flags Flags) PickFormat(TextureRole role) => role switch
{
    TextureRole.BaseColor => (TextureFormat.Bc7Srgb, BlixTex.Flags.Srgb),
    TextureRole.Emissive  => (TextureFormat.Bc7Srgb, BlixTex.Flags.Srgb),
    TextureRole.Normal    => (TextureFormat.Bc7Unorm, BlixTex.Flags.NormalMap),
    TextureRole.Linear    => (TextureFormat.Bc7Unorm, BlixTex.Flags.None),
    _                     => (TextureFormat.Bc7Unorm, BlixTex.Flags.None),
};

// BC5 is the textbook normal-map format (two-channel RG, reconstruct Z in
// shader) but it requires shader changes that haven't landed yet. Until
// then we use BC7 Unorm for normals: same 8 bpp + four-channel storage,
// no shader change needed.
static CompressionFormat ToBcFormat(TextureFormat fmt) => fmt switch
{
    TextureFormat.Bc7Srgb  => CompressionFormat.Bc7,  // sRGB selection lives on the GL internal format side
    TextureFormat.Bc7Unorm => CompressionFormat.Bc7,
    TextureFormat.Bc5Unorm => CompressionFormat.Bc5,
    _ => throw new NotSupportedException($"Unsupported BC format target: {fmt}"),
};

static byte[] EncodeMip(BcEncoder encoder, byte[] rgbaPixels, int width, int height)
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
static List<(byte[] Pixels, int Width, int Height)> GenerateMipsBoxFilter(
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

static TextureRole ClassifyRole(string path)
{
    var name = Path.GetFileNameWithoutExtension(path).ToLowerInvariant();
    if (name.Contains("basecolor") || name.Contains("albedo") || name.Contains("diffuse"))
        return TextureRole.BaseColor;
    if (name.Contains("emiss"))
        return TextureRole.Emissive;
    if (name.Contains("normal") || name.EndsWith("_n") || name.EndsWith(".n"))
        return TextureRole.Normal;
    return TextureRole.Linear;
}

enum TextureRole { BaseColor, Normal, Emissive, Linear }

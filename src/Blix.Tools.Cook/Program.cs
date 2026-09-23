using BCnEncoder.Encoder;
using BCnEncoder.Shared;
using Blix.Assets;
using Blix.Cooked;
using Blix.Graphics.Images;
using Blix.Graphics;
using Microsoft.Toolkit.HighPerformance;
using System.Diagnostics;
using System.Numerics;

// Asset recipe host plus policy-bearing tree and packaging drivers. Invoke through `blix cook`.

using Blix.Core;

namespace Blix.Tools.Cook;

/// <summary>Discovers recipes, runs declared transformations, and hosts asset-specific drivers.</summary>
/// <remarks>
/// <c>list</c>, <c>run</c>, <c>status</c>, and <c>batch</c> are format-neutral host operations.
/// Mesh, texture, probe, asset-tree, and sky verbs carry traversal, packaging, or scene policy that
/// deliberately does not belong in a one-file recipe.
/// </remarks>
public static class Program
{
    [BlixApp("cook", Summary = "run Blix's cooking recipes")]
    public static int Main(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

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
            "asset" => CookAsset(args),
            "sky" => CookSky(args),
            "list" => ListRecipes(args),
            "run" => RunRecipe(args),
            "status" => Status(args),
            "batch" => Batch(args),
            "help" or "-h" or "--help" => Help(args),
            _ => UnknownVerb(args[0]),
        };
    }

    // blix cook sky <dir-of-blixmesh> [--occupancy N] [--probes N] [--rays N] [--albedo N]
    //
    // Bakes how much sky each point in a scene can see. Always prints the profile that makes the
    // result inspectable; writes a volume when --out names one.
    /// <summary>Bakes a sky-visibility volume from a cooked mesh tree.</summary>
    /// <remarks>
    /// This is a scene driver rather than a discoverable one-file recipe: it consumes a collection
    /// of cooked meshes, applies sampling policy, and may write one <c>.blixsky</c> volume.
    /// </remarks>
    static int CookSky(string[] args)
    {
        if (args.Length < 2) { Console.Error.WriteLine("Usage: blix cook sky <dir>"); return 2; }
        var root = args[1];
        var meshes = Directory.Exists(root)
            ? Directory.GetFiles(root, "*.blixmesh", SearchOption.AllDirectories)
            : new[] { root };
        if (meshes.Length == 0) { Console.Error.WriteLine($"No .blixmesh under {root}."); return 2; }

        Console.WriteLine($"Baking sky visibility from {meshes.Length} cooked mesh(es)");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var vol = Blix.Recipes.SkyVisibilityBaker.Bake(
            meshes,
            occupancy: IntFlag(args, "--occupancy", 256),
            probes: IntFlag(args, "--probes", 48),
            rays: IntFlag(args, "--rays", 64),
            albedo: IntFlag(args, "--albedo", 0),
            log: Console.WriteLine);
        Console.WriteLine($"  baked in {sw.Elapsed.TotalSeconds:0.0}s");

        var b = vol.Bounds;
        Console.WriteLine($"  bounds {b.Min} .. {b.Max}");

        var outPath = ValueOf(args, "--out");
        if (outPath is not null)
        {
            var coeffs = new float[vol.SizeX * vol.SizeY * vol.SizeZ * Blix.Graphics.Images.BlixSkyVolume.FloatsPerCell];
            for (var z = 0; z < vol.SizeZ; z++)
            for (var y = 0; y < vol.SizeY; y++)
            for (var x = 0; x < vol.SizeX; x++)
            {
                var c = vol.At(x, y, z);
                var o = ((z * vol.SizeY + y) * vol.SizeX + x) * Blix.Graphics.Images.BlixSkyVolume.FloatsPerCell;
                coeffs[o] = c.L0; coeffs[o + 1] = c.L1.X; coeffs[o + 2] = c.L1.Y; coeffs[o + 3] = c.L1.Z;
                coeffs[o + 4] = c.L2m2; coeffs[o + 5] = c.L2m1; coeffs[o + 6] = c.L20; coeffs[o + 7] = c.L2p1;
                coeffs[o + 8] = c.L2p2;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);
            Blix.Graphics.Images.BlixSkyVolume.Write(outPath, new Blix.Graphics.Images.BlixSkyVolume(
                b.Min, b.Max, vol.SizeX, vol.SizeY, vol.SizeZ, coeffs,
                vol.OccupancyX, vol.OccupancyY, vol.OccupancyZ, vol.Occupancy,
                vol.AlbedoX, vol.AlbedoY, vol.AlbedoZ, vol.Albedo));
            Console.WriteLine($"  wrote {outPath} ({new FileInfo(outPath).Length / 1024.0:0.0} KB)");
        }

        // The profile that says whether this is physics: visibility should rise monotonically with
        // height, be near 1 above the roofline, and be markedly lower inside than out.
        Console.WriteLine("  sky visibility by height, for an UP-facing surface (mean over the layer):");
        for (var y = 0; y < vol.SizeY; y++)
        {
            double sum = 0;
            for (var z = 0; z < vol.SizeZ; z++)
            for (var x = 0; x < vol.SizeX; x++)
                sum += vol.At(x, y, z).Visibility(System.Numerics.Vector3.UnitY);
            var world = b.Min.Y + (y + 0.5f) * (b.Max.Y - b.Min.Y) / vol.SizeY;
            if (y % Math.Max(1, vol.SizeY / 12) == 0 || y == vol.SizeY - 1)
                Console.WriteLine($"    y={world,7:0.0} m  visibility {sum / (vol.SizeX * vol.SizeZ):0.000}");
        }
        return 0;
    }

    static int IntFlag(string[] a, string name, int fallback)
    {
        var i = Array.FindIndex(a, x => x.Equals(name, StringComparison.OrdinalIgnoreCase));
        return i >= 0 && i + 1 < a.Length && int.TryParse(a[i + 1], out var v) ? v : fallback;
    }

    /// <summary>
    /// Cooks one asset and exactly the images it references, into a tree of its own.
    /// </summary>
    /// <remarks>
    /// Follows material image references rather than sweeping the source directory. Textures are
    /// cooked before the mesh so its image table records the destination artifacts. The driver
    /// keeps one output per referenced image; projects that want another grouping policy own a
    /// different driver and table layout.
    /// </remarks>
    static int CookAsset(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("Usage: blix cook asset <gltf-or-glb> --out <dir> [mesh flags]");
            return 2;
        }

        var source = args[1];
        if (!File.Exists(source))
        {
            Console.Error.WriteLine($"No file at {source}.");
            return 2;
        }

        var outDir = ValueOf(args, "--out");
        if (outDir is null)
        {
            Console.Error.WriteLine(
                "blix cook asset needs --out <dir>: it writes a tree you can ship, which is not the "
                + "tree you authored in.");
            return 2;
        }

        var sourceDir = Path.GetDirectoryName(Path.GetFullPath(source)) ?? ".";
        var outputRoot = Path.GetFullPath(outDir);
        Directory.CreateDirectory(outputRoot);

        IReadOnlyList<Blix.Recipes.MeshRecipe.ReferencedImage> references;
        try
        {
            references = Blix.Recipes.MeshRecipe.ReferencedImages(source);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException)
        {
            Console.Error.WriteLine($"  cannot read asset references: {ex.Message}");
            return 1;
        }
        var byUri = references
            .GroupBy(reference => reference.Uri, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        Console.WriteLine($"  {byUri.Length} referenced image(s)");

        var invalid = false;
        foreach (var group in byUri)
        {
            var roles = group.Select(reference => reference.Role).Distinct().ToArray();
            if (roles.Length > 1)
            {
                Console.Error.WriteLine(
                    $"  ambiguous: {group.Key} is used as {string.Join(" and ", roles)}; one "
                    + ".blixtex cannot preserve both material-channel roles");
                invalid = true;
            }

            var sourcePath = Path.GetFullPath(Path.Combine(sourceDir, group.Key));
            var outputPath = Path.GetFullPath(
                Path.Combine(outputRoot, Path.ChangeExtension(group.Key, ".blixtex")));
            if (!IsWithin(sourceDir, sourcePath) || !IsWithin(outputRoot, outputPath))
            {
                Console.Error.WriteLine(
                    $"  outside tree: {group.Key} does not remain inside both source and output roots");
                invalid = true;
            }
            else if (!File.Exists(sourcePath))
            {
                Console.Error.WriteLine($"  missing: {group.Key}");
                invalid = true;
            }
        }

        foreach (var collision in byUri.GroupBy(
                     group => Path.ChangeExtension(group.Key, ".blixtex"),
                     StringComparer.OrdinalIgnoreCase).Where(group => group.Count() > 1))
        {
            Console.Error.WriteLine(
                $"  output collision: {string.Join(", ", collision.Select(group => group.Key))} "
                + $"all map to {collision.Key}");
            invalid = true;
        }

        if (invalid) return 1;

        long sourceBytes = 0, cookedBytes = 0;
        var sw = Stopwatch.StartNew();
        try
        {
            Parallel.ForEach(byUri, group =>
            {
                var reference = group.Single();
                var from = Path.Combine(sourceDir, reference.Uri);
                var to = Path.Combine(outputRoot, Path.ChangeExtension(reference.Uri, ".blixtex"));
                Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                Blix.Recipes.TextureRecipe.CookOne(
                    from, to, out var inLen, out var outLen, reference.Role);
                Interlocked.Add(ref sourceBytes, inLen);
                Interlocked.Add(ref cookedBytes, outLen);
            });
        }
        catch (AggregateException ex)
        {
            foreach (var failure in ex.Flatten().InnerExceptions)
                Console.Error.WriteLine($"  texture cook failed: {failure.Message}");
            return 1;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or NotSupportedException)
        {
            Console.Error.WriteLine($"  texture cook failed: {ex.Message}");
            return 1;
        }

        Console.WriteLine(
            $"  textures: {sourceBytes / 1048576.0:F1} MB -> {cookedBytes / 1048576.0:F1} MB in {sw.Elapsed.TotalSeconds:F0}s");

        var meshOut = Path.Combine(outputRoot, Path.GetFileNameWithoutExtension(source) + ".blixmesh");
        // Use the shared shipped-mesh path so asset trees and direct mesh cooks receive the same LOD
        // and splitting policy.
        var splitBudget = 0;
        var splitIdx = Array.FindIndex(args, x => x.Equals("--split", StringComparison.OrdinalIgnoreCase));
        if (splitIdx >= 0 && splitIdx + 1 < args.Length && int.TryParse(args[splitIdx + 1], out var sb))
            splitBudget = sb;
        // Material patches are explicit inputs to both mesh-producing drivers.
        Blix.Recipes.MaterialPatch? assetPatch = null;
        var apIdx = Array.FindIndex(args, x => x.Equals("--patch", StringComparison.OrdinalIgnoreCase));
        if (apIdx >= 0)
        {
            if (apIdx + 1 >= args.Length)
            {
                Console.Error.WriteLine("--patch needs a file path.");
                return 1;
            }
            try
            {
                assetPatch = Blix.Recipes.MaterialPatch.Load(args[apIdx + 1]);
            }
            catch (Exception ex) when (ex is InvalidDataException or FileNotFoundException)
            {
                Console.Error.WriteLine($"patch: {ex.Message}");
                return 1;
            }
            Console.WriteLine($"  patch {assetPatch.FileName}@{assetPatch.ContentHash}: {assetPatch.Rules.Count} rule(s)");
        }

        int count;
        try
        {
            count = Blix.Recipes.MeshRecipe.CookShipped(
                source, meshOut,
                flipTextureV: HasFlag(args, "--flip-v"),
                includeTangents: HasFlag(args, "--tangents"),
                splitTriBudget: splitBudget,
                splitFoliage: !HasFlag(args, "--no-split-foliage"),
                patch: assetPatch,
                log: Console.WriteLine);
        }
        catch (InvalidDataException ex)
        {
            Console.Error.WriteLine($"cook refused: {ex.Message}");
            return 1;
        }

        var header = Blix.Cooked.CookedFile.TryReadHeader(meshOut);
        Console.WriteLine($"  mesh: {count} primitive(s) -> {meshOut}");
        if (header?.Stamp.Flags != Blix.Cooked.CookedFlags.None)
        {
            Console.Error.WriteLine(
                header is null
                    ? "  packaging failed: the mesh has no readable cooked header."
                    : $"  packaging failed: the mesh still declares {header.Value.Stamp.Flags}; "
                      + "run `blix check --cooked` on the output to inspect the fallback.");
            return 1;
        }

        Console.WriteLine("  this tree stands alone — no source file is needed to load it.");

        return 0;
    }

    private static bool IsWithin(string root, string path)
    {
        var relative = Path.GetRelativePath(Path.GetFullPath(root), Path.GetFullPath(path));
        return relative != ".."
            && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            && !Path.IsPathRooted(relative);
    }

    /// <summary>Every output path claimed by more than one line, described for a person.</summary>
    /// <remarks>
    /// A pure function over the batch lines so it can be tested without a build, a recipe or a
    /// disk: the thing worth asserting is that two claims on one path are caught, not that the cook
    /// can read a file.
    /// </remarks>
    public static IEnumerable<string> FindOutputCollisions(IEnumerable<string> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);

        var claims = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var line in lines)
        {
            if (line.Length == 0) continue;
            var parts = line.Split('\t');
            if (parts.Length < 3) continue;

            var describedAs = parts.Length > 3 && parts[3].Length > 0
                ? $"{parts[1]} ({parts[0]}, {parts[3]})"
                : $"{parts[1]} ({parts[0]})";

            if (!claims.TryGetValue(parts[2], out var claimants))
            {
                claims[parts[2]] = claimants = new List<string>();
            }

            claimants.Add(describedAs);
        }

        foreach (var (output, claimants) in claims)
        {
            if (claimants.Count < 2) continue;
            yield return
                $"{claimants.Count} declarations write '{output}':" + Environment.NewLine
                + string.Join(Environment.NewLine, claimants.Select(c => "         " + c))
                + Environment.NewLine
                + "       A cooked path is derived from the source's name, so these overwrite each "
                + "other and the last one wins. Cook them to different sources, or declare one.";
        }
    }

    static string? ValueOf(string[] args, string name)
    {
        var i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    static bool HasFlag(string[] args, string name) => Array.IndexOf(args, name) >= 0;

    static void PrintUsage()
{
    Console.WriteLine("blix cook — run Blix's cooking recipes.");
    Console.WriteLine();
    Console.WriteLine("  The host — knows nothing about any particular format:");
    Console.WriteLine("    list                     what recipes this build declares");
    Console.WriteLine("    status [<dir>]           what is cooked under <dir>, what is stale, what is not");
    Console.WriteLine("    run <id> <src> [<out>] [-Dkey=value ...]");
    Console.WriteLine("                             run one recipe on one file");
    Console.WriteLine();
    Console.WriteLine("  The drivers — carry tree, packaging, progress or scene policy");
    Console.WriteLine("  that does not belong in the uniform one-file recipe host:");
    Console.WriteLine("    textures <dir>           cook every .png/.jpg under <dir> to .blixtex");
    Console.WriteLine("    probe <hdr> [--env-face=256] [--irr-face=32] [--prefilter-base=128]");
    Console.WriteLine("                [--prefilter-mips=5] [--brdf-size=256] [--clamp=50]");
    Console.WriteLine("                [--yaw=<degrees>]");
    Console.WriteLine("                             bake an HDR sky to a .blixprobe");
    Console.WriteLine("    mesh <path> [--flip-v] [--tangents] [--split N] [--no-split-foliage]");
    Console.WriteLine("                             cook a .gltf/.glb (or a tree of them) to .blixmesh");
    Console.WriteLine("    asset <gltf-or-glb> --out <dir> [mesh flags]");
    Console.WriteLine("                             cook one model and its referenced images into an output tree");
    Console.WriteLine("    sky <dir-or-blixmesh> [--out <file>] [--occupancy N] [--probes N]");
    Console.WriteLine("                            [--rays N] [--albedo N]");
    Console.WriteLine("                             bake scene sky visibility from cooked meshes");
    Console.WriteLine();
    Console.WriteLine("    mesh/textures/probe --out <dir>");
    Console.WriteLine("                             write cooked output into a separate tree, mirroring");
    Console.WriteLine("                             each source's path. mesh writes only .blixmesh;");
    Console.WriteLine("                             uncooked referenced images remain a packaging concern.");
    Console.WriteLine();
    Console.WriteLine("  Related tools:");
    Console.WriteLine("    blix inspect <file>      list what is in an asset — source OR cooked");
    Console.WriteLine("    blix run Blix.Test.Recipes");
    Console.WriteLine("                             prove the native simplifier and BC7 encoder load");
}

    static int CookMesh(string[] args)
{
    var (outDir, a) = ExtractOutDir(args);
    if (a.Length < 2)
    {
        Console.Error.WriteLine("Usage: blix cook mesh <gltf-or-directory> [--out <dir>] [--flip-v] [--tangents] [--split N] [--patch <file>]");
        return 1;
    }
    var target = a[1];
    // Opt-in V canonicalisation for bottom-up (OpenGL-authored) sources, baked
    // into the cooked vertex data. Granular per invocation — mirrors
    // AssetImportContext.FlipTextureV on the runtime-import path.
    var flipV = a.Any(x => x.Equals("--flip-v", StringComparison.OrdinalIgnoreCase));
    // Cook the tangent-bearing layout required by normal-mapped consumers.
    var tangents = a.Any(x => x.Equals("--tangents", StringComparison.OrdinalIgnoreCase));
    // Spatial split: primitives over this triangle budget are recursively
    // partitioned into chunks (each its own LOD chain) so per-prim distance LOD
    // gets fine-grained. 0/absent = off. --no-split-foliage leaves non-OPAQUE
    // (masked/blended) prims whole for the impostor track to own.
    var splitBudget = 0;
    var splitIdx = Array.FindIndex(a, x => x.Equals("--split", StringComparison.OrdinalIgnoreCase));
    if (splitIdx >= 0 && splitIdx + 1 < a.Length && int.TryParse(a[splitIdx + 1], out var sb))
        splitBudget = sb;
    // Patches are explicit command inputs rather than implicitly discovered neighbours.
    var patchIdx = Array.FindIndex(a, x => x.Equals("--patch", StringComparison.OrdinalIgnoreCase));
    Blix.Recipes.MaterialPatch? patch = null;
    if (patchIdx >= 0)
    {
        if (patchIdx + 1 >= a.Length)
        {
            Console.Error.WriteLine("--patch needs a file path.");
            return 1;
        }
        try
        {
            patch = Blix.Recipes.MaterialPatch.Load(a[patchIdx + 1]);
        }
        catch (Exception ex) when (ex is InvalidDataException or FileNotFoundException)
        {
            // A refused patch is the mechanism working, so it reports like a diagnosis and not like
            // a crash: one line naming the file, the line and what it could not find.
            Console.Error.WriteLine($"patch: {ex.Message}");
            return 1;
        }
        Console.WriteLine($"  patch {patch.FileName}@{patch.ContentHash}: {patch.Rules.Count} rule(s)");
    }
    var splitFoliage = !a.Any(x => x.Equals("--no-split-foliage", StringComparison.OrdinalIgnoreCase));

    string[] sources;
    string inRoot;
    if (Directory.Exists(target))
    {
        inRoot = Path.GetFullPath(target);
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
        inRoot = Path.GetDirectoryName(Path.GetFullPath(target)) ?? string.Empty;
        sources = new[] { target };
    }
    else
    {
        Console.Error.WriteLine($"Path not found: {target}");
        return 1;
    }
    if (outDir is not null) Console.WriteLine($"  writing cooked .blixmesh into {outDir}");

    if (sources.Length == 0) return 0;

    foreach (var src in sources)
    {
        var outPath = ResolveDest(src, inRoot, outDir, ".blixmesh");
        if (outDir is not null)
        {
            // Do not destructively clean a tree that may contain deliberately retained sources,
            // but make the migration visible when an older cook left its automatic copy behind.
            var legacySource = ResolveDest(src, inRoot, outDir, Path.GetExtension(src));
            if (!string.Equals(Path.GetFullPath(legacySource), Path.GetFullPath(src), StringComparison.Ordinal)
                && File.Exists(legacySource))
            {
                Console.WriteLine(
                    $"  note: legacy source copy remains at {legacySource}; mesh no longer writes " +
                    "or removes authored sources");
            }
        }
        if (File.Exists(outPath))
        {
            if (Blix.Recipes.MeshRecipe.IsShippedCurrent(
                    src, outPath, flipV, tangents,
                    splitTriBudget: splitBudget, splitFoliage: splitFoliage, patch: patch))
            {
                Console.WriteLine($"  up-to-date: {outPath}{SourceImageDebtNote(outPath)}");
                continue;
            }

            Console.WriteLine($"  re-cooking (source, recipe, or options changed): {outPath}");
        }
        var sw = Stopwatch.StartNew();
        // The recipe owns simplification policy; the driver only selects whether splitting creates
        // borders that must remain locked.
        int count;
        try
        {
            count = Blix.Recipes.MeshRecipe.CookShipped(src, outPath, flipV, tangents,
                splitTriBudget: splitBudget, splitFoliage: splitFoliage,
                patch: patch, log: Console.WriteLine);
        }
        catch (InvalidDataException ex)
        {
            // Recipe and patch validation are content refusals, not tool crashes.
            Console.Error.WriteLine($"cook refused: {ex.Message}");
            return 1;
        }
        var size = new FileInfo(outPath).Length;
        // Quick LOD readout: levels + triangle reduction on the largest primitive.
        var file = Blix.Assets.BlixMeshReader.Read(outPath);
        var biggest = file.Primitives.OrderByDescending(p => p.Lods[0].IndexCount).First();
        var lodCounts = string.Join("/", biggest.Lods.Select(l => l.IndexCount / 3));
        var splitNote = splitBudget > 0
            ? $", split@{splitBudget / 1000}k → biggest chunk {biggest.Lods[0].IndexCount / 3} tris"
            : "";
        var imageDebt = SourceImageDebtNote(outPath);
        Console.WriteLine($"  cooked {Path.GetFileName(src)} -> {Path.GetFileName(outPath)} ({count} prims, {size / 1024.0 / 1024.0:0.00} MB, {tangents}-tan) in {sw.ElapsedMilliseconds} ms; LOD tris (biggest prim): {lodCounts}{splitNote}{imageDebt}");
    }
    return 0;
}

    static int CookProbe(string[] args)
{
    var (outDir, args2) = ExtractOutDir(args);
    if (args2.Length < 2)
    {
        Console.Error.WriteLine("Usage: blix cook probe <hdr-path> [--out <dir>] [options]");
        return 1;
    }
    var hdrPath = args2[1];
    if (!File.Exists(hdrPath))
    {
        Console.Error.WriteLine($"HDR not found: {hdrPath}");
        return 1;
    }

    int envFace = 256, irrFace = 32, prefilterBase = 128, prefilterMips = 5, brdfSize = 256;
    float clamp = 50.0f;
    // Rotate before sun measurement and every convolution so all probe products share orientation.
    // Only yaw preserves the photographed horizon.
    float yawDegrees = 0f;
    foreach (var a in args2.Skip(2))
    {
        if (a.StartsWith("--env-face=")) envFace = int.Parse(a.AsSpan("--env-face=".Length));
        else if (a.StartsWith("--irr-face=")) irrFace = int.Parse(a.AsSpan("--irr-face=".Length));
        else if (a.StartsWith("--prefilter-base=")) prefilterBase = int.Parse(a.AsSpan("--prefilter-base=".Length));
        else if (a.StartsWith("--prefilter-mips=")) prefilterMips = int.Parse(a.AsSpan("--prefilter-mips=".Length));
        else if (a.StartsWith("--brdf-size=")) brdfSize = int.Parse(a.AsSpan("--brdf-size=".Length));
        else if (a.StartsWith("--clamp=")) clamp = float.Parse(a.AsSpan("--clamp=".Length));
        else if (a.StartsWith("--yaw=")) yawDegrees = float.Parse(a.AsSpan("--yaw=".Length));
        else { Console.Error.WriteLine($"Unknown option: {a}"); return 1; }
    }

    string outPath;
    if (string.IsNullOrEmpty(outDir))
    {
        outPath = Path.ChangeExtension(hdrPath, ".blixprobe");
    }
    else
    {
        // Single-file input (no input root to mirror against): preserve the
        // HDR's immediate parent dir under outDir — e.g. <src>/textures/x.hdr ->
        // <out>/textures/x.blixprobe — so it lands where the runtime looks
        // (<assetsRoot>/textures/).
        var parent = Path.GetFileName(Path.GetDirectoryName(Path.GetFullPath(hdrPath)) ?? string.Empty);
        var destDir = string.IsNullOrEmpty(parent) ? outDir : Path.Combine(outDir, parent);
        Directory.CreateDirectory(destDir);
        outPath = Path.Combine(destDir, Path.GetFileNameWithoutExtension(hdrPath) + ".blixprobe");
    }
    Console.WriteLine($"Cooking probe: {hdrPath} -> {outPath}");
    Console.WriteLine($"  env={envFace} irr={irrFace} prefilter={prefilterBase}/{prefilterMips} brdf={brdfSize} clamp={clamp} yaw={yawDegrees}");

    var sw = Stopwatch.StartNew();
    var size = Blix.Recipes.ProbeRecipe.CookOne(
        hdrPath, outPath, envFace, irrFace, prefilterBase, prefilterMips, brdfSize, clamp, yawDegrees);
    Console.WriteLine($"  wrote {outPath} ({size / 1024.0 / 1024.0:0.00} MB) in {sw.ElapsedMilliseconds} ms");
    return 0;
}

    static int Help(string[] args)
{
    if (args.Length != 1)
    {
        Console.Error.WriteLine("Usage: blix cook help");
        return 2;
    }

    PrintUsage();
    return 0;
}

    static int UnknownVerb(string verb)
{
    Console.Error.WriteLine($"Unknown verb '{verb}'.");
    PrintUsage();
    return 1;
}


// --- cook as a host -------------------------------------------------------
//
// The verbs above are DRIVERS: each carries tree, packaging, progress or scene policy that does
// not belong in a one-file recipe. The host verbs below operate on the discovered recipe contract.

    static Blix.Cooked.FoundRecipe[] Recipes() => RecipeCatalog.All();

    static int ListRecipes(string[] args)
{
    if (args.Length != 1)
    {
        Console.Error.WriteLine("Usage: blix cook list");
        return 2;
    }

    foreach (var r in Recipes())
    {
        Console.WriteLine($"  {r.Id}  {string.Join(" ", r.Consumes),-18} -> {r.Produces,-11} v{r.Version}  {r.Summary}");
    }

    return 0;
}

// blix cook run <id> <source> [<output>] [-Dkey=value ...]
//
// The uniform one-file path. Asset-specific traversal and packaging stay in the drivers.
    static int RunRecipe(string[] args)
{
    if (args.Length < 3)
    {
        Console.Error.WriteLine("Usage: blix cook run <recipe-id> <source> [<output>] [-Dkey=value ...]");
        return 2;
    }

    var recipe = Recipes().FirstOrDefault(r => r.Id == args[1]);
    if (recipe is null)
    {
        Console.Error.WriteLine("blix cook run: " + RecipeCatalog.UnknownRecipe(args[1]));
        return 2;
    }

    var source = args[2];
    if (!File.Exists(source))
    {
        Console.Error.WriteLine($"No file at {source}.");
        return 1;
    }

    var options = new Dictionary<string, string>(StringComparer.Ordinal);
    string? output = null;
    var sawOption = false;
    foreach (var argument in args.Skip(3))
    {
        if (!argument.StartsWith("-D", StringComparison.Ordinal))
        {
            if (argument.StartsWith("-", StringComparison.Ordinal))
            {
                Console.Error.WriteLine($"blix cook run: unknown option '{argument}'.");
                return 2;
            }
            if (output is not null || sawOption)
            {
                Console.Error.WriteLine($"blix cook run: unexpected argument '{argument}'.");
                return 2;
            }

            output = argument;
            continue;
        }

        sawOption = true;
        var kv = argument[2..].Split('=', 2);
        if (kv[0].Length == 0)
        {
            Console.Error.WriteLine("blix cook run: -D needs a non-empty option name.");
            return 2;
        }
        options[kv[0]] = kv.Length > 1 ? kv[1] : "1";
    }
    output ??= recipe.OutputFor(source);

    try
    {
        var outcome = recipe.Cook(new Blix.Cooked.CookRequest(source, output, options));
        var verb = outcome.Wrote ? "cooked" : "skipped";
        Console.WriteLine($"  {verb} {recipe.Id}: {source} -> {output} ({outcome.Detail})");
        return 0;
    }
    catch (Blix.Cooked.AssetImportException refused)
    {
        Console.Error.WriteLine($"blix cannot read this: {refused.Message}");
        return 1;
    }
}

// blix cook status <dir>
//
// Reports recipe coverage and provenance from common preambles without loading format bodies.
    static int Status(string[] args)
{
    if (args.Length > 2)
    {
        Console.Error.WriteLine("Usage: blix cook status [<dir>]");
        return 2;
    }

    var root = args.Length > 1 ? args[1] : ".";
    if (!Directory.Exists(root))
    {
        Console.Error.WriteLine($"No directory at {root}.");
        return 2;
    }

    return ReportStatus(root, Recipes());
}

    /// <summary>Reports recipe coverage for one tree using an explicit recipe set.</summary>
    /// <remarks>
    /// Kept separate from discovery so claim resolution and accounting can be checked without
    /// loading project assemblies. A source with multiple claimants is one ambiguous source, not
    /// zero sources and not several independently cookable outputs.
    /// </remarks>
    internal static int ReportStatus(string root, IReadOnlyList<Blix.Cooked.FoundRecipe> recipes)
{
    int cooked = 0, missing = 0, unreadable = 0, ambiguous = 0;
    int stale = 0, unknown = 0, outdated = 0, pinned = 0;

    foreach (var source in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).OrderBy(x => x, StringComparer.Ordinal))
    {
        if (source.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
            source.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)) continue;

        var matches = recipes.Where(r => r.Accepts(source)).ToArray();
        if (matches.Length == 0) continue;
        var rel = Path.GetRelativePath(root, source);
        if (matches.Length > 1)
        {
            ambiguous++;
            Console.WriteLine(
                $"  AMBIGUOUS  {rel}   ({string.Join(", ", matches.Select(r => r.Id))} all claim {Path.GetExtension(source)})");
            continue;
        }
        var recipe = matches[0];

        var output = recipe.OutputFor(source);

        if (!File.Exists(output))
        {
            missing++;
            Console.WriteLine($"  UNCOOKED   {rel}   ({recipe.Id} would make {Path.GetExtension(output)})");
            continue;
        }

        if (Blix.Cooked.CookedFile.TryReadHeader(output) is not { } h)
        {
            unreadable++;
            Console.WriteLine($"  UNREADABLE {rel}   (its {Path.GetExtension(output)} has no Blix preamble)");
            continue;
        }

        cooked++;
        if (h.Stamp.SourceRequired) pinned++;

        // Source freshness and recipe version are independent dimensions.
        var freshness = Blix.Cooked.CookedFile.Compare(h, source);
        if (freshness == Blix.Cooked.CookedFile.Freshness.Stale)
        {
            stale++;
            Console.WriteLine($"  STALE      {rel}   (source changed since it was cooked)");
        }
        else if (freshness == Blix.Cooked.CookedFile.Freshness.Unknown)
        {
            unknown++;
            Console.WriteLine($"  UNKNOWN    {rel}   (cooked, but recorded nothing to compare against)");
        }

        if (h.Stamp.RecipeVersion != recipe.Version)
        {
            outdated++;
            Console.WriteLine($"  OLD COOK   {rel}   (made by {h.Stamp.Recipe} v{h.Stamp.RecipeVersion}, current is v{recipe.Version})");
        }
    }

    var total = cooked + missing + unreadable + ambiguous;
    Console.WriteLine();
    Console.WriteLine(total == 0
        ? $"  nothing under {root} is claimed by a recipe."
        : $"  {cooked}/{total} cooked ({100.0 * cooked / total:0}%) -- {missing} uncooked, {stale} stale, " +
          $"{unreadable} unreadable, {ambiguous} ambiguous, {outdated} by an older recipe, {unknown} unknown");
    if (pinned > 0)
    {
        Console.WriteLine($"  {pinned} declare their source is still REQUIRED at load (an optimisation, not a replacement).");
    }

    // Coverage is a report: incomplete or stale trees still exit successfully. Use the build or
    // `blix check --cooked` when a policy needs an enforcing exit code.
    return 0;
}


// blix cook batch <list-file>
//
// One process for an MSBuild-filtered list, avoiding one managed-process startup per asset.
//
// Each line is TAB-separated: recipeId, source, output, options (k=v, space separated).
    static int Batch(string[] args)
{
    if (args.Length != 2)
    {
        Console.Error.WriteLine("Usage: blix cook batch <list-file>");
        return 2;
    }

    if (!File.Exists(args[1]))
    {
        Console.Error.WriteLine($"No list file at {args[1]}.");
        return 2;
    }

    var lines = File.ReadAllLines(args[1]);

    // Two declarations may not claim one output. Settings do not implicitly disambiguate paths.
    foreach (var collision in FindOutputCollisions(lines))
    {
        Console.Error.WriteLine($"blix cook batch: {collision}");
        return 1;
    }

    var recipes = Recipes();
    int cooked = 0, skipped = 0;

    foreach (var line in lines)
    {
        if (line.Length == 0) continue;
        var parts = line.Split('\t');
        if (parts.Length is < 3 or > 4 || parts[0].Length == 0 || parts[1].Length == 0 || parts[2].Length == 0)
        {
            Console.Error.WriteLine($"blix cook batch: malformed line '{line}'");
            return 1;
        }

        var (id, source, output) = (parts[0], parts[1], parts[2]);
        var recipe = recipes.FirstOrDefault(r => r.Id == id);
        if (recipe is null)
        {
            Console.Error.WriteLine("blix cook batch: " + RecipeCatalog.UnknownRecipe(id));
            return 1;
        }

        // MSBuild already selected this list as out of date; batch must not apply a competing skip
        // policy that can override the build decision.
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        if (parts.Length > 3)
        {
            foreach (var kv in parts[3].Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                var eq = kv.Split('=', 2);
                options[eq[0]] = eq.Length > 1 ? eq[1] : "1";
            }
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
            var outcome = recipe.Cook(new Blix.Cooked.CookRequest(source, output, options));
            if (outcome.Wrote)
            {
                cooked++;
                Console.WriteLine($"  cooked {recipe.Id} {Path.GetFileName(source)} -> {Path.GetFileName(output)} ({outcome.Detail})");
            }
            else
            {
                skipped++;
                Console.WriteLine($"  skipped {recipe.Id} {Path.GetFileName(source)} -> {Path.GetFileName(output)} ({outcome.Detail})");
            }
        }
        catch (Blix.Cooked.AssetImportException refused)
        {
            // A source the engine refuses FAILS the build, unlike a status report. A build that
            // quietly shipped without an asset it was told to cook is the thing this whole rule
            // exists to prevent.
            Console.Error.WriteLine($"blix cook: cannot read {source} — {refused.Message}");
            return 1;
        }
    }

    if (cooked > 0 || skipped > 0) Console.WriteLine($"  {cooked} cooked, {skipped} skipped");
    return 0;
}

// --- Out-of-place cooking -------------------------------------------------
// Extract a shared out-of-place destination and remove it before verb-specific parsing. Tree
// drivers mirror paths relative to their input root.
    static (string? OutDir, string[] Remaining) ExtractOutDir(string[] args)
{
    var i = Array.FindIndex(args, a => a.Equals("--out", StringComparison.OrdinalIgnoreCase));
    if (i < 0) return (null, args);
    if (i + 1 >= args.Length)
    {
        Console.Error.WriteLine("--out requires a directory argument.");
        Environment.Exit(1);
    }
    var dir = Path.GetFullPath(args[i + 1]);
    Directory.CreateDirectory(dir);
    var rest = args.Where((_, idx) => idx != i && idx != i + 1).ToArray();
    return (dir, rest);
}

// Resolve a cooked output path. In-place (outDir null/empty): a sibling of
// `source` with `newExt`. Out-of-place: mirror source's path relative to
// `inRoot` under `outDir`, with `newExt`; the destination directory is created.
    static string ResolveDest(string source, string inRoot, string? outDir, string newExt)
{
    if (string.IsNullOrEmpty(outDir))
        return Path.ChangeExtension(source, newExt);
    var rel = Path.GetRelativePath(inRoot, Path.ChangeExtension(source, newExt));
    var dest = Path.Combine(outDir, rel);
    Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
    return dest;
}

    static string SourceImageDebtNote(string cookedPath)
{
    var flags = CookedFile.TryReadHeader(cookedPath)?.Stamp.Flags ?? CookedFlags.None;
    return flags.HasFlag(CookedFlags.SourceRequiredForImagesOnly)
        ? "; source images still required"
        : "";
}

    static int CookTextures(string[] args)
{
    var (outDir, a) = ExtractOutDir(args);
    if (a.Length < 2)
    {
        Console.Error.WriteLine("Usage: blix cook textures <directory> [--out <dir>] [--force]");
        return 1;
    }
    var root = a[1];
    var force = a.Skip(2).Any(x => x == "--force" || x == "-f");
    if (!Directory.Exists(root))
    {
        Console.Error.WriteLine($"Directory not found: {root}");
        return 1;
    }
    if (force)
    {
        Console.WriteLine("  --force: re-cooking even when the existing stamp identity is current");
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
    if (outDir is not null) Console.WriteLine($"  writing cooked .blixtex into {outDir}");
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

    // Diagnostic mode trades parallel throughput for attributable per-stage timings.
    var serial = Environment.GetEnvironmentVariable("BLIX_COOK_SERIAL") != null;
    var verbose = Environment.GetEnvironmentVariable("BLIX_COOK_VERBOSE") != null;
    Action<string> traceLine = msg =>
    {
        if (verbose) lock (sources) { Console.WriteLine($"\r{msg}"); }
    };

    void ProcessOne(string source)
    {
        var destination = ResolveDest(source, root, outDir, ".blixtex");
        if (!force && Blix.Recipes.TextureRecipe.IsCurrent(source, destination))
        {
            Interlocked.Increment(ref skippedCount);
            Interlocked.Increment(ref doneCount);
            return;
        }
        var name = Path.GetFileName(source);
        traceLine($"  START {name}");
        var sw = Stopwatch.StartNew();
        try
        {
            Blix.Recipes.TextureRecipe.CookOne(source, destination, out var srcLen, out var dstLen);
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
}

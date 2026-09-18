using BCnEncoder.Encoder;
using BCnEncoder.Shared;
using Blix.Assets;
using Blix.Cooked;
using Blix.Graphics.Images;
using Blix.Graphics;
using Microsoft.Toolkit.HighPerformance;
using System.Diagnostics;
using System.Numerics;

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

using Blix.Core;

namespace Blix.Tools.Cook;

/// <summary>Blix's asset cooker, addressable as <c>blix run cook</c>.</summary>
/// <remarks>
/// <b>A class rather than top-level statements, which is the whole of what this change was.</b> It
/// was the last open cell in the tooling arc's Stage B table, and the row's point is why it
/// mattered: <i>"Blix's tools are not special."</i> A launcher layer whose own cooker is the one
/// thing it cannot address is a layer with an exception in it, and an exception is where the next
/// one goes. Top-level statements have no method to hang an attribute on; every other tool here
/// already declares a Program class, so this now matches them.
/// <para>
/// Nothing about how the cooker WORKS changed. The verbs, the helpers and their behaviour are the
/// same code, one indent level in.
/// </para>
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
            "list" => ListRecipes(),
            "run" => RunRecipe(args),
            "status" => Status(args),
            "batch" => Batch(args),
            "help" or "-h" or "--help" => Help(),
            _ => UnknownVerb(args[0]),
        };
    }

    /// <summary>
    /// Cooks one asset and exactly the images it references, into a tree of its own.
    /// </summary>
    /// <remarks>
    /// <b>The driver that makes a cooked tree self-contained AND smaller than its source.</b> The
    /// other drivers sweep a directory, which is right when the directory IS the unit of work and
    /// wrong for an asset: main Sponza ships 137 texture files and names 72, so a sweep pays a
    /// quarter of its time and bytes for images nothing samples.
    /// <para>
    /// <b>Textures first, then the mesh, and the order is load-bearing.</b> The mesh cook records
    /// where each image's pixels are by looking for a cooked artifact at the place the loader will
    /// look. Cooking the mesh first would have it record source PNGs — correct, and useless for
    /// shipping, because those live in the tree the user is trying to delete.
    /// </para>
    /// <para>
    /// Grouping stays the consumer's: this writes one cooked artifact per source image and records
    /// the relative path, which is a POLICY expressed as data. A project that wants atlases writes
    /// its own driver and its own rows, and the engine's resolver never learns the difference.
    /// </para>
    /// </remarks>
    // blix cook sky <dir-of-blixmesh> [--occupancy N] [--probes N] [--rays N]
    //
    // Bakes how much sky each point in a scene can see. Prints a profile rather than writing a file
    // for now: the first question is whether the numbers are physics, and a format that stores
    // wrong numbers is worse than no format.
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
            log: Console.WriteLine);
        Console.WriteLine($"  baked in {sw.Elapsed.TotalSeconds:0.0}s");

        var b = vol.Bounds;
        Console.WriteLine($"  bounds {b.Min} .. {b.Max}");

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
        Directory.CreateDirectory(outDir);

        var uris = Blix.Recipes.MeshRecipe.ReferencedImageUris(source);
        Console.WriteLine($"  {uris.Count} referenced image(s)");

        long sourceBytes = 0, cookedBytes = 0;
        var sw = Stopwatch.StartNew();
        Parallel.ForEach(uris, uri =>
        {
            var from = Path.Combine(sourceDir, uri);
            if (!File.Exists(from))
            {
                Console.Error.WriteLine($"  missing: {uri}");
                return;
            }

            var to = Path.Combine(outDir, Path.ChangeExtension(uri, ".blixtex"));
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            Blix.Recipes.TextureRecipe.CookOne(from, to, out var inLen, out var outLen);
            Interlocked.Add(ref sourceBytes, inLen);
            Interlocked.Add(ref cookedBytes, outLen);
        });

        Console.WriteLine(
            $"  textures: {sourceBytes / 1048576.0:F1} MB -> {cookedBytes / 1048576.0:F1} MB in {sw.Elapsed.TotalSeconds:F0}s");

        var meshOut = Path.Combine(outDir, Path.GetFileNameWithoutExtension(source) + ".blixmesh");
        // <b>The same simplifier `cook mesh` uses, which this path was silently missing.</b> It
        // called CookToBlixMesh without a `simplify:` argument, so the parameter defaulted to null
        // and every mesh cooked through `cook asset` shipped with LOD0 and nothing else. The stamp
        // said so — simplify=none — and nothing read the stamp.
        //
        // The cost was invisible in the cook and expensive at runtime: VulkanSponza's whole
        // screen-space-error LOD system had nothing to select between, so its overlay read
        // lod-maxlevels 1 and lod-hist 401/0/0/0 even at an eight-pixel error budget, and 12.8M
        // triangles were submitted at full detail twice a frame.
        //
        // This is the second time this exact bug has been fixed. The comment above the call in
        // CookMesh records the first: the simplifier used to be a lambda in this file, "which is how
        // the uniform [Recipe] path ended up with no decimation at all". That fix taught the recipe
        // path and `cook mesh` to share one simplifier, and left `cook asset` behind.
        var splitBudget = 0;
        var splitIdx = Array.FindIndex(args, x => x.Equals("--split", StringComparison.OrdinalIgnoreCase));
        if (splitIdx >= 0 && splitIdx + 1 < args.Length && int.TryParse(args[splitIdx + 1], out var sb))
            splitBudget = sb;
        var count = Blix.Recipes.MeshRecipe.CookToBlixMesh(
            source, meshOut,
            flipTextureV: HasFlag(args, "--flip-v"),
            includeTangents: HasFlag(args, "--tangents"),
            simplify: Blix.Recipes.MeshRecipe.DefaultSimplifier(splitBudget > 0),
            splitTriBudget: splitBudget,
            splitFoliage: !HasFlag(args, "--no-split-foliage"));

        var header = Blix.Cooked.CookedFile.TryReadHeader(meshOut);
        Console.WriteLine($"  mesh: {count} primitive(s) -> {meshOut}");
        Console.WriteLine(
            header?.Stamp.Flags == Blix.Cooked.CookedFlags.None
                ? "  this tree stands alone — no source file is needed to load it."
                : "  the source is still needed; run `blix check --cooked` on the output to see why.");

        return 0;
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
    Console.WriteLine("    status <dir>             what is cooked under <dir>, what is stale, what is not");
    Console.WriteLine("    run <id> <src> [<out>] [-Dkey=value ...]");
    Console.WriteLine("                             run one recipe on one file");
    Console.WriteLine();
    Console.WriteLine("  The drivers — each welded to one recipe, because each knows something");
    Console.WriteLine("  the host does not (out-of-place trees, parallelism, a native simplifier):");
    Console.WriteLine("    textures <dir>           cook every .png/.jpg under <dir> to .blixtex");
    Console.WriteLine("    probe <hdr> [--env-face=256] [--irr-face=32] [--prefilter-base=128]");
    Console.WriteLine("                [--prefilter-mips=5] [--brdf-size=256] [--clamp=50]");
    Console.WriteLine("                             bake an HDR sky to a .blixprobe");
    Console.WriteLine("    mesh <path> [--flip-v] [--tangents] [--split N] [--no-split-foliage]");
    Console.WriteLine("                             cook a .gltf/.glb (or a tree of them) to .blixmesh");
    Console.WriteLine();
    Console.WriteLine("    --out <dir>              write cooked output into a separate tree, mirroring");
    Console.WriteLine("                             each source's path. mesh also copies the .gltf, which");
    Console.WriteLine("                             the runtime still needs beside the .blixmesh.");
    Console.WriteLine();
    Console.WriteLine("  Moved out of this tool, because a cooker's verbs cook:");
    Console.WriteLine("    blix inspect <file>      list what is in an asset — source OR cooked");
    Console.WriteLine("    blix run Blix.Test.Recipes");
    Console.WriteLine("                             prove the native simplifier and BC7 encoder load");
}

    static int CookMesh(string[] args)
{
    var (outDir, a) = ExtractOutDir(args);
    if (a.Length < 2)
    {
        Console.Error.WriteLine("Usage: blix-cook mesh <gltf-or-directory> [--out <dir>] [--flip-v] [--tangents] [--split N]");
        return 1;
    }
    var target = a[1];
    // Opt-in V canonicalisation for bottom-up (OpenGL-authored) sources, baked
    // into the cooked vertex data. Granular per invocation — mirrors
    // AssetImportContext.FlipTextureV on the runtime-import path.
    var flipV = a.Any(x => x.Equals("--flip-v", StringComparison.OrdinalIgnoreCase));
    // Cook the 48-byte tangent layout (VulkanSponza needs it for normal
    // mapping). GL's non-tangent path is sunsetting.
    var tangents = a.Any(x => x.Equals("--tangents", StringComparison.OrdinalIgnoreCase));
    // Spatial split: primitives over this triangle budget are recursively
    // partitioned into chunks (each its own LOD chain) so per-prim distance LOD
    // gets fine-grained. 0/absent = off. --no-split-foliage leaves non-OPAQUE
    // (masked/blended) prims whole for the impostor track to own.
    var splitBudget = 0;
    var splitIdx = Array.FindIndex(a, x => x.Equals("--split", StringComparison.OrdinalIgnoreCase));
    if (splitIdx >= 0 && splitIdx + 1 < a.Length && int.TryParse(a[splitIdx + 1], out var sb))
        splitBudget = sb;
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
    if (outDir is not null) Console.WriteLine($"  writing cooked .blixmesh (+ .gltf) into {outDir}");

    if (sources.Length == 0) return 0;

    foreach (var src in sources)
    {
        var outPath = ResolveDest(src, inRoot, outDir, ".blixmesh");
        // Out-of-place: the runtime reads the .gltf (material/image metadata +
        // node graph) from the cooked tree, so copy it alongside the .blixmesh.
        // The .bin stays behind — with a .blixmesh present the importer never
        // reads it (GltfStaticImporter returns empty for buffer requests).
        if (outDir is not null)
        {
            var gltfDest = ResolveDest(src, inRoot, outDir, Path.GetExtension(src));
            if (!string.Equals(Path.GetFullPath(gltfDest), Path.GetFullPath(src), StringComparison.Ordinal))
                File.Copy(src, gltfDest, overwrite: true);
        }
        // Skip if the .blixmesh is newer than its .gltf source AND was written by this format
        // version.
        //
        // <b>The version half is new, and its absence was a real trap.</b> The check used to compare
        // timestamps alone, so bumping the format left every existing file "up-to-date" while no
        // reader would accept it any more — the cook declining to do the one thing the bump
        // required. Found by bumping to v4 and watching Rogue.blixmesh skip.
        //
        // Reading the preamble is what makes this possible at all: before it, the cook had no way
        // to ask a cooked file what version it was without parsing the format itself.
        if (File.Exists(outPath))
        {
            var srcTime = File.GetLastWriteTimeUtc(src);
            var outTime = File.GetLastWriteTimeUtc(outPath);
            var existing = CookedFile.TryReadHeader(outPath);
            var currentFormat = existing is { Magic: BlixMesh.Magic, FormatVersion: BlixMesh.Version8 };
            if (outTime > srcTime && currentFormat)
            {
                Console.WriteLine($"  up-to-date: {outPath}");
                continue;
            }

            if (!currentFormat)
            {
                Console.WriteLine($"  re-cooking (format is not {nameof(BlixMesh)} v{BlixMesh.Version8}): {outPath}");
            }
        }
        var sw = Stopwatch.StartNew();
        // <b>Borders are locked only when there are seams to protect.</b> LockBorder pins every
        // mesh-boundary vertex, and it is there so that spatially split chunks stay watertight where they
        // meet — which matters when --split is on and is meaningless when it is off, because a whole
        // primitive has no seam with anything.
        //
        // It is not a free precaution. On a model made of many open shells — a stylised tree, whose leaf
        // clusters are separate pieces — nearly every vertex is a border vertex, so locking them forbids
        // the simplifier from collapsing anything at all: measured, a 4,345 triangle tree reduced to 3,975
        // and stopped, while the same simplifier takes a closed grid from 8,192 to 818. A model that cannot
        // be decimated is then drawn at full detail on every horizon, which is where that afternoon went.
        // <b>And pruning, which is what finally moves a tree.</b> Unlocking the borders took a blade of
        // grass from 326/224 to 326/162/81/40 and left a tree at 4,345/3,939, because the two fail for
        // different reasons: a tree's canopy is hundreds of small disconnected clusters, and a simplifier
        // that may not delete a component can only thin each one until it would vanish, which is almost
        // immediately. Prune lets whole components go — which for foliage is not a compromise but the
        // correct behaviour, since what a canopy looks like from further away is fewer, larger masses.
        // The recipe's own simplifier, not a second copy of it here. It used to be a lambda in
        // this file, which is how the uniform [Recipe] path ended up with no decimation at all.
        var count = Blix.Recipes.MeshRecipe.CookToBlixMesh(src, outPath, flipV, tangents,
            simplify: Blix.Recipes.MeshRecipe.DefaultSimplifier(splitBudget > 0),
            splitTriBudget: splitBudget, splitFoliage: splitFoliage);
        var size = new FileInfo(outPath).Length;
        // Quick LOD readout: levels + triangle reduction on the largest primitive.
        var file = Blix.Assets.BlixMeshReader.Read(outPath);
        var biggest = file.Primitives.OrderByDescending(p => p.Lods[0].IndexCount).First();
        var lodCounts = string.Join("/", biggest.Lods.Select(l => l.IndexCount / 3));
        var splitNote = splitBudget > 0
            ? $", split@{splitBudget / 1000}k → biggest chunk {biggest.Lods[0].IndexCount / 3} tris"
            : "";
        Console.WriteLine($"  cooked {Path.GetFileName(src)} -> {Path.GetFileName(outPath)} ({count} prims, {size / 1024.0 / 1024.0:0.00} MB, {tangents}-tan) in {sw.ElapsedMilliseconds} ms; LOD tris (biggest prim): {lodCounts}{splitNote}");
    }
    return 0;
}

// Inspect a rigged/articulated glTF for fitting it onto a Transform3D rig
// (hull/turret/barrel, etc.). Prints the node hierarchy and, for every
// mesh-bearing node, its composed-world transform — the SCALE and TRANSLATION
// are the numbers the fit recipe needs: a rigged part's node translation is its
// rotation PIVOT (authors place the node origin there), and the assembled bounds
// give the model's size + forward axis. Read the values off this, hard-code the
// pivots, and the model drops onto the rig with no blind dialing (see how
// Blix.Demos.TankArena consumes the Quaternius tank).
    static int CookProbe(string[] args)
{
    var (outDir, args2) = ExtractOutDir(args);
    if (args2.Length < 2)
    {
        Console.Error.WriteLine("Usage: blix-cook probe <hdr-path> [--out <dir>] [options]");
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
    foreach (var a in args2.Skip(2))
    {
        if (a.StartsWith("--env-face=")) envFace = int.Parse(a.AsSpan("--env-face=".Length));
        else if (a.StartsWith("--irr-face=")) irrFace = int.Parse(a.AsSpan("--irr-face=".Length));
        else if (a.StartsWith("--prefilter-base=")) prefilterBase = int.Parse(a.AsSpan("--prefilter-base=".Length));
        else if (a.StartsWith("--prefilter-mips=")) prefilterMips = int.Parse(a.AsSpan("--prefilter-mips=".Length));
        else if (a.StartsWith("--brdf-size=")) brdfSize = int.Parse(a.AsSpan("--brdf-size=".Length));
        else if (a.StartsWith("--clamp=")) clamp = float.Parse(a.AsSpan("--clamp=".Length));
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
    Console.WriteLine($"  env={envFace} irr={irrFace} prefilter={prefilterBase}/{prefilterMips} brdf={brdfSize} clamp={clamp}");

    var sw = Stopwatch.StartNew();
    var size = Blix.Recipes.ProbeRecipe.CookOne(
        hdrPath, outPath, envFace, irrFace, prefilterBase, prefilterMips, brdfSize, clamp);
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


// --- cook as a host -------------------------------------------------------
//
// The three verbs above are DRIVERS: each knows how to walk a tree, report progress and
// parallelise, and each is welded to one recipe. The three below are the HOST: they know nothing
// about meshes, textures or probes and work on whatever recipes the assembly declares. A recipe
// added tomorrow is listed, runnable and counted by them on the day it exists.

    static Blix.Cooked.FoundRecipe[] Recipes() => RecipeCatalog.All();

    static int ListRecipes()
{
    foreach (var r in Recipes())
    {
        Console.WriteLine($"  {r.Id}  {string.Join(" ", r.Consumes),-18} -> {r.Produces,-11} v{r.Version}  {r.Summary}");
    }

    return 0;
}

// blix cook run <id> <source> [<output>] [-Dkey=value ...]
//
// The uniform path. `mesh`, `textures` and `probe` stay because each carries real driver knowledge
// this does not have -- out-of-place trees, parallelism, a native simplifier -- and folding those
// in would make this a build system, which is policy. This is the entry a build rule uses.
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

    var output = args.Length > 3 && !args[3].StartsWith("-D", StringComparison.Ordinal)
        ? args[3]
        : recipe.OutputFor(source);

    var options = new Dictionary<string, string>(StringComparer.Ordinal);
    foreach (var a in args.Where(a => a.StartsWith("-D", StringComparison.Ordinal)))
    {
        var kv = a[2..].Split('=', 2);
        options[kv[0]] = kv.Length > 1 ? kv[1] : "1";
    }

    try
    {
        var outcome = recipe.Cook(new Blix.Cooked.CookRequest(source, output, options));
        Console.WriteLine($"  {recipe.Id}: {source} -> {output} ({outcome.Detail})");
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
// The answer to "what is cooked here", which nothing could give before. Coverage in this tree was
// decided by shell history -- one directory fully cooked and its sibling untouched, same project
// and same importer -- and there was no way to find that out short of listing files by hand. Every
// column below is read from the cooked artifacts' own preambles, so this knows nothing about any
// particular format.
    static int Status(string[] args)
{
    var root = args.Length > 1 ? args[1] : ".";
    if (!Directory.Exists(root))
    {
        Console.Error.WriteLine($"No directory at {root}.");
        return 2;
    }

    var recipes = Recipes();
    int cooked = 0, missing = 0, stale = 0, unknown = 0, outdated = 0, pinned = 0;

    foreach (var source in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).OrderBy(x => x, StringComparer.Ordinal))
    {
        if (source.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
            source.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)) continue;

        var matches = recipes.Where(r => r.Accepts(source)).ToArray();
        if (matches.Length != 1) continue;   // nothing claims it, or two do -- neither is this verb's call
        var recipe = matches[0];

        var output = recipe.OutputFor(source);
        var rel = Path.GetRelativePath(root, source);

        if (!File.Exists(output))
        {
            missing++;
            Console.WriteLine($"  UNCOOKED   {rel}   ({recipe.Id} would make {Path.GetExtension(output)})");
            continue;
        }

        if (Blix.Cooked.CookedFile.TryReadHeader(output) is not { } h)
        {
            unknown++;
            Console.WriteLine($"  UNREADABLE {rel}   (its {Path.GetExtension(output)} has no Blix preamble)");
            continue;
        }

        cooked++;
        if (h.Stamp.SourceRequired) pinned++;

        // Two different kinds of out-of-date, and flattening them would hide the second: the SOURCE
        // may have changed, or the RECIPE may have. The first is the one everyone thinks of; the
        // second is what silently leaves a tree half-cooked by two versions of one transformation.
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

    var total = cooked + missing;
    Console.WriteLine();
    Console.WriteLine(total == 0
        ? $"  nothing under {root} is claimed by a recipe."
        : $"  {cooked}/{total} cooked ({100.0 * cooked / total:0}%) -- {missing} uncooked, {stale} stale, " +
          $"{outdated} by an older recipe, {unknown} unknown");
    if (pinned > 0)
    {
        Console.WriteLine($"  {pinned} declare their source is still REQUIRED at load (an optimisation, not a replacement).");
    }

    // Reports; does not judge. `blix check --cooked` is where an exit code will live, because a
    // status verb that failed would make every partially-cooked tree a broken build.
    return 0;
}


// blix cook batch <list-file>
//
// One process for a whole build, rather than one per file. The shader pipeline runs glslc once per
// shader and that is fine because glslc starts in milliseconds; a .NET process does not, so 29
// assets would be 29 startups on every build that touched any of them. MSBuild writes the list, and
// this walks it.
//
// Each line is TAB-separated: recipeId, source, output, options (k=v, space separated).
    static int Batch(string[] args)
{
    if (args.Length < 2)
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

    // <b>Two declarations that write one file is an error, not a race.</b> The build rule derives a
    // cooked path from the source's name alone, so two BlixCook items for one source — differing
    // only in Options — both cook and the second silently overwrites the first. Demonstrated:
    // declaring Villager.obj at recenter=0 and recenter=1 produced two "cooked omsh" lines, one
    // Villager.blixmesh, no warning, and the survivor was whichever came last. The consumer whose
    // settings lost then has its cooked file refused by the loader's guard and walks the source
    // forever — correct, slow, and traceable to nothing.
    //
    // Refused rather than disambiguated, which is this tree's standing answer to ambiguity:
    // BlixRecipes.For returns null rather than guessing when two recipes accept one extension, and
    // the app indexer refuses two recipes sharing an id. Naming the settings in the path would be a
    // design — one with no consumer asking for it — and picking a winner silently is what this is.
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
        if (parts.Length < 3)
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

        // <b>No skip check here, deliberately.</b> MSBuild has already filtered @(BlixCook) down
        // to the out-of-date items before this is called, so anything reaching this loop is
        // something the build decided needs doing — and a second opinion can only ever subtract.
        // It did: an early version re-checked the preamble and "helpfully" skipped two files
        // MSBuild had correctly marked stale, which left them carrying a stamp written by an older
        // version of this tool.
        //
        // The preamble check belongs in the `mesh` driver instead, which walks a directory itself
        // and therefore has to decide. Two layers, one decision each: MSBuild decides whether to
        // look, the driver decides whether to work.
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
            cooked++;
            Console.WriteLine($"  cooked {recipe.Id} {Path.GetFileName(source)} -> {Path.GetFileName(output)} ({outcome.Detail})");
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

    if (cooked > 0 || skipped > 0) Console.WriteLine($"  {cooked} cooked, {skipped} already current");
    return 0;
}

// --- Out-of-place cooking -------------------------------------------------
// `--out <dir>` makes a verb write its outputs into a separate tree, mirroring
// each source's path relative to the input root, instead of as siblings of the
// source. Lets the cooked set (what the runtime reads) live apart from the raw
// sources (the re-cook set) — e.g. sponza/ (cooked) vs sponza-src/ (raw).
// Returns the output root (created) and `args` with `--out <dir>` removed, so
// each verb's own argument parser doesn't trip over the flag or its value.
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

    static int CookTextures(string[] args)
{
    var (outDir, a) = ExtractOutDir(args);
    if (a.Length < 2)
    {
        Console.Error.WriteLine("Usage: blix-cook textures <directory> [--out <dir>] [--force]");
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
        Console.WriteLine("  --force: re-cooking even when .blixtex is newer than source");
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
        var destination = ResolveDest(source, root, outDir, ".blixtex");
        if (File.Exists(destination) && !force)
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

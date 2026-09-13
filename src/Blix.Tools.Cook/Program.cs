using System.Diagnostics;
using System.Numerics;
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
    "inspect" => InspectGltf(args),
    "meshopt-selftest" => MeshoptSelfTest(),
    "help" or "-h" or "--help" => Help(),
    _ => UnknownVerb(args[0]),
};

// Proves the meshoptimizer P/Invoke + dylib load end-to-end: builds a
// subdivided grid, simplifies it, prints original vs reduced triangle counts.
static int MeshoptSelfTest()
{
    const int n = 64; // (n+1)^2 verts, 2*n*n tris
    var verts = (n + 1) * (n + 1);
    var positions = new float[verts * 3];
    for (var y = 0; y <= n; y++)
        for (var x = 0; x <= n; x++)
        {
            var i = (y * (n + 1) + x) * 3;
            positions[i] = x / (float)n;
            positions[i + 1] = MathF.Sin(x * 0.3f) * MathF.Cos(y * 0.3f) * 0.2f; // some relief to simplify
            positions[i + 2] = y / (float)n;
        }
    var indices = new uint[n * n * 6];
    var k = 0;
    for (var y = 0; y < n; y++)
        for (var x = 0; x < n; x++)
        {
            uint a = (uint)(y * (n + 1) + x), b = a + 1, c = a + (uint)(n + 1), d = c + 1;
            indices[k++] = a; indices[k++] = c; indices[k++] = b;
            indices[k++] = b; indices[k++] = c; indices[k++] = d;
        }

    Console.WriteLine($"meshopt self-test: grid {verts} verts, {indices.Length / 3} tris");
    foreach (var ratio in new[] { 0.5f, 0.25f, 0.1f })
    {
        var lod = Blix.Tools.Cook.MeshoptNative.Simplify(
            indices, positions, verts, 3, ratio, targetError: 1.0f,
            Blix.Tools.Cook.MeshoptNative.Options.LockBorder, out var err);
        Console.WriteLine($"  ratio {ratio:0.00} -> {lod.Length / 3} tris (error {err:0.0000})");
    }
    Console.WriteLine("meshopt P/Invoke OK.");
    return 0;
}

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
    Console.WriteLine("  blix-cook mesh <path> [--flip-v]");
    Console.WriteLine("    Cook a .gltf/.glb (file) or every .gltf/.glb under a directory");
    Console.WriteLine("    (recursive) into a sibling .blixmesh. Materials remain in the");
    Console.WriteLine("    .gltf -- only the per-primitive vertex/index data is cooked.");
    Console.WriteLine("    --flip-v canonicalises bottom-up (OpenGL) UVs to a top-down");
    Console.WriteLine("    origin, baked into the cooked vertices.");
    Console.WriteLine("  blix-cook inspect <gltf-or-glb>");
    Console.WriteLine("    Print the node hierarchy + each mesh node's composed-world");
    Console.WriteLine("    scale/translation (= rig pivot) and assembled bounds, for");
    Console.WriteLine("    fitting an articulated model onto a Transform3D rig.");
    Console.WriteLine();
    Console.WriteLine("  --out <dir>  (textures/probe/mesh) write cooked output into a separate");
    Console.WriteLine("    tree, mirroring each source's path relative to the input root, instead");
    Console.WriteLine("    of as siblings. Keeps cooked assets apart from raw sources (e.g. a");
    Console.WriteLine("    cooked sponza/ vs a raw sponza-src/). mesh also copies the .gltf into");
    Console.WriteLine("    the output tree, which the runtime needs alongside the .blixmesh.");
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
        var options = Blix.Tools.Cook.MeshoptNative.Options.Prune;
        if (splitBudget > 0) options |= Blix.Tools.Cook.MeshoptNative.Options.LockBorder;
        var count = Blix.GltfStaticImporter.CookToBlixMesh(src, outPath, flipV, tangents,
            simplify: (positions, indices, vertexCount, ratio) =>
            {
                var reduced = Blix.Tools.Cook.MeshoptNative.Simplify(indices, positions, vertexCount, 3, ratio,
                    targetError: 1.0f, options, out var relError);
                // meshopt's resultError is relative to the mesh extent; scale to
                // world units so the runtime can project it to screen pixels.
                var scale = Blix.Tools.Cook.MeshoptNative.SimplifyScale(positions, vertexCount, 3);
                return new Blix.GltfStaticImporter.SimplifyResult(reduced, relError * scale);
            },
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
static int InspectGltf(string[] args)
{
    if (args.Length < 2)
    {
        Console.Error.WriteLine("Usage: blix-cook inspect <gltf-or-glb>");
        return 1;
    }
    var path = args[1];
    if (!File.Exists(path))
    {
        Console.Error.WriteLine($"File not found: {path}");
        return 1;
    }

    var model = new Blix.GltfStaticImporter().ImportNodes(
        new Blix.Assets.AssetImportContext(Blix.Assets.AssetId.Parse("inspect"), path));
    var nodes = model.Nodes;

    // Compose a node's world transform by walking up its parent chain (row-vector:
    // child = local * parent). ImportNodes keeps every node in LOCAL space, so this
    // is where the assembled placement comes from.
    Matrix4x4 World(int i)
    {
        var m = nodes[i].LocalTransform;
        for (var p = nodes[i].ParentIndex; p >= 0; p = nodes[p].ParentIndex) m *= nodes[p].LocalTransform;
        return m;
    }

    var meshNodes = 0;
    foreach (var n in nodes) if (n.Primitives.Length > 0) meshNodes++;
    Console.WriteLine($"{Path.GetFileName(path)}: {nodes.Length} nodes, {meshNodes} mesh-bearing");
    Console.WriteLine("  (mesh nodes show composed-world scale/translation [= rig PIVOT] + assembled bounds)");

    // Hierarchy depth for indentation.
    int Depth(int i)
    {
        var d = 0;
        for (var p = nodes[i].ParentIndex; p >= 0; p = nodes[p].ParentIndex) d++;
        return d;
    }

    for (var i = 0; i < nodes.Length; i++)
    {
        var n = nodes[i];
        var indent = new string(' ', 2 + Depth(i) * 2);
        if (n.Primitives.Length == 0)
        {
            // Transform-only node — list it (it may be an armature pivot) but keep it terse.
            Console.WriteLine($"{indent}[{i,3}] {n.Name}  (no mesh, parent={n.ParentIndex})");
            continue;
        }

        var w = World(i);
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        var verts = 0;
        foreach (var prim in n.Primitives)
        {
            var md = prim.Mesh;
            verts += md.VertexCount;
            var stride = md.Layout.Stride;
            for (var v = 0; v < md.VertexCount; v++)
            {
                var o = v * stride;
                var lp = new Vector3(
                    BitConverter.ToSingle(md.VertexBytes, o),
                    BitConverter.ToSingle(md.VertexBytes, o + 4),
                    BitConverter.ToSingle(md.VertexBytes, o + 8));
                var wp = Vector3.Transform(lp, w);
                min = Vector3.Min(min, wp); max = Vector3.Max(max, wp);
            }
        }
        Matrix4x4.Decompose(w, out var scale, out _, out var trans);
        Console.WriteLine(
            $"{indent}[{i,3}] {n.Name}  parent={n.ParentIndex} prims={n.Primitives.Length} verts={verts}");
        Console.WriteLine(
            $"{indent}      pivot/trans=({trans.X:0.###}, {trans.Y:0.###}, {trans.Z:0.###})  " +
            $"scale=({scale.X:0.###}, {scale.Y:0.###}, {scale.Z:0.###})");
        Console.WriteLine(
            $"{indent}      bounds X[{min.X:0.##}, {max.X:0.##}]  Y[{min.Y:0.##}, {max.Y:0.##}]  Z[{min.Z:0.##}, {max.Z:0.##}]");
    }
    return 0;
}

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
        : Blix.Tools.Cook.Bc7Native.Available;
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
        var role = ClassifyRole(source);
        // MR textures need channel-aware loading: 1-channel grayscale
        // PNGs (Modern Sponza's "*_Roughness.png") get expanded by stb to
        // (Y, Y, Y, 255), which the shader would then read as
        // metallic = roughness. LoadMetallicRoughness detects the
        // grayscale source and zeroes the B channel so the cooked
        // .blixtex stores (255, Y, 0, 255) -- the canonical ORM layout.
        var image = role == TextureRole.MetallicRoughness
            ? ImageLoader.LoadMetallicRoughness(stream)
            : ImageLoader.LoadRgba32(stream);
        if (verbose) Console.WriteLine($"\r    decoded {name} {image.Width}x{image.Height} in {decodeSw.ElapsedMilliseconds} ms");
        var (bcFormat, flags) = PickFormat(role);
        var format = bcMode ? bcFormat : TextureFormat.Rgba8;

        var mipSw = Stopwatch.StartNew();
        var mipsRgba = GenerateMipsBoxFilter(image.Pixels, image.Width, image.Height, minDim: 4);
        if (verbose) Console.WriteLine($"\r    mipped  {name} {mipsRgba.Count} levels in {mipSw.ElapsedMilliseconds} ms");

        byte[][] encodedMips;
        if (bcMode && bcFormat is TextureFormat.Bc7Srgb or TextureFormat.Bc7Unorm && Blix.Tools.Cook.Bc7Native.Available)
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
                encodedMips[i] = Blix.Tools.Cook.Bc7Native.EncodeImage(pixels, w, h, perceptual, quality, numThreads: 1);
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

        BlixTexWriter.Write(destination, new BlixTexImage(
            image.Width, image.Height, format, encodedMips, flags));
    }
    destLen = new FileInfo(destination).Length;
}

// Native BC7 quality knob: 0 fastest, 1 balanced (default), 2 high. Set
// BLIX_BC7_QUALITY to override. Higher = slower cook, better quality.
static int Bc7Quality() =>
    int.TryParse(Environment.GetEnvironmentVariable("BLIX_BC7_QUALITY"), out var q)
        ? Math.Clamp(q, 0, 2)
        : 1;

// Picks a BCn format + flags based on the heuristic role classification.
static (TextureFormat Format, BlixTex.Flags Flags) PickFormat(TextureRole role) => role switch
{
    TextureRole.BaseColor          => (TextureFormat.Bc7Srgb, BlixTex.Flags.Srgb),
    TextureRole.Emissive           => (TextureFormat.Bc7Srgb, BlixTex.Flags.Srgb),
    TextureRole.Normal             => (TextureFormat.Bc7Unorm, BlixTex.Flags.NormalMap),
    TextureRole.MetallicRoughness  => (TextureFormat.Bc7Unorm, BlixTex.Flags.None),
    TextureRole.Linear             => (TextureFormat.Bc7Unorm, BlixTex.Flags.None),
    _                              => (TextureFormat.Bc7Unorm, BlixTex.Flags.None),
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

enum TextureRole { BaseColor, Normal, Emissive, MetallicRoughness, Linear }

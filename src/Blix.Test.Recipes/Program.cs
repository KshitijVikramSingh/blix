using Blix.Assets;
using System.Diagnostics;
using Blix.Cooked;
using Blix.Recipes;
using Blix.Verify;

namespace Blix.Test.Recipes;

// CLI test harness for the recipes and the native capabilities they call.
//
// ── Where this came from ────────────────────────────────────────────────────
//   `blix-cook meshopt-selftest` — a verb on the cooker that cooked nothing. It built a grid,
//   simplified it and printed triangle counts for a person to read, which is a test wearing a
//   verb's clothes. The cooker is a host for recipes; proving a P/Invoke is a suite's job, and it
//   was the same finding `check` had.
//
//   Printing counts is also not a test. Every claim below is paired with something that could
//   fail, because a self-test that only ever prints is a self-test that only ever passes.
public static class Program
{
    public static int Main()
    {
        var t = new TestRunner();

        // ── The native simplifier ───────────────────────────────────────────
        // A subdivided grid with relief, which is a shape that decimates well — unlike a stylised
        // tree, whose canopy is hundreds of disconnected clusters. That difference cost an
        // afternoon once and is why Prune exists; see the mesh recipe's own notes.
        const int n = 64;
        var verts = (n + 1) * (n + 1);
        var positions = new float[verts * 3];
        for (var y = 0; y <= n; y++)
        for (var x = 0; x <= n; x++)
        {
            var i = (y * (n + 1) + x) * 3;
            positions[i] = x / (float)n;
            positions[i + 1] = MathF.Sin(x * 0.3f) * MathF.Cos(y * 0.3f) * 0.2f;
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

        var full = indices.Length / 3;
        var previous = full;
        var previousError = -1f;
        foreach (var ratio in new[] { 0.5f, 0.25f, 0.1f })
        {
            var lod = MeshoptNative.Simplify(
                indices, positions, verts, 3, ratio, targetError: 1.0f, MeshoptNative.Options.LockBorder, out var error);
            var tris = lod.Length / 3;

            t.Expect($"meshopt reduces at ratio {ratio:0.00}", tris < previous, $"{previous} -> {tris}");
            t.Expect($"meshopt error grows as it decimates at {ratio:0.00}", error > previousError,
                $"{previousError:0.0000} -> {error:0.0000}");
            t.ExpectTrue($"meshopt indices stay in range at {ratio:0.00}", lod.All(i => i < verts));
            t.ExpectTrue($"meshopt emits whole triangles at {ratio:0.00}", lod.Length % 3 == 0);
            previous = tris;
            previousError = error;
        }

        // The negative control the printed self-test never had: asking for no reduction must not
        // silently hand back a decimated mesh.
        var unreduced = MeshoptNative.Simplify(
            indices, positions, verts, 3, 1.0f, targetError: 0f, MeshoptNative.Options.LockBorder, out _);
        t.Expect("meshopt at ratio 1.0 does not decimate", unreduced.Length / 3 >= full * 0.99,
            $"{full} -> {unreduced.Length / 3}");

        t.ExpectTrue("SimplifyScale reports a positive world scale",
            MeshoptNative.SimplifyScale(positions, verts, 3) > 0f);

        // ── The native BC7 encoder ──────────────────────────────────────────
        // <b>This one matters more than it looks.</b> When bc7enc is not loadable the texture cook
        // does not fail — it falls back to Rgba8, which is four times larger on disk and in GPU
        // memory, silently. A missing dylib would show up as "the build got bigger" months later.
        t.Expect("the bc7 encoder is loadable — without it textures cook 4x larger, silently",
            Bc7Native.Available);

        // ── The recipes, as declared ────────────────────────────────────────
        var recipes = BlixRecipes.Find(typeof(MeshRecipe).Assembly);
        t.Expect("Blix ships four recipes", recipes.Length == 4, $"found {recipes.Length}");
        t.ExpectTrue("every recipe id is a 4cc", recipes.All(r => r.Id.Length == CookStamp.RecipeIdLength));
        t.Expect("no two recipes share an id",
            recipes.Select(r => r.Id).Distinct(StringComparer.Ordinal).Count() == recipes.Length);

        // Matching a source to a recipe is what a build rule does first, so it is worth a check in
        // both directions.
        t.ExpectTrue("a .glb is claimed by the mesh recipe",
            BlixRecipes.For(typeof(MeshRecipe).Assembly, "x.glb")?.Id == "gmsh");
        t.ExpectTrue("a .png is claimed by the texture recipe",
            BlixRecipes.For(typeof(MeshRecipe).Assembly, "x.png")?.Id == "gtex");
        t.ExpectTrue("an .hdr is claimed by the probe recipe",
            BlixRecipes.For(typeof(MeshRecipe).Assembly, "x.hdr")?.Id == "gpro");
        t.ExpectTrue("a file no recipe wants is claimed by none",
            BlixRecipes.For(typeof(MeshRecipe).Assembly, "notes.txt") is null);

        // Case, because a source tree contains .PNG as readily as .png and a cook that skipped
        // those would look like a coverage gap with no cause.
        t.ExpectTrue("extension matching ignores case",
            BlixRecipes.For(typeof(MeshRecipe).Assembly, "X.PNG")?.Id == "gtex");

        t.ExpectTrue("a recipe names the file it would write",
            BlixRecipes.For(typeof(MeshRecipe).Assembly, "a/b/c.glb")!.OutputFor("a/b/c.glb")
                .EndsWith("c.blixmesh", StringComparison.Ordinal));

        // ── the fourth recipe, which is the point of having a substrate ─────
        // <b>These check what it cost to add one, not what fonts do.</b> The claim the whole cook
        // arc rests on is that a recipe costs the recipe — stamping, discovery, coverage,
        // re-cooking and reporting all arriving for free. Three recipes written together prove
        // nothing about that. A fourth, written afterwards against the finished substrate, is the
        // only honest test of it.
        var fontTemp = Path.Combine(Path.GetTempPath(), $"blix-font-{Guid.NewGuid():N}");
        Directory.CreateDirectory(fontTemp);
        var wasOn = AssetLoadLog.Enabled;
        try
        {
            // The spec and its TTF, copied out of a project so this bakes a real font.
            var ttfSource = FindFile("BowlbyOne-Regular.ttf");
            var specSource = FindFile("BowlbyOne-Regular.font.json");
            if (ttfSource is null || specSource is null)
            {
                t.Fail("a font to bake is findable", "no BowlbyOne under the repo");
            }
            else
            {
                File.Copy(ttfSource, Path.Combine(fontTemp, Path.GetFileName(ttfSource)));
                var spec = Path.Combine(fontTemp, Path.GetFileName(specSource));
                File.Copy(specSource, spec);

                // Rasterised, for comparison — the path every launch took before this recipe.
                var slow = Stopwatch.StartNew();
                var rasterised = new FontImporter().ImportSource(
                    new AssetImportContext(AssetId.Parse("t/font"), spec));
                slow.Stop();

                var baked = Path.ChangeExtension(spec, ".blixfont");
                FontRecipe.CookOne(spec, baked);
                t.ExpectTrue("the font recipe writes an artifact", File.Exists(baked));

                // It is a Blix cooked artifact like any other, readable by a tool that knows
                // nothing about fonts — which is what the shared preamble bought.
                var header = CookedFile.TryReadHeader(baked);
                t.ExpectTrue("and it carries the shared preamble", header is not null);
                t.Expect("stamped by the font recipe", header!.Value.Stamp.Recipe == "fnt1",
                    $"got '{header.Value.Stamp.Recipe}'");
                t.ExpectTrue("recording the sizes it baked", header.Value.Stamp.Parameters.StartsWith("sizes=", StringComparison.Ordinal));
                t.ExpectTrue("and its source, relatively",
                    !Path.IsPathRooted(header.Value.Stamp.SourcePath));

                // Round-trip: the baked atlas must BE the rasterised one, not merely resemble it.
                var fast = Stopwatch.StartNew();
                var read = BlixFontReader.Read(baked);
                fast.Stop();

                t.Expect("the baked font has every size", read.Sizes.Count == rasterised.Sizes.Count,
                    $"{rasterised.Sizes.Count} -> {read.Sizes.Count}");
                t.Expect("and every glyph",
                    read.Sizes.Sum(x => x.Glyphs.Count) == rasterised.Sizes.Sum(x => x.Glyphs.Count));
                t.ExpectTrue("with identical coverage bytes",
                    read.Sizes.Zip(rasterised.Sizes).All(pair => pair.First.AlphaPixels.SequenceEqual(pair.Second.AlphaPixels)));
                t.ExpectTrue("and identical metrics",
                    read.Sizes.Zip(rasterised.Sizes).All(pair =>
                        Math.Abs(pair.First.Ascent - pair.Second.Ascent) < 1e-4f
                        && Math.Abs(pair.First.LineHeight - pair.Second.LineHeight) < 1e-4f));

                Console.WriteLine($"     rasterise {slow.Elapsed.TotalMilliseconds:0.0} ms  ->  read baked {fast.Elapsed.TotalMilliseconds:0.0} ms");

                // Reproducible, like the other three.
                var first = File.ReadAllBytes(baked);
                FontRecipe.CookOne(spec, baked);
                t.ExpectTrue("a second cook is byte-identical", first.SequenceEqual(File.ReadAllBytes(baked)));

                // And the loader prefers it, and says so.
                AssetLoadLog.Start();
                new FontImporter().Import(new AssetImportContext(AssetId.Parse("t/font2"), spec));
                var cookedReport = AssetLoadLog.Drain().SingleOrDefault();
                t.ExpectTrue("the loader reports a baked font as Cooked",
                    cookedReport is { Mode: AssetLoadMode.Cooked, Recipe: "fnt1" });

                File.Delete(baked);
                AssetLoadLog.Start();
                new FontImporter().Import(new AssetImportContext(AssetId.Parse("t/font3"), spec));
                var sourceReport = AssetLoadLog.Drain().SingleOrDefault();
                t.ExpectTrue("and without one, reports Source and says why",
                    sourceReport is { Mode: AssetLoadMode.Source, Warning: { Length: > 0 } });
            }
        }
        finally
        {
            AssetLoadLog.Enabled = wasOn;
            AssetLoadLog.Drain();
            try { Directory.Delete(fontTemp, recursive: true); } catch (IOException) { }
        }

        t.PrintSummary();
        return t.Failed;
    }

    /// <summary>Walks up from the binary to the repo and finds one file by name.</summary>
    /// <remarks>
    /// A suite that baked a synthetic font would test the serialiser and nothing else. This bakes
    /// the font four projects actually ship, so a change that breaks real content fails here.
    /// </remarks>
    private static string? FindFile(string name)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "src"))) dir = dir.Parent;
        return dir is null
            ? null
            : Directory.EnumerateFiles(Path.Combine(dir.FullName, "src"), name, SearchOption.AllDirectories)
                .FirstOrDefault(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                                  && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal));
    }
}

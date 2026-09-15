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
        t.Expect("Blix ships three recipes", recipes.Length == 3, $"found {recipes.Length}");
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

        t.PrintSummary();
        return t.Failed;
    }
}

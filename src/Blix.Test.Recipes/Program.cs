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

        // ── a rigged glTF has a cooked form, and it is equivalent ──────────
        // <b>This block used to assert the opposite, and that is the point of it.</b> It read "it
        // always reports Source, and that is a statement rather than a gap: .blixmesh carries two
        // vertex layouts and neither holds skin weights". That was true and is no longer: the
        // format carries a skin table, a clip table, and per-primitive layouts so a rig's
        // attachments travel with it.
        //
        // Reporting Cooked is the weak half of the claim. The strong half is that the cooked rig is
        // the SAME rig — same bones in the same order, same clips, same attachments — because a
        // cooked character that loads fast and animates differently is worse than one that loads
        // slowly.
        var rig = FindFile("Rogue.glb");
        if (rig is null)
        {
            t.Fail("a rigged asset is findable", "no Rogue.glb under the repo");
        }
        else
        {
            var wasLogging = AssetLoadLog.Enabled;
            var rigTemp = Path.Combine(Path.GetTempPath(), "blix-rig-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(rigTemp);
            try
            {
                AssetLoadLog.Start();
                var viaCookedRig = new Blix.GltfImporter()
                    .Import(new AssetImportContext(AssetId.Parse("t/rig"), rig));
                var rigReports = AssetLoadLog.Drain();

                var meshReport = rigReports.SingleOrDefault(r => r.SourcePath == rig);
                t.ExpectTrue("a rigged load is reported at all", meshReport is not null);
                t.Expect("and now reports Cooked", meshReport!.Mode == AssetLoadMode.Cooked, $"got {meshReport.Mode}");
                t.ExpectTrue("with a cost attached", meshReport.LoadMs > 0 && meshReport.Bytes > 0);

                // The source leg: the same .glb with no cooked sibling beside it.
                var loneRig = Path.Combine(rigTemp, Path.GetFileName(rig));
                File.Copy(rig, loneRig);
                var viaSourceRig = new Blix.GltfImporter()
                    .Import(new AssetImportContext(AssetId.Parse("t/rig-source"), loneRig));

                t.Expect("cooked and source agree on bone count",
                    viaCookedRig.Skeleton.BoneCount == viaSourceRig.Skeleton.BoneCount,
                    $"cooked {viaCookedRig.Skeleton.BoneCount}, source {viaSourceRig.Skeleton.BoneCount}");
                t.Expect("and on clip count",
                    viaCookedRig.Animations.Length == viaSourceRig.Animations.Length,
                    $"cooked {viaCookedRig.Animations.Length}, source {viaSourceRig.Animations.Length}");
                t.Expect("and on attachment count",
                    viaCookedRig.AttachmentsOrEmpty.Length == viaSourceRig.AttachmentsOrEmpty.Length,
                    $"cooked {viaCookedRig.AttachmentsOrEmpty.Length}, source {viaSourceRig.AttachmentsOrEmpty.Length}");
                // Without this the three counts above can all pass on an asset with no attachments,
                // which is the case the per-primitive layout migration was made for.
                t.Expect("on an asset that actually has attachments",
                    viaSourceRig.AttachmentsOrEmpty.Length > 0,
                    $"{viaSourceRig.AttachmentsOrEmpty.Length} attachments");

                var rigMismatch = new List<string>();
                for (var i = 0; i < Math.Min(viaCookedRig.Skeleton.BoneCount, viaSourceRig.Skeleton.BoneCount); i++)
                {
                    var a = viaCookedRig.Skeleton.Bones[i];
                    var b = viaSourceRig.Skeleton.Bones[i];
                    if (a.Name != b.Name) rigMismatch.Add($"bone[{i}] {a.Name} vs {b.Name}");
                    if (a.ParentIndex != b.ParentIndex) rigMismatch.Add($"bone[{i}] parent {a.ParentIndex} vs {b.ParentIndex}");
                    if (a.InverseBindPose != b.InverseBindPose) rigMismatch.Add($"bone[{i}] inverse bind differs");
                }

                t.Expect("bones match name, parent and inverse bind", rigMismatch.Count == 0,
                    string.Join("; ", rigMismatch.Take(4)));

                var clipMismatch = new List<string>();
                for (var i = 0; i < Math.Min(viaCookedRig.Animations.Length, viaSourceRig.Animations.Length); i++)
                {
                    var a = viaCookedRig.Animations[i];
                    var b = viaSourceRig.Animations[i];
                    if (a.Name != b.Name) clipMismatch.Add($"clip[{i}] {a.Name} vs {b.Name}");
                    if (Math.Abs(a.Duration - b.Duration) > 1e-6) clipMismatch.Add($"clip '{a.Name}' duration {a.Duration} vs {b.Duration}");
                    if (a.Tracks.Length != b.Tracks.Length) clipMismatch.Add($"clip '{a.Name}' tracks {a.Tracks.Length} vs {b.Tracks.Length}");
                }

                t.Expect("clips match name, duration and track count", clipMismatch.Count == 0,
                    string.Join("; ", clipMismatch.Take(4)));

                // Vertex bytes, because equal counts are not equal geometry.
                var vertexMismatch = 0;
                for (var i = 0; i < Math.Min(viaCookedRig.Primitives.Length, viaSourceRig.Primitives.Length); i++)
                {
                    if (!viaCookedRig.Primitives[i].Mesh.VertexBytes.AsSpan()
                            .SequenceEqual(viaSourceRig.Primitives[i].Mesh.VertexBytes))
                    {
                        vertexMismatch++;
                    }
                }

                t.Expect("skinned vertices are byte-identical", vertexMismatch == 0,
                    $"{vertexMismatch} primitive(s) differ");

                // <b>Materials, because equal geometry drawn with a different surface is a
                // different picture.</b> Left out of the first version of this control, and a cooked
                // Rogue rendered visibly brighter than the source one with every other assertion
                // here passing — the character was being drawn without its albedo.
                var matMismatch = new List<string>();
                var withAlbedo = 0;
                for (var i = 0; i < Math.Min(viaCookedRig.Primitives.Length, viaSourceRig.Primitives.Length); i++)
                {
                    var x = viaCookedRig.Primitives[i].Material;
                    var y = viaSourceRig.Primitives[i].Material;
                    if (x is null || y is null)
                    {
                        if (!ReferenceEquals(x, y)) matMismatch.Add($"[{i}] material presence {x is not null} vs {y is not null}");
                        continue;
                    }

                    if (x.BaseColorFactor != y.BaseColorFactor) matMismatch.Add($"[{i}] base colour {x.BaseColorFactor} vs {y.BaseColorFactor}");
                    if ((x.BaseColorTexture is null) != (y.BaseColorTexture is null))
                        matMismatch.Add($"[{i}] albedo presence {x.BaseColorTexture is not null} vs {y.BaseColorTexture is not null}");
                    if (x.MetallicFactor != y.MetallicFactor) matMismatch.Add($"[{i}] metallic {x.MetallicFactor} vs {y.MetallicFactor}");
                    if (x.RoughnessFactor != y.RoughnessFactor) matMismatch.Add($"[{i}] roughness {x.RoughnessFactor} vs {y.RoughnessFactor}");
                    if (y.BaseColorTexture is not null) withAlbedo++;
                }

                t.Expect("materials match on the rigged path", matMismatch.Count == 0,
                    string.Join("; ", matMismatch.Take(5)));
                t.Expect("on primitives that actually carry an albedo", withAlbedo > 0,
                    $"{withAlbedo} of {viaSourceRig.Primitives.Length}");

                // <b>And the static importer declines it by name.</b> A rigged cooked file holds
                // skinned vertices it cannot draw; silently drawing them at the wrong stride is the
                // failure this whole session kept meeting.
                AssetLoadLog.Start();
                new Blix.GltfStaticImporter().Import(new AssetImportContext(AssetId.Parse("t/static"), rig));
                var staticReport = AssetLoadLog.Drain().SingleOrDefault(r => r.SourcePath == rig);
                t.ExpectTrue("the static importer walks the glTF rather than reading a rig",
                    staticReport is { Mode: AssetLoadMode.Source }
                    && staticReport.Warning?.Contains("holds a rig", StringComparison.Ordinal) == true);
            }
            finally
            {
                AssetLoadLog.Enabled = wasLogging;
                AssetLoadLog.Drain();
                try { Directory.Delete(rigTemp, recursive: true); } catch (IOException) { }
            }
        }

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

        // ── K-F: a cooked load and a source load describe the same surface ──
        // <b>The control the whole stage rests on.</b> .blixmesh v5 carries every material property
        // except image bytes, and the loader now reads them from the file instead of re-walking the
        // glTF. That is only an improvement if the two paths agree: a property the cook silently
        // drops is one that disappears the moment an asset is cooked, which shows up as an asset
        // rendering differently on machines that have built — the worst shape a bug can take,
        // because the tree that reproduces it is the tree that works.
        //
        // Both halves are loaded from real shipped assets rather than a synthetic material, because
        // a synthetic one tests the writer against the reader and nothing against glTF.
        var cookedAsset = FindFile("Rogue.glb");
        if (cookedAsset is null)
        {
            t.Fail("an asset with a cooked sibling is findable", "no Rogue.glb under the repo");
        }
        else if (!File.Exists(Path.ChangeExtension(cookedAsset, ".blixmesh")))
        {
            t.Fail("the asset has a .blixmesh sibling", $"none beside {cookedAsset}");
        }
        else
        {
            // The source leg is the SAME file with no cooked sibling beside it — copied out rather
            // than the cooked one deleted, so a failure here cannot damage the tree.
            var temp = Path.Combine(Path.GetTempPath(), "blix-kf-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(temp);
            try
            {
                var lone = Path.Combine(temp, Path.GetFileName(cookedAsset));
                File.Copy(cookedAsset, lone);

                var viaCooked = new Blix.GltfStaticImporter()
                    .Import(new AssetImportContext(AssetId.Parse("t/kf-cooked"), cookedAsset));
                var viaSource = new Blix.GltfStaticImporter()
                    .Import(new AssetImportContext(AssetId.Parse("t/kf-source"), lone));

                t.Expect("both paths return the same primitive count",
                    viaCooked.Primitives.Length == viaSource.Primitives.Length,
                    $"cooked {viaCooked.Primitives.Length}, source {viaSource.Primitives.Length}");

                var pairs = Math.Min(viaCooked.Primitives.Length, viaSource.Primitives.Length);
                var mismatches = new List<string>();
                var compared = 0;
                for (var i = 0; i < pairs; i++)
                {
                    var a = viaCooked.Primitives[i].Material;
                    var b = viaSource.Primitives[i].Material;
                    if (a is null || b is null)
                    {
                        if (!ReferenceEquals(a, b)) mismatches.Add($"[{i}] one path has no material");
                        continue;
                    }

                    compared++;
                    void Same(string field, bool ok, object got, object want)
                    {
                        if (!ok) mismatches.Add($"[{i}] {field}: cooked {got}, source {want}");
                    }

                    Same("Name", a.Name == b.Name, a.Name, b.Name);
                    Same("BaseColorFactor", a.BaseColorFactor == b.BaseColorFactor, a.BaseColorFactor, b.BaseColorFactor);
                    Same("BaseColorTexCoord", a.BaseColorTexCoord == b.BaseColorTexCoord, a.BaseColorTexCoord, b.BaseColorTexCoord);
                    Same("MetallicFactor", a.MetallicFactor == b.MetallicFactor, a.MetallicFactor, b.MetallicFactor);
                    Same("RoughnessFactor", a.RoughnessFactor == b.RoughnessFactor, a.RoughnessFactor, b.RoughnessFactor);
                    Same("OcclusionStrength", a.OcclusionStrength == b.OcclusionStrength, a.OcclusionStrength, b.OcclusionStrength);
                    Same("EmissiveFactor", a.EmissiveFactor == b.EmissiveFactor, a.EmissiveFactor, b.EmissiveFactor);
                    Same("EmissiveStrength", a.EmissiveStrength == b.EmissiveStrength, a.EmissiveStrength, b.EmissiveStrength);
                    Same("AlphaMode", a.AlphaMode == b.AlphaMode, a.AlphaMode, b.AlphaMode);
                    Same("AlphaCutoff", a.AlphaCutoff == b.AlphaCutoff, a.AlphaCutoff, b.AlphaCutoff);
                    Same("DoubleSided", a.DoubleSided == b.DoubleSided, a.DoubleSided, b.DoubleSided);
                    Same("TransmissionFactor", a.TransmissionFactor == b.TransmissionFactor, a.TransmissionFactor, b.TransmissionFactor);

                    // The IMAGES are deliberately not compared byte for byte — they come from the
                    // same source on both paths. What is checked is that each channel agrees about
                    // whether it HAS one, which is what a dropped image reference would break.
                    Same("BaseColorTexture presence", (a.BaseColorTexture is null) == (b.BaseColorTexture is null),
                        a.BaseColorTexture is not null, b.BaseColorTexture is not null);
                    Same("NormalTexture presence", (a.NormalTexture is null) == (b.NormalTexture is null),
                        a.NormalTexture is not null, b.NormalTexture is not null);
                    Same("MetallicRoughnessTexture presence", (a.MetallicRoughnessTexture is null) == (b.MetallicRoughnessTexture is null),
                        a.MetallicRoughnessTexture is not null, b.MetallicRoughnessTexture is not null);
                    Same("OcclusionTexture presence", (a.OcclusionTexture is null) == (b.OcclusionTexture is null),
                        a.OcclusionTexture is not null, b.OcclusionTexture is not null);
                    Same("EmissiveTexture presence", (a.EmissiveTexture is null) == (b.EmissiveTexture is null),
                        a.EmissiveTexture is not null, b.EmissiveTexture is not null);
                }

                // Without this the loop above passes on an asset whose materials are all null,
                // which is a test that cannot fail.
                t.Expect("materials were actually compared", compared > 0, $"compared {compared}");
                t.Expect("cooked and source materials agree on every field",
                    mismatches.Count == 0, string.Join("; ", mismatches.Take(5)));
            }
            finally
            {
                try { Directory.Delete(temp, recursive: true); } catch (IOException) { }
            }
        }

        // ── and the debt is gone, which is the flag's whole point ───────────
        // SourceRequired was set on every .blixmesh from K-A onward. K-F narrowed it to image bytes;
        // the image table removed the last reason to open the source at all. A flag that could only
        // ever be true was never telling anyone anything, so the proof it can be FALSE is the proof
        // the arc landed.
        var meshPath = Path.ChangeExtension(cookedAsset ?? "x", ".blixmesh");
        var cookedHeader = CookedFile.TryReadHeader(meshPath);
        t.Expect("a cooked mesh with every image cooked owes nothing",
            cookedHeader?.Stamp.Flags == CookedFlags.None,
            $"flags = {cookedHeader?.Stamp.Flags.ToString() ?? "no header"}");

        // ── and it loads with the source deleted, which is the real claim ───
        // <b>Every other check here runs beside the source file.</b> "Ships on its own" is not
        // implied by any of them: the loader could still be quietly reaching for a sibling, and
        // nothing that runs in a complete tree would notice. So the cooked mesh and its extracted
        // textures are copied somewhere the glTF does not exist, and loaded there.
        if (cookedAsset is not null && File.Exists(meshPath))
        {
            var aloneDir = Path.Combine(Path.GetTempPath(), "blix-alone-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(aloneDir);
            try
            {
                var meshCopy = Path.Combine(aloneDir, Path.GetFileName(meshPath));
                File.Copy(meshPath, meshCopy);

                // Whatever the cook extracted out of the container travels with it.
                var extracted = Path.ChangeExtension(meshPath, null) + BlixMesh.ExtractedImageFolder;
                if (Directory.Exists(extracted))
                {
                    var into = Path.Combine(aloneDir, Path.GetFileName(extracted));
                    Directory.CreateDirectory(into);
                    foreach (var f in Directory.EnumerateFiles(extracted))
                    {
                        File.Copy(f, Path.Combine(into, Path.GetFileName(f)));
                    }
                }

                t.ExpectTrue("no source file travelled with it",
                    !Directory.EnumerateFiles(aloneDir, "*.gl*", SearchOption.AllDirectories).Any());

                // Routed by what the cooked file HOLDS, which is the same rule the judge uses.
                var rigged = BlixMeshReader.Read(meshCopy).IsRigged;
                var alone = rigged
                    ? new Blix.GltfImporter().Import(new AssetImportContext(AssetId.Parse("t/alone"), meshCopy))
                    : new Blix.GltfStaticImporter().Import(new AssetImportContext(AssetId.Parse("t/alone"), meshCopy));

                // The same asset loaded the ordinary way, in its own tree, to compare against.
                var besideSource = rigged
                    ? new Blix.GltfImporter().Import(new AssetImportContext(AssetId.Parse("t/beside"), cookedAsset))
                    : new Blix.GltfStaticImporter().Import(new AssetImportContext(AssetId.Parse("t/beside"), cookedAsset));

                t.Expect("a cooked mesh loads with no source anywhere",
                    alone.Primitives.Length == besideSource.Primitives.Length,
                    $"alone {alone.Primitives.Length}, beside-source {besideSource.Primitives.Length}");

                // Same surfaces, not merely the same count — a load that silently lost its textures
                // would pass a count check and draw grey.
                var aloneMismatch = new List<string>();
                var withTexture = 0;
                for (var i = 0; i < Math.Min(alone.Primitives.Length, besideSource.Primitives.Length); i++)
                {
                    var x = alone.Primitives[i].Material;
                    var y = besideSource.Primitives[i].Material;
                    if (x is null || y is null)
                    {
                        if (!ReferenceEquals(x, y)) aloneMismatch.Add($"[{i}] material presence");
                        continue;
                    }

                    if (x.Name != y.Name) aloneMismatch.Add($"[{i}] name {x.Name} vs {y.Name}");
                    if (x.BaseColorFactor != y.BaseColorFactor) aloneMismatch.Add($"[{i}] base colour");
                    if (x.AlphaMode != y.AlphaMode) aloneMismatch.Add($"[{i}] alpha mode");
                    if ((x.BaseColorTexture is null) != (y.BaseColorTexture is null))
                        aloneMismatch.Add($"[{i}] base colour texture presence");
                    if (x.BaseColorTexture is not null) withTexture++;
                }

                t.Expect("with its materials intact", aloneMismatch.Count == 0,
                    string.Join("; ", aloneMismatch.Take(5)));
                // Without this the material comparison above passes on an asset with no textures,
                // which is exactly the case the image table exists for.
                t.Expect("and its textures resolved from the cooked tree", withTexture > 0,
                    $"{withTexture} primitives carried a base colour texture");
            }
            finally
            {
                try { Directory.Delete(aloneDir, recursive: true); } catch (IOException) { }
            }
        }

        // ── the OBJ path: cooked and source agree, and the settings guard holds ──
        // <b>Closing the gap `blix check --cooked` had over .obj.</b> The judge used to answer
        // "nothing here that this judges" over a directory of them — the same sentence an EMPTY
        // directory produces — because no .obj reader reported what it did. Reporting is only
        // worth having if the cooked path it reports is the same geometry, so both halves are
        // checked here rather than the report alone.
        var objAsset = FindFile("Bush_1.obj");
        if (objAsset is null)
        {
            t.Fail("a cooked .obj is findable", "no Bush_1.obj under the repo");
        }
        else
        {
            var objTemp = Path.Combine(Path.GetTempPath(), "blix-obj-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(objTemp);
            try
            {
                // The source leg needs the .mtl beside it or the colours come back as defaults and
                // the comparison would be measuring the copy, not the cook.
                var loneObj = Path.Combine(objTemp, Path.GetFileName(objAsset));
                File.Copy(objAsset, loneObj);
                var mtl = Path.ChangeExtension(objAsset, ".mtl");
                if (File.Exists(mtl)) File.Copy(mtl, Path.ChangeExtension(loneObj, ".mtl"));

                var partsCooked = WavefrontParts.Import(objAsset);
                var partsSource = WavefrontParts.Import(loneObj);

                t.Expect("cooked and source OBJ yield the same part count",
                    partsCooked.Count == partsSource.Count,
                    $"cooked {partsCooked.Count}, source {partsSource.Count}");

                var objMismatches = new List<string>();
                for (var i = 0; i < Math.Min(partsCooked.Count, partsSource.Count); i++)
                {
                    var a = partsCooked[i];
                    var b = partsSource[i];
                    if (a.Material != b.Material) objMismatches.Add($"[{i}] material {a.Material} vs {b.Material}");
                    if (a.Color != b.Color) objMismatches.Add($"[{i}] colour {a.Color} vs {b.Color}");
                    if (a.Mesh.VertexCount != b.Mesh.VertexCount) objMismatches.Add($"[{i}] vertices {a.Mesh.VertexCount} vs {b.Mesh.VertexCount}");
                    if (a.Mesh.IndexCount != b.Mesh.IndexCount) objMismatches.Add($"[{i}] indices {a.Mesh.IndexCount} vs {b.Mesh.IndexCount}");
                    if (a.Mesh.Bounds.Min != b.Mesh.Bounds.Min || a.Mesh.Bounds.Max != b.Mesh.Bounds.Max)
                        objMismatches.Add($"[{i}] bounds {a.Mesh.Bounds.Min}..{a.Mesh.Bounds.Max} vs {b.Mesh.Bounds.Min}..{b.Mesh.Bounds.Max}");
                    // Byte equality, not just counts and bounds. RTSGame's settlement art now comes
                    // off these cooked files, so "the same number of vertices in the same box" is not
                    // enough — a reordered or re-packed vertex stream passes that and draws
                    // differently. The cook runs this same parser, so anything but equality here is
                    // the writer or the reader losing information.
                    if (!a.Mesh.VertexBytes.AsSpan().SequenceEqual(b.Mesh.VertexBytes))
                        objMismatches.Add($"[{i}] vertex bytes differ");
                    if (!a.Mesh.Indices.AsSpan().SequenceEqual(b.Mesh.Indices))
                        objMismatches.Add($"[{i}] indices differ");
                }

                t.Expect("a cooked OBJ part-for-part matches the parsed one",
                    objMismatches.Count == 0, string.Join("; ", objMismatches.Take(5)));

                // And the reports say which path each took, which is the whole of what the judge reads.
                var wasOnObj = AssetLoadLog.Enabled;
                try
                {
                    AssetLoadLog.Start();
                    WavefrontParts.Import(objAsset);
                    WavefrontParts.Import(loneObj);
                    var objReports = AssetLoadLog.Drain();

                    t.Expect("the cooked OBJ reports Cooked",
                        objReports.SingleOrDefault(r => r.SourcePath == objAsset) is { Mode: AssetLoadMode.Cooked },
                        "no Cooked report");
                    var lonely = objReports.SingleOrDefault(r => r.SourcePath == loneObj);
                    t.Expect("and one with no sibling reports Source, saying why",
                        lonely is { Mode: AssetLoadMode.Source }
                        && lonely.Warning?.Contains("no .blixmesh sibling", StringComparison.Ordinal) == true,
                        lonely?.Warning ?? "no report");

                    // <b>The settings guard.</b> The cook stamps recenter=1; this reader is asked
                    // for the opposite. A loader that ignored the stamp would hand back geometry
                    // shifted by half a bounding box — on machines that had cooked and nowhere
                    // else, which is the shape of bug the whole stamp exists to prevent.
                    AssetLoadLog.Start();
                    new ObjImporter { RecenterToOrigin = false }
                        .Import(new AssetImportContext(AssetId.Parse("t/obj-uncentred"), objAsset));
                    var guarded = AssetLoadLog.Drain().SingleOrDefault(r => r.SourcePath == objAsset);
                    t.Expect("a reader wanting other settings refuses the cooked file",
                        guarded is { Mode: AssetLoadMode.Source }
                        && guarded.Warning?.Contains("recenter=0", StringComparison.Ordinal) == true,
                        guarded?.Warning ?? "no report");

                    // <b>A cooked OBJ whose material library names no texture owes NOTHING.</b>
                    // This kit's .mtl files carry colour and no map_ line at all, and the recipe
                    // declared SourceRequired | SourceRequiredForImagesOnly on every file anyway —
                    // claiming its source was needed for image bytes that do not exist. The same
                    // overstatement K-F removed from the mesh cook, in the one recipe that is not
                    // Blix's, and the reason a flag nobody checks drifts.
                    var objHeader = CookedFile.TryReadHeader(Path.ChangeExtension(objAsset, ".blixmesh"));
                    t.Expect("a cooked OBJ with no textures declares nothing owed",
                        objHeader?.Stamp.Flags == CookedFlags.None,
                        $"flags = {objHeader?.Stamp.Flags.ToString() ?? "no header"}");
                }
                finally
                {
                    AssetLoadLog.Enabled = wasOnObj;
                    AssetLoadLog.Drain();
                }
            }
            finally
            {
                try { Directory.Delete(objTemp, recursive: true); } catch (IOException) { }
            }
        }

        // ── two declarations for one output are refused ─────────────────────
        // <b>A cooked path is derived from the source's NAME, so settings are not in it.</b> Two
        // BlixCook items for one source differing only in Options therefore both cook and both
        // write the same file — demonstrated by declaring Villager.obj at recenter=0 and recenter=1
        // and watching two "cooked omsh" lines produce one artifact, no warning, last one winning.
        // The consumer whose settings lost then has its cooked file refused by the loader's guard
        // and walks the source forever, traceable to nothing.
        //
        // Refusing is this tree's standing answer to ambiguity — BlixRecipes.For returns null
        // rather than guessing between two recipes that accept one extension — and it is the answer
        // that needs no design. Putting settings in the path would be one, and nothing is asking.
        var tab = "\t";
        var collidingBatch = new[]
        {
            $"omsh{tab}/a/Villager.obj{tab}/a/Villager.blixmesh{tab}recenter=0",
            $"omsh{tab}/a/Villager.obj{tab}/a/Villager.blixmesh{tab}recenter=1",
        };
        var collisions = Blix.Tools.Cook.Program.FindOutputCollisions(collidingBatch).ToArray();
        t.Expect("two declarations writing one file are caught", collisions.Length == 1,
            $"{collisions.Length} collision(s)");
        t.ExpectTrue("and the message names the output",
            collisions.Length == 1 && collisions[0].Contains("/a/Villager.blixmesh", StringComparison.Ordinal));
        t.ExpectTrue("and both claimants, with what differs",
            collisions.Length == 1
            && collisions[0].Contains("recenter=0", StringComparison.Ordinal)
            && collisions[0].Contains("recenter=1", StringComparison.Ordinal));

        // The control: distinct outputs are not a collision, or the check would refuse every build.
        var fineBatch = new[]
        {
            $"omsh{tab}/a/Villager.obj{tab}/a/Villager.blixmesh{tab}recenter=0",
            $"omsh{tab}/a/Bush_1.obj{tab}/a/Bush_1.blixmesh{tab}recenter=1",
            string.Empty,
            "malformed line with no tabs",
        };
        t.Expect("distinct outputs are not a collision",
            !Blix.Tools.Cook.Program.FindOutputCollisions(fineBatch).Any(), "reported one anyway");

        // ── a texture's identity is the same across independent loads ───────
        // <b>Every upload cache in this tree is keyed by the OBJECT, so nothing can be shared.</b>
        // GltfTexture is a class with no value equality — its own comment says the cost "nothing
        // relied on" — so two imports of one file produce two instances and upload the same pixels
        // twice, by construction. Three owners hand-roll that key: the engine's GltfTextureLoader,
        // the studio, and VulkanSponza.
        //
        // ResourceId is the fix's foundation, and the only thing worth asserting about it is that
        // two loads AGREE. An identity that differs per load is not an identity; it is the object
        // reference again, spelled as a string.
        var idAsset = FindFile("Rogue.glb");
        if (idAsset is null)
        {
            t.Fail("an asset with textures is findable", "no Rogue.glb under the repo");
        }
        else
        {
            var firstLoad = new Blix.GltfImporter()
                .Import(new AssetImportContext(AssetId.Parse("t/id-1"), idAsset));
            var secondLoad = new Blix.GltfImporter()
                .Import(new AssetImportContext(AssetId.Parse("t/id-2"), idAsset));

            static string[] Ids(Blix.GltfModel m) => m.Primitives
                .Select(p => p.Material?.BaseColorTexture)
                .Where(x => x is not null)
                .Select(x => x!.ResourceId)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(x => x, StringComparer.Ordinal)
                .ToArray();

            var first = Ids(firstLoad);
            var second = Ids(secondLoad);

            t.Expect("a textured load yields identities at all", first.Length > 0, $"{first.Length}");
            t.ExpectTrue("none of them is empty", first.All(x => x.Length > 0));
            t.Expect("two independent loads agree on every identity",
                first.SequenceEqual(second, StringComparer.Ordinal),
                $"[{string.Join(", ", first)}] vs [{string.Join(", ", second)}]");

            // And the objects do NOT compare equal — which is the defect the identity exists to
            // route around, asserted so the two facts stay visibly separate.
            var a = firstLoad.Primitives.Select(p => p.Material?.BaseColorTexture).First(x => x is not null);
            var b = secondLoad.Primitives.Select(p => p.Material?.BaseColorTexture).First(x => x is not null);
            t.ExpectTrue("while the objects themselves are still distinct", !ReferenceEquals(a, b));
        }

        // ── the registry shares by identity, and only where it should ───────
        // <b>No GPU here, and none needed.</b> What is worth asserting is the KEYING — that two
        // objects with one identity upload once, that one identity at two formats uploads twice,
        // and that an unidentified texture is never shared with anything. The upload itself is a
        // delegate, so counting how often it runs is the whole test.
        var uploads = 0;
        var registry = new Blix.TextureRegistry();
        Blix.Graphics.TextureHandle Fake() { uploads++; return default; }

        var pixels = new byte[] { 1, 2, 3, 4 };
        var sameA = Blix.GltfTexture.Rgba8Single("a", pixels, 1, 1, "/assets/x.png");
        var sameB = Blix.GltfTexture.Rgba8Single("a-again", pixels, 1, 1, "/assets/x.png");

        registry.GetOrAdd(sameA, Blix.Graphics.TextureFormat.Rgba8Srgb, Fake);
        registry.GetOrAdd(sameB, Blix.Graphics.TextureFormat.Rgba8Srgb, Fake);
        t.Expect("two objects with one identity upload once", uploads == 1, $"{uploads} uploads");
        t.Expect("and the saving is reported", registry.SharedUploads == 1, $"{registry.SharedUploads}");

        // The same picture as a normal map is a DIFFERENT GPU texture — sRGB versus linear. Keying
        // on identity alone would hand a shader the wrong colour space.
        registry.GetOrAdd(sameB, Blix.Graphics.TextureFormat.Rgba8, Fake);
        t.Expect("one identity at two formats uploads twice", uploads == 2, $"{uploads} uploads");

        // Unidentified textures keep the old behaviour exactly: deduplicated per object, never
        // shared with anything else, and never counted as resident bytes.
        var anonA = Blix.GltfTexture.Rgba8Single("anon", pixels, 1, 1);
        var anonB = Blix.GltfTexture.Rgba8Single("anon", pixels, 1, 1);
        registry.GetOrAdd(anonA, Blix.Graphics.TextureFormat.Rgba8Srgb, Fake);
        registry.GetOrAdd(anonA, Blix.Graphics.TextureFormat.Rgba8Srgb, Fake);
        registry.GetOrAdd(anonB, Blix.Graphics.TextureFormat.Rgba8Srgb, Fake);
        t.Expect("an unidentified texture dedups by object but shares with nobody",
            uploads == 4, $"{uploads} uploads");
        t.Expect("and is counted as unidentified", registry.UnidentifiedCount == 2,
            $"{registry.UnidentifiedCount}");

        // The residency number D5-c needs, covering the identified entries only.
        t.Expect("resident bytes cover the identified textures", registry.ResidentBytes > 0,
            $"{registry.ResidentBytes} bytes");

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

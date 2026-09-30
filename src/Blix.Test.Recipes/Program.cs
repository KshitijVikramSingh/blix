using Blix.Import;
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
    /// <summary>Generated tangents held to authored ones: strip, regenerate, compare corner by corner.</summary>
    /// <returns>(corners compared, direction agreeing within 8 degrees, handedness agreeing).</returns>
    /// <param name="mirrorV">The control: regenerate from v mirrored, which a convention check must catch.</param>
    internal static (int Corners, int Direction, int Handedness) CompareWithAuthored(string asset, bool mirrorV = false)
    {
        var model = SharpGLTF.Schema2.ModelRoot.Load(asset);
        int corners = 0, direction = 0, handedness = 0;
        foreach (var mesh in model.LogicalMeshes)
        foreach (var prim in mesh.Primitives)
        {
            if (prim.GetVertexAccessor("TANGENT") is null || prim.GetVertexAccessor("TEXCOORD_0") is null) continue;
            var authored = Blix.Import.GltfStaticImporter.BuildStaticMeshData(
                mesh.Name ?? "m", prim, System.Numerics.Matrix4x4.Identity, System.Numerics.Matrix4x4.Identity,
                includeTangents: true);
            var stripped = (byte[])authored.VertexBytes.Clone();
            for (var v = 0; v < authored.VertexCount; v++)
            {
                Array.Clear(stripped, (v * 48) + 24, 16);
                if (mirrorV) BitConverter.TryWriteBytes(stripped.AsSpan((v * 48) + 44, 4), 1f - BitConverter.ToSingle(stripped, (v * 48) + 44));
            }
            var generated = TangentGeneration.Generate(authored with { VertexBytes = stripped });

            var before = authored.Indices32 ?? authored.Indices.Select(i => (uint)i).ToArray();
            var after = generated.Indices32 ?? generated.Indices.Select(i => (uint)i).ToArray();
            for (var c = 0; c < before.Length; c++)
            {
                var a = Tangent(authored.VertexBytes, before[c]);
                var g = Tangent(generated.VertexBytes, after[c]);
                corners++;
                if (System.Numerics.Vector3.Dot(Vector3Of(a), Vector3Of(g)) > MathF.Cos(8f * MathF.PI / 180f)) direction++;
                if (MathF.Sign(a.W) == MathF.Sign(g.W)) handedness++;
            }
        }

        return (corners, direction, handedness);

        static System.Numerics.Vector4 Tangent(byte[] bytes, uint vertex)
        {
            var o = ((int)vertex * 48) + 24;
            return new System.Numerics.Vector4(
                BitConverter.ToSingle(bytes, o), BitConverter.ToSingle(bytes, o + 4),
                BitConverter.ToSingle(bytes, o + 8), BitConverter.ToSingle(bytes, o + 12));
        }

        static System.Numerics.Vector3 Vector3Of(System.Numerics.Vector4 v) =>
            System.Numerics.Vector3.Normalize(new System.Numerics.Vector3(v.X, v.Y, v.Z));
    }

    // Per triangle corner, because the cook's tangent weld renumbers vertices; floats to a hair (the
    // cooked path unbakes a node transform the source applied once), the packed colour exactly.
    private static bool SameCorners(Blix.Assets.MeshData a, Blix.Assets.MeshData b, int colourAt)
    {
        var ia = a.Indices32 ?? a.Indices.Select(i => (uint)i).ToArray();
        var ib = b.Indices32 ?? b.Indices.Select(i => (uint)i).ToArray();
        if (ia.Length != ib.Length || a.Layout.Stride != b.Layout.Stride) return false;
        var stride = a.Layout.Stride;
        for (var c = 0; c < ia.Length; c++)
        {
            var va = (int)ia[c] * stride;
            var vb = (int)ib[c] * stride;
            for (var at = 0; at < stride; at += 4)
            {
                if (at == colourAt)
                {
                    if (BitConverter.ToUInt32(a.VertexBytes, va + at) != BitConverter.ToUInt32(b.VertexBytes, vb + at)) return false;
                    continue;
                }

                if (Math.Abs(BitConverter.ToSingle(a.VertexBytes, va + at) - BitConverter.ToSingle(b.VertexBytes, vb + at)) > 1e-4f) return false;
            }
        }

        return true;
    }

    // A cooked file read by the engine (ModelData), in the shapes the cook's importer returns for a
    // source, so a cooked-versus-source comparison compares like with like. Test-side only: the
    // engine has one reader, and it is ModelData.
    private static Blix.Import.GltfNodeModel CookedNodes(string blixmesh, bool tangents = false, bool colour = false)
    {
        var d = Blix.ModelData.Load(blixmesh, new Blix.ModelNeeds(tangents, colour, Skinned: false));
        return new Blix.Import.GltfNodeModel(d.Nodes.Select(n => new Blix.Import.GltfNode(
            n.Name, n.ParentIndex, n.Local,
            n.MeshIndex < 0
                ? Array.Empty<Blix.Import.GltfPrimitive>()
                : d.Meshes[n.MeshIndex].Primitives
                    .Select(p => new Blix.Import.GltfPrimitive(p.Mesh, p.Material, MaterialIndex: p.MaterialIndex)).ToArray())).ToArray());
    }

    private static Blix.Import.GltfModel CookedModel(string blixmesh, bool tangents = false, bool colour = false)
    {
        var d = Blix.ModelData.Load(blixmesh, new Blix.ModelNeeds(tangents, colour, Skinned: true));
        if (d.IsRigged)
        {
            var skinned = Enumerable.Range(0, d.Skins.Count)
                .SelectMany(s => d.SkinnedPrimitives(s).Select(p => new Blix.Import.GltfPrimitive(p.Mesh, p.Material, SkinIndex: s, MaterialIndex: p.MaterialIndex)))
                .ToArray();
            return new Blix.Import.GltfModel(skinned, d.Skins[0].Skeleton, d.Clips.ToArray(), d.Placement(0));
        }

        return CookedFlat(blixmesh, tangents, colour);
    }

    // Every placed primitive in world space, a rigged file's skinned meshes at bind pose: the flat
    // static reading the source importer's Import gives.
    private static Blix.Import.GltfModel CookedFlat(string blixmesh, bool tangents = false, bool colour = false)
    {
        var flat = Blix.ModelData.Load(blixmesh, new Blix.ModelNeeds(tangents, colour, Skinned: false));
        return new Blix.Import.GltfModel(
            flat.Flattened().Select(x => new Blix.Import.GltfPrimitive(x.Primitive.Mesh, x.Primitive.Material)).ToArray(),
            new Skeleton(Array.Empty<Bone>()), Array.Empty<AnimationClip>(), System.Numerics.Matrix4x4.Identity);
    }

    private static void ModelDataReadings(TestRunner t)
    {
        t.ExpectThrows<AssetImportException>("ModelData.Load refuses a source model, naming the cook",
            () => Blix.ModelData.Load("some/asset.glb"));

        var rogue = FindFile("Rogue.glb");
        var morph = FindFile("AnimatedMorphCube.glb");
        var multiUv = FindFile("MultiUVTest.gltf");
        if (rogue is null || morph is null || multiUv is null)
        {
            Console.WriteLine("  --   ModelData checks skipped: Rogue.glb, AnimatedMorphCube.glb or MultiUVTest.gltf not found");
            return;
        }

        // One cooked rig, two readings: skinned vertices for a rig view, bind-pose static ones for a
        // model view, over the same scene graph.
        var rig = Path.ChangeExtension(rogue, ".blixmesh");
        var skinned = Blix.ModelData.Load(rig, new Blix.ModelNeeds(Skinned: true));
        var asStatic = Blix.ModelData.Load(rig, new Blix.ModelNeeds(Colour: true, Skinned: false));
        t.Expect("a rigged file read skinned keeps its skinned meshes skinned",
            skinned.IsRigged && skinned.Meshes.Any(m => m.Skinned && m.Primitives.All(p => p.Mesh.Layout.Stride == 80)),
            string.Join(",", skinned.Meshes.SelectMany(m => m.Primitives).Select(p => p.Mesh.Layout.Stride).Distinct()));
        t.Expect("and read static, the same meshes arrive as static geometry at bind pose",
            asStatic.Meshes.All(m => !m.Skinned) && asStatic.Meshes.SelectMany(m => m.Primitives).All(p => p.Mesh.Layout.Stride == 44),
            string.Join(",", asStatic.Meshes.SelectMany(m => m.Primitives).Select(p => p.Mesh.Layout.Stride).Distinct()));
        t.Expect("over one scene graph", skinned.Nodes.Count == asStatic.Nodes.Count && skinned.Skins.Count == asStatic.Skins.Count,
            $"{skinned.Nodes.Count} vs {asStatic.Nodes.Count} nodes");

        // What the cook did not carry is recorded in the file rather than lost with the source.
        var temp = Path.Combine(Path.GetTempPath(), "blix-ignored-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            var cube = Path.Combine(temp, "AnimatedMorphCube.glb");
            File.Copy(morph, cube);
            MeshRecipe.CookToBlixMesh(cube, Path.ChangeExtension(cube, ".blixmesh"));
            var morphs = Blix.ModelData.Load(Path.ChangeExtension(cube, ".blixmesh")).Ignored;
            t.Expect("a cooked file records the source attributes its cook did not carry (morph targets)",
                morphs.Any(i => i.Semantic == Blix.UnreadAttribute.MorphTargets), string.Join(",", morphs.Select(i => i.Semantic)));

            var uv = Path.Combine(temp, "MultiUVTest.gltf");
            foreach (var f in Directory.EnumerateFiles(Path.GetDirectoryName(multiUv)!)) File.Copy(f, Path.Combine(temp, Path.GetFileName(f)), true);
            MeshRecipe.CookToBlixMesh(uv, Path.ChangeExtension(uv, ".blixmesh"));
            var none = Blix.ModelData.Load(Path.ChangeExtension(uv, ".blixmesh")).Ignored;
            t.Expect("CONTROL and records nothing where it read everything (two UV sets are carried)",
                none.Count == 0, string.Join(",", none.Select(i => i.Semantic)));
        }
        finally
        {
            try { Directory.Delete(temp, recursive: true); } catch (IOException) { }
        }
    }

    private static void CookOnOpen(TestRunner t)
    {
        var rogue = FindFile("Rogue.glb");
        var multiUv = FindFile("MultiUVTest.gltf");
        if (rogue is null || multiUv is null)
        {
            Console.WriteLine("  --   cook-on-open checks skipped: Rogue.glb or MultiUVTest.gltf not found");
            return;
        }

        var cache = Path.Combine(Path.GetTempPath(), "blix-cache-" + Guid.NewGuid().ToString("N"));
        var work = Path.Combine(Path.GetTempPath(), "blix-open-" + Guid.NewGuid().ToString("N"));
        var before = Environment.GetEnvironmentVariable("BLIX_COOK_CACHE");
        Directory.CreateDirectory(work);
        try
        {
            Environment.SetEnvironmentVariable("BLIX_COOK_CACHE", cache);
            foreach (var f in Directory.EnumerateFiles(Path.GetDirectoryName(multiUv)!))
                File.Copy(f, Path.Combine(work, Path.GetFileName(f)));
            var raw = Path.Combine(work, "MultiUVTest.gltf");

            var opened = CookCache.Resolve(raw);
            t.Expect("a raw model with no cooked sibling cooks into the cache",
                opened.StartsWith(cache, StringComparison.Ordinal) && File.Exists(opened), opened);
            t.ExpectTrue("and the cache entry stands alone: its images are cooked beside it",
                CookedFile.TryReadHeader(opened)?.Stamp.Flags == CookedFlags.None);
            var written = File.GetLastWriteTimeUtc(opened);
            t.Expect("a second open reuses the entry rather than cooking again",
                CookCache.Resolve(raw) == opened && File.GetLastWriteTimeUtc(opened) == written, "re-cooked");
            t.Expect("a .blixmesh resolves to itself", CookCache.Resolve(opened) == opened, CookCache.Resolve(opened));

            // Rogue ships a rig sibling, and one cooked file answers both readings: the scene graph a
            // rig is, and the static hierarchy a model view wants.
            var rigSibling = Path.ChangeExtension(rogue, ".blixmesh");
            t.Expect("a current cooked sibling is used as-is", CookCache.Resolve(rogue) == rigSibling, CookCache.Resolve(rogue));
            var asModel = CookedNodes(rigSibling, colour: true);
            t.Expect("and the same cooked rig reads as a model, its skinned meshes at bind pose",
                BlixMeshReader.Read(rigSibling).IsRigged && asModel.Nodes.Any(n => n.Primitives.Length > 0),
                $"{asModel.Nodes.Length} node(s)");

            var broken = Path.Combine(work, "broken.gltf");
            File.WriteAllText(broken, "{ \"asset\": { \"version\": \"2.0\" }, \"meshes\": [ { \"primitives\": [ { \"attributes\": { } } ] } ], \"nodes\": [ { \"mesh\": 0 } ] }");
            var refusal = t.ExpectThrows<AssetImportException>("a model the cook refuses is refused as AssetImportException",
                () => CookCache.Resolve(broken));
            t.Expect("naming the file", refusal?.Message.Contains("broken.gltf", StringComparison.Ordinal) == true, refusal?.Message ?? "(none)");

            // ORM packing: glTF's own case of one image in two channels (R occlusion, G roughness,
            // B metallic). The cook takes it as metallic-roughness rather than refusing it.
            var orm = Path.Combine(work, "orm.gltf");
            File.Copy(Path.Combine(work, "uv0.png"), Path.Combine(work, "orm.png"));
            File.WriteAllText(orm, """
                {
                  "asset": { "version": "2.0" },
                  "images": [ { "uri": "orm.png" } ],
                  "textures": [ { "source": 0 } ],
                  "materials": [ {
                    "pbrMetallicRoughness": { "metallicRoughnessTexture": { "index": 0 } },
                    "occlusionTexture": { "index": 0 }
                  } ]
                }
                """);
            t.ExpectTrue("an image packing occlusion with metallic-roughness cooks as metallic-roughness",
                MeshRecipe.ReferencedImages(orm).SequenceEqual(new[] { new MeshRecipe.ReferencedImage("orm.png", TextureRole.MetallicRoughness) }));
        }
        finally
        {
            Environment.SetEnvironmentVariable("BLIX_COOK_CACHE", before);
            try { Directory.Delete(cache, recursive: true); } catch (IOException) { }
            try { Directory.Delete(work, recursive: true); } catch (IOException) { }
        }
    }

    private static void AuthoredMaterialReachesBothPaths(TestRunner t)
    {
        var temp = Path.Combine(Path.GetTempPath(), "blix-authored-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            // One indexed triangle: POSITION, NORMAL, TEXCOORD_0, TEXCOORD_1, three vertices each.
            var floats = new float[]
            {
                0, 0, 0, 1, 0, 0, 0, 1, 0,          // POSITION
                0, 0, 1, 0, 0, 1, 0, 0, 1,          // NORMAL
                0, 0, 1, 0, 0, 1,                   // TEXCOORD_0
                0, 0, 1, 0, 0, 1,                   // TEXCOORD_1
            };
            var bytes = new byte[floats.Length * 4 + 12];
            Buffer.BlockCopy(floats, 0, bytes, 0, floats.Length * 4);
            Buffer.BlockCopy(new uint[] { 0, 1, 2 }, 0, bytes, floats.Length * 4, 12);
            File.WriteAllBytes(Path.Combine(temp, "tri.bin"), bytes);
            // One image per channel: a cooked image carries one channel's role (sRGB or linear).
            foreach (var channel in new[] { "base", "normal", "mr", "occlusion", "emissive" })
            {
                Blix.Graphics.Images.PngWriter.WriteRgba8(
                    Path.Combine(temp, channel + ".png"), Enumerable.Repeat((byte)128, 4 * 4 * 4).ToArray(), 4, 4);
            }

            var gltf = Path.Combine(temp, "authored.gltf");
            File.WriteAllText(gltf, """
                {
                  "asset": { "version": "2.0" },
                  "scene": 0, "scenes": [ { "nodes": [ 0 ] } ],
                  "nodes": [ { "mesh": 0 } ],
                  "meshes": [ { "primitives": [ { "attributes": { "POSITION": 0, "NORMAL": 1, "TEXCOORD_0": 2, "TEXCOORD_1": 3 }, "indices": 4, "material": 0 } ] } ],
                  "buffers": [ { "uri": "tri.bin", "byteLength": 132 } ],
                  "bufferViews": [
                    { "buffer": 0, "byteOffset": 0, "byteLength": 36 },
                    { "buffer": 0, "byteOffset": 36, "byteLength": 36 },
                    { "buffer": 0, "byteOffset": 72, "byteLength": 24 },
                    { "buffer": 0, "byteOffset": 96, "byteLength": 24 },
                    { "buffer": 0, "byteOffset": 120, "byteLength": 12 }
                  ],
                  "accessors": [
                    { "bufferView": 0, "componentType": 5126, "count": 3, "type": "VEC3", "min": [ 0, 0, 0 ], "max": [ 1, 1, 0 ] },
                    { "bufferView": 1, "componentType": 5126, "count": 3, "type": "VEC3" },
                    { "bufferView": 2, "componentType": 5126, "count": 3, "type": "VEC2" },
                    { "bufferView": 3, "componentType": 5126, "count": 3, "type": "VEC2" },
                    { "bufferView": 4, "componentType": 5125, "count": 3, "type": "SCALAR" }
                  ],
                  "images": [ { "uri": "base.png" }, { "uri": "normal.png" }, { "uri": "mr.png" }, { "uri": "occlusion.png" }, { "uri": "emissive.png" } ],
                  "textures": [ { "source": 0 }, { "source": 1 }, { "source": 2 }, { "source": 3 }, { "source": 4 } ],
                  "materials": [ {
                    "name": "authored",
                    "pbrMetallicRoughness": {
                      "baseColorTexture": { "index": 0, "texCoord": 1 },
                      "metallicRoughnessTexture": { "index": 2, "texCoord": 0 }
                    },
                    "normalTexture": { "index": 1, "texCoord": 1, "scale": 0.5 },
                    "occlusionTexture": { "index": 3, "texCoord": 1, "strength": 0.25 },
                    "emissiveTexture": { "index": 4, "texCoord": 0 },
                    "emissiveFactor": [ 1, 1, 1 ]
                  } ]
                }
                """);

            var fromSource = new Blix.Import.GltfStaticImporter()
                .Import(new AssetImportContext(AssetId.Parse("t/authored-src"), gltf)).Primitives[0].Material;
            var cooked = Path.ChangeExtension(gltf, ".blixmesh");
            MeshRecipe.CookToBlixMesh(gltf, cooked);
            var fromCooked = CookedModel(cooked).Primitives[0].Material;

            foreach (var (path, m) in new[] { ("source", fromSource), ("cooked", fromCooked) })
            {
                t.Expect($"K-F.2 the {path} path reads the authored texture-coordinate sets",
                    m is { BaseColorTexCoord: 1, NormalTexCoord: 1, MetallicRoughnessTexCoord: 0,
                        OcclusionTexCoord: 1, EmissiveTexCoord: 0 },
                    m is null ? "no material" :
                        $"base {m.BaseColorTexCoord}, normal {m.NormalTexCoord}, mr {m.MetallicRoughnessTexCoord}, " +
                        $"occlusion {m.OcclusionTexCoord}, emissive {m.EmissiveTexCoord}");
                t.Expect($"K-F.2 the {path} path reads the authored normal scale and occlusion strength",
                    m is { NormalScale: 0.5f, OcclusionStrength: 0.25f },
                    m is null ? "no material" : $"scale {m.NormalScale}, strength {m.OcclusionStrength}");
            }
        }
        finally
        {
            try { Directory.Delete(temp, recursive: true); } catch (IOException) { }
        }
    }

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

        // This assembly is deliberately not referenced at runtime. It is built as another
        // project's recipe library, indexed from metadata, and found by the cook host through the
        // checkout. That is the mechanism an extracted game relies on.
        var catalog = Blix.Tools.Cook.RecipeCatalog.All();
        t.ExpectTrue("the cook host discovers a project-owned recipe from its generated index",
            catalog.Any(r => r.Id == "tst1"));
        t.ExpectTrue("the foreign recipe keeps its declared source and output contract",
            catalog.Any(r => r.Id == "tst1"
                && r.Consumes.SequenceEqual(new[] { ".fixture" })
                && r.Produces == ".blixfixture"));

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

        // ── asset packaging keeps material-channel texture roles ───────────
        // Image references retain the material role that controls encoding. One logical image used
        // by incompatible channels cannot become one correctly described .blixtex, so the recipe
        // refuses that ambiguity before parallel cooks can race or silently pick one role.
        var referenceTemp = Path.Combine(Path.GetTempPath(), $"blix-references-{Guid.NewGuid():N}");
        Directory.CreateDirectory(referenceTemp);
        try
        {
            var image = Path.Combine(referenceTemp, "shared.png");
            var png = Convert.FromBase64String(
                "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAusB9Wl2nWQAAAAASUVORK5CYII=");
            File.WriteAllBytes(image, png);
            File.WriteAllBytes(Path.Combine(referenceTemp, "normal.png"), png);
            var gltf = Path.Combine(referenceTemp, "roles.gltf");
            File.WriteAllText(gltf, """
                {
                  "asset": { "version": "2.0" },
                  "images": [ { "uri": "shared.png" }, { "uri": "normal.png" } ],
                  "textures": [ { "source": 0 }, { "source": 1 } ],
                  "materials": [ {
                    "pbrMetallicRoughness": { "baseColorTexture": { "index": 0 } },
                    "normalTexture": { "index": 1 }
                  } ]
                }
                """);

            var references = MeshRecipe.ReferencedImages(gltf);
            t.ExpectTrue("material reference discovery retains the base-colour role",
                references.Contains(new MeshRecipe.ReferencedImage("shared.png", TextureRole.BaseColor)));
            t.ExpectTrue("and retains the normal role",
                references.Contains(new MeshRecipe.ReferencedImage("normal.png", TextureRole.Normal)));
            t.Expect("while the URI-only compatibility view remains complete",
                MeshRecipe.ReferencedImageUris(gltf).Count == 2,
                $"{MeshRecipe.ReferencedImageUris(gltf).Count}");

            var textureOut = Path.Combine(referenceTemp, "explicit-role.blixtex");
            var oldFormat = Environment.GetEnvironmentVariable("BLIX_COOK_FORMAT");
            try
            {
                Environment.SetEnvironmentVariable("BLIX_COOK_FORMAT", "rgba8");
                TextureRecipe.CookOne(
                    image, textureOut, out _, out _, TextureRole.Normal);
                t.ExpectTrue("the exact texture source, role and encoder identity is current",
                    TextureRecipe.IsCurrent(image, textureOut, TextureRole.Normal));
                t.ExpectTrue("a different material role invalidates the texture artifact",
                    !TextureRecipe.IsCurrent(image, textureOut, TextureRole.BaseColor));
                Environment.SetEnvironmentVariable("BLIX_COOK_FORMAT", "bc7");
                t.ExpectTrue("a different encoder environment invalidates the texture artifact",
                    !TextureRecipe.IsCurrent(image, textureOut, TextureRole.Normal));
            }
            finally
            {
                Environment.SetEnvironmentVariable("BLIX_COOK_FORMAT", oldFormat);
            }
            var textureStamp = CookedFile.ReadHeader(textureOut).Stamp;
            t.ExpectTrue("a texture stamp records its explicit material role",
                textureStamp.Parameters.Contains("role=Normal", StringComparison.Ordinal));
            t.ExpectTrue("and records the concrete encoder path and quality",
                textureStamp.Parameters.Contains("encoder=raw quality=none", StringComparison.Ordinal));

            var conflict = Path.Combine(referenceTemp, "conflict.gltf");
            File.WriteAllText(conflict, """
                {
                  "asset": { "version": "2.0" },
                  "images": [ { "uri": "shared.png" } ],
                  "textures": [ { "source": 0 } ],
                  "materials": [ {
                    "pbrMetallicRoughness": { "baseColorTexture": { "index": 0 } },
                    "normalTexture": { "index": 0 }
                  } ]
                }
                """);
            t.ExpectThrows<InvalidDataException>(
                "one logical image cannot claim incompatible material roles",
                () => MeshRecipe.ReferencedImages(conflict));

            // ── a patch states a normal map's convention, and the cook flips the image once ──
            // Nothing in a file says a normal map is DirectX-convention (green down); Sponza ships 24
            // of 30 that way inside glTF, which specifies green up. The patch declares it per
            // material, and the cooked map comes out in glTF's convention.
            var named = Path.Combine(referenceTemp, "named.gltf");
            File.WriteAllText(named, """
                {
                  "asset": { "version": "2.0" },
                  "images": [ { "uri": "shared.png" }, { "uri": "normal.png" } ],
                  "textures": [ { "source": 0 }, { "source": 1 } ],
                  "materials": [ {
                    "name": "stone",
                    "pbrMetallicRoughness": { "baseColorTexture": { "index": 0 } },
                    "normalTexture": { "index": 1 }
                  } ]
                }
                """);
            var directXPatch = Path.Combine(referenceTemp, "directx.blixpatch");
            File.WriteAllText(directXPatch, "material stone{1} normal=directx\n");
            var flipped = MeshRecipe.ReferencedImages(named, MaterialPatch.Load(directXPatch));
            t.ExpectTrue("a normal map its patch declares directx is referenced for a green flip",
                flipped.Contains(new MeshRecipe.ReferencedImage("normal.png", TextureRole.Normal, FlipGreen: true)));
            t.ExpectTrue("and the material's other images are not",
                flipped.Contains(new MeshRecipe.ReferencedImage("shared.png", TextureRole.BaseColor)));
            t.ExpectTrue("while without a patch the map is read as glTF's convention",
                MeshRecipe.ReferencedImages(named).Contains(new MeshRecipe.ReferencedImage("normal.png", TextureRole.Normal)));

            var openGlPatch = Path.Combine(referenceTemp, "opengl.blixpatch");
            File.WriteAllText(openGlPatch, "material stone{1} normal=directx\nmaterial stone normal=opengl\n");
            t.ExpectTrue("the last rule to state a material's convention wins",
                MeshRecipe.ReferencedImages(named, MaterialPatch.Load(openGlPatch))
                    .Contains(new MeshRecipe.ReferencedImage("normal.png", TextureRole.Normal)));

            var badValue = Path.Combine(referenceTemp, "bad.blixpatch");
            File.WriteAllText(badValue, "material stone normal=upside\n");
            t.ExpectThrows<InvalidDataException>("a convention other than directx or opengl is refused",
                () => MeshRecipe.ReferencedImages(named, MaterialPatch.Load(badValue)));

            var sharedNormal = Path.Combine(referenceTemp, "shared-normal.gltf");
            File.WriteAllText(sharedNormal, """
                {
                  "asset": { "version": "2.0" },
                  "images": [ { "uri": "normal.png" } ],
                  "textures": [ { "source": 0 } ],
                  "materials": [
                    { "name": "stone", "normalTexture": { "index": 0 } },
                    { "name": "plaster", "normalTexture": { "index": 0 } }
                  ]
                }
                """);
            t.ExpectThrows<InvalidDataException>(
                "one normal image declared directx by one material and not by another is refused",
                () => MeshRecipe.ReferencedImages(sharedNormal, MaterialPatch.Load(directXPatch)));

            var asCooked = Path.Combine(referenceTemp, "normal-as-is.blixtex");
            var greenFlipped = Path.Combine(referenceTemp, "normal-flipped.blixtex");
            var formatBefore = Environment.GetEnvironmentVariable("BLIX_COOK_FORMAT");
            try
            {
                Environment.SetEnvironmentVariable("BLIX_COOK_FORMAT", "rgba8");
                var normalPng = Path.Combine(referenceTemp, "normal.png");
                TextureRecipe.CookOne(normalPng, asCooked, out _, out _, TextureRole.Normal);
                TextureRecipe.CookOne(normalPng, greenFlipped, out _, out _, TextureRole.Normal, flipGreen: true);
                var plain = Blix.Graphics.Images.BlixTexReader.Read(asCooked).MipBytes[0];
                var inverted = Blix.Graphics.Images.BlixTexReader.Read(greenFlipped).MipBytes[0];
                t.Expect("the flipped cook inverts green and only green",
                    inverted[0] == plain[0] && inverted[1] == 255 - plain[1] && inverted[2] == plain[2],
                    $"as cooked {plain[0]},{plain[1]},{plain[2]}; flipped {inverted[0]},{inverted[1]},{inverted[2]}");
                t.ExpectTrue("a flipped texture is current only for a flipped cook",
                    TextureRecipe.IsCurrent(normalPng, greenFlipped, TextureRole.Normal, flipGreen: true)
                    && !TextureRecipe.IsCurrent(normalPng, greenFlipped, TextureRole.Normal));
                t.ExpectThrows<InvalidDataException>("a green flip on anything but a normal map is refused",
                    () => TextureRecipe.CookOne(normalPng, greenFlipped, out _, out _, TextureRole.BaseColor, flipGreen: true));
            }
            finally
            {
                Environment.SetEnvironmentVariable("BLIX_COOK_FORMAT", formatBefore);
            }

            // ── the convention measure is held to a map whose convention is known by construction ──
            // A height field's gradient, written as an OpenGL (green up) normal map, must read opengl;
            // the same map with green inverted must read directx; a flat map must read unclear rather
            // than pick a side.
            const int side = 128;
            var known = new byte[side * side * 4];
            for (var row = 0; row < side; row++)
            for (var col = 0; col < side; col++)
            {
                // h = sin(u) * cos(v) bumps; slopes per pixel, with v measured UP the image.
                var u = col * 0.2; var v = (side - row) * 0.15;
                var dhdu = Math.Cos(u) * Math.Cos(v) * 0.2 * 3;
                var dhdv = -Math.Sin(u) * Math.Sin(v) * 0.15 * 3;
                var nx = -dhdu; var ny = -dhdv; var nz = 1.0;
                var len = Math.Sqrt((nx * nx) + (ny * ny) + (nz * nz));
                var at = ((row * side) + col) * 4;
                known[at] = (byte)Math.Round(((nx / len) * 0.5 + 0.5) * 255);
                known[at + 1] = (byte)Math.Round(((ny / len) * 0.5 + 0.5) * 255);
                known[at + 2] = (byte)Math.Round(((nz / len) * 0.5 + 0.5) * 255);
                known[at + 3] = 255;
            }
            var asMade = NormalMapConvention.Measure(known, side, side);
            var greenDown = (byte[])known.Clone();
            for (var i = 1; i < greenDown.Length; i += 4) greenDown[i] = (byte)(255 - greenDown[i]);
            var greenDownReading = NormalMapConvention.Measure(greenDown, side, side);
            var flat = Enumerable.Range(0, side * side).SelectMany(_ => new byte[] { 128, 128, 255, 255 }).ToArray();
            t.Expect("a height field's gradient written green up measures opengl",
                asMade.Verdict() == "opengl", $"opengl {asMade.OpenGlCurl:0.0000}, directx {asMade.DirectXCurl:0.0000}");
            t.Expect("and the same map with green inverted measures directx",
                greenDownReading.Verdict() == "directx", $"opengl {greenDownReading.OpenGlCurl:0.0000}, directx {greenDownReading.DirectXCurl:0.0000}");
            t.Expect("while a flat map measures unclear rather than picking a side",
                NormalMapConvention.Measure(flat, side, side).Verdict() == "unclear", "flat map took a side");

            var packaged = Path.Combine(referenceTemp, "out");
            t.Expect("cook asset refuses one destination with incompatible roles",
                Blix.Tools.Cook.Program.Main(new[] { "asset", conflict, "--out", packaged }) == 1,
                "driver accepted the ambiguous image");
            t.ExpectTrue("and refuses it before writing a texture",
                !File.Exists(Path.Combine(packaged, "shared.blixtex")));
        }
        finally
        {
            try { Directory.Delete(referenceTemp, recursive: true); } catch (IOException) { }
        }

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
                // The cooked leg is the engine's reader; the source leg is the cook's importer. Both
                // must describe the same rig.
                var cookedRig = Path.ChangeExtension(rig, ".blixmesh");
                AssetLoadLog.Start();
                var cookedData = Blix.ModelData.Load(cookedRig, new Blix.ModelNeeds(Colour: true, Skinned: true));
                var rigReports = AssetLoadLog.Drain();
                var cookedSkeleton = cookedData.Skins[0].Skeleton;
                var cookedClips = cookedData.Clips;
                var cookedAttachments = cookedData.Attachments();
                var cookedPrimitives = Enumerable.Range(0, cookedData.Skins.Count)
                    .SelectMany(s => cookedData.SkinnedPrimitives(s)).ToArray();

                var meshReport = rigReports.SingleOrDefault(r => r.SourcePath == cookedRig);
                t.ExpectTrue("a rigged load is reported at all", meshReport is not null);
                t.Expect("and reports Cooked", meshReport!.Mode == AssetLoadMode.Cooked, $"got {meshReport.Mode}");
                t.ExpectTrue("with a cost attached", meshReport.LoadMs > 0 && meshReport.Bytes > 0);

                // The source leg: the same .glb with no cooked sibling beside it.
                var loneRig = Path.Combine(rigTemp, Path.GetFileName(rig));
                File.Copy(rig, loneRig);
                var viaSourceRig = new Blix.Import.GltfImporter()
                    .Import(new AssetImportContext(AssetId.Parse("t/rig-source"), loneRig));

                t.ExpectThrows<InvalidDataException>(
                    "a rigged cook refuses static-only vertex policy",
                    () => MeshRecipe.CookShipped(
                        loneRig, Path.Combine(rigTemp, "invalid-options.blixmesh"),
                        flipTextureV: true));

                t.Expect("cooked and source agree on bone count",
                    cookedSkeleton.BoneCount == viaSourceRig.Skeleton.BoneCount,
                    $"cooked {cookedSkeleton.BoneCount}, source {viaSourceRig.Skeleton.BoneCount}");
                t.Expect("and on clip count",
                    cookedClips.Count == viaSourceRig.Animations.Length,
                    $"cooked {cookedClips.Count}, source {viaSourceRig.Animations.Length}");
                t.Expect("and on attachment count",
                    cookedAttachments.Count == viaSourceRig.AttachmentsOrEmpty.Length,
                    $"cooked {cookedAttachments.Count}, source {viaSourceRig.AttachmentsOrEmpty.Length}");
                // Without this the three counts above can all pass on an asset with no attachments,
                // which is the case the per-primitive layout migration was made for.
                t.Expect("on an asset that actually has attachments",
                    viaSourceRig.AttachmentsOrEmpty.Length > 0,
                    $"{viaSourceRig.AttachmentsOrEmpty.Length} attachments");

                var rigMismatch = new List<string>();
                for (var i = 0; i < Math.Min(cookedSkeleton.BoneCount, viaSourceRig.Skeleton.BoneCount); i++)
                {
                    var a = cookedSkeleton.Bones[i];
                    var b = viaSourceRig.Skeleton.Bones[i];
                    if (a.Name != b.Name) rigMismatch.Add($"bone[{i}] {a.Name} vs {b.Name}");
                    if (a.ParentIndex != b.ParentIndex) rigMismatch.Add($"bone[{i}] parent {a.ParentIndex} vs {b.ParentIndex}");
                    if (a.InverseBindPose != b.InverseBindPose) rigMismatch.Add($"bone[{i}] inverse bind differs");
                }

                t.Expect("bones match name, parent and inverse bind", rigMismatch.Count == 0,
                    string.Join("; ", rigMismatch.Take(4)));

                var clipMismatch = new List<string>();
                for (var i = 0; i < Math.Min(cookedClips.Count, viaSourceRig.Animations.Length); i++)
                {
                    var a = cookedClips[i];
                    var b = viaSourceRig.Animations[i];
                    if (a.Name != b.Name) clipMismatch.Add($"clip[{i}] {a.Name} vs {b.Name}");
                    if (Math.Abs(a.Duration - b.Duration) > 1e-6) clipMismatch.Add($"clip '{a.Name}' duration {a.Duration} vs {b.Duration}");
                    if (a.Tracks.Length != b.Tracks.Length) clipMismatch.Add($"clip '{a.Name}' tracks {a.Tracks.Length} vs {b.Tracks.Length}");
                }

                t.Expect("clips match name, duration and track count", clipMismatch.Count == 0,
                    string.Join("; ", clipMismatch.Take(4)));

                // Per triangle corner, because equal counts are not equal geometry — and because the
                // cook generates MikkTSpace tangents where the source authored none, splitting a
                // vertex wherever its corners' frames disagree. So every corner must agree on every
                // byte but the tangent's, and the cooked tangent must exist.
                var vertexMismatch = 0;
                var untangented = 0;
                const int tangentAt = 64, stride = 80;
                for (var i = 0; i < Math.Min(cookedPrimitives.Length, viaSourceRig.Primitives.Length); i++)
                {
                    var cookedMesh = cookedPrimitives[i].Mesh;
                    var sourceMesh = viaSourceRig.Primitives[i].Mesh;
                    var cookedIndices = cookedMesh.Indices32 ?? cookedMesh.Indices.Select(x => (uint)x).ToArray();
                    var sourceIndices = sourceMesh.Indices32 ?? sourceMesh.Indices.Select(x => (uint)x).ToArray();
                    if (cookedMesh.Layout.Stride != stride || cookedIndices.Length != sourceIndices.Length)
                    {
                        vertexMismatch++;
                        continue;
                    }

                    var differs = false;
                    for (var c = 0; c < cookedIndices.Length && !differs; c++)
                    {
                        var cv = cookedMesh.VertexBytes.AsSpan((int)cookedIndices[c] * stride, stride);
                        var sv = sourceMesh.VertexBytes.AsSpan((int)sourceIndices[c] * stride, stride);
                        differs = !cv[..tangentAt].SequenceEqual(sv[..tangentAt]);
                    }

                    if (differs) vertexMismatch++;
                    if (TangentGeneration.HasNoTangents(cookedMesh)) untangented++;
                }

                t.Expect("every skinned primitive cooks with a tangent frame", untangented == 0,
                    $"{untangented} primitive(s) still carry zero tangents");

                t.Expect("skinned corners agree on every byte but the generated tangent", vertexMismatch == 0,
                    $"{vertexMismatch} primitive(s) differ");

                // <b>Materials, because equal geometry drawn with a different surface is a
                // different picture.</b> Left out of the first version of this control, and a cooked
                // Rogue rendered visibly brighter than the source one with every other assertion
                // here passing — the character was being drawn without its albedo.
                var matMismatch = new List<string>();
                var withAlbedo = 0;
                for (var i = 0; i < Math.Min(cookedPrimitives.Length, viaSourceRig.Primitives.Length); i++)
                {
                    var x = cookedPrimitives[i].Material;
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

                // <b>And the importers read sources only.</b> A cooked file is the engine's to read, so
                // handing one to the cook's importer is refused by name rather than half-read.
                var refused = t.ExpectThrows<AssetImportException>("the importers refuse a cooked file, naming ModelData",
                    () => new Blix.Import.GltfStaticImporter().Import(new AssetImportContext(AssetId.Parse("t/static"), cookedRig)));
                t.Expect("naming ModelData.Load", refused?.Message.Contains("ModelData.Load", StringComparison.Ordinal) == true,
                    refused?.Message ?? "(none)");
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

                var viaCooked = CookedFlat(Path.ChangeExtension(cookedAsset, ".blixmesh"));
                var viaSource = new Blix.Import.GltfStaticImporter()
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
                    Same("NormalScale", a.NormalScale == b.NormalScale, a.NormalScale, b.NormalScale);
                    Same("NormalTexCoord", a.NormalTexCoord == b.NormalTexCoord, a.NormalTexCoord, b.NormalTexCoord);
                    Same("MetallicRoughnessTexCoord", a.MetallicRoughnessTexCoord == b.MetallicRoughnessTexCoord, a.MetallicRoughnessTexCoord, b.MetallicRoughnessTexCoord);
                    Same("OcclusionTexCoord", a.OcclusionTexCoord == b.OcclusionTexCoord, a.OcclusionTexCoord, b.OcclusionTexCoord);
                    Same("EmissiveTexCoord", a.EmissiveTexCoord == b.EmissiveTexCoord, a.EmissiveTexCoord, b.EmissiveTexCoord);
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

        // ── K-F.2: what a material authors reaches both load paths ────────────
        // K-F compares the cooked path with the source path, which cannot see a value both read the
        // same wrong way — and both did: occlusion strength was looked up by glTF's word "strength"
        // where SharpGLTF names it "OcclusionStrength", so every material read 1. This authors each
        // value, with the channels on DIFFERENT texture-coordinate sets so a swap between two of
        // them fails too, and holds both paths to the file.
        AuthoredMaterialReachesBothPaths(t);

        // ── a cooked mesh carries the whole vertex ───────────────────────────
        // The flat cook used to write position, normal and uv only; a load asking for colour then
        // got white and uv0 copied into uv1, which was invisible only while Studio read sources. A
        // static primitive now cooks as the complete vertex, so a cooked load and a source load of
        // the same asset, asking for the same layout, must hand over the same vertices.
        foreach (var asset in new[] { "BoxVertexColors.glb", "MultiUVTest.gltf" })
        {
            var source = FindFile(asset);
            if (source is null) { Console.WriteLine($"  --   complete-vertex check skipped: {asset} not fetched"); continue; }
            var temp = Path.Combine(Path.GetTempPath(), "blix-complete-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(temp);
            try
            {
                foreach (var f in Directory.EnumerateFiles(Path.GetDirectoryName(source)!))
                    File.Copy(f, Path.Combine(temp, Path.GetFileName(f)));
                var gltf = Path.Combine(temp, asset);
                var cooked = Path.ChangeExtension(gltf, ".blixmesh");
                MeshRecipe.CookToBlixMesh(gltf, cooked);

                foreach (var (tangents, colour) in new[] { (false, true), (true, false), (false, false) })
                {
                    var fromSource = new Blix.Import.GltfStaticImporter().ImportNodes(
                        new AssetImportContext(AssetId.Parse("t/cv-src"), gltf, includeTangents: tangents, includeColour: colour));
                    var fromCooked = CookedNodes(cooked, tangents, colour);
                    var differ = 0;
                    var compared = 0;
                    var byName = fromSource.Nodes.ToDictionary(n => n.Name, StringComparer.Ordinal);
                    foreach (var node in fromCooked.Nodes)
                    {
                        if (!byName.TryGetValue(node.Name, out var twin)) continue;
                        for (var i = 0; i < Math.Min(node.Primitives.Length, twin.Primitives.Length); i++)
                        {
                            var a2 = node.Primitives[i].Mesh;
                            var b2 = twin.Primitives[i].Mesh;
                            compared++;
                            // Tangents are generated by the cook where the source authored none, so
                            // at the tangent layout only everything else must agree.
                            if (a2.Layout.Stride != b2.Layout.Stride || a2.IndexCount != b2.IndexCount) { differ++; continue; }
                            if (!tangents && !SameCorners(a2, b2, colourAt: colour ? 40 : -1)) differ++;
                        }
                    }

                    t.Expect($"{asset}: a cooked load equals a source load at the {(tangents ? "tangent" : colour ? "colour" : "plain")} layout",
                        compared > 0 && differ == 0, $"{differ} of {compared} primitive(s) differ");
                }
            }
            finally
            {
                try { Directory.Delete(temp, recursive: true); } catch (IOException) { }
            }
        }

        // ── one ModelData, whatever the file holds ───────────────────────────
        ModelDataReadings(t);

        // ── tools cook on open ───────────────────────────────────────────────
        // The engine reads cooked models; a tool opening a raw one cooks it into a cache first.
        CookOnOpen(t);

        // ── generated tangents agree with authored ones ──────────────────────
        // The cook generates MikkTSpace tangents where a source authored none. Held to assets that DID
        // author them: strip, regenerate, compare every triangle corner. Measured at 100.00% on both
        // (111,348 corners of ClearCoatTest). The control mirrors v first, which flips the frame's
        // handedness everywhere — a comparison that still passed then could not see a convention error.
        foreach (var asset in new[] { "ClearCoatTest.glb", "AlphaBlendModeTest.glb" })
        {
            var path = FindFile(asset);
            if (path is null) { Console.WriteLine($"  --   tangent comparison skipped: {asset} not fetched"); continue; }
            var (corners, direction, handedness) = CompareWithAuthored(path);
            t.Expect($"generated tangents point where {asset}'s authored ones do",
                corners > 0 && direction >= corners * 0.995, $"{direction} of {corners} corners within 8 degrees");
            t.Expect($"and have their handedness",
                corners > 0 && handedness >= corners * 0.999, $"{handedness} of {corners} corners");
            var control = CompareWithAuthored(path, mirrorV: true);
            t.Expect($"CONTROL with v mirrored first, {asset}'s handedness disagrees",
                control.Handedness < control.Corners * 0.05, $"{control.Handedness} of {control.Corners} corners still agree");
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
                var alone = CookedModel(meshCopy);

                // The same asset loaded the ordinary way, in its own tree, to compare against.
                var besideSource = rigged
                    ? CookedModel(Path.ChangeExtension(cookedAsset, ".blixmesh"))
                    : CookedModel(Path.ChangeExtension(cookedAsset, ".blixmesh"));

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

        // ── the mesh driver writes cooked artifacts, not an authored-source mirror ─────────────
        // `mesh --out` originally copied the glTF because the first .blixmesh format still reopened
        // it for materials and node structure. The cooked format owns those now. Keeping the copy
        // would make a derived tree look source-dependent even when its artifact loads alone, and
        // would duplicate large embedded GLBs for no runtime use.
        if (cookedAsset is not null)
        {
            var driverOut = Path.Combine(Path.GetTempPath(), "blix-mesh-driver-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(driverOut);
            try
            {
                t.Expect("out-of-place mesh cooking succeeds",
                    Blix.Tools.Cook.Program.Main(new[] { "mesh", cookedAsset, "--out", driverOut }) == 0);

                var driverMesh = Path.Combine(
                    driverOut, Path.GetFileNameWithoutExtension(cookedAsset) + ".blixmesh");
                t.ExpectTrue("and writes the cooked mesh", File.Exists(driverMesh));
                t.ExpectTrue("without copying the authored glTF into the cooked tree",
                    !Directory.EnumerateFiles(driverOut, "*.gltf", SearchOption.AllDirectories).Any()
                    && !Directory.EnumerateFiles(driverOut, "*.glb", SearchOption.AllDirectories).Any());

                var fromDriver = CookedModel(driverMesh);
                t.Expect("the driver's source-free output opens directly",
                    fromDriver.Primitives.Length > 0,
                    $"{fromDriver.Primitives.Length} primitive(s)");
            }
            finally
            {
                try { Directory.Delete(driverOut, recursive: true); } catch (IOException) { }
            }
        }

        // ── two declarations for one output are refused ─────────────────────
        // <b>A cooked path is derived from the source's NAME, so settings are not in it.</b> Two
        // BlixCook items for one source differing only in Options therefore both cook and both
        // write the same file — demonstrated by declaring one source at recenter=0 and recenter=1
        // and watching two recipe invocations produce one artifact, no warning, last one winning.
        // The consumer whose settings lost then has its cooked file refused by the loader's guard
        // and walks the source forever, traceable to nothing.
        //
        // Refusing is this tree's standing answer to ambiguity — BlixRecipes.For returns null
        // rather than guessing between two recipes that accept one extension — and it is the answer
        // that needs no design. Putting settings in the path would be one, and nothing is asking.
        var tab = "\t";
        var collidingBatch = new[]
        {
            $"mesh{tab}/a/model.obj{tab}/a/model.blixmesh{tab}recenter=0",
            $"mesh{tab}/a/model.obj{tab}/a/model.blixmesh{tab}recenter=1",
        };
        var collisions = Blix.Tools.Cook.Program.FindOutputCollisions(collidingBatch).ToArray();
        t.Expect("two declarations writing one file are caught", collisions.Length == 1,
            $"{collisions.Length} collision(s)");
        t.ExpectTrue("and the message names the output",
            collisions.Length == 1 && collisions[0].Contains("/a/model.blixmesh", StringComparison.Ordinal));
        t.ExpectTrue("and both claimants, with what differs",
            collisions.Length == 1
            && collisions[0].Contains("recenter=0", StringComparison.Ordinal)
            && collisions[0].Contains("recenter=1", StringComparison.Ordinal));

        // The control: distinct outputs are not a collision, or the check would refuse every build.
        var fineBatch = new[]
        {
            $"mesh{tab}/a/model.obj{tab}/a/model.blixmesh{tab}recenter=0",
            $"mesh{tab}/a/prop.obj{tab}/a/prop.blixmesh{tab}recenter=1",
            string.Empty,
            "malformed line with no tabs",
        };
        t.Expect("distinct outputs are not a collision",
            !Blix.Tools.Cook.Program.FindOutputCollisions(fineBatch).Any(), "reported one anyway");

        // ── cook host accounting and argument boundaries ───────────────────
        var hostTemp = Path.Combine(Path.GetTempPath(), "blix-cook-host-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(hostTemp);
        try
        {
            var recipeMethod = typeof(FontRecipe).GetMethod(nameof(FontRecipe.Cook))!;
            var unreadableSource = Path.Combine(hostTemp, "broken.font.json");
            File.WriteAllText(unreadableSource, "{}");
            File.WriteAllText(Path.ChangeExtension(unreadableSource, ".blixfont"), "not cooked");

            var fontRecipe = recipes.Single(r => r.Id == BlixFont.ShippedRecipe);
            var unreadable = CaptureConsole(() =>
                Blix.Tools.Cook.Program.ReportStatus(hostTemp, new[] { fontRecipe }));
            t.Expect("status counts an unreadable output in its coverage denominator",
                unreadable.ExitCode == 0
                && unreadable.Stdout.Contains("0/1 cooked", StringComparison.Ordinal)
                && unreadable.Stdout.Contains("1 unreadable", StringComparison.Ordinal),
                unreadable.Stdout.Trim());

            var ambiguousSource = Path.Combine(hostTemp, "shared.claim");
            File.WriteAllText(ambiguousSource, "fixture");
            var ambiguousRecipes = new[]
            {
                new FoundRecipe("am01", ".one", new[] { ".claim" }, "first claimant", 1, recipeMethod),
                new FoundRecipe("am02", ".two", new[] { ".claim" }, "second claimant", 1, recipeMethod),
            };
            var ambiguous = CaptureConsole(() =>
                Blix.Tools.Cook.Program.ReportStatus(hostTemp, ambiguousRecipes));
            t.ExpectTrue("status reports both recipes that ambiguously claim one source",
                ambiguous.Stdout.Contains("AMBIGUOUS", StringComparison.Ordinal)
                && ambiguous.Stdout.Contains("am01, am02", StringComparison.Ordinal)
                && ambiguous.Stdout.Contains("1 ambiguous", StringComparison.Ordinal));

            var skippedSource = Path.Combine(hostTemp, "ordinary.json");
            var skippedOutput = Path.Combine(hostTemp, "ordinary.blixfont");
            var batchList = Path.Combine(hostTemp, "batch.txt");
            File.WriteAllText(
                batchList,
                $"{BlixFont.ShippedRecipe}\t{skippedSource}\t{skippedOutput}{Environment.NewLine}");
            var skippedBatch = CaptureConsole(() =>
                Blix.Tools.Cook.Program.Main(new[] { "batch", batchList }));
            t.Expect("batch respects a recipe's skipped outcome",
                skippedBatch.ExitCode == 0
                && skippedBatch.Stdout.Contains("skipped fnt1", StringComparison.Ordinal)
                && skippedBatch.Stdout.Contains("0 cooked, 1 skipped", StringComparison.Ordinal)
                && !File.Exists(skippedOutput),
                skippedBatch.Stdout.Trim());

            var extraStatus = CaptureConsole(() =>
                Blix.Tools.Cook.Program.Main(new[] { "status", hostTemp, "ignored" }));
            t.ExpectTrue("status rejects a surplus argument",
                extraStatus.ExitCode == 2 && extraStatus.Stderr.Contains("Usage:", StringComparison.Ordinal));

            var extraList = CaptureConsole(() =>
                Blix.Tools.Cook.Program.Main(new[] { "list", "ignored" }));
            var extraHelp = CaptureConsole(() =>
                Blix.Tools.Cook.Program.Main(new[] { "help", "ignored" }));
            t.ExpectTrue("zero-argument host verbs reject surplus arguments",
                extraList.ExitCode == 2 && extraHelp.ExitCode == 2);

            var extraBatch = CaptureConsole(() =>
                Blix.Tools.Cook.Program.Main(new[] { "batch", batchList, "ignored" }));
            t.ExpectTrue("batch rejects a surplus argument",
                extraBatch.ExitCode == 2 && extraBatch.Stderr.Contains("Usage:", StringComparison.Ordinal));

            var extraRun = CaptureConsole(() =>
                Blix.Tools.Cook.Program.Main(new[]
                {
                    "run", BlixFont.ShippedRecipe, unreadableSource, skippedOutput, "ignored"
                }));
            t.ExpectTrue("run rejects a second output-like argument",
                extraRun.ExitCode == 2
                && extraRun.Stderr.Contains("unexpected argument", StringComparison.Ordinal)
                && !File.Exists(skippedOutput));

            var malformedBatch = Path.Combine(hostTemp, "malformed-batch.txt");
            File.WriteAllText(
                malformedBatch,
                $"{BlixFont.ShippedRecipe}\t{skippedSource}\t{skippedOutput}\t\textra{Environment.NewLine}");
            var malformed = CaptureConsole(() =>
                Blix.Tools.Cook.Program.Main(new[] { "batch", malformedBatch }));
            t.ExpectTrue("batch rejects ignored tab-separated fields",
                malformed.ExitCode == 1 && malformed.Stderr.Contains("malformed line", StringComparison.Ordinal));
        }
        finally
        {
            try { Directory.Delete(hostTemp, recursive: true); } catch (IOException) { }
        }

        // ── a texture's identity is the same across independent loads ───────
        // <b>Every upload cache in this tree is keyed by the OBJECT, so nothing can be shared.</b>
        // TextureData is a class with no value equality — its own comment says the cost "nothing
        // relied on" — so two imports of one file produce two instances and upload the same pixels
        // twice, by construction. Three owners hand-roll that key: the engine's MaterialTextureLoader,
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
            var firstLoad = new Blix.Import.GltfImporter()
                .Import(new AssetImportContext(AssetId.Parse("t/id-1"), idAsset));
            var secondLoad = new Blix.Import.GltfImporter()
                .Import(new AssetImportContext(AssetId.Parse("t/id-2"), idAsset));

            static string[] Ids(Blix.Import.GltfModel m) => m.Primitives
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
        var sameA = Blix.TextureData.Rgba8Single("a", pixels, 1, 1, "/assets/x.png");
        var sameB = Blix.TextureData.Rgba8Single("a-again", pixels, 1, 1, "/assets/x.png");

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
        var anonA = Blix.TextureData.Rgba8Single("anon", pixels, 1, 1);
        var anonB = Blix.TextureData.Rgba8Single("anon", pixels, 1, 1);
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

        // ── materials get the identity too, but not the registry ────────────
        // <b>The split is the point.</b> A texture has one canonical GPU form, so a shared registry
        // can hold it for everyone. A material does not — the UBO layout is the game's — so Sponza
        // keeps its own storage and shares only the key. Asserted the same way: two loads agree.
        if (idAsset is not null)
        {
            var matA = new Blix.Import.GltfImporter()
                .Import(new AssetImportContext(AssetId.Parse("t/mat-1"), idAsset));
            var matB = new Blix.Import.GltfImporter()
                .Import(new AssetImportContext(AssetId.Parse("t/mat-2"), idAsset));

            static string[] MaterialIds(Blix.Import.GltfModel m) => m.Primitives
                .Select(p => p.Material)
                .Where(x => x is not null)
                .Select(x => x!.ResourceId)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(x => x, StringComparer.Ordinal)
                .ToArray();

            var idsA = MaterialIds(matA);
            t.Expect("a load yields material identities", idsA.Length > 0, $"{idsA.Length}");
            t.ExpectTrue("none of them is empty", idsA.All(x => x.Length > 0));
            t.Expect("two loads agree on every material identity",
                idsA.SequenceEqual(MaterialIds(matB), StringComparer.Ordinal),
                $"[{string.Join(", ", idsA)}]");
            t.ExpectTrue("and they are distinct per material",
                idsA.Length == matA.Primitives.Select(p => p.Material?.ResourceId)
                    .Where(x => !string.IsNullOrEmpty(x)).Distinct(StringComparer.Ordinal).Count());
        }

        // ── a cooked mesh opens as a node hierarchy, and the un-bake is exact ──
        // <b>The cook bakes each node's world transform into its vertices</b>, which is most of what
        // the flat load path buys — and it threw the hierarchy away, so the studio's model view,
        // which is entirely node-shaped, could not open the one form a project ships.
        //
        // A node table fixes that without unbaking anything at cook time: the primitive records its
        // node, so a consumer that wants node-local geometry applies the inverse itself. What is
        // worth asserting is that the inverse RECOVERS the original — a hierarchy that came back
        // with subtly different vertices would be worse than none.
        var nodeAsset = FindFile("MultiUVTest.gltf");
        if (nodeAsset is null)
        {
            // The conformance corpus is fetched, not committed. A default clean clone must keep
            // its gate green without optional test data; fetching the corpus turns this coverage
            // on rather than repairing a broken checkout.
            Console.WriteLine("  --   node-hierarchy checks skipped: glTF corpus not fetched");
        }
        else
        {
            var nodeTemp = Path.Combine(Path.GetTempPath(), "blix-nodes-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(nodeTemp);
            try
            {
                foreach (var f in Directory.EnumerateFiles(Path.GetDirectoryName(nodeAsset)!))
                {
                    File.Copy(f, Path.Combine(nodeTemp, Path.GetFileName(f)));
                }

                var gltf = Path.Combine(nodeTemp, Path.GetFileName(nodeAsset));
                MeshRecipe.CookToBlixMesh(gltf, Path.ChangeExtension(gltf, ".blixmesh"));

                var importer = new Blix.Import.GltfStaticImporter();
                var fromSource = importer.ImportNodes(
                    new AssetImportContext(AssetId.Parse("t/n-src"), gltf, includeColour: true));
                var fromCooked = CookedNodes(Path.ChangeExtension(gltf, ".blixmesh"), colour: true);

                t.Expect("a cooked mesh yields the same node count",
                    fromCooked.Nodes.Length == fromSource.Nodes.Length,
                    $"cooked {fromCooked.Nodes.Length}, source {fromSource.Nodes.Length}");

                // <b>Matched by NAME, not by index, and that is not a weakening.</b> The cook
                // topo-sorts — every parent before its children, which is the invariant the format
                // promises and its reader enforces — while the source keeps glTF's own order, and
                // this very asset lists a child before its parent. Both are valid hierarchies of the
                // same shape; demanding one order would be asserting an accident.
                var sourceByName = fromSource.Nodes.ToDictionary(n => n.Name, StringComparer.Ordinal);
                var nodeMismatch = new List<string>();
                var comparedVerts = 0;

                for (var i = 0; i < fromCooked.Nodes.Length; i++)
                {
                    var a = fromCooked.Nodes[i];
                    if (!sourceByName.TryGetValue(a.Name, out var b))
                    {
                        nodeMismatch.Add($"'{a.Name}' exists only in the cooked file");
                        continue;
                    }

                    // The parent is compared by NAME too, since the indices differ by construction.
                    var aParent = a.ParentIndex < 0 ? null : fromCooked.Nodes[a.ParentIndex].Name;
                    var bParent = b.ParentIndex < 0 ? null : fromSource.Nodes[b.ParentIndex].Name;
                    if (aParent != bParent) nodeMismatch.Add($"'{a.Name}' parent {aParent} vs {bParent}");
                    if (a.LocalTransform != b.LocalTransform) nodeMismatch.Add($"'{a.Name}' local transform");

                    // And the promise the ORDER does make: a parent is always already placed.
                    if (a.ParentIndex >= i) nodeMismatch.Add($"'{a.Name}' parent {a.ParentIndex} is not before {i}");

                    if (a.Primitives.Length != b.Primitives.Length)
                    {
                        nodeMismatch.Add($"'{a.Name}' primitive count {a.Primitives.Length} vs {b.Primitives.Length}");
                        continue;
                    }

                    for (var pi = 0; pi < a.Primitives.Length; pi++)
                    {
                        // <b>The layout has to match too, or the GPU reads garbage.</b> A cooked file
                        // is 32-byte position/normal/uv; the studio's stage declares the 44-byte
                        // colour layout. Handing it the first drew the model as a cloud of shards —
                        // the same stride mismatch that once drew Sponza as grey triangles.
                        if (a.Primitives[pi].Mesh.Layout.Stride != b.Primitives[pi].Mesh.Layout.Stride)
                        {
                            nodeMismatch.Add(
                                $"'{a.Name}' stride {a.Primitives[pi].Mesh.Layout.Stride} vs {b.Primitives[pi].Mesh.Layout.Stride}");
                            continue;
                        }

                        comparedVerts += a.Primitives[pi].Mesh.VertexCount;
                        var av = a.Primitives[pi].Mesh.VertexBytes;
                        var bv = b.Primitives[pi].Mesh.VertexBytes;
                        if (av.Length != bv.Length) { nodeMismatch.Add($"'{a.Name}' vertex bytes length"); continue; }

                        // Not byte equality: the un-bake is float arithmetic, so the test is that it
                        // lands within rounding of where the source path put it.
                        var af = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(av);
                        var bf = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(bv);
                        var worst = 0f;
                        for (var f = 0; f < af.Length; f++) worst = Math.Max(worst, Math.Abs(af[f] - bf[f]));
                        if (worst > 1e-4f) nodeMismatch.Add($"'{a.Name}' vertex delta {worst}");
                    }
                }

                t.Expect("its nodes match parent, transform and geometry by name",
                    nodeMismatch.Count == 0, string.Join("; ", nodeMismatch.Take(4)));
                t.Expect("on vertices that actually exist", comparedVerts > 0, $"{comparedVerts}");
            }
            finally
            {
                try { Directory.Delete(nodeTemp, recursive: true); } catch (IOException) { }
            }
        }

        // ── Every path that cooks a SHIPPED mesh supplies a simplifier ──────
        //
        // The same bug has shipped twice: a caller assembled CookToBlixMesh's arguments itself and
        // omitted `simplify`, which defaults to null. Nothing throws, a valid file is written, and
        // every LOD in it is gone. The first time it was the uniform [Recipe] path; that fix routed
        // the recipe and `cook mesh` through one simplifier and missed `cook asset` — the command
        // the Sponza pipeline uses. 12.8M triangles shipped at full detail, the renderer's LOD
        // selection had nothing to choose between, and fixing it was worth 29% of the frame.
        //
        // <b>Asserted on the STAMP rather than on a triangle count, deliberately.</b> Whether a
        // given mesh decimates depends on the mesh — a rigged figure or a canopy of disconnected
        // cards may legitimately produce one level — so a count makes the test hostage to whichever
        // asset happens to be lying around. Whether a simplifier was SUPPLIED is the thing that
        // broke, it is the thing every driver must not get wrong, and the cooked file records it.
        // The negative control is what makes that reading mean something.
        // A STATIC asset. Rogue.glb was the obvious pick and is the wrong one: a rigged glTF takes
        // the rig branch, which writes "rig=1 skins=..." and never calls BuildLods at all. Skinned
        // meshes get no LOD chain by design and their stamp does not mention a simplifier, so they
        // cannot answer this question either way.
        var lodSource = FindFile("OrientationTest.glb");
        if (lodSource is null)
        {
            // The conformance corpus is fetched, not committed, so its absence is a fact about the
            // checkout rather than a defect. Saying so beats a red line nobody can act on.
            Console.WriteLine("  --   static-glTF LOD checks skipped: corpus not fetched");
        }
        else
        {
            var lodTemp = Path.Combine(Path.GetTempPath(), "blix-lod-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(lodTemp);
            try
            {
                string StampOf(string path) => CookedFile.TryReadHeader(path)?.Stamp.Parameters ?? "";

                var viaShipped = Path.Combine(lodTemp, "shipped.blixmesh");
                MeshRecipe.CookShipped(lodSource, viaShipped);
                var shippedStamp = StampOf(viaShipped);
                t.ExpectTrue("a shipped cook supplies a simplifier",
                    shippedStamp.Contains("simplify=yes", StringComparison.Ordinal));
                t.ExpectTrue("the shipped mesh is current for the exact options that made it",
                    MeshRecipe.IsShippedCurrent(lodSource, viaShipped));
                t.ExpectTrue("and a vertex-option change invalidates it",
                    !MeshRecipe.IsShippedCurrent(lodSource, viaShipped, flipTextureV: true));

                // The uniform path a build rule invokes must agree with the typed one. They are the
                // two ways an asset reaches a shipped tree, and they diverged once already.
                var viaRecipe = Path.Combine(lodTemp, "recipe.blixmesh");
                MeshRecipe.Cook(new CookRequest(lodSource, viaRecipe,
                    new Dictionary<string, string>()));
                t.Expect("and the uniform [Recipe] path stamps identically",
                    StampOf(viaRecipe) == shippedStamp, $"'{StampOf(viaRecipe)}' vs '{shippedStamp}'");

                // The negative control: without one, the stamp must say so. Otherwise the assertion
                // above passes for a file that has no chain at all, which is exactly the failure
                // this suite exists to catch.
                var viaNone = Path.Combine(lodTemp, "none.blixmesh");
                MeshRecipe.CookToBlixMesh(lodSource, viaNone);
                t.ExpectTrue("and a cook WITHOUT one is distinguishable",
                    StampOf(viaNone).Contains("simplify=none", StringComparison.Ordinal));
            }
            finally
            {
                try { Directory.Delete(lodTemp, recursive: true); } catch (IOException) { }
            }
        }

        // ── Material patches ────────────────────────────────────────────────
        // The load-bearing behaviour is REFUSAL. A patch and the name heuristic it replaced both
        // key on a material name; what separates them is that a rule matching nothing stops the
        // cook instead of silently rendering the wrong thing forever. That, and the expected-count
        // assertion, are the two things worth a test — and both caught real mistakes on their first
        // use: the count guard refused a hand-written *stone*{3} against an asset that has 7.
        var patchDir = Path.Combine(Path.GetTempPath(), "blix-patch-test-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(patchDir);
        try
        {
            var table = new[]
            {
                new BlixMeshMaterial("glass", System.Numerics.Vector4.One, 0, 0, 1f, 0, 0, 0, 0f, 1f, 1f,
                    System.Numerics.Vector3.Zero, 1f, BlixMesh.AlphaOpaque, 0.5f, false, 0f),
                new BlixMeshMaterial("stone_wall_01", System.Numerics.Vector4.One, 0, 0, 1f, 0, 0, 0, 0.35f, 1f, 1f,
                    System.Numerics.Vector3.Zero, 1f, BlixMesh.AlphaOpaque, 0.5f, false, 0f),
                new BlixMeshMaterial("stone_trims_01", System.Numerics.Vector4.One, 0, 0, 1f, 0, 0, 0, 0.35f, 1f, 1f,
                    System.Numerics.Vector3.Zero, 1f, BlixMesh.AlphaOpaque, 0.5f, false, 0f),
            };

            string Write(string name, string body)
            {
                var f = Path.Combine(patchDir, name);
                File.WriteAllText(f, body);
                return f;
            }

            var applied = MaterialPatch.Load(Write("ok.blixpatch",
                "material glass transmission=1.0 ior=1.5\nmaterial stone_*{2} metallic=0.0\n")).Apply(table);
            t.Expect("a patch applies a scalar to the material it names",
                Math.Abs(applied[0].Ext.TransmissionFactor - 1.0f) < 1e-6f,
                $"{applied[0].Ext.TransmissionFactor}");
            t.Expect("and an extension field the asset never authored",
                Math.Abs(applied[0].Ext.IndexOfRefraction - 1.5f) < 1e-6f,
                $"{applied[0].Ext.IndexOfRefraction}");
            t.ExpectTrue("a glob reaches every material it matches",
                applied[1].MetallicFactor == 0f && applied[2].MetallicFactor == 0f);
            t.ExpectTrue("and leaves the ones it does not alone", applied[0].MetallicFactor == 0f);

            var missed = Throws(() => MaterialPatch.Load(Write("miss.blixpatch",
                "material curtain_01 sheen=1,0,0\n")).Apply(table));
            t.ExpectTrue("a rule that matches NOTHING fails the cook", missed is not null);
            t.ExpectTrue("and the refusal names what was there instead",
                missed?.Contains("stone_wall_01", StringComparison.Ordinal) == true);

            var miscount = Throws(() => MaterialPatch.Load(Write("count.blixpatch",
                "material stone_*{3} metallic=0.0\n")).Apply(table));
            t.ExpectTrue("an expected count that does not hold fails the cook", miscount is not null);

            var stale = MaterialPatch.Load(Write("pin.blixpatch", "source deadbeef\nmaterial glass ior=1.5\n"));
            t.ExpectTrue("a source pin that no longer matches fails the cook",
                Throws(() => stale.RequireSource("cafe1234")) is not null);
            t.ExpectTrue("and the same pin passes against the source it was written for",
                Throws(() => stale.RequireSource("deadbeef")) is null);

            t.ExpectTrue("an unknown key is refused rather than ignored",
                Throws(() => MaterialPatch.Load(Write("bad.blixpatch", "material glass nonsense=1\n")).Apply(table)) is not null);

            // A patch may point an extension's texture at an image the asset already carries.
            // The leaf case: transmitted light tinted per texel by the same map the surface uses,
            // stated by the scene instead of assumed by the renderer.
            var textured = MaterialPatch.Load(Write("tex.blixpatch",
                "material glass diffuseTransmissionColorTexture=baseColor\n")).Apply(table);
            t.Expect("a patch can point a transmission colour at the base-colour image",
                textured[0].Ext.DiffuseTransmissionColorImage == table[0].BaseColorImage,
                $"got image {textured[0].Ext.DiffuseTransmissionColorImage}, base is {table[0].BaseColorImage}");

            var cleared = MaterialPatch.Load(Write("tex0.blixpatch",
                "material glass diffuseTransmissionColorTexture=none\n")).Apply(table);
            t.Expect("and can clear it back to the factor alone",
                cleared[0].Ext.DiffuseTransmissionColorImage == BlixMesh.NoImage,
                $"got {cleared[0].Ext.DiffuseTransmissionColorImage}");

            // A path would mean growing this file's image table; refusing says so rather than
            // silently doing nothing, which is how a scene learns the rule.
            t.ExpectTrue("but it cannot introduce an image the asset does not carry",
                Throws(() => MaterialPatch.Load(Write("texbad.blixpatch",
                    "material glass diffuseTransmissionColorTexture=leaf.png\n")).Apply(table)) is not null);
        }
        finally
        {
            Directory.Delete(patchDir, recursive: true);
        }


        // ====================================================================
        // The sky volume's albedo alpha: per-material diffuse transmission.
        // ====================================================================
        //
        // <b>The format's MEANING changed under a fixed layout, which is the version bump's whole
        // reason.</b> v3 wrote a constant 255 into the albedo grid's alpha and nothing read it;
        // v4 writes each cell's mean KHR_materials_diffuse_transmission there and the transport
        // reads it. The bytes are identical in size and position, so a v3 file loaded by a v4
        // build would not fail — it would quietly report that every surface in the scene scatters
        // 100% of the light through it, and the result would look like a slightly brighter room.
        //
        // The BAKER's half of this — that a material's factor actually reaches the cell — is
        // checked by the cook's own report rather than here, because reaching it needs real
        // cooked meshes. `blix cook sky` prints the percentage of surface cells that scatter, and
        // a scene known to author the extension printing 0% is the failure that matters.
        var skyDir = Path.Combine(Path.GetTempPath(), "blix-sky-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(skyDir);
        try
        {
            var file = Path.Combine(skyDir, "probe.blixsky");
            var cells = 2 * 2 * 2;
            // Alpha holds 0.45 (foliage, as Sponza's tree and ivy patches author it) in the first
            // cell and 0 (a curtain, authored as an explicit measured zero) in the second.
            var albedo = new byte[cells * 4];
            albedo[3] = (byte)Math.Round(0.45f * 255f);
            albedo[7] = 0;
            var volume = new Blix.Graphics.Images.BlixSkyVolume(
                new System.Numerics.Vector3(-1f), new System.Numerics.Vector3(1f),
                2, 2, 2, new float[cells * Blix.Graphics.Images.BlixSkyVolume.FloatsPerCell],
                2, 2, 2, new byte[cells],
                2, 2, 2, albedo);
            Blix.Graphics.Images.BlixSkyVolume.Write(file, volume);

            var read = Blix.Graphics.Images.BlixSkyVolume.Read(file);
            t.ExpectTrue("a sky volume round-trips its albedo grid", read.HasAlbedo);
            t.Expect("and the alpha channel survives as the cell's diffuse transmission",
                read.Albedo![3] == albedo[3] && read.Albedo![7] == 0,
                $"alpha came back {read.Albedo![3]} and {read.Albedo![7]}, wrote {albedo[3]} and 0");
            t.Expect("an opaque cell's zero is a VALUE, not an absent surface",
                read.Albedo![7] == 0 && read.HasAlbedo,
                "a reader must not treat alpha 0 as 'no surface here'");

            // Stamp the version field back to v3 and confirm the file is refused. Byte offset 8:
            // the magic is one ulong, the version the int immediately after it.
            var stale = Path.Combine(skyDir, "v3.blixsky");
            var bytes = File.ReadAllBytes(file);
            BitConverter.GetBytes(3).CopyTo(bytes, 8);
            File.WriteAllBytes(stale, bytes);
            var refused = Throws(() => Blix.Graphics.Images.BlixSkyVolume.Read(stale));
            t.ExpectTrue("a volume from before alpha meant anything is REFUSED, not reinterpreted",
                refused is not null);
            t.ExpectTrue("and the refusal names the version it found",
                refused?.Contains("v3", StringComparison.Ordinal) == true);
        }
        finally
        {
            Directory.Delete(skyDir, recursive: true);
        }

        // ====================================================================
        // A recipe reports its whole output, not just the file it is named for.
        // ====================================================================
        //
        // The build has to stage everything a cook produces, and for a while the only thing that
        // knew a mesh writes `<name>.textures/` was a glob in Directory.Build.targets — MSBuild
        // holding a fact about one recipe that the recipe had never told it. A second sidecar
        // convention would have needed a second glob there.
        //
        // The recipe declares the folder now and `blix cook outputs` reports the closure, which
        // is also why this is a QUERY: cooking is incremental, so anything produced only while
        // cooking would be missing on the builds that cook nothing.
        {
            var mesh = Blix.Cooked.BlixRecipes.ById(typeof(MeshRecipe).Assembly, BlixMesh.ShippedRecipe);
            t.ExpectTrue("CONTROL the shipped mesh recipe was found", mesh is not null);
            t.ExpectTrue("the mesh recipe DECLARES the folder it writes beside its output",
                mesh?.SidecarFolder == BlixMesh.ExtractedImageFolder);

            var closureDir = Path.Combine(Path.GetTempPath(), "blix-closure-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(closureDir);
            try
            {
                var outPath = Path.Combine(closureDir, "thing.blixmesh");
                File.WriteAllText(outPath, "");

                // No sidecar directory yet: the closure is the named output and nothing else.
                t.ExpectTrue("with no sidecar directory the closure is just the named output",
                    mesh!.OutputClosure(outPath).Count() == 1);

                var side = Path.Combine(closureDir, "thing" + BlixMesh.ExtractedImageFolder);
                Directory.CreateDirectory(side);
                File.WriteAllText(Path.Combine(side, "a.blixtex"), "");
                File.WriteAllText(Path.Combine(side, "b.blixtex"), "");

                var closure = mesh.OutputClosure(outPath).ToArray();
                t.ExpectTrue("the closure picks up every file in the declared sidecar folder",
                    closure.Length == 3, string.Join(", ", closure.Select(Path.GetFileName)));
                t.ExpectTrue("and still names the output itself",
                    closure.Any(c => Path.GetFileName(c) == "thing.blixmesh"));

                // A recipe that declares no sidecar folder must not grow one by accident.
                var font = Blix.Cooked.BlixRecipes.ById(typeof(FontRecipe).Assembly, BlixFont.ShippedRecipe);
                t.ExpectTrue("a recipe declaring no sidecar folder reports only its output",
                    font is not null && font.SidecarFolder.Length == 0);
            }
            finally
            {
                Directory.Delete(closureDir, recursive: true);
            }
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
        if (dir is null) return null;

        // src/ first, then third_party/ — the conformance corpus lives there and is fetched rather
        // than committed, so a test that wants one must look outside the source tree.
        foreach (var root in new[] { "src", "third_party" })
        {
            var where = Path.Combine(dir.FullName, root);
            if (!Directory.Exists(where)) continue;

            var found = Directory.EnumerateFiles(where, name, SearchOption.AllDirectories)
                .FirstOrDefault(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                                  && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal));
            if (found is not null) return found;
        }

        return null;
    }

    /// <summary>Runs an action and returns the exception message, or null if it did not throw.</summary>
    private static string? Throws(Action a)
    {
        try { a(); return null; }
        catch (Exception ex) { return ex.Message; }
    }

    private static (int ExitCode, string Stdout, string Stderr) CaptureConsole(Func<int> action)
    {
        var originalOut = Console.Out;
        var originalError = Console.Error;
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        try
        {
            Console.SetOut(stdout);
            Console.SetError(stderr);
            return (action(), stdout.ToString(), stderr.ToString());
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalError);
        }
    }
}

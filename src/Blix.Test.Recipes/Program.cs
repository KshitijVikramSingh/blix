using Blix.Import;
using Blix.Assets;
using System.Diagnostics;
using Blix.Cooked;
using Blix.Recipes;
using Blix.Verify;
using Blix;

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
            return new Blix.Import.GltfModel(skinned, d.Skeleton!, d.Clips.ToArray(), d.SkeletonPlacement,
                Skins: d.Skins.Select(k => new Blix.Import.GltfSkinBinding(k.Binding, k.Placement)).ToArray());
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

    // A source read as a REFERENCE, unvalidated so the declared-lenient files (ReadLeniently) can be read
    // too. The cook itself validates (MeshRecipe.OpenSource); ValidatorRejectionsAreAccounted audits that.
    private static SharpGLTF.Schema2.ModelRoot LoadSource(string file) =>
        SharpGLTF.Schema2.ModelRoot.Load(file, new SharpGLTF.Schema2.ReadSettings { Validation = SharpGLTF.Validation.ValidationMode.Skip });

    // ── Every textured channel reaches the cooked file ─────────────────────────
    // Enumerated by each material's ACTUAL channel keys (SharpGLTF's), not by a list of names this file
    // shares with the cook — a misspelt key would otherwise drop a texture on both sides and pass.
    private static void EveryTexturedChannelCooks(TestRunner t)
    {
        var root = FindFile("InterpolationTest.glb") is { } it ? Path.GetFullPath(Path.Combine(it, "..", "..", "..")) : null;
        if (root is null) return;

        var seen = new SortedSet<string>(StringComparer.Ordinal);
        var dropped = new List<string>();
        var clashes = new List<string>();
        foreach (var file in Directory.EnumerateFiles(root, "*.gl*", SearchOption.AllDirectories)
            .Where(f => (f.EndsWith(".glb", StringComparison.Ordinal) || f.EndsWith(".gltf", StringComparison.Ordinal))
                && !ExpectedRefusals.ContainsKey(Path.GetFileName(f)))
            .OrderBy(f => f, StringComparer.Ordinal))
        {
            var gltf = LoadSource(file);
            var cooked = BlixMeshReader.Read(CookCache.Resolve(file));
            // One cooked file is one source image: rows of two different pictures never share a resource.
            foreach (var shared in cooked.ImageTable.GroupBy(r => r.Resource).Where(g => g.Select(r => r.ContentHash).Distinct().Count() > 1))
                clashes.Add($"{Path.GetFileName(file)}: {shared.Key} holds {shared.Select(r => r.ContentHash).Distinct().Count()} different images");
            for (var m = 0; m < gltf.LogicalMaterials.Count; m++)
            {
                var row = cooked.MaterialTable[m];
                var x = row.Ext;
                var cookedImage = new Dictionary<string, int>(StringComparer.Ordinal)
                {
                    ["BaseColor"] = row.BaseColorImage, ["Normal"] = row.NormalImage, ["MetallicRoughness"] = row.MetallicRoughnessImage,
                    ["Occlusion"] = row.OcclusionImage, ["Emissive"] = row.EmissiveImage,
                    ["ClearCoat"] = x.ClearcoatImage, ["ClearCoatRoughness"] = x.ClearcoatRoughnessImage, ["ClearCoatNormal"] = x.ClearcoatNormalImage,
                    ["SheenColor"] = x.SheenColorImage, ["SheenRoughness"] = x.SheenRoughnessImage,
                    ["SpecularColor"] = x.SpecularColorImage, ["SpecularFactor"] = x.SpecularImage,
                    ["Transmission"] = x.TransmissionImage, ["VolumeThickness"] = x.ThicknessImage,
                    ["Iridescence"] = x.IridescenceImage, ["IridescenceThickness"] = x.IridescenceThicknessImage,
                    ["Anisotropy"] = x.AnisotropyImage,
                    ["DiffuseTransmissionFactor"] = x.DiffuseTransmissionImage, ["DiffuseTransmissionColor"] = x.DiffuseTransmissionColorImage,
                };
                foreach (var channel in gltf.LogicalMaterials[m].Channels)
                {
                    if (channel.Texture is null) continue;
                    seen.Add(channel.Key);
                    if (!cookedImage.TryGetValue(channel.Key, out var image) || image < 0)
                        dropped.Add($"{Path.GetFileName(file)} material {m}: {channel.Key}");
                }
            }
        }

        t.Expect($"every textured channel in the corpus cooks an image ({seen.Count} channel kinds: {string.Join(",", seen)})",
            dropped.Count == 0, string.Join(" | ", dropped.Take(6)));
        t.Expect("and no cooked file holds two different source images (same-named embedded images do not overwrite each other)",
            clashes.Count == 0, string.Join(" | ", clashes.Take(4)));
    }

    // ── KHR_texture_transform as glTF defines it ───────────────────────────────
    // Every core channel in the corpus that carries the extension cooks to exactly its offset, rotation
    // and scale, with a texCoord override replacing the channel's set; and the engine's UvTransform is the
    // spec's T * R * S, built here from the spec's three matrices and applied to sample UVs.
    private static void TextureTransformsMatchGltf(TestRunner t)
    {
        var root = FindFile("InterpolationTest.glb") is { } it ? Path.GetFullPath(Path.Combine(it, "..", "..", "..")) : null;
        if (root is null) return;

        var transformed = 0;
        var wrong = new List<string>();
        foreach (var file in Directory.EnumerateFiles(root, "*.gl*", SearchOption.AllDirectories)
            .Where(f => (f.EndsWith(".glb", StringComparison.Ordinal) || f.EndsWith(".gltf", StringComparison.Ordinal))
                && !ExpectedRefusals.ContainsKey(Path.GetFileName(f)))
            .OrderBy(f => f, StringComparer.Ordinal))
        {
            var gltf = LoadSource(file);
            if (!gltf.ExtensionsUsed.Contains("KHR_texture_transform")) continue;
            var cooked = BlixMeshReader.Read(CookCache.Resolve(file));
            for (var m = 0; m < gltf.LogicalMaterials.Count; m++)
            {
                var row = cooked.MaterialTable[m];
                var uv = row.UvTransforms ?? BlixMeshUvTransforms.Identity;
                foreach (var (channel, ours, set) in new[]
                {
                    ("BaseColor", uv.BaseColor, row.BaseColorTexCoord), ("Normal", uv.Normal, row.NormalTexCoord),
                    ("MetallicRoughness", uv.MetallicRoughness, row.MetallicRoughnessTexCoord),
                    ("Occlusion", uv.Occlusion, row.OcclusionTexCoord), ("Emissive", uv.Emissive, row.EmissiveTexCoord),
                })
                {
                    if (gltf.LogicalMaterials[m].FindChannel(channel) is not { } c || c.TextureTransform is not { } x) continue;
                    transformed++;
                    var want = new BlixMeshUvTransform(x.Offset, x.Rotation, x.Scale);
                    var wantSet = x.TextureCoordinateOverride ?? c.TextureCoordinate;
                    if (ours != want || set != wantSet)
                        wrong.Add($"{Path.GetFileName(file)} material {m} {channel}: cooked {ours} set {set}, glTF {want} set {wantSet}");
                }
            }
        }

        t.Expect($"every transformed channel cooks to its glTF transform and texCoord ({transformed} channels)",
            transformed > 0 && wrong.Count == 0, string.Join(" | ", wrong.Take(4)));

        // The spec's composition, from its three matrices (KHR_texture_transform README): uv' = T R S uv.
        var sample = new UvTransform(new System.Numerics.Vector2(0.25f, -0.5f), 0.7f, new System.Numerics.Vector2(2f, 0.5f));
        var (cs, sn) = (MathF.Cos(sample.Rotation), MathF.Sin(sample.Rotation));
        float[,] T = { { 1, 0, sample.Offset.X }, { 0, 1, sample.Offset.Y }, { 0, 0, 1 } };
        float[,] R = { { cs, sn, 0 }, { -sn, cs, 0 }, { 0, 0, 1 } };
        float[,] S = { { sample.Scale.X, 0, 0 }, { 0, sample.Scale.Y, 0 }, { 0, 0, 1 } };
        static float[,] Mul(float[,] a, float[,] b)
        {
            var r = new float[3, 3];
            for (var i = 0; i < 3; i++) for (var j = 0; j < 3; j++) for (var k = 0; k < 3; k++) r[i, j] += a[i, k] * b[k, j];
            return r;
        }

        var M = Mul(Mul(T, R), S);
        var worstUv = 0f;
        foreach (var p in new[] { (0f, 0f), (1f, 0f), (0f, 1f), (0.3f, 0.8f) })
        {
            var spec = new System.Numerics.Vector2(M[0, 0] * p.Item1 + M[0, 1] * p.Item2 + M[0, 2], M[1, 0] * p.Item1 + M[1, 1] * p.Item2 + M[1, 2]);
            worstUv = Math.Max(worstUv, System.Numerics.Vector2.Distance(spec, sample.Apply(new System.Numerics.Vector2(p.Item1, p.Item2))));
        }

        t.Expect("UvTransform applies the spec's T * R * S", worstUv < 1e-5f, $"worst {worstUv}");

        // The extension channels: each cooks its TEXCOORD set (override applied) and transform, in the order
        // the engine's enum reads them — a fact in two places, so it is held to one here.
        var engineOrder = Enum.GetNames<PbrExtensionTexture>();
        var keyOf = new Dictionary<string, string>
        {
            ["Clearcoat"] = "ClearCoat", ["ClearcoatRoughness"] = "ClearCoatRoughness", ["ClearcoatNormal"] = "ClearCoatNormal",
            ["SheenColor"] = "SheenColor", ["SheenRoughness"] = "SheenRoughness", ["SpecularColor"] = "SpecularColor",
            ["Specular"] = "SpecularFactor", ["Transmission"] = "Transmission", ["Thickness"] = "VolumeThickness",
            ["Iridescence"] = "Iridescence", ["IridescenceThickness"] = "IridescenceThickness", ["Anisotropy"] = "Anisotropy",
            ["DiffuseTransmission"] = "DiffuseTransmissionFactor", ["DiffuseTransmissionColor"] = "DiffuseTransmissionColor",
        };
        t.Expect("the engine's extension-texture order is the cooked file's",
            engineOrder.Length == BlixMesh.ExtensionChannels.Count
            && engineOrder.Select((n, i) => keyOf.TryGetValue(n, out var k) && k == BlixMesh.ExtensionChannels[i]).All(x => x),
            string.Join(",", engineOrder));

        var extensionChannels = 0;
        var nonDefault = 0;
        var extensionWrong = new List<string>();
        foreach (var file in Directory.EnumerateFiles(root, "*.gl*", SearchOption.AllDirectories)
            .Where(f => (f.EndsWith(".glb", StringComparison.Ordinal) || f.EndsWith(".gltf", StringComparison.Ordinal))
                && !ExpectedRefusals.ContainsKey(Path.GetFileName(f)))
            .OrderBy(f => f, StringComparer.Ordinal))
        {
            var gltf = LoadSource(file);
            var cooked = BlixMeshReader.Read(CookCache.Resolve(file));
            for (var m = 0; m < gltf.LogicalMaterials.Count; m++)
            {
                for (var e = 0; e < BlixMesh.ExtensionChannels.Count; e++)
                {
                    if (gltf.LogicalMaterials[m].FindChannel(BlixMesh.ExtensionChannels[e]) is not { } c || c.Texture is null) continue;
                    extensionChannels++;
                    var want = new BlixMeshChannelUv(
                        c.TextureTransform?.TextureCoordinateOverride ?? c.TextureCoordinate,
                        c.TextureTransform is { } x ? new BlixMeshUvTransform(x.Offset, x.Rotation, x.Scale) : BlixMeshUvTransform.Identity);
                    if (want != BlixMeshChannelUv.Default) nonDefault++;
                    var got = cooked.MaterialTable[m].ExtensionUv is { } all ? all[e] : BlixMeshChannelUv.Default;
                    if (got != want) extensionWrong.Add($"{Path.GetFileName(file)} material {m} {BlixMesh.ExtensionChannels[e]}: cooked {got}, glTF {want}");
                }
            }
        }

        t.Expect($"every extension channel cooks its TEXCOORD set and transform ({extensionChannels} channels, {nonDefault} not the default)",
            extensionChannels > 0 && extensionWrong.Count == 0, string.Join(" | ", extensionWrong.Take(4)));
    }

    // ── Samplers as glTF defines them ──────────────────────────────────────────
    // Every textured core channel of every corpus file: the cooked row it reads carries exactly that
    // texture's glTF sampler, and the engine maps glTF's codes to its own as the spec defines them.
    // ── The demos' skinning, through the animated hierarchy ─────────────────────
    // The demos pack palettes gathered from the model's animated hierarchy, SkeletonPlacement * body baked in.
    // The other way to the same vertices: skin 0's own joint tree, resolved independently from its joints'
    // nodes (JointHierarchy over JointParents), posed and placed at Placement(0) * body. For these single-skin
    // rigs the hierarchy must keep skin 0's joint order (the compatibility convention), and every skinned
    // vertex must land in the same place both ways. Over each demo rig's first clips at five times.
    private static void DemoRigsSkinTheSameThroughModel(TestRunner t)
    {
        var body = System.Numerics.Matrix4x4.CreateScale(1.15f) * System.Numerics.Matrix4x4.CreateRotationY(0.7f)
                   * System.Numerics.Matrix4x4.CreateTranslation(3f, 0f, -2f);
        foreach (var name in new[] { "Rogue.blixmesh", "cesium_man.blixmesh", "enemy.blixmesh" })
        {
            if (FindFile(name) is not { } file) continue;
            var d = Blix.ModelData.Load(file, new Blix.ModelNeeds(Skinned: true));
            var hierarchy = d.Skeleton!;
            var skin = d.Skins[0].Binding;
            var meshes = d.SkinnedPrimitives(0).Select(p => p.Mesh).ToArray();
            t.Expect($"{name}: the hierarchy keeps skin 0's joint order (joint j is bone j)",
                skin.Bones.Select((b, j) => b == j).All(x => x) && skin.JointCount == hierarchy.BoneCount,
                string.Join(",", skin.Bones.Take(8)));
            var own = Blix.JointHierarchy.Resolve(
                Enumerable.Range(0, skin.JointCount).Select(j => hierarchy.Bones[skin.Bones[j]].Name).ToArray(),
                skin.JointParents(), d.Skins[0].JointNodes,
                n => d.Nodes[n].ParentIndex, n => d.Nodes[n].Local, n => d.World[n]);
            var ownSkeleton = new Blix.Skeleton(own.Bones.ToArray());
            var ownWorlds = new System.Numerics.Matrix4x4[ownSkeleton.BoneCount];
            var worst = 0f;
            var vertices = 0;
            var worlds = new Blix.BoneWorlds(hierarchy);
            foreach (var clip in d.Clips.Take(3))
            foreach (var at in new[] { 0.0, 0.25, 0.5, 0.75, 1.0 })
            {
                var pose = hierarchy.CreateRestPose();
                clip.Sample(clip.Duration * at, pose);
                // Skin 0's own joint tree, posed with the same locals (joint j is bone j), placed by its own frame.
                ownSkeleton.ComputeBoneWorlds(pose, ownWorlds);
                var oldPalette = new System.Numerics.Matrix4x4[skin.JointCount];
                for (var j = 0; j < skin.JointCount; j++) oldPalette[j] = skin.InverseBinds[j] * ownWorlds[j];
                var oldModel = own.Placement * body;
                // The demos' path: gathered through the hierarchy, placement baked in.
                worlds.Compute(pose);
                var gathered = new Blix.BonePaletteSet(skin, 1);
                gathered.Add(worlds, d.SkeletonPlacement * body);
                foreach (var m in meshes)
                for (var v = 0; v < m.VertexCount; v++)
                {
                    var o = v * m.Layout.Stride;
                    float F(int k) => BitConverter.ToSingle(m.VertexBytes, o + k);
                    var position = new System.Numerics.Vector3(F(0), F(4), F(8));
                    System.Numerics.Vector3 Skinned(System.Numerics.Matrix4x4[] palette)
                    {
                        var sum = System.Numerics.Vector3.Zero;
                        for (var k = 0; k < 4; k++)
                        {
                            var weight = F(48 + (k * 4));
                            if (weight != 0f) sum += System.Numerics.Vector3.Transform(position, palette[(int)F(32 + (k * 4))]) * weight;
                        }

                        return sum;
                    }

                    var before = System.Numerics.Vector3.Transform(Skinned(oldPalette), oldModel);
                    var after = Skinned(gathered.Matrices);
                    worst = MathF.Max(worst, System.Numerics.Vector3.Distance(before, after));
                    vertices++;
                }
            }

            var extent = meshes.Aggregate(0f, (e, m) => MathF.Max(e, (m.Bounds.Max - m.Bounds.Min).Length()));
            t.Expect($"{name}: skinned through Model as the demo drew it ({vertices} vertex-poses, worst {worst:0.######} for a {extent:0.##}-unit mesh)",
                vertices > 0 && worst <= 1e-4f * MathF.Max(1f, extent * 1.15f), "");
        }
    }

    // MeshDataExtensions.Merge (what the instanced demos bake their props with) under a mirroring
    // transform: each merged triangle's winding agrees with its normals, as Flattened's does. The
    // same NegativeScale corpus files, each mesh node merged at its world.
    private static void MergeKeepsFacesUnderMirrors(TestRunner t)
    {
        var files = new[] { "NegativeScaleTest.glb" }
            .Concat(Enumerable.Range(0, 13).Select(i => $"Node_NegativeScale_{i:00}.gltf"))
            .Select(FindFile).Where(f => f is not null).ToArray();
        if (files.Length == 0) return;
        int triangles = 0, agree = 0, nodes = 0;
        foreach (var file in files)
        {
            var d = Blix.ModelData.Load(CookCache.Resolve(file!), new Blix.ModelNeeds(Tangents: true));
            for (var n = 0; n < d.Nodes.Count; n++)
            {
                if (d.Nodes[n].MeshIndex < 0 || d.World[n].GetDeterminant() >= 0f) continue;
                nodes++;
                var merged = d.Meshes[d.Nodes[n].MeshIndex].Primitives.Select(p => (p.Mesh, d.World[n])).Merge("merged");
                var idx = merged.Indices32 ?? merged.Indices.Select(i => (uint)i).ToArray();
                for (var i = 0; i + 2 < idx.Length; i += 3)
                {
                    System.Numerics.Vector3 P(uint v, int at) => new(
                        BitConverter.ToSingle(merged.VertexBytes, ((int)v * merged.Layout.Stride) + at),
                        BitConverter.ToSingle(merged.VertexBytes, ((int)v * merged.Layout.Stride) + at + 4),
                        BitConverter.ToSingle(merged.VertexBytes, ((int)v * merged.Layout.Stride) + at + 8));
                    var face = System.Numerics.Vector3.Cross(P(idx[i + 1], 0) - P(idx[i], 0), P(idx[i + 2], 0) - P(idx[i], 0));
                    if (face.LengthSquared() < 1e-12f) continue;
                    triangles++;
                    if (System.Numerics.Vector3.Dot(face, P(idx[i], 12) + P(idx[i + 1], 12) + P(idx[i + 2], 12)) > 0f) agree++;
                }
            }
        }

        t.Expect($"a merge of mirrored nodes keeps glTF's front faces: {agree}/{triangles} triangles over {nodes} node(s)",
            triangles > 0 && agree >= triangles * 0.95, "");
    }

    // ── Validation, audited ──────────────────────────────────────────────────────
    // The cook validates every source with SharpGLTF's strict validator and reads past it for exactly two
    // named causes (MeshRecipe.ValidatorFallbacks): invalid glTF read on purpose (a skin with no common
    // root) and valid glTF the validator wrongly refuses (spare inverse binds). This holds the corpus to
    // that: every file the validator refuses is one Blix refuses too (ExpectedRefusals) or one listed here
    // with its cause — the cause the cook itself finds in it — and every listed one is still refused by the
    // validator, so the list cannot outlive its reason.
    private static readonly Dictionary<string, string> ReadLeniently = new(StringComparer.Ordinal)
    {
        ["Animation_Skin_06.gltf"] = MeshRecipe.LenientSkinWithoutCommonRoot,
        ["RiggedSimple_extraibm.gltf"] = MeshRecipe.ValidatorRefusesSpareInverseBinds,
    };

    private static void ValidatorRejectionsAreAccounted(TestRunner t)
    {
        var root = FindFile("InterpolationTest.glb") is { } it ? Path.GetFullPath(Path.Combine(it, "..", "..", "..")) : null;
        if (root is null) return;

        var rejected = new List<string>();
        var wrong = new List<string>();
        foreach (var file in Directory.EnumerateFiles(root, "*.gl*", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".glb", StringComparison.Ordinal) || f.EndsWith(".gltf", StringComparison.Ordinal))
            .OrderBy(f => f, StringComparer.Ordinal))
        {
            var name = Path.GetFileName(file);
            try
            {
                SharpGLTF.Schema2.ModelRoot.Load(file, new SharpGLTF.Schema2.ReadSettings { Validation = SharpGLTF.Validation.ValidationMode.Strict });
                if (ReadLeniently.ContainsKey(name)) wrong.Add($"{name} validates now — take it off the lenient list");
            }
            catch (SharpGLTF.Validation.ModelException)
            {
                rejected.Add(name);
                if (!ExpectedRefusals.ContainsKey(name) && !ReadLeniently.ContainsKey(name))
                    wrong.Add($"{name} is refused by the validator and neither refused nor declared lenient");
                if (ReadLeniently.TryGetValue(name, out var cause) && !MeshRecipe.ValidatorFallbacks(LoadSource(file)).Contains(cause))
                    wrong.Add($"{name} is listed for '{cause}', and the cook does not find that in it");
            }
        }

        t.Expect($"every file the strict validator refuses is refused by Blix or read past it for a named cause " +
                 $"({rejected.Count}: {string.Join(", ", rejected)})",
            wrong.Count == 0 && ReadLeniently.Keys.All(rejected.Contains), string.Join(" | ", wrong));
    }

    // glTF 2.0 §3.7.2.1's counts, on files the validator never sees (the lenient path reads unvalidated):
    // a triangle list whose index count is not a multiple of three, and a strip of two, are refused by
    // name rather than trimmed into something the file did not say.
    private static void IndexCountsAreRefusedNotRepaired(TestRunner t)
    {
        foreach (var (mode, count, label) in new[] { (4, 4, "a 4-index TRIANGLES list"), (5, 2, "a 2-index TRIANGLE_STRIP"), (6, 2, "a 2-index TRIANGLE_FAN") })
        {
            var positions = new float[] { 0, 0, 0, 1, 0, 0, 0, 1, 0, 1, 1, 0 };
            var indices = new ushort[] { 0, 1, 2, 3 }.Take(count).ToArray();
            var bytes = positions.SelectMany(BitConverter.GetBytes).Concat(indices.SelectMany(BitConverter.GetBytes)).ToArray();
            var json = $$"""
            {"asset":{"version":"2.0"},"scenes":[{"nodes":[0]}],"nodes":[{"mesh":0}],
             "meshes":[{"primitives":[{"attributes":{"POSITION":0},"indices":1,"mode":{{mode}}}]}],
             "buffers":[{"byteLength":{{bytes.Length}},"uri":"data:application/octet-stream;base64,{{Convert.ToBase64String(bytes)}}"}],
             "bufferViews":[{"buffer":0,"byteOffset":0,"byteLength":48},{"buffer":0,"byteOffset":48,"byteLength":{{count * 2}}}],
             "accessors":[{"bufferView":0,"componentType":5126,"count":4,"type":"VEC3","min":[0,0,0],"max":[1,1,0]},
                          {"bufferView":1,"componentType":5123,"count":{{count}},"type":"SCALAR"}]}
            """;
            var path = Path.Combine(Path.GetTempPath(), $"blix-indexcount-{mode}.gltf");
            File.WriteAllText(path, json);
            var gltf = LoadSource(path);
            string? refusal = null;
            try
            {
                Blix.Import.GltfStaticImporter.BuildStaticMeshData("probe", gltf.LogicalMeshes[0].Primitives[0],
                    System.Numerics.Matrix4x4.Identity, System.Numerics.Matrix4x4.Identity);
            }
            catch (InvalidOperationException e)
            {
                refusal = e.Message;
            }

            t.Expect($"{label} is refused by name, not trimmed", refusal is not null && refusal.Contains("glTF requires", StringComparison.Ordinal), refusal ?? "it loaded");
        }
    }

    // Generated tangents follow "the texture coordinates associated with the normal texture" (glTF 2.0
    // §3.7.2.1). NormalTangentMirrorTest_normaluv1 (derived by the fetch script) has no TANGENT and its
    // normal texture on a TEXCOORD_1 turned a quarter from TEXCOORD_0, so a frame over set 1 and a frame
    // over set 0 are 90 degrees apart. Each cooked tangent is held to its triangle's du direction in BOTH
    // sets, solved here from positions and UVs: it must follow set 1's, and the CONTROL is set 0's.
    private static void GeneratedTangentsFollowTheNormalTexture(TestRunner t)
    {
        if (FindFile("NormalTangentMirrorTest_normaluv1.gltf") is not { } file) return;
        var d = Blix.ModelData.Load(CookCache.Resolve(file), new Blix.ModelNeeds(Tangents: true, Colour: true));
        int triangles = 0, followsSet1 = 0, followsSet0 = 0;
        foreach (var (_, prim) in d.Flattened())
        {
            var m = prim.Mesh;
            var idx = m.Indices32 ?? m.Indices.Select(i => (uint)i).ToArray();
            for (var i = 0; i + 2 < idx.Length; i += 3)
            {
                var c = new[] { idx[i], idx[i + 1], idx[i + 2] };
                var p = c.Select(v => Read3(m, v, 0)).ToArray();
                var tangent = Read3(m, c[0], 24);
                var t1 = Du(p, c.Select(v => Read2(m, v, 48)).ToArray());
                var t0 = Du(p, c.Select(v => Read2(m, v, 40)).ToArray());
                if (t1 is not { } a || t0 is not { } b || tangent.LengthSquared() < 1e-8f) continue;
                triangles++;
                var n = System.Numerics.Vector3.Normalize(tangent);
                if (System.Numerics.Vector3.Dot(n, a) > 0.7f) followsSet1++;
                if (System.Numerics.Vector3.Dot(n, b) > 0.7f) followsSet0++;
            }
        }

        t.Expect($"generated tangents follow the normal texture's TEXCOORD_1 on {followsSet1}/{triangles} triangles " +
                 $"(CONTROL, TEXCOORD_0's direction: {followsSet0}/{triangles})",
            triangles > 0 && followsSet1 >= triangles * 0.9 && followsSet0 <= triangles * 0.1, "");

        static System.Numerics.Vector3 Read3(Blix.Assets.MeshData m, uint v, int at)
        {
            var o = ((int)v * m.Layout.Stride) + at;
            return new(BitConverter.ToSingle(m.VertexBytes, o), BitConverter.ToSingle(m.VertexBytes, o + 4), BitConverter.ToSingle(m.VertexBytes, o + 8));
        }

        static System.Numerics.Vector2 Read2(Blix.Assets.MeshData m, uint v, int at)
        {
            var o = ((int)v * m.Layout.Stride) + at;
            return new(BitConverter.ToSingle(m.VertexBytes, o), BitConverter.ToSingle(m.VertexBytes, o + 4));
        }

        // The direction u increases across the triangle (the standard tangent-space solve), unit; null when degenerate.
        static System.Numerics.Vector3? Du(System.Numerics.Vector3[] p, System.Numerics.Vector2[] uv)
        {
            var e1 = p[1] - p[0]; var e2 = p[2] - p[0];
            var d1 = uv[1] - uv[0]; var d2 = uv[2] - uv[0];
            var det = (d1.X * d2.Y) - (d2.X * d1.Y);
            if (MathF.Abs(det) < 1e-10f) return null;
            var du = ((e1 * d2.Y) - (e2 * d1.Y)) / det;
            return du.LengthSquared() < 1e-12f ? null : System.Numerics.Vector3.Normalize(du);
        }
    }

    // KHR_mesh_quantization was accepted with nothing exercising it. NormalTangentTest_quantized (derived by the
    // fetch script) is NormalTangentTest re-encoded as an optimiser writes it: POSITION as unsigned shorts with the
    // dequantisation on the node, NORMAL as normalized bytes, TEXCOORD_0 as normalized unsigned shorts. Placed in
    // the world, every vertex must be the float file's to within one quantisation step. CONTROL: the quantized
    // file's own (node-local) positions are nowhere near the floats, so it is the node transform that agrees.
    private static void QuantizedAttributesReadAsTheirFloats(TestRunner t)
    {
        if (FindFile("NormalTangentTest_quantized.gltf") is not { } quantized || FindFile("NormalTangentTest.glb") is not { } plain) return;
        var q = Blix.ModelData.Load(CookCache.Resolve(quantized));
        var f = Blix.ModelData.Load(CookCache.Resolve(plain));
        var qm = q.Flattened().Single().Primitive.Mesh;
        var fm = f.Flattened().Single().Primitive.Mesh;
        var local = q.Meshes.Single().Primitives.Single().Mesh;
        t.Expect("a quantized file cooks to the float file's vertex and index counts",
            qm.VertexCount == fm.VertexCount && qm.IndexCount == fm.IndexCount, $"{qm.VertexCount}/{qm.IndexCount} vs {fm.VertexCount}/{fm.IndexCount}");
        if (qm.VertexCount != fm.VertexCount) return;

        int At(Blix.Assets.MeshData m, int location) => m.Layout.Attributes.First(a => a.Location == location).Offset;
        System.Numerics.Vector3 V3(Blix.Assets.MeshData m, int v, int at)
        {
            var o = (v * m.Layout.Stride) + at;
            return new(BitConverter.ToSingle(m.VertexBytes, o), BitConverter.ToSingle(m.VertexBytes, o + 4), BitConverter.ToSingle(m.VertexBytes, o + 8));
        }

        System.Numerics.Vector2 V2(Blix.Assets.MeshData m, int v, int at)
        {
            var o = (v * m.Layout.Stride) + at;
            return new(BitConverter.ToSingle(m.VertexBytes, o), BitConverter.ToSingle(m.VertexBytes, o + 4));
        }

        var extent = fm.Bounds.Max - fm.Bounds.Min;
        var step = MathF.Max(extent.X, MathF.Max(extent.Y, extent.Z)) / 65535f;
        float worstPosition = 0f, worstNormal = 0f, worstUv = 0f, worstRaw = 0f;
        for (var v = 0; v < fm.VertexCount; v++)
        {
            var fp = V3(fm, v, At(fm, 0));
            worstPosition = MathF.Max(worstPosition, (V3(qm, v, At(qm, 0)) - fp).Length());
            worstRaw = MathF.Max(worstRaw, (V3(local, v, At(local, 0)) - fp).Length());
            worstNormal = MathF.Max(worstNormal, (V3(qm, v, At(qm, 1)) - V3(fm, v, At(fm, 1))).Length());
            worstUv = MathF.Max(worstUv, (V2(qm, v, At(qm, 2)) - V2(fm, v, At(fm, 2))).Length());
        }

        t.Expect($"its positions, placed by the node's dequantisation, are the floats' within a step (worst {worstPosition:G3}, step {step:G3})",
            worstPosition <= step, "");
        t.Expect($"its normalized-byte normals are the floats' within a byte's precision (worst {worstNormal:G3})",
            worstNormal <= 0.015f, "");
        t.Expect($"its normalized-short UVs are the floats' within a short's precision (worst {worstUv:G3})",
            worstUv <= 2f / 65535f, "");
        t.Expect($"CONTROL: its node-local positions are the raw integers, nowhere near the floats (worst {worstRaw:G3})",
            worstRaw > 100f, "");
    }

    // A normal map under KHR_texture_transform is read in [T B] F M^-1 F (studio_lit.frag, normalMapFrame): the cooked
    // frame is MikkTSpace's over (u, 1 - v) (blix_mikk.c), so glTF's M is taken into those axes. This pins that
    // convention where the shader cannot see it. NormalTangentMirrorTest_uvxf (derived) samples the reference's texels
    // through (M, o) — 30 degrees, scale (1.5, -1.5) — from a TEXCOORD_1 of M^-1 (uv - o); its tangents are generated
    // over that set. On the flat normal-mapped tiles the correction must give the reference's frame exactly; CONTROL,
    // the plain [T B] M^-1 (no F) gives it nowhere. (Curved geometry is excluded: there MikkTSpace averages frames
    // across an uneven mapping, and no single 2x2 relates the two cooks.)
    private static void NormalMapFramesFollowTheTextureTransform(TestRunner t)
    {
        if (FindFile("NormalTangentMirrorTest_uvxf.gltf") is not { } transformed || FindFile("NormalTangentMirrorTest_uvxf_ref.gltf") is not { } reference) return;
        Blix.Assets.MeshData MeshOf(string f) => Blix.ModelData.Load(CookCache.Resolve(f), new Blix.ModelNeeds(Tangents: true, Colour: true)).Flattened().Single().Primitive.Mesh;
        var r = MeshOf(reference);
        var x = MeshOf(transformed);
        var sr = Blix.Graphics.VertexSemantics.Of(r.Layout);
        var sx = Blix.Graphics.VertexSemantics.Of(x.Layout);
        System.Numerics.Vector3 V3(Blix.Assets.MeshData m, int v, int at)
        {
            var o = (v * m.Layout.Stride) + at;
            return new(BitConverter.ToSingle(m.VertexBytes, o), BitConverter.ToSingle(m.VertexBytes, o + 4), BitConverter.ToSingle(m.VertexBytes, o + 8));
        }

        float W(Blix.Assets.MeshData m, int v, int at) => BitConverter.ToSingle(m.VertexBytes, (v * m.Layout.Stride) + at + 12);
        (int, int, int, int, int) Key(Blix.Assets.MeshData m, int v, Blix.Graphics.VertexSemantics s)
        {
            var p = V3(m, v, s.Position);
            var o = (v * m.Layout.Stride) + s.Uv0;
            return ((int)MathF.Round(p.X * 1e4f), (int)MathF.Round(p.Y * 1e4f), (int)MathF.Round(p.Z * 1e4f),
                (int)MathF.Round(BitConverter.ToSingle(m.VertexBytes, o) * 1e5f), (int)MathF.Round(BitConverter.ToSingle(m.VertexBytes, o + 4) * 1e5f));
        }

        // M exactly as the reader builds it (UvTransform.Rows), so the test inverts what the shader is handed.
        var (ru, rv) = new Blix.UvTransform(new System.Numerics.Vector2(0.25f, 0.1f), MathF.PI / 6f, new System.Numerics.Vector2(1.5f, -1.5f)).Rows;
        var det = (ru.X * rv.Y) - (ru.Y * rv.X);
        float i00 = rv.Y / det, i01 = -ru.Y / det, i10 = -rv.X / det, i11 = ru.X / det;
        var index = new Dictionary<(int, int, int, int, int), int>();
        for (var v = 0; v < r.VertexCount; v++) index.TryAdd(Key(r, v, sr), v);

        int flat = 0, withF = 0, withoutF = 0;
        for (var v = 0; v < x.VertexCount; v++)
        {
            if (!index.TryGetValue(Key(x, v, sx), out var w)) continue;
            var n = V3(x, v, sx.Normal);
            if (MathF.Abs(n.Z) < 0.999f) continue;
            flat++;
            var tx = V3(x, v, sx.Tangent);
            var bx = System.Numerics.Vector3.Cross(n, tx) * W(x, v, sx.Tangent);
            var tr = V3(r, w, sr.Tangent);
            var br = System.Numerics.Vector3.Cross(n, tr) * W(r, w, sr.Tangent);
            bool Same(System.Numerics.Vector3 a, System.Numerics.Vector3 b) => System.Numerics.Vector3.Dot(System.Numerics.Vector3.Normalize(a), b) > 0.999f;
            if (Same((tx * i00) - (bx * i10), tr) && Same((-tx * i01) + (bx * i11), br)) withF++;
            if (Same((tx * i00) + (bx * i10), tr) && Same((tx * i01) + (bx * i11), br)) withoutF++;
        }

        t.Expect($"under a texture transform, [T B] F M^-1 F is the map's frame on {withF}/{flat} flat normal-mapped vertices " +
                 $"(CONTROL, without F: {withoutF}/{flat})",
            flat > 100 && withF == flat && withoutF == 0, "");

        // A NON-conformal transform (30 degrees, scale (2, 0.5)), built as a real asset is: the attribute is the
        // artist's (conformal) UV, the transform stretches the map, and the reference precomputes M uv + o with no
        // transform, so its generated tangents are MikkTSpace over the transformed coordinates. The corrected
        // vectors are then no longer perpendicular: the frame is Gram-Schmidt of [T B] F M^-1 F (the tangent
        // normalised, the bitangent cross(N, T') on the transformed bitangent's side), and it must be the reference's
        // exactly. CONTROL: normalising each vector separately, which is not a frame, is not.
        if (FindFile("NormalTangentMirrorTest_uvxf2.gltf") is not { } stretched || FindFile("NormalTangentMirrorTest_uvxf2_ref.gltf") is not { } stretchedRef) return;
        var r2 = MeshOf(stretchedRef);
        var x2 = MeshOf(stretched);
        var sr2 = Blix.Graphics.VertexSemantics.Of(r2.Layout);
        var sx2 = Blix.Graphics.VertexSemantics.Of(x2.Layout);
        var (su, sv) = new Blix.UvTransform(new System.Numerics.Vector2(0.25f, 0.1f), MathF.PI / 6f, new System.Numerics.Vector2(2f, 0.5f)).Rows;
        var det2 = (su.X * sv.Y) - (su.Y * sv.X);
        float j00 = sv.Y / det2, j01 = -su.Y / det2, j10 = -sv.X / det2, j11 = su.X / det2;
        var index2 = new Dictionary<(int, int, int, int, int), int>();
        for (var v = 0; v < r2.VertexCount; v++) index2.TryAdd(Key(r2, v, sr2), v);
        int flat2 = 0, orthonormal = 0, eachNormalised = 0;
        for (var v = 0; v < x2.VertexCount; v++)
        {
            if (!index2.TryGetValue(Key(x2, v, sx2), out var w)) continue;
            var n = V3(x2, v, sx2.Normal);
            if (MathF.Abs(n.Z) < 0.999f) continue;
            flat2++;
            var tx = V3(x2, v, sx2.Tangent);
            var bx = System.Numerics.Vector3.Cross(n, tx) * W(x2, v, sx2.Tangent);
            var tr = V3(r2, w, sr2.Tangent);
            var br = System.Numerics.Vector3.Cross(n, tr) * W(r2, w, sr2.Tangent);
            var t0 = (tx * j00) - (bx * j10);
            var t1 = (-tx * j01) + (bx * j11);
            var gt = System.Numerics.Vector3.Normalize(t0 - (n * System.Numerics.Vector3.Dot(n, t0)));
            var gb = System.Numerics.Vector3.Cross(n, gt) * (System.Numerics.Vector3.Dot(System.Numerics.Vector3.Cross(n, gt), t1) < 0f ? -1f : 1f);
            bool Same(System.Numerics.Vector3 a, System.Numerics.Vector3 b) => System.Numerics.Vector3.Dot(System.Numerics.Vector3.Normalize(a), b) > 0.999f;
            if (Same(gt, tr) && Same(gb, br)) orthonormal++;
            if (Same(t0, tr) && Same(t1, br)) eachNormalised++;
        }

        t.Expect($"under a non-conformal transform, the orthonormalised frame is the map's on {orthonormal}/{flat2} flat vertices " +
                 $"(CONTROL, each vector normalised separately: {eachNormalised}/{flat2})",
            flat2 > 100 && orthonormal == flat2 && eachNormalised < flat2 / 2, "");
    }

    // Every message in an exception's chain, joined: what a refusal SAYS, without its stack trace, so a reason token
    // cannot match a method or file name in a frame.
    private static string MessagesOf(Exception e)
    {
        var parts = new List<string>();
        for (Exception? at = e; at is not null; at = at.InnerException) parts.Add(at.Message);
        return string.Join(" | ", parts);
    }

    // One source policy (GltfSourcePolicy): what the cook refuses, the source importers refuse too, or a file the
    // shipped path calls a different shape would load as its base mesh through a documented API.
    private static void EveryImporterRefusesWhatTheCookRefuses(TestRunner t)
    {
        if (FindFile("AnimatedMorphCube.glb") is not { } morphing || FindFile("SimpleMorph_nodezero.gltf") is not { } zeroed) return;
        var importers = new (string Name, Func<string, object> Import)[]
        {
            ("GltfStaticImporter.Import", f => new GltfStaticImporter().Import(new AssetImportContext(AssetId.Parse("k3/static"), f))),
            ("GltfStaticImporter.ImportNodes", f => new GltfStaticImporter().ImportNodes(new AssetImportContext(AssetId.Parse("k3/nodes"), f))),
            ("GltfImporter.Import", f => new GltfImporter().Import(new AssetImportContext(AssetId.Parse("k3/rig"), f))),
        };
        foreach (var (name, import) in importers)
        {
            string? refusal = null;
            try { import(morphing); }
            catch (AssetImportException refused) { refusal = MessagesOf(refused); }
            t.Expect($"{name} refuses morph targets that take effect, as the cook does",
                refusal is not null && refusal.Contains("morph targets", StringComparison.Ordinal), refusal ?? "it loaded");
        }

        // CONTROL: a file whose every instance is its base is not refused for morphing by the static importers. (The
        // rigged importer refuses it for having no skin, which is its own rule.)
        foreach (var (name, import) in importers.Take(2))
        {
            string? refusal = null;
            try { import(zeroed); }
            catch (AssetImportException refused) { refusal = MessagesOf(refused); }
            t.Expect($"CONTROL: {name} reads a morph file whose every instance is its base",
                refusal is null, refusal ?? "");
        }
    }

    // ── A golden that shares nothing with the reader ─────────────────────────────
    // SamplingMatchesGltf and AnimationMatchesGltf evaluate keys independently, but both take the keys
    // through GltfImporter.SampleKeys — so a mis-read of a CUBICSPLINE triple (in, value, out) would sit
    // on both sides. This one decodes InterpolationTest's accessors from the .glb's own bytes, applies
    // glTF Appendix C by hand, and holds Blix's sampled pose to it; plus one literal from the spec
    // computed by hand (CubicSpline Rotation at t = 1.9).
    private static void InterpolationGolden(TestRunner t)
    {
        if (FindFile("InterpolationTest.glb") is not { } file) return;
        var bytes = File.ReadAllBytes(file);
        var jsonLength = (int)BitConverter.ToUInt32(bytes, 12);
        var json = System.Text.Json.Nodes.JsonNode.Parse(bytes.AsSpan(20, jsonLength))!;
        var binAt = 20 + jsonLength + 8;

        float[] Floats(int accessor, int components)
        {
            var a = json["accessors"]![accessor]!;
            if (a["componentType"]!.GetValue<int>() != 5126) throw new InvalidDataException("InterpolationTest changed shape: not float");
            var view = json["bufferViews"]![a["bufferView"]!.GetValue<int>()]!;
            var start = binAt + (view["byteOffset"]?.GetValue<int>() ?? 0) + (a["byteOffset"]?.GetValue<int>() ?? 0);
            var stride = view["byteStride"]?.GetValue<int>() ?? components * 4;
            var count = a["count"]!.GetValue<int>();
            var result = new float[count * components];
            for (var k = 0; k < count; k++)
            for (var c = 0; c < components; c++) result[(k * components) + c] = BitConverter.ToSingle(bytes, start + (k * stride) + (c * 4));
            return result;
        }

        var d = Blix.ModelData.Load(CookCache.Resolve(file));
        var worst = 0f;
        var where = "";
        var checkedPoints = 0;
        System.Numerics.Quaternion? at19 = null;
        foreach (var anim in json["animations"]!.AsArray())
        {
            var channel = anim!["channels"]![0]!;
            var sampler = anim["samplers"]![channel["sampler"]!.GetValue<int>()]!;
            var path = channel["target"]!["path"]!.GetValue<string>();
            var nodeName = json["nodes"]![channel["target"]!["node"]!.GetValue<int>()]!["name"]?.GetValue<string>();
            var mode = sampler["interpolation"]?.GetValue<string>() ?? "LINEAR";
            var width = path == "rotation" ? 4 : 3;
            var times = Floats(sampler["input"]!.GetValue<int>(), 1);
            var values = Floats(sampler["output"]!.GetValue<int>(), width);
            var clip = d.Clips.First(c => c.Name == anim["name"]!.GetValue<string>());
            var bone = d.SkeletonNodes.ToList().IndexOf(d.FindNode(nodeName!));
            var pose = d.Skeleton!.CreateRestPose();
            for (var step = 0; step <= 40; step++)
            {
                var time = times[0] + ((times[^1] - times[0]) * step / 40f);
                if (step == 40) time = 1.9f;
                var want = Spec(time);
                clip.Sample(time, pose);
                var local = pose.Locals[bone];
                var got = path switch
                {
                    "translation" => new[] { local.Translation.X, local.Translation.Y, local.Translation.Z },
                    "scale" => new[] { local.Scale.X, local.Scale.Y, local.Scale.Z },
                    _ => new[] { local.Rotation.X, local.Rotation.Y, local.Rotation.Z, local.Rotation.W },
                };
                // q and -q are one rotation.
                var sign = width == 4 && got.Zip(want).Sum(x => x.First * x.Second) < 0f ? -1f : 1f;
                var e = got.Zip(want).Max(x => MathF.Abs((x.First * sign) - x.Second));
                checkedPoints++;
                if (e > worst) (worst, where) = (e, $"{anim["name"]} {mode} at t={time:0.###}: [{string.Join(", ", got)}] vs [{string.Join(", ", want)}]");
                if (mode == "CUBICSPLINE" && path == "rotation" && step == 40) at19 = new System.Numerics.Quaternion(want[0], want[1], want[2], want[3]);
            }

            float[] Spec(float time)
            {
                var n = times.Length;
                float[] Key(int k, int part) => mode == "CUBICSPLINE"
                    ? values.Skip(((k * 3) + part) * width).Take(width).ToArray()   // (in a_k, value v_k, out b_k)
                    : values.Skip(k * width).Take(width).ToArray();
                const int Value = 1;
                int VPart() => mode == "CUBICSPLINE" ? Value : 0;
                if (time <= times[0]) return Normalised(Key(0, VPart()));
                if (time >= times[^1]) return Normalised(Key(n - 1, VPart()));
                var k = 0;
                while (time >= times[k + 1]) k++;
                var td = times[k + 1] - times[k];
                var s = (time - times[k]) / td;
                switch (mode)
                {
                    case "STEP":
                        return Key(k, 0);
                    case "CUBICSPLINE":
                    {
                        var v0 = Key(k, 1); var b0 = Key(k, 2); var a1 = Key(k + 1, 0); var v1 = Key(k + 1, 1);
                        var s2 = s * s; var s3 = s2 * s;
                        return Normalised(Enumerable.Range(0, width).Select(c =>
                            (((2 * s3) - (3 * s2) + 1) * v0[c]) + (td * (s3 - (2 * s2) + s) * b0[c])
                            + (((-2 * s3) + (3 * s2)) * v1[c]) + (td * (s3 - s2) * a1[c])).ToArray());
                    }
                    default:
                    {
                        var v0 = Key(k, 0); var v1 = Key(k + 1, 0);
                        if (width == 3) return Enumerable.Range(0, 3).Select(c => v0[c] + ((v1[c] - v0[c]) * s)).ToArray();
                        // Appendix C: slerp, the shorter way round.
                        var q0 = new System.Numerics.Quaternion(v0[0], v0[1], v0[2], v0[3]);
                        var q1 = new System.Numerics.Quaternion(v1[0], v1[1], v1[2], v1[3]);
                        var dot = System.Numerics.Quaternion.Dot(q0, q1);
                        if (dot < 0f) { q1 = -q1; dot = -dot; }
                        var angle = MathF.Acos(Math.Clamp(dot, -1f, 1f));
                        var q = angle < 1e-6f ? q0
                            : (q0 * (MathF.Sin((1 - s) * angle) / MathF.Sin(angle))) + (q1 * (MathF.Sin(s * angle) / MathF.Sin(angle)));
                        return new[] { q.X, q.Y, q.Z, q.W };
                    }
                }
            }

            float[] Normalised(float[] v)
            {
                if (width != 4) return v;
                var length = MathF.Sqrt(v.Sum(x => x * x));
                return v.Select(x => x / length).ToArray();
            }
        }

        t.Expect($"InterpolationTest's nine channels, decoded from its own bytes and evaluated by Appendix C, are what Blix samples " +
                 $"({checkedPoints} points, worst {worst:0.######})", checkedPoints > 0 && worst < 1e-4f, where);
        t.Expect($"and CubicSpline Rotation at t=1.9 is the hand-computed (z -0.999966, w -0.008266): {at19}",
            at19 is { } q && MathF.Abs(MathF.Abs(q.Z) - 0.999966f) < 2e-5f && MathF.Abs(MathF.Abs(q.W) - 0.008266f) < 2e-5f
            && MathF.Sign(q.Z) == MathF.Sign(q.W), "");
    }

    // ── The scene level, as glTF states it ──────────────────────────────────────
    // Every corpus file's scenes (roots and default), each node's KHR_node_visibility flag, camera and
    // light, the camera and light tables, EXT_mesh_gpu_instancing transforms rebuilt from the raw
    // accessors by the extension's T * R * S, and KHR_materials_variants mappings read from the JSON
    // mapping-by-mapping — held to what the cook wrote.
    private static void SceneLevelMatchesGltf(TestRunner t)
    {
        var root = FindFile("InterpolationTest.glb") is { } it ? Path.GetFullPath(Path.Combine(it, "..", "..", "..")) : null;
        if (root is null) return;

        int scenes = 0, hidden = 0, instances = 0, cameras = 0, lights = 0, mapped = 0;
        var wrong = new List<string>();
        foreach (var file in Directory.EnumerateFiles(root, "*.gl*", SearchOption.AllDirectories)
            .Where(f => (f.EndsWith(".glb", StringComparison.Ordinal) || f.EndsWith(".gltf", StringComparison.Ordinal))
                && !ExpectedRefusals.ContainsKey(Path.GetFileName(f)))
            .OrderBy(f => f, StringComparer.Ordinal))
        {
            var name = Path.GetFileName(file);
            var gltf = LoadSource(file);
            var cooked = BlixMeshReader.Read(CookCache.Resolve(file));
            // Cooked nodes are reordered parents-first, by the cook's rule: each logical node in order, its
            // ancestors placed before it. Rebuilt here, and held to the names, so the mapping is not assumed.
            var order = new List<int>();
            void Place(SharpGLTF.Schema2.Node n)
            {
                if (order.Contains(n.LogicalIndex)) return;
                if (n.VisualParent is { } parent) Place(parent);
                order.Add(n.LogicalIndex);
            }

            foreach (var n in gltf.LogicalNodes) Place(n);
            var cookedOf = new Dictionary<int, int>();
            for (var i = 0; i < order.Count; i++) cookedOf[order[i]] = i;
            int? Cooked(SharpGLTF.Schema2.Node n) =>
                cookedOf.TryGetValue(n.LogicalIndex, out var i) && i < cooked.Nodes.Count
                && cooked.Nodes[i].Name == (n.Name ?? $"node_{n.LogicalIndex}") ? i : null;
            if (gltf.LogicalNodes.Any(n => Cooked(n) is null)) wrong.Add($"{name}: the cooked node order is not the cook's rule");

            if (cooked.SceneTable.Count != gltf.LogicalScenes.Count) wrong.Add($"{name}: {cooked.SceneTable.Count} scene(s), glTF {gltf.LogicalScenes.Count}");
            for (var sc = 0; sc < Math.Min(cooked.SceneTable.Count, gltf.LogicalScenes.Count); sc++)
            {
                scenes++;
                var want = gltf.LogicalScenes[sc].VisualChildren.Select(Cooked).ToArray();
                if (want.All(w => w is not null) && !want.Select(w => w!.Value).SequenceEqual(cooked.SceneTable[sc].Roots))
                    wrong.Add($"{name} scene {sc}: roots {string.Join(",", cooked.SceneTable[sc].Roots)}, glTF {string.Join(",", want)}");
            }

            if (cooked.DefaultScene != (gltf.DefaultScene?.LogicalIndex ?? -1)) wrong.Add($"{name}: default scene {cooked.DefaultScene}");

            foreach (var node in gltf.LogicalNodes)
            {
                if (Cooked(node) is not { } c) continue;
                var row = cooked.Nodes[c];
                var visible = !node.TryGetVisibility(out var v) || v;
                if (!visible) hidden++;
                if (row.Visible != visible) wrong.Add($"{name} node '{row.Name}': visible {row.Visible}, glTF {visible}");
                if (row.CameraIndex != (node.Camera?.LogicalIndex ?? -1)) wrong.Add($"{name} node '{row.Name}': camera {row.CameraIndex}");
                if (row.LightIndex != (node.PunctualLight?.LogicalIndex ?? -1)) wrong.Add($"{name} node '{row.Name}': light {row.LightIndex}");

                var inst = node.GetGpuInstancing();
                if ((inst is { Count: > 0 }) != (row.Instances is not null)) { wrong.Add($"{name} node '{row.Name}': instancing presence"); continue; }
                if (inst is not { Count: > 0 }) continue;
                var tr = inst.GetAccessor("TRANSLATION")?.AsVector3Array();
                var ro = inst.GetAccessor("ROTATION")?.AsQuaternionArray();
                var sca = inst.GetAccessor("SCALE")?.AsVector3Array();
                for (var k = 0; k < inst.Count; k++)
                {
                    instances++;
                    // Row-vector T * R * S is S * R * T.
                    var want = System.Numerics.Matrix4x4.CreateScale(sca?[k] ?? System.Numerics.Vector3.One)
                        * System.Numerics.Matrix4x4.CreateFromQuaternion(ro?[k] ?? System.Numerics.Quaternion.Identity)
                        * System.Numerics.Matrix4x4.CreateTranslation(tr?[k] ?? System.Numerics.Vector3.Zero);
                    if (!Near(row.Instances![k], want)) { wrong.Add($"{name} node '{row.Name}' instance {k}"); break; }
                }
            }

            for (var ci = 0; ci < gltf.LogicalCameras.Count; ci++)
            {
                cameras++;
                var c = cooked.CameraTable[ci];
                var ok = gltf.LogicalCameras[ci].Settings switch
                {
                    SharpGLTF.Schema2.CameraPerspective p => !c.Orthographic && c.YFov == p.VerticalFOV && c.AspectRatio == (p.AspectRatio ?? 0f) && c.ZNear == p.ZNear,
                    SharpGLTF.Schema2.CameraOrthographic o => c.Orthographic && c.XMag == o.XMag && c.YMag == o.YMag && c.ZNear == o.ZNear && c.ZFar == o.ZFar,
                    _ => false,
                };
                if (!ok) wrong.Add($"{name} camera {ci}: {c}");
            }

            for (var li = 0; li < gltf.LogicalPunctualLights.Count; li++)
            {
                lights++;
                var l = gltf.LogicalPunctualLights[li];
                var c = cooked.LightTable[li];
                var range = l.Range > 0f && float.IsFinite(l.Range) ? l.Range : float.PositiveInfinity;
                if (c.Color != l.Color || c.Intensity != l.Intensity || c.Range != range) wrong.Add($"{name} light {li}: {c}");
            }

            // Variants: each mapping says "these variants give this primitive that material".
            var json = System.Text.Json.Nodes.JsonNode.Parse(GltfJson(file));
            var meshes = json?["meshes"]?.AsArray();
            var variantCount = (json?["extensions"]?["KHR_materials_variants"]?["variants"] as System.Text.Json.Nodes.JsonArray)?.Count ?? 0;
            if (cooked.VariantTable.Count != variantCount) wrong.Add($"{name}: {cooked.VariantTable.Count} variant(s), JSON {variantCount}");
            if (variantCount == 0 || meshes is null) continue;
            foreach (var node in gltf.LogicalNodes)
            {
                if (node.Mesh is null || Cooked(node) is not { } c) continue;
                var cookedMesh = cooked.Meshes[cooked.Nodes[c].MeshIndex];
                // Primitives are split into chunks by the cook; every chunk of a source primitive keeps its mapping.
                foreach (var prim in node.Mesh.Primitives)
                {
                    var want = Enumerable.Repeat(-1, variantCount).ToArray();
                    foreach (var mapping in meshes[node.Mesh.LogicalIndex]?["primitives"]?[prim.LogicalIndex]?["extensions"]?["KHR_materials_variants"]?["mappings"]?.AsArray()
                        ?? new System.Text.Json.Nodes.JsonArray())
                    foreach (var vi in mapping!["variants"]!.AsArray()) want[vi!.GetValue<int>()] = mapping["material"]!.GetValue<int>();
                    var chunks = cookedMesh.Primitives.Where(p => p.MaterialIndex == (prim.Material?.LogicalIndex ?? -1)).ToArray();
                    foreach (var chunk in chunks)
                    {
                        mapped++;
                        if (chunk.VariantMaterials is not { } got || !got.SequenceEqual(want))
                            wrong.Add($"{name} '{chunk.Name}': variants {(chunk.VariantMaterials is null ? "none" : string.Join(",", chunk.VariantMaterials))}, glTF {string.Join(",", want)}");
                    }
                }
            }
        }

        t.Expect($"every scene, visibility flag, instance, camera, light and variant mapping is the file's " +
                 $"({scenes} scenes, {hidden} hidden nodes, {instances} instances, {cameras} cameras, {lights} lights, {mapped} mapped primitives)",
            wrong.Count == 0 && hidden > 0 && instances > 0 && cameras > 0 && lights > 0 && mapped > 0,
            string.Join(" | ", wrong.Take(6)));

        static bool Near(System.Numerics.Matrix4x4 a, System.Numerics.Matrix4x4 b)
        {
            for (var r = 0; r < 4; r++)
            for (var c = 0; c < 4; c++)
            {
                if (MathF.Abs(a[r, c] - b[r, c]) > 1e-4f) return false;
            }

            return true;
        }
    }

    // A .gltf is its JSON; a .glb holds it as its first chunk.
    private static byte[] GltfJson(string path)
    {
        var bytes = File.ReadAllBytes(path);
        return bytes.Length >= 20 && BitConverter.ToUInt32(bytes, 0) == 0x46546C67
            ? bytes.AsSpan(20, (int)BitConverter.ToUInt32(bytes, 12)).ToArray()
            : bytes;
    }

    // What ModelData makes of the scene level: one scene placed, hidden nodes not flattened, an instanced
    // node flattened once per instance. The expected sets are walked here from the source by recursion
    // over each scene's roots — not the forward pass ModelData uses.
    private static void SceneLevelReachesModelData(TestRunner t)
    {
        if (FindFile("MultipleScenes.gltf") is { } scenes)
        {
            var cooked = CookCache.Resolve(scenes);
            var byDefault = Blix.ModelData.Load(cooked);
            var first = Blix.ModelData.Load(cooked, scene: 0);
            string Placed(Blix.ModelData d) => string.Join(",", Enumerable.Range(0, d.Nodes.Count).Where(d.IsPlaced).Select(n => d.Nodes[n].Name));
            t.Expect($"MultipleScenes places its default scene 1 alone ({Placed(byDefault)}), and scene 0 when asked ({Placed(first)})",
                byDefault.SceneIndex == 1 && first.SceneIndex == 0 && byDefault.Flattened().Count() == 1 && first.Flattened().Count() == 1
                && Placed(byDefault) != Placed(first),
                $"default {byDefault.SceneIndex}");
            var refused = false;
            try { Blix.ModelData.Load(cooked, scene: 2); }
            catch (AssetImportException) { refused = true; }
            t.Expect("a scene the file does not have is refused by name", refused, "");
        }

        if (FindFile("NodeVisibilityTest.glb") is { } vis)
        {
            var gltf = LoadSource(vis);
            var wantShown = new HashSet<string>();
            void Walk(SharpGLTF.Schema2.Node n, bool parentShown)
            {
                var shown = parentShown && (!n.TryGetVisibility(out var v) || v);
                if (shown && n.Mesh is not null) wantShown.Add($"{n.LogicalIndex}");
                foreach (var c in n.VisualChildren) Walk(c, shown);
            }

            foreach (var r in gltf.DefaultScene.VisualChildren) Walk(r, true);
            var d = Blix.ModelData.Load(CookCache.Resolve(vis));
            var shownMeshes = d.Flattened().Select(x => x.NodeIndex).Distinct().Count();
            var hiddenMeshes = Enumerable.Range(0, d.Nodes.Count).Count(n => d.Nodes[n].MeshIndex >= 0 && !d.IsShown(n));
            t.Expect($"NodeVisibilityTest flattens exactly the mesh nodes its hierarchy shows ({shownMeshes} shown, {hiddenMeshes} hidden; glTF {wantShown.Count} shown)",
                shownMeshes == wantShown.Count && hiddenMeshes > 0, "");
        }

        if (FindFile("SimpleInstancing.glb") is { } inst)
        {
            var d = Blix.ModelData.Load(CookCache.Resolve(inst));
            var node = Enumerable.Range(0, d.Nodes.Count).First(n => d.Nodes[n].Instances is not null);
            var count = d.Nodes[node].Instances!.Count;
            var flat = d.Flattened().ToArray();
            var perInstance = d.Meshes[d.Nodes[node].MeshIndex].Primitives.Count;
            // Each flattened copy sits where its instance puts the mesh: its bounds centre is the mesh's moved by instance * world.
            var mesh = d.Meshes[d.Nodes[node].MeshIndex].Primitives[0].Mesh;
            var centre = (mesh.Bounds.Min + mesh.Bounds.Max) * 0.5f;
            var placedRight = d.DrawnWorlds(node).Select((w, k) => (w, k)).Count(x =>
            {
                var b = flat[x.k * perInstance].Primitive.Mesh.Bounds;
                return System.Numerics.Vector3.Distance(System.Numerics.Vector3.Transform(centre, x.w), (b.Min + b.Max) * 0.5f) < 0.05f;
            });
            t.Expect($"SimpleInstancing flattens its node once per instance ({flat.Length} for {count} x {perInstance}), each where its instance puts it ({placedRight}/{count})",
                flat.Length == count * perInstance && count > 1 && placedRight == count, "");
        }

        if (FindFile("MaterialsVariantsShoe.glb") is { } shoe)
        {
            var d = Blix.ModelData.Load(CookCache.Resolve(shoe));
            var prims = d.Flattened().Select(x => x.Primitive).ToArray();
            var differ = Enumerable.Range(0, d.Variants.Count)
                .Select(v => prims.Count(p => p.MaterialFor(v).MaterialIndex != p.MaterialIndex)).ToArray();
            var resolved = prims.All(p => Enumerable.Range(0, d.Variants.Count).All(v => p.MaterialFor(v).Material is not null));
            t.Expect($"MaterialsVariantsShoe's {d.Variants.Count} variants ({string.Join(", ", d.Variants)}) each resolve a material per primitive " +
                     $"(primitives differing from their own: {string.Join("/", differ)})",
                d.Variants.Count == 3 && resolved && differ.Count(x => x > 0) >= 2, "");
        }
    }

    // A flattened mirror keeps glTF's faces: each triangle's winding normal agrees with its authored vertex
    // normals, and the bitangent cross(N, T) * w agrees with the direction the texture's v runs — on
    // mirrored nodes as on unmirrored ones. The control is each triangle read reversed, which a flatten
    // that baked the mirror without reversing the winding would have produced.
    private static void FlattenKeepsFacesUnderMirrors(TestRunner t)
    {
        var files = new[] { "NegativeScaleTest.glb" }
            .Concat(Enumerable.Range(0, 13).Select(i => $"Node_NegativeScale_{i:00}.gltf"))
            .Select(FindFile).Where(f => f is not null).ToArray();
        if (files.Length == 0) return;

        int mirroredTris = 0, faceAgrees = 0, reversedAgrees = 0, frameTris = 0, frameAgrees = 0, plainTris = 0, plainAgrees = 0;
        int plainFrameTris = 0, plainFrameAgrees = 0;
        foreach (var file in files)
        {
            var d = Blix.ModelData.Load(CookCache.Resolve(file!), new Blix.ModelNeeds(Tangents: true));
            foreach (var (node, prim) in d.Flattened())
            {
                var mirrored = d.World[node].GetDeterminant() < 0f;
                var m = prim.Mesh;
                var idx = m.Indices32 ?? m.Indices.Select(i => (uint)i).ToArray();
                for (var i = 0; i + 2 < idx.Length; i += 3)
                {
                    var (p0, n0, t0, uv0) = Vertex(m, idx[i]);
                    var (p1, n1, _, uv1) = Vertex(m, idx[i + 1]);
                    var (p2, n2, _, uv2) = Vertex(m, idx[i + 2]);
                    var face = System.Numerics.Vector3.Cross(p1 - p0, p2 - p0);
                    if (face.LengthSquared() < 1e-12f) continue;
                    var normal = n0 + n1 + n2;
                    var agrees = System.Numerics.Vector3.Dot(face, normal) > 0f;
                    if (mirrored)
                    {
                        mirroredTris++;
                        if (agrees) faceAgrees++;
                        if (System.Numerics.Vector3.Dot(-face, normal) > 0f) reversedAgrees++;
                    }
                    else
                    {
                        plainTris++;
                        if (agrees) plainAgrees++;
                    }

                    // The direction v increases across the triangle, from positions and UVs (the standard tangent-space solve).
                    var e1 = p1 - p0; var e2 = p2 - p0;
                    var d1 = uv1 - uv0; var d2 = uv2 - uv0;
                    var det = (d1.X * d2.Y) - (d2.X * d1.Y);
                    if (MathF.Abs(det) < 1e-8f || t0.W == 0f) continue;
                    var dv = ((e2 * d1.X) - (e1 * d2.X)) / det;
                    var bitangent = System.Numerics.Vector3.Cross(n0, new System.Numerics.Vector3(t0.X, t0.Y, t0.Z)) * t0.W;
                    // glTF's green points up the image, against v: the bitangent opposes dv. The rate is
                    // compared with the unmirrored nodes', so no sign is assumed.
                    var opposes = System.Numerics.Vector3.Dot(bitangent, dv) < 0f;
                    if (mirrored) { frameTris++; if (opposes) frameAgrees++; }
                    else { plainFrameTris++; if (opposes) plainFrameAgrees++; }
                }
            }
        }

        t.Expect($"flattened mirrored nodes keep glTF's front faces: {faceAgrees}/{mirroredTris} triangles agree with their normals " +
                 $"(unmirrored {plainAgrees}/{plainTris}; CONTROL, the winding left as baked: {reversedAgrees}/{mirroredTris})",
            mirroredTris > 0 && faceAgrees >= mirroredTris * 0.95 && reversedAgrees <= mirroredTris * 0.05, "");
        // Not every triangle's UV solve is clean (seams, sheared and degenerate UVs), so the claim is a
        // rate: mirrored triangles agree as often as unmirrored ones do. The CONTROL is w left as baked,
        // which turns each mirrored agreement into a disagreement.
        var mirroredRate = frameTris == 0 ? 0.0 : frameAgrees / (double)frameTris;
        var plainRate = plainFrameTris == 0 ? 0.0 : plainFrameAgrees / (double)plainFrameTris;
        t.Expect($"and their normal-map frame: the bitangent points up the image on {mirroredRate:P0} of mirrored triangles, " +
                 $"as on {plainRate:P0} of unmirrored (CONTROL, w left as baked: {1 - mirroredRate:P0})",
            frameTris > 0 && plainFrameTris > 0 && Math.Abs(mirroredRate - plainRate) < 0.1 && Math.Abs((1 - mirroredRate) - plainRate) > 0.3, "");

        static (System.Numerics.Vector3 P, System.Numerics.Vector3 N, System.Numerics.Vector4 T, System.Numerics.Vector2 Uv) Vertex(Blix.Assets.MeshData m, uint v)
        {
            var o = (int)v * m.Layout.Stride;
            float F(int at) => BitConverter.ToSingle(m.VertexBytes, o + at);
            return (new(F(0), F(4), F(8)), new(F(12), F(16), F(20)), new(F(24), F(28), F(32), F(36)), new(F(40), F(44)));
        }
    }

    private static void SamplersMatchGltf(TestRunner t)
    {
        var root = FindFile("InterpolationTest.glb") is { } it ? Path.GetFullPath(Path.Combine(it, "..", "..", "..")) : null;
        if (root is null) return;

        var checkedChannels = 0;
        var nonDefault = 0;
        var wrong = new List<string>();
        foreach (var file in Directory.EnumerateFiles(root, "*.gl*", SearchOption.AllDirectories)
            .Where(f => (f.EndsWith(".glb", StringComparison.Ordinal) || f.EndsWith(".gltf", StringComparison.Ordinal))
                && !ExpectedRefusals.ContainsKey(Path.GetFileName(f)))
            .OrderBy(f => f, StringComparer.Ordinal))
        {
            var gltf = LoadSource(file);
            var cooked = BlixMeshReader.Read(CookCache.Resolve(file));
            for (var m = 0; m < gltf.LogicalMaterials.Count; m++)
            {
                var material = gltf.LogicalMaterials[m];
                var row = cooked.MaterialTable[m];
                foreach (var (channel, image) in new[]
                {
                    ("BaseColor", row.BaseColorImage), ("Normal", row.NormalImage), ("MetallicRoughness", row.MetallicRoughnessImage),
                    ("Occlusion", row.OcclusionImage), ("Emissive", row.EmissiveImage),
                })
                {
                    var texture = material.FindChannel(channel)?.Texture;
                    if (texture is null || image < 0) continue;
                    checkedChannels++;
                    var s = texture.Sampler;
                    var want = s is null ? default : new BlixMeshSampler((int)s.WrapS, (int)s.WrapT, (int)s.MinFilter, (int)s.MagFilter);
                    if (want != default) nonDefault++;
                    if (cooked.ImageTable[image].Sampler != want)
                        wrong.Add($"{Path.GetFileName(file)} material {m} {channel}: cooked {cooked.ImageTable[image].Sampler}, glTF {want}");
                }
            }
        }

        t.Expect($"every textured channel's cooked row carries its glTF sampler ({checkedChannels} channels, {nonDefault} not the default)",
            wrong.Count == 0 && nonDefault > 0, string.Join(" | ", wrong.Take(5)));

        // The engine's reading of glTF's codes.
        var mirrored = new BlixMeshSampler(33648, 33071, 9984, 9728).ToSamplerDescription();
        t.Expect("MIRRORED_REPEAT x CLAMP_TO_EDGE, NEAREST_MIPMAP_NEAREST, NEAREST read as glTF says",
            mirrored is { WrapU: Blix.Graphics.TextureWrap.MirroredRepeat, WrapV: Blix.Graphics.TextureWrap.ClampToEdge, MinFilter: Blix.Graphics.TextureFilter.Nearest,
                MipFilter: Blix.Graphics.TextureMipFilter.Nearest, MagFilter: Blix.Graphics.TextureFilter.Nearest }, $"{mirrored}");
        var noMips = new BlixMeshSampler(10497, 10497, 9729, 9729).ToSamplerDescription();
        t.Expect("a LINEAR min filter names no mipmap mode, so it samples the base level only",
            noMips is { MinFilter: Blix.Graphics.TextureFilter.Linear, MipFilter: Blix.Graphics.TextureMipFilter.None }, $"{noMips}");
        t.Expect("and a sampler that says nothing keeps the loader's default",
            default(BlixMeshSampler).ToSamplerDescription() is null && new BlixMeshSampler(10497, 10497, 0, 0).ToSamplerDescription() is null, "");
    }

    // ── Primitive modes as glTF defines them ───────────────────────────────────
    // Every triangle-mode primitive in the corpus (TRIANGLES, TRIANGLE_STRIP, TRIANGLE_FAN, indexed or not)
    // unrolled here from the raw accessors by glTF 2.0 §3.7.2.1 — strip i = (v_i, v_{i+1+i%2}, v_{i+2-i%2}),
    // fan i = (v_{i+1}, v_{i+2}, v_0) — and held to the cooked triangle list as a set of position triples,
    // winding kept (the cook renumbers vertices, so indices are not comparable).
    private static void TriangleModesMatchSpec(TestRunner t)
    {
        var modes = new HashSet<SharpGLTF.Schema2.PrimitiveType>();
        foreach (var name in new[]
        {
            "Mesh_PrimitiveMode_04.gltf", "Mesh_PrimitiveMode_05.gltf", "Mesh_PrimitiveMode_06.gltf",
            "Mesh_PrimitiveMode_11.gltf", "Mesh_PrimitiveMode_12.gltf", "Mesh_PrimitiveMode_13.gltf",
            "TriangleWithoutIndices.gltf", "Box.glb", "BoxInterleaved.glb", "Fox.glb", "Accessor_Sparse_03.gltf",
        })
        {
            var file = FindFile(name);
            if (file is null) continue;
            var gltf = LoadSource(file);
            var expected = new List<string>();
            foreach (var mesh in gltf.LogicalMeshes)
            foreach (var prim in mesh.Primitives)
            {
                modes.Add(prim.DrawPrimitiveType);
                var p = prim.GetVertexAccessor("POSITION").AsVector3Array();
                // Sparse substitutions applied to the indices themselves (Accessor_Sparse_03's index accessor is sparse).
                var raw = prim.IndexAccessor is not { } ia ? Enumerable.Range(0, p.Count).ToArray()
                    : ia.IsSparse ? ia.AsScalarArray().Select(v => (int)MathF.Round(v)).ToArray()
                    : ia.AsIndicesArray().Select(i => (int)i).ToArray();
                var n = prim.DrawPrimitiveType switch
                {
                    SharpGLTF.Schema2.PrimitiveType.TRIANGLES => raw.Length / 3,
                    _ => Math.Max(0, raw.Length - 2),
                };
                for (var i = 0; i < n; i++)
                {
                    var (a, b, c) = prim.DrawPrimitiveType switch
                    {
                        SharpGLTF.Schema2.PrimitiveType.TRIANGLE_STRIP => (raw[i], raw[i + 1 + i % 2], raw[i + 2 - i % 2]),
                        SharpGLTF.Schema2.PrimitiveType.TRIANGLE_FAN => (raw[i + 1], raw[i + 2], raw[0]),
                        _ => (raw[3 * i], raw[3 * i + 1], raw[3 * i + 2]),
                    };
                    expected.Add(Triangle(p[a], p[b], p[c]));
                }
            }

            var data = Blix.ModelData.Load(CookCache.Resolve(file), new Blix.ModelNeeds(Skinned: true));
            var cooked = new List<string>();
            foreach (var prim in data.Meshes.SelectMany(m => m.Primitives))
            {
                var mesh = prim.Mesh;
                var indices = mesh.Indices32 is { } i32 ? i32.Select(i => (int)i).ToArray() : mesh.Indices.Select(i => (int)i).ToArray();
                System.Numerics.Vector3 At(int v) => new(
                    BitConverter.ToSingle(mesh.VertexBytes, v * mesh.Layout.Stride),
                    BitConverter.ToSingle(mesh.VertexBytes, v * mesh.Layout.Stride + 4),
                    BitConverter.ToSingle(mesh.VertexBytes, v * mesh.Layout.Stride + 8));
                for (var k = 0; k + 2 < indices.Length; k += 3) cooked.Add(Triangle(At(indices[k]), At(indices[k + 1]), At(indices[k + 2])));
            }

            expected.Sort(StringComparer.Ordinal);
            cooked.Sort(StringComparer.Ordinal);
            t.Expect($"{name}: the cooked triangles are glTF's, winding kept", expected.SequenceEqual(cooked),
                $"{expected.Count} expected, {cooked.Count} cooked, first difference {expected.Except(cooked).FirstOrDefault() ?? cooked.Except(expected).FirstOrDefault()}");
        }

        t.Expect("strips and fans are both exercised",
            modes.Contains(SharpGLTF.Schema2.PrimitiveType.TRIANGLE_STRIP) && modes.Contains(SharpGLTF.Schema2.PrimitiveType.TRIANGLE_FAN),
            string.Join(",", modes));

        // A triangle as text, rotated so its smallest corner leads: the same triangle in any starting
        // corner compares equal, and a flipped winding does not.
        static string Triangle(System.Numerics.Vector3 a, System.Numerics.Vector3 b, System.Numerics.Vector3 c)
        {
            string K(System.Numerics.Vector3 v) => $"{MathF.Round(v.X, 4):0.0000},{MathF.Round(v.Y, 4):0.0000},{MathF.Round(v.Z, 4):0.0000}";
            var corners = new[] { K(a), K(b), K(c) };
            var start = Array.IndexOf(corners, corners.Min(StringComparer.Ordinal));
            return $"{corners[start]}|{corners[(start + 1) % 3]}|{corners[(start + 2) % 3]}";
        }
    }

    // ── The corpus, whole ──────────────────────────────────────────────────────
    // Every file in the conformance corpus is cooked, loaded as the engine loads it, and each clip
    // sampled — or it is on this list, which says why not. Both directions fail: a file refused that is
    // not listed, and a listed file that now loads (so the list only shrinks on purpose). Loading is not
    // reading correctly; the other checks here are about that. This is about never crashing and never
    // refusing without saying so.
    private static readonly Dictionary<string, string> ExpectedRefusals = new(StringComparer.Ordinal)
    {
        // Not implemented, and refused by name rather than mis-read.
        ["BoxTexturedKtx2Basis.glb"] = "KHR_texture_basisu",
        ["BoxVertexColorsDracoRGB.gltf"] = "KHR_draco_mesh_compression",
        ["CesiumMan.gltf"] = "KHR_draco_mesh_compression",
        ["BoxWeb3dQuantizedAttributes.gltf"] = "WEB3D_quantized_attributes",
        // Invalid glTF, which must be refused.
        ["Mesh_NoPosition_00.gltf"] = "missing required POSITION",
        ["Mesh_PrimitiveRestart_00.gltf"] = "restart value",
        // Points and lines: Blix draws triangles, and refuses them by name rather than mis-reading them.
        ["MeshPrimitiveModes.gltf"] = "is POINTS",
        ["Mesh_PrimitiveMode_00.gltf"] = "POINTS", ["Mesh_PrimitiveMode_07.gltf"] = "POINTS",
        ["Mesh_PrimitiveMode_01.gltf"] = "LINES", ["Mesh_PrimitiveMode_08.gltf"] = "LINES",
        ["Mesh_PrimitiveMode_02.gltf"] = "LINE_LOOP", ["Mesh_PrimitiveMode_09.gltf"] = "LINE_LOOP",
        ["Mesh_PrimitiveMode_03.gltf"] = "LINE_STRIP", ["Mesh_PrimitiveMode_10.gltf"] = "LINE_STRIP",
        // Valid glTF that Blix does not read yet — the spec gaps (plan.md, the spec audit).
        // Morph targets that take effect: Blix does not deform by them, and the base mesh would be another shape.
        // (SimpleMorph_static, whose weights are all zero and undriven, loads: its base IS the render.)
        ["AnimatedMorphCube.glb"] = "morph targets", ["MorphStressTest.glb"] = "morph targets",
        ["MorphPrimitivesTest.glb"] = "morph targets", ["SimpleMorph.gltf"] = "morph targets",
        // Derived: the mesh's weights nonzero and the node with none, so the mesh's apply (SimpleMorph_nodezero,
        // whose node zeroes them, loads: every instance is its base).
        ["SimpleMorph_meshweights.gltf"] = "morph targets",
    };

    private static void EveryCorpusFileLoads(TestRunner t)
    {
        var root = FindFile("InterpolationTest.glb") is { } it ? Path.GetFullPath(Path.Combine(it, "..", "..", "..")) : null;
        if (root is null)
        {
            Console.WriteLine("  --   corpus load check skipped: corpus not fetched (tools/fetch-gltf-corpus.sh)");
            return;
        }

        var loaded = 0;
        var wrong = new List<string>();
        foreach (var file in Directory.EnumerateFiles(root, "*.gl*", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".glb", StringComparison.Ordinal) || f.EndsWith(".gltf", StringComparison.Ordinal))
            .OrderBy(f => f, StringComparer.Ordinal))
        {
            var name = Path.GetFileName(file);
            var expected = ExpectedRefusals.TryGetValue(name, out var why);
            try
            {
                var data = Blix.ModelData.Load(CookCache.Resolve(file), new Blix.ModelNeeds(Tangents: true, Colour: true, Skinned: true));
                if (data.Skeleton is { } skeleton)
                {
                    foreach (var clip in data.Clips)
                    {
                        var pose = skeleton.CreateRestPose();
                        clip.Sample(clip.Duration * 0.5, pose);
                        skeleton.ComputeBoneWorlds(pose, new System.Numerics.Matrix4x4[skeleton.BoneCount]);
                    }
                }

                loaded++;
                if (expected) wrong.Add($"{name} loads now — take it off the list ({why})");
            }
            catch (AssetImportException refused)
            {
                // Refused for the LISTED reason, as the claim below says: a file refused for another one passes
                // a refused-or-not check while its listed gap could have quietly closed.
                var message = refused.Message.Split('\n')[0];
                if (!expected) wrong.Add($"{name} refused: {message}");
                else if (!MessagesOf(refused).Contains(why!, StringComparison.OrdinalIgnoreCase)) wrong.Add($"{name} refused, but not for '{why}': {message}");
            }
            catch (Exception crash) when (crash is not OutOfMemoryException)
            {
                wrong.Add($"{name} CRASHED: {crash.GetType().Name}: {crash.Message.Split('\n')[0]}");
            }
        }

        t.Expect($"every corpus file loads, or is refused by name for a listed reason ({loaded} loaded, {ExpectedRefusals.Count} listed)",
            wrong.Count == 0, string.Join(" | ", wrong));
    }

    // ── Animation as glTF defines it ───────────────────────────────────────────
    // Every clip of every animated corpus file (and tank.glb, and the Rogue's first clips), at several
    // times: each node's local is its rest TRS with the clip's channels sampled by the spec's formulas,
    // composed up the node tree — nothing of Blix's hierarchy, offsets or placement. Skinned vertices are
    // held to sum(w * jointWorld * inverseBind * v) and every rigid mesh node to its animated world.
    // Blix's side is what a draw does: the hierarchy's rest pose, the clip sampled into it, bone worlds,
    // each skin's palette gathered, and each rigid part at its carrying node.
    private static void AnimationMatchesGltf(TestRunner t)
    {
        var root = FindFile("InterpolationTest.glb") is { } it ? Path.GetFullPath(Path.Combine(it, "..", "..", "..")) : null;
        if (root is null)
        {
            Console.WriteLine("  --   animation conformance skipped: corpus not fetched (tools/fetch-gltf-corpus.sh)");
            return;
        }

        var files = Directory.EnumerateFiles(root, "*.gl*", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".glb", StringComparison.Ordinal) || f.EndsWith(".gltf", StringComparison.Ordinal))
            .Concat(new[] { FindFile("tank.glb"), FindFile("Rogue.glb") }.OfType<string>())
            .OrderBy(f => f, StringComparer.Ordinal);
        foreach (var file in files)
        {
            SharpGLTF.Schema2.ModelRoot gltf;
            try { gltf = LoadSource(file); }
            catch (Exception) { continue; }
            var animations = gltf.LogicalAnimations
                .Where(a => a.Channels.Any(c => c.TargetNode is not null && c.TargetNodePath is
                    SharpGLTF.Schema2.PropertyPath.translation or SharpGLTF.Schema2.PropertyPath.rotation or SharpGLTF.Schema2.PropertyPath.scale))
                .ToArray();
            if (animations.Length == 0) continue;

            Blix.ModelData data;
            try { data = Blix.ModelData.Load(CookCache.Resolve(file), new Blix.ModelNeeds(Skinned: true)); }
            catch (AssetImportException refused)
            {
                t.Fail($"{Path.GetFileName(file)}: an animated file loads", refused.Message);
                continue;
            }

            if (data.Clips.Count != animations.Length || data.Skeleton is not { } skeleton)
            {
                t.Fail($"{Path.GetFileName(file)}: every animation arrives as a clip",
                    $"{animations.Length} animation(s), {data.Clips.Count} clip(s), skeleton {(data.Skeleton is null ? "none" : "present")}");
                continue;
            }

            var nodes = gltf.LogicalNodes;
            var attachments = data.Attachments().ToDictionary(a => a.NodeIndex);
            var worst = 0f;
            var where = string.Empty;
            var size = Math.Max(1e-3f, data.Nodes.Count == 0 ? 1f : 1f);
            var clipCount = Path.GetFileName(file) == "Rogue.glb" ? 4 : animations.Length;
            for (var c = 0; c < clipCount; c++)
            {
                var anim = animations[c];
                var clip = data.Clips[c];
                var duration = Math.Max(clip.Duration, 1e-3);
                foreach (var time in new[] { 0.0, 0.13, 0.37, 0.5, 0.71, 0.93, 1.0 }.Select(f => f * duration))
                {
                    // The reference: rest TRS, the channels sampled by the spec, composed up the tree.
                    var locals = nodes.Select(n => n.LocalMatrix).ToArray();
                    var trs = nodes.Select(n => n.LocalTransform.GetDecomposed())
                        .Select(d => (d.Scale, d.Rotation, d.Translation)).ToArray();
                    var moved = new bool[nodes.Count];
                    foreach (var channel in anim.Channels)
                    {
                        if (channel.TargetNode is null) continue;
                        var n = channel.TargetNode.LogicalIndex;
                        switch (channel.TargetNodePath)
                        {
                            case SharpGLTF.Schema2.PropertyPath.translation:
                                trs[n].Translation = SpecVector(GltfImporter.SampleKeys(channel.GetTranslationSampler()), time); moved[n] = true; break;
                            case SharpGLTF.Schema2.PropertyPath.scale:
                                trs[n].Scale = SpecVector(GltfImporter.SampleKeys(channel.GetScaleSampler()), time); moved[n] = true; break;
                            case SharpGLTF.Schema2.PropertyPath.rotation:
                                trs[n].Rotation = SpecRotation(GltfImporter.SampleKeys(channel.GetRotationSampler()), time); moved[n] = true; break;
                        }
                    }

                    for (var n = 0; n < nodes.Count; n++)
                    {
                        if (!moved[n]) continue;
                        locals[n] = System.Numerics.Matrix4x4.CreateScale(trs[n].Scale)
                            * System.Numerics.Matrix4x4.CreateFromQuaternion(System.Numerics.Quaternion.Normalize(trs[n].Rotation))
                            * System.Numerics.Matrix4x4.CreateTranslation(trs[n].Translation);
                    }

                    var worlds = new System.Numerics.Matrix4x4[nodes.Count];
                    System.Numerics.Matrix4x4 World(int n) => worlds[n] != default ? worlds[n]
                        : worlds[n] = nodes[n].VisualParent is { } parent ? locals[n] * World(parent.LogicalIndex) : locals[n];
                    size = Math.Max(size, Enumerable.Range(0, nodes.Count).Max(n => World(n).Translation.Length()));

                    // Blix: the hierarchy posed by the clip, as a draw poses it.
                    var pose = skeleton.CreateRestPose();
                    clip.Sample(time, pose);
                    var bones = new Blix.BoneWorlds(skeleton);
                    bones.Compute(pose);

                    // Rigid mesh nodes.
                    for (var n = 0; n < data.Nodes.Count; n++)
                    {
                        if (data.Nodes[n].MeshIndex < 0 || data.Nodes[n].SkinIndex >= 0) continue;
                        var ours = attachments.TryGetValue(n, out var a)
                            ? a.Local * bones[a.BoneIndex] * data.SkeletonPlacement
                            : data.World[n];
                        var e = MaxDifference(ours, World(SourceNode(gltf, data, n)));
                        if (e > worst) (worst, where) = (e, $"rigid node '{data.Nodes[n].Name}' in '{clip.Name}' at t={time:0.###}");
                    }

                    // Skinned vertices.
                    for (var s = 0; s < data.Skins.Count; s++)
                    {
                        var palette = new BonePaletteSet(data.Skins[s].Binding, 1);
                        palette.Add(bones, data.SkeletonPlacement);
                        var skinNode = nodes.First(n => n.Skin is not null && n.Mesh is not null
                            && data.Nodes[SourceToCooked(gltf, data, n.LogicalIndex)].SkinIndex == s);
                        var gskin = skinNode.Skin;
                        var ibm = gskin.InverseBindMatrices.Count > 0 ? gskin.InverseBindMatrices
                            : Enumerable.Repeat(System.Numerics.Matrix4x4.Identity, gskin.Joints.Count).ToArray();
                        var reference = new Dictionary<(int, int, int), List<System.Numerics.Vector3>>();
                        foreach (var node in nodes.Where(n => n.Skin == gskin && n.Mesh is not null))
                        foreach (var prim in node.Mesh.Primitives)
                        {
                            var positions = prim.GetVertexAccessor("POSITION").AsVector3Array();
                            var joints = prim.GetVertexAccessor("JOINTS_0").AsVector4Array();
                            var weights = prim.GetVertexAccessor("WEIGHTS_0").AsVector4Array();
                            for (var v = 0; v < positions.Count; v++)
                            {
                                var w = System.Numerics.Vector3.Zero;
                                var total = 0f;
                                for (var q = 0; q < 4; q++)
                                {
                                    if (weights[v][q] <= 0f) continue;
                                    var j = (int)joints[v][q];
                                    w += System.Numerics.Vector3.Transform(positions[v], ibm[j] * World(gskin.Joints[j].LogicalIndex)) * weights[v][q];
                                    total += weights[v][q];
                                }

                                if (total <= 0f) continue;
                                var key = Key(positions[v]);
                                if (!reference.TryGetValue(key, out var list)) reference[key] = list = new();
                                list.Add(w / total);
                            }
                        }

                        foreach (var prim in data.SkinnedPrimitives(s))
                        {
                            var mesh = prim.Mesh;
                            var stride = mesh.Layout.Stride;
                            for (var v = 0; v < mesh.VertexCount; v++)
                            {
                                var at = v * stride;
                                var p = new System.Numerics.Vector3(BitConverter.ToSingle(mesh.VertexBytes, at),
                                    BitConverter.ToSingle(mesh.VertexBytes, at + 4), BitConverter.ToSingle(mesh.VertexBytes, at + 8));
                                var w = System.Numerics.Vector3.Zero;
                                var total = 0f;
                                for (var q = 0; q < 4; q++)
                                {
                                    var weight = BitConverter.ToSingle(mesh.VertexBytes, at + 48 + q * 4);
                                    if (weight <= 0f) continue;
                                    w += System.Numerics.Vector3.Transform(p, palette.Matrices[(int)BitConverter.ToSingle(mesh.VertexBytes, at + 32 + q * 4)]) * weight;
                                    total += weight;
                                }

                                if (total <= 0f || !reference.TryGetValue(Key(p), out var candidates)) continue;
                                var e = candidates.Min(x => System.Numerics.Vector3.Distance(x, w / total));
                                if (e > worst) (worst, where) = (e, $"skin {s} vertex in '{clip.Name}' at t={time:0.###}");
                            }
                        }
                    }
                }
            }

            t.Expect($"{Path.GetFileName(file)}: {clipCount} clip(s) move every part where glTF puts it",
                worst <= Math.Max(1e-3f, size * 1e-4f), $"worst {worst:0.#####} against a {size:0.##}-unit scene ({where})");
        }

        static (int, int, int) Key(System.Numerics.Vector3 p) =>
            ((int)MathF.Round(p.X * 1e4f), (int)MathF.Round(p.Y * 1e4f), (int)MathF.Round(p.Z * 1e4f));

        static float MaxDifference(System.Numerics.Matrix4x4 a, System.Numerics.Matrix4x4 b)
        {
            var d = 0f;
            for (var r = 0; r < 4; r++)
            for (var c = 0; c < 4; c++) d = Math.Max(d, MathF.Abs(a[r, c] - b[r, c]));
            return d;
        }
    }

    // Cooked node <-> source node, by name: the cook may order nodes parent-first, and spells an unnamed
    // node node_{i} by its source index. Equal names pair in order of appearance.
    private static int SourceNode(SharpGLTF.Schema2.ModelRoot gltf, Blix.ModelData data, int cooked) =>
        NodePairs(gltf, data).First(p => p.Cooked == cooked).Source;

    private static int SourceToCooked(SharpGLTF.Schema2.ModelRoot gltf, Blix.ModelData data, int source) =>
        NodePairs(gltf, data).First(p => p.Source == source).Cooked;

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Blix.ModelData, List<(int Cooked, int Source)>> Pairs = new();

    private static List<(int Cooked, int Source)> NodePairs(SharpGLTF.Schema2.ModelRoot gltf, Blix.ModelData data) =>
        Pairs.GetValue(data, _ =>
        {
            var byName = gltf.LogicalNodes.GroupBy(n => n.Name ?? $"node_{n.LogicalIndex}")
                .ToDictionary(g => g.Key, g => new Queue<int>(g.Select(n => n.LogicalIndex)));
            var pairs = new List<(int, int)>();
            for (var c = 0; c < data.Nodes.Count; c++)
            {
                if (!byName.TryGetValue(data.Nodes[c].Name, out var queue) || queue.Count == 0)
                {
                    throw new InvalidDataException($"cooked node '{data.Nodes[c].Name}' has no source node of that name");
                }

                pairs.Add((c, queue.Dequeue()));
            }

            return pairs;
        });

    // The spec's sampling, written from glTF 2.0 Appendix C: STEP holds the earlier key, LINEAR lerps
    // (slerps a rotation on the shorter arc), CUBICSPLINE is the Hermite form over dt-scaled tangents.
    private static System.Numerics.Vector3 SpecVector((Keyframe<System.Numerics.Vector3>[] Keys, Interpolation Mode) sampled, double time)
    {
        var (keys, mode) = sampled;
        if (time <= keys[0].Time) return keys[0].Value;
        if (time >= keys[^1].Time) return keys[^1].Value;
        var k = 0;
        while (time > keys[k + 1].Time) k++;
        var dt = (float)(keys[k + 1].Time - keys[k].Time);
        var u = (float)((time - keys[k].Time) / dt);
        return mode switch
        {
            Interpolation.Step => time < keys[k + 1].Time ? keys[k].Value : keys[k + 1].Value,
            Interpolation.CubicSpline => keys[k].Value * (2 * u * u * u - 3 * u * u + 1) + keys[k].OutTangent * (dt * (u * u * u - 2 * u * u + u))
                + keys[k + 1].Value * (-2 * u * u * u + 3 * u * u) + keys[k + 1].InTangent * (dt * (u * u * u - u * u)),
            _ => keys[k].Value + (keys[k + 1].Value - keys[k].Value) * u,
        };
    }

    private static System.Numerics.Quaternion SpecRotation((Keyframe<System.Numerics.Quaternion>[] Keys, Interpolation Mode) sampled, double time)
    {
        var (keys, mode) = sampled;
        keys = keys.Select(k => k with { Value = System.Numerics.Quaternion.Normalize(k.Value) }).ToArray();
        if (time <= keys[0].Time) return keys[0].Value;
        if (time >= keys[^1].Time) return keys[^1].Value;
        var k = 0;
        while (time > keys[k + 1].Time) k++;
        var dt = (float)(keys[k + 1].Time - keys[k].Time);
        var u = (float)((time - keys[k].Time) / dt);
        switch (mode)
        {
            case Interpolation.Step:
                return time < keys[k + 1].Time ? keys[k].Value : keys[k + 1].Value;
            case Interpolation.CubicSpline:
                return System.Numerics.Quaternion.Normalize(keys[k].Value * (2 * u * u * u - 3 * u * u + 1) + keys[k].OutTangent * (dt * (u * u * u - 2 * u * u + u))
                    + keys[k + 1].Value * (-2 * u * u * u + 3 * u * u) + keys[k + 1].InTangent * (dt * (u * u * u - u * u)));
            default:
                var q0 = keys[k].Value;
                var q1 = keys[k + 1].Value;
                var d = System.Numerics.Quaternion.Dot(q0, q1);
                var sign = d < 0f ? -1f : 1f;
                d = MathF.Abs(d);
                if (d > 0.9995f) return System.Numerics.Quaternion.Normalize(q0 * (1f - u) + q1 * (sign * u));
                var a = MathF.Acos(d);
                return q0 * (MathF.Sin((1f - u) * a) / MathF.Sin(a)) + q1 * (sign * MathF.Sin(u * a) / MathF.Sin(a));
        }
    }

    // ── Sampling as glTF defines it ────────────────────────────────────────────
    // Every channel of every animated file in the corpus, read into Blix's curves by the one sampler
    // reading (GltfImporter.SampleKeys) and through the cooked file, is held to SharpGLTF's own curve
    // evaluator at many times — LINEAR, STEP and CUBICSPLINE alike (InterpolationTest has all three).
    private static void SamplingMatchesGltf(TestRunner t)
    {
        // The corpus root: sample-assets/<Name>/<file> is three levels below it.
        var root = FindFile("InterpolationTest.glb") is { } it ? Path.GetFullPath(Path.Combine(it, "..", "..", "..")) : null;
        if (root is null)
        {
            Console.WriteLine("  --   sampling conformance skipped: corpus not fetched (tools/fetch-gltf-corpus.sh)");
            return;
        }

        var files = Directory.EnumerateFiles(root, "*.gl*", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".glb", StringComparison.Ordinal) || f.EndsWith(".gltf", StringComparison.Ordinal))
            .OrderBy(f => f, StringComparer.Ordinal);
        var modes = new HashSet<Interpolation>();
        foreach (var file in files)
        {
            SharpGLTF.Schema2.ModelRoot gltf;
            try { gltf = LoadSource(file); }
            catch (Exception) { continue; }
            if (gltf.LogicalAnimations.Count == 0) continue;

            string cooked;
            try { cooked = CookCache.Resolve(file); }
            catch (AssetImportException refused)
            {
                // A refused cook is a finding, not a skip: it is how a texture crash hid InterpolationTest. The one
                // exemption is a refusal LISTED as deliberate (a morph-weight animation, say), which
                // EveryCorpusFileLoads holds to its stated reason.
                if (!ExpectedRefusals.ContainsKey(Path.GetFileName(file))) t.Fail($"{Path.GetFileName(file)}: an animated file cooks", refused.Message);
                continue;
            }
            var tracks = BlixMeshReader.Read(cooked).ClipTable.SelectMany(c => c.Tracks).ToArray();

            var worst = 0f;
            var where = string.Empty;
            var channels = 0;
            var lost = 0;
            foreach (var anim in gltf.LogicalAnimations)
            foreach (var channel in anim.Channels)
            {
                if (channel.TargetNode is null) continue;
                var path = channel.TargetNodePath;
                if (path is not (SharpGLTF.Schema2.PropertyPath.translation or SharpGLTF.Schema2.PropertyPath.rotation
                    or SharpGLTF.Schema2.PropertyPath.scale)) continue;
                channels++;
                int count;
                Interpolation wantMode;

                if (path == SharpGLTF.Schema2.PropertyPath.rotation)
                {
                    var sampler = channel.GetRotationSampler();
                    var (keys, mode) = GltfImporter.SampleKeys(sampler);
                    (count, wantMode) = (keys.Length, mode);
                    modes.Add(mode);
                    var ours = new KeyframeQuaternionCurve(keys, mode);
                    var theirs = sampler.CreateCurveSampler(true);
                    foreach (var time in Times(keys[0].Time, keys[^1].Time))
                    {
                        var a = ours.Evaluate(time);
                        // LINEAR rotation is slerp by the spec (Appendix C); SharpGLTF's evaluator differs
                        // from it by ~2 degrees inside a 90-degree key step, so the formula is the reference.
                        // CUBICSPLINE likewise: SharpGLTF's rotation spline disagrees with the spec's formula,
                        // checked by hand from InterpolationTest's raw accessors (t=1.9: z -0.999966, w
                        // -0.008266, which is what this computes), so the formula is the reference there too.
                        var b = mode switch
                        {
                            Interpolation.Linear => SpecSlerp(keys, time),
                            Interpolation.CubicSpline => SpecCubic(keys, time),
                            _ => theirs.GetPoint((float)time),
                        };
                        // q and -q are one rotation.
                        var e = 1f - MathF.Abs(System.Numerics.Quaternion.Dot(
                            System.Numerics.Quaternion.Normalize(a), System.Numerics.Quaternion.Normalize(b)));
                        if (e > worst) (worst, where) = (e, $"rotation {mode} at t={time:0.###}: {a} vs {b}");
                    }
                }
                else
                {
                    var sampler = path == SharpGLTF.Schema2.PropertyPath.translation ? channel.GetTranslationSampler() : channel.GetScaleSampler();
                    var (keys, mode) = GltfImporter.SampleKeys(sampler);
                    (count, wantMode) = (keys.Length, mode);
                    modes.Add(mode);
                    var ours = new KeyframeVector3Curve(keys, mode);
                    var theirs = sampler.CreateCurveSampler(true);
                    foreach (var time in Times(keys[0].Time, keys[^1].Time))
                    {
                        var reference = mode == Interpolation.CubicSpline ? SpecCubicVector(keys, time) : theirs.GetPoint((float)time);
                        var e = System.Numerics.Vector3.Distance(ours.Evaluate(time), reference);
                        if (e > worst) (worst, where) = (e, $"{path} {mode} at t={time:0.###}: {ours.Evaluate(time)} vs {reference}");
                    }
                }

                // And the cooked file kept it: some track carries this channel with its mode and key count.
                var kept = tracks.Any(tr => path switch
                {
                    SharpGLTF.Schema2.PropertyPath.translation => tr.Translation.Length == count && (int)tr.TranslationInterpolation == (int)wantMode,
                    SharpGLTF.Schema2.PropertyPath.rotation => tr.Rotation.Length == count && (int)tr.RotationInterpolation == (int)wantMode,
                    _ => tr.Scale.Length == count && (int)tr.ScaleInterpolation == (int)wantMode,
                });
                if (!kept) lost++;
            }

            if (channels == 0) continue;
            t.Expect($"{Path.GetFileName(file)}: {channels} channel(s) sample as glTF's and survive the cook",
                worst < 1e-4f && lost == 0, $"worst {worst:0.######} ({where}), {lost} not in the cooked file");
        }

        t.Expect("the corpus exercises LINEAR, STEP and CUBICSPLINE", modes.Count == 3, string.Join(",", modes));

        // glTF 2.0 Appendix C: slerp on the shorter arc (the sign of the dot product), linear when the keys
        // are nearly parallel. Written from the spec rather than taken from System.Numerics, which is what
        // the engine's curve uses.
        static System.Numerics.Quaternion SpecSlerp(Keyframe<System.Numerics.Quaternion>[] keys, double time)
        {
            if (time <= keys[0].Time) return keys[0].Value;
            if (time >= keys[^1].Time) return keys[^1].Value;
            var k = 0;
            while (time > keys[k + 1].Time) k++;
            var t = (float)((time - keys[k].Time) / (keys[k + 1].Time - keys[k].Time));
            var q0 = keys[k].Value;
            var q1 = keys[k + 1].Value;
            var d = System.Numerics.Quaternion.Dot(q0, q1);
            var s = d < 0f ? -1f : 1f;
            d = MathF.Abs(d);
            if (d > 0.9995f) return System.Numerics.Quaternion.Normalize(q0 * (1f - t) + q1 * (s * t));
            var a = MathF.Acos(d);
            return q0 * (MathF.Sin((1f - t) * a) / MathF.Sin(a)) + q1 * (s * MathF.Sin(t * a) / MathF.Sin(a));
        }

        // glTF 2.0 Appendix C, CUBICSPLINE: the Hermite basis over (v_k, dt*b_k, v_k+1, dt*a_k+1), a rotation
        // normalised after. Written from the spec text.
        static (int K, float U, float Dt) Segment<T>(Keyframe<T>[] keys, double time)
        {
            var k = 0;
            while (k < keys.Length - 2 && time > keys[k + 1].Time) k++;
            var dt = (float)(keys[k + 1].Time - keys[k].Time);
            return (k, Math.Clamp((float)((time - keys[k].Time) / dt), 0f, 1f), dt);
        }

        static System.Numerics.Vector3 SpecCubicVector(Keyframe<System.Numerics.Vector3>[] keys, double time)
        {
            if (time <= keys[0].Time) return keys[0].Value;
            if (time >= keys[^1].Time) return keys[^1].Value;
            var (k, u, dt) = Segment(keys, time);
            var (h0, h1, h2, h3) = (2 * u * u * u - 3 * u * u + 1, u * u * u - 2 * u * u + u, -2 * u * u * u + 3 * u * u, u * u * u - u * u);
            return keys[k].Value * h0 + keys[k].OutTangent * (dt * h1) + keys[k + 1].Value * h2 + keys[k + 1].InTangent * (dt * h3);
        }

        static System.Numerics.Quaternion SpecCubic(Keyframe<System.Numerics.Quaternion>[] keys, double time)
        {
            if (time <= keys[0].Time) return keys[0].Value;
            if (time >= keys[^1].Time) return keys[^1].Value;
            var (k, u, dt) = Segment(keys, time);
            var (h0, h1, h2, h3) = (2 * u * u * u - 3 * u * u + 1, u * u * u - 2 * u * u + u, -2 * u * u * u + 3 * u * u, u * u * u - u * u);
            return System.Numerics.Quaternion.Normalize(
                keys[k].Value * h0 + keys[k].OutTangent * (dt * h1) + keys[k + 1].Value * h2 + keys[k + 1].InTangent * (dt * h3));
        }

        static IEnumerable<double> Times(double from, double to)
        {
            for (var i = 0; i <= 40; i++) yield return from + (to - from) * i / 40.0 + (i % 3 == 1 ? 1e-3 : 0);
        }
    }

    // ── Skins as glTF defines them ─────────────────────────────────────────────
    // glTF places a skinned vertex at sum(w * jointWorld * inverseBind * v), the joint worlds being the
    // scene graph's. This computes that from SharpGLTF's own node matrices — nothing of Blix's — for every
    // source vertex, and holds each cooked vertex, skinned through the engine's rest palette and skeleton
    // placement, to it. Matched by mesh-space position, because the cook renumbers vertices.
    //
    // The assets are the ones that separate the readings: tank.glb (mesh nodes that are not where the
    // skeleton hangs, and a rest pose that is not its bind pose), two Khronos skin tests (a transformed
    // mesh node; root joints under different parents), and the Rogue as the ordinary case.
    private static void SkinsMatchGltf(TestRunner t)
    {
        foreach (var name in new[] { "tank.glb", "Animation_Skin_02.gltf", "Animation_Skin_09.gltf", "Rogue.glb", "RiggedFigure.glb", "RiggedSimple.glb", "RiggedSimple_bones300.gltf", "RiggedSimple_cutout.gltf",
            "Animation_Skin_03.gltf", "Animation_Skin_06.gltf", "RiggedSimple_extraibm.gltf" })
        {
            var source = FindFile(name);
            if (source is null)
            {
                Console.WriteLine($"  --   skin conformance skipped for {name}: not found (tools/fetch-gltf-corpus.sh)");
                continue;
            }

            // Unvalidated, so the declared-lenient Animation_Skin_06 (invalid: no common root) reads as a reference too.
            var gltf = SharpGLTF.Schema2.ModelRoot.Load(source, new SharpGLTF.Schema2.ReadSettings { Validation = SharpGLTF.Validation.ValidationMode.Skip });
            var expected = new Dictionary<(int, int, int), List<System.Numerics.Vector3>>();
            foreach (var node in gltf.LogicalNodes.Where(n => n.Mesh is not null && n.Skin is not null))
            {
                var skin = node.Skin;
                var jointWorlds = skin.Joints.Select(j => j.WorldMatrix).ToArray();
                // Absent inverse binds are identities (glTF 2.0 §5.27).
                var ibms = skin.InverseBindMatrices.Count > 0 ? skin.InverseBindMatrices
                    : Enumerable.Repeat(System.Numerics.Matrix4x4.Identity, skin.Joints.Count).ToArray();
                foreach (var prim in node.Mesh.Primitives)
                {
                    var positions = prim.GetVertexAccessor("POSITION").AsVector3Array();
                    var joints = prim.GetVertexAccessor("JOINTS_0").AsVector4Array();
                    var weights = prim.GetVertexAccessor("WEIGHTS_0").AsVector4Array();
                    for (var v = 0; v < positions.Count; v++)
                    {
                        var world = System.Numerics.Vector3.Zero;
                        var total = 0f;
                        for (var q = 0; q < 4; q++)
                        {
                            var w = weights[v][q];
                            if (w <= 0f) continue;
                            var j = (int)joints[v][q];
                            world += System.Numerics.Vector3.Transform(positions[v], ibms[j] * jointWorlds[j]) * w;
                            total += w;
                        }

                        var key = Key(positions[v]);
                        if (!expected.TryGetValue(key, out var list)) expected[key] = list = new();
                        list.Add(total > 0f ? world / total : System.Numerics.Vector3.Transform(positions[v], node.WorldMatrix));
                    }
                }
            }

            Blix.ModelData data;
            try
            {
                data = Blix.ModelData.Load(CookCache.Resolve(source), new Blix.ModelNeeds(Skinned: true));
            }
            catch (AssetImportException refused)
            {
                t.Fail($"{name}: the cooked skin loads", refused.Message);
                continue;
            }

            var worst = 0f;
            var unmatched = 0;
            var compared = 0;
            for (var s = 0; s < data.Skins.Count; s++)
            {
                // The skin at the hierarchy's rest, in scene space: the hierarchy's worlds after its placement.
                var binding = data.Skins[s].Binding;
                var restWorlds = new BoneWorlds(binding.Skeleton);
                restWorlds.Compute(binding.Skeleton.CreateRestPose());
                var palette = new BonePaletteSet(binding, 1);
                palette.Add(restWorlds, data.SkeletonPlacement);
                foreach (var prim in data.SkinnedPrimitives(s))
                {
                    var mesh = prim.Mesh;
                    var stride = mesh.Layout.Stride;
                    for (var v = 0; v < mesh.VertexCount; v++)
                    {
                        var at = v * stride;
                        var p = new System.Numerics.Vector3(
                            BitConverter.ToSingle(mesh.VertexBytes, at), BitConverter.ToSingle(mesh.VertexBytes, at + 4),
                            BitConverter.ToSingle(mesh.VertexBytes, at + 8));
                        var world = System.Numerics.Vector3.Zero;
                        var total = 0f;
                        for (var q = 0; q < 4; q++)
                        {
                            var w = BitConverter.ToSingle(mesh.VertexBytes, at + 48 + q * 4);
                            if (w <= 0f) continue;
                            var b = (int)BitConverter.ToSingle(mesh.VertexBytes, at + 32 + q * 4);
                            world += System.Numerics.Vector3.Transform(p, palette.Matrices[b]) * w;
                            total += w;
                        }

                        if (total > 0f) world /= total;
                        if (!expected.TryGetValue(Key(p), out var candidates)) { unmatched++; continue; }
                        worst = Math.Max(worst, candidates.Min(c => System.Numerics.Vector3.Distance(c, world)));
                        compared++;
                    }
                }
            }

            // Relative to the model's size: a millimetre on a character is not a millimetre on a tank.
            var extent = expected.Values.SelectMany(x => x).Aggregate(
                (Min: new System.Numerics.Vector3(float.MaxValue), Max: new System.Numerics.Vector3(float.MinValue)),
                (acc, x) => (System.Numerics.Vector3.Min(acc.Min, x), System.Numerics.Vector3.Max(acc.Max, x)));
            var size = System.Numerics.Vector3.Distance(extent.Min, extent.Max);
            t.Expect($"{name}: every cooked skinned vertex lands where glTF puts it at rest",
                compared > 0 && unmatched == 0 && worst <= size * 1e-4f,
                $"{compared} compared, {unmatched} unmatched, worst {worst:0.#####} against a {size:0.##}-unit model");
        }

        static (int, int, int) Key(System.Numerics.Vector3 p) =>
            ((int)MathF.Round(p.X * 1e4f), (int)MathF.Round(p.Y * 1e4f), (int)MathF.Round(p.Z * 1e4f));
    }

    private static void ModelDataReadings(TestRunner t)
    {
        t.ExpectThrows<AssetImportException>("ModelData.Load refuses a source model, naming the cook",
            () => Blix.ModelData.Load("some/asset.glb"));

        var rogue = FindFile("Rogue.glb");
        var morph = FindFile("SimpleMorph_static.gltf");
        var multiUv = FindFile("MultiUVTest.gltf");
        if (rogue is null || morph is null || multiUv is null)
        {
            Console.WriteLine("  --   ModelData checks skipped: Rogue.glb, SimpleMorph_static.gltf or MultiUVTest.gltf not found");
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
        // Colour asks for COLOR_0 and TEXCOORD_1 in skinned meshes too: the complete vertex as cooked. (Read as 80 bytes
        // whatever was asked, a skinned material on set 1 sampled set 0 and its vertex colour was white.)
        var skinnedColour = Blix.ModelData.Load(rig, new Blix.ModelNeeds(Colour: true, Skinned: true));
        t.Expect("and read with Colour, they keep TEXCOORD_1 and COLOR_0: the complete 92-byte skinned vertex",
            skinnedColour.Meshes.Where(m => m.Skinned).SelectMany(m => m.Primitives)
                .All(p => p.Mesh.Layout == Blix.Graphics.VertexPosition3NormalTextureSkin4Tangent2Color.Layout),
            string.Join(",", skinnedColour.Meshes.SelectMany(m => m.Primitives).Select(p => p.Mesh.Layout.Stride).Distinct()));
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
            // Morph targets whose weights are all zero and undriven: the base mesh is the render, so it cooks.
            var cube = Path.Combine(temp, "SimpleMorph_static.gltf");
            File.Copy(morph, cube);
            MeshRecipe.CookToBlixMesh(cube, Path.ChangeExtension(cube, ".blixmesh"));
            var morphs = Blix.ModelData.Load(Path.ChangeExtension(cube, ".blixmesh")).Ignored;
            t.Expect("a cooked file records the source attributes its cook did not carry (morph targets that take no effect)",
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
            // glTF allows one image to be read two ways; a cooked image has one encoding, so each use is
            // its own cooked file: the first under the image's name, the next with a role suffix.
            var twoRoles = MeshRecipe.ReferencedImages(conflict);
            t.Expect("one image used in two roles is referenced once per role, each with its own cooked file",
                twoRoles.Count == 2
                && twoRoles.Any(r => r.Role == TextureRole.BaseColor && r.CookedUri == "shared.blixtex")
                && twoRoles.Any(r => r.Role == TextureRole.Normal && r.CookedUri == "shared.normal.blixtex"),
                string.Join(", ", twoRoles.Select(r => $"{r.Role}->{r.CookedUri}")));

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
            var directXPatch = MaterialPatch.Parse("material stone{1} normal=directx\n", "directx");
            var flipped = MeshRecipe.ReferencedImages(named, directXPatch);
            t.ExpectTrue("a normal map its patch declares directx is referenced for a green flip",
                flipped.Contains(new MeshRecipe.ReferencedImage("normal.png", TextureRole.Normal, FlipGreen: true)));
            t.ExpectTrue("and the material's other images are not",
                flipped.Contains(new MeshRecipe.ReferencedImage("shared.png", TextureRole.BaseColor)));
            t.ExpectTrue("while without a patch the map is read as glTF's convention",
                MeshRecipe.ReferencedImages(named).Contains(new MeshRecipe.ReferencedImage("normal.png", TextureRole.Normal)));

            var openGlPatch = MaterialPatch.Parse("material stone{1} normal=directx\nmaterial stone normal=opengl\n", "opengl");
            t.ExpectTrue("the last rule to state a material's convention wins",
                MeshRecipe.ReferencedImages(named, openGlPatch)
                    .Contains(new MeshRecipe.ReferencedImage("normal.png", TextureRole.Normal)));

            var badValue = MaterialPatch.Parse("material stone normal=upside\n", "bad");
            t.ExpectThrows<InvalidDataException>("a convention other than directx or opengl is refused",
                () => MeshRecipe.ReferencedImages(named, badValue));

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
            var twoConventions = MeshRecipe.ReferencedImages(sharedNormal, directXPatch);
            t.Expect("one normal image declared directx by one material and not by another cooks once per convention",
                twoConventions.Count == 2 && twoConventions.Count(r => r.FlipGreen) == 1
                && twoConventions.Select(r => r.CookedUri).Distinct().Count() == 2,
                string.Join(", ", twoConventions.Select(r => $"flip={r.FlipGreen}->{r.CookedUri}")));

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
            t.Expect("cook asset cooks an image used in two roles into two files",
                Blix.Tools.Cook.Program.Main(new[] { "asset", conflict, "--out", packaged }) == 0
                && File.Exists(Path.Combine(packaged, "shared.blixtex")) && File.Exists(Path.Combine(packaged, "shared.normal.blixtex")),
                "a cooked file is missing");
            if (File.Exists(Path.Combine(packaged, "shared.normal.blixtex")))
            {
                var colour = Blix.Graphics.Images.BlixTexReader.ReadHandle(Path.Combine(packaged, "shared.blixtex")).Format;
                var normal = Blix.Graphics.Images.BlixTexReader.ReadHandle(Path.Combine(packaged, "shared.normal.blixtex")).Format;
                t.Expect("each in its own role's encoding", colour != normal, $"both {colour}");
            }
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
                var cookedSkeleton = cookedData.Skeleton!;
                var cookedBinds = cookedData.Skins[0].Binding.InverseBinds;
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
                    if (cookedBinds[i] != viaSourceRig.SkinsOrEmpty[0].Binding.InverseBinds[i]) rigMismatch.Add($"bone[{i}] inverse bind differs");
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
                    // Read with Colour, the cooked skinned vertex is the complete 92-byte one, whose first 80 bytes are the
                    // source importer's 80-byte vertex: the shared prefix is what both readers must agree on.
                    var cookedStride = cookedMesh.Layout.Stride;
                    if (cookedStride < stride || cookedIndices.Length != sourceIndices.Length)
                    {
                        vertexMismatch++;
                        continue;
                    }

                    var differs = false;
                    for (var c = 0; c < cookedIndices.Length && !differs; c++)
                    {
                        var cv = cookedMesh.VertexBytes.AsSpan((int)cookedIndices[c] * cookedStride, stride);
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
        SkinsMatchGltf(t);
        SamplingMatchesGltf(t);
        AnimationMatchesGltf(t);
        EveryCorpusFileLoads(t);
        TriangleModesMatchSpec(t);
        SamplersMatchGltf(t);
        TextureTransformsMatchGltf(t);
        ValidatorRejectionsAreAccounted(t);
        IndexCountsAreRefusedNotRepaired(t);
        GeneratedTangentsFollowTheNormalTexture(t);
        QuantizedAttributesReadAsTheirFloats(t);
        NormalMapFramesFollowTheTextureTransform(t);
        EveryImporterRefusesWhatTheCookRefuses(t);
        InterpolationGolden(t);
        SceneLevelMatchesGltf(t);
        SceneLevelReachesModelData(t);
        FlattenKeepsFacesUnderMirrors(t);
        DemoRigsSkinTheSameThroughModel(t);
        MergeKeepsFacesUnderMirrors(t);
        EveryTexturedChannelCooks(t);

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

            var applied = MaterialPatch.Parse(origin: "ok", text: "material glass transmission=1.0 ior=1.5\nmaterial stone_*{2} metallic=0.0\n").Apply(table);
            t.Expect("a patch applies a scalar to the material it names",
                Math.Abs(applied[0].Ext.TransmissionFactor - 1.0f) < 1e-6f,
                $"{applied[0].Ext.TransmissionFactor}");
            t.Expect("and an extension field the asset never authored",
                Math.Abs(applied[0].Ext.IndexOfRefraction - 1.5f) < 1e-6f,
                $"{applied[0].Ext.IndexOfRefraction}");
            t.ExpectTrue("a glob reaches every material it matches",
                applied[1].MetallicFactor == 0f && applied[2].MetallicFactor == 0f);
            t.ExpectTrue("and leaves the ones it does not alone", applied[0].MetallicFactor == 0f);

            var missed = Throws(() => MaterialPatch.Parse(origin: "miss", text: "material curtain_01 sheen=1,0,0\n").Apply(table));
            t.ExpectTrue("a rule that matches NOTHING fails the cook", missed is not null);
            t.ExpectTrue("and the refusal names what was there instead",
                missed?.Contains("stone_wall_01", StringComparison.Ordinal) == true);

            var miscount = Throws(() => MaterialPatch.Parse(origin: "count", text: "material stone_*{3} metallic=0.0\n").Apply(table));
            t.ExpectTrue("an expected count that does not hold fails the cook", miscount is not null);

            var stale = MaterialPatch.Parse(origin: "pin", text: "source deadbeef\nmaterial glass ior=1.5\n");
            t.ExpectTrue("a source pin that no longer matches fails the cook",
                Throws(() => stale.RequireSource("cafe1234")) is not null);
            t.ExpectTrue("and the same pin passes against the source it was written for",
                Throws(() => stale.RequireSource("deadbeef")) is null);

            t.ExpectTrue("an unknown key is refused rather than ignored",
                Throws(() => MaterialPatch.Parse(origin: "bad", text: "material glass nonsense=1\n").Apply(table)) is not null);

            // A patch may point an extension's texture at an image the asset already carries.
            // The leaf case: transmitted light tinted per texel by the same map the surface uses,
            // stated by the scene instead of assumed by the renderer.
            var textured = MaterialPatch.Parse(origin: "tex", text: "material glass diffuseTransmissionColorTexture=baseColor\n").Apply(table);
            t.Expect("a patch can point a transmission colour at the base-colour image",
                textured[0].Ext.DiffuseTransmissionColorImage == table[0].BaseColorImage,
                $"got image {textured[0].Ext.DiffuseTransmissionColorImage}, base is {table[0].BaseColorImage}");

            var cleared = MaterialPatch.Parse(origin: "tex0", text: "material glass diffuseTransmissionColorTexture=none\n").Apply(table);
            t.Expect("and can clear it back to the factor alone",
                cleared[0].Ext.DiffuseTransmissionColorImage == BlixMesh.NoImage,
                $"got {cleared[0].Ext.DiffuseTransmissionColorImage}");

            // A path would mean growing this file's image table; refusing says so rather than
            // silently doing nothing, which is how a scene learns the rule.
            t.ExpectTrue("but it cannot introduce an image the asset does not carry",
                Throws(() => MaterialPatch.Parse(origin: "texbad", text: "material glass diffuseTransmissionColorTexture=leaf.png\n").Apply(table)) is not null);

            // ── Cook configuration ──────────────────────────────────────────
            // One file per project decides every asset it names. What is worth asserting: an asset
            // is decided once, a line is never silently dropped, a mistyped source is not cooked as
            // the defaults, and an edit to one entry re-stamps that entry and no other.
            File.WriteAllText(Path.Combine(patchDir, "a.gltf"), "{}");
            File.WriteAllText(Path.Combine(patchDir, "b.gltf"), "{}");
            string Config(string name, string body)
            {
                var f = Path.Combine(patchDir, name);
                File.WriteAllText(f, body);
                return f;
            }

            const string twoAssets =
                "# the pack\nasset a.gltf -> packs/a\n  split 4096  # fine LOD\n  material glass ior=1.5\n"
                + "asset b.gltf\n  flip-v\n  split-foliage off\n  split-extent 2.5\n";
            var config = CookConfig.Load(Config("pack.blixcook", twoAssets));
            var entryA = config.For(Path.Combine(patchDir, "a.gltf"));
            var entryB = config.For(Path.Combine(patchDir, "b.gltf"));
            t.ExpectTrue("a configuration names each of its assets by source",
                config.Entries.Count == 2 && entryA is not null && entryB is not null);
            t.ExpectTrue("an entry carries its output, split and material rules",
                entryA is { Output: "packs/a", SplitTriBudget: 4096, FlipTextureV: false, Materials.Rules.Count: 1 });
            t.ExpectTrue("and another entry its own decisions, with no rules at all",
                entryB is { Output: null, FlipTextureV: true, SplitFoliage: false, SplitMaxExtent: 2.5f, Materials: null });
            t.ExpectTrue("an asset the configuration does not name is not in it",
                config.For(Path.Combine(patchDir, "c.gltf")) is null);
            t.ExpectTrue("its material rules stamp as the configuration's entry",
                entryA?.Materials?.StampFragment == $"config=pack.blixcook@{entryA?.Hash}");

            var commented = CookConfig.Load(Config("pack2.blixcook", twoAssets.Replace("# fine LOD", "# a different reason")));
            t.ExpectTrue("a comment edit changes no entry's stamp",
                commented.Entries[0].Hash == entryA?.Hash && commented.Entries[1].Hash == entryB?.Hash);
            var edited = CookConfig.Load(Config("pack3.blixcook", twoAssets.Replace("split-extent 2.5", "split-extent 3")));
            t.ExpectTrue("an edit to one entry re-stamps that entry and no other",
                edited.Entries[0].Hash == entryA?.Hash && edited.Entries[1].Hash != entryB?.Hash);

            t.ExpectThrows<InvalidDataException>("an asset named twice is refused",
                () => CookConfig.Load(Config("twice.blixcook", "asset a.gltf\n split 1\nasset ./a.gltf\n")));
            t.ExpectThrows<InvalidDataException>("a line before any asset is refused",
                () => CookConfig.Load(Config("orphan.blixcook", "split 4096\nasset a.gltf\n")));
            t.ExpectThrows<InvalidDataException>("an unknown entry line is refused rather than ignored",
                () => CookConfig.Load(Config("unknown.blixcook", "asset a.gltf\n  tangents on\n")));
            t.ExpectThrows<InvalidDataException>("a malformed value is refused",
                () => CookConfig.Load(Config("value.blixcook", "asset a.gltf\n  split-foliage maybe\n")));
            t.ExpectThrows<InvalidDataException>("a source that does not exist is refused, not cooked as the defaults",
                () => CookConfig.Load(Config("missing.blixcook", "asset c.gltf\n  split 4096\n")));
            t.ExpectThrows<InvalidDataException>("the build's recipe refuses an option beside the configuration",
                () => MeshRecipe.Cook(new Blix.Cooked.CookRequest(
                    Path.Combine(patchDir, "a.gltf"), Path.Combine(patchDir, "a.blixmesh"),
                    new Dictionary<string, string> { ["config"] = Path.Combine(patchDir, "pack.blixcook"), ["split"] = "8" })));
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

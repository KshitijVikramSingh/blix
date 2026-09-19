using System.Numerics;
using Blix;
using Blix.Assets;
using Blix.Cooked;
using Blix.Geometry;
using Blix.Graphics;
using SharpGLTF.Schema2;

namespace Blix.Recipes;

/// <summary>
/// glTF geometry to <c>.blixmesh</c>: the shipped mesh recipe.
/// </summary>
/// <remarks>
/// <para>
/// <b>This used to live inside <c>Blix/GltfStaticImporter.cs</c></b>, which made it engine code and
/// therefore a different kind of thing from a recipe a project writes for itself. Nothing about the
/// work needed it to be there — moving it out cost exactly two engine members becoming public,
/// <see cref="GltfStaticImporter.BuildStaticMeshData"/> and
/// <see cref="GltfStaticImporter.ComputeNormalMatrix"/>, and no internals grant.
/// </para>
/// <para>
/// What stayed behind is the right half: the format (<c>BlixMesh</c>, its reader, its writer and
/// the preamble) is engine, because the runtime reads it. What came here is the decision — which
/// nodes, which layout, whether to split, how far to decimate — which is a judgement about content
/// rather than a capability of the engine.
/// </para>
/// </remarks>
public static class MeshRecipe
{
    // Cook a .gltf/.glb to its .blixmesh sibling. CPU-only -- no GraphicsDevice
    // required; safe to invoke from the offline cook tool. Walks the same
    // node/primitive structure the runtime importer does, packs vertices via
    // BuildStaticMeshData, and serialises each primitive's
    // (name, materialIndex, bounds, vertexBytes, indices) to disk.
    // Target triangle ratios for the LOD chain (relative to full detail). The
    // chain stops early if a level doesn't reduce or falls below MinLodIndices.
    private static readonly float[] LodRatios = { 0.5f, 0.25f, 0.125f };
    private const int MinLodIndices = 96; // 32 triangles — below this, no point
    // Splitting floor: below this a chunk is not halved again whatever its extent, because each
    // split duplicates seam vertices and locks one more border against the simplifier.
    private const int MinSplitTris = 128;
    // <b>How long a chunk may be before distance stops meaning anything for it.</b> Selection uses
    // the distance to the nearest point of a chunk's bounds, so a chunk longer than this has parts
    // at wildly different distances answering to whichever end you stand near. Four metres is about
    // one Sponza arcade bay — close enough that a chunk is at one distance, far enough that the
    // scene does not shatter into thousands of drawables.
    public const float DefaultSplitMaxExtent = 4f;

    // Normal xyz + UV xy, the attributes a collapse is not allowed to wreck.
    private const int AttributeFloats = 5;
    // <b>Weights, and they are the one real judgement call in this file.</b> meshopt scores an edge
    // collapse by position error plus the weighted attribute error, so these set how much UV shear
    // a collapse may buy with a given amount of surface flatness. Normals are unit-length, so 0.5
    // makes a full right-angle normal flip cost about as much as moving the surface half a unit of
    // mesh extent. UVs run 0..1 across a texture, so 1.0 makes a collapse that slides the texture
    // across the whole image as expensive as losing the shape entirely — which is the trade this
    // was changed to make, because the sliding texture is the artifact that gets noticed.
    private static readonly float[] AttributeWeights = { 0.5f, 0.5f, 0.5f, 1.0f, 1.0f };

    // Reduced index list + the world-space geometric error it introduced
    // (max deviation from the original surface, in mesh units). Error drives
    // screen-space-error LOD selection: project it to pixels at the view
    // distance and switch when it's below a pixel threshold.
    public readonly record struct SimplifyResult(uint[] Indices, float WorldError);

    /// <summary>What a simplifier is given about a primitive.</summary>
    /// <remarks>
    /// Positions drove the whole decision on their own until they were shown not to be enough:
    /// a collapse can leave the surface where it was and still shear the UVs across it, which is
    /// what makes a pillar's texture slide as it changes level. <see cref="Attributes"/> is the
    /// interleaved per-vertex data that must survive too — <see cref="AttributeStride"/> floats
    /// each, one weight per float — and is empty for a caller that only cares about shape.
    /// </remarks>
    public readonly record struct SimplifyInput(
        float[] Positions,
        float[] Attributes,
        int AttributeStride,
        float[] AttributeWeights,
        uint[] Indices,
        int VertexCount,
        float TargetRatio);

    // Simplify callback: reduced index list + its world error, sharing the same
    // vertices. The cook tool supplies a meshoptimizer-backed implementation;
    // when null, the file is written LOD0-only (Blix has no simplifier of its own).
    public delegate SimplifyResult SimplifyFn(in SimplifyInput input);

    /// <summary>
    /// The mesh cook's own version, bumped whenever this method would produce different bytes from
    /// the same source and settings. Recorded in every file it writes, so a re-cook can be told
    /// from a rewrite.
    /// </summary>
    // v2: the material table (.blixmesh v5). Bumping this re-cooks every mesh in the tree, which
    // is the point — a v4 file has no table and the reader refuses it by name rather than reading
    // a material count out of whatever followed the last primitive.
    // v3: the image table (.blixmesh v6). Material channels index it instead of naming a glTF
    // logical image, which is what removes the source from the load path entirely.
    // v4: every KHR_materials_* property (.blixmesh v9). The cook was reading two of the thirteen
    // extensions SharpGLTF surfaces, so a cooked material could not carry what the raw importer had
    // begun to read — and a cooked path that silently defaults every extension while the raw path
    // reads them is the same producer/consumer split that let glass be transparent on screen and a
    // solid wall to the lighting.
    public const uint MeshRecipeVersion = 4;

    public static int CookToBlixMesh(
        string gltfPath, string outPath, bool flipTextureV = false, bool includeTangents = false,
        SimplifyFn? simplify = null, int splitTriBudget = 0, bool splitFoliage = true,
        float splitMaxExtent = DefaultSplitMaxExtent,
        MaterialPatch? patch = null, Action<string>? log = null)
    {
        ArgumentNullException.ThrowIfNull(gltfPath);
        ArgumentNullException.ThrowIfNull(outPath);

        // <b>A rigged glTF cooks as a rig, through the same recipe.</b> It is one recipe rather than
        // two because BlixRecipes.For refuses to guess when two recipes accept the same extension —
        // a second .gltf recipe would leave `blix cook` unable to choose — and because "is this
        // rigged?" is a property of the FILE, not a thing a caller should have to know before
        // asking for it cooked.
        //
        // Routed by the rigged importer's own refusal, exactly as `blix check --cooked` routes it:
        // it declines by name when no node carries both a mesh and a skin. Asking it first and
        // letting the refusal decide beats sniffing the JSON for a "skins" array, because the
        // question is not "does this file mention a skin" but "can this importer use it".
        if (TryCookRig(gltfPath, outPath, out var rigPrimitiveCount, patch, log)) return rigPrimitiveCount;

        var layout = includeTangents
            ? VertexPosition3NormalTangentTexture.Layout
            : VertexPosition3NormalTexture.Layout;
        var model = ModelRoot.Load(gltfPath);

        // <b>The hierarchy is recorded, not discarded, even though the vertices are baked.</b> The
        // cook folds each node's world transform into its positions, which is a large part of what
        // the flat load path buys — but it also threw away the authored structure, and the studio's
        // model view is built on exactly that: names, parents, pivots, per-part selection. It could
        // not open a cooked model at all.
        //
        // Every node is written, including ones carrying no geometry: a parent that holds only a
        // transform is still what its children are relative to, and pruning it breaks the
        // composition it exists for.
        var nodes = CookNodes(model, out var nodeOfLogical);

        var primitives = new List<BlixMeshPrimitive>();
        foreach (var node in model.LogicalNodes)
        {
            if (node.Mesh is null) continue;
            // F-016: engine is now row-vector form; matches SharpGLTF.
            var world = node.WorldMatrix;
            var normalMatrix = GltfStaticImporter.ComputeNormalMatrix(world);
            for (var i = 0; i < node.Mesh.Primitives.Count; i++)
            {
                var prim = node.Mesh.Primitives[i];
                var meshName = $"{node.Mesh.Name ?? node.Name ?? "gltf_mesh"}.{i}";
                var meshData = GltfStaticImporter.BuildStaticMeshData(meshName, prim, world, normalMatrix, flipTextureV, includeTangents);
                var materialIndex = prim.Material?.LogicalIndex ?? BlixMesh.NoMaterial;

                // Spatial split of oversized primitives so per-prim distance LOD
                // gets fine-grained — a huge floor/wall becomes many chunks, the
                // far ones coarsen while the near stay dense. Seam verts are
                // duplicated per chunk + LockBorder-locked (BuildLods) → crack-free
                // across LOD mismatch. Foliage (non-OPAQUE) optionally excluded so
                // the impostor track can own it instead.
                var isFoliage = prim.Material is { Alpha: not SharpGLTF.Schema2.AlphaMode.OPAQUE };
                var doSplit = splitTriBudget > 0 && (splitFoliage || !isFoliage);
                var chunks = doSplit
                    ? SplitPrimitive(meshData, layout.Stride, splitTriBudget, splitMaxExtent)
                    : new List<MeshData> { meshData };

                foreach (var chunk in chunks)
                {
                    primitives.Add(new BlixMeshPrimitive(
                        Name: chunk.Name,
                        Layout: layout,
                        NodeIndex: nodeOfLogical[node.LogicalIndex],
                        MaterialIndex: materialIndex,
                        Bounds: chunk.Bounds,
                        VertexCount: chunk.VertexCount,
                        VertexBytes: chunk.VertexBytes,
                        IndexFormat: chunk.IndexFormat,
                        Lods: BuildLods(chunk, layout.Stride, simplify)));
                }
            }
        }

        // Every setting that changes the bytes, recorded verbatim. Before this, --flip-v, --split
        // and --no-split-foliage silently altered the output and nothing anywhere said which had
        // been used — so the cooked half of the tree could not be reproduced from the tree, and
        // "cook it again and compare" was a test nobody could write. Authored order, not sorted,
        // so the string is stable across runs and a byte-compare means something.
        //
        // `simplify` is in here because a null simplifier writes LOD0 only: same source, same
        // flags, a different file. That it is a delegate rather than a flag is exactly why it was
        // the easiest one to forget.
        var parameters =
            $"flipV={(flipTextureV ? 1 : 0)} tangents={(includeTangents ? 1 : 0)} " +
            $"split={splitTriBudget}@{splitMaxExtent:0.##}m splitFoliage={(splitFoliage ? 1 : 0)} " +
            $"simplify={(simplify is null ? "none" : "yes")}" +
            // Recorded, so `blix inspect` can answer "where did this material's sheen come from"
            // without anyone reading a shader. An artifact that was patched and cannot say so is
            // the same unexplainable state the heuristic left behind.
            (patch is null ? "" : $" {patch.StampFragment}");

        var (images, imageRows) = CookImages(model, gltfPath, outPath);

        // <b>The flag can finally be FALSE, and this is the debt stage K-A wrote down.</b> A
        // .blixmesh used to declare its source permanently required because every material was
        // re-parsed from the glTF on each load; K-F cooked the materials and narrowed the debt to
        // image bytes; the image table removes the last reason to open the source at all. So when
        // every image resolves to a cooked artifact, nothing is owed — the first artifact in this
        // tree that can be shipped, moved or opened on its own.
        //
        // When some image is still an uncooked PNG, the thing wanted is that IMAGE and not the
        // glTF, and the narrow flag says so rather than implying the source is needed whole.
        var everyImageCooked = images.All(
            i => i.Resource.EndsWith(".blixtex", StringComparison.OrdinalIgnoreCase));

        var stamp = CookStamp.Of(
            BlixMesh.ShippedRecipe, MeshRecipeVersion, gltfPath, outPath, parameters,
            everyImageCooked
                ? CookedFlags.None
                : CookedFlags.SourceRequired | CookedFlags.SourceRequiredForImagesOnly);

        BlixMeshWriter.Write(
            outPath,
            new BlixMeshFile(primitives, CookMaterials(model, imageRows, patch, log), images, Nodes: nodes),
            stamp);
        return primitives.Count;
    }

    /// <summary>
    /// Cooks a rigged glTF — skinned vertices, skins and clips — or returns false if it is not one.
    /// </summary>
    /// <remarks>
    /// <b>This is the last category of asset in this tree with no cooked form.</b>
    /// <c>blix check --cooked</c> said so on every rigged file: "a rigged glTF has no cooked form —
    /// .blixmesh holds no skinned vertex layout". Measured, the four RTSGame villagers spent ~890 ms
    /// of their ~1,950 ms on geometry and skin, which is about half a second off every launch and is
    /// behind nothing.
    /// <para>
    /// The vertices come from <see cref="GltfImporter"/> rather than being rebuilt here, and that is
    /// deliberate: joint remapping and weight normalisation are subtle, the importer already does
    /// them, and a cook that reimplemented them would be a second opinion whose disagreements would
    /// show up as a character loading differently once cooked.
    /// </para>
    /// </remarks>
    private static bool TryCookRig(
        string gltfPath, string outPath, out int primitiveCount,
        MaterialPatch? patch = null, Action<string>? log = null)
    {
        primitiveCount = 0;

        GltfModel rig;
        try
        {
            // ImportSource, not Import — a recipe must never read through the cooked path, or it
            // consumes its own previous output. See that method for why this is the second time.
            rig = new GltfImporter().ImportSource(new AssetImportContext(AssetId.Parse("cook/rig"), gltfPath));
        }
        catch (AssetImportException noRig) when (noRig.Message.Contains("no rig here", StringComparison.Ordinal))
        {
            return false;
        }

        var skins = rig.SkinsOrEmpty;
        if (skins.Length == 0) return false;

        var model = ModelRoot.Load(gltfPath);
        var (images, imageRows) = CookImages(model, gltfPath, outPath);

        var primitives = rig.Primitives.Select(CookPrimitive).ToArray();

        var cookedSkins = skins.Select(skin => new BlixMeshSkin(
            skin.Skeleton.Bones
                .Select(b => new BlixMeshBone(b.Name, b.ParentIndex, b.InverseBindPose))
                .ToArray(),
            skin.MeshNodeTransform)).ToArray();

        var clips = rig.Animations.Select(CookClip).ToArray();

        // <b>Attachments and static parts cook too, and making that possible is why a primitive
        // owns its layout.</b> Both are built through the STATIC path, so their vertices are a
        // different width from the skinned ones beside them. While a .blixmesh carried one layout
        // per file, the only honest options were to drop them — a cooked Rogue whose cape and two
        // knives had quietly vanished — or to refuse to cook any rig that had them, which left the
        // half-cooked category this stage exists to remove. Of the tree's eight rigged assets
        // exactly one has attachments, so refusing would have looked fine and consolidated nothing.
        var attachments = rig.AttachmentsOrEmpty.Select(a => new BlixMeshAttachment(
            a.Name, a.JointName, a.JointIndex, a.SkinIndex, a.LocalTransform,
            a.Primitives.Select(CookPrimitive).ToArray())).ToArray();

        var staticParts = rig.StaticPartsOrEmpty.Select(sp => new BlixMeshStaticPart(
            sp.Name, sp.WorldTransform, sp.Primitives.Select(CookPrimitive).ToArray())).ToArray();

        var everyImageCooked = images.All(
            i => i.Resource.EndsWith(".blixtex", StringComparison.OrdinalIgnoreCase));

        var parameters =
            $"rig=1 skins={cookedSkins.Length} bones={cookedSkins.Sum(s => s.Bones.Length)} "
            + $"clips={clips.Length} attachments={attachments.Length} staticParts={staticParts.Length}"
            + (patch is null ? "" : $" {patch.StampFragment}");

        var stamp = CookStamp.Of(
            BlixMesh.ShippedRecipe, MeshRecipeVersion, gltfPath, outPath, parameters,
            everyImageCooked
                ? CookedFlags.None
                : CookedFlags.SourceRequired | CookedFlags.SourceRequiredForImagesOnly);

        BlixMeshWriter.Write(
            outPath,
            new BlixMeshFile(
                primitives, CookMaterials(model, imageRows, patch, log), images, cookedSkins, clips,
                attachments, staticParts),
            stamp);

        primitiveCount = primitives.Length;
        return true;
    }

    /// <summary>
    /// The authored node hierarchy, in an order where every parent precedes its children.
    /// </summary>
    /// <remarks>
    /// <b>Re-ordered rather than written as found.</b> glTF does not promise parents come first, and
    /// a consumer composing world matrices in one forward pass needs them to — which is the same
    /// invariant <c>Skeleton</c> enforces on bones, for the same reason. The reader refuses a file
    /// that violates it, naming the node, so a re-order that went wrong cannot pass quietly.
    /// </remarks>
    private static IReadOnlyList<BlixMeshNode> CookNodes(ModelRoot model, out int[] nodeOfLogical)
    {
        var map = new int[model.LogicalNodes.Count];
        Array.Fill(map, -1);

        var ordered = new List<Node>();
        var placed = new HashSet<int>();

        void Place(Node node)
        {
            if (!placed.Add(node.LogicalIndex)) return;
            if (node.VisualParent is { } parent) Place(parent);
            map[node.LogicalIndex] = ordered.Count;
            ordered.Add(node);
        }

        foreach (var node in model.LogicalNodes) Place(node);

        nodeOfLogical = map;
        return ordered
            .Select(n => new BlixMeshNode(
                n.Name ?? $"node_{n.LogicalIndex}",
                n.VisualParent is { } p ? map[p.LogicalIndex] : -1,
                n.LocalMatrix))
            .ToList();
    }

    /// <summary>One imported primitive as the format stores it, layout and skin included.</summary>
    private static BlixMeshPrimitive CookPrimitive(GltfPrimitive p) => new(
        Name: p.Mesh.Name,
        Layout: p.Mesh.Layout,
        MaterialIndex: p.MaterialIndex,
        Bounds: p.Mesh.Bounds,
        VertexCount: p.Mesh.VertexCount,
        VertexBytes: p.Mesh.VertexBytes,
        IndexFormat: p.Mesh.IndexFormat,
        Lods: new[] { new BlixMeshLod(p.Mesh.Indices, p.Mesh.Indices32) },
        SkinIndex: p.SkinIndex);

    /// <summary>One animation, as keyframes.</summary>
    /// <remarks>
    /// <b>Keyframes rather than curve objects, because keyframes are what the source had.</b> The
    /// glTF importer builds exactly two curve types — <c>KeyframeVector3Curve</c> and
    /// <c>KeyframeQuaternionCurve</c> — each from a plain array of (time, value). Writing those
    /// arrays back is lossless; writing a serialised "curve" would be inventing a representation
    /// for something that is already one.
    /// </remarks>
    private static BlixMeshClip CookClip(AnimationClip clip) => new(
        clip.Name,
        clip.Tracks.Select(t => new BlixMeshTrack(
            t.BoneIndex,
            VectorKeys(t.Translation),
            QuaternionKeys(t.Rotation),
            VectorKeys(t.Scale))).ToArray());

    private static BlixMeshVectorKey[] VectorKeys(IFiniteCurve<Vector3>? curve) =>
        curve is KeyframeVector3Curve k
            ? k.Keyframes.Select(x => new BlixMeshVectorKey((float)x.Time, x.Value)).ToArray()
            : Array.Empty<BlixMeshVectorKey>();

    private static BlixMeshQuaternionKey[] QuaternionKeys(IFiniteCurve<Quaternion>? curve) =>
        curve is KeyframeQuaternionCurve k
            ? k.Keyframes.Select(x => new BlixMeshQuaternionKey((float)x.Time, x.Value)).ToArray()
            : Array.Empty<BlixMeshQuaternionKey>();

    /// <summary>
    /// The relative URIs of the external images this asset's materials actually reference.
    /// </summary>
    /// <remarks>
    /// <b>For cooking what an asset USES rather than what a folder CONTAINS.</b> Main Sponza ships
    /// 137 texture files and its own glTF names 72 of them; sweeping the directory spends a quarter
    /// of the time and a quarter of the bytes on images nothing will ever sample. Following
    /// references is what makes a cooked tree smaller than the source tree rather than larger.
    /// <para>
    /// Embedded images are not listed: they have no URI to cook from, and the mesh cook extracts
    /// and cooks them itself as it builds the image table.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<string> ReferencedImageUris(string gltfPath)
    {
        ArgumentNullException.ThrowIfNull(gltfPath);

        var model = ModelRoot.Load(gltfPath);
        var gltfDir = Path.GetDirectoryName(Path.GetFullPath(gltfPath)) ?? ".";
        var uris = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var material in model.LogicalMaterials)
        {
            foreach (var channelName in ImageChannels)
            {
                var image = material.FindChannel(channelName)?.Texture?.PrimaryImage;
                if (image?.Content.SourcePath is not { } uri) continue;
                if (uri.StartsWith("data:", StringComparison.Ordinal)) continue;

                var relative = RelativeImagePath(gltfDir, uri);
                if (seen.Add(relative)) uris.Add(relative);
            }
        }

        return uris;
    }

    /// <summary>
    /// Every image the source's materials reference, and where each one's pixels ended up.
    /// </summary>
    /// <remarks>
    /// <b>This is what lets the cooked file be loaded with no source present.</b> The table records
    /// a LOCATION the recipe knows to be true, rather than a rule the loader applies later — see the
    /// note in <c>BlixMesh</c> on why deriving a location by swapping a URI's extension was a
    /// grouping policy in disguise.
    /// <para>
    /// <b>Embedded images stop being a special case here, and that is the whole trick.</b> A glTF
    /// with its pixels inline has no path to record, so the cook EXTRACTS each one, cooks it beside
    /// the mesh, and writes a row that looks like every other row. Everything above this method
    /// sees one shape.
    /// </para>
    /// <para>
    /// Only images the MATERIALS reach are recorded. A glTF may carry images no channel samples, and
    /// a directory may carry many more — main Sponza ships 137 texture files of which its own glTF
    /// names 72 — so following references rather than sweeping is worth a quarter of the bytes
    /// before any format decision is made.
    /// </para>
    /// </remarks>
    private static (IReadOnlyList<BlixMeshImage> Images, Dictionary<int, int> Rows) CookImages(
        ModelRoot model, string gltfPath, string outPath)
    {
        var images = new List<BlixMeshImage>();
        var rows = new Dictionary<int, int>();
        var sourceDir = Path.GetDirectoryName(Path.GetFullPath(gltfPath)) ?? ".";
        var outDir = Path.GetDirectoryName(Path.GetFullPath(outPath)) ?? ".";
        var extractDir = Path.Combine(
            outDir, Path.GetFileNameWithoutExtension(outPath) + BlixMesh.ExtractedImageFolder);

        foreach (var material in model.LogicalMaterials)
        {
            foreach (var channelName in ImageChannels)
            {
                var image = material.FindChannel(channelName)?.Texture?.PrimaryImage;
                if (image is null || rows.ContainsKey(image.LogicalIndex)) continue;

                // <b>The channel IS the role, so it is passed rather than re-sniffed.</b> An
                // extracted image's name is one this cook invents, and TextureRecipe's classifier
                // reads names: Rogue's base colour came out "rogue_texture", matched nothing, and
                // cooked linear instead of sRGB. The character rendered blown out and nothing
                // errored.
                var role = channelName switch
                {
                    "BaseColor" => TextureRole.BaseColor,
                    "Normal" => TextureRole.Normal,
                    "MetallicRoughness" => TextureRole.MetallicRoughness,
                    "Emissive" => TextureRole.Emissive,
                    // Occlusion is single-channel linear data. It is not MetallicRoughness, whose
                    // loader zeroes a channel to build the ORM layout.
                    _ => TextureRole.Linear,
                };

                var bytes = image.Content.Content;
                var hash = ContentHash(bytes.Span);
                var name = image.Name
                    ?? (image.Content.SourcePath is { } uri ? Path.GetFileNameWithoutExtension(uri) : null)
                    ?? $"image_{image.LogicalIndex}";

                rows[image.LogicalIndex] = images.Count;
                images.Add(new BlixMeshImage(name, hash, Shippable(Resource(image, bytes, name, role), gltfPath)));
            }
        }

        return (images, rows);

        string Resource(SharpGLTF.Schema2.Image image, ReadOnlyMemory<byte> bytes, string name, TextureRole role)
        {
            // External: the file is already on disk beside the glTF. Prefer the cooked artifact
            // when one is there — the asset driver cooks textures BEFORE the mesh precisely so that
            // this check sees them — and otherwise name the source image, which is still a location
            // the loader can open without the glTF.
            if (image.Content.SourcePath is { } uri && !uri.StartsWith("data:", StringComparison.Ordinal))
            {
                var relative = RelativeImagePath(sourceDir, uri);
                var cooked = Path.ChangeExtension(relative, ".blixtex");

                // <b>Checked against the OUTPUT directory, because that is where the loader will
                // look.</b> Resource is relative to the cooked mesh, so for an in-place cook this is
                // the same directory as the source and the distinction is invisible; for a cook into
                // a separate tree it is the whole difference between a path that resolves and one
                // that points back at a folder the user is about to delete.
                return File.Exists(Path.Combine(outDir, cooked)) ? cooked : relative;
            }

            // Embedded: invent a location and put the pixels there.
            Directory.CreateDirectory(extractDir);
            var stem = Sanitise(name);
            var raw = Path.Combine(extractDir, stem + ExtensionFor(bytes.Span));
            File.WriteAllBytes(raw, bytes.ToArray());

            var cookedPath = Path.ChangeExtension(raw, ".blixtex");
            try
            {
                TextureRecipe.CookOne(raw, cookedPath, out _, out _, role);
                // The intermediate is scaffolding, not an artifact. Leaving it would double the
                // bytes and put a second, uncooked copy of every embedded image on disk.
                File.Delete(raw);
                return ToRelative(cookedPath);
            }
            catch (Exception e) when (e is IOException or NotSupportedException or InvalidDataException)
            {
                // A format the texture cook will not take is recorded as its raw self rather than
                // dropped: the mesh still loads, the image still resolves, and `blix check --cooked`
                // reports it on the slow path where a person can see it.
                return ToRelative(raw);
            }
        }

        string ToRelative(string full) =>
            Path.GetRelativePath(outDir, full).Replace(Path.DirectorySeparatorChar, '/');
    }

    /// <summary>The channels whose images are recorded — the same five the loader pre-decodes.</summary>
    private static readonly string[] ImageChannels =
        { "BaseColor", "Normal", "MetallicRoughness", "Occlusion", "Emissive" };

    /// <summary>
    /// An image reference as a path RELATIVE to the asset, whatever form the parser handed back.
    /// </summary>
    /// <remarks>
    /// <b>SharpGLTF resolves <c>Image.Content.SourcePath</c> to an ABSOLUTE path, and that cost real
    /// damage.</b> The value was used directly as a relative reference, so
    /// <c>Path.Combine(outDir, it)</c> silently discarded <c>outDir</c> — .NET's documented
    /// behaviour for a rooted second argument — and an out-of-place cook wrote its cooked textures
    /// back into the SOURCE folder while reporting that it had written them to the output. Both the
    /// "did I cook this?" check and the recorded location then agreed with each other and with
    /// nothing else, which is why the mesh cheerfully declared itself self-contained.
    /// <para>
    /// Normalised through the asset's own directory so a relative URI, an absolute path and an
    /// escaped one all arrive as the same forward-slashed relative string.
    /// </para>
    /// </remarks>
    private static string RelativeImagePath(string assetDir, string sourcePath)
    {
        var unescaped = Uri.UnescapeDataString(sourcePath);
        var full = Path.GetFullPath(Path.Combine(assetDir, unescaped));
        return Path.GetRelativePath(assetDir, full).Replace(Path.DirectorySeparatorChar, '/');
    }

    /// <summary>
    /// Refuses a location that would take a reader outside the cooked tree.
    /// </summary>
    /// <remarks>
    /// A cooked artifact exists to be moved and shipped, so a row pointing at an absolute path or
    /// climbing out with <c>..</c> is not a slightly-wrong file — it is a file that works on this
    /// machine and nowhere else. Thrown rather than logged: this is a bug in a recipe, and the
    /// previous version of it wrote 85 MB into a folder it had been asked not to touch.
    /// </remarks>
    private static string Shippable(string resource, string gltfPath)
    {
        if (Path.IsPathRooted(resource) || resource.StartsWith("../", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"{gltfPath}: image resource '{resource}' leaves the cooked tree — a cooked asset "
                + "must reference only what travels with it.");
        }

        return resource;
    }

    /// <summary>FNV-1a over the source bytes: identity that survives a rename and equates copies.</summary>
    private static ulong ContentHash(ReadOnlySpan<byte> bytes)
    {
        var hash = 14695981039346656037UL;
        foreach (var b in bytes)
        {
            hash ^= b;
            hash *= 1099511628211UL;
        }

        return hash;
    }

    /// <summary>Sniffs a container so an extracted image lands with an extension the cook accepts.</summary>
    private static string ExtensionFor(ReadOnlySpan<byte> bytes) =>
        bytes.Length >= 8 && bytes[0] == 0x89 && bytes[1] == 0x50 ? ".png" : ".jpg";

    private static string Sanitise(string name)
    {
        var chars = name.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            if (Array.IndexOf(Path.GetInvalidFileNameChars(), chars[i]) >= 0) chars[i] = '_';
        }

        return new string(chars);
    }

    /// <summary>
    /// Every material the source declares, in its own order, with image references in place of
    /// image bytes.
    /// </summary>
    /// <remarks>
    /// <b>In the source's order and in full, including materials no primitive uses.</b> A
    /// primitive's MaterialIndex is a glTF logical-material index and was one before this table
    /// existed, so writing a compacted table would silently change what that number means in every
    /// file already on disk. The table is small — a name and twenty-odd scalars each — and an index
    /// that still means what it says is worth more than the bytes.
    /// <para>
    /// This reads the same channels <c>GltfShared.ExtractMaterial</c> does and must keep reading
    /// them: a property the cook drops is one the loader stops seeing the moment a mesh is cooked,
    /// which shows up as an asset that renders differently on machines that have cooked it.
    /// </para>
    /// </remarks>
    private static IReadOnlyList<BlixMeshMaterial> CookMaterials(
        ModelRoot model, IReadOnlyDictionary<int, int> imageRows,
        MaterialPatch? patch = null, Action<string>? log = null)
    {
        var cooked = new BlixMeshMaterial[model.LogicalMaterials.Count];
        for (var i = 0; i < cooked.Length; i++)
        {
            var m = model.LogicalMaterials[i];

            var baseColor = m.FindChannel("BaseColor");
            var normal = m.FindChannel("Normal");
            var mr = m.FindChannel("MetallicRoughness");
            var occlusion = m.FindChannel("Occlusion");
            var emissive = m.FindChannel("Emissive");
            var transmission = m.FindChannel("Transmission");

            var emissiveColour = emissive.HasValue ? emissive.Value.Color : Vector4.Zero;

            cooked[i] = new BlixMeshMaterial(
                Name: m.Name ?? $"material_{i}",
                BaseColorFactor: baseColor.HasValue ? baseColor.Value.Color : Vector4.One,
                BaseColorTexCoord: baseColor.HasValue ? baseColor.Value.TextureCoordinate : 0,
                // Defaults are the glTF spec's for an absent channel, not zero: a material with no
                // MetallicRoughness channel is metallic 1 / rough 1, and writing 0 would quietly
                // turn every such surface into a mirror.
                MetallicFactor: Parameter(mr, "MetallicFactor", 1f),
                RoughnessFactor: Parameter(mr, "RoughnessFactor", 1f),
                OcclusionStrength: Parameter(occlusion, "Strength", 1f),
                EmissiveFactor: new Vector3(emissiveColour.X, emissiveColour.Y, emissiveColour.Z),
                EmissiveStrength: Parameter(emissive, "EmissiveStrength", 1f),
                AlphaMode: m.Alpha switch
                {
                    SharpGLTF.Schema2.AlphaMode.MASK => BlixMesh.AlphaMask,
                    SharpGLTF.Schema2.AlphaMode.BLEND => BlixMesh.AlphaBlend,
                    _ => BlixMesh.AlphaOpaque,
                },
                AlphaCutoff: m.AlphaCutoff,
                DoubleSided: m.DoubleSided,
                TransmissionFactor: Parameter(transmission, "TransmissionFactor", 0f),
                Extensions: CookExtensions(m, ImageIndex),
                BaseColorImage: ImageIndex(baseColor),
                NormalImage: ImageIndex(normal),
                MetallicRoughnessImage: ImageIndex(mr),
                OcclusionImage: ImageIndex(occlusion),
                EmissiveImage: ImageIndex(emissive));
        }

        // Applied here, once, on the cooked table — so a patched property is indistinguishable at
        // load from one the asset authored, and every consumer (renderer, voxel baker, inspect)
        // reads the same number. Patching at load instead would mean each consumer applying it, and
        // the ones that forgot would disagree with the ones that did, which is the exact split this
        // whole mechanism exists to end.
        return patch is null ? cooked : patch.Apply(cooked, log);

        // <b>A ROW IN THIS FILE'S OWN IMAGE TABLE, not a glTF logical image index.</b> The old
        // number could only be resolved by reopening the glTF, which is precisely why a "cooked"
        // mesh still pinned its source. Mapped through imageRows so the cooked file is readable
        // with nothing else present.
        int ImageIndex(MaterialChannel? channel)
        {
            var logical = channel?.Texture?.PrimaryImage?.LogicalIndex;
            return logical is not null && imageRows.TryGetValue(logical.Value, out var row)
                ? row
                : BlixMesh.NoImage;
        }

        float Parameter(MaterialChannel? channel, string name, float fallback)
        {
            if (channel is null) return fallback;
            foreach (var p in channel.Value.Parameters)
            {
                if (p.Name == name) return (float)Convert.ToDouble(p.Value);
            }

            return fallback;
        }
    }

    // LOD0 (full) + decimated levels via the injected simplifier. All levels
    // share the primitive's index format (decimated indices reference the same
    // vertex buffer, so a u16 primitive stays u16).
    private static IReadOnlyList<BlixMeshLod> BuildLods(MeshData meshData, int stride, SimplifyFn? simplify)
    {
        // LOD0 is the original surface: zero geometric error.
        var lods = new List<BlixMeshLod> { new(meshData.Indices, meshData.Indices32, Error: 0f) };
        if (simplify is null) return lods;

        var baseIndices = meshData.Indices32 ?? Array.ConvertAll(meshData.Indices, idx => (uint)idx);
        if (baseIndices.Length < MinLodIndices) return lods;

        var positions = new float[meshData.VertexCount * 3];
        // Normal (3) + UV (2), interleaved, in the order the weights below expect. Both layouts
        // this cook writes put the normal immediately after the position; only the UV moves, and
        // the stride is what says which layout this is — 48 bytes with a tangent between them,
        // 32 without. Reading the UV from the wrong offset would feed the simplifier the tangent's
        // xy and quietly protect the wrong thing.
        var uvFloatOffset = stride == 48 ? 10 : 6;
        var attributes = new float[meshData.VertexCount * AttributeFloats];
        for (var v = 0; v < meshData.VertexCount; v++)
        {
            var o = v * stride;
            positions[v * 3 + 0] = BitConverter.ToSingle(meshData.VertexBytes, o);
            positions[v * 3 + 1] = BitConverter.ToSingle(meshData.VertexBytes, o + 4);
            positions[v * 3 + 2] = BitConverter.ToSingle(meshData.VertexBytes, o + 8);
            var a = v * AttributeFloats;
            attributes[a + 0] = BitConverter.ToSingle(meshData.VertexBytes, o + 12);
            attributes[a + 1] = BitConverter.ToSingle(meshData.VertexBytes, o + 16);
            attributes[a + 2] = BitConverter.ToSingle(meshData.VertexBytes, o + 20);
            attributes[a + 3] = BitConverter.ToSingle(meshData.VertexBytes, o + uvFloatOffset * 4);
            attributes[a + 4] = BitConverter.ToSingle(meshData.VertexBytes, o + uvFloatOffset * 4 + 4);
        }

        var prevCount = baseIndices.Length;
        foreach (var ratio in LodRatios)
        {
            var result = simplify(new SimplifyInput(
                positions, attributes, AttributeFloats, AttributeWeights,
                baseIndices, meshData.VertexCount, ratio));
            var reduced = result.Indices;
            if (reduced.Length < MinLodIndices || reduced.Length >= prevCount) break;
            prevCount = reduced.Length;
            lods.Add(meshData.IndexFormat == IndexFormat.UInt32
                ? new BlixMeshLod(null, reduced, result.WorldError)
                : new BlixMeshLod(Array.ConvertAll(reduced, idx => (ushort)idx), null, result.WorldError));
        }
        return lods;
    }

    // Recursively split a primitive's triangles into spatial chunks under
    // triBudget, each a self-contained MeshData (own gathered + reindexed
    // vertices). Median split along the longest centroid axis. Seam vertices
    // are duplicated across chunks — with BuildLods' LockBorder this keeps chunk
    // boundaries watertight even when adjacent chunks pick different LOD levels.
    //
    // <b>And under an EXTENT, which is the half this was missing.</b> A triangle budget splits
    // dense primitives and leaves sparse ones whole, and a sparse primitive is exactly the one that
    // hurts: a thirty-metre wall carrying a few hundred triangles is one drawable with one level of
    // detail and one distance, so standing at one end of it holds the far end at full detail and no
    // budget can coarsen the part you are not near. Distance-based selection is only as good as the
    // granularity it selects over, and that granularity is a LENGTH.
    private static List<MeshData> SplitPrimitive(MeshData mesh, int stride, int triBudget, float maxExtent)
    {
        var baseIdx = mesh.Indices32 ?? Array.ConvertAll(mesh.Indices, idx => (uint)idx);
        var triCount = baseIdx.Length / 3;
        if (triCount <= triBudget && ExtentOf(mesh, baseIdx, stride) <= maxExtent)
        {
            // Nothing to do only if BOTH hold.
        }
        else if (triCount <= MinSplitTris)
        {
            // A chunk this small cannot usefully be halved again — and every split duplicates its
            // seam vertices and locks another border against the simplifier, so splitting past this
            // point costs memory and decimation for granularity nothing will use.
            return new List<MeshData> { mesh };
        }
        else
        {
            return SplitPrimitiveCore(mesh, stride, triBudget, maxExtent, baseIdx, triCount);
        }
        return new List<MeshData> { mesh };
    }

    // Longest edge of a primitive's world-space bounds.
    private static float ExtentOf(MeshData mesh, uint[] baseIdx, int stride)
    {
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        foreach (var idx in baseIdx)
        {
            var pos = VertexPosition(mesh.VertexBytes, idx, stride);
            min = Vector3.Min(min, pos);
            max = Vector3.Max(max, pos);
        }
        var e = max - min;
        return MathF.Max(e.X, MathF.Max(e.Y, e.Z));
    }

    private static List<MeshData> SplitPrimitiveCore(
        MeshData mesh, int stride, int triBudget, float maxExtent, uint[] baseIdx, int triCount)
    {

        var centroids = new Vector3[triCount];
        for (var t = 0; t < triCount; t++)
        {
            var a = VertexPosition(mesh.VertexBytes, baseIdx[t * 3], stride);
            var b = VertexPosition(mesh.VertexBytes, baseIdx[t * 3 + 1], stride);
            var c = VertexPosition(mesh.VertexBytes, baseIdx[t * 3 + 2], stride);
            centroids[t] = (a + b + c) / 3f;
        }

        var leaves = new List<int[]>();
        var allTris = new int[triCount];
        for (var t = 0; t < triCount; t++) allTris[t] = t;
        SplitTriangles(allTris, centroids, triBudget, maxExtent, leaves);

        var chunks = new List<MeshData>(leaves.Count);
        var chunkIdx = 0;
        foreach (var leaf in leaves)
            chunks.Add(BuildChunk($"{mesh.Name}#{chunkIdx++}", mesh, baseIdx, leaf, stride));
        return chunks;
    }

    private static void SplitTriangles(
        int[] tris, Vector3[] centroids, int budget, float maxExtent, List<int[]> leaves)
    {
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        foreach (var t in tris) { min = Vector3.Min(min, centroids[t]); max = Vector3.Max(max, centroids[t]); }
        var ext = max - min;
        var longest = MathF.Max(ext.X, MathF.Max(ext.Y, ext.Z));
        // Either criterion keeps the recursion going; MinSplitTris is the floor that stops it.
        if ((tris.Length <= budget && longest <= maxExtent) || tris.Length <= MinSplitTris)
        {
            leaves.Add(tris);
            return;
        }
        var axis = ext.X >= ext.Y && ext.X >= ext.Z ? 0 : (ext.Y >= ext.Z ? 1 : 2);
        Array.Sort(tris, (p, q) => Axis(centroids[p], axis).CompareTo(Axis(centroids[q], axis)));
        var mid = tris.Length / 2;
        // Degenerate (centroids coincide along the split axis) — emit whole.
        if (mid == 0 || mid == tris.Length) { leaves.Add(tris); return; }
        SplitTriangles(tris[..mid], centroids, budget, maxExtent, leaves);
        SplitTriangles(tris[mid..], centroids, budget, maxExtent, leaves);
    }

    private static float Axis(Vector3 v, int a) => a == 0 ? v.X : (a == 1 ? v.Y : v.Z);

    private static Vector3 VertexPosition(byte[] vbytes, uint vtx, int stride)
    {
        var o = (int)vtx * stride;
        return new Vector3(
            BitConverter.ToSingle(vbytes, o),
            BitConverter.ToSingle(vbytes, o + 4),
            BitConverter.ToSingle(vbytes, o + 8));
    }

    // Gather the vertices a chunk's triangles reference, reindex compactly, copy
    // their vertex bytes, recompute bounds. Downgrades to u16 indices when the
    // chunk's vertex count fits (split chunks are far smaller than the parent).
    private static MeshData BuildChunk(string name, MeshData mesh, uint[] baseIdx, int[] triIds, int stride)
    {
        var remap = new Dictionary<uint, uint>();
        var newVerts = new List<uint>();
        var newIndices = new uint[triIds.Length * 3];
        var w = 0;
        foreach (var t in triIds)
        {
            for (var k = 0; k < 3; k++)
            {
                var ov = baseIdx[t * 3 + k];
                if (!remap.TryGetValue(ov, out var nv))
                {
                    nv = (uint)newVerts.Count;
                    remap[ov] = nv;
                    newVerts.Add(ov);
                }
                newIndices[w++] = nv;
            }
        }

        var vbytes = new byte[newVerts.Count * stride];
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        for (var v = 0; v < newVerts.Count; v++)
        {
            Array.Copy(mesh.VertexBytes, (int)newVerts[v] * stride, vbytes, v * stride, stride);
            var p = VertexPosition(mesh.VertexBytes, newVerts[v], stride);
            min = Vector3.Min(min, p); max = Vector3.Max(max, p);
        }
        var bounds = new Bounds3(min, max);

        if (newVerts.Count <= ushort.MaxValue + 1)
        {
            var u16 = new ushort[newIndices.Length];
            for (var i = 0; i < newIndices.Length; i++) u16[i] = (ushort)newIndices[i];
            return new MeshData(name, vbytes, u16, mesh.Layout, bounds);
        }
        return new MeshData(name, vbytes, Array.Empty<ushort>(), mesh.Layout, bounds, Indices32: newIndices);
    }



    /// <summary>
    /// The decimation the shipped mesh cook uses, and the only one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This was a lambda inside the cook driver, and leaving it there was a silent downgrade.</b>
    /// The uniform <see cref="Cook"/> path passed no simplifier, so a build rule invoking a recipe
    /// produced LOD0-only files while the same recipe invoked by hand produced full LOD chains —
    /// the build quietly making worse output than the command, which is exactly the class of thing
    /// this arc exists to stop. Caught by watching Rogue.blixmesh lose its chain on the first
    /// build-rule run.
    /// </para>
    /// <para>
    /// <b>Prune, not LockBorder, except when splitting.</b> LockBorder pins every mesh-boundary
    /// vertex, which is right when spatially split chunks must stay watertight where they meet and
    /// ruinous otherwise: on a stylised tree, whose canopy is hundreds of separate leaf clusters,
    /// nearly every vertex is a border vertex, so locking them forbids collapsing anything at all —
    /// measured, a 4,345 triangle tree reduced to 3,975 and stopped. Prune lets whole components
    /// go, which for foliage is the correct behaviour rather than a compromise: what a canopy looks
    /// like from further away is fewer, larger masses.
    /// </para>
    /// </remarks>
    public static SimplifyFn DefaultSimplifier(bool splitting)
    {
        var options = MeshoptNative.Options.Prune;
        if (splitting) options |= MeshoptNative.Options.LockBorder;

        return (in SimplifyInput input) =>
        {
            var reduced = input.Attributes.Length > 0
                ? MeshoptNative.SimplifyWithAttributes(
                    input.Indices, input.Positions, input.VertexCount, 3,
                    input.Attributes, input.AttributeStride, input.AttributeWeights,
                    input.TargetRatio, targetError: 1.0f, options, out var relError)
                : MeshoptNative.Simplify(
                    input.Indices, input.Positions, input.VertexCount, 3,
                    input.TargetRatio, targetError: 1.0f, options, out relError);

            // meshopt's error is relative to the mesh extent; scale it to world units so the
            // runtime can project it to screen pixels.
            var scale = MeshoptNative.SimplifyScale(input.Positions, input.VertexCount, 3);
            return new SimplifyResult(reduced, relError * scale);
        };
    }

    /// <summary>The uniform entry point the index finds and <c>blix cook</c> calls.</summary>
    /// <remarks>
    /// Sits beside the typed <see cref="CookToBlixMesh"/> rather than replacing it. The typed form
    /// is the real API and is what the cook driver and the tests use; this one exists so a recipe
    /// can be invoked without the caller knowing which recipe it is, which is what makes a build
    /// rule and a coverage report possible.
    /// </remarks>
    [Recipe(BlixMesh.ShippedRecipe,
        Produces = ".blixmesh",
        Consumes = ".gltf;.glb",
        Version = MeshRecipeVersion,
        Summary = "glTF geometry to .blixmesh, with LOD chains")]
    public static CookOutcome Cook(CookRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var count = CookShipped(
            request.SourcePath,
            request.OutputPath,
            flipTextureV: request.Flag("flipV"),
            includeTangents: request.Flag("tangents"),
            splitTriBudget: request.Number("split"),
            splitMaxExtent: request.Number("splitExtent") is var e && e > 0 ? e : DefaultSplitMaxExtent,
            splitFoliage: request.Flag("splitFoliage", true));

        return CookOutcome.Written($"{count} primitive(s)");
    }

    /// <summary>
    /// Cook a glTF the way a SHIPPED asset is cooked. The only entry any driver should call.
    /// </summary>
    /// <remarks>
    /// <b>This exists because the same bug has now shipped twice.</b> Three callers each assembled
    /// the arguments to <see cref="CookToBlixMesh"/> themselves, and `simplify` defaults to null —
    /// so forgetting it is silent, produces a valid file, and costs every LOD in it. The first time,
    /// the simplifier was a lambda inside the cook driver and the uniform [Recipe] path ended up
    /// with no decimation at all. That fix taught the recipe and `cook mesh` to share one
    /// simplifier and left `cook asset` behind — which is the command the Sponza pipeline uses, so
    /// 12.8M triangles shipped at full detail and the renderer's LOD selection had nothing to
    /// choose between. Fixing it was worth 29% of the frame.
    ///
    /// The lesson is not "remember the argument". It is that a default of null on a parameter whose
    /// absence is invisible will be forgotten by somebody, and the answer is for there to be one
    /// place that cannot forget. Drivers now pick the SOURCE and the OPTIONS; they do not get to
    /// decide whether a shipped asset has LODs.
    /// </remarks>
    public static int CookShipped(
        string sourcePath, string outputPath,
        bool flipTextureV = false, bool includeTangents = false,
        int splitTriBudget = 0, bool splitFoliage = true,
        float splitMaxExtent = DefaultSplitMaxExtent,
        MaterialPatch? patch = null, Action<string>? log = null) =>
        CookToBlixMesh(
            sourcePath, outputPath,
            flipTextureV: flipTextureV,
            includeTangents: includeTangents,
            simplify: DefaultSimplifier(splitTriBudget > 0),
            splitTriBudget: splitTriBudget,
            splitFoliage: splitFoliage,
            splitMaxExtent: splitMaxExtent,
            patch: patch,
            log: log);

    /// <summary>Cooks every <c>KHR_materials_*</c> property a material declares.</summary>
    /// <remarks>
    /// Read by channel and parameter name, so a material that declares nothing yields no channel and
    /// each field keeps the value the SPEC says absence means — IOR 1.5, attenuation distance
    /// infinite, specular strength 1. Zeroing those would author a different material for every
    /// asset in existence that declines to mention them.
    /// </remarks>
    private static BlixMaterialExtensions CookExtensions(
        SharpGLTF.Schema2.Material m,
        Func<MaterialChannel?, int> imageIndex)
    {
        var d = BlixMaterialExtensions.None;
        float P(string channel, string name, float fallback)
        {
            var c = m.FindChannel(channel);
            if (!c.HasValue) return fallback;
            foreach (var prm in c.Value.Parameters)
                if (prm.Name == name) return (float)Convert.ToDouble(prm.Value);
            return fallback;
        }
        Vector3 C(string channel, Vector3 fallback)
        {
            var c = m.FindChannel(channel);
            if (!c.HasValue) return fallback;
            var v = c.Value.Color;
            return new Vector3(v.X, v.Y, v.Z);
        }
        int I(string channel) => imageIndex(m.FindChannel(channel));

        return d with
        {
            TransmissionFactor = P("Transmission", "TransmissionFactor", d.TransmissionFactor),
            TransmissionImage = I("Transmission"),
            DiffuseTransmissionFactor =
                P("DiffuseTransmissionFactor", "DiffuseTransmissionFactor", d.DiffuseTransmissionFactor),
            DiffuseTransmissionColorFactor = C("DiffuseTransmissionColor", d.DiffuseTransmissionColorFactor),
            DiffuseTransmissionImage = I("DiffuseTransmissionFactor"),
            DiffuseTransmissionColorImage = I("DiffuseTransmissionColor"),
            SheenColorFactor = C("SheenColor", d.SheenColorFactor),
            SheenRoughnessFactor = P("SheenRoughness", "RoughnessFactor", d.SheenRoughnessFactor),
            SheenColorImage = I("SheenColor"),
            SheenRoughnessImage = I("SheenRoughness"),
            ThicknessFactor = P("VolumeThickness", "ThicknessFactor", d.ThicknessFactor),
            AttenuationDistance = P("VolumeAttenuation", "AttenuationDistance", d.AttenuationDistance),
            AttenuationColor = C("VolumeAttenuation", d.AttenuationColor),
            ThicknessImage = I("VolumeThickness"),
            SpecularFactor = P("SpecularFactor", "SpecularFactor", d.SpecularFactor),
            SpecularColorFactor = C("SpecularColor", d.SpecularColorFactor),
            SpecularImage = I("SpecularFactor"),
            SpecularColorImage = I("SpecularColor"),
            IndexOfRefraction = m.IndexOfRefraction > 0f ? m.IndexOfRefraction : d.IndexOfRefraction,
            ClearcoatFactor = P("ClearCoat", "ClearCoatFactor", d.ClearcoatFactor),
            ClearcoatRoughnessFactor = P("ClearCoatRoughness", "RoughnessFactor", d.ClearcoatRoughnessFactor),
            ClearcoatNormalScale = P("ClearCoatNormal", "NormalScale", d.ClearcoatNormalScale),
            ClearcoatImage = I("ClearCoat"),
            ClearcoatRoughnessImage = I("ClearCoatRoughness"),
            ClearcoatNormalImage = I("ClearCoatNormal"),
            IridescenceFactor = P("Iridescence", "IridescenceFactor", d.IridescenceFactor),
            IridescenceIor = P("Iridescence", "IndexOfRefraction", d.IridescenceIor),
            IridescenceThicknessMinimum = P("IridescenceThickness", "Minimum", d.IridescenceThicknessMinimum),
            IridescenceThicknessMaximum = P("IridescenceThickness", "Maximum", d.IridescenceThicknessMaximum),
            IridescenceImage = I("Iridescence"),
            IridescenceThicknessImage = I("IridescenceThickness"),
            AnisotropyStrength = P("Anisotropy", "AnisotropyStrength", d.AnisotropyStrength),
            AnisotropyRotation = P("Anisotropy", "AnisotropyRotation", d.AnisotropyRotation),
            AnisotropyImage = I("Anisotropy"),
            Dispersion = m.Dispersion,
            Unlit = m.Unlit,
        };
    }
}

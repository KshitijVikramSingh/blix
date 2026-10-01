using System.Numerics;
using Blix;
using Blix.Import;
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
/// Runtime format and reader contracts live in <c>Blix.Assets</c>/<c>Blix.Cooked</c>. This recipe
/// owns content policy: node and layout selection, spatial splitting, and decimation.
/// </remarks>
public static class MeshRecipe
{
    /// <summary>An external image reached from a material channel, with its encoding role.</summary>
    /// <param name="FlipGreen">
    /// A normal map the project's patch declares DirectX-convention, to be cooked into glTF's.
    /// </param>
    /// <param name="Cooked">
    /// Where this use cooks to, relative like <paramref name="Uri"/>: the image's own name, or with a role
    /// suffix when the same image is also used another way (one cooked file per role and convention).
    /// </param>
    public readonly record struct ReferencedImage(string Uri, TextureRole Role, bool FlipGreen = false, string? Cooked = null)
    {
        /// <summary>The cooked file's relative path.</summary>
        public string CookedUri => Cooked ?? Path.ChangeExtension(Uri, ".blixtex");
    }

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
    // Maximum chunk extent for meaningful distance-based LOD selection. Selection uses
    // the distance to the nearest point of a chunk's bounds, so a chunk longer than this has parts
    // at wildly different distances answering to whichever end you stand near. Four metres is about
    // one Sponza arcade bay — close enough that a chunk is at one distance, far enough that the
    // scene does not shatter into thousands of drawables.
    public const float DefaultSplitMaxExtent = 4f;

    // Normal xyz + UV xy, the attributes a collapse is not allowed to wreck.
    private const int AttributeFloats = 5;
    // Attribute weights are the recipe's fidelity policy. meshopt scores an edge
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
    /// A collapse can leave the surface nearly fixed while shearing UVs or normals.
    /// <see cref="Attributes"/> is the
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
    // Version 9 writes glTF's scene graph (format v14): meshes once, in mesh space, placed by nodes,
    // as the complete static or skinned vertex, with MikkTSpace tangents wherever none were authored,
    // and a track for every animated node with its channels' interpolation. Version 10 unrolls
    // TRIANGLE_STRIP / TRIANGLE_FAN and non-indexed primitives into triangle lists, and refuses points and lines.
    // Version 11 (format v15) writes an image row per image as used — role, convention and glTF sampler.
    // Version 12 reads sparse index accessors, identity inverse binds when a skin has none, and joints with
    // no common root, and refuses an index past the vertices. Format compatibility is versioned separately by BlixMesh; changing recipe output with the
    // same format bumps this value.
    public const uint MeshRecipeVersion = 12;

    public static int CookToBlixMesh(
        string gltfPath, string outPath, bool flipTextureV = false,
        SimplifyFn? simplify = null, int splitTriBudget = 0, bool splitFoliage = true,
        float splitMaxExtent = DefaultSplitMaxExtent,
        MaterialPatch? patch = null, Action<string>? log = null)
    {
        ArgumentNullException.ThrowIfNull(gltfPath);
        ArgumentNullException.ThrowIfNull(outPath);

        var model = OpenSource(gltfPath);

        // The scene graph glTF has in every file: every node kept, parents first. A skinned node
        // places its mesh through its skin; every other mesh node places its mesh by its own world.
        var nodes = CookNodes(model, out var nodeOfLogical);
        var world = new Matrix4x4[nodes.Count];
        for (var i = 0; i < nodes.Count; i++)
        {
            world[i] = nodes[i].ParentIndex < 0 ? nodes[i].LocalTransform : nodes[i].LocalTransform * world[nodes[i].ParentIndex];
        }

        // Skins in the order their skinned nodes are met — the binding order a rig reads.
        var skinOrder = new List<Skin>();
        foreach (var node in model.LogicalNodes)
        {
            if (node.Mesh is not null && node.Skin is not null && !skinOrder.Contains(node.Skin)) skinOrder.Add(node.Skin);
        }

        if (skinOrder.Count > 0 && flipTextureV)
        {
            throw new InvalidDataException(
                $"'{gltfPath}' is rigged, but flipV only affects static mesh cooking; remove that option "
                + "rather than stamping a setting this artifact did not apply");
        }

        // Nodes driven by one skin share one palette and one placement, so their world matrices must
        // agree; different skins keep independent placements.
        // One skin may be placed by any number of mesh nodes, wherever they sit: glTF ignores a skinned
        // mesh node's transform, and the skeleton's placement is read from its joints at load.

        var skins = new List<BlixMeshSkin>();
        var remaps = new List<int[]>();
        foreach (var skin in skinOrder)
        {
            var (bones, oldToNew) = GltfImporter.BuildSkeletonAndOrdering(skin);
            var jointOfBone = new int[bones.Length];
            for (var old = 0; old < oldToNew.Length; old++) jointOfBone[oldToNew[old]] = nodeOfLogical[skin.Joints[old].LogicalIndex];
            skins.Add(new BlixMeshSkin(bones
                .Select((b, k) => new BlixMeshBone(b.Name, b.ParentIndex, b.InverseBindPose, jointOfBone[k]))
                .ToArray()));
            remaps.Add(oldToNew);
        }

        // Meshes, once per (glTF mesh, skin) pair: the vertices of a skinned placement address that
        // skin's bones, so the same glTF mesh placed skinned and unskinned is two meshes here.
        var meshes = new List<BlixMeshMesh>();
        var meshOf = new Dictionary<(int Mesh, int Skin), int>();
        var cookedNodes = nodes.ToArray();
        var primitiveCount = 0;
        foreach (var node in model.LogicalNodes)
        {
            if (node.Mesh is null) continue;
            var skinIndex = node.Skin is null ? -1 : skinOrder.IndexOf(node.Skin);
            var key = (node.Mesh.LogicalIndex, skinIndex);
            if (!meshOf.TryGetValue(key, out var meshIndex))
            {
                meshIndex = meshes.Count;
                meshOf[key] = meshIndex;
                var primitives = skinIndex < 0
                    ? CookStaticMesh(model, node.Mesh, node, world, nodeOfLogical, flipTextureV, simplify, splitTriBudget, splitFoliage, splitMaxExtent)
                    : CookSkinnedMesh(node.Mesh, remaps[skinIndex]);
                meshes.Add(new BlixMeshMesh(node.Mesh.Name ?? string.Empty, primitives, skinIndex));
                primitiveCount += primitives.Count;
            }

            var at = nodeOfLogical[node.LogicalIndex];
            cookedNodes[at] = cookedNodes[at] with { MeshIndex = meshIndex, SkinIndex = skinIndex };
        }

        // Clips as glTF has them: channels on NODES — joints, and any other node. Which bones a load
        // makes of them is the reader's; the cook records every animated node, skinned file or not.
        var clips = model.LogicalAnimations
            .Select(anim => CookClip(anim, nodeOfLogical))
            .Where(c => c.Tracks.Length > 0)
            .ToList();

        var (images, imageRows) = CookImages(model, gltfPath, outPath, patch);

        // A mesh is source-independent when every image-table resource is cooked. If a row still
        // names a source image, only image bytes remain required; geometry and materials do not.
        var everyImageCooked = images.All(
            i => i.Resource.EndsWith(".blixtex", StringComparison.OrdinalIgnoreCase));

        var stamp = CookStamp.Of(
            BlixMesh.ShippedRecipe, MeshRecipeVersion, gltfPath, outPath,
            StaticParameters(flipTextureV, splitTriBudget, splitFoliage, splitMaxExtent, simplify is not null, patch),
            everyImageCooked
                ? CookedFlags.None
                : CookedFlags.SourceRequired | CookedFlags.SourceRequiredForImagesOnly);

        BlixMeshWriter.Write(
            outPath,
            new BlixMeshFile(
                cookedNodes, meshes, CookMaterials(model, imageRows, patch, log), images, skins, clips,
                Ignored: GltfImporter.UnreadAttributes(model).Select(i => new BlixMeshIgnored(i.Semantic, i.Primitives)).ToArray()),
            stamp);
        return primitiveCount;
    }

    /// <summary>A static glTF mesh's primitives, in mesh space, as the complete vertex with LODs.</summary>
    /// <remarks>
    /// Splitting and LOD error are spatial, and a mesh-space metre is not a world one under a scaled
    /// node, so both use the largest world scale among the nodes placing this mesh: the split extent
    /// is divided by it and the stored error multiplied by it, so both stay in world metres.
    /// </remarks>
    private static List<BlixMeshPrimitive> CookStaticMesh(
        ModelRoot model, Mesh mesh, Node first, Matrix4x4[] world, int[] nodeOfLogical, bool flipTextureV,
        SimplifyFn? simplify, int splitTriBudget, bool splitFoliage, float splitMaxExtent)
    {
        var scale = model.LogicalNodes
            .Where(n => n.Mesh == mesh && n.Skin is null)
            .Select(n => MaxScale(world[nodeOfLogical[n.LogicalIndex]]))
            .DefaultIfEmpty(1f)
            .Max();
        var layout = VertexPosition3NormalTangentTexture2Color.Layout;
        var primitives = new List<BlixMeshPrimitive>();
        for (var i = 0; i < mesh.Primitives.Count; i++)
        {
            var prim = mesh.Primitives[i];
            var meshName = $"{mesh.Name ?? first.Name ?? "gltf_mesh"}.{i}";
            var meshData = GltfStaticImporter.BuildStaticMeshData(
                meshName, prim, Matrix4x4.Identity, Matrix4x4.Identity, flipTextureV, includeTangents: true, includeColour: true);
            // The builder leaves the tangent zero where the source authored no TANGENT; the cook fills
            // it with the MikkTSpace frame glTF asks for.
            if (prim.GetVertexAccessor("TANGENT") is null) meshData = TangentGeneration.Generate(meshData);

            // Spatial split of oversized primitives so per-prim distance LOD gets fine-grained. Seam
            // vertices are duplicated per chunk and LockBorder-locked, so chunks are crack-free across
            // LOD mismatch. Foliage (non-OPAQUE) is optionally left whole for the impostor track.
            var isFoliage = prim.Material is { Alpha: not SharpGLTF.Schema2.AlphaMode.OPAQUE };
            var doSplit = splitTriBudget > 0 && (splitFoliage || !isFoliage);
            var chunks = doSplit
                ? SplitPrimitive(meshData, layout.Stride, splitTriBudget, splitMaxExtent / scale)
                : new List<MeshData> { meshData };

            foreach (var chunk in chunks)
            {
                primitives.Add(new BlixMeshPrimitive(
                    Name: chunk.Name,
                    Layout: layout,
                    MaterialIndex: prim.Material?.LogicalIndex ?? BlixMesh.NoMaterial,
                    Bounds: chunk.Bounds,
                    VertexCount: chunk.VertexCount,
                    VertexBytes: chunk.VertexBytes,
                    IndexFormat: chunk.IndexFormat,
                    Lods: BuildLods(chunk, layout.Stride, simplify)
                        .Select(l => l with { Error = l.Error * scale })
                        .ToArray()));
            }
        }

        return primitives;
    }

    /// <summary>A skinned glTF mesh's primitives, in mesh space, as the complete skinned vertex.</summary>
    /// <remarks>
    /// The vertices come from <see cref="GltfImporter.BuildMeshData"/>, so joint remapping and influence
    /// selection are the importer's. The second set and the colour are appended before tangents are
    /// generated, because generation re-welds and renumbers vertices.
    /// </remarks>
    private static List<BlixMeshPrimitive> CookSkinnedMesh(Mesh mesh, int[] remap)
    {
        var primitives = new List<BlixMeshPrimitive>();
        for (var i = 0; i < mesh.Primitives.Count; i++)
        {
            var prim = mesh.Primitives[i];
            var skinned = GltfImporter.BuildMeshData($"{mesh.Name ?? "gltf_mesh"}.{i}", prim, remap);
            var complete = Complete(skinned, prim);
            if (TangentGeneration.HasNoTangents(complete)) complete = TangentGeneration.Generate(complete);
            primitives.Add(new BlixMeshPrimitive(
                Name: complete.Name,
                Layout: complete.Layout,
                MaterialIndex: prim.Material?.LogicalIndex ?? BlixMesh.NoMaterial,
                Bounds: complete.Bounds,
                VertexCount: complete.VertexCount,
                VertexBytes: complete.VertexBytes,
                IndexFormat: complete.IndexFormat,
                Lods: new[] { new BlixMeshLod(complete.Indices, complete.Indices32) }));
        }

        return primitives;
    }

    // The 80-byte skinned vertex widened to 92 with TEXCOORD_1 (uv0 where absent) and COLOR_0 (white
    // where absent), glTF's defaults, so a skinned mesh carries the whole vertex a static one does.
    private static MeshData Complete(MeshData skinned, MeshPrimitive prim)
    {
        var uv1 = prim.GetVertexAccessor("TEXCOORD_1")?.AsVector2Array();
        var colours = prim.GetVertexAccessor("COLOR_0")?.AsColorArray();
        var from = VertexPosition3NormalTextureSkin4Tangent.Layout.Stride;
        var to = VertexPosition3NormalTextureSkin4Tangent2Color.Layout.Stride;
        var bytes = new byte[skinned.VertexCount * to];
        for (var v = 0; v < skinned.VertexCount; v++)
        {
            System.Buffer.BlockCopy(skinned.VertexBytes, v * from, bytes, v * to, from);
            var u = uv1 is null
                ? new Vector2(BitConverter.ToSingle(skinned.VertexBytes, (v * from) + 24), BitConverter.ToSingle(skinned.VertexBytes, (v * from) + 28))
                : uv1[v];
            BitConverter.TryWriteBytes(bytes.AsSpan((v * to) + 80, 4), u.X);
            BitConverter.TryWriteBytes(bytes.AsSpan((v * to) + 84, 4), u.Y);
            var colour = colours is null
                ? VertexPosition3NormalTextureColor.White
                : VertexPosition3NormalTextureColor.Pack(colours[v].X, colours[v].Y, colours[v].Z, colours[v].W);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan((v * to) + 88, 4), colour);
        }

        return skinned with { VertexBytes = bytes, Layout = VertexPosition3NormalTextureSkin4Tangent2Color.Layout };
    }

    private static float MaxScale(in Matrix4x4 m) => MathF.Max(
        new Vector3(m.M11, m.M12, m.M13).Length(),
        MathF.Max(new Vector3(m.M21, m.M22, m.M23).Length(), new Vector3(m.M31, m.M32, m.M33).Length()));

    /// <summary>
    /// The authored node hierarchy, in an order where every parent precedes its children.
    /// </summary>
    /// <remarks>
    /// glTF does not promise parents come first, and a consumer composing world matrices in one
    /// forward pass needs them to — the same invariant <c>Skeleton</c> enforces on bones. The reader
    /// refuses a file that violates it, naming the node. An unnamed node is written as
    /// <c>node_{logical index}</c>, the one spelling every reader then sees.
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

    /// <summary>One animation, as keyframes.</summary>
    /// <remarks>
    /// Store source keyframes rather than serializing runtime curve objects. The
    /// glTF importer builds exactly two curve types — <c>KeyframeVector3Curve</c> and
    /// <c>KeyframeQuaternionCurve</c> — each from a plain array of (time, value). Writing those
    /// arrays back is lossless; writing a serialised "curve" would be inventing a representation
    /// for something that is already one.
    /// </remarks>
    // One track per animated node, each channel with its interpolation. Morph-weight channels are
    // not read (the unread-attribute table says so), and a channel with no target node is skipped.
    private static BlixMeshClip CookClip(SharpGLTF.Schema2.Animation anim, int[] nodeOfLogical)
    {
        var tracks = new SortedDictionary<int, BlixMeshTrack>();
        foreach (var channel in anim.Channels)
        {
            if (channel.TargetNode is null) continue;
            var node = nodeOfLogical[channel.TargetNode.LogicalIndex];
            var track = tracks.TryGetValue(node, out var t) ? t
                : new BlixMeshTrack(node, Array.Empty<BlixMeshVectorKey>(), Array.Empty<BlixMeshQuaternionKey>(), Array.Empty<BlixMeshVectorKey>());
            switch (channel.TargetNodePath)
            {
                case SharpGLTF.Schema2.PropertyPath.translation:
                {
                    var (keys, mode) = GltfImporter.SampleKeys(channel.GetTranslationSampler());
                    track = track with { Translation = VectorKeys(keys), TranslationInterpolation = Mode(mode) };
                    break;
                }
                case SharpGLTF.Schema2.PropertyPath.rotation:
                {
                    var (keys, mode) = GltfImporter.SampleKeys(channel.GetRotationSampler());
                    track = track with
                    {
                        Rotation = keys.Select(k => new BlixMeshQuaternionKey((float)k.Time, k.Value, k.InTangent, k.OutTangent)).ToArray(),
                        RotationInterpolation = Mode(mode),
                    };
                    break;
                }
                case SharpGLTF.Schema2.PropertyPath.scale:
                {
                    var (keys, mode) = GltfImporter.SampleKeys(channel.GetScaleSampler());
                    track = track with { Scale = VectorKeys(keys), ScaleInterpolation = Mode(mode) };
                    break;
                }
                default:
                    continue;
            }

            tracks[node] = track;
        }

        return new BlixMeshClip(anim.Name ?? "anim", tracks.Values.ToArray());

        static BlixMeshVectorKey[] VectorKeys(Keyframe<Vector3>[] keys) =>
            keys.Select(k => new BlixMeshVectorKey((float)k.Time, k.Value, k.InTangent, k.OutTangent)).ToArray();

        static BlixMeshInterpolation Mode(Interpolation mode) => mode switch
        {
            Interpolation.Step => BlixMeshInterpolation.Step,
            Interpolation.CubicSpline => BlixMeshInterpolation.CubicSpline,
            _ => BlixMeshInterpolation.Linear,
        };
    }

    /// <summary>
    /// The external images this asset's materials actually reference, with material-channel roles.
    /// </summary>
    /// <remarks>
    /// Follows material references instead of sweeping the containing directory, so unrelated and
    /// unused images are not included in an asset cook.
    /// <para>
    /// Embedded images are not listed: they have no URI to cook from, and the mesh cook extracts
    /// and cooks them itself as it builds the image table.
    /// </para>
    /// </remarks>
    /// <param name="patch">The asset's material rules from the project's cook configuration, which may declare normal-map conventions.</param>
    public static IReadOnlyList<ReferencedImage> ReferencedImages(string gltfPath, MaterialPatch? patch = null)
    {
        ArgumentNullException.ThrowIfNull(gltfPath);

        var model = OpenSource(gltfPath);
        var (variants, _) = ImageVariants(model, patch);
        var gltfDir = Path.GetDirectoryName(Path.GetFullPath(gltfPath)) ?? ".";
        var references = new List<ReferencedImage>();
        foreach (var variant in variants)
        {
            var image = model.LogicalImages[variant.Image];
            if (image.Content.SourcePath is not { } uri || uri.StartsWith("data:", StringComparison.Ordinal)) continue;
            var relative = RelativeImagePath(gltfDir, uri);
            // Stated only when it differs from the image's own name: the first variant keeps that.
            references.Add(new ReferencedImage(relative, variant.Role, variant.FlipGreen,
                variant.Suffix.Length == 0 ? null : CookedName(relative, variant.Suffix)));
        }

        return references;
    }

    /// <summary>The unique external image URIs, when channel roles are not needed.</summary>
    public static IReadOnlyList<string> ReferencedImageUris(string gltfPath) =>
        ReferencedImages(gltfPath)
            .Select(reference => reference.Uri)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    /// <summary>
    /// Every image the source's materials reference, and where each one's pixels ended up.
    /// </summary>
    /// <remarks>
    /// The table records a location the recipe knows to be true, rather than a rule the loader applies later — see the
    /// note in <c>BlixMesh</c> on why deriving a location by swapping a URI's extension was a
    /// grouping policy in disguise.
    /// <para>
    /// Embedded images have no source path, so the cook extracts each one, cooks it beside the
    /// mesh, and writes an ordinary resource row.
    /// </para>
    /// <para>
    /// Only images the MATERIALS reach are recorded. A glTF may carry images no channel samples, and
    /// a directory may carry many more — main Sponza ships 137 texture files of which its own glTF
    /// names 72 — so following references rather than sweeping is worth a quarter of the bytes
    /// before any format decision is made.
    /// </para>
    /// </remarks>
    private static (IReadOnlyList<BlixMeshImage> Images, Dictionary<(int Material, string Channel), int> Rows) CookImages(
        ModelRoot model, string gltfPath, string outPath, MaterialPatch? patch)
    {
        var images = new List<BlixMeshImage>();
        var rows = new Dictionary<(int, string), int>();
        var (variants, ofChannel) = ImageVariants(model, patch);
        var sourceDir = Path.GetDirectoryName(Path.GetFullPath(gltfPath)) ?? ".";
        var outDir = Path.GetDirectoryName(Path.GetFullPath(outPath)) ?? ".";
        var extractDir = Path.Combine(
            outDir, Path.GetFileNameWithoutExtension(outPath) + BlixMesh.ExtractedImageFolder);

        // One row per (variant, sampler): an image as used. Two samplers on one variant share its file.
        var resources = new Dictionary<int, (string Name, ulong Hash, string Resource)>();
        var rowOf = new Dictionary<(int Variant, BlixMeshSampler Sampler), int>();
        foreach (var material in model.LogicalMaterials)
        {
            foreach (var channelName in ImageChannels)
            {
                if (!ofChannel.TryGetValue((material.LogicalIndex, channelName), out var v)) continue;
                var texture = material.FindChannel(channelName)!.Value.Texture!;
                var sampler = SamplerOf(texture.Sampler);
                if (!rowOf.TryGetValue((v, sampler), out var row))
                {
                    if (!resources.TryGetValue(v, out var resource))
                    {
                        var variant = variants[v];
                        var image = model.LogicalImages[variant.Image];
                        var bytes = image.Content.Content;
                        var name = (image.Name
                            ?? (image.Content.SourcePath is { } uri ? Path.GetFileNameWithoutExtension(uri) : null)
                            ?? $"image_{image.LogicalIndex}") + variant.Suffix;
                        resource = (name, ContentHash(bytes.Span),
                            Shippable(Resource(image, bytes, name, variant.Role, variant.FlipGreen, variant.Suffix), gltfPath));
                        resources[v] = resource;
                    }

                    row = images.Count;
                    rowOf[(v, sampler)] = row;
                    images.Add(new BlixMeshImage(resource.Name, resource.Hash, resource.Resource, sampler));
                }

                rows[(material.LogicalIndex, channelName)] = row;
            }
        }

        return (images, rows);

        string Resource(SharpGLTF.Schema2.Image image, ReadOnlyMemory<byte> bytes, string name, TextureRole role, bool flipGreen, string suffix)
        {
            // External: the file is already on disk beside the glTF. Prefer the cooked artifact
            // when one is there — the asset driver cooks textures BEFORE the mesh precisely so that
            // this check sees them — and otherwise name the source image, which is still a location
            // the loader can open without the glTF.
            if (image.Content.SourcePath is { } uri && !uri.StartsWith("data:", StringComparison.Ordinal))
            {
                var relative = RelativeImagePath(sourceDir, uri);
                var cooked = CookedName(relative, suffix);

                // Check the output directory because Resource is relative to the cooked mesh. For
                // an out-of-place cook, source and destination trees are intentionally different.
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
                TextureRecipe.CookOne(raw, cookedPath, out _, out _, role, flipGreen);
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

    private static TextureRole RoleForChannel(string channelName) => channelName switch
    {
        "BaseColor" => TextureRole.BaseColor,
        "Normal" => TextureRole.Normal,
        "MetallicRoughness" => TextureRole.MetallicRoughness,
        "Emissive" => TextureRole.Emissive,
        // Occlusion is single-channel linear data. It is not MetallicRoughness, whose loader
        // rewrites grayscale input into the engine's ORM layout.
        _ => TextureRole.Linear,
    };

    /// <summary>
    /// The one pair of roles an image may hold at once: occlusion (R) packed with metallic-roughness
    /// (G, B), glTF's own ORM packing. Both are linear and encode alike, and the metallic-roughness
    /// role passes an RGB image through untouched, so the occlusion channel survives.
    /// </summary>
    private static TextureRole? Packed(TextureRole a, TextureRole b) =>
        (a, b) is (TextureRole.MetallicRoughness, TextureRole.Linear) or (TextureRole.Linear, TextureRole.MetallicRoughness)
            ? TextureRole.MetallicRoughness
            : null;

    /// <summary>One source image as the cook writes it: one role, one normal convention, one cooked name.</summary>
    /// <param name="Suffix">Empty for the image's first variant; <c>.&lt;role&gt;</c> (and <c>.dx</c>) for the others.</param>
    private readonly record struct ImageVariant(int Image, TextureRole Role, bool FlipGreen, string Suffix);

    /// <summary>
    /// Every (image, role, convention) the materials use, and the variant each material channel reads.
    /// </summary>
    /// <remarks>
    /// glTF lets one image be read by several textures in several ways: as colour by one material and as
    /// data by another (TextureEncodingTest does exactly this), or as a normal map in two conventions. A
    /// cooked image has one encoding, so each use is its own variant, cooked to its own file — the first
    /// under the image's own name, the rest with a role suffix. The one exception is glTF's ORM packing:
    /// occlusion (R) beside metallic-roughness (G, B) is one variant, since the metallic-roughness
    /// encoding passes RGB through. The single rule ReferencedImages, CookImages and the materials share.
    /// </remarks>
    private static (List<ImageVariant> Variants, Dictionary<(int Material, string Channel), int> OfChannel) ImageVariants(
        ModelRoot model, MaterialPatch? patch)
    {
        var names = model.LogicalMaterials.Select((m, i) => m.Name ?? $"material_{i}").ToArray();
        var directX = patch?.DirectXNormalMaterials(names) ?? new HashSet<int>();
        var variants = new List<ImageVariant>();
        var ofChannel = new Dictionary<(int, string), int>();
        foreach (var material in model.LogicalMaterials)
        {
            foreach (var channelName in ImageChannels)
            {
                var image = material.FindChannel(channelName)?.Texture?.PrimaryImage;
                if (image is null) continue;

                var role = RoleForChannel(channelName);
                var flip = channelName == "Normal" && directX.Contains(material.LogicalIndex);
                var found = -1;
                for (var v = 0; v < variants.Count && found < 0; v++)
                {
                    var x = variants[v];
                    if (x.Image != image.LogicalIndex || x.FlipGreen != flip) continue;
                    if (x.Role == role) found = v;
                    else if (Packed(x.Role, role) is { } packed)
                    {
                        variants[v] = x with { Role = packed };
                        found = v;
                    }
                }

                if (found < 0)
                {
                    var first = variants.All(x => x.Image != image.LogicalIndex);
                    found = variants.Count;
                    variants.Add(new ImageVariant(image.LogicalIndex, role, flip,
                        first ? string.Empty : $".{role.ToString().ToLowerInvariant()}{(flip ? ".dx" : string.Empty)}"));
                }

                ofChannel[(material.LogicalIndex, channelName)] = found;
            }
        }

        return (variants, ofChannel);
    }

    /// <summary>The extensions this reader reads. A file that REQUIRES any other is refused.</summary>
    /// <remarks>
    /// glTF: a client that does not support an extension in extensionsRequired must not load the asset,
    /// because the file cannot be drawn correctly without it. One merely USED may be ignored, and is.
    /// KHR_mesh_quantization is accessor encoding the parser decodes; the rest are material terms the
    /// cook carries.
    /// </remarks>
    public static readonly IReadOnlySet<string> ReadExtensions = new HashSet<string>(StringComparer.Ordinal)
    {
        "KHR_mesh_quantization",
        "KHR_materials_transmission", "KHR_materials_diffuse_transmission", "KHR_materials_volume",
        "KHR_materials_ior", "KHR_materials_specular", "KHR_materials_sheen", "KHR_materials_clearcoat",
        "KHR_materials_iridescence", "KHR_materials_anisotropy", "KHR_materials_dispersion",
        "KHR_materials_unlit", "KHR_materials_emissive_strength",
    };

    // Opens a source and refuses it, by name, when it requires an extension the reader does not read.
    private static ModelRoot OpenSource(string gltfPath)
    {
        // SharpGLTF's strict validation is skipped: it rejects valid glTF (joints with no common root,
        // which the Asset Generator's positive Animation_Skin_06 exists to exercise and the spec allows).
        // What the cook needs checked it checks itself, by name: POSITION, index range, required extensions.
        var model = ModelRoot.Load(gltfPath, new SharpGLTF.Schema2.ReadSettings { Validation = SharpGLTF.Validation.ValidationMode.Skip });
        var unread = model.ExtensionsRequired.Where(e => !ReadExtensions.Contains(e)).ToArray();
        if (unread.Length > 0)
        {
            throw new InvalidDataException(
                $"'{gltfPath}' requires {string.Join(", ", unread)}, which Blix does not read; glTF says a file whose "
                + "required extensions a reader does not support must not be loaded.");
        }

        return model;
    }

    // A glTF sampler as glTF's codes: what the file says (BlixMeshSampler); the spec's defaults when absent.
    private static BlixMeshSampler SamplerOf(SharpGLTF.Schema2.TextureSampler? sampler) => sampler is null
        ? default
        : new BlixMeshSampler((int)sampler.WrapS, (int)sampler.WrapT, (int)sampler.MinFilter, (int)sampler.MagFilter);

    // A variant's cooked name: the image's own, with the variant's suffix before .blixtex.
    private static string CookedName(string relative, string suffix) =>
        Path.ChangeExtension(relative, null) + suffix + ".blixtex";

    /// <summary>
    /// An image reference as a path RELATIVE to the asset, whatever form the parser handed back.
    /// </summary>
    /// <remarks>
    /// SharpGLTF may return an absolute resolved path while cooked resources require relative,
    /// portable locations.
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
    /// violation is refused rather than logged.
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
    /// Materials remain in source order and include unused entries because a primitive's
    /// <c>MaterialIndex</c> is the source logical-material index. Compacting the table would change
    /// that index's meaning.
    /// <para>
    /// This reads the same channels <c>GltfShared.ExtractMaterial</c> does and must keep reading
    /// them: a property the cook drops is one the loader stops seeing the moment a mesh is cooked,
    /// which shows up as an asset that renders differently on machines that have cooked it.
    /// </para>
    /// </remarks>
    private static IReadOnlyList<BlixMeshMaterial> CookMaterials(
        ModelRoot model, IReadOnlyDictionary<(int Material, string Channel), int> imageRows,
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
                BaseColorTexCoord: TexCoord(baseColor),
                NormalTexCoord: TexCoord(normal),
                NormalScale: Parameter(normal, "NormalScale", 1f),
                MetallicRoughnessTexCoord: TexCoord(mr),
                OcclusionTexCoord: TexCoord(occlusion),
                EmissiveTexCoord: TexCoord(emissive),
                // Defaults are the glTF spec's for an absent channel, not zero: a material with no
                // MetallicRoughness channel is metallic 1 / rough 1, and writing 0 would quietly
                // turn every such surface into a mirror.
                MetallicFactor: Parameter(mr, "MetallicFactor", 1f),
                RoughnessFactor: Parameter(mr, "RoughnessFactor", 1f),
                // SharpGLTF's name, not glTF's "strength": a lookup by the spec's word never matched.
                OcclusionStrength: Parameter(occlusion, "OcclusionStrength", 1f),
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
        var final = patch is null ? cooked : patch.Apply(cooked, log);
        ReportDefaultTransmissionColour(final, log);
        return final;

        // Rows of this cooked file's image table, one per channel as used, not source image indices.
        int ImageIndex(MaterialChannel? channel) =>
            channel is { } c && c.Texture is not null && imageRows.TryGetValue((c.LogicalParent.LogicalIndex, c.Key), out var row)
                ? row
                : BlixMesh.NoImage;

        static int TexCoord(MaterialChannel? channel) => channel.HasValue ? channel.Value.TextureCoordinate : 0;

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

    /// <summary>
    /// Reports a material that scatters light through itself without saying what colour it turns.
    /// </summary>
    /// <remarks>
    /// <para>
    /// glTF's default for <c>diffuseTransmissionColorFactor</c> is WHITE, and a renderer that obeys
    /// the spec gives such a material a white glow from behind. That is correct and it is usually
    /// not what was meant: the term is reached for on leaves and cloth, which tint what passes
    /// through them, and a red curtain glowing white looks like a bug in the renderer.
    /// </para>
    /// <para>
    /// So this reports rather than judges, in the manner of <c>blix inspect</c>. White transmission
    /// is a legal material and refusing it would be the cook overriding an author. But the cook is
    /// where an asset's omissions get answered — it is the one place that sees the material table
    /// after patching and before anything renders — so it is the right place to say out loud that
    /// a default is in force, rather than leaving it to be discovered in a picture.
    /// </para>
    /// <para>
    /// Deliberately silent when a colour texture is present: the factor then multiplies the texture
    /// and white is the correct, intended neutral.
    /// </para>
    /// </remarks>
    private static void ReportDefaultTransmissionColour(
        IReadOnlyList<BlixMeshMaterial> materials, Action<string>? log)
    {
        if (log is null) return;
        foreach (var m in materials)
        {
            var x = m.Ext;
            if (x.DiffuseTransmissionFactor <= 0f) continue;
            if (x.DiffuseTransmissionColorImage != BlixMesh.NoImage) continue;
            if (x.DiffuseTransmissionColorFactor != Vector3.One) continue;
            log($"  note: {m.Name} scatters light through itself (diffuseTransmission " +
                $"{x.DiffuseTransmissionFactor:0.##}) but authors no diffuseTransmissionColor, so " +
                "the glTF default applies and it will glow WHITE from behind. Set " +
                "diffuseTransmissionColor in the patch if it should carry the surface's own tint.");
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
    // A triangle budget splits
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
    /// The shared shipped path always supplies this simplifier so recipe-host and driver invocations
    /// both produce LOD chains.
    /// </para>
    /// <para>
    /// Use Prune generally and add LockBorder only for spatially split chunks. LockBorder pins every mesh-boundary
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
        // Fail here, with a sentence, rather than four frames inside a P/Invoke with
        // DllNotFoundException and no filename. Cooking runs as a build step, so on a platform
        // where the native was never built this is what a person sees instead of a build that
        // stopped for no stated reason. There is deliberately no silent fallback: a mesh cooked
        // without a LOD chain is a quieter asset bug than a build failure, and the engine's whole
        // position is that the loud one is cheaper.
        if (!MeshoptNative.Available)
        {
            throw new InvalidOperationException(
                $"LOD generation needs the meshoptimizer native, and {MeshoptNative.FileName} is " +
                $"not beside the cook. It is built by the BuildMeshopt target in " +
                $"Blix.Recipes.csproj, which is conditioned on macOS — so on another platform it " +
                $"has to be built there first. Cross-publishing from a Mac is unaffected: the " +
                $"cook runs on the host, whatever the target RID.");
        }

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
    /// rule and a coverage report possible. <c>config=&lt;file&gt;</c> names the project's
    /// <see cref="CookConfig"/>, whose entry for the source decides everything else; without one,
    /// <c>flipV</c>, <c>split</c>, <c>splitExtent</c> and <c>splitFoliage</c> decide an ad-hoc cook.
    /// </remarks>
    [Recipe(BlixMesh.ShippedRecipe,
        Produces = ".blixmesh",
        Consumes = ".gltf;.glb",
        SidecarFolder = BlixMesh.ExtractedImageFolder,
        Version = MeshRecipeVersion,
        Summary = "glTF geometry to .blixmesh, with LOD chains")]
    public static CookOutcome Cook(CookRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Text("config") is { } configPath)
        {
            // The project's configuration decides; an option beside it would be a second writer.
            if (request.Options!.Keys.FirstOrDefault(k => k != "config") is { } other)
            {
                throw new InvalidDataException(
                    $"{request.SourcePath}: option '{other}' beside config={configPath}; set it in the configuration's entry.");
            }

            var entry = CookConfig.Load(configPath).For(request.SourcePath);
            var cooked = entry is null
                ? CookShipped(request.SourcePath, request.OutputPath)
                : CookShipped(request.SourcePath, request.OutputPath, entry.FlipTextureV,
                    splitTriBudget: entry.SplitTriBudget, splitFoliage: entry.SplitFoliage,
                    splitMaxExtent: entry.SplitMaxExtent, patch: entry.Materials);
            return CookOutcome.Written($"{cooked} primitive(s)");
        }

        var count = CookShipped(
            request.SourcePath,
            request.OutputPath,
            flipTextureV: request.Flag("flipV"),
            splitTriBudget: request.Number("split"),
            splitMaxExtent: request.Real("splitExtent") is var e && e > 0 ? e : DefaultSplitMaxExtent,
            splitFoliage: request.Flag("splitFoliage", true));

        return CookOutcome.Written($"{count} primitive(s)");
    }

    /// <summary>
    /// Cook a glTF the way a SHIPPED asset is cooked. The only entry any driver should call.
    /// </summary>
    /// <remarks>
    /// Centralises the shipped policy that all drivers must share: LOD simplification is always
    /// enabled, while callers choose source, destination, layout, splitting, and material rules.
    /// Use <see cref="CookToBlixMesh"/> directly only when an LOD0-only artifact is intentional.
    /// </remarks>
    public static int CookShipped(
        string sourcePath, string outputPath,
        bool flipTextureV = false,
        int splitTriBudget = 0, bool splitFoliage = true,
        float splitMaxExtent = DefaultSplitMaxExtent,
        MaterialPatch? patch = null, Action<string>? log = null) =>
        CookToBlixMesh(
            sourcePath, outputPath,
            flipTextureV: flipTextureV,
            simplify: DefaultSimplifier(splitTriBudget > 0),
            splitTriBudget: splitTriBudget,
            splitFoliage: splitFoliage,
            splitMaxExtent: splitMaxExtent,
            patch: patch,
            log: log);

    /// <summary>Whether a shipped mesh artifact matches today's recipe, source and options.</summary>
    public static bool IsShippedCurrent(
        string sourcePath, string outputPath,
        bool flipTextureV = false,
        int splitTriBudget = 0, bool splitFoliage = true,
        float splitMaxExtent = DefaultSplitMaxExtent,
        MaterialPatch? patch = null)
    {
        var header = CookedFile.TryReadHeader(outputPath);
        if (header is not { Magic: BlixMesh.Magic, FormatVersion: BlixMesh.Version15 }) return false;
        var stamp = header.Value.Stamp;
        if (!stamp.MatchesProducerAndSource(BlixMesh.ShippedRecipe, MeshRecipeVersion, sourcePath))
            return false;

        // One vocabulary for every file: a rigged one is a scene graph like any other.
        return stamp.Parameters == StaticParameters(
            flipTextureV, splitTriBudget, splitFoliage, splitMaxExtent,
            simplify: true, patch: patch);
    }

    private static string StaticParameters(
        bool flipTextureV, int splitTriBudget, bool splitFoliage,
        float splitMaxExtent, bool simplify, MaterialPatch? patch) =>
        $"flipV={(flipTextureV ? 1 : 0)} "
        + $"split={splitTriBudget}@{splitMaxExtent.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)}m "
        + $"splitFoliage={(splitFoliage ? 1 : 0)} simplify={(simplify ? "yes" : "none")}"
        // Recorded so inspection can attribute authored material changes to their project policy.
        + (patch is null ? "" : $" {patch.StampFragment}");

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

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

    // Reduced index list + the world-space geometric error it introduced
    // (max deviation from the original surface, in mesh units). Error drives
    // screen-space-error LOD selection: project it to pixels at the view
    // distance and switch when it's below a pixel threshold.
    public readonly record struct SimplifyResult(uint[] Indices, float WorldError);

    // Simplify callback: (positions xyz tight float[3*vtx], indices, vertexCount,
    // targetRatio) -> reduced index list + its world error, sharing the same
    // vertices. The cook tool supplies a meshoptimizer-backed implementation;
    // when null, the file is written LOD0-only (Blix has no simplifier of its own).
    public delegate SimplifyResult SimplifyFn(float[] positions, uint[] indices, int vertexCount, float targetRatio);

    /// <summary>
    /// The mesh cook's own version, bumped whenever this method would produce different bytes from
    /// the same source and settings. Recorded in every file it writes, so a re-cook can be told
    /// from a rewrite.
    /// </summary>
    public const uint MeshRecipeVersion = 1;

    public static int CookToBlixMesh(
        string gltfPath, string outPath, bool flipTextureV = false, bool includeTangents = false,
        SimplifyFn? simplify = null, int splitTriBudget = 0, bool splitFoliage = true)
    {
        ArgumentNullException.ThrowIfNull(gltfPath);
        ArgumentNullException.ThrowIfNull(outPath);

        var layout = includeTangents
            ? VertexPosition3NormalTangentTexture.Layout
            : VertexPosition3NormalTexture.Layout;
        var model = ModelRoot.Load(gltfPath);
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
                    ? SplitPrimitive(meshData, layout.Stride, splitTriBudget)
                    : new List<MeshData> { meshData };

                foreach (var chunk in chunks)
                {
                    primitives.Add(new BlixMeshPrimitive(
                        Name: chunk.Name,
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
            $"split={splitTriBudget} splitFoliage={(splitFoliage ? 1 : 0)} " +
            $"simplify={(simplify is null ? "none" : "yes")}";

        // SourceRequired, and it is not a formality: this cook replaces geometry only. Every
        // material factor, texture reference and alpha mode is still parsed out of the sibling
        // glTF on every load, so the source is a permanent runtime dependency and the flag says so
        // where a tool can see it. Stage K-F is finished when this stops being set.
        var stamp = CookStamp.Of(
            BlixMesh.ShippedRecipe, MeshRecipeVersion, gltfPath, outPath, parameters, CookedFlags.SourceRequired);

        BlixMeshWriter.Write(outPath, new BlixMeshFile(layout, primitives), stamp);
        return primitives.Count;
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
        for (var v = 0; v < meshData.VertexCount; v++)
        {
            var o = v * stride;
            positions[v * 3 + 0] = BitConverter.ToSingle(meshData.VertexBytes, o);
            positions[v * 3 + 1] = BitConverter.ToSingle(meshData.VertexBytes, o + 4);
            positions[v * 3 + 2] = BitConverter.ToSingle(meshData.VertexBytes, o + 8);
        }

        var prevCount = baseIndices.Length;
        foreach (var ratio in LodRatios)
        {
            var result = simplify(positions, baseIndices, meshData.VertexCount, ratio);
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
    private static List<MeshData> SplitPrimitive(MeshData mesh, int stride, int triBudget)
    {
        var baseIdx = mesh.Indices32 ?? Array.ConvertAll(mesh.Indices, idx => (uint)idx);
        var triCount = baseIdx.Length / 3;
        if (triCount <= triBudget) return new List<MeshData> { mesh };

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
        SplitTriangles(allTris, centroids, triBudget, leaves);

        var chunks = new List<MeshData>(leaves.Count);
        var chunkIdx = 0;
        foreach (var leaf in leaves)
            chunks.Add(BuildChunk($"{mesh.Name}#{chunkIdx++}", mesh, baseIdx, leaf, stride));
        return chunks;
    }

    private static void SplitTriangles(int[] tris, Vector3[] centroids, int budget, List<int[]> leaves)
    {
        if (tris.Length <= budget) { leaves.Add(tris); return; }
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        foreach (var t in tris) { min = Vector3.Min(min, centroids[t]); max = Vector3.Max(max, centroids[t]); }
        var ext = max - min;
        var axis = ext.X >= ext.Y && ext.X >= ext.Z ? 0 : (ext.Y >= ext.Z ? 1 : 2);
        Array.Sort(tris, (p, q) => Axis(centroids[p], axis).CompareTo(Axis(centroids[q], axis)));
        var mid = tris.Length / 2;
        // Degenerate (centroids coincide along the split axis) — emit whole.
        if (mid == 0 || mid == tris.Length) { leaves.Add(tris); return; }
        SplitTriangles(tris[..mid], centroids, budget, leaves);
        SplitTriangles(tris[mid..], centroids, budget, leaves);
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

        return (positions, indices, vertexCount, ratio) =>
        {
            var reduced = MeshoptNative.Simplify(
                indices, positions, vertexCount, 3, ratio, targetError: 1.0f, options, out var relError);

            // meshopt's error is relative to the mesh extent; scale it to world units so the
            // runtime can project it to screen pixels.
            var scale = MeshoptNative.SimplifyScale(positions, vertexCount, 3);
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
        var split = request.Number("split");
        var count = CookToBlixMesh(
            request.SourcePath,
            request.OutputPath,
            flipTextureV: request.Flag("flipV"),
            includeTangents: request.Flag("tangents"),
            simplify: DefaultSimplifier(split > 0),
            splitTriBudget: split,
            splitFoliage: request.Flag("splitFoliage", true));

        return CookOutcome.Written($"{count} primitive(s)");
    }
}

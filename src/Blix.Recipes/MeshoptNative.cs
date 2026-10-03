using System.Numerics;
using System.Runtime.InteropServices;

namespace Blix.Recipes;

// P/Invoke into vendored meshoptimizer (third_party/meshoptimizer, built next to
// the cook by the BuildMeshopt MSBuild target; NativeLibraries names the file).
// Cook-time only — used to generate mesh LOD index chains. The runtime never
// links meshopt; it just loads the cooked LOD buffers.
public static unsafe class MeshoptNative
{
    private const string Lib = "meshoptimizer";

    // Load the library explicitly from the app's own directory (the BuildMeshopt
    // target drops it next to the cook assembly). Default native probing doesn't
    // reliably find an app-local library by the bare name on macOS, so resolve it
    // by absolute path. NativeLibraries knows what the file is called here.
    static MeshoptNative() => NativeLibraries.Ensure();

    /// <summary>Is the native simplifier present?</summary>
    /// <remarks>
    /// <para>
    /// <b>Bc7Native has had this since it was written; this did not, and the difference is a
    /// build that fails without saying why.</b> BC7 probes and falls back to a managed encoder, so
    /// a missing library costs cook time. LOD generation has no managed fallback, so a missing
    /// library threw <c>DllNotFoundException</c> from four frames inside a P/Invoke — during a
    /// build, because cooking runs as a build step.
    /// </para>
    /// <para>
    /// The library is built by an MSBuild target conditioned on macOS, so today this is false
    /// everywhere else. That is the honest state of the port and not a thing to paper over: see
    /// <c>MeshRecipe.DefaultSimplifier</c>, which turns it into a sentence naming the file.
    /// </para>
    /// </remarks>
    public static bool Available => NativeLibraries.Present(Lib);

    /// <summary>The filename this platform's loader will look for.</summary>
    public static string FileName => NativeLibraries.FileName(Lib);

    // meshopt_Simplify* option flags (meshoptimizer.h).
    [Flags]
    public enum Options : uint
    {
        None = 0,
        LockBorder = 1 << 0,   // keep mesh-boundary vertices fixed (no cracks between primitives)
        Sparse = 1 << 1,
        ErrorAbsolute = 1 << 2,
        Prune = 1 << 3,
    }

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern nuint meshopt_simplify(
        uint* destination, uint* indices, nuint indexCount,
        float* vertexPositions, nuint vertexCount, nuint vertexPositionsStride,
        nuint targetIndexCount, float targetError, uint options, float* resultError);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern nuint meshopt_simplifyWithAttributes(
        uint* destination, uint* indices, nuint indexCount,
        float* vertexPositions, nuint vertexCount, nuint vertexPositionsStride,
        float* vertexAttributes, nuint vertexAttributesStride,
        float* attributeWeights, nuint attributeCount,
        byte* vertexLock,
        nuint targetIndexCount, float targetError, uint options, float* resultError);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern float meshopt_simplifyScale(
        float* vertexPositions, nuint vertexCount, nuint vertexPositionsStride);

    // Simplify to ~targetRatio of the triangle count, sharing the original
    // vertex buffer (returns a reduced index list indexing the same vertices).
    // positions: tightly-packed-by-stride xyz; positionStrideFloats is the
    // float count between consecutive vertices' positions. targetError is the
    // max allowed deviation (relative to mesh extent unless ErrorAbsolute);
    // pass it large so the ratio drives the result. resultError reports the
    // achieved error.
    public static uint[] Simplify(
        uint[] indices, float[] positions, int vertexCount, int positionStrideFloats,
        float targetRatio, float targetError, Options options, out float resultError)
    {
        // meshopt writes at most index_count indices into destination.
        var dest = new uint[indices.Length];
        var targetIndexCount = (nuint)(((int)(indices.Length * targetRatio)) / 3 * 3);
        float err;
        nuint count;
        fixed (uint* d = dest)
        fixed (uint* idx = indices)
        fixed (float* pos = positions)
        {
            count = meshopt_simplify(
                d, idx, (nuint)indices.Length,
                pos, (nuint)vertexCount, (nuint)(positionStrideFloats * sizeof(float)),
                targetIndexCount, targetError, (uint)options, &err);
        }
        resultError = err;
        Array.Resize(ref dest, (int)count);
        return dest;
    }

    // Attribute-aware simplification judges appearance as well as shape. meshopt_simplify sees only
    // positions, so a collapse that barely moves the surface is free to shear the UVs across it —
    // and a wall or a pillar whose texture slides as it changes level is a far more obvious artifact
    // than the silhouette error the metric was actually bounding. Attributes are interleaved,
    // `attributeCount` floats per vertex, each with its own weight.
    //
    // The returned error is then a COMBINED position-and-attribute deviation rather than a purely
    // geometric one. That is the point — it is what lets selection see texture distortion at all —
    // and is not interchangeable with a geometry-only simplification error.
    public static uint[] SimplifyWithAttributes(
        uint[] indices, float[] positions, int vertexCount, int positionStrideFloats,
        float[] attributes, int attributeStrideFloats, float[] attributeWeights,
        float targetRatio, float targetError, Options options, out float resultError)
    {
        var dest = new uint[indices.Length];
        var targetIndexCount = (nuint)(((int)(indices.Length * targetRatio)) / 3 * 3);
        float err;
        nuint count;
        fixed (uint* d = dest)
        fixed (uint* idx = indices)
        fixed (float* pos = positions)
        fixed (float* attr = attributes)
        fixed (float* w = attributeWeights)
        {
            count = meshopt_simplifyWithAttributes(
                d, idx, (nuint)indices.Length,
                pos, (nuint)vertexCount, (nuint)(positionStrideFloats * sizeof(float)),
                attr, (nuint)(attributeStrideFloats * sizeof(float)),
                w, (nuint)attributeWeights.Length,
                null,
                targetIndexCount, targetError, (uint)options, &err);
        }
        resultError = err;
        Array.Resize(ref dest, (int)count);
        return dest;
    }

    // Mesh extent meshopt normalises simplification error against. Multiply a
    // relative resultError by this to get a world-space deviation (used to drive
    // screen-space-error LOD selection at runtime). Depends only on positions,
    // so it's constant across a primitive's LOD ratios.
    public static float SimplifyScale(float[] positions, int vertexCount, int positionStrideFloats)
    {
        fixed (float* pos = positions)
        {
            return meshopt_simplifyScale(
                pos, (nuint)vertexCount, (nuint)(positionStrideFloats * sizeof(float)));
        }
    }

    // ── Clusters (meshoptimizer's meshlets) ──────────────────────────────────────────────────────

    [StructLayout(LayoutKind.Sequential)]
    private struct Meshlet
    {
        public uint VertexOffset;
        public uint TriangleOffset;
        public uint VertexCount;
        public uint TriangleCount;
    }

    /// <summary>meshopt_Bounds: a cluster's bounding sphere and normal cone (cutoff = cos of half the cone's angle).</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct ClusterBounds
    {
        public Vector3 Center;
        public float Radius;
        public Vector3 ConeApex;
        public Vector3 ConeAxis;
        public float ConeCutoff;
        public sbyte ConeAxisX, ConeAxisY, ConeAxisZ, ConeCutoffS8;
    }

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern nuint meshopt_buildMeshletsBound(nuint indexCount, nuint maxVertices, nuint maxTriangles);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern nuint meshopt_buildMeshlets(
        Meshlet* meshlets, uint* meshletVertices, byte* meshletTriangles, uint* indices, nuint indexCount,
        float* vertexPositions, nuint vertexCount, nuint vertexPositionsStride,
        nuint maxVertices, nuint maxTriangles, float coneWeight);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern void meshopt_optimizeMeshlet(uint* meshletVertices, byte* meshletTriangles, nuint triangleCount, nuint vertexCount);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern ClusterBounds meshopt_computeClusterBounds(
        uint* indices, nuint indexCount, float* vertexPositions, nuint vertexCount, nuint vertexPositionsStride);

    /// <summary>
    /// Splits a triangle list into clusters and returns the same triangles reordered so each cluster is one
    /// run, with the runs' (first index, index count). Windings are kept: a cluster's local triangle indices
    /// map back through its vertex list in the order meshoptimizer wrote them.
    /// </summary>
    /// <param name="positions">Tightly packed xyz, one per vertex.</param>
    public static (uint[] Indices, (int First, int Count)[] Runs) BuildClusters(
        uint[] indices, float[] positions, int vertexCount, int maxVertices, int maxTriangles, float coneWeight)
    {
        var bound = (int)meshopt_buildMeshletsBound((nuint)indices.Length, (nuint)maxVertices, (nuint)maxTriangles);
        var meshlets = new Meshlet[bound];
        var meshletVertices = new uint[indices.Length];
        var meshletTriangles = new byte[indices.Length];
        nuint count;
        fixed (Meshlet* m = meshlets)
        fixed (uint* mv = meshletVertices)
        fixed (byte* mt = meshletTriangles)
        fixed (uint* idx = indices)
        fixed (float* pos = positions)
        {
            count = meshopt_buildMeshlets(
                m, mv, mt, idx, (nuint)indices.Length, pos, (nuint)vertexCount, sizeof(float) * 3,
                (nuint)maxVertices, (nuint)maxTriangles, coneWeight);
            // Locality inside each cluster: the order the rasteriser (and later a ray traversal) reads it in.
            for (nuint i = 0; i < count; i++)
            {
                meshopt_optimizeMeshlet(mv + m[i].VertexOffset, mt + m[i].TriangleOffset, m[i].TriangleCount, m[i].VertexCount);
            }
        }

        var reordered = new uint[indices.Length];
        var runs = new (int First, int Count)[(int)count];
        var at = 0;
        for (var i = 0; i < (int)count; i++)
        {
            var meshlet = meshlets[i];
            runs[i] = (at, (int)meshlet.TriangleCount * 3);
            for (var t = 0; t < meshlet.TriangleCount * 3; t++)
            {
                reordered[at++] = meshletVertices[meshlet.VertexOffset + meshletTriangles[meshlet.TriangleOffset + t]];
            }
        }

        if (at != indices.Length)
        {
            throw new InvalidOperationException($"meshoptimizer clustered {at / 3} of {indices.Length / 3} triangles.");
        }

        return (reordered, runs);
    }

    /// <summary>One run's bounding sphere and normal cone.</summary>
    public static ClusterBounds Bounds(ReadOnlySpan<uint> run, float[] positions, int vertexCount)
    {
        fixed (uint* idx = run)
        fixed (float* pos = positions)
        {
            return meshopt_computeClusterBounds(idx, (nuint)run.Length, pos, (nuint)vertexCount, sizeof(float) * 3);
        }
    }
}

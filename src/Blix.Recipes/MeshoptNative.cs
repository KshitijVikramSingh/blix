using System.Runtime.InteropServices;

namespace Blix.Recipes;

// P/Invoke into vendored meshoptimizer (third_party/meshoptimizer, built to
// libmeshoptimizer.dylib next to the cook by the BuildMeshopt MSBuild target).
// Cook-time only — used to generate mesh LOD index chains. The runtime never
// links meshopt; it just loads the cooked LOD buffers.
public static unsafe class MeshoptNative
{
    private const string Lib = "meshoptimizer";

    // Load libmeshoptimizer.dylib explicitly from the app's own directory (the
    // BuildMeshopt target drops it next to the cook assembly). Default native
    // probing doesn't reliably find an app-local .dylib by the bare name on
    // macOS, so resolve it by absolute path.
    static MeshoptNative() => NativeLibraries.Ensure();

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
}

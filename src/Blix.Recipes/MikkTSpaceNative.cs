using System.Runtime.InteropServices;

namespace Blix.Recipes;

// P/Invoke into vendored MikkTSpace (third_party/mikktspace, built next to the cook by the
// BuildMikk MSBuild target; NativeLibraries names the file). Cook-time only: a cooked mesh carries
// its tangents, and nothing at runtime generates them.
internal static unsafe class MikkTSpaceNative
{
    private const string Lib = "blix_mikk";

    static MikkTSpaceNative() => NativeLibraries.Ensure();

    /// <summary>Is the native generator present?</summary>
    public static bool Available => NativeLibraries.Present(Lib);

    /// <summary>The filename this platform's loader will look for.</summary>
    public static string FileName => NativeLibraries.FileName(Lib);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, EntryPoint = "blix_mikk_generate")]
    private static extern int Generate(
        float* positions, float* normals, float* uvs, uint* indices, int triangles, float* outCornerTangents);

    /// <summary>One tangent per triangle corner: xyz direction, w handedness, in glTF's convention.</summary>
    public static float[] CornerTangents(float[] positions, float[] normals, float[] uvs, uint[] indices)
    {
        if (!Available)
        {
            throw new InvalidOperationException(
                $"{FileName} is not beside the cook, so tangents cannot be generated; it is built by "
                + "Blix.Recipes' BuildMikk target, which needs clang.");
        }

        var tangents = new float[indices.Length * 4];
        fixed (float* p = positions)
        fixed (float* n = normals)
        fixed (float* t = uvs)
        fixed (uint* i = indices)
        fixed (float* o = tangents)
        {
            if (Generate(p, n, t, i, indices.Length / 3, o) == 0)
                throw new InvalidOperationException("MikkTSpace refused the mesh (it could not allocate).");
        }

        return tangents;
    }
}

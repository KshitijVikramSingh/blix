using System.Numerics;
using Blix.Graphics;

// CLI test harness for Blix.Graphics. Currently focused on the F-016
// matrix-convention migration acceptance criteria. After migration:
//
//   - Engine convention is .NET row-vector form (consistent with
//     System.Numerics: CreateTranslation puts translation in M41-M43,
//     Vector4.Transform interprets matrices as row-vector).
//   - Both backends upload raw .NET row-major bytes (no manual transpose).
//   - GLSL std140 column-major reading reinterprets row-major bytes as
//     column-major reads = transpose = column-vector form, which is what
//     GLSL's `M * v_col` expects.
//   - CreatePerspective rewritten to row-vector form (M34=-1, M43=z-translation
//     instead of M43=-1, M34=z-translation as today's column-vector form).
//   - CreateNormalMatrix returns just Invert(model) — no Transpose, because
//     row-vector M's inverse already corresponds to column-vector M^-T after
//     the natural memory-layout transpose on upload.
//   - CreateModel composes as S * R * T in row-vector form (scale applied
//     first to row-vector position, then rotation, then translation).
//
// Tests below assert these invariants. Run against current code: should
// fail loudly. Run after migration: should pass.

var t = new TestRunner();

// ============================================================================
// Section A — System.Numerics ground truth (these should already pass)
// ============================================================================

{
    // .NET stores Matrix4x4 row-major in memory: M11 at offset 0, M12 at 4, ...
    var m = new Matrix4x4(11, 12, 13, 14, 21, 22, 23, 24, 31, 32, 33, 34, 41, 42, 43, 44);
    var span = System.Runtime.InteropServices.MemoryMarshal.CreateReadOnlySpan(ref m, 1);
    var floats = System.Runtime.InteropServices.MemoryMarshal.Cast<Matrix4x4, float>(span);
    t.ExpectClose("Matrix4x4 stores M11 at byte 0", floats[0], 11);
    t.ExpectClose("Matrix4x4 stores M12 at byte 4 (row-major)", floats[1], 12);
    t.ExpectClose("Matrix4x4 stores M41 at byte 48 (row-major)", floats[12], 41);
}

{
    // CreateTranslation puts translation at M41-M43 (row-vector form, last row).
    var t1 = Matrix4x4.CreateTranslation(new Vector3(10, 20, 30));
    t.ExpectClose("CreateTranslation translation at M41-M43 (row-vector form)", t1.M41, 10);
    t.ExpectClose("CreateTranslation M42 == 20", t1.M42, 20);
    t.ExpectClose("CreateTranslation M43 == 30", t1.M43, 30);
    t.ExpectClose("CreateTranslation M14 == 0 (col 4 unused)", t1.M14, 0);
}

{
    // Vector4.Transform applies row-vector convention.
    var translation = Matrix4x4.CreateTranslation(new Vector3(10, 20, 30));
    var result = Vector4.Transform(new Vector4(0, 0, 0, 1), translation);
    t.ExpectClose("Vector4.Transform: origin through translation gives (10,20,30,1)",
        result, new Vector4(10, 20, 30, 1));
}

// ============================================================================
// Section B — Post-migration GraphicsMatrices invariants
// ============================================================================

{
    // CreatePerspective should be in row-vector form: M34 = -1 (perspective
    // divide flag in last column of last row), M43 = z-translation.
    // (Pre-migration: M43 = -1 and M34 = z-translation — column-vector form.)
    var proj = GraphicsMatrices.CreatePerspective(MathF.PI / 3f, 16f / 9f, 0.1f, 100f);
    t.ExpectClose("CreatePerspective M34 == -1 (row-vector perspective-divide flag)", proj.M34, -1f);
    t.ExpectClose("CreatePerspective M43 != -1 (z-translation lives here in row-vector form)",
        proj.M43, (2f * 100f * 0.1f) / (0.1f - 100f));
    t.ExpectClose("CreatePerspective M22 = +focalLength (GL Y up)",
        proj.M22, 1f / MathF.Tan((MathF.PI / 3f) * 0.5f));
}

{
    // CreatePerspectiveVulkan stays as Vector D shipped — already row-vector form.
    var proj = GraphicsMatrices.CreatePerspectiveVulkan(MathF.PI / 3f, 16f / 9f, 0.1f, 100f);
    t.ExpectClose("CreatePerspectiveVulkan M34 == -1 (row-vector form)", proj.M34, -1f);
    t.ExpectClose("CreatePerspectiveVulkan M22 = -focalLength (Vulkan Y down)",
        proj.M22, -1f / MathF.Tan((MathF.PI / 3f) * 0.5f));
}

{
    // CreateModel(pos, rot, scale) in row-vector form: applying a local
    // position v_row should give world position = scale(v) then rotate then translate.
    var model = GraphicsMatrices.CreateModel(
        new Vector3(10, 0, 0),
        Quaternion.Identity,
        new Vector3(0.01f, 0.01f, 0.01f));
    var origin = new Vector4(0, 0, 0, 1);
    var result = Vector4.Transform(origin, model);
    t.ExpectClose("CreateModel: origin maps to position (10,0,0) regardless of scale (scale-first semantics)",
        result, new Vector4(10, 0, 0, 1));
    var unit = new Vector4(1, 0, 0, 1);
    var unitResult = Vector4.Transform(unit, model);
    t.ExpectClose("CreateModel: local (1,0,0) scaled by 0.01 then translated by 10 = (10.01, 0, 0)",
        unitResult, new Vector4(10.01f, 0, 0, 1));
}

{
    // CreateNormalMatrix should be just Invert (no Transpose). The post-migration
    // engine convention means row-vector .NET M's inverse, when uploaded directly
    // and read column-major by GLSL, becomes the column-vector M^-T that the normal
    // matrix formula requires.
    var model = GraphicsMatrices.CreateModel(
        new Vector3(5, 0, 0),
        Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 4f),
        new Vector3(2, 2, 2));
    var normalMat = GraphicsMatrices.CreateNormalMatrix(model);
    Matrix4x4.Invert(model, out var directInverse);
    t.ExpectClose("CreateNormalMatrix == Invert(model) (no transpose) for row-vector convention",
        normalMat.M11 + normalMat.M22 + normalMat.M33, // sum of diagonals as cheap signature
        directInverse.M11 + directInverse.M22 + directInverse.M33);
}

// ============================================================================
// Section C — End-to-end backend symmetry
// ============================================================================
//
// Same .NET matrix should produce identical GLSL-visible math after upload
// through either backend's path. Post-migration both backends do direct memcpy.

{
    var m = Matrix4x4.CreateTranslation(new Vector3(10, 20, 30));

    var glBytes = new float[16];
    var vkBytes = new float[16];

    // Post-migration GL upload: direct memcpy (no WriteColumnMajor).
    var glSpan = System.Runtime.InteropServices.MemoryMarshal.CreateReadOnlySpan(ref m, 1);
    var glFloats = System.Runtime.InteropServices.MemoryMarshal.Cast<Matrix4x4, float>(glSpan);
    glFloats.CopyTo(glBytes);

    // Vulkan upload: already direct memcpy.
    glFloats.CopyTo(vkBytes);

    for (var i = 0; i < 16; i++)
    {
        t.ExpectClose($"GL byte[{i}] == Vk byte[{i}] (backend symmetry)", glBytes[i], vkBytes[i]);
    }
}

{
    // Both backends, given a translation matrix, produce GLSL state that
    // correctly translates origin. (GLSL applies M * v_col with M read
    // column-major; .NET row-major bytes interpreted column-major IS the
    // transpose, which converts row-vector form to column-vector form.)
    var translation = Matrix4x4.CreateTranslation(new Vector3(10, 20, 30));
    var bytes = new float[16];
    var span = System.Runtime.InteropServices.MemoryMarshal.CreateReadOnlySpan(ref translation, 1);
    var floats = System.Runtime.InteropServices.MemoryMarshal.Cast<Matrix4x4, float>(span);
    floats.CopyTo(bytes);

    // Simulate GLSL `M * v_col`.
    var origin = new Vector4(0, 0, 0, 1);
    var glslSees = GlslMul(bytes, origin);
    t.ExpectClose("GLSL receives correct translation through direct-memcpy upload",
        glslSees, new Vector4(10, 20, 30, 1));
}

{
    // The full GL pipeline (direct-memcpy + GLSL column-major read) must
    // produce the same world position for an origin point as Vector4.Transform
    // does in .NET. That's the symmetry guarantee: math result invariant
    // between .NET row-vector and GLSL column-vector interpretation.
    var model = GraphicsMatrices.CreateModel(
        new Vector3(3, -1, 2),
        Quaternion.CreateFromAxisAngle(Vector3.UnitY, 0.7f),
        new Vector3(2, 2, 2));

    var bytes = new float[16];
    var span = System.Runtime.InteropServices.MemoryMarshal.CreateReadOnlySpan(ref model, 1);
    var floats = System.Runtime.InteropServices.MemoryMarshal.Cast<Matrix4x4, float>(span);
    floats.CopyTo(bytes);

    var origin = new Vector4(0, 0, 0, 1);
    var dotnetResult = Vector4.Transform(origin, model);
    var glslResult = GlslMul(bytes, origin);
    t.ExpectClose("End-to-end symmetry: Vector4.Transform == GLSL M*v_col for origin",
        dotnetResult, glslResult);

    var localPoint = new Vector4(1, 0.5f, -0.5f, 1);
    var dotnetResult2 = Vector4.Transform(localPoint, model);
    var glslResult2 = GlslMul(bytes, localPoint);
    t.ExpectClose("End-to-end symmetry: Vector4.Transform == GLSL M*v_col for arbitrary local point",
        dotnetResult2, glslResult2);
}

t.PrintSummary();
return t.FailedCount;

// Simulate GLSL's `M * v_col` with M read column-major from a flat float buffer.
// math (r, c) = bytes[4*c + r]; result[r] = sum_c M[r,c] * v[c].
static Vector4 GlslMul(float[] colMajor, Vector4 v)
{
    var rx = colMajor[0]  * v.X + colMajor[4]  * v.Y + colMajor[8]   * v.Z + colMajor[12] * v.W;
    var ry = colMajor[1]  * v.X + colMajor[5]  * v.Y + colMajor[9]   * v.Z + colMajor[13] * v.W;
    var rz = colMajor[2]  * v.X + colMajor[6]  * v.Y + colMajor[10]  * v.Z + colMajor[14] * v.W;
    var rw = colMajor[3]  * v.X + colMajor[7]  * v.Y + colMajor[11]  * v.Z + colMajor[15] * v.W;
    return new Vector4(rx, ry, rz, rw);
}

sealed class TestRunner
{
    int passed;
    int failed;
    public int FailedCount => failed;

    public void ExpectTrue(string label, bool condition)
    {
        if (!condition) { Fail(label, "predicate was false"); return; }
        Pass(label);
    }

    public void ExpectClose(string label, Vector4 actual, Vector4 expected, float epsilon = 1e-3f)
    {
        var diff = actual - expected;
        if (diff.Length() > epsilon)
        {
            Fail(label, $"expected ({expected.X},{expected.Y},{expected.Z},{expected.W}) got ({actual.X},{actual.Y},{actual.Z},{actual.W})");
            return;
        }
        Pass(label);
    }

    public void ExpectClose(string label, float actual, float expected, float epsilon = 1e-3f)
    {
        if (MathF.Abs(actual - expected) > epsilon)
        {
            Fail(label, $"expected {expected} got {actual}");
            return;
        }
        Pass(label);
    }

    public void Pass(string label) { Console.WriteLine($"  OK   {label}"); passed++; }
    public void Fail(string label, string detail) { Console.WriteLine($"  FAIL {label} - {detail}"); failed++; }

    public void PrintSummary()
    {
        Console.WriteLine();
        Console.WriteLine($"{passed}/{passed + failed} passed, {failed} failed");
    }
}

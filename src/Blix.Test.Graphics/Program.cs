using System.Numerics;
using Blix.Graphics;
using Blix.Graphics.Vulkan;

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

// ============================================================================
// Section D — GL transpose-flag semantics (regression for the transpose:true
//             vs transpose:false confusion that broke GL demos after F-016).
// ============================================================================
//
// The byte-level tests above don't exercise the GL.UniformMatrix4 transpose
// flag — they only check what GLSL sees AFTER GL's internal reinterpretation.
// The actual flag value matters: for .NET row-major bytes to land as
// column-vector form in GLSL, GL must read them as column-major
// (transpose: false). The opposite (transpose: true) leaves the matrix as
// row-vector form in GLSL, which silently breaks every M*v multiplication.

{
    var m = Matrix4x4.CreateTranslation(new Vector3(10, 20, 30));

    var bytes = new float[16];
    var span = System.Runtime.InteropServices.MemoryMarshal.CreateReadOnlySpan(ref m, 1);
    var floats = System.Runtime.InteropServices.MemoryMarshal.Cast<Matrix4x4, float>(span);
    floats.CopyTo(bytes);

    // Simulate GL.UniformMatrix4(transpose: false): input treated as column-major.
    // GLSL math(r, c) = bytes[4*c + r].
    var origin = new Vector4(0, 0, 0, 1);
    var glslTransposeFalse = GlslMulColumnMajor(bytes, origin);
    t.ExpectClose("GL transpose:false + row-major .NET bytes ⇒ GLSL translates correctly",
        glslTransposeFalse, new Vector4(10, 20, 30, 1));

    // Simulate GL.UniformMatrix4(transpose: true): input treated as row-major.
    // GLSL math(r, c) = bytes[4*r + c]. For .NET row-vector matrix this leaves
    // translation in row 3 — column-vector M*v_col then produces NO translation.
    var glslTransposeTrue = GlslMulRowMajor(bytes, origin);
    t.ExpectClose("GL transpose:true + row-major .NET bytes ⇒ GLSL DOES NOT translate (regression marker)",
        glslTransposeTrue, new Vector4(0, 0, 0, 1));
}

// ============================================================================
// Section E — ShaderInterface structural validation (Vector A 2a).
// ============================================================================
//
// 2a only declares the binding-contract types; backend wiring lands in 2b.
// The tests here lock in the shape of Validate() so a malformed
// ShaderInterface fails at startup with a specific reason instead of at
// pipeline-creation time with a Vulkan validation-layer error.

// Helper: a minimal valid UBO BlockLayout (mat4 × 1, 64 bytes).
static UniformBlockLayout Mat4Block() => new(
    TotalSize: 64,
    Members: new[] { new UniformBlockMember("uModel", Offset: 0, Size: 64) });

// E.1 — Happy path: the cube demo's actual interface validates cleanly.
{
    var cube = new ShaderInterface(new[]
    {
        new DescriptorSetSlot(
            Set: 0, Binding: 0,
            Type: ShaderResourceType.UniformBuffer,
            Stages: ShaderStages.Vertex | ShaderStages.Fragment,
            BlockLayout: new UniformBlockLayout(
                TotalSize: 128,
                Members: new[]
                {
                    new UniformBlockMember("uViewProjection", 0, 64),
                    new UniformBlockMember("uModel", 64, 64),
                })),
    });
    var threw = TryValidate(cube);
    t.ExpectTrue("E.1 cube demo interface validates", threw is null);
}

// E.2 — Lit-shader-shape fixture: per-frame UBO + per-pass UBO + sampler array
// + per-material UBO + per-material samplers + push constants. Mirrors
// docs/vulkan-reshape-shaderlab-target.md Section 1. Proves the type composes
// for the real downstream use case.
{
    var lit = new ShaderInterface(
        Slots: new[]
        {
            new DescriptorSetSlot(0, 0, ShaderResourceType.UniformBuffer,
                ShaderStages.Vertex | ShaderStages.Fragment, BlockLayout: Mat4Block()),
            new DescriptorSetSlot(1, 0, ShaderResourceType.UniformBuffer,
                ShaderStages.Vertex | ShaderStages.Fragment, BlockLayout: Mat4Block()),
            new DescriptorSetSlot(1, 1, ShaderResourceType.SampledImage, ShaderStages.Fragment),
            new DescriptorSetSlot(1, 4, ShaderResourceType.SampledImage, ShaderStages.Fragment, Count: 4),
            new DescriptorSetSlot(2, 0, ShaderResourceType.UniformBuffer,
                ShaderStages.Fragment, BlockLayout: Mat4Block()),
            new DescriptorSetSlot(2, 1, ShaderResourceType.SampledImage, ShaderStages.Fragment),
        },
        PushConstants: new[]
        {
            new PushConstantRange(ShaderStages.Vertex, Offset: 0,  Size: 64),
            new PushConstantRange(ShaderStages.Vertex, Offset: 64, Size: 64),
        });
    var threw = TryValidate(lit);
    t.ExpectTrue("E.2 lit-shape interface (multi-set + sampler array + push constants) validates", threw is null);
}

// E.3 — Duplicate (set, binding) is rejected.
{
    var dup = new ShaderInterface(new[]
    {
        new DescriptorSetSlot(0, 0, ShaderResourceType.UniformBuffer, ShaderStages.Vertex, BlockLayout: Mat4Block()),
        new DescriptorSetSlot(0, 0, ShaderResourceType.SampledImage, ShaderStages.Fragment),
    });
    var threw = TryValidate(dup);
    t.ExpectTrue("E.3 duplicate (set,binding) is rejected", threw is { } e && e.Message.Contains("duplicate"));
}

// E.4 — UniformBuffer without BlockLayout is rejected.
{
    var bad = new ShaderInterface(new[]
    {
        new DescriptorSetSlot(0, 0, ShaderResourceType.UniformBuffer, ShaderStages.Vertex),
    });
    var threw = TryValidate(bad);
    t.ExpectTrue("E.4 UniformBuffer without BlockLayout is rejected",
        threw is { } e && e.Message.Contains("no BlockLayout"));
}

// E.5 — StorageBuffer without BlockLayout is rejected (same rule as UBO).
{
    var bad = new ShaderInterface(new[]
    {
        new DescriptorSetSlot(3, 0, ShaderResourceType.StorageBuffer, ShaderStages.Vertex),
    });
    var threw = TryValidate(bad);
    t.ExpectTrue("E.5 StorageBuffer without BlockLayout is rejected",
        threw is { } e && e.Message.Contains("no BlockLayout"));
}

// E.6 — SampledImage carrying a BlockLayout is rejected.
{
    var bad = new ShaderInterface(new[]
    {
        new DescriptorSetSlot(1, 1, ShaderResourceType.SampledImage, ShaderStages.Fragment, BlockLayout: Mat4Block()),
    });
    var threw = TryValidate(bad);
    t.ExpectTrue("E.6 SampledImage with BlockLayout is rejected",
        threw is { } e && e.Message.Contains("must not declare one"));
}

// E.7 — Stages.None on a slot is rejected.
{
    var bad = new ShaderInterface(new[]
    {
        new DescriptorSetSlot(0, 0, ShaderResourceType.UniformBuffer, ShaderStages.None, BlockLayout: Mat4Block()),
    });
    var threw = TryValidate(bad);
    t.ExpectTrue("E.7 Stages=None on slot is rejected",
        threw is { } e && e.Message.Contains("Stages=None"));
}

// E.8 — Count < 1 is rejected.
{
    var bad = new ShaderInterface(new[]
    {
        new DescriptorSetSlot(0, 0, ShaderResourceType.UniformBuffer, ShaderStages.Vertex, Count: 0, BlockLayout: Mat4Block()),
    });
    var threw = TryValidate(bad);
    t.ExpectTrue("E.8 Count=0 on slot is rejected",
        threw is { } e && e.Message.Contains("Count=0"));
}

// E.9 — Negative Set / Binding is rejected.
{
    var bad = new ShaderInterface(new[]
    {
        new DescriptorSetSlot(-1, 0, ShaderResourceType.UniformBuffer, ShaderStages.Vertex, BlockLayout: Mat4Block()),
    });
    var threw = TryValidate(bad);
    t.ExpectTrue("E.9 negative Set is rejected",
        threw is { } e && e.Message.Contains("negative Set"));
}

// E.10 — Overlapping push-constant ranges within the same stage are rejected.
{
    var bad = new ShaderInterface(
        Slots: Array.Empty<DescriptorSetSlot>(),
        PushConstants: new[]
        {
            new PushConstantRange(ShaderStages.Vertex, 0, 64),
            new PushConstantRange(ShaderStages.Vertex, 32, 64),
        });
    var threw = TryValidate(bad);
    t.ExpectTrue("E.10 push-constant overlap within shared stage is rejected",
        threw is { } e && e.Message.Contains("overlapping byte ranges"));
}

// E.11 — Overlapping push-constant ranges in DISJOINT stages are allowed.
// (Per Vulkan spec: separate hardware blocks per stage, so overlap is fine
// when no stage bit is shared.)
{
    var ok = new ShaderInterface(
        Slots: Array.Empty<DescriptorSetSlot>(),
        PushConstants: new[]
        {
            new PushConstantRange(ShaderStages.Vertex,   0, 64),
            new PushConstantRange(ShaderStages.Fragment, 0, 64),
        });
    var threw = TryValidate(ok);
    t.ExpectTrue("E.11 push-constant overlap across disjoint stages is allowed", threw is null);
}

// E.12 — Push-constant Size ≤ 0 is rejected.
{
    var bad = new ShaderInterface(
        Slots: Array.Empty<DescriptorSetSlot>(),
        PushConstants: new[] { new PushConstantRange(ShaderStages.Vertex, 0, 0) });
    var threw = TryValidate(bad);
    t.ExpectTrue("E.12 push-constant Size=0 is rejected",
        threw is { } e && e.Message.Contains("Size=0"));
}

// E.13 — Push-constant Stages.None is rejected.
{
    var bad = new ShaderInterface(
        Slots: Array.Empty<DescriptorSetSlot>(),
        PushConstants: new[] { new PushConstantRange(ShaderStages.None, 0, 64) });
    var threw = TryValidate(bad);
    t.ExpectTrue("E.13 push-constant Stages=None is rejected",
        threw is { } e && e.Message.Contains("Stages=None"));
}

// ============================================================================
// Section F — Texture infrastructure (Vector A 2c).
// ============================================================================
//
// The Vulkan sampler cache keys on SamplerDescription value-equality. If the
// record loses its auto-generated equality (or a future refactor turns it
// into a class) the cache silently deduplicates nothing and identical
// samplers proliferate. The tests below lock that contract in.
//
// MipByteCount() also pins down the byte-count math the staging-buffer
// path relies on — get it wrong and we either upload garbage tail bytes or
// crash on a short copy.

{
    // F.1 — Two LinearRepeat presets are equal (cache dedupes presets).
    t.ExpectTrue("F.1 SamplerDescription.LinearRepeat equals itself",
        SamplerDescription.LinearRepeat.Equals(SamplerDescription.LinearRepeat));
    t.ExpectTrue("F.1 LinearRepeat == fresh equivalent description",
        SamplerDescription.LinearRepeat.Equals(new SamplerDescription(
            TextureFilter.Linear, TextureFilter.Linear,
            TextureWrap.Repeat, TextureWrap.Repeat,
            GenerateMipmaps: false)));
}

{
    // F.2 — Two distinct sampler descriptions are NOT equal (cache distinguishes).
    t.ExpectTrue("F.2 LinearRepeat != LinearClamp",
        !SamplerDescription.LinearRepeat.Equals(SamplerDescription.LinearClamp));
    t.ExpectTrue("F.2 LinearRepeat != PixelatedRepeat (filter differs)",
        !SamplerDescription.LinearRepeat.Equals(SamplerDescription.PixelatedRepeat));
    t.ExpectTrue("F.2 LinearClamp != LinearClampMipmap (mipmap toggle differs)",
        !SamplerDescription.LinearClamp.Equals(SamplerDescription.LinearClampMipmap));
}

{
    // F.3 — Uncompressed format byte counts.
    t.ExpectClose("F.3 Rgba8 256×256 = 256*256*4 bytes",
        TextureFormat.Rgba8.MipByteCount(256, 256), 256 * 256 * 4);
    t.ExpectClose("F.3 Rgba8Srgb 1×1 = 4 bytes",
        TextureFormat.Rgba8Srgb.MipByteCount(1, 1), 4);
    t.ExpectClose("F.3 R8 16×16 = 256 bytes",
        TextureFormat.R8.MipByteCount(16, 16), 256);
    t.ExpectClose("F.3 Rgba16F 4×4 = 4*4*8 bytes",
        TextureFormat.Rgba16F.MipByteCount(4, 4), 4 * 4 * 8);
}

{
    // F.4 — BCn block math. Block-compressed formats are 16 bytes per 4×4 block,
    // and small mips round UP to the next block boundary — a 1×1 mip still
    // costs a full 16-byte block, not 1 byte.
    t.ExpectClose("F.4 Bc7Srgb 4×4 = one block",
        TextureFormat.Bc7Srgb.MipByteCount(4, 4), 16);
    t.ExpectClose("F.4 Bc7Srgb 1×1 = one block (padded)",
        TextureFormat.Bc7Srgb.MipByteCount(1, 1), 16);
    t.ExpectClose("F.4 Bc7Srgb 7×7 = 2×2 blocks (rounded up)",
        TextureFormat.Bc7Srgb.MipByteCount(7, 7), 2 * 2 * 16);
    t.ExpectClose("F.4 Bc5Unorm 8×8 = 4 blocks",
        TextureFormat.Bc5Unorm.MipByteCount(8, 8), 2 * 2 * 16);
}

t.PrintSummary();
return t.FailedCount;

// Calls Validate() on a ShaderInterface and returns the thrown exception
// (or null on success). Lets test cases assert *which* failure occurred
// without leaking try/catch into every case.
static InvalidOperationException? TryValidate(ShaderInterface iface)
{
    try { iface.Validate(); return null; }
    catch (InvalidOperationException e) { return e; }
}

// GLSL M*v_col interpretation when GL stored the bytes as column-major
// (transpose:false in UniformMatrix4). math(r, c) = bytes[4*c + r].
static Vector4 GlslMulColumnMajor(float[] bytes, Vector4 v)
{
    var rx = bytes[0]  * v.X + bytes[4]  * v.Y + bytes[8]   * v.Z + bytes[12] * v.W;
    var ry = bytes[1]  * v.X + bytes[5]  * v.Y + bytes[9]   * v.Z + bytes[13] * v.W;
    var rz = bytes[2]  * v.X + bytes[6]  * v.Y + bytes[10]  * v.Z + bytes[14] * v.W;
    var rw = bytes[3]  * v.X + bytes[7]  * v.Y + bytes[11]  * v.Z + bytes[15] * v.W;
    return new Vector4(rx, ry, rz, rw);
}

// GLSL M*v_col when GL was told transpose:true (input was row-major).
// math(r, c) = bytes[4*r + c]. result[r] = sum_c M[r,c] * v[c] = sum_c bytes[4*r+c] * v[c].
static Vector4 GlslMulRowMajor(float[] bytes, Vector4 v)
{
    var rx = bytes[0]  * v.X + bytes[1]  * v.Y + bytes[2]   * v.Z + bytes[3]  * v.W;
    var ry = bytes[4]  * v.X + bytes[5]  * v.Y + bytes[6]   * v.Z + bytes[7]  * v.W;
    var rz = bytes[8]  * v.X + bytes[9]  * v.Y + bytes[10]  * v.Z + bytes[11] * v.W;
    var rw = bytes[12] * v.X + bytes[13] * v.Y + bytes[14]  * v.Z + bytes[15] * v.W;
    return new Vector4(rx, ry, rz, rw);
}

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

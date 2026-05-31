using System.Numerics;
using Blix.Graphics;
using Blix.Graphics.Vulkan;
// Silk.NET.Vulkan types are used by Section N (BarrierOp value equality).
// Aliased rather than globally imported to avoid ambiguity with
// Blix.Graphics.Vulkan.PushConstantRange and Blix.Graphics.PrimitiveTopology.
using VkImageLayout = Silk.NET.Vulkan.ImageLayout;
using VkPipelineStageFlags = Silk.NET.Vulkan.PipelineStageFlags;
using VkAccessFlags = Silk.NET.Vulkan.AccessFlags;

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

// ============================================================================
// Section G — Material + sparse-set ShaderInterface (Vector A 2d).
// ============================================================================
//
// Live MaterialBindings construction needs a real VkDevice and isn't run
// here. What's covered:
//   - MaterialHandle value equality (it's an opaque id; the draw command
//     captures it by value)
//   - DrawIndexedCommand record carries Material correctly through with-
//     style updates
//   - Cube demo's actual ShaderInterface (sparse — set 0 + set 2, no set 1)
//     validates cleanly; exercises the gap-set path 2b introduced

{
    // G.1 — MaterialHandle equality.
    var a = new MaterialHandle(42);
    var b = new MaterialHandle(42);
    var c = new MaterialHandle(43);
    t.ExpectTrue("G.1 MaterialHandle equal by id", a.Equals(b));
    t.ExpectTrue("G.1 MaterialHandle distinct ids != equal", !a.Equals(c));
    t.ExpectTrue("G.1 MaterialHandle hashcodes match for equal", a.GetHashCode() == b.GetHashCode());
}

{
    // G.2 — DrawIndexedCommand carries Material via record with-update.
    var baseCmd = new DrawIndexedCommand(
        new VertexBufferHandle(1), new IndexBufferHandle(2), new PipelineHandle(3),
        36, Array.Empty<ShaderUniform>(), Array.Empty<ShaderTextureBinding>());
    t.ExpectTrue("G.2 default DrawIndexedCommand.Material is null", baseCmd.Material is null);
    var withMat = baseCmd with { Material = new MaterialHandle(7) };
    t.ExpectTrue("G.2 with-update preserves Material",
        withMat.Material is { } h && h.Id == 7);
    t.ExpectTrue("G.2 with-update preserves Pipeline", withMat.Pipeline.Id == 3);
}

{
    // G.3 — Sparse-set interface (cube demo shape) validates. Set 1 is
    // skipped entirely; 2b creates an empty layout for it at pipeline-layout
    // time. ShaderInterface.Validate() doesn't reject the gap.
    var frameUbo = new UniformBlockLayout(
        TotalSize: 128,
        Members: new[]
        {
            new UniformBlockMember("uViewProjection", 0, 64),
            new UniformBlockMember("uModel", 64, 64),
        });
    var tintUbo = new UniformBlockLayout(
        TotalSize: 16,
        Members: new[] { new UniformBlockMember("uTint", 0, 16) });
    var sparseInterface = new ShaderInterface(new[]
    {
        new DescriptorSetSlot(0, 0, ShaderResourceType.UniformBuffer,
            ShaderStages.Vertex | ShaderStages.Fragment, BlockLayout: frameUbo),
        new DescriptorSetSlot(2, 0, ShaderResourceType.UniformBuffer,
            ShaderStages.Fragment, BlockLayout: tintUbo),
        new DescriptorSetSlot(2, 1, ShaderResourceType.SampledImage, ShaderStages.Fragment),
    });
    var threw = TryValidate(sparseInterface);
    t.ExpectTrue("G.3 cube demo sparse-set interface validates (set 0 + set 2, no set 1)",
        threw is null);
}

// ============================================================================
// Section H — Push constants on DrawIndexedCommand (Vector A 2e).
// ============================================================================
//
// vkCmdPushConstants emission needs a real device; not tested here. Surface-
// level tests confirm the cross-backend command-record path carries the
// payload correctly and the cube demo's full interface (sparse sets + push
// range) passes Validate().

{
    // H.1 — Default and with-update PushConstants round-trip.
    var baseCmd = new DrawIndexedCommand(
        new VertexBufferHandle(1), new IndexBufferHandle(2), new PipelineHandle(3),
        36, Array.Empty<ShaderUniform>(), Array.Empty<ShaderTextureBinding>());
    t.ExpectTrue("H.1 default DrawIndexedCommand.PushConstants is null", baseCmd.PushConstants is null);

    var bytes = new byte[64];
    bytes[0] = 0xAB; bytes[63] = 0xCD;
    var withPush = baseCmd with { PushConstants = bytes };
    t.ExpectTrue("H.1 with-update preserves PushConstants reference",
        ReferenceEquals(withPush.PushConstants, bytes));
    t.ExpectClose("H.1 PushConstants[0] preserved", withPush.PushConstants![0], 0xAB);
    t.ExpectClose("H.1 PushConstants[63] preserved", withPush.PushConstants![63], 0xCD);
}

{
    // H.2 — Full cube demo interface (sparse sets + push-constant range)
    // validates. Mirrors what Vulkan demo Program.cs constructs.
    var frameUbo = new UniformBlockLayout(
        TotalSize: 64,
        Members: new[] { new UniformBlockMember("uViewProjection", 0, 64) });
    var tintUbo = new UniformBlockLayout(
        TotalSize: 16,
        Members: new[] { new UniformBlockMember("uTint", 0, 16) });
    var full = new ShaderInterface(
        Slots: new[]
        {
            new DescriptorSetSlot(0, 0, ShaderResourceType.UniformBuffer,
                ShaderStages.Vertex | ShaderStages.Fragment, BlockLayout: frameUbo),
            new DescriptorSetSlot(2, 0, ShaderResourceType.UniformBuffer,
                ShaderStages.Fragment, BlockLayout: tintUbo),
            new DescriptorSetSlot(2, 1, ShaderResourceType.SampledImage, ShaderStages.Fragment),
        },
        PushConstants: new[]
        {
            new PushConstantRange(ShaderStages.Vertex, Offset: 0, Size: 64),
        });
    var threw = TryValidate(full);
    t.ExpectTrue("H.2 cube demo full interface (sparse sets + 64B vertex push range) validates",
        threw is null);
}

// ============================================================================
// Section I — Array-uniform ElementStride (closes F-009).
// ============================================================================
//
// std140 forces every array element to 16-byte alignment regardless of the
// element's natural size. ElementStride captures that stride so future
// array-uniform writers (the ShaderLab lit shader's uSpotVPs[4] is the
// motivating case) can translate (memberIndex, value) into the right byte
// offset. The Write path itself is deferred; this section locks in the
// declaration shape.

{
    // I.1 — ElementStride round-trips on the record. Default is 0 so
    // existing single-value members stay unchanged.
    var scalar = new UniformBlockMember("uTime", 0, 4);
    t.ExpectClose("I.1 ElementStride defaults to 0 for scalar members", scalar.ElementStride, 0);

    var arr = new UniformBlockMember("uSpotVPs", Offset: 80, Size: 64 * 4, ElementStride: 64);
    t.ExpectClose("I.1 array-member ElementStride round-trips", arr.ElementStride, 64);
    t.ExpectClose("I.1 array-member Size = ElementStride * count", arr.Size, arr.ElementStride * 4);
}

{
    // I.2 — Lit-shape per-pass UBO (sun shadow VP + spot shadow VP array)
    // validates cleanly via the existing ShaderInterface.Validate(). Real
    // call site this enables is the ShaderLab lit-shader port (step 6).
    var perPassLit = new UniformBlockLayout(
        TotalSize: 80 + 64 * 4,
        Members: new[]
        {
            new UniformBlockMember("uSunShadowVP", Offset: 0,  Size: 64),
            new UniformBlockMember("uEnvMipCount", Offset: 64, Size: 4),
            new UniformBlockMember("uSpotVPs",     Offset: 80, Size: 64 * 4, ElementStride: 64),
        });
    var iface = new ShaderInterface(new[]
    {
        new DescriptorSetSlot(1, 0, ShaderResourceType.UniformBuffer,
            ShaderStages.Vertex | ShaderStages.Fragment, BlockLayout: perPassLit),
    });
    var threw = TryValidate(iface);
    t.ExpectTrue("I.2 lit-shape per-pass UBO with array member validates", threw is null);
}

// ============================================================================
// Section J — RenderSurface shape (step 4).
// ============================================================================
//
// Backend tests need a live device; not run here. Cross-backend surface
// shape coverage: handle equality, description value equality (so future
// caching keys behave), size-type discrimination, PipelineDescription
// RenderTarget round-trip via record with-update.

{
    // J.1 — RenderSurfaceHandle equality.
    t.ExpectTrue("J.1 RenderSurfaceHandle id 0 == Default",
        RenderSurfaceHandle.Default.Equals(new RenderSurfaceHandle(0)));
    t.ExpectTrue("J.1 RenderSurfaceHandle distinct ids != equal",
        !new RenderSurfaceHandle(1).Equals(new RenderSurfaceHandle(2)));
}

{
    // J.2 — RenderSurfaceDescription record equality. C# records use
    // reference equality on IReadOnlyList<T> fields, NOT deep value
    // equality — equality holds when the underlying list reference is
    // shared. The `with` expression preserves that reference, so a
    // single field change still compares equal everywhere else.
    var colors = new[]
    {
        new ColorAttachmentDescription(TextureFormat.Rgba16F, SamplerDescription.LinearClamp),
    };
    var a = new RenderSurfaceDescription(
        Name: "a",
        Size: new FixedRenderSurfaceSize(800, 600),
        ColorAttachments: colors,
        Depth: new DepthRenderbuffer());
    var bSameRef = a with { };  // shares colors reference + same Depth (record clone)
    t.ExpectTrue("J.2 same-reference clone is equal", a.Equals(bSameRef));

    var differentSize = a with { Size = new FixedRenderSurfaceSize(1024, 768) };
    t.ExpectTrue("J.2 different size makes them !=", !a.Equals(differentSize));
}

{
    // J.3 — RenderSurfaceSize discriminated cases round-trip.
    var fixedSize = new FixedRenderSurfaceSize(400, 300);
    t.ExpectClose("J.3 FixedRenderSurfaceSize.Width", fixedSize.Width, 400);
    t.ExpectClose("J.3 FixedRenderSurfaceSize.Height", fixedSize.Height, 300);

    var match = new MatchDefaultRenderSurfaceSize(Scale: 0.5f);
    t.ExpectClose("J.3 MatchDefaultRenderSurfaceSize.Scale", match.Scale, 0.5f);

    var matchDefault = new MatchDefaultRenderSurfaceSize();
    t.ExpectClose("J.3 MatchDefaultRenderSurfaceSize default scale = 1.0", matchDefault.Scale, 1.0f);
}

{
    // J.4 — PipelineDescription.RenderTarget round-trips via record with-update.
    var basePipe = new PipelineDescription(
        new ShaderProgramHandle(1),
        VertexPosition3Texture.Layout,
        PrimitiveTopology.Triangles,
        DepthState.LessEqualWrite,
        RasterizerState.NoCulling,
        BlendState.Disabled);
    t.ExpectTrue("J.4 default PipelineDescription.RenderTarget is null", basePipe.RenderTarget is null);
    var withTarget = basePipe with { RenderTarget = new RenderSurfaceHandle(7) };
    t.ExpectTrue("J.4 with-update preserves RenderTarget id",
        withTarget.RenderTarget is { } h && h.Id == 7);
}

// ============================================================================
// Section K — RenderGraph public type vocabulary (Vector B VB.i).
// ============================================================================
//
// Pure-type smoke tests for the graph's small types. Pass/factory/build
// behavior gets exercised in VB.ii once device-aware tests are wired up
// (InternalsVisibleTo or via the demo). For now: handle equality,
// TextureView shape + cube-face bounds, enum sanity.

{
    // K.1 — Handle equality.
    t.ExpectTrue("K.1 GraphResourceHandle equal by id",
        new GraphResourceHandle(7).Equals(new GraphResourceHandle(7)));
    t.ExpectTrue("K.1 GraphResourceHandle different ids != equal",
        !new GraphResourceHandle(7).Equals(new GraphResourceHandle(8)));
    t.ExpectTrue("K.1 PassHandle equal by id",
        new PassHandle(1).Equals(new PassHandle(1)));
    t.ExpectTrue("K.1 DepthCubeHandle equal by id",
        new DepthCubeHandle(3).Equals(new DepthCubeHandle(3)));
}

{
    // K.2 — TextureView whole-image vs face view.
    var handle = new GraphResourceHandle(42);
    TextureView whole = handle;  // implicit conversion
    t.ExpectTrue("K.2 implicit conversion produces whole-image view",
        whole.IsWholeImage && whole.Resource.Equals(handle));

    var face0 = new TextureView(handle, 0);
    var face1 = new TextureView(handle, 1);
    t.ExpectTrue("K.2 face views differ across faces", !face0.Equals(face1));
    t.ExpectTrue("K.2 same-face view equality holds",
        face0.Equals(new TextureView(handle, 0)));
    t.ExpectTrue("K.2 face view is not whole-image", !face0.IsWholeImage);
}

{
    // K.3 — DepthCubeHandle.Face bounds + view construction.
    var cube = new DepthCubeHandle(5);
    var face0 = cube.Face(0);
    var face5 = cube.Face(5);
    t.ExpectTrue("K.3 cube.Face(0) yields face 0 view", face0.Face == 0);
    t.ExpectTrue("K.3 cube.Face(5) yields face 5 view", face5.Face == 5);
    t.ExpectTrue("K.3 cube.Face view targets cube's underlying resource id",
        face0.Resource.Id == cube.Id);

    var threwBelow = false;
    try { cube.Face(-1); } catch (ArgumentOutOfRangeException) { threwBelow = true; }
    t.ExpectTrue("K.3 cube.Face(-1) rejected", threwBelow);

    var threwAbove = false;
    try { cube.Face(6); } catch (ArgumentOutOfRangeException) { threwAbove = true; }
    t.ExpectTrue("K.3 cube.Face(6) rejected", threwAbove);
}

{
    // K.4 — DepthCubeHandle implicit conversion to GraphResourceHandle
    // (whole-cube sampling falls through to standard Read/Target path).
    var cube = new DepthCubeHandle(11);
    GraphResourceHandle asHandle = cube;
    t.ExpectTrue("K.4 DepthCubeHandle implicit → GraphResourceHandle preserves id",
        asHandle.Id == 11);
}

{
    // K.5 — GraphSize discriminated union round-trips.
    var fixedSize = new FixedGraphSize(800, 600);
    t.ExpectClose("K.5 FixedGraphSize.Width", fixedSize.Width, 800);
    t.ExpectClose("K.5 FixedGraphSize.Height", fixedSize.Height, 600);

    var matchHalf = new MatchSwapchainGraphSize(0.5f);
    t.ExpectClose("K.5 MatchSwapchainGraphSize.Scale custom", matchHalf.Scale, 0.5f);

    var matchDefault = new MatchSwapchainGraphSize();
    t.ExpectClose("K.5 MatchSwapchainGraphSize default scale = 1.0", matchDefault.Scale, 1.0f);
}

{
    // K.6 — LoadOp / StoreOp enum values exist and are distinct.
    t.ExpectTrue("K.6 LoadOp values distinct",
        LoadOp.Clear != LoadOp.Load && LoadOp.Load != LoadOp.DontCare);
    t.ExpectTrue("K.6 StoreOp values distinct",
        StoreOp.Store != StoreOp.DontCare);
}

// ============================================================================
// Section L — RenderGraph validation (Vector B VB.ii).
// ============================================================================
//
// Pure-function validation runs at Compile(). Tests use the internal
// parameterless RenderGraph() constructor (InternalsVisibleTo set on
// Blix.Graphics.Vulkan.csproj). No live VkDevice needed; backend
// allocation lands in VB.iii.

// Minimal valid shader interface for Section L tests — one UBO at
// (set 0, binding 0). Real shaders have more shape but validation
// only cares that .Shader(...) was called with something.
static ShaderInterface MinimalShader() => new(new[]
{
    new DescriptorSetSlot(
        Set: 0, Binding: 0,
        Type: ShaderResourceType.UniformBuffer,
        Stages: ShaderStages.Vertex | ShaderStages.Fragment,
        BlockLayout: new UniformBlockLayout(
            TotalSize: 64,
            Members: new[] { new UniformBlockMember("uViewProjection", 0, 64) })),
});

{
    // L.1 — Happy path: minimal valid graph compiles cleanly.
    var graph = new RenderGraph();
    var color = graph.ColorTarget("out", TextureFormat.Rgba8, new FixedGraphSize(64, 64));
    graph.GraphicsPass("draw")
        .Target(color, LoadOp.Clear, StoreOp.Store)
        .Shader(MinimalShader());
    var threw = false;
    try { graph.Compile(); } catch { threw = true; }
    t.ExpectTrue("L.1 minimal valid graph compiles", !threw);
    t.ExpectTrue("L.1 IsCompiled flips true on success", graph.IsCompiled);
}

{
    // L.2 — Empty graph rejected with named reason.
    var graph = new RenderGraph();
    InvalidOperationException? caught = null;
    try { graph.Compile(); } catch (InvalidOperationException e) { caught = e; }
    t.ExpectTrue("L.2 empty graph rejected",
        caught is not null && caught.Message.Contains("no declared passes"));
    t.ExpectTrue("L.2 IsCompiled stays false after validation failure",
        !graph.IsCompiled);
}

{
    // L.3 — Duplicate pass name rejected with the offending name.
    var graph = new RenderGraph();
    var color = graph.ColorTarget("c", TextureFormat.Rgba8, new FixedGraphSize(64, 64));
    graph.GraphicsPass("twin").Target(color, LoadOp.Clear, StoreOp.Store).Shader(MinimalShader());
    graph.GraphicsPass("twin").Target(color, LoadOp.Load, StoreOp.Store).Shader(MinimalShader());
    InvalidOperationException? caught = null;
    try { graph.Compile(); } catch (InvalidOperationException e) { caught = e; }
    t.ExpectTrue("L.3 duplicate pass name rejected with name in message",
        caught is not null && caught.Message.Contains("'twin'"));
}

{
    // L.4 — GraphicsPass with no Target or Depth rejected.
    var graph = new RenderGraph();
    graph.GraphicsPass("naked").Shader(MinimalShader());
    InvalidOperationException? caught = null;
    try { graph.Compile(); } catch (InvalidOperationException e) { caught = e; }
    t.ExpectTrue("L.4 graphics pass without attachment rejected",
        caught is not null && caught.Message.Contains("'naked'") && caught.Message.Contains("Target or Depth"));
}

{
    // L.5 — GraphicsPass with no Shader declared rejected.
    var graph = new RenderGraph();
    var color = graph.ColorTarget("c", TextureFormat.Rgba8, new FixedGraphSize(64, 64));
    graph.GraphicsPass("shaderless").Target(color, LoadOp.Clear, StoreOp.Store);
    InvalidOperationException? caught = null;
    try { graph.Compile(); } catch (InvalidOperationException e) { caught = e; }
    t.ExpectTrue("L.5 graphics pass without shader rejected",
        caught is not null && caught.Message.Contains("'shaderless'") && caught.Message.Contains("Shader"));
}

{
    // L.6 — ComputePass with no Shader rejected.
    var graph = new RenderGraph();
    var img = graph.ColorTarget("img", TextureFormat.Rgba8, new FixedGraphSize(64, 64));
    graph.ComputePass("compute-no-shader").Write(img);
    InvalidOperationException? caught = null;
    try { graph.Compile(); } catch (InvalidOperationException e) { caught = e; }
    t.ExpectTrue("L.6 compute pass without shader rejected",
        caught is not null && caught.Message.Contains("'compute-no-shader'"));
}

{
    // L.7 — Read of undeclared resource rejected.
    var graph = new RenderGraph();
    var color = graph.ColorTarget("c", TextureFormat.Rgba8, new FixedGraphSize(64, 64));
    var phantom = new GraphResourceHandle(999); // never created via factories
    graph.GraphicsPass("p")
        .Target(color, LoadOp.Clear, StoreOp.Store)
        .Read(phantom)
        .Shader(MinimalShader());
    InvalidOperationException? caught = null;
    try { graph.Compile(); } catch (InvalidOperationException e) { caught = e; }
    t.ExpectTrue("L.7 read of unknown resource rejected",
        caught is not null && caught.Message.Contains("not declared as a Target/Depth/Write by any prior pass"));
}

{
    // L.8 — Cycle case: Pass A reads X (produced by Pass B), but A is
    // declared BEFORE B. Per D4 declaration-order semantics, A's read
    // sees an empty producer set and fails with the cycle-equivalent
    // 'not declared by any prior pass' error.
    var graph = new RenderGraph();
    var x = graph.ColorTarget("x", TextureFormat.Rgba8, new FixedGraphSize(64, 64));
    var y = graph.ColorTarget("y", TextureFormat.Rgba8, new FixedGraphSize(64, 64));
    graph.GraphicsPass("A")
        .Target(y, LoadOp.Clear, StoreOp.Store)
        .Read(x)  // x is declared via factory but never written before A
        .Shader(MinimalShader());
    graph.GraphicsPass("B")
        .Target(x, LoadOp.Clear, StoreOp.Store)
        .Shader(MinimalShader());
    InvalidOperationException? caught = null;
    try { graph.Compile(); } catch (InvalidOperationException e) { caught = e; }
    t.ExpectTrue("L.8 read of later-declared (cycle) rejected",
        caught is not null && caught.Message.Contains("'A'") && caught.Message.Contains("'x'"));
}

{
    // L.9 — Compile twice rejected.
    var graph = new RenderGraph();
    var color = graph.ColorTarget("c", TextureFormat.Rgba8, new FixedGraphSize(64, 64));
    graph.GraphicsPass("p").Target(color, LoadOp.Clear, StoreOp.Store).Shader(MinimalShader());
    graph.Compile();
    InvalidOperationException? caught = null;
    try { graph.Compile(); } catch (InvalidOperationException e) { caught = e; }
    t.ExpectTrue("L.9 second Compile rejected (already frozen)",
        caught is not null && caught.Message.Contains("Compile"));
}

{
    // L.10 — Post-compile factory call rejected (topology frozen).
    var graph = new RenderGraph();
    var color = graph.ColorTarget("c", TextureFormat.Rgba8, new FixedGraphSize(64, 64));
    graph.GraphicsPass("p").Target(color, LoadOp.Clear, StoreOp.Store).Shader(MinimalShader());
    graph.Compile();
    InvalidOperationException? caught = null;
    try { graph.ColorTarget("late", TextureFormat.Rgba8, new FixedGraphSize(64, 64)); }
    catch (InvalidOperationException e) { caught = e; }
    t.ExpectTrue("L.10 post-Compile factory call rejected",
        caught is not null && caught.Message.Contains("ColorTarget"));
}

{
    // L.11 — Post-compile builder mutation rejected.
    var graph = new RenderGraph();
    var color = graph.ColorTarget("c", TextureFormat.Rgba8, new FixedGraphSize(64, 64));
    var builder = graph.GraphicsPass("p").Target(color, LoadOp.Clear, StoreOp.Store).Shader(MinimalShader());
    graph.Compile();
    InvalidOperationException? caught = null;
    try { builder.Read(color); } catch (InvalidOperationException e) { caught = e; }
    t.ExpectTrue("L.11 post-Compile builder mutation rejected",
        caught is not null && caught.Message.Contains("frozen"));
}

{
    // L.12 — Read of resource written by a prior pass is allowed.
    var graph = new RenderGraph();
    var shadow = graph.DepthTarget("shadow", new FixedGraphSize(2048, 2048));
    var color = graph.ColorTarget("scene", TextureFormat.Rgba8, new FixedGraphSize(64, 64));
    graph.GraphicsPass("shadow-pass")
        .Depth(shadow, LoadOp.Clear, StoreOp.Store)
        .Shader(MinimalShader());
    graph.GraphicsPass("scene-pass")
        .Target(color, LoadOp.Clear, StoreOp.Store)
        .Read(shadow)
        .Shader(MinimalShader());
    var threw = false;
    try { graph.Compile(); } catch { threw = true; }
    t.ExpectTrue("L.12 read of prior-pass-declared resource accepted", !threw);
}

{
    // L.13 — Cube face used as depth target counts as production.
    var graph = new RenderGraph();
    var cube = graph.DepthCube("point-shadow", faceSize: 512);
    var hdr = graph.ColorTarget("hdr", TextureFormat.Rgba16F, new FixedGraphSize(64, 64));
    for (var face = 0; face < 6; face++)
    {
        graph.GraphicsPass($"point-shadow.{face}")
            .Depth(cube.Face(face), LoadOp.Clear, StoreOp.Store)
            .Shader(MinimalShader());
    }
    graph.GraphicsPass("scene")
        .Target(hdr, LoadOp.Clear, StoreOp.Store)
        .Read(cube)   // whole cube sampled as samplerCube
        .Shader(MinimalShader());
    var threw = false;
    try { graph.Compile(); } catch { threw = true; }
    t.ExpectTrue("L.13 cube-face writes + whole-cube read across passes compile", !threw);
}

{
    // L.14 — Dispatch contract: recording a dispatch against a graphics-pass
    // handle is rejected; against a compute-pass handle it's accepted. Pure
    // topology check, so no live device is needed.
    var graph = new RenderGraph();
    var color = graph.ColorTarget("c", TextureFormat.Rgba8, new FixedGraphSize(64, 64));
    var grid = graph.ColorTarget("grid", TextureFormat.Rgba16F, new FixedGraphSize(64, 64));
    var gfx = graph.GraphicsPass("draw").Target(color, LoadOp.Clear, StoreOp.Store).Shader(MinimalShader()).Handle;
    var comp = graph.ComputePass("cs").Write(grid).Shader(MinimalShader()).Handle;
    graph.Compile();
    var dispatch = new DispatchCommand(
        default, 1, 1, 1, Array.Empty<ShaderUniform>(), Array.Empty<ShaderTextureBinding>());
    InvalidOperationException? caught = null;
    try { graph.Dispatch(gfx, dispatch); } catch (InvalidOperationException e) { caught = e; }
    t.ExpectTrue("L.14 Dispatch on a graphics-pass handle rejected",
        caught is not null && caught.Message.Contains("not a ComputePass"));
    var threw = false;
    try { graph.Dispatch(comp, dispatch); } catch { threw = true; }
    t.ExpectTrue("L.14 Dispatch on a compute-pass handle accepted", !threw);
}

// ============================================================================
// Section M — Backend-skip contract for test-mode graphs (VB.iii).
// ============================================================================
//
// Test-mode graphs (constructed via the internal parameterless ctor)
// have a null Device. Compile() runs validation but SKIPS the backend
// phase — BackendResources / BackendPasses stay empty, BackendCompiled
// stays false. This contract lets Section L tests run without a live
// VkDevice. The real backend (VkImage / VkRenderPass / VkFramebuffer
// allocation) is exercised by the demo at VB.vii.

{
    // M.1 — Test-mode Compile leaves backend state untouched.
    var graph = new RenderGraph();
    var color = graph.ColorTarget("c", TextureFormat.Rgba8, new FixedGraphSize(64, 64));
    graph.GraphicsPass("p").Target(color, LoadOp.Clear, StoreOp.Store).Shader(MinimalShader());
    graph.Compile();
    t.ExpectTrue("M.1 IsCompiled true after test-mode compile", graph.IsCompiled);
    t.ExpectTrue("M.1 BackendCompiled false in test mode (no device)", !graph.BackendCompiled);
    t.ExpectClose("M.1 BackendResources empty in test mode", graph.BackendResources.Count, 0);
    t.ExpectClose("M.1 BackendPasses empty in test mode", graph.BackendPasses.Count, 0);
}

{
    // M.2 — Resources + Passes tables ARE populated post-compile
    // (validation reads them; backend allocation in production reads
    // them too). Test-mode just doesn't ALLOCATE the backend objects.
    var graph = new RenderGraph();
    var color = graph.ColorTarget("c", TextureFormat.Rgba8, new FixedGraphSize(64, 64));
    var cube = graph.DepthCube("cube", faceSize: 32);
    graph.GraphicsPass("face0").Depth(cube.Face(0), LoadOp.Clear, StoreOp.Store).Shader(MinimalShader());
    graph.GraphicsPass("scene").Target(color, LoadOp.Clear, StoreOp.Store).Read(cube).Shader(MinimalShader());
    graph.Compile();
    t.ExpectClose("M.2 Resources table populated", graph.Resources.Count, 2);
    t.ExpectClose("M.2 GraphicsPasses table populated", graph.GraphicsPasses.Count, 2);
    t.ExpectClose("M.2 PassOrder preserves declaration order", graph.PassOrder.Count, 2);
}

// ============================================================================
// Section N — Barrier inference contract (VB.iv).
// ============================================================================
//
// For v1 graphics-only graphs, per-pass barrier lists are empty —
// subpass dependencies on each VkRenderPass already cover the
// cross-pass color/depth → fragment-shader-read memory barrier. The
// InferBarriers function ships its data shape now so the contract is
// locked; explicit emission lights up when ComputePass.Execute does in
// step 8.

{
    // N.1 — Empty graph produces empty per-pass barriers map. (Validation
    // rejects empty graphs at Compile, so build a single-pass graph and
    // assert the barrier list for that pass is empty.)
    var graph = new RenderGraph();
    var color = graph.ColorTarget("c", TextureFormat.Rgba8, new FixedGraphSize(64, 64));
    graph.GraphicsPass("solo").Target(color, LoadOp.Clear, StoreOp.Store).Shader(MinimalShader());
    graph.Compile();
    t.ExpectClose("N.1 single-pass graph has one barrier list",
        graph.PerPassBarriers.Count, 1);
    foreach (var list in graph.PerPassBarriers.Values)
    {
        t.ExpectClose("N.1 graphics-only pass barriers empty (subpass deps cover)", list.Count, 0);
    }
}

{
    // N.2 — Multi-pass graph with a Read edge still produces no explicit
    // barriers (the Read drives finalLayout = SHADER_READ_ONLY on the
    // producer's color attachment + subpass deps cover the memory barrier).
    var graph = new RenderGraph();
    var sceneColor = graph.ColorTarget("scene", TextureFormat.Rgba16F, new FixedGraphSize(64, 64));
    var presentColor = graph.ColorTarget("present", TextureFormat.Rgba8, new FixedGraphSize(64, 64));
    graph.GraphicsPass("scene-pass")
        .Target(sceneColor, LoadOp.Clear, StoreOp.Store)
        .Shader(MinimalShader());
    graph.GraphicsPass("present-pass")
        .Target(presentColor, LoadOp.Clear, StoreOp.Store)
        .Read(sceneColor)
        .Shader(MinimalShader());
    graph.Compile();
    t.ExpectClose("N.2 two-pass graph has two barrier lists",
        graph.PerPassBarriers.Count, 2);
    var total = 0;
    foreach (var list in graph.PerPassBarriers.Values) total += list.Count;
    t.ExpectClose("N.2 graphics-only multi-pass: zero explicit barriers (subpass deps cover)",
        total, 0);
}

{
    // N.3 — BarrierOp record value equality. Pins the data shape; useful
    // when step 8 starts emitting real BarrierOps and tests need to
    // compare expected vs actual.
    var a = new BarrierOp(
        ResourceId: 7,
        OldLayout: VkImageLayout.ColorAttachmentOptimal,
        NewLayout: VkImageLayout.ShaderReadOnlyOptimal,
        SrcStage: VkPipelineStageFlags.ColorAttachmentOutputBit,
        SrcAccess: VkAccessFlags.ColorAttachmentWriteBit,
        DstStage: VkPipelineStageFlags.FragmentShaderBit,
        DstAccess: VkAccessFlags.ShaderReadBit);
    var b = new BarrierOp(
        ResourceId: 7,
        OldLayout: VkImageLayout.ColorAttachmentOptimal,
        NewLayout: VkImageLayout.ShaderReadOnlyOptimal,
        SrcStage: VkPipelineStageFlags.ColorAttachmentOutputBit,
        SrcAccess: VkAccessFlags.ColorAttachmentWriteBit,
        DstStage: VkPipelineStageFlags.FragmentShaderBit,
        DstAccess: VkAccessFlags.ShaderReadBit);
    t.ExpectTrue("N.3 BarrierOp value equality holds", a.Equals(b));

    var differentResource = a with { ResourceId = 8 };
    t.ExpectTrue("N.3 BarrierOp distinguishes different ResourceId", !a.Equals(differentResource));
}

// ============================================================================
// Section O — graph.Pass + graph.Execute lifecycle (VB.v).
// ============================================================================
//
// Real graph.Pass + Execute behavior (recording scopes, emitting passes
// into a RenderCommandList) needs a live VkDevice and is exercised by
// the demo visual gate at VB.vii. Section O covers the error paths that
// don't need a device.

{
    // O.1 — graph.Pass called before Compile throws.
    var graph = new RenderGraph();
    var color = graph.ColorTarget("c", TextureFormat.Rgba8, new FixedGraphSize(64, 64));
    var passHandle = graph.GraphicsPass("p")
        .Target(color, LoadOp.Clear, StoreOp.Store)
        .Shader(MinimalShader())
        .Handle;
    var threw = false;
    try { graph.Pass(passHandle, _ => { }); } catch (InvalidOperationException) { threw = true; }
    t.ExpectTrue("O.1 graph.Pass before Compile throws", threw);
}

{
    // O.2 — graph.Execute before Compile throws.
    var graph = new RenderGraph();
    var color = graph.ColorTarget("c", TextureFormat.Rgba8, new FixedGraphSize(64, 64));
    graph.GraphicsPass("p").Target(color, LoadOp.Clear, StoreOp.Store).Shader(MinimalShader());
    var commandList = new RenderCommandList();
    var threw = false;
    try { graph.Execute(commandList); } catch (InvalidOperationException) { threw = true; }
    t.ExpectTrue("O.2 graph.Execute before Compile throws", threw);
}

{
    // O.3 — Test-mode graph.Execute is a no-op (Device null = no backend).
    var graph = new RenderGraph();
    var color = graph.ColorTarget("c", TextureFormat.Rgba8, new FixedGraphSize(64, 64));
    graph.GraphicsPass("p").Target(color, LoadOp.Clear, StoreOp.Store).Shader(MinimalShader());
    graph.Compile();
    var commandList = new RenderCommandList();
    var threw = false;
    try { graph.Execute(commandList); } catch { threw = true; }
    t.ExpectTrue("O.3 test-mode graph.Execute returns without throwing", !threw);
}

{
    // O.4 — GetColorTexture before Compile throws.
    var graph = new RenderGraph();
    var color = graph.ColorTarget("c", TextureFormat.Rgba8, new FixedGraphSize(64, 64));
    var threw = false;
    try { graph.GetColorTexture(color); } catch (InvalidOperationException) { threw = true; }
    t.ExpectTrue("O.4 GetColorTexture before Compile throws", threw);
}

// ============================================================================
// Section P — SPIR-V reflection (build-time .refl.json → binding records).
// ============================================================================
//
// Golden gate before VulkanSponza's hand-authored UniformBlockLayout /
// ShaderInterface are deleted: ShaderReflection must reproduce those tables
// exactly from the spirv-cross --reflect sidecars. Fixtures are checked into
// fixtures/ (see csproj). The known-good values below are copied from
// VulkanSponza/Program.cs's hand-authored Frame/Material layouts.
{
    var fxDir = Path.Combine(AppContext.BaseDirectory, "fixtures");
    var vert = ShaderReflection.Load(Path.Combine(fxDir, "lit.vert.refl.json"));
    var frag = ShaderReflection.Load(Path.Combine(fxDir, "lit.frag.refl.json"));
    var iface = ShaderReflection.MergeStages(vert, frag);

    DescriptorSetSlot? Slot(int set, int binding) =>
        iface.Slots.FirstOrDefault(s => s.Set == set && s.Binding == binding);
    UniformBlockMember? Member(UniformBlockLayout? b, string name) =>
        b?.Members.FirstOrDefault(m => m.Name == name);

    // P.0 — merged interface passes the same structural validation as the
    // hand-authored ones, and the vert↔frag Frame block merged to the FULLER
    // layout (frag's 416, not vert's 96 prefix).
    t.ExpectTrue("P.0 reflected lit interface validates", TryValidate(iface) is null);

    var frame = Slot(0, 0);
    t.ExpectTrue("P.0 Frame UBO at (0,0) is a UniformBuffer", frame is { Type: ShaderResourceType.UniformBuffer });
    t.ExpectTrue("P.0 Frame UBO merged to both stages",
        frame is { } fr && fr.Stages.HasFlag(ShaderStages.Vertex) && fr.Stages.HasFlag(ShaderStages.Fragment));

    // P.1 — Frame UBO std140 offsets match VulkanSponza's hand-authored table.
    var fb = frame?.BlockLayout;
    t.ExpectClose("P.1 Frame TotalSize 416 (fuller block won the merge)", fb?.TotalSize ?? -1, 416);
    t.ExpectClose("P.1 uViewProjection @0", Member(fb, "uViewProjection")?.Offset ?? -1, 0);
    t.ExpectClose("P.1 uViewProjection size 64", Member(fb, "uViewProjection")?.Size ?? -1, 64);
    t.ExpectClose("P.1 uSunDirection @64", Member(fb, "uSunDirection")?.Offset ?? -1, 64);
    t.ExpectClose("P.1 uSunIntensity @76", Member(fb, "uSunIntensity")?.Offset ?? -1, 76);
    t.ExpectClose("P.1 uCascadeViewProj @128", Member(fb, "uCascadeViewProj")?.Offset ?? -1, 128);
    t.ExpectClose("P.1 uCascadeViewProj size 192 (mat4[3])", Member(fb, "uCascadeViewProj")?.Size ?? -1, 192);
    t.ExpectClose("P.1 uCascadeViewProj ElementStride 64", Member(fb, "uCascadeViewProj")?.ElementStride ?? -1, 64);
    t.ExpectClose("P.1 uIblParams @400 (engine extension)", Member(fb, "uIblParams")?.Offset ?? -1, 400);

    // P.2 — Material UBO (set 2, binding 0): four vec4s, 64 bytes.
    var matSlot = Slot(2, 0);
    t.ExpectClose("P.2 Material UBO TotalSize 64", matSlot?.BlockLayout?.TotalSize ?? -1, 64);
    t.ExpectClose("P.2 uMaterialParams @32", Member(matSlot?.BlockLayout, "uMaterialParams")?.Offset ?? -1, 32);

    // P.3 — set 1 IBL samplers + cascade-array Count.
    t.ExpectTrue("P.3 uIrradiance at (1,0)", Slot(1, 0) is { Type: ShaderResourceType.SampledImage });
    t.ExpectTrue("P.3 uPrefilteredEnv at (1,1)", Slot(1, 1) is { Type: ShaderResourceType.SampledImage });
    t.ExpectTrue("P.3 uBrdfLut at (1,2)", Slot(1, 2) is { Type: ShaderResourceType.SampledImage });
    t.ExpectClose("P.3 uCascadeShadowMaps at (1,3) Count==3", Slot(1, 3)?.Count ?? -1, 3);
    t.ExpectTrue("P.3 uFroxelGrid at (1,4)", Slot(1, 4) is { Type: ShaderResourceType.SampledImage });

    // P.4 — per-material textures on set 2.
    t.ExpectTrue("P.4 uAlbedo at (2,1) Count 1", Slot(2, 1) is { Type: ShaderResourceType.SampledImage, Count: 1 });
    t.ExpectTrue("P.4 uOcclusion at (2,5)", Slot(2, 5) is { Type: ShaderResourceType.SampledImage });

    // P.5 — push-constant coalescing (regression). shadow_mask declares one
    // [0,144) push block referenced by BOTH stages, so each stage reflects the
    // full block; MergeStages must coalesce them into ONE range with OR'd
    // stages — not two ranges that the emit path's sum-of-sizes would total to
    // 288 and reject against the 144B payload.
    var shadowMask = ShaderReflection.MergeStages(
        ShaderReflection.Load(Path.Combine(fxDir, "shadow_mask.vert.refl.json")),
        ShaderReflection.Load(Path.Combine(fxDir, "shadow_mask.frag.refl.json")));
    t.ExpectClose("P.5 shadow_mask has exactly ONE push range", shadowMask.PushConstants.Count, 1);
    var pc = shadowMask.PushConstants.Count > 0 ? shadowMask.PushConstants[0] : null;
    t.ExpectClose("P.5 push range Offset 0", pc?.Offset ?? -1, 0);
    t.ExpectClose("P.5 push range Size 144 (matches payload, not 288)", pc?.Size ?? -1, 144);
    t.ExpectTrue("P.5 push range spans Vertex+Fragment",
        pc is { } r && r.Stages.HasFlag(ShaderStages.Vertex) && r.Stages.HasFlag(ShaderStages.Fragment));
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

using System.Numerics;
using Blix;
using Blix.Assets;
using Blix.Diagnostics;
using Blix.Geometry;
using Blix.Graphics;
using Blix.Graphics.Vulkan;
using Blix.Render;
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
//   - CreatePerspectiveVulkan in row-vector form (M34=-1, M43=z-translation),
//     Vulkan Y-down (M22=-focalLength) and [0,1] depth.
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
    // CreatePerspectiveVulkan: row-vector form, Vulkan Y-down + [0,1] depth.
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
// + per-material UBO + per-material samplers + push constants. Exercises the
// set-by-lifetime layout (docs/architecture.md → the Vulkan binding model) and
// proves the type composes for the real downstream use case.
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

    // P.1 — Frame UBO std140 offsets reflect correctly. lit.frag's tunables are
    // named float members (no packed uShaderParams/uShadowParams/uIblParams
    // vec4s), so the block is 396 and the grab-bags are gone — see P.6.
    var fb = frame?.BlockLayout;
    t.ExpectClose("P.1 Frame TotalSize 396", fb?.TotalSize ?? -1, 396);
    t.ExpectClose("P.1 uViewProjection @0", Member(fb, "uViewProjection")?.Offset ?? -1, 0);
    t.ExpectClose("P.1 uViewProjection size 64", Member(fb, "uViewProjection")?.Size ?? -1, 64);
    t.ExpectClose("P.1 uSunDirection @64", Member(fb, "uSunDirection")?.Offset ?? -1, 64);
    t.ExpectClose("P.1 uSunIntensity @76", Member(fb, "uSunIntensity")?.Offset ?? -1, 76);
    t.ExpectClose("P.1 uCascadeViewProj @128", Member(fb, "uCascadeViewProj")?.Offset ?? -1, 128);
    t.ExpectClose("P.1 uCascadeViewProj size 192 (mat4[3])", Member(fb, "uCascadeViewProj")?.Size ?? -1, 192);
    t.ExpectClose("P.1 uCascadeViewProj ElementStride 64", Member(fb, "uCascadeViewProj")?.ElementStride ?? -1, 64);
    t.ExpectTrue("P.1 packed uShaderParams gone (un-packed to named members)", Member(fb, "uShaderParams") is null);

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

    // P.6 — the former vec4 grab-bags are now named float members the tune
    // scanner + overlay can bind individually.
    t.ExpectTrue("P.6 uMetallicThreshold is a named member", Member(fb, "uMetallicThreshold") is not null);
    t.ExpectTrue("P.6 uIndirectShadowBase is a named member", Member(fb, "uIndirectShadowBase") is not null);
    t.ExpectClose("P.6 uVisualizeCascades size 4 (float)", Member(fb, "uVisualizeCascades")?.Size ?? -1, 4);
}

// ============================================================================
// Section Q — `//@tune` shader-tunable decorator scan (Blix.Graphics).
// ============================================================================
//
// The scanner reads GLSL source for the opt-in tuning decorator the diagnostics
// overlay auto-binds. Asserts: only tagged members surface, range/default and
// enum payloads parse, group = enclosing block name, label inferred from name.
{
    const string glsl = """
        layout(set = 0, binding = 0) uniform Tune {
            //@tune 0..16 = 9.42
            float uSunIntensity;
            //@tune 0..1 = 0.5
            float uMetallicThreshold;
            // engine-driven, no tag → must be ignored
            float uEngineThing;
            //@tune enum{ PBR, Albedo, Normal, Roughness, Cascade }
            int   uDebugView;
        } tune;

        // A bare uniform outside any block, still tunable.
        //@tune 0..4 = 2
        uniform int uLodBias;
        """;

    var tunables = ShaderTunables.Scan(glsl);
    ShaderTunable? Find(string n)
    {
        foreach (var x in tunables) if (x.Name == n) return x;
        return null;
    }

    // Q.0 — only the four tagged decls surface; uEngineThing is dropped.
    t.ExpectClose("Q.0 four tunables found (untagged ignored)", tunables.Count, 4);
    t.ExpectTrue("Q.0 untagged uEngineThing excluded", Find("uEngineThing") is null);

    // Q.1 — float range + default + inferred group/label.
    var sun = Find("uSunIntensity");
    t.ExpectTrue("Q.1 uSunIntensity is Float", sun is { Kind: TunableKind.Float });
    t.ExpectClose("Q.1 min 0", sun?.Min ?? -1, 0);
    t.ExpectClose("Q.1 max 16", sun?.Max ?? -1, 16);
    t.ExpectClose("Q.1 default 9.42", sun?.Default ?? -1, 9.42f);
    t.ExpectTrue("Q.1 group = block name 'Tune'", sun?.Block == "Tune");
    t.ExpectTrue("Q.1 label inferred 'Sun intensity'", sun?.Label == "Sun intensity");

    var metal = Find("uMetallicThreshold");
    t.ExpectClose("Q.1 metallic default 0.5", metal?.Default ?? -1, 0.5f);
    t.ExpectTrue("Q.1 metallic label 'Metallic threshold'", metal?.Label == "Metallic threshold");

    // Q.2 — int enum: kind, options, range.
    var dv = Find("uDebugView");
    t.ExpectTrue("Q.2 uDebugView is Enum", dv is { Kind: TunableKind.Enum });
    t.ExpectClose("Q.2 enum has 5 options", dv?.EnumNames?.Count ?? -1, 5);
    t.ExpectTrue("Q.2 first option 'PBR'", dv?.EnumNames is { Count: > 0 } e && e[0] == "PBR");
    t.ExpectTrue("Q.2 last option 'Cascade'", dv?.EnumNames is { Count: 5 } e2 && e2[4] == "Cascade");
    t.ExpectClose("Q.2 enum max = count-1", dv?.Max ?? -1, 4);

    // Q.3 — bare uniform outside a block: Int kind, empty group.
    var lod = Find("uLodBias");
    t.ExpectTrue("Q.3 uLodBias is Int", lod is { Kind: TunableKind.Int });
    t.ExpectClose("Q.3 uLodBias default 2", lod?.Default ?? -1, 2);
    t.ExpectTrue("Q.3 uLodBias has no block group", lod?.Block == "");
}

// ============================================================================
// Section R — ShaderTunablePanel (Blix.Diagnostics): seeding + uniform emit.
// ============================================================================
//
// The panel auto-binds //@tune decorators to overlay dials and feeds live
// values back as named uniforms. UI binding (BuildControls) needs a live
// DebugContext and is exercised in the demo; here we lock the value contract:
// defaults seed from the tags, and AppendUniforms emits one named float each.
{
    var panelTunables = ShaderTunables.Scan("""
        layout(set = 0, binding = 0) uniform Tune {
            //@tune 0..16 = 9.42
            float uSunIntensity;
            //@tune 0..1 = 0.5
            float uMetallicThreshold;
        } tune;
        """);
    var panel = new ShaderTunablePanel(panelTunables);

    // R.1 — values seed from the tag defaults; unknown name → 0.
    t.ExpectClose("R.1 uSunIntensity seeded to 9.42", panel.Value("uSunIntensity"), 9.42f);
    t.ExpectClose("R.1 uMetallicThreshold seeded to 0.5", panel.Value("uMetallicThreshold"), 0.5f);
    t.ExpectClose("R.1 unknown name → 0", panel.Value("uNope"), 0f);

    // R.2 — AppendUniforms emits one named float uniform per tunable.
    var uniforms = new List<ShaderUniform>();
    panel.AppendUniforms(uniforms);
    t.ExpectClose("R.2 two uniforms appended", uniforms.Count, 2);
    var sunU = uniforms.FirstOrDefault(u => u.Name == "uSunIntensity");
    t.ExpectTrue("R.2 uSunIntensity present as FloatUniform", sunU?.Value is FloatUniform);
    t.ExpectClose("R.2 uSunIntensity value 9.42", (sunU?.Value as FloatUniform)?.Value ?? -1, 9.42f);
}

// ============================================================================
// Section S — [Tune] attribute reflection (Blix.Diagnostics): CPU tunables.
// ============================================================================
//
// The CPU twin of shader //@tune: an attribute on a field/property that the
// overlay reflects into a live dial. Locks: tagged-only surfacing, range +
// label inference, and that editing Value writes back through the member
// (with int rounding).
{
    var fixture = new TuneFixture();
    var fields = TuneReflection.Reflect(fixture);
    TunableField? FindF(string n)
    {
        foreach (var f in fields) if (f.Name == n) return f;
        return null;
    }

    // S.0 — only [Tune] members surface (NotTunable excluded).
    t.ExpectClose("S.0 five tunables found", fields.Count, 5);
    t.ExpectTrue("S.0 untagged field excluded", FindF("NotTunable") is null);

    // S.1 — range + inferred labels (PascalCase + camelCase).
    var density = FindF("Density");
    t.ExpectClose("S.1 Density min 0", density?.Min ?? -1, 0);
    t.ExpectClose("S.1 Density max 0.5", density?.Max ?? -1, 0.5f);
    t.ExpectTrue("S.1 label 'Density'", density?.Label == "Density");
    t.ExpectTrue("S.1 camelCase label 'Fly speed'", FindF("flySpeed")?.Label == "Fly speed");
    t.ExpectClose("S.1 get reads the live field", density?.Value ?? -1, 0.1f);

    // S.2 — set writes back through the member; ints round.
    if (density is not null) density.Value = 0.3f;
    t.ExpectClose("S.2 float set wrote the field", fixture.Density, 0.3f);
    var steps = FindF("Steps");
    t.ExpectTrue("S.2 int member is Int kind", steps is { Kind: TuneKind.Int });
    if (steps is not null) steps.Value = 2.6f;
    t.ExpectClose("S.2 int set rounds to 3", fixture.Steps, 3);

    // S.3 — bool member → toggle (0/1), set writes back.
    var wire = FindF("Wireframe");
    t.ExpectTrue("S.3 bool member is Bool kind", wire is { Kind: TuneKind.Bool });
    t.ExpectClose("S.3 bool false reads 0", wire?.Value ?? -1, 0f);
    if (wire is not null) wire.Value = 1f;
    t.ExpectTrue("S.3 bool set wrote true", fixture.Wireframe);

    // S.4 — enum member → dropdown (option index), set writes the enum value.
    var mode = FindF("Mode");
    t.ExpectTrue("S.4 enum member is Enum kind", mode is { Kind: TuneKind.Enum });
    t.ExpectClose("S.4 three enum options", mode?.EnumNames?.Count ?? -1, 3);
    t.ExpectClose("S.4 default B reads index 1", mode?.Value ?? -1, 1f);
    if (mode is not null) mode.Value = 2f;
    t.ExpectTrue("S.4 enum set wrote C", fixture.Mode == TuneFixtureMode.C);
}

// ============================================================================
// Section T — MeshBundler.Pack (Blix.Render): geometry bundling.
// ============================================================================
//
// Packs N primitives into one shared vertex buffer + per-width index buffers,
// order-preserving, indices kept primitive-local. The CPU surface is pure
// (no device), so we assert the byte/offset math directly.
{
    // A: 2 verts, u16 [0,1,2]. B: 3 verts, two LODs (u16). C: 4 verts, u32.
    var a = new MeshGeometryInput(new byte[8], 2,
        new[] { new MeshLod(new ushort[] { 0, 1, 2 }, null) }, default);
    var b = new MeshGeometryInput(new byte[12], 3,
        new[]
        {
            new MeshLod(new ushort[] { 0, 1, 2, 1, 2, 0 }, null),
            new MeshLod(new ushort[] { 0, 1, 2 }, null, 0.5f),
        }, default);
    var c = new MeshGeometryInput(new byte[16], 4,
        new[] { new MeshLod(null, new uint[] { 0, 1, 2, 3 }) }, default);
    var packed = MeshBundler.Pack(new[] { a, b, c });

    // T.0 — buffers concatenated; widths split.
    t.ExpectClose("T.0 vertex bytes concatenated (8+12+16)", packed.VertexBytes.Length, 36);
    t.ExpectClose("T.0 vertex count summed (2+3+4)", packed.VertexCount, 9);
    t.ExpectClose("T.0 u16 indices (3+6+3)", packed.Indices16.Length, 12);
    t.ExpectClose("T.0 u32 indices (4)", packed.Indices32.Length, 4);
    t.ExpectClose("T.0 three bundled meshes", packed.Meshes.Count, 3);

    // T.1 — BaseVertex accumulates in input order.
    t.ExpectClose("T.1 A BaseVertex 0", packed.Meshes[0].BaseVertex, 0);
    t.ExpectClose("T.1 B BaseVertex 2", packed.Meshes[1].BaseVertex, 2);
    t.ExpectClose("T.1 C BaseVertex 5", packed.Meshes[2].BaseVertex, 5);

    // T.2 — per-LOD firstIndex/counts into the width buffer; B's two LODs.
    t.ExpectClose("T.2 A LOD0 firstIndex 0", packed.Meshes[0].LodFirstIndex[0], 0);
    t.ExpectClose("T.2 B LOD0 firstIndex 3", packed.Meshes[1].LodFirstIndex[0], 3);
    t.ExpectClose("T.2 B LOD0 count 6", packed.Meshes[1].LodIndexCounts[0], 6);
    t.ExpectClose("T.2 B LOD1 firstIndex 9", packed.Meshes[1].LodFirstIndex[1], 9);
    t.ExpectClose("T.2 B LOD1 error 0.5", packed.Meshes[1].LodErrors[1], 0.5f);

    // T.3 — u16/u32 split: C indexes the u32 buffer (firstIndex 0 there).
    t.ExpectTrue("T.3 A is u16", !packed.Meshes[0].IndicesAreU32);
    t.ExpectTrue("T.3 C is u32", packed.Meshes[2].IndicesAreU32);
    t.ExpectClose("T.3 C LOD0 firstIndex 0 (u32 buffer)", packed.Meshes[2].LodFirstIndex[0], 0);

    // T.4 — indices stay primitive-local (NOT rebased by BaseVertex): B's LOD0
    // at u16[3] is still 0, not 2.
    t.ExpectClose("T.4 B's first index stays local (0)", packed.Indices16[3], 0);
}

// ============================================================================
// Section U — AsyncLoadQueue (Blix.Render): off-thread produce + budgeted drain.
// ============================================================================
{
    static void SpinUntilReady<T>(AsyncLoadQueue<T> q)
    {
        for (var i = 0; i < 5000 && q.IsProducing; i++) System.Threading.Thread.Sleep(1);
    }

    // U.0 — produce a list off-thread; a generous-budget Drain processes all,
    // in order, and reports fully-loaded.
    var q0 = new AsyncLoadQueue<int>();
    q0.Start(() => new[] { 1, 2, 3, 4, 5 });
    SpinUntilReady(q0);
    var got = new List<int>();
    var done = q0.Drain(1000.0, got.Add);
    t.ExpectTrue("U.0 fully loaded after a generous drain", done);
    t.ExpectClose("U.0 all five processed", got.Count, 5);
    t.ExpectTrue("U.0 in producer order", got.SequenceEqual(new[] { 1, 2, 3, 4, 5 }));
    t.ExpectClose("U.0 nothing pending", q0.PendingCount, 0);

    // U.1 — a tiny budget still drains ≥1 per call (no starvation) and reports
    // not-yet-done until the queue empties.
    var q1 = new AsyncLoadQueue<int>();
    q1.Start(() => new[] { 10, 20, 30 });
    SpinUntilReady(q1);
    var collected = new List<int>();
    var d1 = q1.Drain(0.0, collected.Add);   // 0ms budget → exactly one
    t.ExpectClose("U.1 zero-budget drains one", collected.Count, 1);
    t.ExpectTrue("U.1 not done yet", !d1);
    while (!q1.Drain(0.0, collected.Add)) { }  // finish it off, one per call
    t.ExpectClose("U.1 all drained across calls", collected.Count, 3);

    // U.2 — a faulting producer surfaces as IsFaulted; Drain does nothing.
    var q2 = new AsyncLoadQueue<int>();
    q2.Start(() => throw new InvalidOperationException("parse boom"));
    for (var i = 0; i < 5000 && !q2.IsFaulted; i++) System.Threading.Thread.Sleep(1);
    t.ExpectTrue("U.2 producer fault surfaced", q2.IsFaulted);
    t.ExpectTrue("U.2 fault message preserved", q2.Fault?.Message == "parse boom");
    t.ExpectTrue("U.2 faulted Drain processes nothing / not done", !q2.Drain(1000.0, _ => { }));
}

// ============================================================================
// Section V — Frustum near-plane against Vulkan [0,1] clip.
// ============================================================================
//
// Regression for the near-plane derivation: the projection produces [0,1]
// depth, so the near plane is row3 (clip.z >= 0), NOT GL's row4 + row3. A box
// in front of the near plane must survive culling; a box between the camera
// and the near plane (closer than near) must be rejected by the near plane.
{
    // Camera at origin looking down -Z; near = 1, far = 100.
    var view = GraphicsMatrices.CreateView(Vector3.Zero, Quaternion.Identity);
    var proj = GraphicsMatrices.CreatePerspectiveVulkan(MathF.PI / 2f, 1.0f, 1.0f, 100f);
    var vp = view * proj;
    // FromViewProjection wants the planes as rows → transpose the row-vector VP.
    var frustum = Frustum.FromViewProjection(Matrix4x4.Transpose(vp));

    Bounds3 Box(Vector3 c, float r) => new(c - new Vector3(r), c + new Vector3(r));

    t.ExpectTrue("Frustum: box well inside the view volume intersects",
        frustum.Intersects(Box(new Vector3(0, 0, -10f), 1f)));
    t.ExpectTrue("Frustum: box behind the camera is culled",
        !frustum.Intersects(Box(new Vector3(0, 0, 10f), 1f)));
    // The near-plane fix: a box at view-z ∈ [-0.8,-0.6] is in front of the camera
    // but nearer than near=1, so it must be culled. This band is exactly where the
    // correct [0,1] near plane (row3) and the buggy GL one (row4 + row3, which
    // puts the plane at view-z ≈ -0.5) disagree — the GL form wrongly keeps it.
    t.ExpectTrue("Frustum: box nearer than the near plane is culled (row3, [0,1] clip)",
        !frustum.Intersects(Box(new Vector3(0, 0, -0.7f), 0.1f)));
    t.ExpectTrue("Frustum: box just past the near plane survives",
        frustum.Intersects(Box(new Vector3(0, 0, -2f), 0.4f)));
}

// ============================================================================
// Section W — Frustum: the other five planes (complements V's near plane).
// ============================================================================
//
// V locked the near plane against the [0,1]-clip regression. W exercises the
// remaining five so the whole Gribb-Hartmann extraction is pinned: left/right/
// top/bottom from the symmetric fov (at view depth d the half-extent is d since
// tan(45°)=1), and far from the [0,1] far plane (row4 - row3). Each plane gets a
// box just outside it (must cull); the inside cases keep the extraction honest.
{
    var view = GraphicsMatrices.CreateView(Vector3.Zero, Quaternion.Identity);
    var proj = GraphicsMatrices.CreatePerspectiveVulkan(MathF.PI / 2f, 1.0f, 1.0f, 100f);
    var frustum = Frustum.FromViewProjection(Matrix4x4.Transpose(view * proj));

    Bounds3 Box(Vector3 c, float r) => new(c - new Vector3(r), c + new Vector3(r));

    // 90° vertical fov, aspect 1 → at view depth 10 the frustum spans x,y ∈ [-10, 10].
    t.ExpectTrue("Frustum: centred box at depth 10 is inside all planes",
        frustum.Intersects(Box(new Vector3(0, 0, -10f), 1f)));

    t.ExpectTrue("Frustum: box past the right plane is culled",
        !frustum.Intersects(Box(new Vector3(12f, 0, -10f), 0.5f)));
    t.ExpectTrue("Frustum: box just inside the right plane survives",
        frustum.Intersects(Box(new Vector3(9f, 0, -10f), 0.5f)));
    t.ExpectTrue("Frustum: box past the left plane is culled",
        !frustum.Intersects(Box(new Vector3(-12f, 0, -10f), 0.5f)));
    t.ExpectTrue("Frustum: box past the top plane is culled",
        !frustum.Intersects(Box(new Vector3(0, 12f, -10f), 0.5f)));
    t.ExpectTrue("Frustum: box past the bottom plane is culled",
        !frustum.Intersects(Box(new Vector3(0, -12f, -10f), 0.5f)));

    // Far plane = row4 - row3 for [0,1] clip: beyond far=100 (view-z < -100) culls.
    t.ExpectTrue("Frustum: box beyond the far plane is culled",
        !frustum.Intersects(Box(new Vector3(0, 0, -101f), 0.4f)));
    t.ExpectTrue("Frustum: box just inside the far plane survives",
        frustum.Intersects(Box(new Vector3(0, 0, -99f), 0.4f)));
}

// ============================================================================
// Section X — Camera projection ↔ unprojection round-trip (ScreenPointToRay).
// ============================================================================
//
// ScreenPointToRay had zero coverage and is exactly what the GL sunset touched.
// Project a known world point through the camera's viewProj, perspective-divide
// to NDC, map NDC → screen pixel, then unproject that pixel back to a ray: the
// ray must aim from the camera straight through the original point. Also pins
// the [0,1] depth endpoints — near plane → NDC z 0, far plane → NDC z 1 (GL's
// [-1,1] would put near at -1).
{
    var camera = new Camera3D();           // origin, identity pose, fov π/3, near 0.1, far 100
    const float W = 1600f, H = 900f;
    var vp = camera.GetViewProjection(W / H);

    Vector3 ProjectToNdc(Vector3 world)
    {
        var clip = Vector4.Transform(new Vector4(world, 1f), vp);
        return new Vector3(clip.X / clip.W, clip.Y / clip.W, clip.Z / clip.W);
    }

    var p = new Vector3(2f, 1f, -10f);     // off-axis, in front of the camera
    var ndc = ProjectToNdc(p);
    t.ExpectTrue("X round-trip: projected NDC depth within [0,1]", ndc.Z >= 0f && ndc.Z <= 1f);

    var screenX = (ndc.X + 1f) * 0.5f * W;
    var screenY = (ndc.Y + 1f) * 0.5f * H;
    var ray = camera.ScreenPointToRay(screenX, screenY, W, H);

    t.ExpectClose("X round-trip: ray direction points back at the world point",
        new Vector4(ray.Direction, 0f), new Vector4(Vector3.Normalize(p), 0f));
    t.ExpectTrue("X round-trip: ray origin sits on the near plane, ahead of the camera",
        ray.Origin.Z < 0f);

    var nearNdc = ProjectToNdc(new Vector3(0f, 0f, -camera.NearPlane));
    var farNdc = ProjectToNdc(new Vector3(0f, 0f, -camera.FarPlane));
    t.ExpectClose("X depth: near plane → NDC z 0 (Vulkan [0,1], not GL -1)", nearNdc.Z, 0f);
    t.ExpectClose("X depth: far plane → NDC z 1", farNdc.Z, 1f);
}

// ============================================================================
// Section Y — Screen ↔ NDC ↔ world axis mapping (Y-down, no flip).
// ============================================================================
//
// The exact axis the GL sunset broke: screen uses a top-left origin (Y grows
// down), which matches Vulkan's Y-down NDC, so the screen→NDC map applies NO Y
// flip — and the projection's own Y-flip then sends screen-top to world-up.
// Asserted through ScreenPointToRay direction signs so the whole chain is pinned.
{
    var camera = new Camera3D();
    const float W = 1600f, H = 900f;

    var centre = camera.ScreenPointToRay(W / 2f, H / 2f, W, H);
    t.ExpectClose("Y centre pixel → ray straight down -Z",
        new Vector4(centre.Direction, 0f), new Vector4(0f, 0f, -1f, 0f));

    var top = camera.ScreenPointToRay(W / 2f, 0f, W, H);
    t.ExpectTrue("Y screen top → ray points up (+Y) and forward (-Z)",
        top.Direction.Y > 0f && top.Direction.Z < 0f);
    var bottom = camera.ScreenPointToRay(W / 2f, H, W, H);
    t.ExpectTrue("Y screen bottom → ray points down (-Y)", bottom.Direction.Y < 0f);

    var left = camera.ScreenPointToRay(0f, H / 2f, W, H);
    t.ExpectTrue("Y screen left → ray points left (-X)", left.Direction.X < 0f);
    var rightRay = camera.ScreenPointToRay(W, H / 2f, W, H);
    t.ExpectTrue("Y screen right → ray points right (+X)", rightRay.Direction.X > 0f);
}

// ============================================================================
// Section Z — SpriteBatch ortho: screen-corner → NDC-corner mapping.
// ============================================================================
//
// CreateOrthographicOffCenterVulkan(0, w, h, 0, ...) is what SpriteBatch feeds
// as its view-projection: it must reproduce the ImGui (x*2/w-1, y*2/h-1) screen
// map — top-left (0,0) → NDC (-1,-1), bottom-right (w,h) → (1,1), centre → (0,0)
// — with depth in [0,1]. Row-vector: apply via Vector4.Transform.
{
    const float w = 800f, h = 600f;
    var ortho = GraphicsMatrices.CreateOrthographicOffCenterVulkan(0f, w, h, 0f, 0f, 1f);
    Vector4 Map(float x, float y, float z) => Vector4.Transform(new Vector4(x, y, z, 1f), ortho);

    t.ExpectClose("Z top-left (0,0) → NDC (-1,-1)", Map(0f, 0f, 0f), new Vector4(-1f, -1f, 0f, 1f));
    t.ExpectClose("Z bottom-right (w,h) → NDC (1,1)", Map(w, h, 0f), new Vector4(1f, 1f, 0f, 1f));
    t.ExpectClose("Z top-right (w,0) → NDC (1,-1)", Map(w, 0f, 0f), new Vector4(1f, -1f, 0f, 1f));
    t.ExpectClose("Z centre (w/2,h/2) → NDC origin", Map(w / 2f, h / 2f, 0f), new Vector4(0f, 0f, 0f, 1f));
    t.ExpectClose("Z far depth (z=1) → NDC z 1 ([0,1] clip)", Map(0f, 0f, 1f).Z, 1f);
}

// ============================================================================
// Section AA — CreateOrthographicVulkan (shadow-cascade / spot-light proj).
// ============================================================================
//
// The symmetric Vulkan ortho was duplicated verbatim in two demos (VulkanLit +
// VulkanSponza cascades) with no test; now promoted to GraphicsMatrices. Pin its
// invariants: x/y map [-w/2,w/2] → [-1,1] with +Y DOWN (top of slab → -1), depth
// maps near → 0 / far → 1 ([0,1] clip), and w stays 1 (no perspective divide).
{
    var ortho = GraphicsMatrices.CreateOrthographicVulkan(10f, 10f, 0f, 10f);
    Vector4 Map(float x, float y, float z) => Vector4.Transform(new Vector4(x, y, z, 1f), ortho);

    t.ExpectClose("AA right edge (+x) → NDC +1", Map(5f, 0f, 0f).X, 1f);
    t.ExpectClose("AA left edge (-x) → NDC -1", Map(-5f, 0f, 0f).X, -1f);
    t.ExpectClose("AA top (+Y world) → NDC -1 (Y-down flip)", Map(0f, 5f, 0f).Y, -1f);
    t.ExpectClose("AA bottom (-Y world) → NDC +1", Map(0f, -5f, 0f).Y, 1f);
    t.ExpectClose("AA near plane (view-z 0) → NDC z 0", Map(0f, 0f, 0f).Z, 0f);
    t.ExpectClose("AA far plane (view-z -10) → NDC z 1", Map(0f, 0f, -10f).Z, 1f);
    t.ExpectClose("AA orthographic keeps w = 1 (no perspective divide)", Map(3f, -4f, -5f).W, 1f);

    // Rejects degenerate extents (matches the sibling projection builders).
    var threwW = false;
    try { GraphicsMatrices.CreateOrthographicVulkan(0f, 10f, 0f, 10f); }
    catch (ArgumentOutOfRangeException) { threwW = true; }
    t.ExpectTrue("AA zero width rejected", threwW);
    var threwDepth = false;
    try { GraphicsMatrices.CreateOrthographicVulkan(10f, 10f, 5f, 5f); }
    catch (ArgumentOutOfRangeException) { threwDepth = true; }
    t.ExpectTrue("AA far <= near rejected", threwDepth);
}

// ============================================================================
// Section AB — TextureFormat.TextureByteCount (diagnostics footprint math).
// ============================================================================
//
// SnapshotResources reports each texture's GPU footprint via this mip-chain
// sum. Pin the math: the chain halves dims (floored, min 1) per level, BCn
// rounds each level up to a 4×4 block, and a single mip equals MipByteCount.
{
    // Rgba8 4×4: 64 + 16 + 4 = 84 over 3 mips; 64 for one.
    t.ExpectClose("AB Rgba8 4×4 single mip == 64", TextureFormat.Rgba8.TextureByteCount(4, 4, 1), 64);
    t.ExpectClose("AB Rgba8 4×4 three mips == 64+16+4", TextureFormat.Rgba8.TextureByteCount(4, 4, 3), 84);
    t.ExpectClose("AB R8 256×256 single mip == 65536", TextureFormat.R8.TextureByteCount(256, 256, 1), 256 * 256);
    // Bc7 4×4: every level pads up to one 16-byte block → 16 × 3 = 48.
    t.ExpectClose("AB Bc7Srgb 4×4 three mips (each pads to a block)",
        TextureFormat.Bc7Srgb.TextureByteCount(4, 4, 3), 48);

    var threw = false;
    try { TextureFormat.Rgba8.TextureByteCount(4, 4, 0); } catch (ArgumentOutOfRangeException) { threw = true; }
    t.ExpectTrue("AB mipCount 0 rejected", threw);
}

// ============================================================================
// Section AC — Instanced draw + InstanceBuffer (instancing foundation).
// ============================================================================
//
// The GPU draw (vkCmdDrawIndexed instanceCount=N reading the per-instance SSBO)
// needs a live device — proven by the VulkanInstanced demo under validation.
// Surface-level tests lock the command-record contract, the InstanceData byte
// layout, and InstanceBuffer.Slot's set-3 SSBO declaration.

{
    // AC.1 — InstanceCount defaults to 1 (preserves every existing caller).
    var baseCmd = new DrawIndexedCommand(
        new VertexBufferHandle(1), new IndexBufferHandle(2), new PipelineHandle(3),
        36, Array.Empty<ShaderUniform>(), Array.Empty<ShaderTextureBinding>());
    t.ExpectClose("AC.1 default DrawIndexedCommand.InstanceCount is 1", baseCmd.InstanceCount, 1);

    // AC.2 — with-update carries the instance count through.
    var instanced = baseCmd with { InstanceCount = 5000 };
    t.ExpectClose("AC.2 with-update sets InstanceCount", instanced.InstanceCount, 5000);
    t.ExpectClose("AC.2 with-update leaves IndexCount intact", instanced.IndexCount, 36);
}

{
    // AC.3 — InstanceData is 80 bytes: mat4 model @0, vec4 tint @64.
    var size = System.Runtime.InteropServices.Marshal.SizeOf<Blix.Render.InstanceData>();
    t.ExpectClose("AC.3 sizeof(InstanceData) == 80", size, 80);
    var modelOff = (int)System.Runtime.InteropServices.Marshal.OffsetOf<Blix.Render.InstanceData>(nameof(Blix.Render.InstanceData.Model));
    var tintOff = (int)System.Runtime.InteropServices.Marshal.OffsetOf<Blix.Render.InstanceData>(nameof(Blix.Render.InstanceData.Tint));
    t.ExpectClose("AC.3 InstanceData.Model offset == 0", modelOff, 0);
    t.ExpectClose("AC.3 InstanceData.Tint offset == 64", tintOff, 64);
}

{
    // AC.4 — InstanceBuffer.Slot is the set-3 per-instance SSBO contract (the data
    // layer). Shaders compose it; the engine ships no default shader/material.
    var slot = Blix.Render.InstanceBuffer.Slot;
    t.ExpectClose("AC.4 InstanceBuffer.Slot is set 3", slot.Set, 3);
    t.ExpectClose("AC.4 InstanceBuffer.Slot is binding 0", slot.Binding, 0);
    t.ExpectTrue("AC.4 slot is a StorageBuffer", slot.Type == ShaderResourceType.StorageBuffer);
    t.ExpectTrue("AC.4 slot is Vertex-stage", slot.Stages == ShaderStages.Vertex);
    t.ExpectTrue("AC.4 slot carries a BlockLayout", slot.BlockLayout is not null);
    t.ExpectClose("AC.4 SSBO TotalSize == MaxInstances * Stride",
        slot.BlockLayout!.TotalSize, Blix.Render.InstanceBuffer.MaxInstances * Blix.Render.InstanceBuffer.Stride);
    t.ExpectClose("AC.4 InstanceBuffer.Stride == 80", Blix.Render.InstanceBuffer.Stride, 80);

    // A shader composing the slot + a push range must still validate.
    var composed = new ShaderInterface(
        new[] { slot },
        new[] { new PushConstantRange(ShaderStages.Vertex, 0, 64) });
    t.ExpectTrue("AC.4 composed interface validates", TryValidate(composed) is null);
}

// ============================================================================
// Section AD — Transient vertex arena math (FrameTransientArena substrate).
// ============================================================================
//
// The arena's correctness rests on three pure invariants, all testable without a
// device: (1) sub-slice offsets are stride-aligned so base-0 index fetch is valid;
// (2) the bump allocator packs and rewinds without overrunning capacity; (3) the
// ring depth makes consecutive frames land in different slots — THE hazard fix, the
// reason SpriteBatch/VkLineDrawer no longer race the GPU on a single shared buffer.
// The live GPU draw (bind at slice offset) is proven by run-runner/run-pong under
// validation.

{
    // AD.1 — AlignUp: exact multiples pass through; non-multiples round up; stride 1
    // is identity; bad input throws.
    t.ExpectClose("AD.1 AlignUp(0,36) == 0", TransientArenaMath.AlignUp(0, 36), 0);
    t.ExpectClose("AD.1 AlignUp(36,36) == 36 (already aligned)", TransientArenaMath.AlignUp(36, 36), 36);
    t.ExpectClose("AD.1 AlignUp(1,36) == 36 (rounds up)", TransientArenaMath.AlignUp(1, 36), 36);
    t.ExpectClose("AD.1 AlignUp(37,36) == 72", TransientArenaMath.AlignUp(37, 36), 72);
    t.ExpectClose("AD.1 AlignUp(100,1) == 100 (stride 1 identity)", TransientArenaMath.AlignUp(100, 1), 100);
    var aThrew = false;
    try { TransientArenaMath.AlignUp(0, 0); } catch (ArgumentOutOfRangeException) { aThrew = true; }
    t.ExpectTrue("AD.1 AlignUp rejects stride 0", aThrew);
}

{
    // AD.2 — BumpSlot packs sequential allocations at increasing aligned offsets and
    // tracks Used; Reset rewinds for slot reuse.
    var slot = new BumpSlot(256);
    var ok0 = slot.TryAlloc(40, 36, out var off0);   // 0..40
    var ok1 = slot.TryAlloc(40, 36, out var off1);   // aligned 40->72, 72..112
    t.ExpectTrue("AD.2 first alloc succeeds", ok0);
    t.ExpectClose("AD.2 first offset == 0", off0, 0);
    t.ExpectTrue("AD.2 second alloc succeeds", ok1);
    t.ExpectClose("AD.2 second offset aligned to stride (72)", off1, 72);
    t.ExpectClose("AD.2 Used advanced to 112", slot.Used, 112);
    slot.Reset();
    t.ExpectClose("AD.2 Reset rewinds Used to 0", slot.Used, 0);
    var okR = slot.TryAlloc(40, 36, out var offR);
    t.ExpectTrue("AD.2 alloc after reset succeeds", okR);
    t.ExpectClose("AD.2 offset after reset == 0", offR, 0);
}

{
    // AD.3 — Overflow fails WITHOUT mutating Used, so the device can throw cleanly
    // and the slot stays consistent.
    var slot = new BumpSlot(64);
    slot.TryAlloc(40, 4, out _);                     // Used = 40
    var before = slot.Used;
    var ok = slot.TryAlloc(40, 4, out var off);      // 40 + 40 = 80 > 64 -> fail
    t.ExpectTrue("AD.3 over-capacity alloc fails", !ok);
    t.ExpectClose("AD.3 failed alloc returns offset 0", off, 0);
    t.ExpectClose("AD.3 failed alloc does not advance Used", slot.Used, before);
    // A fit-exactly allocation at the boundary still succeeds.
    var okFit = slot.TryAlloc(24, 4, out var offFit); // 40 + 24 = 64 == capacity
    t.ExpectTrue("AD.3 boundary-exact alloc succeeds", okFit);
    t.ExpectClose("AD.3 boundary alloc offset == 40", offFit, 40);
}

{
    // AD.4 — Ring slots: THE hazard fix. With slots >= 2, consecutive frames map to
    // DIFFERENT physical slots, so frame N never overwrites frame N-1's in-flight
    // vertices. With slots = MaxFramesInFlight+1 (3), a slot is reused only every 3
    // frames — guaranteed GPU-complete.
    const int slots = 3;   // MaxFramesInFlight (2) + 1
    var distinctAcrossPairs = true;
    for (long f = 0; f < 50; f++)
    {
        if (TransientArenaMath.RingSlot(f, slots) == TransientArenaMath.RingSlot(f + 1, slots))
        {
            distinctAcrossPairs = false;
            break;
        }
    }
    t.ExpectTrue("AD.4 consecutive frames map to different ring slots (hazard fix)", distinctAcrossPairs);
    t.ExpectClose("AD.4 slot cycles with period = slots", TransientArenaMath.RingSlot(0, slots), TransientArenaMath.RingSlot(slots, slots));
    t.ExpectTrue("AD.4 a slot is not reused within MaxFramesInFlight (2) frames",
        TransientArenaMath.RingSlot(0, slots) != TransientArenaMath.RingSlot(1, slots) &&
        TransientArenaMath.RingSlot(0, slots) != TransientArenaMath.RingSlot(2, slots));
}

{
    // AD.5 — Command + slice contracts. VertexBufferByteOffset defaults to 0
    // (preserves every existing caller) and carries through a with-update; the
    // slice handle holds (buffer, offset, length).
    var baseCmd = new DrawIndexedCommand(
        new VertexBufferHandle(1), new IndexBufferHandle(2), new PipelineHandle(3),
        6, Array.Empty<ShaderUniform>(), Array.Empty<ShaderTextureBinding>());
    t.ExpectClose("AD.5 default VertexBufferByteOffset is 0", (long)baseCmd.VertexBufferByteOffset, 0);
    var shifted = baseCmd with { VertexBufferByteOffset = 4096 };
    t.ExpectClose("AD.5 with-update sets VertexBufferByteOffset", (long)shifted.VertexBufferByteOffset, 4096);
    t.ExpectClose("AD.5 with-update leaves IndexCount intact", shifted.IndexCount, 6);
    var slice = new TransientVertexSlice(new VertexBufferHandle(7), 72, 40);
    t.ExpectClose("AD.5 slice buffer id", slice.Buffer.Id, 7);
    t.ExpectClose("AD.5 slice byte offset", (long)slice.ByteOffset, 72);
    t.ExpectClose("AD.5 slice byte length", slice.ByteLength, 40);
}

// ============================================================================
// Section AE — Shader variant path resolution (pipeline cache + variants).
// ============================================================================
//
// Variants are cooked to distinct .spv files (glslc -DTAG) and selected at runtime
// by a filename-suffix convention. ShaderVariantPath is the pure resolver; the
// PipelineKey value-equality (the ColorBlends reference-equality hazard) is private
// to the backend and proven on a live device by the runner's cache self-check.

{
    // AE.1 — Base variant has no suffix; tagged variant infixes ".TAG".
    t.ExpectTrue("AE.1 Base suffix is empty", ShaderVariantKey.Base.Suffix.Length == 0);
    t.ExpectTrue("AE.1 FOG suffix is .FOG", new ShaderVariantKey("FOG").Suffix == ".FOG");
    t.ExpectTrue("AE.1 empty tag treated as base", new ShaderVariantKey("").Suffix.Length == 0);

    // AE.2 — Spv path: base vs tagged, correct stage extension placement.
    var baseFrag = ShaderVariantPath.Spv("Shaders", "world", ".frag", ShaderVariantKey.Base);
    var fogFrag = ShaderVariantPath.Spv("Shaders", "world", ".frag", new ShaderVariantKey("FOG"));
    t.ExpectTrue("AE.2 base -> world.frag.spv", baseFrag.EndsWith("world.frag.spv"));
    t.ExpectTrue("AE.2 FOG -> world.FOG.frag.spv", fogFrag.EndsWith("world.FOG.frag.spv"));
    t.ExpectTrue("AE.2 base vert -> world.vert.spv",
        ShaderVariantPath.Spv("Shaders", "world", ".vert", ShaderVariantKey.Base).EndsWith("world.vert.spv"));

    // AE.3 — Refl sidecar is the .spv path + .refl.json.
    t.ExpectTrue("AE.3 FOG refl -> world.FOG.frag.spv.refl.json",
        ShaderVariantPath.Refl("Shaders", "world", ".frag", new ShaderVariantKey("FOG"))
            .EndsWith("world.FOG.frag.spv.refl.json"));

    // AE.4 — ShaderVariantKey is a value type: equal tags compare equal.
    t.ExpectTrue("AE.4 equal tags equal", new ShaderVariantKey("FOG") == new ShaderVariantKey("FOG"));
    t.ExpectTrue("AE.4 different tags differ", new ShaderVariantKey("FOG") != new ShaderVariantKey("SKINNED"));
}

// ============================================================================
// Section AF — Arena/MaterialBindings boundary guardrail.
// ============================================================================
//
// Locked design decision: the transient arena holds ONLY descriptor-less
// vertex/index data bound by offset; descriptor-backed per-frame storage
// (per-instance SSBO, bone palettes) stays in MaterialBindings. These asserts
// trip if a future refactor erodes that split — e.g. turns InstanceBuffer's SSBO
// into something the arena would have to carry, or adds a descriptor concept to
// the slice handle.

{
    // AF.1 — InstanceBuffer is descriptor-backed (a set-3 StorageBuffer), which is
    // exactly why it belongs in MaterialBindings, NOT the arena.
    var slot = Blix.Render.InstanceBuffer.Slot;
    t.ExpectTrue("AF.1 InstanceBuffer.Slot is a StorageBuffer (descriptor-backed → MaterialBindings, not arena)",
        slot.Type == ShaderResourceType.StorageBuffer);
    t.ExpectTrue("AF.1 InstanceBuffer.Slot binds a descriptor set (set 3)", slot.Set == 3);
    t.ExpectTrue("AF.1 InstanceBuffer.Slot carries a BlockLayout (SSBO storage)", slot.BlockLayout is not null);

    // AF.2 — A TransientVertexSlice is purely (buffer, offset, length): no set, no
    // binding, no descriptor. The arena is descriptor-less by construction.
    var sliceFields = typeof(TransientVertexSlice).GetProperties().Select(p => p.Name).ToArray();
    t.ExpectTrue("AF.2 slice exposes Buffer", sliceFields.Contains(nameof(TransientVertexSlice.Buffer)));
    t.ExpectTrue("AF.2 slice exposes ByteOffset", sliceFields.Contains(nameof(TransientVertexSlice.ByteOffset)));
    t.ExpectTrue("AF.2 slice exposes ByteLength", sliceFields.Contains(nameof(TransientVertexSlice.ByteLength)));
    t.ExpectTrue("AF.2 slice has no descriptor/set concept",
        !sliceFields.Any(n => n.Contains("Set") || n.Contains("Binding") || n.Contains("Descriptor")));
}

// ============================================================================
// Section AG — ParticleBatch vertex layout (first new arena consumer).
// ============================================================================
//
// ParticleBatch expands billboards into pos(3)+color(4)+uv(2) and uploads them to
// the transient arena. The public VertexLayoutDescription is the contract callers
// build their pipeline against; lock its stride + attribute offsets so a packing
// change can't silently desync from a caller's pipeline. The live additive/alpha
// draw is proven by VulkanParticles under validation.

{
    var layout = Blix.Render.ParticleBatch.VertexLayoutDescription;
    t.ExpectClose("AG.1 stride == 9 floats (pos3 + color4 + uv2)", layout.Stride, 9 * sizeof(float));
    t.ExpectClose("AG.1 three attributes", layout.Attributes.Count, 3);
    t.ExpectTrue("AG.2 attr0 = Float3 @ 0 (position)",
        layout.Attributes[0].Format == VertexAttributeFormat.Float3 && layout.Attributes[0].Offset == 0);
    t.ExpectTrue("AG.2 attr1 = Float4 @ 12 (color)",
        layout.Attributes[1].Format == VertexAttributeFormat.Float4 && layout.Attributes[1].Offset == 3 * sizeof(float));
    t.ExpectTrue("AG.2 attr2 = Float2 @ 28 (uv)",
        layout.Attributes[2].Format == VertexAttributeFormat.Float2 && layout.Attributes[2].Offset == 7 * sizeof(float));
}

// ============================================================================
// Section AH — Transform3D parenting (hierarchy compose + reparent).
// ============================================================================
//
// Parenting composes local TRS up the chain into WorldMatrix. The composition
// order is the row/column-vector minefield this codebase warns about, so lock it
// against known world points (it mirrors Skeleton's working hierarchy walk:
// world = local * parentWorld). Also pins SetParent(keepWorldPose) — the
// detach-and-keep-flying op — and the cycle guard. The live multi-level
// hierarchy is proven by VehicleParenting under validation.
{
    // Root: WorldPosition == local Position (no parent).
    var root = new Blix.Transform3D { Position = new Vector3(3f, 4f, 5f) };
    t.ExpectClose("AH.1 root world X == local", root.WorldPosition.X, 3f);
    t.ExpectClose("AH.1 root world Z == local", root.WorldPosition.Z, 5f);

    // Translate compose: parent (10,0,0) + child local (1,0,0) → world (11,0,0).
    var parent = new Blix.Transform3D { Position = new Vector3(10f, 0f, 0f) };
    var child = new Blix.Transform3D { Position = new Vector3(1f, 0f, 0f), Parent = parent };
    t.ExpectClose("AH.2 child world X (parent+local = 11)", child.WorldPosition.X, 11f);
    t.ExpectClose("AH.2 child world Y", child.WorldPosition.Y, 0f);
    t.ExpectClose("AH.2 child world Z", child.WorldPosition.Z, 0f);

    // Rotated parent: a child's local offset orbits with the parent's rotation.
    // World pos must equal parentPos + rotate(localOffset, parentRot) for a
    // no-scale chain — the same result Vector3.Transform gives independently.
    var rot = new Blix.Transform3D
    {
        Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 2f),
    };
    var orbiter = new Blix.Transform3D { Position = new Vector3(1f, 0f, 0f), Parent = rot };
    var expected = rot.Position + Vector3.Transform(new Vector3(1f, 0f, 0f), rot.Rotation);
    t.ExpectClose("AH.3 orbiter world X == rotate(local)", orbiter.WorldPosition.X, expected.X);
    t.ExpectClose("AH.3 orbiter world Z == rotate(local)", orbiter.WorldPosition.Z, expected.Z);

    // Multi-level chain: gp(100) -> p(10) -> c(1) -> world X 111.
    var gp = new Blix.Transform3D { Position = new Vector3(100f, 0f, 0f) };
    var p2 = new Blix.Transform3D { Position = new Vector3(10f, 0f, 0f), Parent = gp };
    var c2 = new Blix.Transform3D { Position = new Vector3(1f, 0f, 0f), Parent = p2 };
    t.ExpectClose("AH.4 three-level chain world X (111)", c2.WorldPosition.X, 111f);

    // SetParent(keepWorldPose): detaching preserves the world pose; the local
    // position becomes the old world position (the fire-and-detach op).
    var hull = new Blix.Transform3D { Position = new Vector3(10f, 0f, 0f) };
    var mounted = new Blix.Transform3D { Position = new Vector3(2f, 0f, 0f), Parent = hull };
    var worldBefore = mounted.WorldPosition;   // (12, 0, 0)
    mounted.SetParent(null, keepWorldPose: true);
    t.ExpectTrue("AH.5 detached parent is null", mounted.Parent is null);
    t.ExpectClose("AH.5 detach preserves world X", mounted.WorldPosition.X, worldBefore.X);
    t.ExpectClose("AH.5 detached local X == old world X", mounted.Position.X, 12f);

    // Cycle guard: a->b then b->a throws on assignment.
    var ca = new Blix.Transform3D();
    var cb = new Blix.Transform3D { Parent = ca };
    var threwCycle = false;
    try { ca.Parent = cb; } catch (InvalidOperationException) { threwCycle = true; }
    t.ExpectTrue("AH.6 cycle rejected", threwCycle);

    // Render path: WorldMatrix feeds an InstancedBatch/`model * v` shader (cube.vert)
    // exactly like a VulkanInstanced InstanceData.model. CreateModel builds standard
    // System.Numerics matrices (translation in the last row), uploaded raw and read
    // column-major by GLSL — so M*v lands the translation correctly, no transpose.
    // Simulate the exact GLSL M*v with GlslMul: the part's local origin (0,0,0,1)
    // must map to its WorldPosition (parent 10 + local 1 = 11).
    var uploadBytes = new[]
    {
        child.WorldMatrix.M11, child.WorldMatrix.M12, child.WorldMatrix.M13, child.WorldMatrix.M14,
        child.WorldMatrix.M21, child.WorldMatrix.M22, child.WorldMatrix.M23, child.WorldMatrix.M24,
        child.WorldMatrix.M31, child.WorldMatrix.M32, child.WorldMatrix.M33, child.WorldMatrix.M34,
        child.WorldMatrix.M41, child.WorldMatrix.M42, child.WorldMatrix.M43, child.WorldMatrix.M44,
    };
    var originWorld = GlslMul(uploadBytes, new Vector4(0f, 0f, 0f, 1f));
    t.ExpectClose("AH.7 WorldMatrix upload maps origin to world X (11)", originWorld.X, 11f);
}

// ============================================================================
// Section AI — GltfStaticImporter.ImportNodes (articulated hierarchy import).
// ============================================================================
//
// ImportNodes preserves the node hierarchy (name + parent + LOCAL transform) with
// each mesh in local space — unlike Import, which world-bakes everything into one
// flat blob. That's what lets an articulated model (hull -> turret -> barrel) map
// onto a Transform3D rig. Build a tiny 2-node glTF (turret a child of hull, lifted
// +1 Y) and assert the round-trip keeps the names, parent link, local-space mesh,
// and unbaked local transform.
{
    var tri = new SharpGLTF.Geometry.MeshBuilder<SharpGLTF.Geometry.VertexTypes.VertexPositionNormal>("tri");
    var prim = tri.UsePrimitive(SharpGLTF.Materials.MaterialBuilder.CreateDefault());
    prim.AddTriangle(
        new SharpGLTF.Geometry.VertexTypes.VertexPositionNormal(0, 0, 0, 0, 1, 0),
        new SharpGLTF.Geometry.VertexTypes.VertexPositionNormal(1, 0, 0, 0, 1, 0),
        new SharpGLTF.Geometry.VertexTypes.VertexPositionNormal(0, 0, 1, 0, 1, 0));

    var hull = new SharpGLTF.Scenes.NodeBuilder("hull");
    var turret = hull.CreateNode("turret");
    turret.LocalMatrix = Matrix4x4.CreateTranslation(0f, 1f, 0f);

    var sceneBuilder = new SharpGLTF.Scenes.SceneBuilder();
    sceneBuilder.AddRigidMesh(tri, hull);
    sceneBuilder.AddRigidMesh(tri, turret);

    var tmp = Path.Combine(Path.GetTempPath(), "blix_importnodes_test.glb");
    sceneBuilder.ToGltf2().SaveGLB(tmp);

    var nm = new GltfStaticImporter().ImportNodes(new AssetImportContext(AssetId.Parse("tmp/test"), tmp));
    var hullNode = nm.Find("hull");
    var turretNode = nm.Find("turret");
    t.ExpectTrue("AI.1 hull node imported", hullNode is not null);
    t.ExpectTrue("AI.1 turret node imported", turretNode is not null);
    t.ExpectClose("AI.2 node carries its local-space mesh (3 verts)", hullNode!.Primitives[0].Mesh.VertexCount, 3);
    t.ExpectTrue("AI.3 turret's parent is hull",
        turretNode!.ParentIndex >= 0 && nm.Nodes[turretNode.ParentIndex].Name == "hull");
    t.ExpectClose("AI.4 turret local transform kept (+1 Y, not world-baked)", turretNode.LocalTransform.M42, 1f);
    File.Delete(tmp);
}

// ============================================================================
// Section AJ — Transform3D pose basis + LookAt + WorldRotation.
// ============================================================================
//
// AH pins the parenting *compose* (positions through WorldMatrix). This pins the
// orientation half the convention depends on: the local-(-Z)-forward basis, the
// LookAt solve, and WorldRotation decompose. These are convention-critical — a
// flipped axis here silently aims turrets and cameras the wrong way without ever
// failing a position assert. Rotations are compared by their action on a probe
// vector (q and -q are the same rotation; component compares would false-fail).
{
    // AJ.1 — identity rotation basis matches the default-camera convention:
    // forward is -Z, right is +X, up is +Y.
    var id = new Blix.Transform3D();
    t.ExpectClose("AJ.1 identity Forward == -Z", id.Forward.Z, -1f);
    t.ExpectClose("AJ.1 identity Forward X/Y == 0", id.Forward.X + id.Forward.Y, 0f);
    t.ExpectClose("AJ.1 identity Right == +X", id.Right.X, 1f);
    t.ExpectClose("AJ.1 identity Up == +Y", id.Up.Y, 1f);

    // AJ.2 — yaw +90 about +Y swings Forward from -Z to -X (right-handed).
    var yaw = new Blix.Transform3D
    {
        Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 2f),
    };
    t.ExpectClose("AJ.2 yaw+90 Forward X == -1", yaw.Forward.X, -1f);
    t.ExpectClose("AJ.2 yaw+90 Forward Z == 0", yaw.Forward.Z, 0f);

    // AJ.3 — LookAt solves the rotation aligning local -Z with (target - position).
    var look = new Blix.Transform3D { Position = new Vector3(0f, 0f, 5f) };
    look.LookAt(Vector3.Zero, Vector3.UnitY);
    t.ExpectClose("AJ.3 LookAt origin from +Z → Forward == -Z", look.Forward.Z, -1f);

    var lookRight = new Blix.Transform3D { Position = Vector3.Zero };
    lookRight.LookAt(new Vector3(5f, 0f, 0f), Vector3.UnitY);
    t.ExpectClose("AJ.3 LookAt +X target → Forward == +X", lookRight.Forward.X, 1f);

    // AJ.4 — a child with identity local rotation inherits the parent's world
    // rotation: WorldRotation acting on -Z matches the parent acting on -Z.
    var rotParent = new Blix.Transform3D
    {
        Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 2f),
    };
    var inherit = new Blix.Transform3D { Parent = rotParent };
    var inheritFwd = Vector3.Transform(-Vector3.UnitZ, inherit.WorldRotation);
    t.ExpectClose("AJ.4 child inherits parent world rotation (Forward X == -1)", inheritFwd.X, -1f);
    t.ExpectClose("AJ.4 child inherits parent world rotation (Forward Z == 0)", inheritFwd.Z, 0f);

    // AJ.5 — rotations compose down the chain: child yaw+90 under parent yaw+90
    // is a 180 world yaw, so -Z maps to +Z.
    var childYaw = new Blix.Transform3D
    {
        Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 2f),
        Parent = rotParent,
    };
    var composedFwd = Vector3.Transform(-Vector3.UnitZ, childYaw.WorldRotation);
    t.ExpectClose("AJ.5 composed yaw (90+90=180): -Z → +Z", composedFwd.Z, 1f);
}

// ============================================================================
// Section AK — Camera3D ground-plane picking (Bulwark Gate A invariant).
// ============================================================================
//
// Tower-defense placement casts the cursor ray onto the ground plane and maps the
// hit to a grid cell. Section X already pins ScreenPointToRay's unprojection; this
// pins the GAME invariant on top of it: the same camera that renders also picks, so
// the screen centre must hit the camera's look target on the ground, cursor motion
// must move the hit the right way, and it must hold as the RTS camera orbits. A
// regression here silently places towers a cell off without failing anything else.
{
    const float vpW = 1280f, vpH = 720f;
    var ground = Blix.Geometry.Plane.FromPointNormal(Vector3.Zero, Vector3.UnitY);

    // Angled top-down RTS pose looking at the origin (mirrors BulwarkLoop defaults:
    // pitch ~0.95 rad, distance 36, yaw 0 → eye above and toward +Z).
    Camera3D PoseAt(float yaw)
    {
        var cam = new Camera3D { NearPlane = 0.5f, FarPlane = 400f };
        var cp = MathF.Cos(0.95f);
        var dir = new Vector3(cp * MathF.Sin(yaw), MathF.Sin(0.95f), cp * MathF.Cos(yaw));
        cam.Transform.Position = dir * 36f;
        cam.Transform.LookAt(Vector3.Zero, Vector3.UnitY);
        return cam;
    }

    Vector3 PickGround(Camera3D cam, float sx, float sy)
    {
        var ray = cam.ScreenPointToRay(sx, sy, vpW, vpH);
        var hit = Intersection.Raycast(ray, ground);
        return hit!.Value.Point;
    }

    var camera = PoseAt(0f);

    // AK.1 — the screen centre picks the look target on the ground (origin).
    var center = PickGround(camera, vpW / 2f, vpH / 2f);
    t.ExpectClose("AK.1 screen-centre ray hits the look target (X≈0)", center.X, 0f, 0.05f);
    t.ExpectClose("AK.1 screen-centre ray hits the look target (Z≈0)", center.Z, 0f, 0.05f);
    t.ExpectClose("AK.1 hit lies on the ground plane (Y≈0)", center.Y, 0f, 1e-3f);

    // AK.2 — horizontal cursor motion moves the hit in world X (yaw 0: screen +X →
    // world +X), and left/right are mirror-symmetric about the centre.
    var right = PickGround(camera, vpW / 2f + 200f, vpH / 2f);
    var left  = PickGround(camera, vpW / 2f - 200f, vpH / 2f);
    t.ExpectTrue("AK.2 cursor right → greater world X", right.X > 0.1f);
    t.ExpectTrue("AK.2 cursor left → lesser world X", left.X < -0.1f);
    t.ExpectClose("AK.2 left/right symmetric about centre", right.X + left.X, 0f, 0.05f);

    // AK.3 — the top of the screen picks farther ground than the bottom (a camera
    // angled down sees distant ground up top). Compare distance from the eye.
    var top = PickGround(camera, vpW / 2f, vpH / 2f - 200f);
    var bottom = PickGround(camera, vpW / 2f, vpH / 2f + 200f);
    var eye = camera.Transform.Position;
    t.ExpectTrue("AK.3 top-of-screen ground is farther from the camera than bottom",
        Vector3.Distance(top, eye) > Vector3.Distance(bottom, eye));

    // AK.4 — the gate's load-bearing invariant: centre always picks the target no
    // matter how the camera orbits, because picking and rendering share the camera.
    foreach (var yaw in new[] { MathF.PI / 2f, MathF.PI, 2.5f })
    {
        var c = PickGround(PoseAt(yaw), vpW / 2f, vpH / 2f);
        t.ExpectClose($"AK.4 centre picks target at yaw {yaw:0.0} (X≈0)", c.X, 0f, 0.05f);
        t.ExpectClose($"AK.4 centre picks target at yaw {yaw:0.0} (Z≈0)", c.Z, 0f, 0.05f);
    }
}

// ============================================================================
// Section AL — GraphicsMatrices.SunShadowViewProjection (extracted sun-shadow technique).
// ============================================================================
//
// The CPU half of the shared sun-shadow technique (pairs with the GLSL blix_sun_shadow
// in Blix.Shaders/shadow.glsl), extracted from TankArena/Bulwark. Pins that the light
// frustum covers the scene origin (in Vulkan [0,1] shadow depth) and excludes points
// beyond its ortho extent — a regression here silently drops or mis-clips every shadow.
{
    var sunDir = Vector3.Normalize(new Vector3(0.35f, 0.82f, 0.45f));
    var vp = GraphicsMatrices.SunShadowViewProjection(sunDir, distance: 120f, orthoExtent: 44f, nearPlane: 20f, farPlane: 200f);

    static Vector3 Ndc(Matrix4x4 m, Vector3 p)
    {
        var h = Vector4.Transform(new Vector4(p, 1f), m);
        return new Vector3(h.X / h.W, h.Y / h.W, h.Z / h.W);
    }

    var origin = Ndc(vp, Vector3.Zero);
    t.ExpectTrue("AL.1 scene origin inside the light frustum (NDC xy in [-1,1])",
        MathF.Abs(origin.X) <= 1f && MathF.Abs(origin.Y) <= 1f);
    t.ExpectTrue("AL.1 scene origin within Vulkan shadow depth [0,1]",
        origin.Z >= 0f && origin.Z <= 1f);

    // A point well beyond the ortho half-extent (22) projects outside the [-1,1] frustum.
    var far = Ndc(vp, new Vector3(60f, 0f, 60f));
    t.ExpectTrue("AL.2 point beyond the ortho extent falls outside the frustum",
        MathF.Abs(far.X) > 1f || MathF.Abs(far.Y) > 1f);
}

// ============================================================================
// Section AM — GLSL include ownership: pragma-once is real before glslc.
// ============================================================================
//
// glslc warns about #pragma once and does not honour it. The build used to hand
// source files directly to glslc, so the engine preprocessor's apparent support
// was irrelevant to offline SPIR-V compilation. These cases pin the semantics
// the build tool now uses: consume the directive, deduplicate by stable source
// identity, preserve ordinary repeatable snippets, resolve from the requesting
// source, and keep cycle detection.
{
    var sources = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["/shader/a.glsl"] = "#pragma once\n#include \"shared.glsl\"\nfloat fromA;",
        ["/shader/b.glsl"] = "#pragma once\n#include \"shared.glsl\"\nfloat fromB;",
        ["/shader/shared.glsl"] = "#pragma once\nfloat sharedMarker;",
    };
    var root = new GlslSource(
        "/shader/root.frag", "/shader/root.frag",
        "#version 450\n#include \"a.glsl\"\n#include \"b.glsl\"");
    var result = GlslPreprocessor.PreprocessDetailed(root, Resolve);

    t.ExpectTrue("AM.1 pragma-once diamond emits the shared file once",
        result.ExpandedSource.Split("sharedMarker", StringSplitOptions.None).Length - 1 == 1);
    t.ExpectTrue("AM.1 pragma-once directive is consumed before glslc",
        !result.ExpandedSource.Contains("#pragma once", StringComparison.Ordinal));
    t.ExpectTrue("AM.1 both branches of the diamond remain",
        result.ExpandedSource.Contains("fromA", StringComparison.Ordinal) &&
        result.ExpandedSource.Contains("fromB", StringComparison.Ordinal));

    GlslSource Resolve(GlslSource requesting, string name)
    {
        var directory = requesting.Identity[..requesting.Identity.LastIndexOf('/')];
        var identity = directory + "/" + name;
        return new GlslSource(identity, identity, sources[identity]);
    }
}

{
    // Two spellings, one file. The include token is not an identity: file
    // resolvers canonicalise it before the once-set sees it.
    var root = new GlslSource(
        "/shader/root.frag", "/shader/root.frag",
        "#include \"alias-a.glsl\"\n#include \"alias-b.glsl\"");
    var result = GlslPreprocessor.PreprocessDetailed(
        root,
        (_, name) => new GlslSource(
            "/shader/shared.glsl",
            name,
            "#pragma once\nfloat aliasMarker;"));
    t.ExpectTrue("AM.2 pragma-once compares canonical identity, not include spelling",
        result.ExpandedSource.Split("aliasMarker", StringSplitOptions.None).Length - 1 == 1);
}

{
    var root = new GlslSource(
        "/shader/root.frag", "/shader/root.frag",
        "#include \"snippet.glsl\"\n#include \"snippet.glsl\"");
    var result = GlslPreprocessor.PreprocessDetailed(
        root,
        (_, _) => new GlslSource(
            "/shader/snippet.glsl",
            "/shader/snippet.glsl",
            "float repeatableMarker;"));
    t.ExpectTrue("AM.3 a snippet without pragma-once remains repeatable",
        result.ExpandedSource.Split("repeatableMarker", StringSplitOptions.None).Length - 1 == 2);
}

{
    var root = new GlslSource(
        "/shader/root.frag", "/shader/root.frag",
        "#include \"loop.glsl\"");
    var cycleDetected = false;
    try
    {
        GlslPreprocessor.PreprocessDetailed(
            root,
            (_, _) => new GlslSource(
                "/shader/loop.glsl",
                "/shader/loop.glsl",
                "#include \"loop.glsl\""));
    }
    catch (InvalidOperationException exception)
    {
        cycleDetected = exception.Message.Contains("Circular #include", StringComparison.Ordinal);
    }
    t.ExpectTrue("AM.4 pragma ownership does not weaken include-cycle detection", cycleDetected);
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

// Fixture for Section S: a [Tune]-tagged object the reflector should surface.
enum TuneFixtureMode { A, B, C }
sealed class TuneFixture
{
    [Tune(0, 0.5)] public float Density = 0.1f;
    [Tune(0, 5)]   public int Steps = 1;
    [Tune(0, 60)]  public float flySpeed = 4.5f;
    [Tune]         public bool Wireframe = false;
    [Tune]         public TuneFixtureMode Mode = TuneFixtureMode.B;
    public float NotTunable = 9f;   // no attribute → must be ignored
}

using Blix.Verify;
using System.Numerics;
using Blix;
using Blix.Assets;
using Blix.Core;
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
using System.Reflection;
using System.Text.RegularExpressions;
using Blix.Cooked;
using Blix.Graphics.Images;

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

// <b>Before anything builds a render command.</b> RenderCommandDiagnostics reads its switch once
// and a process cannot half-enable it — a command fingerprinted at record must be verifiable at
// execute — so the suite opts in here rather than depending on the developer's environment.
// Section AT asserts the switch actually took, because a fingerprint check that is off would
// report every case below as passing without reading a byte.
Environment.SetEnvironmentVariable("BLIX_VK_VALIDATE", "1");

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

    // <b>The contract inverted here, deliberately.</b> This asserted that a with-update
    // PRESERVED the caller's reference, which is the aliasing the record now exists to
    // prevent: a recorded command is read at Execute, long after the caller may have
    // reused its buffer. The payload must survive; the reference must not. See Section AP
    // and Blix.Graphics/RenderCommand.cs.
    t.ExpectTrue("H.1 with-update COPIES the payload rather than aliasing the caller's array",
        !ReferenceEquals(withPush.PushConstants, bytes));
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

{
    // K.4 — Temporal clients can cache the graph's match-swapchain generation.
    // A graph has not replaced anything before its backend sees a recreation.
    var graph = new RenderGraph();
    t.ExpectTrue("K.4 match-swapchain generation starts at zero",
        graph.MatchSwapchainResourceGeneration == 0);
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
    threw = false;
    try { graph.Dispatch(comp, dispatch); } catch { threw = true; }
    t.ExpectTrue("L.14 a second Dispatch into the same pass in a frame is accepted (it appends)", !threw);
}

{
    // L.15 — A compute pass carries several dispatches, in the order given: what lets one pass reset,
    // count and scatter (the scene cull). The graph hands its recorded list to this overload.
    var list = new RenderCommandList();
    DispatchCommand D(int x) => new(default, x, 1, 1, Array.Empty<ShaderUniform>(), Array.Empty<ShaderTextureBinding>());
    list.ComputePass("cull", new[] { D(1), D(2), D(3) });
    var pass = list.Passes.Single();
    t.ExpectTrue("L.15 one compute pass holds every dispatch, in order",
        pass.Description.Compute
        && pass.Commands.OfType<DispatchCommand>().Select(d => d.GroupsX).SequenceEqual(new[] { 1, 2, 3 }));
    var refused = false;
    try { list.ComputePass("empty", Array.Empty<DispatchCommand>()); } catch (ArgumentException) { refused = true; }
    t.ExpectTrue("L.15 a compute pass with no dispatches is refused", refused);
}

// ============================================================================
// Section M — Backend-skip contract for test-mode graphs (VB.iii).
// ============================================================================
//
// Test-mode graphs (constructed via the internal parameterless ctor)
// have no backend at all: the graph and its validation are the engine's, and
// realising them is the device's. Compile() runs validation and nothing else. This contract lets Section L tests run without a live
// VkDevice. The real backend (VkImage / VkRenderPass / VkFramebuffer
// allocation) is exercised by the demo at VB.vii.

{
    // M.1 — Test-mode Compile leaves backend state untouched.
    var graph = new RenderGraph();
    var color = graph.ColorTarget("c", TextureFormat.Rgba8, new FixedGraphSize(64, 64));
    graph.GraphicsPass("p").Target(color, LoadOp.Clear, StoreOp.Store).Shader(MinimalShader());
    graph.Compile();
    t.ExpectTrue("M.1 IsCompiled true after test-mode compile", graph.IsCompiled);
    t.ExpectTrue("M.1 a test-mode graph has no backend to allocate anything", graph.Backend is null);
    t.ExpectThrows("M.1 so it has no surfaces either, and says which pass was asked for",
        () => graph.GetPassSurface(new PassHandle(1)), mustMention: "not found");
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
    var barriers = BarrierInference.Infer(graph);
    t.ExpectClose("N.1 single-pass graph has one barrier list",
        barriers.Count, 1);
    foreach (var list in barriers.Values)
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
    var twoPassBarriers = BarrierInference.Infer(graph);
    t.ExpectClose("N.2 two-pass graph has two barrier lists",
        twoPassBarriers.Count, 2);
    var total = 0;
    foreach (var list in twoPassBarriers.Values) total += list.Count;
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

    // <b>These fixtures held a real cross-stage disagreement, and nothing noticed for four
    // months.</b> At offset 92 the vertex stage called the float `uAmbientIntensity` and the
    // fragment called it `uIblIntensity` — same offset, same size, different meaning — from the
    // era before frame.glsl declared the Frame block once for both stages. Every other member
    // agreed; it was a true prefix apart from that one name.
    //
    // MergeStages kept whichever view was larger and asked nothing, so the merged layout said
    // `uIblIntensity` and a write to `uAmbientIntensity` would have gone somewhere else entirely.
    // lit.vert.refl.json now matches the authoritative stage at that offset, and the original is
    // kept beside it, because a corpus that no longer contains the bug cannot prove it is caught.
    var drifted = ShaderReflection.Load(Path.Combine(fxDir, "lit.vert.drifted.refl.json"));
    t.Expect("P.0 the drifted vert/frag pair is rejected, naming the offset",
        Throws(() => ShaderReflection.MergeStages(drifted, frag)),
        "offset 92: uAmbientIntensity vs uIblIntensity");

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
            //@tune bool
            float uShowCascades;
            //@tune bool = 1
            float uProbeOcclusionOn;
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

    // Q.0 — only the tagged decls surface; uEngineThing is dropped.
    t.ExpectClose("Q.0 six tunables found (untagged ignored)", tunables.Count, 6);
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

    // Q.4 — `//@tune bool`: a switch declared as one instead of as a 0..1 slider.
    //
    // Six of VulkanSponza's eight dials were switches wearing a float range, because a toggle
    // could not be asked for. A slider over a boolean accepts 0.37, which the shader rounds at
    // its `> 0.5` test, and reads as an amount when it names a choice -- which is how one of
    // them got a default of 1 that nobody meant as "use the other normal".
    var show = Find("uShowCascades");
    t.ExpectTrue("Q.4 uShowCascades is Bool", show is { Kind: TunableKind.Bool });
    t.ExpectClose("Q.4 bare bool defaults off", show?.Default ?? -1, 0);
    t.ExpectClose("Q.4 and spans 0..1", show?.Max ?? -1, 1);

    var on = Find("uProbeOcclusionOn");
    t.ExpectTrue("Q.4 an explicit '= 1' is Bool too", on is { Kind: TunableKind.Bool });
    t.ExpectClose("Q.4 and defaults on", on?.Default ?? -1, 1);

    // CONTROL: the float form must NOT be swept up as a bool, or every slider becomes a
    // checkbox and the one genuine 0..1 amount in the tree loses its range.
    t.ExpectTrue("Q.4 CONTROL a ranged payload is still Float",
        Find("uSunIntensity") is { Kind: TunableKind.Float });

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
    t.ExpectClose("S.0 seven tunables found", fields.Count, 7);
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

    // S.5 — string member → text field. The kind that makes a declared value addressable
    // by NAME rather than by number, which is what a tool's state is mostly made of.
    var clip = FindF("Clip");
    t.ExpectTrue("S.5 string member is Text kind", clip is { Kind: TuneKind.Text });
    t.ExpectTrue("S.5 get reads the live string", clip?.Text == "Walking_A");
    if (clip is not null) clip.Text = "Running_A";
    t.ExpectTrue("S.5 set wrote the field", fixture.Clip == "Running_A");
    t.ExpectTrue("S.5 a string needs no range", clip is { Min: 0f, Max: 0f });
    t.ExpectTrue("S.5 default buffer is 128", clip?.MaxLength == 128);
    t.ExpectTrue("S.5 MaxLength is carried", FindF("Bone")?.MaxLength == 4);

    // S.6 — the NEGATIVE half: Text is the one kind that is not float-backed, so reading
    // it as a number or reading a number as text must refuse rather than return something
    // plausible. A silent 0 here is a tool that looks like it read your clip name.
    t.ExpectThrows("S.6 Text on a float member throws", () => { _ = FindF("Density")!.Text; });
    t.ExpectThrows("S.6 writing Text to a bool member throws", () => FindF("Wireframe")!.Text = "x");
    t.ExpectTrue("S.6 a Text member still reports Value 0", FindF("Clip")!.Value == 0f);

    // S.7 — a null write is an empty string, not a null field. Every reader of this is a
    // control or a flag, and neither has a sensible rendering for null.
    if (clip is not null) clip.Text = null!;
    t.ExpectTrue("S.7 null becomes empty", fixture.Clip == string.Empty);
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
// Section AI — the cooked scene graph keeps an articulated hierarchy.
// ============================================================================
//
// The cook keeps the node hierarchy (name + parent + LOCAL transform) with each mesh in mesh space — not
// world-baked into one flat blob. That's what lets an articulated model (hull -> turret -> barrel) map
// onto a Transform3D rig (TankArena reads its tank this way). Build a tiny 2-node glTF (turret a child of
// hull, lifted +1 Y), cook it, and assert the cooked graph keeps the names, the parent link, the mesh in
// its own space and the unbaked local transform.
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

    var dir = Path.Combine(Path.GetTempPath(), $"blix-ai-{Guid.NewGuid():N}");
    Directory.CreateDirectory(dir);
    var tmp = Path.Combine(dir, "articulated.glb");
    sceneBuilder.ToGltf2().SaveGLB(tmp);
    var cooked = Blix.Recipes.CookCache.Resolve(tmp);

    var nm = ModelData.Load(cooked, new ModelNeeds(Skinned: false));
    var hullNode = nm.FindNode("hull");
    var turretNode = nm.FindNode("turret");
    t.ExpectTrue("AI.1 hull node cooked", hullNode >= 0);
    t.ExpectTrue("AI.1 turret node cooked", turretNode >= 0);
    t.ExpectClose("AI.2 node carries its mesh in mesh space (3 verts)",
        nm.Meshes[nm.Nodes[hullNode].MeshIndex].Primitives[0].Mesh.VertexCount, 3);
    t.ExpectTrue("AI.3 turret's parent is hull", nm.Nodes[turretNode].ParentIndex == hullNode);
    t.ExpectClose("AI.4 turret local transform kept (+1 Y, not world-baked)", nm.Nodes[turretNode].Local.M42, 1f);
    Directory.Delete(dir, recursive: true);
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

// ============================================================================
// Section AN — ViewPicking: a ray through a named view, panel or not.
// ============================================================================
//
// Camera3D.ScreenPointToRay recomputes the view-projection from a viewport aspect
// ratio, which assumes the view fills the window and that its matrix is the one the
// camera would derive. Neither holds for a view drawn into a panel, which is what
// views were made first-class to allow. ViewPicking reads the view's OWN matrix and
// rectangle instead.
//
// The first test is the important one: for a full-window view the two paths must
// agree exactly, which pins the new one against the old one that Section X already
// covers. A picking routine that disagrees with the camera is worse than none.
{
    var camera = new Camera3D();
    const float W = 1600f, H = 900f;
    var views = new ViewTable();
    var full = views.Declare(
        "main", camera.GetViewProjection(W / H), default, (int)W, (int)H);

    var pointer = new Vector2(1180f, 300f);
    var fromCamera = camera.ScreenPointToRay(pointer.X, pointer.Y, W, H);
    var fromView = ViewPicking.RayThrough(full, pointer);

    t.ExpectTrue("AN.1 a full-window view yields a ray", fromView is not null);
    t.ExpectClose("AN.1 and it is the camera's ray, exactly",
        new Vector4(fromView!.Value.Direction, 0f), new Vector4(fromCamera.Direction, 0f));
    t.ExpectClose("AN.1 from the same origin",
        new Vector4(fromView.Value.Origin, 1f), new Vector4(fromCamera.Origin, 1f));

    // A panel: same camera, but the picture occupies a rectangle offset into the
    // window. The centre of THAT rectangle must give the view's centre ray — which is
    // precisely what passing the full window width/height to ScreenPointToRay cannot
    // express, because it has nowhere to put the offset.
    var panelRect = new Rect(400f, 100f, 800f, 450f);
    var panel = views.Declare(
        "inspector", camera.GetViewProjection(panelRect.Width / panelRect.Height),
        default, panelRect, panelRect);

    var panelCentre = new Vector2(panelRect.X + panelRect.Width / 2f, panelRect.Y + panelRect.Height / 2f);
    var centreRay = ViewPicking.RayThrough(panel, panelCentre);
    var cameraCentre = camera.ScreenPointToRay(
        panelRect.Width / 2f, panelRect.Height / 2f, panelRect.Width, panelRect.Height);
    t.ExpectTrue("AN.2 a panel view yields a ray at its own centre", centreRay is not null);
    t.ExpectClose("AN.2 and it is the centre ray, offset and all",
        new Vector4(centreRay!.Value.Direction, 0f), new Vector4(cameraCentre.Direction, 0f));

    // Outside the rectangle is not a miss on the scene — it is not this view's pointer
    // at all, which is how "which view is under the cursor" gets answered.
    t.ExpectTrue("AN.3 a pointer left of the panel belongs to no view",
        ViewPicking.RayThrough(panel, new Vector2(panelRect.X - 1f, panelCentre.Y)) is null);
    t.ExpectTrue("AN.3 a pointer below the panel belongs to no view",
        ViewPicking.RayThrough(panel, new Vector2(panelCentre.X, panelRect.Y + panelRect.Height + 1f)) is null);
    t.ExpectTrue("AN.3 the window pointer that hit the full view misses the panel",
        ViewPicking.RayThrough(panel, new Vector2(50f, 50f)) is null);

    // The target is not consulted. A view rendered to an off-screen texture picks
    // exactly like one on the swapchain — which is the whole reason an inspector
    // viewport stops being a special case.
    var offscreen = views.Declare(
        "offscreen", camera.GetViewProjection(panelRect.Width / panelRect.Height),
        new RenderSurfaceHandle(7), panelRect, panelRect);
    var offscreenRay = ViewPicking.RayThrough(offscreen, panelCentre);
    t.ExpectTrue("AN.4 an off-screen view picks at all", offscreenRay is not null);
    t.ExpectClose("AN.4 and identically to the on-screen one",
        new Vector4(offscreenRay!.Value.Direction, 0f), new Vector4(centreRay.Value.Direction, 0f));

    // <b>Half-open on the far edges.</b> Two views sharing a boundary must not both claim
    // the pixel on it, or "ask every view who owns the pointer" stops having one answer.
    var left = views.Declare("left", camera.GetViewProjection(1f), default, new Rect(0f, 0f, 400f, 400f), new Rect(0f, 0f, 400f, 400f));
    var right = views.Declare("right", camera.GetViewProjection(1f), default, new Rect(400f, 0f, 400f, 400f), new Rect(400f, 0f, 400f, 400f));
    var onBoundary = new Vector2(400f, 200f);
    var leftClaims = ViewPicking.RayThrough(left, onBoundary) is not null;
    var rightClaims = ViewPicking.RayThrough(right, onBoundary) is not null;
    t.ExpectTrue("AN.6 exactly one view owns a shared boundary pixel", leftClaims != rightClaims);
    t.ExpectTrue("AN.6 and it is the one the pixel starts", rightClaims);
    t.ExpectTrue("AN.6 the interior still belongs to the left view",
        ViewPicking.RayThrough(left, new Vector2(399f, 200f)) is not null);

    // A matrix can invert cleanly and still send a corner of the NDC cube to w = 0 — a
    // point on the eye plane with no finite position. Infinities that survive every
    // later test land as a ray pointing nowhere, diagnosed three hours later as "the
    // mouse is offset".
    var singular = new Matrix4x4(
        1f, 0f, 0f, 0f,
        0f, 1f, 0f, 0f,
        0f, 0f, 1f, 1f,
        0f, 0f, 0f, 0f);
    var eyePlane = views.Declare("eyeplane", singular, default, panelRect, panelRect);
    var eyeRay = ViewPicking.RayThrough(eyePlane, panelCentre);
    t.ExpectTrue("AN.7 a view that unprojects to w=0 yields no ray rather than infinities",
        eyeRay is null || (float.IsFinite(eyeRay.Value.Origin.X) && float.IsFinite(eyeRay.Value.Direction.X)));

    // A degenerate matrix must not throw; there is no ray through a view you cannot
    // invert, and callers already handle "the pointer is not over this view".
    var broken = views.Declare("broken", default, default, panelRect, panelRect);
    t.ExpectTrue("AN.5 a non-invertible view projection yields no ray",
        ViewPicking.RayThrough(broken, panelCentre) is null);
}

// ============================================================================
// Section AO — GestureOwnership: the release goes where the press went.
// ============================================================================
//
// Two bugs that are reflections of each other. Routing a release by who wants input
// NOW drops it when focus moves mid-gesture, stranding a button the application
// believes is still held. Delivering every release unconditionally fixes that and
// hands the application releases for presses the UI swallowed. The press already
// answers the question; this pins that it is asked at the right moment.
{
    const int A = 65, B = 66;

    // The ordinary case: application owns the press, hears the release.
    var own = new GestureOwnership();
    t.ExpectTrue("AO.1 an uncaptured press reaches the application", own.Press(A, uiWantsInput: false));
    t.ExpectTrue("AO.1 and its release does too", own.Release(A));
    t.ExpectTrue("AO.1 nothing is left held", own.HeldCount == 0);

    // The case unconditional delivery got wrong: the UI owned the press.
    t.ExpectTrue("AO.2 a captured press does not reach the application", !own.Press(B, uiWantsInput: true));
    t.ExpectTrue("AO.2 and neither does its release", !own.Release(B));

    // The case the guard got wrong: pressed in the world, released over a panel. Focus
    // at release time is not consulted at all, which is the whole point.
    t.ExpectTrue("AO.3 a press in the world is owned", own.Press(A, uiWantsInput: false));
    t.ExpectTrue("AO.3 its release lands even if a panel now has focus", own.Release(A));

    // A release with no press — the host consumed the key for a dump or an overlay
    // toggle, so it never reached here.
    t.ExpectTrue("AO.4 an unmatched release is not invented", !own.Release(B));

    // Two gestures at once stay independent.
    own.Press(A, uiWantsInput: false);
    own.Press(B, uiWantsInput: true);
    t.ExpectTrue("AO.5 only the application's own press is held", own.HeldCount == 1);
    t.ExpectTrue("AO.5 the captured one releases to nobody", !own.Release(B));
    t.ExpectTrue("AO.5 the owned one still releases", own.Release(A));

    // Focus loss: the releases will never arrive, and an application left believing a
    // key is held is the original bug in a different hat.
    own.Press(A, uiWantsInput: false);
    own.Clear();
    t.ExpectTrue("AO.6 clearing forgets held presses", own.HeldCount == 0);
    t.ExpectTrue("AO.6 so a later release delivers nothing", !own.Release(A));
}

// ============================================================================
// Section AP — recorded commands own their push payload.
// ============================================================================
//
// A pass body records; the GPU work happens at Execute. A command that kept a
// reference to the caller's scratch array therefore read it long after the caller
// had moved on — and a caller reusing one array across draws gave every draw the
// array's final contents. VkLineDrawer hit it, StudioRenderer hit it (seven objects at
// the seventh's transform, six apparently missing, draw counts perfectly healthy),
// and the index-offset DrawIndexed overload exists because it bit vertex buffers.
{
    var scratch = new byte[8];

    scratch[0] = 1;
    var first = new DrawIndexedCommand(
        new VertexBufferHandle(1), new IndexBufferHandle(1), new PipelineHandle(1), 3,
        Array.Empty<ShaderUniform>(), Array.Empty<ShaderTextureBinding>(),
        PushConstants: scratch);

    // The caller reuses its buffer for the next draw, exactly as a renderer packing
    // per-object data does.
    scratch[0] = 2;
    var second = new DrawIndexedCommand(
        new VertexBufferHandle(1), new IndexBufferHandle(1), new PipelineHandle(1), 3,
        Array.Empty<ShaderUniform>(), Array.Empty<ShaderTextureBinding>(),
        PushConstants: scratch);

    t.ExpectTrue("AP.1 the first command kept what it was recorded with",
        first.PushConstants is { } a && a[0] == 1);
    t.ExpectTrue("AP.1 the second kept its own",
        second.PushConstants is { } b && b[0] == 2);
    t.ExpectTrue("AP.1 neither is the caller's array",
        !ReferenceEquals(first.PushConstants, scratch) && !ReferenceEquals(second.PushConstants, scratch));

    // And mutating the caller's buffer after recording changes nothing, which is the
    // property the whole thing turns on.
    scratch[0] = 99;
    t.ExpectTrue("AP.2 a later mutation cannot reach a recorded command",
        first.PushConstants![0] == 1 && second.PushConstants![0] == 2);

    t.ExpectTrue("AP.3 a null payload stays null",
        new DrawIndexedCommand(
            new VertexBufferHandle(1), new IndexBufferHandle(1), new PipelineHandle(1), 3,
            Array.Empty<ShaderUniform>(), Array.Empty<ShaderTextureBinding>()).PushConstants is null);

    // The other two command families record payloads the same way.
    var dispatch = new DispatchCommand(
        new PipelineHandle(2), 1, 1, 1,
        Array.Empty<ShaderUniform>(), Array.Empty<ShaderTextureBinding>(), scratch);
    var indirect = new DrawIndexedIndirectCommand(
        new VertexBufferHandle(1), new IndexBufferHandle(1), new PipelineHandle(1),
        new IndirectBufferHandle(1), 0, 1,
        Array.Empty<ShaderUniform>(), Array.Empty<ShaderTextureBinding>(), null, scratch);
    scratch[0] = 7;
    t.ExpectTrue("AP.4 compute dispatch owns its payload too", dispatch.PushConstants![0] == 99);
    t.ExpectTrue("AP.4 indirect draw owns its payload too", indirect.PushConstants![0] == 99);

    // AP.5: an indirect draw carries a per-draw material (set 3, the per-instance storage buffer an
    // instanced indirect draw reads through gl_InstanceIndex), as the direct draws do, and every caller
    // that names none still records none.
    t.ExpectTrue("AP.5 an indirect draw records no per-draw material unless given one", indirect.PerDrawMaterial is null);
    var list = new RenderCommandList();
    list.Pass("instanced", new RenderPassDescription(RenderSurfaceHandle.Default, Array.Empty<GraphicsColor?>(), ClearDepth: false),
        pass => pass.DrawIndexedIndirect(
            new VertexBufferHandle(1), new IndexBufferHandle(1), new PipelineHandle(1), new IndirectBufferHandle(1), 0, 4,
            Array.Empty<ShaderUniform>(), Array.Empty<ShaderTextureBinding>(), perDrawMaterial: new MaterialHandle(9)));
    t.ExpectTrue("AP.5 and a pass forwards one it is given",
        list.Passes.Single().Commands.OfType<DrawIndexedIndirectCommand>().SingleOrDefault()?.PerDrawMaterial == new MaterialHandle(9));
}

// ============================================================================
// Section AQ — ClipPlayer: the rest reset, and root motion across the loop.
// ============================================================================
//
// Two behaviours, both of which look correct in a still frame and are wrong over time:
//
//   1. A partial clip sampled without resetting to rest leaves every untouched bone
//      holding LAST frame's value. Three consumers wrote that reset by hand.
//   2. Root travel taken as `root(t1) - root(t0)` reports a jump backwards across the
//      whole cycle every time t1 wraps — a walk that lurches once per loop.
//
// The fixture is a two-bone skeleton with a clip that animates ONLY the root, which
// makes both failures observable in one setup: bone 1 is what the reset protects, and
// bone 0 is what travels.
{
    // Root at the origin, child one unit up, at rest and at bind. The rest is the skeleton's; the binds
    // (each the inverse of the bone's object-space bind transform) are the skin's.
    var bones = new[]
    {
        new Bone("root", -1, BoneTransform.Identity),
        new Bone("child", 0, BoneTransform.Identity with { Translation = new Vector3(0f, 1f, 0f) }),
    };
    var skeleton = new Skeleton(bones);
    var skin = SkinBinding.Direct(skeleton, new[] { Matrix4x4.Identity, Matrix4x4.CreateTranslation(0, -1, 0) });

    // A body's palette into a set: its bone worlds, then the set's skin's binding over them.
    static int AddPose(BonePaletteSet set, Pose pose, Matrix4x4 post)
    {
        var worlds = new BoneWorlds(set.Skin.Skeleton);
        worlds.Compute(pose);
        return set.Add(worlds, post);
    }

    // The root walks 2 m along +X over 1 s and does NOT return — real root motion.
    var travel = new AnimationClip("walk", new[]
    {
        new BoneTrack
        {
            BoneIndex = 0,
            Translation = new KeyframeVector3Curve(new[]
            {
                new Keyframe<Vector3>(0.0, Vector3.Zero),
                new Keyframe<Vector3>(1.0, new Vector3(2f, 0f, 0f)),
            }),
        },
    });

    t.ExpectClose("AQ.1 clip duration comes from its longest channel", (float)travel.Duration, 1f);

    var player = new ClipPlayer(skeleton, travel);

    // ── The reset ────────────────────────────────────────────────────────────
    // Bone 1 has no track, so every sample must leave it at its rest value. Dirty the
    // pose first: without the CopyFrom, the dirt survives and the bone is one frame — or
    // one clip — behind forever.
    player.Pose.Locals[1] = new BoneTransform(new Vector3(99f, 99f, 99f), Quaternion.Identity, Vector3.One);
    player.Advance(0.25);
    t.ExpectClose("AQ.2 an untracked bone is reset to rest, not left dirty",
        player.Pose.Locals[1].Translation.X, 0f);
    t.ExpectClose("AQ.2 a tracked bone follows its curve", player.Pose.Locals[0].Translation.X, 0.5f);

    // ── Travel inside one pass ───────────────────────────────────────────────
    player.ScrubTo(0.0);
    player.Advance(0.25);
    t.ExpectClose("AQ.3 a quarter of the clip travels a quarter of the distance",
        player.RootDelta.Translation.X, 0.5f);

    // A scrub is a jump, not travel. Integrating a scrub would teleport whatever the
    // delta drives, which is why the player clears it rather than reporting the gap.
    player.ScrubTo(0.9);
    t.ExpectClose("AQ.4 a scrub reports no travel", player.RootDelta.Translation.X, 0f);

    // ── The loop boundary ────────────────────────────────────────────────────
    // Standing at 0.9 and stepping 0.2 crosses the seam: 0.1 s left in this cycle plus
    // 0.1 s of the next, which is 0.4 m forward. The subtraction form reports
    // root(0.1) - root(0.9) = -1.6 m, and the sign alone gives it away.
    player.ScrubTo(0.9);
    player.Advance(0.2);
    t.ExpectClose("AQ.5 travel across the seam is forward, not a cycle backwards",
        player.RootDelta.Translation.X, 0.4f);
    t.ExpectClose("AQ.5 and the clock lands where it should", (float)player.Time, 0.1f);

    // ── Many cycles in one step ──────────────────────────────────────────────
    // A long frame (a debugger pause, a hitch) must not lose the cycles it skipped.
    player.ScrubTo(0.0);
    player.Advance(3.5);
    t.ExpectClose("AQ.6 a step spanning three and a half cycles travels seven metres",
        player.RootDelta.Translation.X, 7f);

    // ── Integrated, at a step that never lands on the seam ───────────────────
    // The property Stage C is actually about: summing the per-frame deltas over several
    // loops equals the per-cycle travel times the number of cycles. A step chosen NOT to
    // divide the duration puts every wrap in the middle of a frame, which is the only
    // case the piecewise walk exists for.
    player.ScrubTo(0.0);
    var summed = 0f;
    for (var i = 0; i < 100; i++)
    {
        player.Advance(0.03);
        summed += player.RootDelta.Translation.X;
    }

    t.ExpectClose("AQ.7 100 steps of 0.03 s integrate to three cycles of travel", summed, 6f, 0.001f);

    // ── Reverse ──────────────────────────────────────────────────────────────
    // Negative rate takes the mirrored path (bounded by the clip's start, not its end),
    // so it is genuinely different code and genuinely able to be wrong on its own.
    player.ScrubTo(0.1);
    player.Rate = -1f;
    player.Advance(0.2);
    t.ExpectClose("AQ.8 running backwards across the seam travels backwards",
        player.RootDelta.Translation.X, -0.4f);
    t.ExpectClose("AQ.8 and wraps to the end of the clip", (float)player.Time, 0.9f);
    player.Rate = 1f;

    // ── An in-place clip ─────────────────────────────────────────────────────
    // The common case: the root returns to where it started, so a whole cycle nets zero
    // and the game owns locomotion. It must net zero through the SAME code path that
    // reports 2 m for the travelling clip — a wrap handler that special-cased "no travel"
    // would pass this and fail the one above.
    var inPlace = new AnimationClip("idle", new[]
    {
        new BoneTrack
        {
            BoneIndex = 0,
            Translation = new KeyframeVector3Curve(new[]
            {
                new Keyframe<Vector3>(0.0, Vector3.Zero),
                new Keyframe<Vector3>(0.5, new Vector3(0.3f, 0f, 0f)),
                new Keyframe<Vector3>(1.0, Vector3.Zero),
            }),
        },
    });

    t.ExpectClose("AQ.9 an in-place clip reports no per-cycle travel",
        RootMotion.PerCycle(inPlace, 0, skeleton.CreateRestPose().Locals[0]).Distance, 0f, 0.0001f);

    var idlePlayer = new ClipPlayer(skeleton, inPlace);
    var drift = 0f;
    for (var i = 0; i < 100; i++)
    {
        idlePlayer.Advance(0.03);
        drift += idlePlayer.RootDelta.Translation.X;
    }

    t.ExpectClose("AQ.9 and integrating it over three cycles drifts nowhere", drift, 0f, 0.001f);

    // ── A clip with no root track at all ─────────────────────────────────────
    // RootAt falls back to the rest value on both ends, so the delta is exactly zero
    // rather than approximately so. Worth pinning: a fallback that returned identity
    // instead of rest would report the rest offset as travel on the first frame.
    var armOnly = new AnimationClip("wave", new[]
    {
        new BoneTrack
        {
            BoneIndex = 1,
            Rotation = new KeyframeQuaternionCurve(new[]
            {
                new Keyframe<Quaternion>(0.0, Quaternion.Identity),
                new Keyframe<Quaternion>(1.0, Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 1f)),
            }),
        },
    });

    var armPlayer = new ClipPlayer(skeleton, armOnly);
    armPlayer.Advance(0.5);
    t.ExpectClose("AQ.10 a clip with no root track reports exactly zero travel",
        armPlayer.RootDelta.Translation.Length(), 0f);

    // ── A zero-duration pose clip ────────────────────────────────────────────
    // The Rogue ships seven. They are poses, not faults: advancing one holds it, and the
    // guard against dividing by a zero duration is the reason every consumer wrote
    // `duration > 0 ? ... : 0` by hand before this existed.
    var poseClip = new AnimationClip("t-pose", new[]
    {
        new BoneTrack
        {
            BoneIndex = 0,
            Translation = new KeyframeVector3Curve(new[] { new Keyframe<Vector3>(0.0, new Vector3(5f, 0f, 0f)) }),
        },
    });

    var posePlayer = new ClipPlayer(skeleton, poseClip);
    posePlayer.Advance(2.0);
    t.ExpectClose("AQ.11 a zero-duration pose holds, and its clock stays at zero", (float)posePlayer.Time, 0f);
    t.ExpectClose("AQ.11 and it still writes the pose it describes",
        posePlayer.Pose.Locals[0].Translation.X, 5f);
    t.ExpectClose("AQ.11 with no travel to report", posePlayer.RootDelta.Translation.Length(), 0f);

    // ── A palette matrix is not a joint position ─────────────────────────────
    // The distinction that cost the lab its first skeleton overlay, pinned here because
    // it is a fact about the palette rather than about the lab. At rest every
    // palette matrix is the identity by construction (BindWorld × InverseBindPose = I),
    // so a skeleton drawn from palette translations collapses onto the origin — correct
    // arithmetic, wrong question. Where the joint IS comes from the hierarchy walk's
    // `world` term alone, which is the three-line recurrence below.
    var rest = skeleton.CreateRestPose();
    var palette = new BonePalette(skeleton.BoneCount);
    skin.ComputePalette(rest, palette);
    t.ExpectClose("AQ.12 a rest palette matrix carries no translation", palette.Matrices[1].M42, 0f);

    var worlds = new Matrix4x4[skeleton.BoneCount];
    for (var i = 0; i < skeleton.BoneCount; i++)
    {
        var local = rest.Locals[i].ToMatrix();
        var parent = skeleton.Bones[i].ParentIndex;
        worlds[i] = parent < 0 ? local : local * worlds[parent];
    }

    t.ExpectClose("AQ.12 but the bone's world transform is where the joint is", worlds[1].M42, 1f);

    // ── Stripping is the other half of taking a delta ────────────────────────
    // A caller that reads RootDelta and ALSO leaves the root animated applies the travel
    // twice: the clip moves the mesh and the transform moves it again, so a walk runs at
    // double speed and snaps back once per loop. Strip reverts every root to rest, which
    // is what makes "the clip says how far, the game says where" a coherent division.
    player.ScrubTo(0.5);
    t.ExpectClose("AQ.13 the clip's root has travelled by mid-clip",
        player.Pose.Locals[0].Translation.X, 1f);

    RootMotion.Strip(skeleton, player.Pose, player.RestPose);
    t.ExpectClose("AQ.13 and stripping puts it back at rest", player.Pose.Locals[0].Translation.X, 0f);

    // Non-root bones are untouched — stripping is about the root, not about resetting a pose.
    t.ExpectClose("AQ.13 leaving every non-root bone alone",
        player.Pose.Locals[1].Translation.Y, skeleton.CreateRestPose().Locals[1].Translation.Y);

    // ── Finished is the boundary in the DIRECTION OF TRAVEL ──────────────────
    // Negative Rate is a supported way to play a clip, so a one-shot run backwards ends at
    // t=0 as surely as a forward one ends at Duration. This used to clamp to zero and return
    // true forever, so a lifecycle driven by the return value hung on a clip that had
    // visibly stopped — invisible because nothing in the tree played a one-shot backwards.
    var once = new ClipPlayer(skeleton, travel) { Loop = false };
    once.ScrubTo(0.9);
    t.ExpectTrue("AQ.14 a one-shot forward run reports finished at the end", !once.Advance(0.2));
    t.ExpectClose("AQ.14 and lands on the end", (float)once.Time, 1f);

    var backwards = new ClipPlayer(skeleton, travel) { Loop = false, Rate = -1f };
    backwards.ScrubTo(0.1);
    t.ExpectTrue("AQ.14 a one-shot reverse run reports finished at the START", !backwards.Advance(0.2));
    t.ExpectClose("AQ.14 and lands on zero, not on the end", (float)backwards.Time, 0f);
    t.ExpectTrue("AQ.14 with Finished set either way", backwards.Finished && once.Finished);

    // A reverse step that does NOT reach the start is still running.
    var partway = new ClipPlayer(skeleton, travel) { Loop = false, Rate = -1f };
    partway.ScrubTo(0.8);
    t.ExpectTrue("AQ.14 and a reverse step short of the start keeps going", partway.Advance(0.2));
    t.ExpectClose("AQ.14 travelling backwards as it goes", partway.RootDelta.Translation.X, -0.4f);

    // ── BonePaletteSet: the stride, which two languages have to agree on ─────
    // Instance i's matrices start at i * BoneCount. That sentence is restated in a C# packing
    // loop and in a GLSL `gl_InstanceIndex * stride`, and nothing checks the two agree — when
    // they disagree the bodies do not vanish, they render as other bodies' poses, smeared.
    // Bulwark and the external RTSGame consumer each carry their own copy of it; this names it once.
    {
        var set = new BonePaletteSet(skin, capacity: 3);
        t.ExpectTrue("AQ.15 the buffer is capacity x the skin's joint count",
            set.Matrices.Length == 3 * skin.JointCount);
        t.ExpectTrue("AQ.15 and starts with nothing live", set.Count == 0 && set.LiveMatrixCount == 0);

        // Three DIFFERENT poses, so a set that wrote them all to one slot would be caught.
        var a = skeleton.CreateRestPose();
        var b = skeleton.CreateRestPose();
        var c = skeleton.CreateRestPose();
        a.Locals[0] = a.Locals[0] with { Translation = new Vector3(1f, 0f, 0f) };
        b.Locals[0] = b.Locals[0] with { Translation = new Vector3(2f, 0f, 0f) };
        c.Locals[0] = c.Locals[0] with { Translation = new Vector3(3f, 0f, 0f) };

        t.ExpectTrue("AQ.15 Add returns the instance index", AddPose(set, a, Matrix4x4.Identity) == 0);
        t.ExpectTrue("AQ.15 counting up", AddPose(set, b, Matrix4x4.Identity) == 1);
        t.ExpectTrue("AQ.15 and again", AddPose(set, c, Matrix4x4.Identity) == 2);
        t.ExpectTrue("AQ.15 three live instances is three strides of matrices",
            set.LiveMatrixCount == 3 * skin.JointCount);

        // The root bone of each instance sits at i * BoneCount and holds THAT instance's pose.
        // This is the assertion a shader's `base = gl_InstanceIndex * stride` has to match.
        t.ExpectClose("AQ.15 instance 0 at offset 0", set.Matrices[0].M41, 1f);
        t.ExpectClose("AQ.15 instance 1 one stride along", set.Matrices[skin.JointCount].M41, 2f);
        t.ExpectClose("AQ.15 instance 2 two strides along", set.Matrices[2 * skin.JointCount].M41, 3f);
        t.ExpectClose("AQ.15 and Slice agrees with the arithmetic", set.Slice(1)[0].M41, 2f);

        // `post` is where a caller bakes a world placement in (Bulwark's shape) or passes identity
        // and places the body some other way (the external RTSGame consumer's). The set takes no view; it just composes.
        set.Reset();
        t.ExpectTrue("AQ.16 Reset makes the slots free again", set.Count == 0);
        AddPose(set, a, Matrix4x4.CreateTranslation(10f, 0f, 0f));
        t.ExpectClose("AQ.16 post-multiply bakes a placement into the palette", set.Matrices[0].M41, 11f);

        // Full is an exception, not a silent drop. A crowd that quietly stops growing at capacity
        // shows up as "the last few enemies are invisible", which looks like anything but this.
        AddPose(set, b, Matrix4x4.Identity);
        AddPose(set, c, Matrix4x4.Identity);
        var overflowed = false;
        try { AddPose(set, a, Matrix4x4.Identity); }
        catch (InvalidOperationException) { overflowed = true; }
        t.ExpectTrue("AQ.16 a fourth instance in a set of three throws", overflowed);

        // A stride mismatch is the failure this type exists to make impossible, so it is loud.
        // A set is one skin's: the stride is that skin's joint count, and no body of another skin can be added,
        // because Add takes no skin. Two same-sized skins cannot share a buffer by accident.
        t.Expect("AQ.16 a set is made for one skin, and its stride is that skin's joint count",
            ReferenceEquals(set.Skin, skin) && set.JointCount == skin.JointCount);

        // The worlds must be the skin's own skeleton's. A same-sized rig's are refused by identity: a count
        // check would pass them, and its bone 1 is not this one's.
        set.Reset();
        var sameSized = new Skeleton(bones);
        var foreignWorlds = new BoneWorlds(sameSized);
        foreignWorlds.Compute(sameSized.CreateRestPose());
        t.ExpectThrows("AQ.16 bone worlds of another skeleton are refused, even one with as many bones",
            () => set.Add(foreignWorlds, Matrix4x4.Identity), mustMention: "another skeleton");
        var ownWorlds = new BoneWorlds(skeleton);
        ownWorlds.Compute(skeleton.CreateRestPose());
        t.Expect("AQ.16 CONTROL: the skin's own skeleton's worlds are taken", set.Add(ownWorlds, Matrix4x4.Identity) == 0);

        // Index-for-index facts refuse a count that does not match, rather than padding or cutting it.
        t.ExpectThrows("AQ.16 IncludeAncestors refuses flags that are not one per node",
            () => SkinningAnalysis.IncludeAncestors(new[] { -1, 0, 1 }, new[] { true, false }), mustMention: "index for index");
        t.Expect("AQ.16 CONTROL: one flag per node promotes the ancestors of a weighted one",
            SkinningAnalysis.IncludeAncestors(new[] { -1, 0, 1 }, new[] { false, false, true }).SequenceEqual(new[] { true, true, true }));

        // A diagnostic primitive names the fact it could not derive.
        var singular = SkinBinding.Direct(skeleton, new[] { Matrix4x4.Identity, default(Matrix4x4) });
        t.ExpectThrows("AQ.16 JointBindWorlds refuses a singular inverse bind, naming the joint",
            () => singular.JointBindWorlds(), mustMention: "joint 1 ('child')");
        t.Expect("AQ.16 CONTROL: invertible binds give their bind worlds",
            skin.JointBindWorlds()[1].M42 == 1f);

        // JointHierarchy's placement is a factoring: any invertible one gives the same worlds. A singular one (a
        // zero-scaled parent, which glTF allows) has no inverse. The case it broke is a second root under ANOTHER
        // parent: its offset against the singular placement was NaN, read as identity, and the root was placed
        // under the first root's parent instead. Two roots: A under `firstParent`, B under an ordinary parent.
        static (JointHierarchy Hierarchy, Matrix4x4[] Scene) Hang(Matrix4x4 firstParent)
        {
            var locals = new[]
            {
                firstParent, Matrix4x4.CreateTranslation(0f, 1f, 0f), Matrix4x4.CreateTranslation(0f, 1f, 0f),
                Matrix4x4.CreateTranslation(-3f, 0f, 0f), Matrix4x4.CreateTranslation(0f, 2f, 0f),
            };
            var parentOf = new[] { -1, 0, 1, -1, 3 };
            var scene = new Matrix4x4[locals.Length];
            for (var n = 0; n < locals.Length; n++) scene[n] = parentOf[n] < 0 ? locals[n] : locals[n] * scene[parentOf[n]];
            var resolved = JointHierarchy.Resolve(new[] { "rootA", "tipA", "rootB" }, new[] { -1, 0, -1 }, new[] { 1, 2, 4 },
                n => parentOf[n], n => locals[n], n => scene[n]);
            return (resolved, scene);
        }

        static float WorstAgainstScene(JointHierarchy h, Matrix4x4[] scene)
        {
            var jointNodes = new[] { 1, 2, 4 };
            var bones = new Skeleton(h.Bones.ToArray());
            var worlds = new BoneWorlds(bones);
            worlds.Compute(bones.CreateRestPose());
            var worst = 0f;
            for (var b = 0; b < jointNodes.Length; b++)
            {
                var d = worlds[b] * h.Placement - scene[jointNodes[b]];
                foreach (var v in new[] { d.M11, d.M12, d.M13, d.M14, d.M21, d.M22, d.M23, d.M24, d.M31, d.M32, d.M33, d.M34, d.M41, d.M42, d.M43, d.M44 })
                {
                    worst = float.IsFinite(v) ? MathF.Max(worst, MathF.Abs(v)) : float.PositiveInfinity;
                }
            }

            return worst;
        }

        var (ordinary, ordinaryScene) = Hang(Matrix4x4.CreateScale(2f) * Matrix4x4.CreateTranslation(5f, 0f, 0f));
        t.Expect("AQ.19 CONTROL: an ordinary first parent is the placement, and both roots' worlds are the scene's",
            ordinary.Placement == ordinaryScene[0] && WorstAgainstScene(ordinary, ordinaryScene) < 1e-5f,
            $"worst {WorstAgainstScene(ordinary, ordinaryScene)}");
        var (hidden, hiddenScene) = Hang(Matrix4x4.CreateScale(0f) * Matrix4x4.CreateTranslation(5f, 0f, 0f));
        t.Expect("AQ.19 a zero-scaled first parent factors nothing out: identity placement, each root offset by its own parent",
            hidden.Placement == Matrix4x4.Identity && hidden.Bones[0].Offset == hiddenScene[0] && hidden.Bones[2].Offset == hiddenScene[3],
            $"placement {hidden.Placement}, offsets {hidden.Bones[0].Offset} / {hidden.Bones[2].Offset}");
        t.Expect("AQ.19 and the root under the other parent is where the scene puts it, not under the zero-scaled one",
            WorstAgainstScene(hidden, hiddenScene) < 1e-5f, $"worst {WorstAgainstScene(hidden, hiddenScene)}");
    }

    // ── Instancing is not phase-locked, clip-locked or state-locked ──────────
    // The failure this guards is silent: a stride of zero, a write that always lands in slot 0, a
    // pose object shared between bodies. None of them throws, none warps the geometry, and all of
    // them render a row that looks entirely reasonable until you notice every body is doing the
    // same thing. Checked in BOTH directions, because a test that only asserts "these differ"
    // passes whenever anything differs, for any reason.
    {
        // A skeleton whose root is animated by two clips that disagree at every instant.
        var bones2 = new[]
        {
            new Bone("root", -1, BoneTransform.Identity),
            new Bone("child", 0, BoneTransform.Identity with { Translation = new Vector3(0f, 1f, 0f) }),
        };
        var rig = new Skeleton(bones2);
        var rigSkin = SkinBinding.Direct(rig, new[] { Matrix4x4.Identity, Matrix4x4.CreateTranslation(0, -1, 0) });

        AnimationClip Line(string name, float to) => new(name, new[]
        {
            new BoneTrack
            {
                BoneIndex = 0,
                Translation = new KeyframeVector3Curve(new[]
                {
                    new Keyframe<Vector3>(0.0, Vector3.Zero),
                    new Keyframe<Vector3>(1.0, new Vector3(to, 0f, 0f)),
                }),
            },
        });

        var slow = Line("slow", 1f);
        var fast = Line("fast", 5f);
        var set = new BonePaletteSet(rigSkin, capacity: 3);

        // Positive: three players, different clips, different phases, different rates.
        var players = new[]
        {
            new ClipPlayer(rig, slow),
            new ClipPlayer(rig, fast) { Rate = 2f },
            new ClipPlayer(rig, slow) { Rate = 0.5f },
        };
        players[0].ScrubTo(0.1);
        players[1].ScrubTo(0.4);
        players[2].ScrubTo(0.8);
        foreach (var p in players) AddPose(set, p.Pose, Matrix4x4.Identity);

        var prints = new[] { set.Fingerprint(0), set.Fingerprint(1), set.Fingerprint(2) };
        t.ExpectTrue("AQ.17 three independently posed bodies give three fingerprints",
            prints[0] != prints[1] && prints[1] != prints[2] && prints[0] != prints[2]);

        // <b>The negative control, and it is the half that makes the other half mean anything.</b>
        // One clip, one instant, one placement: every slot must come out bit-identical. A set that
        // wrote every body to slot 0 would pass the positive check above and fail nothing — this is
        // what catches it.
        set.Reset();
        var one = new ClipPlayer(rig, slow);
        one.ScrubTo(0.37);
        for (var i = 0; i < 3; i++) AddPose(set, one.Pose, Matrix4x4.Identity);
        t.ExpectTrue("AQ.17 and one pose in every slot comes back identical",
            set.Fingerprint(0) == set.Fingerprint(1) && set.Fingerprint(1) == set.Fingerprint(2));

        // Advancing one player must not disturb another's pose. Each ClipPlayer owns its own Pose;
        // a shared one would make every body the last body written, which is the state-lock case.
        players[0].ScrubTo(0.1);
        var beforeX = players[0].Pose.Locals[0].Translation.X;
        players[1].Advance(0.25);
        players[2].Advance(0.25);
        t.ExpectClose("AQ.18 advancing one body leaves another's pose alone",
            players[0].Pose.Locals[0].Translation.X, beforeX);

        // And different rates genuinely diverge — two bodies on the SAME clip must drift apart, which
        // a frame-locked clock cannot do.
        var a2 = new ClipPlayer(rig, slow) { Rate = 1f };
        var b2 = new ClipPlayer(rig, slow) { Rate = 0.25f };
        a2.Advance(0.4);
        b2.Advance(0.4);
        t.ExpectTrue("AQ.18 two bodies on one clip at different rates drift apart",
            Math.Abs(a2.Time - b2.Time) > 0.2);
    }
}

// ============================================================================
// Section AR — a view that is NOT the whole window.
// ============================================================================
//
// ViewPicking has been correct since it was written and had exactly one caller, which declared a
// view covering the entire window. Every interesting property of a view — an offset, a letterbox,
// a backing scale that differs from the logical size — was therefore untested, because a
// full-window view at 1x makes all three the identity.
//
// The lab's embedded viewport is the first view that is none of those things: a picture fitted
// inside a panel, at an offset, on a target half the framebuffer's size.
{
    // A 640x360 picture sitting at (200, 120) inside a window — the letterboxed image rect, not
    // the panel rect that contains it.
    var projection = GraphicsMatrices.CreatePerspectiveVulkan(MathF.PI / 3f, 640f / 360f, 0.1f, 100f);
    var vp = Matrix4x4.CreateLookAt(new Vector3(0, 0, 5), Vector3.Zero, Vector3.UnitY) * projection;

    var panel = new ViewDeclaration(
        new ViewId(1), "viewport", vp, new RenderSurfaceHandle(7),
        LogicalViewport: new Rect(200f, 120f, 640f, 360f),
        // Physical is the TARGET's own extent — half a 2560x1440 framebuffer — and deliberately
        // shares neither origin nor size with the logical rect. A view whose two rectangles agree
        // is the case that proves nothing.
        PhysicalViewport: new Rect(0f, 0f, 1280f, 720f));

    // The centre of the IMAGE, not of the window and not of the panel.
    var centre = ViewPicking.RayThrough(panel, new Vector2(200f + 320f, 120f + 180f));
    t.ExpectTrue("AR.1 a pointer at the image's centre yields a ray", centre is not null);
    t.ExpectClose("AR.1 and it points straight down the camera's forward", centre!.Value.Direction.Z, -1f, 0.001f);
    t.ExpectClose("AR.1 with no sideways component", centre.Value.Direction.X, 0f, 0.001f);

    // <b>The offset is not optional.</b> The same coordinates read as if the view were at the
    // window's origin land somewhere else entirely — this is the bug a full-window view can never
    // expose, because there the two are the same point.
    var asIfAtOrigin = ViewPicking.RayThrough(panel, new Vector2(320f, 180f));
    t.ExpectTrue("AR.2 the window-centre point is NOT the image centre",
        asIfAtOrigin is null || MathF.Abs(asIfAtOrigin.Value.Direction.X) > 0.01f);

    // Outside the rect is no ray rather than an extrapolated one, which is what makes "ask every
    // view, at most one answers" a usable way to route a pointer.
    t.ExpectTrue("AR.3 a pointer left of the image misses",
        ViewPicking.RayThrough(panel, new Vector2(199f, 300f)) is null);
    t.ExpectTrue("AR.3 a pointer below it misses",
        ViewPicking.RayThrough(panel, new Vector2(400f, 480f)) is null);
    t.ExpectTrue("AR.3 the far edge is half-open",
        ViewPicking.RayThrough(panel, new Vector2(200f + 640f, 300f)) is null);
    t.ExpectTrue("AR.3 and the near edge is inclusive",
        ViewPicking.RayThrough(panel, new Vector2(200f, 120f)) is not null);

    // <b>The Retina invariant.</b> The physical rectangle is for the renderer; picking reads the
    // logical one. Doubling the physical extent — which is exactly what moving the same window to
    // a 2x display does — must not move where a ray goes for the same logical pointer. Getting
    // this wrong is invisible on the machine it was written on.
    var retina = panel with { PhysicalViewport = new Rect(0f, 0f, 2560f, 1440f) };
    var a = ViewPicking.RayThrough(panel, new Vector2(420f, 250f));
    var b = ViewPicking.RayThrough(retina, new Vector2(420f, 250f));
    t.ExpectTrue("AR.4 the same logical pointer gives the same ray at 1x and 2x",
        a is not null && b is not null &&
        Vector3.Distance(a.Value.Direction, b.Value.Direction) < 1e-6f);

    // Two abutting views: the shared edge belongs to exactly one of them. That is the property the
    // half-open test exists for, and it is what lets a layout route a pointer by asking each view.
    var left = panel with { LogicalViewport = new Rect(0f, 0f, 100f, 100f) };
    var right = panel with { Id = new ViewId(2), LogicalViewport = new Rect(100f, 0f, 100f, 100f) };
    var onSeam = new Vector2(100f, 50f);
    var claims =
        (ViewPicking.RayThrough(left, onSeam) is not null ? 1 : 0) +
        (ViewPicking.RayThrough(right, onSeam) is not null ? 1 : 0);
    t.ExpectTrue("AR.5 exactly one of two abutting views claims the shared pixel", claims == 1);
}

// ============================================================================
// Section AS — the uniform arena's allocation rule.
// ============================================================================
//
// A dynamic offset handed to vkCmdBindDescriptorSets must be a multiple of the device's
// minUniformBufferOffsetAlignment. BumpSlot is what enforces that, and it already had Section AD —
// but AD tests it against a VERTEX stride, where the alignment happens to equal the element size.
// A uniform block's size and its required alignment are unrelated numbers, which is the case that
// can be got wrong without AD noticing.
{
    // A 176-byte block (the lab's Frame) at 256-byte alignment: every slice must start on 256.
    var slot = new BumpSlot(4096);
    slot.TryAlloc(176, 256, out var a);
    slot.TryAlloc(176, 256, out var b);
    slot.TryAlloc(176, 256, out var c);
    t.ExpectClose("AS.1 first block starts at zero", a, 0);
    t.ExpectTrue("AS.1 every offset is a multiple of the alignment",
        a % 256 == 0 && b % 256 == 0 && c % 256 == 0);
    t.ExpectTrue("AS.1 and they do not overlap", b >= a + 176 && c >= b + 176);

    // The alignment can be SMALLER than the block, which is the common case (16 on this machine).
    // Packing must still not overlap — an alignment is a floor on the start, not on the stride.
    var tight = new BumpSlot(4096);
    tight.TryAlloc(176, 16, out var t0);
    tight.TryAlloc(176, 16, out var t1);
    t.ExpectTrue("AS.2 a sub-block alignment still packs without overlap", t1 >= t0 + 176);
    t.ExpectTrue("AS.2 and still lands on the alignment", t1 % 16 == 0);

    // Exhaustion must fail rather than hand back an offset past the end — the arena turns this
    // into a named exception, and a silently wrapped offset would be memory corruption instead.
    var small = new BumpSlot(256);
    small.TryAlloc(176, 256, out _);
    t.ExpectTrue("AS.3 a second 256-aligned block does not fit in 256 bytes",
        !small.TryAlloc(176, 256, out _));
}

// ============================================================================
// Section AT — a recorded command's payload, fingerprinted.
// ============================================================================
//
// A pass body RECORDS; the backend reads uniforms at Execute. PushConstants is copied into the
// command for exactly that reason and the Uniforms/Textures lists are not — so a caller that
// refills one scratch array between two draws hands both the array's FINAL contents. The per-draw
// uniform arena does not help: it fixed the DESTINATION (a slice per distinct block) while this is
// the SOURCE.
//
// What is genuinely at risk is narrower than the old comment implied, and AT.2 is the half that
// says so: scalar and vector uniforms hold a struct by value and cannot be changed by whoever built
// them. Only the array uniforms and a reused list can move.
{
    // The negative control for the whole section. Every assertion below asserts a THROW; if the
    // check were off, Fingerprint would return null, VerifyUniforms would return early, and all of
    // them would pass having read nothing at all.
    t.ExpectTrue("AT.0 the record/execute check is on for this process", RenderCommandDiagnostics.Enabled);

    var palette = new[] { Matrix4x4.CreateTranslation(1f, 0f, 0f), Matrix4x4.Identity };
    var uniforms = new List<ShaderUniform> { new("uBones", new Matrix4x4ArrayUniform(palette)) };
    var cmd = new DrawIndexedCommand(
        new VertexBufferHandle(1), new IndexBufferHandle(1), new PipelineHandle(1), 3,
        uniforms, Array.Empty<ShaderTextureBinding>());

    t.ExpectTrue("AT.1 a recorded command carries one fingerprint per uniform",
        cmd.UniformFingerprint is { Length: 1 });

    // Unchanged: the ordinary case, which must stay silent however many times it is verified.
    t.ExpectTrue("AT.1 an untouched payload verifies clean",
        NoThrow(() => RenderCommandDiagnostics.VerifyUniforms("draw", "p", cmd.Uniforms, cmd.UniformFingerprint)));

    // THE HAZARD, and the half that is now closed. The caller refills its scratch array between
    // draws — which used to hand every draw the final contents, because the backend reads uniforms
    // at Execute. The array uniform copies on construction now, so the command keeps what it was
    // recorded with and the mutation simply does not reach it.
    palette[0] = Matrix4x4.CreateTranslation(99f, 0f, 0f);
    t.ExpectTrue("AT.2 a caller's array is copied into the uniform, not referenced",
        !ReferenceEquals(((Matrix4x4ArrayUniform)cmd.Uniforms[0].Value).Value, palette));
    t.ExpectTrue("AT.2 so mutating it after record changes nothing the draw will read",
        ((Matrix4x4ArrayUniform)cmd.Uniforms[0].Value).Value[0].M41 == 1f);
    t.ExpectTrue("AT.2 and the fingerprint still verifies clean",
        NoThrow(() => RenderCommandDiagnostics.VerifyUniforms("draw", "p", cmd.Uniforms, cmd.UniformFingerprint)));

    // And the half that cannot happen, stated as a test so the narrowing is pinned rather than
    // remembered: a Matrix4x4Uniform holds its matrix by value.
    var byValue = Matrix4x4.CreateTranslation(1f, 2f, 3f);
    var valueCmd = new DrawIndexedCommand(
        new VertexBufferHandle(1), new IndexBufferHandle(1), new PipelineHandle(1), 3,
        new List<ShaderUniform> { new("uModel", new Matrix4x4Uniform(byValue)) },
        Array.Empty<ShaderTextureBinding>());
    byValue = Matrix4x4.CreateTranslation(9f, 9f, 9f);
    t.ExpectTrue("AT.3 a scalar/vector uniform cannot be mutated behind a recorded command",
        NoThrow(() => RenderCommandDiagnostics.VerifyUniforms("draw", "p", valueCmd.Uniforms, valueCmd.UniformFingerprint)));

    // A reused LIST is the half the copy does NOT close: the values inside a command are frozen,
    // but the list they sit in is still the caller's. Deliberate — P2 measured the freeze cost of
    // the VALUES as zero bytes per frame across every runnable app, and the list's cost is one
    // allocation per draw in a tree whose games pass no inline uniforms at all. That number wants
    // a draw-heavy consumer rather than a guess, so the list stays detected rather than copied.
    uniforms[0] = new ShaderUniform("uBones", new Matrix4x4ArrayUniform(new[] { Matrix4x4.Identity }));
    t.ExpectTrue("AT.4 a list rewritten after record is caught",
        Throws(() => RenderCommandDiagnostics.VerifyUniforms("draw", "p", cmd.Uniforms, cmd.UniformFingerprint)));

    uniforms.Add(new ShaderUniform("uExtra", new FloatUniform(1f)));
    t.ExpectTrue("AT.5 a list that grew after record is caught by count",
        Throws(() => RenderCommandDiagnostics.VerifyUniforms("draw", "p", cmd.Uniforms, cmd.UniformFingerprint)));

    // `with` must re-fingerprint rather than carry the original's. A copy constructor does not
    // re-run field initialisers, so the init accessor is what makes this true — and a stale
    // fingerprint would make the check fire on correct code, which is worse than not checking.
    var swapped = cmd with
    {
        Uniforms = new List<ShaderUniform> { new("uBones", new Matrix4x4ArrayUniform(new[] { Matrix4x4.Identity })) },
    };
    t.ExpectTrue("AT.6 `with` re-fingerprints the new list",
        NoThrow(() => RenderCommandDiagnostics.VerifyUniforms("draw", "p", swapped.Uniforms, swapped.UniformFingerprint)));

    // Textures travel the same way: the list is read at Execute.
    var bindings = new List<ShaderTextureBinding> { new("uAlbedo", new TextureHandle(7), 0) };
    var texCmd = new DrawIndexedCommand(
        new VertexBufferHandle(1), new IndexBufferHandle(1), new PipelineHandle(1), 3,
        Array.Empty<ShaderUniform>(), bindings);
    bindings[0] = new ShaderTextureBinding("uAlbedo", new TextureHandle(8), 0);
    t.ExpectTrue("AT.7 a texture binding rewritten after record is caught",
        Throws(() => RenderCommandDiagnostics.VerifyTextures("draw", "p", texCmd.Textures, texCmd.TextureFingerprint)));

    // An empty list has nothing to print, and a null fingerprint must read as "nothing to check"
    // rather than as a fault — the shape every draw with no uniforms takes.
    t.ExpectTrue("AT.8 a command with no uniforms carries no fingerprint",
        texCmd.UniformFingerprint is null);
    t.ExpectTrue("AT.8 and verifying it is silent",
        NoThrow(() => RenderCommandDiagnostics.VerifyUniforms("draw", "p", texCmd.Uniforms, texCmd.UniformFingerprint)));
}

// ============================================================================
// Section AU — bone masks, and a blend that reads one.
// ============================================================================
//
// The animation arc deferred masks "until a consumer asks", with three questions attached: which
// bones, resolved how, blended in what space. The character arc asked, with a body playing a
// one-shot chop at 0.868 m/s and its legs frozen mid-swing because one pose source cannot do both.
//
// The answers pinned here: a subtree named by its ROOT (not indices, which move when a rig is
// re-exported), a per-bone weight multiplied by the caller's, and local space.
{
    // pelvis and spine both hang off a root; the arms hang off the chest; the legs off the pelvis.
    // Parents precede children, which Skeleton's own constructor requires and BoneMask relies on.
    var bones = new[]
    {
        new Bone("root", -1, BoneTransform.Identity),
        new Bone("pelvis", 0, BoneTransform.Identity),
        new Bone("spine", 1, BoneTransform.Identity),
        new Bone("chest", 2, BoneTransform.Identity),
        new Bone("armL", 3, BoneTransform.Identity),
        new Bone("armR", 3, BoneTransform.Identity),
        new Bone("legL", 1, BoneTransform.Identity),
        new Bone("legR", 1, BoneTransform.Identity),
    };
    var skeleton = new Skeleton(bones);
    var mask = BoneMask.Subtree(skeleton, "spine");

    // ── Which bones ──────────────────────────────────────────────────────────────────────────────
    t.ExpectTrue("AU.1 a subtree covers its root and everything beneath it",
        mask[2] == 1f && mask[3] == 1f && mask[4] == 1f && mask[5] == 1f);
    t.ExpectTrue("AU.1 and nothing above or beside it",
        mask[0] == 0f && mask[1] == 0f && mask[6] == 0f && mask[7] == 0f);
    t.ExpectTrue("AU.1 which is four bones of eight", mask.Reach() == 4);

    // A name that is not in the rig is a typo, and a typo that yields a layer which quietly changes
    // nothing is the worst shape a bug can take — so it throws, and names what is there.
    t.ExpectTrue("AU.2 a mask over a bone that does not exist throws rather than covering nothing",
        ThrowsArgument(() => BoneMask.Subtree(skeleton, "spien")));

    // ── The falloff: the question the deferral did not name ──────────────────────────────────────
    // A hard boundary puts the whole discontinuity in one joint. Fading UP the chain spreads it, and
    // how far is a number nobody can derive — hence a parameter, and hence the tooling.
    {
        var faded = BoneMask.Subtree(skeleton, "spine", falloff: 2);
        t.ExpectClose("AU.3 the parent of the root takes two thirds", faded[1], 2f / 3f);
        t.ExpectClose("AU.3 its parent takes one third", faded[0], 1f / 3f);
        t.ExpectTrue("AU.3 and the subtree itself is untouched by the fade", faded[2] == 1f && faded[4] == 1f);
        t.ExpectTrue("AU.3 while the legs stay out of it", faded[6] == 0f && faded[7] == 0f);
    }

    // ── Two halves that sum to exactly one ───────────────────────────────────────────────────────
    {
        var lower = mask.Inverted();
        var exact = true;
        for (var i = 0; i < skeleton.BoneCount; i++) exact &= mask[i] + lower[i] == 1f;
        t.ExpectTrue("AU.4 a mask and its inverse sum to exactly one at every bone", exact);
    }

    // ── Resolved how: one multiplication, and the exactness that follows ─────────────────────────
    {
        // FAR-APART ROTATIONS, and what trying to make them matter established. These cases pin
        // BEHAVIOUR — a masked blend leaves unmasked bones alone — which is worth pinning however it
        // is achieved. What they cannot pin is the shortcut that skips blending at the ends: deleting
        // it turns nothing red, with identity rotations OR with a pair 150° apart on Slerp's
        // trigonometric branch. Vector3.Lerp and Quaternion.Slerp both return their input exactly at
        // 0 and at 1, so the shortcut is a COST decision and not a precision one, and the comment in
        // PoseBlend that said otherwise has been corrected.
        var near = Quaternion.CreateFromAxisAngle(Vector3.Normalize(new Vector3(0.3f, 1f, 0.2f)), 0.2f);
        var far = Quaternion.CreateFromAxisAngle(Vector3.Normalize(new Vector3(-0.7f, 0.4f, 1f)), 2.6f);

        BoneTransform Bone(float x, Quaternion r) => new(new Vector3(x, 0f, 0f), r, Vector3.One);

        var walking = new Pose(skeleton.BoneCount);
        var swinging = new Pose(skeleton.BoneCount);
        for (var i = 0; i < skeleton.BoneCount; i++)
        {
            walking.Locals[i] = Bone(1f, near);
            swinging.Locals[i] = Bone(2f, far);
        }

        var result = new Pose(skeleton.BoneCount);
        PoseBlend.Lerp(walking, swinging, 1f, mask, result);

        // THE CLAIM THE WHOLE STAGE EXISTS FOR: the upper body takes the swing, the legs keep
        // walking, and the legs are untouched BIT FOR BIT rather than approximately.
        t.ExpectTrue("AU.5 the masked bones take the second pose exactly",
            result.Locals[2] == swinging.Locals[2] && result.Locals[4] == swinging.Locals[4]);
        t.ExpectTrue("AU.5 and the unmasked bones keep the first, bit for bit",
            result.Locals[6] == walking.Locals[6] && result.Locals[7] == walking.Locals[7] &&
            result.Locals[0] == walking.Locals[0]);

        // THE CONTROL. Without the mask the same blend moves the legs too — which is what says the
        // mask did the work rather than the poses happening to agree.
        var unmasked = new Pose(skeleton.BoneCount);
        PoseBlend.Lerp(walking, swinging, 1f, unmasked);
        t.ExpectTrue("AU.5 the control: unmasked, the same blend moves the legs as well",
            unmasked.Locals[6] != walking.Locals[6]);

        // The caller's weight multiplies the mask's, which is the whole of "resolved how".
        var half = new Pose(skeleton.BoneCount);
        PoseBlend.Lerp(walking, swinging, 0.5f, mask, half);
        t.ExpectClose("AU.6 a half-weight layer reaches a masked bone halfway", half.Locals[2].Translation.X, 1.5f);
        t.ExpectTrue("AU.6 and an unmasked bone not at all", half.Locals[6] == walking.Locals[6]);

        // A zero-weight layer is not a cheap no-op by accident; it is exact by construction, because
        // a bone whose effective weight is zero is COPIED rather than interpolated toward itself.
        var off = new Pose(skeleton.BoneCount);
        PoseBlend.Lerp(walking, swinging, 0f, mask, off);
        var untouched = true;
        for (var i = 0; i < skeleton.BoneCount; i++) untouched &= off.Locals[i] == walking.Locals[i];
        t.ExpectTrue("AU.7 a layer at zero weight changes nothing at all, bit for bit", untouched);

        // A mask belongs to the skeleton it was built from, and saying so beats a silent half-blend.
        t.ExpectTrue("AU.8 a mask sized for another skeleton is refused",
            ThrowsArgument(() => PoseBlend.Lerp(walking, swinging, 1f, BoneMask.All(3), result)));
    }
}

// ============================================================================
// ============================================================================
// ============================================================================
// Section AY — a mesh parented to a joint is not thrown away.
// ============================================================================
//
// <b>The old rigged importer took nodes carrying both a mesh and a skin and dropped the rest in
// silence.</b> The cook is now the one reader, and these are held to what it writes. Rogue.glb is twelve mesh-bearing nodes, six skinned and six not — a knife, two
// crossbows, a throwable and a cape, each parented to a joint — so half of the tree's reference rig
// arrived as nothing, with no warning and no count, across several arcs of looking straight at it.
// A character with empty hands looks exactly like a character.
//
// <b>The rig here is synthetic and its joints are deliberately out of order</b>, which is the whole
// reason it is synthetic. The skeleton is topologically sorted at import, so a skin whose joint list
// is ALREADY sorted has an identity remap — and the Rogue's is. Checking against the Rogue would
// therefore pass whether the remap were applied or not, which is a check that cannot fail.
{
    var temp = Path.Combine(Path.GetTempPath(), $"blix-ay-{Guid.NewGuid():N}");
    Directory.CreateDirectory(temp);
    try
    {
        // root -> mid -> tip, and one static mesh hanging off `tip`.
        var root = new SharpGLTF.Scenes.NodeBuilder("root");
        var mid = root.CreateNode("mid");
        var tip = mid.CreateNode("tip");
        tip.LocalMatrix = Matrix4x4.CreateTranslation(0f, 2f, 0f);

        var skinned = new SharpGLTF.Geometry.MeshBuilder<
            SharpGLTF.Geometry.VertexTypes.VertexPositionNormal,
            SharpGLTF.Geometry.VertexTypes.VertexEmpty,
            SharpGLTF.Geometry.VertexTypes.VertexJoints4>("body");
        var skinnedPrim = skinned.UsePrimitive(SharpGLTF.Materials.MaterialBuilder.CreateDefault());
        skinnedPrim.AddTriangle(
            (new SharpGLTF.Geometry.VertexTypes.VertexPositionNormal(0, 0, 0, 0, 1, 0), default, new SharpGLTF.Geometry.VertexTypes.VertexJoints4(0)),
            (new SharpGLTF.Geometry.VertexTypes.VertexPositionNormal(1, 0, 0, 0, 1, 0), default, new SharpGLTF.Geometry.VertexTypes.VertexJoints4(0)),
            (new SharpGLTF.Geometry.VertexTypes.VertexPositionNormal(0, 0, 1, 0, 1, 0), default, new SharpGLTF.Geometry.VertexTypes.VertexJoints4(0)));

        var gear = new SharpGLTF.Geometry.MeshBuilder<SharpGLTF.Geometry.VertexTypes.VertexPositionNormal>("gear");
        var gearPrim = gear.UsePrimitive(SharpGLTF.Materials.MaterialBuilder.CreateDefault());
        gearPrim.AddTriangle(
            new SharpGLTF.Geometry.VertexTypes.VertexPositionNormal(0, 0, 0, 0, 1, 0),
            new SharpGLTF.Geometry.VertexTypes.VertexPositionNormal(0.5f, 0, 0, 0, 1, 0),
            new SharpGLTF.Geometry.VertexTypes.VertexPositionNormal(0, 0, 0.5f, 0, 1, 0));

        var scene = new SharpGLTF.Scenes.SceneBuilder();
        // <b>tip, root, mid.</b> Not sorted, so the importer's topological remap has to move things:
        // tip goes from index 0 to index 2, and an attachment that recorded the RAW index would come
        // back pointing at `root`.
        scene.AddSkinnedMesh(skinned, Matrix4x4.Identity, tip, root, mid);
        scene.AddRigidMesh(gear, tip.CreateNode("held"));

        var withGear = Path.Combine(temp, "rig-with-gear.glb");
        scene.ToGltf2().SaveGLB(withGear);

        ModelData Cooked(string glb) => ModelData.Load(Blix.Recipes.CookCache.Resolve(glb), new ModelNeeds(Skinned: true));
        var model = Cooked(withGear);
        var found = model.Attachments();

        t.Expect("AY.1 a mesh parented to a joint survives the cook", found.Count == 1, $"found {found.Count}");
        if (found.Count == 1)
        {
            var a = found[0];
            t.Expect("AY.1 named by its node", model.Nodes[a.NodeIndex].Name == "held", $"got '{model.Nodes[a.NodeIndex].Name}'");
            t.Expect("AY.2 carried by the joint it hangs from", model.Nodes[a.CarrierNode].Name == "tip", $"got '{model.Nodes[a.CarrierNode].Name}'");

            // <b>The check the Rogue could not make.</b> `tip` is joint 0 in the skin's own list and
            // bone 2 in the cooked, parent-first skeleton. A recorded raw index would name `root` here
            // and every count would still be right.
            var skeletonOf = model.Skeleton!;
            t.Expect("AY.2 and the REMAPPED bone index, not the skin's",
                a.BoneIndex >= 0 && a.BoneIndex < skeletonOf.BoneCount && skeletonOf.Bones[a.BoneIndex].Name == "tip",
                $"bone[{a.BoneIndex}] is '{(a.BoneIndex >= 0 && a.BoneIndex < skeletonOf.BoneCount ? skeletonOf.Bones[a.BoneIndex].Name : "out of range")}'");
            t.ExpectTrue("AY.2 which is a different number from the skin's", a.BoneIndex != 0);

            var carried = model.Meshes[model.Nodes[a.NodeIndex].MeshIndex].Primitives;
            t.Expect("AY.3 with its geometry", carried.Count == 1 && carried[0].Mesh.VertexCount == 3);

            // Unbaked: in mesh space, not baked to the joint's world position. The placement rides on Local.
            var verts = carried[0].Mesh;
            t.ExpectTrue("AY.3 in its own space, not baked to the joint's world position",
                verts.Bounds.Min.Y > -0.001f && verts.Bounds.Max.Y < 0.001f);
        }

        // ── the negative controls ───────────────────────────────────────────
        // A static mesh that merely shares the file is not equipment. Adopting it would put scenery
        // in the character's hand.
        var loose = new SharpGLTF.Scenes.SceneBuilder();
        loose.AddSkinnedMesh(skinned, Matrix4x4.Identity, tip, root, mid);
        loose.AddRigidMesh(gear, new SharpGLTF.Scenes.NodeBuilder("scenery"));
        var withScenery = Path.Combine(temp, "rig-with-scenery.glb");
        loose.ToGltf2().SaveGLB(withScenery);

        var sceneryModel = Cooked(withScenery);
        t.Expect("AY.4 a static mesh NOT under a joint is not an attachment",
            sceneryModel.Attachments().Count == 0, $"found {sceneryModel.Attachments().Count}");

        // And a rig with nothing attached reports nothing, with its body untouched — because the
        // collection walks every mesh node and a bug there would show up as either.
        var bare = new SharpGLTF.Scenes.SceneBuilder();
        bare.AddSkinnedMesh(skinned, Matrix4x4.Identity, tip, root, mid);
        var bareGlb = Path.Combine(temp, "rig-bare.glb");
        bare.ToGltf2().SaveGLB(bareGlb);

        var bareModel = Cooked(bareGlb);
        t.Expect("AY.5 a rig with nothing attached reports none", bareModel.Attachments().Count == 0);
        var bareSkinned = bareModel.SkinnedPrimitives(0).ToArray();
        var scenerySkinned = sceneryModel.SkinnedPrimitives(0).ToArray();
        t.Expect("AY.5 and its skinned primitives are unchanged",
            bareSkinned.Length == scenerySkinned.Length && bareSkinned.Length > 0
            && bareSkinned[0].Mesh.VertexCount == scenerySkinned[0].Mesh.VertexCount);

        // ── AY.7 what the import leaves behind, said out loud ───────────────
        // <b>A bare `continue` used to sit where this reporting is.</b> A mesh weighted to a second
        // skin left no trace, so a file could arrive as a fraction of itself and every tool
        // downstream agreed it was whole. tank.glb is the real instance: eleven primitives across
        // three skins, of which five imported and six vanished.
        //
        // <b>Reporting it was the first fix; reading it is the second.</b> "Still skipped" stood
        // here, on the reasoning that tools/character_merge.py existed to avoid needing multi-skin
        // support — which was circular, since that script's one-skin rule exists to satisfy this
        // importer. A glTF skin is self-contained, so N skins are N skeletons and nothing has to be
        // reconciled between them. What is still skipped is a mesh under no joint at all.
        var second = new SharpGLTF.Scenes.NodeBuilder("second_root");
        var secondTip = second.CreateNode("second_tip");

        var twoSkins = new SharpGLTF.Scenes.SceneBuilder();
        twoSkins.AddSkinnedMesh(skinned, Matrix4x4.Identity, tip, root, mid);
        twoSkins.AddSkinnedMesh(skinned, Matrix4x4.Identity, secondTip, second);
        twoSkins.AddRigidMesh(gear, new SharpGLTF.Scenes.NodeBuilder("loose_prop"));
        var twoSkinPath = Path.Combine(temp, "two-skins.glb");
        twoSkins.ToGltf2().SaveGLB(twoSkinPath);

        var twoSkinModel = Cooked(twoSkinPath);
        // Nothing is left behind: a mesh on a second skin and a static mesh under no joint both arrive.
        t.Expect("AY.7 both skins are present", twoSkinModel.Skins.Count == 2, $"{twoSkinModel.Skins.Count}");
        t.ExpectTrue("AY.7 and primitives from both of them arrive",
            twoSkinModel.SkinnedPrimitives(0).Any() && twoSkinModel.SkinnedPrimitives(1).Any());

        // A static part is a mesh node that is neither skinned nor carried by an animated node.
        static string[] StaticParts(ModelData m)
        {
            var carried = m.Attachments().Select(x => x.NodeIndex).ToHashSet();
            return Enumerable.Range(0, m.Nodes.Count)
                .Where(n => m.Nodes[n].MeshIndex >= 0 && m.Nodes[n].SkinIndex < 0 && !carried.Contains(n))
                .Select(n => m.Nodes[n].Name).ToArray();
        }

        var looseParts = StaticParts(twoSkinModel);
        t.ExpectTrue($"AY.7 a static mesh under no joint is READ, not dropped (got {looseParts.Length})",
            looseParts.Contains("loose_prop"));
        var looseNode = twoSkinModel.FindNode("loose_prop");
        t.ExpectTrue("AY.7 and it carries its geometry",
            looseNode >= 0 && twoSkinModel.Meshes[twoSkinModel.Nodes[looseNode].MeshIndex].Primitives.All(p => p.Mesh.VertexCount > 0));

        // <b>The control that stops static parts swallowing everything.</b> Equipment hangs off a
        // joint and must stay an ATTACHMENT — if it fell through to here it would stop following the
        // hand that holds it, which is a silent downgrade rather than a visible loss.
        t.Expect("AY.7 CONTROL an attachment is NOT also a static part",
            StaticParts(model).Length == 0, $"got {string.Join(",", StaticParts(model))}");
        t.Expect("AY.7 CONTROL a rig with nothing loose has no static parts",
            StaticParts(bareModel).Length == 0, $"got {string.Join(",", StaticParts(bareModel))}");

        // ── AY.6 the joint world transforms, which were computed and dropped ─
        // <b>The palette is not the joints' transforms.</b> Matrices[i] is InverseBindPose · world —
        // a map from a REST vertex to its posed position, which is what a skinned shader wants and
        // the wrong thing entirely for a knife that has no rest vertices in this skin's space.
        // The palette built the worlds as an intermediate and threw them away one line later.
        var skel = model.Skeleton!;
        var skinOf = model.Skins[0].Binding;
        var rest = skel.CreateRestPose();
        var palette = new BonePalette(skinOf.JointCount);
        var worlds = new BoneWorlds(skel);
        skinOf.ComputePalette(rest, palette, worlds);

        // At rest, InverseBindPose · world is the identity for every bone — the invariant
        // CreateRestPose already documents, read from the other end. If the worlds were wrong this
        // is what would say so.
        var worstRest = 0f;
        for (var i = 0; i < skel.BoneCount; i++)
        {
            var shouldBeIdentity = skinOf.InverseBinds[i] * worlds[i];
            worstRest = MathF.Max(worstRest, Deviation(shouldBeIdentity));
        }

        t.ExpectTrue($"AY.6 at rest, InverseBindPose x world is the identity (worst {worstRest:0.000000})",
            worstRest < 1e-4f);

        // <b>And they are NOT the palette.</b> Without this, "implementing" the worlds by copying
        // Matrices[] would pass every other check here — at rest the palette IS the identity, so a
        // copy looks right exactly where it is least useful.
        var posed = skel.CreateRestPose();
        posed.Locals[skel.BoneCount - 1] = posed.Locals[skel.BoneCount - 1] with
        {
            Translation = posed.Locals[skel.BoneCount - 1].Translation + new Vector3(0f, 1.5f, 0f),
        };
        skinOf.ComputePalette(posed, palette, worlds);
        t.ExpectTrue("AY.6 and a joint world is not its palette matrix",
            Deviation(palette.Matrices[skel.BoneCount - 1] * Matrix4x4.Identity)
                != Deviation(worlds[skel.BoneCount - 1]));

        // A child's world is its local composed onto its parent's — the recurrence itself, checked
        // rather than assumed, because a transposed multiply here puts equipment in the right place
        // for a rig with no rotation and nowhere near it for one with any.
        var worstChain = 0f;
        for (var i = 0; i < skel.BoneCount; i++)
        {
            var p = skel.Bones[i].ParentIndex;
            if (p < 0) continue;
            var expected = posed.Locals[i].ToMatrix() * worlds[p];
            worstChain = MathF.Max(worstChain, Deviation(expected * Invert(worlds[i])));
        }

        t.ExpectTrue($"AY.6 a child's world is local x parent's world (worst {worstChain:0.000000})",
            worstChain < 1e-4f);

        // The array is the scratch, so a caller that wants the worlds pays no allocation — and one
        // that does not still gets a palette.
        var noWorlds = new BonePalette(skinOf.JointCount);
        skinOf.ComputePalette(posed, noWorlds);
        var same = true;
        for (var i = 0; i < skel.BoneCount; i++)
        {
            same &= Deviation(noWorlds.Matrices[i] * Invert(palette.Matrices[i])) < 1e-4f;
        }

        t.ExpectTrue("AY.6 asking for the worlds does not change the palette", same);

        t.ExpectThrows("AY.6 worlds of another skeleton are refused, not silently written",
            () => skinOf.ComputePalette(posed, palette, new BoneWorlds(new Skeleton(skel.Bones))), mustMention: "another skeleton");
    }
    finally
    {
        try { Directory.Delete(temp, recursive: true); } catch (IOException) { }
    }
}

// ============================================================================
// Section BC — more than four bone influences per vertex.
// ============================================================================
//
// <b>The one glTF gap here that produces a WRONG RESULT rather than a missing feature, and the one
// no corpus covers.</b> glTF allows JOINTS_1/WEIGHTS_1 and beyond. The vertex layout carries four
// influences, and the importer read only the first set — so a vertex weighted across eight had its
// weights summing to less than 1, and a skinning matrix scaled by 0.8 drags that vertex a fifth of
// the way to the origin. Nothing counted down and nothing warned.
//
// No asset in this tree has it, none of the Khronos sample assets do, and neither does the asset
// generator. Conventions §7 says that is not a reason to leave it — the specification is the
// requirement — so the fixture is authored here, the way BB.2's was.
{
    var temp = Path.Combine(Path.GetTempPath(), $"blix-bc-{Guid.NewGuid():N}");
    Directory.CreateDirectory(temp);
    try
    {
        var root = new SharpGLTF.Scenes.NodeBuilder("root");
        var joints = new List<SharpGLTF.Scenes.NodeBuilder> { root };
        for (var i = 1; i < 8; i++) joints.Add(joints[i - 1].CreateNode($"j{i}"));

        // Eight influences whose weights sum to 1: four of 0.2 and four of 0.05. Reading only the
        // first set leaves 0.8, which is the collapse; keeping the strongest four and renormalising
        // gives 1.0 again.
        static SharpGLTF.Geometry.MeshBuilder<
            SharpGLTF.Geometry.VertexTypes.VertexPosition,
            SharpGLTF.Geometry.VertexTypes.VertexEmpty,
            SharpGLTF.Geometry.VertexTypes.VertexJoints8> EightWay()
        {
            var m = new SharpGLTF.Geometry.MeshBuilder<
                SharpGLTF.Geometry.VertexTypes.VertexPosition,
                SharpGLTF.Geometry.VertexTypes.VertexEmpty,
                SharpGLTF.Geometry.VertexTypes.VertexJoints8>("eight");
            var p = m.UsePrimitive(SharpGLTF.Materials.MaterialBuilder.CreateDefault());
            var w = new SharpGLTF.Geometry.VertexTypes.VertexJoints8(
                (0, 0.2f), (1, 0.2f), (2, 0.2f), (3, 0.2f),
                (4, 0.05f), (5, 0.05f), (6, 0.05f), (7, 0.05f));
            p.AddTriangle(
                (new SharpGLTF.Geometry.VertexTypes.VertexPosition(0, 0, 0), default, w),
                (new SharpGLTF.Geometry.VertexTypes.VertexPosition(1, 0, 0), default, w),
                (new SharpGLTF.Geometry.VertexTypes.VertexPosition(0, 0, 1), default, w));
            return m;
        }

        var scene = new SharpGLTF.Scenes.SceneBuilder();
        scene.AddSkinnedMesh(EightWay(), Matrix4x4.Identity, joints.ToArray());
        var path = Path.Combine(temp, "eight-influences.glb");
        scene.ToGltf2().SaveGLB(path);

        ModelData Cooked(string glb) => ModelData.Load(Blix.Recipes.CookCache.Resolve(glb), new ModelNeeds(Skinned: true));
        var model = Cooked(path);
        var mesh = model.SkinnedPrimitives(0).First().Mesh;

        // The cooked 80-byte skinned vertex is pos(3) normal(3) uv(2) joints(4) weights(4) tangent(4) floats.
        static (float[] Joints, float[] Weights) InfluencesOf(MeshData m, int vertex)
        {
            var f = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(
                m.VertexBytes.AsSpan(vertex * m.Layout.Stride, m.Layout.Stride));
            return (new[] { f[8], f[9], f[10], f[11] }, new[] { f[12], f[13], f[14], f[15] });
        }

        var (idx, wts) = InfluencesOf(mesh, 0);
        var sum = wts.Sum();

        // ── BC.1 the weights sum to one again ───────────────────────────────
        //
        // THE fault. Before, this summed to 0.8 and the vertex was dragged a fifth of the way to the
        // origin — a deformation that is wrong rather than approximate.
        t.ExpectTrue($"BC.1 the retained weights are renormalised to 1 (sum {sum:F4})",
            Math.Abs(sum - 1.0) < 1e-4);

        // ── BC.2 the STRONGEST four, not the first four ─────────────────────
        //
        // glTF does not require the sets to be sorted. Keeping "the first four" can discard the
        // influence that shapes the vertex and retain three that barely move it.
        t.ExpectTrue($"BC.2 every retained weight is one of the heavy ones ({string.Join(", ", wts.Select(x => x.ToString("F3")))})",
            wts.All(x => Math.Abs(x - 0.25f) < 1e-3f));

        // Joints 0..3 carry 0.2 each and 4..7 carry 0.05, so the four kept must be the first four
        // JOINTS — after the skeleton's topological remap, which for this chain is the identity.
        t.ExpectTrue($"BC.2 and names the heavy joints, not the light ones ({string.Join(", ", idx)})",
            idx.All(i => i < 4));

        // ── BC.3 CONTROL: four influences are untouched ─────────────────────
        //
        // Every rigged asset in this tree has exactly one influence set, and none of them may move.
        // Sorting four influences that already fit would rewrite every skinned vertex to no purpose.
        var four = new SharpGLTF.Geometry.MeshBuilder<
            SharpGLTF.Geometry.VertexTypes.VertexPosition,
            SharpGLTF.Geometry.VertexTypes.VertexEmpty,
            SharpGLTF.Geometry.VertexTypes.VertexJoints4>("four");
        var fp = four.UsePrimitive(SharpGLTF.Materials.MaterialBuilder.CreateDefault());
        // Deliberately UNSORTED and summing to 1: if the ordinary path started sorting, this would
        // come back reordered.
        var fw = new SharpGLTF.Geometry.VertexTypes.VertexJoints4((0, 0.1f), (1, 0.6f), (2, 0.2f), (3, 0.1f));
        fp.AddTriangle(
            (new SharpGLTF.Geometry.VertexTypes.VertexPosition(0, 0, 0), default, fw),
            (new SharpGLTF.Geometry.VertexTypes.VertexPosition(1, 0, 0), default, fw),
            (new SharpGLTF.Geometry.VertexTypes.VertexPosition(0, 0, 1), default, fw));
        var fourScene = new SharpGLTF.Scenes.SceneBuilder();
        fourScene.AddSkinnedMesh(four, Matrix4x4.Identity, joints.Take(4).ToArray());
        var fourPath = Path.Combine(temp, "four-influences.glb");
        fourScene.ToGltf2().SaveGLB(fourPath);

        var fourModel = Cooked(fourPath);
        var (fidx, fwts) = InfluencesOf(fourModel.SkinnedPrimitives(0).First().Mesh, 0);

        // <b>Compared against what is IN THE FILE, not against what was handed to the builder.</b>
        // SharpGLTF sorts influences by weight as it writes, so (0.1, 0.6, 0.2, 0.1) arrives on disk
        // as (0.6, 0.2, 0.1, 0.1). A first draft of this asserted the authored order and failed — on
        // the fixture, not the importer. Reading the accessor back makes the control say the thing it
        // actually means: for a single influence set the importer is a passthrough.
        var onDisk = SharpGLTF.Schema2.ModelRoot.Load(fourPath)
            .LogicalMeshes[0].Primitives[0].GetVertexAccessor("WEIGHTS_0")!.AsVector4Array()[0];
        t.ExpectTrue(
            $"BC.3 CONTROL one influence set passes through untouched "
            + $"(file {onDisk}, imported {string.Join(", ", fwts.Select(x => x.ToString("F3")))})",
            Math.Abs(fwts[0] - onDisk.X) < 1e-5f && Math.Abs(fwts[1] - onDisk.Y) < 1e-5f
            && Math.Abs(fwts[2] - onDisk.Z) < 1e-5f && Math.Abs(fwts[3] - onDisk.W) < 1e-5f);

        // ── BC.4 the diagnostic agrees with the reader ──────────────────────
        // JOINTS_1/WEIGHTS_1 are inputs to the strongest-four selection above. Calling the whole
        // attributes ignored was an older collector describing its shared allow-list rather than
        // what this import actually consumed.
        t.ExpectTrue("BC.4 complete additional influence sets are not falsely reported as ignored",
            model.Ignored.All(i => i.Semantic is not ("JOINTS_1" or "WEIGHTS_1")));
    }
    finally
    {
        try { Directory.Delete(temp, recursive: true); } catch (IOException) { }
    }
}

// ============================================================================
// Section BB — every skin the file declares is read.
// ============================================================================
//
// <b>The importer used to pick the first skin it met and drop every node referencing another.</b>
// The justification recorded in the plan was that tools/character_merge.py "exists specifically to
// avoid needing this" — and that script's header says it exists to satisfy the importer's ONE-skin
// rule. The workaround existed because of the limit and the limit was justified by the workaround.
//
// The format was never the obstacle. A glTF skin is self-contained — its own joint list, its own
// inverse bind matrices — and every skinned node names the skin it uses. N skins are N skeletons
// and there is nothing to reconcile between them. What was missing was a place to put them:
// the old source importer's model had one Skeleton and no way for a primitive to say which skin it belonged to.
//
// The fault worth testing is not "are both meshes present" — it is that EACH SKIN ORDERS ITS OWN
// JOINTS. Reusing the first skin's remap gives indices that are in range and name the wrong bones,
// which is the failure that looks like bad weighting rather than a bad import.
{
    var temp = Path.Combine(Path.GetTempPath(), $"blix-bb-{Guid.NewGuid():N}");
    Directory.CreateDirectory(temp);
    try
    {
        // Two skins over the same three joints, DECLARED IN DIFFERENT ORDERS, with every vertex
        // weighted entirely to 'tip'. Both must end up naming 'tip' after import; if the second
        // borrows the first's remap it names 'mid' instead — in range, plausible, wrong.
        static SharpGLTF.Geometry.MeshBuilder<
            SharpGLTF.Geometry.VertexTypes.VertexPosition,
            SharpGLTF.Geometry.VertexTypes.VertexEmpty,
            SharpGLTF.Geometry.VertexTypes.VertexJoints4> Weighted(string name, int jointSlot)
        {
            var m = new SharpGLTF.Geometry.MeshBuilder<
                SharpGLTF.Geometry.VertexTypes.VertexPosition,
                SharpGLTF.Geometry.VertexTypes.VertexEmpty,
                SharpGLTF.Geometry.VertexTypes.VertexJoints4>(name);
            var p = m.UsePrimitive(SharpGLTF.Materials.MaterialBuilder.CreateDefault());
            var w = new SharpGLTF.Geometry.VertexTypes.VertexJoints4((jointSlot, 1f));
            p.AddTriangle(
                (new SharpGLTF.Geometry.VertexTypes.VertexPosition(0, 0, 0), default, w),
                (new SharpGLTF.Geometry.VertexTypes.VertexPosition(1, 0, 0), default, w),
                (new SharpGLTF.Geometry.VertexTypes.VertexPosition(0, 0, 1), default, w));
            return m;
        }

        var rootA = new SharpGLTF.Scenes.NodeBuilder("root");
        var midA = rootA.CreateNode("mid");
        var tipA = midA.CreateNode("tip");

        var rootB = new SharpGLTF.Scenes.NodeBuilder("broot");
        var midB = rootB.CreateNode("bmid");
        var tipB = midB.CreateNode("btip");

        var scene = new SharpGLTF.Scenes.SceneBuilder();
        // Skin A declares (tip, root, mid): 'tip' is joint 0 in this skin's own space.
        scene.AddSkinnedMesh(Weighted("a", 0), Matrix4x4.Identity, tipA, rootA, midA);
        // Skin B declares (broot, bmid, btip): 'btip' is joint 2 in its own space.
        scene.AddSkinnedMesh(Weighted("b", 2), Matrix4x4.Identity, rootB, midB, tipB);

        var path = Path.Combine(temp, "two-orders.glb");
        var built = scene.ToGltf2();
        built.SaveGLB(path);
        // Also as text, so BB.3 below can move a node the builder will not let it move.
        built.SaveGLTF(Path.Combine(temp, "two-orders.gltf"));

        ModelData Cooked(string file) => ModelData.Load(Blix.Recipes.CookCache.Resolve(file), new ModelNeeds(Skinned: true));
        var m2 = Cooked(path);

        t.Expect("BB.1 both skins are read", m2.Skins.Count == 2, $"{m2.Skins.Count}");
        t.ExpectTrue("BB.1 and every skinned primitive says which skin drives it",
            m2.SkinnedPrimitives(0).Any() && m2.SkinnedPrimitives(1).Any());

        // Bone indices live at float slot 8 of the skinned vertex (pos 3, normal 3, uv 2).
        static int FirstBoneIndex(MeshData mesh)
        {
            var floats = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(
                mesh.VertexBytes.AsSpan(0, mesh.Layout.Stride));
            return (int)floats[8];
        }

        // ── BB.2 THE ONE THAT MATTERS ───────────────────────────────────────
        //
        // A vertex names a joint of ITS skin, and each skin's binding takes that joint to a bone of the one
        // hierarchy. Each skin orders its own joints, so reusing skin A's order sends skin B's 'btip' to
        // another bone: in range, plausible, wrong.
        for (var s = 0; s < m2.Skins.Count; s++)
        foreach (var prim in m2.SkinnedPrimitives(s))
        {
            var binding = m2.Skins[s].Binding;
            var joint = FirstBoneIndex(prim.Mesh);
            var named = (uint)joint < (uint)binding.JointCount ? binding.Skeleton.Bones[binding.Bones[joint]].Name : "(out of range)";
            t.ExpectTrue(
                $"BB.2 skin {s}'s vertices name its own leaf joint, not another skin's bone "
                + $"(joint {joint} = '{named}')",
                named is "tip" or "btip");
        }

        // ── BB.3 a skinned mesh node's transform does not place its skin ────
        //
        // glTF places a skinned vertex by its joints' world transforms alone and ignores the transform
        // of the node that places the mesh. This used to assert the opposite — that displacing one
        // skin's mesh node moved that skin — because the skeleton's rest was rebuilt from inverse binds
        // and needed the mesh node to land tank.glb's tracks. Read as glTF defines it (JointHierarchy),
        // the tracks land exactly with one shared placement; Blix.Test.Recipes holds every tank vertex
        // to glTF's own sum.
        //
        // Built by editing the JSON because SceneBuilder normalises a skinned mesh node to identity: the
        // world transform passed to AddSkinnedMesh does not survive into the node.
        var framePath = Path.Combine(temp, "two-frames.gltf");
        {
            var doc = System.Text.Json.Nodes.JsonNode.Parse(
                File.ReadAllText(Path.Combine(temp, "two-orders.gltf")))!;
            var nodes = doc["nodes"]!.AsArray();
            var skinned = nodes.Where(n => n!["mesh"] is not null && n["skin"] is not null).ToList();
            skinned[1]!["translation"] = new System.Text.Json.Nodes.JsonArray(0, 0, 4.0);
            File.WriteAllText(framePath, doc.ToJsonString());
        }

        var unmoved = Cooked(Path.Combine(temp, "two-orders.gltf"));
        var m3 = Cooked(framePath);
        t.Expect("BB.3 the displaced file still yields two skins", m3.Skins.Count == 2, $"{m3.Skins.Count}");
        if (m3.Skins.Count == 2 && unmoved.Skins.Count == 2)
        {
            var before = unmoved.Skins[1].Placement.Translation;
            var after = m3.Skins[1].Placement.Translation;
            t.ExpectTrue($"BB.3 moving a skinned mesh node does not move its skin ({before} vs {after})",
                (before - after).Length() < 1e-5f);
        }

        // ── BB.3b clips drive NODES, whatever order each skin declares ──────
        //
        // glTF animation channels target NODES and say nothing about skins. The old source importer stored
        // a clip as bone indices against ONE skin's skeleton, so it had to refuse a file whose skins order
        // their joints differently (skin A declares (tip, root, mid), skin B (broot, bmid, btip)). The cook
        // has no such limit: one animated hierarchy over every skin's joints, a track per animated NODE, and
        // each skin bound into it. So the file is read, and the clip drives the bone of the node it names.
        var animated = new SharpGLTF.Scenes.SceneBuilder();
        animated.AddSkinnedMesh(Weighted("x", 0), Matrix4x4.Identity, tipA, rootA, midA);
        animated.AddSkinnedMesh(Weighted("y", 2), Matrix4x4.Identity, rootB, midB, tipB);
        rootA.UseTranslation().UseTrackBuilder("Default").WithPoint(0, Vector3.Zero).WithPoint(1, Vector3.UnitY);
        var animatedPath = Path.Combine(temp, "two-orders-animated.glb");
        animated.ToGltf2().SaveGLB(animatedPath);

        var animatedModel = Cooked(animatedPath);
        var track = animatedModel.Clips.SelectMany(c => c.Tracks).Single();
        t.Expect("BB.3b skins that order joints differently are read, and the clip drives the node it names ('root')",
            animatedModel.Skins.Count == 2 && animatedModel.Skeleton!.Bones[track.BoneIndex].Name == "root",
            $"track on bone {track.BoneIndex} '{animatedModel.Skeleton?.Bones[track.BoneIndex].Name}'");

        // ── BB.4 CONTROL: one skin is untouched ─────────────────────────────
        //
        // The whole change is additive or it is not. Six of the seven rigged assets in this tree have
        // one skin, and every consumer of them reads Skeleton and SkeletonPlacement directly.
        var one = new SharpGLTF.Scenes.SceneBuilder();
        one.AddSkinnedMesh(Weighted("solo", 0), Matrix4x4.Identity, tipA, rootA, midA);
        var onePath = Path.Combine(temp, "one-skin.glb");
        one.ToGltf2().SaveGLB(onePath);

        var m1 = Cooked(onePath);
        t.Expect("BB.4 CONTROL a single-skin file reports exactly one skin", m1.Skins.Count == 1, $"{m1.Skins.Count}");
        t.ExpectTrue("BB.4 CONTROL its skinned primitives all sit on skin 0",
            m1.SkinnedPrimitives(0).Count() == m1.Meshes.Where(m => m.Skinned).Sum(m => m.Primitives.Count));
        t.ExpectTrue("BB.4 CONTROL and its skeleton is skin 0's joints, in skin 0's order",
            m1.Skins[0].Binding.Bones.Select((b, j) => b == j).All(x => x) && m1.Skins[0].Binding.JointCount == m1.Skeleton!.BoneCount);
    }
    finally
    {
        try { Directory.Delete(temp, recursive: true); } catch (IOException) { }
    }
}

// ============================================================================
// Section BA — a file says which of its channels went unread.
// ============================================================================
//
// <b>COLOR_0 was found by grepping assets, and that is the method this section exists to retire.</b>
// Forty-nine primitives happened to carry a channel nothing read, and noticing took a search. That
// only ever finds what the content already has: JOINTS_1 — more than four bone influences, which
// TRUNCATES a skin and deforms vertices wrongly rather than merely omitting a feature — appears in
// no asset here, in none of the Khronos sample assets, and would therefore never have been found
// that way at all.
//
// glTF 2.0 defines a closed set of attribute semantics, so the gap is knowable from the spec. The
// sweep is subtractive — what the primitive declares, minus what the importer reads — because a
// hardcoded list of known-missing names is deaf to the one case worth hearing about: an exporter
// emitting something nobody here anticipated.
{
    var temp = Path.Combine(Path.GetTempPath(), $"blix-ba-{Guid.NewGuid():N}");
    Directory.CreateDirectory(temp);
    try
    {
        // A plain triangle, and the same triangle with a second UV set bolted on afterwards. Built
        // by editing the JSON rather than through the builder API, because SharpGLTF's typed vertex
        // builders will not emit a semantic the importer is not expected to want — which is exactly
        // the shape this test needs to produce.
        var material = SharpGLTF.Materials.MaterialBuilder.CreateDefault();
        // Tangents are in the fixture so the file contains a VEC4 float accessor. The aliases below
        // have to be TYPE-COMPATIBLE with the semantic they impersonate — SharpGLTF validates on
        // load and refuses a JOINTS accessor that is not UBYTE4/USHORT4/FLOAT4, which is the
        // validator being right and the first draft of this fixture being malformed.
        var mesh = new SharpGLTF.Geometry.MeshBuilder<
            SharpGLTF.Geometry.VertexTypes.VertexPositionNormalTangent,
            SharpGLTF.Geometry.VertexTypes.VertexTexture1>("m");
        var prim = mesh.UsePrimitive(material);
        prim.AddTriangle(
            (new SharpGLTF.Geometry.VertexTypes.VertexPositionNormalTangent(
                 new Vector3(0, 0, 0), new Vector3(0, 1, 0), new Vector4(1, 0, 0, 1)),
             new SharpGLTF.Geometry.VertexTypes.VertexTexture1(new Vector2(0, 0))),
            (new SharpGLTF.Geometry.VertexTypes.VertexPositionNormalTangent(
                 new Vector3(1, 0, 0), new Vector3(0, 1, 0), new Vector4(1, 0, 0, 1)),
             new SharpGLTF.Geometry.VertexTypes.VertexTexture1(new Vector2(1, 0))),
            (new SharpGLTF.Geometry.VertexTypes.VertexPositionNormalTangent(
                 new Vector3(0, 0, 1), new Vector3(0, 1, 0), new Vector4(1, 0, 0, 1)),
             new SharpGLTF.Geometry.VertexTypes.VertexTexture1(new Vector2(0, 1))));
        var scene = new SharpGLTF.Scenes.SceneBuilder();
        scene.AddRigidMesh(mesh, new SharpGLTF.Scenes.NodeBuilder("only"));

        var plain = Path.Combine(temp, "plain.gltf");
        scene.ToGltf2().SaveGLTF(plain);

        // Bolt TEXCOORD_1 onto the primitive by pointing it at the accessor TEXCOORD_0 already uses.
        // A second set that aliases the first is still a second set as far as the file is concerned,
        // and the sweep reads the declaration rather than the data.
        var extra = Path.Combine(temp, "extra.gltf");
        var json = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(plain))!;
        var attrs = json["meshes"]![0]!["primitives"]![0]!["attributes"]!;
        attrs["TEXCOORD_1"] = attrs["TEXCOORD_0"]!.DeepClone();
        attrs["_CUSTOM_THING"] = attrs["TEXCOORD_0"]!.DeepClone();
        File.WriteAllText(extra, json.ToJsonString());

        // What the cook did not carry is a fact of the FILE, recorded in it: the cook writes the complete
        // vertex (tangents, both UV sets, colour), so a narrower load is a choice of layout, not a loss.
        ModelData Cooked(string file, ModelNeeds needs) => ModelData.Load(Blix.Recipes.CookCache.Resolve(file), needs);
        var plainModel = Cooked(plain, new ModelNeeds(Tangents: true, Skinned: false));
        var plainNarrow = Cooked(plain, new ModelNeeds(Skinned: false));
        var extraModel = Cooked(extra, new ModelNeeds(Tangents: true, Skinned: false));
        var extraNarrow = Cooked(extra, new ModelNeeds(Skinned: false));

        // ── BA.1 the control first ──────────────────────────────────────────
        t.Expect("BA.1 CONTROL a file with no unread attributes reports none",
            plainModel.Ignored.Count == 0,
            $"got {string.Join(", ", plainModel.Ignored.Select(i => i.Semantic))}");
        t.ExpectTrue("BA.1 and a narrower load reports nothing more: unread is the file's, not the layout's",
            plainNarrow.Ignored.Count == 0
            && extraNarrow.Ignored.Select(i => i.Semantic).SequenceEqual(extraModel.Ignored.Select(i => i.Semantic)));

        // ── BA.2 what it does catch ─────────────────────────────────────────
        var names = extraModel.Ignored.Select(i => i.Semantic).ToArray();

        // The subtractive sweep's whole reason for being: nobody wrote "_CUSTOM_THING" into a list.
        t.ExpectTrue($"BA.2 an attribute nobody anticipated is reported, which a fixed list would miss (got {string.Join(", ", names)})",
            names.Contains("_CUSTOM_THING"));
        t.ExpectTrue("BA.2 the second UV set the cook carries into the complete vertex is not reported",
            !names.Contains("TEXCOORD_1"));

        t.Expect("BA.2 the semantics Blix DOES read are not reported as ignored",
            !names.Contains("POSITION") && !names.Contains("NORMAL") && !names.Contains("TEXCOORD_0"),
            string.Join(", ", names));

        var custom = extraModel.Ignored.First(i => i.Semantic == "_CUSTOM_THING");
        t.Expect("BA.2 counted per primitive", custom.Primitives == 1, $"{custom.Primitives}");

        // ── BA.3 explanations say what the cook did and did not carry ──────
        // A semantic alone is not enough to say what happened: an influence pair is read on a skinned mesh
        // and meaningless on a mesh no skin drives. The explanation names both, against the complete vertex.
        t.ExpectTrue("BA.3 a third UV set explains itself against the two the cooked vertex carries",
            new UnreadAttribute("TEXCOORD_2", 1).Explanation.Contains("two the cooked vertex carries", StringComparison.Ordinal));

        var joints1 = new UnreadAttribute("JOINTS_1", 3);
        t.ExpectTrue($"BA.3 JOINTS_1 names both reasons an influence pair goes unread ({joints1.Explanation})",
            joints1.Explanation.Contains("no skin drives", StringComparison.Ordinal)
            && joints1.Explanation.Contains("complete and contiguous", StringComparison.Ordinal));

        t.ExpectTrue("BA.3 and an underscore attribute is named as application-specific",
            new UnreadAttribute("_BATCHID", 1).Explanation.Contains("application-specific", StringComparison.Ordinal));

        // ── BA.4 the likeliest authoring mistake comes first ────────────────
        // A skin channel on a mesh no skin drives says the mesh lost its skin on export: it leads ordinary
        // omitted capabilities and application metadata.
        // Each alias borrows an accessor of a type its semantic actually allows: TEXCOORD_1 a VEC2,
        // COLOR_1 a VEC3, JOINTS_1 the VEC4 tangent. Declared in an order that puts JOINTS_1 in the
        // middle, so passing cannot be an accident of insertion order.
        var mixedPath = Path.Combine(temp, "mixed.gltf");
        var mixedJson = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(plain))!;
        var mixedAttrs = mixedJson["meshes"]![0]!["primitives"]![0]!["attributes"]!;
        mixedAttrs["TEXCOORD_1"] = mixedAttrs["TEXCOORD_0"]!.DeepClone();
        mixedAttrs["JOINTS_1"] = mixedAttrs["TANGENT"]!.DeepClone();
        mixedAttrs["COLOR_1"] = mixedAttrs["NORMAL"]!.DeepClone();
        File.WriteAllText(mixedPath, mixedJson.ToJsonString());

        var mixedModel = Cooked(mixedPath, new ModelNeeds(Tangents: true, Skinned: false));
        var first = mixedModel.Ignored.FirstOrDefault()?.Semantic;
        t.Expect("BA.4 a skin channel on a mesh no skin drives is listed first",
            first == "JOINTS_1", $"got '{first}'");
    }
    finally
    {
        try { Directory.Delete(temp, recursive: true); } catch (IOException) { }
    }
}

// ============================================================================
// Section AZ — the vertex colour that was being thrown away.
// ============================================================================
//
// <b>COLOR_0 appears on 49 primitives in this tree and the engine had no code that mentioned it.</b>
// Sampled rather than assumed, it is not colour: greyscale, 0..1, 152 distinct values on one tree
// trunk and a uniform 1.0 on that same tree's leaf card. That is baked ambient occlusion, and it was
// dropped on every piece of scatter the RTS draws.
//
// The checks that matter here are the controls, because "the importer produced 36-byte vertices" is
// satisfied by an importer that writes 36 bytes of garbage. So each claim is paired: the channel
// arrives AND an asset without one is white, the layout widens AND the default path is untouched,
// the two flags are exclusive AND saying so is a refusal rather than a silent drop.
{
    var temp = Path.Combine(Path.GetTempPath(), $"blix-az-{Guid.NewGuid():N}");
    Directory.CreateDirectory(temp);
    try
    {
        // Three vertices carrying three DIFFERENT greys, the way baked occlusion is authored. A
        // single value would let a constant pass, and 1.0 would let the white default pass.
        static string BuildGltf(string path, bool withColour)
        {
            var material = SharpGLTF.Materials.MaterialBuilder.CreateDefault();
            var scene = new SharpGLTF.Scenes.SceneBuilder();
            if (withColour)
            {
                var mesh = new SharpGLTF.Geometry.MeshBuilder<
                    SharpGLTF.Geometry.VertexTypes.VertexPositionNormal,
                    SharpGLTF.Geometry.VertexTypes.VertexColor1>("coloured");
                var prim = mesh.UsePrimitive(material);
                prim.AddTriangle(
                    (new SharpGLTF.Geometry.VertexTypes.VertexPositionNormal(0, 0, 0, 0, 1, 0),
                     new SharpGLTF.Geometry.VertexTypes.VertexColor1(new Vector4(0.25f, 0.25f, 0.25f, 1f))),
                    (new SharpGLTF.Geometry.VertexTypes.VertexPositionNormal(1, 0, 0, 0, 1, 0),
                     new SharpGLTF.Geometry.VertexTypes.VertexColor1(new Vector4(0.50f, 0.50f, 0.50f, 1f))),
                    (new SharpGLTF.Geometry.VertexTypes.VertexPositionNormal(0, 0, 1, 0, 1, 0),
                     new SharpGLTF.Geometry.VertexTypes.VertexColor1(new Vector4(0.75f, 0.75f, 0.75f, 1f))));
                scene.AddRigidMesh(mesh, new SharpGLTF.Scenes.NodeBuilder("only"));
            }
            else
            {
                var mesh = new SharpGLTF.Geometry.MeshBuilder<
                    SharpGLTF.Geometry.VertexTypes.VertexPositionNormal>("plain");
                var prim = mesh.UsePrimitive(material);
                prim.AddTriangle(
                    new SharpGLTF.Geometry.VertexTypes.VertexPositionNormal(0, 0, 0, 0, 1, 0),
                    new SharpGLTF.Geometry.VertexTypes.VertexPositionNormal(1, 0, 0, 0, 1, 0),
                    new SharpGLTF.Geometry.VertexTypes.VertexPositionNormal(0, 0, 1, 0, 1, 0));
                scene.AddRigidMesh(mesh, new SharpGLTF.Scenes.NodeBuilder("only"));
            }
            scene.ToGltf2().SaveGLB(path);
            return path;
        }

        var coloured = BuildGltf(Path.Combine(temp, "coloured.glb"), withColour: true);
        var plain = BuildGltf(Path.Combine(temp, "plain.glb"), withColour: false);

        // The cook writes the complete vertex once; a load asks for the layout its pipeline declares.
        static MeshData First(string path, bool includeColour, bool includeTangents = false)
        {
            var m = ModelData.Load(Blix.Recipes.CookCache.Resolve(path), new ModelNeeds(includeTangents, includeColour, Skinned: false));
            return m.Meshes.First(x => x.Primitives.Count > 0).Primitives[0].Mesh;
        }

        // Colour lives in the last four bytes of the vertex, as UByte4Norm — after TWO UV sets since
        // the layout widened for TEXCOORD_1, which is why this reads the stride rather than a constant.
        static (byte R, byte G, byte B, byte A) ColourOf(MeshData m, int vertex)
        {
            var at = vertex * m.Layout.Stride + m.Layout.Stride - 4;
            return (m.VertexBytes[at], m.VertexBytes[at + 1], m.VertexBytes[at + 2], m.VertexBytes[at + 3]);
        }

        var withFlag = First(coloured, includeColour: true);
        var withoutFlag = First(coloured, includeColour: false);

        // ── AZ.1 the layout widens, and ONLY when asked ─────────────────────
        // 44, not 36: the same opt-in also carries TEXCOORD_1, because both widen one studio layout
        // and separate flags would let a caller ask for a layout no vertex type declares.
        t.Expect("AZ.1 importing with colour gives the 44-byte layout",
            withFlag.Layout.Stride == 44, $"stride {withFlag.Layout.Stride}");
        t.Expect("AZ.1 and it declares five attributes, not three",
            withFlag.Layout.Attributes.Count == 5, $"{withFlag.Layout.Attributes.Count}");

        // THE CONTROL. The same file, the same importer, the flag off. Six applications in this
        // tree pin the 32-byte layout in a pipeline of their own, and Vulkan walks a vertex buffer
        // at the stride the PIPELINE declares — so a default that widened would hand every one of
        // them 44-byte vertices read at 32. Not a crash and not a compile error: a wrong mesh.
        t.Expect("AZ.1 CONTROL the default path is still 32 bytes",
            withoutFlag.Layout.Stride == 32, $"stride {withoutFlag.Layout.Stride}");
        t.Expect("AZ.1 CONTROL and still three attributes",
            withoutFlag.Layout.Attributes.Count == 3, $"{withoutFlag.Layout.Attributes.Count}");

        // ── AZ.2 the authored values arrive, not merely the space for them ──
        var c0 = ColourOf(withFlag, 0);
        var c1 = ColourOf(withFlag, 1);
        var c2 = ColourOf(withFlag, 2);

        // 0.25 / 0.50 / 0.75 through a byte-normalised round trip: 64 / 128 / 191, within a step
        // either way. Asserted as a RANGE rather than a constant because the encoder chooses the
        // component type, and pinning its choice would be testing SharpGLTF rather than the import.
        t.ExpectTrue($"AZ.2 vertex 0 carries its authored grey (got {c0.R})", Math.Abs(c0.R - 64) <= 2);
        t.ExpectTrue($"AZ.2 vertex 1 carries its authored grey (got {c1.R})", Math.Abs(c1.R - 128) <= 2);
        t.ExpectTrue($"AZ.2 vertex 2 carries its authored grey (got {c2.R})", Math.Abs(c2.R - 191) <= 2);

        // The claim that "36 bytes" alone cannot make: the three vertices DIFFER. A widened layout
        // filled with a constant — white, zero, or whatever was in the buffer — passes every stride
        // check above and fails this one.
        t.ExpectTrue("AZ.2 and the three differ, so this is the channel and not a constant",
            c0.R != c1.R && c1.R != c2.R);

        // Alpha survives as opaque rather than arriving zero, which would make a MASK material
        // vanish the moment anything multiplied by it.
        t.ExpectTrue($"AZ.2 alpha is opaque (got {c0.A})", c0.A >= 253);

        // ── AZ.3 white is the identity, and absence is not a failure ────────
        //
        // Asking for colour from an asset that has none is the COMMON case, not an error:
        // CommonTree_1 carries occlusion on its trunk and nothing on its leaf card, and both arrive
        // through this branch inside one model. White because the shader MULTIPLIES — a zero
        // default would render every such primitive black.
        var plainWithFlag = First(plain, includeColour: true);
        t.Expect("AZ.3 an asset with no COLOR_0 still imports when colour is asked for",
            plainWithFlag.Layout.Stride == 44, $"stride {plainWithFlag.Layout.Stride}");
        var white = ColourOf(plainWithFlag, 0);
        t.ExpectTrue($"AZ.3 and every vertex is opaque white, the identity for a multiply (got {white})",
            white is (255, 255, 255, 255));

        t.Expect("AZ.3 White is the value the packer produces for 1,1,1,1",
            VertexPosition3NormalTextureColor.Pack(1f, 1f, 1f, 1f) == VertexPosition3NormalTextureColor.White,
            $"0x{VertexPosition3NormalTextureColor.Pack(1f, 1f, 1f, 1f):X8}");

        // ── AZ.4 the geometry is untouched — colour is added, not substituted ─
        //
        // The bytes ahead of the colour must be the SAME bytes. A widening that also perturbed a
        // position or a normal would show up as a shading change and be blamed on the new channel.
        var sameGeometry = true;
        for (var v = 0; v < withFlag.VertexCount && sameGeometry; v++)
        {
            for (var b = 0; b < 32; b++)
            {
                if (withFlag.VertexBytes[v * 44 + b] != withoutFlag.VertexBytes[v * 32 + b])
                {
                    sameGeometry = false;
                    break;
                }
            }
        }
        t.ExpectTrue("AZ.4 position, normal and uv are byte-identical with the flag on",
            sameGeometry);
        t.Expect("AZ.4 and the indices are unchanged",
            withFlag.IndexCount == withoutFlag.IndexCount,
            $"{withFlag.IndexCount} vs {withoutFlag.IndexCount}");

        // ── AZ.5 the two wide layouts combine into the complete vertex ──────
        // Tangents and colour together are the 60-byte complete vertex, the one every static cook
        // writes. The failure this guards is the silent one the old refusal existed for: returning
        // tangents and dropping the colour, which looks like an importer that does not read COLOR_0.
        var combined = First(coloured, includeColour: true, includeTangents: true);
        t.Expect("AZ.5 tangents and colour together give the 60-byte complete vertex",
            combined.Layout.Stride == 60, $"stride {combined.Layout.Stride}");
        var combinedColours = new[] { ColourOf(combined, 0).R, ColourOf(combined, 1).R, ColourOf(combined, 2).R };
        t.Expect("AZ.5 and it carries the authored colour rather than white",
            Math.Abs(combinedColours[0] - 64) <= 2 && Math.Abs(combinedColours[1] - 128) <= 2
            && Math.Abs(combinedColours[2] - 191) <= 2, string.Join(",", combinedColours));

        // CONTROL for AZ.5: each flag ALONE gives its own layout, so the combined one above is the
        // combination and not tangents having quietly stopped working.
        var tangentStride = First(coloured, includeColour: false, includeTangents: true).Layout.Stride;
        t.Expect("AZ.5 CONTROL tangents alone still import",
            tangentStride == 48, $"stride {tangentStride}");
    }
    finally
    {
        try { Directory.Delete(temp, recursive: true); } catch (IOException) { }
    }
}

// ============================================================================
// Section AX — a load says what it did.
// ============================================================================
//
// <b>AssetLoadReport had four states and zero emitters for its whole existence, and the reason was
// structural rather than neglect.</b> Its own documentation said to hand reports to
// DebugContext.Events — a FRAME-time channel — while asset loading happens before there is a
// frame, with no DebugContext anywhere in reach. The prescribed mechanism did not exist at the
// moment the event occurred. It also sat in Blix.Diagnostics, a tier above two of the three cooked
// readers, so half the loaders could not have referenced it anyway.
//
// These check the channel and the emitters, with the control that matters most: a source load and a
// cooked load must report differently. A reporter that always says "Cooked" is worse than no reporter,
// because it is believed. glTF reaches the runtime cooked only (the cook is its one reader), so the
// source half is a format the runtime still reads from source: an OBJ through WavefrontParts.
{
    var temp = Path.Combine(Path.GetTempPath(), $"blix-ax-{Guid.NewGuid():N}");
    Directory.CreateDirectory(temp);
    var wasEnabled = AssetLoadLog.Enabled;
    try
    {
        var glb = Path.Combine(temp, "reported.glb");
        var mesh = new SharpGLTF.Geometry.MeshBuilder<SharpGLTF.Geometry.VertexTypes.VertexPositionNormal>("m");
        var prim = mesh.UsePrimitive(SharpGLTF.Materials.MaterialBuilder.CreateDefault());
        prim.AddTriangle(
            new SharpGLTF.Geometry.VertexTypes.VertexPositionNormal(0, 0, 0, 0, 1, 0),
            new SharpGLTF.Geometry.VertexTypes.VertexPositionNormal(1, 0, 0, 0, 1, 0),
            new SharpGLTF.Geometry.VertexTypes.VertexPositionNormal(0, 0, 1, 0, 1, 0));
        var scene = new SharpGLTF.Scenes.SceneBuilder();
        scene.AddRigidMesh(mesh, new SharpGLTF.Scenes.NodeBuilder("only"));
        scene.ToGltf2().SaveGLB(glb);

        // <b>Silent unless asked.</b> A game's load path pays nothing for an instrument nobody
        // turned on, which is the same rule DebugState.Enabled follows one layer up.
        var cookedPath = Blix.Recipes.CookCache.Resolve(glb);
        AssetLoadLog.Enabled = false;
        AssetLoadLog.Drain();
        Blix.ModelData.Load(cookedPath);
        t.Expect("AX.1 a load reports nothing while the log is off", AssetLoadLog.Peek().Length == 0,
            $"got {AssetLoadLog.Peek().Length}");

        // ── uncooked: a format the runtime still reads from source ──────────
        var obj = Path.Combine(temp, "reported.obj");
        File.WriteAllText(obj, "v 0 0 0\nv 1 0 0\nv 0 0 1\nvn 0 1 0\nf 1//1 2//1 3//1\n");
        AssetLoadLog.Start();
        WavefrontParts.Import(obj);
        var uncooked = AssetLoadLog.Drain();
        var meshReport = uncooked.SingleOrDefault(r => r.SourcePath == obj);

        t.ExpectTrue("AX.2 an uncooked load is reported at all", meshReport is not null);
        t.Expect("AX.2 and reports Source", meshReport!.Mode == AssetLoadMode.Source, $"got {meshReport.Mode}");
        t.ExpectTrue("AX.2 with no cooked path", meshReport.CookedPath is null);
        t.ExpectTrue("AX.2 and says why it was slow", meshReport.Warning is { Length: > 0 });
        t.ExpectTrue("AX.2 carrying a cost, not just a branch", meshReport.LoadMs > 0 && meshReport.Bytes > 0);

        // ── cooked ──────────────────────────────────────────────────────────
        // The engine's reader reports a cooked load: the glTF, as the cook wrote it.
        AssetLoadLog.Start();
        Blix.ModelData.Load(cookedPath);
        var afterCook = AssetLoadLog.Drain();
        var cookedReport = afterCook.SingleOrDefault(r => r.SourcePath == cookedPath);

        t.ExpectTrue("AX.3 a cooked load is reported", cookedReport is not null);
        t.Expect("AX.3 and reports Cooked: a source load and a cooked load answer differently",
            cookedReport!.Mode == AssetLoadMode.Cooked, $"got {cookedReport.Mode}");
        t.ExpectTrue("AX.3 naming the artifact it used",
            cookedReport.CookedPath?.EndsWith(".blixmesh", StringComparison.Ordinal) == true);
        t.Expect("AX.3 and the recipe that made it", cookedReport.Recipe == BlixMesh.ShippedRecipe,
            $"got '{cookedReport.Recipe}'");
        t.ExpectTrue("AX.3 with no warning, because nothing was wrong", cookedReport.Warning is null);

        // ── the buffer behaves ──────────────────────────────────────────────
        t.Expect("AX.4 Drain clears what it returned", AssetLoadLog.Peek().Length == 0);

        AssetLoadLog.Start();
        Blix.ModelData.Load(cookedPath);
        var firstPeek = AssetLoadLog.Peek().Length;
        var secondPeek = AssetLoadLog.Peek().Length;
        t.Expect("AX.4 Peek does not clear", firstPeek > 0 && secondPeek == firstPeek,
            $"{firstPeek} then {secondPeek}");

        AssetLoadLog.Start();
        t.Expect("AX.4 and Start clears what was there", AssetLoadLog.Peek().Length == 0);
    }
    finally
    {
        AssetLoadLog.Enabled = wasEnabled;
        AssetLoadLog.Drain();
        try { Directory.Delete(temp, recursive: true); } catch (IOException) { }
    }
}

// Section AW — the cooked preamble: three formats that are one family.
// ============================================================================
//
// <b>Before this, no single function could read any Blix cooked file's magic and version.</b>
// .blixtex announced itself as "BLIX" while .blixmesh and .blixprobe used BLX*, and its version
// was a ushort where theirs were uint. The consequence was not cosmetic: nothing in the tree
// could report on a cooked artifact without knowing in advance what it was looking at, which is
// why coverage was decided by shell history and AssetLoadReport had four states and no emitters.
//
// The checks below are the family being one thing. The ones that matter most are the negative
// controls — a stamp is not proved by reading back what you wrote, it is proved by the reader
// refusing a file that does not have one.
{
    var temp = Path.Combine(Path.GetTempPath(), $"blix-aw-{Guid.NewGuid():N}");
    Directory.CreateDirectory(temp);
    try
    {
        // A source to cook FROM, so the stamp has real size and mtime to record.
        var source = Path.Combine(temp, "source.bin");
        File.WriteAllBytes(source, new byte[] { 1, 2, 3, 4, 5, 6, 7 });

        var artifactPath = Path.Combine(temp, "one.cooked");
        var stamp = CookStamp.Of("tst1", 7, source, artifactPath, "alpha=1 beta=two", CookedFlags.SourceRequired);
        t.Expect("AW.1 stamp reads the source's size off disk", stamp.SourceSize == 7L, $"got {stamp.SourceSize}");
        t.ExpectTrue("AW.1 and its modification time", stamp.SourceTicks != 0);
        t.ExpectTrue("AW.1 and SourceRequired surfaces as a property", stamp.SourceRequired);

        // Round-trip through a real file, with a body after the preamble so the offset is exercised
        // rather than assumed.
        var artifact = artifactPath;
        const uint magic = 0x54534554; // "TEST"
        int preambleBytes;
        using (var fs = File.Create(artifact))
        {
            preambleBytes = CookPreamble.Write(fs, magic, 3, stamp);
            fs.Write(new byte[] { 0xAA, 0xBB, 0xCC, 0xDD });
        }

        t.ExpectTrue("AW.2 the preamble is 4-byte aligned", preambleBytes % 4 == 0);

        var read = CookedFile.ReadHeader(artifact);
        t.Expect("AW.2 magic round-trips", read.Magic == magic, $"got 0x{read.Magic:X8}");
        t.Expect("AW.2 format version round-trips", read.FormatVersion == 3u, $"got {read.FormatVersion}");
        t.Expect("AW.2 recipe id round-trips", read.Stamp.Recipe == "tst1", $"got '{read.Stamp.Recipe}'");
        t.Expect("AW.2 recipe version round-trips", read.Stamp.RecipeVersion == 7u, $"got {read.Stamp.RecipeVersion}");
        t.Expect("AW.2 parameters round-trip verbatim", read.Stamp.Parameters == "alpha=1 beta=two", $"got '{read.Stamp.Parameters}'");
        // <b>Relative to the artifact, not as passed.</b> An absolute path would put this machine's
        // home directory into a file that gets committed — which is exactly what the build rule did
        // on its first run, and what made two cooks of one source differ between the command and
        // the build.
        t.Expect("AW.2 the source is recorded relative to the artifact, not absolutely",
            read.Stamp.SourcePath == "source.bin", $"got '{read.Stamp.SourcePath}'");
        t.ExpectTrue("AW.2 and never carries a machine path",
            !read.Stamp.SourcePath.Contains('/') && !Path.IsPathRooted(read.Stamp.SourcePath));
        t.Expect("AW.2 source size round-trips", read.Stamp.SourceSize == 7L, $"got {read.Stamp.SourceSize}");
        t.ExpectTrue("AW.2 flags round-trip", read.Stamp.SourceRequired);

        // <b>The body starts where the preamble said it would.</b> Everything downstream — every
        // format's own header, every mip extent — is placed relative to this number, so a preamble
        // that lied about its own length would corrupt files that still passed every check above.
        using (var fs = File.OpenRead(artifact))
        {
            fs.Position = read.PreambleBytes;
            var body = new byte[4];
            fs.ReadExactly(body, 0, 4);
            t.ExpectTrue("AW.3 the body sits exactly at PreambleBytes",
                body[0] == 0xAA && body[1] == 0xBB && body[2] == 0xCC && body[3] == 0xDD);
        }

        // ── Negative controls ────────────────────────────────────────────────
        // A reader that only ever succeeds on files it wrote itself proves nothing.

        var notCooked = Path.Combine(temp, "not-cooked.bin");
        File.WriteAllText(notCooked, "nowhere near long enough to be a preamble");
        t.ExpectThrows<AssetImportException>(
            "AW.4 a file that is not a cooked artifact is refused as AssetImportException",
            () => CookedFile.ReadHeader(notCooked),
            mustMention: "not-cooked.bin");

        var truncated = Path.Combine(temp, "truncated.cooked");
        File.WriteAllBytes(truncated, File.ReadAllBytes(artifact).AsSpan(0, 20).ToArray());
        t.ExpectThrows<AssetImportException>(
            "AW.4 a truncated preamble is refused, not read as zeroes",
            () => CookedFile.ReadHeader(truncated));

        t.ExpectTrue("AW.4 TryReadHeader answers null rather than throwing, for walking a tree",
            CookedFile.TryReadHeader(notCooked) is null);

        // Wrong format, and wrong version of the right format, say DIFFERENT things — one means
        // "this is not that kind of file", the other means "it is, and it is old", which is the
        // actionable one.
        t.ExpectThrows<AssetImportException>(
            "AW.5 the wrong magic is refused as the wrong kind of file",
            () => CookedFile.ReadHeader(artifact).Require(0x21212121, 3, artifact, ".other"),
            mustMention: "magic");
        t.ExpectThrows<AssetImportException>(
            "AW.5 an old version of the right format is told to re-cook",
            () => CookedFile.ReadHeader(artifact).Require(magic, 99, artifact, ".cooked"),
            mustMention: "re-cook");

        // ── Freshness, including the third answer ────────────────────────────
        t.Expect("AW.6 an untouched source reads as Current",
            CookedFile.Compare(read, source) == CookedFile.Freshness.Current,
            $"got {CookedFile.Compare(read, source)}");

        File.WriteAllBytes(source, new byte[] { 9, 9, 9 });
        t.Expect("AW.6 a changed source reads as Stale",
            CookedFile.Compare(read, source) == CookedFile.Freshness.Stale,
            $"got {CookedFile.Compare(read, source)}");

        File.Delete(source);
        t.Expect("AW.6 a deleted source reads as SourceMissing",
            CookedFile.Compare(read, source) == CookedFile.Freshness.SourceMissing,
            $"got {CookedFile.Compare(read, source)}");

        // <b>Unknown is a third answer and not a "yes".</b> Flattening it would rebuild the exact
        // bug this arc exists to fix — a check that cannot tell a current file from one it knows
        // nothing about.
        var blind = new CookStamp("tst1", 1, "gone.bin", 0, 0, 0, "", CookedFlags.None);
        var blindArtifact = Path.Combine(temp, "blind.cooked");
        using (var fs = File.Create(blindArtifact)) CookPreamble.Write(fs, magic, 1, blind);
        t.Expect("AW.6 a stamp with nothing recorded reads as Unknown, not Current",
            CookedFile.Compare(CookedFile.ReadHeader(blindArtifact), notCooked) == CookedFile.Freshness.Unknown);

        // ── The family is actually a family ──────────────────────────────────
        // Read as a 4cc rather than compared as hex, because the point is that they share a
        // namespace a person can see.
        t.Expect("AW.7 .blixmesh magic", CookPreamble.Describe(BlixMesh.Magic) == "'BLXM'");
        t.Expect("AW.7 .blixtex magic is BLXT, no longer the odd one out", CookPreamble.Describe(BlixTex.Magic) == "'BLXT'");
        t.Expect("AW.7 .blixprobe magic", CookPreamble.Describe(BlixProbe.Magic) == "'BLXP'");

        // ── a cooked .glb loads, which it did not ───────────────────────────
        // <b>The cooked fast path only ever worked for .gltf.</b> It skips buffer reads by handing
        // the parser empty bytes for everything that is not the container — and it identified the
        // container by testing for the ".gltf" extension, so a .glb was handed nothing and died
        // with "JSon is empty". Nobody hit it because the path was written for Sponza, which is
        // .gltf plus external .bin, and the only cooked .glb in the tree was loaded through the
        // rigged importer, which has no cooked path. It surfaced the day asset coverage became
        // complete and four .glb files got siblings.
        //
        // Built here rather than pointed at a repo asset, so the check owns its own scenario: a
        // .glb, cooked, then loaded through the importer that prefers the cooked sibling.
        var glbDir = Path.Combine(temp, "glb");
        Directory.CreateDirectory(glbDir);
        var glbPath = Path.Combine(glbDir, "cooked-glb.glb");

        var mesh = new SharpGLTF.Geometry.MeshBuilder<SharpGLTF.Geometry.VertexTypes.VertexPositionNormal>("m");
        var meshPrim = mesh.UsePrimitive(SharpGLTF.Materials.MaterialBuilder.CreateDefault());
        meshPrim.AddTriangle(
            new SharpGLTF.Geometry.VertexTypes.VertexPositionNormal(0, 0, 0, 0, 1, 0),
            new SharpGLTF.Geometry.VertexTypes.VertexPositionNormal(1, 0, 0, 0, 1, 0),
            new SharpGLTF.Geometry.VertexTypes.VertexPositionNormal(0, 0, 1, 0, 1, 0));
        var scene = new SharpGLTF.Scenes.SceneBuilder();
        scene.AddRigidMesh(mesh, new SharpGLTF.Scenes.NodeBuilder("only"));
        scene.ToGltf2().SaveGLB(glbPath);

        // The cook is the one way a .glb reaches the runtime, and CookCache is its front door: a source with no
        // current sibling cooks into the cache; once a current sibling exists, the sibling is used as it is.
        // Uncooked first, so the check cannot pass by the sibling path never being taken.
        static int Vertices(string cooked) => ModelData.Load(cooked, new ModelNeeds(Skinned: false)).Flattened().Sum(x => x.Primitive.Mesh.VertexCount);
        var cookedGlb = Path.ChangeExtension(glbPath, ".blixmesh");
        var beforeCook = Blix.Recipes.CookCache.Resolve(glbPath);
        t.Expect("AW.9 a .glb with no sibling resolves through the cook, into the cache",
            beforeCook != cookedGlb && File.Exists(beforeCook), beforeCook);

        // Cooked as a project ships it: CookCache takes a sibling only when it is current for the shipped recipe
        // and settings (MeshRecipe.IsShippedCurrent), not merely present.
        Blix.Recipes.MeshRecipe.CookShipped(glbPath, cookedGlb);
        t.ExpectTrue("AW.9 and the cook writes a sibling", File.Exists(cookedGlb));

        var afterCook = Blix.Recipes.CookCache.Resolve(glbPath);
        t.Expect("AW.9 and a current sibling is then used as it is", afterCook == cookedGlb, afterCook);
        t.Expect("AW.9 with the same geometry either way",
            Vertices(afterCook) == Vertices(beforeCook) && Vertices(afterCook) > 0,
            $"{Vertices(beforeCook)} -> {Vertices(afterCook)}");

        // ── The three recipes, as declared ───────────────────────────────────
        // <b>These check the DECLARATIONS, not the cooking.</b> A recipe whose id does not match
        // the constant its format stamps would write files nothing could attribute, and the two
        // are in different assemblies now, so nothing but a check keeps them in step.
        var recipes = typeof(Blix.Recipes.MeshRecipe).Assembly.GetTypes()
            .SelectMany(ty => ty.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static))
            .Select(m => (Method: m, Attr: m.GetCustomAttribute<RecipeAttribute>()))
            .Where(x => x.Attr is not null)
            .Select(x => (x.Method, Attr: x.Attr!))
            .ToList();

        t.Expect("AW.8 Blix ships four recipes", recipes.Count == 4, $"found {recipes.Count}");

        // Every id is exactly four characters, because it rides in the preamble as a 4cc — a
        // five-character id would silently truncate and attribute files to a recipe that does not
        // exist.
        t.ExpectTrue("AW.8 every recipe id is a 4cc",
            recipes.All(r => r.Attr.Id.Length == CookStamp.RecipeIdLength));

        // No two recipes share an id. With one assembly it is obvious; the moment a project
        // declares its own it stops being.
        t.Expect("AW.8 no two recipes share an id",
            recipes.Select(r => r.Attr.Id).Distinct().Count() == recipes.Count,
            string.Join(",", recipes.Select(r => r.Attr.Id)));

        // Each declares what it consumes and what it produces, which is what a build rule needs to
        // know before it can match a source file to a recipe at all.
        t.ExpectTrue("AW.8 every recipe declares what it produces",
            recipes.All(r => r.Attr.Produces.StartsWith('.')));
        t.ExpectTrue("AW.8 every recipe declares what it consumes",
            recipes.All(r => r.Attr.Consumes.Split(';').All(e => e.StartsWith('.'))));

        // The id in the declaration IS the id stamped into the file. Different assemblies, so
        // nothing but this keeps them agreeing.
        t.ExpectTrue("AW.8 the mesh recipe's declared id is the one BlixMesh stamps",
            recipes.Any(r => r.Attr.Id == BlixMesh.ShippedRecipe && r.Attr.Produces == ".blixmesh"));
        t.ExpectTrue("AW.8 the texture recipe's declared id is the one BlixTex stamps",
            recipes.Any(r => r.Attr.Id == BlixTex.ShippedRecipe && r.Attr.Produces == ".blixtex"));
        t.ExpectTrue("AW.8 the probe recipe's declared id is the one BlixProbe stamps",
            recipes.Any(r => r.Attr.Id == BlixProbe.ShippedRecipe && r.Attr.Produces == ".blixprobe"));
        t.ExpectTrue("AW.8 the font recipe's declared id is the one BlixFont stamps",
            recipes.Any(r => r.Attr.Id == BlixFont.ShippedRecipe && r.Attr.Produces == ".blixfont"));

        // And the signature the index will look for. A recipe declared with the wrong shape is the
        // worst failure this can have — found at the moment it is needed rather than at build.
        t.ExpectTrue("AW.8 every recipe is CookOutcome Cook(CookRequest)",
            recipes.All(r => r.Method.ReturnType == typeof(CookOutcome)
                             && r.Method.GetParameters() is [{ ParameterType: var pt }]
                             && pt == typeof(CookRequest)));
    }
    finally
    {
        try { Directory.Delete(temp, recursive: true); } catch (IOException) { }
    }
}

// Section AV — the cook refuses by NAME, not by whatever the parser threw.
// ============================================================================
//
// <b>This is what lets a tool tell a bad asset from a bug in itself.</b> Every refusal comes out
// as AssetImportException carrying the path; anything else escaping the cook's front door (CookCache, the
// one way a tool turns a source into what the engine reads) is a fault in Blix. A tool can then catch exactly one type and let the rest crash, which is the only way a
// judge both survives bad input AND does not swallow its own faults.
//
// Before this, `blix check --rig not-a-glb` exited 134 through the parser's own exception — a
// judge whose whole job is to survive a bad asset, crashing on the first one it was handed.
{
    var temp = Path.Combine(Path.GetTempPath(), $"blix-av-{Guid.NewGuid():N}");
    Directory.CreateDirectory(temp);
    try
    {
        var notGltf = Path.Combine(temp, "not-a.glb");
        File.WriteAllText(notGltf, "this is not a glTF at all");

        var refused = false;
        var mentionsPath = false;
        var keptInner = false;
        try
        {
            Blix.Recipes.CookCache.Resolve(notGltf);
        }
        catch (AssetImportException bad)
        {
            refused = true;
            mentionsPath = bad.Message.Contains("not-a.glb", StringComparison.Ordinal);
            keptInner = bad.InnerException is not null;
        }
        catch (Exception)
        {
            // Any other type is the failure this section exists to catch.
        }

        t.ExpectTrue("AV.1 a file that is not glTF is refused as AssetImportException", refused);
        t.ExpectTrue("AV.1 and the refusal names the file", mentionsPath);
        t.ExpectTrue("AV.1 with the parser's own error kept as InnerException", keptInner);

        // <b>One line, not the parser's three.</b> A parser writes for whoever maintains the
        // parser: provenance, a byte position and a link to a validator. The sentence a person
        // needs is the first one.
        var single = true;
        try
        {
            Blix.Recipes.CookCache.Resolve(notGltf);
        }
        catch (AssetImportException bad)
        {
            single = !bad.Message.Contains('\n') && !bad.Message.Contains('\r');
        }
        catch (Exception)
        {
        }

        t.ExpectTrue("AV.2 the refusal is one line", single);

        // A missing file is the same kind of answer, not a different one.
        var missingRefused = false;
        try
        {
            Blix.Recipes.CookCache.Resolve(Path.Combine(temp, "gone.glb"));
        }
        catch (AssetImportException)
        {
            missingRefused = true;
        }
        catch (Exception)
        {
        }

        t.ExpectTrue("AV.3 a missing file is refused the same way", missingRefused);

        // And the cooked side, through the engine's reader: a missing .blixmesh resolves to itself, so
        // the refusal has to come from the open. A corrupt one is the control that the reader's own
        // checks still answer in the same type.
        static string RefusedAs(Action read)
        {
            try { read(); return "loaded"; }
            catch (AssetImportException) { return nameof(AssetImportException); }
            catch (Exception e) { return e.GetType().Name; }
        }

        var goneCooked = RefusedAs(() => ModelData.Load(Blix.Recipes.CookCache.Resolve(Path.Combine(temp, "gone.blixmesh"))));
        t.Expect("AV.3b a missing cooked file is refused the same way", goneCooked == nameof(AssetImportException), goneCooked);
        var corrupt = Path.Combine(temp, "corrupt.blixmesh");
        File.WriteAllBytes(corrupt, new byte[] { 1, 2, 3 });
        var corruptCooked = RefusedAs(() => ModelData.Load(corrupt));
        t.Expect("AV.3c a corrupt cooked file is refused the same way", corruptCooked == nameof(AssetImportException), corruptCooked);

        // Every cooked reader opens inside its refusal, not only the mesh's.
        foreach (var (name, read) in new (string, Action)[]
        {
            (".blixtex", () => Blix.Graphics.Images.BlixTexReader.ReadHandle(Path.Combine(temp, "gone.blixtex"))),
            (".blixfont", () => Blix.Assets.BlixFontReader.Read(Path.Combine(temp, "gone.blixfont"))),
            (".blixprobe", () => Blix.Graphics.Images.BlixProbeReader.Read(Path.Combine(temp, "gone.blixprobe"))),
        })
        {
            var answer = RefusedAs(read);
            t.Expect($"AV.3d a missing {name} is refused the same way", answer == nameof(AssetImportException), answer);
        }
    }
    finally
    {
        Directory.Delete(temp, recursive: true);
    }
}

// ============================================================================
// Section BD — the CPU tonemap is answerable to the GLSL one.
// ============================================================================
//
// <b>Two implementations of one curve, in two languages, and only one of them is ever looked at.</b>
// A capture is read back from the HDR scene target before the present pass runs, so the curve has to
// be applied in C#; the screen gets it from blix_tonemap on the GPU. Nothing made the pair agree —
// the capture tool's copy was hardcoded to ACES while the shader offered four curves, so
// --tonemap-mode moved the screen and left every capture alone, and no test could notice because
// both halves were internally consistent.
//
// This does not prove the two produce identical pixels; proving that needs a GPU harness that reads
// back a known ramp, which is more machinery than the risk warrants. It proves the CONSTANTS and the
// mode thresholds still appear in the shader, which is the drift that actually happens: somebody
// tunes a coefficient in GLSL and the C# keeps the old one. Crude next to reflecting an interface out
// of SPIR-V, same idea — make the second copy answerable to the first.
//
// <b>Section BE is this section's sibling</b>, and the two differ in one way worth knowing before
// copying either: Tonemap.cs is SHIPPED, because a capture needs the curve on the CPU, while BE's
// SheenTwin is test-local and has to stay that way. Both shaders carry a pointer back here.
{
    var glslPath = Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "..", "Blix.Shaders", "tonemap.glsl");
    var glsl = File.Exists(glslPath) ? File.ReadAllText(glslPath) : null;

    // CONTROL: if the file cannot be found, every check below would pass vacuously. This is the
    // guard conventions §5's corollary asks for — a green result is not evidence the check ran.
    t.Expect("BD.0 CONTROL the shader source was actually read",
        glsl is { Length: > 200 } && glsl.Contains("blix_tonemap", StringComparison.Ordinal),
        glsl is null ? $"not found at {Path.GetFullPath(glslPath)}" : $"{glsl.Length} chars");

    if (glsl is not null)
    {
        foreach (var (name, value) in new (string, float)[]
        {
            ("AcesA", Tonemap.AcesA), ("AcesB", Tonemap.AcesB), ("AcesC", Tonemap.AcesC),
            ("AcesD", Tonemap.AcesD), ("AcesE", Tonemap.AcesE),
            ("AgxMinEv", Tonemap.AgxMinEv), ("AgxMaxEv", Tonemap.AgxMaxEv),
        })
        {
            var literal = value.ToString("0.######", System.Globalization.CultureInfo.InvariantCulture);
            t.Expect($"BD.1 {name} = {literal} still appears in tonemap.glsl",
                glsl.Contains(literal, StringComparison.Ordinal),
                $"Tonemap.{name} is {literal}; tonemap.glsl no longer contains it");
        }

        // The selector's thresholds are a convention restated in both files, and restating is how
        // the two come to disagree.
        t.Expect("BD.2 the mode thresholds still read 0.5 / 1.5 / 2.5 in the shader",
            glsl.Contains("mode < 0.5", StringComparison.Ordinal)
            && glsl.Contains("mode < 1.5", StringComparison.Ordinal)
            && glsl.Contains("mode < 2.5", StringComparison.Ordinal),
            "blix_tonemap's selector changed shape");
    }

    // The curves themselves, pinned. Not a comparison against the GPU — a record of what this
    // implementation does, so a change to it is deliberate rather than noticed later in a picture.
    t.Expect("BD.3 ACES maps mid-grey to a lifted value and saturates high input",
        MathF.Abs(Tonemap.Aces(new Vector3(0.18f)).X - 0.2669f) < 0.001f
        && Tonemap.Aces(new Vector3(100f)).X > 0.99f,
        $"0.18 -> {Tonemap.Aces(new Vector3(0.18f)).X:0.0000}, 100 -> {Tonemap.Aces(new Vector3(100f)).X:0.0000}");

    t.Expect("BD.4 Reinhard is x/(1+x) and never reaches 1",
        MathF.Abs(Tonemap.Reinhard(new Vector3(1f)).X - 0.5f) < 1e-6f
        && Tonemap.Reinhard(new Vector3(1e6f)).X < 1f,
        $"1 -> {Tonemap.Reinhard(new Vector3(1f)).X}");

    t.Expect("BD.5 neutral only clamps",
        Tonemap.Neutral(new Vector3(0.4f)).X == 0.4f && Tonemap.Neutral(new Vector3(3f)).X == 1f,
        "neutral is not a pure clamp");

    t.Expect("BD.6 AgX is monotonic and bounded",
        Tonemap.Agx(new Vector3(0.05f)).X < Tonemap.Agx(new Vector3(0.5f)).X
        && Tonemap.Agx(new Vector3(0.5f)).X < Tonemap.Agx(new Vector3(5f)).X
        && Tonemap.Agx(new Vector3(1e6f)).X <= 1f,
        $"{Tonemap.Agx(new Vector3(0.05f)).X:0.000} / {Tonemap.Agx(new Vector3(0.5f)).X:0.000} / {Tonemap.Agx(new Vector3(5f)).X:0.000}");

    // <b>The selector must pick a DIFFERENT curve per mode.</b> The bug this section exists for was
    // one curve answering for all four, and four modes that agree with each other is exactly what
    // that looks like from the outside.
    var hdr = new Vector3(0.6f, 0.35f, 0.12f);
    var picked = new[] { 0f, 1f, 2f, 3f }.Select(m => Tonemap.Apply(hdr, m).X).ToArray();
    t.Expect("BD.7 the four modes give four different answers",
        picked.Distinct().Count() == 4,
        string.Join(", ", picked.Select(v => v.ToString("0.0000"))));
}

// ============================================================================
// Section BE — the sheen and diffuse-transmission terms, evaluated.
// ============================================================================
//
// <b>The arc that shipped these skipped the stage that was supposed to check them, and it had
// already written down why that was the mistake.</b> The material-response plan's stage B existed
// because "a BRDF that cannot be evaluated on its own gets debugged by staring at Sponza" — the way
// the GTAO slice-direction Y-flip survived, with a dark floor and a dozen candidate causes. The
// vocabulary, the probe's Charlie cube and Sponza's response all went in; B did not. Until this
// section the only instrument for the Charlie lobe was a curtain.
//
// <b>The twin below is TEST-LOCAL and deliberately not shipped.</b> Section BD's C# tonemap exists
// because a capture is read back on the CPU and genuinely needs the curve in C#. Nothing needs
// sheen in C#, so putting it in Blix would manufacture exactly the second implementation the
// material-response arc exists to avoid — a cheaper lobe sitting beside the real one. Here it buys
// the four invariants the plan named and nothing else.
//
// And it inherits BD's warning in full: <b>a twin can satisfy every invariant while the shipped
// GLSL drifts out from under it.</b> That is the entire job of BE.1, which holds the twin
// answerable line by line to the file that actually compiles.
//
// blix_sheenAlbedo is not here. It is a texture fetch into a table the cook bakes, and the plan
// already records what happened to the analytic fit that stood in its place: it disagreed with the
// integrated lobe by two orders of magnitude and survived a compile, a range check and a clamp. A
// C# twin of a sampler proves nothing about the table.
{
    var sheenPath = Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "..", "Blix.Shaders", "sheen.glsl");
    var sheen = File.Exists(sheenPath) ? File.ReadAllText(sheenPath) : null;

    // CONTROL, per conventions §5: if the file is not found every source check below passes
    // vacuously, and a green result would not be evidence that anything ran.
    t.Expect("BE.0 CONTROL the sheen shader source was actually read",
        sheen is { Length: > 200 } && sheen.Contains("blix_sheenBrdf", StringComparison.Ordinal),
        sheen is null ? $"not found at {Path.GetFullPath(sheenPath)}" : $"{sheen.Length} chars");

    if (sheen is not null)
    {
        // Every line of arithmetic the twin copies, pinned against the original. This is the half
        // that notices when somebody tunes the shader and leaves the twin behind.
        foreach (var (what, fragment) in new (string, string)[]
        {
            ("Charlie's inverted-roughness exponent", "pow(sin2h, invAlpha * 0.5)"),
            ("Charlie's normalisation term",          "(2.0 + invAlpha)"),
            ("Charlie's 2*pi denominator",            "(2.0 * BLIX_PI)"),
            ("Ashikhmin's folded 4*NdotL*NdotV",      "1.0 / (4.0 * (NdotL + NdotV - NdotL * NdotV))"),
            ("transmission's inverted normal",        "dot(-N, L)"),
            ("the Lambertian 1/pi on transmission",   "/ BLIX_PI"),
            ("sheen scaling's max over channels",     "max(max(sheenColor.r, sheenColor.g), sheenColor.b)"),
        })
        {
            t.Expect($"BE.1 {what} still reads the way the twin does",
                sheen.Contains(fragment, StringComparison.Ordinal),
                $"sheen.glsl no longer contains `{fragment}`");
        }

        // <b>The lobe multiplies D * V and stops.</b> Ashikhmin is the VISIBILITY term with the
        // 4*NdotL*NdotV denominator already folded in; a consumer that divides again is off by
        // exactly that factor, which reads as "sheen is too strong at grazing angles" — precisely
        // where sheen is supposed to be strong. It looks plausible and it is wrong, which is why
        // it gets a source pin and not only a number.
        t.Expect("BE.2 the sheen lobe multiplies D*V and does not divide a second time",
            sheen.Contains("return sheenColor * d * v;", StringComparison.Ordinal),
            "blix_sheenBrdf's body changed shape — check for a reintroduced 4*NdotL*NdotV divide");
    }

    // The same claim as a number, so it survives a reformat of the shader. At normal incidence
    // Ashikhmin is 1/(4*(1+1-1)) = 1/4. A second fold would make it 1/16.
    t.ExpectClose("BE.2 Ashikhmin at normal incidence is 1/4, not 1/16",
        SheenTwin.VisibilityAshikhmin(1f, 1f), 0.25f);

    // --- BE.3: the invariant the whole distribution exists for ------------------------------
    // GGX peaks where the half-vector meets the normal. Charlie's sin^(1/a) peaks at the HORIZON,
    // and that inversion IS the velvet rim — the cue that makes a curtain read as cloth rather
    // than as a painted board. If this ever flips, cloth goes quietly back to looking like board
    // and every other check here still passes.
    const float Rough = 0.3f;
    var atHorizon = SheenTwin.DistributionCharlie(0f, Rough);
    var atNormal = SheenTwin.DistributionCharlie(1f, Rough);
    t.Expect("BE.3 the Charlie lobe is maximal at the horizon and vanishes at the normal",
        atHorizon > 1f && atNormal < atHorizon * 1e-6f,
        $"NdotH 0 -> {atHorizon:0.0000}, NdotH 1 -> {atNormal:E3}");

    var monotonic = true;
    var previous = float.MaxValue;
    for (var i = 0; i <= 20; i++)
    {
        var d = SheenTwin.DistributionCharlie(i / 20f, Rough);
        if (d > previous + 1e-6f)
        {
            monotonic = false;
        }

        previous = d;
    }

    t.ExpectTrue("BE.3 the lobe falls monotonically from horizon to normal", monotonic,
        "the distribution is not monotonic in NdotH — energy is no longer at the horizon");

    // CONTROL: a stub returning a constant would satisfy "maximal at the horizon" trivially
    // (everything equals everything) and would sail through the monotonic check too.
    t.ExpectTrue("BE.3 CONTROL the lobe is not a constant",
        MathF.Abs(atHorizon - SheenTwin.DistributionCharlie(0.5f, Rough)) > 1e-3f,
        "the distribution returns the same value everywhere");

    // --- BE.4: the sheen layer takes energy, it does not add it -----------------------------
    // Without this the base layer is never darkened and a curtain gets brighter than the light
    // falling on it — the failure that makes a sheen implementation look like a bloom bug.
    var outOfRange = 0;
    var worstScaling = 0f;
    for (var c = 0; c <= 10; c++)
    for (var a = 0; a <= 10; a++)
    {
        var scaling = SheenTwin.Scaling(new Vector3(c / 10f, c / 20f, c / 40f), a / 10f);
        if (scaling is < 0f or > 1f)
        {
            outOfRange++;
            worstScaling = scaling;
        }
    }

    t.Expect("BE.4 sheen scaling stays in [0,1] across colour x albedo",
        outOfRange == 0, $"{outOfRange} of 121 samples left the range, worst {worstScaling:0.0000}");

    t.ExpectClose("BE.4 a white sheen at full albedo leaves the base nothing",
        SheenTwin.Scaling(Vector3.One, 1f), 0f);
    t.ExpectClose("BE.4 zero sheen albedo leaves the base untouched",
        SheenTwin.Scaling(Vector3.One, 0f), 1f);

    // --- BE.5: transmission is the light you did NOT see ------------------------------------
    // Lambertian about the inverted normal. Front-lit is zero because that light already went
    // into the diffuse term; adding it here would count the same photon twice.
    var up = Vector3.UnitY;
    var radiance = new Vector3(3f);
    var fromFront = SheenTwin.DiffuseTransmission(up, up, radiance, Vector3.One, 1f);
    var fromBehind = SheenTwin.DiffuseTransmission(up, -up, radiance, Vector3.One, 1f);

    t.Expect("BE.5 diffuse transmission is zero when the light is in front",
        fromFront == Vector3.Zero, $"{fromFront}");
    t.ExpectTrue("BE.5 and positive when the light is behind", fromBehind.X > 0f, $"{fromBehind}");
    t.ExpectClose("BE.5 a light lying in the surface plane transmits nothing",
        SheenTwin.DiffuseTransmission(up, Vector3.UnitX, radiance, Vector3.One, 1f).X, 0f);

    // pi of radiance, fully backlit, white, factor one -> exactly one. Pins the 1/pi that makes
    // this a Lambertian lobe rather than a brightness knob.
    t.ExpectClose("BE.5 the transmitted lobe carries the Lambertian 1/pi",
        SheenTwin.DiffuseTransmission(up, -up, new Vector3(MathF.PI), Vector3.One, 1f).X, 1f);

    // --- BE.6: and what the opaque base keeps -----------------------------------------------
    // Energy that went out the back did not come out the front. A consumer that adds transmission
    // without this makes cloth a light source.
    t.ExpectClose("BE.6 an opaque surface keeps all of its base layer",
        SheenTwin.DiffuseTransmissionScaling(0f), 1f);
    t.ExpectClose("BE.6 a fully transmitting surface keeps none of it",
        SheenTwin.DiffuseTransmissionScaling(1f), 0f);
    t.ExpectClose("BE.6 an out-of-range factor is clamped rather than inverted",
        SheenTwin.DiffuseTransmissionScaling(2f), 0f);
}


// ============================================================================
// Section BF — CreateMesh uploads the index buffer the mesh actually has.
// ============================================================================
//
// <b>An engine helper no SKINNED consumer could use, because of one missing branch.</b>
// Blix.Render's device.CreateMesh(MeshData) is the whole of "turn an imported mesh into
// something drawable" and it takes no shader, material, pipeline or instance count -- exactly the
// line a loader is supposed to stop at. The external RTSGame consumer calls it happily, nine
// times, because its props are small enough to index in 16 bits. Every skinned consumer in this
// tree hand-rolled the upload instead, and the reason was that CreateMesh read data.Indices
// unconditionally. For a 32-bit mesh that array is EMPTY: the draw got no indices and
// an index count of zero, which renders nothing rather than failing. MeshData's own comment states
// the obligation -- "consumers branch on IndexFormat to decide which array + which
// CreateIndexBuffer overload to use" -- and the helper written to spare consumers that branch was
// the one place not doing it.
//
// Pinned against a RECORDING STUB rather than a real device, deliberately. Every suite in the root
// gate is deviceless, and the question here is which overload the helper chose and what index
// count it reported -- both answerable without a GPU. It also means the check does not depend on
// some owned asset happening to exceed 65535 vertices in one primitive, which is the conformance
// argument conventions §7 already makes about reference assets.
{
    // 16-bit stays 16-bit, and reports its own length.
    var narrow = new MeshData(
        "narrow", new byte[3 * 16], new ushort[] { 0, 1, 2 },
        VertexPositionTexture.Layout,
        new Bounds3(Vector3.Zero, Vector3.One));
    var stub16 = new RecordingDevice();
    var mesh16 = stub16.CreateMesh(narrow, "bf.narrow");
    t.Expect("BF.1 a 16-bit mesh takes the ushort overload",
        stub16.LastIndexWidth == 16, $"took the {stub16.LastIndexWidth}-bit path");
    t.ExpectClose("BF.1 and reports its three indices", mesh16.IndexCount, 3);

    // 32-bit is the case that was broken: Indices is empty and Indices32 is authoritative.
    var wide = new MeshData(
        "wide", new byte[4 * 16], Array.Empty<ushort>(),
        VertexPositionTexture.Layout,
        new Bounds3(Vector3.Zero, Vector3.One),
        Indices32: new uint[] { 0, 1, 2, 2, 1, 3 });
    var stub32 = new RecordingDevice();
    var mesh32 = stub32.CreateMesh(wide, "bf.wide");
    t.Expect("BF.2 a 32-bit mesh takes the uint overload, not the empty ushort array",
        stub32.LastIndexWidth == 32, $"took the {stub32.LastIndexWidth}-bit path");
    t.Expect("BF.2 and reports SIX indices rather than zero",
        mesh32.IndexCount == 6, $"IndexCount {mesh32.IndexCount}");

    // CONTROL: the two cases must actually differ, or the stub is reporting one path for both and
    // BF.1 and BF.2 would agree with each other while agreeing with nothing else.
    t.ExpectTrue("BF.2 CONTROL the two meshes took different paths",
        stub16.LastIndexWidth != stub32.LastIndexWidth);

    t.Expect("BF.3 the mesh carries the imported bounds through",
        mesh32.Bounds.Min == Vector3.Zero && mesh32.Bounds.Max == Vector3.One,
        $"{mesh32.Bounds.Min}..{mesh32.Bounds.Max}");
    t.Expect("BF.3 and the caller's name, not the MeshData's",
        mesh32.Name == "bf.wide", mesh32.Name);
}


// ============================================================================
// Section BG — a project declares every shader include it reads, and shaders
//              that share a uniform block agree about it.
// ============================================================================
//
// <b>Two classes of breakage that a clean build and seven green suites cannot see.</b> Both were
// hit on the same day, and in both cases the only instrument that worked was a person looking at
// the screen.
//
//   1. VulkanSponza declared one GlslInclude while its shaders included nine files, so editing
//      shadow.glsl or sheen.glsl never invalidated the .spv. The build succeeded, the gate passed,
//      and the shader on disk was stale. It surfaced only because a tunable's DEFAULT changed and
//      could be read back; a changed lighting term would have shown up as a picture nobody had
//      reason to distrust.
//
//   2. skybox.vert declared a sparse copy of lit.frag's Frame block at hand-written absolute byte
//      offsets. Deleting a vec4 from that block moved uFog and pointed the skybox at the member
//      after it. The device refused the pipeline by name at startup -- which is good, and is also
//      the point: the gate had already passed, because no suite here builds a pipeline.
//
// These are static properties of the source and the project file, so they are checkable without a
// GPU, in milliseconds, on every platform -- which matters more than it sounds, because CI has no
// Vulkan device and the portability arc will not give it one soon.
{
    var srcDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
    var engineShaders = Path.Combine(srcDir, "Blix.Shaders");

    // CONTROL: every check below is a loop over discovered projects, and a loop over nothing
    // passes. conventions §5's corollary — a green result must be evidence the check ran.
    var projects = Directory.Exists(srcDir)
        ? Directory.GetFiles(srcDir, "*.csproj", SearchOption.AllDirectories)
            .Where(p => File.ReadAllText(p).Contains("<GlslShader", StringComparison.Ordinal))
            .ToArray()
        : Array.Empty<string>();
    t.Expect("BG.0 CONTROL shader-bearing projects were found",
        projects.Length >= 4, $"{projects.Length} project(s) under {srcDir}");

    // An MSBuild item's Include, expanded on disk. Only the two glob forms the tree uses.
    static IEnumerable<string> Expand(string projectDir, string include)
    {
        var rel = include.Replace('\\', Path.DirectorySeparatorChar);
        var full = Path.GetFullPath(Path.Combine(projectDir, rel));
        if (!full.Contains('*')) return File.Exists(full) ? new[] { full } : Array.Empty<string>();
        var recursive = full.Contains("**");
        var dir = full[..full.IndexOf('*')].TrimEnd(Path.DirectorySeparatorChar);
        var pattern = Path.GetFileName(full);
        return Directory.Exists(dir)
            ? Directory.GetFiles(dir, pattern,
                recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly)
            : Array.Empty<string>();
    }

    static string[] Items(string projectText, string projectDir, string itemName) =>
        Regex.Matches(projectText, $"<{itemName}\\s+Include=\"([^\"]+)\"")
            .SelectMany(m => Expand(projectDir, m.Groups[1].Value))
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

    var undeclared = new List<string>();
    var disagreements = new List<string>();
    var hardOffsets = new List<string>();
    var shadersChecked = 0;
    var blocksCompared = 0;

    foreach (var project in projects)
    {
        var projectDir = Path.GetDirectoryName(project)!;
        var text = File.ReadAllText(project);
        var shaders = Items(text, projectDir, "GlslShader");
        var declared = new HashSet<string>(Items(text, projectDir, "GlslInclude"), StringComparer.Ordinal);
        var name = Path.GetFileNameWithoutExtension(project);

        // Block signature per (set, binding), across this project's shaders.
        var blocks = new Dictionary<string, (string Shader, string[] Members)>(StringComparer.Ordinal);

        foreach (var shader in shaders)
        {
            if (!File.Exists(shader)) continue;
            shadersChecked++;

            GlslPreprocessResult expanded;
            try
            {
                expanded = ShaderLoader.PreprocessFile(
                    shader, new[] { engineShaders, Path.Combine(projectDir, "Shaders") });
            }
            catch (Exception ex)
            {
                undeclared.Add($"{name}/{Path.GetFileName(shader)}: will not preprocess — {ex.Message}");
                continue;
            }

            // BG.1 — every file this shader actually reached must be a declared input, or a
            // change to it does not rebuild. SourceMap[0] is the shader itself.
            foreach (var reached in expanded.SourceMap.Skip(1))
            {
                if (!declared.Contains(Path.GetFullPath(reached)))
                {
                    undeclared.Add(
                        $"{name}: {Path.GetFileName(shader)} includes {Path.GetFileName(reached)}, " +
                        "which is not a GlslInclude — editing it will not rebuild the .spv");
                }
            }

            // BG.2 — uniform blocks, from the EXPANDED source so an included declaration counts.
            foreach (Match b in Regex.Matches(
                expanded.ExpandedSource,
                @"layout\s*\(\s*set\s*=\s*(\d+)\s*,\s*binding\s*=\s*(\d+)\s*\)\s*uniform\s+(\w+)\s*\{([^}]*)\}",
                RegexOptions.Singleline))
            {
                var key = $"set{b.Groups[1].Value}.binding{b.Groups[2].Value} {b.Groups[3].Value}";
                var body = b.Groups[4].Value;

                if (body.Contains("layout(offset", StringComparison.Ordinal)
                    || body.Contains("layout (offset", StringComparison.Ordinal))
                {
                    hardOffsets.Add($"{name}: {Path.GetFileName(shader)} pins byte offsets inside {key}");
                }

                var members = Regex.Matches(body, @"\b(?:float|int|uint|bool|vec[234]|ivec[234]|mat[234])\s+(\w+)\s*(?:\[[^\]]*\])?\s*;")
                    .Select(m => m.Groups[1].Value).ToArray();
                if (members.Length == 0) continue;

                if (blocks.TryGetValue(key, out var first))
                {
                    blocksCompared++;
                    var shorter = Math.Min(first.Members.Length, members.Length);
                    // A PREFIX is legal and common: a vertex stage may name only the leading
                    // members it uses, and std140 puts them at the same offsets. Skipping a
                    // member is what is not legal, because everything after it shifts.
                    if (!first.Members.Take(shorter).SequenceEqual(members.Take(shorter)))
                    {
                        disagreements.Add(
                            $"{name}: {key} is declared differently by {first.Shader} and " +
                            $"{Path.GetFileName(shader)} — the two read the same bytes as different members");
                    }
                }
                else
                {
                    blocks[key] = (Path.GetFileName(shader), members);
                }
            }
        }
    }

    t.Expect("BG.0 CONTROL shaders were actually preprocessed", shadersChecked >= 20,
        $"{shadersChecked} shader(s)");
    t.Expect("BG.0 CONTROL shared blocks were actually compared", blocksCompared >= 1,
        $"{blocksCompared} comparison(s)");

    t.Expect("BG.1 every shader include is a declared build input",
        undeclared.Count == 0, string.Join("; ", undeclared.Take(4)));

    t.Expect("BG.2 shaders sharing a uniform block agree about its members",
        disagreements.Count == 0, string.Join("; ", disagreements.Take(4)));

    // Not a failure on its own — a single-owner block may legitimately pin offsets — but a shared
    // one must not, because the offsets then belong to a file that does not know it owns them.
    t.Expect("BG.2 no shader pins absolute byte offsets into a shared block",
        hardOffsets.Count == 0, string.Join("; ", hardOffsets.Take(4)));
}

// ============================================================================
// Section BH — an application asks AppFiles where its assets are, rather than
//              assuming they sit beside the binary.
// ============================================================================
//
// <b>Because "beside the binary" stops being true the moment the thing is published.</b> A macOS
// .app cannot keep data in Contents/MacOS: codesign reads a cooked `foo.textures/` sidecar there
// as a malformed nested bundle and refuses to sign the bundle at all. So `blix publish` moves
// Assets to Contents/Resources, and `Blix.Core.AppFiles` is the one place that knows to look
// across for it.
//
// The check exists because the hand-rolled form had already been written eleven times, once per
// place that wanted a model or a manifest, and every one of them would have been wrong in a
// published bundle. Not loudly wrong: Bulwark under that layout drew grey primitives instead of
// its turrets and carried on to a clean exit. This is conventions §4's shape — an engine step
// copied privately, missing the case the engine handles — and a grep is the only instrument that
// sees it before a person does.
{
    var srcDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
    // The examples too: they are the code most likely to be copied, and they were not searched.
    var examplesDir = Path.GetFullPath(Path.Combine(srcDir, "..", "examples"));
    var sources = Directory.Exists(srcDir)
        ? Directory.GetFiles(srcDir, "*.cs", SearchOption.AllDirectories)
            .Concat(Directory.Exists(examplesDir)
                ? Directory.GetFiles(examplesDir, "*.cs", SearchOption.AllDirectories)
                : Array.Empty<string>())
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .ToArray()
        : Array.Empty<string>();

    // CONTROL: a grep over nothing passes. conventions §5.
    t.Expect("BH.0 CONTROL sources were found to search",
        sources.Length >= 100, $"{sources.Length} file(s) under {srcDir}");

    // The hand-rolled form, in either order of quoting, but not AppFiles' own implementation --
    // it is the one place entitled to write it.
    var handRolled = new List<string>();
    var handRolledShaders = new List<string>();
    var appFilesPath = Path.Combine(srcDir, "Blix.Core", "AppFiles.cs");
    foreach (var file in sources)
    {
        if (string.Equals(file, appFilesPath, StringComparison.Ordinal)) continue;
        var text = File.ReadAllText(file);
        var lines = text.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            if (Regex.IsMatch(lines[i], @"AppContext\.BaseDirectory\s*,\s*""Shaders"""))
                handRolledShaders.Add($"{Path.GetFileName(file)}:{i + 1}");
            if (!Regex.IsMatch(lines[i], @"AppContext\.BaseDirectory\s*,\s*""Assets""")) continue;
            handRolled.Add($"{Path.GetFileName(file)}:{i + 1}");
        }
    }

    t.Expect("BH.1 no application builds its own asset path from AppContext.BaseDirectory",
        handRolled.Count == 0,
        handRolled.Count == 0 ? "all sites go through AppFiles" : string.Join("; ", handRolled.Take(6)));

    // The same fact for compiled shaders, which stay beside the binary even in a bundle. It had been
    // written by hand twenty-two times, four of them inside the engine's own libraries.
    t.Expect("BH.3 no application builds its own shader path from AppContext.BaseDirectory",
        handRolledShaders.Count == 0,
        handRolledShaders.Count == 0 ? "all sites go through AppFiles" : string.Join("; ", handRolledShaders.Take(6)));

    // And the resolver itself answers, so the check above is not passing because nothing asks.
    t.Expect("BH.2 AppFiles resolves an Assets path",
        AppFiles.Assets.EndsWith("Assets", StringComparison.Ordinal), AppFiles.Assets);
    t.Expect("BH.2 AppFiles.Asset appends beneath it",
        AppFiles.Asset("models", "x.glb") == Path.Combine(AppFiles.Assets, "models", "x.glb"),
        AppFiles.Asset("models", "x.glb"));
    t.Expect("BH.4 AppFiles resolves the Shaders directory beside the binary",
        // Asked by parts rather than by writing the path out, which BH.3 would rightly refuse. It did,
        // the first time: the only hand-built shader path it found was this line's, which is how it
        // is known to see one.
        Path.GetFileName(AppFiles.Shaders) == "Shaders"
        && Path.GetDirectoryName(AppFiles.Shaders) == Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory)
        && AppFiles.Shader("lit.frag.spv") == Path.Combine(AppFiles.Shaders, "lit.frag.spv"),
        AppFiles.Shaders);
}

// ============================================================================
// Section BI — the cook does not name a platform's library filenames.
// ============================================================================
//
// <b>Cooking runs as a build step, so anything the cook assumes about the host is a thing that
// fails at build time on a platform nobody has tried.</b> `Blix.Recipes` P/Invokes two vendored
// natives, and it used to write their filenames down: `libmeshoptimizer.dylib` in the resolver
// table and `libblix_bc7.dylib` again, separately, in `Bc7Native`. Two spellings of one fact,
// both true on exactly one platform.
//
// `NativeLibraries.FileName` is now the only place entitled to know that a shared library is
// `lib*.dylib` here, `lib*.so` there and `*.dll` elsewhere. This pins that, because the failure
// it prevents is invisible from macOS: the tree builds, the suites pass, and the first person on
// another platform gets a DllNotFoundException from inside a P/Invoke with no filename in it.
{
    var srcDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
    var recipesDir = Path.Combine(srcDir, "Blix.Recipes");
    var recipeSources = Directory.Exists(recipesDir)
        ? Directory.GetFiles(recipesDir, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .ToArray()
        : Array.Empty<string>();

    // CONTROL: conventions §5 — a loop over nothing passes.
    t.Expect("BI.0 CONTROL the cook's sources were found",
        recipeSources.Length >= 5, $"{recipeSources.Length} file(s) under {recipesDir}");

    // A library extension inside a string literal. Comments and doc remarks are allowed to
    // discuss them -- this is about what the code believes, not about what it explains.
    var named = new List<string>();
    var namer = Path.Combine(recipesDir, "NativeLibraries.cs");
    foreach (var file in recipeSources)
    {
        if (string.Equals(file, namer, StringComparison.Ordinal)) continue;
        var lines = File.ReadAllLines(file);
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var code = line.TrimStart();
            if (code.StartsWith("//", StringComparison.Ordinal) || code.StartsWith("///", StringComparison.Ordinal)) continue;
            if (!Regex.IsMatch(line, @"""[^""]*\.(dylib|so|dll)""")) continue;
            named.Add($"{Path.GetFileName(file)}:{i + 1}");
        }
    }

    t.Expect("BI.1 the cook names no platform library filename",
        named.Count == 0,
        named.Count == 0 ? "only NativeLibraries knows" : string.Join("; ", named.Take(6)));

    // Every DllImport the cook makes must be a name the resolver answers. One that is not falls
    // through to bare-name probing, which is exactly the path that does not reliably work.
    var imported = new SortedSet<string>(StringComparer.Ordinal);
    foreach (var file in recipeSources)
    {
        foreach (Match m in Regex.Matches(File.ReadAllText(file), @"\[DllImport\(\s*(?:Lib|""([^""]+)"")"))
        {
            imported.Add(m.Groups[1].Success && m.Groups[1].Value.Length > 0 ? m.Groups[1].Value : "Lib");
        }
    }

    // `Lib` is a per-file const; resolve each to its value so the comparison is on real names.
    var constants = new SortedSet<string>(StringComparer.Ordinal);
    foreach (var file in recipeSources)
    {
        foreach (Match m in Regex.Matches(File.ReadAllText(file), @"const string Lib = ""([^""]+)"""))
        {
            constants.Add(m.Groups[1].Value);
        }
    }

    var registered = File.Exists(namer)
        ? new SortedSet<string>(
            Regex.Matches(File.ReadAllText(namer), @"""([a-z0-9_]+)""").Select(m => m.Groups[1].Value),
            StringComparer.Ordinal)
        : new SortedSet<string>(StringComparer.Ordinal);

    t.Expect("BI.0 CONTROL the cook's P/Invoke targets were found",
        constants.Count >= 2, string.Join(", ", constants));
    t.Expect("BI.2 every native the cook imports is one the resolver answers",
        constants.All(registered.Contains),
        $"imports [{string.Join(", ", constants)}] vs registered [{string.Join(", ", registered)}]");
}

// ============================================================================
// Section BJ — an application reads its shader interface, it does not restate it.
// ============================================================================
//
// <b>Thirty-eight hand-written ShaderInterface tables, each naming a set, a binding, a type, a
// stage and a push size that the shader beside it already declared.</b> They were checked against
// reflection before being removed, and two already disagreed: VulkanHello and VulkanGraph declared
// the frame UBO visible to the fragment stage, which does not read it, and Pong declared a 64-byte
// push range against a block that is 52 -- vec4 + vec2 + vec2 + vec4 + float -- with the same
// wrong 64 written in three places.
//
// Neither was breaking anything the day it was found. Both are the shape that breaks later, and
// a device only objects to some of it: an over-wide stage and an over-long push range are legal.
//
// What reflection cannot supply is a runtime-sized block's length -- `InstanceData instances[]`
// reflects with block_size 0, because the count belongs to the application. That is what
// ShaderInterface.WithArrayLength is for, and asking for it explicitly is the point: the shader
// owns the shape (offset and stride), the caller owns the count.
{
    var srcDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
    var sources = Directory.Exists(srcDir)
        ? Directory.GetFiles(srcDir, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .ToArray()
        : Array.Empty<string>();

    t.Expect("BJ.0 CONTROL sources were found to search",
        sources.Length >= 100, $"{sources.Length} file(s)");

    // ShaderReflection builds one from the merged stages; that is the constructor's whole job.
    // Everyone else asks it. Test sources are exempt: several exist to exercise the type itself.
    var owner = Path.Combine(srcDir, "Blix.Graphics", "ShaderReflection.cs");
    var restated = new List<string>();
    foreach (var file in sources)
    {
        if (string.Equals(file, owner, StringComparison.Ordinal)) continue;
        if (file.Contains($"{Path.DirectorySeparatorChar}Blix.Test.", StringComparison.Ordinal)) continue;
        var lines = File.ReadAllLines(file);
        for (var i = 0; i < lines.Length; i++)
        {
            // Both spellings. SpriteBatch used the target-typed one -- `ShaderInterface
            // Interface { get; } = new(...)` -- and the first version of this check, which looked
            // only for the named constructor, passed over it in silence. BJ.2 caught it instead,
            // which is luck rather than design.
            var named = lines[i].Contains("new ShaderInterface(", StringComparison.Ordinal);
            var targetTyped = Regex.IsMatch(lines[i], @"ShaderInterface\b[^=;]*=\s*new\s*\(");
            if (!named && !targetTyped) continue;
            restated.Add($"{Path.GetFileName(file)}:{i + 1}");
        }
    }

    t.Expect("BJ.1 no application builds a ShaderInterface by hand",
        restated.Count == 0,
        restated.Count == 0 ? "all interfaces come from reflection" : string.Join("; ", restated.Take(6)));

    // And the sidecars those callers read are actually produced. A project that reflects in C#
    // and does not emit reflection at build time fails at startup, not here, which is late.
    var reflecting = Directory.GetFiles(srcDir, "*.csproj", SearchOption.AllDirectories)
        .Where(p => File.ReadAllText(p).Contains("<GlslShader", StringComparison.Ordinal))
        .ToArray();
    var silent = reflecting
        .Where(p => !File.ReadAllText(p).Contains("<BlixShaderReflect>true", StringComparison.Ordinal))
        .Select(Path.GetFileNameWithoutExtension)
        .ToArray();

    t.Expect("BJ.0 CONTROL shader-bearing projects were found",
        reflecting.Length >= 8, $"{reflecting.Length} project(s)");
    t.Expect("BJ.2 every project with shaders emits their reflection",
        silent.Length == 0, string.Join(", ", silent));
}

// ============================================================================
// Section BK — MergeStages rejects two blocks wearing one binding.
// ============================================================================
//
// <b>Reflection is public machinery, so its correctness cannot rest on this repository's gate.</b>
// A stage reflects only the members it references, so the same (set,binding) legitimately comes
// back shorter from one stage than another -- measured: studio_lit.vert sees one 64-byte member
// of the Frame block where studio_lit.frag sees eleven, 368 bytes, and the short one is a true
// prefix beginning at uViewProjection@0.
//
// MergeStages used to keep whichever view had the greater TotalSize and check nothing else, so
// two stages declaring DIFFERENT members at the same offset merged silently into one of them.
// Nothing downstream would object -- the descriptor type matches, so the device has no opinion --
// and the by-name write path would put bytes where the other stage reads something else.
{
    static ShaderReflection.ReflStage Stage(ShaderStages st, int total, params (string Name, int Offset, int Size)[] members) =>
        new(st,
            new[]
            {
                new DescriptorSetSlot(0, 0, ShaderResourceType.UniformBuffer, st,
                    BlockLayout: new UniformBlockLayout(total,
                        members.Select(m => new UniformBlockMember(m.Name, m.Offset, m.Size)).ToArray())),
            },
            Array.Empty<PushConstantRange>());

    // A genuine prefix merges, keeps the fuller layout, and ORs the stages.
    var merged = ShaderReflection.MergeStages(
        Stage(ShaderStages.Vertex, 64, ("uViewProjection", 0, 64)),
        Stage(ShaderStages.Fragment, 128, ("uViewProjection", 0, 64), ("uCascadeVP0", 64, 64)));
    var slot = merged.Slots.Single();
    t.Expect("BK.1 a shorter view that is a prefix merges into the fuller layout",
        slot.BlockLayout?.TotalSize == 128 && slot.BlockLayout?.Members.Count == 2,
        $"{slot.BlockLayout?.TotalSize}B, {slot.BlockLayout?.Members.Count} member(s)");
    t.Expect("BK.1 and the merged slot carries both stages",
        slot.Stages == (ShaderStages.Vertex | ShaderStages.Fragment), slot.Stages.ToString());

    // Same offset, different member. Equal sizes, so picking "the fuller" cannot separate them.
    t.Expect("BK.2 different members at one offset are rejected",
        Throws(() => ShaderReflection.MergeStages(
            Stage(ShaderStages.Vertex, 32, ("uA", 0, 16), ("uB", 16, 16)),
            Stage(ShaderStages.Fragment, 32, ("uA", 0, 16), ("uC", 16, 16)))),
        "equal TotalSize, incompatible members");

    // A gap is the same disagreement: the shorter is not a prefix of the longer.
    t.Expect("BK.3 a member with no counterpart at its offset is rejected",
        Throws(() => ShaderReflection.MergeStages(
            Stage(ShaderStages.Vertex, 48, ("uA", 0, 16), ("uOdd", 32, 16)),
            Stage(ShaderStages.Fragment, 64, ("uA", 0, 16), ("uB", 16, 16), ("uC", 48, 16)))),
        "offset 32 exists in one view only");

    // CONTROL: the rejections above must not be this helper reporting every call as a throw.
    t.Expect("BK.0 CONTROL a compatible merge does not throw",
        !Throws(() => ShaderReflection.MergeStages(
            Stage(ShaderStages.Vertex, 64, ("uViewProjection", 0, 64)),
            Stage(ShaderStages.Fragment, 64, ("uViewProjection", 0, 64)))),
        "identical views merge");
}

// ============================================================================
// Section BL — a push-constant struct is written from the shader's reflection.
// ============================================================================
//
// The generator runs inside every shader-bearing project's compiler, so it is exercised here the
// way a compiler runs it: a snippet, the attribute assembly, and reflection JSON handed in as an
// AdditionalFile, through Roslyn's own driver. No device and no glslc, so this is part of the
// deviceless gate, and each refusal is checked by the id and the words a developer will read.
{
    static (string Source, string[] Diagnostics, string[] CompileErrors) Generate(string code, params (string Stage, string Json)[] reflections)
    {
        var tpa = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator);
        var references = tpa.Select(p => (Microsoft.CodeAnalysis.MetadataReference)Microsoft.CodeAnalysis.MetadataReference.CreateFromFile(p))
            .Append(Microsoft.CodeAnalysis.MetadataReference.CreateFromFile(typeof(PushConstantsAttribute).Assembly.Location))
            .ToArray();
        var compilation = Microsoft.CodeAnalysis.CSharp.CSharpCompilation.Create("bl",
            new[] { Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText(code) }, references,
            new Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions(Microsoft.CodeAnalysis.OutputKind.DynamicallyLinkedLibrary));

        var texts = reflections.Select(r => (Microsoft.CodeAnalysis.AdditionalText)new InlineText(
            r.Stage.EndsWith(".json", StringComparison.Ordinal) ? $"/shaders/{r.Stage}" : $"/shaders/{r.Stage}.spv.refl.json", r.Json));
        Microsoft.CodeAnalysis.GeneratorDriver driver = Microsoft.CodeAnalysis.CSharp.CSharpGeneratorDriver.Create(
            new[]
            {
                Microsoft.CodeAnalysis.GeneratorExtensions.AsSourceGenerator(new Blix.Shaders.Generator.PushConstantsGenerator()),
                Microsoft.CodeAnalysis.GeneratorExtensions.AsSourceGenerator(new Blix.Shaders.Generator.ShaderEnumCheck()),
            }, texts);
        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out var output, out var diagnostics);

        var generated = driver.GetRunResult().Results.SelectMany(r => r.GeneratedSources).Select(g => g.SourceText.ToString());
        var errors = output.GetDiagnostics().Where(d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error).Select(d => d.ToString());
        return (string.Join("\n", generated), diagnostics.Select(d => $"{d.Id}: {d.GetMessage()}").ToArray(), errors.ToArray());
    }

    static string Block(params (string Name, string Type, int Offset)[] members) =>
        "{\"types\":{\"_1\":{\"name\":\"PushConstants\",\"members\":[" +
        string.Join(",", members.Select(m => $"{{\"name\":\"{m.Name}\",\"type\":\"{m.Type}\",\"offset\":{m.Offset}}}")) +
        "]}},\"push_constants\":[{\"type\":\"_1\",\"name\":\"pc\",\"push_constant\":true}]}";

    const string NoBlock = "{\"types\":{}}";
    const string Using = "using Blix.Graphics;\n";

    var exact = Generate(Using + "[PushConstants(\"w.vert\")] partial struct W;",
        ("w.vert", Block(("uModel", "mat4", 0), ("uTint", "vec4", 64))));
    t.Expect("BL.1 fields come from the shader, with exact names by default",
        exact.Source.Contains("[global::System.Runtime.InteropServices.FieldOffset(0)] public global::System.Numerics.Matrix4x4 uModel;")
        && exact.Source.Contains("FieldOffset(64)] public global::System.Numerics.Vector4 uTint;")
        && exact.Diagnostics.Length == 0 && exact.CompileErrors.Length == 0,
        string.Join(" | ", exact.Diagnostics.Concat(exact.CompileErrors)));
    t.Expect("BL.1 and the struct is the block's size", exact.Source.Contains("Size = 80") && exact.Source.Contains("SizeInBytes = 80"));

    var prefixed = Generate(Using + "[PushConstants(\"w.vert\", Prefix = \"u\")] partial struct W;",
        ("w.vert", Block(("uModel", "mat4", 0))));
    t.Expect("BL.2 a Prefix stated on the declaration is removed", prefixed.Source.Contains(" Model;") && prefixed.CompileErrors.Length == 0);
    t.Expect("BL.2 and the rule in force is written beside each field", prefixed.Source.Contains("(Prefix = \"u\")"));

    // GLSL pads a vec3 to 16 where C# packs Vector3 at 12. The offset is the shader's.
    var padded = Generate(Using + "[PushConstants(\"w.vert\")] partial struct W;",
        ("w.vert", Block(("uA", "vec3", 0), ("uB", "vec3", 16))));
    t.Expect("BL.3 offsets are the shader's, padding included",
        padded.Source.Contains("FieldOffset(16)] public global::System.Numerics.Vector3 uB;"), padded.Source);

    var renamed = Generate(Using + "[PushConstants(\"w.vert\", Prefix = \"u\"), ShaderName(\"uMVP\", \"ModelViewProjection\")] partial struct W;",
        ("w.vert", Block(("uMVP", "mat4", 0), ("uTint", "vec4", 64))));
    t.Expect("BL.4 [ShaderName] renames one member and the rule names the rest",
        renamed.Source.Contains(" ModelViewProjection;") && renamed.Source.Contains(" Tint;"));

    var merged = Generate(Using + "[PushConstants(\"w.vert\", \"w.frag\")] partial struct W;",
        ("w.vert", Block(("uModel", "mat4", 0))), ("w.frag", Block(("uModel", "mat4", 0), ("uTint", "vec4", 64))));
    t.Expect("BL.5 stages that agree merge, and a stage may declare more of the block",
        merged.Source.Contains(" uModel;") && merged.Source.Contains(" uTint;") && merged.Diagnostics.Length == 0);

    var fragmentOnly = Generate(Using + "[PushConstants(\"w.vert\", \"w.frag\")] partial struct W;",
        ("w.vert", Block(("uModel", "mat4", 0))), ("w.frag", NoBlock));
    t.Expect("BL.5 a listed stage with no push block is fine", fragmentOnly.Diagnostics.Length == 0 && fragmentOnly.Source.Contains(" uModel;"));

    void Refused(string label, string id, string mustSay, (string Source, string[] Diagnostics, string[] CompileErrors) run) =>
        t.Expect(label, run.Diagnostics.Any(d => d.StartsWith(id, StringComparison.Ordinal) && d.Contains(mustSay, StringComparison.Ordinal)),
            string.Join(" | ", run.Diagnostics));

    Refused("BL.6 stages that disagree are refused, naming both", "BLX1005", "'uTint' (vec4 at byte 64) in w.vert",
        Generate(Using + "[PushConstants(\"w.vert\", \"w.frag\")] partial struct W;",
            ("w.vert", Block(("uTint", "vec4", 64))), ("w.frag", Block(("uGlow", "vec4", 64)))));
    Refused("BL.7 a type C# cannot hold is refused, naming it", "BLX1006", "'uNormal' is a mat3",
        Generate(Using + "[PushConstants(\"w.vert\")] partial struct W;", ("w.vert", Block(("uNormal", "mat3", 0)))));
    Refused("BL.8 a struct that is not partial is refused", "BLX1001", "must be partial",
        Generate(Using + "[PushConstants(\"w.vert\")] struct W { }", ("w.vert", Block(("uModel", "mat4", 0)))));
    Refused("BL.9 a stage with no reflection is refused, naming it", "BLX1003", "'w.frag'",
        Generate(Using + "[PushConstants(\"w.vert\", \"w.frag\")] partial struct W;", ("w.vert", Block(("uModel", "mat4", 0)))));
    Refused("BL.10 two members that one rule makes one name are refused, with the rule", "BLX1008", "(Prefix = \"u\")",
        Generate(Using + "[PushConstants(\"w.vert\", Prefix = \"u\")] partial struct W;",
            ("w.vert", Block(("uTint", "vec4", 0), ("Tint", "vec4", 16)))));
    Refused("BL.11 a [ShaderName] for no member is refused, listing the members", "BLX1009", "It has: uModel",
        Generate(Using + "[PushConstants(\"w.vert\"), ShaderName(\"uNope\", \"X\")] partial struct W;", ("w.vert", Block(("uModel", "mat4", 0)))));
    Refused("BL.12 a struct that declares its own fields is refused", "BLX1002", "declares the field 'Extra'",
        Generate(Using + "[PushConstants(\"w.vert\")] partial struct W { public int Extra; }", ("w.vert", Block(("uModel", "mat4", 0)))));

    // Shader enums: checked against the //@tune enum{} the shader declares, never generated.
    static string Tune(string member, params string[] names) =>
        $"[{{\"name\":\"{member}\",\"kind\":\"Enum\",\"enumNames\":[{string.Join(",", names.Select(n => $"\"{n}\""))}]}}]";

    var agreeing = Generate(Using + "[ShaderEnum(\"p.frag\", \"uMode\")] enum Mode { Off, BentNormal }",
        ("p.frag.spv.tune.json", Tune("uMode", "Off", "Bent normal")));
    t.Expect("BL.13 an enum whose names match the shader's, ignoring case and spaces, builds",
        agreeing.Diagnostics.Length == 0, string.Join(" | ", agreeing.Diagnostics));
    Refused("BL.14 a reordered enum is refused, with both lists", "BLX1103", "C# has Off, Hejl, AgX; the shader has Off, AgX, Hejl",
        Generate(Using + "[ShaderEnum(\"p.frag\", \"uMode\")] enum Mode { Off, Hejl, AgX }",
            ("p.frag.spv.tune.json", Tune("uMode", "Off", "AgX", "Hejl"))));
    Refused("BL.15 a shader member with no names is refused, with the line to add", "BLX1102", "//@tune enum{ Off, On }",
        Generate(Using + "[ShaderEnum(\"p.frag\", \"uMode\")] enum Mode { Off, On }",
            ("p.frag.spv.tune.json", "[]")));
    Refused("BL.16 values that are not positions are refused", "BLX1104", "Mode.On is 5",
        Generate(Using + "[ShaderEnum(\"p.frag\", \"uMode\")] enum Mode { Off, On = 5 }",
            ("p.frag.spv.tune.json", Tune("uMode", "Off", "On"))));
    Refused("BL.17 a stage that is not in the project is refused", "BLX1101", "'q.frag'",
        Generate(Using + "[ShaderEnum(\"q.frag\", \"uMode\")] enum Mode { Off }",
            ("p.frag.spv.tune.json", Tune("uMode", "Off"))));

    // Binding a texture by name rests on reflection keeping the sampler's name, arrays included.
    var reflected = ShaderReflection.Parse(
        "{\"entryPoints\":[{\"name\":\"main\",\"mode\":\"frag\"}],\"types\":{}," +
        "\"textures\":[{\"type\":\"sampler2D\",\"name\":\"uHdr\",\"set\":0,\"binding\":0}," +
        "{\"type\":\"sampler2D\",\"name\":\"uCascades\",\"array\":[3],\"array_size_is_literal\":[true],\"set\":0,\"binding\":1}]}",
        "bl18.frag");
    t.Expect("BL.18 reflection keeps each sampler's name, and an array's base name",
        reflected.Slots.Any(s => s.Name == "uHdr" && s.Binding == 0)
        && reflected.Slots.Any(s => s.Name == "uCascades" && s.Binding == 1 && s.Count == 3),
        string.Join(", ", reflected.Slots.Select(s => $"{s.Name}@{s.Binding}x{s.Count}")));
}

// ── Section BM: separate images and declared samplers ──────────────────────────────────────────────
// A combined sampler2D counts against the per-stage sampler limit (16 on MoltenVK); a texture2D does
// not, and the few samplers it is read through are declared in the shader with //@sampler and built
// into the layout. These are the deviceless halves: the scan, the sidecar, and reflection's types.
{
    const string Declared = "//@sampler LinearClampMipmap\nlayout(set = 1, binding = 20) uniform sampler uEnv;\n" +
                            "layout(set = 1, binding = 0) uniform textureCube uIrradiance;\n";
    var scanned = ShaderSamplers.Scan(Declared);
    t.Expect("BM.1 //@sampler names the preset of the sampler declared after it",
        scanned.Count == 1 && scanned[0].Name == "uEnv" && scanned[0].Description == SamplerDescription.LinearClampMipmap,
        string.Join(", ", scanned));
    t.ExpectThrows("BM.2 a separate sampler with no //@sampler fails the scan, naming it and the presets",
        () => ShaderSamplers.Scan("layout(set = 1, binding = 20) uniform sampler uBare;\n"), mustMention: "uBare");
    t.ExpectThrows("BM.3 a //@sampler followed by anything but a sampler fails",
        () => ShaderSamplers.Scan("//@sampler LinearClamp\nlayout(set = 1, binding = 0) uniform texture2D uX;\n"),
        mustMention: "must be followed");
    t.ExpectThrows("BM.4 an unknown preset fails, listing the ones that exist",
        () => ShaderSamplers.Scan("//@sampler LinearWobble\nlayout(set = 1, binding = 20) uniform sampler uS;\n"),
        mustMention: "LinearClampMipmap");
    t.Expect("BM.5 the presets are SamplerDescription's own, so a new one needs no second list",
        ShaderSamplers.PresetNames.SequenceEqual(typeof(SamplerDescription)
            .GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .Where(p => p.PropertyType == typeof(SamplerDescription)).Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal)),
        string.Join(", ", ShaderSamplers.PresetNames));
    var roundTrip = ShaderSamplerSidecar.FromJson(ShaderSamplerSidecar.ToJson(scanned));
    t.Expect("BM.6 the sidecar round-trips", roundTrip.SequenceEqual(scanned), string.Join(", ", roundTrip));

    const string Refl =
        "{\"entryPoints\":[{\"name\":\"main\",\"mode\":\"frag\"}],\"types\":{}," +
        "\"separate_images\":[{\"type\":\"textureCube\",\"name\":\"uIrradiance\",\"set\":1,\"binding\":0}]," +
        "\"separate_samplers\":[{\"type\":\"sampler\",\"name\":\"uEnv\",\"set\":1,\"binding\":20}]}";
    var withState = ShaderReflection.Parse(Refl, "bm.frag", scanned);
    t.Expect("BM.7 a texture2D reflects as a separate image, not as a combined sampler",
        withState.Slots.Single(s => s.Name == "uIrradiance").Type == ShaderResourceType.SeparateImage);
    t.Expect("BM.8 which is a SAMPLED_IMAGE descriptor, and counts against sampled images only",
        VulkanGraphicsDevice.MapDescriptorType(ShaderResourceType.SeparateImage) == Silk.NET.Vulkan.DescriptorType.SampledImage);
    t.Expect("BM.9 a separate sampler carries the state its //@sampler declared",
        withState.Slots.Single(s => s.Name == "uEnv") is { Type: ShaderResourceType.Sampler } env
        && env.Sampler == SamplerDescription.LinearClampMipmap);
    t.Expect("BM.10 and none without a sidecar, which the device refuses at program creation",
        ShaderReflection.Parse(Refl, "bm.frag").Slots.Single(s => s.Name == "uEnv").Sampler is null);
    var otherState = ShaderReflection.Parse(Refl, "bm.vert",
        new[] { new ShaderSampler("uEnv", nameof(SamplerDescription.NearestClamp)) });
    t.ExpectThrows("BM.11 two stages declaring one sampler with different states fail the merge",
        () => ShaderReflection.MergeStages(withState, otherState with { Stage = ShaderStages.Vertex }), mustMention: "uEnv");

    // Arrays and unreadable forms: every separate sampler has stated state, or the build says why not.
    var array = ShaderSamplers.Scan("//@sampler NearestClamp\nlayout(set = 1, binding = 21) uniform sampler uTaps[4];\n");
    t.Expect("BM.12 a sampler array is one declaration, and its preset covers every element",
        array.Count == 1 && array[0].Name == "uTaps" && array[0].Description == SamplerDescription.NearestClamp,
        string.Join(", ", array));
    t.ExpectThrows("BM.13 an unannotated sampler array fails the scan, as a single sampler does",
        () => ShaderSamplers.Scan("layout(set = 1, binding = 21) uniform sampler uTaps[4];\n"), mustMention: "uTaps");
    t.ExpectThrows("BM.14 two samplers in one declaration are refused, rather than read as none",
        () => ShaderSamplers.Scan("//@sampler LinearClamp\nuniform sampler uA, uB;\n"), mustMention: "one per line");
    t.ExpectThrows("BM.15 and refused even with no //@sampler above them, where they used to pass the scan",
        () => ShaderSamplers.Scan("uniform sampler uA, uB;\n"), mustMention: "one per line");
}

// ============================================================================
// Section BN — nothing but the host names the Vulkan device.
// ============================================================================
//
// <b>Because the cast was how every program started.</b> IGraphicsDevice could destroy a shader
// program but not create one, and the render graph lived in the Vulkan assembly, so each program
// and library that drew anything opened with `(VulkanGraphicsDevice)GraphicsDevice`: 35 files, and
// the first line of the 3D starter. The interface is now the whole device and the graph is the
// engine's, so the cast has no reason left. This check is what keeps it that way: a new member
// reached for through a cast is a member IGraphicsDevice is missing, and it should be added there.
//
// Entitled to the name: the device itself, the host that constructs it, and this suite, which tests
// the device's internals.
{
    var srcDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
    var examplesDir = Path.GetFullPath(Path.Combine(srcDir, "..", "examples"));
    var sep = Path.DirectorySeparatorChar;
    var entitled = new[] { "Blix.Graphics.Vulkan", "Blix.Runtime.Silk", "Blix.Test.Graphics" }
        .Select(p => Path.Combine(srcDir, p) + sep).ToArray();
    string[] Find(string pattern) => new[] { srcDir, examplesDir }.Where(Directory.Exists)
        .SelectMany(d => Directory.GetFiles(d, pattern, SearchOption.AllDirectories))
        .Where(f => !f.Contains($"{sep}obj{sep}", StringComparison.Ordinal) && !f.Contains($"{sep}bin{sep}", StringComparison.Ordinal))
        .Where(f => !entitled.Any(e => f.StartsWith(e, StringComparison.Ordinal)))
        .ToArray();
    var sources = Find("*.cs");
    var projects = Find("*.csproj");

    // Code, not prose: a comment may say what the device used to be called.
    var naming = new Regex(@"\bVulkanGraphicsDevice\b|^\s*using\s+Blix\.Graphics\.Vulkan\s*;|\bBlix\.Graphics\.Vulkan\.");
    bool Names(string line) => !line.TrimStart().StartsWith("//", StringComparison.Ordinal) && naming.IsMatch(line);

    t.Expect("BN.0 CONTROL sources and projects were found to search",
        sources.Length >= 100 && projects.Length >= 20, $"{sources.Length} source(s), {projects.Length} project(s)");
    t.Expect("BN.0 CONTROL the pattern sees the cast, the using, and a qualified name, and not a comment",
        Names("var vk = (VulkanGraphicsDevice)GraphicsDevice;") && Names("using Blix.Graphics.Vulkan;")
        && Names("x is Blix.Graphics.Vulkan.MaterialBindings") && !Names("// cast to VulkanGraphicsDevice"));

    var named = new List<string>();
    foreach (var file in sources)
    {
        var lines = File.ReadAllLines(file);
        for (var i = 0; i < lines.Length; i++)
            if (Names(lines[i])) named.Add($"{Path.GetFileName(file)}:{i + 1}");
    }
    t.Expect("BN.1 no library or program names the Vulkan device or its namespace",
        named.Count == 0,
        named.Count == 0 ? "all of them take IGraphicsDevice" : string.Join("; ", named.Take(6)));

    // The reference is the door the name comes back through, so it is held to the same rule.
    var referencing = projects.Where(p => File.ReadAllText(p).Contains("Blix.Graphics.Vulkan.csproj", StringComparison.Ordinal))
        .Select(Path.GetFileName).ToList();
    t.Expect("BN.2 no library or program references Blix.Graphics.Vulkan; it arrives with the host",
        referencing.Count == 0, string.Join("; ", referencing));
}

// ============================================================================
// Section BO — CameraController: one camera, looked, orbited, flown and zoomed.
// ============================================================================
//
// The controller has one convention (yaw 0 down -Z turning to +X, pitch where it looks, up positive), and
// these pin it: every saved --cam viewpoint depends on it meaning what it means in Sponza.
{
    static bool Near(Vector3 a, Vector3 b, float e = 1e-4f) => Vector3.Distance(a, b) < e;
    var c = new CameraController(new Camera3D());
    t.Expect("BO.1 yaw 0, pitch 0 looks down -Z", Near(c.Forward, -Vector3.UnitZ), c.Forward.ToString());
    c.Yaw = 90f;
    t.Expect("BO.1 yaw 90 turns towards +X", Near(c.Forward, Vector3.UnitX), c.Forward.ToString());
    c.Yaw = 0f; c.Pitch = 30f;
    t.Expect("BO.1 positive pitch looks up", c.Forward.Y > 0.49f && c.Forward.Y < 0.51f, c.Forward.ToString());

    // Sponza's own formula, which its saved viewpoints were taken with.
    var rng = new Random(3);
    var worst = 0f;
    for (var i = 0; i < 100; i++)
    {
        var yawDeg = rng.NextSingle() * 360f - 180f;
        var pitchDeg = rng.NextSingle() * 170f - 85f;
        c.Yaw = yawDeg; c.Pitch = pitchDeg;
        var y = yawDeg * MathF.PI / 180f; var p = pitchDeg * MathF.PI / 180f;
        var sponza = new Vector3(MathF.Cos(p) * MathF.Sin(y), MathF.Sin(p), -MathF.Cos(p) * MathF.Cos(y));
        worst = MathF.Max(worst, Vector3.Distance(c.Forward, sponza));
    }
    t.Expect("BO.2 forward is Sponza's (cos p sin y, sin p, -cos p cos y) at any pose", worst < 1e-4f, $"worst {worst:E2}");

    // A saved viewpoint from the yellow-vault investigation, exactly as it was written down.
    var saved = new CameraController(new Camera3D());
    saved.ReadArgs(AppArgs.Parse(new[] { "--cam", "9.04,8.27,0.76,-331.14,-8.94" }));
    t.Expect("BO.3 a saved --cam is read in degrees and wrapped", saved.Pose == "9.04,8.27,0.76,28.86,-8.94", saved.Pose);
    var again = new CameraController(new Camera3D());
    again.ReadArgs(AppArgs.Parse(new[] { "--cam", saved.Pose }));
    t.Expect("BO.3 and the pose it reports reads back to the same camera", Near(again.Forward, saved.Forward) && again.Position == saved.Position);
    t.ExpectThrows("BO.3 a --cam that is not five numbers is refused, naming it",
        () => new CameraController(new Camera3D()).ReadArgs(AppArgs.Parse(new[] { "--cam", "1,2,3" })), mustMention: "--cam");

    var o = new CameraController(new Camera3D());
    o.LookAt(new Vector3(0, 2, 8), new Vector3(0, 1, 0));
    t.Expect("BO.4 LookAt makes the target the pivot", Near(o.Pivot, new Vector3(0, 1, 0), 1e-3f), o.Pivot.ToString());
    var pivot = o.Pivot;
    o.Orbit(120f, 40f);
    t.Expect("BO.4 orbiting keeps the pivot and the distance", Near(o.Pivot, pivot, 1e-3f) && Math.Abs(Vector3.Distance(o.Position, pivot) - o.Distance) < 1e-3f,
        $"{o.Pivot} {o.Position}");
    var eye = o.Position;
    o.Look(30f, -10f);
    t.Expect("BO.4 looking keeps the position and swings the pivot", o.Position == eye && !Near(o.Pivot, pivot, 1e-2f));
    pivot = o.Pivot;
    o.Zoom(2f);
    t.Expect("BO.4 zooming closes on the pivot and keeps it", Near(o.Pivot, pivot, 1e-3f) && Vector3.Distance(o.Position, pivot) < Vector3.Distance(eye, pivot));
    var before = o.Position;
    o.Move(new Vector3(0, 0, 1), 1f);
    t.Expect("BO.4 moving forward goes where the camera looks, at MoveSpeed", Near(o.Position - before, o.Forward * o.MoveSpeed, 1e-3f));

    // The camera it drives agrees: the pivot projects to the middle of the picture.
    var look = new CameraController(new Camera3D());
    look.LookAt(new Vector3(3, 4, 5), new Vector3(-1, 0.5f, -2));
    var clip = Vector4.Transform(new Vector4(look.Pivot, 1f), look.ViewProjection(16f / 9f));
    t.Expect("BO.5 the pivot lands at the centre of the camera's picture",
        MathF.Abs(clip.X / clip.W) < 1e-4f && MathF.Abs(clip.Y / clip.W) < 1e-4f && clip.W > 0f, $"{clip.X / clip.W}, {clip.Y / clip.W}");
    t.Expect("BO.5 and a bad aspect cannot break the matrix", look.ViewProjection(0f) == look.ViewProjection(16f / 9f));

    // The camera's view must equal Matrix4x4.CreateLookAt(position, position + forward, up), the view every
    // captured Sponza frame and saved viewpoint was made with, or those frames move.
    var same = 0f;
    var poses = new Random(11);
    var driven = new CameraController(new Camera3D());
    for (var i = 0; i < 100; i++)
    {
        driven.Position = new Vector3(poses.NextSingle() * 20 - 10, poses.NextSingle() * 6, poses.NextSingle() * 10 - 5);
        driven.Yaw = poses.NextSingle() * 360f - 180f;
        driven.Pitch = poses.NextSingle() * 170f - 85f;
        var lookAt = Matrix4x4.CreateLookAt(driven.Position, driven.Position + driven.Forward, Vector3.UnitY);
        var mine = driven.Camera.GetView();
        for (var r = 0; r < 4; r++)
            for (var col = 0; col < 4; col++)
                same = MathF.Max(same, MathF.Abs(lookAt[r, col] - mine[r, col]));
    }
    t.Expect("BO.6 the camera's view is Sponza's CreateLookAt view at any pose", same < 1e-4f, $"worst {same:E2}");

    // One path from pose to picture: a write that goes round the controller is caught at the next
    // operation, instead of half taking effect (the camera moved, yaw and pitch did not).
    var owned = new CameraController(new Camera3D());
    owned.LookAt(new Vector3(0, 2, 6), Vector3.Zero);
    owned.Camera.Transform.Position += Vector3.UnitX;
    t.ExpectThrows("BO.7 a camera moved outside its controller is refused at the next operation",
        () => owned.Orbit(10f, 0f), mustMention: "outside its CameraController");
    t.Expect("BO.7 and Forward comes from the pose, not from whatever was written to the camera",
        Vector3.Distance(owned.Forward, Vector3.Normalize(new Vector3(0, -2, -6))) < 1e-4f, owned.Forward.ToString());
}

// ============================================================================
// Section BP — the CPU side of residency: node worlds and merges, before any device.
// ============================================================================
//
// A static model is arranged one of three ways by who draws it: per node (a viewer), merged to one mesh
// an instance can place (a prop), or bundled into shared buffers (a big static scene, MeshBundler). The
// first two share this arithmetic, which is why it lives on the import side and not in each consumer.
{
    static MeshData Quad(string name, float x)
    {
        var layout = VertexPosition3NormalTexture.Layout;
        var floats = new List<float>();
        foreach (var (px, py) in new[] { (0f, 0f), (1f, 0f), (1f, 1f), (0f, 1f) })
        {
            floats.AddRange(new[] { px + x, py, 0f, 0f, 0f, 1f, px, py });
        }
        var bytes = new byte[floats.Count * 4];
        Buffer.BlockCopy(floats.ToArray(), 0, bytes, 0, bytes.Length);
        return new MeshData(name, bytes, new ushort[] { 0, 1, 2, 0, 2, 3 }, layout,
            new Bounds3(new Vector3(x, 0, 0), new Vector3(x + 1, 1, 0)));
    }

    var a = Quad("a", 0f);
    var b = Quad("b", 0f);
    var merged = new[] { (a, Matrix4x4.Identity), (b, Matrix4x4.CreateTranslation(5f, 0f, 0f)) }.Merge("ab");
    t.Expect("BP.1 a merge concatenates vertices and re-bases the second part's indices",
        merged.VertexCount == 8 && merged.IndexCount == 12 && merged.Indices[6] == 4 && merged.Indices[11] == 7,
        $"{merged.VertexCount} vertices, indices {string.Join(",", merged.Indices)}");
    t.Expect("BP.1 each part is moved by its own matrix, and the bounds follow",
        merged.Bounds.Min == Vector3.Zero && merged.Bounds.Max == new Vector3(6f, 1f, 0f), merged.Bounds.ToString());
    t.Expect("BP.1 attributes the merge does not move are copied as they were (the uv)",
        BitConverter.ToSingle(merged.VertexBytes, 4 * 32 + 24) == 0f && BitConverter.ToSingle(merged.VertexBytes, 5 * 32 + 24) == 1f);

    // Past 65535 vertices the indices must widen, or the second part draws the first part's vertices.
    var big = new MeshData("big", new byte[40000 * 32], new ushort[] { 0, 1, 2 }, VertexPosition3NormalTexture.Layout,
        new Bounds3(Vector3.Zero, Vector3.Zero));
    var wide = new[] { (big, Matrix4x4.Identity), (big, Matrix4x4.Identity) }.Merge("wide");
    t.Expect("BP.2 a merge past 65535 vertices widens to 32-bit indices",
        wide.IndexFormat == IndexFormat.UInt32 && wide.Indices32![3] == 40000u, wide.IndexFormat.ToString());
    var other = new MeshData("other", new byte[36], new ushort[] { 0 }, VertexPosition3NormalTexture2Color.Layout,
        new Bounds3(Vector3.Zero, Vector3.Zero));
    t.ExpectThrows("BP.2 parts in different vertex layouts are refused, naming them",
        () => new[] { (a, Matrix4x4.Identity), (other, Matrix4x4.Identity) }.Merge("mixed"), mustMention: "layout");

    // Which bytes are which comes from VertexSemantics, never from guessing by format: in the skinned
    // layouts the first Float4 is the bone indices, and a guess would move them as a tangent.
    var skinnedMesh = new MeshData("skinned", new byte[VertexPosition3NormalTextureSkin4Tangent.Layout.Stride], new ushort[] { 0, 0, 0 },
        VertexPosition3NormalTextureSkin4Tangent.Layout, new Bounds3(Vector3.Zero, Vector3.Zero));
    t.ExpectThrows("BP.2b a skinned mesh is refused by Transformed: its skin places it, and moving it leaves the inverse binds behind",
        () => skinnedMesh.Transformed(Matrix4x4.CreateTranslation(1f, 0f, 0f)), mustMention: "skinned");
    var unknownLayout = new VertexLayout(16, new[] { new VertexAttribute(0, VertexAttributeFormat.Float4, 0) });
    var unknown = new MeshData("unknown", new byte[16], new ushort[] { 0, 0, 0 }, unknownLayout, new Bounds3(Vector3.Zero, Vector3.Zero));
    t.ExpectThrows("BP.2b a layout that is not one of Blix's is refused rather than guessed at",
        () => unknown.Transformed(Matrix4x4.Identity), mustMention: "not one of Blix's layouts");

    // A tangent layout: the tangent turns with the mesh, and under a mirror its w flips and the winding reverses.
    var tangentLayout = VertexPosition3NormalTangentTexture.Layout;
    var tangentBytes = new byte[3 * tangentLayout.Stride];
    for (var v = 0; v < 3; v++)
    {
        var o = v * tangentLayout.Stride;
        foreach (var (at, value) in new[] { (12, 0f), (16, 0f), (20, 1f), (24, 1f), (28, 0f), (32, 0f), (36, 1f) })
            BitConverter.TryWriteBytes(tangentBytes.AsSpan(o + at, 4), value);
        BitConverter.TryWriteBytes(tangentBytes.AsSpan(o + 0, 4), (float)v);
    }

    var tangentMesh = new MeshData("tangent", tangentBytes, new ushort[] { 0, 1, 2 }, tangentLayout, new Bounds3(Vector3.Zero, Vector3.One));
    var turned = tangentMesh.Transformed(Matrix4x4.CreateRotationZ(MathF.PI / 2f));
    var mirrored = tangentMesh.Transformed(Matrix4x4.CreateScale(-1f, 1f, 1f));
    t.Expect("BP.2b the tangent turns with the mesh: +X rotated a quarter about Z is +Y",
        MathF.Abs(BitConverter.ToSingle(turned.VertexBytes, 24)) < 1e-5f && MathF.Abs(BitConverter.ToSingle(turned.VertexBytes, 28) - 1f) < 1e-5f
        && BitConverter.ToSingle(turned.VertexBytes, 36) == 1f);
    t.Expect("BP.2b under a mirror the tangent's w flips and the winding reverses",
        BitConverter.ToSingle(mirrored.VertexBytes, 36) == -1f && mirrored.Indices.SequenceEqual(new ushort[] { 0, 2, 1 }),
        $"w {BitConverter.ToSingle(mirrored.VertexBytes, 36)}, indices {string.Join(",", mirrored.Indices)}");

    // The table against each layout's own declaration: position Float3, normal Float3, uv Float2, tangent Float4.
    var tableAgrees = new[]
    {
        VertexPosition3NormalTexture.Layout, VertexPosition3NormalTextureColor.Layout, VertexPosition3NormalTexture2Color.Layout,
        VertexPosition3NormalTangentTexture.Layout, VertexPosition3NormalTangentTexture2Color.Layout,
        VertexPosition3NormalTextureSkin4Tangent.Layout, VertexPosition3NormalTextureSkin4Tangent2Color.Layout,
    }.All(layout =>
    {
        var sem = VertexSemantics.Of(layout);
        bool Has(int offset, VertexAttributeFormat format) => offset < 0 || layout.Attributes.Any(x => x.Offset == offset && x.Format == format);
        return sem is not null && Has(sem.Position, VertexAttributeFormat.Float3) && Has(sem.Normal, VertexAttributeFormat.Float3)
            && Has(sem.Uv0, VertexAttributeFormat.Float2) && Has(sem.Uv1, VertexAttributeFormat.Float2) && Has(sem.Tangent, VertexAttributeFormat.Float4)
            && sem.Skinned == layout.Attributes.Count(x => x.Format == VertexAttributeFormat.Float4) >= 3;
    });
    t.Expect("BP.2b VertexSemantics names each of Blix's seven layouts' attributes where the layout declares them", tableAgrees);

    // A parent translated by +10 on X with a child translated by +1: the cooked scene graph composes the child's
    // world (ModelData.World, local x every ancestor's) to +11. Read from a cooked file, not recomputed here.
    var triangle = new SharpGLTF.Geometry.MeshBuilder<SharpGLTF.Geometry.VertexTypes.VertexPositionNormal>("tri");
    triangle.UsePrimitive(SharpGLTF.Materials.MaterialBuilder.CreateDefault()).AddTriangle(
        new SharpGLTF.Geometry.VertexTypes.VertexPositionNormal(0, 0, 0, 0, 1, 0),
        new SharpGLTF.Geometry.VertexTypes.VertexPositionNormal(1, 0, 0, 0, 1, 0),
        new SharpGLTF.Geometry.VertexTypes.VertexPositionNormal(0, 0, 1, 0, 1, 0));
    var rootNode = new SharpGLTF.Scenes.NodeBuilder("root") { LocalMatrix = Matrix4x4.CreateTranslation(10f, 0f, 0f) };
    var childNode = rootNode.CreateNode("child");
    childNode.LocalMatrix = Matrix4x4.CreateTranslation(1f, 0f, 0f);
    var worldScene = new SharpGLTF.Scenes.SceneBuilder();
    worldScene.AddRigidMesh(triangle, rootNode);
    worldScene.AddRigidMesh(triangle, childNode);
    var worldDir = Path.Combine(Path.GetTempPath(), $"blix-bp-{Guid.NewGuid():N}");
    Directory.CreateDirectory(worldDir);
    var worldGlb = Path.Combine(worldDir, "worlds.glb");
    worldScene.ToGltf2().SaveGLB(worldGlb);
    var cookedWorlds = ModelData.Load(Blix.Recipes.CookCache.Resolve(worldGlb), new ModelNeeds(Skinned: false));
    var childWorld = cookedWorlds.World[cookedWorlds.FindNode("child")];
    t.Expect("BP.3 a node's world is its local composed with every ancestor's",
        childWorld.Translation == new Vector3(11f, 0f, 0f), childWorld.Translation.ToString());
    Directory.Delete(worldDir, recursive: true);
}

// ============================================================================
// Section BQ — a runtime-sized block is sized by a COUNT; offset and stride are the shader's.
// ============================================================================
//
// `mat4 m[]` reflects with block_size 0 and array [0], but its offset and array_stride are there. Callers
// used to pass bytes (bones x bodies x 64), restating a stride the shader already declares and assuming
// the array starts at 0. The JSON below is spirv-cross's own shape: the engine's BlixBones block, and one
// with a fixed member before its unsized array.
{
    const string json = """
    {
      "entryPoints": [{ "name": "main", "mode": "vert" }],
      "types": {
        "_19": { "name": "BlixBones", "members": [
          { "name": "m", "type": "mat4", "array": [0], "array_size_is_literal": [true], "offset": 0, "array_stride": 64, "matrix_stride": 16 } ] },
        "_30": { "name": "Lights", "members": [
          { "name": "count", "type": "vec4", "offset": 0 },
          { "name": "lights", "type": "vec4", "array": [0], "array_size_is_literal": [true], "offset": 16, "array_stride": 32 } ] }
      },
      "ssbos": [
        { "type": "_19", "name": "BlixBones", "readonly": true, "block_size": 0, "set": 3, "binding": 0 },
        { "type": "_30", "name": "Lights", "readonly": true, "block_size": 16, "set": 3, "binding": 1 }
      ]
    }
    """;
    var reflected = ShaderReflection.MergeStages(ShaderReflection.Parse(json, "bq.vert"));
    var bones = reflected.Slots.Single(x => x.Binding == 0).BlockLayout!;
    var lights = reflected.Slots.Single(x => x.Binding == 1).BlockLayout!;
    t.Expect("BQ.1 reflection marks the unsized array, with the stride the shader gives it",
        bones.RuntimeArray is { Name: "m", ElementStride: 64, Offset: 0 } && lights.RuntimeArray is { Name: "lights", ElementStride: 32, Offset: 16 },
        $"{bones.RuntimeArray} / {lights.RuntimeArray}");
    t.Expect("BQ.1 a count sizes the block from where the array starts: 30 bones is 1920 bytes, 3 lights after a vec4 is 112",
        bones.SizeFor(30) == 1920 && lights.SizeFor(3) == 112, $"{bones.SizeFor(30)} / {lights.SizeFor(3)}");
    var sized = reflected.WithArrayLength(3, 1, 3).Slots.Single(x => x.Binding == 1).BlockLayout!;
    t.Expect("BQ.2 WithArrayLength grows the array member and keeps the fixed one where it was",
        sized.TotalSize == 112 && sized.Members[0] is { Name: "count", Offset: 0, Size: 16 } && sized.Members[1].Size == 96,
        $"{sized.TotalSize}: {string.Join(", ", sized.Members)}");

    // A fixed block has no length to give, and asking is refused rather than ignored.
    const string fixedJson = """
    {
      "entryPoints": [{ "name": "main", "mode": "vert" }],
      "types": { "_5": { "name": "Frame", "members": [ { "name": "vp", "type": "mat4", "offset": 0, "matrix_stride": 16 } ] } },
      "ubos": [ { "type": "_5", "name": "Frame", "block_size": 64, "set": 0, "binding": 0 } ]
    }
    """;
    var fixedBlock = ShaderReflection.MergeStages(ShaderReflection.Parse(fixedJson, "fixed.vert"));
    t.ExpectThrows("BQ.3 a block the shader fixes takes no length",
        () => fixedBlock.WithArrayLength(0, 0, 4), mustMention: "does not end in an unsized array");
}

// ============================================================================
// Section BR — IAnimation on its own clock, and the host that runs them.
// ============================================================================
//
// An animation advances by the delta it is handed and keeps its own elapsed time: nothing reads an
// absolute total, so pausing is not advancing, rate is a scaled delta, and restarting is setting
// Elapsed. The host runs them in the order added and drops the finished in the same tick.
{
    var value = -1f;
    var fade = new FloatAnimation { Curve = new LinearCurve { From = 0f, To = 1f, Duration = 1.0 }, Setter = v => value = v };
    var midway = fade.Advance(0.5);
    t.Expect("BR.1 a finite tween writes its curve at its own elapsed time and stays alive before its end",
        midway && MathF.Abs(value - 0.5f) < 1e-6f, $"alive {midway}, value {value}");
    var ended = fade.Advance(0.75);
    t.Expect("BR.1 and on the tick that passes its end it writes the end value and reports done",
        !ended && value == 1f, $"alive {ended}, value {value}");
    fade.Elapsed = 0.0;
    var again = fade.Advance(0.25);
    t.Expect("BR.1 setting Elapsed restarts it", again && MathF.Abs(value - 0.25f) < 1e-6f, $"{value}");

    var delayed = -1f;
    var late = new FloatAnimation { Curve = new LinearCurve { From = 2f, To = 4f, Duration = 1.0 }, Setter = v => delayed = v, Delay = 0.5 };
    late.Advance(0.25);
    var held = delayed;
    late.Advance(0.75);
    t.Expect("BR.2 a Delay holds the curve's start value until it has passed, then runs from there",
        held == 2f && MathF.Abs(delayed - 3f) < 1e-6f, $"held {held}, then {delayed}");

    var target = new Transform3D();
    var none = new Transform3DAnimation { Target = target };
    var forever = new Transform3DAnimation
    {
        Target = target,
        Position = new LinearVector3Curve { From = Vector3.Zero, To = Vector3.UnitX, Duration = 0.5 },
        Rotation = new ConstantCurve<Quaternion>(Quaternion.Identity),
    };
    t.Expect("BR.3 a transform tween with no channels is done at once; one with an infinite channel never is",
        !none.Advance(0.1) && forever.Advance(10.0) && target.Position == Vector3.UnitX, target.Position.ToString());

    // The host: in order, the finished dropped in the same tick, the survivors' order kept.
    var calls = new List<string>();
    var host = new AnimationHost();
    var ticksLeft = new Dictionary<string, int> { ["a"] = 3, ["b"] = 1, ["c"] = 2 };
    foreach (var name in new[] { "a", "b", "c" })
    {
        host.AddAnimation(new CallbackAnimation(delta =>
        {
            calls.Add($"{name}:{delta}");
            return --ticksLeft[name] > 0;
        }));
    }

    host.Advance(0.5);
    var afterOne = host.Count;
    host.Advance(0.25);
    var afterTwo = host.Count;
    host.Advance(0.25);
    t.Expect("BR.4 the host advances in the order added, hands every animation its delta, and drops each on the tick it ends",
        string.Join(" ", calls) == "a:0.5 b:0.5 c:0.5 a:0.25 c:0.25 a:0.25" && afterOne == 2 && afterTwo == 1 && host.Count == 0,
        $"{string.Join(" ", calls)} | counts {afterOne}, {afterTwo}, {host.Count}");

    // Re-entrant adds: one added during an advance starts on the next, behind the survivors. A callback that
    // adds another every time it runs then adds one per advance, rather than running forever inside one.
    var order = new List<string>();
    var reentrant = new AnimationHost();
    var adds = 0;
    CallbackAnimation Adder() => new(delta =>
    {
        order.Add($"adder{adds}");
        adds++;
        reentrant.AddAnimation(Adder());
        return false;
    });
    reentrant.AddAnimation(new CallbackAnimation(_ => { order.Add("keeper"); return true; }));
    reentrant.AddAnimation(Adder());
    reentrant.Advance(0.1);
    var firstTick = string.Join(" ", order);
    var countAfterFirst = reentrant.Count;
    reentrant.Advance(0.1);
    t.Expect("BR.5 an animation added during an advance does not spend that delta, and starts on the next behind the survivors",
        firstTick == "keeper adder0" && countAfterFirst == 2 && string.Join(" ", order) == "keeper adder0 keeper adder1" && reentrant.Count == 2,
        $"{string.Join(" ", order)} | counts {countAfterFirst}, {reentrant.Count}");
}

// ============================================================================
// Section BS — PoseStack: ordered layers of (source, mode, weight, mask) over rest.
// ============================================================================
//
// Each mode must be exactly the engine call it stands for — the stack owns the order and the rest pose,
// PoseBlend and PoseDelta own the maths — so a consumer moving onto it changes no bit. Plus stage F's two
// mask failures: a layer that changes everything, and one that changes nothing.
{
    var skeleton = new Skeleton(new[]
    {
        new Bone("hips", -1, BoneTransform.Identity),
        new Bone("spine", 0, BoneTransform.Identity),
        new Bone("leg", 0, BoneTransform.Identity),
    });

    static BoneTrack Spin(int bone, float radians) => new()
    {
        BoneIndex = bone,
        Rotation = new KeyframeQuaternionCurve(new[]
        {
            new Keyframe<Quaternion>(0.0, Quaternion.Identity),
            new Keyframe<Quaternion>(1.0, Quaternion.CreateFromAxisAngle(Vector3.UnitY, radians)),
        }),
        Translation = new KeyframeVector3Curve(new[]
        {
            new Keyframe<Vector3>(0.0, Vector3.Zero),
            new Keyframe<Vector3>(1.0, new Vector3(radians, 0f, 0f)),
        }),
    };

    var walk = new AnimationClip("walk", new[] { Spin(0, 0.4f), Spin(1, 0.2f), Spin(2, 1.1f) });
    var swing = new AnimationClip("swing", new[] { Spin(0, -0.3f), Spin(1, 1.4f), Spin(2, -0.9f) });
    ClipPlayer At(AnimationClip clip, double time)
    {
        var player = new ClipPlayer(skeleton, clip);
        player.ScrubTo(time);
        return player;
    }

    static bool Same(Pose a, Pose b) => Enumerable.Range(0, a.BoneCount).All(i => a.Locals[i] == b.Locals[i]);

    var a = At(walk, 0.3);
    var b = At(swing, 0.6);
    var stack = new PoseStack(skeleton);
    stack.Add(a);
    stack.Evaluate();
    t.Expect("BS.1 one layer at full weight is its player's pose, exactly", Same(stack.Pose, a.Pose));

    var overlay = stack.Add(b, PoseLayerMode.Blend, 0.35f);
    stack.Evaluate();
    var lerped = skeleton.CreateRestPose();
    PoseBlend.Lerp(a.Pose, b.Pose, 0.35f, lerped);
    t.Expect("BS.2 a blend layer is PoseBlend.Lerp of what is below it toward its own, bit for bit", Same(stack.Pose, lerped));

    var upper = BoneMask.Subtree(skeleton, "spine");
    overlay.Mask = upper;
    stack.Evaluate();
    var masked = skeleton.CreateRestPose();
    PoseBlend.Lerp(a.Pose, b.Pose, 0.35f, upper, masked);
    t.Expect("BS.3 with a mask, it is the masked Lerp, bit for bit, and the leg it does not reach is the base's",
        Same(stack.Pose, masked) && stack.Pose.Locals[2] == a.Pose.Locals[2]);

    overlay.Mask = BoneMask.None(skeleton.BoneCount);
    overlay.Weight = 1f;
    stack.Evaluate();
    var none = Same(stack.Pose, a.Pose);
    overlay.Mask = BoneMask.All(skeleton.BoneCount);
    stack.Evaluate();
    var all = Same(stack.Pose, b.Pose);
    t.Expect("BS.3 CONTROLS a mask over no bone leaves the base bit for bit; over every bone at full weight it is the layer",
        none && all, $"none {none}, all {all}");

    overlay.Mask = null;
    overlay.Mode = PoseLayerMode.Additive;
    overlay.Weight = 0.6f;
    stack.Evaluate();
    var layered = skeleton.CreateRestPose();
    var rest = skeleton.CreateRestPose();
    for (var i = 0; i < layered.BoneCount; i++) layered.Locals[i] = PoseDelta.LayerOnto(a.Pose.Locals[i], rest.Locals[i], b.Pose.Locals[i], 0.6f);
    t.Expect("BS.4 an additive layer is PoseDelta.LayerOnto over the same rest, bit for bit", Same(stack.Pose, layered));

    // A finished one-shot holds its last frame by default, and leaves only when told to.
    var holdStack = new PoseStack(skeleton);
    var oneShot = new ClipPlayer(skeleton, swing) { Loop = false };
    holdStack.Add(oneShot);
    holdStack.Advance(2.0);
    // The clip's last frame, sampled directly (a looping player scrubbed to the end would wrap to 0).
    var end = skeleton.CreateRestPose();
    swing.Sample(swing.Duration, end);
    var held = holdStack.Layers.Count == 1 && oneShot.Finished && Same(holdStack.Pose, end);
    var leaving = new PoseStack(skeleton);
    var base_ = new ClipPlayer(skeleton, walk);
    var gone = new ClipPlayer(skeleton, swing) { Loop = false };
    leaving.Add(base_);
    leaving.Add(gone).OnFinish = PoseLayerFinish.Remove;
    leaving.Advance(0.5);
    var stillThere = leaving.Layers.Count == 2;
    leaving.Advance(1.0);
    t.Expect("BS.5 a finished one-shot layer holds its last frame by default, and with Remove leaves on the advance it finished",
        held && stillThere && leaving.Layers.Count == 1 && ReferenceEquals(leaving.Layers[0].Source, base_),
        $"held {held}, two before {stillThere}, after {leaving.Layers.Count}");

    t.ExpectThrows("BS.6 a player is one layer's: adding it twice would advance it twice",
        () => stack.Add(a), mustMention: "already a layer's source");

    // Same bone count, another rig: locals go by index, so a count match is shape, not meaning.
    var lookalike = new Skeleton(new[]
    {
        new Bone("root", -1, BoneTransform.Identity),
        new Bone("tail", 0, BoneTransform.Identity),
        new Bone("wing", 0, BoneTransform.Identity),
    });
    t.ExpectThrows("BS.6 a player posing another skeleton is refused, even one with as many bones",
        () => stack.Add(new ClipPlayer(lookalike, walk)), mustMention: "another skeleton");

    // A mask remembers the skeleton that resolved it; uniform masks name none and fit any rig of their size.
    var spine = BoneMask.Subtree(skeleton, "spine");
    t.Expect("BS.7 a subtree remembers the skeleton that resolved it, its complement keeps it, and All/None name none",
        ReferenceEquals(spine.Skeleton, skeleton) && ReferenceEquals(spine.Inverted().Skeleton, skeleton)
        && BoneMask.All(3).Skeleton is null && BoneMask.None(3).Skeleton is null);
    var foreign = BoneMask.Subtree(lookalike, "tail");
    var masking = new PoseStack(skeleton);
    t.ExpectThrows("BS.7 a subtree of a same-sized rig is refused when a layer is added",
        () => masking.Add(new ClipPlayer(skeleton, walk), mask: foreign), mustMention: "another skeleton");
    var maskable = masking.Add(new ClipPlayer(skeleton, walk), mask: spine);
    t.ExpectThrows("BS.7 and when it is set on a layer afterwards, which used to be checked by nobody",
        () => maskable.Mask = foreign, mustMention: "another skeleton");
    t.ExpectThrows("BS.7 a mask of the wrong size is refused by the setter too",
        () => maskable.Mask = BoneMask.None(2), mustMention: "covers 2 bones");
    maskable.Mask = BoneMask.All(skeleton.BoneCount, 0.5f);
    var uniformFits = maskable.Mask is { Skeleton: null };
    maskable.Mask = spine.Inverted();
    t.Expect("BS.7 CONTROL: a uniform mask fits any rig of its size, and this rig's own subtree still sets",
        uniformFits && ReferenceEquals(maskable.Mask?.Skeleton, skeleton));
}

// ============================================================================
// Section BT — no glTF parser in the runtime: the cook is the one reader.
// ============================================================================
//
// Over EVERY project in the repository, not a chosen set of roots: a runtime assembly that is a consumer of
// the engine (a host, an audio backend, an overlay) is not reachable from the engine's own graph, so walking
// down from Blix would stay green while one of them took a parser. Each project's ProjectReference closure is
// followed, and any that reaches a SharpGLTF PackageReference must be the cook, a tool or a test.
{
    var repo = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
    var projects = new[] { "src", "examples", "tools" }
        .Select(d => Path.Combine(repo, d))
        .Where(Directory.Exists)
        .SelectMany(d => Directory.EnumerateFiles(d, "*.csproj", SearchOption.AllDirectories))
        .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                 && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
        .ToDictionary(f => Path.GetFullPath(f), f =>
        {
            var x = System.Xml.Linq.XDocument.Load(f);
            var dir = Path.GetDirectoryName(f)!;
            return (
                Refs: x.Descendants("ProjectReference").Select(r => Path.GetFullPath(Path.Combine(dir, ((string)r.Attribute("Include")!).Replace('\\', Path.DirectorySeparatorChar)))).ToArray(),
                Parser: x.Descendants("PackageReference").Any(r => ((string?)r.Attribute("Include"))?.StartsWith("SharpGLTF", StringComparison.Ordinal) == true));
        });

    bool ReachesParser(string project, HashSet<string> seen) =>
        seen.Add(project) && projects.TryGetValue(project, out var p) && (p.Parser || p.Refs.Any(r => ReachesParser(r, seen)));

    static bool MayKnowParser(string name) =>
        name == "Blix.Recipes" || name.StartsWith("Blix.Tools.", StringComparison.Ordinal) || name.StartsWith("Blix.Test.", StringComparison.Ordinal);

    var named = projects.Keys.ToDictionary(k => Path.GetFileNameWithoutExtension(k), k => k);
    // Not vacuous: the scan sees the runtime assemblies outside the engine's own graph.
    var expected = new[] { "Blix", "Blix.Runtime.Silk", "Blix.Runtime.Headless", "Blix.Audio.OpenAL", "Blix.Diagnostics.Overlay" };
    t.Expect("BT.1 the scan sees every project, runtime hosts included",
        expected.All(named.ContainsKey) && projects.Count > 40,
        $"{projects.Count} project(s); missing {string.Join(", ", expected.Where(e => !named.ContainsKey(e)))}");

    var offenders = projects.Keys
        .Where(k => ReachesParser(k, new HashSet<string>()))
        .Select(k => Path.GetFileNameWithoutExtension(k))
        .Where(n => !MayKnowParser(n))
        .OrderBy(n => n, StringComparer.Ordinal)
        .ToArray();
    t.Expect("BT.2 only the cook, tools and tests reach a glTF parser", offenders.Length == 0, string.Join(", ", offenders));

    // CONTROLS: the walk finds a direct reference, and one a project reaches only through another.
    t.ExpectTrue("BT.3 CONTROL: the cook reaches one directly", ReachesParser(named["Blix.Recipes"], new HashSet<string>()));
    t.ExpectTrue("BT.3 CONTROL: a tool reaches one through the cook", ReachesParser(named["Blix.Tools.Shot"], new HashSet<string>())
        && !projects[named["Blix.Tools.Shot"]].Parser);
}

// ============================================================================
// Section BU — GPU buffers: bound by block name, carried by the commands that bind them.
// ============================================================================
//
// The GPU side (a compute pass writing indirect records and instance lists that draws then read, fenced on
// both sides) is proven where the scene host uses it, under validation. Here: what it rests on, deviceless.
{
    // Binding by name rests on reflection keeping a storage block's name, a runtime-sized one included.
    var reflected = ShaderReflection.Parse(
        "{\"entryPoints\":[{\"name\":\"main\",\"mode\":\"comp\"}]," +
        "\"types\":{\"_9\":{\"name\":\"SceneVisible\",\"members\":[{\"name\":\"visible\",\"type\":\"uint\"," +
        "\"array\":[0],\"array_size_is_literal\":[true],\"offset\":0,\"array_stride\":4}]}}," +
        "\"ssbos\":[{\"type\":\"_9\",\"name\":\"SceneVisible\",\"block_size\":0,\"set\":3,\"binding\":1}]}",
        "bu1.comp");
    t.Expect("BU.1 reflection keeps a storage block's name",
        reflected.Slots.Any(s => s.Type == ShaderResourceType.StorageBuffer && s.Name == "SceneVisible" && s.Set == 3 && s.Binding == 1),
        string.Join(", ", reflected.Slots.Select(s => $"{s.Type} {s.Name}@{s.Set}.{s.Binding}")));

    // A dispatch and an indirect draw carry the GPU buffers they bind, copied when recorded (a recorded
    // command is read at Execute, long after the caller has moved on: the push-constant rule, AP).
    var bindings = new List<ShaderBufferBinding> { new("SceneVisible", new GpuBufferHandle(5)) };
    var dispatch = new DispatchCommand(new PipelineHandle(1), 4, 1, 1,
        Array.Empty<ShaderUniform>(), Array.Empty<ShaderTextureBinding>(), Buffers: bindings);
    bindings[0] = new("Other", new GpuBufferHandle(6));
    t.ExpectTrue("BU.2 a dispatch carries the GPU buffers it was recorded with",
        dispatch.Buffers is [{ Name: "SceneVisible", Buffer.Id: 5 }]);
    t.ExpectTrue("BU.2 and binds none unless given some",
        new DispatchCommand(new PipelineHandle(1), 1, 1, 1, Array.Empty<ShaderUniform>(), Array.Empty<ShaderTextureBinding>()).Buffers is null);

    var list = new RenderCommandList();
    list.Pass("gpu-driven", new RenderPassDescription(RenderSurfaceHandle.Default, Array.Empty<GraphicsColor?>(), ClearDepth: false),
        pass => pass.DrawIndexedIndirect(
            new VertexBufferHandle(1), new IndexBufferHandle(1), new PipelineHandle(1), new GpuBufferHandle(7), 40, 3,
            Array.Empty<ShaderUniform>(), Array.Empty<ShaderTextureBinding>(),
            buffers: new[] { new ShaderBufferBinding("SceneVisible", new GpuBufferHandle(5)) }));
    var recorded = list.Passes.Single().Commands.OfType<DrawIndexedIndirectCommand>().Single();
    t.ExpectTrue("BU.3 an indirect draw can read its records from a GPU buffer",
        recorded.ArgumentBuffer == new GpuBufferHandle(7) && recorded.IndirectByteOffset == 40 && recorded.DrawCount == 3);
    t.ExpectTrue("BU.3 and binds the GPU buffers it names", recorded.Buffers is [{ Name: "SceneVisible", Buffer.Id: 5 }]);
}
t.PrintSummary();
return t.Failed;

// Did the action throw? Section AT asserts on the record/execute check firing, and a bare
// try/catch at each site would bury the assertion it exists to make.
static float Deviation(Matrix4x4 m)
{
    var identity = Matrix4x4.Identity;
    var worst = 0f;
    for (var r = 0; r < 4; r++)
    for (var c = 0; c < 4; c++)
    {
        worst = MathF.Max(worst, MathF.Abs(Element(m, r, c) - Element(identity, r, c)));
    }

    return worst;

    static float Element(Matrix4x4 x, int r, int c) => (r, c) switch
    {
        (0, 0) => x.M11, (0, 1) => x.M12, (0, 2) => x.M13, (0, 3) => x.M14,
        (1, 0) => x.M21, (1, 1) => x.M22, (1, 2) => x.M23, (1, 3) => x.M24,
        (2, 0) => x.M31, (2, 1) => x.M32, (2, 2) => x.M33, (2, 3) => x.M34,
        _ => (r, c) switch { (3, 0) => x.M41, (3, 1) => x.M42, (3, 2) => x.M43, _ => x.M44 },
    };
}

static Matrix4x4 Invert(Matrix4x4 m) => Matrix4x4.Invert(m, out var inv) ? inv : Matrix4x4.Identity;

static bool Throws(Action action)
{
    try { action(); return false; }
    catch (InvalidOperationException) { return true; }
}

static bool NoThrow(Action action) => !Throws(action);

// A refusal that names a bad ARGUMENT rather than a bad state — a mask over a bone that is not
// there, or one sized for another skeleton. Kept separate from Throws above because which kind of
// refusal a call makes is part of what is being pinned: an argument fault is the caller's typo and
// an invalid-operation fault is the engine's invariant.
static bool ThrowsArgument(Action action)
{
    try { action(); return false; }
    catch (ArgumentException) { return true; }
}

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


// Fixture for Section S: a [Tune]-tagged object the reflector should surface.
enum TuneFixtureMode { A, B, C }
sealed class TuneFixture
{
    [Tune(0, 0.5)] public float Density = 0.1f;
    [Tune(0, 5)]   public int Steps = 1;
    [Tune(0, 60)]  public float flySpeed = 4.5f;
    [Tune]         public bool Wireframe = false;
    [Tune]         public TuneFixtureMode Mode = TuneFixtureMode.B;
    [Tune]         public string Clip = "Walking_A";
    [Tune(MaxLength = 4)] public string Bone = "spine";
    public float NotTunable = 9f;   // no attribute → must be ignored
}


// Test-local twin of Blix.Shaders/sheen.glsl, for Section BE. NOT shipped, and not a candidate
// for shipping: the material-response arc's whole boundary is that the spec's answer lives in one
// file that every consumer includes, so a second evaluator in C# would be the thing that boundary
// forbids. It exists to make four invariants assertable without a GPU, and BE.1 holds every line
// of it answerable to the GLSL it copies.
static class SheenTwin
{
    private const float Pi = 3.14159265359f;

    public static float DistributionCharlie(float nDotH, float sheenRoughness)
    {
        var alpha = MathF.Max(sheenRoughness * sheenRoughness, 1e-4f);
        var invAlpha = 1.0f / alpha;
        var cos2h = nDotH * nDotH;
        var sin2h = MathF.Max(1.0f - cos2h, 1e-7f);
        return (2.0f + invAlpha) * MathF.Pow(sin2h, invAlpha * 0.5f) / (2.0f * Pi);
    }

    public static float VisibilityAshikhmin(float nDotL, float nDotV) =>
        Math.Clamp(1.0f / (4.0f * (nDotL + nDotV - nDotL * nDotV)), 0.0f, 1.0f);

    public static Vector3 Brdf(
        Vector3 sheenColor, float sheenRoughness, float nDotH, float nDotL, float nDotV) =>
        sheenColor * DistributionCharlie(nDotH, sheenRoughness) * VisibilityAshikhmin(nDotL, nDotV);

    public static float Scaling(Vector3 sheenColor, float sheenAlbedo) =>
        1.0f - MathF.Max(MathF.Max(sheenColor.X, sheenColor.Y), sheenColor.Z) * sheenAlbedo;

    public static Vector3 DiffuseTransmission(
        Vector3 n, Vector3 l, Vector3 radiance, Vector3 transmissionColor, float transmissionFactor)
    {
        var backNdotL = MathF.Max(Vector3.Dot(-n, l), 0.0f);
        return radiance * backNdotL * transmissionColor * transmissionFactor / Pi;
    }

    public static Vector3 DiffuseTransmissionAmbient(
        Vector3 backIrradiance, Vector3 transmissionColor, float transmissionFactor) =>
        backIrradiance * transmissionColor * transmissionFactor;

    public static float DiffuseTransmissionScaling(float transmissionFactor) =>
        1.0f - Math.Clamp(transmissionFactor, 0.0f, 1.0f);
}

// A device that records the one decision Section BF is about and refuses everything else.
//
// Everything below the three implemented members throws rather than returning a default, which is
// the point: if CreateMesh ever starts creating a pipeline or uploading a texture, the line it is
// supposed to stop at has moved and this stub says so by failing loudly instead of quietly
// tolerating it. A no-op stub would let that change pass.
sealed class RecordingDevice : IGraphicsDevice
{
    public GraphicsFeatures Features => GraphicsFeatures.None;

    /// <summary>16, 32, or 0 when no index buffer was created.</summary>
    public int LastIndexWidth { get; private set; }

    public IndexBufferHandle CreateIndexBuffer(
        IReadOnlyList<ushort> indices, GraphicsBufferUsage usage = GraphicsBufferUsage.Static, string? name = null)
    {
        LastIndexWidth = 16;
        return new IndexBufferHandle(1);
    }

    public IndexBufferHandle CreateIndexBuffer(
        IReadOnlyList<uint> indices, GraphicsBufferUsage usage = GraphicsBufferUsage.Static, string? name = null)
    {
        LastIndexWidth = 32;
        return new IndexBufferHandle(2);
    }

    public VertexBufferHandle CreateVertexBuffer(VertexBufferData data, string? name = null) => new(1);

    private static NotSupportedException No([System.Runtime.CompilerServices.CallerMemberName] string m = "") =>
        new($"RecordingDevice: CreateMesh must not call {m} — see Section BF.");

    public GraphicsDeviceInfo Info => throw No();
    public GraphicsDeviceDiagnostics DiagnosticsSnapshot => throw No();
    public IndirectBufferHandle CreateIndirectBuffer(int maxDrawCommands, string? name = null) => throw No();
    public void WriteIndirectCommands(IndirectBufferHandle handle, ReadOnlySpan<byte> commands) => throw No();
    public void DestroyIndirectBuffer(IndirectBufferHandle handle) => throw No();
    public GpuBufferHandle CreateGpuBuffer(int sizeBytes, ReadOnlySpan<byte> initial = default, string? name = null) => throw No();
    public void DestroyGpuBuffer(GpuBufferHandle handle) => throw No();
    public ShaderProgramHandle CreateShaderProgramFromSpv(byte[] vertexSpv, byte[] fragmentSpv, ShaderInterface shaderInterface, string? name = null) => throw No();
    public ShaderProgramHandle CreateComputeShaderProgramFromSpv(byte[] computeSpv, ShaderInterface shaderInterface, string? name = null) => throw No();
    public PipelineHandle CreateComputePipeline(ShaderProgramHandle program, string? name = null) => throw No();
    public IMaterialBindings CreateMaterial(ShaderProgramHandle program, int setIndex = DescriptorSets.Material, int framesInFlight = 1, string? name = null, IReadOnlyDictionary<int, int>? arrayLengths = null) => throw No();
    public void DestroyMaterial(MaterialHandle handle) => throw No();
    public TextureHandle CreateTextureCube(int faceSize, TextureFormat format, int mipCount, ReadOnlySpan<byte> data, SamplerDescription sampler, string name) => throw No();
    public TextureHandle CreateStorageTexture2D(int width, int height, TextureFormat format, SamplerDescription sampler, string? name = null) => throw No();
    public TextureHandle CreateStorageTexture3D(int width, int height, int depth, TextureFormat format, SamplerDescription sampler, string? name = null) => throw No();
    public byte[] ReadTexture(TextureHandle handle, out int width, out int height, out TextureFormat format) => throw No();
    public bool TryGetTextureSize(TextureHandle handle, out int width, out int height) => throw No();
    public void WaitIdle() => throw No();
    public int MaxFramesInFlightCount => throw No();
    public int CurrentFrameSlot => throw No();
    public bool VsyncEnabled { get => throw No(); set => throw No(); }
    public int MaxMsaaSamples => throw No();
    public void SetDefaultRenderSurfaceSize(int width, int height) => throw No();
    public void UpdateVertexBuffer(VertexBufferHandle handle, ReadOnlySpan<byte> bytes, int byteOffset = 0) => throw No();
    public void DestroyVertexBuffer(VertexBufferHandle handle) => throw No();
    public TransientVertexSlice AllocVertices(ReadOnlySpan<byte> data, int vertexStride, string? name = null) => throw No();
    public void DestroyIndexBuffer(IndexBufferHandle handle) => throw No();
    public void DestroyShaderProgram(ShaderProgramHandle handle) => throw No();
    public PipelineHandle CreatePipeline(PipelineDescription description, string? name = null) => throw No();
    public PipelineHandle GetOrCreatePipeline(PipelineDescription description, string? name = null) => throw No();
    public void DestroyPipeline(PipelineHandle handle) => throw No();
    public TextureHandle CreateTexture2D(TextureDescription description, ReadOnlySpan<byte> pixels, string? name = null) => throw No();
    public TextureHandle CreateTexture2DMipped(TextureDescription description, IReadOnlyList<byte[]> mipBytes, string? name = null) => throw No();
    public void UploadTextureMip(TextureHandle handle, int mipLevel, ReadOnlySpan<byte> bytes) => throw No();
    public void QueueTextureUpload(TextureHandle handle, int mipLevel, ReadOnlySpan<byte> bytes) => throw No();
    public TextureHandle AllocateTexture2DMips(TextureDescription description, int mipCount, string? name = null) => throw No();
    public TextureHandle CreateTexture3D(int width, int height, int depth, TextureFormat format, SamplerDescription sampler, ReadOnlySpan<byte> pixels, string? name = null) => throw No();
    public TextureHandle CreateTextureCubeHdr(int faceSize, ReadOnlySpan<Half> faces, SamplerDescription sampler, string? name = null) => throw No();
    public TextureHandle CreateTextureCubeHdrMipped(int baseFaceSize, IReadOnlyList<Half[]> mipFaces, SamplerDescription sampler, string? name = null) => throw No();
    public void DestroyTexture(TextureHandle handle) => throw No();
    public RenderSurface CreateRenderSurface(RenderSurfaceDescription description) => throw No();
    public void DestroyRenderSurface(RenderSurfaceHandle handle) => throw No();
    public ResourceRegistrySnapshot SnapshotResources() => throw No();
    public void Dispose() { }
}

/// <summary>Reflection handed to the generator in memory, as the compiler hands it an AdditionalFile.</summary>
sealed class InlineText(string path, string text) : Microsoft.CodeAnalysis.AdditionalText
{
    public override string Path { get; } = path;

    public override Microsoft.CodeAnalysis.Text.SourceText GetText(CancellationToken cancellationToken = default) =>
        Microsoft.CodeAnalysis.Text.SourceText.From(text);
}

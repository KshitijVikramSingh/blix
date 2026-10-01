namespace Blix.Graphics;

// Per-program declaration of "which uniform name lives at which byte offset
// in a UBO." Bridges the name-keyed cross-backend ShaderUniform API to
// Vulkan's offset-into-packed-UBO model.
//
// All sizes/offsets are bytes, computed against GLSL std140:
//   float / int / bool: align 4,  size 4
//   vec2:               align 8,  size 8
//   vec3 / vec4:        align 16, size 12 / 16
//   mat4:               align 16, size 64
//   arrays:             each element aligned to 16
public sealed record UniformBlockLayout(int TotalSize, IReadOnlyList<UniformBlockMember> Members)
{
    /// <summary>The block's runtime-sized array (its last member, declared <c>T m[]</c>), or null for a fixed block.</summary>
    public UniformBlockMember? RuntimeArray => Members.Count > 0 && Members[^1].IsRuntimeSized ? Members[^1] : null;

    /// <summary>Bytes the block takes holding <paramref name="elements"/> of its runtime-sized array.</summary>
    /// <remarks>
    /// Everything but the count is the shader's: where the array starts (after any fixed members) and its
    /// element stride both come from reflection, so a caller never multiplies by a stride it restated.
    /// </remarks>
    public int SizeFor(int elements)
    {
        var array = RuntimeArray ?? throw new InvalidOperationException(
            "this block is fixed by its shader; only one ending in an unsized array is sized by a count.");
        ArgumentOutOfRangeException.ThrowIfLessThan(elements, 1);
        return checked(array.Offset + (elements * array.ElementStride));
    }
}

// ElementStride is the std140 array stride (16 for scalar/vec arrays, 64
// for mat4 arrays). 0 = scalar member; write path ignores it. IsRuntimeSized marks
// an unsized array (`T m[]`), whose Size is 0 until a count is given.
public sealed record UniformBlockMember(string Name, int Offset, int Size, int ElementStride = 0, bool IsRuntimeSized = false);

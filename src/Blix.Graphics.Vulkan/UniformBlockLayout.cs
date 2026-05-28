namespace Blix.Graphics.Vulkan;

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
public sealed record UniformBlockLayout(int TotalSize, IReadOnlyList<UniformBlockMember> Members);

// ElementStride is the std140 array stride (16 for scalar/vec arrays, 64
// for mat4 arrays). 0 = scalar member; write path ignores it.
public sealed record UniformBlockMember(string Name, int Offset, int Size, int ElementStride = 0);

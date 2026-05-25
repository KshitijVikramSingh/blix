namespace Blix.Graphics.Vulkan;

// Vulkan-only shape: a per-shader-program declaration of "which uniform
// names live at which byte offsets inside the single bound UBO." Routed
// through CreateShaderProgramFromSpv at program creation time; consulted
// in the draw path to translate name-keyed ShaderUniform writes into UBO
// offset writes.
//
// This exists because the cross-backend ShaderUniform API is name-keyed
// (a GL-style legacy of glGetUniformLocation), but Vulkan binds via
// explicit (set, binding) and reads from offsets in a packed UBO. The
// mapping has to live somewhere — for the validation push it's an
// explicit declaration the demo passes in, deliberately exposing the
// mismatch (see docs/vulkan-friction.md F-002).
//
// Sizes / offsets are in BYTES. The shader's GLSL uniform block is
// expected to use std140 layout (the GLSL default for uniform blocks);
// callers compute offsets that match GLSL's std140 rules:
//   float / int / bool:   align 4,  size 4
//   vec2:                 align 8,  size 8
//   vec3 / vec4:          align 16, size 12 / 16
//   mat4 (column-major):  align 16, size 64
//   arrays:               each element aligned to 16
public sealed record UniformBlockLayout(int TotalSize, IReadOnlyList<UniformBlockMember> Members);

public sealed record UniformBlockMember(string Name, int Offset, int Size);

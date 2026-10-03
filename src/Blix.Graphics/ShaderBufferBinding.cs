namespace Blix.Graphics;

/// <summary>A GPU buffer bound to a shader's storage block, by the block's name.</summary>
/// <remarks>
/// The storage-buffer counterpart of <see cref="ShaderTextureBinding"/>: a dispatch or an indirect draw lists
/// these, and each backs the block of that name in whatever set the program declares it. A runtime-sized block
/// (one ending in an unsized array) can be backed this way, which is what lets a compute pass write a list a
/// later draw reads. The name is the block's (<c>buffer SceneVisible { uint visible[]; }</c> is "SceneVisible").
/// </remarks>
public sealed record ShaderBufferBinding(string Name, GpuBufferHandle Buffer);

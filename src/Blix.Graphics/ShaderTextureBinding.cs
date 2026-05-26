namespace Blix.Graphics;

// One texture bound at a (Slot, ArrayIndex) within a shader's descriptor set.
// Slot is the binding number; ArrayIndex selects the element for array
// bindings (sampler2D uMaps[N] / samplerCube uCubes[N]) and is 0 for the
// common single-texture case. The backend resolves Slot → (set, binding)
// and writes the descriptor at the given array element.
public sealed record ShaderTextureBinding(string Name, TextureHandle Texture, int Slot, int ArrayIndex = 0);

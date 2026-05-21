namespace Blix;

// Decoded glTF texture image — RGBA8 byte array + dimensions, ready to upload via
// IGraphicsDevice.CreateTexture. Cached and shared by the importer when multiple
// materials reference the same image (deduping by glTF image index).
//
// Format is fixed to RGBA8 because that's what the stb-based decoder we route
// through (Blix.Graphics.Images.ImageLoader) emits, and what every downstream
// material consumer expects. KTX2 / Basis Universal compressed-texture support
// would land alongside its own dedicated importer path.
public sealed record GltfTexture(
    string Name,
    byte[] RgbaPixels,
    int Width,
    int Height);

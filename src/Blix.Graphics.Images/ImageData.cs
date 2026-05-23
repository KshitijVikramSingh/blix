namespace Blix.Graphics.Images;

public sealed record ImageData(
    int Width,
    int Height,
    Blix.Graphics.TextureFormat Format,
    byte[] Pixels);

// Floating-point image. Pixels is RGBA stored as 4*Width*Height floats laid
// out [r0,g0,b0,a0, r1,g1,b1,a1, ...]. Used for HDR sources (Radiance .hdr
// and similar) whose dynamic range would clip in Rgba8.
public sealed record HdrImageData(
    int Width,
    int Height,
    float[] Pixels);

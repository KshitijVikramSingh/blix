namespace Blix.Graphics.Images;

public sealed record ImageData(
    int Width,
    int Height,
    Blix.Graphics.TextureFormat Format,
    byte[] Pixels);

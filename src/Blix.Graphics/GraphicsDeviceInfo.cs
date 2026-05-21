namespace Blix.Graphics;

public sealed record GraphicsDeviceInfo(
    string Vendor,
    string Renderer,
    string Version,
    string ShadingLanguageVersion);

namespace Blix.Graphics.Images;

public static class GraphicsDeviceImageExtensions
{
    public static Blix.Graphics.TextureHandle CreateTexture2D(
        this Blix.Graphics.IGraphicsDevice device,
        ImageData image,
        Blix.Graphics.SamplerDescription sampler,
        string? name = null)
    {
        var description = new Blix.Graphics.TextureDescription(
            image.Width,
            image.Height,
            image.Format,
            sampler);

        return device.CreateTexture2D(description, image.Pixels, name);
    }
}

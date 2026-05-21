using StbImageSharp;

namespace Blix.Graphics.Images;

public static class ImageLoader
{
    public static ImageData LoadRgba32(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"Image file not found: {path}", path);
        }

        // stb defaults to bottom-up; OpenGL UV origin is bottom-left, so we want
        // image rows top-to-bottom to match the engine's UV convention (top-left = (0, 0)).
        StbImage.stbi_set_flip_vertically_on_load(1);

        using var stream = File.OpenRead(path);
        var result = ImageResult.FromStream(stream, ColorComponents.RedGreenBlueAlpha);

        if (result.Width <= 0 || result.Height <= 0 || result.Data is null)
        {
            throw new InvalidDataException($"Image file is not a valid decodable image: {path}");
        }

        return new ImageData(
            result.Width,
            result.Height,
            Blix.Graphics.TextureFormat.Rgba8,
            result.Data);
    }

    // In-memory decode for image bytes that aren't on disk — the canonical case is a
    // PNG/JPEG embedded in a .glb's binary chunk. Same Y-flip as the disk path so UV
    // conventions match across both loading sources.
    public static ImageData LoadRgba32(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        StbImage.stbi_set_flip_vertically_on_load(1);
        var result = ImageResult.FromStream(stream, ColorComponents.RedGreenBlueAlpha);
        if (result.Width <= 0 || result.Height <= 0 || result.Data is null)
        {
            throw new InvalidDataException("Stream is not a valid decodable image.");
        }
        return new ImageData(
            result.Width,
            result.Height,
            Blix.Graphics.TextureFormat.Rgba8,
            result.Data);
    }
}

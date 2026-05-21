using Blix.Graphics.Images;

namespace Blix.Assets;

public sealed class TextureImporter : IAssetImporter<ImageData>
{
    public string Name => "texture.rgba8";

    public ImageData Import(AssetImportContext context)
    {
        return ImageLoader.LoadRgba32(context.SourcePath);
    }
}

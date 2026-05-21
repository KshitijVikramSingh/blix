namespace Blix.Assets;

public sealed class AssetImportContext
{
    public AssetImportContext(AssetId assetId, string sourcePath)
    {
        AssetId = assetId;
        SourcePath = sourcePath;
    }

    public AssetId AssetId { get; }

    public string SourcePath { get; }
}

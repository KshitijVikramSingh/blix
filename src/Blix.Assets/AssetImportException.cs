namespace Blix.Assets;

public sealed class AssetImportException : Exception
{
    public AssetImportException(string sourcePath, int? lineNumber, string message)
        : base(Format(sourcePath, lineNumber, message))
    {
        SourcePath = sourcePath;
        LineNumber = lineNumber;
    }

    public AssetImportException(string sourcePath, int? lineNumber, string message, Exception innerException)
        : base(Format(sourcePath, lineNumber, message), innerException)
    {
        SourcePath = sourcePath;
        LineNumber = lineNumber;
    }

    public string SourcePath { get; }

    public int? LineNumber { get; }

    private static string Format(string sourcePath, int? lineNumber, string message)
    {
        return lineNumber.HasValue
            ? $"{sourcePath}:{lineNumber.Value}: {message}"
            : $"{sourcePath}: {message}";
    }
}

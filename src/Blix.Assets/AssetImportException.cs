namespace Blix.Assets;

public sealed class AssetImportException : Exception
{
    public AssetImportException(string sourcePath, int? lineNumber, string message)
        : base(Format(sourcePath, lineNumber, message))
    {
        SourcePath = sourcePath;
        LineNumber = lineNumber;
    }

    /// <summary>
    /// Run a read, and turn anything it throws into a refusal that names the file.
    /// </summary>
    /// <remarks>
    /// <b>Here rather than copied into each importer, because there are more importers than anyone
    /// remembers.</b> GltfImporter was given this treatment and GltfStaticImporter was not, so
    /// `blix view --rig bad.glb` reported cleanly while `--model bad.glb` still exited 134 through
    /// the parser's own exception — the same bug, one file away, found only by handing every entry
    /// point a broken file.
    /// <para>
    /// The first line only. A parser writes for whoever maintains the parser: provenance, a byte
    /// position, a link to a validator. The sentence a person needs is the first one, and the rest
    /// stays on <see cref="Exception.InnerException"/> for whoever wants it.
    /// </para>
    /// </remarks>
    public static T Refusing<T>(string sourcePath, Func<T> read)
    {
        ArgumentNullException.ThrowIfNull(read);

        try
        {
            return read();
        }
        catch (AssetImportException)
        {
            throw;
        }
        catch (Exception reason)
        {
            var first = reason.Message.Split('\n', '\r')[0].Trim();
            throw new AssetImportException(sourcePath, null, $"not a glTF Blix can read — {first}", reason);
        }
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

namespace Blix.Cooked;

/// <summary>
/// The engine declining a file: Blix cannot read this, and here is which file and why.
/// </summary>
/// <remarks>Lives in Blix.Cooked so source importers and every cooked-format reader can share it.</remarks>
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
    /// Centralizes exception normalization so every importer reports the same file-naming refusal.
    /// <para>
    /// The first line only. A parser writes for whoever maintains the parser: provenance, a byte
    /// position, a link to a validator. The sentence a person needs is the first one, and the rest
    /// stays on <see cref="Exception.InnerException"/> for whoever wants it.
    /// </para>
    /// </remarks>
    /// <param name="what">
    /// What was being read, for the message. Defaults to a glTF because that is where this started;
    /// a cooked reader passes its own extension so a bad <c>.blixmesh</c> does not report itself as
    /// a bad glTF.
    /// </param>
    public static T Refusing<T>(string sourcePath, Func<T> read, string what = "glTF")
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
            throw new AssetImportException(sourcePath, null, $"not a {what} Blix can read — {first}", reason);
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

using System.Text.Json;

namespace Blix.Assets;

public sealed class AssetDatabase
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly Dictionary<AssetId, Registration> registrations = [];
    private readonly Dictionary<string, ImporterEntry> importers = [];

    public AssetDatabase RegisterImporter<TOutput>(IAssetImporter<TOutput> importer)
    {
        ArgumentNullException.ThrowIfNull(importer);

        if (importers.ContainsKey(importer.Name))
        {
            throw new InvalidOperationException($"Importer '{importer.Name}' is already registered.");
        }

        importers[importer.Name] = new ImporterEntry(
            typeof(TOutput),
            ctx => importer.Import(ctx)!);

        return this;
    }

    public AssetDatabase LoadManifest(string manifestPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestPath);

        if (!File.Exists(manifestPath))
        {
            throw new FileNotFoundException($"Manifest not found: {manifestPath}", manifestPath);
        }

        var manifestDir = Path.GetDirectoryName(Path.GetFullPath(manifestPath))
            ?? throw new InvalidOperationException($"Cannot resolve manifest directory: {manifestPath}");

        ManifestSchema? schema;

        try
        {
            using var stream = File.OpenRead(manifestPath);
            schema = JsonSerializer.Deserialize<ManifestSchema>(stream, JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new AssetImportException(manifestPath, null, $"invalid manifest JSON: {ex.Message}", ex);
        }

        if (schema?.Assets is null)
        {
            throw new AssetImportException(manifestPath, null, "manifest missing 'assets' array.");
        }

        foreach (var entry in schema.Assets)
        {
            if (string.IsNullOrWhiteSpace(entry.Id))
            {
                throw new AssetImportException(manifestPath, null, "manifest entry missing 'id'.");
            }

            if (string.IsNullOrWhiteSpace(entry.Importer))
            {
                throw new AssetImportException(manifestPath, null, $"asset '{entry.Id}' missing 'importer'.");
            }

            if (string.IsNullOrWhiteSpace(entry.Source))
            {
                throw new AssetImportException(manifestPath, null, $"asset '{entry.Id}' missing 'source'.");
            }

            if (!importers.TryGetValue(entry.Importer, out var importerEntry))
            {
                throw new AssetImportException(
                    manifestPath,
                    null,
                    $"asset '{entry.Id}' uses unknown importer '{entry.Importer}'. Call RegisterImporter before LoadManifest.");
            }

            AssetId id;

            try
            {
                id = AssetId.Parse(entry.Id);
            }
            catch (ArgumentException ex)
            {
                throw new AssetImportException(manifestPath, null, $"invalid asset id '{entry.Id}': {ex.Message}", ex);
            }

            if (registrations.ContainsKey(id))
            {
                throw new AssetImportException(manifestPath, null, $"asset '{id.Value}' is already registered.");
            }

            var sourcePath = Path.GetFullPath(Path.Combine(manifestDir, entry.Source));

            registrations[id] = new Registration(
                id,
                sourcePath,
                importerEntry.OutputType,
                entry.Importer,
                importerEntry.Invoke);
        }

        return this;
    }

    public TOutput Load<TOutput>(AssetId id)
    {
        if (!registrations.TryGetValue(id, out var registration))
        {
            throw new InvalidOperationException($"Unknown asset id: '{id.Value}'.");
        }

        if (registration.OutputType != typeof(TOutput))
        {
            throw new InvalidOperationException(
                $"Asset '{id.Value}' is registered as {registration.OutputType.Name}, not {typeof(TOutput).Name}.");
        }

        var context = new AssetImportContext(id, registration.SourcePath);
        return (TOutput)registration.Invoke(context);
    }

    private sealed record Registration(
        AssetId Id,
        string SourcePath,
        Type OutputType,
        string ImporterName,
        Func<AssetImportContext, object> Invoke);

    private sealed record ImporterEntry(
        Type OutputType,
        Func<AssetImportContext, object> Invoke);

    private sealed class ManifestSchema
    {
        public List<ManifestEntry>? Assets { get; set; }
    }

    private sealed class ManifestEntry
    {
        public string? Id { get; set; }
        public string? Importer { get; set; }
        public string? Source { get; set; }
    }
}

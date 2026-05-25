namespace Blix.Diagnostics;

// Documented payload schema for asset-load events. Loaders that decide
// between cooked / source / fallback paths should construct one of these
// and pass it to DebugContext.Events.Info/Warn (info on the happy path,
// warn when a fallback was taken). Sinks can pattern-match Payload as
// AssetLoadReport for structured display or analysis.
//
// Modes:
//   Source   — original file (e.g. .gltf, .png, .hdr) was parsed directly
//   Cooked   — engine-native binary (.blixmesh / .blixtex / .blixprobe) used
//   Fallback — desired path missing/invalid; loader used a substitute
//              (procedural sky, placeholder texture, default material)
//   Missing  — nothing usable was found; the asset slot is empty
public enum AssetLoadMode
{
    Source,
    Cooked,
    Fallback,
    Missing
}

public sealed record AssetLoadReport(
    string SourcePath,
    string? CookedPath,
    AssetLoadMode Mode,
    long Bytes,
    double LoadMs,
    string? Warning = null);

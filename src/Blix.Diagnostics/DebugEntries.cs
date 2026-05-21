namespace Blix.Diagnostics;

public enum DebugControlKind
{
    Boolean,
    Float,
    Enum,
    Button
}

public sealed record DebugValueEntry(
    string Path,
    string Scope,
    string Name,
    object? Value);

public sealed record DebugControlEntry(
    string Path,
    string Scope,
    string Name,
    DebugControlKind Kind,
    object Value,
    float Min = 0.0f,
    float Max = 1.0f,
    IReadOnlyList<string>? Options = null);

namespace Blix.Diagnostics;

// Producer-side hook for "I have inspection data for some entity."
//
// Called by the registry once per frame when DebugSystem.SelectedPath is
// non-null — every registered IDebugInspectable receives the selected
// path and decides whether it owns it (typically via prefix-check
// against its own DebugName or a more-specific path it tracks).
//
// Producers that don't own the path simply return without emitting. The
// runtime doesn't try to route a single inspect call to "the right"
// inspectable; each producer self-filters. That keeps the same interface
// usable for both single-entity (Camera) and multi-entity (every glTF
// submesh) producers.
//
// Emissions are auto-scoped under "selection" by the runtime, so
//   debug.Values.Value("material", "Marble")
// resolves to path "selection/material" — the dedicated ImGui Selection
// panel pulls these and the State panel skips them.
public interface IDebugInspectable : IDebugContributor
{
    void Inspect(string entityPath, DebugContext debug);
}

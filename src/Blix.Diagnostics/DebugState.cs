namespace Blix.Diagnostics;

public sealed class DebugState
{
    public bool Enabled { get; set; }

    public bool ShowOverlay { get; set; } = true;

    public bool ShowDebugDraw { get; set; } = true;
}

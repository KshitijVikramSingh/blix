using Blix.Graphics;

namespace Blix.Core;

public interface IRuntimeDiagnosticsSink
{
    void OnFrameDebug(FrameDebugPacket packet, ResourceRegistrySnapshot resources);
}

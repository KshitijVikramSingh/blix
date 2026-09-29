using Blix.Core;
using Blix.Graphics;

namespace Blix;

// The game-shape loop contract. Game code targets this; the runtime (Blix.Runtime.*)
// adapts platform events to it. Input is intentionally not on this surface and is not a
// callback either: read IRenderHost.Input during OnUpdate, where it holds still.
public interface IGameLoop
{
    void OnLoad(IRenderHost host, IGraphicsDevice graphicsDevice) { }

    void OnUpdate(Time time) { }

    void OnRender(Time time, RenderFrameContext frame, RenderCommandList commandList);

    void OnResize(int width, int height) { }

    // Called once, when the host is done with the loop and the GPU is idle, so a loop can release
    // its graphics resources here. The last thing either host calls before tearing down.
    void OnUnload() { }
}

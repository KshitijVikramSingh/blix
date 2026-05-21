using Blix.Core;
using Blix.Graphics;

namespace Blix;

// The game-shape loop contract. Game code targets this; the runtime (Blix.Runtime.*)
// adapts platform events to it. Input is intentionally not on this surface — implement
// Blix.Core.IInputHandler alongside this to receive key/mouse events.
public interface IGameLoop
{
    void OnLoad(IRenderHost host, IGraphicsDevice graphicsDevice) { }

    void OnUpdate(Time time) { }

    void OnRender(Time time, RenderFrameContext frame, RenderCommandList commandList);

    void OnResize(int width, int height) { }

    void OnUnload() { }
}

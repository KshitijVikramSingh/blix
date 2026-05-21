namespace Blix.Graphics;

public interface IRenderer
{
    FrameDebugPacket Execute(RenderCommandList commandList);
}

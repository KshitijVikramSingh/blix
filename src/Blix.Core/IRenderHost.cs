namespace Blix.Core;

public interface IRenderHost
{
    void SetTitle(string title);

    void RequestClose();

    // FPS-style cursor capture: when true, the cursor is hidden and locked to the
    // window so all mouse movement is delivered as deltas without the pointer
    // wandering off-screen. When false, the cursor returns to normal.
    void SetCursorCaptured(bool captured);

    // Size of the window's client area in LOGICAL pixels — the same coordinate
    // system mouse events use. On high-DPI displays (macOS Retina, Windows DPI
    // scaling) this differs from `RenderFrameContext.Width/Height`, which reports
    // the framebuffer in physical pixels and is typically 2× larger.
    //
    // Anything that mixes mouse coords with viewport size (the canonical case is
    // screen-point-to-ray picking) must use the same coordinate system on both
    // sides — pass `LogicalSize` here, not the framebuffer dimensions.
    (int Width, int Height) LogicalSize { get; }

    // Toggle vertical sync. Off lets the GPU run uncapped (useful for
    // profiling — the displayed FPS reflects real frame cost, not what
    // the refresh rate clamps it to). On is the user-facing default.
    void SetVSync(bool enabled);
}

using Blix.Graphics;

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

    // Make a texture drawable inside a UI panel, and return the opaque id the UI layer
    // knows it by. Registering the same handle twice returns the same id.
    //
    // <b>Why the host and not the UI library.</b> A panel is built by application code that has
    // no reference to the runtime; it can only reach `ImGui.Image(id, size)`, and an id is the
    // one thing that can cross that gap. The host owns the UI renderer, so the host is the only
    // place that can turn a graphics handle into one.
    //
    // The id is valid until ReleaseUiTexture, or until the texture is destroyed — after which it
    // is a dangling reference the UI layer cannot detect, exactly like any other handle. Register
    // long-lived targets once at load rather than per frame.
    nint RegisterUiTexture(TextureHandle texture);

    // Forget a registered id. Safe to call with an id that was never registered.
    void ReleaseUiTexture(nint id);

    // The display's refresh rate in hertz, or null where the platform will not say.
    //
    // Needed because vsync-on pacing is measured against a deadline, and a deadline
    // cannot be inferred from the frames that miss it. Three attempts at deriving it
    // from a run went wrong in three different ways — a tenth percentile concluded the
    // display refreshed at 33 ms when every frame was a double, a minimum picked up
    // jitter at 14.18 ms on a 16.67 ms panel, and a "cheap case" median read 63 ms once
    // the machine was busy. The display knows; ask the display.
    int? DisplayRefreshHz { get; }
}

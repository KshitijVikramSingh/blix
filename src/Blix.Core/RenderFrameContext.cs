namespace Blix.Core;

// Render-side per-frame info. Width/Height describe the default framebuffer the runtime
// is about to swap. Time progression lives in Blix.Time, passed alongside
// this context wherever a frame is dispatched.
public readonly record struct RenderFrameContext(int Width, int Height);

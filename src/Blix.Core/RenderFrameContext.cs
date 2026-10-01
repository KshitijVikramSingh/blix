namespace Blix.Core;

// Render-side per-frame info. Width/Height describe the default framebuffer the runtime
// is about to swap. Time progression lives in Blix.Time, passed alongside
// this context wherever a frame is dispatched.
//
// FixedAlpha is how far simulation time is toward the next fixed step, in [0, 1), for a loop the host
// runs on one (IFixedGameLoop): a renderer interpolates between the last two steps' states by it. On the
// context and not on Time, because it describes the frame being drawn, not a clock; 0 for every other loop.
public readonly record struct RenderFrameContext(int Width, int Height, double FixedAlpha = 0.0);

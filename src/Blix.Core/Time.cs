namespace Blix;

// Per-frame clock seen by update code in the Blix layer. Total grows monotonically; Delta
// is the seconds elapsed since the previous tick. Kept separate from RenderFrameContext
// because the two can diverge later (fixed-step updates, time scale, pause) without
// shaking the render-side signal.
public readonly record struct Time(double Total, double Delta);

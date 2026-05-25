namespace Blix.Graphics.OpenGL;

// One ready-to-consume GPU timing for a named pass. Lives in the OpenGL
// backend so the existence of GPU timing doesn't leak as a new public
// type in Blix.Graphics — consumers (Window.cs, diagnostics adapters)
// already reference Blix.Graphics.OpenGL.
//
// FrameIssued: the frame on which the GL backend issued the timestamp
// queries that produced this measurement. The result becomes available
// 1–N frames later (driver-dependent). Sinks that want true cross-frame
// attribution can lean on this; a simple HUD can ignore it.
public sealed record GpuPassTiming(string PassName, double ElapsedMs, int FrameIssued);

namespace Blix.Graphics.Vulkan;

// Mirror of Blix.Graphics.OpenGL.GpuPassTiming for the Vulkan backend. Kept
// per-backend (rather than lifted to Blix.Graphics) so each backend's nuances
// stay local — Vulkan's are likely to grow a CalibratedTimestamps flag, a
// queue-family attribution, etc. that don't apply to GL.
public sealed record VkGpuPassTiming(string PassName, double ElapsedMs, int FrameIssued);

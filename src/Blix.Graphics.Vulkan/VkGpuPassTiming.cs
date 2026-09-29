namespace Blix.Graphics.Vulkan;

// A resolved GPU pass timing. Kept in the backend (rather than lifted to
// Blix.Graphics) so Vulkan's nuances stay local — likely to grow a
// CalibratedTimestamps flag, queue-family attribution, etc.
public sealed record VkGpuPassTiming(string PassName, double ElapsedMs, int FrameIssued);

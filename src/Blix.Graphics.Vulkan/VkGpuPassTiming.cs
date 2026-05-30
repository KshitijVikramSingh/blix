namespace Blix.Graphics.Vulkan;

// Mirror of Blix.Graphics.OpenGL.GpuPassTiming for the Vulkan backend. Kept
// per-backend (rather than lifted to Blix.Graphics) so each backend's nuances
// stay local — Vulkan's are likely to grow a CalibratedTimestamps flag, a
// queue-family attribution, etc. that don't apply to GL.
public sealed record VkGpuPassTiming(string PassName, double ElapsedMs, int FrameIssued);

// CPU-side breakdown of one AcquireRecordSubmitPresent call — the three
// phases that, together, are what the demo's bundled `execute` timer measures
// on the CPU thread. Separates them so we can tell what actually moves when we
// change the workload:
//   WaitMs          — vkWaitForFences on the in-flight slot (GPU/vsync throttle;
//                     high = GPU-bound or present-pacing, NOT a CPU cost we can cut).
//   EncodeMs        — BeginCommandBuffer→EndCommandBuffer, i.e. recording every
//                     vkCmd (draws translated to MoltenVK/Metal encoder calls).
//                     This is the cost draw-call COUNT drives — the number that
//                     batching (fewer draws) or GPU-driven indirect (one draw)
//                     would actually move.
//   SubmitPresentMs — vkQueueSubmit + vkQueuePresentKHR (kick + flip enqueue).
// GPU execution itself overlaps these and is measured separately by the
// per-pass timestamps (VkGpuPassTiming).
public sealed record VkCpuFrameTiming(double WaitMs, double EncodeMs, double SubmitPresentMs);

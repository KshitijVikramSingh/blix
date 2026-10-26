using System.Runtime.InteropServices;
using Blix.Core;

namespace Blix.Demos.VulkanSponza;

// GPU captures, for Xcode (a .gputrace document: every encoder's real GPU time, the raster passes' too, which the Vulkan
// timestamps cannot give on a tile-based GPU -- they bracket encoder submission):
//   --gpu-capture PATH [--gpu-capture-frame N]  frame N after load (600), the whole device -- the Vulkan frame and the
//                                               Metal tracer's batches -- through MTLCaptureManager (MetalRayTracer)
//   --mvk-capture SCOPE PATH                    MoltenVK's own: MVK_CONFIG_AUTO_GPU_CAPTURE_SCOPE (1 the device's
//                                               lifetime, 2 the first frame) into MVK_CONFIG_AUTO_GPU_CAPTURE_OUTPUT_FILE
// Either turns the passes' debug labels on (BLIX_GPU_LABELS), so the capture names them. Open the document with
// `open PATH` (Xcode). For numbers to read without Xcode: tools/gpu/trace.sh (a Metal System Trace, exported).
internal sealed partial class SponzaLoop
{
    private string? gpuCapturePath;
    private int gpuCaptureFrame = 600;
    private bool gpuCaptureStarted;

    [DllImport("libc", EntryPoint = "setenv")]
    private static extern int SetEnvNative(string name, string value, int overwrite);

    // Before the Vulkan instance and the Metal device exist: MoltenVK and Metal read these natively at start-up, where
    // Environment.SetEnvironmentVariable does not reach (on macOS it keeps .NET's own copy).
    internal static void ApplyGpuCaptureEnvironment(AppArgs args)
    {
        void Set(string name, string value)
        {
            SetEnvNative(name, value, 1);
            Environment.SetEnvironmentVariable(name, value);
        }
        var wanted = false;
        if (args.String("gpu-capture") is { Length: > 0 })
        {
            Set("MTL_CAPTURE_ENABLED", "1");
            wanted = true;
        }
        if (args.Values("mvk-capture", 2) is [var scope, var path])
        {
            Set("MTL_CAPTURE_ENABLED", "1");
            Set("MVK_CONFIG_AUTO_GPU_CAPTURE_SCOPE", scope);
            Set("MVK_CONFIG_AUTO_GPU_CAPTURE_OUTPUT_FILE", Path.GetFullPath(path));
            Console.WriteLine($"[VulkanSponza] MoltenVK capture: scope {scope} into {Path.GetFullPath(path)}.");
            wanted = true;
        }
        if (wanted) Set("BLIX_GPU_LABELS", "1");
    }

    private void ParseGpuCapture(AppArgs args)
    {
        if (args.String("gpu-capture") is { Length: > 0 } path) gpuCapturePath = Path.GetFullPath(path);
        if (args.Int("gpu-capture-frame") is { } frame) gpuCaptureFrame = Math.Max(1, frame);
    }

    // Each frame, after the post-load count moves: frame N begins the capture, N + 1 ends it (frame N's work inside).
    private void GpuCaptureTick()
    {
        if (gpuCapturePath is null) return;
        if (!gpuCaptureStarted && postLoadFrames == gpuCaptureFrame)
        {
            if (frameTracer is not { } tracer)
            {
                Console.WriteLine("[VulkanSponza] GPU capture: no Metal tracer yet to capture through; none taken.");
                gpuCapturePath = null;
                return;
            }
            if (Directory.Exists(gpuCapturePath)) Directory.Delete(gpuCapturePath, recursive: true);
            if (!tracer.BeginCapture(gpuCapturePath, out var reason))
            {
                Console.WriteLine($"[VulkanSponza] GPU capture: not started ({reason}).");
                gpuCapturePath = null;
                return;
            }
            gpuCaptureStarted = true;
            Console.WriteLine($"[VulkanSponza] GPU capture: frame {postLoadFrames} into {gpuCapturePath}.");
        }
        else if (gpuCaptureStarted && postLoadFrames == gpuCaptureFrame + 1)
        {
            frameTracer?.EndCapture();
            Console.WriteLine($"[VulkanSponza] GPU capture written: {gpuCapturePath} (open it in Xcode: open \"{gpuCapturePath}\").");
            gpuCapturePath = null;
        }
    }
}

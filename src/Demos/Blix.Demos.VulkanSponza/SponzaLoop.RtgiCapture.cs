using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Blix.Core;
using Blix.Graphics;

namespace Blix.Demos.VulkanSponza;

// --rtgi-capture DIR --rtgi-capture-pixels FILE [--rtgi-capture-at N] [--rtgi-capture-frames F]: the final gather's samples,
// for tools/gi-model/final_gather.py to model a per-pixel gather and its denoising against the cache as displayed. The
// first K visible points are pinned to FILE's pixels (one "x y" a line, full-res; K at most the frame's visible points), each
// tracing its ray every frame as any visible point does -- cosine or guided, its hit lit by the sun traced there and the
// cache there (rtgi_train.comp's pointSample): one bounce of a gather. From post-load frame N (600) for F frames (512),
// each frame's sample per point -- three rgba: rgb PI x L_in x the ray's weight, w 4 live + 2 sky + 1 counted; the hit and the
// ray's weight; the hit's normal and distance (1e30 a miss) -- then the points' positions and normals: DIR/capture.f32
// (float32 rgba, F x K x 3 then 2 x K) and DIR/capture.json. The cache trains on as ever around
// them. Hold the camera still (--cam), the dumps' window.
internal sealed partial class SponzaLoop
{
    private string? rtgiCaptureDir;
    private int rtgiCaptureAt = 600, rtgiCaptureFrames = 512;
    private (int X, int Y)[] rtgiCapturePixels = Array.Empty<(int, int)>();
    private GpuBufferHandle rtgiCapturePixelBuf, rtgiCaptureOut;
    private bool rtgiCaptureWritten;

    private void ParseRtgiCapture(AppArgs args)
    {
        rtgiCaptureDir = args.String("rtgi-capture");
        if (rtgiCaptureDir is null) return;
        if (args.Int("rtgi-capture-at") is { } at) rtgiCaptureAt = Math.Max(1, at);
        if (args.Int("rtgi-capture-frames") is { } f) rtgiCaptureFrames = Math.Max(1, f);
        var file = args.String("rtgi-capture-pixels") ?? throw new ArgumentException("--rtgi-capture needs --rtgi-capture-pixels FILE");
        rtgiCapturePixels = File.ReadAllLines(file).Where(l => l.Trim().Length > 0).Select(l =>
        {
            var p = l.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return (int.Parse(p[0], CultureInfo.InvariantCulture), int.Parse(p[1], CultureInfo.InvariantCulture));
        }).ToArray();
    }

    // The frame's capture slot, -1 outside the window.
    private int RtgiCaptureSlot => rtgiCaptureDir is null || rtgiCaptureWritten ? -1
        : postLoadFrames >= rtgiCaptureAt && postLoadFrames < rtgiCaptureAt + rtgiCaptureFrames ? postLoadFrames - rtgiCaptureAt : -1;

    private int RtgiCapturePoints => RtgiCaptureSlot >= 0 ? Math.Min(rtgiCapturePixels.Length, rtgiTilesNow) : 0;

    // uCapture for both passes: x the pinned points, y the slot, z the slots.
    private Vector4 RtgiCaptureUniform => new(RtgiCapturePoints, Math.Max(0, RtgiCaptureSlot), rtgiCaptureFrames, 0f);

    private void EnsureRtgiCapture()
    {
        if (!rtgiCapturePixelBuf.Equals(default(GpuBufferHandle))) return;
        var k = Math.Max(1, rtgiCapturePixels.Length);
        var words = new uint[k];
        for (var i = 0; i < rtgiCapturePixels.Length; i++) words[i] = (uint)rtgiCapturePixels[i].X | ((uint)rtgiCapturePixels[i].Y << 16);
        rtgiCapturePixelBuf = Own(device.CreateGpuBuffer(k * 4, MemoryMarshal.AsBytes(words.AsSpan()).ToArray(), "sponza.rtgi.capture-pixels"));
        var bytes = (3 * rtgiCaptureFrames + 2) * k * 16;
        rtgiCaptureOut = Own(device.CreateGpuBuffer(bytes, new byte[bytes], "sponza.rtgi.capture"));
    }

    private GpuBufferHandle RtgiCapturePixelBuffer { get { EnsureRtgiCapture(); return rtgiCapturePixelBuf; } }
    private GpuBufferHandle RtgiCaptureOutBuffer { get { EnsureRtgiCapture(); return rtgiCaptureOut; } }

    // A few frames past the window (the training a frame behind the placement): read back and written.
    private void WriteRtgiCapture()
    {
        if (rtgiCaptureDir is null || rtgiCaptureWritten || postLoadFrames < rtgiCaptureAt + rtgiCaptureFrames + 3) return;
        rtgiCaptureWritten = true;
        device.WaitIdle();
        var k = Math.Max(1, rtgiCapturePixels.Length);
        var data = device.ReadGpuBuffer(RtgiCaptureOutBuffer, 0, (3 * rtgiCaptureFrames + 2) * k * 16);
        Directory.CreateDirectory(rtgiCaptureDir);
        File.WriteAllBytes(Path.Combine(rtgiCaptureDir, "capture.f32"), data);
        File.WriteAllText(Path.Combine(rtgiCaptureDir, "capture.json"), JsonSerializer.Serialize(new
        {
            points = k,
            frames = rtgiCaptureFrames,
            words = 3,
            at = rtgiCaptureAt,
            width = rtgiCaptureSize.W,
            height = rtgiCaptureSize.H,
            pixels = rtgiCapturePixels.Select(p => new[] { p.X, p.Y }).ToArray(),
            camera = new[] { cameraPosition.X, cameraPosition.Y, cameraPosition.Z },
        }));
        Console.WriteLine($"[VulkanSponza] rtgi capture: {k} points x {rtgiCaptureFrames} frames into {rtgiCaptureDir}.");
    }

    private (int W, int H) rtgiCaptureSize;
}

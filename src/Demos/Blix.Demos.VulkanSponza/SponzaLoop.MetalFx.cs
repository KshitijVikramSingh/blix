using System.Globalization;
using System.Numerics;
using Blix.Core;
using Blix.Graphics;
using Blix.Graphics.MetalHWX;

namespace Blix.Demos.VulkanSponza;

// --metalfx: the scene rendered below the window's resolution (render scale 0.5 unless --render-scale says otherwise) and
// brought up to it by MetalFX's temporal scaler, in place of the TAA resolve: this frame's jittered HDR colour, its depth
// and its motion (the pre-pass's velocity) at the render size, into a target at the window's size that the present pass
// tonemaps. Everything before it -- the scene, the lighting, RTGI's screen passes -- runs at the render size; at 0.5 a
// quarter of the pixels. Native Metal through the tracer's context (MetalRayTracer.EncodeUpscale), ordered against the
// frame on its timeline like the tracer's work. Opt-in (2026-10-27).
//
// BLIX_METALFX_JITTER_SIGN / BLIX_METALFX_MOTION_SIGN ("x,y", each 1 or -1): the conventions for the jitter offset and
// the motion vectors' direction flipped per axis, to check them by eye -- a wrong one shows as a shimmer that does not
// settle on a still view (jitter) or smearing behind what moves (motion).
internal sealed partial class SponzaLoop
{
    private bool metalFx;
    private GraphResourceHandle metalFxOutHandle;
    private PassHandle metalFxPassHandle;
    // This frame's sample offset in render pixels (the jitter, as the clip-space translation's), and whether the scaler's
    // output holds this frame.
    private Vector2 metalFxJitter;
    private bool metalFxWritten;
    private bool metalFxHistoryValid;
    private string? metalFxRefused;
    private Vector2 metalFxJitterSign = SignPair("BLIX_METALFX_JITTER_SIGN", new Vector2(1f, 1f));
    private Vector2 metalFxMotionSign = SignPair("BLIX_METALFX_MOTION_SIGN", new Vector2(-1f, -1f));
    private ulong profMetalFxCount, profMetalFxNs;

    private static Vector2 SignPair(string name, Vector2 fallback)
    {
        var text = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(text)) return fallback;
        var parts = text.Split(',');
        return parts.Length == 2
            && float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var x)
            && float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var y)
            ? new Vector2(MathF.Sign(x), MathF.Sign(y)) : fallback;
    }

    private void ParseMetalFx(AppArgs args)
    {
        metalFx = args.Flag("metalfx");
        // Half the window each way unless asked otherwise (a later --render-scale overrides this).
        if (metalFx) render.RenderScale = 0.5f;
    }

    // Setup, after the last scene pass and before the graph compiles: the window-sized output and the external pass.
    private void DeclareMetalFx()
    {
        if (!metalFx) return;
        if (!SurfaceTargets) { metalFxRefused = "it needs the pre-pass's velocity (no MSAA)"; return; }
        if ((device.Features & GraphicsFeatures.MetalObjects) == 0) { metalFxRefused = "the device exports no Metal objects"; return; }
        metalFxOutHandle = graph.ColorTarget("metalfx-out", TextureFormat.Rgba16F, new MatchSwapchainGraphSize(1f, Display: true), storage: true);
        metalFxPassHandle = graph.ExternalPass("metalfx");
    }

    private bool MetalFxOn => metalFx && metalFxRefused is null && !metalFxOutHandle.Equals(default(GraphResourceHandle));

    // In the TAA resolve's place: the scaler, once the tracer's Metal context exists (until then the TAA resolve).
    private bool RecordMetalFx()
    {
        metalFxWritten = false;
        if (!MetalFxOn) return false;
        if (!EnsureFrameTracer() || frameTracer is not { } tracer) return false;
        var reset = !metalFxHistoryValid;
        var color = graph.GetColorTexture(hdrHandle);
        if (!device.TryGetTextureSize(color, out var w, out var h)) return false;
        var depth = graph.GetDepthTexture(SampleableSceneDepth);
        var motion = graph.GetColorTexture(velocityHandle);
        var output = graph.GetColorTexture(metalFxOutHandle);
        // The velocity is this frame's UV minus last frame's (depth_prepass.frag); the scaler takes pixels toward last
        // frame's position.
        var motionScale = new Vector2(w, h) * metalFxMotionSign;
        var jitter = metalFxJitter * metalFxJitterSign;
        graph.External(metalFxPassHandle, sync =>
        {
            var failed = tracer.EncodeUpscale(sync, color, depth, motion, output, jitter, motionScale, reset);
            if (failed is not null && metalFxRefused is null)
            {
                metalFxRefused = failed;
                Console.WriteLine($"[VulkanSponza] MetalFX: {failed}; the TAA resolve from here.");
            }
        });
        metalFxHistoryValid = true;
        metalFxWritten = true;
        return true;
    }

    // At the profile window: the scaler's GPU time a frame.
    private string MetalFxProfile(int frames)
    {
        if (!MetalFxOn) return metalFx && metalFxRefused is not null ? $"metalfx: off ({metalFxRefused})" : "";
        var (count, ns, _, failures) = MetalRayTracer.UpscaleStats();
        var line = string.Create(CultureInfo.InvariantCulture,
            $"metalfx: {(ns - profMetalFxNs) / 1e6 / Math.Max(1, frames):0.00} ms a frame ({(count - profMetalFxCount) / (double)Math.Max(1, frames):0.0} a frame{(failures > 0 ? $", {failures} failed" : "")})");
        profMetalFxCount = count;
        profMetalFxNs = ns;
        return line;
    }
}

using Blix.Graphics;

namespace Blix.Diagnostics;

// Engine-default debug contributor for an IGraphicsDevice. Surfaces the
// device's vendor / renderer string as Values (so a JSON dump records what
// hardware the run targeted) and the per-frame error counter as a Stat —
// "draws are happening but the device API rejected something" is the kind
// of signal that should be one glance away in the overlay's Stats tab.
//
// Wired via the IGraphicsDeviceExtensions.RegisterDebug convenience method:
//
//     graphicsDevice.RegisterDebug(debugSystem);
//
// Demos opt in once and get the surface for free; the diagnostics core
// owns the contributor lifetime via DebugSystem.Register.
public sealed class GraphicsDeviceContributor : IDebuggable
{
    private readonly IGraphicsDevice device;
    private string lastReportedError = string.Empty;

    public GraphicsDeviceContributor(IGraphicsDevice device)
    {
        ArgumentNullException.ThrowIfNull(device);
        this.device = device;
    }

    public string DebugName => "gpu";

    public void Debug(DebugContext debug)
    {
        var info = device.Info;
        debug.Values.Value("vendor", info.Vendor);
        debug.Values.Value("renderer", info.Renderer);

        var diag = device.DiagnosticsSnapshot;
        // Gauge, not Count: FrameErrorCount is the device's own
        // already-aggregated per-frame total. Count would accumulate
        // across calls inside the same frame.
        debug.Stats.Gauge("frame-errors", diag.FrameErrorCount);

        // Transient vertex arena traffic — KiB used this frame in the current ring
        // slot, plus the all-time per-slot high-water mark. Makes "how much
        // per-frame vertex data am I uploading?" one glance away.
        if (diag.TransientArenaCapacityBytes > 0)
        {
            debug.Stats.Gauge("arena-kib", diag.TransientArenaBytesUsed / 1024);
            debug.Stats.Gauge("arena-peak-kib", diag.TransientArenaHighWaterBytes / 1024);
        }

        // Pipeline cache effectiveness: distinct cached pipelines + cumulative hits.
        if (diag.PipelineCacheCount > 0 || diag.PipelineCacheHits > 0)
        {
            debug.Stats.Gauge("pipelines-cached", diag.PipelineCacheCount);
            debug.Stats.Gauge("pipeline-cache-hits", diag.PipelineCacheHits);
        }

        // De-dup repeated error messages: the device snapshot holds the
        // most recent error so it'd otherwise re-emit every frame until
        // something else replaces it. Echo it once per distinct message.
        if (!string.IsNullOrEmpty(diag.LastErrorMessage) &&
            diag.LastErrorMessage != lastReportedError)
        {
            var label = string.IsNullOrEmpty(diag.LastErrorContext)
                ? diag.LastErrorMessage
                : $"{diag.LastErrorContext}: {diag.LastErrorMessage}";
            debug.Events.Warn(label);
            lastReportedError = diag.LastErrorMessage;
        }
    }
}

public static class GraphicsDeviceDebugExtensions
{
    // Convenience for "engine default" registration. Demos call this once
    // after constructing their device to wire the vendor/renderer/error
    // surface into the overlay without writing a contributor themselves.
    // Idempotent against DebugSystem.Register (the registry de-dupes by
    // reference), but each call still allocates a fresh contributor, so
    // call it once per device.
    public static GraphicsDeviceContributor RegisterDebug(this IGraphicsDevice device, DebugSystem debug)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(debug);
        var contributor = new GraphicsDeviceContributor(device);
        debug.Register(contributor);
        return contributor;
    }
}

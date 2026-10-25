using System.Globalization;
using Blix.Core;

namespace Blix.Demos.VulkanSponza;

// --change-boost-ms X (0 off; Live's boost sets it a step): a sun change (after the first landing) runs its sweep slot at
// X ms a frame and its re-aim's bounces one a frame (--live-bounce-split 1), the frame rate dipping for the change's few
// seconds -- a small step's time was ~2-2.5 s of re-aim and setup at the 3 ms slot and the bounces over 3 frames each,
// then its screen gather (Crytek Sponza, 2026-10-25). Back at the landing.
internal sealed partial class SponzaLoop
{
    private float changeBoostMs;
    private (float GpuMs, int BounceSplit)? boostSaved;

    private void ParseChangeBoost(AppArgs args)
    {
        if (args.Float("change-boost-ms") is { } b) changeBoostMs = Math.Max(0f, b);
    }

    // LiveRetarget: the change begins.
    private void BeginChangeBoost()
    {
        if (changeBoostMs <= 0f || !liveSkyGathered || boostSaved is not null) return;
        boostSaved = (sweepGpuMs, liveBounceSplit);
        sweepGpuMs = MathF.Max(sweepGpuMs, changeBoostMs);
        liveBounceSplit = 1;
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"[VulkanSponza] change boost: {sweepGpuMs:0.#} ms a frame, a bounce a frame, until the landing."));
    }

    // FinishSplitCook.
    private void EndChangeBoost()
    {
        if (boostSaved is not { } saved) return;
        sweepGpuMs = saved.GpuMs;
        liveBounceSplit = saved.BounceSplit;
        boostSaved = null;
    }
}

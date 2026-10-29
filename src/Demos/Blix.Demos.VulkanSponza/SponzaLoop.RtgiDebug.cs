using Blix.Diagnostics;

namespace Blix.Demos.VulkanSponza;

// The RTGI panel (the debug overlay's "RTGI" scope): what the cache is doing, painted in place of the light; the knobs
// that were flags, live; reset and freeze, to watch it converge from nothing or hold a state still; the cache's counts
// and Metal's trace time beside the Perf tab's passes.
internal sealed partial class SponzaLoop
{
    private static readonly string[] RtgiViewNames =
    {
        "Light", "Done (green) / open (red)", "Unsure (done threshold to 16x)", "Samples held (1 to 16,384, log)",
        "Cell level", "Hit distance (5 cm red to 20 m blue)", "Sky visibility", "Sun visibility", "Light before the blur",
        "Blur radius", "World volume alone (--rtgi-volume)",
    };

    // The view (rtgi_readout.frag's uContact.w, rtgi_blur.frag's uHalf.x): 0 the light; 1-7 a cell's state; 8 the light
    // unblurred; 9 the blur's radius.
    private int rtgiView;
    // The Show channel before a view took it (a view is shown as the raw bounce radiance, no albedo on it).
    private int rtgiShowBefore = -1;
    // Freeze: no rays, no training -- the cache held as it is, still read. Reset: every slot emptied next frame.
    private bool rtgiFrozen, rtgiResetPending;
    // The last profile window's cache counts and Metal trace time (SponzaLoop.Profile.cs), for the panel.
    private string rtgiPanelCounts = "", rtgiPanelMetal = "";

    // The blur as the view wants it: off while a cell's state or the unblurred light is painted.
    private float RtgiBlurNow => rtgiView is (>= 1 and <= 8) or 10 ? 0f : rtgiBlur;

    private void RtgiControls(DebugContext debug)
    {
        if (!rtgi) return;
        using (debug.Scope("RTGI"))
        {
            var view = debug.Controls.Enum("View", rtgiView, RtgiViewNames);
            if (view != rtgiView)
            {
                // A view paints the incident light: shown as the raw bounce radiance (channel 10), the Show channel kept
                // to give back.
                if (rtgiView == 0 && view != 0) { rtgiShowBefore = (int)MathF.Round(vizChannel); vizChannel = 10; }
                if (view == 0 && rtgiShowBefore >= 0) { vizChannel = rtgiShowBefore; rtgiShowBefore = -1; }
                rtgiView = view;
            }
            if (debug.Controls.Button("Reset cache")) rtgiResetPending = true;
            rtgiFrozen = debug.Controls.Toggle("Freeze (no rays, no training)", rtgiFrozen);
            if (rtgiHybridBuilt) rtgiHybrid = debug.Controls.Toggle("Hybrid near field (--rtgi-hybrid)", rtgiHybrid);
            if (rtgiHybridBuilt) rtgiHybridWorld = debug.Controls.Toggle("Near field in world cells (off: per-pixel history)", rtgiHybridWorld);
            if (rtgiVolumeBuilt)
            {
                rtgiVolume = debug.Controls.Toggle("World volume (--rtgi-volume)", rtgiVolume);
                rtgiVolumeRead = debug.Controls.Toggle("Volume: readout uses probes", rtgiVolumeRead);
                if (rtgiNearCookBuilt) rtgiNearCook = debug.Controls.Toggle("Cooked near field (--rtgi-near-cook)", rtgiNearCook);
                rtgiVolumeNeighbours = debug.Controls.Toggle("Volume: neighbour test (traced)", rtgiVolumeNeighbours);
                var geomCells = debug.Controls.Toggle("Cells sized by geometry (off: by camera distance)", rtgiVolumeCells > 0f);
                rtgiVolumeCells = geomCells ? (rtgiVolumeCells > 0f ? rtgiVolumeCells : 0.1f) : 0f;
                rtgiVolumeTrust = debug.Controls.Float("Volume: trust (rel. error as a cell's)", rtgiVolumeTrust, 0.02f, 2f);
                rtgiVolumeShare = debug.Controls.Float("Volume: share of rays", rtgiVolumeShare, 0f, 1f);
            }
            rtgiPoolLevels = MathF.Round(debug.Controls.Float("Pool levels up (--rtgi-pool, 0 off)", rtgiPoolLevels, 0f, 4f));
            rtgiPoolSigma = debug.Controls.Float("Pool agreement (standard errors)", rtgiPoolSigma, 0f, 3f);
            rtgiGuide = debug.Controls.Float("Guided share of rays (--rtgi-guide)", rtgiGuide, 0f, 0.95f);
            rtgiBlur = debug.Controls.Float("Blur (px at most, 0 off)", rtgiBlur, 0f, 32f);
            if (rtgiTemporal) rtgiTemporalMoving = MathF.Round(debug.Controls.Float("Temporal: frames while moving", rtgiTemporalMoving, 1f, 16f));
            if (rtgiTemporal) rtgiTemporalMotion = debug.Controls.Toggle("Temporal: 2 frames while moving (old)", rtgiTemporalMotion);
            rtgiBlurHit = debug.Controls.Float("Blur reach (of hit distance)", rtgiBlurHit, 0f, 1f);
            rtgiHitPrior = debug.Controls.Float("Hit prior (0 off)", rtgiHitPrior, 0f, 2f);
            rtgiParentShrink = debug.Controls.Toggle("Cascade: shrinkage", rtgiParentShrink);
            rtgiAge = debug.Controls.Float("History: share of age (0 no limit)", rtgiAge, 0f, 1f);
            rtgiDoneError = debug.Controls.Float("Done at relative error", rtgiDoneError, 0.001f, 0.05f);
            rtgiAdaptive = debug.Controls.Float("Placement by uncertainty (share)", rtgiAdaptive, 0f, 1f);
            rtgiBlock = debug.Controls.Toggle("Placement in blocks", rtgiBlock);
            rtgiBlockGlobal = debug.Controls.Float("Blocks: global share", rtgiBlockGlobal, 0f, 1f);
            rtgiGuide = debug.Controls.Float("Guided rays (share)", rtgiGuide, 0f, 0.9f);
            rtgiBlue = debug.Controls.Toggle("Blue-noise directions", rtgiBlue);
            rtgiSettledSun = debug.Controls.Toggle("Settled sun: 1 ray in 8", rtgiSettledSun);
            rtgiSubgroup = debug.Controls.Toggle("Subgroup sums and appends", rtgiSubgroup);
            if (rtgiPanelCounts.Length > 0) debug.Values.Value("rtgi-cache", rtgiPanelCounts);
            if (rtgiPanelMetal.Length > 0) debug.Values.Value("rtgi-metal", rtgiPanelMetal);
        }
    }
}

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
        "Blur radius",
        "Cell ID (colour per cache cell)", "Cell size (cooked; 5 cm blue - 1 m red, grey not cooked)", "Cell raw light (unblended)",
        "Cell blended light (the record)", "Cell cooked neighbours (grey all clear; magenta not cooked)", "Cooked L0 cell ID",
        "Cooked L0 cell state (red buried, grey no near field, blue ungathered)", "Cooked near: near light",
        "Cell samples held (log)", "Fix-up pixels (magenta)",
        "Temporal history held (blue 0 - red 16)", "Cell region (grey: none cooked)",
        "Cooked near: far term (escape share x far light per ray)", "Cooked near: the cache's light under it",
        "Cooked cell (red buried, yellow junction, green own level, blue ancestor; white its points (red retired), black ring its representative; none here: orange finer, cyan coarser, magenta neither)",
        "World position (diagnostic, for dumps)", "Level, region, cooked size (diagnostic, for dumps)",
        "Own level read alone (diagnostic, for dumps)", "Next level read alone (diagnostic, for dumps)",
        "Next level own cell (assembled when on), w its region (diagnostic, for dumps)",
        "Light vs snapshot (blue darker, grey same, red brighter; a stop full)",
        "Samples gained since snapshot (dark red none, green-yellow 1 to 16k, cyan restarted)",
        "Still moving: recent light vs shown, in standard errors (grey under 2, blue darker, red brighter, full at 6)",
        "Own light (unblended) vs snapshot (as view 30, before the blend)",
        "Cell assembled light (before the neighbour blend)",
        "Assembly: children vs own (white own only, blue children darker, red brighter; black children only)",
    };

    // The view (rtgi_readout.frag's uView.x, rtgi_blur.frag's uHalf.x, rtgi_temporal.frag's uDebug.x): 0 the light; 1-7 a
    // cell's state; 8 the light unblurred; 9 the blur's radius; 10 on the cooked and world-space state (RtgiViewNames;
    // renumbered 2026-10-30 when the world volume's two views went: 18-23 were 20-25, 24 is new).
    // BLIX_RTGI_VIEW=N: the view from the start (dumps of a view, --rtgi-dump).
    private int rtgiView = int.TryParse(Environment.GetEnvironmentVariable("BLIX_RTGI_VIEW"), out var bv) ? bv : 0;
    // The Show channel before a view took it (a view is shown as the raw bounce radiance, no albedo on it).
    private int rtgiShowBefore = -1;
    // Freeze: no rays, no training -- the cache held as it is, still read. Reset: every slot emptied next frame.
    private bool rtgiFrozen, rtgiResetPending;
    // The last profile window's cache counts and Metal trace time (SponzaLoop.Profile.cs), for the panel.
    private string rtgiPanelCounts = "", rtgiPanelMetal = "";

    // The blur as the view wants it: off while a cell's state or the unblurred light is painted.
    private float RtgiBlurNow => rtgiView is (>= 1 and <= 8) or >= 10 ? 0f : rtgiBlur;

    private void RtgiControls(DebugContext debug)
    {
        using (debug.Scope("RTGI"))
        {
            var view = debug.Controls.Enum("View", rtgiView, RtgiViewNames);
            if (view != rtgiView)
            {
                // A view paints the incident light: shown as the raw bounce radiance (channel 8), the Show channel kept
                // to give back.
                if (rtgiView == 0 && view != 0) { rtgiShowBefore = (int)MathF.Round(vizChannel); vizChannel = 8; }
                if (view == 0 && rtgiShowBefore >= 0) { vizChannel = rtgiShowBefore; rtgiShowBefore = -1; }
                rtgiView = view;
            }
            if (debug.Controls.Button("Reset cache")) rtgiResetPending = true;
            // Every slot's light and samples now, for views 30 and 31 to compare against (drift, cells no training reaches).
            if (debug.Controls.Button("Snapshot cell light")) rtgiSnapshotPending = true;
            rtgiFrozen = debug.Controls.Toggle("Freeze (no rays, no training)", rtgiFrozen);
            if (rtgiCookReady)
            {
                rtgiNearCook = debug.Controls.Toggle("Cooked near field (--rtgi-no-near-cook off)", rtgiNearCook);
                rtgiSurfaceTrain = debug.Controls.Toggle("Cells trained from their cooked point", rtgiSurfaceTrain);
                rtgiCookNeighbours = debug.Controls.Toggle("Readout: cooked neighbour masks", rtgiCookNeighbours);
                rtgiFilterReach = debug.Controls.Toggle("Blend: cooked reach", rtgiFilterReach);
                rtgiFilterWide = MathF.Round(debug.Controls.Float("Blend: 5x5 under samples (0 off)", rtgiFilterWide, 0f, 4096f));
            }
            rtgiBlur = debug.Controls.Float("Blur (px at most, 0 off)", rtgiBlur, 0f, 32f);
            if (rtgiTemporal) rtgiTemporalMoving = MathF.Round(debug.Controls.Float("Temporal: frames while moving", rtgiTemporalMoving, 1f, 16f));
            rtgiBlurHit = debug.Controls.Float("Blur reach (of hit distance)", rtgiBlurHit, 0f, 1f);
            rtgiHitPrior = debug.Controls.Float("Hit prior (0 off)", rtgiHitPrior, 0f, 2f);
            rtgiParentShrink = debug.Controls.Toggle("Cascade: shrinkage", rtgiParentShrink);
            // A switch of what trains the coarse cells: the cache emptied with it.
            var assemble = debug.Controls.Toggle("Coarse cells assembled by area (--rtgi-no-assemble off; resets the cache)", rtgiAssemble);
            if (assemble != rtgiAssemble) { rtgiAssemble = assemble; rtgiResetPending = true; }
            rtgiAge = debug.Controls.Float("History: share of age (0 no limit)", rtgiAge, 0f, 1f);
            rtgiDoneError = debug.Controls.Float("Done at relative error", rtgiDoneError, 0.001f, 0.05f);
            rtgiAdaptive = debug.Controls.Float("Placement by uncertainty (share)", rtgiAdaptive, 0f, 1f);
            rtgiBlock = debug.Controls.Toggle("Placement in blocks", rtgiBlock);
            rtgiBlockGlobal = debug.Controls.Float("Blocks: global share", rtgiBlockGlobal, 0f, 1f);
            rtgiBlue = debug.Controls.Toggle("Blue-noise directions", rtgiBlue);
            rtgiSettledSun = debug.Controls.Toggle("Settled sun: 1 ray in 8", rtgiSettledSun);
            rtgiSubgroup = debug.Controls.Toggle("Subgroup sums and appends", rtgiSubgroup);
            if (rtgiPanelCounts.Length > 0) debug.Values.Value("rtgi-cache", rtgiPanelCounts);
            if (rtgiPanelMetal.Length > 0) debug.Values.Value("rtgi-metal", rtgiPanelMetal);
        }
    }
}

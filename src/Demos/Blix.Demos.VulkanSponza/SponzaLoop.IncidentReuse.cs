using System.Numerics;
using Blix.Graphics;

namespace Blix.Demos.VulkanSponza;

// The incident pass reuses last frame's answer (incident_clipmap.frag, uReuse). At rest -- the light the pass reads
// still -- a pixel's incident light changes only as the camera moves, and three of each 2x2 reproject last frame's where
// the same surface showed there; the fourth recomputes, a different one each frame. With the camera still four frames,
// every pixel has been recomputed under it: the pass and its copy are skipped and the lit pass reads the targets as they
// stand. --no-incident-reuse: every pixel every frame; --ab increuse alternates the two.
//
// The rest was the texels' (resolved and still), and nothing reports one now (incidentLightResting): the reuse and the
// skips do not engage. The incident pass goes with the clipmap; MarkIncidentDirty's events stay the traced shadows' keep
// rule too (RtShadowKeep).
internal sealed partial class SponzaLoop
{
    private GraphResourceHandle incidentPrevHandle, incidentGradientPrevHandle, reuseStandInFloat, reuseStandInUint;
    private PassHandle incidentHistoryPassHandle;
    private PipelineHandle incidentHistoryPipeline;
    private bool incidentReuse = true;
    // The frame the incident history last matched the targets (copied, or both left standing); the frame the light the
    // pass reads last changed (a probe solve, a moved light or object).
    private long incidentHistoryFrame = -10, incidentDirtyFrame = -10;
    private Matrix4x4 incidentLastView, incidentLastProjection;
    private int incidentStillFrames;
    // The frame whose surface keys the history holds (RenderFrame records the copy only for a reader).
    private long surfaceKeyHistoryFrame = -10;
    private long incidentSkipped, incidentReused, incidentFull, incidentDirtyFrames;
    private string IncidentReuseCounts() { var t = $"incident: skipped {incidentSkipped}, reused {incidentReused}, full {incidentFull} frames (dirty {incidentDirtyFrames}); shadow masks kept {rtShadowKept}, interleaved {rtShadowInterleaved} (history {rtShadowInterleavedHistory})"; incidentSkipped = incidentReused = incidentFull = incidentDirtyFrames = 0; rtShadowKept = rtShadowInterleaved = rtShadowInterleavedHistory = 0; return t; }

    private GraphicsPassBuilder IncidentReuseReads(GraphicsPassBuilder b)
    {
        b = b.ReadHistory(incidentPrevHandle).ReadHistory(incidentGradientPrevHandle);
        return SurfaceTargets
            ? b.Read(velocityHandle).Read(surfaceKeyHandle).ReadHistory(surfaceKeyHistoryHandle)
            : b.Read(reuseStandInFloat).Read(reuseStandInUint);
    }

    private bool IncidentReuseOn => incidentReuse && !(abMode == "increuse" && AbOffPhase);
    // Whether the light the incident pass reads is at rest (see above): never, since the texels went.
    private readonly bool incidentLightResting = false;

    // Whether this frame's incident pass may reuse last frame's: everything it reads at rest, last frame's answer kept.
    private bool IncidentReuseNow(bool half) =>
        IncidentReuseOn && SurfaceTargets && !half && incidentLightResting && !MoverActive
        && framesRendered - incidentDirtyFrame > 1 && incidentHistoryFrame == framesRendered - 1 && surfaceKeyHistoryFrame == framesRendered - 1;

    // The reuse textures and uniform for RecordIncidentClipmap.
    private IEnumerable<ShaderTextureBinding> IncidentReuseTextures() => new[]
    {
        new ShaderTextureBinding("uReuseVelocity", graph.GetColorTexture(SurfaceTargets ? velocityHandle : reuseStandInFloat)),
        new ShaderTextureBinding("uReuseKey", graph.GetColorTexture(SurfaceTargets ? surfaceKeyHandle : reuseStandInUint)),
        new ShaderTextureBinding("uReuseKeyPrev", graph.GetColorTexture(SurfaceTargets ? surfaceKeyHistoryHandle : reuseStandInUint)),
        new ShaderTextureBinding("uIncidentPrev", graph.GetColorTexture(incidentPrevHandle)),
        new ShaderTextureBinding("uIncidentGradientPrev", graph.GetColorTexture(incidentGradientPrevHandle)),
    };

    private Vector4 IncidentReuseUniform(bool half)
    {
        if (IncidentReuseNow(half)) { incidentReused++; return new Vector4(1f, framesRendered % 4, 0f, 0f); }
        incidentFull++;
        return Vector4.Zero;
    }

    // Before recording the full-res pass: false when it can be skipped this frame (the camera still four frames, every
    // pixel recomputed under it, nothing it reads changed). Counts the frames either way.
    private bool IncidentPassNeeded()
    {
        var same = cameraView == incidentLastView && cameraProjection == incidentLastProjection;
        incidentLastView = cameraView;
        incidentLastProjection = cameraProjection;
        if (same && incidentStillFrames >= 4 && IncidentReuseNow(half: false))
        {
            incidentHistoryFrame = framesRendered;   // the history still matches the targets: neither was written
            incidentSkipped++;
            return false;
        }
        if (!same || !IncidentReuseNow(half: false)) { incidentStillFrames = 0; return true; }
        incidentStillFrames++;
        return true;
    }

    // The half-res pass and its upsample, as IncidentPassNeeded: skipped with the camera still four frames and nothing they
    // read changed -- the upsample writes the targets the lit pass reads, which hold its last answer. No TAA only: with it,
    // the 2x2's sampled pixel turns each frame. Without this, a still frame paid the pass, the upsample and the mark,
    // ~10-12 ms where the full-res frame rested at 8.3 (2026-10-24).
    private bool IncidentHalfPassNeeded()
    {
        var same = cameraView == incidentLastView && cameraProjection == incidentLastProjection;
        incidentLastView = cameraView;
        incidentLastProjection = cameraProjection;
        var restable = IncidentReuseOn && render.Taa <= 0f && incidentLightResting && !MoverActive && framesRendered - incidentDirtyFrame > 1;
        if (!same || !restable) { incidentStillFrames = 0; return true; }
        if (incidentStillFrames >= 4) { incidentSkipped++; return false; }
        incidentStillFrames++;
        return true;
    }

    // After the full-res incident pass: its answer becomes the next frame's history.
    private void RecordIncidentHistory()
    {
        graph.Pass(incidentHistoryPassHandle, scope => fullscreen.Draw(scope, incidentHistoryPipeline, new[]
        {
            new ShaderTextureBinding("uIncident", graph.GetColorTexture(incidentHandle)),
            new ShaderTextureBinding("uGradient", graph.GetColorTexture(incidentGradientHandle)),
        }));
        incidentHistoryFrame = framesRendered;
    }

    // What the incident pass reads changed this frame: no reuse this frame or the next.
    private void MarkIncidentDirty() { if (incidentDirtyFrame != framesRendered) incidentDirtyFrames++; incidentDirtyFrame = framesRendered; }
}

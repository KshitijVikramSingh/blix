using System.Diagnostics;
using Blix.Core;
using Blix.Graphics;
using ImGuiNET;

namespace Blix.Demos.VulkanSponza;

// The cook screen: while the transport cook runs in the background the scene is not rendered at all -- the frame
// does only what the cook waits on (the ray surfaces' bake, the bake scene and the frame's tracer being built, the
// cook's landing) and clears -- at CookFrameCap frames a second, and a window over it shows the cook's progress (the
// Logs tab's section: stages, time left, memory). The cook had the GPU and the CPU to share with full frames of
// traced shadows and GI: each of its Metal round trips waited behind a frame's passes (Sponza's couplings waited
// 35 s on Metal for 5 s of GPU work, 2026-10-13). --no-cook-screen renders as before.
internal sealed partial class SponzaLoop : IUiSource
{
    private const double CookFrameCap = 10;
    private bool cookScreenOff;
    private readonly Stopwatch cookFrameClock = new();

    public string UiName => "vulkan-sponza";

    // A cook on: the transport's (in the background), the split light's (its sweep and refining rounds), or the sun
    // records' (lightlets). The records' bake beside full frames waited on Metal behind each frame's traced shadows: 43%
    // of its thread in LightletVisibility / LightletRings, 25 min for 347 s of GPU work (2026-10-14).
    private bool SplitCookRunning => splitLight && texelsUploaded && sunNodeStage is not (SunNodeStage.Off or SunNodeStage.Done);
    // A split cook with light already up (a relight) runs live, the old light giving way as it lands (SplitLanding).
    // The split light's cook never holds the screen: it runs live (SponzaLoop.SplitLanding.cs), at boot too -- the probes
    // light the scene until its texels land, what is on screen first. The transport cook and the sun records' bake do:
    // their Metal round trips waited behind every full frame (35 s for 5 s of GPU; 25 min for 6).
    private bool CookScreenNow => !cookScreenOff && (transportCookTask is { IsCompleted: false } || LightletBakePending);

    // The cook screen's frame (RenderFrame, while CookScreenNow): the cook's needs, a cleared screen, the cap.
    // The split cook's sweep of Sponza's 14.8M texels is ~3,800 frames: at full frames (3 fps) ~1,600 s. The cap is
    // for the transport cook alone (its work is off the frame).
    // Vsync is off while the cook screen is up (each frame carries a split-cook pass: one waiting on the display's
    // refresh held the cook to it), and back on after (RestoreVsyncAfterCook).
    private bool cookTurnedVsyncOff;
    // A scene frame has been drawn and presented: the cook screen shows it rather than clearing (RecordCookFrame).
    private bool sceneFrameDrawn;
    // The progress window was up last frame: a cook that starts opens it (DrawUi).
    private bool progressWindowShown;

    // A vsync change recreates the swapchain and drops the frame recorded beside it (VulkanGraphicsDevice), so that frame
    // is a cleared screen alone: a split-cook pass or the compose in it would be lost (the compose was: every texel's
    // light left zero, 2026-10-13).
    private bool RestoreVsyncAfterCook(RenderCommandList commandList)
    {
        if (!cookTurnedVsyncOff || CookScreenNow) return false;
        cookTurnedVsyncOff = false;
        device.VsyncEnabled = true;
        RecordClearedFrame(commandList);
        return true;
    }

    private void RecordClearedFrame(RenderCommandList commandList)
    {
        graph.Pass(litPassHandle, _ => { }, clearColor: new GraphicsColor(0.03f, 0.04f, 0.06f, 1f));
        graph.Execute(commandList);
        RecordPresentPass(commandList);
    }

    private void RecordCookFrame(RenderCommandList commandList)
    {
        // Vsync stays as it is: it went off here so the split cook's in-frame passes were not held to the display, and the
        // split cook no longer runs behind this screen -- while the toggle's recreate threw away the last image the
        // screen now keeps showing.
        RecordRaySurfaceBake();
        UpdateBakeScene();
        EnsureFrameTracer();
        CookTransportWhenReady();
        // A sun relight that lands while the screen is up is taken here (its patches' light, the split's new sun part):
        // the split cook waits for no relight in flight (BeginSunNodes), and only FollowSun clears one. Without it a sun
        // moved twice left the cook at Waiting for good (2026-10-13).
        if (texelsUploaded) FollowSun();
        // The sun records' bake starts on a tracer and lands here as it does in RecordTexels.
        if (texelsUploaded) LightletBakeWhenReady();
        // Up to SunNodePassCopies of the split cook's passes a frame, each in its own copy of the passes (a frame-graph
        // Metal pass takes one piece a frame), stopping at a slice's readback (it needs the frame's GPU work done).
        var splitCooking = SplitCookRunning && rayBlockBuffers.Length > 0;
        if (splitCooking)
        {
            for (sunNodeCopy = 0; sunNodeCopy < sunNodePassesPerFrame; sunNodeCopy++)
            {
                RecordSunNodes();
                if (sunNodeStage != SunNodeStage.Tracing || sunNodeReadback) break;
            }
            sunNodeCopy = 0;
            RecordSplitCompose();
        }
        // The last image the scene drew stays up under the cook window (the present pass reads TAA's history, or the HDR
        // target, neither touched here): the user watched streamed geometry and textures vanish for the cook, which needs
        // the GPU, not the screen. Before any scene frame, a cleared screen.
        if (!sceneFrameDrawn) graph.Pass(litPassHandle, _ => { }, clearColor: new GraphicsColor(0.03f, 0.04f, 0.06f, 1f));
        graph.Execute(commandList);
        RecordPresentPass(commandList);
        var budgetMs = 1000.0 / CookFrameCap;
        if (!splitCooking && cookFrameClock.IsRunning && cookFrameClock.Elapsed.TotalMilliseconds < budgetMs)
            Thread.Sleep(TimeSpan.FromMilliseconds(budgetMs - cookFrameClock.Elapsed.TotalMilliseconds));
        cookFrameClock.Restart();
    }

    // The progress window: while the scene loads, its textures stream, or a cook runs -- the engine's jobs
    // (DebugProgress: load, stream, cook) and the machine's memory.
    public void DrawUi()
    {
        ReportCookProgress();
        var loading = !sceneLoaded;
        var streaming = sceneLoaded && !fullyLoaded;
        var cooking = CookScreenNow;
        // A live cook (the split light's, at boot or after a sun move): the same window over the scene, collapsible, opened
        // again each time a cook starts and otherwise left as the user leaves it.
        var live = !loading && !streaming && !cooking && SplitCookRunning;
        var shown = loading || streaming || cooking || live;
        var opened = shown && !progressWindowShown;
        progressWindowShown = shown;
        if (!shown) return;
        var io = ImGui.GetIO();
        ImGui.SetNextWindowPos(io.DisplaySize * 0.5f, ImGuiCond.Always, new System.Numerics.Vector2(0.5f, 0.5f));
        ImGui.SetNextWindowSize(new System.Numerics.Vector2(MathF.Min(760f, io.DisplaySize.X - 32f), 0f), ImGuiCond.Always);
        if (opened) ImGui.SetNextWindowCollapsed(false, ImGuiCond.Always);
        var title = loading ? "Loading" : streaming ? "Streaming textures" : "Cooking";
        var flags = ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoSavedSettings;
        if (!live) flags |= ImGuiWindowFlags.NoCollapse;
        if (!ImGui.Begin(title + "##progress-screen", flags))
        {
            ImGui.End();
            return;
        }
        ImGui.TextUnformatted(loading ? "Loading the scene." : streaming ? "Streaming the scene's textures; drawn flat until they are in."
            : live ? "Cooking the scene's light -- the scene stays up, its light landing as it is cooked, what is on screen first."
            : "Cooking the scene's light -- the scene is not drawn meanwhile (--no-cook-screen draws it).");
        ImGui.Separator();
        if (!Blix.Diagnostics.Overlay.DebugProgressUi.Draw(runningOnly: false)) ImGui.TextDisabled("  starting");
        ImGui.Separator();
        DrawMemory();
        ImGui.End();
    }
}

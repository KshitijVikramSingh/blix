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

    // A cook on: the transport's (in the background), or the split light's (its sweep and refining rounds).
    private bool SplitCookRunning => splitLight && texelsUploaded && sunNodeStage is not (SunNodeStage.Off or SunNodeStage.Done);
    private bool CookScreenNow => !cookScreenOff && (transportCookTask is { IsCompleted: false } || SplitCookRunning);

    // The cook screen's frame (RenderFrame, while CookScreenNow): the cook's needs, a cleared screen, the cap.
    // The split cook's sweep of Sponza's 14.8M texels is ~3,800 frames: at full frames (3 fps) ~1,600 s. The cap is
    // for the transport cook alone (its work is off the frame).
    // Vsync is off while the cook screen is up (each frame carries a split-cook pass: one waiting on the display's
    // refresh held the cook to it), and back on after (RestoreVsyncAfterCook).
    private bool cookTurnedVsyncOff;

    private void RestoreVsyncAfterCook()
    {
        if (!cookTurnedVsyncOff || CookScreenNow) return;
        cookTurnedVsyncOff = false;
        device.VsyncEnabled = true;
    }

    private void RecordCookFrame(RenderCommandList commandList)
    {
        if (device.VsyncEnabled)
        {
            device.VsyncEnabled = false;
            cookTurnedVsyncOff = true;
        }
        RecordRaySurfaceBake();
        UpdateBakeScene();
        EnsureFrameTracer();
        CookTransportWhenReady();
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
        graph.Pass(litPassHandle, _ => { }, clearColor: new GraphicsColor(0.03f, 0.04f, 0.06f, 1f));
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
        if (!loading && !streaming && !cooking) return;
        var io = ImGui.GetIO();
        ImGui.SetNextWindowPos(io.DisplaySize * 0.5f, ImGuiCond.Always, new System.Numerics.Vector2(0.5f, 0.5f));
        ImGui.SetNextWindowSize(new System.Numerics.Vector2(MathF.Min(760f, io.DisplaySize.X - 32f), 0f), ImGuiCond.Always);
        var title = loading ? "Loading" : streaming ? "Streaming textures" : "Cooking";
        ImGui.Begin(title + "##progress-screen", ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoSavedSettings);
        ImGui.TextUnformatted(loading ? "Loading the scene." : streaming ? "Streaming the scene's textures; drawn flat until they are in."
            : "Cooking the scene's light -- the scene is not drawn meanwhile (--no-cook-screen draws it).");
        ImGui.Separator();
        if (!Blix.Diagnostics.Overlay.DebugProgressUi.Draw(runningOnly: false)) ImGui.TextDisabled("  starting");
        ImGui.Separator();
        DrawMemory();
        ImGui.End();
    }
}

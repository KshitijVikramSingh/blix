using System.Diagnostics;
using Blix.Core;
using Blix.Graphics;
using ImGuiNET;

namespace Blix.Demos.VulkanSponza;

// The cook screen: while the frame's tracer is built, or the gate's reference wave traces (ReferenceCookingNow), the scene
// is not rendered at all -- the frame does only what that work waits on and clears (or keeps the last image) -- at
// CookFrameCap frames a second, and a window over it shows the progress (the engine's jobs, memory). Work like it had
// the GPU and the CPU to share with full frames of traced shadows and GI: each of its Metal round trips waited behind a
// frame's passes. The same window shows RTGI's settle ("Settling the light", DrawRtgiSettle) over the scene.
// --no-cook-screen renders as before.
internal sealed partial class SponzaLoop : IUiSource
{
    private const double CookFrameCap = 10;
    private bool cookScreenOff;
    private readonly Stopwatch cookFrameClock = new();

    public string UiName => "vulkan-sponza";

    // The frame's tracer, with traced shadows (EnsureFrameTracer, built during the cook screen's frames): before it the
    // frame ran on a fallback sun -- shadows from another direction, then a snap -- and the clipmap, the specular, cloth
    // and foliage held that light for hundreds of solves after (the user, Crytek Sponza, 2026-10-26).
    private bool TracerPending => rtShadows && frameTracer is null && !frameTracerFailed;
    private bool CookScreenNow => !cookScreenOff && (ReferenceCookingNow || TracerPending);

    // A scene frame has been drawn and presented: the cook screen shows it rather than clearing (RecordCookFrame).
    private bool sceneFrameDrawn;
    // The progress window was up last frame: a cook that starts opens it (DrawUi).
    private bool progressWindowShown;

    // The cook screen's frame (RenderFrame, while CookScreenNow): the cook's needs, the last image or a cleared screen, the cap.
    private void RecordCookFrame(RenderCommandList commandList)
    {
        RecordRaySurfaceBake();
        EnsureFrameTracer();
        // The last image the scene drew stays up under the cook window (the present pass reads TAA's history, or the HDR
        // target, neither touched here): the user watched streamed geometry and textures vanish for the cook, which needs
        // the GPU, not the screen. Before any scene frame, a cleared screen.
        // The gate's reference wave, a step a frame with the GPU to itself (ReferenceCookingNow).
        if (ReferenceCookingNow)
        {
            if (!referenceCookClock.IsRunning) referenceCookClock.Start();
            RecordGpuReference();
            FlushTracerGroup();
        }
        if (!sceneFrameDrawn) graph.Pass(litPassHandle, _ => { }, clearColor: new GraphicsColor(0.03f, 0.04f, 0.06f, 1f));
        graph.Execute(commandList);
        RecordPresentPass(commandList);
        var budgetMs = 1000.0 / CookFrameCap;
        if (!ReferenceCookingNow && cookFrameClock.IsRunning && cookFrameClock.Elapsed.TotalMilliseconds < budgetMs)
            Thread.Sleep(TimeSpan.FromMilliseconds(budgetMs - cookFrameClock.Elapsed.TotalMilliseconds));
        cookFrameClock.Restart();
    }

    // The progress window: while the scene loads, its textures stream, the cook screen is up or RTGI settles -- the
    // engine's jobs (DebugProgress: load, stream, cook) and the machine's memory.
    public void DrawUi()
    {
        var loading = !sceneLoaded;
        var streaming = sceneLoaded && !fullyLoaded;
        var settling = RtgiSettleNow;
        var cooking = CookScreenNow || settling;
        var shown = loading || streaming || cooking;
        var opened = shown && !progressWindowShown;
        progressWindowShown = shown;
        if (!shown) return;
        var io = ImGui.GetIO();
        ImGui.SetNextWindowPos(io.DisplaySize * 0.5f, ImGuiCond.Always, new System.Numerics.Vector2(0.5f, 0.5f));
        ImGui.SetNextWindowSize(new System.Numerics.Vector2(MathF.Min(760f, io.DisplaySize.X - 32f), 0f), ImGuiCond.Always);
        if (opened) ImGui.SetNextWindowCollapsed(false, ImGuiCond.Always);
        var title = loading ? "Loading" : streaming ? "Streaming textures" : settling ? "Settling the light" : "Cooking";
        var flags = ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoCollapse;
        if (!ImGui.Begin(title + "##progress-screen", flags))
        {
            ImGui.End();
            return;
        }
        if (settling) DrawRtgiSettle();
        else ImGui.TextUnformatted(loading ? "Loading the scene." : streaming ? "Streaming the scene's textures; drawn flat until they are in."
            : ReferenceCookingNow ? "Tracing the reference -- the scene is not drawn meanwhile (--ref-in-frame draws it)."
            : "Building the frame's ray tracer -- the scene is not drawn meanwhile (--no-cook-screen draws it).");
        // The gate's reference wave behind the screen: how far through its frames, and how long it has taken.
        if (ReferenceCookingNow && gateWave is { } rw)
        {
            var frames = ReferenceWaveFrames(rw);
            ImGui.ProgressBar(rw.Dispatch / (float)frames, new System.Numerics.Vector2(-1f, 0f),
                string.Create(System.Globalization.CultureInfo.InvariantCulture, $"reference: {rw.Dispatch:N0} of {frames:N0} steps, {referenceCookClock.Elapsed.TotalSeconds:0.0} s"));
        }
        ImGui.Separator();
        if (!Blix.Diagnostics.Overlay.DebugProgressUi.Draw(runningOnly: false)) ImGui.TextDisabled("  starting");
        ImGui.Separator();
        DrawMemory();
        ImGui.End();
    }
}

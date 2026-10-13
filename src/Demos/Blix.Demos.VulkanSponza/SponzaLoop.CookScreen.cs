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

    private bool CookScreenNow => !cookScreenOff && transportCookTask is { IsCompleted: false };

    // The cook screen's frame (RenderFrame, while CookScreenNow): the cook's needs, a cleared screen, the cap.
    private void RecordCookFrame(RenderCommandList commandList)
    {
        RecordRaySurfaceBake();
        UpdateBakeScene();
        EnsureFrameTracer();
        CookTransportWhenReady();
        graph.Pass(litPassHandle, _ => { }, clearColor: new GraphicsColor(0.03f, 0.04f, 0.06f, 1f));
        graph.Execute(commandList);
        RecordPresentPass(commandList);
        var budgetMs = 1000.0 / CookFrameCap;
        if (cookFrameClock.IsRunning && cookFrameClock.Elapsed.TotalMilliseconds < budgetMs)
            Thread.Sleep(TimeSpan.FromMilliseconds(budgetMs - cookFrameClock.Elapsed.TotalMilliseconds));
        cookFrameClock.Restart();
    }

    public void DrawUi()
    {
        if (!CookScreenNow) return;
        var io = ImGui.GetIO();
        ImGui.SetNextWindowPos(io.DisplaySize * 0.5f, ImGuiCond.Always, new System.Numerics.Vector2(0.5f, 0.5f));
        ImGui.SetNextWindowSize(new System.Numerics.Vector2(MathF.Min(720f, io.DisplaySize.X - 32f), 0f), ImGuiCond.Always);
        ImGui.Begin("Cooking##cook-screen", ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoSavedSettings);
        ImGui.TextUnformatted("Cooking the scene's light transport -- the scene is not drawn meanwhile (--no-cook-screen draws it).");
        ImGui.Separator();
        OnLogsSection();
        ImGui.End();
    }
}

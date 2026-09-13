using Blix;
using Blix.Core;
using Blix.Graphics;
using Blix.Runtime.Silk;
using ImGuiNET;
using System.Numerics;

namespace Blix.Demos.Chassis;

// An executable spec for the application chassis, and the smallest one that can exist.
//
// It is here to answer two questions the rest of the tree cannot, because every other
// application that wants an interface is also a diagnostics producer:
//
//   1. Does an application get a UI WITHOUT being IDebuggable? Until §8 the answer was
//      no — `gameLoop is IDebuggable` decided whether ImGui was created at all, so
//      "produces diagnostics" and "may have an interface" were the same question. This
//      loop is deliberately NOT IDebuggable. If the panel appears, they are separate.
//
//   2. Does UI capture actually suppress game input? VkImGuiRenderer has exposed
//      WantCaptureKeyboard since it was written and nothing read it, so typing into any
//      ImGui field also drove the game. Nothing caught it because the only interface
//      that existed was the diagnostics overlay, which has almost no text fields. This
//      loop counts every input event it receives and shows the counts next to a text
//      field, so the fix is checkable rather than asserted: type into the field and the
//      key counter must not move.
//
// It also renders, with no shaders of its own — a single clear pass whose colour walks
// with time, so "is it running" is answerable at a glance.
public static class Program
{
    public static void Main(string[] args)
    {
        var options = WindowOptions.FromArgs(args, WindowOptions.Default with
        {
            Title = "Blix — chassis",
            Width = 900,
            Height = 560,
        });

        var loop = new ChassisLoop();
        using var window = new Window(loop, options);
        window.Run();
    }
}

/// <summary>The whole application. No diagnostics, its own interface, its own input.</summary>
internal sealed class ChassisLoop : IGameLoop, IUiSource, IInputHandler
{
    private int keyDowns;
    private int keyUps;
    private int mouseDowns;
    private int mouseUps;
    private int mouseMoves;
    private int wheels;
    private int frames;

    // Counts calls to DrawUi. The host only calls it inside an ImGui frame it has built, so a non-zero
    // count IS the answer to question 1 — and it is answerable from a log rather than from a pair of
    // eyes, which matters because a loop with no diagnostics has no overlay to report pass stats.
    private int uiFrames;
    private double now;

    private string typeHere = "click here and type";
    private int keysAtFocus = -1;

    public string UiName => "chassis";

    public void OnRender(Time time, RenderFrameContext frame, RenderCommandList commandList)
    {
        frames++;
        now = time.Total;

        // A clear pass and nothing else: no pipeline, no vertex buffer, no shader. The
        // colour walks so a still screen is distinguishable from a stopped one.
        var tint = (float)(Math.Sin(now * 0.6) * 0.5 + 0.5) * 0.10f;
        commandList.Pass(
            "clear",
            new RenderPassDescription(
                Target: RenderSurfaceHandle.Default,
                ClearColors: new GraphicsColor?[] { new(0.05f + tint, 0.06f + tint * 0.5f, 0.09f, 1f) },
                ClearDepth: false),
            _ => { });
    }

    public void DrawUi()
    {
        uiFrames++;
        ImGui.SetNextWindowSize(new Vector2(430, 260), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowPos(new Vector2(24, 24), ImGuiCond.FirstUseEver);
        if (!ImGui.Begin("chassis"))
        {
            ImGui.End();
            return;
        }

        ImGui.TextDisabled("no IDebuggable on this loop — this panel is the proof");
        ImGui.Separator();

        ImGui.Text($"frames {frames}   t {now:0.0}s");
        ImGui.Text($"keys   down {keyDowns}   up {keyUps}");
        ImGui.Text($"mouse  down {mouseDowns}   up {mouseUps}   moves {mouseMoves}   wheel {wheels}");
        ImGui.Separator();

        // The capture test, made checkable. Remember the game's key count when the field
        // takes focus; while it is focused that number must not move, however much is
        // typed. A release is expected to still arrive — see Window.OnKeyUp, where the
        // asymmetry is deliberate.
        ImGui.InputText("type here", ref typeHere, 96);
        if (ImGui.IsItemActivated()) keysAtFocus = keyDowns;
        if (ImGui.IsItemActive())
        {
            var leaked = keyDowns - keysAtFocus;
            if (leaked == 0) ImGui.TextColored(new Vector4(0.4f, 0.9f, 0.5f, 1f), "captured: 0 keys reached the game");
            else ImGui.TextColored(new Vector4(1f, 0.4f, 0.4f, 1f), $"LEAKING: {leaked} key(s) reached the game");
        }
        else if (keysAtFocus >= 0)
        {
            ImGui.TextDisabled("field not focused — keys go to the application, as they should");
        }

        ImGui.End();
    }

    public void OnUnload()
    {
        Console.WriteLine(
            uiFrames > 0
                ? $"chassis: UI drawn on {uiFrames}/{frames} frame(s) with NO IDebuggable on this loop."
                : $"chassis: UI NEVER DRAWN over {frames} frame(s) — the host built no ImGui frame.");
        Console.WriteLine(
            $"chassis: {frames} frame(s); input reaching the application — " +
            $"keys {keyDowns}/{keyUps}, mouse {mouseDowns}/{mouseUps}, moves {mouseMoves}, wheel {wheels}");
    }

    public void OnKeyDown(Key key) => keyDowns++;

    public void OnKeyUp(Key key) => keyUps++;

    public void OnMouseDown(MouseButton button) => mouseDowns++;

    public void OnMouseUp(MouseButton button) => mouseUps++;

    public void OnMouseMove(float x, float y, float dx, float dy) => mouseMoves++;

    public void OnMouseWheel(float dx, float dy) => wheels++;
}

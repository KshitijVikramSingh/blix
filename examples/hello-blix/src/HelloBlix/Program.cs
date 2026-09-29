using Blix;
using Blix.Core;
using Blix.Graphics;
using Blix.Runtime.Headless;
using Blix.Runtime.Silk;

namespace HelloBlix;

public static class Program
{
    public static int Main(string[] args) => BlixApps.Main(args);

    [BlixApp("hello", Summary = "the smallest owned Blix application", Headed = true, Default = true)]
    public static int Hello(AppArgs args)
    {
        var options = WindowOptions.FromArgs(args, WindowOptions.Default with
        {
            Title = "Hello Blix",
            Width = 960,
            Height = 560,
        });

        using var window = new Window(new HelloGame(), options);
        window.Run();
        return 0;
    }

    // The same game, run with no window: Space is pressed on a frame chosen by the command line,
    // and the check is that the game made the decision it owns.
    [BlixApp("hello-check", Summary = "run the game headless, press Space, check it turned warm")]
    public static int Check(int pressOn = 3)
    {
        var game = new HelloGame();
        new HeadlessHost(game, HeadlessOptions.Default with { ExitAfterFrames = pressOn + 2 }, (frame, input) =>
        {
            if (frame == pressOn) input.RecordKeyDown(Key.Space);
        }).Run();

        var colour = HelloGame.Background(game.Warm);
        var correct = game.Warm && colour.Red > colour.Blue;
        Console.WriteLine(correct
            ? "hello-check: Space turned the background warm"
            : "hello-check: Space was pressed and the background did not turn warm");
        return correct ? 0 : 1;
    }
}

internal sealed class HelloGame : Game
{
    public bool Warm { get; private set; }

    public override void OnUpdate(Time time)
    {
        if (Host.Input[Key.Space].Pressed)
            Warm = !Warm;

        if (Host.Input[Key.Escape].Pressed)
            Host.RequestClose();
    }

    public override void OnRender(
        Time time,
        RenderFrameContext frame,
        RenderCommandList commandList)
    {
        commandList.Pass(
            "background",
            new RenderPassDescription(
                Target: RenderSurfaceHandle.Default,
                ClearColors: new GraphicsColor?[] { Background(Warm) },
                ClearDepth: false),
            _ => { });
    }

    internal static GraphicsColor Background(bool warm) => warm
        ? new GraphicsColor(0.22f, 0.06f, 0.03f, 1f)
        : new GraphicsColor(0.03f, 0.07f, 0.18f, 1f);
}

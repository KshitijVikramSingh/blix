using Blix.Runtime.Silk;
using RTSGame.Debug;

namespace RTSGame;

public static class Program
{
    public static void Main(string[] args)
    {
        if (args.Contains("--selftest"))
        {
            Environment.Exit(SimulationSelfTests.Run());
        }

        if (args.Contains("--benchmark"))
        {
            Environment.Exit(MovementBenchmarks.Run());
        }

        if (args.Contains("--arrivaltest"))
        {
            Environment.Exit(SimulationSelfTests.RunSharedDestinationRegression());
        }

        if (args.Contains("--cornerlooptest"))
        {
            Environment.Exit(SimulationSelfTests.RunCornerCircuitDiagnostic());
        }

        if (args.Contains("--cornerlegtest"))
        {
            Environment.Exit(SimulationSelfTests.RunCornerCircuitDiagnostic(maximumLegs: 1));
        }

        if (args.Contains("--cornertwolegtest"))
        {
            Environment.Exit(SimulationSelfTests.RunCornerCircuitDiagnostic(maximumLegs: 2));
        }

        if (args.Contains("--cornerthreelegtest"))
        {
            Environment.Exit(SimulationSelfTests.RunCornerCircuitDiagnostic(maximumLegs: 3));
        }

        if (args.Contains("--terraincornertest"))
        {
            Environment.Exit(SimulationSelfTests.RunTerrainCornerDiagnostic());
        }

        if (args.Contains("--doorwaytest"))
        {
            Environment.Exit(SimulationSelfTests.RunDoorwayContentionDiagnostic());
        }

        if (args.Contains("--gatetest"))
        {
            Environment.Exit(SimulationSelfTests.RunSingleCellGateDiagnostic());
        }

        if (args.Contains("--yieldtest"))
        {
            Environment.Exit(SimulationSelfTests.RunIdleYieldDiagnostic());
        }

        var exitAfterFrames = 0;
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == "--frames" && int.TryParse(args[i + 1], out var count))
            {
                exitAfterFrames = count;
            }
        }

        var traceMovement = args.Contains("--trace-movement");
        var startTerrainLab = args.Contains("--terrain-lab");
        var debugAll = args.Contains("--debug-all");
        var game = new RtsGameLoop(exitAfterFrames, traceMovement, startTerrainLab, debugAll);
        using var window = new Window(game, new WindowOptions("RTSGame — Greybox Kingdom", 1280, 720));
        window.Run();
    }
}

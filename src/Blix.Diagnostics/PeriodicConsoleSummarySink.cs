using System.Numerics;

namespace Blix.Diagnostics;

// Prints a multi-line digest of each Nth frame to a TextWriter (stdout by
// default). Companion to ConsoleEventSink — events go to stderr at warn+,
// PeriodicConsoleSummarySink dumps the live Values + Stats + Timers + GPU
// pass timings to stdout at a configurable cadence. Lets a CLI-only run
// of a Vulkan demo (no ImGui overlay yet) still answer "what state is
// the engine in this frame."
//
// Output shape:
//
//   [diag] f120 t=2.00s
//     timers: frame=16.65ms execute=0.31ms swap=15.42ms
//     values:
//       vulkan-cube/light/direction: <0.55, 1.00, 0.45>
//       vulkan-cube/cube/rotY: 1.612
//       vulkan-cube/camera/position: <2.50, 1.80, 3.50>
//     stats:
//       draws: 1
//       passes/cube/draws: 1
//     gpu/passes:
//       cube: 0.123ms
public sealed class PeriodicConsoleSummarySink : IDebugFrameSink
{
    private readonly int intervalFrames;
    private readonly TextWriter writer;
    private int lastPrintedFrame;

    public PeriodicConsoleSummarySink(int intervalFrames = 60, TextWriter? writer = null)
    {
        if (intervalFrames < 1) throw new ArgumentOutOfRangeException(nameof(intervalFrames));
        this.intervalFrames = intervalFrames;
        this.writer = writer ?? Console.Out;
    }

    public void Consume(DebugFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (frame.Number - lastPrintedFrame < intervalFrames) return;
        lastPrintedFrame = frame.Number;
        PrintSummary(frame);
    }

    private void PrintSummary(DebugFrame frame)
    {
        writer.WriteLine($"[diag] f{frame.Number} t={frame.WallClockMs / 1000.0:F2}s");

        // Phase timers in canonical order. Skip ones the runtime didn't emit
        // so the line stays clean on minimal demos.
        var phasePaths = new[] { "frame", "build-commands", "execute", "overlay", "swap", "run-debuggables" };
        var phaseParts = new List<string>(phasePaths.Length);
        foreach (var path in phasePaths)
        {
            var t = FindTimer(frame, path);
            if (t is null) continue;
            phaseParts.Add($"{path}={t.TotalMs:F2}ms");
        }
        if (phaseParts.Count > 0)
        {
            writer.WriteLine($"  timers: {string.Join(" ", phaseParts)}");
        }

        if (frame.Values.Count > 0)
        {
            writer.WriteLine("  values:");
            foreach (var v in frame.Values)
            {
                writer.WriteLine($"    {v.Path}: {FormatValue(v.Value)}");
            }
        }

        if (frame.Stats.Count > 0)
        {
            writer.WriteLine("  stats:");
            foreach (var s in frame.Stats)
            {
                writer.WriteLine($"    {s.Path}: {s.Value:F0} ({s.Kind})");
            }
        }

        // GPU pass timings (drained by the runtime from VkQueryPool /
        // glQueryCounter) live under scope "gpu/passes". Surface them
        // separately so they're not lost in the bulk timer list.
        var gpuTimings = new List<DebugTimerEntry>();
        for (var i = 0; i < frame.Timers.Count; i++)
        {
            if (frame.Timers[i].Scope == "gpu/passes") gpuTimings.Add(frame.Timers[i]);
        }
        if (gpuTimings.Count > 0)
        {
            writer.WriteLine("  gpu/passes:");
            foreach (var t in gpuTimings)
            {
                writer.WriteLine($"    {t.Name}: {t.TotalMs:F3}ms");
            }
        }
    }

    private static DebugTimerEntry? FindTimer(DebugFrame frame, string path)
    {
        for (var i = 0; i < frame.Timers.Count; i++)
        {
            if (frame.Timers[i].Path == path) return frame.Timers[i];
        }
        return null;
    }

    private static string FormatValue(object? v)
    {
        return v switch
        {
            null => "<null>",
            Vector2 v2 => $"<{v2.X:F2}, {v2.Y:F2}>",
            Vector3 v3 => $"<{v3.X:F2}, {v3.Y:F2}, {v3.Z:F2}>",
            Vector4 v4 => $"<{v4.X:F2}, {v4.Y:F2}, {v4.Z:F2}, {v4.W:F2}>",
            float f => f.ToString("F3"),
            double d => d.ToString("F3"),
            _ => v.ToString() ?? "<null>",
        };
    }
}

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Blix.Diagnostics.Overlay;
using ImGuiNET;

namespace Blix.Demos.VulkanSponza;

// Sponza's section of the overlay's Logs tab (IDebugLogsSection): each cook's progress and time left, and the
// machine's memory -- what a long Sponza run is watched for (the old gather's light bake drove a 16 GB machine to
// critical memory pressure, 2026-10-13). The log itself is the engine's (DebugLog).
//   Progress: the CPU stages that report through Progress* (the transport cook's couplings, the hashed texels, the
//   exposure diagnostics), and the GPU cooks read from their own counters (the split cook's sweep and refine rounds,
//   the old gather's light bake).
//   Memory: this process's resident size, the GPU's allocation (Metal's count, every client's), the kernel's memory
//   pressure level and swap in use; read once a second.
internal sealed partial class SponzaLoop : IDebugLogsSection
{
    private sealed class CookTask
    {
        public long Done, Total;
        public readonly Stopwatch Clock = Stopwatch.StartNew();
        public double? EndedSeconds;
    }

    private readonly ConcurrentDictionary<string, CookTask> cookTasks = new(StringComparer.Ordinal);
    private readonly List<string> cookTaskOrder = new();

    /// <summary>A CPU stage starts (thread-safe): <paramref name="total"/> units, 0 when unknown.</summary>
    private void ProgressBegin(string name, long total)
    {
        cookTasks[name] = new CookTask { Total = total };
        lock (cookTaskOrder) { cookTaskOrder.Remove(name); cookTaskOrder.Add(name); }
    }

    private void ProgressAdd(string name, long units = 1)
    {
        if (cookTasks.TryGetValue(name, out var task)) Interlocked.Add(ref task.Done, units);
    }

    private void ProgressEnd(string name)
    {
        if (cookTasks.TryGetValue(name, out var task)) task.EndedSeconds = task.Clock.Elapsed.TotalSeconds;
    }

    private readonly Stopwatch memoryClock = new();
    private string memoryLine = "", pressureLine = "";
    private bool pressureHigh;

    public void OnLogsSection()
    {
        ImGui.TextDisabled("progress");
        var any = false;
        // The GPU cooks, from their own counters.
        if (sunNodeStage is not (SunNodeStage.Off or SunNodeStage.Done) && sunNodeSuns is not null)
        {
            any = true;
            if (sunNodeStage == SunNodeStage.Tracing)
            {
                var refining = sunNodeRefineRound > 0;
                var live = refining ? sunNodeRefineIds.Length : sunNodeTraceIds.Length;
                var passes = refining ? RefinePasses : sunNodeRays / 32;
                var slice = Math.Min(sunNodeSlice, Math.Max(0, live - sunNodeFirst));
                var done = live > 0 ? (sunNodeFirst + (double)sunNodePass / passes * slice) / live : 0;
                Bar(refining ? $"split cook, refine round {sunNodeRefineRound}" : "split cook, sweep", done,
                    $"{sunNodeFirst:N0} of {live:N0} texels", sunNodeClock.Elapsed.TotalSeconds, finished: false);
            }
            else ImGui.Text($"split cook: {sunNodeStage} ({sunNodeClock.Elapsed.TotalSeconds:0} s)");
        }
        if (lightBakeTotal > 0 && lightBakeIssued > 0 && lightBakeIssued < lightBakeTotal)
        {
            any = true;
            Bar("light bake (the old gather)", (double)lightBakeIssued / lightBakeTotal, $"{lightBakeIssued:N0} of {lightBakeTotal:N0} texel passes", null, finished: false);
        }
        // The CPU stages, newest last; finished ones stay listed with their time.
        string[] order;
        lock (cookTaskOrder) order = cookTaskOrder.ToArray();
        foreach (var name in order)
        {
            if (!cookTasks.TryGetValue(name, out var task)) continue;
            any = true;
            var done = Interlocked.Read(ref task.Done);
            var fraction = task.Total > 0 ? Math.Min(1.0, (double)done / task.Total) : task.EndedSeconds is null ? 0 : 1;
            Bar(name, fraction, task.Total > 0 ? $"{done:N0} of {task.Total:N0}" : "", task.EndedSeconds ?? task.Clock.Elapsed.TotalSeconds,
                finished: task.EndedSeconds is not null);
        }
        if (!any) ImGui.TextDisabled("  nothing cooking");

        ImGui.Separator();
        ImGui.TextDisabled("memory");
        if (!memoryClock.IsRunning || memoryClock.Elapsed.TotalSeconds >= 1)
        {
            memoryClock.Restart();
            ReadMemory();
        }
        ImGui.TextUnformatted(memoryLine);
        if (pressureHigh) ImGui.TextColored(new System.Numerics.Vector4(1f, 0.45f, 0.45f, 1f), pressureLine);
        else ImGui.TextUnformatted(pressureLine);
    }

    // One progress row: a bar, what it counts, and elapsed and left (from the rate so far).
    private static void Bar(string name, double fraction, string counts, double? seconds, bool finished)
    {
        var left = !finished && seconds is { } s && fraction > 0.01 ? s * (1 - fraction) / fraction : (double?)null;
        var time = seconds is { } e
            ? finished ? $"{e:0} s" : left is { } l ? $"{e:0} s, ~{l:0} s left" : $"{e:0} s"
            : "";
        ImGui.ProgressBar((float)fraction, new System.Numerics.Vector2(160f, 0f), $"{fraction * 100:0}%");
        ImGui.SameLine();
        ImGui.TextUnformatted($"{name}  {counts}  {time}");
    }

    private void ReadMemory()
    {
        var rss = Process.GetCurrentProcess().WorkingSet64 / 1048576.0;
        var gpu = frameTracer is { } tracer ? tracer.Memory : (AllocatedMb: 0.0, RecommendedMb: 0.0);
        memoryLine = gpu.RecommendedMb > 0
            ? $"process {rss:N0} MB; GPU {gpu.AllocatedMb:N0} of {gpu.RecommendedMb:N0} MB recommended"
            : $"process {rss:N0} MB";
        var level = SysctlInt("kern.memorystatus_vm_pressure_level");
        var swap = SwapUsage();
        var levelName = level switch { 1 => "normal", 2 => "warning", 4 => "critical", _ => $"level {level}" };
        pressureHigh = level >= 2;
        pressureLine = $"pressure {levelName}" + (swap is { } sw ? $"; swap {sw.Used / 1048576.0:N0} of {sw.Total / 1048576.0:N0} MB" : "");
    }

    [DllImport("libc", EntryPoint = "sysctlbyname")]
    private static extern int SysctlInt(string name, ref int value, ref nint length, nint newValue, nint newLength);

    [DllImport("libc", EntryPoint = "sysctlbyname")]
    private static extern int SysctlWords(string name, [Out] ulong[] value, ref nint length, nint newValue, nint newLength);

    private static int SysctlInt(string name)
    {
        if (!OperatingSystem.IsMacOS()) return 0;
        var value = 0;
        nint length = sizeof(int);
        return SysctlInt(name, ref value, ref length, 0, 0) == 0 ? value : 0;
    }

    // vm.swapusage: struct xsw_usage { u64 total, avail, used; u32 pagesize; bool encrypted; } (32 bytes).
    private static (ulong Total, ulong Used)? SwapUsage()
    {
        if (!OperatingSystem.IsMacOS()) return null;
        var words = new ulong[4];
        nint length = 32;
        if (SysctlWords("vm.swapusage", words, ref length, 0, 0) != 0) return null;
        return (words[0], words[2]);
    }
}

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Blix.Diagnostics;
using Blix.Diagnostics.Overlay;
using ImGuiNET;

namespace Blix.Demos.VulkanSponza;

// Sponza's section of the overlay's Logs tab (IDebugLogsSection): each cook's progress and time left, and the
// machine's memory -- what a long Sponza run is watched for (the old gather's light bake drove a 16 GB machine to
// critical memory pressure, 2026-10-13). The log itself is the engine's (DebugLog).
//   Progress: the CPU stages that report through Progress* (the transport cook's couplings, the hashed texels, the
//   exposure diagnostics), and the GPU cooks read from their own counters (the split cook's sweep and refine rounds,
//   the old gather's light bake).
//   Memory: this process's resident size, the GPU's allocation (Metal's count, every client's), what is available, and
//   whether pages are going to swap now; read once a second. The verdict is ours, not the kernel's pressure level: the
//   kernel sits at "warning" whenever free pages are low and anything is compressed or swapped -- other apps' old pages
//   included -- so it read "warning" through whole runs that pressed on nothing (the swap total falling, not rising).
//   Red: pages going out to swap now (or the kernel at critical). Yellow: little left available. The kernel's level
//   stays on the line, dimmed.
internal sealed partial class SponzaLoop : IDebugLogsSection
{
    // The cook's CPU stages report through the engine's registry (DebugProgress), the Logs tab and the cook screen
    // drawing them; the "cook" group.
    // BLIX_STAGE_TIMES=1: each stage's wall time logged at its end (the boot's timeline, 2026-10-16).
    private static readonly bool stageTimes = Environment.GetEnvironmentVariable("BLIX_STAGE_TIMES") == "1";
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, long> stageStarts = new();
    private static void ProgressBegin(string name, long total)
    {
        if (stageTimes) stageStarts[name] = System.Diagnostics.Stopwatch.GetTimestamp();
        DebugProgress.Begin(name, total, "cook");
    }
    private static void ProgressAdd(string name, long units = 1) => DebugProgress.Add(name, units);
    private static void ProgressEnd(string name)
    {
        if (stageTimes && stageStarts.TryRemove(name, out var t0))
            Console.WriteLine($"[stage] {name}: {System.Diagnostics.Stopwatch.GetElapsedTime(t0).TotalSeconds:0.00} s");
        DebugProgress.End(name);
    }

    // Each frame: the GPU cooks' own counters into the registry (the split cook's sweep and rounds, the light bake).
    private string? splitProgressName;
    private void ReportCookProgress()
    {
        if (sunNodeStage is not (SunNodeStage.Off or SunNodeStage.Done) && sunNodeSuns is not null)
        {
            var refining = sunNodeRefineRound > 0;
            var name = sunNodeStage != SunNodeStage.Tracing ? $"split cook: {sunNodeStage}"
                : (fineGathering ? "light change, 2 of 2 (fine texels): " : TransferOn ? "light change, 1 of 2 (coarse texels): " : "split cook: ")
                  + (refining ? $"refining round {sunNodeRefineRound}" : "sweep");
            if (splitProgressName != name && splitProgressName is not null) DebugProgress.End(splitProgressName);
            splitProgressName = name;
            if (sunNodeStage == SunNodeStage.Tracing)
            {
                var live = refining ? sunNodeRefineIds.Length : sunNodeTraceIds.Length;
                var passes = refining ? RefinePasses : sunNodeRays / 32;
                var slice = Math.Min(sunNodeSlice, Math.Max(0, live - sunNodeFirst));
                DebugProgress.Set(name, sunNodeFirst + (long)((double)sunNodePass / passes * slice), live);
            }
            else DebugProgress.Set(name, 0, 0);
        }
        else if (splitProgressName is not null)
        {
            DebugProgress.End(splitProgressName);
            splitProgressName = null;
        }
        if (lightBakeTotal > 0 && lightBakeIssued > 0 && lightBakeIssued < lightBakeTotal)
            DebugProgress.Set("light bake (the old gather)", lightBakeIssued, lightBakeTotal);
        else DebugProgress.End("light bake (the old gather)");
    }

    private readonly Stopwatch memoryClock = new();
    private string memoryLine = "", pressureLine = "", kernelLine = "";
    private int pressureVerdict;   // 0 fine, 1 tight (little available), 2 pressing (swapping out now)
    private (ulong SwapIns, ulong SwapOuts, long Ticks)? vmLast;
    private double swapInRate, swapOutRate;   // MB/s, smoothed over a few seconds

    public void OnLogsSection() => DrawMemory();

    // The machine's memory, read once a second: this process's footprint, the GPU's allocation, pressure and swap.
    private void DrawMemory()
    {
        ImGui.TextDisabled("memory");
        if (!memoryClock.IsRunning || memoryClock.Elapsed.TotalSeconds >= 1)
        {
            memoryClock.Restart();
            ReadMemory();
        }
        ImGui.TextUnformatted(memoryLine);
        ImGui.SameLine();
        if (ImGui.SmallButton("inventory")) LogMemoryInventory("asked");
        if (pressureVerdict == 2) ImGui.TextColored(new System.Numerics.Vector4(1f, 0.45f, 0.45f, 1f), pressureLine);
        else if (pressureVerdict == 1) ImGui.TextColored(new System.Numerics.Vector4(1f, 0.85f, 0.35f, 1f), pressureLine);
        else ImGui.TextUnformatted(pressureLine);
        ImGui.SameLine();
        ImGui.TextDisabled(kernelLine);
    }

    private void ReadMemory()
    {
        // The kernel's footprint of the process (what Activity Monitor shows as its memory): .NET's working set on macOS
        // counted a fraction of it (169 MB beside 9 GB of GPU allocations, 2026-10-13).
        var rss = (OperatingSystem.IsMacOS() && ProcessFootprint() is { } bytes ? bytes : Process.GetCurrentProcess().WorkingSet64) / 1048576.0;
        var gpu = frameTracer is { } tracer ? tracer.Memory : (AllocatedMb: 0.0, RecommendedMb: 0.0);
        memoryLine = gpu.RecommendedMb > 0
            ? $"process {rss:N0} MB; GPU {gpu.AllocatedMb:N0} of {gpu.RecommendedMb:N0} MB recommended"
            : $"process {rss:N0} MB";
        var level = SysctlInt("kern.memorystatus_vm_pressure_level");
        var swap = SwapUsage();
        var levelName = level switch { 1 => "normal", 2 => "warning", 4 => "critical", _ => $"level {level}" };
        kernelLine = $"(kernel: {levelName})";
        var vm = VmStatistics();
        double? availableMb = null;
        if (vm is { } v)
        {
            var now = Stopwatch.GetTimestamp();
            if (vmLast is { } last && now > last.Ticks)
            {
                var seconds = (now - last.Ticks) / (double)Stopwatch.Frequency;
                // Smoothed over ~3 reads: one burst of a few pages does not turn the line red.
                swapInRate += ((double)(v.SwapIns - last.SwapIns) * v.PageBytes / 1048576.0 / seconds - swapInRate) * 0.4;
                swapOutRate += ((double)(v.SwapOuts - last.SwapOuts) * v.PageBytes / 1048576.0 / seconds - swapOutRate) * 0.4;
            }
            vmLast = (v.SwapIns, v.SwapOuts, now);
            availableMb = v.AvailablePages * (double)v.PageBytes / 1048576.0;
        }
        var totalMb = SysctlLong("hw.memsize") / 1048576.0;
        var pressing = level >= 4 || swapOutRate >= 4.0;
        var tight = availableMb is { } a && totalMb > 0 && a < 0.08 * totalMb;
        pressureVerdict = pressing ? 2 : tight ? 1 : 0;
        var verdict = pressing ? "pressing: pages going to swap" : tight ? "tight" : "ok";
        pressureLine = $"memory {verdict}"
            + (availableMb is { } av ? $"; {av:N0} MB available" : "")
            + (swap is { } sw ? $"; swap {sw.Used / 1048576.0:N0} MB in use (in {swapInRate:0.0}, out {swapOutRate:0.0} MB/s)" : "");
    }

    // host_statistics64(HOST_VM_INFO64): vm_statistics64, 38 natural_t words. Available: free (offset 0; it counts the
    // speculative pages already -- vm_stat prints it without them), inactive (8), purgeable (88). Counters: swapins (112),
    // swapouts (120).
    [DllImport("libSystem.dylib", EntryPoint = "mach_host_self")]
    private static extern uint MachHostSelf();

    [DllImport("libSystem.dylib", EntryPoint = "host_statistics64")]
    private static extern int HostStatistics64(uint host, int flavor, [Out] byte[] info, ref uint count);

    private static (ulong AvailablePages, ulong SwapIns, ulong SwapOuts, long PageBytes)? VmStatistics()
    {
        if (!OperatingSystem.IsMacOS()) return null;
        var info = new byte[38 * 4];
        uint count = 38;
        if (HostStatistics64(MachHostSelf(), 4, info, ref count) != 0) return null;
        uint U32(int o) => BitConverter.ToUInt32(info, o);
        ulong U64(int o) => BitConverter.ToUInt64(info, o);
        var available = (ulong)U32(0) + U32(8) + U32(88);
        var page = SysctlLong("hw.pagesize");
        return (available, U64(112), U64(120), page > 0 ? page : 16384);
    }

    [DllImport("libc", EntryPoint = "sysctlbyname")]
    private static extern int SysctlLong(string name, ref long value, ref nint length, nint newValue, nint newLength);

    private static long SysctlLong(string name)
    {
        if (!OperatingSystem.IsMacOS()) return 0;
        long value = 0;
        nint length = sizeof(long);
        return SysctlLong(name, ref value, ref length, 0, 0) == 0 ? value : 0;
    }

    // proc_pid_rusage(pid, RUSAGE_INFO_V0, &info): ri_phys_footprint is the eighth 64-bit word after the 16-byte uuid.
    [DllImport("libproc", EntryPoint = "proc_pid_rusage")]
    private static extern int ProcPidRusage(int pid, int flavor, [Out] ulong[] info);

    private static long? ProcessFootprint()
    {
        var info = new ulong[32];
        if (ProcPidRusage(Environment.ProcessId, 0, info) != 0) return null;
        return (long)info[2 + 7];
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

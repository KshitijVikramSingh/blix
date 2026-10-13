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
//   Memory: this process's resident size, the GPU's allocation (Metal's count, every client's), the kernel's memory
//   pressure level and swap in use; read once a second.
internal sealed partial class SponzaLoop : IDebugLogsSection
{
    // The cook's CPU stages report through the engine's registry (DebugProgress), the Logs tab and the cook screen
    // drawing them; the "cook" group.
    private static void ProgressBegin(string name, long total) => DebugProgress.Begin(name, total, "cook");
    private static void ProgressAdd(string name, long units = 1) => DebugProgress.Add(name, units);
    private static void ProgressEnd(string name) => DebugProgress.End(name);

    // Each frame: the GPU cooks' own counters into the registry (the split cook's sweep and rounds, the light bake).
    private string? splitProgressName;
    private void ReportCookProgress()
    {
        if (sunNodeStage is not (SunNodeStage.Off or SunNodeStage.Done) && sunNodeSuns is not null)
        {
            var refining = sunNodeRefineRound > 0;
            var name = sunNodeStage != SunNodeStage.Tracing ? $"split cook: {sunNodeStage}"
                : refining ? $"split cook: refining round {sunNodeRefineRound}" : "split cook: sweep";
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
    private string memoryLine = "", pressureLine = "";
    private bool pressureHigh;

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
        if (pressureHigh) ImGui.TextColored(new System.Numerics.Vector4(1f, 0.45f, 0.45f, 1f), pressureLine);
        else ImGui.TextUnformatted(pressureLine);
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
        pressureHigh = level >= 2;
        pressureLine = $"pressure {levelName}" + (swap is { } sw ? $"; swap {sw.Used / 1048576.0:N0} of {sw.Total / 1048576.0:N0} MB" : "");
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

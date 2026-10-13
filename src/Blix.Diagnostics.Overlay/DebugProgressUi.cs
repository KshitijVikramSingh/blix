using Blix.Diagnostics;
using ImGuiNET;
using System.Numerics;

namespace Blix.Diagnostics.Overlay;

// The run's long jobs (DebugProgress) as ImGui rows: a bar, what it counts, the time spent and, while running, the time
// left at the rate so far. The Logs tab draws them for every application; an application's load or cook screen draws
// them too (the same rows, one look).
public static class DebugProgressUi
{
    /// <summary>Draws the jobs (of <paramref name="group"/>, or all); finished ones dimmed. False when there were none.</summary>
    public static bool Draw(string? group = null, bool runningOnly = false)
    {
        var any = false;
        foreach (var job in DebugProgress.Snapshot())
        {
            if (group is not null && job.Group != group) continue;
            if (runningOnly && job.Finished) continue;
            any = true;
            var fraction = job.Total > 0 ? Math.Min(1.0, (double)job.Done / job.Total) : job.Finished ? 1.0 : 0.0;
            var left = !job.Finished && fraction > 0.01 ? job.Seconds * (1 - fraction) / fraction : (double?)null;
            var time = job.Finished ? $"{job.Seconds:0.0} s" : left is { } l ? $"{job.Seconds:0} s, ~{l:0} s left" : $"{job.Seconds:0} s";
            var counts = job.Total > 0 ? $"{job.Done:N0} of {job.Total:N0}" : "";
            if (job.Finished) ImGui.PushStyleVar(ImGuiStyleVar.Alpha, 0.55f);
            ImGui.ProgressBar((float)fraction, new Vector2(160f, 0f), job.Total > 0 || job.Finished ? $"{fraction * 100:0}%" : "...");
            ImGui.SameLine();
            ImGui.TextUnformatted($"{job.Group}: {job.Name}  {counts}  {time}");
            if (job.Finished) ImGui.PopStyleVar();
        }
        return any;
    }
}

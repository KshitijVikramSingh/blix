using System.Collections.Concurrent;
using System.Diagnostics;

namespace Blix.Diagnostics;

/// <summary>One long job's state: what it is called, which phase it belongs to, how far along, for how long.</summary>
public readonly record struct DebugProgressEntry(string Name, string Group, long Done, long Total, double Seconds, bool Finished);

// The run's long jobs, for every screen that shows progress -- the overlay's Logs tab, an application's load or cook
// screen: loading a scene, streaming its textures, cooking its light. A job starts with Begin (its total, 0 when not
// known), moves with Add or Set from any thread, and ends with End; a finished job stays listed with its time until the
// same name begins again. Groups name the phase ("load", "stream", "cook") so a screen can show one or all.
public static class DebugProgress
{
    private sealed class Job
    {
        public long Done, Total;
        public readonly string Group;
        public readonly Stopwatch Clock = Stopwatch.StartNew();
        public double? EndedSeconds;
        public Job(string group, long total) { Group = group; Total = total; }
    }

    private static readonly ConcurrentDictionary<string, Job> Jobs = new(StringComparer.Ordinal);
    private static readonly List<string> Order = new();

    /// <summary>A job starts (again): <paramref name="total"/> units, 0 when not known.</summary>
    public static void Begin(string name, long total, string group = "cook")
    {
        Jobs[name] = new Job(group, total);
        lock (Order) { Order.Remove(name); Order.Add(name); }
    }

    public static void Add(string name, long units = 1)
    {
        if (Jobs.TryGetValue(name, out var job)) Interlocked.Add(ref job.Done, units);
    }

    /// <summary>Sets a job's counts outright (a job that keeps its own, read each frame); begins it if it is not running.</summary>
    public static void Set(string name, long done, long total, string group = "cook")
    {
        if (!Jobs.TryGetValue(name, out var job) || job.EndedSeconds is not null) { Begin(name, total, group); job = Jobs[name]; }
        Interlocked.Exchange(ref job.Done, done);
        Interlocked.Exchange(ref job.Total, total);
    }

    public static void End(string name)
    {
        if (Jobs.TryGetValue(name, out var job) && job.EndedSeconds is null) job.EndedSeconds = job.Clock.Elapsed.TotalSeconds;
    }

    /// <summary>Whether any job (of <paramref name="group"/>, or any) is running.</summary>
    public static bool Running(string? group = null) =>
        Jobs.Values.Any(j => j.EndedSeconds is null && (group is null || j.Group == group));

    /// <summary>Every job, in the order they began (most recent last).</summary>
    public static IReadOnlyList<DebugProgressEntry> Snapshot()
    {
        string[] order;
        lock (Order) order = Order.ToArray();
        var list = new List<DebugProgressEntry>(order.Length);
        foreach (var name in order)
            if (Jobs.TryGetValue(name, out var j))
                list.Add(new DebugProgressEntry(name, j.Group, Interlocked.Read(ref j.Done), Interlocked.Read(ref j.Total),
                    j.EndedSeconds ?? j.Clock.Elapsed.TotalSeconds, j.EndedSeconds is not null));
        return list;
    }
}

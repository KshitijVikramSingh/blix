using System.Diagnostics;
using System.Text;

namespace Blix.Diagnostics;

/// <summary>One line of the run's log: when (ms since the log started), its tag, how severe, and the text.</summary>
public readonly record struct DebugLogLine(long Index, double TimestampMs, string Tag, DebugEventSeverity Severity, string Text);

// The run's log, kept for the overlay's Logs tab: every line the process writes to stdout or stderr once
// CaptureConsole has run (the host runs it whenever it keeps a debug system), and whatever is written to it
// directly. A line's tag is its leading "[Tag]" ("[VulkanSponza] transport: ..."); an indented line carries on the
// tag of the line before it (a report's table rows), and anything else is "stdout" or "stderr". stderr lines are
// warnings. The last Capacity lines are kept; Index counts every line ever written, so a reader can tell what it has
// already seen. Thread-safe: cooks log from worker threads.
public static class DebugLog
{
    public const int Capacity = 20_000;
    private static readonly object Gate = new();
    private static readonly DebugLogLine[] Ring = new DebugLogLine[Capacity];
    private static readonly Stopwatch Clock = Stopwatch.StartNew();
    private static readonly SortedSet<string> TagSet = new(StringComparer.Ordinal);
    private static long written;
    private static string lastTag = "stdout";
    private static bool captured;

    /// <summary>Lines written since the log started (the newest line's Index + 1).</summary>
    public static long Written { get { lock (Gate) return written; } }

    /// <summary>The tags seen so far, sorted.</summary>
    public static string[] Tags { get { lock (Gate) return TagSet.ToArray(); } }

    /// <summary>Tees stdout and stderr into the log (once; later calls do nothing). The console still gets everything.</summary>
    public static void CaptureConsole()
    {
        lock (Gate)
        {
            if (captured) return;
            captured = true;
        }
        Console.SetOut(new Tee(Console.Out, DebugEventSeverity.Info, "stdout"));
        Console.SetError(new Tee(Console.Error, DebugEventSeverity.Warn, "stderr"));
    }

    /// <summary>Adds a line under a tag of the caller's choosing (no console echo).</summary>
    public static void Write(string tag, string text, DebugEventSeverity severity = DebugEventSeverity.Info)
    {
        lock (Gate) Add(tag, severity, text);
    }

    /// <summary>The kept lines from <paramref name="fromIndex"/> on (oldest first), at most <paramref name="max"/>.</summary>
    public static List<DebugLogLine> Since(long fromIndex, int max = Capacity)
    {
        lock (Gate)
        {
            var first = Math.Max(fromIndex, Math.Max(0, written - Capacity));
            var result = new List<DebugLogLine>((int)Math.Min(max, Math.Max(0, written - first)));
            for (var i = first; i < written && result.Count < max; i++) result.Add(Ring[i % Capacity]);
            return result;
        }
    }

    private static void Add(string tag, DebugEventSeverity severity, string text)
    {
        Ring[written % Capacity] = new DebugLogLine(written, Clock.Elapsed.TotalMilliseconds, tag, severity, text);
        written++;
        TagSet.Add(tag);
    }

    // A console line's tag: "[Tag] ..." names it; an indented line continues the last one's; else the stream's.
    private static void AddConsoleLine(string line, DebugEventSeverity severity, string streamTag)
    {
        lock (Gate)
        {
            string tag;
            if (line.Length > 2 && line[0] == '[' && line.IndexOf(']') is var close and > 1 and < 48)
                tag = line[1..close];
            else if (line.Length > 0 && (line[0] == ' ' || line[0] == '\t'))
                tag = lastTag;
            else
                tag = streamTag;
            lastTag = tag;
            Add(tag, severity, line);
        }
    }

    // Writes through to the console and gathers whole lines for the log.
    private sealed class Tee : TextWriter
    {
        private readonly TextWriter inner;
        private readonly DebugEventSeverity severity;
        private readonly string streamTag;
        private readonly StringBuilder pending = new();

        public Tee(TextWriter inner, DebugEventSeverity severity, string streamTag)
        {
            this.inner = inner;
            this.severity = severity;
            this.streamTag = streamTag;
        }

        public override Encoding Encoding => inner.Encoding;

        public override void Write(char value)
        {
            inner.Write(value);
            lock (pending) Gather(value);
        }

        public override void Write(string? value)
        {
            if (value is null) return;
            inner.Write(value);
            lock (pending) foreach (var c in value) Gather(c);
        }

        public override void WriteLine(string? value)
        {
            inner.WriteLine(value);
            lock (pending)
            {
                if (value is not null) foreach (var c in value) Gather(c);
                Gather('\n');
            }
        }

        public override void Flush() => inner.Flush();

        private void Gather(char c)
        {
            if (c == '\n')
            {
                AddConsoleLine(pending.ToString(), severity, streamTag);
                pending.Clear();
            }
            else if (c != '\r') pending.Append(c);
        }
    }
}

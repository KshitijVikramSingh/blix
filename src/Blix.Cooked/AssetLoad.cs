namespace Blix.Cooked;

/// <summary>Which path a load actually took.</summary>
public enum AssetLoadMode
{
    /// <summary>The original file was parsed directly — a .gltf, a .png, an .hdr.</summary>
    Source,

    /// <summary>An engine-native cooked artifact was used.</summary>
    Cooked,

    /// <summary>What was wanted was missing or unusable; a substitute was used.</summary>
    Fallback,

    /// <summary>Nothing usable was found. The slot is empty.</summary>
    Missing,
}

/// <summary>
/// What one load did: which path, from what, at what cost.
/// </summary>
/// <param name="SourcePath">The asset as the caller asked for it.</param>
/// <param name="CookedPath">The cooked artifact used, when one was.</param>
/// <param name="Mode">Which of the four things happened.</param>
/// <param name="Bytes">How much was read.</param>
/// <param name="LoadMs">How long it took.</param>
/// <param name="Recipe">The 4cc of the recipe that made the cooked artifact, when there was one.</param>
/// <param name="Warning">Optional diagnostic detail: fallback reason, missing resource, or ignored content.</param>
/// <remarks>
/// Cost accompanies the branch because the cooked formats buy different
/// things — <c>.blixmesh</c> is load time, <c>.blixtex</c> is GPU memory and bandwidth, and
/// <c>.blixprobe</c> is work that cannot happen at load at all — so a bare Cooked/Source flag
/// flattens three economics into a checkmark and tells a reader nothing about whether cooking was
/// worth it here.
/// </remarks>
public sealed record AssetLoadReport(
    string SourcePath,
    string? CookedPath,
    AssetLoadMode Mode,
    long Bytes,
    double LoadMs,
    string? Recipe = null,
    string? Warning = null);

/// <summary>
/// Where a loader says what it did.
/// </summary>
/// <remarks>
/// <para>
/// The channel is ambient because imports may run before a frame diagnostics context exists and
/// importers should not depend on a particular observer.
/// </para>
/// <para>
/// Collection is off by default so ordinary loads do not retain diagnostic records.
/// </para>
/// </remarks>
public static class AssetLoadLog
{
    private static readonly object Gate = new();
    private static readonly List<AssetLoadReport> Collected = new();

    /// <summary>Whether loaders should report. False until something asks.</summary>
    public static bool Enabled { get; set; }

    /// <summary>Raised as each report arrives, for a live reader.</summary>
    public static event Action<AssetLoadReport>? Reported;

    /// <summary>Turns collection on and clears anything already held.</summary>
    public static void Start()
    {
        lock (Gate) Collected.Clear();
        Enabled = true;
    }

    /// <summary>Records one load. A no-op, and allocation-free at the call site, when disabled.</summary>
    public static void Report(AssetLoadReport report)
    {
        if (!Enabled) return;
        ArgumentNullException.ThrowIfNull(report);

        // Loaders run in parallel — the texture pre-decode is explicitly parallel — so this is
        // locked rather than merely thread-local. A report lost to a race is a coverage gap that
        // looks exactly like an asset nobody cooked.
        lock (Gate) Collected.Add(report);
        Reported?.Invoke(report);
    }

    /// <summary>Takes everything collected so far and clears the buffer.</summary>
    public static AssetLoadReport[] Drain()
    {
        lock (Gate)
        {
            var all = Collected.ToArray();
            Collected.Clear();
            return all;
        }
    }

    /// <summary>What has been collected, without clearing it.</summary>
    public static AssetLoadReport[] Peek()
    {
        lock (Gate) return Collected.ToArray();
    }
}

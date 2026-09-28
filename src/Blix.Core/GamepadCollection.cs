using System.Collections;

namespace Blix.Core;

/// <summary>
/// The gamepads this tick knows about.
/// </summary>
/// <remarks>
/// <para>
/// Indexed by the backend's id rather than by position, because position moves: unplug the first
/// of two pads and the second would become <c>Gamepads[0]</c>, silently handing a game a different
/// controller than the one the player was holding. An id that refers to the same physical device
/// for as long as it is plugged in is the only index worth offering.
/// </para>
/// <para>
/// A pad is kept for exactly one tick after it disappears so that <see
/// cref="GamepadState.DisconnectedThisTick"/> can be read, and dropped at the following flip.
/// </para>
/// </remarks>
public sealed class GamepadCollection : IReadOnlyCollection<GamepadState>
{
    private readonly Dictionary<int, GamepadState> pads = new();

    // True for exactly the next sample: what it reads is what was already there, not what just
    // happened. Set when the application becomes eligible again and cleared by the flip that
    // shows the result.
    private bool priming;
    private readonly List<int> leaving = new();
    private static readonly GamepadState Absent = new(-1);

    /// <summary>How many pads this tick knows about, including one disconnecting.</summary>
    public int Count => pads.Count;

    /// <summary>
    /// The pad with that id, or a neutral one that reports <see cref="GamepadState.Connected"/>
    /// false.
    /// </summary>
    public GamepadState this[int id] => pads.TryGetValue(id, out var pad) ? pad : Absent;

    public IEnumerator<GamepadState> GetEnumerator() => pads.Values.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    /// <summary>
    /// Treat the next sample as re-acquisition rather than as events.
    /// </summary>
    /// <remarks>
    /// Collection-wide rather than per pad, because a pad plugged in while the application was
    /// not watching is in the same position as one that was held throughout: whatever it reports
    /// first was true before anyone was entitled to see it.
    /// </remarks>
    internal void BeginResync() => priming = true;

    internal bool Priming => priming;

    internal GamepadState Mutable(int id)
    {
        if (pads.TryGetValue(id, out var pad)) return pad;
        pad = new GamepadState(id);
        pads[id] = pad;
        return pad;
    }

    internal void Connect(int id, string name)
    {
        var pad = Mutable(id);
        pad.Name = name;
        pad.MarkAppeared();
    }

    internal void Disconnect(int id)
    {
        if (pads.TryGetValue(id, out var pad)) pad.MarkVanished();
    }

    internal void BeginTick()
    {
        // Forget the pads whose disconnect a tick has already seen. This happens BEFORE the flip,
        // which is the whole subtlety: retiring them in the same flip that makes their releases
        // visible would mean no tick ever reported the disconnect at all — a pad that was simply
        // there one frame and gone the next, which is the bookkeeping this layer exists to remove.
        leaving.Clear();
        foreach (var pad in pads.Values)
        {
            if (pad.Retired) leaving.Add(pad.Id);
        }

        foreach (var id in leaving) pads.Remove(id);

        foreach (var pad in pads.Values) pad.BeginTick(priming);
        priming = false;
    }

    internal void ReleaseAll()
    {
        foreach (var pad in pads.Values) pad.ReleaseAll();
    }
}

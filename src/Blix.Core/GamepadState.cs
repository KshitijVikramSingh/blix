using System.Numerics;

namespace Blix.Core;

/// <summary>
/// One gamepad, as of this tick.
/// </summary>
/// <remarks>
/// <b>A pad that is not there reads as a pad holding nothing.</b> Asking for a gamepad nobody
/// plugged in returns this in its disconnected state rather than null, so a game that supports a
/// controller does not have to be written twice — once for when there is one and once for when
/// there is not. <see cref="Connected"/> is there for the code that genuinely needs to know, which
/// is usually only the code that draws a prompt.
/// </remarks>
public sealed class GamepadState
{
    private readonly ButtonTrack buttons = new(Enum.GetValues<GamepadButton>().Length);
    private readonly float[] liveAxes = new float[Enum.GetValues<GamepadAxis>().Length];
    private readonly float[] tickAxes = new float[Enum.GetValues<GamepadAxis>().Length];
    private readonly float[] previousAxes = new float[Enum.GetValues<GamepadAxis>().Length];

    internal GamepadState(int id) => Id = id;

    /// <summary>Which pad this is, as the backend numbers them.</summary>
    public int Id { get; }

    /// <summary>What the backend calls it, where it says.</summary>
    public string Name { get; internal set; } = "";

    /// <summary>Whether it is plugged in, as of this tick.</summary>
    public bool Connected { get; internal set; }

    /// <summary>It appeared since the previous tick.</summary>
    public bool ConnectedThisTick { get; private set; }

    /// <summary>
    /// It went away since the previous tick, and this is the last tick that will say so.
    /// </summary>
    /// <remarks>
    /// The pad is kept for exactly one tick after it vanishes, which is what gives a game somewhere
    /// to notice. Without it, a disconnect is a pad that was there last frame and simply is not
    /// now — detectable only by a game that kept its own list, which is the sort of bookkeeping
    /// this whole layer exists to stop handing out.
    /// </remarks>
    public bool DisconnectedThisTick { get; private set; }

    /// <summary>Its disconnect has been reported to a tick; the next flip may forget it.</summary>
    internal bool Retired { get; private set; }

    // Both edges are recorded live and revealed at the flip, exactly like a button's press. Setting
    // the public flag directly is the obvious thing and it is wrong in both directions: a connect
    // set outside the tick is cleared by the very flip that should have shown it, and a disconnect
    // is forgotten before anything reads it. The suite found each of those separately.
    private bool appeared;
    private bool vanished;

    /// <summary>A button, as of this tick.</summary>
    public ButtonState this[GamepadButton button] => buttons[(int)button];

    /// <summary>An axis, as of this tick.</summary>
    public AxisState this[GamepadAxis axis] =>
        (uint)axis < (uint)tickAxes.Length
            ? new AxisState(tickAxes[(int)axis], previousAxes[(int)axis])
            : default;

    /// <summary>The left stick, as one vector. Raw: no deadzone has been applied.</summary>
    public Vector2 LeftStick => new(tickAxes[(int)GamepadAxis.LeftX], tickAxes[(int)GamepadAxis.LeftY]);

    /// <summary>The right stick, as one vector. Raw: no deadzone has been applied.</summary>
    public Vector2 RightStick => new(tickAxes[(int)GamepadAxis.RightX], tickAxes[(int)GamepadAxis.RightY]);

    /// <summary>The left trigger, in [0, 1].</summary>
    public float LeftTrigger => tickAxes[(int)GamepadAxis.LeftTrigger];

    /// <summary>The right trigger, in [0, 1].</summary>
    public float RightTrigger => tickAxes[(int)GamepadAxis.RightTrigger];

    /// <summary>Record a button as sampled, or as re-acquired.</summary>
    /// <remarks>
    /// <b><paramref name="priming"/> is the difference between "this was pressed" and "this was
    /// already held when we were allowed to look again".</b> The second is not a press: nothing
    /// happened, the application merely became eligible. Reporting one fires whatever the button
    /// means — and one-shot actions are exactly what <c>Pressed</c> is for, so coming back to a
    /// window while resting a thumb on A would select, confirm or shoot.
    /// </remarks>
    internal void RecordButton(GamepadButton button, bool pressed, bool priming)
    {
        if (priming) buttons.Prime((int)button, pressed);
        else if (pressed) buttons.Down((int)button);
        else buttons.Up((int)button);
    }

    internal void RecordAxis(GamepadAxis axis, float value)
    {
        if ((uint)axis < (uint)liveAxes.Length) liveAxes[(int)axis] = value;
    }

    internal void BeginTick(bool primed)
    {
        buttons.BeginTick();
        Array.Copy(tickAxes, previousAxes, tickAxes.Length);
        Array.Copy(liveAxes, tickAxes, liveAxes.Length);

        // An axis has no edges, so priming looks like nothing at all -- until the delta is read.
        // ReleaseAll zeroed these when the application stopped being eligible, so the first sample
        // back would report the stick travelling from centre to wherever the thumb had been
        // resting the whole time. The mouse's jump, on a stick. A re-acquired axis has a previous
        // equal to its value, which is the truth: it did not move, we started watching.
        if (primed) Array.Copy(tickAxes, previousAxes, tickAxes.Length);

        ConnectedThisTick = appeared;
        DisconnectedThisTick = vanished;
        appeared = false;
        vanished = false;

        // Its releases and zeroed axes are visible in the tick starting now; one flip later it goes.
        if (DisconnectedThisTick) Retired = true;
    }

    internal void MarkAppeared()
    {
        if (!Connected) appeared = true;
        Connected = true;
    }

    internal void MarkVanished()
    {
        if (!Connected) return;
        ReleaseAll();
        Connected = false;
        vanished = true;
    }

    /// <summary>
    /// Let go of everything, because the pad stopped existing.
    /// </summary>
    /// <remarks>
    /// The same rule as a window losing focus with a key held. A pad unplugged at full throttle
    /// never sends the trigger back to zero, so a game that was accelerating carries on
    /// accelerating with nothing in its own logic to explain it. Synthesising the releases and
    /// zeroing the axes means the tick that reports the disconnect also reports letting go — which
    /// is the only honest description of what happened.
    /// </remarks>
    internal void ReleaseAll()
    {
        buttons.ReleaseAll();
        Array.Clear(liveAxes);
    }
}

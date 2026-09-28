using System.Numerics;

namespace Blix.Core;

/// <summary>
/// What the input devices did, as one answer that does not change while a tick is running.
/// </summary>
/// <remarks>
/// <para>
/// <b>The engine owed this and was charging every game for it.</b> The callback surface it replaces
/// reported platform events, and a platform event is almost never what a game wants to know. Three
/// different reconstructions of the same missing thing grew in the tree: a <c>HashSet&lt;Key&gt;</c>
/// filled on key-down and emptied on key-up (three consumers, twenty references in the largest),
/// one <c>bool</c> per key doing the same job by hand, and gameplay run straight from the callback
/// — which meant a wave starting, or a game restarting, at platform-event time rather than at a
/// point the game had chosen.
/// </para>
/// <para>
/// None of that is policy. Working out that a key went down this tick, that a stick moved, or that
/// a pad was unplugged while the trigger was held is reusable mechanism, and mechanism is the
/// engine's job. What remains the game's job is everything this deliberately will not do: no
/// actions, no bindings, no contexts, no chords, no rebinding, no deadzone policy, no idea that
/// Space might mean jump.
/// </para>
/// <para>
/// <b>The tick is the unit.</b> <see cref="BeginTick"/> is called exactly once by the host,
/// immediately before the game's update, and everything here is fixed until the next one. Rendering
/// observes the same answer the update before it saw. Should a host ever run several updates per
/// rendered frame, a press still reports <c>Pressed</c> on exactly one of them, because the flip is
/// tied to the update and not to the frame.
/// </para>
/// <para>
/// The recording methods are public because a host is not the only legitimate driver of input: a
/// test, a replay and a recorded demo are the same shape, and hiding them behind assembly tricks
/// would buy nothing but a harder suite.
/// </para>
/// </remarks>
public sealed class InputState
{
    private readonly ButtonTrack keys = new(Enum.GetValues<Key>().Length);
    private readonly ButtonTrack buttons = new(Enum.GetValues<MouseButton>().Length);

    private Vector2 livePosition;
    private Vector2 liveDeltaAccum;
    private Vector2 liveWheelAccum;

    private Vector2 tickPosition;
    private Vector2 tickDelta;
    private Vector2 tickWheel;

    /// <summary>The keyboard, as of this tick.</summary>
    public ButtonState this[Key key] => keys[(int)key];

    /// <summary>A mouse button, as of this tick.</summary>
    public ButtonState this[MouseButton button] => buttons[(int)button];

    /// <summary>Where the pointer is, in logical pixels — the latest position, not an accumulation.</summary>
    /// <remarks>
    /// Deliberately asymmetric with <see cref="MouseDelta"/>. A position is a place and only the
    /// newest one is meaningful; movement is a quantity and every scrap of it counts, which is why
    /// one is sampled and the other summed. Mixing them up gives a cursor that lags or a camera
    /// that under-turns, and both look like something else.
    /// </remarks>
    public Vector2 MousePosition => tickPosition;

    /// <summary>How far the pointer moved since the previous tick, summed over every report.</summary>
    public Vector2 MouseDelta => tickDelta;

    /// <summary>How far the wheel turned since the previous tick, summed over every report.</summary>
    public Vector2 MouseWheel => tickWheel;

    /// <summary>
    /// The gamepads, by the id the backend gives them.
    /// </summary>
    /// <remarks>
    /// Asking for one that is not plugged in gives a pad reading neutral rather than null, so a
    /// game with controller support does not have to be written twice.
    /// </remarks>
    public GamepadCollection Gamepads { get; } = new();

    /// <summary>Start a new tick: everything read from here on describes what happened since the last one.</summary>
    public void BeginTick()
    {
        keys.BeginTick();
        buttons.BeginTick();
        Gamepads.BeginTick();

        tickPosition = livePosition;
        tickDelta = liveDeltaAccum;
        tickWheel = liveWheelAccum;
        liveDeltaAccum = Vector2.Zero;
        liveWheelAccum = Vector2.Zero;
    }

    /// <summary>Record a key going down. Repeats are harmless; a held key presses once per tick at most.</summary>
    public void RecordKeyDown(Key key) => keys.Down((int)key);

    /// <summary>Record a key coming up.</summary>
    public void RecordKeyUp(Key key) => keys.Up((int)key);

    /// <summary>Record a mouse button going down.</summary>
    public void RecordMouseDown(MouseButton button) => buttons.Down((int)button);

    /// <summary>Record a mouse button coming up.</summary>
    public void RecordMouseUp(MouseButton button) => buttons.Up((int)button);

    /// <summary>Record pointer movement: where it is now, and how far it came.</summary>
    public void RecordMouseMove(Vector2 position, Vector2 delta)
    {
        livePosition = position;
        liveDeltaAccum += delta;
    }

    /// <summary>Record wheel movement.</summary>
    public void RecordMouseWheel(Vector2 amount) => liveWheelAccum += amount;

    /// <summary>Record that a pad appeared, or that one already known is still here.</summary>
    public void RecordGamepadConnected(int id, string name) => Gamepads.Connect(id, name);

    /// <summary>Record that a pad went away. Its releases are synthesised; see <see cref="GamepadState"/>.</summary>
    public void RecordGamepadDisconnected(int id) => Gamepads.Disconnect(id);

    /// <summary>Record a pad's button, as sampled.</summary>
    public void RecordGamepadButton(int id, GamepadButton button, bool pressed) =>
        Gamepads.Mutable(id).RecordButton(button, pressed);

    /// <summary>Record a pad's axis, as sampled, in the backend's own range.</summary>
    public void RecordGamepadAxis(int id, GamepadAxis axis, float value) =>
        Gamepads.Mutable(id).RecordAxis(axis, value);

    /// <summary>
    /// Let go of everything, as though every held input had been released.
    /// </summary>
    /// <remarks>
    /// <b>One rule for two situations that look unrelated and are not.</b> A window losing focus
    /// with W held will never be sent the key-up, and a pad unplugged mid-throttle will never send
    /// the trigger back to zero. Both leave a game holding an input that physically stopped
    /// existing, and the symptom is the same both times: something keeps happening and nothing in
    /// the game's own logic explains why. So a loss of focus or of a device synthesises the
    /// releases, and the tick that follows reports them exactly as though a person had let go.
    /// </remarks>
    public void ReleaseAll()
    {
        keys.ReleaseAll();
        buttons.ReleaseAll();
        Gamepads.ReleaseAll();
        liveDeltaAccum = Vector2.Zero;
        liveWheelAccum = Vector2.Zero;
    }
}

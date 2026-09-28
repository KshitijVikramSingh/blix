namespace Blix.Core;

/// <summary>
/// What one button did, as of this tick.
/// </summary>
/// <remarks>
/// <para>
/// <b>Three facts, not two.</b> The obvious encoding is "down now" plus "down last tick", with
/// pressed and released derived — and it silently loses a key tapped and released inside one tick,
/// which on a 16 ms frame is an ordinary thing for a person to do. So a press and a release in the
/// same tick reports <c>Down: false, Pressed: true, Released: true</c>, which looks contradictory
/// and is exactly right.
/// </para>
/// <para>
/// <see cref="Pressed"/> and <see cref="Released"/> are TICK transitions, not platform events. A
/// key held down through a keyboard's auto-repeat is pressed once, however many events the backend
/// chose to send, because the question being answered is "did this change since the game last
/// looked" and the answer to that is not a function of how chatty the driver is.
/// </para>
/// </remarks>
public readonly record struct ButtonState
{
    /// <summary>Constructed by whatever is driving input, and nothing else.</summary>
    public ButtonState(bool down, bool pressed, bool released)
    {
        Down = down;
        Pressed = pressed;
        Released = released;
    }

    /// <summary>Held, as of this tick.</summary>
    public bool Down { get; }

    /// <summary>Went down at least once since the previous tick.</summary>
    public bool Pressed { get; }

    /// <summary>Came up at least once since the previous tick.</summary>
    public bool Released { get; }
}

/// <summary>
/// Where one continuous input is, and where it was.
/// </summary>
/// <remarks>
/// Axes are state and never events. There is no threshold at which a stick has "changed enough" to
/// be worth a callback, and inventing one would mean the engine deciding how small a movement
/// matters — which is the aiming feel of every game that ever used it.
/// </remarks>
public readonly record struct AxisState(float Value, float Previous)
{
    /// <summary>How far it moved since the previous tick. Derived, so it cannot disagree.</summary>
    public float Delta => Value - Previous;
}

using System.Numerics;

namespace Blix.Core;

/// <summary>
/// What the input devices did, as of this tick — the reading half.
/// </summary>
/// <remarks>
/// <para>
/// <b>So that "fixed for the length of an update" is enforced rather than promised.</b> The
/// concrete <see cref="InputState"/> has to expose the recording side publicly — a host drives it,
/// and so do a test, a replay and a recorded demo, which are the same shape and all legitimate.
/// But handing that surface to game code made the thing documented as a snapshot something an
/// application was free to rewrite mid-frame.
/// </para>
/// <para>
/// So the host hands out this, and whatever is driving input holds the class. One type, two
/// audiences, and the API says which is which instead of a comment asking nicely.
/// </para>
/// </remarks>
public interface IInputState
{
    /// <summary>A key, as of this tick.</summary>
    ButtonState this[Key key] { get; }

    /// <summary>A mouse button, as of this tick.</summary>
    ButtonState this[MouseButton button] { get; }

    /// <summary>Where the pointer is, in logical pixels. The latest position, not an accumulation.</summary>
    Vector2 MousePosition { get; }

    /// <summary>How far the pointer moved since the previous tick, summed over every report.</summary>
    Vector2 MouseDelta { get; }

    /// <summary>How far the wheel turned since the previous tick, summed over every report.</summary>
    Vector2 MouseWheel { get; }

    /// <summary>The gamepads, by the id the backend gives them.</summary>
    GamepadCollection Gamepads { get; }
}

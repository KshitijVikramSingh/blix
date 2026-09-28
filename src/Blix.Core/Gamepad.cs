namespace Blix.Core;

/// <summary>
/// The buttons a gamepad has, named for the layout every pad is described in.
/// </summary>
/// <remarks>
/// <para>
/// Face buttons are A/B/X/Y because that is what the backend reports and what documentation
/// everywhere says, not because Blix believes every pad has those letters printed on it. A pad
/// with different glyphs still reports its south face button as A; what a game draws for it is the
/// game's business, exactly like what it decides A means.
/// </para>
/// <para>
/// The d-pad is four buttons rather than an axis pair. It is four switches in the hardware and in
/// the backend, and turning them into an axis here would be inventing a number nobody measured.
/// </para>
/// </remarks>
public enum GamepadButton
{
    Unknown = 0,
    A,
    B,
    X,
    Y,
    LeftBumper,
    RightBumper,
    Back,
    Start,
    Home,

    /// <summary>The click in a thumbstick, which is a button and not part of its axes.</summary>
    LeftStick,
    RightStick,

    DPadUp,
    DPadRight,
    DPadDown,
    DPadLeft,
}

/// <summary>
/// The continuous inputs a gamepad has.
/// </summary>
/// <remarks>
/// <para>
/// Sticks report <c>[-1, 1]</c> per axis and triggers <c>[0, 1]</c>, as close to what the backend
/// says as practicable. <b>No deadzone is applied.</b> A deadzone is not a correction, it is a
/// decision about how small a movement should count — which is the aiming feel of every game that
/// ever used one, and therefore the game's to make. An engine that quietly zeroed everything under
/// 0.15 would be tuning every shooter built on it.
/// </para>
/// <para>
/// Radial and axial deadzone helpers are ordinary reusable maths and can live beside this the day
/// something asks for them. Applying one for everybody is the part that cannot be undone.
/// </para>
/// </remarks>
public enum GamepadAxis
{
    LeftX = 0,
    LeftY,
    RightX,
    RightY,
    LeftTrigger,
    RightTrigger,
}

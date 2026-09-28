namespace Blix.Core;

public enum Key
{
    Unknown = 0,
    Escape,
    Space,
    Enter,
    Tab,
    Backspace,
    Left,
    Right,
    Up,
    Down,
    A,
    B,
    C,
    D,
    E,
    F,
    G,
    H,
    I,
    J,
    K,
    L,
    M,
    N,
    O,
    P,
    Q,
    R,
    S,
    T,
    U,
    V,
    W,
    X,
    Y,
    Z,
    LeftControl,
    RightControl,
    LeftSuper,
    RightSuper,

    // Appended rather than slotted in beside the letters, because these are enum values a backend maps
    // against and renumbering LeftControl to make the list read nicely is not worth anything to anyone.
    LeftShift,
    RightShift,

    /// <summary>
    /// The number row. The keypad has its own values below — see <see cref="Keypad0"/>.
    /// </summary>
    Number0,
    Number1,
    Number2,
    Number3,
    Number4,
    Number5,
    Number6,
    Number7,
    Number8,
    Number9,

    // ── Everything below completes the vocabulary ────────────────────────────────────────────
    //
    // A keyboard vocabulary that cannot say Alt, Delete, '=' or F5 is not a small vocabulary, it
    // is an incomplete one, and the difference shows up as a wall rather than a compromise: there
    // was no way to express the key at all. The digits arrived the same way, late and under
    // pressure, when a game wanted control groups and found the enum could not say "4".
    //
    // Still appended rather than sorted in, for the reason above: these are values a backend maps
    // against, and renumbering to make the list read nicely is worth nothing to anyone.

    /// <summary>The third modifier. Absent until now, while Shift, Control and Super all had pairs.</summary>
    LeftAlt,
    RightAlt,

    /// <summary>The context-menu key, where a keyboard has one.</summary>
    Menu,

    // Navigation and editing.
    Insert,
    Delete,
    Home,
    End,
    PageUp,
    PageDown,

    // Locks and system keys. Reported like any other key; what a lock's LED is doing is not
    // something this layer claims to know.
    CapsLock,
    ScrollLock,
    NumLock,
    PrintScreen,
    Pause,

    // Punctuation, named for the key rather than for what a layout prints on it. GraveAccent
    // rather than Backtick because that is what the platforms call it.
    Apostrophe,
    Comma,
    Minus,
    Period,
    Slash,
    Semicolon,
    Equal,
    LeftBracket,
    BackSlash,
    RightBracket,
    GraveAccent,

    /// <summary>The two extra keys non-US layouts put beside the left shift and the enter key.</summary>
    World1,
    World2,

    // Function keys. All twenty-five, because the backend has all twenty-five and stopping at F12
    // would be this file deciding which keyboards exist.
    F1, F2, F3, F4, F5, F6, F7, F8, F9, F10, F11, F12, F13,
    F14, F15, F16, F17, F18, F19, F20, F21, F22, F23, F24, F25,

    /// <summary>
    /// The keypad, distinct from the number row.
    /// </summary>
    /// <remarks>
    /// <b>Keypad digits used to arrive as <see cref="Number0"/> and friends</b>, on the reasoning
    /// that a caller wants the digit rather than the key. That is true of most callers and it is a
    /// decision this layer is not entitled to make: it is below bindings, and a binding can always
    /// map both onto one action. The reverse is not available — information thrown away here
    /// cannot be recovered by anything above. Modelling, CAD and strategy games distinguish them,
    /// and so does most debug tooling.
    ///
    /// A consumer that wants either digit now says so, which is the change this costs.
    /// </remarks>
    Keypad0,
    Keypad1,
    Keypad2,
    Keypad3,
    Keypad4,
    Keypad5,
    Keypad6,
    Keypad7,
    Keypad8,
    Keypad9,
    KeypadDecimal,
    KeypadDivide,
    KeypadMultiply,
    KeypadSubtract,
    KeypadAdd,
    KeypadEnter,
    KeypadEqual
}

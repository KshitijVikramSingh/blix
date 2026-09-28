using System.Numerics;
using System.Reflection;
using Blix.Core;
using Blix.Verify;

// Input, judged without a device.
//
// <b>Every question this arc set out to answer is a pure function of an event order and a tick
// boundary</b>, which is why this suite needs no window, no GPU and no hands: feed it the events a
// backend would have sent, say when the ticks fell, and read what a game would have seen. That
// makes the awkward cases — a key tapped inside one frame, a pad unplugged mid-throttle, a press
// the UI owned and a release it did not — ordinary assertions that run on every platform in
// milliseconds, rather than things discovered by a person holding a key and frowning.
//
// Sections:
//   A  the tick boundary
//   B  transitions that platform events cannot express
//   C  losing focus, losing a device
//   D  the mouse's two different kinds of number
//   E  the backend's vocabulary reaches Blix's intact
//   F  gamepads: axes as state, and a pad that stops existing

var t = new TestRunner();

// ============================================================================
// Section A — a tick is the unit, and nothing moves inside one.
// ============================================================================
{
    var input = new InputState();

    // Nothing has happened, and nothing claims to have.
    input.BeginTick();
    t.Expect("A.1 an untouched key is down/pressed/released = false",
        input[Key.W] is { Down: false, Pressed: false, Released: false });

    // An event before the flip is not visible until the flip.
    input.RecordKeyDown(Key.W);
    t.Expect("A.2 an event mid-tick does not change the answer the tick is giving",
        input[Key.W].Down == false, "the snapshot must not move under the game's feet");

    input.BeginTick();
    t.Expect("A.3 after the flip it is down, and pressed exactly once",
        input[Key.W] is { Down: true, Pressed: true, Released: false });

    // Held across a tick with no new events: still down, no longer pressed.
    input.BeginTick();
    t.Expect("A.4 a key held across a tick is down but NOT pressed again",
        input[Key.W] is { Down: true, Pressed: false, Released: false });

    // A backend that repeats key-down while held must not produce a second press.
    input.RecordKeyDown(Key.W);
    input.RecordKeyDown(Key.W);
    input.RecordKeyDown(Key.W);
    input.BeginTick();
    t.Expect("A.5 auto-repeat does not turn one press into several",
        input[Key.W] is { Down: true, Pressed: false },
        "Pressed is a tick transition, not a count of platform events");

    input.RecordKeyUp(Key.W);
    input.BeginTick();
    t.Expect("A.6 release shows once",
        input[Key.W] is { Down: false, Pressed: false, Released: true });

    input.BeginTick();
    t.Expect("A.7 and does not linger into the tick after",
        input[Key.W] is { Down: false, Released: false });
}

// ============================================================================
// Section B — the transitions a two-state encoding would have lost.
// ============================================================================
//
// Down-now plus down-last-tick is the obvious encoding and it cannot say "tapped": on a 16 ms
// frame a person pressing and releasing a key between two ticks is ordinary, and a scheme that
// reports nothing at all for it drops real input on the floor silently.
{
    var input = new InputState();
    input.BeginTick();

    input.RecordKeyDown(Key.Space);
    input.RecordKeyUp(Key.Space);
    input.BeginTick();
    t.Expect("B.1 a key tapped INSIDE one tick reports both transitions and no hold",
        input[Key.Space] is { Down: false, Pressed: true, Released: true },
        "contradictory-looking and exactly right");

    input.BeginTick();
    t.Expect("B.2 and the tick after is clean", input[Key.Space] is { Pressed: false, Released: false });

    // Several taps in one tick still collapse to one press and one release: the question is "did
    // this change since the game last looked", not "how many times".
    input.RecordKeyDown(Key.Space);
    input.RecordKeyUp(Key.Space);
    input.RecordKeyDown(Key.Space);
    input.RecordKeyUp(Key.Space);
    input.BeginTick();
    t.Expect("B.3 two taps in one tick are still one press and one release",
        input[Key.Space] is { Down: false, Pressed: true, Released: true });

    // Down then up then down, ending held.
    input.RecordKeyDown(Key.Q);
    input.RecordKeyUp(Key.Q);
    input.RecordKeyDown(Key.Q);
    input.BeginTick();
    t.Expect("B.4 a tap followed by a hold ends down, having pressed and released",
        input[Key.Q] is { Down: true, Pressed: true, Released: true });
}

// ============================================================================
// Section C — losing focus and losing a device are the same rule.
// ============================================================================
//
// A window that loses focus with W held is never sent the key-up; a pad unplugged at full throttle
// never sends the trigger back to zero. Both leave a game holding an input that stopped existing,
// and both show up as something that keeps happening for no reason the game can find.
{
    var input = new InputState();
    input.RecordKeyDown(Key.W);
    input.RecordKeyDown(Key.LeftShift);
    input.RecordMouseDown(MouseButton.Left);
    input.BeginTick();
    t.Expect("C.0 CONTROL three inputs are held before focus is lost",
        input[Key.W].Down && input[Key.LeftShift].Down && input[MouseButton.Left].Down);

    input.ReleaseAll();
    input.BeginTick();
    t.Expect("C.1 losing focus releases every held key",
        input[Key.W] is { Down: false, Released: true }
        && input[Key.LeftShift] is { Down: false, Released: true });
    t.Expect("C.2 and every held mouse button",
        input[MouseButton.Left] is { Down: false, Released: true });

    input.BeginTick();
    t.Expect("C.3 a second ReleaseAll on nothing held reports nothing",
        input[Key.W] is { Down: false, Released: false });

    // The release is synthesised once, not every tick until something happens.
    input.ReleaseAll();
    input.BeginTick();
    t.Expect("C.4 releasing what is already released is silent",
        input[Key.W] is { Released: false });
}

// ============================================================================
// Section D — the mouse reports two kinds of number, and they behave differently.
// ============================================================================
{
    var input = new InputState();

    input.RecordMouseMove(new Vector2(10, 10), new Vector2(10, 10));
    input.RecordMouseMove(new Vector2(14, 13), new Vector2(4, 3));
    input.RecordMouseMove(new Vector2(20, 13), new Vector2(6, 0));
    input.BeginTick();

    t.Expect("D.1 position is the LATEST sample, not a sum",
        input.MousePosition == new Vector2(20, 13), input.MousePosition.ToString());
    t.Expect("D.2 delta is the SUM of every report in the tick",
        input.MouseDelta == new Vector2(20, 13), input.MouseDelta.ToString());

    input.BeginTick();
    t.Expect("D.3 delta resets when nothing moved", input.MouseDelta == Vector2.Zero);
    t.Expect("D.4 position does NOT reset — it is where the pointer is",
        input.MousePosition == new Vector2(20, 13));

    input.RecordMouseWheel(new Vector2(0, 1));
    input.RecordMouseWheel(new Vector2(0, 2));
    input.BeginTick();
    t.Expect("D.5 wheel accumulates within a tick", input.MouseWheel == new Vector2(0, 3));
    input.BeginTick();
    t.Expect("D.6 and resets after it", input.MouseWheel == Vector2.Zero);
}

// ============================================================================
// Section E — the backend's vocabulary arrives intact.
// ============================================================================
//
// A key the mapping forgets becomes Key.Unknown, which is indistinguishable from a key the hardware
// does not have. So the gap is not visible to a consumer as a gap; it is a key that does nothing,
// for a reason nobody can find by reading their own code.
{
    var silkKey = typeof(Silk.NET.Input.Key);
    var mapKey = typeof(Blix.Runtime.Silk.Window)
        .GetMethod("MapKey", BindingFlags.NonPublic | BindingFlags.Static);

    t.Expect("E.0 CONTROL the mapping was found to test", mapKey is not null);

    var names = Enum.GetNames(silkKey).Where(n => n != "Unknown").ToArray();
    t.Expect("E.0 CONTROL the backend declares a keyboard worth mapping",
        names.Length >= 100, $"{names.Length} key(s)");

    var unmapped = new List<string>();
    foreach (var name in names)
    {
        var value = Enum.Parse(silkKey, name);
        if ((Key)mapKey!.Invoke(null, new[] { value })! == Key.Unknown) unmapped.Add(name);
    }

    t.Expect("E.1 every key the backend can report has a Blix value",
        unmapped.Count == 0, string.Join(", ", unmapped.Take(12)));

    // The pad's vocabulary has the same obligation, and the same failure if it is not met.
    var mapButton = typeof(Blix.Runtime.Silk.Window)
        .GetMethod("MapGamepadButton", BindingFlags.NonPublic | BindingFlags.Static);
    t.Expect("E.0 CONTROL the gamepad mapping was found to test", mapButton is not null);

    var buttonNames = Enum.GetNames(typeof(Silk.NET.Input.ButtonName)).Where(n => n != "Unknown").ToArray();
    t.Expect("E.0 CONTROL the backend declares a pad worth mapping",
        buttonNames.Length >= 10, $"{buttonNames.Length} button(s)");

    var unmappedButtons = buttonNames
        .Where(n => (GamepadButton)mapButton!.Invoke(
            null, new[] { Enum.Parse(typeof(Silk.NET.Input.ButtonName), n) })! == GamepadButton.Unknown)
        .ToArray();
    t.Expect("E.3 every gamepad button the backend can report has a Blix value",
        unmappedButtons.Length == 0, string.Join(", ", unmappedButtons));

    // And the distinction the split exists for: the keypad is not the number row.
    t.Expect("E.2 keypad digits are their own keys, not the number row",
        (Key)mapKey!.Invoke(null, new object[] { Enum.Parse(silkKey, "Keypad4") })! == Key.Keypad4);
    t.Expect("E.2 and the number row is still the number row",
        (Key)mapKey!.Invoke(null, new object[] { Enum.Parse(silkKey, "Number4") })! == Key.Number4);
}

// ============================================================================
// Section F — a gamepad is the same model, plus a lifetime.
// ============================================================================
//
// Buttons and axes need nothing new: a button is a button and an axis is the continuous twin of
// one. What a pad adds is that it can stop existing mid-hold, which a keyboard cannot, and that is
// the only part worth testing hard.
{
    var input = new InputState();

    // A pad nobody plugged in reads as a pad holding nothing, rather than as a null reference.
    t.Expect("F.1 an absent pad reads neutral, not null",
        input.Gamepads[0] is { Connected: false } absent
        && !absent[GamepadButton.A].Down && absent.LeftStick == Vector2.Zero);

    input.RecordGamepadConnected(0, "Test Pad");
    input.BeginTick();
    t.Expect("F.2 a pad that appeared says so for one tick",
        input.Gamepads[0] is { Connected: true, ConnectedThisTick: true, Name: "Test Pad" });

    input.BeginTick();
    t.Expect("F.3 and not for the tick after",
        input.Gamepads[0] is { Connected: true, ConnectedThisTick: false });

    // Buttons behave exactly as a key does.
    input.RecordGamepadButton(0, GamepadButton.A, pressed: true);
    input.BeginTick();
    t.Expect("F.4 a pad button presses once", input.Gamepads[0][GamepadButton.A] is { Down: true, Pressed: true });
    input.BeginTick();
    t.Expect("F.5 and holds without pressing again", input.Gamepads[0][GamepadButton.A] is { Down: true, Pressed: false });

    // Axes are state, and remember where they were.
    input.RecordGamepadAxis(0, GamepadAxis.LeftX, 0.25f);
    input.BeginTick();
    input.RecordGamepadAxis(0, GamepadAxis.LeftX, 0.75f);
    input.BeginTick();
    var axis = input.Gamepads[0][GamepadAxis.LeftX];
    t.Expect("F.6 an axis carries value, previous and a derived delta",
        MathF.Abs(axis.Value - 0.75f) < 1e-6f
        && MathF.Abs(axis.Previous - 0.25f) < 1e-6f
        && MathF.Abs(axis.Delta - 0.5f) < 1e-6f,
        $"{axis.Value} / {axis.Previous} / {axis.Delta}");

    // Raw: nothing here has an opinion about how small a movement counts.
    input.RecordGamepadAxis(0, GamepadAxis.RightX, 0.05f);
    input.BeginTick();
    t.Expect("F.7 a small stick movement is reported, not swallowed by a deadzone",
        MathF.Abs(input.Gamepads[0][GamepadAxis.RightX].Value - 0.05f) < 1e-6f);

    // The case the lifetime exists for: unplugged at full throttle, holding a button.
    input.RecordGamepadAxis(0, GamepadAxis.RightTrigger, 1.0f);
    input.BeginTick();
    t.Expect("F.0 CONTROL the pad is holding A and full throttle before it is pulled",
        input.Gamepads[0][GamepadButton.A].Down
        && MathF.Abs(input.Gamepads[0].RightTrigger - 1.0f) < 1e-6f);

    input.RecordGamepadDisconnected(0);
    input.BeginTick();
    var gone = input.Gamepads[0];
    t.Expect("F.8 the disconnect tick reports the pad leaving",
        gone is { Connected: false, DisconnectedThisTick: true });
    t.Expect("F.9 and synthesises the release it will never be sent",
        gone[GamepadButton.A] is { Down: false, Released: true },
        "otherwise the game is holding a button that stopped existing");
    t.Expect("F.10 and zeroes the throttle, rather than leaving it stuck on",
        MathF.Abs(gone.RightTrigger) < 1e-6f, gone.RightTrigger.ToString());

    input.BeginTick();
    t.Expect("F.11 one tick later the pad is forgotten",
        input.Gamepads.Count == 0 && !input.Gamepads[0].DisconnectedThisTick);

    // Reconnecting is an appearance, not a resurrection: nothing is still held.
    input.RecordGamepadConnected(0, "Test Pad");
    input.BeginTick();
    t.Expect("F.12 a reconnected pad starts holding nothing",
        input.Gamepads[0] is { Connected: true, ConnectedThisTick: true }
        && !input.Gamepads[0][GamepadButton.A].Down);

    // Ids, not positions: unplugging pad 0 must not promote pad 1 into its place.
    input.RecordGamepadConnected(1, "Second");
    input.BeginTick();
    input.RecordGamepadDisconnected(0);
    input.BeginTick();
    input.BeginTick();
    t.Expect("F.13 the surviving pad keeps its own id when another leaves",
        input.Gamepads.Count == 1 && input.Gamepads[1] is { Connected: true, Name: "Second" }
        && !input.Gamepads[0].Connected,
        "indexing by position would have handed the game a different controller");
}

t.PrintSummary();
return t.Failed;

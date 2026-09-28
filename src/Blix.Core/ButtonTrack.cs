namespace Blix.Core;

/// <summary>
/// The live and per-tick halves of one set of buttons.
/// </summary>
/// <remarks>
/// Double-buffered rather than read live, so that when a platform delivers an event matters to
/// nobody. Today's host polls between ticks and reading the live set would happen to work;
/// "happens to work" is how a timing assumption gets discovered years later by a backend that
/// does it differently.
/// </remarks>
internal sealed class ButtonTrack(int count)
{
    private readonly bool[] liveDown = new bool[count];
    private readonly bool[] pressedAccum = new bool[count];
    private readonly bool[] releasedAccum = new bool[count];

    private readonly bool[] tickDown = new bool[count];
    private readonly bool[] tickPressed = new bool[count];
    private readonly bool[] tickReleased = new bool[count];

    public ButtonState this[int i] =>
        (uint)i < (uint)count
            ? new ButtonState(tickDown[i], tickPressed[i], tickReleased[i])
            : default;

    public void Down(int i)
    {
        if ((uint)i >= (uint)count) return;
        // Only a transition counts. A backend that repeats key-down while a key is held must
        // not turn one press into one per repeat.
        if (!liveDown[i]) pressedAccum[i] = true;
        liveDown[i] = true;
    }

    public void Up(int i)
    {
        if ((uint)i >= (uint)count) return;
        if (liveDown[i]) releasedAccum[i] = true;
        liveDown[i] = false;
    }

    public void BeginTick()
    {
        Array.Copy(liveDown, tickDown, count);
        Array.Copy(pressedAccum, tickPressed, count);
        Array.Copy(releasedAccum, tickReleased, count);
        Array.Clear(pressedAccum);
        Array.Clear(releasedAccum);
    }

    public void ReleaseAll()
    {
        for (var i = 0; i < count; i++)
        {
            if (liveDown[i]) releasedAccum[i] = true;
            liveDown[i] = false;
        }
    }
}

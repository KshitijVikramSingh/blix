namespace Blix;

// A thing that mutates state as a function of time. Sample is called once per tick by
// whichever host the animation is attached to. Returns true while the animation is
// running; returning false signals "done, please remove me" — the host drops the
// animation in the same tick.
//
// Infinite animations (loops, constant-rate rotations) just return true forever.
// Finite animations return false once their duration is exceeded or their target
// state is reached.
public interface IAnimation
{
    bool Sample(Time time);
}

namespace Blix;

// A thing that changes state over time, on its own clock. Advance moves it on by `delta` seconds and
// writes whatever it animates; it returns true while running, and false to say "done, remove me" —
// the host drops it in the same tick. Infinite animations (loops, constant-rate motion) return true
// forever; finite ones return false once their duration has passed.
//
// Each animation owns its time. That is what lets one pause (stop advancing it), change rate (advance
// it by a scaled delta), restart or scrub (set its elapsed time) without being rebuilt — the shape
// ClipPlayer.Advance proved. It is also what a fixed-step loop drives: the host decides the delta,
// the animation never reads a wall clock or an absolute total.
public interface IAnimation
{
    bool Advance(double delta);
}

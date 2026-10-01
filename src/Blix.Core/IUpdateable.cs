namespace Blix;

// Universal "tick me each frame" contract. Game code that wants per-frame work in a
// uniform shape implements this and gets driven from a caller's update loop.
//
// Opt-in: a thing that only sits in a scene is not IUpdateable, so it pays no virtual call
// and claims no behaviour it lacks. Whether the host ticks these, and on which clock, is the
// fixed-step decision (plan.md §J4).
public interface IUpdateable
{
    void Update(Time time);
}

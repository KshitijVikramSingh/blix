namespace Blix;

// Reusable animation-list host: anything that wants to own animations composes one of these, rather
// than reimplementing the list + reverse-iterate-and-remove logic per class. PhysicsHost3D (in
// PhysicsHost3D.cs) is the sibling pattern for motion integration; a type that wants both behaviours
// composes both hosts.
//
// Animations advance in the order they were added, and one that reports done is removed in the same
// tick without disturbing the others' order. The host owns no time of its own: it hands each
// animation the delta it was given.
public sealed class AnimationHost : IUpdateable
{
    private readonly List<IAnimation> animations = new();

    /// <summary>How many animations are still running.</summary>
    public int Count => animations.Count;

    public void AddAnimation(IAnimation animation)
    {
        ArgumentNullException.ThrowIfNull(animation);
        animations.Add(animation);
    }

    /// <summary>Advances every animation by <paramref name="delta"/> seconds, in the order added, and drops the finished.</summary>
    public void Advance(double delta)
    {
        // In order, compacting in place: an animation that finishes is skipped over and the survivors
        // keep their order, with no list copy per frame.
        var kept = 0;
        for (var i = 0; i < animations.Count; i++)
        {
            var animation = animations[i];
            if (animation.Advance(delta)) animations[kept++] = animation;
        }

        animations.RemoveRange(kept, animations.Count - kept);
    }

    // As an IUpdateable, the frame's delta. Whether hosts are ticked through IUpdateable at all is the
    // fixed-step decision (plan.md §J4).
    void IUpdateable.Update(Time time) => Advance(time.Delta);
}

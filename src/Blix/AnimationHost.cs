namespace Blix;

// Reusable animation-list host: anything that wants to own animations composes one of
// these, rather than reimplementing the list + reverse-iterate-and-remove logic
// per class. The host implements IUpdateable + IAnimated so callers can either expose
// it directly or wrap it.
//
// AnimatedGameObject delegates AddAnimation/Update to a private host. PhysicsHost3D
// (in PhysicsHost3D.cs) is the sibling pattern for motion integration; a GameObject
// subclass that wants both behaviours composes both hosts directly.
public sealed class AnimationHost : IUpdateable, IAnimated
{
    private readonly List<IAnimation> animations = new();

    public void AddAnimation(IAnimation animation)
    {
        ArgumentNullException.ThrowIfNull(animation);
        animations.Add(animation);
    }

    public void Update(Time time)
    {
        // Reverse iteration keeps the index walk valid as items are removed; no list
        // copy per frame.
        for (var i = animations.Count - 1; i >= 0; i--)
        {
            if (!animations[i].Sample(time))
            {
                animations.RemoveAt(i);
            }
        }
    }
}

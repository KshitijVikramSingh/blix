using Blix.Render;

namespace Blix;

// A GameObject that hosts animations and ticks them as its own update. Animations are
// attached after construction (the typical case needs `obj.Transform` as a target,
// which doesn't exist until the object is constructed).
//
// The animation list + iterate-and-remove machinery lives on AnimationHost so other
// "I host animations" types (a future AnimatedCamera, e.g.) can compose the same
// behaviour without reimplementing it. This class is now a thin delegate.
public sealed class AnimatedGameObject : GameObject, IUpdateable, IAnimated
{
    private readonly AnimationHost host = new();

    public AnimatedGameObject(string name, Mesh mesh, Material material, Transform3D? transform = null)
        : base(name, mesh, material, transform)
    {
    }

    public void AddAnimation(IAnimation animation) => host.AddAnimation(animation);

    public void Update(Time time) => host.Update(time);
}

using Blix.Graphics;
using Blix.Render;

namespace Blix;

// A GameObject that composes a PhysicsHost3D. Sibling of AnimatedGameObject — both are
// thin convenience wrappers that lift a single host's behaviour onto a typed object.
//
// For an object that needs both animations AND physics, game code defines its own
// GameObject subclass that composes both hosts directly. The engine intentionally does
// not ship an AnimatedPhysicsGameObject (combinatorial explosion of subclasses).
public sealed class PhysicsGameObject : GameObject, IFixedUpdateable
{
    public PhysicsHost3D Physics { get; }

    public PhysicsGameObject(string name, Mesh mesh, MaterialHandle material, Transform3D? transform = null)
        : base(name, mesh, material, transform)
    {
        Physics = new PhysicsHost3D { Target = Transform };
    }

    public void FixedUpdate(Time time) => Physics.FixedUpdate(time);
}

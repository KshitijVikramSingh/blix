using System.Numerics;

namespace Blix;

// 2D analogue of PhysicsHost3D. Velocity integration into a Transform2D, with the
// same scoping decisions: no mass, no forces, no impulses — those land alongside
// the first feature that needs them. Gravity is a 2D vector with Y-down default
// matching screen-space convention (positive Y goes down on screen, gravity
// pulls bodies "down" along +Y).
//
// Collision detection and response are NOT this host's job. The host advances
// motion; the game (or a higher-level resolver) calls Intersection2D.Test after
// integration and writes back to Velocity / Target.Position. The engine holds no
// opinion about what contact means.
public sealed class PhysicsHost2D : IFixedUpdateable
{
    public Transform2D Target { get; init; } = null!;

    public Vector2 Velocity { get; set; }

    public float GravityScale { get; set; }

    // Y-down default: matches screen-space ortho where (0,0) is top-left and Y
    // grows downward. Games that want world-style Y-up should set this to (0, -9.81)
    // or (0, 9.81) depending on the convention they're using.
    public Vector2 Gravity { get; set; } = new(0.0f, 9.81f);

    public void FixedUpdate(Time time)
    {
        if (Target is null) return;
        var dt = (float)time.Delta;
        Velocity += Gravity * GravityScale * dt;
        Target.Position += Velocity * dt;
    }
}

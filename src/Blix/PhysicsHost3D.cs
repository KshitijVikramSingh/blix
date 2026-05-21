using System.Numerics;

namespace Blix;

// Reusable motion-integration host: anything that wants velocity-driven motion
// composes one of these and tells it which Transform3D to write to. Sibling of
// AnimationHost — the engine's chosen composition unit is "a host per behaviour."
//
// v0 is purely kinematic: gravity (an acceleration) is integrated into velocity,
// velocity is integrated into position. There is no Mass field because mass only
// matters when forces or impulses are applied — adding it before impulses exist would
// be a no-op field with a confusing name. Mass + ApplyForce / ApplyImpulse land
// alongside the first feature that actually needs them.
//
// Collision detection and response are NOT this host's job. The host advances motion;
// the host's owner (or a higher-level resolver loop) calls Intersection.Test / .Sweep
// after integration and writes back to Velocity / Target.Position as it sees fit. The
// engine deliberately holds no opinion about whether contact means "stop," "bounce,"
// "slide," or "trigger an event" — those are game-shaped decisions.
public sealed class PhysicsHost3D : IFixedUpdateable
{
    public Transform3D Target { get; init; } = null!;

    public Vector3 Velocity { get; set; }

    // Multiplier on Gravity. 0 disables gravity for this body without forcing the
    // caller to flip Gravity to Vector3.Zero (which would lose direction info).
    public float GravityScale { get; set; }

    // Per-host so a single demo can run different bodies under different gravity (a
    // moon-gravity zone, a zero-G section). Earth-like Y-down by convention. Games
    // that want a global gravity can set this once at construction and forget it.
    public Vector3 Gravity { get; set; } = new(0.0f, -9.81f, 0.0f);

    // Fixed-step integration: stable Δt produces consistent trajectories across frame
    // rates and avoids the overshoot/undershoot that variable-Δt physics suffers when
    // the renderer hitches. Game.OnFixedUpdate calls this at the engine's configured
    // FixedStep cadence.
    public void FixedUpdate(Time time)
    {
        if (Target is null) return;
        var dt = (float)time.Delta;
        Velocity += Gravity * GravityScale * dt;
        Target.Position += Velocity * dt;
    }
}

using System.Numerics;

namespace Blix.Geometry;

// Stateless velocity-response helpers. Pure vector math; no policy. Game code picks
// the response and calls the matching helper after a Test or Sweep returned a hit.
//
// Maps roughly to five canonical contact-mode vocabularies that game code tends to
// pick from (vocabulary only — there is no engine-level enum):
//
//   None     — ignore the hit entirely (no velocity change, no depenetration).
//   Trigger  — fire a callback / event; don't touch velocity or position.
//   Stop     — `velocity = Vector3.Zero`. Object halts on contact. No helper needed.
//   Slide    — kill the component of velocity moving INTO the surface, preserve
//              tangential motion. → `RemoveNormalComponent`.
//   Bounce   — reflect the into-surface component with a restitution coefficient.
//              → `ReflectVelocity`. `restitution = 0` collapses to Slide.
//
// "Stick" (Stop + lock position) and per-axis friction are higher-level behaviours
// that combine these primitives; they aren't separate helpers yet.
public static class CollisionResponse
{
    // Bounce. Reflect the velocity component along the contact normal with restitution
    // in [0, 1] — 0 collapses to slide (kills normal velocity), 1 is a perfectly elastic
    // bounce. Negative `velocity · normal` means the body is moving into the surface;
    // when it's already moving away, no change.
    public static Vector3 ReflectVelocity(Vector3 velocity, Vector3 normal, float restitution)
    {
        var alongNormal = Vector3.Dot(velocity, normal);
        if (alongNormal >= 0.0f) return velocity;
        return velocity - normal * alongNormal * (1.0f + restitution);
    }

    // Slide. Removes the velocity component pointing into the surface while leaving
    // tangential motion alone. Use for objects that should keep moving along a wall
    // or floor without bouncing off it. Equivalent to `ReflectVelocity(..., 0)`, but
    // named for the intent.
    public static Vector3 RemoveNormalComponent(Vector3 velocity, Vector3 normal)
    {
        var alongNormal = Vector3.Dot(velocity, normal);
        if (alongNormal >= 0.0f) return velocity;
        return velocity - normal * alongNormal;
    }

    // Two-body bounce. Both sides update based on their relative
    // NormalisedInverseMass — kinematically equivalent to 1 / mass, scaled so that
    // 1.0 is the baseline "unit-mobile" body and 0.0 means anchored (doesn't move).
    // Avoids committing to forces / impulses / F=ma: nothing here is integrated,
    // it's a per-collision velocity adjustment using mass-equivalent ratios.
    //
    // Math:
    //   v_rel · n < 0  means the bodies are approaching along the contact normal.
    //   J = -(1 + r) * (v_rel · n) / (mA + mB)   where mA, mB are the inverse masses
    //   ΔvA = +J * mA * n   (A pushed in +normal direction)
    //   ΔvB = -J * mB * n   (B pushed in -normal direction)
    //
    // Each body's velocity change is proportional to ITS inverse mass — the lighter
    // (more mobile) body absorbs more of the impulse, which matches intuition. An
    // anchored body (inverse mass 0) gets no velocity change; the other body absorbs
    // the full impulse, recovering the single-body ReflectVelocity case exactly.
    //
    // The "both anchored" degenerate case (mA + mB == 0) returns inputs unchanged —
    // immovable objects in contact have no defined response.
    //
    // Restitution is in [0, 1]: 0 = inelastic (no bounce, kills normal-component
    // velocity), 1 = perfectly elastic (full reflection, no energy loss).
    public static (Vector3 NewVelocityA, Vector3 NewVelocityB) ReflectVelocities(
        Vector3 velocityA, float normalisedInverseMassA,
        Vector3 velocityB, float normalisedInverseMassB,
        Vector3 normal, float restitution)
    {
        var relative = velocityA - velocityB;
        var alongNormal = Vector3.Dot(relative, normal);
        if (alongNormal >= 0.0f) return (velocityA, velocityB);   // separating

        var totalMobility = normalisedInverseMassA + normalisedInverseMassB;
        if (totalMobility <= 0.0f) return (velocityA, velocityB); // both anchored

        var impulseMagnitude = -(1.0f + restitution) * alongNormal / totalMobility;
        var impulse = normal * impulseMagnitude;
        return (
            velocityA + impulse * normalisedInverseMassA,
            velocityB - impulse * normalisedInverseMassB);
    }
}

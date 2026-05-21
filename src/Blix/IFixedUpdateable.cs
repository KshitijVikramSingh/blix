namespace Blix;

// Sibling of IUpdateable for things that tick at a fixed timestep rather than the
// variable frame rate. Physics is the canonical case: integration accuracy depends on
// stable Δt, and frame-rate-dependent physics produces visibly different behaviour at
// different display refreshes.
//
// The method is named `FixedUpdate` (not `Update`) to dodge the same-signature collision
// with IUpdateable — a single class can implement both interfaces with separate method
// bodies, ticking variable-rate behaviour in `Update` and fixed-rate behaviour in
// `FixedUpdate`. The engine's loop (Game base class) accumulates frame deltas into a
// FixedStepClock and dispatches FixedUpdate at the fixed cadence.
public interface IFixedUpdateable
{
    void FixedUpdate(Time time);
}

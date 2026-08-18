using System.Numerics;
using RTSGame.Simulation.Agents;
using RTSGame.Simulation.Collision;
using RTSGame.Simulation.Navigation;
using RTSGame.Simulation.Spatial;

namespace RTSGame.Simulation.Movement;

/// <summary>
/// Turns each agent's path intent into a body velocity.
/// </summary>
/// <remarks>
/// The reciprocal velocity solve is the only controller. This layer converts a
/// path into a desired velocity and hands it over; it neither steers sideways nor
/// scales speed.
/// <para>
/// It used to also run an explicit queue: find the ally ahead in your lane, clamp
/// your speed to hold a gap behind it. That was the last of four controllers
/// competing for the same vector, and it was the worst behaved — leader selection
/// was recomputed from scratch every tick, so a few centimetres of jostle flipped
/// membership, the speed clamp switched on and off several times a second, and
/// the crowd milled around a gate instead of waiting in it. Queueing is not a
/// thing that needs implementing: a body that cannot pass slows down because the
/// velocity solve gives it nowhere to go, which is what a queue is.
/// </para>
/// </remarks>
internal sealed class LocalSteeringSystem
{
    // Below this speed a resolved velocity is mostly avoidance noise, and facing
    // it makes a body pirouette in place.
    private const float FacingDeadZone = 0.30f;

    private readonly ReciprocalVelocitySolver reciprocalSolver = new();

    public ReciprocalVelocitySolver Solver => reciprocalSolver;
    private Vector2[] steeredVelocities = Array.Empty<Vector2>();
    private bool[] crowdPressured = Array.Empty<bool>();

    public void Update(
        AgentStore agents,
        AgentSpatialIndex index,
        PathService paths,
        float deltaSeconds)
    {
        EnsureCapacity(agents.Count);
        index.Rebuild(agents.All);
        var source = agents.All;

        for (var i = 0; i < source.Length; i++)
        {
            ref readonly var agent = ref source[i];
            if (!agent.IsAlive)
            {
                steeredVelocities[i] = Vector2.Zero;
                crowdPressured[i] = false;
                continue;
            }
            if (!agent.HasDestination)
            {
                // An idle body holds its ground. It is not this layer's job to
                // scuttle it out of someone's way: contact resolution moves it if
                // it is genuinely in the way, and SimulationWorld walks it home
                // afterwards. Steering idle units was the largest single source
                // of restless, insect-like motion in a settled formation.
                steeredVelocities[i] = MoveTowards(
                    agent.Velocity,
                    Vector2.Zero,
                    agent.Acceleration * deltaSeconds);
                crowdPressured[i] = false;
                continue;
            }

            var heading = NormalizedOrZero(agent.PreferredVelocity);
            var preferredSpeed = agent.PreferredVelocity.Length();
            var desired = agent.PreferredVelocity;

            // A right-of-way rule was tried here for the doorway standoff: yield
            // to any ally with a better claim (distance still to travel, tie-broken
            // by id) when approaching a constriction. It is measurably not worth
            // it. Broadly scoped it made most of a crowd give way to most of the
            // rest and the approach crawled (pen 18.9s -> 26.7s); narrowed to
            // genuine lane contention it helped one pen seed and hurt the other
            // (17.1s / 20.3s against 18.9s / 16.5s without) while costing a test.
            // Bodies swapping turns at a gap is an artefact of the velocity solve
            // answering a symmetric contest with a tangential escape, and it wants
            // fixing there rather than papering over in the layer above.
            if (agent.CongestionYieldSeconds > 0f) desired = Vector2.Zero;
            if (desired.LengthSquared() > agent.MaximumSpeed * agent.MaximumSpeed)
            {
                desired = Vector2.Normalize(desired) * agent.MaximumSpeed;
            }

            steeredVelocities[i] = MoveTowards(
                agent.Velocity,
                desired,
                agent.Acceleration * deltaSeconds);
            var resolvedForwardSpeed = heading == Vector2.Zero
                ? desired.Length()
                : MathF.Max(0f, Vector2.Dot(desired, heading));
            crowdPressured[i] = preferredSpeed > 0.25f &&
                                resolvedForwardSpeed < preferredSpeed * 0.62f;
        }

        reciprocalSolver.Solve(agents, index, steeredVelocities, paths, deltaSeconds);

        var mutable = agents.MutableSpan();
        for (var i = 0; i < mutable.Length; i++)
        {
            ref var agent = ref mutable[i];
            if (!agent.IsAlive) continue;
            agent.Velocity = LimitTurn(agent, steeredVelocities[i], deltaSeconds);
            // Face the intent, not the avoidance output. A body that turns to
            // meet every reciprocal correction reads as twitchy even when its
            // path is perfectly smooth.
            var facingTarget = agent.PreferredVelocity.LengthSquared() > FacingDeadZone * FacingDeadZone
                ? agent.PreferredVelocity
                : agent.Velocity;
            if (facingTarget.LengthSquared() > FacingDeadZone * FacingDeadZone)
            {
                agent.Facing = RotateTowards(
                    agent.Facing,
                    Vector2.Normalize(facingTarget),
                    agent.MaximumTurnSpeed * deltaSeconds);
            }

            if (!agent.HasDestination) continue;
            if (crowdPressured[i])
            {
                agent.CrowdPressureSeconds = MathF.Max(agent.CrowdPressureSeconds, 0.25f);
            }
        }
    }


    /// <summary>
    /// Applies the body's turn rate to the solved velocity, keeping its speed.
    /// </summary>
    /// <remarks>
    /// The velocity solve answers "where could I go this instant", and at a
    /// contested gap that answer swings hard every tick as the feasible region
    /// shifts under it. A body cannot follow that, and letting it try is what the
    /// spinning was: measured at over 200 degrees a second of direction change,
    /// sustained. A unit already moving must turn before it can go somewhere
    /// else; one that is nearly stopped may pivot on the spot.
    /// <para>
    /// Clamping the retreat component instead was tried — a blocked body waits
    /// rather than backing off — and it does not touch this at all (7.0 and 9.3
    /// degrees per tick, against 7.1 and 8.2 unclamped). The swinging is lateral,
    /// not reversal, so only a turn rate addresses it.
    /// </para>
    /// <para>
    /// It is a flat rate, and scaling it by speed — which is the honest physics, since
    /// what limits a turn is lateral acceleration and so <c>ω = a/v</c> — was measured and
    /// is much worse: one unit never escaped the pen at all, dead stops at a one-cell gate
    /// went from 5 to 288, infeasible solves from 3.8% to 13.2%. The reason is that this
    /// limit does two jobs. It is a physical bound, and it is also the low-pass that keeps
    /// the velocity solve from spinning bodies on the spot. In a crowd every body is slow,
    /// so scaling by speed lifts the limit precisely where it was doing the most work.
    /// A stuck body that ought to turn round is a real problem, but it is not this one:
    /// it can already rotate, and what keeps it pointed at the obstruction is that its
    /// intent keeps pointing there. That belongs in the layer above.
    /// </para>
    /// </remarks>
    private static Vector2 LimitTurn(in AgentState agent, Vector2 solved, float deltaSeconds)
    {
        var speed = solved.Length();
        if (speed <= 0.0001f) return solved;
        var current = agent.Velocity;
        if (current.LengthSquared() <= AgentDefaults.FreeTurnSpeed * AgentDefaults.FreeTurnSpeed)
        {
            return solved;
        }
        var heading = RotateTowards(
            Vector2.Normalize(current),
            solved / speed,
            agent.MaximumTurnSpeed * deltaSeconds);
        return heading * speed;
    }

    private void EnsureCapacity(int count)
    {
        if (steeredVelocities.Length < count) Array.Resize(ref steeredVelocities, count);
        if (crowdPressured.Length < count) Array.Resize(ref crowdPressured, count);
    }

    private static float Cross(Vector2 first, Vector2 second) => first.X * second.Y - first.Y * second.X;

    private static Vector2 NormalizedOrZero(Vector2 value) =>
        value.LengthSquared() > 0.0001f ? Vector2.Normalize(value) : Vector2.Zero;



    private static Vector2 MoveTowards(Vector2 current, Vector2 target, float maximumDelta)
    {
        var delta = target - current;
        var distance = delta.Length();
        return distance <= maximumDelta || distance < 0.00001f
            ? target
            : current + delta / distance * maximumDelta;
    }

    private static Vector2 RotateTowards(Vector2 current, Vector2 target, float maximumRadians)
    {
        current = NormalizedOrZero(current);
        if (current == Vector2.Zero) return target;
        var cross = Cross(current, target);
        var dot = Math.Clamp(Vector2.Dot(current, target), -1f, 1f);
        var angle = MathF.Atan2(cross, dot);
        if (MathF.Abs(angle) <= maximumRadians) return target;
        var turn = MathF.CopySign(maximumRadians, angle);
        var cosine = MathF.Cos(turn);
        var sine = MathF.Sin(turn);
        return new Vector2(
            current.X * cosine - current.Y * sine,
            current.X * sine + current.Y * cosine);
    }
}

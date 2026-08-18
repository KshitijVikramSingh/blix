using System.Numerics;
using RTSGame.Simulation.Agents;
using RTSGame.Simulation.Navigation;
using RTSGame.Simulation.Spatial;

namespace RTSGame.Simulation.Movement;

/// <summary>
/// Reciprocal velocity-obstacle solve for dynamic circular agents. Static
/// terrain remains the path/sweep layer's responsibility; this solver prevents
/// moving bodies from manufacturing penetrations that the position solver would
/// otherwise have to undo.
/// </summary>
internal sealed class ReciprocalVelocitySolver
{
    private const float NeighborDistance = 3f;
    // Reusing a fraction of last tick's answer as the optimisation target gives
    // the solve a memory. Without it an agent re-derives which side to pass on
    // from scratch every tick and flips between symmetric solutions, which is
    // what a crowd of continuously re-negotiating bodies looks like.
    private const float VelocityCommitment = 0.25f;
    // This is local collision avoidance, not route reservation. A long horizon
    // makes a dense merge reserve several body widths of hypothetical future
    // space and can reduce an otherwise valid crowd to a near-zero velocity.
    private const float TimeHorizon = 0.75f;
    // A body that is standing still is not going to walk into anybody, so there
    // is nothing to predict: reserving three quarters of a second of space around
    // it just makes passers-by swerve from a distance. A late, tight horizon
    // reads as walking past someone rather than avoiding them, and lets contact
    // resolution do the last centimetre of the job.
    private const float StationaryTimeHorizon = 0.30f;
    // Ceiling on how fast an agent with no destination may be steered.
    private const float IdleYieldSpeed = 1.10f;
    // Overlap is bled off over several ticks rather than in one, so a contact
    // cannot translate into an unsatisfiable single-tick velocity demand.
    private const float OverlapRecoverySeconds = 0.25f;
    /// <summary>Extra separation aimed for by bodies already in contact.</summary>
    private const float ContactSeparationMargin = 0.015f;
    private const float Epsilon = 0.00001f;
    private readonly List<VelocityLine> lines = new();
    private readonly List<VelocityLine> projectedLines = new();
    private readonly List<int> neighbors = new();
    private bool[] blocked = Array.Empty<bool>();
    private Vector2[] solved = Array.Empty<Vector2>();

    /// <summary>Solves whose linear program was infeasible and needed relaxing.</summary>
    public long InfeasibleSolves { get; private set; }
    /// <summary>Solves whose result hit static geometry and fell back to a cone search.</summary>
    public long TerrainFallbacks { get; private set; }
    /// <summary>Cone searches that found nothing and returned a dead stop.</summary>
    public long TerrainFallbackFailures { get; private set; }
    /// <summary>Total solves, for ratios.</summary>
    public long Solves { get; private set; }
    private bool[] velocityChosen = Array.Empty<bool>();
    private int[] solveOrder = Array.Empty<int>();

    public void Solve(
        AgentStore agents,
        AgentSpatialIndex index,
        Vector2[] desiredVelocities,
        PathService paths,
        float deltaSeconds)
    {
        var source = agents.All;
        EnsureCapacity(source.Length);
        Array.Clear(blocked, 0, source.Length);

        // One solve for every map. Branching the responsibility model on whether
        // the terrain had been edited meant flat and sculpted maps ran genuinely
        // different avoidance algorithms, so every constant tuned here applied to
        // exactly one of them. The front-to-back solve is kept because it is the
        // only one of the two that keeps a queue moving through a doorway: a
        // symmetric split has no way to break the standoff and the column
        // negotiates itself to a halt.
        for (var i = 0; i < source.Length; i++) solveOrder[i] = i;
        Array.Clear(velocityChosen, 0, source.Length);
        // Insertion sort is deterministic and allocation-free; typical local
        // crowds are already close to progress order.
        for (var i = 1; i < source.Length; i++)
        {
            var candidate = solveOrder[i];
            var insertion = i - 1;
            while (insertion >= 0 && HasHigherPriority(source[candidate], source[solveOrder[insertion]]))
            {
                solveOrder[insertion + 1] = solveOrder[insertion];
                insertion--;
            }
            solveOrder[insertion + 1] = candidate;
        }

        for (var order = 0; order < source.Length; order++)
        {
            var agentIndex = solveOrder[order];
            ref readonly var agent = ref source[agentIndex];
            if (!agent.IsAlive)
            {
                solved[agentIndex] = Vector2.Zero;
                continue;
            }
            lines.Clear();
            var desired = desiredVelocities[agentIndex];
            index.Query(agent.Position, NeighborDistance, agentIndex, neighbors);
            foreach (var otherIndex in neighbors)
            {
                ref readonly var other = ref source[otherIndex];
                var relativePosition = other.Position - agent.Position;
                var distanceSquared = relativePosition.LengthSquared();
                if (distanceSquared > NeighborDistance * NeighborDistance) continue;
                if (IsShovableAlly(agent, other)) continue;
                // Movers that have not chosen yet are handled when their own turn
                // comes and they see this agent's committed velocity.
                if (other.HasDestination && !velocityChosen[otherIndex]) continue;
                var otherVelocity = velocityChosen[otherIndex] ? solved[otherIndex] : other.Velocity;
                AddAgentLine(
                    agent,
                    other,
                    otherVelocity,
                    1f,
                    other.HasDestination ? TimeHorizon : StationaryTimeHorizon,
                    relativePosition,
                    distanceSquared,
                    deltaSeconds);
            }

            // A body with no order is allowed to shuffle out of the way, not to
            // sprint. Without this cap the solve happily drives a settled unit at
            // full speed to dodge a passer-by, so it flees down the corridor
            // ahead of the traveller and has to walk the whole way back.
            var speedLimit = agent.HasDestination
                ? agent.MaximumSpeed
                : MathF.Min(agent.MaximumSpeed, IdleYieldSpeed);
            if (desired.LengthSquared() > speedLimit * speedLimit)
                desired = Vector2.Normalize(desired) * speedLimit;
            // Bias the optimisation target toward the velocity this body already
            // has. The constraint set is unchanged, so this cannot cause overlap;
            // it only breaks ties toward continuing what the unit was doing.
            var optimizationTarget = agent.HasDestination
                ? Vector2.Lerp(desired, agent.Velocity, VelocityCommitment)
                : desired;
            if (optimizationTarget.LengthSquared() > speedLimit * speedLimit)
                optimizationTarget = Vector2.Normalize(optimizationTarget) * speedLimit;
            Solves++;
            var failedLine = LinearProgram2(lines, speedLimit, optimizationTarget, false, out var result);
            if (failedLine < lines.Count)
            {
                InfeasibleSolves++;
                LinearProgram3(lines, failedLine, speedLimit, ref result);
            }
            if (!VelocityStepIsTerrainSafe(agent, result, paths, deltaSeconds))
            {
                TerrainFallbacks++;
                result = FindTerrainSafeVelocity(agent, desired, paths, deltaSeconds);
                if (result == Vector2.Zero) TerrainFallbackFailures++;
            }
            solved[agentIndex] = result;
            velocityChosen[agentIndex] = true;
            var desiredSpeed = desired.Length();
            var resolvedForwardSpeed = desiredSpeed > Epsilon
                ? Vector2.Dot(result, desired / desiredSpeed)
                : result.Length();
            blocked[agentIndex] = agent.HasDestination && desiredSpeed > 0.25f &&
                                  resolvedForwardSpeed < desiredSpeed * 0.45f;
        }

        Array.Copy(solved, desiredVelocities, source.Length);
        var mutable = agents.MutableSpan();
        for (var i = 0; i < mutable.Length; i++)
        {
            if (!mutable[i].IsAlive) continue;
            mutable[i].AvoidanceBlockedThisTick = blocked[i];
            if (blocked[i])
                mutable[i].CrowdPressureSeconds = MathF.Max(
                    mutable[i].CrowdPressureSeconds,
                    0.25f);
        }
    }

    private void EnsureCapacity(int count)
    {
        if (blocked.Length >= count) return;
        Array.Resize(ref blocked, count);
        Array.Resize(ref solved, count);
        Array.Resize(ref velocityChosen, count);
        Array.Resize(ref solveOrder, count);
    }

    /// <summary>
    /// Share of a pair's correction this agent absorbs.
    /// </summary>
    /// <remarks>
    /// A perfectly even 50/50 split is stable but has no way to break a symmetric
    /// standoff, so a column of units meeting a doorway negotiates itself to a
    /// halt. Skewing the split by progress order restores the right-of-way that
    /// makes a queue flow, while keeping both agents below full responsibility so
    /// neither pair member can be pinned the way the front-to-back solve pinned
    /// them. The weights are antisymmetric, so a pair still sums to exactly one.
    /// </remarks>
    /// <summary>
    /// A mover and a settled ally do not negotiate: the traveller walks through
    /// and the settled body steps aside.
    /// </summary>
    /// <remarks>
    /// The exclusion is symmetric on purpose. Avoiding in one direction only
    /// meant the settled unit still tried to dodge the traveller, and since it is
    /// pushed along the same axis it was fleeing down, it ran several metres
    /// ahead of the mover before turning round to walk back. With neither side
    /// predicting, the only interaction left is the contact solve, which resolves
    /// it as a sidestep. A body with no speed budget is deliberately immovable
    /// and keeps its constraint, so a unit told to hold a doorway still holds it.
    /// </remarks>
    private static bool IsShovableAlly(in AgentState agent, in AgentState other) =>
        agent.HasDestination != other.HasDestination &&
        agent.MaximumSpeed > 0f &&
        other.MaximumSpeed > 0f &&
        agent.Faction == other.Faction;

    private static bool HasHigherPriority(in AgentState candidate, in AgentState incumbent)
    {
        if (candidate.HasDestination != incumbent.HasDestination) return candidate.HasDestination;
        if (!candidate.HasDestination) return candidate.Id.Value < incumbent.Id.Value;
        var candidateDistance = Vector2.DistanceSquared(candidate.Position, candidate.RequestedDestination);
        var incumbentDistance = Vector2.DistanceSquared(incumbent.Position, incumbent.RequestedDestination);
        return candidateDistance < incumbentDistance - 0.0001f ||
               MathF.Abs(candidateDistance - incumbentDistance) <= 0.0001f &&
               candidate.Id.Value < incumbent.Id.Value;
    }

    private Vector2 FindTerrainSafeVelocity(
        in AgentState agent,
        Vector2 desired,
        PathService paths,
        float deltaSeconds)
    {
        var desiredSpeed = desired.Length();
        if (desiredSpeed <= Epsilon) return Vector2.Zero;
        var forward = desired / desiredSpeed;
        var best = Vector2.Zero;
        var bestCost = float.PositiveInfinity;
        ReadOnlySpan<float> speedFractions = [1f, 0.75f, 0.5f, 0.25f, 0.10f];

        for (var degrees = 0; degrees <= 170; degrees += 10)
        {
            var sideCount = degrees == 0 ? 1 : 2;
            for (var side = 0; side < sideCount; side++)
            {
                var signedRadians = degrees * (MathF.PI / 180f) * (side == 0 ? 1f : -1f);
                var cosine = MathF.Cos(signedRadians);
                var sine = MathF.Sin(signedRadians);
                var direction = new Vector2(
                    forward.X * cosine - forward.Y * sine,
                    forward.X * sine + forward.Y * cosine);
                foreach (var fraction in speedFractions)
                {
                    var candidate = direction * desiredSpeed * fraction;
                    if (!SatisfiesAllLines(candidate) ||
                        !VelocityStepIsTerrainSafe(agent, candidate, paths, deltaSeconds))
                    {
                        continue;
                    }
                    var cost = Vector2.DistanceSquared(candidate, desired);
                    if (cost >= bestCost) continue;
                    best = candidate;
                    bestCost = cost;
                }
            }
        }
        return best;
    }

    private bool SatisfiesAllLines(Vector2 velocity)
    {
        foreach (var line in lines)
        {
            if (Determinant(line.Direction, line.Point - velocity) > Epsilon) return false;
        }
        return true;
    }

    private static bool VelocityStepIsTerrainSafe(
        in AgentState agent,
        Vector2 velocity,
        PathService paths,
        float deltaSeconds) =>
        paths.IsContinuousStepClear(
            agent.Position,
            agent.Position + velocity * deltaSeconds,
            agent.Radius);

    private void AddAgentLine(
        in AgentState agent,
        in AgentState other,
        Vector2 otherVelocity,
        float responsibility,
        float timeHorizon,
        Vector2 relativePosition,
        float distanceSquared,
        float deltaSeconds)
    {
        var relativeVelocity = agent.Velocity - otherVelocity;
        // Deliberately near-zero. A wider avoidance margin was tried to stop
        // single-tick contact registering as overlap; it made tight passages
        // measurably worse, because the extra clearance is exactly what a body
        // does not have at a chokepoint. Contact is the position solver's job.
        var combinedRadius = agent.Radius + other.Radius + 0.015f;
        var combinedRadiusSquared = combinedRadius * combinedRadius;
        Vector2 direction;
        Vector2 correction;

        if (distanceSquared > combinedRadiusSquared)
        {
            var inverseHorizon = 1f / timeHorizon;
            var w = relativeVelocity - relativePosition * inverseHorizon;
            var wLengthSquared = w.LengthSquared();
            var projection = Vector2.Dot(w, relativePosition);
            if (projection < 0f && projection * projection > combinedRadiusSquared * wLengthSquared)
            {
                var wLength = MathF.Sqrt(wLengthSquared);
                var unitW = wLength > Epsilon ? w / wLength : StableDirection(agent.Id, other.Id);
                direction = new Vector2(unitW.Y, -unitW.X);
                correction = (combinedRadius * inverseHorizon - wLength) * unitW;
            }
            else
            {
                var leg = MathF.Sqrt(MathF.Max(0f, distanceSquared - combinedRadiusSquared));
                if (Determinant(relativePosition, w) > 0f)
                {
                    direction = new Vector2(
                        relativePosition.X * leg - relativePosition.Y * combinedRadius,
                        relativePosition.X * combinedRadius + relativePosition.Y * leg) /
                                distanceSquared;
                }
                else
                {
                    direction = -new Vector2(
                        relativePosition.X * leg + relativePosition.Y * combinedRadius,
                        -relativePosition.X * combinedRadius + relativePosition.Y * leg) /
                                distanceSquared;
                }
                var projectedVelocity = Vector2.Dot(relativeVelocity, direction) * direction;
                correction = projectedVelocity - relativeVelocity;
            }
        }
        else
        {
            // Already overlapping. Textbook RVO2 demands the whole penetration
            // be undone within one step, which at exact contact distance pins
            // both bodies at zero velocity and stalls the crowd around them.
            // Position-level depenetration now guarantees separation, so this
            // constraint only has to stop the pair closing further.
            //
            // Aim a little past merely touching, though, and only here. Widening
            // the radius everywhere was tried and made tight passages worse — that
            // clearance is exactly what a body does not have at a chokepoint — but
            // this branch runs only on bodies that are already in contact, where
            // free-space manoeuvring is not at stake and the sole job is to come
            // apart. Solving for exact tangency leaves a tick's worth of closing
            // speed unaccounted for, which is the residual overlap that survives.
            var separationRadius = combinedRadius + ContactSeparationMargin;
            var inverseStep = 1f / MathF.Max(OverlapRecoverySeconds, deltaSeconds);
            var w = relativeVelocity - relativePosition * inverseStep;
            var wLength = w.Length();
            var unitW = wLength > Epsilon ? w / wLength : StableDirection(agent.Id, other.Id);
            direction = new Vector2(unitW.Y, -unitW.X);
            correction = (separationRadius * inverseStep - wLength) * unitW;
        }

        // Solve front-to-back. This agent takes full responsibility relative to
        // the already chosen velocity of every higher-priority mover. Lower
        // priority movers are solved later against this agent's actual result.
        // Unlike asymmetric simultaneous solves, the pair therefore agrees on
        // one concrete velocity prediction for the current tick.
        lines.Add(new VelocityLine(agent.Velocity + correction * responsibility, direction));
    }

    private static int LinearProgram2(
        IReadOnlyList<VelocityLine> constraints,
        float radius,
        Vector2 optimalVelocity,
        bool directionOnly,
        out Vector2 result)
    {
        if (directionOnly)
        {
            result = optimalVelocity * radius;
        }
        else if (optimalVelocity.LengthSquared() > radius * radius)
        {
            result = Vector2.Normalize(optimalVelocity) * radius;
        }
        else
        {
            result = optimalVelocity;
        }

        for (var line = 0; line < constraints.Count; line++)
        {
            if (Determinant(constraints[line].Direction, constraints[line].Point - result) <= 0f) continue;
            var previous = result;
            if (!LinearProgram1(constraints, line, radius, optimalVelocity, directionOnly, out result))
            {
                result = previous;
                return line;
            }
        }
        return constraints.Count;
    }

    private static bool LinearProgram1(
        IReadOnlyList<VelocityLine> constraints,
        int lineIndex,
        float radius,
        Vector2 optimalVelocity,
        bool directionOnly,
        out Vector2 result)
    {
        var line = constraints[lineIndex];
        var dot = Vector2.Dot(line.Point, line.Direction);
        var discriminant = dot * dot + radius * radius - line.Point.LengthSquared();
        if (discriminant < 0f)
        {
            result = default;
            return false;
        }

        var root = MathF.Sqrt(discriminant);
        var left = -dot - root;
        var right = -dot + root;
        for (var previousIndex = 0; previousIndex < lineIndex; previousIndex++)
        {
            var previous = constraints[previousIndex];
            var denominator = Determinant(line.Direction, previous.Direction);
            var numerator = Determinant(previous.Direction, line.Point - previous.Point);
            if (MathF.Abs(denominator) <= Epsilon)
            {
                if (numerator < 0f)
                {
                    result = default;
                    return false;
                }
                continue;
            }

            var parameter = numerator / denominator;
            if (denominator >= 0f) right = MathF.Min(right, parameter);
            else left = MathF.Max(left, parameter);
            if (left > right)
            {
                result = default;
                return false;
            }
        }

        if (directionOnly)
        {
            result = line.Point +
                     (Vector2.Dot(optimalVelocity, line.Direction) > 0f ? right : left) *
                     line.Direction;
        }
        else
        {
            var parameter = Vector2.Dot(line.Direction, optimalVelocity - line.Point);
            parameter = Math.Clamp(parameter, left, right);
            result = line.Point + parameter * line.Direction;
        }
        return true;
    }

    private void LinearProgram3(
        IReadOnlyList<VelocityLine> constraints,
        int beginLine,
        float radius,
        ref Vector2 result)
    {
        var violationDistance = 0f;
        for (var lineIndex = beginLine; lineIndex < constraints.Count; lineIndex++)
        {
            var line = constraints[lineIndex];
            var violation = Determinant(line.Direction, line.Point - result);
            if (violation <= violationDistance) continue;

            projectedLines.Clear();
            for (var previousIndex = 0; previousIndex < lineIndex; previousIndex++)
            {
                var previous = constraints[previousIndex];
                var determinant = Determinant(line.Direction, previous.Direction);
                Vector2 point;
                if (MathF.Abs(determinant) <= Epsilon)
                {
                    if (Vector2.Dot(line.Direction, previous.Direction) > 0f) continue;
                    point = (line.Point + previous.Point) * 0.5f;
                }
                else
                {
                    point = line.Point +
                            Determinant(previous.Direction, line.Point - previous.Point) /
                            determinant * line.Direction;
                }
                var direction = previous.Direction - line.Direction;
                if (direction.LengthSquared() <= Epsilon) continue;
                projectedLines.Add(new VelocityLine(point, Vector2.Normalize(direction)));
            }

            var previousResult = result;
            var optimizationDirection = new Vector2(-line.Direction.Y, line.Direction.X);
            if (LinearProgram2(
                    projectedLines,
                    radius,
                    optimizationDirection,
                    true,
                    out result) < projectedLines.Count)
            {
                result = previousResult;
            }
            violationDistance = Determinant(line.Direction, line.Point - result);
        }
    }

    private static float Determinant(Vector2 first, Vector2 second) =>
        first.X * second.Y - first.Y * second.X;

    private static Vector2 StableDirection(AgentId first, AgentId second)
    {
        var hash = unchecked((uint)(first.Value * 73856093) ^ (uint)(second.Value * 19349663));
        var angle = hash % 1024 / 1024f * MathF.Tau;
        return new Vector2(MathF.Cos(angle), MathF.Sin(angle));
    }

    private readonly record struct VelocityLine(Vector2 Point, Vector2 Direction);
}

using RTSGame.Simulation.Agents;
using RTSGame.Simulation.Economy;
using RTSGame.Simulation.Jobs;

namespace RTSGame.Rendering;

/// <summary>
/// Reads a body's simulation state and says what it is doing, as far as the screen is concerned.
/// </summary>
/// <remarks>
/// <b>Static and pure so it can be asked without a window.</b> This lived inside the game loop, where the
/// only way to check it was to look at the screen — and "is the chopping animation playing while wood
/// leaves the tree?" is a question about a body's state, not about pixels. Pulled out here, a self-test can
/// put a villager at a trunk, watch the wood go down, and assert the pose the renderer would have chosen on
/// exactly those ticks. That is the difference between believing the mapping and knowing it.
/// <para>
/// It reads state and never writes any, which is what keeps a view concern out of the simulation: nothing
/// here is fingerprinted, saved, or visible to a rule.
/// </para>
/// </remarks>
internal static class BodyActions
{
    /// <summary>Below this a body is standing, not walking. Squared metres per second.</summary>
    public const float WalkingSpeedSquared = 0.08f * 0.08f;

    /// <summary>
    /// What this body is doing. <paramref name="hurt"/> is the view's own recently-wounded flag.
    /// </summary>
    /// <remarks>
    /// The order is the whole design. Flinching first, because a body taking a hit is the most urgent thing
    /// on screen. Then fighting, which must never be misread as work. Then <b>working before moving</b>: a
    /// body at its place is nudged constantly by its neighbours and by depenetration, and those nudges
    /// cross the walking threshold for single frames — with movement tested first the pose flickered between
    /// labouring and walking, which is what "the construction animation glitches instead of proceeding"
    /// was. <c>IsWorking</c> is only ever true of a body already at its place, so testing it first cannot
    /// steal a frame from something genuinely walking there.
    /// </remarks>
    public static BodyAction For(in AgentState agent, bool hurt, out bool locomotion) =>
        For(in agent, hurt, waiting: false, out locomotion);

    /// <summary>
    /// As above, with <paramref name="waiting"/> for a body whose work cannot proceed.
    /// </summary>
    /// <remarks>
    /// <b>A body waiting for materials is doing one thing, not two things alternately.</b> Reported from
    /// the chair, and the body log caught it exactly: three builders, the timber runs out, "one guy goes to
    /// fetch, other two glitch". Those two hold a Build assignment with <c>activity=None</c> and cycle
    /// their legs — walk to the store, find nothing, walk back, find nothing — beginning and abandoning an
    /// activity each time round. <c>IsWorking</c> therefore flickers, and a pose read off it flickers with
    /// it. The animation was not glitching; it was accurately reporting a simulation that could not settle.
    /// <para>
    /// The oscillation itself belongs to the jobs layer and is worth fixing there. But the pose must not
    /// depend on the fix: <b>whether a project can be worked at all is a slow, stable fact</b>, and reading
    /// that instead of the flapping activity gives a steady figure standing about — which is both correct
    /// and what a person waiting for a delivery looks like.
    /// </para>
    /// </remarks>
    public static BodyAction For(in AgentState agent, bool hurt, bool waiting, out bool locomotion)
    {
        locomotion = false;
        if (hurt) return BodyAction.Flinch;

        if (agent.Jobs.Assignment.Kind == AssignmentKind.Attack && JobSystem.IsWorking(in agent))
        {
            return BodyAction.Strike;
        }

        if (JobSystem.IsWorking(in agent)) return Labour(in agent);

        // <b>Waiting is only waiting when there is nothing being done.</b> This test used to sit ABOVE the
        // working one, on the reasoning that "can this project be worked at all" is the stable fact and the
        // flapping activity is not. It was wrong in a way the body log caught exactly: builders at the site
        // with empty hands were shown standing about while <c>working=True</c> — because empty hands do not
        // mean idle, they mean the timber is already in the wall. Three of nine builders were labouring and
        // being drawn as bystanders, which is the construction animation "still not working".
        //
        // Below the working test it costs nothing and still does its job: a body that genuinely has no
        // activity gets a settled pose instead of one that flickers as the jobs layer cycles its legs.
        if (waiting && agent.Velocity.LengthSquared() <= WalkingSpeedSquared) return BodyAction.Idle;

        if (agent.Velocity.LengthSquared() > WalkingSpeedSquared)
        {
            locomotion = true;
            return agent.Jobs.CarriedUnits > 0 ? BodyAction.Carry : BodyAction.Walk;
        }

        if (agent.Jobs.Assignment.Kind == AssignmentKind.Guard || agent.Role == AgentRole.Militia)
        {
            return BodyAction.Guard;
        }

        return BodyAction.Idle;
    }

    /// <summary>
    /// Which kind of work this body is at, from its assignment's own cargo.
    /// </summary>
    /// <remarks>
    /// <c>ActivityKind</c> is deliberately just "working" — one kind, on purpose — but the <b>assignment</b>
    /// knows what it is for. So felling a tree, reaping a field and cutting stone are three different things
    /// on screen out of state the simulation already had, and "the wood chopping isn't working with the
    /// kneeling we currently have" turned out to be one pose standing in for four jobs.
    /// </remarks>
    public static BodyAction Labour(in AgentState agent)
    {
        var assignment = agent.Jobs.Assignment;
        if (assignment.Kind is AssignmentKind.Build or AssignmentKind.Train) return BodyAction.Build;

        return assignment.Cargo switch
        {
            Resource.Wood => BodyAction.Chop,
            Resource.Stone => BodyAction.Quarry,
            Resource.Grain => BodyAction.Reap,
            _ => BodyAction.Build,
        };
    }
}

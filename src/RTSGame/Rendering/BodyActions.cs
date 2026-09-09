using System.Numerics;
using RTSGame.Simulation.Agents;
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
/// <para>
/// <b>And it no longer infers.</b> §180. Every version of this file before today worked out which labour a
/// body was performing by reading <c>Assignment.Cargo</c>, which is how a kneeling repair came to stand in
/// for felling a tree and how idle militia came to reap — <c>default(Resource)</c> is grain. The simulation
/// names the act now, at the moment it begins one, so this file's whole job is a translation from acts to
/// clips. There is no arithmetic left in it to get wrong.
/// </para>
/// </remarks>
internal static class BodyActions
{
    /// <summary>Below this a body is standing, not walking. Squared metres per second.</summary>
    public const float WalkingSpeedSquared = 0.08f * 0.08f;

    /// <summary>
    /// What this body is doing. <paramref name="hurt"/> is the view's own recently-wounded flag.
    /// </summary>
    public static BodyAction For(in AgentState agent, bool hurt, out bool locomotion) =>
        For(in agent, hurt, waiting: false, builder: null, out locomotion);

    public static BodyAction For(in AgentState agent, bool hurt, bool waiting, out bool locomotion) =>
        For(in agent, hurt, waiting, builder: null, out locomotion);

    /// <summary>
    /// What a builder standing at its site is doing, or null if this body is not one.
    /// </summary>
    /// <remarks>
    /// <b>Read from the project, never from the activity.</b> A builder's activity opens and abandons as the
    /// jobs layer cycles its legs hunting for timber, so any pose derived from it alternates at several
    /// hertz — which is what "the animation glitching during resource wait" is, reported three times and
    /// patched twice from the wrong end. Both facts used here are slow: whether this body is assigned to
    /// build, and whether the project has material delivered to work on. A delivery changes the second one;
    /// nothing changes it per tick.
    /// <para>
    /// The oscillation itself belongs to the jobs layer and is still worth fixing there. This is a
    /// deliberate exception to "the pose reads the act", scoped to the one assignment whose act cannot
    /// settle, and it should be deleted the moment that is fixed.
    /// </para>
    /// </remarks>
    public static BodyAction? ForBuilder(in AgentState agent, bool atSite, bool canBeWorked)
    {
        if (agent.Jobs.Assignment.Kind is not (AssignmentKind.Build or AssignmentKind.Train)) return null;

        // <b>At the site, not standing still.</b> Gating on velocity was the same mistake one layer along:
        // a body at a work site is jostled constantly, so velocity crosses the walking threshold for single
        // frames and the pose flipped between this decision and the general one. Whether a body is at the
        // thing it is building is geometry, and geometry does not flap.
        if (!atSite) return null;
        return canBeWorked ? BodyAction.Build : BodyAction.Idle;
    }

    /// <summary>
    /// As above, with <paramref name="builder"/> pre-decided from the project for a body at a build site.
    /// </summary>
    /// <remarks>
    /// The order is the whole design. Flinching first, because a body taking a hit is the most urgent thing
    /// on screen. Then the builder exception. Then <b>the act before the movement</b>: a body at its place
    /// is nudged constantly by its neighbours and by depenetration, and those nudges cross the walking
    /// threshold for single frames — with movement tested first the pose flickered between labouring and
    /// walking, which is what "the construction animation glitches instead of proceeding" was. An act is
    /// only ever underway for a body already at its place, so testing it first cannot steal a frame from
    /// something genuinely walking there.
    /// </remarks>
    public static BodyAction For(
        in AgentState agent, bool hurt, bool waiting, BodyAction? builder, out bool locomotion)
    {
        locomotion = false;
        if (hurt) return BodyAction.Flinch;

        // A builder standing at its site: decided from the project, above the act that cannot settle.
        if (builder is { } settled) return settled;

        // <b>The act, if one is underway here.</b> IsWorking is the simulation's own single definition of
        // "at its place and not interrupted", and the act is what the jobs layer named when it began. One
        // lookup, no inference, and nothing to fall through.
        if (JobSystem.IsWorking(in agent)) return Doing(agent.Jobs.Activity, in agent);

        if (agent.Velocity.LengthSquared() > WalkingSpeedSquared) return Walking(in agent, out locomotion);

        // <b>Waiting is only waiting when there is nothing being done.</b> This test used to sit ABOVE the
        // act, on the reasoning that "can this project be worked at all" is the stable fact and the flapping
        // activity is not. It was wrong in a way the body log caught exactly: builders at the site with
        // empty hands were shown standing about while working — because empty hands do not mean idle, they
        // mean the timber is already in the wall. Three of nine builders were labouring and being drawn as
        // bystanders, which is the construction animation "still not working".
        if (waiting) return Standing(in agent);

        return Standing(in agent);
    }

    /// <summary>Which clip an act reads as.</summary>
    /// <remarks>
    /// <b>A table, and every arm of it comes from a named act rather than a guess.</b> The one thing still
    /// read off the body rather than off the act is a soldier's bearing: standing a post and loitering are
    /// the same act — see <see cref="ActivityKind.Standing"/> — and which of them it looks like is a fact
    /// about who is standing there.
    /// </remarks>
    private static BodyAction Doing(ActivityKind activity, in AgentState agent) => activity switch
    {
        ActivityKind.Reaping => BodyAction.Reap,
        ActivityKind.Felling => BodyAction.Chop,
        ActivityKind.Quarrying => BodyAction.Quarry,
        ActivityKind.Building => BodyAction.Build,
        ActivityKind.Striking => BodyAction.Strike,
        // Putting a load on or taking it off, at either end of a trip. Both are handovers of a few seconds
        // and neither has a clip of its own; standing at the store is what they look like.
        ActivityKind.Loading or ActivityKind.Unloading => Standing(in agent),
        _ => Standing(in agent),
    };

    /// <summary>Not moving: a soldier holds a bearing, everybody else stands about.</summary>
    private static BodyAction Standing(in AgentState agent) =>
        agent.Role == AgentRole.Militia || agent.Jobs.Assignment.Kind == AssignmentKind.Guard
            ? BodyAction.Guard
            : BodyAction.Idle;

    /// <summary>
    /// Which way this body should be looking, in world space.
    /// </summary>
    /// <remarks>
    /// <b>Extracted so "they whip around like motorbikes" can be a number.</b> §184. This lived inline in
    /// the render loop, where the only way to judge it was to watch — and it was wrong there for three
    /// sightings running, each found by eye and each costing a session. A heading is a function of a body's
    /// state, so a self-test can stand nine builders at a wall and measure how many degrees a second they
    /// swing, which is the difference between believing the facing and knowing it.
    /// <para>
    /// <b>The act before the velocity.</b> The bar for "moving" is eight centimetres a second, and a body
    /// standing correctly at its work is shoved past that constantly by depenetration and by its
    /// neighbours — so with velocity tested first, an arrived body handed a fresh random direction to the
    /// slew every frame. <c>IsWorking</c> is only ever true of a body already at its place, so testing it
    /// first cannot steal a frame from something genuinely walking there.
    /// </para>
    /// <para>
    /// Three fallbacks, each for a case the one above it cannot answer: a body at its work faces its work
    /// (§172 — a villager who walked past a tree to reach its free side otherwise chopped at thin air with
    /// the trunk behind them); a body under way faces where it is actually going, which cannot disagree with
    /// its direction of travel because it <em>is</em> its direction of travel (§168 — bodies walked backwards
    /// when this read the steering layer's own heading, which lags or opposes travel in a crowd); and a body
    /// that has stopped keeps the last heading the steering gave it, because there is nothing else to read.
    /// </para>
    /// </remarks>
    public static Vector2 HeadingOf(in AgentState agent)
    {
        var toWork = agent.Jobs.Place - agent.Position;
        if (JobSystem.IsWorking(in agent) && toWork.LengthSquared() > AtWorkSquared) return toWork;
        if (agent.Velocity.LengthSquared() > WalkingSpeedSquared) return agent.Velocity;
        return agent.Facing;
    }

    /// <summary>
    /// How near a body has to be standing on its place before "face the work" has no direction left.
    /// </summary>
    /// <remarks>
    /// Two centimetres, squared. Below it the vector toward the work is shorter than the noise in the
    /// body's own position, so its direction is meaningless and the steering heading is the honest answer.
    /// </remarks>
    private const float AtWorkSquared = 0.0004f;

    /// <summary>Under way, laden or not.</summary>
    private static BodyAction Walking(in AgentState agent, out bool locomotion)
    {
        locomotion = true;
        return agent.Jobs.CarriedUnits > 0 ? BodyAction.Carry : BodyAction.Walk;
    }
}

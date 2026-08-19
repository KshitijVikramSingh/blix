using System.Numerics;
using RTSGame.Simulation.Agents;

namespace RTSGame.Simulation.Jobs;

/// <summary>What the jobs layer wants done to a body this tick.</summary>
internal enum JobStep
{
    /// <summary>Nothing. Either there is no assignment, or the unit is already doing it.</summary>
    None,

    /// <summary>Send it to a place. The world owns routing; the jobs layer only asks.</summary>
    WalkTo,

    /// <summary>
    /// A leg of work has just finished here. For a haul, the tick to move cargo on or off the body.
    /// </summary>
    /// <remarks>
    /// The jobs layer cannot do the transfer itself — stock lives in nodes and this decides only — so
    /// it reports the moment and the leg, and the world acts. The leg reported is the one that
    /// <em>finished</em>, not the one starting, because the assignment has already moved on by then and
    /// asking the world to work that out was the sort of subtlety that goes wrong once and silently.
    /// </remarks>
    LegFinished,
}

internal readonly record struct JobRequest(JobStep Step, Vector2 Target, int Leg = -1)
{
    public static JobRequest None => default;
}

/// <summary>What the ground where a job is looks like, which only the world can say.</summary>
/// <remarks>
/// The three cases are genuinely different and they look identical from inside the body — it
/// walked, it stopped, it is not there. Telling them apart is what stops a crowded workplace
/// being treated as a broken one, and a broken one as a crowded one.
/// </remarks>
internal enum PlaceCondition
{
    /// <summary>Free ground. Falling short of it was the journey's fault, so try again.</summary>
    Open,

    /// <summary>
    /// Somebody is standing on it. A workplace holds several pairs of hands, so work from
    /// wherever they left room rather than queueing for one square metre.
    /// </summary>
    Crowded,

    /// <summary>
    /// No body this size can stand there — built over, or ground that has gone. Keep asking, at
    /// a bounded rate: walls come down.
    /// </summary>
    Unreachable,
}

/// <summary>
/// The three-layer jobs model: assignment, activity, interrupt. Decides only; the world
/// carries out whatever it asks for.
/// </summary>
/// <remarks>
/// Written as a function of one body's state so that it is testable without a world and so
/// that the seam between deciding and moving is exactly one <see cref="JobRequest"/> wide.
/// Everything it reads — where the body is, whether it is under orders, how big it is — is
/// already on <c>AgentState</c>.
/// <para>
/// The order of the three layers is the whole design and it is worth reading it in that order.
/// An interrupt, if live, wins outright and nothing else runs. Otherwise the assignment is
/// asked for an activity if there is not one. Otherwise the activity is served: walk to the
/// place, then work there for as long as the work takes. Nothing in that sequence can rewrite
/// the assignment, which is why an order cannot cost a unit its job.
/// </para>
/// </remarks>
internal static class JobSystem
{
    /// <summary>
    /// Advances one body's jobs and returns what the world should do about it.
    /// </summary>
    /// <param name="place">
    /// What the ground where the job is looks like. The jobs layer cannot answer that itself and
    /// only needs it on the ticks <see cref="AsksAboutItsPlace"/> is true, which is what keeps
    /// the world from paying for the query every tick per unit.
    /// </param>
    public static JobRequest Advance(ref AgentState agent, float deltaSeconds, PlaceCondition place)
    {
        ref var jobs = ref agent.Jobs;
        if (jobs.IsInterrupted) return ServeInterrupt(ref agent, deltaSeconds, place);
        if (!jobs.HasAssignment) return JobRequest.None;
        // A finished job is cleared here rather than where it finished, so the tick that reported the
        // last leg is the tick the world moved the cargo on.
        if (jobs.Finished)
        {
            Assign(ref agent, Assignment.None);
            return JobRequest.None;
        }


        if (jobs.Activity == ActivityKind.None) BeginActivity(ref jobs);

        if (jobs.RetryCooldown > 0f)
        {
            jobs.RetryCooldown = MathF.Max(0f, jobs.RetryCooldown - deltaSeconds);
            return JobRequest.None;
        }

        if (!IsAtPlace(agent)) return WalkToPlace(ref agent, place);

        // At the place. Whatever the walk did or did not manage, the unit is here now.
        jobs.WalkIssued = false;
        jobs.DwellRemaining -= deltaSeconds;
        if (jobs.DwellRemaining > 0f) return JobRequest.None;

        var finished = jobs.Leg;
        CompleteActivity(ref jobs);
        return new JobRequest(JobStep.LegFinished, jobs.Place, finished);
    }

    /// <summary>
    /// Whether this body counts as being where its work is.
    /// </summary>
    /// <remarks>
    /// Two distances, and the second only after the body has tried and failed to make the first.
    /// A workplace is a place for several pairs of hands rather than a spot for one body, so
    /// once a crowd has taken the exact point the honest answer for everybody behind them is
    /// that they are here.
    /// </remarks>
    private static bool IsAtPlace(in AgentState agent)
    {
        if (agent.Jobs.PlaceExtent <= 0f)
        {
            // A bare point on the ground. A few of the body's own radii, as it always was.
            var toPoint = Vector2.DistanceSquared(agent.Position, agent.Jobs.Place);
            return toPoint <= Square(JobDefaults.AtPlaceDistance(agent.Radius)) ||
                   (agent.Jobs.SettledNearby &&
                    toPoint <= Square(JobDefaults.CrowdedPlaceDistance(agent.Radius)));
        }

        // Distance to the building's wall rather than to a circle drawn round it, so a body approaching
        // a face stops at the face. Measured from the box, which is what the wall is.
        var gap = DistanceToPlace(in agent);
        if (gap <= agent.Radius + JobDefaults.TouchSlack + JobDefaults.RasterReach) return true;
        return agent.Jobs.SettledNearby && gap <= agent.Radius * JobDefaults.CrowdedTouchShare;
    }

    /// <summary>Whether this body is standing at its place rather than still on its way there.</summary>
    /// <remarks>
    /// For the economy, which may only count labour from a body that has actually arrived. A hand walking
    /// toward a field is not breaking its ground, and a hand walking its grain to a store is not either.
    /// </remarks>
    public static bool IsWorking(in AgentState agent) =>
        agent.Jobs.Activity != ActivityKind.None &&
        !agent.Jobs.IsInterrupted &&
        IsAtPlace(in agent);

    /// <summary>How far this body is from the wall of its place, or zero if it is against it.</summary>
    public static float DistanceToPlace(in AgentState agent)
    {
        var half = new Vector2(agent.Jobs.PlaceHalfWidth);
        var nearest = Vector2.Clamp(
            agent.Position, agent.Jobs.Place - half, agent.Jobs.Place + half);
        return Vector2.Distance(agent.Position, nearest);
    }

    /// <summary>
    /// Ends the current shift of work now, whatever time was left on it.
    /// </summary>
    /// <remarks>
    /// For the one thing the jobs layer cannot know: a shift at a workplace is over when the body's hands
    /// are full or the field has nothing left to give, and both of those are facts about the economy. The
    /// dwell is a fallback rather than the rule — a body delivers at least once a shift even if it never
    /// fills up, which is how a phase that completes mid-shift gets its last few units carried in.
    /// </remarks>
    public static void EndShift(ref AgentState agent)
    {
        if (agent.Jobs.Activity == ActivityKind.None) return;
        agent.Jobs.DwellRemaining = 0f;
    }

    /// <summary>
    /// Records that this unit has been given a direct order, whatever the order was.
    /// </summary>
    /// <remarks>
    /// Called from the command layer and nowhere else, which is what makes an order an
    /// interrupt rather than a state: the jobs layer's own movement goes through the same
    /// routing without passing here, so it cannot interrupt itself.
    /// <para>
    /// A unit with no assignment is left entirely alone. That is not a special case to be
    /// tidied away later — it is what keeps every scenario predating this layer behaving
    /// exactly as it did, and it is what an unemployed unit should do.
    /// </para>
    /// </remarks>
    public static void Interrupt(ref AgentState agent)
    {
        if (!agent.Jobs.HasAssignment) return;
        agent.Jobs.Interrupt = InterruptKind.Order;
        agent.Jobs.InterruptGrace = JobDefaults.OrderGraceSeconds;
    }

    /// <summary>Gives this unit a standing commitment, or takes its current one away.</summary>
    /// <remarks>
    /// Everything about the old job goes — its progress, its activity, its interrupt — <b>except what
    /// the body is physically carrying</b>, because cargo is a thing in the world and not a note about
    /// intent. Wiping the whole struct destroyed a wagon's load the first time a granary filled up mid
    /// delivery, and the exact ledger named it: seventeen grain gone at tick 116,520.
    /// </remarks>
    public static void Assign(ref AgentState agent, Assignment assignment)
    {
        agent.Jobs = new AgentJobs
        {
            Assignment = assignment,
            Carrying = agent.Jobs.Carrying,
            CarriedUnits = agent.Jobs.CarriedUnits,
        };
    }

    /// <summary>
    /// Sends a body that is already carrying to a different place to put it down.
    /// </summary>
    /// <remarks>
    /// For a delivery that arrives to find the store full. The assignment is not finished — there is
    /// still a load on the cart — so it is pointed at somewhere else with room and walks on, rather than
    /// ending and leaving the units in limbo.
    /// </remarks>
    public static void Retarget(ref AgentState agent, Assignment assignment, int leg)
    {
        ref var jobs = ref agent.Jobs;
        jobs.Assignment = assignment;
        jobs.Leg = leg;
        jobs.Finished = false;
        // Cleared rather than recomputed: the next Advance begins the activity and sets the place and
        // its extent together, which is the only place those two are allowed to be chosen.
        jobs.Activity = ActivityKind.None;
        jobs.WalkIssued = false;
        jobs.Retries = 0;
        jobs.RetryCooldown = 0f;
        jobs.SettledNearby = false;
    }

    /// <summary>
    /// While an order is being carried out, the assignment waits. Once the unit is standing
    /// free, grace runs down and then it goes back to work.
    /// </summary>
    private static JobRequest ServeInterrupt(
        ref AgentState agent,
        float deltaSeconds,
        PlaceCondition place)
    {
        ref var jobs = ref agent.Jobs;
        if (UnderOrders(agent))
        {
            jobs.InterruptGrace = JobDefaults.OrderGraceSeconds;
            return JobRequest.None;
        }

        jobs.InterruptGrace -= deltaSeconds;
        if (jobs.InterruptGrace > 0f) return JobRequest.None;

        // Expired. The activity was never touched, so resuming is just doing it again: walk
        // back to the place and finish however much of the work was left.
        jobs.Interrupt = InterruptKind.None;
        jobs.InterruptGrace = 0f;
        jobs.WalkIssued = false;
        jobs.RetryCooldown = 0f;
        return Advance(ref agent, deltaSeconds, place);
    }

    private static JobRequest WalkToPlace(ref AgentState agent, PlaceCondition place)
    {
        ref var jobs = ref agent.Jobs;
        if (UnderOrders(agent)) return JobRequest.None;

        if (jobs.WalkIssued)
        {
            // The walk ended and the unit is not there. Waiting a couple of seconds before
            // trying again is the difference between a unit failing politely and a unit asking
            // for a route every tick forever.
            jobs.WalkIssued = false;
            jobs.Retries++;
            jobs.RetryCooldown = JobDefaults.RetrySeconds;

            // Near enough to be doing the work, on ground that is taken rather than gone: this
            // is where the hands stand. A crowd is accepted the moment the body stops, because
            // walking at an occupied square twice more changes nothing about who is on it. Open
            // ground gets the retries first, since falling short of empty ground means the
            // journey went wrong and journeys come right.
            var nearEnough = jobs.PlaceExtent > 0f
                ? DistanceToPlace(in agent) <= agent.Radius * JobDefaults.CrowdedTouchShare
                : Vector2.DistanceSquared(agent.Position, jobs.Place) <=
                  Square(JobDefaults.CrowdedPlaceDistance(agent.Radius));
            if (nearEnough && place is PlaceCondition.Crowded ||
                nearEnough && place is PlaceCondition.Open && jobs.Retries >= JobDefaults.CrowdedAttempts)
            {
                jobs.SettledNearby = true;
            }

            return JobRequest.None;
        }

        jobs.WalkIssued = true;
        return new JobRequest(JobStep.WalkTo, jobs.Place);
    }

    /// <summary>
    /// Whether this body's next step depends on what the ground at its place is like.
    /// </summary>
    /// <remarks>
    /// True only on the tick a walk has ended without arriving, which is rare — so the world can
    /// pay for a navigability test and a collider query then, instead of once per assigned unit
    /// per tick. The predicate lives here rather than in the world because it is made of the same
    /// three conditions the decision below is, and the two coming apart would be a silent bug:
    /// the wrong ground reported for the right body.
    /// </remarks>
    public static bool AsksAboutItsPlace(in AgentState agent) =>
        agent.Jobs.HasAssignment &&
        agent.Jobs.Activity != ActivityKind.None &&
        agent.Jobs.WalkIssued &&
        agent.Jobs.RetryCooldown <= 0f &&
        !agent.Jobs.IsInterrupted &&
        !UnderOrders(agent) &&
        !IsAtPlace(agent);

    private static void BeginActivity(ref AgentJobs jobs)
    {
        jobs.Activity = ActivityKind.Working;
        jobs.Place = jobs.Assignment.PlaceOfLeg(jobs.Leg);
        jobs.PlaceExtent = jobs.Assignment.ExtentOfLeg(jobs.Leg);
        jobs.DwellRemaining = jobs.Assignment.DwellOfLeg(jobs.Leg);
        jobs.WalkIssued = false;
        jobs.Retries = 0;
        jobs.RetryCooldown = 0f;
        jobs.SettledNearby = false;
    }

    private static void CompleteActivity(ref AgentJobs jobs)
    {
        jobs.LegsCompleted++;
        var wasLastLeg = !jobs.Assignment.HasTwoEnds || jobs.Leg % 2 != 0;
        jobs.Leg = jobs.Assignment.HasTwoEnds ? (jobs.Leg + 1) % 2 : 0;
        jobs.Activity = ActivityKind.None;
        jobs.DwellRemaining = 0f;
        // A job with an end reaches it, and the body goes back to being available. The world is told
        // which leg finished first, so a haul's cargo is delivered before the assignment vanishes.
        if (wasLastLeg && !jobs.Assignment.RepeatsForever) jobs.Finished = true;
    }

    /// <summary>Whether the body still has somewhere it has been told to be.</summary>
    /// <remarks>
    /// A destination counts, obviously. So does any locomotion state that renews its own
    /// destination — a follow or a chase has no arrival, so an interrupt that imposed one stays
    /// live until something drops the body back to idle, and the assignment waits rather than
    /// fighting it.
    /// <para>
    /// Written as the two exceptions rather than as a list of the behaviours, so that a state
    /// added later defaults to the safe side: the jobs layer waits for something it does not
    /// recognise instead of talking over it. The exceptions matter. <c>Move</c> without a
    /// destination is a body that has stopped — arrived, or failed to be given a route at all —
    /// and reading that as "busy" cost this layer its first bug: a unit whose workplace could
    /// not be reached was left standing in it forever, having asked for a route twice, because
    /// a failed order leaves the state set and the destination clear.
    /// </para>
    /// </remarks>
    private static bool UnderOrders(in AgentState agent) =>
        agent.HasDestination ||
        agent.LocomotionState is not (AgentLocomotionState.Idle or AgentLocomotionState.Move);

    private static float Square(float value) => value * value;
}

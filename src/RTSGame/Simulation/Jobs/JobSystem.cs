using System.Numerics;
using RTSGame.Simulation.Agents;
using RTSGame.Simulation.Economy;

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
    /// <summary>
    /// Whether this body has reached the place its assignment sent it to.
    /// </summary>
    /// <remarks>
    /// <b>Exposed so the movement layer can stop calling an arrived body stuck.</b> Stuck time accrues
    /// while a body wants to move and is not moving — and a body standing at its work still has a residual
    /// preferred velocity for the last few centimetres it cannot have, so it accrued stuck time forever and
    /// the overlay painted it red. A quarter of the stuck reports in a hands-off village were bodies that
    /// had simply arrived.
    /// <para>
    /// A wrapper rather than a second predicate, deliberately: two notions of "is it there" is the exact
    /// near-synonym fault that has cost this codebase days, and arrival is already defined once, here.
    /// </para>
    /// </remarks>
    public static bool IsAtItsPlace(in AgentState agent) =>
        agent.Jobs.HasAssignment && agent.Jobs.Activity != ActivityKind.None && IsAtPlace(in agent);

    private static bool IsAtPlace(in AgentState agent)
    {
        // <b>An attack arrives at weapon reach, not at "a few radii".</b> §194. The general test is
        // max(radius x 3, extent + radius + slack), which for a body target is 1.110 m — and harm reaches
        // 0.814 m, so an ordered attacker walked to thirty centimetres outside its own weapon, decided it
        // had arrived, and stood there. It could never land a blow by design; the blows it did land on a
        // standing target came from bodies jostling each other through the last thirty centimetres by
        // accident, which is exactly why contact was 23% and why sending MORE attackers lowered it.
        //
        // It is also why §193's sweep of aim and re-aim cadence was flat across the whole grid: the body
        // was not failing to arrive, it was arriving somewhere too far away, and no amount of better aim
        // fixes a destination that is out of range.
        //
        // Reads ThreatSystem.HarmReach so there is one definition of touching — §192 split that once
        // already, between the harm rule and an instrument, and this is the third caller.
        if (agent.Jobs.Assignment.Kind == AssignmentKind.Attack && agent.Jobs.PlaceExtent <= 0f)
        {
            var quarry = agent.Jobs.Assignment.Quarry;
            if (quarry.IsValid)
            {
                var reach = Threat.ThreatSystem.HarmReach(agent.Radius, agent.Radius);
                return Vector2.DistanceSquared(agent.Position, agent.Jobs.Place) <= Square(reach);
            }
        }

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
            ReservedUnits = 0,
            Project = assignment.Kind is AssignmentKind.Build or AssignmentKind.Train
                ? assignment.Sink
                : NodeId.None,
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

        // An order holds until something overrides it. A body that has been sent somewhere reaches it and
        // stays posted; it does not drift back to work two seconds later because it happened to stand still.
        // The assignment is kept, not cancelled — a new assignment, or being taken off work, resumes it.
        //
        // <b>Except a guard, which always goes back to its post.</b> §143. The rule is stated in terms of the
        // assignment rather than of who gave the order, and that is deliberate: the alternative was to mark
        // the defence's own chases as a different kind of interrupt, which puts provenance into a layer that
        // has done without it, and it would still have been wrong for a player's order — a soldier told to go
        // and look at something is expected to come back, and that expectation is what a post <em>is</em>.
        //
        // The first attempt at this released the interrupt in StandDown, where the threat system says the
        // danger has passed. It does not fire: §134's bounded peace proof skips every body with no hostile
        // in range, so at peace nothing calls it at all — the optimisation is right and its premise is that
        // a settlement at peace has nothing to decide. Measured as a guard that left its post for a raider
        // and stopped 8.8 m short of home for good.
        if (jobs.Interrupt == InterruptKind.Order && jobs.Assignment.Kind != AssignmentKind.Guard)
        {
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

    /// <summary>
    /// Hands a body back to its standing assignment, dropping whatever was overriding it.
    /// </summary>
    /// <remarks>
    /// <b>Because an order holds until something overrides it, and nothing overrode a defence.</b> §143: a
    /// militia committed by §30 is issued a chase, which is an <see cref="InterruptKind.Order"/> — and
    /// ServeInterrupt returns early for those forever, deliberately, so that a body sent somewhere stays
    /// there instead of drifting back to work two seconds later. When the danger passed, the defence issued a
    /// stop, which halts the walk and leaves the interrupt exactly where it was. So a defender stood on the
    /// ground the fight ended on until something else happened to it, which for a settlement at peace is
    /// never.
    /// <para>
    /// This is the "something" that overrides it, and the only caller is standing down from a threat. It is
    /// not a general release: taking a worker off an order is still the player's business, and a hand told to
    /// go somewhere is still expected to stay.
    /// </para>
    /// </remarks>
    public static void ReturnToAssignment(ref AgentState agent)
    {
        ref var jobs = ref agent.Jobs;
        jobs.Interrupt = InterruptKind.None;
        jobs.InterruptGrace = 0f;
        jobs.WalkIssued = false;
        jobs.RetryCooldown = 0f;
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

    /// <summary>
    /// Which act an assignment's leg is, named once at the moment the activity begins.
    /// </summary>
    /// <remarks>
    /// <b>The assignment kind is checked first and a cargo is read on one branch only.</b> That ordering is
    /// not stylistic — it is the reason this cannot reproduce the fault it replaces. The pose used to reach
    /// for <c>Assignment.Cargo</c> whatever the assignment was, and <c>default(Resource)</c> is
    /// <see cref="Resource.Grain"/>, so every body with an activity and no cargo reaped: militia standing a
    /// guard played the farming animation. Here, only <see cref="AssignmentKind.Work"/> consults the cargo,
    /// and <see cref="Assignment.Work"/> takes its output as a required argument rather than leaving a field
    /// at its default. <b>There is no branch on which an unset value can be mistaken for a set one.</b>
    /// <para>
    /// The leg matters as much as the kind, and this is where the old inference lost the most. A two-ended
    /// assignment is a different act at each end — leg zero of a <see cref="AssignmentKind.Work"/> is
    /// breaking ground and leg one is putting the load down; leg zero of a
    /// <see cref="AssignmentKind.Build"/> is collecting timber and leg one is raising the wall. Read off the
    /// assignment alone, a builder standing in a granary was building.
    /// </para>
    /// <para>
    /// Public because a self-test asks it directly. That is the point of naming the act in the simulation:
    /// "is the chopping animation playing while wood leaves the tree" becomes a question with an answer that
    /// does not require a window.
    /// </para>
    /// </remarks>
    public static ActivityKind NameActivity(in Assignment assignment, int leg)
    {
        // Whether this leg is the far end. Single-ended assignments are always at their one place.
        var far = assignment.HasTwoEnds && leg % 2 != 0;

        switch (assignment.Kind)
        {
            case AssignmentKind.None:
                return ActivityKind.None;

            // A post is a post. Which of the three it is belongs to the assignment, and the body's role is
            // what tells a soldier's alertness from a villager's loitering — not this.
            case AssignmentKind.Hold:
            case AssignmentKind.Guard:
            case AssignmentKind.Shuttle:
                return ActivityKind.Standing;

            case AssignmentKind.Attack:
                return ActivityKind.Striking;

            // Cargo moved between two stores: it goes on at one end and comes off at the other, and a raid
            // is the same act with somebody else's granary at the near end.
            case AssignmentKind.Haul:
            case AssignmentKind.Carry:
            case AssignmentKind.Loot:
                return far ? ActivityKind.Unloading : ActivityKind.Loading;

            // The producer's loop. The near leg is the work and the far leg is the delivery, which is the
            // distinction reading the assignment alone could not make.
            case AssignmentKind.Work:
                if (far) return ActivityKind.Unloading;
                return assignment.Cargo switch
                {
                    Resource.Wood => ActivityKind.Felling,
                    Resource.Stone => ActivityKind.Quarrying,
                    Resource.Grain => ActivityKind.Reaping,
                    // No fourth resource exists; if one arrives it should be named here rather than
                    // silently reaped. Standing is the honest answer for an act with no name yet.
                    _ => ActivityKind.Standing,
                };

            // Leg zero fetches the material, leg one puts it into the structure.
            case AssignmentKind.Build:
                return far ? ActivityKind.Building : ActivityKind.Loading;

            // Leg zero fetches the equipment; leg one is waiting at the barracks to be converted, which is
            // standing about and not labour.
            case AssignmentKind.Train:
                return far ? ActivityKind.Standing : ActivityKind.Loading;

            default:
                return ActivityKind.Standing;
        }
    }

    private static void BeginActivity(ref AgentJobs jobs)
    {
        jobs.Activity = NameActivity(in jobs.Assignment, jobs.Leg);
        // <b>An assigned body's activity is never nameless.</b> Reached only if a new assignment kind is
        // added without giving its legs names, in which case the body would re-begin the same activity
        // every tick and never finish a leg — a stall with no error, which is the shape §179 was written
        // about. §180 says such a thing should crash rather than be rendered.
        if (jobs.Activity == ActivityKind.None)
        {
            throw new InvalidOperationException(
                $"{jobs.Assignment.Kind} leg {jobs.Leg} has no named activity — see JobSystem.NameActivity.");
        }

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

using System.Numerics;
using RTSGame.Simulation.Agents;
using RTSGame.Simulation.Collision;
using RTSGame.Simulation.Economy;

namespace RTSGame.Simulation.Threat;

/// <summary>
/// Harm: what happens when two hostile bodies are standing next to each other.
/// </summary>
/// <remarks>
/// Deliberately not a combat model. There is no attack animation, no cooldown, no facing requirement and no
/// to-hit roll — a body next to something hostile is hurting it, at its strength, per second. That is enough
/// for the only thing Stage E has to find out, which is whether the <em>decision</em> to fight or run is
/// interesting; a richer model would add numbers to tune before anybody knows whether the mechanic works.
/// <para>
/// Written against <b>hostility</b> rather than against raiders, which is the line §28 draws. The scripted
/// thief is scaffolding and lives in <c>Debug/</c>; this is in the simulation because it has to behave the
/// same when the hostility is another player, and it would be the wrong shape if it knew what a raider was.
/// </para>
/// </remarks>
internal sealed class ThreatSystem
{
    /// <summary>
    /// How far apart two bodies can be and still be fighting, on top of their radii.
    /// </summary>
    /// <remarks>
    /// A body's length again, roughly — near enough that the pair are visibly in contact, and loose enough
    /// that the velocity solve keeping them a hair apart does not stop the fight. Tying it to the radii
    /// rather than fixing a distance means a wide body reaches further, which is what a wide body should do.
    /// </remarks>
    internal static float ReachShare = 1.1f;

    /// <summary>
    /// Retired: reach is a body's own business again.
    /// </summary>
    /// <remarks>
    /// <b>§33 made the harm reach a floor of <c>ChaseStopMetres + ContactSlack</c> and that dependency was
    /// written the wrong way round.</b> The intent was sound — reach must not be shorter than the distance
    /// at which the movement layer stops bringing bodies together — but tying one to the other means that
    /// closing the gap by lowering the stop distance lowers the reach with it, and the two can never meet.
    /// Visible in the sweep: at stop 0 the bodies came within 0.96 m of each other while the floor had just
    /// shrunk to 0.15, so reach was 0.81 and still short.
    /// <para>
    /// A chase now closes to contact, so the constraint is satisfied by the movement layer doing its job
    /// rather than by the harm rule compensating for it not doing it. Kept as a named number because the
    /// invariant is still real and worth stating: if a chase ever stops short again, this is where it will
    /// show up as raiders that cannot be hurt.
    /// </para>
    /// </remarks>
    /// <remarks>
    /// <b>The two numbers have to agree, and they did not.</b> A chasing body settles at
    /// <see cref="AgentDefaults.ChaseStopMetres"/> — 0.95 m — and two 0.37 m bodies reached 0.81 m, so a
    /// defender sent to fight came to a stop a hand's breadth outside striking distance and stood there for
    /// the rest of the raid. It is the same mistake this project keeps finding: two layers each with their
    /// own definition of "next to", and the one that happened to be convenient winning.
    /// <para>
    /// Written as a floor rather than by raising <see cref="ReachShare"/> until it happened to clear, so
    /// that the dependency is stated: reach is a body's own business, <em>except</em> that it can never be
    /// shorter than the distance at which the movement layer will stop bringing bodies together.
    /// </para>
    /// </remarks>
    internal static float ContactSlack = 0.15f;

    /// <summary>Bodies killed by hostiles since the world began.</summary>
    public long Killed { get; private set; }

    /// <summary>Damage dealt since the world began, in body-seconds.</summary>
    public float Dealt { get; private set; }

    /// <summary>
    /// How far from a resource a hostile has to be to be threatening it, in metres.
    /// </summary>
    /// <remarks>
    /// A raider walking past a granary two hundred metres away is not a reason to abandon a harvest. Twelve
    /// metres is close enough to be reaching for it and far enough that the decision is made before the
    /// grain is in its hands.
    /// </remarks>
    internal static float ThreatMetres = 12f;

    /// <summary>
    /// Seconds a defender may be away from a fight and still count toward whether it can be won.
    /// </summary>
    /// <remarks>
    /// <b>This is the answer to "distance belongs in question 2".</b> Strength that cannot arrive before the
    /// fight is decided is not strength: a farmhand twenty metres off counts toward the sum that tells two
    /// people at the granary to stand, and then they die alone while it walks. Eight seconds is about how
    /// long a villager survives a raider, so it is the window inside which help is help.
    /// </remarks>
    internal static float RallySeconds = 8f;

    /// <summary>
    /// How much stronger than the assailants a defence wants to be before it stands.
    /// </summary>
    /// <remarks>
    /// Above one, because equal strength means everybody dies and the grain is taken anyway. A defence that
    /// only fights when it expects to win is not cowardice, it is the whole content of the decision.
    /// </remarks>
    internal static float StandMargin = 1.25f;

    /// <summary>Seconds a body holds a decision to stand or run before asking again.</summary>
    internal static float ResolveSeconds = 3f;

    /// <summary>
    /// How wide an assailant is, in radians of the ring it has to stand on to reach a body.
    /// </summary>
    /// <remarks>
    /// <b>The engagement limit, and it is a derivation rather than a dial.</b> A body of radius <c>a</c>
    /// standing in contact with one of radius <c>t</c> has its centre on a circle of radius
    /// <c>t + a</c> and occupies an arc of <c>2·asin(a / (t + a))</c> of it. Sum those until the ring is
    /// full and you have how many can physically get at it — for two 0.37 m bodies, exactly six.
    /// <para>
    /// Which is the answer to "surround them and kill them with body heat". Twelve villagers on one raider
    /// were twelve villagers' worth of damage, so a fight was decided by <em>count</em> and nothing else,
    /// and no unit could ever be worth more than a warm body. Six of them fit; the other six are standing
    /// behind and doing nothing, which is what makes it worth having better ones rather than more.
    /// </para>
    /// <para>
    /// Mixed radii fall out of it for free: a wide body takes more of the ring and therefore crowds out
    /// more of its own side, and it reaches further, both of which a flat count would have to be told.
    /// </para>
    /// </remarks>
    internal static float ContactArc(float targetRadius, float attackerRadius)
    {
        var ring = targetRadius + attackerRadius;
        if (ring <= 1e-4f) return MathF.Tau;
        return 2f * MathF.Asin(Math.Clamp(attackerRadius / ring, 0f, 1f));
    }

    /// <summary>Blows that landed this tick — one per assailant that got at somebody.</summary>
    public int Contacts { get; private set; }

    /// <summary>
    /// Which bodies landed a blow this tick, so a report can ask who ever actually fought.
    /// </summary>
    /// <remarks>
    /// The distinction that keeps mattering: <em>committing</em> to a fight and <em>being in</em> one are
    /// different, and the gap between them has been the answer three times running. A count of contacts
    /// cannot tell you whether it was three bodies for a long time or thirty for an instant.
    /// </remarks>
    public IReadOnlyList<AgentId> LandedThisTick => landed;

    /// <summary>Distinct bodies that were being fought over this tick, and distinct bodies fighting.</summary>
    public int UnderAttack { get; private set; }

    public int Attacking { get; private set; }

    /// <summary>Who died this tick and whose they were, so a report can take sides.</summary>
    /// <remarks>
    /// Faction is carried out with the id because by the time anybody reads this the body is gone from the
    /// roster — "how many of ours died" cannot be answered after the fact, only recorded as it happens.
    /// </remarks>
    public IReadOnlyList<(AgentId Body, FactionId Faction)> FellThisTick => fellWithFaction;

    /// <summary>How many assailants were in reach this tick but could not get at their target.</summary>
    /// <remarks>
    /// The number that says the front is doing something. It was structurally zero before, because there
    /// was no front: everyone in reach landed a blow.
    /// </remarks>
    public int Crowded { get; private set; }

    private readonly List<AgentId> fallen = new();
    private readonly List<(AgentId Body, FactionId Faction)> fellWithFaction = new();
    private readonly List<(float Gap, int Id, int Index)> engaged = new();
    private readonly List<Vector2> declined = new();
    private readonly List<AgentId> landed = new();
    private readonly List<int> hostiles = new();
    private readonly List<Vector2> guarded = new();
    private readonly List<float> menace = new();

    /// <summary>
    /// Applies a tick of harm, and reports who died so the world can take them out of it.
    /// </summary>
    /// <remarks>
    /// Damage is accumulated for every body first and applied afterwards, in id order, so that two bodies
    /// killing each other in the same tick both die — the alternative resolves whoever the loop reached
    /// first and lets them survive, which makes the outcome of a fight depend on spawn order.
    /// <para>
    /// Deaths are handed back rather than acted on, because removing a body means dropping its cargo,
    /// releasing four colliders and finishing its path, and only the world knows how to do that.
    /// </para>
    /// </remarks>
    public IReadOnlyList<AgentId> Update(AgentStore agents, FactionRelations factions, float deltaSeconds)
    {
        fallen.Clear();
        fellWithFaction.Clear();
        var bodies = agents.MutableSpan();

        // One pass per body being fought over, rather than per attacker: the number of assailants that
        // can reach one body at once is capped, and a cap has to be applied where the thing being capped
        // is — see Engaged. Quadratic in bodies, affordable only because it early-outs on hostility: a
        // settlement at peace does one faction comparison per pair and nothing else.
        Crowded = 0;
        Contacts = 0;
        landed.Clear();
        UnderAttack = 0;
        Attacking = 0;
        for (var j = 0; j < bodies.Length; j++)
        {
            ref readonly var defender = ref bodies[j];
            // Indoors: it can neither reach out nor be reached. See AgentState.Sheltered — this is what
            // makes looting a window that runs to completion rather than a shoving match at the door.
            if (!defender.IsAlive || defender.Sheltered) continue;

            engaged.Clear();
            for (var i = 0; i < bodies.Length; i++)
            {
                if (i == j) continue;
                ref readonly var attacker = ref bodies[i];
                if (!attacker.IsAlive || attacker.Sheltered || attacker.Strength <= 0f) continue;
                if ((factions.Between(attacker.Faction, defender.Faction) & RelationMask.Enemy) == 0)
                {
                    continue;
                }

                var reach = (attacker.Radius + defender.Radius) * ReachShare;
                var gap = Vector2.Distance(attacker.Position, defender.Position);
                if (gap > reach) continue;
                engaged.Add((gap, attacker.Id.Value, i));
            }

            if (engaged.Count == 0) continue;

            // Nearest first, ties by id — so two runs of one fight admit the same people in the same order.
            engaged.Sort();

            var room = MathF.Tau;
            var struck = 0;
            foreach (var (_, _, index) in engaged)
            {
                ref readonly var attacker = ref bodies[index];
                var width = ContactArc(defender.Radius, attacker.Radius);
                // Squeezed out. Continue rather than break, because a narrower body further back may
                // still fit in what a wide one left — which is what "as many as fit" actually means.
                if (width > room) continue;
                room -= width;
                struck++;
                landed.Add(attacker.Id);
                bodies[j].Health -= attacker.Strength * deltaSeconds;
                Dealt += attacker.Strength * deltaSeconds;
            }

            Crowded += engaged.Count - struck;
            Contacts += struck;
            Attacking += struck;
            if (struck > 0) UnderAttack++;
        }

        for (var i = 0; i < bodies.Length; i++)
        {
            ref readonly var body = ref bodies[i];
            if (!body.IsAlive || body.Health > 0f) continue;
            fallen.Add(body.Id);
            fellWithFaction.Add((body.Id, body.Faction));
            Killed++;
        }

        return fallen;
    }

    /// <summary>What a body may be told to do about a threat, and where.</summary>
    internal delegate void March(AgentId body, Vector2 toward);

    /// <summary>
    /// Stop, because there is nothing to defend any more.
    /// </summary>
    /// <remarks>
    /// <b>The half of the interrupt that was missing.</b> An interrupt that only ever starts things leaves
    /// its last order standing: a defender marched at a raider, the raider ran, question one stopped finding
    /// anything worth protecting — and the defender kept walking to where the raider had been, in a straight
    /// line, until it got there. Watched, that is a column of villagers filing across the map after nothing
    /// at all. Standing down is an action and has to be taken.
    /// </remarks>
    internal delegate void Halt(AgentId body);

    /// <summary>
    /// Deal with what this body is carrying before it goes anywhere, and say if it is still busy.
    /// </summary>
    /// <remarks>
    /// The world owns the answer, because the answer is about stores, room and routes and none of those
    /// are the defence's business. All this layer knows is that a body with its hands full is not ready to
    /// fight, and that where the load goes must not be into the fight — hence the danger it is given.
    /// </remarks>
    internal delegate bool Stow(AgentId body, Vector2 danger);

    /// <summary>
    /// Go at a body rather than at a place, and keep going at it as it moves.
    /// </summary>
    /// <remarks>
    /// <b>A fight is a moving target, and marching to a point cannot catch one.</b> Measured: defenders
    /// re-aimed every three seconds, which is the commitment window and exactly right for <em>deciding</em>
    /// — and it meant each one was always walking to where the raider had been. A raider makes four metres
    /// in three seconds and a villager closes at a third of a metre a second, so the stale gap could never
    /// be shut: over eight raids, six hundred grain carried off, not one raider killed, and a column of
    /// villagers a hundred metres from home still following.
    /// <para>
    /// Deciding every three seconds and <em>steering</em> every tick are different jobs, and the movement
    /// layer already owns the second one. So a defender that stands is given the body, not the ground.
    /// </para>
    /// </remarks>
    internal delegate void Charge(AgentId body, AgentId target);

    /// <summary>Whether an observer can see a point — the world's sight test, trees and all.</summary>
    internal delegate bool Sees(in AgentState observer, Vector2 target);

    /// <summary>Bodies standing and bodies running, this instant, for the report.</summary>
    public int Standing { get; private set; }

    public int Fleeing { get; private set; }

    /// <summary>
    /// Bodies that saw a threat this tick and concluded somebody closer had it.
    /// </summary>
    /// <remarks>
    /// The number that says whether question two is doing its job. It was structurally zero before, because
    /// there was no such answer to give: twenty-six people saw one alarm and twenty-six people answered it.
    /// </remarks>
    public int Surplus { get; private set; }

    /// <summary>
    /// Civilians decide, without being asked, whether to defend what they can see or run for help.
    /// </summary>
    /// <remarks>
    /// <b>Three questions, in order, and nobody gives the order.</b> Protecting your own food should not need
    /// asking, which is what the interrupt layer was invented for long before it was ever a manual command —
    /// so this suspends the assignment and never touches it, and a villager who fights goes back to the
    /// field afterwards with its shift intact.
    /// <list type="number">
    /// <item><b>What can I see that I want to protect?</b> A store with something in it, a heap on the
    /// ground, or an ally carrying a load — anything that would leave with a raider. <em>Seen</em> is
    /// load-bearing: sight is occluded by trees, so an approach through a wood is not answered until it
    /// clears the trees.</item>
    /// <item><b>Am I needed?</b> Which is a sharper question than "can we take them", and the difference is
    /// the whole of what the first version got wrong. Everyone who can see the threatened thing and could
    /// reach it in time is ordered by <em>when they would arrive</em>, and a body stands only if the people
    /// arriving ahead of it are not already enough. Three answers rather than two: <b>needed</b> — stand;
    /// <b>surplus</b> — it is being handled by people closer than me, go back to work; <b>hopeless</b> —
    /// even everybody is not enough, run. Distance is in the sum twice over, because strength that cannot
    /// arrive before the fight is decided is not strength, and because who is nearest decides who goes.
    /// <para>
    /// Still no leader and no rally order: the candidate set is an objective fact — every ally that can see
    /// the place and reach it — so every observer builds the same ordered list and reads its own name in the
    /// same position. Ties break by id, or two runs of one raid disagree about who went.
    /// </para></item>
    /// <item><b>Fight, or run toward the nearest group bigger than mine.</b> Toward rather than away, which
    /// balls a settlement up under threat without anybody authoring a rally point — and the ball, once
    /// formed, may be strong enough that question two answers differently next time it is asked.</item>
    /// </list>
    /// </remarks>
    public void Defend(
        AgentStore agents,
        NodeStore nodes,
        FactionRelations factions,
        float deltaSeconds,
        Sees sees,
        March march,
        Halt halt,
        Stow stow,
        Charge charge)
    {
        Standing = 0;
        Fleeing = 0;
        Surplus = 0;
        var bodies = agents.MutableSpan();

        // Who is hostile to the settlement. Faction-agnostic: gathered once as indices, and each defender
        // asks the relation itself, so this works the same when the hostility is another player's people.
        //
        // <b>Sheltered bodies are deliberately in this list.</b> A raider rummaging inside the granary
        // cannot be fought and cannot be shoved, but it is emphatically still a threat to the granary — so
        // the alarm holds while it is in there, the defence gathers at the door it went in by, and the
        // people who gathered are standing there when it comes out. Excluding it here would look like
        // tidiness and would mean a settlement that goes back to work while it is being robbed.
        hostiles.Clear();
        for (var i = 0; i < bodies.Length; i++)
        {
            if (bodies[i].IsAlive && bodies[i].Strength > 0f) hostiles.Add(i);
        }

        // Peace is the overwhelmingly common case, and it has a much cheaper proof than a full defence
        // decision. Previously each villager rebuilt the list of guarded stores by walking every node in
        // the world — including tens of thousands of trees — even when every living body was an ally. One
        // faction comparison per body pair proves there can be no alarm at all and avoids those scans.
        //
        // <b>And the proof was "is there anybody to fight", which stopped being cheap the day there was.</b>
        // §133: founding a neighbour a hundred and ninety-eight metres away made an enemy relation exist
        // somewhere on the map, so every villager rebuilt its guarded list every tick for a war nobody was
        // fighting — 1.44 ms of a 1.55 ms tick, measured from the chair. The optimisation was right and its
        // premise was that there was nobody else in the world.
        //
        // <b>The range is stated by the tests below rather than chosen.</b> A body reacts to a resource
        // within its own <see cref="AgentState.SightMetres"/>, and to a hostile within
        // <see cref="ThreatMetres"/> of that resource — those are the two filters in Question, and together
        // they mean no hostile further than the sum of them can change any decision this pass makes. A
        // hostile carrying loot is itself the resource, so it is covered by the same bound at a gap of zero.
        // Conservative in the safe direction: the sum is an upper bound, so the full pass still runs in some
        // cases where nothing would have changed, and never fails to run in one where something would.
        //
        // Still one pair of comparisons per hostile per body, which is what it was — the expensive thing was
        // never this proof but the scans it gates. If body counts grow enough for an O(n squared) proof to
        // matter, the spatial index answers the same question in O(neighbours) and is not currently passed in.
        var enemyPresent = false;
        foreach (var hostileIndex in hostiles)
        {
            ref readonly var hostile = ref bodies[hostileIndex];
            for (var i = 0; i < bodies.Length; i++)
            {
                if (i == hostileIndex || !bodies[i].IsAlive) continue;
                if ((factions.Between(hostile.Faction, bodies[i].Faction) & RelationMask.Enemy) == 0)
                {
                    continue;
                }

                var matters = bodies[i].SightMetres + ThreatMetres;
                if (Vector2.DistanceSquared(hostile.Position, bodies[i].Position) > matters * matters)
                {
                    continue;
                }

                enemyPresent = true;
                break;
            }

            if (enemyPresent) break;
        }

        if (!enemyPresent)
        {
            for (var i = 0; i < bodies.Length; i++)
            {
                ref var body = ref bodies[i];
                if (!body.IsAlive || body.Directed || body.Sheltered) continue;
                body.Resolve = MathF.Max(0f, body.Resolve - deltaSeconds);
                body.HasQuarry = false;
                if (body.Resolve > 0f)
                {
                    body.Resolve = 0f;
                    halt(body.Id);
                }

                body.Standing = false;
            }

            return;
        }

        for (var i = 0; i < bodies.Length; i++)
        {
            ref var body = ref bodies[i];
            if (!body.IsAlive) continue;

            // Somebody else is already deciding where this body goes. See AgentState.Directed: the defence
            // is deliberately written against hostility rather than against raiders, and the price of that
            // is that it would run the raiders too if nothing said not to.
            if (body.Directed || body.Sheltered) continue;
            body.Resolve = MathF.Max(0f, body.Resolve - deltaSeconds);

            // 1. What can I see that is worth protecting, and is something reaching for it?
            //
            // <b>Asked repeatedly, nearest first, until one of them wants me.</b> One threat and one
            // answer was the shape, and it wasted the surplus: if six can already get at the raider at
            // the granary I am not needed there — which is not the same as not being needed. The rest of
            // its party is elsewhere, and a body that has already left its field should go where it can
            // do something rather than turn round. So being surplus at the nearest alarm is a reason to
            // look at the next one, and only when every alarm in sight is covered is the answer "back to
            // work".
            declined.Clear();
            var threat = 0f;
            var where = Vector2.Zero;
            var at = Vector2.Zero;
            var assailant = new AgentId(-1);
            var ours = 0f;
            var stand = false;
            var needed = false;
            while (true)
            {
                threat = NearestThreatSeen(
                    bodies, nodes, factions, sees, in body, out where, out at, out assailant);
                if (threat <= 0f) break;

                Muster(bodies, factions, sees, in body, where, out ours, out var ahead);
                if (ahead < threat * StandMargin)
                {
                    needed = true;
                    stand = ours >= threat * StandMargin;
                    break;
                }

                // Covered by people closer than me. Try the next thing I can see.
                declined.Add(where);
            }

            if (!needed)
            {
                // Nothing wants me: either there is no alarm at all, or every one I can see is already
                // covered by people closer to it. Both end the same way — back to work — and both stand
                // down once, on the tick it becomes true, rather than walking off after the memory of a
                // raider. A body with no commitment to drop was never engaged and is left alone, which is
                // what keeps this off the twenty-five people who are simply working.
                if (declined.Count > 0) Surplus++;
                body.HasQuarry = false;
                if (body.Resolve > 0f)
                {
                    body.Resolve = 0f;
                    halt(body.Id);
                }

                body.Standing = false;
                continue;
            }

            // <b>Hands first.</b> A villager who runs at a raider with forty grain on its back is carrying
            // the raider's prize into its reach: it loses the fight, the goods change hands on the spot,
            // and the raid is paid for by the defence. So a defender that has decided to fight stows its
            // load first — into a store out of the trouble if there is one, on the ground if not — and
            // joins with its hands free. Which store, and whether there is one, is the world's question.
            //
            // Note what this is *not*: it does not touch the assignment. The villager goes back to the
            // same field afterwards, and the units it was carrying are still in the ledger the whole time.
            if (stand && body.Jobs.CarriedUnits > 0 && stow(body.Id, at))
            {
                // Decided again next tick rather than held, so that the instant its hands are empty it
                // goes — a commitment window here would leave it standing about having just put a sack
                // down while the fight it committed to happens without it.
                body.Standing = false;
                body.Resolve = 0f;
                continue;
            }

            if (stand) Standing++;
            else Fleeing++;

            // Held for a few seconds, or a body on the margin flips forever and does neither — but the
            // hold is on the <em>decision</em>, not on the target.
            //
            // <b>Conflating those two cost fourteen of twenty-four raiders their escape.</b> A body
            // committed to the granary while a thief was inside it kept that commitment when the thief came
            // out and ran, because "still standing" looked like nothing had changed: same answer, resolve
            // unexpired, so no new order. It walked to a doorway with nobody in it while the loot went over
            // the hill. Whether to fight is worth holding for three seconds; <em>what to fight</em> is not,
            // and a fight whose subject has moved on is a different fight.
            var elsewhere = Vector2.DistanceSquared(body.Guarding, where) > ElsewhereSquared;
            if (body.Resolve > 0f && body.Standing == stand && !elsewhere) continue;
            body.Resolve = ResolveSeconds;
            body.Standing = stand;
            body.Guarding = where;

            // 3. At them, or toward the nearest group bigger than ours.
            if (stand)
            {
                if (assailant.Value >= 0)
                {
                    body.Quarry = assailant;
                    body.HasQuarry = true;
                    charge(body.Id, assailant);
                }
                else
                {
                    body.HasQuarry = false;
                    march(body.Id, at);
                }
                continue;
            }

            march(body.Id, Refuge(bodies, factions, in body, ours, at));
        }
    }

    /// <summary>
    /// The nearest thing this body can see that it wants to protect and something is reaching for.
    /// </summary>
    /// <remarks>
    /// Stores and heaps are places; a loaded ally is a resource that walks, which is why a carter on the
    /// road is worth defending and an empty one is not. Returns the assailants' strength there, so question
    /// two has the number it needs without looking again.
    /// </remarks>
    private float NearestThreatSeen(
        Span<AgentState> bodies,
        NodeStore nodes,
        FactionRelations factions,
        Sees sees,
        in AgentState body,
        out Vector2 where,
        out Vector2 at,
        out AgentId assailant)
    {
        where = default;
        at = default;
        assailant = new AgentId(-1);
        var bestCost = float.PositiveInfinity;
        var bestThreat = 0f;

        guarded.Clear();
        foreach (ref readonly var node in nodes.All)
        {
            if (!node.IsAlive || node.Stock.Total <= 0) continue;
            if (node.IsNaturalDeposit) continue;
            if (!node.Stores && !node.IsPile) continue;
            if (node.Faction != body.Faction && !node.IsPile) continue;
            guarded.Add(node.Position);
        }

        for (var i = 0; i < bodies.Length; i++)
        {
            ref readonly var other = ref bodies[i];
            if (!other.IsAlive || other.Sheltered || other.Jobs.CarriedUnits <= 0) continue;
            var relation = factions.Between(body.Faction, other.Faction);
            if ((relation & RelationMask.Ally) != 0)
            {
                // A carter on the road is a resource that walks, which is why a loaded one is worth
                // defending and an empty one is not.
                guarded.Add(other.Position);
                continue;
            }

            // <b>And so is a raider carrying your grain, which is the case that was missing.</b> Question
            // one asks what would leave with a raider; the raider that is already carrying it is the most
            // literal answer there is, and without it the numbers said this plainly — over eight raids,
            // twenty-four raiders, six hundred grain carried off and <em>not one raider killed</em>. The
            // reason was the stand-down: once a thief was twelve metres from the granary it threatened
            // nothing, so the defence correctly went back to work and the loot always got home.
            //
            // Pursuit is leashed by sight rather than by a distance anybody chose. A defender chases what
            // it can see; a raider with a big enough head start is gone, and one that is slowed by what it
            // is carrying is not. That is §7's argument in full — killing it returns the grain rather than
            // denying it, so the walk home is the window and the loot is what makes the window worth taking.
            if ((relation & RelationMask.Enemy) != 0) guarded.Add(other.Position);
        }

        foreach (var resource in guarded)
        {
            var distance = Vector2.Distance(body.Position, resource);
            if (distance > body.SightMetres) continue;
            if (AlreadyDeclined(resource)) continue;
            if (!sees(in body, resource)) continue;

            // Anything hostile close enough to be reaching for it. Nearest to the thing being protected,
            // <em>except</em> that a body sticks to the quarry it already has — see below.
            var strength = 0f;
            var nearest = float.PositiveInfinity;
            var attacker = Vector2.Zero;
            var who = new AgentId(-1);
            var held = false;
            foreach (var index in hostiles)
            {
                ref readonly var hostile = ref bodies[index];
                if ((factions.Between(body.Faction, hostile.Faction) & RelationMask.Enemy) == 0) continue;
                if (Vector2.Distance(hostile.Position, resource) > ThreatMetres) continue;
                strength += hostile.Strength;

                // <b>Stay on the one you are already fighting.</b> A third of all the harm in a raid was
                // going into bodies that walked away wounded, because a defender re-chose its target every
                // few seconds and picked whatever was nearest — so a raid that scattered scattered the
                // damage with it, and every thief left just under the threshold.
                //
                // Global triage was tried first and is <b>rejected on measurement</b>: scoring every fight
                // by how soon it would be over if I joined it, so that a wounded thief with three people on
                // it beat a fresh one standing nearer. It reads well and it was worse on every metric over
                // ten runs — their dead 3.2 to 2.4, stolen 472 to 504, and half again as much harm wasted.
                // It sent people walking to distant fights instead of fighting, and it thrashed: joining a
                // target makes it more attractive, which re-scores it for everybody at once.
                //
                // Concentration by <em>commitment</em> costs one identity comparison and cannot thrash,
                // because nothing about my target changes when somebody else picks theirs.
                var isQuarry = body.HasQuarry && hostile.Id == body.Quarry && !hostile.Sheltered;
                if (held && !isQuarry) continue;
                var gap = Vector2.Distance(hostile.Position, resource);
                if (!isQuarry && gap >= nearest) continue;
                nearest = gap;
                attacker = hostile.Position;
                // Named, so a defender can be sent at it rather than at the ground it is standing on.
                // A body indoors is not something to charge — it is a reason to be waiting at the door.
                who = hostile.Sheltered ? new AgentId(-1) : hostile.Id;
                held = isQuarry;
            }

            if (strength <= 0f) continue;
            var cost = distance;
            if (cost >= bestCost) continue;
            bestCost = cost;
            bestThreat = strength;
            where = resource;
            at = attacker;
            assailant = who;
        }

        return bestThreat;
    }

    /// <summary>
    /// How much help a place can get, and how much of it arrives ahead of this body.
    /// </summary>
    /// <remarks>
    /// One walk of the roster answers both halves of question two. <c>total</c> is everyone who could be
    /// there in time, which decides whether the fight is winnable at all; <c>ahead</c> is the part of that
    /// which arrives before this body does, which decides whether this body is wanted.
    /// <para>
    /// Ordered by <em>seconds</em> rather than metres, so a body that is far but quick counts as nearer than
    /// one that is close and slow, and ties break by id so two runs of the same raid send the same people.
    /// </para>
    /// <para>
    /// An ally already committed somewhere else is not available here. Without that, a settlement's whole
    /// strength is counted against every alarm on the map at once — so two raiders at opposite ends of a
    /// village each look answerable by everybody, the same people are notionally sent to both, and neither
    /// gets answered.
    /// </para>
    /// </remarks>
    private void Muster(
        Span<AgentState> bodies,
        FactionRelations factions,
        Sees sees,
        in AgentState body,
        Vector2 where,
        out float total,
        out float ahead)
    {
        total = 0f;
        ahead = 0f;
        var mine = ArrivalSeconds(in body, where);
        for (var i = 0; i < bodies.Length; i++)
        {
            ref readonly var ally = ref bodies[i];
            if (!ally.IsAlive || ally.Strength <= 0f || ally.Directed) continue;
            if ((factions.Between(body.Faction, ally.Faction) & RelationMask.Ally) == 0) continue;

            // Distance is in the sum, in seconds of walking rather than metres, because what decides
            // whether help is help is whether it arrives.
            var theirs = ArrivalSeconds(in ally, where);
            if (theirs > RallySeconds) continue;

            // Busy elsewhere, and it stays busy: the commitment window is what makes this stable rather
            // than a settlement that re-allocates itself every tick.
            if (ally.Standing && Vector2.DistanceSquared(ally.Guarding, where) > ElsewhereSquared) continue;

            // <b>Hands full is not available, and this one was measured.</b> A body walking a load to a
            // store still sorts by where it is standing, so the people behind it in the queue read it as
            // covering the fight while it is in fact off delivering grain — and the defence arrives two
            // bodies short of what it committed to. Excluded, so the next of the surplus steps up instead.
            // Never the asker: a body must count itself, or a laden villager reads the fight as hopeless
            // and runs from something it could win once its hands were free.
            if (ally.PuttingDown && ally.Id != body.Id) continue;
            if (ally.Sheltered) continue;
            if (!sees(in ally, where)) continue;

            total += ally.Strength;
            if (theirs < mine || (theirs == mine && ally.Id.Value < body.Id.Value)) ahead += ally.Strength;
        }
    }

    /// <summary>How long this body would take to walk to a place, at its own best pace.</summary>
    private static float ArrivalSeconds(in AgentState body, Vector2 where) =>
        body.MaximumSpeed > 0.01f ? Vector2.Distance(body.Position, where) / body.MaximumSpeed : 1e9f;

    /// <summary>Whether this body has already found itself surplus to a place this tick.</summary>
    private bool AlreadyDeclined(Vector2 resource)
    {
        foreach (var seen in declined)
        {
            if (Vector2.DistanceSquared(seen, resource) <= ElsewhereSquared) return true;
        }

        return false;
    }

    /// <summary>How far apart two threatened places have to be to count as different alarms.</summary>
    /// <remarks>
    /// A granary and the heap beside it are one fight, not two, and a defender committed to either should
    /// count toward both. Loose enough to cover a building and its yard.
    /// </remarks>
    private static float ElsewhereSquared => 8f * 8f;

    /// <summary>
    /// Where to run: toward the nearest ally standing in a bigger group than this one.
    /// </summary>
    /// <remarks>
    /// Toward rather than away, which is what makes a settlement gather rather than scatter — and a
    /// gathering is how question two comes to be answered differently. Nearest, with ties broken by id, or
    /// two runs of the same raid part company over which way somebody ran.
    /// <para>
    /// With nowhere better to be, directly away from whatever is coming. That is the honest fallback: a lone
    /// villager with no larger group anywhere should still not stand there.
    /// </para>
    /// </remarks>
    private Vector2 Refuge(
        Span<AgentState> bodies,
        FactionRelations factions,
        in AgentState body,
        float ours,
        Vector2 attacker)
    {
        var best = Vector2.Zero;
        var bestDistance = float.PositiveInfinity;
        for (var i = 0; i < bodies.Length; i++)
        {
            ref readonly var ally = ref bodies[i];
            if (!ally.IsAlive || ally.Id == body.Id || ally.Strength <= 0f) continue;
            if ((factions.Between(body.Faction, ally.Faction) & RelationMask.Ally) == 0) continue;

            var theirs = 0f;
            for (var j = 0; j < bodies.Length; j++)
            {
                ref readonly var other = ref bodies[j];
                if (!other.IsAlive || other.Strength <= 0f) continue;
                if ((factions.Between(ally.Faction, other.Faction) & RelationMask.Ally) == 0) continue;
                if (Vector2.Distance(other.Position, ally.Position) > other.MaximumSpeed * RallySeconds)
                {
                    continue;
                }

                theirs += other.Strength;
            }

            if (theirs <= ours) continue;
            var distance = Vector2.Distance(body.Position, ally.Position);
            if (distance >= bestDistance) continue;
            bestDistance = distance;
            best = ally.Position;
        }

        if (bestDistance < float.PositiveInfinity) return best;
        var away = body.Position - attacker;
        return away.LengthSquared() > 0.0001f
            ? body.Position + Vector2.Normalize(away) * (body.MaximumSpeed * RallySeconds)
            : body.Position;
    }

    internal void Write(Persistence.WorldWriter writer)
    {
        writer.Long(Killed);
        writer.Float(Dealt);
    }

    internal void Read(Persistence.WorldReader reader)
    {
        Killed = reader.Long();
        Dealt = reader.Float();
    }
}

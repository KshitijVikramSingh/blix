using System.Numerics;
using RTSGame.Simulation;
using RTSGame.Simulation.Agents;
using RTSGame.Simulation.Collision;
using RTSGame.Simulation.Economy;
using RTSGame.Simulation.Terrain;
using RTSGame.Simulation.Jobs;

namespace RTSGame.Debug;

/// <summary>
/// Fighting and chasing, isolated, so a change to either can actually be measured.
/// </summary>
/// <remarks>
/// <b>This exists because five locomotion changes were judged by the wrong instrument and all five
/// verdicts were wrong.</b> §40: <c>--raidtest</c> measures economy, jobs, hauling, threat, pathing and a
/// scripted director together over six minutes, so anything that perturbs timing reroutes the run — and
/// the same unchanged configuration produced 4.4 or 2.4 raiders killed depending on which five seeds were
/// drawn. It is a good gate for "did something break" and useless for "is this better".
/// <para>
/// So these follow the pattern the movement benchmarks already established for crowds: one mechanism per
/// scenario, open ground, no economy, seconds to run, and every number reported rather than summarised. The
/// three cases are the three things this session kept guessing about:
/// </para>
/// <list type="number">
/// <item><b>A ring.</b> N defenders against one raider standing still. How long to kill it, how much of the
/// time anybody is actually in contact, and how many of the N ever land a blow. This is the engagement
/// limit and the concentration question, with nothing else in the frame.</item>
/// <item><b>A chase.</b> One pursuer, one quarry, a speed ratio. What share of the chase is spent
/// <em>inside reach</em> — the number every one of the five rejected hypotheses was really about, and not
/// one of them measured it.</item>
/// <item><b>A cordon.</b> One body trying to cross a ring of enemies standing in its way. Does it get
/// through, how long does it take, how far does it deviate. That is the reported bug — "slid in, got
/// surrounded, walked away" — stated as a measurement.</item>
/// </list>
/// <para>
/// Deterministic and cheap, so a question can be swept across a dozen configurations instead of five.
/// </para>
/// </remarks>
internal static class FightBenchmarks
{
    private const int TicksPerSecond = 30;
    private static readonly FactionId Ours = new(0);
    private static readonly FactionId Theirs = new(1);

    public static int Run()
    {
        Console.WriteLine("RTSGame fight benchmarks — one mechanism per scenario, open ground");

        Console.WriteLine();
        Console.WriteLine("  a ring: N villagers on one stationary raider");
        Console.WriteLine(
            "    N | killed in | contact% | landed/N | most at once | body-s of contact");
        foreach (var count in new[] { 1, 2, 3, 4, 6, 8, 12 })
        {
            Ring(count);
        }

        // <b>Offence, swept, on three grounds.</b> §189. The chair asked for "all scenarios of offence and
        // defence" with spread accounted for, and then for every one of them to run on flat ground AND on
        // relief and dense maps — "to see how aggression/defence plays when geography, geometry, other
        // non-participating units and buildings are around". That last axis is the one most likely to
        // explain what §188 could not reproduce on an empty plain.
        var grounds = new[] { Ground.Flat, Ground.Rough, Ground.Village };

        Console.WriteLine();
        Console.WriteLine("  an ordered assault: N of ours TOLD to attack one of theirs, which fights back");
        Console.WriteLine(
            "    ground  |  N | target   | force   | spread | to 1st blow | killed in | contact% | " +
            "landed/N | gaps>1s | rev/body | drifted | apart at contact");
        foreach (var ground in grounds)
        foreach (var posture in new[] { TargetPosture.Still, TargetPosture.Fleeing, TargetPosture.Charging })
        {
            Assault(4, posture, Force.Militia, spreadMetres: 20f, ground);
        }

        Console.WriteLine("    -- villagers pulled off their work, mixed in with militia:");
        foreach (var ground in grounds)
        {
            Assault(4, TargetPosture.Charging, Force.Mixed, spreadMetres: 20f, ground);
        }

        Console.WriteLine("    -- eight sent from close together and from far apart:");
        foreach (var ground in grounds)
        foreach (var spread in new[] { 4f, 20f })
        {
            Assault(8, TargetPosture.Charging, Force.Militia, spread, ground);
        }

        Console.WriteLine();
        Console.WriteLine("  a nearer enemy: ours ordered at a distant target with a hostile in their faces");
        Console.WriteLine(
            "    ground  |  N | blows on near | to 1st | blows on the ordered one | " +
            "closed on the near one by");
        foreach (var ground in grounds)
        {
            NearerEnemy(4, ground);
        }

        Console.WriteLine();
        Console.WriteLine("  a structure: N ordered at a hostile palisade, undefended and defended");
        Console.WriteLine(
            "    ground  |  N | defenders | to 1st hit | condition lost | " +
            "ticks anybody was working | interrupted%");
        foreach (var ground in grounds)
        foreach (var defenders in new[] { 0, 3 })
        {
            StructureAssault(4, defenders, ground);
        }

        Console.WriteLine();
        Console.WriteLine("  a chase: one pursuer, one quarry, 40 m of running room");
        Console.WriteLine(
            "    quarry pace | caught | inside reach% | gap at end | closing m/s");
        foreach (var share in new[] { 1.15f, 1.00f, 0.90f, 0.80f, 0.70f, 0.60f })
        {
            Chase(share);
        }

        Console.WriteLine();
        Console.WriteLine("  a chase, swept on the stop distance the chase behaviour asks for");
        Console.WriteLine(
            "    stop at | effective gap | inside reach% | caught a 0.7x quarry in");
        var wasStop = AgentDefaults.ChaseStopMetres;
        foreach (var stop in new[] { 0.95f, 0.60f, 0.30f, 0.0f })
        {
            AgentDefaults.ChaseStopMetres = stop;
            Chase(0.70f, label: $"{stop:F2} m");
        }

        AgentDefaults.ChaseStopMetres = wasStop;

        Console.WriteLine();
        Console.WriteLine("  a cordon: one body crossing a ring of N enemies stood in the way");
        Console.WriteLine(
            "    N | crossed | seconds | detour | held below half pace for");
        foreach (var count in new[] { 2, 4, 6, 8, 12 })
        {
            Cordon(count);
        }

        return 0;
    }

    /// <summary>Which ground a fight is being measured on.</summary>
    /// <remarks>
    /// <b>Asked for from the chair, and it is the axis most likely to explain what open ground could not.</b>
    /// §189: §188 measured every case on a flat empty field and could not reproduce "they go there and
    /// stand" at all — first blow landed within two seconds of arriving. A fight has geometry in it, and a
    /// flat void has none: no slope to charge up, no trunk to path around, nobody else's body in the way.
    /// <para>
    /// The original three scenarios (ring, chase, cordon) stay flat on purpose — §40 built them to isolate
    /// one mechanism each, and terrain is exactly the confound they exist to exclude. The ordered-attack
    /// scenarios run on all three grounds, because the complaint they encode is about a real map.
    /// </para>
    /// </remarks>
    private enum Ground
    {
        /// <summary>An empty plain, which is where every combat constant was measured.</summary>
        Flat,

        /// <summary>Thirty metres of relief with the country painted on it, and woodland scattered.</summary>
        Rough,

        /// <summary>Rough, plus one of our working settlements: buildings, trees and busy bystanders.</summary>
        Village,
    }

    /// <summary>
    /// Builds the ground a fight will be measured on, and says where on it there is room to fight.
    /// </summary>
    /// <remarks>
    /// The centre comes from <c>SettlementScenarios.ChooseSite</c> rather than being picked, because that is
    /// the routine the game itself uses to find ground level enough to stand a settlement on — so a fight
    /// placed there is on ground somebody would plausibly be fighting over, and not in a river.
    /// </remarks>
    private static SimulationWorld BuildGround(Ground ground, float extent, out Vector2 centre)
    {
        if (ground == Ground.Flat)
        {
            centre = Vector2.Zero;
            return new SimulationWorld(extent);
        }

        // <b>Quiet during setup.</b> Founding a settlement prints its country, its site reasoning and five
        // lines of economy survey, and the ground axis calls it nine times — which buried the table this
        // harness exists to print under two hundred lines of scenery. The setup's own reporting is right
        // where it lives; it is simply not what is being measured here.
        var spoke = Console.Out;
        Console.SetOut(TextWriter.Null);
        try
        {
            return BuildGroundQuietly(ground, extent, out centre);
        }
        finally
        {
            Console.SetOut(spoke);
        }
    }

    private static SimulationWorld BuildGroundQuietly(Ground ground, float extent, out Vector2 centre)
    {
        var world = new SimulationWorld(extent);
        world.Terrain.SetRegion(Region.Downland);
        var layout = MapLayout.Composed(Archetype.SplitValley, extent, 0x5EED1234u, 30f);
        ReliefPlan.FromLayout(layout, extent, 0x5EED1234u).Apply(world.Terrain);
        SettlementScenarios.PaintCountry(world);
        world.RebuildTerrainNavigation();
        centre = SettlementScenarios.ChooseSite(world, extent);

        if (ground == Ground.Village)
        {
            // <b>Somebody else's day going on around the fight.</b> A settlement brings buildings to path
            // around, woodland scattered over the whole map, and a dozen bodies with jobs of their own that
            // the fight has to happen through. That is the "other non participating units/buildings" the
            // chair asked for, and it is one call because the settlement definition is shared.
            SettlementScenarios.Populate(
                world, farms: 4, woodcutters: 3, quarriers: 1, carts: 2, wagons: 1,
                centre: centre, faction: Ours);
            for (var t = 0; t < 30 * TicksPerSecond; t++)
            {
                world.Tick((float)SimulationWorld.FixedDeltaSeconds);
            }
        }

        return world;
    }

    /// <summary>What the thing being attacked is doing about it.</summary>
    /// <remarks>
    /// <b>The axis that mattered most.</b> §188 measured a still target and a moving one and the difference
    /// was three and a half times the time to kill. Fleeing and charging are split apart because they are
    /// not one axis with a sign: a body running away is a pursuit problem, and a body running at you is a
    /// problem about whether anyone stops walking and swings.
    /// </remarks>
    private enum TargetPosture
    {
        /// <summary>Stands and fights. The fight with the running taken out.</summary>
        Still,

        /// <summary>Walks away from the fight it is losing.</summary>
        Fleeing,

        /// <summary>Comes at us, which is the case the chair reported ours backing away from.</summary>
        Charging,
    }

    /// <summary>Who we sent.</summary>
    private enum Force
    {
        /// <summary>Soldiers, who have no other job to go back to.</summary>
        Militia,

        /// <summary>
        /// Soldiers plus villagers taken off their work, which is what a player actually does.
        /// </summary>
        /// <remarks>
        /// <b>Asked for by name, to check they do not run back midway.</b> A villager ordered to attack has
        /// its <c>Work</c> assignment replaced, so nothing should pull it home — but §187 has just changed
        /// what hands a body back to its standing job, and a fixture that would have caught that going wrong
        /// is worth more than the argument that it cannot.
        /// </remarks>
        Mixed,
    }

    /// <summary>
    /// N bodies <em>ordered</em> to attack, which is a different path from N bodies deciding to.
    /// </summary>
    /// <remarks>
    /// <b>The path nothing measured.</b> §188. All three original scenarios exercise the <em>defence</em> —
    /// bodies that see a hostile and commit themselves through §30. An <see cref="AssignmentKind.Attack"/>
    /// arrives the other way, through <c>QueueAttack</c>, the door a player's right-click goes through, and
    /// it had no harness. Reported from the chair after ordering attacks by hand: they "go there and stand,
    /// won't attack enemies efficiently, get stuck in stagger loops", and then "don't attack nearby
    /// targets, instead try to stand/move back".
    /// <para>
    /// Each complaint is a column, because a complaint that is not a number gets argued about instead of
    /// fixed. <c>to 1st blow</c> is "go there and stand". <c>contact%</c> and <c>landed/N</c> are the duty
    /// cycle and the concentration, the same two the ring reports so the ordered path and the volunteered
    /// one compare directly. <c>gaps&gt;1s</c> and <c>rev/body</c> are two independent readings of a
    /// stagger — one temporal, one geometric — because they can disagree, and a stagger visible in only one
    /// is a different mechanism from one visible in both. <c>drifted</c> catches a body abandoning the
    /// order. <c>apart at contact</c> is the spread: how far our own bodies are from each other when the
    /// first blow lands, which is the crowding question stated as a distance.
    /// </para>
    /// </remarks>
    private static void Assault(
        int count, TargetPosture posture, Force force, float spreadMetres, Ground ground)
    {
        var world = BuildGround(ground, 240f, out var centre);
        var victim = world.SpawnAgent(centre, UnitType.Militia, Theirs);
        ref var quarry = ref world.Agents.Get(victim);
        // Directed so it does not run the civilian defence and make its own decisions — see
        // AgentState.Directed. It still fights back, because harm is dealt on contact and not decided.
        quarry.Directed = true;
        var health = quarry.Health;

        // A field for the villagers to be taken off, when there are any.
        var field = world.AddNode(
            NodeKind.Farm, centre + new Vector2(-spreadMetres - 12f, 0f), capacity: 400, faction: Ours);

        var mob = new List<AgentId>();
        var villagers = new List<AgentId>();
        for (var i = 0; i < count; i++)
        {
            var angle = i / (float)MathF.Max(1, count) * MathF.Tau;
            var soldier = force == Force.Militia || i % 2 == 0;
            var id = world.SpawnAgent(
                centre + new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * spreadMetres,
                soldier ? UnitType.Militia : UnitType.Villager,
                Ours);
            mob.Add(id);
            if (!soldier) villagers.Add(id);
        }

        // <b>Given real work first, then taken off it.</b> Ordering an idle villager to fight proves
        // nothing about a villager being pulled off a job, which is the reported case.
        if (villagers.Count > 0)
        {
            ref readonly var crop = ref world.Nodes.Get(field);
            world.QueueAssign(villagers, Assignment.Hold(crop.Position, 0.5f, crop.FootprintRadius));
            for (var t = 0; t < 60; t++) world.Tick((float)SimulationWorld.FixedDeltaSeconds);
        }

        world.QueueAttack(mob, victim, NodeId.None);

        // What the target does about it. Charging aims at the middle of our formation.
        if (posture == TargetPosture.Fleeing)
        {
            world.QueueMove(new[] { victim }, centre + new Vector2(90f, 0f));
        }
        else if (posture == TargetPosture.Charging)
        {
            world.QueueMove(new[] { victim }, centre + new Vector2(spreadMetres, 0f));
        }

        var ours = mob.Select(id => id.Value).ToHashSet();
        var landed = new HashSet<int>();
        var contactTicks = 0;
        var firstBlow = -1f;
        var killedAt = -1f;
        var apartAtContact = -1f;

        var lastBlowTick = new Dictionary<int, int>();
        var longGaps = 0;
        var closing = new Dictionary<int, float>();
        var wasClosing = new Dictionary<int, bool>();
        var reversals = new Dictionary<int, int>();

        var limit = 120 * TicksPerSecond;
        for (var tick = 1; tick <= limit; tick++)
        {
            world.Tick((float)SimulationWorld.FixedDeltaSeconds);
            if (!world.Agents.Contains(victim) || !world.Agents.Get(victim).IsAlive)
            {
                killedAt = tick / (float)TicksPerSecond;
                break;
            }

            if (world.Threat.Contacts > 0) contactTicks++;

            foreach (var who in world.Threat.LandedThisTick)
            {
                // <b>Ours only.</b> LandedThisTick is every blow anybody landed, the target's included —
                // it fights back, and counting its blows pushed landed/N above N. The ring scenario read
                // 5/4 and 5/12 for four sections for the same reason.
                if (!ours.Contains(who.Value)) continue;
                landed.Add(who.Value);
                if (firstBlow < 0f)
                {
                    firstBlow = tick / (float)TicksPerSecond;
                    apartAtContact = MeanSeparation(world, mob);
                }

                if (lastBlowTick.TryGetValue(who.Value, out var previous) &&
                    tick - previous > TicksPerSecond)
                {
                    longGaps++;
                }

                lastBlowTick[who.Value] = tick;
            }

            // The geometric stagger reading, and only for a body that has been in contact once: before
            // that, closing on the target is simply the walk and reverses for good reasons.
            var at = world.Agents.Get(victim).Position;
            foreach (var id in mob)
            {
                if (!lastBlowTick.ContainsKey(id.Value)) continue;
                if (!world.Agents.Contains(id) || !world.Agents.Get(id).IsAlive) continue;
                var gap = Vector2.Distance(world.Agents.Get(id).Position, at);
                if (closing.TryGetValue(id.Value, out var before))
                {
                    var nearing = gap < before - 0.01f;
                    if (nearing || gap > before + 0.01f)
                    {
                        if (wasClosing.TryGetValue(id.Value, out var then) && then != nearing)
                        {
                            reversals[id.Value] = reversals.GetValueOrDefault(id.Value) + 1;
                        }

                        wasClosing[id.Value] = nearing;
                    }
                }

                closing[id.Value] = gap;
            }
        }

        var drifted = mob.Count(id =>
            world.Agents.Contains(id) && world.Agents.Get(id).IsAlive &&
            world.Agents.Get(id).Jobs.Assignment.Kind != AssignmentKind.Attack);
        var elapsed = killedAt > 0f ? killedAt : limit / (float)TicksPerSecond;
        var perBody = landed.Count == 0 ? 0f : reversals.Values.Sum() / (float)landed.Count;
        var done = world.Agents.Contains(victim) ? health - world.Agents.Get(victim).Health : health;

        Console.WriteLine(
            $"    {ground,-7} | {count,2} | {posture,-8} | {force,-7} | {spreadMetres,4:F0} m | " +
            $"{(firstBlow > 0f ? $"{firstBlow,8:F1} s" : "   never"),11} | " +
            $"{(killedAt > 0f ? $"{killedAt,6:F1} s" : "never"),9} | " +
            $"{100f * contactTicks / (elapsed * TicksPerSecond),7:F0}% | {landed.Count,4}/{count,-3} | " +
            $"{longGaps,7} | {perBody,8:F1} | {drifted,7} | " +
            $"{(apartAtContact >= 0f ? $"{apartAtContact,10:F1} m" : "         -"),12}" +
            (killedAt > 0f ? string.Empty : $"  ({done:F0} of {health:F0} done)"));
    }

    /// <summary>Mean distance between every pair of our bodies, which is the spread.</summary>
    private static float MeanSeparation(SimulationWorld world, List<AgentId> mob)
    {
        var total = 0f;
        var pairs = 0;
        for (var i = 0; i < mob.Count; i++)
        for (var j = i + 1; j < mob.Count; j++)
        {
            if (!world.Agents.Contains(mob[i]) || !world.Agents.Contains(mob[j])) continue;
            total += Vector2.Distance(
                world.Agents.Get(mob[i]).Position, world.Agents.Get(mob[j]).Position);
            pairs++;
        }

        return pairs == 0 ? 0f : total / pairs;
    }

    /// <summary>
    /// Ours ordered at something far away, with a hostile standing in their faces.
    /// </summary>
    /// <remarks>
    /// <b>Reported from the chair: "they don't attack nearby targets/attackers and instead try to stand or
    /// move back".</b> An <see cref="AssignmentKind.Attack"/> names one target, so the question is what
    /// happens to the enemy that is not it — whether the defence's own judgement (§30) cuts in and swings,
    /// or whether the order walks the body straight past somebody hitting it.
    /// <para>
    /// <c>closed on the near one by</c> is the "move back" half, and the sign is the finding: negative means
    /// our bodies ended the run <em>further</em> from the enemy in their faces than they started.
    /// </para>
    /// </remarks>
    private static void NearerEnemy(int count, Ground ground)
    {
        var world = BuildGround(ground, 260f, out var centre);

        // The ordered target, a long way off so the walk cannot be mistaken for engaging.
        var far = world.SpawnAgent(centre + new Vector2(80f, 0f), UnitType.Militia, Theirs);
        world.Agents.Get(far).Directed = true;
        world.Agents.Get(far).MaximumSpeed = 0f;

        var mob = new List<AgentId>();
        for (var i = 0; i < count; i++)
        {
            mob.Add(world.SpawnAgent(
                centre + new Vector2(0f, -2f + i * 1.1f), UnitType.Militia, Ours));
        }

        world.QueueAttack(mob, far, NodeId.None);

        // And a hostile right on top of them, which nobody ordered anything about.
        var near = world.SpawnAgent(centre + new Vector2(2.5f, 0f), UnitType.Militia, Theirs);
        world.Agents.Get(near).Directed = true;
        world.Agents.Get(near).MaximumSpeed = 0f;
        var nearHealth = world.Agents.Get(near).Health;
        var farHealth = world.Agents.Get(far).Health;
        var gapBefore = MeanGapTo(world, mob, near);

        var firstOnNear = -1f;
        var limit = 90 * TicksPerSecond;
        for (var tick = 1; tick <= limit; tick++)
        {
            world.Tick((float)SimulationWorld.FixedDeltaSeconds);
            if (firstOnNear < 0f && world.Agents.Contains(near) &&
                world.Agents.Get(near).Health < nearHealth - 0.01f)
            {
                firstOnNear = tick / (float)TicksPerSecond;
            }

            if (!world.Agents.Contains(near) && !world.Agents.Contains(far)) break;
        }

        var onNear = world.Agents.Contains(near)
            ? nearHealth - world.Agents.Get(near).Health
            : nearHealth;
        var onFar = world.Agents.Contains(far) ? farHealth - world.Agents.Get(far).Health : farHealth;
        var closed = gapBefore - MeanGapTo(world, mob, near);

        Console.WriteLine(
            $"    {ground,-7} | {count,2} | {onNear,15:F0} | " +
            $"{(firstOnNear > 0f ? $"{firstOnNear,4:F1} s" : "never"),6} | " +
            $"{onFar,24:F0} | {closed,22:F1} m");
    }

    private static float MeanGapTo(SimulationWorld world, List<AgentId> mob, AgentId target)
    {
        if (!world.Agents.Contains(target)) return 0f;
        var at = world.Agents.Get(target).Position;
        var total = 0f;
        var live = 0;
        foreach (var id in mob)
        {
            if (!world.Agents.Contains(id) || !world.Agents.Get(id).IsAlive) continue;
            total += Vector2.Distance(world.Agents.Get(id).Position, at);
            live++;
        }

        return live == 0 ? 0f : total / live;
    }

    /// <summary>
    /// N ordered at a hostile wall, with and without somebody defending it.
    /// </summary>
    /// <remarks>
    /// <b>Reported from the chair: "they just won't attack buildings correctly".</b> There is a mechanism
    /// worth suspecting before anything else, visible in <c>SimulationWorld.BreakStructures</c>: a body only
    /// damages a structure while <c>JobSystem.IsWorking</c> is true of it, and that requires the body
    /// <em>not to be interrupted</em>. A defender appearing beside the wall makes §30 commit our attacker,
    /// which is an interrupt — so the moment anybody defends the building, the assault may stop scratching
    /// it entirely while looking exactly as busy.
    /// <para>
    /// Hence the defender count is the axis, and <c>interrupted%</c> is printed beside the damage: if the
    /// two move together, that is the mechanism.
    /// </para>
    /// </remarks>
    private static void StructureAssault(int count, int defenders, Ground ground)
    {
        var world = BuildGround(ground, 240f, out var site);
        // <b>Off the settlement, not on it.</b> First run of this put the wall at the site centre — which
        // on the Village ground is exactly where Populate has already stood a granary and houses — so the
        // palisade was spawned inside other buildings and nobody could reach it. It reported "never
        // scratched, zero working ticks", which reads as a damning finding about attacking structures and
        // was a fixture placing its target inside a wall. Sixty metres out is the neighbour's palisade
        // rather than one built on top of our own granary.
        var centre = site + new Vector2(60f, 0f);
        var wall = world.AddNode(
            NodeKind.PalisadeWall, centre, capacity: 0, faction: Theirs, built: true);
        var before = world.Nodes.Get(wall).Condition;

        var mob = new List<AgentId>();
        for (var i = 0; i < count; i++)
        {
            mob.Add(world.SpawnAgent(
                centre + new Vector2(-16f, -2f + i * 1.1f), UnitType.Militia, Ours));
        }

        for (var i = 0; i < defenders; i++)
        {
            // <b>NOT Directed, unlike every other enemy in this file.</b> First run marked them Directed
            // for consistency and the defended arm then printed numbers identical to the undefended one to
            // the tick — because Directed means "somebody else drives this body", so the keepers stood
            // inertly and defended nothing. A defended wall has to have somebody who will actually come at
            // you, which is §30 deciding for itself.
            // <b>Between the assault and the wall.</b> At +4 m they were behind it, our bodies come from
            // -16 m, and the two arms printed identically to the tick because the keepers were never in
            // anybody's way. A defender that cannot be walked into is scenery.
            world.SpawnAgent(
                centre + new Vector2(-7f, -1.5f + i * 1.2f), UnitType.Militia, Theirs);
        }

        world.QueueAttack(mob, AgentId.None, wall);

        var firstHit = -1f;
        var workingTicks = 0;
        var interruptedTicks = 0;
        var samples = 0;
        var limit = 120 * TicksPerSecond;
        for (var tick = 1; tick <= limit; tick++)
        {
            world.Tick((float)SimulationWorld.FixedDeltaSeconds);
            if (!world.Nodes.Contains(wall)) break;
            if (firstHit < 0f && world.Nodes.Get(wall).Condition < before - 0.01f)
            {
                firstHit = tick / (float)TicksPerSecond;
            }

            var anyWorking = false;
            foreach (var id in mob)
            {
                if (!world.Agents.Contains(id) || !world.Agents.Get(id).IsAlive) continue;
                samples++;
                ref readonly var body = ref world.Agents.Get(id);
                if (body.Jobs.IsInterrupted) interruptedTicks++;
                if (JobSystem.IsWorking(in body)) anyWorking = true;
            }

            if (anyWorking) workingTicks++;
        }

        var lost = world.Nodes.Contains(wall) ? before - world.Nodes.Get(wall).Condition : before;
        Console.WriteLine(
            $"    {ground,-7} | {count,2} | {defenders,9} | " +
            $"{(firstHit > 0f ? $"{firstHit,7:F1} s" : "  never"),10} | " +
            $"{lost,8:F0} of {before,-4:F0} | {workingTicks,25} | " +
            $"{(samples == 0 ? 0f : 100f * interruptedTicks / samples),11:F0}%");
    }

    /// <summary>
    /// N bodies set on one that cannot run, which is the fight with the running taken out.
    /// </summary>
    /// <remarks>
    /// The quarry is given no orders and no speed, so nothing here depends on pursuit: what is left is
    /// purely how many can get at it and how continuously. <c>landed/N</c> is the concentration number and
    /// <c>contact%</c> is the duty cycle — a fight where six are present and three are ever touching is a
    /// different mechanism from one where six touch half the time, and the raid scenario could not tell
    /// them apart.
    /// </remarks>
    private static void Ring(int count)
    {
        var world = new SimulationWorld(120f);
        var victim = world.SpawnAgent(Vector2.Zero, UnitType.Raider, Theirs);
        // Directed and speedless: it is a post to hit, not an opponent, because this scenario is not about
        // whether it can get away.
        ref var quarry = ref world.Agents.Get(victim);
        quarry.Directed = true;
        quarry.MaximumSpeed = 0f;
        // <b>And carrying, which is what makes anybody attack it.</b> The first version of this fixture
        // measured zero contacts at every N, and the fixture was wrong rather than the game: question one
        // asks what I can see that is worth protecting, and an empty field with one thief standing in it
        // contains nothing. A thief with grain on its back is itself the thing worth protecting — §33 — so
        // this is the smallest world in which a defence has any reason to exist.
        quarry.Jobs.Carrying = Simulation.Economy.Resource.Grain;
        quarry.Jobs.CarriedUnits = 20;
        var health = quarry.Health;

        var ring = UnitType.Raider.Radius + UnitType.Villager.Radius + 0.4f;
        var mob = new List<AgentId>();
        for (var i = 0; i < count; i++)
        {
            var angle = i / (float)count * MathF.Tau;
            mob.Add(world.SpawnAgent(
                new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * ring,
                UnitType.Villager,
                Ours));
        }

        var ring_ours = mob.Select(id => id.Value).ToHashSet();
        var landed = new HashSet<int>();
        var contactTicks = 0;
        var mostAtOnce = 0;
        var contactSeconds = 0f;
        var killedAt = -1f;
        var limit = 60 * TicksPerSecond;
        for (var tick = 1; tick <= limit; tick++)
        {
            world.Tick((float)SimulationWorld.FixedDeltaSeconds);
            var struck = world.Threat.Contacts;
            if (struck > 0) contactTicks++;
            mostAtOnce = Math.Max(mostAtOnce, struck);
            contactSeconds += struck * (float)SimulationWorld.FixedDeltaSeconds;
            // Ours only — see the note in Assault. The victim fights back and was being counted as one
            // of the attackers, which is how this column came to read 5 of 4.
            foreach (var who in world.Threat.LandedThisTick)
            {
                if (ring_ours.Contains(who.Value)) landed.Add(who.Value);
            }
            if (!world.Agents.Contains(victim) || !world.Agents.Get(victim).IsAlive)
            {
                killedAt = tick / (float)TicksPerSecond;
                break;
            }
        }

        var elapsed = killedAt > 0f ? killedAt : limit / (float)TicksPerSecond;
        Console.WriteLine(
            $"    {count,2} | {(killedAt > 0f ? $"{killedAt,7:F1} s" : "   never"),9} | " +
            $"{100f * contactTicks / (elapsed * TicksPerSecond),7:F0}% | {landed.Count,4}/{count,-3} | " +
            $"{mostAtOnce,12} | {contactSeconds,17:F1}" +
            (killedAt > 0f ? string.Empty : $"  ({health - world.Agents.Get(victim).Health:F0} of {health:F0} done)"));
    }

    /// <summary>
    /// One body running from another, and the only question that matters: how much of it is spent in reach.
    /// </summary>
    /// <remarks>
    /// A chase that never closes and a chase that closes and loses contact every second are both "did not
    /// catch it", and they want completely different fixes. The quarry runs in a straight line rather than
    /// evading, because evasion is a separate mechanism and mixing them is how the raid scenario stopped
    /// being able to answer anything.
    /// </remarks>
    private static void Chase(float quarryPaceShare, string? label = null)
    {
        var world = new SimulationWorld(240f);
        var quarry = world.SpawnAgent(new Vector2(-40f, 0f), UnitType.Villager, Theirs);
        var hunter = world.SpawnAgent(new Vector2(-41.2f, 0f), UnitType.Villager, Ours);
        ref var runner = ref world.Agents.Get(quarry);
        runner.Directed = true;
        runner.MaximumSpeed = UnitType.Villager.MaximumSpeed * quarryPaceShare;
        // Away down the long axis, and far enough that it never arrives and stops.
        world.QueueMove(new[] { quarry }, new Vector2(100f, 0f));
        world.QueueChase(new[] { hunter }, quarry);

        var reach = MathF.Max(
            (UnitType.Villager.Radius + UnitType.Villager.Radius) *
            Simulation.Threat.ThreatSystem.ReachShare,
            AgentDefaults.ChaseStopMetres + Simulation.Threat.ThreatSystem.ContactSlack);
        var inReach = 0;
        var startGap = 1.2f;
        var caught = -1f;
        var limit = 60 * TicksPerSecond;
        var ticks = 0;
        for (var tick = 1; tick <= limit; tick++)
        {
            world.Tick((float)SimulationWorld.FixedDeltaSeconds);
            ticks = tick;
            if (!world.Agents.Contains(quarry) || !world.Agents.Get(quarry).IsAlive)
            {
                caught = tick / (float)TicksPerSecond;
                break;
            }

            var gap = Vector2.Distance(
                world.Agents.Get(hunter).Position, world.Agents.Get(quarry).Position);
            if (gap <= reach) inReach++;
        }

        var endGap = world.Agents.Contains(quarry) && world.Agents.Get(quarry).IsAlive
            ? Vector2.Distance(world.Agents.Get(hunter).Position, world.Agents.Get(quarry).Position)
            : 0f;
        var seconds = ticks / (float)TicksPerSecond;
        // Positive closes, negative loses ground. The arithmetic prediction is the speed difference; what
        // this reports is what the layer actually delivered, and the two are not the same number.
        var closing = (startGap - endGap) / MathF.Max(0.01f, seconds);
        if (label is not null)
        {
            Console.WriteLine(
                $"    {label,7} | {endGap,13:F2} m | {100f * inReach / ticks,12:F0}% | " +
                $"{(caught > 0f ? $"{caught:F1} s" : "never"),24}");
            return;
        }

        Console.WriteLine(
            $"    {quarryPaceShare * UnitType.Villager.MaximumSpeed,7:F2} m/s | " +
            $"{(caught > 0f ? $"{caught,6:F1} s" : "  no"),8} | {100f * inReach / ticks,12:F0}% | " +
            $"{endGap,7:F2} m | {closing,+8:F3}");
    }

    /// <summary>
    /// A body walking through a wall of enemies, which is the reported bug as a number.
    /// </summary>
    /// <remarks>
    /// The cordon stands still and is given no orders, so this measures one thing: what the movement layer
    /// does when a body's route runs through bodies it is hostile to. A crossing that costs nothing is the
    /// bug; a crossing that costs seconds and a detour is a cordon doing its job.
    /// </remarks>
    private static void Cordon(int count)
    {
        var world = new SimulationWorld(120f);
        var runner = world.SpawnAgent(new Vector2(-12f, 0f), UnitType.Raider, Theirs);
        ref var body = ref world.Agents.Get(runner);
        body.Directed = true;
        // Health out of the way: this is about getting past, not about surviving.
        body.Health = 1e6f;

        // A line across the path, a body's width apart, so there is no clean gap to walk through.
        var span = UnitType.Villager.Radius * 2f * 1.05f;
        for (var i = 0; i < count; i++)
        {
            var offset = (i - (count - 1) * 0.5f) * span;
            world.SpawnAgent(new Vector2(0f, offset), UnitType.Villager, Ours);
        }

        world.QueueMove(new[] { runner }, new Vector2(12f, 0f));
        var walked = 0f;
        var slowTicks = 0;
        var halfPace = UnitType.Raider.MaximumSpeed * 0.5f;
        var previous = world.Agents.Get(runner).Position;
        var crossedAt = -1f;
        var limit = 60 * TicksPerSecond;
        var ticks = 0;
        for (var tick = 1; tick <= limit; tick++)
        {
            world.Tick((float)SimulationWorld.FixedDeltaSeconds);
            ticks = tick;
            ref readonly var it = ref world.Agents.Get(runner);
            walked += Vector2.Distance(it.Position, previous);
            previous = it.Position;
            if (it.Velocity.Length() < halfPace) slowTicks++;
            if (it.Position.X > 4f)
            {
                crossedAt = tick / (float)TicksPerSecond;
                break;
            }
        }

        // Detour: how much further it walked than the straight line it was given.
        var straight = 16f;
        Console.WriteLine(
            $"    {count,2} | {(crossedAt > 0f ? "yes" : " no"),7} | " +
            $"{(crossedAt > 0f ? crossedAt : ticks / (float)TicksPerSecond),7:F1} | " +
            $"{walked / straight,6:F2}x | {slowTicks / (float)TicksPerSecond,6:F1} s");
    }
}

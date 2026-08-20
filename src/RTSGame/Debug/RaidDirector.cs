using System.Numerics;
using Blix.Diagnostics;
using RTSGame.Simulation;
using RTSGame.Simulation.Agents;
using RTSGame.Simulation.Collision;
using RTSGame.Simulation.Economy;

namespace RTSGame.Debug;

/// <summary>How often something comes over the hill, and how much of it. A scenario knob.</summary>
/// <remarks>
/// <b>On a slider because it is scaffolding, not a rule.</b> §28: in the real game the thing over the hill
/// is another player, and nothing in the economy may come to depend on raids arriving on a curve. The
/// pressure schedule is therefore scenario configuration — turn it off and the settlement still works —
/// and it is turned well up by default because these sessions are for exploring the shape of the game
/// rather than converging on a figure.
/// </remarks>
internal sealed class RaidSettings
{
    [Tune(Label = "raids on", Group = "raids")]
    public bool Enabled = true;

    /// <summary>Sim seconds between raids.</summary>
    /// <remarks>
    /// A season is 1,000–1,800 s, so 240 is several raids a season — far more than the design would want
    /// and exactly what you want while finding out whether defence is interesting at all. The prototype's
    /// cooldown argument (long enough that recovery windows are real) applies to a shipped game, not to a
    /// test bench.
    /// </remarks>
    [Tune(30.0, 1800.0, Label = "seconds between raids", Group = "raids")]
    public float SecondsBetween = 240f;

    [Tune(1.0, 12.0, Label = "raiders per raid", Group = "raids")]
    public int Party = 3;

    /// <summary>Seconds a raider spends inside a granary filling its hands with grain.</summary>
    /// <remarks>
    /// Grain is the slow case and therefore the one the dial is written in: it is stored loose or in sacks
    /// and a thief has to fill something before it can carry any. See <see cref="WoodLootShare"/> for the
    /// other end of that — the two together are what make a granary a different kind of target from a
    /// timber yard rather than the same target with a different label.
    /// </remarks>
    [Tune(1.0, 30.0, Label = "seconds looting grain", Group = "raids")]
    public float LootSeconds = 6f;

    /// <summary>
    /// How long taking timber takes, as a share of taking grain.
    /// </summary>
    /// <remarks>
    /// Much shorter, because logs are stacked and you carry them: a thief in a timber yard picks up what it
    /// can hold and leaves, where one in a granary has to fill a sack. Which gives the two kinds of store
    /// genuinely different defensive problems — a lumber camp is robbed before anybody can gather, and a
    /// granary gives you a window. That is worth having as a real asymmetry rather than as flavour, and it
    /// costs one number.
    /// </remarks>
    [Tune(0.1, 1.0, Label = "timber loots this much faster", Group = "raids")]
    public float WoodLootShare = 0.4f;

    /// <summary>Seconds inside a store, for what is being taken out of it.</summary>
    public float RummageSecondsFor(Resource resource) =>
        resource == Resource.Wood
            ? LootSeconds * Math.Clamp(WoodLootShare, 0.05f, 1f)
            : LootSeconds;

    /// <summary>How far out a raid appears, in metres.</summary>
    /// <remarks>
    /// <b>Not the edge of the map, which is what it was and which was a mistake worth recording.</b> A raid
    /// spawning 282 m out takes two and a half minutes to arrive, so the panel announced "3 RAIDERS" and
    /// then nothing visible happened for minutes — the count was true and the feedback was a lie. A hundred
    /// and twenty metres is beyond a settlement's sight and beyond the wood, so a raid still has to be
    /// noticed arriving, and it arrives inside a minute.
    /// </remarks>
    [Tune(40.0, 290.0, Label = "raid appears at (m)", Group = "raids")]
    public float ArrivesAt = 120f;

    /// <summary>
    /// A loaded raider's pace, as a share of an empty one's.
    /// </summary>
    /// <remarks>
    /// <b>Below a villager's, and that is the whole of §7's argument for interception.</b> A raider walks
    /// in at 2.05 m/s against a villager's 1.79, which is right — you cannot catch a raid on its way in,
    /// and you should not be able to. It walked <em>out</em> at 2.05 too, which meant a defence could never
    /// catch it either: measured over eight raids, twenty-four raiders and not one of them died. The
    /// villagers formed a column and followed it to the map edge, which is what "they don't actually die
    /// even surrounded" looks like from above.
    /// <para>
    /// At 0.7 a loaded raider makes 1.44 m/s against an unloaded villager's 1.79, so the walk home is a
    /// window that closes on it. Killing it returns the grain rather than denying it, which is why the loot
    /// is worth chasing at all.
    /// </para>
    /// </remarks>
    [Tune(0.3, 1.0, Label = "loaded raider pace", Group = "raids")]
    public float LadenShare = 0.7f;

    /// <summary>Whether the camera snaps to a raid when it appears.</summary>
    /// <remarks>
    /// A test-bench convenience and no more: watching whether defence is interesting requires being able to
    /// see the fight, and hunting for it across six hundred metres is not what these sessions are for.
    /// </remarks>
    [Tune(Label = "camera jumps to raids", Group = "raids")]
    public bool CameraJumps = true;
}

/// <summary>
/// A scripted adversary: walk in at an edge, take what you can carry, run for the nearest edge.
/// </summary>
/// <remarks>
/// <b>Scaffolding, and it lives here for that reason.</b> This is a fixture of the same kind as the
/// pen-escape crowd — something that exercises the world so a single-player session has a reason to pay
/// attention. The defence it provokes is a real mechanic and lives in the simulation, written against
/// hostility rather than against thieves, so that deleting this file leaves a working settlement and a
/// defence layer with nothing to do. That is the test of whether the line held.
/// <para>
/// Slower loaded than empty, which is the whole of §7's argument for interception: killing a loaded raider
/// <em>returns</em> the grain rather than denying it, and the return trip is the defender's window precisely
/// because the loot is recoverable at the end of it. A raider that got away with it has taken the grain out
/// of the world, so the ledger counts it as consumed — it is gone, and pretending otherwise would be a
/// conservation hole rather than a kindness.
/// </para>
/// </remarks>
internal sealed class RaidDirector
{
    private readonly RaidSettings settings;
    private readonly List<Raider> party = new();
    private readonly FactionId enemy = new(1);

    /// <summary>How far past a store's wall a raider can still get in at it, in metres.</summary>
    /// <remarks>
    /// One number for both getting there and being there. Two definitions of "at the granary" is how a
    /// raider came to be able to loot from outside one.
    /// </remarks>
    private const float LootReach = 1.2f;
    private float untilNext;
    private uint seed = 0x1B873593u;

    public RaidDirector(RaidSettings settings)
    {
        this.settings = settings;
        untilNext = settings.SecondsBetween * 0.4f;
    }

    /// <summary>Raids started, raiders killed, and what has been carried off the map.</summary>
    public int Raids { get; private set; }

    public int Escaped { get; private set; }

    public int Stolen { get; private set; }

    public int Alive => party.Count;

    /// <summary>Somewhere worth looking, once, when a raid appears.</summary>
    public Vector2? LookAt { get; private set; }

    /// <summary>Takes the look-here request, so it fires once rather than every frame.</summary>
    public Vector2? TakeLookAt()
    {
        var at = LookAt;
        LookAt = null;
        return at;
    }

    /// <summary>How far the nearest raider still is from the settlement, for the panel.</summary>
    private float nearest;

    /// <summary>
    /// What the raid is doing, for the panel.
    /// </summary>
    /// <remarks>
    /// <b>With the distance in it</b>, because "3 RAIDERS" on its own is the feedback bug rather than the
    /// feedback: a raid that has appeared two hundred metres away and a raid that is at the granary door are
    /// the same sentence and want entirely different reactions.
    /// </remarks>
    public string Status
    {
        get
        {
            if (party.Count == 0)
            {
                return settings.Enabled
                    ? $"quiet — next raid in {MathF.Max(0f, untilNext):F0}s"
                    : "raids off";
            }

            // Inside is worth its own word. A raider in the granary is not visible on the map, so the
            // panel is the only thing that can say why the villagers are standing around a building.
            var inside = CountBy(RaiderMood.Looting);
            var where = inside > 0
                ? $"{inside} INSIDE YOUR STORES"
                : nearest <= 14f
                    ? "AT YOUR STORES"
                    : $"{nearest:F0} m OFF";
            return $"{party.Count} RAIDERS · {where} · {CountBy(RaiderMood.Escaping)} getting away";
        }
    }

    private enum RaiderMood
    {
        Approaching,
        Looting,
        Escaping,
    }

    private sealed class Raider
    {
        public AgentId Body;
        public RaiderMood Mood;
        public NodeId Target;
        public Vector2 Exit;
        public float Timer;

        /// <summary>Seconds left to reach the store before giving up and going home.</summary>
        public float Patience;

        /// <summary>Where it went in, which is where it comes back out.</summary>
        public Vector2 Doorway;

        /// <summary>What it went in for, decided on the way in because that is what sets the time.</summary>
        public Resource Wanted;
    }

    /// <summary>
    /// Runs the raid: spawn on schedule, and move each raider through approach, loot and escape.
    /// </summary>
    /// <remarks>
    /// Driven off simulation seconds rather than frames, so a raid arrives at the same moment whatever the
    /// time compression is set to and a headless run and a watched one see the same thing.
    /// </remarks>
    public void Update(SimulationWorld world, float deltaSeconds)
    {
        Sweep(world);
        nearest = float.PositiveInfinity;
        foreach (var raider in party)
        {
            if (!world.Agents.Contains(raider.Body)) continue;
            nearest = MathF.Min(nearest, world.Agents.Get(raider.Body).Position.Length());
        }

        if (settings.Enabled)
        {
            untilNext -= deltaSeconds;
            if (untilNext <= 0f)
            {
                untilNext = settings.SecondsBetween;
                Launch(world);
            }
        }

        for (var i = party.Count - 1; i >= 0; i--)
        {
            var raider = party[i];
            if (!world.Agents.Contains(raider.Body)) continue;
            switch (raider.Mood)
            {
                case RaiderMood.Approaching:
                    Approach(world, raider, deltaSeconds);
                    break;
                case RaiderMood.Looting:
                    Loot(world, raider, deltaSeconds);
                    break;
                default:
                    Escape(world, raider, i);
                    break;
            }
        }
    }

    /// <summary>Forgets raiders the world has already removed — killed, mostly.</summary>
    private void Sweep(SimulationWorld world)
    {
        for (var i = party.Count - 1; i >= 0; i--)
        {
            if (!world.Agents.Contains(party[i].Body)) party.RemoveAt(i);
        }
    }

    private void Launch(SimulationWorld world)
    {
        // <b>Down a ride, not out of the middle of a wood.</b> Measured by watching: a bearing picked at
        // random put raiders inside the impassable interior of a stand, where the spawn nudge could not
        // find open ground within its search and they stood there for the rest of the session. The rides
        // are the lanes the woodland was carved with precisely so that something could come down them, so
        // a raid uses one — which is also the better mechanic, because it means a settlement's approaches
        // are knowable and watching them is worth doing.
        var ride = (int)(Next() * MathF.Max(1, Woodland.Rides));
        var angle = Woodland.Rides > 0
            ? ride / (float)Woodland.Rides * MathF.Tau
            : Next() * MathF.Tau;
        var edge = MathF.Min(settings.ArrivesAt, world.ExtentMeters * 0.47f);
        var arrival = new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * edge;
        var started = 0;
        for (var i = 0; i < settings.Party; i++)
        {
            // Scattered along the ride rather than across it, so the party stays in the lane: the rides are
            // ten metres wide and a three-metre sideways scatter puts somebody in the trees.
            var along = Vector2.Normalize(arrival);
            var at = arrival - along * (i * 2.2f) +
                     new Vector2(-along.Y, along.X) * (Next() * 4f - 2f);
            var body = world.SpawnAgent(world.Terrain.ClampPosition(at), UnitType.Raider, enemy);
            // This director owns where these bodies go. Without it they also ran the settlement's own
            // defence — see AgentState.Directed — and marched themselves off after loot they were carrying.
            world.Agents.Get(body).Directed = true;
            party.Add(new Raider
            {
                Body = body,
                Mood = RaiderMood.Approaching,
                Exit = arrival,
                // Generous, but finite: about the time it takes to walk in from the far edge at raider pace,
                // so a raid that cannot find its way home rather than standing on the map forever.
                Patience = world.ExtentMeters / UnitType.Raider.MaximumSpeed,
            });
            started++;
        }

        if (started == 0) return;
        Raids++;
        if (settings.CameraJumps) LookAt = arrival;
        Console.WriteLine(
            $"  RAID {Raids}: {started} raiders {edge:F0} m out at " +
            $"({arrival.X:F0}, {arrival.Y:F0}) — {world.Date}");
    }

    /// <summary>
    /// Walks at the nearest store that has anything in it, and goes home if it cannot get there.
    /// </summary>
    /// <remarks>
    /// <b>Re-issues the walk and gives up eventually</b>, both of which were missing and both of which
    /// mattered. A raider that was ordered once and then stopped short — a route that failed, a wood that
    /// closed, a crowd in the way — stood where it halted for the rest of the session, and the party
    /// accumulated: measured, fifty-six raiders standing about on the map at once. Anything scripted to walk
    /// somewhere needs to notice that it is not walking, and needs a limit on how long it will keep trying.
    /// </remarks>
    private void Approach(SimulationWorld world, Raider raider, float deltaSeconds)
    {
        ref readonly var body = ref world.Agents.Get(raider.Body);
        raider.Patience -= deltaSeconds;
        if (raider.Patience <= 0f)
        {
            raider.Mood = RaiderMood.Escaping;
            return;
        }

        var retarget = !world.Nodes.Contains(raider.Target) ||
                       world.Nodes.Get(raider.Target).Stock.Total <= 0;
        if (retarget)
        {
            raider.Target = RichestStore(world, body.Position);
            if (!world.Nodes.Contains(raider.Target))
            {
                // Nothing worth taking. Go home rather than mill about, which is also what makes a
                // settlement that has been stripped stop being raided.
                raider.Mood = RaiderMood.Escaping;
                return;
            }
        }

        ref readonly var store = ref world.Nodes.Get(raider.Target);
        var reach = store.FootprintRadius + body.Radius + LootReach;
        if (Vector2.DistanceSquared(body.Position, store.Position) <= reach * reach)
        {
            // <b>In it goes.</b> Looting happens inside the building: it cannot be shoved off the door by
            // the crowd that came to stop it, and it cannot be fought in there either. So the window runs
            // to completion and the fight is about what comes out — which is a far better shape than a
            // shoving match at the doorway whose outcome depended on crowd physics.
            // <b>Decided on the way in, because what it is after is what decides how long it takes.</b>
            // A thief filling a sack with grain is in there for a while; one picking up logs is not.
            raider.Wanted = store.Stock.Grain >= store.Stock.Wood ? Resource.Grain : Resource.Wood;
            raider.Mood = RaiderMood.Looting;
            raider.Timer = settings.RummageSecondsFor(raider.Wanted);
            raider.Doorway = body.Position;
            world.QueueStop(new[] { raider.Body });
            world.EnterShelter(raider.Body);
            return;
        }

        // Ask again whenever it is not on its way, rather than only when the target changes.
        if (retarget || !body.HasDestination)
        {
            world.QueueMove(new[] { raider.Body }, store.Position);
        }
    }

    /// <summary>
    /// Rummages inside for a while, then pops out with everything it can carry and runs.
    /// </summary>
    /// <remarks>
    /// <b>The looting happens indoors, which fixes two things at once.</b> It was a stationary body at the
    /// door with a countdown, and the countdown was the only part that was checked — so a raider shoved out
    /// of the yard by the crowd went on emptying the granary from wherever it had been pushed to. Reported
    /// as looting "without even being near the granary", which is exactly what it was.
    /// <para>
    /// Putting it inside is better than re-checking the distance, which was the first fix and made shoving
    /// a raider off a doorway into an accidental defence mechanic decided by crowd physics. Now the window
    /// runs to completion and the interesting moment is the one after it: the thing comes out loaded and
    /// slow, and the people who gathered while it was in there get their chance.
    /// </para>
    /// <para>
    /// The grain is taken on the way <em>out</em> rather than on the way in, which is not a detail — while
    /// it is in there the units are still in the granary, so the ledger needs no term for goods in
    /// somebody's pockets inside a building, and a raid interrupted by the store being destroyed cannot
    /// vanish anything.
    /// </para>
    /// </remarks>
    private void Loot(SimulationWorld world, Raider raider, float deltaSeconds)
    {
        raider.Patience -= deltaSeconds;
        raider.Timer -= deltaSeconds;
        if (raider.Timer > 0f) return;

        // Out it comes, at the door it went in at.
        world.LeaveShelter(raider.Body, raider.Doorway);
        if (world.Nodes.Contains(raider.Target))
        {
            ref var store = ref world.Nodes.Get(raider.Target);
            ref var body = ref world.Agents.Get(raider.Body);
            // What it went in for, not whatever is most plentiful now — a granary that was emptied while it
            // rummaged sends it out with nothing, which is a defence working rather than a case to paper
            // over.
            var resource = raider.Wanted;
            var taken = Math.Min(body.CarryCapacity - body.Jobs.CarriedUnits, store.Stock[resource]);
            if (taken > 0)
            {
                store.Stock.Add(resource, -taken);
                body.Jobs.Carrying = resource;
                body.Jobs.CarriedUnits += taken;
            }
        }

        Laden(world, raider);
        raider.Mood = RaiderMood.Escaping;
        world.QueueMove(new[] { raider.Body }, raider.Exit);
    }

    /// <summary>Sets a raider's pace to match what it is carrying.</summary>
    /// <remarks>
    /// The one place the loaded pace is applied, so an empty raider that dropped its load — killed and
    /// revived it cannot be, but a store that had nothing in it leaves it empty-handed — walks home at
    /// full speed rather than limping for no reason.
    /// </remarks>
    private void Laden(SimulationWorld world, Raider raider)
    {
        if (!world.Agents.Contains(raider.Body)) return;
        ref var body = ref world.Agents.Get(raider.Body);
        var share = body.Jobs.CarriedUnits > 0 ? Math.Clamp(settings.LadenShare, 0.1f, 1f) : 1f;
        body.MaximumSpeed = UnitType.Raider.MaximumSpeed * share;
    }

    /// <summary>Runs for the edge it came in at, and is gone when it gets there.</summary>
    private void Escape(SimulationWorld world, Raider raider, int index)
    {
        ref readonly var body = ref world.Agents.Get(raider.Body);
        if (Vector2.DistanceSquared(body.Position, raider.Exit) > 36f)
        {
            if (!body.HasDestination) world.QueueMove(new[] { raider.Body }, raider.Exit);
            return;
        }

        // Off the map with it. Whatever it is carrying has left the world, so the ledger says consumed —
        // the grain is gone, and giving it a term of its own would be a hole in the identity dressed up as
        // bookkeeping.
        var carried = body.Jobs.CarriedUnits;
        if (carried > 0)
        {
            Stolen += carried;
            world.TakeOutOfTheWorld(raider.Body);
        }

        Escaped++;
        world.DespawnAgents(new[] { raider.Body });
        party.RemoveAt(index);
    }

    private int CountBy(RaiderMood mood)
    {
        var count = 0;
        foreach (var raider in party)
        {
            if (raider.Mood == mood) count++;
        }

        return count;
    }

    private static NodeId RichestStore(SimulationWorld world, Vector2 from)
    {
        var best = NodeId.None;
        var bestScore = 0f;
        foreach (ref readonly var node in world.Nodes.All)
        {
            if (!node.IsAlive || !node.Stores || node.Stock.Total <= 0) continue;
            // Fullest, discounted by how far it is: a raid goes for the biggest prize it can reach, which
            // is what makes a remote depot a liability and a central granary a target.
            var score = node.Stock.Total / MathF.Max(20f, Vector2.Distance(node.Position, from));
            if (score <= bestScore) continue;
            bestScore = score;
            best = node.Id;
        }

        return best;
    }

    /// <summary>splitmix32, so a raid arrives in the same place in two runs of the same world.</summary>
    private float Next()
    {
        seed += 0x9E3779B9u;
        var z = seed;
        z = (z ^ (z >> 16)) * 0x21F0AAADu;
        z = (z ^ (z >> 15)) * 0x735A2D97u;
        z ^= z >> 15;
        return (z & 0xFFFFFFu) / (float)0x1000000u;
    }
}

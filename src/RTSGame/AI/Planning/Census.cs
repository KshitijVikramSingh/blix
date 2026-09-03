using System.Collections.Generic;
using System.Numerics;
using RTSGame.Simulation;
using RTSGame.Simulation.Agents;
using RTSGame.Simulation.Collision;
using RTSGame.Simulation.Economy;
using RTSGame.Simulation.Jobs;

namespace RTSGame.AI.Planning;

/// <summary>
/// Everything one faction can see about itself, read once per decision.
/// </summary>
/// <remarks>
/// <b>One pass, so that every reading is a field lookup and no rule can pay for a sweep.</b> §141. The
/// alternative — each reading walking the node and body stores for itself — makes the cost of a plan
/// proportional to how many conditions it has, which would mean the honest way to write a rule was to write
/// as few as possible. A vocabulary nobody can afford to use is not a vocabulary.
/// <para>
/// It holds only what a player can see on their own screen: this faction's nodes, this faction's bodies, and
/// what §131's knowledge says about ground it has reached. Never another faction's stores.
/// </para>
/// </remarks>
internal sealed class Census
{
    public FactionId Faction { get; private set; }

    public NodeId Store { get; private set; }

    public Vector2 Centre { get; private set; }

    /// <summary>Fields, and the hands standing on each, in the same order.</summary>
    public List<(NodeId Id, Vector2 At, float Extent)> Fields { get; } = new();

    public int[] FieldHands { get; private set; } = System.Array.Empty<int>();

    /// <summary>Anything wanting labour: going up for the first time, or turning to stone.</summary>
    public List<(NodeId Id, Vector2 At, float Extent)> Projects { get; } = new();

    public NodeId Barracks { get; private set; }

    public bool HasBarracksSite { get; private set; }

    /// <summary>Sound palisades, which are what an upgrade can begin on.</summary>
    public List<NodeId> SoundPalisades { get; } = new();

    public int Walls { get; private set; }

    public int Stone { get; private set; }

    public int Timber { get; private set; }

    public int Mouths { get; private set; }

    /// <summary>Bodies that can be given work: not carts, not militia, not under a player's order.</summary>
    public int Workforce { get; private set; }

    public int Militia { get; private set; }

    /// <summary>
    /// Hands the economy actually has: idle, on grain, on wood. Not builders, haulers or trainees.
    /// </summary>
    /// <remarks>
    /// <b>A share has to be a share of what the sharing rule can reach, and taking it of the whole workforce
    /// made two rules fight over the same bodies all year.</b> §141: with five of nine hands committed to a
    /// building site, "two thirds on grain" targeted seven of nine and the gap could never close — so the
    /// employment rule pulled builders back to the fields twice a second while the staffing rule pulled them
    /// back to the site. Measured: 499 orders in 540 decisions, against about forty from the cascade this
    /// layer replaced.
    /// <para>
    /// Builders and trainees are committed elsewhere by rules that outrank the economy, and the pool shrinking
    /// is the mechanism by which that priority is expressed. A target computed over hands somebody else has
    /// already claimed is a target that is not about this settlement's economy at all.
    /// </para>
    /// </remarks>
    public int EconomyHands => OnGrain + OnWood + Idle.Count;

    public int OnGrain { get; private set; }

    public int OnWood { get; private set; }

    public float GrainSeasons { get; private set; }

    public float WoodSeasons { get; private set; }

    /// <summary>Idle hands, then hands that can be taken off grain. The order the pool draws in.</summary>
    public List<AgentId> Idle { get; } = new();

    public List<AgentId> OnGrainBodies { get; } = new();

    public List<AgentId> OnWoodBodies { get; } = new();

    /// <summary>Hands standing at one site, by node slot.</summary>
    public Dictionary<int, int> HandsAtSite { get; } = new();

    public void Take(SimulationWorld world, FactionId faction)
    {
        Faction = faction;
        Store = NodeId.None;
        Barracks = NodeId.None;
        HasBarracksSite = false;
        Walls = Stone = Timber = Mouths = Workforce = Militia = OnGrain = OnWood = 0;
        Fields.Clear();
        Projects.Clear();
        SoundPalisades.Clear();
        Idle.Clear();
        OnGrainBodies.Clear();
        OnWoodBodies.Clear();
        HandsAtSite.Clear();

        foreach (var id in world.Nodes.SettlementNodes)
        {
            ref readonly var node = ref world.Nodes.Get(id);
            if (!node.IsAlive || node.Faction != faction) continue;
            if (node.Stores)
            {
                if (!Store.IsValid) Store = node.Id;
                Stone += node.Stock[Resource.Stone];
                Timber += node.Stock[Resource.Wood];
            }

            if (node.Kind == NodeKind.Farm) Fields.Add((node.Id, node.Position, node.HalfExtent));
            if (node.Kind == NodeKind.Barracks)
            {
                if (node.IsBuilt) Barracks = node.Id;
                else HasBarracksSite = true;
            }

            if (node.Kind is NodeKind.PalisadeWall or NodeKind.StoneWall) Walls++;
            if (node.Kind == NodeKind.PalisadeWall && node.IsBuilt && !node.HasStructuralProject &&
                node.Condition >= node.MaxCondition - 0.0001f)
            {
                SoundPalisades.Add(node.Id);
            }

            if (node.IsUnderConstruction || node.HasStructuralProject)
            {
                Projects.Add((node.Id, node.Position, node.HalfExtent));
            }
        }

        if (Store.IsValid) Centre = world.Nodes.Get(Store).Position;
        FieldHands = new int[Fields.Count];

        foreach (ref readonly var body in world.Agents.All)
        {
            if (!body.IsAlive || body.Faction != faction) continue;
            Mouths++;
            if (body.Role == AgentRole.Militia)
            {
                Militia++;
                continue;
            }

            // A cart is not a spare pair of hands. §135: the hauling board finds carts by their being idle,
            // so employing one takes it off the board for good and the crop sits in the farm yards.
            if (body.HasCart) continue;

            // A body under a player's order is not the bot's to move. §7 makes a manual order an interrupt
            // that expires, and --handsoff means a person can reach into a bot-run settlement.
            if (body.Jobs.IsInterrupted) continue;
            Workforce++;

            var job = body.Jobs.Assignment;
            if (!body.Jobs.HasAssignment)
            {
                Idle.Add(body.Id);
                continue;
            }

            switch (job.Kind)
            {
                case AssignmentKind.Work when job.Cargo == Resource.Grain:
                    OnGrain++;
                    OnGrainBodies.Add(body.Id);
                    for (var i = 0; i < Fields.Count; i++)
                    {
                        if (job.Source != Fields[i].Id) continue;
                        FieldHands[i]++;
                        break;
                    }

                    break;
                case AssignmentKind.Work when job.Cargo == Resource.Wood:
                    OnWood++;
                    OnWoodBodies.Add(body.Id);
                    break;
                case AssignmentKind.Build:
                    Count(job.Source.Value);
                    break;
                case AssignmentKind.Hold:
                    // Posted on a site and not yet converted by PostedOnAWorkSite. Counted as standing there,
                    // because the conversion happens on the body's next tick and the difference is invisible
                    // to a decision half a second later.
                    foreach (var (site, at, _) in Projects)
                    {
                        if (Vector2.DistanceSquared(job.Anchor, at) < 4f) Count(site.Value);
                    }

                    break;
            }
        }

        GrainSeasons = world.Economy.Outlook(
            Resource.Grain, world.Nodes, world.Agents, world.Date.Season, faction).Seasons;
        WoodSeasons = world.Economy.Outlook(
            Resource.Wood, world.Nodes, world.Agents, world.Date.Season, faction).Seasons;

        void Count(int slot) =>
            HandsAtSite[slot] = HandsAtSite.TryGetValue(slot, out var standing) ? standing + 1 : 1;
    }

    public int StandingAt(NodeId site) => HandsAtSite.TryGetValue(site.Value, out var n) ? n : 0;
}

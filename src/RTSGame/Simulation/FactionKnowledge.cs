using System.Numerics;
using RTSGame.Simulation.Collision;
using RTSGame.Simulation.Economy;

namespace RTSGame.Simulation;

/// <summary>
/// What each faction can see now and what it remembers seeing, as simulation state.
/// </summary>
/// <remarks>
/// <b>Not the fog of war, and §119 wrote down why the difference matters.</b> The renderer's
/// <see cref="Rendering.FogOfWar"/> holds two masks recording what the <em>player</em> has scouted and is
/// watching; it is view state, and the rule is a direction rather than a location — <b>fog may read the
/// simulation, the simulation may never read fog.</b> The moment a decision reads those masks, "where the
/// camera has been" becomes part of what the world does, and two runs that differ only in where somebody
/// looked would diverge.
/// <para>
/// This is the other thing, which that note explicitly reserved: <em>a per-faction knowledge aggregate — which
/// cells a faction can currently see, as read by an AI opponent or by a defence deciding whether it is needed
/// — is simulation state and belongs in Carried with a census entry.</em> So it is fingerprinted, it is saved,
/// and it is read by decisions. Conflating the two in either direction is the mistake the note exists to
/// prevent.
/// </para>
/// <para>
/// <b>Two questions, one grid.</b> Whether a faction can see a place right now is recomputed every tick from
/// its bodies and its buildings; what it remembers is the tick each cell was last seen on, which accumulates
/// and therefore has to be carried. One int per cell per faction: zero means never seen, and any other value
/// is both "known" and "how stale". That single number answers "do I know about this" and "should I go and
/// look again" without a second structure or a decay pass.
/// </para>
/// <para>
/// Ten-metre cells, matching the fog's, because the two are answering the same question at the same
/// resolution and a knowledge grid finer than the thing that draws it would be precision nobody can see.
/// </para></remarks>
internal sealed class FactionKnowledge
{
    /// <summary>Side of one knowledge cell. The fog's own resolution; see the class note.</summary>
    public const float CellMetres = 10f;

    /// <summary>
    /// Factions the world keeps knowledge for.
    /// </summary>
    /// <remarks>
    /// Fixed rather than grown on demand, because the array has to be fingerprinted and saved and a structure
    /// whose size depends on the order factions were first mentioned is a structure two peers can disagree
    /// about. Four is more than the game has any use for and costs a few kilobytes.
    /// </remarks>
    public const int Factions = 4;

    private readonly int cells;
    private readonly float extentMetres;

    /// <summary>Tick each cell was last seen on, per faction. Zero is never. Carried; see the class note.</summary>
    private readonly long[][] lastSeen;

    public FactionKnowledge(float extentMetres)
    {
        this.extentMetres = extentMetres;
        cells = Math.Max(2, (int)MathF.Ceiling(extentMetres / CellMetres) + 1);
        lastSeen = new long[Factions][];
        for (var faction = 0; faction < Factions; faction++)
        {
            lastSeen[faction] = new long[cells * cells];
        }
    }

    /// <summary>Cells across, so a report can say how much ground this covers.</summary>
    public int Cells => cells;

    /// <summary>
    /// Marks the ground within reach of this faction's bodies and buildings as known on this tick.
    /// </summary>
    /// <remarks>
    /// <b>Reach, not line of sight, and that is a decision rather than an omission.</b> The first version
    /// asked <see cref="SimulationWorld.CanSee"/> for every cell of every watcher's radius, which is the one
    /// authority on what <em>seeing</em> means — and it cost <b>166 milliseconds a tick</b> in a settlement,
    /// because that predicate marches the navigation raster at a quarter of a metre and a granary's
    /// hundred-and-five-metre reach is four hundred samples per cell across five hundred cells. A year of that
    /// is seven hours. The phase timer added with it is what said so, on the first run.
    /// <para>
    /// Rather than invent a coarser ray — a second opinion about seeing is exactly what
    /// <c>CanSee</c>'s own comment warns against — the question changed to the one this aggregate is actually
    /// for. <b>Knowing is not seeing.</b> A settlement knows the lie of its own land whether or not a ridge
    /// stands between the granary and the far side of a field, and a bot asking "do I know what is over there"
    /// wants the ground it has had people on, not the ground currently in line of sight. The fog keeps
    /// occlusion because it draws what a player can see; this keeps reach because it records what a faction
    /// has learned.
    /// </para>
    /// <para>
    /// So the two answer different questions and are allowed to differ, which is the opposite of the failure
    /// §119 warns about — that was about a decision reading the <em>view</em>. Nothing here reads the fog and
    /// the fog reads nothing here.
    /// </para></remarks>
    /// <summary>Off switches the whole pass, so its cost can be told from everything else's.</summary>
    internal static bool Enabled = true;

    /// <summary>
    /// Watchers refreshed per tick, as a share of all of them.
    /// </summary>
    /// <remarks>
    /// <b>Because a whole pass every tick was a sevenfold increase in the simulation's own cost.</b> Measured:
    /// 1.3 ms a tick in a settlement whose entire tick is about 0.2. Nothing needs it that often — cells are
    /// ten metres across and a body walks at 1.5 m/s, so it takes nearly seven seconds to change a single
    /// answer, and a building never changes one at all. A quarter of a second between refreshes is still an
    /// order of magnitude finer than anything that reads this can act on.
    /// </remarks>
    internal const int RefreshInterval = 15;

    public void Observe(SimulationWorld world, long tick)
    {
        if (!Enabled) return;

        // Staggered by index, so every watcher is refreshed once per interval and the cost per tick is flat
        // rather than a spike shared by all of them.
        var slot = (int)(tick % RefreshInterval);
        var index = 0;
        foreach (ref readonly var body in world.Agents.All)
        {
            if (!body.IsAlive) continue;
            if (index++ % RefreshInterval != slot) continue;
            Mark(body.Faction, body.Position, body.SightMetres, tick);
        }

        // The settlement index rather than every node, because the other sixty-nine thousand are trees and
        // outcrops and none of them watch anything. §82 built that index for exactly this shape of loop.
        foreach (var id in world.Nodes.SettlementNodes)
        {
            if (index++ % RefreshInterval != slot) continue;
            ref readonly var node = ref world.Nodes.Get(id);
            if (!node.IsAlive) continue;
            var reach = NodeWatch.MetresFor(node.Kind);
            if (reach <= 0f) continue;
            Mark(node.Faction, node.Position, reach, tick);
        }
    }

    private void Mark(FactionId faction, Vector2 from, float reach, long tick)
    {
        if (faction.Value < 0 || faction.Value >= Factions || reach <= 0f) return;
        var seen = lastSeen[faction.Value];
        var span = (int)MathF.Ceiling(reach / CellMetres);
        if (!TryCellOf(from, out var originX, out var originZ)) return;
        for (var dz = -span; dz <= span; dz++)
        for (var dx = -span; dx <= span; dx++)
        {
            var x = originX + dx;
            var z = originZ + dz;
            if (x < 0 || z < 0 || x >= cells || z >= cells) continue;
            // The cell's centre inside the watcher's reach. A cell whose centre is beyond it but whose corner
            // is not is left unknown, which errs toward a faction knowing less than it might — the safe
            // direction for anything a decision will be made on.
            if (Vector2.DistanceSquared(CentreOf(x, z), from) > reach * reach) continue;
            seen[z * cells + x] = tick;
        }
    }

    /// <summary>The tick this faction last saw this place, or zero if it never has.</summary>
    public long LastSeen(FactionId faction, Vector2 at)
    {
        if (faction.Value < 0 || faction.Value >= Factions) return 0;
        return TryCellOf(at, out var x, out var z) ? lastSeen[faction.Value][z * cells + x] : 0;
    }

    /// <summary>Whether this faction has ever seen this place.</summary>
    public bool Knows(FactionId faction, Vector2 at) => LastSeen(faction, at) > 0;

    /// <summary>
    /// Whether this faction has seen this place within the last <paramref name="window"/> ticks.
    /// </summary>
    /// <remarks>
    /// <b>A window rather than "this tick", because the refresh is staggered.</b> Watchers are re-marked once
    /// every <see cref="RefreshInterval"/> ticks, so a cell in plain view of a granary carries a stamp up to
    /// that many ticks old and an exact-tick test would call it unseen most of the time. The default window is
    /// the interval itself, which is the tightest question this structure can answer honestly.
    /// </remarks>
    public bool SeenWithin(FactionId faction, Vector2 at, long tick, int window = RefreshInterval)
    {
        var seen = LastSeen(faction, at);
        return seen > 0 && tick - seen <= window;
    }

    /// <summary>Cells this faction has ever seen, for a report that would rather count than assume.</summary>
    public int KnownCells(FactionId faction)
    {
        if (faction.Value < 0 || faction.Value >= Factions) return 0;
        var known = 0;
        foreach (var seen in lastSeen[faction.Value])
        {
            if (seen > 0) known++;
        }

        return known;
    }

    internal void Write(Persistence.WorldWriter writer)
    {
        writer.Int(cells);
        for (var faction = 0; faction < Factions; faction++)
        {
            foreach (var seen in lastSeen[faction]) writer.Long(seen);
        }
    }

    internal void Read(Persistence.WorldReader reader)
    {
        // <b>The saved grid's size decides how much to read, and this world's decides where it lands.</b> They
        // are the same on any save this game will load — the extent is part of the world — but reading a
        // count that was not written is how a save format desynchronises silently, so the number goes in the
        // file and comes back out of it.
        var written = reader.Int();
        for (var faction = 0; faction < Factions; faction++)
        {
            var seen = lastSeen[faction];
            Array.Clear(seen);
            for (var i = 0; i < written * written; i++)
            {
                var value = reader.Long();
                if (i < seen.Length) seen[i] = value;
            }
        }
    }

    /// <summary>
    /// One faction's last-seen ticks, in cell order, for the fingerprint and the save to read.
    /// </summary>
    /// <remarks>
    /// A span rather than a callback because the fingerprint's sink is a by-ref struct and cannot be captured
    /// — the walker has to do its own looping. Read-only so that the only way to change what a faction knows
    /// is to observe.
    /// </remarks>
    internal ReadOnlySpan<long> SeenBy(int faction) => lastSeen[faction];

    private bool TryCellOf(Vector2 at, out int x, out int z)
    {
        var local = (at + new Vector2(extentMetres * 0.5f)) / CellMetres;
        x = (int)MathF.Floor(local.X);
        z = (int)MathF.Floor(local.Y);
        return x >= 0 && z >= 0 && x < cells && z < cells;
    }

    private Vector2 CentreOf(int x, int z) => new(
        (x + 0.5f) * CellMetres - extentMetres * 0.5f,
        (z + 0.5f) * CellMetres - extentMetres * 0.5f);
}

/// <summary>
/// How far a building watches, as the simulation's own number.
/// </summary>
/// <remarks>
/// <b>One authority, because there were about to be two.</b> These radii lived in the renderer's fog settings
/// and nothing outside it read them — which was fine while the only thing that cared about seeing was the
/// thing that draws it. A faction's knowledge is a simulation fact that decisions read, so it cannot take its
/// reach from a view tunable, and copying the numbers is precisely the failure
/// <see cref="SimulationWorld.CanSee"/>'s own comment warns about: two pieces of code deciding independently
/// what can be seen, disagreeing invisibly until somebody is standing in the difference.
/// </remarks>
internal static class NodeWatch
{
    public const float GranaryMetres = 105f;
    public const float DepotMetres = 190f;
    public const float DwellingMetres = 45f;

    public static float MetresFor(NodeKind kind) => kind switch
    {
        NodeKind.Granary => GranaryMetres,
        NodeKind.ForwardDepot => DepotMetres,
        NodeKind.House or NodeKind.Farm or NodeKind.Barracks => DwellingMetres,
        _ => 0f,
    };
}

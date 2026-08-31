using RTSGame.Simulation.Agents;

namespace RTSGame.Control;

/// <summary>
/// The sets the player has named, recalled by number.
/// </summary>
/// <remarks>
/// <b>The third of §82's four representations, and deliberately not any of the other three.</b> A transient
/// selection is what is highlighted right now; a command cohort is what one order produced and is the thing
/// that owns a shared field, slots and a formation; a crew is a set somebody chose to be able to ask for
/// again. Recalling a crew and giving it an order produces a cohort — it does not <em>become</em> one. Making
/// these one type is exactly the collapse §82 warned about, where a control group accidentally owns
/// locomotion and a work crew accidentally becomes a formation.
/// <para>
/// <b>It stays out of the simulation, by ruling.</b> Nothing here is ticked, fingerprinted or saved, and the
/// world has no idea it exists. That is cheap and it is safe: <see cref="AgentStore"/> tombstones rather than
/// compacts and never reuses an id, precisely so a stale reference resolves to a dead body and is refused
/// rather than resolving to somebody else — which is the one property a list of ids held outside the
/// simulation needs. The costs are real and worth naming rather than discovering: a crew does not survive a
/// save, and nothing computed at group resolution can be shared through it. Both become questions the moment
/// something wants either, and neither is a reason to put it in the world today.
/// </para></remarks>
internal sealed class ControlGroups
{
    /// <summary>Slots 0-9, addressed by the digit that recalls them.</summary>
    public const int Slots = 10;

    private readonly List<AgentId>[] crews =
        Enumerable.Range(0, Slots).Select(_ => new List<AgentId>()).ToArray();

    /// <summary>Replaces a crew with whoever is given.</summary>
    public void Assign(int slot, IEnumerable<AgentId> members)
    {
        var crew = crews[slot];
        crew.Clear();
        foreach (var id in members)
        {
            if (!crew.Contains(id)) crew.Add(id);
        }
    }

    /// <summary>Extends a crew, which is how a set is edited rather than rebuilt.</summary>
    public void Add(int slot, IEnumerable<AgentId> members)
    {
        var crew = crews[slot];
        foreach (var id in members)
        {
            if (!crew.Contains(id)) crew.Add(id);
        }
    }

    /// <summary>
    /// Who is in a crew, with the dead dropped.
    /// </summary>
    /// <remarks>
    /// Pruned on the way out rather than swept on a timer, because the only moment the answer matters is the
    /// moment somebody asks, and a crew nobody has recalled since a raid is allowed to be stale. The prune
    /// is written back so a crew that has lost half its members reports the truth on the panel afterwards.
    /// </remarks>
    public IReadOnlyList<AgentId> Members(int slot, AgentStore agents)
    {
        var crew = crews[slot];
        crew.RemoveAll(id => !agents.Contains(id));
        return crew;
    }

    /// <summary>The crews that have anybody in them, as "1:12 · 4:3", or null if none do.</summary>
    public string? Summary(AgentStore agents)
    {
        var parts = new List<string>();
        for (var slot = 0; slot < Slots; slot++)
        {
            var size = Members(slot, agents).Count;
            if (size > 0) parts.Add($"{slot}:{size}");
        }
        return parts.Count == 0 ? null : string.Join(" · ", parts);
    }

    /// <summary>Whether a crew holds exactly the given set — the test for "you asked for this twice".</summary>
    public bool Holds(int slot, IReadOnlyCollection<AgentId> selection, AgentStore agents)
    {
        var crew = Members(slot, agents);
        if (crew.Count == 0 || crew.Count != selection.Count) return false;
        foreach (var id in crew)
        {
            if (!selection.Contains(id)) return false;
        }
        return true;
    }
}

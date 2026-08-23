using System.Diagnostics;
using System.Numerics;
using Blix.Diagnostics;
using RTSGame.Simulation;
using RTSGame.Simulation.Collision;
using RTSGame.Simulation.Economy;

namespace RTSGame.Rendering;

/// <summary>How much of a piece of ground the player is entitled to be shown.</summary>
/// <remarks>
/// Three states rather than two, because "have I ever been here" and "can I see it now" are different
/// questions with different answers and different consumers. A static thing is shown on the strength of the
/// first; a moving one only on the strength of the second.
/// </remarks>
internal enum FogTier
{
    /// <summary>Never scouted.</summary>
    Unexplored,

    /// <summary>Scouted once, not currently watched. What is drawn here is a memory.</summary>
    Remembered,

    /// <summary>Watched right now, by a body or by a building.</summary>
    Visible,
}

/// <summary>The dials on the fog: who sees how far, and how much work a frame may spend finding out.</summary>
internal sealed class FogSettings
{
    /// <summary>Whether anything is revealed at all, or the whole map is treated as watched.</summary>
    /// <remarks>
    /// Off by default while this is an instrument. Fog changes what every other subsystem is allowed to
    /// draw, so it goes in behind a switch that can be thrown back — and the switch has a second job, which
    /// is the verification that fog is genuinely outside the simulation: the determinism fingerprint and
    /// <c>--years</c> must produce identical results with it on and off, and that is only checkable if both
    /// are reachable from one build.
    /// </remarks>
    [Tune(Label = "fog of war", Group = "fog")]
    public bool Enabled = false;

    /// <summary>Paints every fog cell in the colour of its tier.</summary>
    /// <remarks>
    /// <b>The instrument, and it comes before anything reads the masks.</b> §73's lesson from the cascades,
    /// which cost four rounds of screenshots before a debug tint answered the question in one: a spatial
    /// field that is wrong is invisible except as an absence somewhere, and an absence is exactly what the
    /// eye is worst at. Fog is a worse case than the cascades were, because a fog bug and correct fog look
    /// the same from the chair — dark ground is what both of them produce.
    /// </remarks>
    [Tune(Label = "show fog cells", Group = "fog")]
    public bool ShowCells = false;

    /// <summary>How far a granary watches, in metres.</summary>
    /// <remarks>
    /// §7's detection radius at the settlement, reused rather than derived — and flagged as reused, because
    /// that number was chosen for the threat layer's question ("how much warning does a defence get") and
    /// this is a different question ("how much of the map is lit"). They may want to diverge. What makes a
    /// building's reach matter more than a body's is arithmetic: a villager sees 22 m on a 600 m map, so
    /// unit vision alone is a keyhole and the settlement is what makes the middle distance legible.
    /// </remarks>
    [Tune(10.0, 260.0, Label = "granary sight (m)", Group = "fog")]
    public float GranarySightMetres = 105f;

    /// <summary>How far a forward depot watches, in metres.</summary>
    /// <remarks>§7's outpost figure. An outpost's whole purpose is to see, so it sees furthest.</remarks>
    [Tune(10.0, 320.0, Label = "depot sight (m)", Group = "fog")]
    public float DepotSightMetres = 190f;

    /// <summary>How far a house or a field watches, in metres.</summary>
    /// <remarks>
    /// Not from §7, which has nothing to say about a house. Between a body's 22 m and the granary's 105:
    /// enough that a settlement's built area is continuously lit rather than reading as a row of separate
    /// lamps, which is the artefact a too-small figure here produces.
    /// </remarks>
    [Tune(5.0, 160.0, Label = "dwelling sight (m)", Group = "fog")]
    public float DwellingSightMetres = 45f;

    /// <summary>How many watchers get their view recomputed each frame.</summary>
    /// <remarks>
    /// <b>The budget that keeps this off the frame's critical path, and it is a real constraint rather than
    /// caution.</b> <see cref="SimulationWorld.CanSee"/> marches the ray at the navigation raster's half
    /// metre, so one granary asking about every cell in its 105 m reach is some three hundred and fifty
    /// cells at up to two hundred samples each — around thirty-five thousand terrain lookups for one
    /// building. Refreshing every watcher every frame would put a millisecond-scale spike in a phase this
    /// session just spent an evening measuring at 11 ms.
    /// <para>
    /// So watchers are refreshed round-robin, a few per frame, and the masks are at most one cycle stale.
    /// That is invisible for exploration — a cycle is a fraction of a second and a body walks under a metre
    /// in it, against a ten metre cell — and it is what lets fog share the sim's own sight predicate rather
    /// than approximating it with something cheaper. Sharing the predicate is the point; a cheaper copy is
    /// the drift bug.
    /// </para>
    /// </remarks>
    [Tune(1, 24, Label = "watchers per frame", Group = "fog")]
    public int WatchersPerFrame = 4;
}

/// <summary>
/// What the player has seen of the map, and what they are watching now.
/// </summary>
/// <remarks>
/// <b>Two masks on one grid, and the grid is the canopy grid's.</b> Ten metre cells, indexed by the same
/// arithmetic as the tree density field, because the fog is going to be read by two things that must never
/// disagree: the shader that dims the ground, and the per-cell gate that decides whether the trees in a cell
/// are submitted at all. Two grids for those two jobs is the bug this file's history is made of — a rule
/// implemented locally at each call site until the copies drift — and it would surface as trees popping in
/// and out along a boundary the player can see the fog at. One indexing function makes the disagreement
/// unrepresentable rather than unlikely.
/// <para>
/// <b>Dressing, in §52's sense, and deliberately so.</b> Nothing here is on <see cref="SimulationWorld"/>,
/// nothing is fingerprinted, nothing is saved. It is derived from unit positions and is therefore perfectly
/// deterministic, which is exactly the argument that would justify moving it inside — and the reason not to
/// is that the moment a simulation decision reads it, "what the player can see" becomes "what the world
/// does" and view state is inside the determinism fingerprint for good. The rule is one-way and it is worth
/// stating as a direction rather than as a location: <b>fog may read the simulation; the simulation may
/// never read fog.</b>
/// </para>
/// <para>
/// It holds cheaply because the thing shared across the seam is a pure predicate —
/// <see cref="SimulationWorld.CanSee"/> — rather than state. Fog calls it and mutates nothing.
/// </para>
/// <para>
/// <b>What is not here.</b> A per-faction knowledge aggregate — "which cells does faction F see" — is what
/// an AI opponent and a §30 defence decision will need, and that <em>is</em> simulation state, carried and
/// fingerprinted. It is a different structure from these two masks and it is not built. When it arrives the
/// player's fog should be derived from it rather than computed alongside it, or the two will drift in the
/// one way a player notices: seeing a unit the simulation has decided is hidden.
/// </para>
/// <para>
/// <b>The masks say where; they do not say what.</b> Whether unexplored ground is black or merely dim, and
/// whether a landmark is exempt from being hidden at all, are decisions for each consumer and are not
/// encoded here. The landform is exempt — §71's rock is meant to be seen from across the valley and that is
/// how an unreachable resource becomes a reason to expand — but that is the ground renderer's business to
/// honour, not a third mask.
/// </para>
/// </remarks>
internal sealed class FogOfWar
{
    /// <summary>
    /// How much ground one fog cell covers, in metres.
    /// </summary>
    /// <remarks>
    /// Ten, which is the canopy grid's cell and is not a coincidence: this <em>is</em> that grid, and the
    /// argument for ten is recorded there — about two canopies across, so a tree is never alone in its own
    /// cell however thick the wood, and a village's cleared ring does not average into the wood beside it.
    /// <para>
    /// It is coarse for a fog mask and that is the right trade. The edge does not need to be finer than the
    /// filtering that will smooth it, and ten metres of soft boundary is what fog edges look like anyway.
    /// What the coarseness buys is that the gate reading this is testing four thousand cells instead of
    /// thirty-four thousand nodes.
    /// </remarks>
    public const float CellMetres = 10f;

    /// <summary>Has this ground ever been watched. Monotonic: it only ever rises.</summary>
    /// <remarks>
    /// Monotonic is what makes the round-robin refresh safe. A partial update can only add, so there is no
    /// frame in which explored ground reads as unexplored because its watcher's turn has not come round —
    /// which is the flicker a decaying exploration mask would produce.
    /// </remarks>
    private float[] explored = Array.Empty<float>();

    /// <summary>Is this ground watched now. Replaced wholesale at the end of each cycle.</summary>
    private float[] visible = Array.Empty<float>();

    /// <summary>The cycle in progress, which becomes <see cref="visible"/> when it completes.</summary>
    /// <remarks>
    /// <b>Double-buffered rather than cleared and restamped in place.</b> Round-robin and "currently
    /// visible" pull against each other: clearing the live mask each frame would blank every watcher whose
    /// turn has not come, and never clearing it would leave vision smeared behind a walking body forever.
    /// Accumulating a whole cycle into a scratch mask and swapping resolves it — the live mask is always a
    /// complete answer, just an answer from up to one cycle ago.
    /// </remarks>
    private float[] pending = Array.Empty<float>();

    /// <summary>The watchers, rebuilt once per cycle rather than once per frame.</summary>
    /// <remarks>
    /// <b>Once per cycle, because finding the buildings means walking the nodes.</b> Buildings are a handful
    /// among tens of thousands of nodes, so collecting them is the same full sweep this session measured at
    /// seven milliseconds — and adding a second one per frame to feed the thing meant to remove the first
    /// would be its own joke. Amortised over a cycle it is a few hundred nodes a frame.
    /// <para>
    /// <b>And it is left as a sweep on purpose.</b> The obvious fix is to cache the buildings and rebuild
    /// only when they change, and the obvious fix is wrong here for the same reason the compact tree array
    /// was wrong: the next piece of work is a spatial index over these nodes, which makes this scan cheap for
    /// every caller rather than cheap for this one. A cache written now is a cache thrown away then. What
    /// this owes in the meantime is honesty about the cost, which is what the cycle figures in the readout
    /// are for — the frame that starts a cycle is the expensive one and it says so.
    /// </para>
    /// </remarks>
    private readonly List<(Vector2 At, float Sight)> watchers = new();

    /// <summary>
    /// Whose fog this is.
    /// </summary>
    /// <remarks>
    /// <b>Faction zero, and the filter is the point rather than a detail.</b> Without it every agent on the
    /// map is a watcher, so a raider walking in from the edge scouts the map <em>for the player</em> — the
    /// exact inverse of what fog is for, and a bug that would have been invisible on a village run because
    /// there are no raiders on one. The convention is <c>SimulationWorld</c>'s: an unspecified faction
    /// resolves to zero and <c>RaidDirector</c> takes one.
    /// <para>
    /// This is also the seam the knowledge layer will widen. One mask pair per faction is the shape that
    /// makes an AI opponent read its own fog rather than the player's, and it is why the field is named for
    /// a faction rather than hard-coded into the loops below.
    /// </para>
    /// </remarks>
    private static readonly FactionId Player = new(0);

    private int cursor;
    private int cells;
    private float extent;

    /// <summary>Whether the masks currently hold the "fog off" fill rather than anything scouted.</summary>
    private bool revealed;

    /// <summary>Frames left to wait before looking for watchers again, after finding none.</summary>
    private int emptyCycleWait;

    /// <summary>How many cells the grid is on a side.</summary>
    public int Cells => cells;

    /// <summary>
    /// How many watchers the current cycle covers, split by what kind of thing is doing the watching.
    /// </summary>
    /// <remarks>
    /// <b>Split, because one total cannot say whether the granary is in it.</b> The first run of this reported
    /// twenty-nine watchers on a settlement of thirteen people and sixteen buildings, and twenty-nine is
    /// consistent with two completely different worlds: every body and every building, or every body and no
    /// building at all. It was the second, and a single count had no way of saying so — §74's lesson about
    /// instruments that print plausible numbers, arriving on schedule.
    /// </remarks>
    public (int Bodies, int Buildings) WatcherCount { get; private set; }

    /// <summary>Sight queries asked on the last frame, and what they cost.</summary>
    public int QueriesLastFrame { get; private set; }

    /// <summary>
    /// Over the last complete cycle: cells asked about, cells granted, and the longest reach in the list.
    /// </summary>
    /// <remarks>
    /// <b>The three numbers that separate "occlusion is working" from "the loop is not running".</b> A small
    /// lit area is the expected output of a settlement ringed by woodland and also the expected output of a
    /// stamp loop that never iterates — and per-frame counts cannot tell them apart, because the round-robin
    /// smears one watcher's work across several frames and every frame's figure looks equally small.
    /// <para>
    /// Asked against granted says how much of the reach the trees are eating. The widest reach says whether
    /// the granary's hundred metres is in the list at all, which is the other way a small lit area happens.
    /// </para>
    /// </remarks>
    public (int Asked, int Granted, float WidestReach) LastCycle { get; private set; }

    private int askedThisCycle;
    private int grantedThisCycle;

    /// <summary>Milliseconds the last frame's share of the cycle took.</summary>
    public double MillisecondsLastFrame { get; private set; }

    /// <summary>How many cells sit in each tier, which is what says the fog is doing anything at all.</summary>
    /// <remarks>
    /// <b>Counted, because "the map is dark" is what both a working fog and a broken one look like.</b> §74's
    /// lesson from the ablation harnesses that read plausible numbers while being wrong: an instrument needs
    /// to report the thing that would tell you it is lying. Three counts that sum to the cell total, moving
    /// in the directions a walking villager should move them, is that thing.
    /// </remarks>
    public (int Unexplored, int Remembered, int Visible) Counts { get; private set; }

    /// <summary>Sizes the grid to a map, discarding whatever was known about the last one.</summary>
    /// <remarks>
    /// The cell count is the canopy grid's formula exactly, and if that ever changes this has to change with
    /// it — which is the argument for the canopy field reading its geometry from here rather than computing
    /// its own.
    /// </remarks>
    public void Resize(float extentMetres)
    {
        extent = extentMetres;
        cells = Math.Max(2, (int)MathF.Ceiling(extentMetres / CellMetres) + 1);
        var total = cells * cells;
        if (explored.Length != total)
        {
            explored = new float[total];
            visible = new float[total];
            pending = new float[total];
        }
        else
        {
            Array.Clear(explored);
            Array.Clear(visible);
            Array.Clear(pending);
        }

        watchers.Clear();
        cursor = 0;
        askedThisCycle = 0;
        grantedThisCycle = 0;
        // Cleared along with the masks, or a map rolled while the fog is switched off comes up dark: the
        // fill that "off" depends on would be skipped as already done, against arrays that were just zeroed.
        revealed = false;
        Counts = (total, 0, 0);
    }

    /// <summary>Which cell a place falls in.</summary>
    public int Index(Vector2 at)
    {
        var local = (at + new Vector2(extent * 0.5f)) / CellMetres;
        var x = Math.Clamp((int)local.X, 0, cells - 1);
        var z = Math.Clamp((int)local.Y, 0, cells - 1);
        return z * cells + x;
    }

    /// <summary>Where the centre of a cell is, which is the point its visibility is decided at.</summary>
    /// <remarks>
    /// <b>One sample per cell, at its centre, and the coarseness is admitted rather than hidden.</b> A ten
    /// metre cell is all-or-nothing on its middle, so a wood's edge cutting a cell in half resolves to
    /// whichever side the centre is on. Sampling the corners too would cost four times as much to soften a
    /// boundary that the mask's own filtering softens anyway.
    /// </remarks>
    public Vector2 CentreOf(int index)
    {
        var x = index % cells;
        var z = index / cells;
        return new Vector2(
            (x + 0.5f) * CellMetres - extent * 0.5f,
            (z + 0.5f) * CellMetres - extent * 0.5f);
    }

    /// <summary>What the player is entitled to be shown of a place.</summary>
    public FogTier TierAt(Vector2 at) => TierOf(Index(at));

    /// <summary>What the player is entitled to be shown of a cell.</summary>
    public FogTier TierOf(int index) =>
        visible[index] > 0.5f ? FogTier.Visible
        : explored[index] > 0.5f ? FogTier.Remembered
        : FogTier.Unexplored;

    /// <summary>
    /// Advances one frame's share of the refresh cycle.
    /// </summary>
    /// <remarks>
    /// Reads the simulation and writes nothing back to it. The one-way rule in this class's remarks is
    /// enforced by that being the only direction any call here points.
    /// </remarks>
    public void Advance(SimulationWorld simulation, FogSettings settings)
    {
        if (cells == 0) return;

        QueriesLastFrame = 0;
        MillisecondsLastFrame = 0.0;

        // <b>Everything watched, rather than a second code path.</b> Fog off has to mean the fog says yes to
        // everything, not that its consumers learn to skip it — a gate with two behaviours is a gate that
        // gets one of them wrong. So the masks are filled and the tiers are honest; nothing downstream needs
        // to know the switch exists.
        //
        // <b>And the transitions are handled in both directions, which the first cut got wrong.</b> Filling
        // the masks is destructive: switch off and on again and the map is explored for good, because "off"
        // has written the same ones into <see cref="explored"/> that scouting would have. Off wins nothing by
        // being subtle here — the switch is a reset — so the fill happens once on the way off, and the way
        // back on clears. What must not happen is a toggle that silently reveals the map and leaves no trace
        // of having done it.
        if (!settings.Enabled)
        {
            if (revealed) return;
            revealed = true;
            Array.Fill(explored, 1f);
            Array.Fill(visible, 1f);
            Counts = (0, 0, cells * cells);
            return;
        }

        if (revealed)
        {
            revealed = false;
            Array.Clear(explored);
            Array.Clear(visible);
            Array.Clear(pending);
            watchers.Clear();
            cursor = 0;
            askedThisCycle = 0;
            grantedThisCycle = 0;
            Counts = (cells * cells, 0, 0);
        }

        var clock = Stopwatch.StartNew();

        if (cursor >= watchers.Count)
        {
            // <b>A map with nobody on it must not rebuild the list every frame.</b> The cycle-start condition
            // is "the cursor has run off the end", and an empty list satisfies it immediately and permanently
            // — so on the map lab, or a settlement that has died out, this would sweep all thirty-four
            // thousand nodes on every single frame looking for a granary that is not there. Which is the
            // precise cost this session spent an evening measuring, reintroduced by a subsystem meant to help
            // remove it.
            if (watchers.Count == 0 && emptyCycleWait > 0)
            {
                emptyCycleWait--;
                MillisecondsLastFrame = clock.Elapsed.TotalMilliseconds;
                return;
            }

            StartCycle(simulation, settings);
            // Half a second before asking again. Long enough that an empty map costs nothing, short enough
            // that the first building put down is lit before the player has finished looking at it.
            if (watchers.Count == 0) emptyCycleWait = 30;
        }

        var until = Math.Min(watchers.Count, cursor + Math.Max(1, settings.WatchersPerFrame));
        for (; cursor < until; cursor++)
        {
            Stamp(simulation, watchers[cursor]);
        }

        // The cycle closed on this frame: the scratch mask is now a complete answer, so it becomes the
        // answer. Swapped rather than copied, and the outgoing mask is cleared to become the next scratch.
        if (cursor >= watchers.Count && watchers.Count > 0)
        {
            (visible, pending) = (pending, visible);
            Array.Clear(pending);
            Recount();

            var widest = 0f;
            foreach (var watcher in watchers) widest = MathF.Max(widest, watcher.Sight);
            LastCycle = (askedThisCycle, grantedThisCycle, widest);
            askedThisCycle = 0;
            grantedThisCycle = 0;
        }

        MillisecondsLastFrame = clock.Elapsed.TotalMilliseconds;
    }

    /// <summary>Collects who is watching, for the cycle about to start.</summary>
    private void StartCycle(SimulationWorld simulation, FogSettings settings)
    {
        watchers.Clear();
        cursor = 0;
        var bodies = 0;

        foreach (ref readonly var agent in simulation.Agents.All)
        {
            // A sheltered body is indoors, and the building it is in does its own watching. Counting both
            // would put a body's reach on top of its house's for no reason anybody could see.
            if (!agent.IsAlive || agent.Sheltered) continue;
            if (agent.Faction != Player) continue;
            watchers.Add((agent.Position, agent.SightMetres));
            bodies++;
        }

        foreach (ref readonly var node in simulation.Nodes.All)
        {
            if (!node.IsAlive || !node.IsBuilt) continue;
            if (node.Faction != Player) continue;
            var sight = node.Kind switch
            {
                NodeKind.Granary => settings.GranarySightMetres,
                NodeKind.ForwardDepot => settings.DepotSightMetres,
                NodeKind.House or NodeKind.Farm => settings.DwellingSightMetres,
                _ => 0f,
            };
            if (sight > 0f) watchers.Add((node.Position, sight));
        }

        WatcherCount = (bodies, watchers.Count - bodies);
    }

    /// <summary>Marks every cell one watcher can see.</summary>
    private void Stamp(SimulationWorld simulation, (Vector2 At, float Sight) watcher)
    {
        var reach = watcher.Sight;
        if (reach <= 0f) return;

        // The square of cells the disc could touch, then the disc, then the sight test. Cheapest rejection
        // first: a bounding box compare, a squared distance, and only then a ray march.
        var span = (int)MathF.Ceiling(reach / CellMetres);
        var origin = Index(watcher.At);
        var originX = origin % cells;
        var originZ = origin / cells;
        var reachSquared = reach * reach;

        for (var dz = -span; dz <= span; dz++)
        for (var dx = -span; dx <= span; dx++)
        {
            var x = originX + dx;
            var z = originZ + dz;
            if (x < 0 || z < 0 || x >= cells || z >= cells) continue;

            var index = z * cells + x;
            var centre = CentreOf(index);
            if (Vector2.DistanceSquared(centre, watcher.At) > reachSquared) continue;

            // Already stamped this cycle by another watcher, and visibility is not a quantity — a second
            // yes is the same yes, and the ray march it would cost is the expensive part.
            if (pending[index] > 0.5f) continue;

            QueriesLastFrame++;
            askedThisCycle++;
            if (!simulation.CanSee(watcher.At, reach, centre)) continue;

            grantedThisCycle++;
            pending[index] = 1f;
            explored[index] = 1f;
        }
    }

    private void Recount()
    {
        var unexplored = 0;
        var remembered = 0;
        var seen = 0;
        for (var i = 0; i < explored.Length; i++)
        {
            if (visible[i] > 0.5f) seen++;
            else if (explored[i] > 0.5f) remembered++;
            else unexplored++;
        }

        Counts = (unexplored, remembered, seen);
    }
}

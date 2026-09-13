using System.Collections.Generic;
using System.Linq;
using RTSGame.Simulation;
using RTSGame.Simulation.Collision;

namespace RTSGame.AI.Planning;

/// <summary>What one rule did on one decision, for the report that would rather count than assume.</summary>
internal readonly record struct Firing(long Tick, string Rule, int Gap, int Closed)
{
    public override string ToString() =>
        $"t{Tick} {Rule}: gap {Gap}, closed {Closed}{(Closed < Gap ? " (short)" : string.Empty)}";
}

/// <summary>
/// Runs a plan for one faction: census, then rules in priority order until the attention budget is spent.
/// </summary>
/// <remarks>
/// <b>Attention is the budget, which is what §7 asked for.</b> Difficulty in this game is an attention
/// budget rather than a cheat, and here that is literal: <see cref="GapsPerDecision"/> is how many things a
/// planner may put right at once, and the decision interval is how often it looks. Two knobs, no resource
/// cheat and no vision cheat anywhere in the layer.
/// <para>
/// Nothing in here reads another faction's stores, and the only ground it consults is what §131's knowledge
/// says this faction has reached. It holds an <see cref="OrderLedger"/> outside the world on the
/// RaidDirector's precedent, which is also the one thing about it a save does not capture — see §140.
/// </para>
/// </remarks>
internal sealed class Planner
{
    /// <summary>
    /// Ticks between decisions.
    /// </summary>
    /// <remarks>
    /// A delegation-layer player thinks on a human cadence, not a simulation one. Half a second is faster
    /// than a person and slow enough that the cost is nothing.
    /// </remarks>
    public const int DecideEveryTicks = 30;

    /// <summary>Gaps one decision may close.</summary>
    /// <remarks>
    /// Four, which is enough to post two builders and put two hands to work in the same half second, and few
    /// enough that a settlement with everything wrong at once puts it right in order rather than all at once.
    /// This is the number to turn when difficulty becomes a thing to set.
    /// </remarks>
    public const int GapsPerDecision = 4;

    /// <summary>What this planner may close per decision. See <see cref="GapsPerDecision"/>.</summary>
    private readonly int gapsPerDecision;

    private readonly FactionId faction;
    private readonly Plan plan;
    private readonly Census census = new();
    private readonly OrderLedger ledger = new();
    private readonly HandPool hands = new();
    private readonly Queue<Firing> firings = new();

    /// <summary>Orders and open gaps per rule, which is what says who is churning.</summary>
    private readonly Dictionary<string, (int Orders, int Short)> perRule = new();
    private long nextDecision;

    public Planner(FactionId faction, Plan? plan = null, int gapsPerDecision = GapsPerDecision)
    {
        this.faction = faction;
        this.plan = plan ?? Plan.Settler();
        this.gapsPerDecision = gapsPerDecision;
    }

    public FactionId Faction => faction;

    public string PlanName => plan.Name;

    public int Decisions { get; private set; }

    public int OrdersIssued { get; private set; }

    /// <summary>Rules that fired and closed nothing, which is the interesting half of a plan.</summary>
    public int GapsLeftOpen { get; private set; }

    public void Update(SimulationWorld world)
    {
        if (world.TickNumber < nextDecision) return;
        nextDecision = world.TickNumber + DecideEveryTicks;
        Decisions++;

        census.Take(world, faction);
        if (!census.Store.IsValid || census.Mouths == 0) return;
        hands.Refill(census);

        var allowance = gapsPerDecision;
        foreach (var rule in plan.Rules)
        {
            if (allowance <= 0) break;
            if (!rule.Holds(census)) continue;
            var gap = rule.Intent.Gap(census, ledger, world.TickNumber);
            if (gap <= 0) continue;
            var closed = rule.Intent.Close(
                world, census, ledger, hands, world.TickNumber, System.Math.Min(gap, allowance));
            allowance -= closed;
            OrdersIssued += closed;
            if (closed < gap) GapsLeftOpen++;
            var text = rule.ToString();
            var tally = perRule.TryGetValue(text, out var had) ? had : default;
            perRule[text] = (tally.Orders + closed, tally.Short + (closed < gap ? 1 : 0));
            Remember(new Firing(world.TickNumber, text, gap, closed));
        }
    }

    /// <summary>The last few firings, verbatim, so "why did it do that" has an answer.</summary>
    /// <remarks>
    /// Sixty-four, which is a couple of minutes of decisions — long enough to cover whatever was just watched
    /// from the chair and short enough to print. The same shape and the same reasoning as §116's route ring.
    /// </remarks>
    public IEnumerable<Firing> Recent => firings;

    private void Remember(Firing firing)
    {
        firings.Enqueue(firing);
        while (firings.Count > 64) firings.Dequeue();
    }

    /// <summary>The plan as written, and what it has been doing, for --why.</summary>
    public string Describe()
    {
        var lines = new List<string> { $"  plan '{plan.Name}' for faction {faction.Value}:" };
        lines.AddRange(plan.Rules.Select(rule =>
        {
            var text = rule.ToString();
            var tally = perRule.TryGetValue(text, out var had) ? had : default;
            return $"    {text}  [{tally.Orders} order(s), short on {tally.Short} decision(s)]";
        }));
        lines.Add(
            $"    {Decisions:N0} decisions at {gapsPerDecision} gap(s) each, {OrdersIssued:N0} orders, " +
            $"{GapsLeftOpen:N0} gap(s) left open");
        // The census as the rules last saw it. A gap that will not close is nearly always a "standing" count
        // that does not add up to the workforce it is a share of, so these are printed side by side.
        lines.Add(
            $"    last census: workforce {census.Workforce} = {census.OnGrain} grain + {census.OnWood} wood " +
            $"+ {census.Idle.Count} idle + {census.Workforce - census.OnGrain - census.OnWood - census.Idle.Count} " +
            $"elsewhere; {census.Militia} militia, {census.Mouths} mouths");
        lines.Add("    last firings:");
        lines.AddRange(firings.Reverse().Take(8).Select(firing => $"      {firing}"));
        return string.Join('\n', lines);
    }
}

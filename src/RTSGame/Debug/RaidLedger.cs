using System.Numerics;
using RTSGame.Simulation;
using RTSGame.Simulation.Agents;
using RTSGame.Simulation.Collision;
using RTSGame.Simulation.Economy;

namespace RTSGame.Debug;

/// <summary>
/// What a raid actually cost, counted rather than guessed at.
/// </summary>
/// <remarks>
/// <b>Three sessions of watching raids produced three wrong conclusions, and every one of them would have
/// been caught by a number.</b> "They surround them and push each other around" was a crowd held apart by
/// avoidance with three bodies in contact. "They don't die even surrounded" was a chase settling 0.14 m
/// outside striking distance. "Nothing is being stolen" was six hundred grain a session. Watching tells you
/// something is wrong; only counting tells you what.
/// <para>
/// So this counts the whole transaction, on both sides, in four groups:
/// </para>
/// <list type="bullet">
/// <item><b>Who did what.</b> Raids launched, raiders sent, alarms raised, how many answered, how many
/// were ever actually in contact — which is the number that has been the surprise every time.</item>
/// <item><b>Who died.</b> Both sides, separately, because "bodies killed" hid the fact that no raider had
/// ever died at all for three sessions running.</item>
/// <item><b>What moved.</b> Taken out of the world, and recovered off a corpse — §7's whole argument for
/// interception is that killing a loaded raider <em>returns</em> the grain, so the two have to be
/// distinguishable or the argument is untestable.</item>
/// <item><b>What the interruption cost</b>, which is the part nobody counts and which may be the largest
/// term. A raid that takes nothing and stops twenty people working for a minute has still done damage.</item>
/// </list>
/// <para>
/// Observational: it reads the world and writes nothing to it, which is why it lives in <c>Debug/</c> and
/// needs no place in the fingerprint. It is safe to run in a headless gate and in a watched session.
/// </para>
/// </remarks>
internal sealed class RaidLedger
{
    private readonly FactionId ours;
    private bool[] engagedLastTick = new bool[64];
    private bool[] everAnswered = new bool[64];
    private bool[] everFought = new bool[64];
    private float previousStock;
    private bool seenStock;

    public RaidLedger(FactionId ours) => this.ours = ours;

    /// <summary>Seconds with at least one hostile body on the map.</summary>
    public float SecondsUnderThreat { get; private set; }

    /// <summary>Seconds with at least one of ours committed to a threat.</summary>
    public float SecondsWithTheAlarmUp { get; private set; }

    /// <summary>
    /// Times one of ours took up a threat decision it was not already holding.
    /// </summary>
    /// <remarks>
    /// The count of interruptions, which is not the count of fighters: most alarms end in going back to
    /// work, either because somebody closer had it or because the fight was not winnable.
    /// </remarks>
    public int Alarms { get; private set; }

    /// <summary>The most of ours committed at once, and the most ever in contact at once.</summary>
    public int MostAnswering { get; private set; }

    public int MostInContact { get; private set; }

    /// <summary>The most bodies being fought over at once, on either side.</summary>
    public int MostUnderAttack { get; private set; }

    /// <summary>Body-seconds of contact, which is what a fight is made of.</summary>
    public float ContactSeconds { get; private set; }

    /// <summary>
    /// How many of ours ever committed to a fight, and how many of those were ever in one.
    /// </summary>
    /// <remarks>
    /// <b>The gap between these two has been the answer three times running</b>, so it is measured rather
    /// than inferred from peaks. A peak cannot distinguish three bodies fighting for a long time from
    /// thirty for an instant, and "sixteen commit and three fight" is the shape of every finding in this
    /// arc: the front was never the limiter, arrival was.
    /// </remarks>
    public int EverAnswered { get; private set; }

    public int EverFought { get; private set; }

    /// <summary>Deaths, by side.</summary>
    public int OursKilled { get; private set; }

    public int TheirsKilled { get; private set; }

    /// <summary>
    /// Labour-seconds of ours withheld from work by the threat, and what that projects to in grain.
    /// </summary>
    /// <remarks>
    /// <b>The honest form of "what did the raid really cost".</b> Grain is per <em>farm</em> per year in
    /// this economy, not per hand — hands only decide whether a crop's three labour windows are met — so
    /// "hands times a rate" would be the wrong arithmetic. What is countable is labour-seconds withheld,
    /// and the projection is deliberately marginal: a settlement's twelve farms yield
    /// <c>GrainPerFarmPerYear</c> each for about a hand-year of attention each, so a labour-second is worth
    /// <c>700 / year</c> grain <em>at the margin</em>.
    /// <para>
    /// It over-states whenever the window would have been met anyway — a hand pulled off a field that was
    /// going to be finished on time costs nothing — and that is stated rather than corrected, because the
    /// correction needs to know which window each body was inside and the over-statement is a ceiling,
    /// which is the useful direction for a cost.
    /// </para>
    /// </remarks>
    public float LabourSecondsLost { get; private set; }

    public float GrainForgone =>
        LabourSecondsLost * EconomyRates.GrainPerFarmPerYear / WorldCalendar.YearSeconds;

    /// <summary>Stock of ours that left the world, and stock recovered from a body that fell carrying it.</summary>
    public int Recovered { get; private set; }

    /// <summary>
    /// Total harm dealt by anybody to anybody, so wasted damage can be seen rather than deduced.
    /// </summary>
    /// <remarks>
    /// Harm that killed nothing is the interesting residue: subtract the health of everything that died
    /// from this and what is left went into bodies that walked away. A large residue means damage is being
    /// <em>spread</em> rather than concentrated, which is a different problem from not enough of it.
    /// </remarks>
    public float HarmDealt { get; private set; }

    public void Observe(SimulationWorld world, float deltaSeconds)
    {
        var threat = world.Threat;
        var hostiles = 0;
        var answering = 0;
        var interrupted = 0f;

        var bodies = world.Agents.All;
        if (engagedLastTick.Length < bodies.Length)
        {
            Array.Resize(ref engagedLastTick, bodies.Length * 2);
            Array.Resize(ref everAnswered, bodies.Length * 2);
            Array.Resize(ref everFought, bodies.Length * 2);
        }

        for (var i = 0; i < bodies.Length; i++)
        {
            ref readonly var body = ref bodies[i];
            if (!body.IsAlive)
            {
                engagedLastTick[i] = false;
                continue;
            }

            if (body.Faction != ours)
            {
                if (body.Strength > 0f) hostiles++;
                continue;
            }

            // Committed, running, or dealing with a load because of the threat — all three are the threat
            // layer holding a body off its work, which is the thing being priced.
            var engaged = body.Resolve > 0f || body.PuttingDown;
            if (engaged)
            {
                answering++;
                interrupted += deltaSeconds;
                if (!engagedLastTick[i]) Alarms++;
                if (!everAnswered[i])
                {
                    everAnswered[i] = true;
                    EverAnswered++;
                }
            }

            engagedLastTick[i] = engaged;
        }

        HarmDealt = threat.Dealt;
        if (hostiles > 0) SecondsUnderThreat += deltaSeconds;
        if (answering > 0) SecondsWithTheAlarmUp += deltaSeconds;
        LabourSecondsLost += interrupted;
        MostAnswering = Math.Max(MostAnswering, answering);
        MostInContact = Math.Max(MostInContact, threat.Attacking);
        MostUnderAttack = Math.Max(MostUnderAttack, threat.UnderAttack);
        ContactSeconds += threat.Contacts * deltaSeconds;

        foreach (var striker in threat.LandedThisTick)
        {
            var slot = striker.Value;
            if (slot < 0 || slot >= everFought.Length) continue;
            if (!world.Agents.Contains(striker) || world.Agents.Get(striker).Faction != ours) continue;
            if (everFought[slot]) continue;
            everFought[slot] = true;
            EverFought++;
        }

        foreach (var (_, faction) in threat.FellThisTick)
        {
            if (faction == ours) OursKilled++;
            else TheirsKilled++;
        }

        // Recovery, read off the heaps: goods that fell out of a dead raider's hands and are lying about
        // to be fetched. Measured as the rise in what is on the ground, which is the only place dropped
        // cargo can be, and only counted upward — a heap shrinking is a hauler doing its job.
        var onTheGround = 0f;
        foreach (ref readonly var node in world.Nodes.All)
        {
            if (node.IsAlive && node.IsPile) onTheGround += node.Stock.Total;
        }

        if (seenStock && onTheGround > previousStock) Recovered += (int)(onTheGround - previousStock);
        previousStock = onTheGround;
        seenStock = true;
    }

    /// <summary>The whole transaction, in the order somebody would ask about it.</summary>
    public IEnumerable<string> Lines(int raidsLaunched, int raidersSent, int escaped, int stolen)
    {
        var stopped = raidersSent - escaped;
        yield return
            $"  raids {raidsLaunched}, raiders sent {raidersSent}, of whom {escaped} got home and " +
            $"{stopped} did not";
        yield return
            $"  alarms raised {Alarms}, most answering at once {MostAnswering}, most ever in contact " +
            $"{MostInContact} — {ContactSeconds:F0} body-seconds of actual fighting";
        yield return
            $"  of ours, {EverAnswered} ever left their work for a fight and {EverFought} were ever in " +
            $"one" +
            (EverAnswered > 0
                ? $" — {100f * EverFought / EverAnswered:F0}% of those who went actually got there"
                : string.Empty);
        yield return
            $"  {HarmDealt:F0} body-seconds of harm dealt in all, of which " +
            $"{OursKilled * 20f + TheirsKilled * 18f:F0} went into something that died — " +
            $"the rest into bodies that walked away";
        yield return
            $"  killed: {TheirsKilled} of theirs, {OursKilled} of ours" +
            (TheirsKilled > 0 ? $" ({OursKilled / (float)TheirsKilled:F1} of ours per raider)" : "");
        yield return
            $"  stock: {stolen} carried off the map, {Recovered} dropped and recoverable";
        yield return
            $"  interruption: {SecondsUnderThreat:F0} s with something on the map, " +
            $"{SecondsWithTheAlarmUp:F0} s with the alarm up, {LabourSecondsLost:F0} labour-seconds " +
            $"withheld — about {GrainForgone:F0} grain of work not done";
    }
}

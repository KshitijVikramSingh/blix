using System.Collections.Generic;
using System.Linq;
using RTSGame.Simulation;
using RTSGame.Simulation.Economy;

namespace RTSGame.AI.Planning;

/// <summary>One line of a plan: what has to be true, and what is then wanted.</summary>
internal sealed record Rule(Condition[] When, Intent Intent)
{
    public bool Holds(Census census) => When.All(condition => condition.Holds(census));

    public override string ToString() =>
        When.Length == 0
            ? $"always → {Intent.Name}"
            : $"when {string.Join(" and ", When.Select(c => c.ToString()))} → {Intent.Name}";
}

/// <summary>
/// An ordered list of rules, where order is priority.
/// </summary>
/// <remarks>
/// <b>A list and not a tree or a score.</b> §141 rejected both for the same reason: they hide which rule
/// fired behind a number, and every finding in this project came from an instrument that made something
/// visible. A priority list is a sentence somebody can disagree with.
/// <para>
/// Typed here, with every reading and intent carrying a name, so a text plan file is a transcription of this
/// rather than a redesign of it. The parser is the least interesting part and the vocabulary will churn
/// through the first few designs.
/// </para>
/// </remarks>
internal sealed class Plan
{
    private readonly List<Rule> rules = new();


    public string Name { get; }

    public Plan(string name) => Name = name;

    public IReadOnlyList<Rule> Rules => rules;

    public Plan When(Reading reading, Comparison op, float value, Intent intent)
    {
        rules.Add(new Rule(new[] { new Condition(reading, op, value) }, intent));
        return this;
    }

    public Plan When(Condition[] conditions, Intent intent)
    {
        rules.Add(new Rule(conditions, intent));
        return this;
    }

    public Plan Always(Intent intent)
    {
        rules.Add(new Rule(System.Array.Empty<Condition>(), intent));
        return this;
    }

    /// <summary>
    /// The settler's plan: what §132 through §140 arrived at, written out.
    /// </summary>
    /// <remarks>
    /// <b>The first plan reproduces the hand-written cascade exactly</b>, so the layer is provable rather
    /// than a rewrite — the year legs and the two-settlement legs are the test, and they assert an economy
    /// this arrangement already produced. Every number in it has a section behind it:
    /// <list type="bullet">
    /// <item>2.5 seasons is §8's autonomy time, the figure the HUD shows.</item>
    /// <item>0.85 and 0.35 are §140's lean, replacing a switch that emptied eight fields for a summer.</item>
    /// <item>two thirds is the founding's own ratio, eight fields to four cutters (§135).</item>
    /// <item>four militia and four walls are what the founding cache of stone pays for (§140).</item>
    /// </list>
    /// </remarks>
    /// <summary>
    /// The settler's plan with the walls taken out, for the comparison that settles what they cost.
    /// </summary>
    /// <remarks>
    /// §141. Two plans on one map is the instrument this layer is for: the question "did the walls cost the
    /// population" has no answer from reasoning about the code and one clean answer from running the same
    /// settlement twice.
    /// </remarks>
    public static Plan Unwalled()
    {
        var plan = new Plan("unwalled");
        foreach (var rule in Settler().Rules)
        {
            if (rule.Intent is Upgrade || rule.Intent is Structure { Kind: NodeKind.PalisadeWall }) continue;
            plan.rules.Add(rule);
        }

        return plan;
    }

    /// <summary>Looks a plan up by the name a command line uses.</summary>
    public static Plan Named(string name) => name switch
    {
        "unwalled" => Unwalled(),
        _ => Settler(),
    };

    public static Plan Settler() =>
        new Plan("settler")
            // <b>Projects before employment, because employment is always hungry.</b> The last two rules
            // want every unemployed hand in the settlement, so anything after them is reached only once the
            // workforce is placed — which on the first run of this layer meant never. Order is not a
            // presentation detail in a priority list; it is the whole of what the list says.
            //
            // Somewhere to train, hands on it, a garrison, then walls, then stone. A settlement that walls
            // itself in before it can field anybody has spent its timber on the wrong half of a defence.
            // <b>Every condition an act depends on is written here.</b> One site at a time is the
            // projects_open term: hands are the scarce thing, and a second site only splits the two that
            // staffing can spare.
            .When(
                new[]
                {
                    new Condition(Reading.Barracks, Comparison.Below, 1f),
                    new Condition(Reading.ProjectsOpen, Comparison.Exactly, 0f),
                    new Condition(Reading.Timber, Comparison.AtLeast, Construction.TimberFor(NodeKind.Barracks)),
                    new Condition(Reading.Stone, Comparison.AtLeast, Construction.StoneFor(NodeKind.Barracks)),
                },
                new Structure(NodeKind.Barracks, 1, ring: 16f))
            .Always(new StaffProjects(builders: 2))
            .When(Reading.Barracks, Comparison.AtLeast, 1f, new Garrison(4))
            // <b>Walls out of a comfortable woodpile, and the resource in that condition is the whole
            // finding.</b> §141. The plan without a gate here builds four walls and upgrades three of them,
            // and both settlements end the year smaller for it. Measured against the same plan with the wall
            // rules removed, at winter of the first year:
            //
            //   walled    f0 16 people, 4,073 grain,   0 wood | f1 12 people, 3,689 grain
            //   unwalled  f0 19 people, 5,148 grain, 169 wood | f1 17 people, 4,621 grain
            //
            // The unwalled plan beats even the hand-written cascade this layer replaced (17 at winter), so
            // the layer is not what costs anything: <b>four walls is more than this economy affords while it
            // is also feeding itself.</b>
            //
            // Gated on wood and not on grain, which is the part worth being right about. A wall is 60 timber
            // and a stone upgrade takes the hands that would be cutting; wood is also half of what §138's
            // readiness is computed from, so a woodpile spent on walls stops the births two seasons later
            // where nothing connects the two. I tried the grain gate first — 4,200 in the founding cache is
            // far above any threshold, all the building happens before it runs down, and the run came back
            // byte-identical. <b>The condition has to name the resource the act actually spends.</b>
            .When(
                new[]
                {
                    new Condition(Reading.Barracks, Comparison.AtLeast, 1f),
                    new Condition(Reading.Walls, Comparison.Below, 4f),
                    new Condition(Reading.ProjectsOpen, Comparison.Exactly, 0f),
                    new Condition(Reading.WoodSeasons, Comparison.AtLeast, 4f),
                    new Condition(
                        Reading.Timber, Comparison.AtLeast, Construction.TimberFor(NodeKind.PalisadeWall)),
                },
                new Structure(NodeKind.PalisadeWall, 4, ring: 27f))
            .When(
                new[]
                {
                    new Condition(Reading.Stone, Comparison.AtLeast, Upgrade.StoneForAWall),
                    new Condition(Reading.ProjectsOpen, Comparison.Exactly, 0f),
                    new Condition(Reading.WoodSeasons, Comparison.AtLeast, 4f),
                },
                new Upgrade(4))
            // A shortage leans the split and never replaces it. §140.
            .When(Reading.GrainSeasons, Comparison.Below, 2.5f, new Employ(Resource.Grain, 0.85f))
            .When(Reading.WoodSeasons, Comparison.Below, 2.5f, new Employ(Resource.Grain, 0.35f))
            .Always(new Employ(Resource.Grain, 2f / 3f))
            // Everybody the fields did not want goes to the wood.
            .Always(Employ.Rest(Resource.Wood));
}

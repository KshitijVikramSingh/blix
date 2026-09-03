using System;

namespace RTSGame.AI.Planning;

/// <summary>How a condition compares a reading to a number.</summary>
internal enum Comparison
{
    Below,
    AtLeast,
    Exactly,
}

/// <summary>
/// One named figure a faction can see about itself.
/// </summary>
/// <remarks>
/// <b>Named, because a rule has to be printable to be arguable.</b> §141: the explainer prints the condition
/// that fired, and "grain_seasons &lt; 2.5" is a sentence somebody can disagree with while a lambda is not.
/// The names are the text plan format's vocabulary before that format exists, which is the point of writing
/// them now — the loader becomes a transcription rather than a redesign.
/// <para>
/// The standing rule: <b>a reading must be a figure the HUD shows or could show.</b> It was already the rule
/// for Outlook — a bot reasoning in units nobody displays is a bot whose decisions cannot be argued with from
/// the chair — and it is now the rule for the whole vocabulary.
/// </para>
/// </remarks>
internal readonly record struct Reading(string Name, Func<Census, float> Of)
{
    public static readonly Reading GrainSeasons = new("grain_seasons", c => c.GrainSeasons);
    public static readonly Reading WoodSeasons = new("wood_seasons", c => c.WoodSeasons);
    public static readonly Reading Stone = new("stone", c => c.Stone);
    public static readonly Reading Timber = new("timber", c => c.Timber);
    public static readonly Reading Mouths = new("mouths", c => c.Mouths);
    public static readonly Reading Workforce = new("workforce", c => c.Workforce);
    public static readonly Reading EconomyHands = new("economy_hands", c => c.EconomyHands);
    public static readonly Reading Idle = new("idle", c => c.Idle.Count);
    public static readonly Reading Militia = new("militia", c => c.Militia);
    public static readonly Reading Walls = new("walls", c => c.Walls);
    public static readonly Reading Barracks = new("barracks", c => c.Barracks.IsValid ? 1 : 0);
    public static readonly Reading ProjectsOpen = new("projects_open", c => c.Projects.Count);
    public static readonly Reading Fields = new("fields", c => c.Fields.Count);

    public override string ToString() => Name;
}

/// <summary>A reading held against a number. The whole of what a rule may ask.</summary>
/// <remarks>
/// <b>No arithmetic and no state.</b> §141: the moment a rule can accumulate, it can re-derive a figure
/// nobody displays and hold state no save captures. Everything a plan wants to say that this cannot express
/// belongs in a reading, where it gets a name and a place in the report.
/// </remarks>
internal readonly record struct Condition(Reading Reading, Comparison Op, float Value)
{
    public bool Holds(Census census)
    {
        var read = Reading.Of(census);
        return Op switch
        {
            Comparison.Below => read < Value,
            Comparison.AtLeast => read >= Value,
            Comparison.Exactly => Math.Abs(read - Value) < 0.0001f,
            _ => false,
        };
    }

    public override string ToString()
    {
        var op = Op switch
        {
            Comparison.Below => "<",
            Comparison.AtLeast => ">=",
            _ => "==",
        };
        return $"{Reading.Name} {op} {Value:0.##}";
    }
}

namespace RTSGame.Simulation.Economy;

/// <summary>What a field wants doing to it right now.</summary>
internal enum CropPhase
{
    /// <summary>Nothing. The ground is resting and the farmers are elsewhere.</summary>
    Rest,

    /// <summary>Breaking the ground. Sets the ceiling on the whole year's crop.</summary>
    Prepare,

    /// <summary>Tending it. Cheap, and cannot raise the ceiling — only keep it.</summary>
    Maintain,

    /// <summary>Reaping. Earns the crop second by second, and only what is reaped is had.</summary>
    Reap,
}

/// <summary>
/// A year of one field, as three windows of labour with deadlines.
/// </summary>
/// <remarks>
/// <b>This is the game.</b> A field does not produce at a rate; it is prepared, kept and reaped, each in
/// its own window, and missing a window costs something a later window cannot give back. That is what
/// makes the four seasons the four verbs the design names them by — <em>commit, exploit, scramble,
/// survive</em> — rather than four multipliers on an output figure.
/// <list type="bullet">
/// <item><b>Prepare</b> sets the ceiling. <c>potential = prep / target</c>, and nothing later raises it.
/// A field never broken yields nothing whatever happens afterwards.</item>
/// <item><b>Maintain</b> only retains, from <see cref="NeglectedRetention"/> to all of it. Cheap enough
/// to be an afterthought and expensive enough to matter.</item>
/// <item><b>Reap</b> earns the crop as it goes. Grain still standing when the window shuts is grain the
/// settlement never had.</item>
/// </list>
/// <para>
/// The windows are the seasons themselves, which is the correction §6 demanded. The prototype had a crop
/// model on its own calendar and season names on another, so its second harvest fell in the season called
/// Winter and three different functions disagreed about when anything happened. Here there is one cycle a
/// year and it <em>is</em> the year: spring prepares, summer keeps, harvest reaps, winter rests.
/// </para>
/// </remarks>
internal static class CropCycle
{
    /// <summary>Labour-seconds of breaking ground that a full ceiling costs.</summary>
    /// <remarks>
    /// Sized against its window rather than chosen: 900 in a 1,200 s spring is 75% of the season for one
    /// pair of hands, so a field is a real commitment and a farmer pulled away in spring is a ceiling
    /// lowered. That is <em>commit</em>.
    /// </remarks>
    internal static float PrepareLabour = 900f;

    /// <summary>Labour-seconds of tending that keeps the whole ceiling.</summary>
    /// <remarks>
    /// 300 in an 1,800 s summer — 17% of the season, so the other 83% is free for wood and building. That
    /// is <em>exploit</em>, and it is why summer is where a settlement grows rather than where it eats.
    /// </remarks>
    internal static float MaintainLabour = 300f;

    /// <summary>Labour-seconds of reaping that brings in a full crop.</summary>
    /// <remarks>
    /// 800 in a 1,000 s harvest, and <b>it does not fit</b>. One pair of hands spends 80% of the window
    /// reaping and another 26% walking the grain in, which is 106% of a window that does not stretch — so
    /// a single farmer cannot quite bring in a whole field and something has to come and help. That is
    /// <em>scramble</em>, and it is the seasonal reallocation the design is about rather than a number that
    /// happens to be tight.
    /// </remarks>
    internal static float ReapLabour = 800f;

    /// <summary>Share of the ceiling a field keeps when nobody tends it at all.</summary>
    internal static float NeglectedRetention = 0.76f;

    /// <summary>Which window the year is in. The seasons are the windows.</summary>
    public static CropPhase PhaseOf(Season season) => season switch
    {
        Season.Spring => CropPhase.Prepare,
        Season.Summer => CropPhase.Maintain,
        Season.Harvest => CropPhase.Reap,
        _ => CropPhase.Rest,
    };

    /// <summary>Labour-seconds this phase asks for.</summary>
    public static float LabourFor(CropPhase phase) => phase switch
    {
        CropPhase.Prepare => PrepareLabour,
        CropPhase.Maintain => MaintainLabour,
        CropPhase.Reap => ReapLabour,
        _ => 0f,
    };

    /// <summary>Ceiling this field set for itself in spring, from nothing to all of it.</summary>
    public static float CeilingOf(in EconomyNode farm) =>
        Math.Clamp(farm.PrepareWork / MathF.Max(1f, PrepareLabour), 0f, 1f);

    /// <summary>Share of the ceiling still standing, given how well it was tended.</summary>
    public static float RetentionOf(in EconomyNode farm) =>
        NeglectedRetention + (1f - NeglectedRetention) *
        Math.Clamp(farm.MaintainWork / MathF.Max(1f, MaintainLabour), 0f, 1f);

    /// <summary>What this field can still yield this year, in whole units.</summary>
    public static float PotentialOf(in EconomyNode farm) => CeilingOf(in farm) * RetentionOf(in farm);

    /// <summary>Labour-seconds of reaping this field's own crop is worth.</summary>
    public static float ReapTargetOf(in EconomyNode farm) => ReapLabour * PotentialOf(in farm);

    /// <summary>Whether this field still wants work in this phase.</summary>
    public static bool WantsWork(in EconomyNode farm, CropPhase phase) => phase switch
    {
        CropPhase.Prepare => farm.PrepareWork < PrepareLabour - 0.001f,
        CropPhase.Maintain => farm.PrepareWork > 0.1f && farm.MaintainWork < MaintainLabour - 0.001f,
        CropPhase.Reap => farm.ReapWork < ReapTargetOf(in farm) - 0.001f,
        _ => false,
    };

    /// <summary>
    /// What this field would say about itself, which is what the player needs to read.
    /// </summary>
    /// <remarks>
    /// A field's trouble is always in the past — a ceiling not set in spring cannot be explained by
    /// anything visible at harvest — so it has to say so out loud, in its own words, at the time. This is
    /// the whole interface for the mechanic.
    /// </remarks>
    public static string StateOf(in EconomyNode farm, Season season)
    {
        var phase = PhaseOf(season);
        if (phase == CropPhase.Rest) return "resting";
        if (CeilingOf(in farm) <= 0.01f)
        {
            return phase == CropPhase.Prepare ? "unbroken" : "failed";
        }

        return phase switch
        {
            CropPhase.Prepare => farm.PrepareWork >= PrepareLabour - 0.001f
                ? "prepared"
                : $"preparing {CeilingOf(in farm) * 100f:F0}%",
            CropPhase.Maintain => farm.MaintainWork >= MaintainLabour - 0.001f
                ? "tended"
                : farm.MaintainWork <= 0.05f
                    ? "neglected"
                    : $"tending {RetentionOf(in farm) * 100f:F0}%",
            _ => farm.ReapWork >= ReapTargetOf(in farm) - 0.001f
                ? "reaped"
                : farm.ReapWork <= 0.05f
                    ? "standing"
                    : $"reaping {farm.ReapWork / MathF.Max(1f, ReapTargetOf(in farm)) * 100f:F0}%",
        };
    }
}

namespace RTSGame.Rendering;

/// <summary>
/// What a body can be doing, as far as the screen is concerned.
/// </summary>
/// <remarks>
/// <b>Five, because at this camera distance there are five readable states and not fifteen.</b> What a
/// person reads at eighty metres is: moving or not, swinging or standing, fighting or working, upright or
/// down. The jobs layer knows a great deal more than that — assignment, activity, interrupt, cargo,
/// cohort — and none of the rest of it survives the trip to the screen, so this deliberately stops here.
/// Adding a state is a claim that a player could tell it apart, and that claim should be tested from the
/// chair before it is written down.
/// </remarks>
internal enum BodyAction
{
    /// <summary>Standing about: no assignment, or one it has not reached.</summary>
    Idle,

    /// <summary>Under way. The gait is driven by real speed, so this covers a trudge and a trot both.</summary>
    Walk,

    /// <summary>Working at its place — reaping, cutting, quarrying, building, hauling into a store.</summary>
    Labour,

    /// <summary>Swinging at something that is not its own.</summary>
    Strike,

    /// <summary>Down.</summary>
    Fall,
}

/// <summary>
/// Which clip in an asset serves which action, by name, in order of preference.
/// </summary>
/// <remarks>
/// <b>The whole point is that this is data and the game asks for an action, not a clip.</b> Characters
/// arrive from wherever the art comes from and name their animations however that pipeline names them —
/// Quaternius ships <c>HumanArmature|Man_Walk</c>, Mixamo ships <c>Walking</c>, a hand-authored rig ships
/// whatever somebody typed. If the renderer asked for <c>"Man_Walk"</c> by name then every new character
/// would be a code change, and the first thing anybody would do is add a second literal beside the first.
/// <para>
/// So each action lists the names it will accept, best first, and whatever the file happens to contain gets
/// bound at load. A body's job never mentions a clip name; it names an action. Dropping in a pack with a
/// vocabulary nobody anticipated costs one line here.
/// </para>
/// <para>
/// <b>What is bound is printed at load, including what is missing</b> — see the bind report in
/// <see cref="SkinnedBodies"/>. A character with no work animation is a fact worth reading in the terminal
/// rather than discovering from the chair as "the villagers look odd when they farm".
/// </para>
/// </remarks>
internal static class CharacterClips
{
    /// <summary>
    /// Per action: the names that <em>mean</em> that action, then the names that will only stand in for it.
    /// Both best-first, matched without case and without the exporter's armature prefix
    /// (<c>HumanArmature|Man_Walk</c> is matched as <c>Man_Walk</c>).
    /// </summary>
    /// <remarks>
    /// <b>Two lists rather than one, because a marker that fires on everything says nothing.</b> The first
    /// version ranked all the names together and starred any match past the first — which starred all five
    /// actions, since no pack happens to use this file's preferred spelling. The distinction worth reporting
    /// is not "an unusual name" but "there is no clip for this and something else is covering", so the
    /// stand-ins are their own list and only they are marked.
    /// </remarks>
    public static readonly (BodyAction Action, string[] Names, string[] StandIns)[] Table =
    {
        // "Standing" is Quaternius's second idle and reads calmer than Man_Idle's weight shift.
        (BodyAction.Idle,
            new[] { "Idle_Loop", "Idle", "Man_Idle", "Standing", "Man_Standing", "Breathing Idle" },
            Array.Empty<string>()),
        // Running is a real gait but not this one, so it is a stand-in: the phase is driven by metres
        // covered, and a run cycle played at walking distance reads as a mince.
        // <b>The non-root-motion spelling first, every time.</b> The Universal library ships pairs — a clip
        // and a <c>_RM</c> twin that travels — and while horizontal root translation is stripped anyway,
        // binding the travelling one means the renderer spends every frame undoing what the exporter did.
        (BodyAction.Walk,
            new[] { "Walk_Loop", "Walk", "Man_Walk", "Walking", "Walk_Formal_Loop" },
            new[] { "Jog_Fwd_Loop", "Run", "Man_Run", "Sprint_Loop" }),
        // <b>No pack yet ships a labour clip.</b> Neither the Animated Men Pack nor Quaternius's Universal
        // Animation Library has a chop, a dig, a reap or a hammer — so a sword slash stands in, because a
        // downward arc at this distance reads as an axe, a scythe or a pick. The names ahead of it are what
        // a Mixamo search for the real thing returns, so real content binds ahead of the stand-in the moment
        // it exists, with no code change.
        // The farming names lead because the Universal Animation Library 2 is where a real work loop is
        // coming from, so it binds ahead of everything below the moment it is dropped in.
        // <c>Fixing_Kneeling</c> is a genuine work animation — somebody kneeling and working at something —
        // so it is a real clip for this action rather than a stand-in, which is why it sits in this list.
        (BodyAction.Labour,
            new[]
            {
                "Labour", "Farming", "Work", "Chopping", "Chop", "Mining", "Axe Chop", "Hammering",
                "Digging", "Fixing_Kneeling", "PickUp_Table", "PickUp",
            },
            // A fight animation covering for an axe. Only reached if nothing above exists.
            new[] { "Push_Loop", "Interact", "Man_SwordSlash", "SwordSlash", "Man_Punch" }),
        (BodyAction.Strike,
            new[]
            {
                "Sword_Attack", "Strike", "Attack", "Man_SwordSlash", "SwordSlash",
                "Sword And Shield Slash", "Punch_Cross", "Punch_Jab", "Man_Punch", "Punch",
            },
            Array.Empty<string>()),
        (BodyAction.Fall,
            new[] { "Death01", "Death", "Man_Death", "Dying", "Falling Back Death" },
            Array.Empty<string>()),
    };

    /// <summary>How many actions there are, for the arrays indexed by one.</summary>
    public const int Count = 5;
}

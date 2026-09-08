namespace RTSGame.Rendering;

/// <summary>
/// What a body can be doing, as far as the screen is concerned.
/// </summary>
/// <remarks>
/// <b>One entry per thing a player could tell apart at this camera distance</b>, and not one more. The jobs
/// layer knows a great deal that does not survive the trip to the screen — which cohort, which interrupt,
/// how many seconds of dwell remain — and none of that earns a pose.
/// <para>
/// It started at five and grew to eleven, one reason each and every one of them reported from the chair
/// rather than imagined: a kneeling repair pose does not read as felling a tree, a hauler with a coloured
/// box on their head does not read as carrying, and a soldier standing exactly like a farmer does not read
/// as a guard.
/// </para>
/// <para>
/// <b>What is deliberately absent is Run.</b> There is no run in the simulation — every body moves at
/// <c>MaximumSpeed × terrain × laden</c> and nothing raises that — so a run clip would have no state to key
/// off and would either never play or always play. If fleeing should look different, the simulation needs a
/// flee speed first, and then this list gains an entry. A pose with no state behind it is decoration.
/// </para>
/// </remarks>
internal enum BodyAction
{
    /// <summary>Standing about: no assignment, or one it has not reached.</summary>
    Idle,

    /// <summary>Under way, hands empty.</summary>
    Walk,

    /// <summary>
    /// Under way with a load.
    /// </summary>
    /// <remarks>
    /// <b>The most legible thing a body in this game can be doing.</b> The whole economy is goods moving
    /// from where they are to where they are used. It also puts §167's laden slowdown in the body rather
    /// than only in the speed.
    /// </remarks>
    Carry,

    /// <summary>Reaping a field.</summary>
    Reap,

    /// <summary>Felling a tree: standing and swinging, which is exactly what a kneeling pose is not.</summary>
    Chop,

    /// <summary>Cutting stone.</summary>
    Quarry,

    /// <summary>Working at a structure — raising it, repairing it, upgrading it.</summary>
    Build,

    /// <summary>Standing a post as a soldier, which should not look like standing about.</summary>
    Guard,

    /// <summary>Swinging at something that is not its own.</summary>
    Strike,

    /// <summary>Just took a hit. View-side state only — see the health watch in RtsGameLoop.</summary>
    Flinch,

    /// <summary>Down.</summary>
    Fall,
}

/// <summary>
/// Which clip in an asset serves which action, by name, in order of preference.
/// </summary>
/// <remarks>
/// <b>The game asks for an action and never for a clip.</b> Characters arrive from wherever the art comes
/// from and name their animations however that pipeline names them — Quaternius's libraries ship
/// <c>Walk_Loop</c> and <c>TreeChopping_Loop</c>, Mixamo ships <c>Walking</c>, a hand-authored rig ships
/// whatever somebody typed. Naming a clip in code would make every new character a code change.
/// <para>
/// Each action lists the names that <em>mean</em> it, then the names that will only stand in for it. Two
/// lists rather than one, because a marker that fires on everything says nothing: the load report stars
/// stand-ins only, so <c>Quarry=TreeChopping_Loop*</c> reads as "no pick swing exists and a felling swing
/// is covering", while <c>Chop=TreeChopping_Loop</c> reads as the real thing.
/// </para>
/// </remarks>
internal static class CharacterClips
{
    /// <summary>
    /// Accepted names per action: first the ones that mean it, then the ones that stand in for it.
    /// Matched without case and without the exporter's armature prefix.
    /// </summary>
    public static readonly (BodyAction Action, string[] Names, string[] StandIns)[] Table =
    {
        (BodyAction.Idle,
            new[] { "Idle_Loop", "Idle", "Man_Idle", "Standing", "Man_Standing", "Breathing Idle" },
            new[] { "Idle_No_Loop", "Idle_Neutral" }),

        // The non-root-motion spelling first, every time: these libraries ship pairs, and while root
        // placement is discarded anyway, binding the travelling twin means undoing the exporter's work
        // every frame.
        (BodyAction.Walk,
            new[] { "Walk_Loop", "Walk", "Man_Walk", "Walking", "Walk_Formal_Loop" },
            new[] { "Jog_Fwd_Loop", "Run", "Man_Run" }),

        (BodyAction.Carry,
            new[] { "Walk_Carry_Loop", "Carry_Walk", "Walk_Carry", "Carrying" },
            // A push reads as shifting something heavy, which is nearer the truth than empty hands.
            new[] { "Push_Loop", "Walk_Loop", "Walk" }),

        (BodyAction.Reap,
            new[] { "Farm_Harvest", "Harvest", "Reap", "Scythe", "Farming" },
            new[] { "Farm_PlantSeed", "Farm_Watering", "Fixing_Kneeling", "Interact" }),

        // <b>The one the chair asked for by name.</b> A kneeling repair does not read as felling a tree.
        (BodyAction.Chop,
            new[] { "TreeChopping_Loop", "TreeChopping", "Chopping", "Chop", "Axe Chop" },
            new[] { "Melee_Hook", "Sword_Regular_A", "Fixing_Kneeling" }),

        (BodyAction.Quarry,
            new[] { "Mining_Loop", "Mining", "Pickaxe", "Quarry" },
            // No pack ships a pick swing. A felling swing is the same shape — two hands, overhead, into
            // something solid — and reads correctly at this distance.
            new[] { "TreeChopping_Loop", "Melee_Hook", "Fixing_Kneeling" }),

        (BodyAction.Build,
            new[] { "Fixing_Kneeling", "Hammering", "Building", "Repair" },
            new[] { "Interact", "PickUp_Table" }),

        (BodyAction.Guard,
            new[] { "Idle_FoldArms_Loop", "Sword_Idle", "Idle_Sword", "Guard_Idle" },
            new[] { "Idle_Lantern_Loop", "Idle_Torch_Loop", "Idle_Loop" }),

        (BodyAction.Strike,
            new[]
            {
                "Sword_Regular_A", "Sword_Attack", "Sword_Slash", "Strike", "Attack",
                "Man_SwordSlash", "SwordSlash",
            },
            new[] { "Melee_Hook", "Punch_Cross", "Punch_Jab", "Punch_Right", "Man_Punch", "Punch" }),

        (BodyAction.Flinch,
            new[] { "Hit_Chest", "Hit_Knockback", "HitRecieve", "Flinch" },
            new[] { "Hit_Head", "Idle_Loop" }),

        (BodyAction.Fall,
            new[] { "Death01", "Death", "Man_Death", "Dying", "Falling Back Death" },
            System.Array.Empty<string>()),
    };

    /// <summary>How many actions there are, for the arrays indexed by one.</summary>
    public const int Count = 11;
}

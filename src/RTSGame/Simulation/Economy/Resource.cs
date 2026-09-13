namespace RTSGame.Simulation.Economy;

/// <summary>What a settlement moves and stores.</summary>
/// <remarks>
/// Grain and wood were the whole list, on the argument that two is the smallest number that produces the
/// mechanic — they peak and bottom at different times of year, so storage is needed for both on different
/// clocks and summer's free labour is a real allocation decision with a deadline — and that "a third resource
/// adds bookkeeping and no new question."
/// <para>
/// <b>Stone answers that objection rather than ignoring it, and the answer is about the map.</b> A field is
/// something you <em>make</em>, on any level ground you like; trees are scattered by the land but scattered
/// nearly everywhere, so which ones you cut is a choice. Stone is where the rock is, and the rock is where the
/// generator put crag and scree — high, steep, broken ground. <b>It is the first resource whose location the
/// player cannot influence at all</b>, and, because the site scorer rejects exactly that ground as unbuildable,
/// it is the first that is always far from home and always uphill.
/// </para>
/// <para>
/// So it is not a third clock. It is the first resource that cannot be brought inside the arrangement, which
/// makes the forward depot and the cart <em>necessary</em> rather than merely available — the chain §51 has
/// been waiting on. It is also the answer to "is the ledger written for two, or written for resources": see
/// the remarks on <see cref="NodeStock"/> for what had to change before this line could be added safely.
/// </para>
/// </remarks>
internal enum Resource
{
    Grain,
    Wood,

    /// <summary>Quarried from an outcrop, where the map put one. Never grown, never felled.</summary>
    Stone,
}

/// <summary>
/// What is physically at a place, in whole units.
/// </summary>
/// <remarks>
/// <b>Integers, and that is the important decision in this file.</b> Session 6's gate is a year with
/// no counter drifting, and a float ledger accumulated over 162,000 ticks cannot be checked for drift
/// — only for drift beyond a tolerance somebody picked, which is a different and much weaker claim.
/// In whole units, conservation is exact arithmetic: everything produced, minus everything consumed,
/// equals everything stored plus everything being carried. That assertion either holds or names the
/// unit that went missing.
/// <para>
/// Rates are still continuous, as rule 1 requires. A node accumulates fractional production in
/// <c>NodePending</c> and spills whole units into here, so the rate is honest and the ledger is exact.
/// </para>
/// <para>
/// One field per resource rather than an array, because plain data is what makes a node saveable by
/// memory copy and fingerprintable without being asked. Adding a resource adds a field and the
/// compiler finds every switch that needs it.
/// </para>
/// </remarks>
internal struct NodeStock
{
    public int Grain;
    public int Wood;
    public int Stone;

    /// <summary>
    /// The one place a resource becomes a field, and deliberately a switch with no default arm.
    /// </summary>
    /// <remarks>
    /// <b>This was <c>resource == Resource.Grain ? Grain : Wood</c>, and that is a trap rather than a
    /// shorthand.</b> The remarks above promise that "adding a resource adds a field and the compiler finds
    /// every switch that needs it" — a two-way ternary keeps that promise for the <em>field</em> and breaks it
    /// for the lookup: a third resource compiles perfectly and silently reads and writes the second one's
    /// store. Every unit of it would be a unit of wood, conservation would balance, and nothing would say so.
    /// <para>
    /// A switch that throws on anything it has no field for is the weaker but workable version of that: the
    /// compiler will not name the sites (it insists on a default arm for cast values, and a build with three
    /// standing warnings in it teaches people to ignore warnings), but a resource that has been added to the
    /// enum and not to the stores <b>fails on its first tick instead of quietly becoming wood</b>. Loud beats
    /// early when the alternative is silent. It is also why the sweeps below loop over
    /// <see cref="Resources.All"/> rather than adding two fields by name: a sum written as
    /// <c>total.Grain + total.Wood</c> is the same silent hole with none of the syntax to warn about.
    /// </para>
    /// </remarks>
    public int this[Resource resource]
    {
        readonly get => resource switch
        {
            Resource.Grain => Grain,
            Resource.Wood => Wood,
            Resource.Stone => Stone,
            _ => throw new ArgumentOutOfRangeException(
                nameof(resource), resource, "this store has no field for that resource"),
        };
        set
        {
            switch (resource)
            {
                case Resource.Grain: Grain = value; break;
                case Resource.Wood: Wood = value; break;
                case Resource.Stone: Stone = value; break;
                default:
                    throw new ArgumentOutOfRangeException(
                        nameof(resource), resource, "this store has no field for that resource");
            }
        }
    }

    /// <summary>
    /// Everything here, whatever it is.
    /// </summary>
    /// <remarks>
    /// <b>Named fields rather than a loop over <see cref="Resources.All"/>, and it is the one place in this file
    /// that earns the exception.</b> Written as a loop it is correct by construction and it is also the hottest
    /// arithmetic in the simulation: <c>SweepSpentNodes</c> asks it for every node on every tick, so on a map
    /// with thirty thousand trees that is a million indexer calls a second through a switch. Measured, the
    /// settlement year went from minutes to not finishing.
    /// <para>
    /// The exchange is that a new resource has to be added here by hand, which is exactly the silent hole this
    /// file's remarks warn about — so it is pinned by a self-test that compares this against the loop. The rule
    /// is not "never write it out", it is "never write it out without something checking".
    /// </para>
    /// </remarks>
    public readonly int Total => Grain + Wood + Stone;

    /// <summary>The same sum, computed generically, for the test that pins <see cref="Total"/>.</summary>
    public readonly int TotalBySweep
    {
        get
        {
            var total = 0;
            foreach (var resource in Resources.All) total += this[resource];
            return total;
        }
    }

    /// <summary>Whether every resource is exactly zero — which is not the same as the total being zero.</summary>
    /// <remarks>
    /// Conservation asks this, and it has to ask it per resource: a drift of one unit of grain appearing and
    /// one unit of wood vanishing sums to nothing, and is two broken ledgers rather than none.
    /// </remarks>
    public readonly bool IsZero => Grain == 0 && Wood == 0 && Stone == 0;

    public void Add(Resource resource, int amount) => this[resource] += amount;

    /// <summary>
    /// Adds another stock to this one, field by field.
    /// </summary>
    /// <remarks>
    /// <b>The same exception <see cref="Total"/> earns, and for the same measured reason.</b> Written as a loop
    /// over <see cref="Resources.All"/> it costs six indexer calls through a switch per node, and the callers
    /// are <c>TotalStored</c> and <c>TotalHeld</c> — swept once per node per tick by the conservation check. On
    /// a four-thousand-node map that is a hundred thousand switch dispatches a second, and it took the
    /// settlement year's tick from 1.1 ms to somewhere north of 2.5.
    /// <para>
    /// Pinned by the same self-test that pins Total, so a resource added to the enum and not to this line is
    /// caught rather than silently dropped from every conservation sum in the game.
    /// </para>
    /// </remarks>
    public void Add(in NodeStock other)
    {
        Grain += other.Grain;
        Wood += other.Wood;
        Stone += other.Stone;
    }
}

/// <summary>Sub-unit accumulation, so a continuous rate can fill an integer store.</summary>
internal struct NodePending
{
    public float Grain;
    public float Wood;
    public float Stone;

    public float this[Resource resource]
    {
        readonly get => resource switch
        {
            Resource.Grain => Grain,
            Resource.Wood => Wood,
            Resource.Stone => Stone,
            _ => throw new ArgumentOutOfRangeException(
                nameof(resource), resource, "this store has no field for that resource"),
        };
        set
        {
            switch (resource)
            {
                case Resource.Grain: Grain = value; break;
                case Resource.Wood: Wood = value; break;
                case Resource.Stone: Stone = value; break;
                default:
                    throw new ArgumentOutOfRangeException(
                        nameof(resource), resource, "this store has no field for that resource");
            }
        }
    }

    /// <summary>
    /// Adds a continuous amount and returns however many whole units have accumulated, leaving the
    /// remainder behind.
    /// </summary>
    public int Accrue(Resource resource, float amount)
    {
        var pending = this[resource] + amount;
        var whole = (int)MathF.Floor(pending);
        this[resource] = pending - whole;
        return whole;
    }
}

/// <summary>Every resource there is, for anything that has to sweep them.</summary>
internal static class Resources
{
    public static readonly Resource[] All = Enum.GetValues<Resource>();
}

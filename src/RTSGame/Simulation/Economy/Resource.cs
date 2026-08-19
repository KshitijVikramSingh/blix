namespace RTSGame.Simulation.Economy;

/// <summary>What a settlement moves and stores. Two, on offset cycles — §6's flow calendar.</summary>
/// <remarks>
/// Grain and wood rather than a longer list, because two is the smallest number that produces the
/// mechanic: they peak and bottom at different times of year, so storage is needed for both on
/// different clocks and summer's free labour is a real allocation decision with a deadline. A third
/// resource adds bookkeeping and no new question.
/// </remarks>
internal enum Resource
{
    Grain,
    Wood,
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

    public int this[Resource resource]
    {
        readonly get => resource == Resource.Grain ? Grain : Wood;
        set
        {
            if (resource == Resource.Grain) Grain = value;
            else Wood = value;
        }
    }

    public readonly int Total => Grain + Wood;

    public void Add(Resource resource, int amount) => this[resource] += amount;
}

/// <summary>Sub-unit accumulation, so a continuous rate can fill an integer store.</summary>
internal struct NodePending
{
    public float Grain;
    public float Wood;

    public float this[Resource resource]
    {
        readonly get => resource == Resource.Grain ? Grain : Wood;
        set
        {
            if (resource == Resource.Grain) Grain = value;
            else Wood = value;
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

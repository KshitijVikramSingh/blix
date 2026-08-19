namespace RTSGame.Simulation.Economy;

/// <summary>
/// Where this body lives.
/// </summary>
/// <remarks>
/// A body's tie to the economy is its household, not a store. It used to be a store — every villager
/// asked every few seconds which granary was nearest it in route seconds — and that was wrong twice
/// over: it put a routing query per person on the tick when the answer never depended on the person,
/// and it meant a farmhand posted at the edge of a holding could starve while living in a house next
/// door to a full granary.
/// <para>
/// So the catchment question is asked of the <em>house</em>, which does not move, and the body only has
/// to know which house it belongs to. Plain data, so it saves and fingerprints with the rest of the
/// body.
/// </para>
/// </remarks>
internal struct AgentHome
{
    /// <summary>The household this body belongs to, or none if there is no room anywhere.</summary>
    /// <remarks>
    /// None is a signal rather than an error: §6 wants a persistent surplus to name its blocked sink,
    /// and "grain rising, population capped by housing" is exactly a settlement with unhoused people in
    /// it.
    /// </remarks>
    public NodeId House;

    /// <summary>Seconds until this body looks for a place to live again.</summary>
    public float RebindSeconds;

    /// <summary>Node-network revision the binding was made against.</summary>
    public int Revision;
}

namespace RTSGame.Simulation.Economy;

/// <summary>
/// Which store feeds this body, and when it will ask again.
/// </summary>
/// <remarks>
/// §6 inverts the obvious arrangement: a source owns a catchment and consumers inside it are bound to
/// it, rather than every consumer asking every tick where the nearest source is. This is a body's end
/// of that binding, and it is re-asked on a timer rather than continuously because the answer only
/// changes when the body has walked somewhere or the network has changed.
/// <para>
/// A body bound to nothing is outside every catchment and goes without, which is what makes reach
/// decide whether a place is supplied at all — and therefore what makes settlement layout a decision
/// rather than decoration. Plain data, so it saves and fingerprints with the rest of the body.
/// </para>
/// </remarks>
internal struct AgentSupply
{
    /// <summary>The store this body draws from, or none if it is outside every catchment.</summary>
    public NodeId Node;

    /// <summary>Seconds until this body asks again which store is nearest.</summary>
    public float RebindSeconds;

    /// <summary>Node-network revision the binding was made against.</summary>
    /// <remarks>
    /// So building or losing a granary re-binds everybody on the next tick rather than up to a rebind
    /// period later, which is the case a player actually watches: place a second granary and the
    /// nearer half of the settlement should start drawing from it.
    /// </remarks>
    public int Revision;

    /// <summary>Route seconds from this body to its store, for the overlay and for hauling.</summary>
    public float Seconds;
}

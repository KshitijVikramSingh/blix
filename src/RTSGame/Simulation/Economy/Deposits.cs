namespace RTSGame.Simulation.Economy;

/// <summary>
/// What the two placed, finite resources have in common: they are taken out of a thing standing on the map.
/// </summary>
/// <remarks>
/// <b>One indirection so the job layer stops naming wood.</b> The whole chain that sends a body to work — find
/// the nearest deposit, re-base on a store that can still reach one, spend a shift taking units out of it, walk
/// them home — was written against <c>Resource.Wood</c> by name, because wood was the only thing on the map.
/// Adding stone by copying that chain would have produced two of everything, and the two would have drifted:
/// the reach fix, the claim check and the re-basing rule are one behaviour, not two.
/// <para>
/// So the behaviour stays single and asks here for the numbers. <see cref="Woodland"/> and
/// <see cref="Quarrying"/> still own their own figures — this decides nothing, it only knows which of them to
/// ask. Grain is not a deposit: a field is not a stock standing on the ground, it is labour in three windows,
/// which is why <c>CropCycle</c> exists and why this returns zero for it rather than pretending.
/// </para>
/// </remarks>
internal static class Deposits
{
    /// <summary>Whether this resource is taken out of something standing on the map.</summary>
    public static bool IsDeposit(Resource resource) => resource is Resource.Wood or Resource.Stone;

    /// <summary>Whether a node of this kind is standing natural stock rather than anybody's property.</summary>
    /// <remarks>
    /// Lives here rather than beside <see cref="EconomyNode.IsNaturalDeposit"/> because two places now need
    /// it and only one of them has a node to ask: ownership is resolved while the node is being built. One
    /// definition, and <c>IsNaturalDeposit</c> defers to it — a second spelling of this list is precisely the
    /// near-synonym that keeps costing this codebase days.
    /// </remarks>
    public static bool IsNaturalDepositKind(NodeKind kind) => kind is NodeKind.Tree or NodeKind.Outcrop;

    /// <summary>How far from its store a body will go for this resource.</summary>
    public static float ReachMetres(Resource resource) => resource switch
    {
        Resource.Wood => Woodland.ReachMetres,
        Resource.Stone => Quarrying.ReachMetres,
        _ => 0f,
    };

    /// <summary>Units a second of labour frees from a deposit of this resource.</summary>
    public static float TakePerSecond(Resource resource) => resource switch
    {
        Resource.Wood => Woodland.CutPerSecond,
        Resource.Stone => Quarrying.CutPerSecond,
        _ => 0f,
    };

    /// <summary>Seconds of labour one body's load of this resource is.</summary>
    public static float LoadSeconds(Resource resource, int carryCapacity) => resource switch
    {
        Resource.Wood => Woodland.LoadSeconds(carryCapacity),
        Resource.Stone => Quarrying.LoadSeconds(carryCapacity),
        _ => 0f,
    };

    /// <summary>What one deposit of this resource holds when untouched.</summary>
    public static float PerDeposit(Resource resource) => resource switch
    {
        Resource.Wood => Woodland.WoodPerTree,
        Resource.Stone => Quarrying.StonePerOutcrop,
        _ => 0f,
    };

    /// <summary>The node kind a deposit of this resource is.</summary>
    public static NodeKind KindOf(Resource resource) => resource switch
    {
        Resource.Stone => NodeKind.Outcrop,
        _ => NodeKind.Tree,
    };

    /// <summary>Which resource a deposit node holds.</summary>
    public static Resource ResourceOf(NodeKind kind) =>
        kind == NodeKind.Outcrop ? Resource.Stone : Resource.Wood;
}

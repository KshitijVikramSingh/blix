namespace RTSGame.Simulation;

/// <summary>
/// How much room a body needs beyond its own radius before it will fit somewhere.
/// </summary>
/// <remarks>
/// This was the literal <c>0.035f</c>, written out beside a radius in sixteen places across three
/// layers — the walkability test, the clearance comparison, every terrain clamp, and the body
/// traversability check. All sixteen were asking the same question, so a change to any one of them
/// was a change to a definition that lived nowhere.
/// <para>
/// It sits in the root namespace rather than in <c>AgentDefaults</c> deliberately. Navigation and
/// terrain take a radius as a number and know nothing about agents, which is a separation worth
/// keeping; this is the one fact about bodies that all three layers genuinely share, and the root
/// namespace is visible from every one of them without a using or a dependency.
/// </para>
/// <para>
/// Why it is not zero: a body whose radius exactly equals the clearance of a cell is touching the
/// obstacle, and the position solver has to be able to place it there without the navigation layer
/// immediately calling the result invalid. Three and a half centimetres is a tenth of a standard
/// body and well inside a navigation cell, so it costs no route and buys the two layers room to
/// disagree by a rounding error without a body being declared stuck.
/// </para>
/// </remarks>
internal static class BodyFootprint
{
    /// <summary>Clearance a body needs beyond its radius, in metres.</summary>
    public const float NavigationMargin = 0.035f;
}

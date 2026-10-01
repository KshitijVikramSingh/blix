namespace Blix;

/// <summary>
/// A vertex attribute the cook did not carry into the cooked file, recorded in it.
/// </summary>
/// <remarks>
/// <para>
/// Collection is subtractive, so application-specific semantics and future standard attributes are
/// surfaced even when the engine has no prewritten list of them. It is judged against the complete
/// cooked vertex (tangent, two UV sets, colour, and four skin influences), so it is a fact of the file:
/// a load that asks for a narrower layout chooses less, and loses nothing it would be told about.
/// </para>
/// </remarks>
/// <param name="Semantic">The glTF attribute name, verbatim — <c>TEXCOORD_2</c>, <c>_BATCHID</c>.</param>
/// <param name="Primitives">How many cooked primitives declared it.</param>
public sealed record UnreadAttribute(string Semantic, int Primitives)
{
    /// <summary>What the unread attribute represents.</summary>
    public string Explanation => Semantic switch
    {
        _ when Semantic.StartsWith("JOINTS_", StringComparison.Ordinal)
            || Semantic.StartsWith("WEIGHTS_", StringComparison.Ordinal) =>
            "skinning influences the cook did not read: on a mesh no skin drives, or a pair that is not "
            + "complete and contiguous (the four strongest of the complete pairs are kept)",
        _ when Semantic.StartsWith("TEXCOORD_", StringComparison.Ordinal) =>
            "a UV set beyond the two the cooked vertex carries",
        _ when Semantic.StartsWith("COLOR_", StringComparison.Ordinal) =>
            "a vertex colour set beyond COLOR_0, the one the cooked vertex carries",
        MorphTargets =>
            "morph targets whose weights are all zero and undriven: the base mesh is the render "
            + "(where they take effect, the cook refuses the file)",
        _ when Semantic.StartsWith("_", StringComparison.Ordinal) =>
            "an application-specific attribute; the spec reserves the underscore prefix for these",
        _ => "not read by the cook",
    };

    /// <summary>
    /// The pseudo-semantic used for morph targets, which are not an attribute name.
    /// </summary>
    /// <remarks>
    /// Morph targets hang off the primitive rather than appearing in its attribute dictionary, so
    /// they cannot be caught by the subtractive sweep and are counted under this name instead.
    /// Spelled as a reserved-looking token so it cannot collide with a real semantic.
    /// </remarks>
    public const string MorphTargets = "(morph targets)";
}

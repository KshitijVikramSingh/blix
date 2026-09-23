namespace Blix;

/// <summary>
/// A vertex attribute the selected glTF import path did not consume.
/// </summary>
/// <remarks>
/// <para>
/// Collection is subtractive, so application-specific semantics and future standard attributes are
/// surfaced even when the engine has no prewritten list of them. Tangent, colour/second-UV, and
/// skin-influence channels are judged against the concrete layout used by that import.
/// </para>
/// </remarks>
/// <param name="Semantic">The glTF attribute name, verbatim — <c>TEXCOORD_1</c>, <c>_BATCHID</c>.</param>
/// <param name="Primitives">How many imported primitive/layout pairs declared it.</param>
public sealed record GltfIgnored(string Semantic, int Primitives)
{
    /// <summary>What the unread attribute represents.</summary>
    public string Explanation => Semantic switch
    {
        _ when Semantic.StartsWith("JOINTS_", StringComparison.Ordinal)
            || Semantic.StartsWith("WEIGHTS_", StringComparison.Ordinal) =>
            "skinning influences not consumed by this import path; rigged import reads contiguous "
            + "complete pairs, while static import does not apply skinning",
        "TEXCOORD_1" =>
            "a second UV set not carried by the selected static vertex layout",
        _ when Semantic.StartsWith("TEXCOORD_", StringComparison.Ordinal) =>
            "an additional UV set not represented by the current import layouts",
        _ when Semantic.StartsWith("COLOR_", StringComparison.Ordinal) =>
            "a second vertex colour set",
        MorphTargets =>
            "morph targets — blend shapes are not applied",
        _ when Semantic.StartsWith("_", StringComparison.Ordinal) =>
            "an application-specific attribute; the spec reserves the underscore prefix for these",
        _ => "not read by this importer",
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

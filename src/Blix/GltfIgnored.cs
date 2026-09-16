namespace Blix;

/// <summary>
/// A vertex attribute a glTF carried that Blix did not read.
/// </summary>
/// <remarks>
/// <para>
/// <b>What the import did not read, and the last of its kind.</b> There was a companion,
/// <c>GltfSkipped</c>, reporting whole mesh NODES the rigged import declined — first meshes on a
/// second skin, then static meshes under no joint. Both turned out to be this importer's rules
/// rather than the format's, and once they were removed there was nothing left to skip, so it went.
/// This one remains because attributes genuinely are unread: a constraint nobody can see is
/// indistinguishable from an asset that had nothing there.
/// </para>
/// <para>
/// <b>Derived from the spec rather than from the assets, which is the whole point.</b> glTF 2.0
/// defines a closed set of mesh attribute semantics — POSITION, NORMAL, TANGENT, TEXCOORD_n,
/// COLOR_n, JOINTS_n, WEIGHTS_n, plus application-specific names the spec reserves the underscore
/// prefix for. Blix reads index 0 of each and nothing else. That gap is knowable without opening a
/// single file, and it was previously found the other way round: COLOR_0 was noticed because 49
/// primitives in this tree happened to carry it. That method only ever finds what the content
/// already has.
/// </para>
/// <para>
/// So the check here is <b>subtractive</b> — every attribute the primitive declares that is not one
/// this importer reads — rather than a list of known-missing names. A hardcoded list would go stale
/// the moment an exporter emitted something nobody here had thought of, which is exactly the case
/// worth hearing about.
/// </para>
/// </remarks>
/// <param name="Semantic">The glTF attribute name, verbatim — <c>TEXCOORD_1</c>, <c>_BATCHID</c>.</param>
/// <param name="Primitives">How many primitives in the file declared it.</param>
public sealed record GltfIgnored(string Semantic, int Primitives)
{
    /// <summary>What is actually lost, in words. An attribute name is not a consequence.</summary>
    /// <remarks>
    /// <b>The distinction that matters is missing feature vs wrong result.</b> A second UV set that
    /// goes unread is a capability Blix does not have; a fifth bone influence that goes unread is a
    /// vertex that DEFORMS INCORRECTLY, silently, and no count anywhere goes down. Saying so here
    /// keeps that difference in one place instead of in whoever happens to read the list.
    /// </remarks>
    public string Explanation => Semantic switch
    {
        "JOINTS_1" or "WEIGHTS_1" or "JOINTS_2" or "WEIGHTS_2" =>
            "more than four bone influences per vertex — the skin is TRUNCATED to its first four "
            + "and those vertices deform incorrectly",
        _ when Semantic.StartsWith("TEXCOORD_", StringComparison.Ordinal) =>
            "a second UV set — lightmaps and detail layers cannot be sampled",
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

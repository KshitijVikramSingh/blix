namespace Blix;

/// <summary>Why a mesh node in a rigged glTF did not make it into the import.</summary>
public enum GltfSkipReason
{
    /// <summary>Weighted to a skin other than the primary one. Blix reads one skin per file.</summary>
    SecondarySkin,

    /// <summary>Static, and not parented to a joint — so neither skinned geometry nor an attachment.</summary>
    UnparentedStatic,
}

/// <summary>
/// A mesh node the rigged import did not take, and why.
/// </summary>
/// <remarks>
/// <para>
/// <b>These used to be dropped in silence, which is the same fault attachments had.</b> The
/// importer takes nodes carrying the primary skin and skipped everything else with a bare
/// <c>continue</c> — so a file could arrive as a fraction of itself and every tool would agree it
/// was complete. Measured on <c>tank.glb</c>, which has three skins: eleven mesh primitives in the
/// file, five imported, six gone without a word.
/// </para>
/// <para>
/// <b>Reported rather than fixed, deliberately.</b> Reading more than one skin is a real capability
/// with real design questions behind it — one palette or several, one skeleton or a mapping between
/// them — and the 689-line Blender merge in <c>tools/character_merge.py</c> exists precisely to
/// avoid needing it. What is not defensible is the silence: a constraint nobody can see is
/// indistinguishable from an asset that had nothing there.
/// </para>
/// </remarks>
/// <param name="Name">The glTF node's name.</param>
/// <param name="Reason">Which rule excluded it.</param>
/// <param name="Primitives">How many primitives were lost with it.</param>
/// <param name="Vertices">How many vertices, so the size of the loss is legible.</param>
public sealed record GltfSkipped(string Name, GltfSkipReason Reason, int Primitives, int Vertices)
{
    /// <summary>The rule in words, because an enum name is not a sentence anyone can act on.</summary>
    /// <remarks>
    /// On the record rather than on the importer: whoever holds one of these is the one who has to
    /// explain it, and a tool should not have to keep its own table of reasons in step with this.
    /// </remarks>
    public string Explanation => Reason switch
    {
        GltfSkipReason.SecondarySkin => "weighted to a second skin; Blix reads one skin per file",
        _ => "static and not parented to a joint, so neither skin nor attachment",
    };
}

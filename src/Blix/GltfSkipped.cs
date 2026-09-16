namespace Blix;

/// <summary>Why a mesh node in a rigged glTF did not make it into the import.</summary>
public enum GltfSkipReason
{
    /// <summary>Static, and not parented to a joint — so neither skinned geometry nor an attachment.</summary>
    UnparentedStatic,
}

/// <summary>
/// A mesh node the rigged import did not take, and why.
/// </summary>
/// <remarks>
/// <para>
/// <b>These used to be dropped in silence, which is the same fault attachments had.</b> The
/// importer kept the nodes carrying one chosen skin and skipped everything else with a bare
/// <c>continue</c> — so a file could arrive as a fraction of itself and every tool would agree it
/// was complete. Measured on <c>tank.glb</c>, which has three skins: eleven mesh primitives in the
/// file, five imported, six gone without a word.
/// </para>
/// <para>
/// <b>Reporting was the first fix and reading was the second.</b> This record's original remarks
/// argued the second skins should stay unread because <c>tools/character_merge.py</c> "exists
/// precisely to avoid needing it" — and that script's header says it exists to satisfy the
/// importer's one-skin rule. The importer now reads every skin a file declares, so the only reason
/// left in this enum is a mesh under no joint at all: neither skinned geometry nor equipment, so
/// nothing takes it. Four of tank.glb's six lost primitives were that, and still are.
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
        _ => "static and not parented to a joint, so neither skin nor attachment",
    };
}

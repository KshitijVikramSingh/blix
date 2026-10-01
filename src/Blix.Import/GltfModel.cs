namespace Blix.Import;

// Bundle produced by GltfImporter. A single glTF file typically carries several
// related pieces (a mesh split into primitives, an optional skeleton, zero or more
// animation clips, plus the materials and textures the primitives reference);
// rather than splitting into separate asset registrations that all point at the
// same file, the importer emits one bundle and game code picks out what it needs.
//
// Multi-mesh skinned characters (body + hair + clothing sharing one skeleton)
// concatenate into a single `Primitives[]` array; one skeleton drives them all.
public sealed record GltfModel(
    GltfPrimitive[] Primitives,
    /// <summary>Shorthand for skin 0's skeleton, kept because most rigs declare exactly one.</summary>
    /// <remarks>
    /// A convenience, NOT a privileged skin — skin 0 is simply the first one met, and the order
    /// carries no other meaning. Anything that may meet a multi-skin file should read
    /// <see cref="SkinsOrEmpty"/> together with each primitive's <c>SkinIndex</c>.
    /// </remarks>
    Skeleton Skeleton,
    AnimationClip[] Animations,
    // Row-vector (F-016): where skin 0's skeleton hangs — the world of the node its root joint
    // hangs from (JointHierarchy). Most authored characters apply their axis correction
    // (Z-up → Y-up) at that parent; it goes after every bone world. Identity for a skeleton
    // whose roots are scene roots.
    System.Numerics.Matrix4x4 SkeletonPlacement,

    // Static meshes parented to joints — equipment, capes, anything the asset hangs off the
    // skeleton without skinning it. Empty for most rigs and not empty for any character that holds
    // something. Defaulted so every existing construction site is unchanged, because the importer
    // is the only thing that can fill it.
    GltfAttachment[]? Attachments = null,

    /// <summary>
    /// Static geometry hanging off no joint — a turret, a gun, anything the model carries that is
    /// neither skinned nor equipment.
    /// </summary>
    GltfStaticPart[]? StaticParts = null,
    /// <summary>Attributes the file declared that this importer did not read.</summary>
    UnreadAttribute[]? Ignored = null,

    /// <summary>
    /// Every skin the file declares, in the order the importer met them, with their inverse binds.
    /// </summary>
    /// <remarks>
    /// The singular <see cref="Skeleton"/> and <see cref="SkeletonPlacement"/> members are
    /// compatibility shorthands for skin 0. Multi-skin consumers should use this collection and
    /// each primitive's <c>SkinIndex</c>.
    /// </remarks>
    GltfSkinBinding[]? Skins = null)
{
    /// <summary>Attachments, never null.</summary>
    public GltfAttachment[] AttachmentsOrEmpty => Attachments ?? [];

    /// <summary>Static parts, never null.</summary>
    public GltfStaticPart[] StaticPartsOrEmpty => StaticParts ?? [];

    public UnreadAttribute[] IgnoredOrEmpty => Ignored ?? [];

    /// <summary>
    /// Every skin, never null. A consumer writes one loop and never branches on how many there are.
    /// </summary>
    /// <remarks>
    /// Empty when no skins were given: a skeleton alone carries no inverse binds, so a binding cannot be made
    /// up from <see cref="Skeleton"/>. The importer always gives its skins.
    /// </remarks>
    public GltfSkinBinding[] SkinsOrEmpty => Skins ?? [];
}

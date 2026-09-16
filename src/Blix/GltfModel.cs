namespace Blix;

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
    Skeleton Skeleton,
    AnimationClip[] Animations,
    // Row-vector model matrix (F-016) produced by walking the skin node's ancestor chain in
    // the source glTF. Most authored characters apply their axis-orientation
    // correction (Z-up → Y-up, etc.) at a parent node rather than per-vertex;
    // composing this matrix into uModel (`uModel = userTransform * MeshNodeTransform`)
    // is what makes the imported mesh come out oriented as the asset author
    // intended. Identity is a valid value for assets whose skin node sits at the
    // scene root with no ancestor transform.
    System.Numerics.Matrix4x4 MeshNodeTransform,

    // Static meshes parented to joints — equipment, capes, anything the asset hangs off the
    // skeleton without skinning it. Empty for most rigs and not empty for any character that holds
    // something. Defaulted so every existing construction site is unchanged, because the importer
    // is the only thing that can fill it.
    GltfAttachment[]? Attachments = null,

    // Mesh nodes this import did NOT take, and why. Empty for the ordinary one-skin character;
    // not empty for anything the importer's rules exclude, which used to leave no trace at all.
    GltfSkipped[]? Skipped = null,
    /// <summary>Attributes the file declared that this importer did not read.</summary>
    GltfIgnored[]? Ignored = null)
{
    /// <summary>Attachments, never null.</summary>
    public GltfAttachment[] AttachmentsOrEmpty => Attachments ?? [];

    /// <summary>What the import left behind, never null.</summary>
    public GltfSkipped[] SkippedOrEmpty => Skipped ?? [];

    public GltfIgnored[] IgnoredOrEmpty => Ignored ?? [];
}

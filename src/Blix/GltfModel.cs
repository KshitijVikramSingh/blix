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
    // Column-vector matrix produced by walking the skin node's ancestor chain in
    // the source glTF. Most authored characters apply their axis-orientation
    // correction (Z-up → Y-up, etc.) at a parent node rather than per-vertex;
    // composing this matrix into uModel (`uModel = userTransform * MeshNodeTransform`)
    // is what makes the imported mesh come out oriented as the asset author
    // intended. Identity is a valid value for assets whose skin node sits at the
    // scene root with no ancestor transform.
    System.Numerics.Matrix4x4 MeshNodeTransform);

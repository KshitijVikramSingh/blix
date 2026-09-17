using Blix.Assets;

namespace Blix;

// One primitive imported from a glTF mesh: its skinned vertex+index data plus the
// material it renders with. A glTF mesh node can split into N primitives — one per
// material assignment — and each becomes one of these. The caller turns each into a
// Submesh by uploading the MeshData and constructing a Material from the GltfMaterial.
//
// Material may be null if the source primitive had no material reference; callers
// substitute their default (white-tinted skin material in the demo).
public sealed record GltfPrimitive(
    MeshData Mesh,
    GltfMaterial? Material,
    // Which of GltfModel.Skins drives this primitive. 0 for every static import and for every
    // single-skin rig, which is why it defaults -- a primitive that never knew its skin was the
    // reason the importer had to choose one skin and discard the rest.
    int SkinIndex = 0,
    /// <summary>
    /// The source's own material index, or -1 when it declares none.
    /// </summary>
    /// <remarks>
    /// <b>Carried so a COOK can link a primitive to a material table without matching on names.</b>
    /// The importer resolves a material into an object, which is what a renderer wants and exactly
    /// what a writer cannot use: <c>.blixmesh</c> stores materials once and has primitives index
    /// them. Recovering that index by comparing names would be a guess, and two materials in one
    /// glTF may legitimately share a name.
    /// </remarks>
    int MaterialIndex = -1);

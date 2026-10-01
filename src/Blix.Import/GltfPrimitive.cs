using Blix.Assets;

namespace Blix.Import;

// One primitive imported from a glTF mesh: its skinned vertex+index data plus the
// material it renders with. A glTF mesh node can split into N primitives — one per
// material assignment — and each becomes one of these. The caller uploads the MeshData
// and builds its own material from the PbrMaterial.
//
// Material may be null if the source primitive had no material reference; callers
// substitute their default (white-tinted skin material in the demo).
public sealed record GltfPrimitive(
    MeshData Mesh,
    PbrMaterial? Material,
    // Which of GltfModel.Skins drives this primitive. 0 for every static import and for every
    // single-skin rig, which is why it defaults -- a primitive that never knew its skin was the
    // reason the importer had to choose one skin and discard the rest.
    int SkinIndex = 0,
    /// <summary>
    /// The source's own material index, or -1 when it declares none.
    /// </summary>
    /// <remarks>
    /// The cooker uses this source index to link the primitive to the deduplicated material table.
    /// Material names are not identities and may be shared within one glTF.
    /// </remarks>
    int MaterialIndex = -1);

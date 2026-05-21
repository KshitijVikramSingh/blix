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
    GltfMaterial? Material);

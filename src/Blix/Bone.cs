using System.Numerics;

namespace Blix;

// Per-bone immutable metadata. All fields are bind-time-constant — name, hierarchy,
// and the inverse-bind matrix don't change as the skeleton is posed; only the Pose
// carries per-frame state.
//
// Mirrors a glTF `Node` that appears in a `Skin.Joints` list, with two structural
// choices that diverge from SharpGLTF:
//
// - Hierarchy is `ParentIndex` (a flat-array index) rather than a parent reference.
//   Inner-loop traversal in ComputeBonePalette / CreateRestPose is a single forward
//   walk over the array; references would mean pointer chasing per bone.
//   `ParentIndex == -1` means root; otherwise the parent must appear earlier in the
//   array (the topology invariant Skeleton enforces at construction).
//
// - `InverseBindPose` lives on the Bone struct rather than in a parallel array on
//   the Skeleton. Bundles all bind-time-constant data together; the GPU consumes it
//   directly (no decomposition needed), so storing the matrix form is the natural
//   shape.
public readonly record struct Bone(
    string Name,
    int ParentIndex,
    Matrix4x4 InverseBindPose);

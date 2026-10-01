using System.Numerics;

namespace Blix;

// One bone of a pose hierarchy (Skeleton): its name, its parent, what it holds at rest, and the fixed
// transform between it and its parent. Immutable; only a Pose carries per-frame state.
//
// - Hierarchy is `ParentIndex` (a flat-array index) rather than a parent reference. Traversal is a single
//   forward walk over the array; `ParentIndex == -1` means root, otherwise the parent appears earlier (the
//   topology invariant Skeleton enforces at construction).
//
// - `Rest` is what the bone holds when no track moves it: glTF's joint node local transform. Required,
//   because nothing else can supply it. It used to be optional and rebuilt from inverse binds, but inverse
//   binds belong to a skin (SkinBinding), not to the hierarchy, and a file's rest need not be its bind.
//   A hand-built skeleton states its rest; identity is a rest like any other.
//
// - `Offset` is the fixed transform between this bone and its parent: the non-joint nodes glTF allows in
//   between (row-vector, nearest first), or, for a root, from its parent node to the hierarchy's placement.
//   Null is identity. A track writes the joint's own local, so the in-between nodes cannot be folded into
//   it; they are composed here instead.
//
// No inverse bind: the same bone can be a joint of two skins with different binds, and a bone that is no
// skin's joint has none at all. See SkinBinding.
public readonly record struct Bone(
    string Name,
    int ParentIndex,
    BoneTransform Rest,
    Matrix4x4? Offset = null);

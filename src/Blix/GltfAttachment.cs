using System.Numerics;

namespace Blix;

/// <summary>
/// A static mesh parented to a joint: a knife in a hand, a cape on a chest.
/// </summary>
/// <remarks>
/// <para>
/// <b>Before this existed the importer dropped these silently.</b> <c>Rogue.glb</c> is twelve
/// mesh-bearing nodes — six skinned and six not — and the rigged importer took nodes carrying both
/// a mesh and a skin, so half the file arrived as nothing, with no warning and no count. A
/// character with empty hands looks exactly like a character, which is why it went unnoticed across
/// several arcs of looking directly at it.
/// </para>
/// <para>
/// <b>Not skinned, and that is the whole point.</b> An attachment has its own vertices in its own
/// space and one joint that carries it — which is how equipment is authored everywhere, because it
/// is what lets a weapon be swapped without re-skinning a mesh. Its vertices are therefore stored
/// unbaked, in the node's own local space, and placed at draw time.
/// </para>
/// <para>
/// <b>Which attachment is shown is not the engine's business.</b> Five of the Rogue's six hang off
/// <c>handslot.r</c> and a game shows one; the importer reports all of them by name and draws
/// nothing on its own, the same way stage D hands the caller weights and no structure. An engine
/// that picked would be an equipment system.
/// </para>
/// </remarks>
/// <param name="Name">The glTF node's name, which is what a caller selects by.</param>
/// <param name="JointName">The joint it hangs from, for reporting and for checks that read.</param>
/// <param name="JointIndex">
/// Index into <see cref="Skeleton.Bones"/> — the <em>remapped</em> ordering, not the skin's.
/// <b>Those differ</b>: the skeleton is topologically sorted at import so parents precede children,
/// so an attachment carrying the glTF's own joint index would point at the wrong bone on any rig
/// whose joint list was not already sorted.
/// </param>
/// <param name="LocalTransform">
/// Row-vector transform from this node's space to the joint's, composed down the ancestor chain.
/// Usually the node's own local matrix, because equipment is usually parented straight to the joint.
/// </param>
/// <param name="Primitives">Its geometry and materials. A node may split into several.</param>
public sealed record GltfAttachment(
    string Name,
    string JointName,
    int JointIndex,
    Matrix4x4 LocalTransform,
    GltfPrimitive[] Primitives);

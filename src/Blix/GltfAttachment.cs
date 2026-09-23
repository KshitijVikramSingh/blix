using System.Numerics;

namespace Blix;

/// <summary>
/// A static mesh parented to a joint: a knife in a hand, a cape on a chest.
/// </summary>
/// <remarks>
/// <para>
/// An attachment is not skinned. It has its own vertices in its own
/// space and one joint that carries it — which is how equipment is authored everywhere, because it
/// is what lets a weapon be swapped without re-skinning a mesh. Its vertices are therefore stored
/// unbaked, in the node's own local space, and placed at draw time.
/// </para>
/// <para>
/// The importer returns every attachment by name and does not choose which one to show. Selection
/// and equipment policy belong to the application.
/// </para>
/// </remarks>
/// <param name="Name">The glTF node's name, which is what a caller selects by.</param>
/// <param name="JointName">The joint it hangs from, for reporting and for checks that read.</param>
/// <param name="JointIndex">
/// Index into <see cref="Skeleton.Bones"/> — the <em>remapped</em> ordering, not the skin's. The
/// skeleton is topologically sorted at import so parents precede children,
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
    GltfPrimitive[] Primitives,
    /// <summary>Which of <c>GltfModel.Skins</c> the <see cref="JointIndex"/> counts against.</summary>
    /// <remarks>
    /// A bone index means nothing without the skeleton it indexes, and a file may declare several.
    /// Joint WORLDS are shared by any skin using the same joint node — the inverse binds, which do
    /// differ between skins, play no part in placing an attachment — so this selects the bone array
    /// and nothing about the transform.
    /// </remarks>
    int SkinIndex = 0);

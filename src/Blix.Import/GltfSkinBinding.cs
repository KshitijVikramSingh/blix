using System.Numerics;

namespace Blix.Import;

/// <summary>
/// One glTF skin, as the source importer reads it: its joints as a skeleton of their own, bound directly
/// (joint i is bone i) with the skin's inverse binds, and the frame its meshes are authored in.
/// </summary>
/// <remarks>
/// <para>
/// A glTF skin carries its own joint list and its own inverse bind matrices, and every skinned node names
/// the skin it uses. The source importer gives each skin a skeleton of its own joints; the cooked reader
/// instead binds every skin into one animated hierarchy (<c>ModelData.Skeleton</c>). Either way the binds
/// are the binding's, never the skeleton's.
/// </para>
/// <para>
/// The placement belongs to the skin because independently placed skins may hang from different nodes.
/// </para>
/// </remarks>
/// <param name="Binding">The skin's joints, as bones of <see cref="Skeleton"/> in topological order, and their inverse binds.</param>
/// <param name="SkeletonPlacement">
/// Where the skeleton hangs: the world of the node its first root joint hangs from
/// (<see cref="JointHierarchy"/>). Goes after every bone world. Not the mesh nodes' world: glTF
/// ignores a skinned mesh node's transform, so any number of them, anywhere, may use one skin.
/// </param>
public sealed record GltfSkinBinding(SkinBinding Binding, Matrix4x4 SkeletonPlacement)
{
    /// <summary>The skin's own joint hierarchy (<see cref="SkinBinding.Skeleton"/>).</summary>
    public Skeleton Skeleton => Binding.Skeleton;
}

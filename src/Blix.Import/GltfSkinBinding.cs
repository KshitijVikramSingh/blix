using System.Numerics;

namespace Blix.Import;

/// <summary>
/// One glTF skin: the skeleton it defines, and the frame its meshes are authored in.
/// </summary>
/// <remarks>
/// <para>
/// A glTF skin is self-contained. It
/// carries its own joint list and its own inverse bind matrices, and every skinned node names the
/// skin it uses. Each skin therefore produces its own skeleton, palette, and binding index.
/// </para>
/// <para>
/// The placement belongs to the skin binding because independently placed skins may hang from
/// different nodes.
/// </para>
/// </remarks>
/// <param name="Skeleton">Bones in topological order, with this skin's own inverse bind matrices.</param>
/// <param name="SkeletonPlacement">
/// Where the skeleton hangs: the world of the node its first root joint hangs from
/// (<see cref="JointHierarchy"/>). Goes after every bone world. Not the mesh nodes' world — glTF
/// ignores a skinned mesh node's transform, so any number of them, anywhere, may use one skin.
/// </param>
public sealed record GltfSkinBinding(Skeleton Skeleton, Matrix4x4 SkeletonPlacement);

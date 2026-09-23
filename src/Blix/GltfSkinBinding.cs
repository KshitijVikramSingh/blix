using System.Numerics;

namespace Blix;

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
/// The mesh-node transform belongs to the skin binding because independently placed skins may use
/// different authored frames.
/// </para>
/// </remarks>
/// <param name="Skeleton">Bones in topological order, with this skin's own inverse bind matrices.</param>
/// <param name="MeshNodeTransform">
/// The shared world matrix of the mesh nodes this skin drives. Composed into the model matrix at
/// draw time rather than baked into vertices: the inverse binds map MESH-LOCAL vertices into joint
/// space, so baking it would put the skinning maths in the wrong frame.
/// </param>
public sealed record GltfSkinBinding(Skeleton Skeleton, Matrix4x4 MeshNodeTransform);

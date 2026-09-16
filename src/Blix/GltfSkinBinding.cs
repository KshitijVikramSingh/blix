using System.Numerics;

namespace Blix;

/// <summary>
/// One glTF skin: the skeleton it defines, and the frame its meshes are authored in.
/// </summary>
/// <remarks>
/// <para>
/// <b>A glTF skin is self-contained, and that is the fact the old one-skin limit obscured.</b> It
/// carries its own joint list and its own inverse bind matrices, and every skinned node names the
/// skin it uses. So N skins are N skeletons and N palettes — there is nothing to reconcile between
/// them and no mapping to invent. The importer used to pick the first skin it found and drop every
/// node referencing another, not because the format is hard but because
/// <see cref="GltfModel"/> had one <c>Skeleton</c> field and <see cref="GltfPrimitive"/> had no way
/// to say which skin it belonged to.
/// </para>
/// <para>
/// <b>The mesh-node transform belongs HERE rather than on the model.</b> It was singular, which held
/// only while there was one skin. <c>tank.glb</c> is the counterexample: its two track meshes sit
/// ±3.97 along Z from the hull, and each skin's inverse bind matrices encode that same offset —
/// 0.0397 in the other space, the two differing by exactly the 0.01 scale on the node. Hand a track
/// the hull's frame and it lands a fifth of the tank away, which is the kind of wrong that looks
/// like a physics bug rather than an import one.
/// </para>
/// </remarks>
/// <param name="Skeleton">Bones in topological order, with this skin's own inverse bind matrices.</param>
/// <param name="MeshNodeTransform">
/// The shared world matrix of the mesh nodes this skin drives. Composed into the model matrix at
/// draw time rather than baked into vertices: the inverse binds map MESH-LOCAL vertices into joint
/// space, so baking it would put the skinning maths in the wrong frame.
/// </param>
public sealed record GltfSkinBinding(Skeleton Skeleton, Matrix4x4 MeshNodeTransform);

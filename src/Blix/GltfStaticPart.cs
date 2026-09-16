using System.Numerics;

namespace Blix;

/// <summary>
/// Static geometry belonging to a rigged model that hangs off no joint.
/// </summary>
/// <remarks>
/// <para>
/// <b>The last thing the rigged importer used to throw away.</b> It took skinned nodes, adopted
/// static nodes parented to a joint as <see cref="GltfAttachment"/>s, and dropped the rest with a
/// recorded reason. <c>tank.glb</c> is the case: its gun and turret are four primitives sitting
/// under the scene root at their own transforms, animated by nothing and weighted to nothing. They
/// were named in the skipped report and left out of the model.
/// </para>
/// <para>
/// <b>Nothing in glTF excludes them.</b> A node with a mesh and no skin is an ordinary mesh in the
/// scene; "not equipment" was a rule this importer had rather than one the format has. So they are
/// read, placed by the world matrix their node already carries.
/// </para>
/// <para>
/// <b>Separate from <see cref="GltfAttachment"/> because the distinction is real.</b> An attachment
/// follows a joint and moves when the rig moves; this does not. Folding them together would need a
/// joint index meaning "no joint", which is a contradiction sitting in a field name — and the
/// viewer's attachment panel, where the meaningful act is choosing which weapon a hand holds, would
/// fill up with scenery.
/// </para>
/// </remarks>
/// <param name="Name">The glTF node's name.</param>
/// <param name="WorldTransform">
/// The node's composed world matrix. Absolute rather than joint-relative, because there is no joint
/// to be relative to — a consumer composes it with its own placement and nothing else.
/// </param>
/// <param name="Primitives">Its geometry and materials. A node may split into several.</param>
public sealed record GltfStaticPart(string Name, Matrix4x4 WorldTransform, GltfPrimitive[] Primitives);

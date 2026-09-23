using System.Numerics;

namespace Blix;

/// <summary>
/// Static geometry belonging to a rigged model that hangs off no joint.
/// </summary>
/// <remarks>
/// <para>
/// A node with a mesh and no skin is ordinary static scene geometry. It is placed by the world
/// matrix its node carries.
/// </para>
/// <para>
/// This is separate from <see cref="GltfAttachment"/> because an attachment
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

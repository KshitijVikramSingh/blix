using System.Numerics;
using Blix.Geometry;
using Blix.Graphics;

namespace Blix.Diagnostics;

/// <summary>A pickable entity, as a producer offers it to the overlay.</summary>
/// <param name="EntityPath">
/// The stable identity: the selection key, the inspection key (IDebugInspectable matches against it), and
/// the path the selection highlight is drawn under.
/// </param>
/// <param name="Bounds">World-space box, used only to draw the highlight. Picking never reads it.</param>
/// <param name="Geometry">What the pick pass draws to find out whether this is under the cursor.</param>
/// <param name="Label">What the Selection tab calls it. A path identifies; a name is what a person recognises.</param>
/// <remarks>
/// <b>Geometry, not a box, answers a click.</b> Picking used to ray-test these bounds, and a box says where a
/// thing might be, not where its surface is: Sponza's vaulted ceiling pieces have boxes that are mostly the
/// air under the vault, so a click on the tree through that air chose the ceiling, and a list of every box
/// on the ray was 52 long for a pixel that showed a tree against the sky. The pick pass draws the geometry
/// itself, which is how Unity, Unreal and Blender answer the same question.
/// </remarks>
public readonly record struct DebugSelectable(
    string EntityPath, Bounds3 Bounds, DebugPickGeometry Geometry, string? Label = null);

/// <summary>An indexed triangle range the pick pass can draw: where the vertices are, which of them, and where.</summary>
/// <param name="Vertices">The vertex buffer.</param>
/// <param name="Layout">Its layout. The pick pass reads the float3 attribute at <paramref name="PositionLocation"/> and nothing else.</param>
/// <param name="Indices">The index buffer, which knows its own index width.</param>
/// <param name="FirstIndex">The first index of the range.</param>
/// <param name="IndexCount">How many indices.</param>
/// <param name="BaseVertex">Added to each index, for a range out of a shared vertex buffer.</param>
/// <param name="Model">Object to world. Identity for geometry already in world space.</param>
/// <param name="PositionLocation">The layout location of the position.</param>
/// <remarks>
/// Positions only, so the limits are stated here rather than discovered: a cut-out leaf is picked as its
/// whole card, a skinned mesh in its rest pose, and geometry that only exists on the GPU (indirect records a
/// compute pass wrote, instances expanded in a shader) cannot be offered at all.
/// </remarks>
public readonly record struct DebugPickGeometry(
    VertexBufferHandle Vertices,
    VertexLayout Layout,
    IndexBufferHandle Indices,
    int FirstIndex,
    int IndexCount,
    int BaseVertex,
    Matrix4x4 Model,
    int PositionLocation = 0);

/// <summary>A click in pick mode, as the pick saw it.</summary>
/// <param name="View">The view the pointer was over.</param>
/// <param name="Pointer">The pointer, in the window's logical coordinates.</param>
/// <param name="Pixel">The physical pixel of the view's target that the pick pass rendered.</param>
/// <param name="Drawn">How many selectables the pass drew.</param>
/// <param name="Excluded">How many earlier hits at this spot were left out (clicking again steps behind them).</param>
/// <param name="Hit">What was under the cursor, or null for a miss.</param>
public sealed record DebugPick(string View, Vector2 Pointer, Vector2 Pixel, int Drawn, int Excluded, string? Hit);

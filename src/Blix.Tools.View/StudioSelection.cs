using System.Numerics;
using Blix.Core;
using Blix.Geometry;
using Blix.Tools.Studio;

namespace Blix.Tools.View;

/// <summary>
/// What is selected, and what a click through a view selects.
/// </summary>
/// <remarks>
/// <b>Local decomposition, not an engine extraction.</b> Only the viewer picks — the capture tool
/// has no pointer and the probe has no device — so this stays in the executable. The bar for moving
/// something into the lab library is a second consumer; the bar for making a 1,600-line file into
/// several is that the file had stopped being readable.
/// <para>
/// Two things are picked and they are genuinely different questions: a static model's NODE (against
/// the bounds the viewer already outlines) and a rig's JOINT (against the cross the overlay already
/// draws). Both follow the same rule — <b>what gets picked is what is drawn</b> — because picking
/// against geometry the viewer does not show selects things the picture cannot explain.
/// </para>
/// </remarks>
internal sealed class StudioSelection
{
    /// <summary>The selected node of a static model, or -1.</summary>
    public int Node { get; set; } = -1;

    /// <summary>The selected bone of a rig, or -1.</summary>
    public int Bone { get; set; } = -1;

    /// <summary>
    /// Picks through a view. Returns false when the pointer was not over it.
    /// </summary>
    /// <remarks>
    /// Returning false rather than silently doing nothing is what lets a caller ask each view in
    /// turn — "who owns this pointer?" is answered by exactly one for a non-overlapping layout.
    /// </remarks>
    public bool PickThrough(
        in ViewDeclaration view,
        Vector2 pointer,
        StudioRig? rig,
        Matrix4x4 rigPlacement,
        IReadOnlyList<Matrix4x4> boneWorlds,
        IReadOnlyList<bool>? boneFilter,
        float gizmoScale,
        StudioModel? model,
        Matrix4x4 modelTransform)
    {
        if (ViewPicking.RayThrough(view, pointer) is not { } ray) return false;

        if (rig is not null)
        {
            Bone = PickBone(ray, rig, rigPlacement, boneWorlds, boneFilter, gizmoScale);
            return true;
        }

        if (model is not null) Node = PickNode(ray, model, modelTransform);
        return true;
    }

    /// <summary>Nearest node whose drawn bounds the ray enters; -1 for empty space.</summary>
    /// <remarks>
    /// Bounds rather than triangles, deliberately: a node's AABB is what the lab computes and draws,
    /// so what gets picked is exactly what is outlined. Triangle-accurate picking is a different
    /// question and no consumer has asked it.
    /// </remarks>
    private static int PickNode(Ray ray, StudioModel model, Matrix4x4 modelTransform)
    {
        var best = -1;
        var nearest = float.MaxValue;
        for (var i = 0; i < model.Nodes.Count; i++)
        {
            var node = model.Nodes[i];
            if (node.PrimitiveCount == 0) continue;

            var lo = Vector3.Transform(node.BoundsMin, modelTransform);
            var hi = Vector3.Transform(node.BoundsMax, modelTransform);
            var bounds = new Bounds3(Vector3.Min(lo, hi), Vector3.Max(lo, hi));

            if (Intersection.Raycast(ray, bounds) is not { } hit) continue;
            if (hit.Time >= nearest) continue;
            nearest = hit.Time;
            best = i;
        }

        // Clicking empty space clears, which is what every viewport does and what a reader expects.
        return best;
    }

    /// <summary>Nearest joint whose drawn cross the ray enters; -1 for empty space.</summary>
    /// <remarks>
    /// Against the cross, not the skinned triangles — picking the mesh would be more "accurate" and
    /// would select bones the picture cannot explain, an elbow through a sleeve. Hidden bones are
    /// unpickable for the same reason: with IK controls filtered out, a click near the feet must not
    /// select <c>control-heel-roll.r</c>.
    /// </remarks>
    private static int PickBone(
        Ray ray,
        StudioRig rig,
        Matrix4x4 placement,
        IReadOnlyList<Matrix4x4> boneWorlds,
        IReadOnlyList<bool>? filter,
        float gizmoScale)
    {
        var place = rig.MeshNodeTransform * placement;
        var span = SkeletonGizmo.Span(rig.Skeleton, boneWorlds, place, filter);

        // Twice the joint cross's own arm, so a click has to be close but not surgical — the same
        // slack the four-pixel click/drag threshold grants the gesture one layer up.
        var reach = MathF.Max(0.005f, span * 0.012f) * gizmoScale * 2f;

        var best = -1;
        var nearest = float.MaxValue;
        for (var i = 0; i < rig.Skeleton.BoneCount; i++)
        {
            if (filter is not null && (i >= filter.Count || !filter[i])) continue;

            var world = boneWorlds[i] * place;
            var at = new Vector3(world.M41, world.M42, world.M43);
            var bounds = new Bounds3(at - new Vector3(reach), at + new Vector3(reach));
            if (Intersection.Raycast(ray, bounds) is not { } hit) continue;
            if (hit.Time >= nearest) continue;
            nearest = hit.Time;
            best = i;
        }

        return best;
    }
}

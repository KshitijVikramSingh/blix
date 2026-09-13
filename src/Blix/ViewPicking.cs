using System.Numerics;
using Blix.Core;
using Blix.Geometry;

namespace Blix;

/// <summary>
/// Turns a pointer position into a ray through a named view.
/// </summary>
/// <remarks>
/// <b>The engine could not do this until views existed.</b> <c>IDebugSelectable</c> still carries the
/// reason in its header — "the runtime never picks for the demo — it doesn't know about cameras or screen
/// space" — which was true when it was written and is not any more. A <see cref="ViewDeclaration"/> is
/// exactly a camera and a screen rectangle with a name on it.
/// <para>
/// <b>What this does that <see cref="Camera3D.ScreenPointToRay"/> cannot.</b> That method takes a viewport
/// width and height and RECOMPUTES the view-projection from their aspect ratio, which quietly assumes two
/// things: that the view fills the window, and that its matrix is the one the camera would derive. Neither
/// holds for a view drawn into a panel — an inspector viewport, a split screen, a picture-in-picture — and
/// both are exactly what a view was made first-class to allow. This reads the view's OWN matrix and its own
/// rectangle, so a picture drawn anywhere can be picked anywhere.
/// </para>
/// <para>
/// <b>It is not a scene, and does not want to be.</b> It answers where a click points, not what is there.
/// What exists in a world, how that world is stored, and what selecting something means are the
/// application's business — and the two applications in this tree that pick things disagree about all three
/// already.
/// </para>
/// </remarks>
public static class ViewPicking
{
    /// <summary>
    /// The ray through <paramref name="pointer"/>, or null when the pointer is not over this view.
    /// </summary>
    /// <remarks>
    /// <paramref name="pointer"/> is in the window's logical coordinates — what the input layer reports —
    /// and is compared against <see cref="ViewDeclaration.LogicalViewport"/>. The physical rectangle is for
    /// the renderer; mixing the two is the retina bug every application has had a chance to write, which is
    /// why a view carries both and neither is derived at the call site.
    /// <para>
    /// Returning null for a pointer outside the rectangle is also how "which view is the pointer over?"
    /// gets answered: ask each declared view, and at most one says yes for a non-overlapping layout.
    /// Overlapping panels are an ordering question, which is the caller's to answer.
    /// </para>
    /// </remarks>
    public static Ray? RayThrough(in ViewDeclaration view, Vector2 pointer)
    {
        var rect = view.LogicalViewport;
        if (rect.Width <= 0f || rect.Height <= 0f) return null;

        // <b>Half-open on the far edges.</b> With an inclusive test two abutting views both claim the
        // pixel on their shared boundary, which quietly contradicts the one useful thing about returning
        // null — that asking every view answers "who owns this pointer?" with exactly one yes. [x, x+w)
        // is also the convention every other pixel rectangle in the stack already uses.
        var localX = pointer.X - rect.X;
        var localY = pointer.Y - rect.Y;
        if (localX < 0f || localY < 0f || localX >= rect.Width || localY >= rect.Height) return null;

        // Screen -> Vulkan NDC. NDC y points down, same as screen y — no flip, matching Camera3D.
        var ndcX = 2.0f * localX / rect.Width - 1.0f;
        var ndcY = 2.0f * localY / rect.Height - 1.0f;

        if (!Matrix4x4.Invert(view.ViewProjection, out var inverse)) return null;

        // Vulkan depth range is [0, 1], so the near plane is z = 0 and the far plane z = 1.
        var near = Unproject(inverse, ndcX, ndcY, 0.0f);
        var far = Unproject(inverse, ndcX, ndcY, 1.0f);

        var direction = far - near;
        var length = direction.Length();
        if (length <= float.Epsilon) return null;

        // <b>Invertibility is not enough.</b> A matrix can invert cleanly and still send a particular
        // corner of the NDC cube to w = 0 — a point on the eye plane, which has no finite position. The
        // divide below would then hand back infinities that survive every later test and land as a ray
        // pointing nowhere, which is exactly the shape of bug that gets diagnosed as "the mouse is
        // offset" three hours later.
        var ray = new Ray(near, direction / length);
        return IsFinite(ray.Origin) && IsFinite(ray.Direction) ? ray : null;
    }

    private static Vector3 Unproject(Matrix4x4 inverseViewProjection, float ndcX, float ndcY, float ndcZ)
    {
        var p = Vector4.Transform(new Vector4(ndcX, ndcY, ndcZ, 1.0f), inverseViewProjection);
        if (p.W == 0f || !float.IsFinite(p.W)) return new Vector3(float.NaN);
        return new Vector3(p.X, p.Y, p.Z) / p.W;
    }

    private static bool IsFinite(Vector3 v) =>
        float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);
}

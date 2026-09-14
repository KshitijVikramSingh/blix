using System.Numerics;
using Blix.Geometry;

namespace Blix.Labs.Character;

/// <summary>
/// Accumulates a room's solids as flat-shaded triangles in WORLD space.
/// </summary>
/// <remarks>
/// <para>
/// <b>World space, because that is what the collider is.</b> <see cref="TriangleMesh3D"/> stores its
/// vertices baked, with no per-mesh transform — so a room built anywhere else would need a transform
/// applied twice, once for the picture and once for the collider, and those are the two things this
/// stage exists to keep from disagreeing. There are no model matrices in this lab at all.
/// </para>
/// <para>
/// <b>Flat-shaded, on purpose.</b> Smooth normals would draw a dome the collider does not have. Every
/// facet you can see here is a facet the sweep will actually hit, which is the whole reason a physics
/// room is worth looking at.
/// </para>
/// <para>
/// <b>Every face states its outward normal, rather than deriving one.</b> The winding is what the
/// collider will read a contact normal out of, so a face whose winding disagrees with the direction it
/// claims to face is a bug that lights correctly and pushes a body the wrong way. Stating both makes
/// them checkable against each other — and <c>Blix.Labs.Character.Probe</c> checks every triangle in
/// the room. Deriving the normal from the winding would have made that check vacuous.
/// </para>
/// </remarks>
public sealed class SolidBuilder
{
    private readonly List<Vector3> positions = new();
    private readonly List<Vector3> normals = new();
    private readonly List<int> solidStarts = new();

    /// <summary>Triangle count so far. Parts are index ranges into one shared buffer.</summary>
    public int TriangleCount => positions.Count / 3;

    public IReadOnlyList<Vector3> Positions => positions;

    public IReadOnlyList<Vector3> Normals => normals;

    /// <summary>First triangle of each closed solid, in order.</summary>
    /// <remarks>
    /// <b>Closure is a property of a SOLID, not of a part.</b> The probe found this rather than the
    /// author: the four wall boxes meet at the hall's corners and share a vertical edge exactly, so
    /// that edge belongs to four triangles and a per-part check called the walls open. They are not
    /// open — each box is closed and they touch. Same for a flight of stairs, where all six boxes
    /// share the bottom edge at the far end. Recording where each solid begins is what lets the check
    /// ask the question it meant to ask.
    /// </remarks>
    public IReadOnlyList<int> SolidStarts => solidStarts;

    /// <summary>Begins a new closed solid at the next triangle.</summary>
    private void BeginSolid() => solidStarts.Add(TriangleCount);

    /// <summary>One triangle, wound so that <paramref name="outward"/> is its front face.</summary>
    public void Triangle(Vector3 a, Vector3 b, Vector3 c, Vector3 outward)
    {
        var n = Vector3.Normalize(outward);
        positions.Add(a); positions.Add(b); positions.Add(c);
        normals.Add(n); normals.Add(n); normals.Add(n);
    }

    /// <summary>A planar quad a→b→c→d, split into two triangles.</summary>
    public void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d, Vector3 outward)
    {
        Triangle(a, b, c, outward);
        Triangle(a, c, d, outward);
    }

    /// <summary>An axis-aligned closed box.</summary>
    /// <remarks>
    /// Closed matters: an open box is a surface a body can end up inside, and "inside a solid" is the
    /// one state a sweep-and-slide resolver has no good answer for. The probe checks each part is a
    /// closed manifold — every edge shared by exactly two triangles — which is a property this method
    /// has and a stray quad does not.
    /// </remarks>
    public void Box(Vector3 min, Vector3 max)
    {
        BeginSolid();

        var (x0, y0, z0) = (min.X, min.Y, min.Z);
        var (x1, y1, z1) = (max.X, max.Y, max.Z);

        Quad(new(x0, y1, z1), new(x1, y1, z1), new(x1, y1, z0), new(x0, y1, z0), Vector3.UnitY);
        Quad(new(x0, y0, z0), new(x1, y0, z0), new(x1, y0, z1), new(x0, y0, z1), -Vector3.UnitY);
        Quad(new(x0, y0, z1), new(x1, y0, z1), new(x1, y1, z1), new(x0, y1, z1), Vector3.UnitZ);
        Quad(new(x1, y0, z0), new(x0, y0, z0), new(x0, y1, z0), new(x1, y1, z0), -Vector3.UnitZ);
        Quad(new(x1, y0, z1), new(x1, y0, z0), new(x1, y1, z0), new(x1, y1, z1), Vector3.UnitX);
        Quad(new(x0, y0, z0), new(x0, y0, z1), new(x0, y1, z1), new(x0, y1, z0), -Vector3.UnitX);
    }

    /// <summary>
    /// A ramp: a right-triangular prism whose foot is at <paramref name="foot"/> and which rises
    /// toward <paramref name="upSlope"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Directional, because the first version could only rise along +X</b> — which meant every
    /// ramp's walkable face looked west, and the room had to be arranged around that rather than
    /// around what is worth looking at. A ramp you approach from the wrong side is a ramp nobody
    /// climbs.
    /// </para>
    /// <para>
    /// The wedge is built in its own frame — <c>u</c> up-slope, <c>+Y</c>, <c>w = cross(u, Y)</c>
    /// across — and mapped out. That map is a ROTATION, not a reflection, so every winding derived
    /// for the axis-aligned case still holds; mirroring the coordinates instead would reverse the
    /// orientation of every face and turn the solid inside out, which the probe would catch and
    /// nothing else would.
    /// </para>
    /// <para>
    /// The rise is the caller's, not an angle, because a run and a rise are exact in binary and an
    /// angle is not — <c>2.5 * tan(30°)</c> is 1.4433757 and the probe would then be checking a
    /// float against its own rounding. The angle is derived from them for the claim, so the claim is
    /// exactly what the geometry is.
    /// </para>
    /// </remarks>
    public void Ramp(Vector3 foot, Vector3 upSlope, float run, float rise, float width)
    {
        BeginSolid();

        var u = Vector3.Normalize(new Vector3(upSlope.X, 0f, upSlope.Z));
        var w = Vector3.Cross(u, Vector3.UnitY);
        var h = width * 0.5f;

        Vector3 P(float alongSlope, float up, float across) =>
            foot + (u * alongSlope) + (Vector3.UnitY * up) + (w * across);

        // The inclined face — the only walkable one, and the only one with a slope claim.
        var slopeNormal = Vector3.Normalize((u * -rise) + (Vector3.UnitY * run));
        Quad(P(0f, 0f, h), P(run, rise, h), P(run, rise, -h), P(0f, 0f, -h), slopeNormal);

        Quad(P(run, 0f, -h), P(run, rise, -h), P(run, rise, h), P(run, 0f, h), u);
        Quad(P(0f, 0f, -h), P(run, 0f, -h), P(run, 0f, h), P(0f, 0f, h), -Vector3.UnitY);
        Triangle(P(0f, 0f, h), P(run, 0f, h), P(run, rise, h), w);
        Triangle(P(0f, 0f, -h), P(run, rise, -h), P(run, 0f, -h), -w);
    }

    /// <summary>
    /// A spherical cap standing on <paramref name="yBase"/>, closed by a disc underneath.
    /// </summary>
    /// <remarks>
    /// The one curved thing in the room, and the only surface whose slope varies continuously: at
    /// horizontal distance r from the apex axis the surface is <c>asin(r / radius)</c> from flat, which
    /// is a ground truth in closed form rather than a number read off the mesh. A slope limit that is
    /// applied per-triangle passes a stair and can still fail here, where the body crosses the limit
    /// mid-step rather than at an edge.
    /// </remarks>
    public void Dome(Vector3 centre, float radius, float yBase, int rings = 8, int segments = 24)
    {
        BeginSolid();

        Vector3 On(int ring, int seg)
        {
            var phi = MathF.PI * 0.5f * ring / rings;          // 0 at the equator, pi/2 at the apex
            // seg wraps rather than running to `segments`, so the seam's two sides are the SAME float
            // rather than cos(tau) and cos(0) — which differ, and would leave a hairline crack the
            // manifold check reports and a sweep can fall through.
            var theta = MathF.Tau * (seg % segments) / segments;
            var r = radius * MathF.Cos(phi);
            return new Vector3(centre.X + r * MathF.Cos(theta), yBase + radius * MathF.Sin(phi), centre.Z + r * MathF.Sin(theta));
        }

        // Outward is radial from the cap's centre of curvature, which sits ON yBase — analytic, and
        // therefore independent of the winding it is checked against.
        var origin = new Vector3(centre.X, yBase, centre.Z);
        Vector3 Outward(Vector3 p) => Vector3.Normalize(p - origin);

        for (var ring = 0; ring < rings; ring++)
        {
            for (var seg = 0; seg < segments; seg++)
            {
                var a = On(ring, seg);
                var b = On(ring, seg + 1);
                var c = On(ring + 1, seg + 1);
                var d = On(ring + 1, seg);

                // The top ring degenerates to the apex: b and c collapse onto the same point, so it is
                // one triangle rather than a quad with a zero-area half. A degenerate triangle has no
                // normal, and a collider full of them reports contacts with NaN normals.
                if (ring == rings - 1)
                {
                    Triangle(a, d, b, Outward((a + b + d) / 3f));
                    continue;
                }

                Quad(a, d, c, b, Outward((a + b + c + d) / 4f));
            }
        }

        for (var seg = 0; seg < segments; seg++)
        {
            Triangle(origin, On(0, seg), On(0, seg + 1), -Vector3.UnitY);
        }
    }
}

using System.Numerics;
using Blix.Geometry;
using Blix.Labs.Character;

// The character lab's probe: it CHECKS, and exits non-zero when the room is not what it claims.
//
// The pattern the toolchain probe established, pointed at geometry instead of a binding model. No
// window, no device, no launcher — the room's ground truth is closed form, so verifying it is
// arithmetic. `blix-cook inspect` lists what is in a file and always exits 0; this judges.
//
// WHY A ROOM NEEDS JUDGING AT ALL. Every number here was authored, so "it looks like a ramp" is not
// the question — the question is whether the ramp the sweep will hit is the 30 degrees the claim
// says. A resolver that comes to rest at 31 and one that slides at 29 look identical in a
// screenshot, and the difference is the entire slope-limit stage.

var room = Room.Build();
var t = new ProbeRunner();

Console.WriteLine($"room — {room.TriangleCount} triangles across {room.Parts.Count} part(s)");
Console.WriteLine();

// ── Every triangle, whatever part it is in ──────────────────────────────────────────────────────
{
    var degenerate = 0;
    var wrongWound = 0;
    var worstDot = 1f;

    for (var i = 0; i < room.TriangleCount; i++)
    {
        var tri = room.TriangleAt(i);
        var cross = Vector3.Cross(tri.V1 - tri.V0, tri.V2 - tri.V0);
        if (cross.Length() < 1e-9f) { degenerate++; continue; }

        // THE CHECK THAT PAYS FOR STATING NORMALS SEPARATELY. The builder declares which way each
        // face points; the collider will read a contact normal out of the WINDING. A face where
        // those two disagree lights correctly and pushes a body into the surface it just hit.
        var dot = Vector3.Dot(room.WoundNormal(i), room.ClaimedNormal(i));
        worstDot = MathF.Min(worstDot, dot);
        if (dot < 0.999f) wrongWound++;
    }

    t.Expect("no degenerate triangles", degenerate == 0, $"{degenerate} with zero area");
    t.Expect("every winding agrees with its declared normal", wrongWound == 0,
        $"{wrongWound} disagree, worst dot {worstDot:0.0000}");
}

// ── One source: the collider IS the render vertices ─────────────────────────────────────────────
{
    var mismatched = 0;
    for (var i = 0; i < room.TriangleCount; i++)
    {
        var collider = room.Collider.Triangles[i];
        var drawn = room.TriangleAt(i);
        if (collider != drawn) mismatched++;

        var v = room.Vertices[i * 3];
        if (MathF.Abs(v.Position.X - drawn.V0.X) > 0f ||
            MathF.Abs(v.Position.Y - drawn.V0.Y) > 0f ||
            MathF.Abs(v.Position.Z - drawn.V0.Z) > 0f)
        {
            mismatched++;
        }
    }

    t.Expect("the collider is the drawn geometry, triangle for triangle", mismatched == 0,
        $"{mismatched} differ");
    t.Expect("and there is one of each", room.Collider.Count == room.TriangleCount,
        $"{room.Collider.Count} collider vs {room.TriangleCount} drawn");
}

// ── Every solid is closed ───────────────────────────────────────────────────────────────────────
//
// Every edge shared by exactly two triangles. An open solid is a surface a body can end up INSIDE,
// and "inside a solid" is the one state a sweep-and-slide resolver has no good answer for — it will
// happily push out through whichever face it finds first, which may be the far side.
//
// PER SOLID, and the first version of this got it wrong at the room's expense. Asking it per PART
// called the walls open: the four wall boxes meet at the hall's corners and share a vertical edge
// exactly, so that edge has four triangles on it. Same for a flight of stairs, whose six boxes all
// share the bottom edge at the far end. Nothing was open — the question was.
{
    var openSolids = 0;
    var worstEdges = 0;
    for (var s = 0; s < room.SolidStarts.Count; s++)
    {
        var first = room.SolidStarts[s];
        var end = s + 1 < room.SolidStarts.Count ? room.SolidStarts[s + 1] : room.TriangleCount;

        var edges = new Dictionary<(Key, Key), int>();
        for (var i = first; i < end; i++)
        {
            var tri = room.TriangleAt(i);
            AddEdge(edges, tri.V0, tri.V1);
            AddEdge(edges, tri.V1, tri.V2);
            AddEdge(edges, tri.V2, tri.V0);
        }

        var open = edges.Count(e => e.Value != 2);
        if (open == 0) continue;
        openSolids++;
        worstEdges = Math.Max(worstEdges, open);
    }

    t.Expect($"all {room.SolidStarts.Count} solids are closed (every edge shared by exactly two triangles)",
        openSolids == 0, $"{openSolids} solid(s) open, worst with {worstEdges} unpaired edges");
}

// ── The claims, one feature at a time ───────────────────────────────────────────────────────────
foreach (var part in room.Parts)
{
    if (part.WalkSlopeDegrees is { } claimed)
    {
        var worst = 0f;
        var offenders = 0;
        var walkable = 0;
        for (var i = part.FirstTriangle; i < part.FirstTriangle + part.TriangleCount; i++)
        {
            var n = room.WoundNormal(i);
            if (n.Y <= 1e-4f) continue;        // vertical or downward: not a surface anything stands on
            walkable++;
            var slope = Room.SlopeDegrees(n);
            var error = MathF.Abs(slope - claimed);
            worst = MathF.Max(worst, error);
            if (error > 0.01f) offenders++;
        }

        t.Expect($"{part.Name}: every walkable face is {claimed:0.##}°", offenders == 0 && walkable > 0,
            walkable == 0 ? "no walkable faces at all" : $"{offenders} off, worst by {worst:0.###}°");
    }

    if (part.RiserHeight is { } riser)
    {
        var levels = new SortedSet<float>();
        for (var i = part.FirstTriangle; i < part.FirstTriangle + part.TriangleCount; i++)
        {
            var n = room.WoundNormal(i);
            if (n.Y < 0.999f) continue;        // treads only
            levels.Add(MathF.Round(room.TriangleAt(i).V0.Y, 4));
        }

        var heights = levels.ToArray();
        var worst = 0f;
        for (var i = 1; i < heights.Length; i++) worst = MathF.Max(worst, MathF.Abs(heights[i] - heights[i - 1] - riser));

        t.Expect($"{part.Name}: {heights.Length} treads, each {riser:0.00} m above the last",
            heights.Length == 6 && worst < 1e-4f, $"{heights.Length} treads, worst step off by {worst:0.00000}");
    }

    if (part.ClearWidth is { } clear)
    {
        // The gap is two solids; the clear distance is the nearest approach between them. Measured
        // off the geometry rather than restated, so moving a wall moves the claim with it.
        var mid = part.FirstTriangle + part.TriangleCount / 2;
        var a = SpanOf(room, part.FirstTriangle, mid);
        var b = SpanOf(room, mid, part.FirstTriangle + part.TriangleCount);
        var measured = MathF.Max(b.MinZ - a.MaxZ, a.MinZ - b.MaxZ);

        t.Expect($"{part.Name}: {clear:0.00} m clear between the two solids",
            MathF.Abs(measured - clear) < 1e-4f, $"measured {measured:0.0000}");
    }

    if (part.TopHeight is { } top)
    {
        var span = SpanOf(room, part.FirstTriangle, part.FirstTriangle + part.TriangleCount);
        var measured = part.Name == "beam" ? span.MinY : span.MaxY;
        t.Expect($"{part.Name}: {(part.Name == "beam" ? "underside" : "top")} at {top:0.00} m",
            MathF.Abs(measured - top) < 1e-4f, $"measured {measured:0.0000}");
    }

    if (part.CurvatureRadius is { } radius)
    {
        // THE ONLY SURFACE WHOSE SLOPE VARIES CONTINUOUSLY, and the three things that makes checkable.
        //
        // The first version asked whether each facet's slope equalled asin(r/R) at its CENTROID, and
        // it was wrong in a way worth keeping: a flat facet's centroid sits INSIDE the sphere, so r/R
        // there understates the true angle systematically — the error was one-sided and grew with
        // latitude, which is the signature of a bad estimator rather than bad geometry. The vertices
        // are on the surface; the centroid is not.
        var offSphere = 0;
        var worstRadius = 0f;
        var slopeOffenders = 0;
        var worstSlope = 0f;
        var maxSlope = 0f;
        var monotonic = true;
        var previousSlope = -1f;
        var previousRadius = -1f;

        for (var i = part.FirstTriangle; i < part.FirstTriangle + part.TriangleCount; i++)
        {
            var tri = room.TriangleAt(i);
            var n = room.WoundNormal(i);
            if (n.Y <= 1e-3f) continue;                      // the base disc faces down

            // Every cap vertex is on the sphere. This checks the trig that placed it, and it is what
            // makes the slope claim below analytic rather than circular.
            var meanRadius = 0f;
            var lowest = float.MaxValue;
            var highest = float.MinValue;
            foreach (var v in new[] { tri.V0, tri.V1, tri.V2 })
            {
                var d = (v - part.Centre).Length();
                worstRadius = MathF.Max(worstRadius, MathF.Abs(d - radius));
                if (MathF.Abs(d - radius) > 1e-3f) offSphere++;

                var rv = new Vector2(v.X - part.Centre.X, v.Z - part.Centre.Z).Length();
                meanRadius += rv / 3f;
                var atVertex = MathF.Asin(Math.Clamp(rv / radius, 0f, 1f)) * 180f / MathF.PI;
                lowest = MathF.Min(lowest, atVertex);
                highest = MathF.Max(highest, atVertex);
            }

            // BRACKETED BY ITS OWN CORNERS, rather than measured against a tolerance. A flat facet's
            // normal is a blend of the true surface normals at its corners, so its slope must lie
            // between the steepest and shallowest of them — exactly, with no tessellation fudge. The
            // second version of this check used the mean corner radius and a 3° tolerance, and the
            // apex ring failed it by 3.6°: those triangles reach from the pole (r = 0) to the last
            // ring, so their mean is a bad estimator of anything. The bracket is what the geometry
            // actually promises, and it is TIGHTER than the tolerance was over most of the cap.
            var slope = Room.SlopeDegrees(n);
            var outside = MathF.Max(lowest - slope, slope - highest);
            worstSlope = MathF.Max(worstSlope, outside);
            if (outside > 0.5f) slopeOffenders++;

            maxSlope = MathF.Max(maxSlope, slope);
            if (previousRadius >= 0f && meanRadius > previousRadius + 1e-3f && slope < previousSlope - 1e-2f)
            {
                monotonic = false;
            }
            previousRadius = meanRadius;
            previousSlope = slope;
        }

        t.Expect($"{part.Name}: every cap vertex is on the sphere of radius {radius:0.#}", offSphere == 0,
            $"{offSphere} vertices off, worst by {worstRadius:0.00000}");
        t.Expect($"{part.Name}: every facet's slope is bracketed by asin(r/{radius:0.#}) at its corners",
            slopeOffenders == 0, $"{slopeOffenders} facets outside their bracket, worst by {worstSlope:0.##}°");

        // The two properties that make a dome an instrument rather than decoration: slope rises with
        // distance from the axis, and the cap crosses any slope limit anyone would plausibly set —
        // so a body walking off it passes through the limit mid-surface rather than at an edge.
        t.Expect($"{part.Name}: slope rises with distance from the axis", monotonic, "a ring bucked the trend");
        t.Expect($"{part.Name}: the cap reaches past 80° at its foot", maxSlope > 80f, $"steepest is {maxSlope:0.#}°");
    }
}

// ── Nothing has escaped the hall ────────────────────────────────────────────────────────────────
{
    var all = SpanOf(room, 0, room.TriangleCount);
    t.Expect("every triangle is inside the hall",
        all.MinX >= -Room.HallHalfX - 0.75f && all.MaxX <= Room.HallHalfX + 0.75f &&
        all.MinZ >= -Room.HallHalfZ - 0.75f && all.MaxZ <= Room.HallHalfZ + 0.75f,
        $"x [{all.MinX:0.##}, {all.MaxX:0.##}] z [{all.MinZ:0.##}, {all.MaxZ:0.##}]");

    // The spawn point has to be somewhere a body can actually stand, or every run of the room starts
    // with a bug that is not the resolver's.
    var spawn = Room.SpawnPoint;
    var clear = true;
    for (var i = 0; i < room.TriangleCount; i++)
    {
        var c = Centroid(room.TriangleAt(i));
        if (c.Y > 0.05f && new Vector2(c.X - spawn.X, c.Z - spawn.Z).Length() < 1.0f) clear = false;
    }
    t.Expect("the spawn point is clear of every solid", clear, $"something stands at {spawn}");
}

t.PrintSummary();
return t.Failed;

static void AddEdge(Dictionary<(Key, Key), int> edges, Vector3 a, Vector3 b)
{
    var ka = new Key(a);
    var kb = new Key(b);
    var key = ka.CompareTo(kb) <= 0 ? (ka, kb) : (kb, ka);
    edges[key] = edges.TryGetValue(key, out var n) ? n + 1 : 1;
}

static Vector3 Centroid(Triangle tri) => (tri.V0 + tri.V1 + tri.V2) / 3f;

static Span3 SpanOf(Room room, int firstTriangle, int endTriangle)
{
    var min = new Vector3(float.MaxValue);
    var max = new Vector3(float.MinValue);
    for (var i = firstTriangle * 3; i < endTriangle * 3; i++)
    {
        min = Vector3.Min(min, room.Positions[i]);
        max = Vector3.Max(max, room.Positions[i]);
    }
    return new Span3(min.X, max.X, min.Y, max.Y, min.Z, max.Z);
}

readonly record struct Span3(float MinX, float MaxX, float MinY, float MaxY, float MinZ, float MaxZ);

/// <summary>A vertex position as an exact key. Quantised, because an edge shared by two faces has to
/// hash the same from both — and two faces built from the same corner floats do produce the same
/// bits, so the quantisation is a guard rather than a fudge.</summary>
readonly record struct Key(int X, int Y, int Z) : IComparable<Key>
{
    public Key(Vector3 v) : this(Q(v.X), Q(v.Y), Q(v.Z)) { }

    private static int Q(float f) => (int)MathF.Round(f * 8192f);

    public int CompareTo(Key other)
    {
        var c = X.CompareTo(other.X);
        if (c != 0) return c;
        c = Y.CompareTo(other.Y);
        return c != 0 ? c : Z.CompareTo(other.Z);
    }
}

sealed class ProbeRunner
{
    private int passed;

    public int Failed { get; private set; }

    public void Expect(string label, bool condition, string detail)
    {
        if (condition) { Console.WriteLine($"  OK   {label}"); passed++; return; }
        Console.WriteLine($"  FAIL {label} — {detail}");
        Failed++;
    }

    public void PrintSummary()
    {
        Console.WriteLine();
        Console.WriteLine($"{passed}/{passed + Failed} passed, {Failed} failed");
    }
}

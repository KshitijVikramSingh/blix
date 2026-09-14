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

// ── The sweep meets the room ────────────────────────────────────────────────────────────────────
//
// The first time the new capsule sweep and the room's claims are pointed at each other, and it is
// worth more than either alone: the room says the ledge's top is at 1.20 m, the sweep says where a
// body dropped onto it comes to rest, and those two numbers have no common ancestor. A room built
// wrong and a sweep built wrong would have to be wrong in exactly the same way to agree.
//
// Dropped rather than placed: a capsule with its segment from y0 to y0+1 and radius 0.35 has its
// lowest point at y0 - 0.35, so it rests with y0 exactly a radius above whatever it lands on.
{
    const float radius = 0.35f;
    const float dropFrom = 8f;
    const float dropBy = 12f;

    foreach (var (name, x, z, expected) in new[]
    {
        ("floor at the spawn", Room.SpawnPoint.X, Room.SpawnPoint.Z, 0f),
        ("the ledge's top", 4.5f, -5.5f, 1.2f),
        ("the beam's top", 5f, 1.2f, 1.6f),
        ("the dome's apex", 10.5f, 4.5f, 3f),
        ("the top tread of the 0.30 m flight", -2.5f, 2f, 1.8f),
        ("the top tread of the 0.10 m flight", -2.5f, -6f, 0.6f),
    })
    {
        var body = new Capsule(new(x, dropFrom, z), new(x, dropFrom + 1f, z), radius);
        var hit = Intersection.Sweep(body, new Vector3(0f, -dropBy, 0f), room.Collider);

        if (hit is null)
        {
            t.Expect($"a body dropped on {name} lands", false, "swept through everything");
            continue;
        }

        var restedAt = dropFrom - (hit.Value.Time * dropBy) - radius;
        t.Expect($"a body dropped on {name} rests at {expected:0.000} m",
            MathF.Abs(restedAt - expected) < 2e-3f, $"rested at {restedAt:0.0000}");
    }

    // A SLOPE IS NOT A FLOOR, and the drop test above cannot be used on one — which is worth a check
    // of its own rather than a fudged expectation. A capsule resting on an incline touches it
    // UP-SLOPE of its axis, so its lowest point sits r/cos(theta) above the surface rather than r,
    // and a straight-down "how far to the ground" probe reads 5.4 cm high on a 30° ramp. That error
    // is a ground check that thinks the body is floating, which is how a controller on a slope
    // starts falling every frame.
    //
    // So the claim here is about the CONTACT rather than the rest height: wherever the sweep says
    // the body touched, that point is on the ramp's analytic surface — y = tan(30°) x (x + 11).
    {
        var body = new Capsule(new(-10.9f, dropFrom, 0f), new(-10.9f, dropFrom + 1f, 0f), radius);
        var hit = Intersection.Sweep(body, new Vector3(0f, -dropBy, 0f), room.Collider);
        var tan30 = MathF.Tan(30f * MathF.PI / 180f);
        var onSurface = hit is { } h && MathF.Abs(h.Point.Y - (tan30 * (h.Point.X + 11f))) < 2e-3f;

        t.Expect("a body dropped on the 30° ramp touches it ON its surface", onSurface,
            hit is null ? "no contact at all" : $"contact at {hit.Value.Point} is off the ramp plane");

        // And the contact normal is the ramp's, not the floor's — the number a slope limit reads.
        var slope = hit is { } h2 ? Room.SlopeDegrees(h2.Normal) : -1f;
        t.Expect("and the contact normal reports 30°", MathF.Abs(slope - 30f) < 0.05f, $"reported {slope:0.###}°");
    }

    // IT CANNOT TUNNEL THROUGH THE ROOM EITHER. A body crossing the east wall at 100 m/s in a 16 ms
    // step travels 1.6 m through a wall 0.5 m thick — and the control is that a discrete test where
    // it would have ENDED sees nothing at all.
    var runner = new Capsule(new(13f, 0.4f, 0f), new(13f, 1.4f, 0f), radius);

    // 2.4 m, not 1.6. The first attempt had the body END inside the wall rather than past it, so the
    // "discrete test sees nothing" control failed — correctly, and it was the control that was
    // wrong. A tunnelling test whose body stops inside the obstacle is not testing tunnelling.
    var motion = new Vector3(2.4f, 0f, 0f);
    var arrived = new Capsule(runner.PointA + motion, runner.PointB + motion, radius);

    t.Expect("the control: a discrete test past the east wall sees nothing",
        Intersection.Test(arrived, room.Collider) is null, "it happened to overlap after all");
    t.Expect("a body crossing the east wall at 144 m/s is stopped by it",
        Intersection.Sweep(runner, motion, room.Collider) is not null, "swept clean through");
}

// ── The resolver ────────────────────────────────────────────────────────────────────────────────
//
// Sweep, stop at the contact, deflect what is left, repeat. These are the invariants the loop is
// judged by, and each one is followed by the SAME check with the loop switched off — because a
// property of the geometry and a property of the resolver look identical when everything passes.
{
    const float radius = 0.35f;

    static Capsule Body(float x, float y, float z) =>
        new(new(x, y + radius, z), new(x, y + 1.45f, z), radius);

    static Capsule Moved(Capsule body, Vector3 delta) =>
        new(body.PointA + delta, body.PointB + delta, radius);

    var resolver = new BodyResolver();

    // ── 1. A walk that never ends inside anything ────────────────────────────────────────────────
    //
    // 200 steps of 8 cm from the spawn, straight at the ramp fan: across open floor, into the 30°
    // ramp, and up it — the deflection carrying the body up the slope, since nothing here has
    // gravity yet. Checked after EVERY step, because a resolver that is right 199 times and wrong
    // once has a bug that a final position cannot see.
    {
        var body = Body(Room.SpawnPoint.X, 0f, Room.SpawnPoint.Z);
        var worstDepth = 0f;
        var stuckAt = -1;

        for (var step = 0; step < 200; step++)
        {
            var result = resolver.Move(body, new Vector3(0.08f, 0f, 0f), room.Collider);
            body = Moved(body, result.Position);

            if (Intersection.Test(body, room.Collider) is { } overlap)
            {
                worstDepth = MathF.Max(worstDepth, overlap.Depth);
                if (overlap.Depth > 2f * resolver.SkinWidth && stuckAt < 0) stuckAt = step;
            }
        }

        t.Expect("a 16 m walk into the ramp fan never ends a step inside a surface",
            stuckAt < 0, $"first penetration at step {stuckAt}, worst {worstDepth:0.0000} m");

        // And it got somewhere: a body that refused to move would pass the check above trivially.
        t.Expect("and the walk actually climbed the 30° ramp",
            body.PointA.Y - radius > 1.0f, $"ended at y {body.PointA.Y - radius:0.000}");
    }

    // ── 2. It cannot be pushed through a wall ────────────────────────────────────────────────────
    {
        var body = Body(13f, 0f, 0f);
        var result = resolver.Move(body, new Vector3(2.4f, 0f, 0f), room.Collider);
        var ended = Moved(body, result.Position);

        t.Expect("a body driven at the east wall at 144 m/s stays west of it",
            ended.PointA.X + radius <= 14f + 1e-3f, $"ended at x {ended.PointA.X:0.000}");
        t.Expect("and is not inside anything when it stops",
            Intersection.Test(ended, room.Collider) is null, "it came to rest overlapping");
    }

    // ── 3. Grazing a wall keeps the speed along it ───────────────────────────────────────────────
    //
    // THE ONE THAT SEPARATES A RESOLVER FROM A STOP. A body moving diagonally into a wall should
    // arrive at the far end of its along-wall motion — sliding past a doorframe rather than sticking
    // to it. The east wall's inner face is at x = 14; the body starts 15 cm short of touching it.
    {
        var body = Body(13.5f, 0f, 0f);
        var motion = new Vector3(1f, 0f, 1f);

        var slid = resolver.Move(body, motion, room.Collider);
        t.Expect("a body grazing a wall keeps its along-wall travel",
            slid.Position.Z > 0.98f, $"travelled {slid.Position.Z:0.000} of 1.0 along the wall");
        t.Expect("and stops against it across the wall",
            slid.Position.X < 0.2f, $"travelled {slid.Position.X:0.000} into the wall");

        // THE CONTROL. With the loop off the body simply stops at the contact, so the along-wall
        // travel is whatever it managed before touching — about 15 cm. If this were ALSO ~1.0, the
        // check above would be measuring the geometry rather than the resolver.
        var stopper = new BodyResolver { Enabled = false };
        var stopped = stopper.Move(body, motion, room.Collider);
        t.Expect("the control: with deflection off it stops dead at the wall",
            stopped.Position.Z < 0.2f, $"still travelled {stopped.Position.Z:0.000} along the wall");
    }

    // ── 4. A body that starts inside a solid gets out ────────────────────────────────────────────
    //
    // Sunk 25 cm into the floor, which is where the impaled contact lives: the segment crosses the
    // floor's top face, so the closest pair collapses and the only thing that says which way is out
    // is the winding. This is the check that made the outward-normal decision in Intersection.
    {
        var sunk = Body(0f, -0.25f, -6f);
        t.Expect("the fixture really does start inside the floor",
            Intersection.Test(sunk, room.Collider) is not null, "it was already clear");

        var freed = resolver.Move(sunk, Vector3.Zero, room.Collider);
        var after = Moved(sunk, freed.Position);

        t.Expect("a body sunk into the floor is pushed out", freed.Depenetrations > 0, "no passes ran");
        t.Expect("and ends up clear of it",
            Intersection.Test(after, room.Collider) is null,
            $"still overlapping after {freed.Depenetrations} pass(es)");
        t.Expect("upward, not further in", freed.Position.Y > 0f, $"moved {freed.Position.Y:0.000} in Y");
    }

    // ── 5. The same move twice is the same move ──────────────────────────────────────────────────
    {
        var body = Body(13.5f, 0f, 0f);
        var a = resolver.Move(body, new Vector3(1f, 0f, 1f), room.Collider);
        var b = resolver.Move(body, new Vector3(1f, 0f, 1f), room.Collider);
        t.Expect("the resolver is deterministic", a.Position == b.Position,
            $"{a.Position} then {b.Position}");
    }
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

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
        ("the ledge's top", 3.5f, -5f, 1.2f),
        ("the beam's top", 4f, 2.4f, 1.6f),
        ("the dome's apex", 11f, -4f, 3f),
        ("the top tread of the 0.30 m flight", -7.05f, 2f, 1.8f),
        ("the top tread of the 0.10 m flight", -7.05f, -6f, 0.6f),
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
        // The 30° ramp's foot is at x = -8.5 and it rises toward -X, so its surface is
        // y = tan(30°) x (-8.5 - x) for x in [-11, -8.5].
        var body = new Capsule(new(-8.6f, dropFrom, 0f), new(-8.6f, dropFrom + 1f, 0f), radius);
        var hit = Intersection.Sweep(body, new Vector3(0f, -dropBy, 0f), room.Collider);
        var tan30 = MathF.Tan(30f * MathF.PI / 180f);
        var onSurface = hit is { } h && MathF.Abs(h.Point.Y - (tan30 * (-8.5f - h.Point.X))) < 2e-3f;

        t.Expect("a body dropped on the 30° ramp touches it ON its surface", onSurface,
            hit is null ? "no contact at all" : $"contact at {hit.Value.Point} is off the ramp plane");

        // And the contact normal is the ramp's, not the floor's — the number a slope limit reads.
        var slope = hit is { } h2 ? Room.SlopeDegrees(h2.Normal) : -1f;
        t.Expect("and the contact normal reports 30°", MathF.Abs(slope - 30f) < 0.05f, $"reported {slope:0.###}°");
    }

    // IT CANNOT TUNNEL THROUGH THE ROOM EITHER. A body crossing the east wall at 100 m/s in a 16 ms
    // step travels 1.6 m through a wall 0.5 m thick — and the control is that a discrete test where
    // it would have ENDED sees nothing at all.
    var runner = new Capsule(new(-13f, 0.4f, 0f), new(-13f, 1.4f, 0f), radius);

    // 2.4 m, not 1.6. The first attempt had the body END inside the wall rather than past it, so the
    // "discrete test sees nothing" control failed — correctly, and it was the control that was
    // wrong. A tunnelling test whose body stops inside the obstacle is not testing tunnelling.
    var motion = new Vector3(-2.4f, 0f, 0f);
    var arrived = new Capsule(runner.PointA + motion, runner.PointB + motion, radius);

    t.Expect("the control: a discrete test past the west wall sees nothing",
        Intersection.Test(arrived, room.Collider) is null, "it happened to overlap after all");
    t.Expect("a body crossing the west wall at 144 m/s is stopped by it",
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
            var result = resolver.Move(body, new Vector3(-0.08f, 0f, 0f), room.Collider);
            body = Moved(body, result.Position);

            if (Intersection.Test(body, room.Collider) is { } overlap)
            {
                worstDepth = MathF.Max(worstDepth, overlap.Depth);
                if (overlap.Depth > 2f * resolver.SkinWidth && stuckAt < 0) stuckAt = step;
            }
        }

        t.Expect("a 16 m walk west into the ramp fan never ends a step inside a surface",
            stuckAt < 0, $"first penetration at step {stuckAt}, worst {worstDepth:0.0000} m");

        // And it got somewhere: a body that refused to move would pass the check above trivially.
        t.Expect("and the walk actually climbed the 30° ramp",
            body.PointA.Y - radius > 1.0f, $"ended at y {body.PointA.Y - radius:0.000}");
    }

    // ── 2. It cannot be pushed through a wall ────────────────────────────────────────────────────
    {
        var body = Body(-13f, 0f, 0f);
        var result = resolver.Move(body, new Vector3(-2.4f, 0f, 0f), room.Collider);
        var ended = Moved(body, result.Position);

        t.Expect("a body driven at the west wall at 144 m/s stays east of it",
            ended.PointA.X - radius >= -14f - 1e-3f, $"ended at x {ended.PointA.X:0.000}");
        t.Expect("and is not inside anything when it stops",
            Intersection.Test(ended, room.Collider) is null, "it came to rest overlapping");
    }

    // ── 3. Grazing a wall keeps the speed along it ───────────────────────────────────────────────
    //
    // THE ONE THAT SEPARATES A RESOLVER FROM A STOP. A body moving diagonally into a wall should
    // arrive at the far end of its along-wall motion — sliding past a doorframe rather than sticking
    // to it. The west wall's inner face is at x = -14; the body starts 15 cm short of touching it.
    {
        var body = Body(-13.5f, 0f, 0f);
        var motion = new Vector3(-1f, 0f, 1f);

        var slid = resolver.Move(body, motion, room.Collider);
        t.Expect("a body grazing a wall keeps its along-wall travel",
            slid.Position.Z > 0.98f, $"travelled {slid.Position.Z:0.000} of 1.0 along the wall");
        t.Expect("and stops against it across the wall",
            MathF.Abs(slid.Position.X) < 0.2f, $"travelled {slid.Position.X:0.000} into the wall");

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

// ── Standing, sliding, and climbing ─────────────────────────────────────────────────────────────
//
// The numbers only a lab can find, and the checks that say whether they were found or guessed. Each
// one is paired with the SAME situation under a changed RULE rather than changed geometry — if a
// body stops sliding when the slope limit moves, the limit is what stopped it.
{
    // Drop a body at (x, z) and let it settle before asking it anything.
    static CharacterMotor Settle(TriangleMesh3D world, float x, float z, Action<CharacterMotor>? tune = null)
    {
        var motor = new CharacterMotor();
        tune?.Invoke(motor);
        motor.Teleport(new Vector3(x, 4f, z));

        // Until it lands, not for a fixed time. A body dropped on ground too steep to stand on
        // starts sliding the moment it touches, so settling it for four seconds settles it at the
        // BOTTOM of the ramp — where it is standing on the floor, which is the opposite of what the
        // steep cases mean to ask about.
        for (var i = 0; i < 600 && !motor.Grounded; i++) motor.Step(Vector3.Zero, 1f / 60f, world);
        return motor;
    }

    // The ramp fan's feet are at x = -8.5 and it rises toward -X, so x = -9.75 is the middle of
    // every ramp's face.
    const float rampMiddle = -9.75f;

    // ── At rest on ground it can stand on, and STILL at rest ten seconds later ───────────────────
    foreach (var (name, z, expectedSlope) in new[] { ("15°", -3.6f, 15f), ("30°", 0f, 30f), ("45°", 3.6f, 45f) })
    {
        var motor = Settle(room.Collider, rampMiddle, z);

        t.Expect($"a body settles onto the {name} ramp and calls it standable",
            motor.Standing && MathF.Abs(motor.GroundSlopeDegrees - expectedSlope) < 0.5f,
            $"grounded {motor.Grounded}, slope {motor.GroundSlopeDegrees:0.##}°");

        var settled = motor.Feet;
        for (var i = 0; i < 600; i++) motor.Step(Vector3.Zero, 1f / 60f, room.Collider);
        var drift = (motor.Feet - settled).Length();

        // EXACTLY at rest, not nearly. A micro-slide is not a tolerance to be tuned down — it is
        // gravity being deflected along the surface every frame, and the fix is that gravity does
        // not deflect at all. A body that creeps 1 mm a second has crossed the room in an hour.
        t.Expect($"and has not moved a millimetre after ten more seconds on the {name} ramp",
            drift < 1e-3f, $"drifted {drift * 1000f:0.###} mm");
    }

    // ── Too steep to stand on, and it goes DOWNHILL ──────────────────────────────────────────────
    {
        var motor = Settle(room.Collider, rampMiddle, 7.2f);
        t.Expect("a body on the 60° ramp is grounded but not standing",
            motor.Grounded && !motor.Standing, $"grounded {motor.Grounded}, standing {motor.Standing}");

        var from = motor.Feet;
        for (var i = 0; i < 30; i++) motor.Step(Vector3.Zero, 1f / 60f, room.Collider);
        var travelled = motor.Feet - from;

        t.Expect("and slides", travelled.Length() > 0.05f, $"moved {travelled.Length():0.0000} m");

        // Downhill on a ramp rising toward -X means +X, and downward.
        t.Expect("downhill rather than in some other direction",
            travelled.X > 0f && travelled.Y < 0f, $"went {travelled}");

        // THE CONTROL. Same ramp, same body, same gravity — a slope limit that permits 60° and the
        // slide stops. If it did not, the limit is not what was holding anything up.
        var permissive = Settle(room.Collider, rampMiddle, 7.2f, m => m.SlopeLimitDegrees = 89f);
        var held = permissive.Feet;
        for (var i = 0; i < 30; i++) permissive.Step(Vector3.Zero, 1f / 60f, room.Collider);
        t.Expect("the control: raise the limit past 60° and the same body stands still",
            (permissive.Feet - held).Length() < 1e-3f,
            $"still moved {(permissive.Feet - held).Length():0.0000} m");
    }

    // ── Climbing a step, and refusing one ────────────────────────────────────────────────────────
    //
    // Each flight is met from the open (east) side and climbed westward. Walking is the only way to
    // test this: a step rule that is never blocked is never exercised.
    // The HIGHEST it got, not where it ended up. The first version returned the final height and
    // reported that nothing climbed anything — the body was crossing all six treads, walking off the
    // top of the flight, and coming back down to the floor well inside the step budget. A trace
    // showed it at 0.589 m on a 0.60 m tread at step 60 and back at 0.005 by step 80. "Did it climb"
    // is a question about the journey.
    static float WalkWest(TriangleMesh3D world, CharacterMotor motor, int steps)
    {
        var highest = motor.Feet.Y;
        for (var i = 0; i < steps; i++)
        {
            motor.Step(-Vector3.UnitX, 1f / 60f, world);
            highest = MathF.Max(highest, motor.Feet.Y);
        }
        return highest;
    }

    foreach (var (name, z, topTread) in new[] { ("0.10 m", -6f, 0.6f), ("0.20 m", -2f, 1.2f), ("0.30 m", 2f, 1.8f) })
    {
        var motor = Settle(room.Collider, -2.2f, z);
        var top = WalkWest(room.Collider, motor, 120);

        t.Expect($"a body walks up the {name} flight", top > topTread - 0.05f,
            $"reached {top:0.000} of {topTread:0.000}");
    }

    {
        // THE CONTROL FOR THE RULE ITSELF: no step allowance, same stairs, and the flight becomes a
        // wall. Worth stating because it is not obvious that it would be — a capsule is round
        // underneath, and a lip shorter than its radius is met on its top EDGE rather than its face,
        // where the contact normal tilts upward and an ordinary slide would carry the body over it.
        // What stops that is the slope limit: an edge that steep is deflected as a wall, with its
        // upward component clamped away, precisely so that "walk up anything with a corner on it"
        // is not a way around the limit. So the two rules are load-bearing together — the limit
        // refuses the climb and the step rule grants the exception.
        var noStepRule = Settle(room.Collider, -2.2f, 2f, m => m.StepHeight = 0f);
        t.Expect("the control: with no step allowance the same flight stops the body dead",
            WalkWest(room.Collider, noStepRule, 120) < 0.35f, "it climbed without a step rule");
    }

    {
        // SO THE RULE IS TESTED WHERE ROLLING CANNOT HELP: a 1.2 m face, four times the radius.
        // Raised allowance climbs it, default refuses it — same geometry, changed rule.
        var vaulter = Settle(room.Collider, 8.5f, -6.5f, m => m.StepHeight = 1.5f);
        t.Expect("the control: a step allowance taller than the ledge climbs it",
            WalkWest(room.Collider, vaulter, 180) > 1.1f, "even 1.5 m of step allowance did not");
    }

    {
        // And at the default allowance a ledge is not a step. 1.2 m against 0.35.
        // Clear of the dome, which reaches further than its footprint suggests: a capsule at
        // (8, -5) is 3.16 m from the dome's axis and its 0.35 m radius puts its flank inside the
        // 3 m cap, so it settled a metre up the dome's side and the test measured that instead.
        var motor = Settle(room.Collider, 8.5f, -6.5f);
        var reached = WalkWest(room.Collider, motor, 180);
        t.Expect("a body cannot climb the 1.2 m ledge by walking at it",
            reached < 0.35f, $"reached {reached:0.000}");
    }


    // ── No step may move the body faster than it walks ──────────────────────────────────────────
    //
    // THE SKID. Reported from the chair: a body pressed against a surface at an angle stops obeying
    // its own speed and tears along the wall. It is the classic sweep-and-slide failure and every
    // implementation meets it, so it gets an invariant rather than a fix and a hope: no single step
    // may displace the body further than its speed allows, whatever it is touching.
    //
    // Measured per frame rather than as a total, because an average hides it — one frame in three at
    // six times speed reads as twice speed over a second, which sounds like a tuning problem.
    {
        // The rule, stated exactly: a frame that CLIMBS may advance a radius, because that is what it
        // costs a round body to get its axis onto a tread. Every other frame is limited to walking
        // pace. Sliding is excluded by turning it off rather than by widening the bound, so anything
        // over the line is the step rule cheating and nothing else.
        static (float Walking, float Climbing) FastestStep(
            TriangleMesh3D world, CharacterMotor motor, Vector3 wish, int steps)
        {
            var walking = 0f;
            var climbing = 0f;
            for (var i = 0; i < steps; i++)
            {
                var before = motor.Feet;
                motor.Step(wish, 1f / 60f, world);
                var moved = new Vector3(motor.Feet.X - before.X, 0f, motor.Feet.Z - before.Z).Length();
                if (motor.SteppedUp) climbing = MathF.Max(climbing, moved);
                else walking = MathF.Max(walking, moved);
            }
            return (walking, climbing);
        }

        var allowed = 3.5f / 60f;   // the default walk speed for one frame
        var perClimb = 0.35f + 0.05f;   // a radius, which is what mounting a step costs

        // Diagonally into the west wall: the component along the wall should carry on at walking
        // pace and the component into it should stop.
        var against = Settle(room.Collider, -13f, 0f, m => m.SlideSpeed = 0f);
        var wall = FastestStep(room.Collider, against, Vector3.Normalize(new Vector3(-1f, 0f, 1f)), 120);
        t.Expect("a body sliding along a wall never outruns its own walk speed",
            wall.Walking <= allowed * 1.1f, $"one step moved {wall.Walking:0.0000} m, {wall.Walking / allowed:0.0} x walking");
        t.Expect("and sliding along a wall is never mistaken for climbing one",
            wall.Climbing == 0f, $"a step was accepted against a flat wall, moving {wall.Climbing:0.0000} m");

        // And into the ramp fan, where the blocking surface is walkable rather than a wall.
        var uphill = Settle(room.Collider, -7.5f, 0f, m => m.SlideSpeed = 0f);
        var ramp = FastestStep(room.Collider, uphill, Vector3.Normalize(new Vector3(-1f, 0f, 0.4f)), 120);
        t.Expect("nor does one working its way along a ramp",
            ramp.Walking <= allowed * 1.1f, $"one step moved {ramp.Walking:0.0000} m, {ramp.Walking / allowed:0.0} x walking");
        t.Expect("and a walkable ramp is never treated as something to climb over",
            ramp.Climbing == 0f, $"a step was accepted on a ramp, moving {ramp.Climbing:0.0000} m");

        // And climbing stairs, which is where a step rule has the most excuse to cheat.
        var climbing = Settle(room.Collider, -2.2f, 2f, m => m.SlideSpeed = 0f);
        var stairs = FastestStep(room.Collider, climbing, -Vector3.UnitX, 120);
        t.Expect("nor does one walking between stair treads",
            stairs.Walking <= allowed * 1.1f, $"one step moved {stairs.Walking:0.0000} m, {stairs.Walking / allowed:0.0} x walking");
        t.Expect("and mounting a tread costs a radius and no more",
            stairs.Climbing <= perClimb, $"one climb moved {stairs.Climbing:0.0000} m");
    }

    // ── Walking off a ledge falls; walking down stairs does not ─────────────────────────────────
    {
        // Standing on the ledge, walking east off its edge: there is nothing within a step, so the
        // snap finds nothing and gravity takes over.
        var motor = Settle(room.Collider, 3.5f, -5f);
        t.Expect("a body settles on the ledge", motor.Standing && motor.Feet.Y > 1.1f,
            $"at y {motor.Feet.Y:0.000}");

        for (var i = 0; i < 180; i++) motor.Step(Vector3.UnitX, 1f / 60f, room.Collider);
        t.Expect("and walking off its edge drops it to the floor", motor.Feet.Y < 0.05f,
            $"ended at y {motor.Feet.Y:0.000}");
    }
}

// ── What steers the body is what the camera looks along ─────────────────────────────────────────
//
// Reported from the chair: movement felt right in first person and 15-30 degrees off in every other
// rig. It was the shoulder offset — applied to the EYE only, which leaves the camera looking across
// the body rather than along its own yaw, while the body is still steered by that yaw. The error is
// atan(shoulder / (distance * cos pitch)): 7.6° at the default 4.2 m and 25° once the camera pulls
// in against a wall. First person has no shoulder, which is exactly why it felt right.
//
// So the rule gets an invariant, for every rig and at offsets no one would choose: the direction the
// camera looks, flattened to the ground, IS the direction the body is steered by. A camera needs no
// GPU to answer that.
{
    var camera = new RoomCamera();

    foreach (var rig in new[] { CameraRig.Orbit, CameraRig.ThirdPerson, CameraRig.FirstPerson, CameraRig.Isometric })
    {
        foreach (var shoulder in new[] { 0f, 0.55f, -1.2f })
        {
            camera.Rig = rig;                      // applies the rig's defaults, so set the rest after
            camera.ShoulderOffset = shoulder;
            camera.Yaw = 0.7f;

            // Against the west wall, so the third-person rig's pull-in is actually exercised — a
            // pull-in that turned the view rather than shortening it would be this same bug again,
            // arriving from the other direction.
            camera.Place(new Vector3(-13.4f, 0f, 0f), 1.8f, room.Collider);

            var toTarget = camera.Target - camera.Position;
            var flat = new Vector3(toTarget.X, 0f, toTarget.Z);
            var (forward, _) = camera.GroundBasis;

            var agreement = flat.LengthSquared() < 1e-8f ? 1f : Vector3.Dot(Vector3.Normalize(flat), forward);
            var offBy = MathF.Acos(Math.Clamp(agreement, -1f, 1f)) * 180f / MathF.PI;

            t.Expect($"{rig} at shoulder {shoulder:0.00} steers where it looks",
                offBy < 0.05f, $"off by {offBy:0.##}°");
        }
    }

    // And the pull-in shortens the view without turning it, which is what lets the check above be
    // this simple — and what makes a camera in a corner still walk the body up the screen.
    camera.Rig = CameraRig.ThirdPerson;
    camera.ShoulderOffset = 0.55f;
    camera.Yaw = 0.7f;
    camera.Place(new Vector3(0f, 0f, 0f), 1.8f, room.Collider);
    var openAir = Vector3.Normalize(camera.Target - camera.Position);

    camera.Place(new Vector3(-13.4f, 0f, 0f), 1.8f, room.Collider);
    var againstWall = Vector3.Normalize(camera.Target - camera.Position);

    t.Expect("and pulling the camera out of a wall shortens the view without turning it",
        Vector3.Dot(openAir, againstWall) > 0.9999f,
        $"turned by {MathF.Acos(Math.Clamp(Vector3.Dot(openAir, againstWall), -1f, 1f)) * 180f / MathF.PI:0.##}°");
}

// ── A straight input makes a straight path ──────────────────────────────────────────────────────
//
// Reported from the chair: "I'm only pressing W/S so I'd expect the trail to be straighter". That is
// a claim about the motor and it can be measured — walk in one direction with nothing in the way and
// see how far the body wanders off the line it started on.
{
    static float WanderOffLine(TriangleMesh3D world, CharacterMotor motor, Vector3 direction, int steps)
    {
        var start = motor.Feet;
        var worst = 0f;
        for (var i = 0; i < steps; i++)
        {
            motor.Step(direction, 1f / 60f, world);
            var along = Vector3.Dot(motor.Feet - start, direction);
            var sideways = (motor.Feet - start) - (direction * along);
            worst = MathF.Max(worst, new Vector3(sideways.X, 0f, sideways.Z).Length());
        }
        return worst;
    }

    static CharacterMotor Standing(TriangleMesh3D world, float x, float z)
    {
        var motor = new CharacterMotor();
        motor.Teleport(new Vector3(x, 4f, z));
        for (var i = 0; i < 240; i++) motor.Step(Vector3.Zero, 1f / 60f, world);
        return motor;
    }

    // Open floor, well clear of every solid: down the middle of the hall heading east.
    foreach (var (name, x, z, dx, dz, steps) in new[]
    {
        ("east", -1f, -4f, 1f, 0f, 180),
        ("north-east", -2f, -3f, 0.707f, 0.707f, 60),
        ("south", -1f, 0f, 0f, -1f, 180),
        ("west-north-west", 0f, 7f, -0.92f, 0.38f, 90),
    })
    {
        var motor = Standing(room.Collider, x, z);
        var direction = Vector3.Normalize(new Vector3(dx, 0f, dz));
        var wander = WanderOffLine(room.Collider, motor, direction, steps);

        t.Expect($"walking {name} across open floor holds a straight line",
            wander < 1e-3f, $"wandered {wander * 1000f:0.##} mm off it");
    }
}

// ── The motion graph ────────────────────────────────────────────────────────────────────────────
//
// M-A. What is being checked is not "does a state machine work" — it is the specific set of
// orderings RTSGame paid three bug reports for, turned into things that fail when they are wrong.
{
    var graph = MotionGraph.Humanoid();

    // ── Well-formedness, which is cheap and catches the mistakes that are invisible at runtime ────
    {
        var names = new HashSet<string>(graph.States.Select(x => x.Name));
        var unknown = 0;
        foreach (var edge in graph.Transitions)
        {
            if (edge.From != MotionTransition.AnyState && !names.Contains(edge.From)) unknown++;
            if (!names.Contains(edge.To)) unknown++;
        }

        t.Expect("every transition names states that exist", unknown == 0, $"{unknown} dangling end(s)");
        t.Expect("the initial state exists", names.Contains(graph.Initial), $"initial is '{graph.Initial}'");

        // Every state is reachable, or it is a clip nothing will ever play — which is a graph that
        // LOOKS complete and has a dead limb in it.
        var reachable = new HashSet<string> { graph.Initial };
        for (var pass = 0; pass < graph.States.Count; pass++)
        {
            foreach (var edge in graph.Transitions)
            {
                if (edge.From == MotionTransition.AnyState || reachable.Contains(edge.From)) reachable.Add(edge.To);
            }
        }
        t.Expect("every state is reachable", reachable.Count == names.Count,
            $"{names.Count - reachable.Count} unreachable: {string.Join(", ", names.Except(reachable))}");

        // THE ORDERING RULE, CHECKED RATHER THAN TRUSTED. First match wins, so an interrupt declared
        // after an ordinary transition that also matches would never be reached — it would still be
        // marked "interrupts" and would still never interrupt anything.
        var lastInterrupt = -1;
        var firstOrdinary = int.MaxValue;
        for (var i = 0; i < graph.Transitions.Count; i++)
        {
            if (graph.Transitions[i].Interrupts) lastInterrupt = i;
            else firstOrdinary = Math.Min(firstOrdinary, i);
        }
        t.Expect("every interrupt is declared before every ordinary transition", lastInterrupt < firstOrdinary,
            $"an interrupt at {lastInterrupt} sits after an ordinary transition at {firstOrdinary}");
    }

    // ── A threshold crossed for one frame does not move the body ─────────────────────────────────
    {
        var machine = new MotionMachine(graph);
        machine.Step(new MotionInput(Speed: 0f), 1f / 60f);

        // One frame over the walk threshold, then back. This is the shape of every jostle RTSGame
        // described: a body at its place nudged by a neighbour, a velocity spike lasting a frame.
        machine.Step(new MotionInput(Speed: 3f), 1f / 60f);
        machine.Step(new MotionInput(Speed: 0f), 1f / 60f);

        t.Expect("a one-frame spike does not change state", machine.State == "idle",
            $"ended in '{machine.State}' after {machine.Changes} change(s)");

        // THE CONTROL FOR THE DWELL. The same spike with the dwell off moves the body and moves it
        // back, which is what the dwell exists to prevent and what makes the assertion above mean
        // something.
        var ungated = new MotionMachine(graph) { DwellEnabled = false };
        ungated.Step(new MotionInput(Speed: 0f), 1f / 60f);
        ungated.Step(new MotionInput(Speed: 3f), 1f / 60f);
        ungated.Step(new MotionInput(Speed: 0f), 1f / 60f);

        t.Expect("the control: with the dwell off the same spike moves it and moves it back",
            ungated.Changes >= 2, $"only {ungated.Changes} change(s)");
    }

    // ── And a real change of intent still gets through ───────────────────────────────────────────
    {
        var machine = new MotionMachine(graph);
        for (var i = 0; i < 60; i++) machine.Step(new MotionInput(Speed: 1.2f), 1f / 60f);

        t.Expect("a sustained walk does reach the walking state", machine.State == "walk",
            $"ended in '{machine.State}'");
        t.Expect("and says why it went there", machine.Last?.Why == "reached walk speed",
            $"last reason was '{machine.Last?.Why}'");
    }

    // ── Interrupts bypass the dwell ──────────────────────────────────────────────────────────────
    {
        var machine = new MotionMachine(graph);
        for (var i = 0; i < 60; i++) machine.Step(new MotionInput(Speed: 1.2f), 1f / 60f);

        // Hurt on the very next frame — well inside the dwell that just blocked everything else.
        machine.Step(new MotionInput(Speed: 1.2f, Hurt: true), 1f / 60f);

        t.Expect("being hit flinches immediately, inside the dwell", machine.State == "flinch",
            $"ended in '{machine.State}' with {machine.TimeInState:0.000}s in state");
        t.Expect("and the change is marked as an interrupt", machine.Last?.Interrupted == true,
            "it was recorded as an ordinary transition");
    }

    // ── THE ACT BEFORE THE MOVEMENT ──────────────────────────────────────────────────────────────
    //
    // RTSGame's hardest-won ordering, as a test: a body that is both acting and moving is acting. It
    // is only ever underway for a body already at its place, so testing it first cannot steal a
    // frame from anything — and testing it second is what made the construction animation flicker
    // between labouring and walking.
    {
        var machine = new MotionMachine(graph);
        for (var i = 0; i < 60; i++) machine.Step(new MotionInput(Speed: 1.2f, Act: true), 1f / 60f);

        t.Expect("a body that is acting and drifting is acting, not walking", machine.State == "act",
            $"ended in '{machine.State}'");
    }

    // ── Landing is not skipped by a body that is still moving ────────────────────────────────────
    {
        var machine = new MotionMachine(graph);
        for (var i = 0; i < 30; i++) machine.Step(new MotionInput(Speed: 3f, Grounded: false, VerticalSpeed: -4f), 1f / 60f);
        t.Expect("a body off the ground is falling", machine.State == "fall", $"in '{machine.State}'");

        machine.Step(new MotionInput(Speed: 3f, Grounded: true, VerticalSpeed: 0f), 1f / 60f);
        t.Expect("and touching down at a run LANDS rather than going straight to running",
            machine.State == "land", $"went to '{machine.State}'");
    }

    // ── A BODY SITTING ON THE THRESHOLD, and what actually settles it ────────────────────────────
    //
    // The situation every one of RTSGame's reports describes: a speed hovering either side of a gait
    // threshold. THE DWELL DOES NOT FIX THIS, and finding that out is the most useful thing in this
    // section — a dwell stops a spike, but a body on a boundary simply alternates once per dwell
    // instead of once per frame. Measured at 5.4 changes a second, which is slower flicker and still
    // flicker.
    //
    // What settles it is a BAND: leave a gait at a lower speed than you entered it at. The control
    // collapses the band to a line and shows the flicker come back with the dwell still on, which is
    // what says the band and not the dwell is doing the work.
    {
        static float Flicker(MotionGraph g)
        {
            var machine = new MotionMachine(g);
            for (var i = 0; i < 600; i++)
            {
                // Alternating either side of 0.2 m/s, which is the walk threshold exactly.
                machine.Step(new MotionInput(Speed: i % 2 == 0 ? 0.19f : 0.21f), 1f / 60f);
            }
            return machine.ChangesPerSecond;
        }

        var banded = Flicker(MotionGraph.Humanoid());
        var onALine = Flicker(MotionGraph.Humanoid(walkAbove: 0.2f, walkBelow: 0.2f));

        t.Expect("a body on the threshold settles when leaving costs less than entering", banded < 0.2f,
            $"{banded:0.##} changes a second");
        t.Expect("the control: collapse the band to a line and it alternates again, dwell and all",
            onALine > 4f, $"only {onALine:0.#} changes a second, so the band was not what settled it");
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

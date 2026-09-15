using Blix.Verify;
using System.Numerics;
using Blix.Geometry;

// System.Numerics has a Plane of its own and it is not this one.
using Plane = Blix.Geometry.Plane;

// CLI test harness for the 3D collision math — the suite Blix.Geometry has never had.
//
// Same shape as Blix.Test.Physics2D, which is the point: docs/blix.md describes Intersection2D as
// "mirrors the 3D pattern, pressure-tested by the Blix.Test.Physics2D CLI harness (43 cases)". The
// mirror was tested; the original it mirrors was not, and had not been touched since the initial
// commit.
//
// The capsule is where this starts because the capsule is what a character is.

var t = new TestRunner();

// ── Capsule vs triangle, discrete ────────────────────────────────────────────────────────────────
//
// One triangle in the XZ plane at y = 0, spanning a couple of metres about the origin, so "above",
// "below" and "through" are all easy to state exactly.
{
    var tri = new Triangle(new(-1f, 0f, -1f), new(1f, 0f, -1f), new(0f, 0f, 1f));
    var mesh = new TriangleMesh3D(new[] { tri });

    // A capsule standing well clear.
    t.ExpectMiss("capsule clear above the triangle",
        Intersection.Test(new Capsule(new(0f, 2f, 0f), new(0f, 4f, 0f), 0.35f), mesh));

    // Standing ON it: the lower cap is 0.2 below the surface.
    t.ExpectHit("capsule resting on the face",
        Intersection.Test(new Capsule(new(0f, 0.15f, 0f), new(0f, 1.9f, 0f), 0.35f), mesh),
        expectedDepth: 0.2f);

    // Beside an edge, within a radius of it.
    t.ExpectHit("capsule against an edge",
        Intersection.Test(new Capsule(new(0f, -0.9f, 1.2f), new(0f, 0.9f, 1.2f), 0.35f), mesh),
        expectedMinDepth: 0.01f);

    t.ExpectMiss("capsule beyond an edge by more than its radius",
        Intersection.Test(new Capsule(new(0f, -0.9f, 1.5f), new(0f, 0.9f, 1.5f), 0.35f), mesh));

    // THE ONE THAT MATTERS. A capsule whose SEGMENT passes clean through the triangle's interior is
    // as intersected as anything can be — and the closest pair between a segment and a triangle is
    // zero there. A candidate set built from endpoints, vertices and edges never evaluates the
    // segment's crossing point, so every candidate is a metre away and the test reports no contact.
    // This is the impaled case, and a resolver that cannot see it lets a body sit on a wall's
    // midline for ever.
    t.ExpectHit("capsule skewered on the triangle's interior",
        Intersection.Test(new Capsule(new(0f, -1f, 0f), new(0f, 1f, 0f), 0.35f), mesh),
        expectedMinDepth: 0.3f);

    // A wide, short capsule lying flat on the face — the closest feature is the segment's interior
    // against the face interior, which endpoint tests only cover because it is parallel.
    t.ExpectHit("capsule lying along the face",
        Intersection.Test(new Capsule(new(-0.4f, 0.2f, -0.2f), new(0.4f, 0.2f, -0.2f), 0.35f), mesh),
        expectedDepth: 0.15f);
}

// ── Capsule sweep vs plane, which is exact ───────────────────────────────────────────────────────
//
// The floor is y = 0 with the normal up, so the solid half is below it (Plane's own convention).
{
    var floor = new Plane(Vector3.UnitY, 0f);

    // A capsule whose lower cap is 1 m clear, falling 4 m. It touches after 1 of those 4 metres.
    var falling = new Capsule(new(0f, 1.35f, 0f), new(0f, 3.0f, 0f), 0.35f);
    var hit = Intersection.Sweep(falling, new Vector3(0f, -4f, 0f), floor);
    t.ExpectTrue("a falling capsule meets the floor", hit is not null);
    t.ExpectClose("and does it a quarter of the way through the step", hit?.Time ?? -1f, 0.25f);
    t.ExpectClose("touching down at y = 0", hit?.Point.Y ?? -1f, 0f);
    t.ExpectClose("with the floor's normal", hit?.Normal.Y ?? 0f, 1f);

    // The same fall, one centimetre short of reaching.
    t.ExpectMiss("a fall that stops short does not touch",
        Intersection.Sweep(falling, new Vector3(0f, -0.99f, 0f), floor));

    t.ExpectMiss("a capsule moving away never touches",
        Intersection.Sweep(falling, new Vector3(0f, 4f, 0f), floor));

    t.ExpectMiss("nor does one sliding parallel to it",
        Intersection.Sweep(falling, new Vector3(6f, 0f, 0f), floor));

    // Already through the surface: a sweep answers with the DISCRETE contact at time 0, which is
    // what CollisionHit's convention says and what a resolver needs to depenetrate rather than
    // advance.
    var sunk = new Capsule(new(0f, 0.2f, 0f), new(0f, 1.9f, 0f), 0.35f);
    var sunkHit = Intersection.Sweep(sunk, new Vector3(0f, -1f, 0f), floor);
    t.ExpectTrue("a capsule already through the floor reports a contact", sunkHit is not null);
    t.ExpectClose("at time zero", sunkHit?.Time ?? -1f, 0f);
    t.ExpectClose("with the overlap as depth", sunkHit?.Depth ?? -1f, 0.15f);
}

// ── Capsule sweep vs triangle, checked against the exact case ────────────────────────────────────
//
// THE POINT OF THIS SECTION. The triangle sweep converges rather than solving, so the only honest
// way to state its accuracy is to give it a problem the plane sweep answers in closed form and
// compare. A big triangle in the y = 0 plane IS a plane, as far as a capsule landing in the middle
// of it is concerned.
{
    var floor = new Plane(Vector3.UnitY, 0f);
    var bigTriangle = new Triangle(new(-50f, 0f, -50f), new(50f, 0f, -50f), new(0f, 0f, 50f));

    var falling = new Capsule(new(0f, 1.35f, 0f), new(0f, 3.0f, 0f), 0.35f);
    var motion = new Vector3(0f, -4f, 0f);

    var exact = Intersection.Sweep(falling, motion, floor);
    var converged = Intersection.Sweep(falling, motion, bigTriangle);

    t.ExpectTrue("the triangle sweep finds the same contact the plane sweep does", converged is not null);
    t.ExpectClose("to within a tenth of a millimetre of the step",
        converged?.Time ?? -1f, exact?.Time ?? -2f, 1e-4f);
    t.ExpectClose("and agrees about the normal", converged?.Normal.Y ?? 0f, 1f, 1e-3f);

    // CONSERVATIVE, AND THE DIRECTION MATTERS. Advancement only ever undershoots, so the answer is
    // at or before the true contact — never after. A body that stops a hair early is a body beside
    // a wall; one that stops a hair late is inside it.
    t.ExpectTrue("and is never LATE — advancement undershoots by construction",
        (converged?.Time ?? 1f) <= (exact?.Time ?? 0f) + 1e-6f,
        $"swept {converged?.Time:0.000000} vs exact {exact?.Time:0.000000}");

    // A sweep that ends before it arrives.
    t.ExpectMiss("a step that falls short of the triangle misses",
        Intersection.Sweep(falling, new Vector3(0f, -0.9f, 0f), bigTriangle));

    // Zero motion collapses to the discrete question rather than dividing by it.
    t.ExpectMiss("a capsule that does not move and is not touching misses",
        Intersection.Sweep(falling, Vector3.Zero, bigTriangle));
    t.ExpectHit("a capsule that does not move and IS touching reports the overlap",
        Intersection.Sweep(new Capsule(new(0f, 0.2f, 0f), new(0f, 1.9f, 0f), 0.35f), Vector3.Zero, bigTriangle),
        expectedDepth: 0.15f);
}

// ── It cannot tunnel, and the control that makes that mean something ─────────────────────────────
//
// A 0.1 m wall and a capsule crossing it at 100 m/s in a 16 ms step: 1.6 m of travel through a
// tenth of a metre of geometry. This is the case a discrete test cannot answer at any sampling
// rate, and the assertion below is in TWO halves on purpose — the sweep hits, AND the discrete
// test at the destination misses. Without the second half the first would pass just as well on a
// body that was never going anywhere.
{
    var wall = new TriangleMesh3D(new[]
    {
        new Triangle(new(0f, -2f, -2f), new(0f, -2f, 2f), new(0f, 2f, 2f)),
        new Triangle(new(0f, -2f, -2f), new(0f, 2f, 2f), new(0f, 2f, -2f)),
    });

    var body = new Capsule(new(-1f, -0.4f, 0f), new(-1f, 0.4f, 0f), 0.3f);
    var motion = new Vector3(1.6f, 0f, 0f);

    var arrived = new Capsule(body.PointA + motion, body.PointB + motion, body.Radius);
    t.ExpectMiss("the control: a discrete test at the far side of the step sees nothing",
        Intersection.Test(arrived, wall));

    var swept = Intersection.Sweep(body, motion, wall);
    t.ExpectTrue("but the sweep stops the body at the wall", swept is not null);

    // The capsule's surface starts 0.7 m from the wall and travels 1.6, so contact is at 0.4375.
    t.ExpectClose("at the moment its surface reaches the surface", swept?.Time ?? -1f, 0.7f / 1.6f, 1e-3f);
    t.ExpectTrue("with a normal pointing back the way it came",
        (swept?.Normal.X ?? 0f) < -0.99f, $"normal {swept?.Normal}");
}

// ── The earliest contact, not the deepest ────────────────────────────────────────────────────────
{
    // Two walls, one behind the other. A body crossing both must be stopped by the near one.
    var near = new Triangle(new(1f, -2f, -2f), new(1f, -2f, 2f), new(1f, 2f, 2f));
    var far = new Triangle(new(3f, -2f, -2f), new(3f, -2f, 2f), new(3f, 2f, 2f));
    var mesh = new TriangleMesh3D(new[] { far, near });   // far FIRST, so order cannot be what decides

    var body = new Capsule(new(-1f, -0.4f, 0f), new(-1f, 0.4f, 0f), 0.3f);
    var hit = Intersection.Sweep(body, new Vector3(6f, 0f, 0f), mesh);

    t.ExpectTrue("a sweep through two walls finds one", hit is not null);
    t.ExpectClose("and it is the NEAR one, whichever order the mesh holds them in",
        hit?.Point.X ?? -99f, 1f, 1e-3f);
}

// ── A surface you are not moving into does not obstruct you ──────────────────────────────────────
//
// A body resting on the floor is in contact with it at time zero, and stays in contact for as long
// as it rests there. A sweep that answers "the floor, now" to a horizontal step is technically
// telling the truth and practically useless: a resolver spends its whole iteration budget
// deflecting a motion that was already tangential and arrives nowhere. Found exactly that way — a
// 200-step walk that did not move.
{
    var ground = new Triangle(new(-50f, 0f, -50f), new(50f, 0f, -50f), new(0f, 0f, 50f));

    // Resting exactly on it: the lowest point of the capsule is at y = 0.
    var resting = new Capsule(new(0f, 0.35f, 0f), new(0f, 1.45f, 0f), 0.35f);

    t.ExpectMiss("a body resting on a surface is not obstructed by it when it walks along",
        Intersection.Sweep(resting, new Vector3(1f, 0f, 0f), ground));

    t.ExpectTrue("but is obstructed the moment it moves INTO it",
        Intersection.Sweep(resting, new Vector3(0f, -1f, 0f), ground) is not null);

    t.ExpectMiss("and is free to leave",
        Intersection.Sweep(resting, new Vector3(0f, 1f, 0f), ground));

    // Descending while travelling: still a contact, because the motion approaches.
    var hit = Intersection.Sweep(resting, new Vector3(1f, -0.2f, 0f), ground);
    t.ExpectTrue("a body walking downhill into a surface still meets it", hit is not null);
    t.ExpectClose("at the very start of the step, since it was already touching", hit?.Time ?? -1f, 0f, 1e-3f);

    // OVERLAP IS A DIFFERENT QUESTION and is deliberately not filtered: a body inside a solid has to
    // be pushed out whichever way it happens to be moving. That answer comes from the discrete test,
    // which a resolver runs before it sweeps.
    var sunk = new Capsule(new(0f, 0.2f, 0f), new(0f, 1.3f, 0f), 0.35f);
    t.ExpectHit("a body already inside a surface still reports the overlap, whatever its motion",
        Intersection.Test(sunk, new TriangleMesh3D(new[] { ground })), expectedDepth: 0.15f);
}

t.PrintSummary();
return t.Failed;

// <b>The domain half only.</b> A capsule sweep's hit-or-miss is this suite's vocabulary; the tally,
// the OK/FAIL printing and the exit count come from Blix.Verify, which learns no geometry.
//
// Extension methods rather than a subclass: the tally is sealed on purpose, because inheriting it
// would make every suite a KIND of thing instead of a caller of one.
static class Physics3DChecks
{
    public static void ExpectHit(this TestRunner t, 
        string label, CollisionHit? actual,
        float? expectedDepth = null, float? expectedMinDepth = null, float tolerance = 1e-3f)
    {
        if (actual is null) { t.Fail(label, "expected hit, got null"); return; }
        var hit = actual.Value;
        if (expectedDepth is float d && MathF.Abs(hit.Depth - d) > tolerance)
        {
            t.Fail(label, $"depth {hit.Depth:0.0000} != expected {d:0.0000}");
            return;
        }
        if (expectedMinDepth is float md && hit.Depth < md)
        {
            t.Fail(label, $"depth {hit.Depth:0.0000} below floor {md:0.0000}");
            return;
        }
        t.Pass(label);
    }

    public static void ExpectMiss(this TestRunner t, string label, CollisionHit? actual)
    {
        if (actual is not null)
        {
            t.Fail(label, $"expected miss, got hit at {actual.Value.Point} depth {actual.Value.Depth:0.0000}");
            return;
        }
        t.Pass(label);
    }
}
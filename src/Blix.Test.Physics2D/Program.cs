using Blix.Verify;
using System.Numerics;
using Blix;
using Blix.Geometry;

// CLI test harness for the 2D collision math. Each primitive pair is exercised with
// (a) a known overlap, (b) a known disjoint case, and (c) where it makes sense, an
// edge case (touching exactly, concentric, AABB-aligned-with-OBB, etc.). Raycasts
// likewise check hit + miss. Failure printed as RED FAIL with the expected vs
// actual, success as GREEN OK. Exits non-zero if any case fails — wire-in to CI by
// running this binary and checking the exit code.

var t = new TestRunner();

// -- Bounds2 vs Bounds2 ---------------------------------------------------------
t.ExpectHit("AABB-AABB overlap",
    Intersection2D.Test(new Bounds2(new(0, 0), new(2, 2)), new Bounds2(new(1, 1), new(3, 3))),
    expectedDepth: 1.0f);
t.ExpectMiss("AABB-AABB disjoint",
    Intersection2D.Test(new Bounds2(new(0, 0), new(1, 1)), new Bounds2(new(2, 2), new(3, 3))));
t.ExpectMiss("AABB-AABB touch (edge)",
    Intersection2D.Test(new Bounds2(new(0, 0), new(1, 1)), new Bounds2(new(1, 0), new(2, 1))));

// -- Circle vs Circle -----------------------------------------------------------
t.ExpectHit("Circle-Circle overlap",
    Intersection2D.Test(new Circle(new(0, 0), 1.0f), new Circle(new(1.5f, 0), 1.0f)),
    expectedDepth: 0.5f);
t.ExpectMiss("Circle-Circle disjoint",
    Intersection2D.Test(new Circle(new(0, 0), 1.0f), new Circle(new(3, 0), 1.0f)));
t.ExpectHit("Circle-Circle concentric (degenerate normal)",
    Intersection2D.Test(new Circle(new(0, 0), 1.0f), new Circle(new(0, 0), 1.0f)),
    expectedDepth: 2.0f);

// -- Circle vs Bounds2 ----------------------------------------------------------
t.ExpectHit("Circle-AABB overlap (corner)",
    Intersection2D.Test(new Circle(new(2, 2), 1.5f), new Bounds2(new(0, 0), new(1, 1))),
    expectedDepth: 1.5f - MathF.Sqrt(2.0f));
t.ExpectMiss("Circle-AABB disjoint",
    Intersection2D.Test(new Circle(new(5, 5), 1.0f), new Bounds2(new(0, 0), new(1, 1))));
t.ExpectHit("Circle-AABB center-inside",
    Intersection2D.Test(new Circle(new(0.5f, 0.5f), 0.25f), new Bounds2(new(0, 0), new(1, 1))),
    expectedDepth: 0.75f);
t.ExpectHit("AABB-Circle order (flipped normal)",
    Intersection2D.Test(new Bounds2(new(0, 0), new(1, 1)), new Circle(new(2, 2), 1.5f)),
    expectedDepth: 1.5f - MathF.Sqrt(2.0f));

// -- Capsule2D vs Capsule2D -----------------------------------------------------
t.ExpectHit("Capsule-Capsule overlap (parallel)",
    Intersection2D.Test(
        new Capsule2D(new(0, 0), new(2, 0), 0.5f),
        new Capsule2D(new(0, 0.8f), new(2, 0.8f), 0.5f)),
    expectedDepth: 0.2f);
t.ExpectMiss("Capsule-Capsule disjoint",
    Intersection2D.Test(
        new Capsule2D(new(0, 0), new(2, 0), 0.5f),
        new Capsule2D(new(0, 5), new(2, 5), 0.5f)));
t.ExpectHit("Capsule-Capsule crossing",
    Intersection2D.Test(
        new Capsule2D(new(-1, 0), new(1, 0), 0.1f),
        new Capsule2D(new(0, -1), new(0, 1), 0.1f)),
    expectedDepth: 0.2f);

// -- Circle vs Capsule2D --------------------------------------------------------
// Circle at (0, 0.8) r=0.5 -> distance 0.8 to segment, sum of radii 1.0, overlap 0.2.
t.ExpectHit("Circle-Capsule overlap",
    Intersection2D.Test(new Circle(new(0, 0.8f), 0.5f), new Capsule2D(new(-1, 0), new(1, 0), 0.5f)),
    expectedDepth: 0.2f);
t.ExpectMiss("Circle-Capsule disjoint",
    Intersection2D.Test(new Circle(new(0, 3), 0.5f), new Capsule2D(new(-1, 0), new(1, 0), 0.5f)));

// -- Capsule2D vs Bounds2 -------------------------------------------------------
t.ExpectHit("Capsule-AABB overlap",
    Intersection2D.Test(new Capsule2D(new(-1, 0.5f), new(1, 0.5f), 0.6f), new Bounds2(new(0, 0), new(2, 2))),
    expectedMinDepth: 0.0f);
t.ExpectMiss("Capsule-AABB disjoint",
    Intersection2D.Test(new Capsule2D(new(-5, -5), new(-3, -5), 0.5f), new Bounds2(new(0, 0), new(1, 1))));

// -- OrientedBounds2 ------------------------------------------------------------
var obbZero = new OrientedBounds2(new(0, 0), 0.0f, new(1, 1));
var obbRot45 = new OrientedBounds2(new(0, 0), MathF.PI / 4.0f, new(1, 1));
t.ExpectHit("OBB-OBB axis-aligned (same rotation)",
    Intersection2D.Test(obbZero, new OrientedBounds2(new(1, 0), 0.0f, new(1, 1))),
    expectedDepth: 1.0f);
t.ExpectMiss("OBB-OBB disjoint",
    Intersection2D.Test(obbZero, new OrientedBounds2(new(5, 5), MathF.PI / 4.0f, new(1, 1))));
t.ExpectHit("OBB-OBB rotated overlap",
    Intersection2D.Test(obbZero, obbRot45),
    expectedMinDepth: 0.0f);

// -- OBB vs AABB ----------------------------------------------------------------
t.ExpectHit("OBB-AABB axis-aligned overlap",
    Intersection2D.Test(obbZero, new Bounds2(new(0.5f, -1), new(2.5f, 1))),
    expectedDepth: 0.5f);
t.ExpectMiss("OBB-AABB disjoint",
    Intersection2D.Test(obbZero, new Bounds2(new(5, 5), new(7, 7))));

// -- Circle vs OBB --------------------------------------------------------------
t.ExpectHit("Circle-OBB overlap",
    Intersection2D.Test(new Circle(new(1.5f, 0), 1.0f), obbZero),
    expectedDepth: 0.5f);
t.ExpectMiss("Circle-OBB disjoint",
    Intersection2D.Test(new Circle(new(5, 0), 1.0f), obbZero));
// Circle at (1,1) in world maps to (sqrt(2),0) in OBB-local; that's 0.414 past the
// AABB x edge, so a radius-1 circle overlaps by ~0.586.
t.ExpectHit("Circle-rotated-OBB overlap",
    Intersection2D.Test(new Circle(new(1.0f, 1.0f), 1.0f), obbRot45),
    expectedMinDepth: 0.0f);

// -- Capsule vs OBB -------------------------------------------------------------
t.ExpectHit("Capsule-OBB overlap",
    Intersection2D.Test(new Capsule2D(new(-1, 0), new(2, 0), 0.5f), obbZero),
    expectedMinDepth: 0.0f);
t.ExpectMiss("Capsule-OBB disjoint",
    Intersection2D.Test(new Capsule2D(new(5, 5), new(7, 7), 0.5f), obbZero));

// -- LineMesh2D -----------------------------------------------------------------
var lineMesh = new LineMesh2D(new[]
{
    new Segment2D(new(0, 0), new(10, 0)),    // ground at y=0
    new Segment2D(new(0, 5), new(0, -5)),    // wall at x=0
    new Segment2D(new(10, 0), new(10, 5)),   // wall at x=10
});
t.ExpectHit("Circle-LineMesh hits ground",
    Intersection2D.Test(new Circle(new(5, 0.3f), 0.5f), lineMesh),
    expectedDepth: 0.2f);
t.ExpectMiss("Circle-LineMesh miss",
    Intersection2D.Test(new Circle(new(5, 5), 0.5f), lineMesh));
t.ExpectHit("Capsule-LineMesh hits wall",
    Intersection2D.Test(new Capsule2D(new(-0.3f, 1), new(-0.3f, 3), 0.5f), lineMesh),
    expectedMinDepth: 0.0f);
t.ExpectHit("AABB-LineMesh hits ground",
    Intersection2D.Test(new Bounds2(new(4, -0.5f), new(6, 0.5f)), lineMesh),
    expectedMinDepth: 0.0f);
t.ExpectHit("OBB-LineMesh hits ground",
    Intersection2D.Test(new OrientedBounds2(new(5, 0), 0.0f, new(1, 0.5f)), lineMesh),
    expectedMinDepth: 0.0f);

// -- Raycasts --------------------------------------------------------------------
t.ExpectRayHit("Ray-Circle hit",
    Intersection2D.Raycast(new Ray2D(new(-5, 0), new(1, 0)), new Circle(new(0, 0), 1.0f)),
    expectedTime: 4.0f);
t.ExpectRayMiss("Ray-Circle miss",
    Intersection2D.Raycast(new Ray2D(new(-5, 5), new(1, 0)), new Circle(new(0, 0), 1.0f)));
t.ExpectRayHit("Ray-AABB hit",
    Intersection2D.Raycast(new Ray2D(new(-5, 0.5f), new(1, 0)), new Bounds2(new(0, 0), new(2, 2))),
    expectedTime: 5.0f);
t.ExpectRayMiss("Ray-AABB miss",
    Intersection2D.Raycast(new Ray2D(new(-5, 5), new(1, 0)), new Bounds2(new(0, 0), new(2, 2))));
t.ExpectRayHit("Ray-Capsule end-cap hit",
    Intersection2D.Raycast(new Ray2D(new(-5, 0), new(1, 0)), new Capsule2D(new(0, 0), new(2, 0), 0.5f)),
    expectedTime: 4.5f);
t.ExpectRayHit("Ray-OBB hit",
    Intersection2D.Raycast(new Ray2D(new(-5, 0), new(1, 0)), obbZero),
    expectedTime: 4.0f);
t.ExpectRayHit("Ray-OBB rotated hit",
    Intersection2D.Raycast(new Ray2D(new(-5, 0), new(1, 0)), obbRot45),
    expectedTime: 5.0f - MathF.Sqrt(2.0f));
t.ExpectRayHit("Ray-LineMesh hit (ground)",
    Intersection2D.Raycast(new Ray2D(new(5, 3), new(0, -1)), lineMesh),
    expectedTime: 3.0f);
t.ExpectRayMiss("Ray-LineMesh miss",
    Intersection2D.Raycast(new Ray2D(new(20, 3), new(0, -1)), lineMesh));

// -- CollisionWorld2D integration ------------------------------------------------
var world = new CollisionWorld2D<string>();
world.Add("ground", new Bounds2(new(-10, -0.1f), new(10, 0)));
world.Add("ball", new Circle(new(5, 5), 0.5f));
world.Add("obstacle", new OrientedBounds2(new(0, 2), MathF.PI / 6.0f, new(1, 0.5f)));

var rc = world.Raycast(new Ray2D(new(5, 5), new(0, -1)));
t.ExpectTrue("World raycast hits closest", rc is { Owner: "ball" });

var overlapResults = new List<CollisionContact2D<string>>();
world.Overlap(new Circle(new(0, 2), 1.0f), overlapResults);
t.ExpectTrue("World overlap finds OBB", overlapResults.Any(c => c.Owner == "obstacle"));

t.PrintSummary();
Environment.Exit(t.Failed);

// ---------------------------------------------------------------------------------

// <b>The domain half, and only the domain half.</b> Hit-and-miss against a collision result is this
// suite's vocabulary and belongs here; the tally, the OK/FAIL printing and the exit count are the
// same in every judge in this tree and come from Blix.Verify. A runner that knew what a raycast was
// would be the framework this deliberately is not.
//
// <b>Extension methods, not a subclass.</b> The tally is sealed on purpose: a base class is the
// one shape that would turn a shared helper into a framework, because inheriting it makes every
// suite a KIND of thing rather than a caller of one. Hit-and-miss is this suite's vocabulary,
// expressed on top of Expect, and it reaches the runner the same way any caller would.
static class Physics2DChecks
{
    public static void ExpectHit(this TestRunner t, string label, CollisionHit2D? actual, float? expectedDepth = null, float? expectedMinDepth = null, float tolerance = 1e-3f)
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

    public static void ExpectMiss(this TestRunner t, string label, CollisionHit2D? actual)
    {
        if (actual is not null) { t.Fail(label, $"expected miss, got hit at {actual.Value.Point} depth {actual.Value.Depth:0.0000}"); return; }
        t.Pass(label);
    }

    public static void ExpectRayHit(this TestRunner t, string label, CollisionHit2D? actual, float expectedTime, float tolerance = 1e-3f)
    {
        if (actual is null) { t.Fail(label, "expected hit, got null"); return; }
        var hit = actual.Value;
        if (MathF.Abs(hit.Time - expectedTime) > tolerance)
        {
            t.Fail(label, $"time {hit.Time:0.0000} != expected {expectedTime:0.0000}");
            return;
        }
        t.Pass(label);
    }

    public static void ExpectRayMiss(this TestRunner t, string label, CollisionHit2D? actual) => t.ExpectMiss(label, actual);

}

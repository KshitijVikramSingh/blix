# Character arc — plan

> The first arc aimed at Spear's own domain: **motion, contact, camera, combat**.
> `plan-blix-chassis.md` §11 ended by inverting its premise — *"Blix's theory cannot be
> proved any further until Spear exists"* — and named the vocabulary deliberately left
> uninvented for a first Spear application to pressure out: **world, editor, character
> controller, IK**. This arc takes the third.

Labs first, the game gated behind them. One library, **three isolated subjects** and the
instruments they need: a room with no character, a character with no room, and then the
two together. Isolation first is the whole method — when a body walks up a ramp with the
wrong clip playing, the only way to know which half is wrong is to have watched each half
alone.

---

## What already exists

Not "what an engine usually has" — what is in this tree, checked rather than remembered:

- **3D geometry.** `Blix.Geometry` carries `Bounds3`, `BoundingSphere`, `Capsule`,
  `OrientedBounds3`, `Plane`, `Triangle`, `TriangleMesh3D`, `Ray`, and a 1,399-line
  `Intersection` with all-pairs discrete tests, six raycasts and five sweeps.
  `CollisionResponse` has slide, bounce and a two-body inverse-mass reflection.
- **Motion.** `PhysicsHost3D` — 46 lines, gravity into velocity, velocity into position,
  fixed step. It says in as many words that detection and response are the owner's job.
- **Containment.** `CollisionWorld3D<T>` — owner-tagged static colliders, per-shape lists,
  layers/masks, `Raycast` and `Overlap`. No broadphase, stated as a swap-later decision.
- **The animation chain, complete.** `AnimationClip.Sample` → `Pose` →
  `Skeleton.ComputeBonePalette` → `BonePalette` → skinned shader, plus `ClipPlayer`,
  `PoseBlend`, `PoseDelta`, `RootMotion` (`Between`/`AcrossLoop`/`Strip`) and
  `BonePaletteSet` for N bodies in one draw — all landed by the animation arc.
- **The lab family.** `Blix.Labs.Toolchain` — one library, three executables, reflected
  binding model, gizmos that depth-test, capture to PNG, `--frames-out` sequences, a
  headless probe with an exit code, and a root that constructs its parts.

So this arc writes very little new *vocabulary*. What it writes is the first consumer of a
large amount of vocabulary that has never had one.

---

## The findings that start it

**F1 — the 3D collision math is original, unconsumed and untested.**
`git log --follow src/Blix.Geometry/Intersection.cs` returns **one commit: the initial
one**. Measured consumption across the whole tree:

| Claim | Measured |
| --- | --- |
| `Intersection.Sweep` callers outside `Blix.Geometry` | **0** |
| `new Capsule(...)` / `new TriangleMesh3D(...)` outside `Blix.Geometry` | **0** |
| 3D intersection calls across all three test suites | **1** (`Raycast(ray, ground)`) |
| `Blix.Test.Physics2D` — `Intersection2D` calls / `Intersection` calls | **41 / 0** |

`docs/blix.md` describes `Intersection2D` as "mirrors the 3D pattern… pressure-tested by
the `Blix.Test.Physics2D` CLI harness (43 cases)". The mirror is tested; the original it
mirrors is not. Nothing in Blix has ever collided with a capsule or a triangle mesh.

This is the same category as the animation arc's *"`PoseBlend` and `PoseDelta` had unit
tests and no callers outside them, which is its own kind of unverified"* and the view
arc's *"`ViewPicking` gets its first caller since it was built"* — one degree worse,
because here there are not even the tests.

**F2 — nothing in Blix has ever moved *through* a world.** Runner is `PhysicsHost3D`'s
only consumer: it clamps Y against a ground constant and uses sphere `Overlap` for
pickups. TankArena overlaps AABBs. "Sweep, stop at time of impact, deflect the remainder,
repeat" does not exist at any layer, and neither does the question that forces it.

**F3 — the animation state machine already exists three times, unnamed.** RTSGame carries
the complete one inside `RtsGameLoop.cs`: `BodyAction`, a pure `BodyActions.For`, a
**dwell table** (`heldAction` / `heldUntil`) so nothing alternates at frame rate, an
**interrupt set** (`Flinch`, `Fall` may cut in early), and `GaitOf` running **three
clocks** — distance for locomotion against a stride *measured off the walk clip*,
act-charge placed against a *measured impact frame* for strikes, and wall time with a
per-body offset for everything else. Runner has the two-line version
(`grounded ? run : jump`, rate tracking speed). Bulwark has the crowd version
(phase-staggered walk, one-shot death).

Three consumers, one set of decisions each is making alone — §4's bar, met before the arc
starts. And the bug history is the argument for the *instrument* rather than the
abstraction: §211's *"our farmers are statues and our woodcutters are on cocaine"* and
*"each animation only plays until the impact frame and then resets"* were both **reported
from the chair**. Every transition fault this project has had was found by a person
watching, because nothing could show a transition.

**F4 — the uniform hazard is a source-side problem the arena did not touch.** Verified in
the backend: `WriteUniformsAcrossSets` runs at **Execute**, iterating `pass.Commands`, so
it reads the caller's `Matrix4x4[]` long after the draw was recorded. `PushConstants` is
copied on the record; `Uniforms` and `Textures` are not. The per-draw uniform arena fixed
the **destination** (each distinct block gets its own slice, bound by dynamic offset); a
caller reusing one scratch array still writes the same final contents into every slice.
The only `Matrix4x4ArrayUniform` consumer in the tree is Sponza's per-pass cascade
view-projections, which is why the note in `RenderCommand.cs` is still true and still
unexercised.

---

## The shape — one library, three subjects

```
src/Blix.Labs.Character/              the library: rooms, controller, state machine,
                                      renderer, shaders — and the three ROOTS
src/Blix.Labs.Character.Room/         contact only. No rig, no clips.
src/Blix.Labs.Character.Motion/       clips and states only. No contact, flat ground.
src/Blix.Labs.Character.Controller/   the two together.
src/Blix.Labs.Character.Probe/        headless, exit code, no device.
src/Blix.Labs.Character.Capture/      fixed-step PNG / PNG sequence of any root.
```

Each executable is a thin `Program.cs` that parses arguments, opens a window and
constructs one root — the shape `architecture.md` §"How an application is put together"
records. No mode dispatcher, no registry: *a different executable simply builds a
different root*, which is precisely what three subjects instead of one is here to prove.

**The guard against this becoming a framework.** Three apps over one library is exactly
the pressure that grows one. The bar stays as documented: into the library needs a
**second consumer**; out of a file needs only that the file stopped being readable. The
renderer, the room geometry and the shaders have three consumers on day one and belong in
the library from the start. The controller and the state machine do **not** move into
`Blix` until the arc can name who else wants them — and stage C-C is designed to find out.

---

## Prologue — the record-time freeze (engine) — **DONE**

**Outcome, and it split rather than resolving one way.** The measurement (P2) is what made the
decision takeable, and it said something no amount of reading would have:

| 120 frames, `BLIX_VK_VALIDATE=1` | uniform-bearing commands | uniform entries | array payload |
| --- | --- | --- | --- |
| toolchain lab | 2,880 | 10,560 | **0 B** |
| VulkanHello | 120 | 120 | 0 B |
| TankArena · Bulwark · VulkanParticles · RTSGame | **0** | **0** | 0 B |

Every game passes **zero** inline uniforms — push constants and materials instead — and nothing in
the tree uses the uniform-array path at all. The note's worry that copying "costs far more than 128
bytes" was about a shape no application here has.

It also turned out the risk was narrower than the note implied: scalar and vector uniforms hold a
struct **by value** and never could move. Only the list and the three array uniforms can. So:

- **Values frozen.** `Matrix4x4ArrayUniform`, `Vector3ArrayUniform` and `FloatArrayUniform` copy on
  construction, as `PushConstants` already did. Measured cost 0 B/frame everywhere runnable.
- **Lists detected, not copied.** One allocation per draw is a price nothing here can quote — the
  lab is the only consumer of the path at all. `RenderCommandDiagnostics` fingerprints each list at
  record and the backend re-checks at execute, throwing with the uniform and the pass. **The Room
  lab is the draw-heavy consumer that will finally price the list**, which is the one open thread
  this prologue hands forward.

Section **AT** (13 assertions) pins it, verified to fail where it should. `RenderCommand.cs`'s note
and `conventions.md` §2 now describe the contract instead of deferring it.

**Owed:** VulkanSponza is the tree's only `Matrix4x4ArrayUniform` consumer and its pack is on an
unmounted SSD, so the one application whose behaviour this actually changes has not been run.

### What it was

Small, first, and it is a decision rather than a feature. The comment already says the
honest blocker is measurement.

- **P1 — make it visible.** A debug-only detector that fingerprints each uniform payload
  at record and re-checks at execute, throwing with the uniform's name and the pass's.
  `detectUniformConflicts` is the precedent for the switch and the message shape. Nothing
  has ever been able to *see* this hazard; that comes before paying for it.
- **P2 — measure.** Uniform bytes per frame that a record-time copy would cost, on the
  tree's heaviest frames: Sponza's cascades, the RTS at its worst node phase, Bulwark's
  crowd. The comment asks for exactly this number and it has never been taken.
- **P3 — decide.** Freeze on record (arrays copied in the record constructor, as
  `PushConstants` already is), or keep the detector and write the contract down. Either
  way the note in `RenderCommand.cs` stops saying *unexercised*.

**Acceptance, and it must be on a GPU.** `Blix.Test.Graphics` creates no device — it is
CPU-only — so the exercise is a capture: two draws in one frame, one program, two
*different* `mat4[]` values written from one caller-owned scratch array, which must come
back as two different pictures. The Room lab is the natural place to stage it. A
Test.Graphics section pins the detector's fingerprint, not the rendering.

---

## Room — contact, with nothing else in the picture

### Status — R-A is in

`Blix.Labs.Character` (the library, its own shaders and renderer), `.Room` (the viewer), `.Probe`
(headless, 24 checks) and `.Capture` (reproducible PNGs). 748 triangles, 14 parts, 33 closed solids.
`tools/run-room.sh`, `tools/run-room-capture.sh`.

**The instrument R-A produced** is the slope tint: every surface shaded by the normal the *collider*
reads, unlit, so the same slope is the same colour in sun and in shadow. The ramp fan comes out as a
monotone gradient at 5/15/30/45/60°, the stairs as green treads and red risers, and the dome as a
continuous sweep from green at the apex to orange at its foot.

**Found by building it, in the order found:**

1. **Closure is a property of a SOLID, not a part.** Asked per part it called the walls open — the
   four wall boxes share a vertical edge at each hall corner, so that edge has four triangles on it.
2. **The dome's seam was a hairline crack**: `cos(tau)` and `cos(0)` are different floats.
3. **A facet's slope is bracketed by its corners**, not measured against a tolerance. Two earlier
   versions used the centroid (which sits *inside* the sphere, so the error was one-sided) and then
   the mean corner radius (which the apex ring, reaching from the pole, failed by 3.6°).
4. **The ramps faced a wall.** Every probe check passed and the fan was unusable: its walkable faces
   looked west with one metre of floor in front of them. Found in a capture — five vertical backs
   and not one inclined surface. **The probe cannot catch a room that is merely useless.**
5. **The tint was multiplied by the lighting**, so the same slope read differently in shadow. A gauge
   whose reading depends on where the sun is is not a gauge; it replaces the shading now.
6. **The spawn point moved, by the probe rather than by eye** — turning the fan around put its foot
   where the spawn stood, which would have started every run of stage R-C inside a solid.

**R-A — a room whose ground truth is analytic.** Built, not imported: a flat floor, a ramp
fan (5°, 15°, 30°, 45°, 60°), stairs at 0.1 / 0.2 / 0.3 m risers, a ledge, a low beam, a
narrow gap, a curved bowl. Every answer is known in closed form before anything runs.

The render mesh and the `TriangleMesh3D` come from **one** source. The recurring lesson of
the last arc is that two things which should agree and cannot contradict each other hide
bugs for weeks; a collider authored beside its picture is that fault waiting.

### Status — R-B is in

`Blix.Test.Physics3D` (29 cases), the 3D math's first suite — a fourth engine suite, deliberately.
`Intersection` gains `Sweep(Capsule, motion, Plane)`, `Sweep(…, Triangle)` and `Sweep(…, TriangleMesh3D)`,
and the character probe grows a section where the new math and the room's claims check each other.

**The bug the suite found before it found anything else.** `TestCapsuleTriangle` has shipped since
the initial commit with an **eight-candidate** closest-pair search: two endpoints against the
triangle, three vertices against the segment, three edges against the segment. None of them can see
a segment that passes clean THROUGH the triangle's interior — every candidate is a metre away while
the true distance is zero. A capsule with a wall through its waist reported no contact at all. The
ninth candidate is the segment's crossing of the triangle's plane, added as a candidate rather than
special-cased, so when it lands inside the pair is (p, p) and the distance is zero.

**Conservative advancement, not a closed form.** The exact TOI is a root of a piecewise system whose
active polynomial changes as the closest features change, and the edge cases between those regions
are where analytic implementations get it wrong. Advancement measures the gap, steps forward by the
most the body could travel without closing it, and measures again — every step exact, the sequence
only ever undershooting. **It cannot tunnel**, and the iteration cap is a budget rather than a
correctness condition: exhaustion returns a time still before contact, so the body stops short.

**The accuracy claim is made by the exact case.** `Sweep(Capsule, motion, Plane)` is one division,
so the triangle sweep is pointed at a 100 m triangle in the same plane and required to agree to
1e-4 of a step — and, separately, to never be LATE. Verified to fail: advancing by 1.35× the gap
turns four cases red, and one of them is the contact NORMAL flipping to −1, because an overshooting
sweep does not merely mistime — it lands the body through the surface and then pushes it further in.

**Two things found by pointing the math at the room.** A drop test reads 5.4 cm high on a 30° ramp:
a capsule rests on an incline touching it UP-SLOPE of its axis, so its lowest point sits r/cos θ
above the surface, not r — which is how a straight-down ground probe decides a standing body is
falling. And the first tunnelling control was wrong rather than the sweep: the body ended *inside*
the 0.5 m wall instead of past it, and a tunnelling test whose body stops inside the obstacle is not
testing tunnelling.

**R-B — the math that does not exist.** There is no capsule sweep of any kind. Write
`Sweep(Capsule, motion, Plane)`, `Sweep(Capsule, motion, Bounds3)` and
`Sweep(Capsule, motion, TriangleMesh3D)`, and pin the discrete `Test(Capsule,
TriangleMesh3D)` that has been sitting untested since the initial commit. New suite:
**`Blix.Test.Physics3D`**, mirroring the 2D harness's `ExpectHit` / `ExpectMiss` /
`ExpectRayHit` shape — because the symmetry is the finding, and because collision math is
not graphics. (This takes the engine's suite count from three to four; that is a
deliberate change to `blix-verification-scope`, not an accident.)

**R-C — the resolver.** Sweep → advance to time of impact → deflect the remaining motion
along the contact plane → repeat, bounded. Plus depenetration for a body that starts
overlapped (single-pass is already a documented limit of the engine's response layer).
**Lab-local.** `CollisionResponse.RemoveNormalComponent` is the deflection primitive and
already exists; the *loop* is policy until something else wants it.

**R-D — rest, and the numbers only a lab can find.** Slope limit, step up and step down,
ledge behaviour, and the classic: a body on a slope below the limit must come to **actual
rest**, not micro-slide forever. These are policy — the engine says it holds no opinion —
and a lab is where the right number is found rather than guessed a second time (the
gizmo-trail precedent).

**R-E — the instruments.**
- *Capture* draws each resolver iteration: the swept capsule, the contact point and
  normal, the deflected remainder, and the residual motion left when the iteration cap
  is hit.
- *Probe* runs the controller headlessly over the room at a fixed step and asserts.

**The invariants, which are the arc's real acceptance criteria:**

1. A step never ends inside a triangle (penetration ≤ ε).
2. No tunnelling: 100 m/s at a 0.1 m wall still stops. (Discrete tests pass this by
   accident at low speed, which is why the speed is named.)
3. A 20° slope holds a body at rest, and *still* holds it 10 s later.
4. A 60° slope slides it, downhill within ε.
5. A 0.2 m step is climbed; a 0.5 m step is not.
6. Grazing a flat wall preserves tangential speed — the deflection bug that reads as
   "walking into a wall sticks you to it".
7. Same inputs, same path, bit-for-bit, at a fixed step.

**And the negative control.** A test that can only pass is not a test. With the resolver
switched off, invariants 1–6 must **fail** — verified by running it that way, the same
medicine as `--lockstep` in the instancing work and the deliberately naive loop wrap in
Section AQ.

---

## Motion — states and clips, on flat ground

**M-A — a state graph as data.** States (idle, walk, run, jump, fall, land, act) and
transitions whose conditions read a small input struct. The §4 question is *not* whether
the graph is engine-worthy — the three existing consumers disagree about where conditions
come from, which is the shape §4 names with the turret rigs. What they agree on is
underneath: **dwell, interrupts, blend-over-duration and the clock a clip runs on.**

**M-B — transitions that blend.** Cross-fade over a duration via the existing
`PoseBlend.Lerp`. Instrument: the blend weight plotted while it happens, because "it
looked like a pop" and "it was a 0.05 s blend" are different bugs.

**M-C — clocks, named.** The RTS's hardest-won knowledge, extracted from a 7,000-line
file: a **phase source** is *wall time with a per-body offset*, *distance against the
clip's own measured stride*, or *an act's charge placed against a measured impact frame*.
§211's two bugs are the acceptance test, stated as a rule: **a clip plays at the rate it
was authored at, and all that is chosen is where in it the body is.**

**M-D — the state graph you can see.** Current state, time in state, the last transition
*and which condition fired it*, live blend weights, and each active clip's clock and
phase — plus a scrolling history of the last N transitions, which `DebugTrails` already
makes possible because it survives across frames.

**M-E — a scripted input track.** No wall clock: a list of (time, input) so a capture
sequence of a transition is byte-diffable frame to frame. This is `--frames-out` pointed
at state instead of at motion, and it is the instrument the RTS never had.

**Negative control:** a state machine with dwell disabled must be *shown* to flicker —
transitions per second measured at a condition boundary — or the dwell test passes by
construction.

---

## Controller — the two together

**C-A — contact drives the states.** Speed and groundedness come out of the resolver
rather than from a guess; feet stop scuffing because the locomotion clock is distance and
the distance is now real.

**C-B — and then the clip drives the contact.** Root motion says *how far*, the room says
*where you can actually go*. `RootMotion.Strip` exists for exactly this seam, and the
animation arc already found the trap: driving the body while the root is still animated
applies the travel twice. Honest expectation — the Rogue has 4 travelling clips out of 76,
so this stage may end as a **decided-no** like view arc D. That is a legitimate output.

**C-C — the camera, and it is the §4 evidence.** A third-person follow camera that does
not pass through walls is a **second consumer of the sweep resolver**, and it is what
decides whether the resolver crosses into `Blix` or stays lab-local. This is the stage
that answers R-C's deferred question, which is why it is in the plan rather than left to
be noticed.

**C-D — acceptance, from the chair.** A person walks the room: up ramps, up stairs, off
ledges, along walls, with no scuffing and no state flicker. Every arc on this branch had
at least one bug that only a person watching could see; planning for that is cheaper than
being surprised by it a fourth time.

---

## Probe work, throughout

Headless, exit code, no device — the pattern the toolchain probe established:

- room geometry is sound: closed, consistently wound, no degenerate triangles;
- every invariant in R-E, run as a simulation rather than as a picture;
- a state graph is well-formed: reachable states, no transition whose condition can never
  fire, no dwell shorter than the blend it gates;
- every clip a graph names exists on the rig, and its measured stride / impact frame is
  finite — the class of fault `blix-cook inspect` cannot catch because it lists rather
  than judges.

---

## What this arc will not build

Constraint-solver physics · rigid-body dynamics · ragdolls · a broadphase or BVH before
something **measures** n² hurting · an animation-graph editor or a serialised graph asset ·
retargeting · IK (until Motion asks, and then lab-local first, per the animation plan's
stage E) · networking or rollback · a game.

Each is a decision two consumers would disagree about. The arc's job is to make contact
and transitions **legible**, and to name the few decisions three consumers are already
making alone — not to become a physics engine or an animation system before anything has
asked.

---

## Order, and why

P (the prologue) is independent and goes first because it is cheap, because it is a note
this branch left open, and because the Room lab is the first thing in the tree that will
record many draws with per-draw uniform values out of shared scratch.

Room → Motion → Controller, isolated before combined. Room first because contact is the
half with no existing consumer at all, and because Motion's distance clock is a guess
until something real produces the distance. Motion second because its §4 evidence is
already in hand and it needs no new math. Controller last because it is the only stage
whose faults are ambiguous between two new subsystems — and by then neither is new.

The honest stopping point is after Controller C-A: at that point Blix can move a body
through a world and animate it truthfully, which is the whole of what a character *is*.
C-B and C-C are each gated on a question the earlier stages ask, and Spear should be
pulled into existence by what they answer rather than pushed.

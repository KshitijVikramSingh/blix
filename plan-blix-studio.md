# Studio arc — plan

> **Status 2026-09-21 — active decision.** S-A through S-E are complete. S-F's
> proposed Runner migration should be accepted or retired explicitly against
> Studio's current role as an optional reference pipeline for tools.

> The bridge between **"here's Vulkan and Blix, go nuts"** and **"here's a basic 3D setup, hack your
> tools together."** Nothing in the tree occupies that gap, which is why every tool so far started
> by writing a renderer.

---

## What this is, and the distinction it rests on

**Blix ships an opinionated 3D tooling setup.** Authored, with taste, the way a design system is —
**not extracted because projects repeated themselves.**

That distinction is the whole plan, and getting it wrong is easy. Ten applications in this tree
declare ten shadow → lit → present graphs, and it is tempting to read that as a missing abstraction.
It is not. **Bulwark and TankArena look alike because we made them alike. Sponza looks different
because we made it different.** Those are choices, and none of them is asking for anything.

So there is no second consumer to wait for and nothing to prove by extraction. The bar is narrower
and harder: **does this make a tool cheap to write, and does it look right.**

### Who it is for

**Tools.** Blix's own — the rig and animation viewer, the model viewer, the cooker — and, just as
much, a project's own. The clarifying example is the **RTS map generator**: a tool living inside a
game project, which needs to show terrain and should not be inventing a render pipeline to do it.
The game it lives in is untouched.

### Who it is not for, and why that is not enforced

**Games.** By design goal and convention, not by a rule — there is no folder anyone can be refused
from, and there should not be. Sometimes a game will want it, and that is fine.

**Not being blessed is the blessing.** The moment this is canonical, every game inherits a default
it did not choose, and a look becomes a thing you have rather than a thing you decided.

**`Blix.Demos.Runner` moves onto it deliberately**, as a standing demonstration that a game *may*.
Smallest of the three candidates, and the skinned-character showcase, so it exercises the one thing
every rig tool needs. TankArena and Bulwark keep their hand-built graphs precisely as evidence that
games choose their own. The reason goes in Runner's own header rather than in this document, so the
next reader finds it where it happened.

**And `Blix.Labs.Character`'s room brings its own**, because its look *is* its argument: the slope
tint shades every surface by the normal the collider reads, so a mis-wound face reads as a floor
standing up rather than as odd lighting. A shell that handed it a nice default would be taking away
the instrument.

---

## The shell is welded to the setup — and split at one seam

Designed and shipped together, in **two projects**, divided at the one place a real dependency
divides them: **ImGui**. `Blix.Tools.Studio` names no UI toolkit and a present consumer depends on
that staying true — `Blix.Tools.Check` is a headless probe with no device and no window that links
the studio for the stage's declared look, and putting ImGui in the stage would make a tool that
judges glTF files pull a toolkit and a native binary it never calls.

That is what keeps "welded" and "the setup is pullable" both true at once. A game that wants the
look takes no ImGui.

**Revised from "one library, not two" after S-D**, which is also where the rest of this section's
original claim went: the frame layout does **not** move into the studio. See below.

---

## The ladder — what "configurable" means, rung by rung

The case that sets the shape is the map generator: **terrain is neither a model nor a rig.** So the
setup cannot be closed around its subjects the way the renderer is today —

```csharp
Render(commandList, scene, viewProjection, cameraPosition,
       StudioModel? model, Matrix4x4 modelTransform,
       StudioRig? rig, int rigInstances, ...)
```

— a signature that forces a third subject to either pretend to be one of two, or leave.

The setup owns the **stage**; a tool contributes **draws** to it.

| rung | what it means | who is here |
|---|---|---|
| **1 · Knobs** | Sun, ground, grid, exposure, shadow resolution — `[Tune]`, so every tool gets a Scene panel and `--sun-angle` free | everyone |
| **2 · Bring a draw** | Register geometry into the lit and shadow passes, using the pipelines the setup already made. **The ordinary case, not an escape hatch** | the map generator |
| **3 · Bring a pipeline** | Your own material when the standard lit shader will not do — terrain splatting, a custom look. Still on the stage; you brought your own brush | a level editor |
| **4 · Append a pass** | A selection outline, an id buffer — *after* the stage's scene passes and before presentation. The first rung that changes the stage rather than what stands on it | expected, not hypothetical |
| **5 · Replace the graph** | You have left the setup. Allowed, unsupported, and saying so plainly is what keeps 1–3 honest | nobody yet |

Rung 4 is in the first version on the strength of one prediction and it is worth recording as one:
**tooling asks to extend a graph before it asks to replace one.** If that turns out false it is one
thing to remove.

**And it appends rather than inserts, which is a statement of fact rather than a limitation
chosen.** A tool's passes are declared in the window after the stage has declared its own, and
`RenderGraph.Execute` walks declaration order — so an extension always runs after `studio.lit` and
before presentation. A true depth pre-pass, which would have to run *before* it, is not reachable
this way. That is left alone deliberately: a selection outline and an object-id buffer are both
appends, they are the pressure tooling actually applies, and the first tool that genuinely needs
something before the lit pass should be the thing that forces the next shape rather than a
speculative insertion point.

And the setup ships **ready-made contributors** for the common subjects — a model, a rig, a ground
plane — so the rig viewer writes no draw at all and the map generator writes exactly one.

---

## Stage S-A — the rename, because `Lab` names nothing — **DONE**

Every type in `Blix.Tools.Studio` carries the name; no `Lab*` type remains. Two predictions in the
list below turned out differently and are worth recording rather than quietly dropping:
`SkeletonGizmo` ended up ENGINE-side in `Blix/Gizmos` rather than on the stage, which is the better
home — a skeleton overlay is not stage furniture; and `StudioScene` never existed, because the look
it was going to carry became `StudioLook` by a different route (see S-C).


`Lab` was a folder name for experiments that were not games or demos. It is stamped on the eight
types that carry the actual taste, and it tells a reader nothing true.

`Blix.Tools.Preview` → **`Blix.Tools.Studio`**, and every `Lab*` type with it: `StudioRenderer`,
`StudioScene`, `StudioCamera`, `StudioGeometry`, `StudioModel`, `StudioRig`, `StudioObject`,
`SkeletonGizmo`, `StudioSelection`. Later, `StudioShell` and the `ModelView` / `RigView` /
`GroundView` contributors.
`RigAnimation` keeps its name: it is a subject, not stage furniture.

It sits under `Blix.Tools.` deliberately. A game referencing `Blix.Tools.Studio` then reads as
exactly the right signal — *I am using the tool setup on purpose* — and that slight awkwardness is
the convention doing its job.

## Stage S-B — open the stage — **DONE**

`IStudioView` is the contributor seam, and rung four — a tool declaring a pass of its own — is real
and EXERCISED: `--stage-selftest` declares a pass, asserts the stage recorded it, and exits non-zero
when it did not. A hook nothing calls is a hook that rots.


The renderer stops naming its subjects and starts taking contributors. Passes become things a tool
can add to (rung 4) rather than a fixed five.

## Stage S-C — the knobs, and the symmetry — **DONE, by another road**

**It landed as `StudioLook`, not as `StudioScene`, and for a different reason than this stage gave.**
The stage predicted the substrate would use its own `[Tune]` capability for symmetry's sake. What
actually forced it was the house-style arc asking where Blix is allowed to have a visual opinion —
see `docs/history/plans/plan-blix-house-style.md`. The result is the one this stage wanted (18 declared knobs,
`ITunable`, the stage recomposing on a sun change exactly as a session does on a weight change) and
it arrived carrying more than this stage asked for: `Structural`, for values read when the graph is
built, because a slider on a sample count is a slider that changes nothing.


`StudioScene` declares its look with `[Tune]` and implements `ITunable`, so the stage recomposes on
a sun-angle change exactly as a session recomposes on a weight change — **the first time the
substrate uses its own capability rather than only providing it**, which is worth watching: if it
reads badly here it will read badly for everyone.

## Stage S-D — the shell — **DONE, and smaller than this said**

The viewport panel moves into `Blix.Tools.Studio.Shell` as a widget a tool calls. **The frame layout
does not move, and neither does selection** — both argued their own way out.

The test that decided it has a mechanical form: **who calls whom.** A thing you invoke inside your
own `ImGui.Begin` is a library; a thing that invokes your code is a host. A tool's window title, its
panel order, what it says when nothing is loaded — that is its identity, and a shell that owns it
has started deciding what an application is. And `StudioSelection`'s own comment already said the
rest: *only the viewer picks — the capture has no pointer and the probe has no device.*

The widget reports rather than calling back. It sets `Clicked`; the caller casts its own ray through
its own view declaration into its own selection.

## Stage S-E — the rig and animation tool, on it — **DONE**

`Blix.Tools.View` is that tool and it is on the stage. Everything the arc predicted about it is now
true and then some: clips, masks, instances, attachments, root motion, unread channels, materials.


The first real consumer, and the reason for all of the above. It is the subject where we already
know the panels, the checks, and what a capture is worth.

## Stage S-F — Runner, deliberately — **OPEN**

Checked rather than assumed: `Blix.Demos.Runner` does not reference `Blix.Tools.Studio`. This is the
one stage of the arc still to do, and it is the statement one — a game on the studio setup, which is
the claim that the setup is not tool-only.


One game on the studio setup, as a statement rather than a cleanup.

---

## What this arc will not build

A plugin system · a scene format · an asset authoring path · a node graph · a material editor ·
a docking framework of its own · anything that writes an asset.

**And it will not make the setup canonical.** No project is required to use it, no demo is migrated
to it for tidiness, and the two games with hand-built graphs keep them.

---

## Order, and why

S-A first because it is mechanical and total, and doing it while nothing is built on top means the
one irreversible-feeling step happens cheaply. S-B before S-C because knobs on a closed stage would
have to be redesigned the moment it opens. S-D after both, since the shell is the thinnest layer and
wants the stage settled underneath it.

The honest stopping point is after S-D: at that point Blix has a 3D tooling setup a project can pull,
and whether we build another tool on it is a separate decision made with the thing in hand.

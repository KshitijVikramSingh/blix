# Studio arc — plan

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

## The shell is welded to the setup

One library, not two. What must stay true is that the **setup itself is pullable** — a project that
wants the look without the window can take it — but that does not require a seam that only one
hypothetical case uses.

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
| **4 · Add a pass** | A pre-pass, a selection outline. The first rung that changes the stage rather than what stands on it | expected, not hypothetical |
| **5 · Replace the graph** | You have left the setup. Allowed, unsupported, and saying so plainly is what keeps 1–3 honest | nobody yet |

Rung 4 is in the first version on the strength of one prediction and it is worth recording as one:
**tooling asks to extend a graph before it asks to replace one.** If that turns out false it is one
thing to remove.

And the setup ships **ready-made contributors** for the common subjects — a model, a rig, a ground
plane — so the rig viewer writes no draw at all and the map generator writes exactly one.

---

## Stage S-A — the rename, because `Lab` names nothing

`Lab` was a folder name for experiments that were not games or demos. It is stamped on the eight
types that carry the actual taste, and it tells a reader nothing true.

`Blix.Tools.Preview` → **`Blix.Tools.Studio`**, and every `Lab*` type with it: `StudioRenderer`,
`StudioScene`, `StudioCamera`, `StudioGeometry`, `StudioModel`, `StudioRig`, `StudioObject`,
`SkeletonGizmo`, `StudioSelection`. Later, `StudioShell` and the `ModelView` / `RigView` /
`GroundView` contributors.
`RigSession` keeps its name: it is a subject, not stage furniture.

It sits under `Blix.Tools.` deliberately. A game referencing `Blix.Tools.Studio` then reads as
exactly the right signal — *I am using the tool setup on purpose* — and that slight awkwardness is
the convention doing its job.

## Stage S-B — open the stage

The renderer stops naming its subjects and starts taking contributors. Passes become things a tool
can add to (rung 4) rather than a fixed five.

## Stage S-C — the knobs, and the symmetry

`StudioScene` declares its look with `[Tune]` and implements `ITunable`, so the stage recomposes on
a sun-angle change exactly as a session recomposes on a weight change — **the first time the
substrate uses its own capability rather than only providing it**, which is worth watching: if it
reads badly here it will read badly for everyone.

## Stage S-D — the shell

The viewport panel (render-to-texture into ImGui, offset and Retina picking) and the frame layout
move out of `Blix.Tools.View`'s executable, where nothing else can reach them, and into the studio.

## Stage S-E — the rig and animation tool, on it

The first real consumer, and the reason for all of the above. It is the subject where we already
know the panels, the checks, and what a capture is worth.

## Stage S-F — Runner, deliberately

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

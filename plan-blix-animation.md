# Animation arc — plan

> Follows the toolchain lab. Same shape: a lab that makes a thing visible, a probe that
> checks it headlessly, and engine extractions only where pressure has already shown.

## What already exists

The primitive chain is complete and in use:

```
AnimationClip.Sample(time, pose) → Pose.Locals → Skeleton.ComputeBonePalette → BonePalette.Matrices → shader
```

`Skeleton` (bones, parents, `CreateRestPose`), `Pose` (local `BoneTransform[]`), `BonePalette`,
`AnimationClip` with arbitrary-time sampling and root-translation tracks. `GltfImporter`
returns skeleton + clips + primitives in one bundle. `VertexPosition3NormalTextureSkin4Tangent`
carries joints and weights. `VulkanLit` has `skinned_lit.vert`; RTSGame does instanced
skinning with a palette per instance.

**So the arc is not about making animation possible.** It is about making it *legible*, and
about the handful of decisions every consumer is currently making alone.

## The finding that starts it

Three consumers perform the same five-line ritual, independently:

```csharp
pose.CopyFrom(restPose);               // Runner, Bulwark, RTSGame
clip.Sample(time % duration, pose);
skeleton.ComputeBonePalette(pose, palette);
```

The reset is not decoration: `Sample` writes only the bones a clip has tracks for, so without
it every untracked bone keeps the previous frame's pose. Three consumers, one non-obvious
decision — past the extract-under-pressure bar in `conventions.md` §4 before the arc begins.

A second finding, in the other direction: **root motion is discarded, not extracted.**
RTSGame's `StripRootMotion` reverts the root to its rest transform entirely. Nothing in the
tree takes the delta a clip travels and hands it to a game. So root-driven locomotion is
absent rather than unextracted, and the arc should not assume otherwise.

## Status — A, B and C are in

Shipped on `view-first-class`, in the toolchain lab and the engine:

- `Blix/ClipPlayer.cs` — the extracted ritual: rest reset, loop wrap, zero-duration guard,
  clock, and the root delta.
- `Blix/RootMotion.cs` — travel between two clip times, correct across the loop seam.
- `Blix.Tools.Studio/StudioRig.cs` — a rigged glTF as skeleton + clips + a set-3 palette
  buffer; `ComputeBoneWorlds`; `FindDeformBones`.
- `Blix.Tools.Studio/SkeletonView.cs` — the overlay, shared by viewer and capture.
- `Shaders/studio_skinned.vert` + `studio_skinned_shadow.vert`, both reusing the unskinned
  fragment stages.
- Viewer: `--rig`, transport, clip filter, blend/additive, bone panel, root-motion path.
- Capture: `--rig --clip --time --xray`, sampled once so a run is reproducible.
- Probe: `--rig` — hierarchy, rest-palette identity, deform census, finiteness across every
  clip, loop-seam continuity analytically *and* by integrating the real player.
- `Blix.Test.Graphics` Section **AQ** — 19 assertions; verified to fail (4 of them) against
  a deliberately naive wrap.

**What was found by doing it, not by planning it:**

1. **A palette matrix is not a joint position.** The first overlay drew a knot at the
   origin. `palette = InverseBindPose × world` is a displacement and is exactly zero at
   rest; the joint is at `world` alone. Now pinned by AQ.12 and stated in conventions §1.
2. **Half the Rogue's rig is not skinned.** 21 of 41 bones deform; the other 20 are IK
   handles parented to the root, which is why an honest overlay looks like a starburst at
   the feet. Only the vertex weights record the difference.
3. **Four of 76 clips travel.** The dodges. Walk and run are authored in place — so the
   asset the lab was built around has almost no root motion to show, which is itself the
   answer to "should Blix drive locomotion from clips".
4. **The probe was calling seven sound clips broken.** Zero-duration `*_Pose` clips are
   poses. Reporting seven problems on a good file trains a reader to ignore the output.
5. **`Matrix4x4ArrayUniform` was never the question.** The palette travels as an SSBO
   written per frame slot through `MaterialBindings`, which is what all three existing
   consumers do — so the uniform-array aliasing hazard recorded in `RenderCommand.cs` is
   still open and still unexercised. Not settled: sidestepped, deliberately, and the
   remaining hazard is named in `StudioRig.Load` (two draws in one frame sharing one palette
   material would both render the second pose).
6. **A fixed-size SSBO array, because reflection demands it.** spirv-cross reflects
   `mat4 m[]` as `block_size: 0`; `mat4 m[128]` reflects as 8192 bytes with a 64-byte
   stride, which is what `MaterialBindings` needs to size the buffer. Short writes are
   legal, so a 41-bone rig uploads 2,624 bytes.

**The extraction test, answered.** Runner migrated and is three lines and three fields
lighter. Bulwark and RTSGame did **not**: both drive many bodies off one shared pose with
times computed from a global clock, so a per-entity `ClipPlayer` would allocate a pose per
body and `ScrubTo` would re-sample twice per switch. Not smaller ⇒ not migrated, per §4.

**Still open:** D (masks) and E (IK), both waiting on a consumer, as planned.

---

## Stage A — see a pose

The lab draws the skeleton of a loaded model: bone lines parent→child, joint points, optional
per-bone axes, rest pose versus current, one selected bone with its local and world TRS.

Needs **nothing new in the engine** — `Skeleton` and `CreateRestPose` are enough, and the
debug vocabulary (lines, crosses, polylines, depth-tested) already exists. The lab already
loads `Rogue.glb` (41 bones, 76 clips) and draws it in its rest pose.

**Why first:** you cannot tell "the solver produced a bad pose" from "the clip was already
wrong" without seeing the clip. Every stage after this is debuggable because of it, and this
stage is debuggable because of the capture tool.

*Acceptance:* the Rogue's skeleton drawn over the Rogue, with a selected bone's TRS in the
panel and a capture that shows both.

## Stage B — play a clip

Scrub, pause, step one frame, set rate, pick from the clip list the probe already prints.
Then extract the ritual: a small player owning time, loop wrap, and the rest reset.

**Extraction test:** three consumers already want the same decision, so it passes §4 — but
what is being named must be the *decision* (reset-then-sample-then-palette), not a scheduler.
Migrate Runner and Bulwark only if the result is smaller than what they have; if it is not,
the extraction was wrong.

**Engine gap this hits first:** `Matrix4x4ArrayUniform` retains its array by reference —
recorded commands copy push payloads but **not** uniform arrays, and a bone palette travels as
exactly that. Two draws sharing one palette buffer would render the second pose twice. This is
recorded in `RenderCommand.cs` as the next thing to settle and Stage B is when it must be.

**Second gap:** the lab's lit pipeline takes `VertexPosition3NormalTexture`. Skinned meshes
need the Skin4 layout and a skinned vertex shader — a second pipeline in the lab, borrowing
`VulkanLit`'s approach.

*Acceptance:* a clip scrubbable frame by frame, the skeleton following it, and a capture at a
chosen time that is reproducible.

## Stage C — root motion as a delta

Sample the root's travel over an interval and hand it back rather than discarding it, correct
across the loop boundary (the wrap is where every implementation of this is wrong).

The lab shows it directly: the extracted delta as a **trail** — which exists — with the model
either driven by it or held still while the trail accumulates. A root-motion bug is a drifting
or stuttering trail, which is a thing you can see.

*Acceptance:* a walk clip's delta integrates to a straight line at constant speed across
several loops, with no step at the wrap.

## Stage D — pose composition — **RESUMED 2026-09-15, the consumer asked**

Bone masks and layered poses — aim the upper body while the legs walk. Deferred since the arc
shipped, on the grounds that masks are policy — *which bones, resolved how, blended in what space* —
and that guessing produces an API that fits nothing.

### The consumer, and exactly what it asked

The character arc's Motion stage, and the ask arrived as a dump rather than an opinion. A body in the
lab at frame 11,373: playing a one-shot chop, `speed: 0.868` at the same instant, **legs frozen
mid-swing** because a single pose source cannot walk and swing at once. That is this stage's sentence
— "aim the upper body while the legs walk" — with a number attached.

### The three questions, answered with the case in front of us

- **Which bones** — a subtree, named by its root and read from the skeleton's hierarchy. Not a list
  of indices, which would be an asset's private numbering leaking into a game's source.
- **Resolved how** — a per-bone weight in [0, 1], multiplied by the layer's own weight. One
  multiplication, no modes.
- **In what space** — local, per bone, which is what `PoseBlend.Lerp` already does.

Plus one the deferral did not name and the lab will have to find: **the falloff**. A hard subtree
boundary kinks at the waist, and softening it over a few bones up the chain is a number nobody can
derive — which makes it a dial, and the reason there is a tool.

### Weights only. The engine learns no structure at all

`Blix` composes poses from **explicit weights and masks** and knows nothing about states, links,
conditions or durations. A crossfade is the caller moving a weight; the engine never asks who decided
it or why.

This is a deliberate refusal, and it is the lesson of the character arc's M-A. That stage built a
discrete state machine modelled on RTSGame's, shipped it, and its own dump killed it in two minutes —
because §4 evidence about a *failure mode* had been allowed to choose a *model*, and the two
consumers' problems are not the same shape at all. The mechanical half — interpolate between poses
given explicit weights — has one right answer and belongs here. The contested half — which pose,
when, why — has at least two, so it waits, exactly as the resolver and these masks did.

**Whether "a state machine" is a thing Blix ever defines is not decided.** It may be data, it may be
each game's own code, it may be nothing. None of that has to be answered for this to be built.

### Status — D1 and D2 are in

#### D1

`Blix/BoneMask.cs` and a masked `PoseBlend.Lerp`, pinned by `Blix.Test.Graphics` Section **AU** (16
assertions). Weights only: no states, no links, no durations anywhere in it.

**A subtree is one forward pass**, because `Skeleton`'s constructor already requires parents to
precede children — so a bone is in the subtree exactly when it is the root or its parent already is.
No recursion and no child lists, and the guarantee is checked where the skeleton is built rather than
assumed where it is read.

**A name that is not in the rig throws, and names what is.** A mask over a misspelled bone that
quietly covered nothing would be the worst shape this bug can take: a layer that runs, costs, and
changes nothing.

**And one claim was withdrawn rather than shipped.** The masked blend skips bones whose effective
weight is 0 or 1, and the first comment said that was what made "the legs are untouched, bit for
bit" exact rather than approximate — that a slerp at weight 0 perturbs low bits. Deleting the
shortcut turned no test red: not with identity rotations, and not with a pair 150° apart, which puts
`Quaternion.Slerp` on its trigonometric branch. Both it and `Vector3.Lerp` return their input exactly
at 0 and 1. The shortcut is a **cost** decision — a quarter-mask does a quarter of the work — and the
comment now says so. The guarantee is real; that was not what provided it.

#### D2

No new executables after all. The three that exist each grew the one thing they were already the
right place for: the **viewer** got a Mask panel and a composition mode, the **capture** got
`--mask-from`/`--mask-falloff`/`--skeleton-only`/`--zoom`, and the **probe** got a `layer masks`
section. The plan said "new sibling executables rather than more flags, name the question each tool
answers" — but the question here is not a new one. "Which bones does this layer reach" is the same
question the viewer, the capture and the probe were already asking of a rig, asked of one more
thing; a fourth executable would have been a fourth copy of rig loading answering it.

**The mask is a colour on the skeleton.** Magenta at full weight, grey at none, a three-stop ramp
between, so a falloff is a thing you look at rather than a number you trust. `--skeleton-only` drops
the mesh, because the instrument is invisible inside an opaque body.

**The probe judges rather than lists.** A mask reaching every bone is a whole-body blend wearing a
mask's name; one reaching none is a layer that runs, costs and changes nothing. Both look fine on a
slider and neither survives a count. Beyond the counts it asserts what the mask *means* — an
upper-body mask reaches no bone named like a leg — and that claim survives the algorithm being
wrong, which is the point of writing it that way.

**The negative control, run.** Breaking `BoneMask.Subtree`'s walk to mark every bone with a parent
takes the Rogue from 13 of 41 bones to 39 of 41 — which passes "not everything, not nothing" and
passes the falloff check, and trips the leg check immediately, naming `upperleg.l, lowerleg.l,
foot.l, toes.l, ...`. Section **AU** goes red at the same time (5 failures). Restored, both are
green.

**And the build that lied.** The first run of that control reported 39 of 41 with *no* problem — the
probe had failed to compile (a shadowed local), `--no-build` had run yesterday's binary, and a
solution build piped through `tail -2` had shown only the elapsed time. Nothing was wrong with the
mask; the instrument was a week-old photograph. Grep the build output for `error CS`, not the tail
of it.

**Framing was a real fault, not a nicety.** The capture aimed at a hardcoded 1.4 m, which is over
the head of the Rogue — so the first zoomed mask capture centred on empty sky with the skeleton
falling off the bottom edge. The target is now the rig's own half-height, which is what `--zoom`
composes with.

### D1 — the mask, and the blend that reads it

`BoneMask` built from a subtree root, with an optional falloff up the chain; `PoseBlend.Lerp` gaining
a masked overload where the effective per-bone weight is `weight × mask[i]`. Pinned by a
`Blix.Test.Graphics` section: a mask that covers a subtree covers exactly that subtree, a weight of 0
or 1 reproduces one input exactly, and a masked blend leaves unmasked bones untouched bit for bit.

### D2 — the tooling, in the toolchain lab

Masks, layers and blends become things you can **see and check**, in
`Blix.Tools.Studio` — where `StudioRig`, `SkeletonView` and `RigSession` already are, where the
probe already judges clips, and where a rig viewer already exists. Building a second one elsewhere
was the mistake the character arc caught itself about to make.

The instrument that matters: **the mask drawn on the skeleton**, bones coloured by weight. "Which
bones" is a question no amount of arithmetic answers as well as a picture, and the falloff is a
number you tune by looking.

New sibling executables rather than more flags, on the principle that already split `blix-cook
inspect` from the probe: name the question each tool answers.

### What stage D will not build

State machines · transitions · durations · blend trees · an authoring format · retargeting. The
engine gets weights; the lab gets the tools to see them; the structure waits for a consumer.

## Stage E — analytic IK

Two-bone and look-at, **lab-local first**, not an engine primitive and never an IK graph. The
lab draws target, effector, chain, pole vector, joint axes and the error vector, plus an
iteration/error readout.

Seeing "converged to 8 mm after 5 iterations" beside the skeleton is worth more than an IK
editor, and it is what tells you whether the solver or the clip is at fault. Promote to the
engine only when a second consumer wants the same solver.

## Probe work, throughout

Headless, no device, exit code — the pattern the toolchain probe established:

- skeleton validity: parents before children, no cycles, single root
- rest pose builds a finite palette (already written, needs the `Pose`/`BonePalette` API)
- clip track coverage: which bones a clip touches, which it leaves to rest
- finiteness across a clip sampled at N points — a NaN at t=0.7 is a character folding inside out
- root-motion continuity at the loop boundary, once Stage C exists

## What this arc will not build

State machines · blend trees · retargeting · an animation editor · an authored-event UI ·
constraint IK · physics-driven secondary motion.

Each is a decision two consumers would disagree about. The arc's job is to make poses legible
and to name the few decisions every consumer is already making alone — not to become an
animation system before anything has asked for one.

## Order, and why

A → B are sequential: playback is only debuggable once a pose is visible. C follows B because a
delta needs a clock. D and E are independent of each other and both wait on a consumer — E is
the more interesting problem and A–C are what make it tractable.

The honest stopping point is after C. At that point Blix can show you a pose, play a clip and
tell you where a clip travels, which is the whole of what "is the motion right?" needs — and
anything past it should be pulled into existence by Spear rather than pushed.

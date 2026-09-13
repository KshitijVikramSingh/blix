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

## Stage D — pose composition

Bone masks and layered poses — aim the upper body while the legs walk. **Deferred until a
consumer asks.** Masks are policy: which bones, resolved how, blended in what space. Nothing
in the tree wants it yet, and guessing produces an API that fits nothing.

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

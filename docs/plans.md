# Plan status

Root `plan-*.md` files are live design records. When a plan completes, its
durable contracts move into the focused documentation and executable tests;
the working record then leaves the current tree. Git history remains the
archaeology rather than a second documentation hierarchy.

Status was reconciled against the checkout on 2026-09-27.

## Active plans

| Plan | Status | Why it remains at the root |
| --- | --- | --- |
| `plan-blix-character.md` | Active, resumed | Room is built and Motion was deleted. The arc resumes at rig residency (T-C1), which the tooling arc took and did not land: `StudioRig` still loads glTF itself and the tree still carries several independent skinned-load paths. C-A is blocked on it. |

Three records closed on 2026-09-27 and their durable decisions moved into the
focused documentation:

- **Studio arc** — S-A through S-E shipped. S-F, which proposed migrating
  `Blix.Demos.Runner` onto the studio stage, is **retired rather than built**:
  it was a statement about the pipeline, not a capability, and the evidence
  that matters already runs the other way. Recorded in
  [Architecture](architecture.md#studio-reference-rendering-pipeline).
- **Bulwark** — M0 through M4 shipped. The durable output is the four
  extraction verdicts decided against real duplication, now in
  [Demos](demos.md#bulwark). What remained were content-variety and visual
  polish items, which are not decisions and do not keep a plan alive.
- **Material response** — all five stages are now in. B had been skipped rather
  than deferred and is `Blix.Test.Graphics` section **BE**; E is per-material
  diffuse transmission in the sun-bounce bake, carried in the `.blixsky` albedo
  grid's alpha ([Assets](assets.md), [Renderer](renderer.md)). The three
  responses the arc deliberately did not build, and why, are recorded with the
  Sponza renderer rather than left in a plan.

## Deferred work with no plan of its own

Neither of these owns a design decision large enough to be a plan record, and
neither should be lost because of that. Both were found on 2026-09-27 while
closing the material-response arc.

- **One declaration for Sponza's `Frame` block.** `skybox.vert` declares a
  sparse copy of `lit.frag`'s `Frame` uniform block using hand-written absolute
  byte offsets, so adding or removing a member in `lit.frag` silently
  invalidates it. It is survivable only because the device cross-checks shared
  blocks at pipeline creation and refuses the mismatch by name — which it did,
  when `uClothOverride` was deleted and `uFog` moved 416 to 400. The fix is one
  shared include both shaders pull in, so the offsets stop existing rather than
  staying correct by vigilance. Note that `blix test` cannot catch this class of
  break: every suite is deviceless and builds no pipeline.
- **Texture authoring in `MaterialPatch`.** The patch mechanism sets scalars and
  vectors, so a scene can author `diffuseTransmissionColor` but not
  `diffuseTransmissionColorTexture`. `lit.frag` now consumes that texture and
  nothing in any pack can supply one, which makes the binding conformance
  plumbing with no way to exercise it. Teaching the patch to name a texture
  would also turn the deleted `* albedo` hack into a declared decision: a scene
  that wants per-texel transmission variation says so, instead of the renderer
  assuming it.

## Lifecycle

- **Active** means the document still owns a concrete unresolved decision,
  acceptance step, or implementation stage.
- **Completed** means it reached the stopping condition written in the plan.
  Distill its current contracts into canonical documentation and tests, then
  remove the plan. Ideas deliberately deferred until a future consumer do not
  keep it active.
- **Superseded** means another named design record now owns the same decision.
- **Abandoned** means its intended outcome was rejected without a replacement.

Any non-active record leaves the current tree after its durable decision is
captured. When a plan changes state, update this page in the same change. Do not
leave a completed development journal at the repository root or reproduce it
under a history directory; version control already preserves it.

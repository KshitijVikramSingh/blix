# Plan status

Root `plan-*.md` files are live design records. When a plan completes, its
durable contracts move into the focused documentation and executable tests;
the working record then leaves the current tree. Git history remains the
archaeology rather than a second documentation hierarchy.

Status was reconciled against the checkout on 2026-09-27.

## The active plan

| Plan | Status | Why it remains at the root |
| --- | --- | --- |
| `plan.md` | Active | The portability arc — publish, runtime closure, Windows, the CI matrix, input completion — plus the one stage the character arc still owns. Opened 2026-09-28. |

`plan-blix-character.md` was folded into it the same day: Room is built, Motion
was deleted, C-0 closed as a decided-no and C-C dropped on evidence, so what
remained (C-A, C-B, acceptance, and the undecided selection question) is one
section of the current plan rather than a record of its own.

Four records closed on 2026-09-27 and their durable decisions moved into the
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
- **Character arc** — folded into `plan.md` rather than closed; see above.

## Work found beside a plan, and finished without one

Both were found on 2026-09-27 while closing the material-response arc, were too
small to be plan records, and are **done**. They are recorded here because what
each one exposed outlived the fix.

- **One declaration for Sponza's `Frame` block.** `skybox.vert` held a sparse
  copy of `lit.frag`'s block at hand-written byte offsets; deleting a `vec4`
  moved `uFog` under it. Now one `Shaders/frame.glsl` that every shader
  includes, so the offsets are the compiler's problem. Moving the block also
  moved the `//@tune` decorators out of the file a load-time scan could see,
  which is why that metadata became a build sidecar and why an application no
  longer ships its GLSL at all.
- **Texture authoring in `MaterialPatch`.** A patch can now point
  `diffuseTransmissionColorTexture` at an image the asset already carries, which
  is what makes the renderer's spec-correct behaviour liveable: the per-texel
  variation the deleted `* albedo` hack used to provide is authored by the scene
  instead of assumed for everybody.

The durable lesson is neither of those. **Both were invisible to a green gate**,
as were two other faults the same day, because every suite here is deviceless
and builds no Vulkan pipeline. Two of the four turned out to be static
properties of the source, and are now checked as such in `Blix.Test.Graphics`
section **BG**: a shader's includes must be declared build inputs, and shaders
sharing a uniform block must agree about it. The rest still need an application
to actually boot, which is a pre-commit concern rather than a CI one.

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

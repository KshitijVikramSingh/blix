# Plan status

Root `plan-*.md` files are live design records. When a plan completes, its
durable contracts move into the focused documentation and executable tests;
the working record then leaves the current tree. Git history remains the
archaeology rather than a second documentation hierarchy.

Status was reconciled against the checkout on 2026-09-21.

## Active plans

| Plan | Status | Why it remains at the root |
| --- | --- | --- |
| `plan-blix-character.md` | Active, paused | The Room/contact instrument is built; the combined controller and animation acceptance phase is explicitly paused. |
| `plan-blix-material-response.md` | Active | Shared sheen vocabulary, probe data, and Sponza raster response exist; the planned per-material response in the light-transport bake is not yet represented by the current global translucency control. |
| `plan-blix-studio.md` | Active, decision required | Stages S-A through S-E are complete. S-F still proposes moving Runner onto Studio; current documentation treats Studio primarily as an optional tool reference pipeline, so that stage should be accepted or retired explicitly. |
| `plan-bulwark.md` | Active, acceptance/polish | The playable game and skinned crowd are built, but the plan still records visual review and content-variety follow-ups. |
| `plan-rts-game.md` | Active | This is the continuing RTS game-design and implementation record. |
| `plan-rts.md` | Active reference | The locomotion invariants and measured refusals remain referenced by the current RTS implementation and larger game plan. |

The two RTS plans belong to the separate, co-located game project. They remain
at the checkout root while that project remains here, but they are not engine
documentation debt and should move with the game if it receives its own repository.

No root plan is currently classified as abandoned. None of the six active
records is safe to call superseded without first resolving the open decision it
still carries.

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

# Plan status

Root `plan-*.md` files are live design records. Completed plans move to
`docs/history/plans/`; they remain useful archaeology, but they are not current
API documentation. The canonical descriptions of present behavior live in the
README and the focused pages under `docs/`.

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

## Completed plans

| Archived plan | Completion boundary |
| --- | --- |
| [`plan-blix-animation.md`](history/plans/plan-blix-animation.md) | Pose inspection, playback, root motion, and mask composition shipped; analytic IK remains deliberately consumer-driven. |
| [`plan-blix-attachment.md`](history/plans/plan-blix-attachment.md) | W-A through W-E completed and accepted. |
| [`plan-blix-chassis.md`](history/plans/plan-blix-chassis.md) | The application/host/view/diagnostics reshape reached its stated inversion; later view and Studio arcs continued from it. |
| [`plan-blix-cook.md`](history/plans/plan-blix-cook.md) | The declared recipe, build, provenance, reporting, standalone artifact, and project-owned recipe arc shipped; capacity-triggered texture questions remain future work, not unfinished stages. |
| [`plan-blix-house-style.md`](history/plans/plan-blix-house-style.md) | StudioLook, IBL, cascades, depth-prepass decision, MSAA, and resize instrumentation shipped. |
| [`plan-blix-inlet.md`](history/plans/plan-blix-inlet.md) | The importer/viewer completeness stages and conformance-corpus follow-up completed. |
| [`plan-blix-tooling.md`](history/plans/plan-blix-tooling.md) | App declaration, generated discovery, dispatch, CLI, project scope, and representative migrations shipped. |
| [`plan-blix-view.md`](history/plans/plan-blix-view.md) | Embedded rendering, picking, and ownership were settled; multi-view rectangles in one target remain deferred until a real consumer asks. |

## Classification rule

- **Active** means the document still owns a concrete unresolved decision,
  acceptance step, or implementation stage.
- **Completed** means it reached the stopping condition written in the plan.
  Ideas deliberately deferred until a future consumer do not keep it active.
- **Superseded** means another named design record now owns the same decision.
- **Abandoned** means its intended outcome was rejected without a replacement.

When a plan changes state, update this page and move the document in the same
change. Do not leave a completed development journal at the repository root.

# Bulwark — game #2 plan

> Working title. `Blix.Demos.Bulwark`. Branch `bulwark`.

A tower-defense game, built as a **deliberate instrument** to pressure the engine
axes Tank Arena / Runner / Pong never touched — so the duplication that shows up
tells us what (if anything) to extract. See [`docs/conventions.md`](docs/conventions.md)
§4 (demos are executable specs; extraction under repeated pressure).

## Concept + core loop

Place towers on a grid to defend a **central core** from enemies that converge from
**multiple fronts** (N/S/E/W) in coordinated waves. Earn scrap on kills, build +
upgrade between/within waves, survive N waves. A leak costs a life; lose all lives →
game over. (The core was originally a single corner-to-corner lane; moving it to the
centre with four fronts turned a shooting gallery into a real coverage problem.)

## The engine thesis (why this game)

Tank Arena / Runner / Pong are all real-time reflex action with a fixed camera.
Bulwark's shift to **pointer-driven, deliberate interaction** lights up three
confirmed blind spots — things that exist but no *game* has ever exercised:

| New axis | What pressures it | Status before Bulwark |
| --- | --- | --- |
| Picking → world placement | mouse ray → ground plane → grid cell → place tower | `ScreenPointToRay` tested (Section X), used only by Sponza debug — **0 games** |
| Pathfinding | A* on the grid, towers as blockers, dynamic re-path | nav has exactly **1 consumer** (Tank Arena `AvoidObstacles`) |
| Instanced crowds | many enemies marching one path | `InstancedBatch` drives static/props only |
| Particles as gameplay | tower shots, impacts, deaths | `ParticleBatch` is showcase-only |

## Reuse vs. build

**Lift wholesale (already proven — do not re-prove):** RenderGraph sun-shadow + HDR
· `InstancedBatch` · glTF `ImportNodes` + measured-pivot fit · **Transform3D
parenting** (a tower *is* Tank Arena's turret→barrel rig minus the hull) ·
`SetParent` detach for projectiles · SpriteBatch/Font HUD · OpenAL SFX · `[Tune]`
overlay · `--frames` headless gate.

**Build new (the learning):** ground-plane picker (`Camera3D.ScreenPointToRay` +
`Intersection.Raycast(ray, plane)`) · `Grid2D<T>` (shared by placement + nav) ·
**A\* pathfinding** with dynamic re-path · placement/build UI + ghost preview ·
RTS camera (orbit / zoom / pan) · wave director + economy.

## Milestones — proof-gate-first

- **M0 · Gate A — Pick & Place** *(done)*: angled orbit cam over a flat grid; mouse
  ray → cell; hover ghost (green placeable / red blocked); click place, right-click
  remove. Proves picking + camera + grid + build UI. Pinned by Test.Graphics
  Section AK + `--frames` smoke.
- **M0 · Gate B — Path & March** *(done)*: A* spawn→core; placed towers block;
  **reject a placement that fully walls the path**; instanced markers march the path;
  re-path on every build. Nav kept **local** (not extracted). Proven by the demo's
  `--selftest` (A* + wall-off + dynamic re-path) + `--frames` smoke.
- **M1 · Playable core** *(done)*: towers aim a Transform3D turret→barrel rig
  (LookAt + parented-barrel muzzle, lifted from TankArena, kept local) at the nearest
  enemy in range and fire homing shots; enemies have HP; a leak costs a life; a kill
  pays scrap; scrap builds towers (with cost + wall-off gating). Continuous spawn +
  3 free starter towers. Verified clean under validation; combat exercised headless.
- **M2a · Game structure + HUD** *(done)*: discrete wave director (escalating
  count/HP, win on clearing wave 5, defeat at 0 lives, ENTER to restart), tower
  upgrades (left-click an existing tower → +damage/+range, gold at max), and a
  SpriteBatch/Font HUD (wave/lives/scrap + centre banners) composited over the 3D
  pass. Verified clean under validation; waves auto-run in the `--frames` smoke.
- **M2a.1 · Multi-front map** *(done)*: core moved to the centre; four spawn fronts
  (N/S/E/W), each with its own A* path; coordinated bursts (one enemy per front per
  tick); a build that walls off *any* front is rejected. Tuned for active play (no
  free towers in interactive; tankier/faster enemies; 200 start scrap ≈ one tower
  per front). Nav self-test rewritten for the centre topology (11 checks).
- **M2b · Juice** *(done)*: impact + death bursts via `ParticleBatch` (showcase →
  gameplay mechanic; geometry-only primitive driven by a minimal caller-owned
  additive pipeline — no soft-depth/HDR/bloom), and synthesized OpenAL SFX
  (fire/death/leak), both lifted patterns. Verified clean under validation; audio
  device loads in the headless smoke. **M2 complete.**
- **M3a · Art (meshes)** *(wired; visual tuning pending)*: CC0 models fetched from
  poly.pizza — Quaternius **Turret Cannon** (base + aiming top on the rig), Quaternius
  **Robot Enemy** (bind-pose static), iPoly3D **Crystal** core — imported via
  `ImportNodes` + `BakeMerge` (lifted from TankArena) onto the shared cube pipeline,
  one `InstancedBatch` per mesh; tiles/shots/ghost stay cubes. Builds + renders clean
  under validation; fit knobs (`TowerScale`/`TurretYawFix`/`EnemyScale`/`CoreScale`/
  `CoreLift`) exposed for the playtest dial-in. Falls back to primitives if load fails.
- **M3b · Lighting & HDR** *(next)*: lift TankArena's sun-shadow + HDR RenderGraph.
- **M3c · Polish**: 2–3 tower/enemy types, README + overview.

## The extraction decisions — RESOLVED (decided on real code, not guessed)

The point of game #2 was to make these calls against real duplication. Verdicts
(by the engine owner, 2026-06-04): **extract nothing.**

1. **Navigation** — *hold (don't extract).* Two consumers (Bulwark grid-A* vs Tank
   Arena steering) but they differ enough — grid shortest-path search vs continuous
   obstacle-avoidance steering — that a shared `NavGrid`/`A*` primitive would be a
   forced abstraction over two genuinely different algorithms. Revisit only if a
   third consumer wants the *same* shape.
2. **Turret rig** — *don't extract.* The Transform3D turret→barrel + LookAt + muzzle
   pattern is something these two games happen to share, not engine substance — it's
   a few lines of composition over primitives that already exist (`Transform3D`,
   `LookAt`, `WorldPosition`). Abstracting it would add an API without adding
   capability.
3. **`Grid2D<T>`** — *not now.* One real consumer (Bulwark); a `bool[]` + `Idx`
   helper is the right size. Earns a place only if a second grid-shaped consumer
   appears.
4. **Picking** — *keep local* (1st consumer), annotated, exactly as Tank Arena did
   with nav.

This is the conventions principle working as intended: real duplication surfaced,
inspected, and **deliberately not abstracted** — see `docs/conventions.md` §4.

## Defaults

- **Square grid** (hex is a fancier later call).
- **Enemies start as simple instanced meshes**, not skinned. Skinned-mesh
  instancing is a real engine gap but we only build it if a milestone genuinely
  wants a crowd of *animated* characters — earned, not assumed.

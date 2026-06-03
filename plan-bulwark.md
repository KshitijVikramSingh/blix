# Bulwark — game #2 plan

> Working title. `Blix.Demos.Bulwark`. Branch `bulwark`.

A tower-defense game, built as a **deliberate instrument** to pressure the engine
axes Tank Arena / Runner / Pong never touched — so the duplication that shows up
tells us what (if anything) to extract. See [`docs/conventions.md`](docs/conventions.md)
§4 (demos are executable specs; extraction under repeated pressure).

## Concept + core loop

Place towers on a grid. Waves of enemies path from a spawn to your core. Hold the
line, earn scrap on kills, build + upgrade between waves, survive N waves. A leak
costs a life; lose all lives → game over.

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
- **M2 · The game**: wave director, tower upgrades, win/lose, juice (impact
  particles, SFX, HUD readouts).
- **M3 · Polish**: 2–3 tower + enemy types, CC0 art pass (KayKit Tower Defense pack
  — CC0, license-check per the sourcing rule), README + overview.

## The four extraction decisions (decided on real code, not guessed)

1. **Navigation** — grid-A* (here) vs steering-`AvoidObstacles` (Tank Arena).
   Extract a `NavGrid` + A* primitive, or rule it premature because steering ≠
   grid-search? **The headline signal.**
2. **Turret rig** — TD tower aim vs Tank Arena turret aim are both Transform3D
   turret→barrel + lead-target → a real 2nd consumer. Extract a `TurretRig`/aim
   helper?
3. **`Grid2D<T>`** — placement + nav share one grid → does it earn a spot in
   `Blix.Geometry`?
4. **Picking** — 1st consumer → almost certainly keep local + annotate "1st
   pressure," exactly as Tank Arena did with nav.

## Defaults

- **Square grid** (hex is a fancier later call).
- **Enemies start as simple instanced meshes**, not skinned. Skinned-mesh
  instancing is a real engine gap but we only build it if a milestone genuinely
  wants a crowd of *animated* characters — earned, not assumed.

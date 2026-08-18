# RTSGame — locomotion layer: state, seams, and what not to break

Status as of 2026-08-18. `--selftest` **37/37 passing**. ~9.2k lines in `src/RTSGame`.
Untracked on `main` (nothing committed yet).

Run it: `tools/run-rts-game.sh [--debug-all]`
Verify it: `dotnet run --project src/RTSGame/RTSGame.csproj -c Release -- --selftest`
Measure it: same with `--benchmark`

---

## 1. What this layer is now

One fixed 30 Hz tick, in this order:

| # | Step | Owns |
|---|---|---|
| 1 | `Congestion.Update` | decaying per-cell backpressure map |
| 2 | `ApplyCommands` | move / stop / follow / patrol / chase / flee, group creation |
| 3 | `UpdateBehaviors` + `UpdateGroupFormations` | locomotion states; cohort centroid; slot hand-off |
| 4 | `RefreshInvalidPaths` (+ `ReconsiderCongestedRoute`) | route validity and congestion re-evaluation |
| 5 | `PreparePreferredVelocities` | **intent** — path following or flow-gradient transit |
| 6 | `LocalSteeringSystem` → `ReciprocalVelocitySolver` | **velocity** — ORCA is the only controller here |
| 7 | `IntegrateMovement` | swept static check, then move |
| 8 | `CollisionSystem` | **position** — static + agent depenetration |
| 9 | `ConstrainAgentsToTerrain` | last-resort revert |
| 10 | `UpdateStuckAgents` | progress accounting, watchdog, detour grants |

**The layering rule that makes it work:** intent → velocity → position, each owning one
thing. Every regression this session came from something reaching across those.

### Routing model
Cost is **seconds**, not arbitrary units. `TerrainSurfaceRules.PathCost` is *derived* as
`1 / SpeedMultiplier` so the router and the physics agree. Congestion is expected delay in
seconds. That means route choice optimises **total travel time**, which is the premise the
resource-hauling game needs.

Group moves build **one** flow field (shared Dijkstra) and members steer down its gradient
with no stored path. Slots are claimed on arrival, not before.

---

## 2. Seams to build the game inside

These are deliberately clean; extend here rather than inside the movement code.

- **`AgentCommand` / `ApplyCommands`** — new orders (attack, gather, build, garrison) are
  new command records plus a case. Locomotion needs no knowledge of them.
- **`AgentLocomotionState`** — Idle/Move/Follow/Patrol/Chase/Flee. New behaviours slot in
  as states with an `UpdateBehaviors` case; they set `RequestedDestination` and let the
  movement stack do the rest.
- **`ColliderRole`** — `MovementSolid | Avoidance | PlacementBlocker | Interactable |
  Damageable` already exist per agent. Combat, selection, and interaction should query
  `ColliderWorld` by role rather than iterating agents.
- **`FactionRelations`** — ally/neutral/enemy with per-pair overrides. Already respected by
  avoidance and queries.
- **`AgentDefaults`** — one place for body radius/height/speed/turn rate. Unit *types*
  should extend from here, not re-litter literals (that was a real bug source).
- **`MoveGroup`** — owns target, members, slots, formation radius, cohort centroid/flow.
  Formation shapes and stances belong here.
- **`CongestionField`** — a generic "where is movement failing" map. Anything that wants to
  reason about traffic (hauling routes, threat avoidance) can read `At` /
  `DirectionalFactor`.
- **`AgentStore.Despawn`** — tombstones, ids never reused. Death/production hooks here.

**Note:** `AgentId.Value` is used directly as an array index by `FindQueueRoot`-style code
and by every diagnostic buffer. Keep ids stable; do not compact the agent array.

---

## 3. Invariants — hard-won, do not "simplify" these

Each of these was measured. Reverting one costs specific tests.

1. **Progress is measured along the route, not straight-line to the destination.**
   Anything going *round* something closes no straight-line distance while running flat
   out. `StuckSeconds` feeds congestion deposits and detour selection, so straight-line
   progress made good routes deposit obstruction and beg for reroutes.
2. **`SampleFlowGradient` probes a ring of directions; it must not use finite differences.**
   A central difference averages across a cost ridge, and between two comparable exits that
   points *along* the ridge — the whole group walks into the corner between two gates.
3. **`RelaxationPasses = 5`.** At 4, four units end up permanently wedged in the blocked
   pen (26/30 out, one red for 193 s). At 3, chokepoint overlap fails. Raising the
   *factor* instead is worse at every value tried.
4. **Contacts cancel closing velocity, and the solver aims past tangency only when already
   overlapping** (`ContactSeparationMargin = 0.015`). Widening the avoidance radius
   globally makes tight passages worse — that clearance is what a body lacks at a gap.
5. **A stored route supersedes flow transit** (`AssignPath` clears `UsesFlowTransit`).
   Without it a granted repath is computed and ignored; a unit stood still for 79 s.
6. **Never stand under orders with no intent.** The watchdog demands a route at 0.4 s and
   abandons the order at 1.3 s. Its counter must **not** reset on a failed retry, or the
   escalation can never fire (that bug cost 21.8 s of standing still).
7. **Idle allies and movers ignore each other in ORCA, symmetrically**, and idle agents have
   a speed cap. One-directional exclusion makes settled units flee metres down a corridor.
8. **Do not throttle `TryAdvanceToVisibleWaypoint` with `RepathCooldown`** — that cooldown
   gates the immediate repair, the detour picker and the congestion re-plan too, so
   suppressing one scan starves every recovery (cost 5 tests).
9. **`IsDirectPathClear`'s placement segment test is not redundant** with the swept check;
   it has a wider margin. Removing it fails wall-detour and chokepoint separation.
10. **Detours go to bodies at the *edge* of a jam** (lowest local pressure), never the
    worst-stalled one — the most stuck body is hemmed in and cannot act.
11. **Congestion deposits are weighted by constriction.** Full weight in a gap, ~22% in the
    open. Otherwise a crowd saturates its own surroundings and poisons its escape route.
12. **Route commitment (3 s).** Without it: the empty alternative looks better, the unit
    sets off, its own arrival makes it no better, the original drains, repeat.

---

## 4. Measured baselines (regression detection)

500 agents, 120 ticks, Release, M4:

| Metric | Value |
|---|---|
| wall / tick | **~4.3 ms** (500–547 ms per 120 ticks) |
| group-order frame | ~5.8 ms |
| `walked / optimal route` | 1.17× (pen), 1.56× (one-cell gate) |
| mean direction change | 4.5–6.4 °/tick |
| pen escape, 30 units | 17.0 s / 17.6 s, all out, 3–4 exits used |
| terrain corner crossing | ~11 s |

For reference: pen escape was **43.5 s / 59.8 s** and the terrain ramp **42 s** at the
start of this work.

**Use the `wall` column, not the EMA phase table, for anything decision-grade.** The EMA is
noisy run-to-run. (`Pathfinding` used to be recorded *per A\* call* while every other phase
was per-tick — it read 4.3 ms at 50 agents and 2.9 ms at 500. Fixed, but the lesson stands.)

---

## 5. Instrumentation

- `--debug-all` — trace + timings + congestion overlay + colliders + velocity + paths + states
- In-game: `M` trace, `T` timings, `N` overlays (nav / surface / slope / **congestion**),
  `C` colliders, `V` velocity, `K` paths, `I` states, `Backspace` despawn selection
- Trace reports `orbiting`, `overlap`, `pressure/rev`, `cohort/slot`, and per-stall
  `flow` / `flowRejects` / `noIntent`
- `--benchmark` adds constricted scenarios with `walked/optimal`, `mean-turn`,
  `infeasible%`, `terrain-fallback%`, `dead-stops`, `detour-grants`, `congestion-reroutes`

**Tests must assert outcomes, not mechanisms.** Two assertions here were unsatisfiable by
correct behaviour and had to be restated (`openFlowRatio`, and "no unit ever waits at a
gap"). A test that passes because a mechanism exists will not notice when the mechanism
stops helping — and a two-target version of the deadlock test passed while a unit stood
still for 22 s in play. Sweep the space.

---

## 6. Known open items

- **Give-up behaviour is blunt.** A unit that cannot route stops and drops the order. An
  RTS should walk as close as it can get.
- **Congestion delay does not scale with unit speed.** A jam costs everyone the same
  wall-clock seconds, so once unit speeds differ, fast units will under-value detours.
- **`Avoidance` collider (radius + 0.30) is vestigial** — created and moved every tick,
  read only by the debug renderer since steering moved to `AgentSpatialIndex`. Either use
  it (detection ranges) or drop it.
- **Doorway turn-taking.** Bodies still swap places at a contested gap. Attacked three ways
  (aperture right-of-way, retreat clamping, turn-rate limiting); only the turn rate helped.
  Genuinely wants obstacle-aware ORCA lines rather than another steering-layer patch.
- **No unit types, combat, resources, or production** — that is the next session.

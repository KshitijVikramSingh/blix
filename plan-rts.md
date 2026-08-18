# RTSGame — locomotion layer: state, seams, and what not to break

Status as of 2026-08-18. `--selftest` **42/42 passing**, on a body that walks at 1.5 m/s — see
`plan-rts-game.md` §13 Session 2 for what the re-base moved and why. Durations in the tests carry a
`WalkingPace` factor recording that they were tuned against a body running at 4.5. ~10k lines in `src/RTSGame`.
Branch `rts-locomotion`.

Run it: `tools/run-rts-game.sh [--debug-all] [--extent <metres>]`
Verify it: `dotnet run --project src/RTSGame/RTSGame.csproj -c Release -- --selftest`
Measure it: same with `--benchmark`, and `--doorwaytest` for two-way gap contention
Measure it at size: same with `--scale` — see §5, and `plan-rts-game.md` §13 for what it found
Substrate question open: **§8** argues whether this layer should be a navmesh, and names the
measurement that decides it

---

## 1. What this layer is now

One fixed 30 Hz tick, in this order:

| # | Step | Owns |
|---|---|---|
| 1 | `Congestion.Update` | decaying per-cell backpressure map (sparse: only cells holding pressure) |
| 2 | `ApplyCommands` | move / stop / follow / patrol / chase / flee, group creation |
| 3 | `UpdateBehaviors` + `UpdateGroupFormations` | locomotion states; cohort centroid; slot hand-off |
| 4 | `RefreshInvalidPaths` (+ `ReconsiderCongestedRoute`) | route validity and congestion re-evaluation |
| 5 | `PreparePreferredVelocities` | **intent** — path following or flow-gradient transit |
| 6 | `LocalSteeringSystem` → `ReciprocalVelocitySolver` | **velocity** — ORCA is the only controller here, and it sees walls |
| 7 | `IntegrateMovement` | swept static check, then move |
| 8 | `CollisionSystem` | **position** — static + agent depenetration |
| 9 | `ConstrainAgentsToTerrain` | last-resort revert |
| 10 | `UpdateStuckAgents` | progress accounting, watchdog, detour grants |

**The layering rule that makes it work:** intent → velocity → position, each owning one
thing. Every regression this session came from something reaching across those.

### Static geometry is part of the velocity solve
Walls reach the solver as half-plane constraints (`AddStaticLines`, fed by
`PathService.GatherBlockingBoxes`), not as a veto on its output. They used to be the
latter: the linear program answered as though the map were empty, the answer was
swept against the ground, and a collision threw it away in favour of whichever of
175 sampled directions survived. That search returned **nothing** more often than
something — a body freezing for a tick — and it made the doorway standoff unfixable
in principle, because the natural answer to a symmetric contest in a gap is to step
sideways into the wall, which was then vetoed for both bodies, every tick, with the
roles reversed. The sweep and the cone search remain as the safety net that
guarantees no tunnelling; they now fire on well under 1% of solves.

### Turning costs time in the router
A turn of θ at turn rate ω costs `(θ - 2·sin(θ/2))/ω` seconds if the body can arc through
it at speed, and the full `θ/ω` if the walls are closer than its turning circle, which the
clearance field decides. Both derived, no tuned constant. Until this existed a hairpin
priced exactly like a straight line, so a route could be optimal on paper and unfollowable
in practice — the body found out by failing.

It is an **approximation**: exact turn-aware routing needs the heading in the search state,
which multiplies the state space by eight and puts the group-order hitch back, so instead
each cell records the one heading its best-known route leaves with. A route that would
rather arrive slower but better aligned cannot express that. Worth it — gap reversals at a
two-way doorway fell 54 → 18 — but it is why routes are not provably optimal any more.

**It did not deliver the turn-rate ↔ route-choice coupling it was expected to**, and the
reason is arithmetic: a 90° turn in the open costs 0.039 s, about a third of a cell, while
the pen's alternate exits are 60–90 cells further. Turn cost cannot outweigh that and should
not be inflated until it does — that is the barrier-as-cost mistake in a new costume. What
would move route choice is scaling congestion by local manoeuvre difficulty: at a
constriction the cost is not *your* turn, it is that everyone must turn, one at a time.

### Routing model
Cost is **seconds**, not arbitrary units. `TerrainSurfaceRules.PathCost` is *derived* as
`1 / SpeedMultiplier` so the router and the physics agree. Congestion is expected delay in
seconds. That means route choice optimises **total travel time**, which is the premise the
resource-hauling game needs.

Group moves build **one** flow field (shared Dijkstra) and members steer down its gradient
with no stored path. Slots are claimed on arrival, not before.

**Every term in the route cost is a genuine number of seconds, and that took two
goes to get right.** The detour bubble, the group bottleneck reservation and the
per-repath body-proximity grid used to be bare constants of 2.25 to 24 — between 20
and 216 cells of detour, which is not a delay, it is a barrier. Restating them as
honest delays *on their own* made things worse and looked like proof the barriers
were load-bearing: the pen funnelled all thirty units through one gap instead of
four, and finished slower.

They were not load-bearing. They were compensating for `CongestionField.DecaySeconds`
being 4.5 — nothing deposits pressure once a jam starts moving, so all of that is
fade tail, and a cleared gap went on looking blocked for four and a half seconds.
Honest costs were bidding against pressure that no longer existed, so only absurd
ones could win. Shorten the fade to 2.2 s, and half a second for a detour and a
second for a body in the way are not merely adequate but **better than the barriers
were on every measure**: two exits instead of four, routes at 1.06× optimal against
1.19×, pen cleared in 13.4 s against 19.2 s. The same stale tail was independently
visible in play as units committing to a detour just as the jam they were avoiding
finished draining. Two symptoms, one cause.

The one term still stated in cells is `BlockedFlowCells`, and deliberately: it is a
boundary value for sampling near a wall, not a claim about how long blocked ground
takes to cross.

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
13. **`StaticTimeHorizon = 0.25`, and static geometry is reported at true cell extent.**
    A long horizon (0.35) makes bodies defend ground they are nowhere near and they walk
    7–9% further; a short one (0.12) brings the freezing back and fails two tests.
    Separately: do *not* inset the boxes the way `IsPositionFreeOfPlacement` insets its
    test. That inset is a tolerance on a point test, not geometry — applying it detaches
    every block from its neighbour by 5 cm, so a solid wall acquires a slot per cell and
    the solve steers bodies at gaps that do not exist. It cost 4× the dead stops at a gate.
14. **The mover/idle sidestep bias scales down with overlap depth.** Rotating a correction
    toward the mover's flank is what makes a brush-past look right, but only ~⅔ of it then
    lies along the normal, so a genuinely interpenetrated pair separates slowly. In a crowd
    converging on one destination the residual never gets a quiet tick to clear. A sidestep
    for a touch; the shortest way apart for anything deeper than 15 mm.
15. **The congestion field's fade must match how fast a jam actually clears.** At 4.5 s
    every cost that competes with it has to be inflated to absurdity to be heard, and
    detour decisions land after the jam has drained. This is the single highest-leverage
    constant in the routing layer; changing it invalidates the tuning of every reservation.
16. **`RelaxationPasses` is an optimum, not a floor.** 6 and 7 both break the terrain-ramp
    crossing. If overlap appears, it is almost certainly not a convergence problem — check
    *where* it happens first. The one that looked like a chokepoint failure was 12 m past
    the wall, in the arrival cluster.

---

## 4. Measured baselines (regression detection)

120 ticks, Release, M4. **Best of four runs** — see the warning below.

| Metric | Before this pass | Now |
|---|---|---|
| wall, 50 agents | 315 ms | **92 ms** |
| wall, 200 agents | 430 ms | **194 ms** |
| wall, 500 agents | 508 ms | **234 ms** |
| terrain command frame | 11.3 ms | **6.3 ms** |
| `walked / optimal`, pen | 1.17× | **1.06×** |
| `walked / optimal`, gate | 1.56× | **1.40×** |
| pen exits used (both seeds) | 3–4 | **2 / 2** |
| pen clearing time | 17.0 / 17.6 s | **13.4 / 16.2 s** |
| solver dead stops | 212 / 208 | **6 / 41** |
| terrain fallback rate | 3.4% / 1.6% | **0.1% / 0.3%** |
| two-way doorway, 16 units | 13.0 s, 31 frozen ticks, 68 gap reversals | **10.7 s, 0, 50** |
| mean direction change | 4.5 / 6.4 °/tick | 5.0 / 6.7 |
| turns > 60° | 1.09% / 1.37% | 1.23% / 1.47% |

The last two rows are the trade the congestion tuning made and are worth watching:
routes this direct leave less room to give way, so bodies change direction slightly
more often than they used to. Mid-pass, before the routing was tightened, they read
4.2 / 6.3 and 0.75% / 1.22% — better than baseline on both. If direction stability
ever matters more than route length, that trade is available by loosening the detour
costs (12 cells / 0.2× body scale gives three exits and 1.14× / 1.45×, all tests
passing).

For reference: pen escape was 43.5 s / 59.8 s and the terrain ramp 42 s at the start
of the locomotion work.

**`walked / optimal` is a ratio of two distances, and its denominator is built without
turn cost on purpose.** When turn cost first landed the denominator included it, the figure
went to 0.90 — a body apparently walking less far than the shortest route — and the gate
appeared to improve from 1.40× to 1.27× when nothing about the route had changed. A metric
that flatters the change being measured is worse than no metric. Note also that the
denominator is cell-centre-to-cell-centre while bodies walk smoothed straight lines, so
roughly 1.0 is the floor and values near it carry a few per cent of quantisation error.

**Wall time is much noisier than it looks — take the best of several runs.** The same
build measured 239 ms and 397 ms for the 500-agent scenario on consecutive runs; the
first run of a batch is always the slowest. The quality columns, by contrast, are
exactly reproducible and are the ones to trust from a single run. (Do not use the EMA
phase table for anything decision-grade either. `Pathfinding` used to be recorded
*per A\* call* while every other phase was per-tick — it read 4.3 ms at 50 agents and
2.9 ms at 500. Fixed, but the lesson stands.)

### Where the time went

The whole 2× came from four memoizations and a partition, none of which changed a
single behavioural metric — every quality number was bit-identical across them,
which is what a correct memo looks like:

- **Body traversability at cell centres** (`PathService.CellCenterAdmitsBody`). A*
  asked this of eight neighbours per expansion and the honest answer is nine terrain
  grade probes of four bilinear height samples each. Cell centres are a fixed finite
  set; the answer cannot change until the raster does. A single A* query cost about
  **4 ms** on a 3600-cell grid before this.
- **Level-ground fast path** (`TerrainMap.LevelNeighborhood`). Where every vertex a
  body's grade probes could reach is at one height, the grade is exactly zero and the
  only question left is the bounds test. Most of any map, all of a flat one.
- **Placement lookups by cell** (`IsPositionFreeOfPlacement`). Was a linear scan over
  every occupied cell on the map — a few hundred box tests to answer a question about
  one square metre — and it is the hottest predicate in the simulation.
- **Collider hash partitioned by layer.** Every agent carries four proxies, all
  re-centred twice a tick, and each write marked one shared hash stale, so the next
  query rebuilt several thousand proxies from a fresh dictionary. The only query the
  tick makes is for static geometry.
- **Agent index as a dense array**, and the ORCA priority key hoisted out of the
  comparison (an insertion sort makes a quadratic number of comparisons; it was doing
  a quarter of a million squared distances a tick to order 500 numbers).

---

## 5. Instrumentation

- `--debug-all` — trace + timings + congestion overlay + colliders + velocity + paths + states
- In-game: `M` trace, `T` timings, `N` overlays (nav / surface / slope / **congestion**),
  `C` colliders, `V` velocity, `K` paths, `I` states, `Backspace` despawn selection,
  `Z` camera-follows-selection, `R` recentre camera
- `--extent <metres>` runs the lab on a world of any size (default 30 m, the tuned one). Above
  ~70 m the ground is drawn as a coarse checker rather than per cell — the surface meshes index
  with `ushort` — and the per-cell debug overlay is windowed to the camera, because at 1200 m it
  is 5.76M instances. The camera's zoom range, far plane and focus all scale with the extent.
- Trace reports `orbiting`, `overlap`, `pressure/rev`, `cohort/slot`, and per-stall
  `flow` / `flowRejects` / `noIntent`
- `--benchmark` adds constricted scenarios with `walked/optimal`, `mean-turn`,
  `infeasible%`, `terrain-fallback%`, `dead-stops`, `detour-grants`, `congestion-reroutes`
- `--benchmark`'s constricted scenarios also report a `stuck` line: `red` agent-seconds,
  the `worst-red-run`, and `deepest-pile` / `mean-pile` — the largest cluster of red bodies
  connected within 1.2 m. Red is `StuckSeconds > 0.35` exactly as the renderer draws it, so
  these describe what is on screen. `deepest-pile` is the one that corresponds to the
  complaint "they bunch against a gap instead of going round": thirty units spread over four
  exits and ten wedged in one corner are the same headcount and very different pictures.
- `--scale` reports the per-phase breakdown on a world the size the game wants rather than the 30 m
  square everything here was tuned on: 800 / 1000 / 1200 m at 500 / 1000 / 2000 agents, each in an
  **idle** pass (no destinations, so the area-scaled floor alone) and a **moving** pass (one group
  move, the honest tick). `--extents` and `--agents` take comma-separated overrides. Two of its
  columns are new and one is a trap: `congestion` used to be outside every phase, and `index` is a
  *subset* of `steering` and `collision` — the broad phase is rebuilt inside both — rather than a
  column beside them. It also reports `live cells`, the congestion cells actually being swept, and
  `first-route tick`, which is what one move order costs before anything moves.
- `--ordertest` reports what a move order costs as a function of distance, and what eight
  *scattered* successive orders cost in one world. Scattered matters: alternating between two ends
  re-uses the corridor the first order paid for and reports a cache that never existed.
- `--routingtest` compares hierarchical cost-to-goal against the flat whole-map search it replaced,
  cell by cell, on a 200 m map of staggered walls, at several settings of the abstract search's
  horizon (`--spans`). `lost` is the column that matters — a cell the hierarchy cannot price is a
  body that believes it has no route and stops.
- **Live tuning overlay** (backtick to toggle). **Eight dials, down from forty-five.** What is on
  it is what is still a question: the body (top speed, acceleration, deceleration, turn rate), the
  clock, the two route-cost terms that are game design rather than solver tuning (congestion
  cells/pressure, climb s/m), and formation station-keeping. Everything the locomotion work settled
  is now a constant with its measurements beside it, and everything that is a fixed proportion of
  another number is written as that proportion — free-turn speed is an eighth of top speed, the
  router's turn rate follows the body's, and every duration describing how long a physical condition
  lasts carries `AgentDefaults.PaceScale`. A slider that silently re-tunes itself when you move the
  one above it is not a dial, it is a trap. `RtsGameLoop` implements `IDebuggable`, so
  the shared Blix diagnostics overlay appears with a Controls tab of `[Tune]` sliders
  (`BodyFeelSettings`: top speed, acceleration, deceleration, turn rate, free-turn speed)
  applied to every live body each frame, and Values/Stats showing `red`, `deepest-pile`,
  `mean-speed`, `worst-stuck`, `contacts`, `infeasible-%`, `dead-stops`, `congestion-peak`.
  Feel cannot be settled headlessly — a crowd can score well on route length and stall time
  and still look wrong — so these are sliders rather than constants under test.
  <br>**43 sliders across 7 groups**: Body, Avoidance, Contact, Congestion, Routing, Group,
  Recovery. The tuned values themselves stay `internal static` in their own classes, with
  their measured rationale beside them; the settings classes in `MovementTuning.cs` are
  proxy properties onto those, so a slider moves the real value rather than a copy.
  <br>Two things to know before changing this. Those values are now **mutable process-wide
  state** rather than compile-time constants — nothing headless writes them, so tests are
  unaffected, but a future parallel-worlds harness would see them shared. And the turn-cost
  tables are stored in **radians**, divided by the live turn rate at use: baked in seconds at
  static init, as they originally were, the routed-turn-rate slider would silently do nothing.
  <br>`Routing → routed turn rate` and `Body → turn rate` are meant to track each other.
  Moving one alone reproduces exactly the mismatch that made tightening turning fail to
  divert traffic — the router plans for a body that does not exist. An experiment, not a
  setting.
- `--doorwaytest` drives two files through one gap in opposite directions and reports
  clearing time, frozen ticks, gap reversals and dead stops. This is the case with no
  good answer historically, so it is a diagnostic rather than an assertion — the numbers
  are the point. Promote it once gap reversals are genuinely low.

**Tests must assert outcomes, not mechanisms.** Three assertions here were unsatisfiable by
correct behaviour and had to be restated (`openFlowRatio`; "no unit ever waits at a gap";
and the chokepoint's single minimum separation over 900 ticks, which was reporting one
frame of contact in a twelve-body arrival cluster twelve metres past the gap as though it
described the resting state — it passed by two millimetres and flipped on routing constants
with no bearing on chokepoints. Now split into settled separation, contact duration and
contact depth, each with margin). A test that passes because a mechanism exists will not notice when the mechanism
stops helping — and a two-target version of the deadlock test passed while a unit stood
still for 22 s in play. Sweep the space.

---

## 6. Measured refusals — tried, worse, reverted

Kept because each looked obviously right and cost real time to disprove.

- **March order as ORCA priority.** Order group members by their place in the column
  rather than by distance to goal, so a queue stops re-negotiating who goes first. Dead
  stops at a one-cell gate: **5** without it, **386** with a rank fixed at order time,
  **168** with one re-derived every half second. The ordering is not right of way — it
  decides who takes *responsibility* for avoidance, and the lower-priority body avoids the
  higher one's chosen velocity. By distance, the bodies yielding are the ones behind, which
  matches the geometry. By column rank, bodies physically in front must yield to one behind
  them, and the group's avoidance stops corresponding to where anything is. Group-level
  sequencing is the right idea in the wrong layer: it belongs in who enters a gap next.
- **Turn rate scaled by speed.** Honest physics — a turn is limited by lateral
  acceleration, so `ω = a/v` and a stopped body pivots freely. One unit never escaped the
  pen, gate dead stops went 5 → 288, infeasible solves 3.8% → 13.2%. `MaximumTurnSpeed` is
  doing two jobs: a physical bound, and the low-pass that stops the velocity solve spinning
  bodies on the spot. In a crowd every body is slow, so scaling by speed lifts the limit
  precisely where it was doing the most work.
- **Releasing flow smoothing for stalled bodies**, so a stuck unit can change its mind
  sharply instead of easing round. Gate dead stops 5 → 98.
- **Damped formation station-keeping** (subtract the closing rate). Fixes a group-level
  swing on paper, and costs freezing: gate dead stops 5 → 28 at a damping of 0.3, and three
  tests fail at 0.45. The swing it was aimed at was most likely march order's doing, and
  went away when that did.

The pattern in all four: the constants in this layer are load-bearing in more than one way
at once, and a change justified by one of their jobs breaks the other.

---

## 7. Known open items

- **Doorway turn-taking is improved, not solved.** Freezing and dead stops at a two-way
  gap are gone (31 frozen ticks → 2, 28 dead stops → 0) and clearing is 24% quicker, but
  bodies still reverse near the gap 48 times against 68. What remains is not the wall any
  more: with static lines present the solve must pick who goes and who waits, and it picks
  by progress order, which flips tick to tick between two near-equidistant contenders. A
  yield *commitment* — a body that gave way keeps giving way briefly — is the untried idea.
  Note that `HasHigherPriority` is not a strict weak ordering (the epsilon band makes it
  intransitive), so it cannot simply be made hysteretic by persisting last tick's order.
- **`terrain.Revision == 0` still forks routing.** Flat maps skip `grid.CanTraverse`, skip
  per-sample walkability in `IsDirectPathClear`, and smooth with uncapped segments where
  sculpted maps cap at two cells. The velocity solver deliberately deleted its equivalent
  branch — "flat and sculpted maps ran genuinely different avoidance algorithms, so every
  constant tuned here applied to exactly one of them" — and the same smell survives one
  layer down, with three of five benchmark scenarios flat.
- **Give-up behaviour is blunt.** A unit that cannot route stops and drops the order. An
  RTS should walk as close as it can get.
- **Congestion delay does not scale with unit speed.** A jam costs everyone the same
  wall-clock seconds, so once unit speeds differ, fast units will under-value detours.
- **`Avoidance` collider (radius + 0.30) is vestigial** — created and moved every tick,
  read only by the debug renderer since steering moved to `AgentSpatialIndex`. It no longer
  costs a hash rebuild now the collider hash is partitioned, so this is tidiness rather
  than performance. Either use it (detection ranges) or drop it.
- **`DestinationIsLocallyContested` is O(n) twice per path-following agent per tick.** It
  did not show up as hot in these scenarios, because a group move puts most bodies on flow
  transit and they return before reaching it — a game with many individually-pathing units
  will feel it. The shared-destination count can be tallied once per tick, and the packing
  loop wants `agentIndex` rather than a scan of every agent for neighbours within 0.82 m.
  It is load-bearing (805 firings per 120 ticks at 500 agents), so make it cheap, not gone.
- **Bodies meet gaps oblique, then reorient inside them.** Measured: mean approach angle to
  a constriction is 26 degrees in the pen and **33.5 at a one-cell gate, with 46% of samples
  past thirty** (`gaps` line in `--benchmark`; `SimulationWorld.ApertureApproachDegrees`
  against `PathService.TryFindPassageAxis`). A 0.74 m body at 33 degrees presents 0.88 m into
  a 1.5 m opening and has to reorient in the one place with no room — this is what the
  shuffling at a chokepoint is.
  <br>`Routing → gap line-up standoff` addresses it and is **defaulted off (0)**. At 1.2 it
  aims the intent at a staging point on the gap's axis instead of at its mouth, and suppresses
  station-keeping while lining up (a formation cannot be held through a one-body gap; the
  correction that tries is a sideways shove where there is no sideways). Gate approach falls
  33.5 → 27.6 degrees, oblique 46 → 32%, pen route length 1.10x → 1.00x — and pen red time
  rises 37 → 62 agent-s, gate dead stops 29 → 74, mean turn 4.8 → 5.8. A trade between how a
  crowd looks entering a gap and how fast it gets through; no headless measure settles it, so
  it is a slider rather than a decision.
  <br>Two other routes to the same problem measured worse and were dropped: refusing to let
  `SmoothPath` straighten across a constriction (barely moved the angle — most bodies here are
  cohort members on the shared field, not on smoothed paths), and charging flow-probe
  reversals scaled by a progress-derived commitment.
- **At least two distinct causes of orbiting, one still open.** The cohort-of-one degeneracy
  is fixed (station-keeping needs somebody to keep station with). The trace has since caught a
  second: `group=0 flow=False stuck=0.00`, an *ungrouped* body on a stored path circling near a
  dense arrival at half speed, walking ten times its net displacement with the stall detector
  reading zero. `CrowdedArrivalAttempts` exists for that shape; whether this is a defect or
  just the rim of a 500-body convergence is not yet established.
- **Bodies bunch against an exit rather than backing off and going round.** The most
  visible remaining flaw and the one a player notices. Two things have been done about it
  and both are cures rather than preventions:
  - Granted detours now exclude *the constriction the body is failing at*
    (`TryFindObstructingAperture`) instead of a point 1.25 m in front of its nose, which
    against a queue several deep just routed it back into the same queue.
  - A body stalled 1.8 s at the same gap **abandons** it for 4 s and re-plans around it, a
    decision held rather than re-derived, and deliberately available to bodies buried in a
    queue — the detour grant picks the least congested and so by design never reaches the
    ones actually wedged.

  Together: pen red time 45.7 → 36.6 agent-s, worst run 2.7 → 2.4 s, pile duration 7.3 →
  5.6 s; gate red 117.0 → 104.1, worst run 4.4 → 2.9 s. But **`deepest-pile` barely moved**
  (11 → 10, 22 → 22) and gate dead stops went 5 → 29. The pile still forms exactly as deep;
  it drains sooner.

  What remains untried is prevention: hold an approaching body back short of a saturated
  aperture so the pile never forms and the front stays mobile. `CongestionYieldSeconds`
  already exists for this — it is decremented every tick, zeroed in six places, and read by
  `LocalSteeringSystem` to zero a body's desired velocity, and **nothing ever sets it
  positive**. A complete hold-and-wait mechanism, wired in and dead. It is also the closest
  thing to the explicit queue this project removed once, so it wants measuring hard against
  `deepest-pile` before it is believed.
- **Acceleration is 16 m/s² and should not be.** Over one and a half g: whatever the
  velocity solve asks for is granted within a tick, so the solve's answer and the body's
  motion are the same thing and there is no momentum to read on screen. A walking person
  manages perhaps 1 to 3. It is left alone because every threshold in the self-tests was
  tuned against a body that reaches its speed instantly, and moving it to 8 with a
  deceleration of 14 breaks two of them immediately. The order of work is: settle the feel
  on the slider, then re-base the tests against the answer — not the reverse. Deceleration
  is now a separate rate (steering used one for both, so a body shed speed as gently as it
  built it, which is why a crowd coasts into things rather than stopping short of them),
  defaulted equal so shipped behaviour is unchanged until somebody moves it.
- **No unit types, combat, resources, or production** — that is the next session.

---

## 8. The substrate question: should this be a navmesh?

Raised 2026-08-18, from a sharper version of the rule that has driven most of this session:
**spend the budget where navigation and velocity decisions happen, not uniformly over a
square.** Written down before anyone starts, because it is a substrate change and the
substrate is what every constant in this document is calibrated against.

### What uniform storage actually costs

At 600 m the map is 1,440,000 fine cells. Its content is a ridge, a lake and a road.

| structure | bytes/cell | at 600 m | how much of it says anything |
|---|---|---|---|
| navigation raster — blocked, clearance, height, cost, speed | 17 | **24.5 MB** | ~1% is near an obstacle |
| congestion field — pressure, flowX, flowZ, live flag | 13 | **18.7 MB** | **1.2k–4.2k live cells: 0.3%** |

The congestion field is the cleanest illustration in the codebase. Session 1 made its *sweep*
proportional to the number of jams and left its *storage* proportional to the area of the map,
so 18.7 MB is allocated to hold, at peak, four thousand cells' worth of "somebody is stuck
here". Nothing about that is a navmesh argument — it is just an unfinished one.

### The argument that actually bites

**Portal routing is an adaptive decomposition bolted onto a uniform grid.** Regions, portals,
region-local searches, tiles, and the analytic path for plain regions all exist to recover
information the grid threw away by storing open ground at the same resolution as a doorway. A
navmesh has that structure natively: convex polygons whose vertices are obstacle corners, so an
empty map is a handful of polygons and detail exists exactly where geometry demands it. Map
extent stops being a cost driver at all, which matters because §3 of `plan-rts-game.md` has
already moved the map size twice.

Measured, on the ridge map: a move order costs 6–190 ms and the searches are region tiles at
4,096 cells each. A navmesh does not have that number.

### The argument against, which is not sentiment

Every one of these reads the 0.5 m grid, and each is a separate problem:

1. **Clearance** — per cell, and consumed by five different things (radius filtering, the
   turn-cost confinement ramp, `CongestionField.Constriction`, aperture detection,
   `RegionIsPlain`). On a mesh it becomes distance-to-nearest-edge, which is computable and
   wants its own acceleration structure.
2. **The congestion field.** A jam is a metres-wide phenomenon that can happen anywhere. It
   cannot live on polygons — they are tens of metres across where the ground is open, which is
   exactly where a crowd is free to jam in the middle of nothing. **This layer wants uniform
   resolution independently of the mesh.**
3. **The steering gradient.** `SampleFlowGradient` rings sixteen probes around a body and
   bilinearly samples a dense scalar field, and §1 records why: materialised waypoints produced
   jerky, snapping motion and the continuous field is what fixed it. A corridor from a funnel
   algorithm is a sequence of portals — which is materialised waypoints wearing a hat. **This
   is the largest risk in the whole change and it is a behavioural one, not a performance one.**
4. **Surfaces and slope** become polygon attributes, so the mesh must be split along every
   surface and grade boundary. §10 of `plan-rts-game.md` wants rivers that freeze and mud that
   comes and goes with the season; a grid repaints, a mesh re-partitions.
5. **Dynamic edits.** Placing a building is a local re-rasterise today. Tile-based mesh rebuild
   is well-trodden (Recast does it) but it is machinery.
6. **Determinism.** Lockstep LAN and career persistence both depend on bit-identical
   simulation, and triangulation is precisely where floating-point tie-breaks live.
7. **42 tests and every constant here** are calibrated against the grid.

### What that adds up to

**The navmesh is right for route topology and insufficient for fields.** Congestion and the
steering gradient want a uniform resolution that has nothing to do with polygon size, so a pure
mesh does not remove the second structure — it renames the question. The realistic end state is
a hybrid: a mesh for topology, and a *sparse* uniform field for the metres-scale phenomena, held
only where bodies actually are.

Which reframes the question usefully. It is not "navmesh or grid". It is: **which layers need
uniform resolution, and can their storage be made proportional to occupancy without changing
their semantics?**

### Staged, so each step pays for itself and none of them is a leap

| stage | change | wins | risk |
|---|---|---|---|
| **0** | congestion storage chunked by region, allocated on demand — **DONE 2026-08-18** | **18.7 MB → 271 KB at 600 m, 74.9 MB → 253 KB at 1200 m**, and it no longer scales with extent at all | none, as expected: `--selftest` output bit-identical |
| **1** | nav raster chunked per region, uniform regions keeping five numbers — **DONE 2026-08-19** | **24.5 → 8.2 MB at 600 m, 98 → 17.2 MB at 1200 m**, and routing got ~15% *faster* | none realised: `--selftest` output bit-identical |
| **2** | mesh replaces the portal graph as the abstract layer; fine grid stays as the local sampling structure | route cost stops scaling with extent; portals/tiles/region profiles all go | mesh generation, determinism, seasonal re-partition |
| **3** | remove the fine grid entirely | the honest end of the argument | congestion and the steering gradient need a new home first — see (2) and (3) above |

Stages 0 and 1 are worth doing whatever is decided about the mesh, because they are the same
rule applied to storage and neither can break calibration.

**Stage 0, measured.** Chunked by region — 4,096 entries allocated the first time anything in a
region deposits, released when the last of it fades — rather than hashed, because a chunk keeps
the read path to a null check and two array reads, which is what it was before. With 2,000
agents moving: **271 KB at 600 m and 253 KB at 1200 m**. The second number is the point. Storage
now tracks how much of the map is jammed, and a map four times the area jams no harder, so the
figure stopped depending on extent — which is the property the whole substrate argument is
about. Self-test output is bit-identical, which is what "no semantic change" has to mean.

**Stage 1, measured, including the part that went wrong.** Uniform regions keep five numbers;
the rest keep a chunk. On an empty 600 m map 105 of 361 regions need a chunk and the raster is
8.2 MB against 24.5 dense; at 1200 m it is 219 of 1,444 and 17.2 MB against 98. The chunked
*fraction* falls as the map grows — the ring of regions near the map edge, where clearance varies
because the edge is the nearest thing to it, is a smaller share of a bigger map. On the sculpted
ridge map it is 57% and 16.0 MB, because clearance varies within
`ObstacleIndex.Reach` of every obstacle and the ridge crosses the whole map. That halo is the
limit on this stage, and shrinking `Reach` would shrink it at the cost of changing a reported
metric.

The part worth recording: **five parallel chunk arrays made routing 1.7x slower than the dense
grid they replaced.** Everything that reads this grid wants several of the five about the same
cell — `CanTraverse` alone wants blocked, clearance and height for two of them — so five arrays
is five chunk lookups and five cache lines to answer one question. Interleaving them into one
struct array per region took a move order on the ridge map from 305 ms back to 161, which is
**15% faster than the dense grid was**. Chunking a hot structure is a cache-layout change wearing
a memory-saving costume, and it can go either way.

### The measurement that decides it — **run 2026-08-19**

| criterion written in advance | result |
|---|---|
| a 600 m map is ~5 MB | **no**: 8.2 MB open, 16.0 MB sculpted, plus 0.27 MB congestion |
| an order on obstacle-rich ground fits inside a 33 ms tick | **no**: 5–161 ms, and the expensive ones are region tiles |

**So the mesh case stands, and on performance rather than elegance.** Stages 0 and 1 took the
memory argument largely off the table — 1200 m went from 315 MB resident to 85 — and left the
routing argument exactly where it was. What costs is searching 4,096-cell tiles in regions that
contain an obstacle, which is precisely the work a mesh does not have to do. Stage 2 is
justified by the number that was written down before it was measured.

The caveats in "the argument against" are unchanged and are what stage 2 has to answer first:
**congestion and the steering gradient still need a uniform-resolution home**, and stage 0 is
now the model for it — chunked, allocated where something is happening, released when it is not.

### Stage 2, settled 2026-08-19

Six questions, six answers, and the shape they add up to is narrower than "replace the
substrate" — which is the point. **The mesh replaces what routing searches, not what steering
reads.**

| question | settled | why |
|---|---|---|
| rectangles or triangles | **rectangles** | every coordinate is an integer cell index, so there are no floating-point tie-breaks to make deterministic — and lockstep and career persistence both rest on bit-identity |
| how steering follows a route | **a dense local field over the corridor** | §1 records that materialised waypoints are exactly what the continuous field was introduced to fix; a funnel corridor is waypoints wearing a hat |
| does the 0.5 m raster survive | **yes, as the sampling layer** | clearance, surface, height and congestion all want uniform resolution, and stage 1 already made it cost content rather than area |
| surfaces as polygon attributes | **no — a sampled layer route cost integrates** | §10 wants rivers that freeze and mud that comes and goes; as an attribute every season re-partitions the mesh, as a layer a season is a repaint |
| radius | **clearance test at query time** | one decomposition serves every body, as one grid does today; per-radius erosion only if Session 4's carts prove it necessary |
| rebuild granularity | **deferred** | it interacts with the tile cache, and there is no point designing it before the decomposition has been lived with |

### The decomposition, measured before it is wired to anything

Maximal rectangles of uniform ground — same walkability for the radius, same traversal cost,
height within 5 cm — by greedy row runs merged downwards. Deterministic by construction: integer
arithmetic in a fixed order.

| map | rectangles | mean | largest | built in |
|---|---|---|---|---|
| empty, 600 m | **1** | 1,435,204 cells | — | 8 ms |
| ridge, lake and road, 600 m | **754** | 1,783 cells | 484,513 | 10 ms |

Against a fixed partition of 361 regions of 4,096 cells, of which more than half fail the
uniformity test and are therefore searched cell by cell. **A rectangle is uniform by
construction, so crossing one is always the arithmetic case.** An empty map is one rectangle;
a map with a ridge, a lake and a road is 754 — a graph small enough that the abstract search
over it is free, and the per-node region searches that cost 5–161 ms have nothing left to do.

The obstacle no longer condemns the region around it: it gets its own thin rectangles and the
plain beside it stays one.

**Adjacency, and the number that decides how the cost field is evaluated.** Crossings are shared
border *runs* rather than points, because a portal reduced to its midpoint makes every route
through a wide opening detour to the middle of it; a run is broken wherever the neighbour changes
or the step is not traversable, so a rectangle sitting against a ridge does not get a crossing
nobody can cross.

| map | crossings | per rectangle: median | p99 | worst |
|---|---|---|---|---|
| empty, 600 m | 0 | 0 | 0 | 0 |
| ridge, lake and road | 1,682 | **4** | 6 | **150** |

A rectangle is uniform, so cost-to-goal for a cell inside one is the cheapest of *(octile
distance to a crossing) + (cost-to-goal at that crossing)*. At a median of four crossings that is
four clamps and four distances — **evaluated per query, with no tile and no search at all**,
which is what removes both `IngressCosts` and the region tiles in one move.

The worst case is 150, and it is the obvious one: the 484,513-cell plain touches every small
rectangle along the ridge. Sixteen gradient probes per body against 150 crossings is not
affordable, so that tail needs one of — a spatial index over a rectangle's own border, a cost
horizon pruning crossings that cannot win, or falling back to a cached dense tile for
high-degree rectangles only. **Median four says the fast path is the common path; the tail is a
separate decision and should be made against a measurement rather than in advance.**

### The original criterion, for the record

**After stages 0 and 1, re-run `--ordertest --terrain` and the memory arithmetic.** If a 600 m
map is ~5 MB and a move order on obstacle-rich ground is inside a 33 ms tick, then the mesh buys
elegance rather than performance, and elegance is not worth re-deriving every constant in this
document. If order cost is still hundreds of milliseconds, the mesh is the answer and stage 2
should start.

Written down in advance so that the decision is made by the number rather than by whoever is
holding the keyboard when it comes up.

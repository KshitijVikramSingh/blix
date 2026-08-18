# RTSGame — Thread A: the game layer

Written at the close of the locomotion arc (Thread B), before any game-layer code exists.
Thread B's state and its hard-won invariants live in `plan-rts.md`; read that first if you
are about to touch movement, because most of what looks tunable there is load-bearing twice.

---

## 1. What the game is

A real-time strategy game about a settlement that becomes a territory. Players on the map,
allies and enemies, trade carts and docks. **200–300 units per player, up to 500**, several
players. Roughly 50–60 units on screen at a time.

Two modes of play, and **both are first-class**:

- **High attention** — "harvest / raid / move these people / finish this construction."
- **Low attention** — "I have arranged a little society; let me see what it does without me."

The second is not a fallback for when there is nothing to do. Leaving the game running and
coming back to watch consequences propagate is *the point*, and it is evidence the autonomous
layer is working rather than evidence the game is empty. Every engineering decision below is
downstream of taking that literally.

## 2. The design thesis: structures buy back attention

The defence progression states the whole design once:

| rung | what changes for the player |
|---|---|
| no defences | I respond to every incursion personally |
| outpost | I know earlier |
| palisade | the enemy's route is constrained |
| gate | I choose where that route converges |
| guards | manpower waits at the convergence |
| stone wall | that arrangement is much harder to overwhelm |
| manned watchtower | the arrangement begins responding intelligently without me |

That is not a tech tree. It is a **progression from attention to delegation**, and it is the
same axis as the two modes of play.

**Physical hauling is the economic form of the identical ladder.** In the prototype
`totalSecured()` sums every building's stock into one global number, so resources teleport
and logistics is free. Make it physical — hauling points built with labour, haulers assigned
to routes — and the rungs are: no hauling point → I carry every load · hauling point → a
route exists · assigned hauler → the route runs without me · linked points → a network runs
without me.

**Use this as the scope test.** A mechanic earns its place if it sits on that ladder. Trade
carts do; a trade route is delegated income. Docks do. A global resource counter fights it.

Two consequences worth holding onto:

- **Logistics must be attackable.** What stops a hauling point being busywork is that its
  placement is a real trade-off — near the forest is fast and exposed, near the core is safe
  and slow. That requires raidable infrastructure, which is what makes both ladders
  load-bearing at once. It is also the mechanism behind the story this design came from: the
  settlement stopped meaning a cluster of buildings and started meaning territory worth
  holding, and an L-shaped palisade appeared.
- **The settlement core should be the root of the hauling graph, not a spawn point.** Placed
  at the start, and foundable again later, so expansion becomes "establish a new root and
  connect it" rather than "occupy more space".

---

## 3. The hardest engineering problem is map size, not unit count

Agent-side we are in reasonable shape: 2.3 ms/tick at 500 agents, and the quadratic costs are
mostly gone. Map-side there is an algorithm that does not survive the target.

The Thread B test world is **30 m × 30 m — `GridTransform(60, 60, 0.5f)`, 3600 nav cells**.
A real map with resource regions deliberately far apart is plausibly **40,000–160,000 cells**.
Against that:

- **`BuildFlowField` is a full-grid Dijkstra with no early termination**, and every group move
  order builds one. At 3600 cells the 30-agent command frame is 6.3 ms. At 160k cells the work
  is roughly 44× — an *estimate from cell counts, not a measurement*, which is exactly why the
  first task is to measure it. The right-click hitch was fought from 82 ms to 4.3 ms once; at
  real map scale it returns far worse.
- `FindNearestWalkable` allocates `bool[W*H]` and a queue **per call**, and sits on the
  per-agent-per-tick path whenever an ordered goal cell is unwalkable.
- Flow-field cache memory is ~640 KB per field at 160k cells, multiplied by distinct goals and
  retained congestion revisions.
- `CongestionField.Update` sweeps W*H twice per tick — cheap per cell, but it stops being free.

The likely answer is hierarchical or portal-based routing, or flow fields bounded to a region
rather than the whole map. **This is the largest single piece of engine work Thread A needs and
it should be measured before anything is built on top of it.**

### No simulation LOD

The usual escape hatch — freeze or coarsen distant units — is **foreclosed by the design**.
50–60 units rendering while 2000 simulate is a deliberate inversion of the normal budget, and
"I came back to watch what happened" requires that what happened was actually simulated. This
is a design commitment with a large engineering bill; it was taken knowingly.

### Soak tests

The lone-orbiter defect only appeared *after* 29 of 30 units had settled, and a human watching
found it — two headless tests written specifically to reproduce it both passed with the defect
fully present. Every one of the 37 self-tests is short-horizon.

A game whose selling point is "leave it running" needs **soak tests**: ten-plus minutes of game
time asserting no unit ever permanently stops making progress, no counter drifts, no route
churns forever. That entire class of bug is currently invisible to us.

---

## 4. What Thread B bought, and where it pays off here

- **Routing cost is time.** `TerrainSurfaceRules.PathCost` is derived as `1/SpeedMultiplier`
  so router and physics agree, and congestion is expected delay in seconds. That means a
  hauling assignment can be priced in the same currency as everything else: how long this haul
  will *actually* take given terrain and the traffic already on the route. A jammed route makes
  a different hauler cheaper, automatically. This is what makes hauling strategic rather than a
  chore, and it is the payoff of the cost-is-time decision.
- **The congestion field** is more valuable for many independent haulers sharing routes than it
  ever was for squad moves.
- **Static ORCA lines, turn cost and manoeuvre amplification** matter because the game has
  gates in palisades — the exact geometry they were built for.
- **`ColliderRole.Interactable` / `Damageable` and `FactionRelations`** finally get consumers.
- **Formations and cohort transit are core, not incidental.** An earlier reading of this design
  as a settlement sim concluded squad movement barely mattered. That was wrong: real combat,
  allies and enemies, and armies to stabilise all need it.

### Debts that go live immediately

- **One body model.** `AgentDefaults` assumes a single radius, speed and turn rate. Workers,
  soldiers, carts and raider types break that at once, and the logged "congestion delay does
  not scale with unit speed" item goes live the moment speeds differ.
- **Carts want a turning circle.** The speed-scaled turn rate tried and reverted in Thread B
  (`ω = a/v`) was wrong for people because it lifted the anti-spin limit exactly where crowds
  need it — but it is *correct* for a loaded cart. Per-type turn models resolve the conflict.
- **Acceleration is 16 m/s²**, over one and a half g, and every self-test threshold is
  calibrated against a body that reaches its speed instantly. Settle the feel on the slider,
  then re-base the tests.

---

## 5. Build order

1. **Map-scale proof.** A realistically sized map (40k–160k cells) with 500–2000 agents. Find
   where the tick budget goes and what a group order actually costs. Design everything else
   against real numbers. *This is the agreed opening task.*
2. **Unit types.** Per-type body, speed, turn model and carry capacity off `AgentDefaults`.
   Unblocks everything; pays a logged debt; gives carts vehicle turning and people person
   acceleration.
3. **Jobs layer.** Port the prototype's three-layer worker model — *assignment* (persistent
   commitment) / *activity* (what it is doing now) / *interrupt* (temporary local defence that
   never rewrites assignment) — onto the `AgentCommand` and `AgentLocomotionState` seams. This
   is the biggest new subsystem and the whole autonomous layer rests on it. Do **not** carry
   across `w.task`, which in the prototype is a `defineProperty` alias for `activity` kept for
   legacy readers.
4. **Stock and the hauling network.** Physical local storage, hauling points as graph nodes,
   assignment priced in seconds through the congestion field. Assignment-level graph,
   locomotion-level paths.
5. **Soak tests**, alongside 3 and 4 rather than after them.
6. Combat and the defence ladder; then trade carts and docks.

---

## 6. Open design questions

- **Is offence on the ladder?** Defence has seven rungs to delegation. Offence as standard RTS
  fare is maximally high-attention, so the low-attention mode dies whenever you are at war.
  Attacking needs delegable equivalents — standing patrols, a rally line that holds, "raid
  their lumber camp" as an order rather than a micro sequence — or the game has two modes and
  combat silently deletes one.
- **Physical local storage kills the global resource HUD.** `Food 45.0 secured` cannot survive
  distributed stock and multi-minute hauls. The UI has to express *where* things are and
  *whether they will arrive in time*. This is probably what makes the game feel different more
  than any single mechanic.
- **Which agricultural model is canonical?** The prototype has two that disagree. Seasons run
  Spring 0–60, Summer 60–150, Harvest 150–200, Winter 200–270. Crops run A prep 0–45, A maint
  45–90, A harvest 90–135, B prep 135–175, B maint 175–215, B harvest 215–255. So A harvest
  falls entirely in Summer, B harvest entirely in Winter, and **nothing is harvested during the
  season called Harvest**. `farmPressure`'s anchors follow the season names, `cropCycleAt`
  follows the crop model, and `summerRetention`'s comment follows a third window (60–150 versus
  45–90). Pick one before porting either.
- **Win condition.** The prototype has none: raids tier up forever and the year counter climbs,
  so structurally it is a slow loss with no terminus. Survive N years, reach a population,
  escape, endless with a score — this decides whether progression systems are needed at all.
- **Priority sliders.** In the prototype `state.priorities` carries farm/build/wood/stone;
  `priorityScore` is called once, for `build`, and only `pBuild` exists in the DOM. If
  policy-level control is the intended verb at 100+ workers it needs building properly; if
  direct assignment is the verb, population has to be deliberately capped. These are different
  games and they need different engine work.

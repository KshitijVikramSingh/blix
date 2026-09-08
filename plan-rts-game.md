# RTSGame — Thread A: the game layer

## Handoff — read this first

**You are picking up a design that is fully specified, and a codebase that now implements the
bottom third of it.**

- **The code** is `src/RTSGame`: one fixed 30 Hz tick, ORCA velocity control that sees walls,
  flow-field group movement, congestion routing priced in seconds, an adaptive rectangle partition
  for routes, six unit types across two body classes, a jobs layer, save and load, and an economy.
  `--selftest` **71/71** on branch `rts-locomotion`. Its tick order, its invariants and its **five measured
  refusals** live in **`plan-rts.md`** — read that before touching movement, because most of what
  looks tunable there is load-bearing in two ways at once.
- **This document** is the game that goes on top, settled across long design sessions in August
  2026. §1–§11 are decided, not speculative — the numbers are derived and the derivations are
  recorded beside them, because they move as a set. **§12 is what is genuinely still open**, and it
  is shorter than it was.
- **Sessions 1 through 6 are done** (2026-08-18/19); their records are in §13 and the measured state
  is §14. In order: the 1200 m map went from a 549 ms tick and a 4.1 s move order to **5.8 ms at
  2,000 agents**; the body came down from a 4.5 m/s run to a **1.79 m/s walk** with every threshold
  re-based; the map came down to **600 m**; the routing layer was rebuilt twice more into rectangles
  and tiles; the roster landed — villager, soldier, hauler cart, light cavalry, heavy cavalry,
  wagon; the jobs layer landed with assignment, activity and interrupt on the body; and the economy
  landed — calendar, stock, catchments, hauling priced in seconds, and a two-year settlement whose books
  balance exactly.

### Start at the MVP slice, and read §15 and §17 first

**§15 is an audit of what Sessions 6 to 9 actually owe**, made on 2026-08-19 by checking the code
rather than re-reading this document. It found four things worth knowing before any of them starts,
including one design claim that measurement contradicts and one units trap that would switch the
catchment mechanic off for the second time. It also says where the **first playable slice** is, and
argues for one change to the order.

The two numbers Sessions 5 and 6 rest on were re-derived against the map they actually run on:

- **The catchment is 60 s at hauler pace, 66 m** (§6). The figure it replaced covered a player's
  whole territory, so the second-granary mechanic never fired at all. §15 has it measured on real
  ground: 62.6 m effective on open terrain, and 10 to 40 catchments per territory near features
  rather than the derived 6.58.
- **The hauler exists and is 1.10 m/s at 0.55 m** (§3), which is what made that derivation possible.

**What Session 6 must not assume.** Hauling is priced in seconds through the congestion field, and
that field now charges a body by its own **width and speed** as well as by the jam — see
`plan-rts.md` §7. Both are on dials at 1, both are unmeasured at scale, and **Session 6 is the run
that settles them**: many haulers of differing sizes sharing routes continuously is the workload
those terms exist for and nothing before it will decide the numbers. Do not tune them on a scenario;
build the economy and read them off it. `--jobs` is already most of that run.

Four things that will bite you if you skip them:

1. **Do not move the default world size.** Every self-test and every tuned constant in `plan-rts.md`
   is calibrated against `GridTransform(60, 60, 0.5f)`. Extent is now a *parameter* and the default
   stays exactly where it is — asserted by `a larger world leaves the tuned one untouched`, so this
   is one of the four you can no longer break silently.
2. **Any new term in the route cost is honest seconds, with a fade matched to how fast the real
   condition actually clears.** This project has paid for that lesson twice — `plan-rts.md` §1 on
   barriers-as-cost, and invariant 15 on the decay tail. Threat will be the third opportunity.
3. **The determinism self-test now reads whatever the state declares**, so new fields on a body are
   covered the moment they compile. What is still owed per session is a *scenario* that exercises
   them, and a ledger entry for anything hung off `SimulationWorld` — which the census will demand by
   name, with instructions. Do not switch that census off to get a build green.
4. **Serialization exists now, and its acceptance test is stricter than it looks.** A save is not
   correct because it round-trips; it is correct because the loaded world *continues* as the same
   world, and §16 records the three things that distinction found — a manifest that is a superset of
   what the fingerprint reads, work counters that describe the process rather than the world, and a
   cost field that remembers the order it was asked. Add state to the world and the round-trip test
   will tell you, by name, a few ticks later.

**This document records decisions together with their derivations.** Nearly every number below was
derived from other numbers rather than chosen, and they move as a set — changing unit speed changes
the map, changing the map changes the career length, changing the year changes what a catchment is
worth. A derivation left unrecorded is a constant somebody will "clean up" later.

---

## 1. What the game is

A real-time strategy game about a settlement that becomes a territory. Several players — human or
AI — with allies and enemies, trade carts and docks. **200–300 units per player, up to 500**,
roughly 50–60 on screen at a time.

Two modes of play, and **both are first-class**:

- **High attention** — "harvest / raid / move these people / finish this construction."
- **Low attention** — "I have arranged a little society; let me see what it does without me."

The second is not a fallback for when there is nothing to do. Leaving the game running and coming
back to watch consequences propagate is *the point*.

**The world never stops.** Idle means idle, not quit: the game sits in a background window and keeps
ticking, AI neighbours keep playing, and your arrangement is being tested whether you are looking or
not. The world ticks whenever any participant has the game open — not 24/7, or a weekend would burn
three careers before anyone sat down.

---

## 2. The thesis, and the two rules that follow from it

### Structures buy back attention

| rung | what changes for the player |
|---|---|
| no defences | I respond to every incursion personally |
| outpost | I know earlier |
| palisade | the enemy's route is constrained |
| gate | I choose where that route converges |
| guards | manpower waits at the convergence |
| stone wall | that arrangement is much harder to overwhelm |
| manned watchtower | the arrangement begins responding intelligently without me |

Not a tech tree — a **progression from attention to delegation**, and the same axis as the two modes
of play. The rungs are four distinct mechanisms (information, constraint, standing commitment,
autonomy), which is why four different systems are needed to express one ladder.

The rungs are also **forced by each other**, which is worth knowing before anyone tries to reorder
them. See §7 for the derivation that rung 5 (guards) exists precisely because rung 2's geometry runs
out of room as your territory grows.

### The economic identity

A farm's output is continuous in the hands assigned to it, with diminishing returns. A single farmer
brings in a fraction of the harvest; a second one — disguised unemployment, marginal product near
zero — brings in more and eats. From that one rule:

> **Labour and attention are substitutes. Structures improve the exchange rate.**

Lean staffing is efficient and demands your presence at every season boundary to redistribute.
Overstaffing is inefficient and *autonomous* — the same hands cover prep, maintenance and the
harvest spike with no reassignment at all. Every structure on the ladder improves the rate at which
one converts into the other. This is the thesis stated as economics rather than metaphor, and it is
also the reason harvest failure is graded rather than binary: you bring in what your standing
arrangement can carry, and idling costs you growth rather than your career.

The identity has a **second domain**: in combat, attention and *material* are substitutes in exactly
the same way, which is what keeps the low-attention mode alive during a war. See §7.

### Rule 1 — rates, not gates

**Express every mechanic as a rate or a cost, never as a prerequisite.** Continuous output in
labour; cost in seconds; capture rather than unlock. Strategies nobody thought of survive a
substrate made of rates and die on gates. Explicitly to be preserved: raiding-only economies
sustaining a small population, partial raiding to regrow a dwindling one or to supply a distant
frontier, and economic-muscle overstaffing. None of these should need a special case; all of them
should fall out of the rates.

### Rule 2 — everything is denominated in time

Route cost is seconds. Congestion is expected delay. Threat is expected delay plus expected loss. A
catchment is a travel-time radius. Storage is seasons-of-draw. An outpost's value is lead time.
Autonomy is how long it runs without you. The career itself is years survived.

This is not a coincidence, it is the design's signature, and it means **the HUD has exactly one
verb: *how long*.** How long until this arrives, until this runs out, until they get here, until I
am needed. A strikingly small interface for a game this large, and the thing that makes the
low-attention mode legible at a glance.

---

## 3. Time, distance and the map

### Fixing the scale against AoE2

Target was 1–4× an AoE2 (1999) Large map, which is 220×220 tiles. Converting tiles to metres has two
self-consistent readings that differ by 2.5×:

- **Unit-scaled.** Villager collision radius ≈ 0.2 tiles against Blix's 0.37 m body → tile ≈ 1.85 m.
  A 2×2-tile house is then 3.7 m across, a one-room dwelling. Independent cross-check: villager
  speed 0.8 tiles/s → **1.48 m/s**, a brisk walk.
- **Building-art-scaled.** A castle *looks* about 20 m → tile ≈ 4.5 m, villager 3.6 m/s (a sustained
  run), Large map ≈ 1 km.

Isometric sprite art draws buildings oversized relative to their footprint, which is why both
survive in AoE2. **In 3D neither can hide** — a 1.45 m body beside a modelled granary settles it. So
the unit-scaled reading is the one that survives the medium, and it agrees with walking pace.

**Tile ≈ 1.8 m → AoE2 Large ≈ 400 m**, ~4.4 minutes to walk across. Matches how a Large map feels.

### Body constants — and why the current ones are test artefacts

`AgentDefaults.MaximumSpeed = 4.5f` is a hard run, near world-record marathon pace, sustained, by
everybody. It was chosen so locomotion tests resolve quickly across a 30 m world — the same category
of decision as `Acceleration = 16f`, which the file itself admits is "not a person starting to walk,
it is a body teleporting to its target velocity."

**Unit speed and map size are a single decision**, because the map is sized by response time:

| top speed | periphery reachable in 40 s | implied map | cells at 0.5 m |
|---|---|---|---|
| 4.5 m/s (current) | 180 m | 400–500 m | 640k – 1M |
| 1.5 m/s (walking) | 60 m | 150–200 m | 90k – 160k |

**Settled and shipped, 2026-08-18.** The values were proposed here, taken to the slider, and then
used to re-base the self-test thresholds — in that order, never the reverse:

```
Walk (worker)         1.79 m/s    was 4.5      SHIPPED — settled on the slider, above the
                                               proposed 1.5 and just above soldier pace
Acceleration          2.0 m/s²    was 16       SHIPPED
Deceleration          3.0 m/s²    was 16       SHIPPED
Turn rate             3.03 rad/s  was 4.0      SHIPPED
Soldier               1.7 m/s                  awaits unit types (Session 4)
Loaded cart           1.1 m/s                  awaits unit types
Scout / mounted       3.5 m/s                  awaits unit types
Body radius           0.37 m      UNCHANGED
Nav fine cell         0.5 m       UNCHANGED — this is what preserves Thread B's tuning
Road SpeedMultiplier  x1.8        -> PathCost 0.56, derived, no new constant
```

**Speed is not a preference, it is what the medium fixes**, and four independent readings agree: a
person walks 1.3–1.5 m/s, a marching column makes about 1.4, a loaded handcart 1.0–1.2, and an AoE2
villager converted through the unit-scaled tile above is 1.48.

The road multiplier needs no new machinery: `TerrainSurfaceRules.PathCost = 1/SpeedMultiplier`
already exists, so a road is a surface type the router prices correctly for free.

### The roster — **shipped 2026-08-19**

Six types, in `UnitType`. A type names a body rather than carrying loose numbers, which is what
`AgentDefaults`' own remarks record as a real bug source the last time it was otherwise.

| type | class | radius | speed | turning circle | carries |
|---|---|---|---|---|---|
| villager | Foot | 0.37 | 1.79 | free | 8 |
| soldier | Foot | 0.37 | 1.70 | free | — |
| hauler cart | Foot | **0.55** | 1.10 | free | 40 |
| light cavalry | Foot | 0.37 | 3.50 | free | — |
| heavy cavalry | Heavy | 0.90 | **2.50** | 2.6 m | — |
| wagon | Heavy | 0.90 | 1.10 | 3.4 m | 200 |

- **The hauler is mid-sized and in the Foot class on purpose.** It takes the same passages as a
  villager and merely takes up more room in the queue for one. Its 0.55 m and a villager's 0.37 m
  are *provably the same body* to the router, so it routes on the Foot field exactly rather than
  approximately, and six types are two decompositions rather than six.
- **Cavalry splits by weight rather than by role.** Scout cavalry is a horse's pace on a person's
  footprint, so it goes wherever infantry goes; heavy cavalry is the size of a wagon and has to use
  a gate. Speed and access are traded against each other by the rider, not by the unit's job.
- **The wagon is not slower than the cart.** §3 derived one cart speed and there is no reading that
  makes a drawn wagon slower than a pushed handcart, so its trade is capacity and access — carries
  five times as much, needs a gate — rather than a number nobody could defend.
- **Two numbers here are not derived and are marked as such in the code**: heavy cavalry's 2.50 m/s
  is "between a walk and a scout", and the two turning circles are shapes rather than measurements.
  They are slider questions. Every other speed comes from a real-world reading (§3 above).
- **Only Heavy gets a turning circle**, because the speed-scaled turn rate was measured badly wrong
  inside a crowd — gate dead stops 5 to 288 — and light cavalry is in the crowd. That leaves a
  scout cornering like a person at 3.5 m/s, which is the one thing the split looks odd about, and
  it is a look question rather than a correctness one.

### Body radius — two classes, and the one threshold that separates them

**Settled 2026-08-19, measured with `--radiisweep` before any unit type existed**, because §13's
Session 4 rests on it and so does everything after: the first body with a radius other than 0.37 m
decides whether one rectangle decomposition serves every unit or the map needs one per radius.

**The rule, and it is a property of the raster rather than of any map.** A body may stand on a cell
when `Clearance >= radius + 0.035`, and clearance is one sample per cell taken at its centre. Cell
centres are half a metre apart, so the best-placed sample inside a gap sits anywhere from its middle
to a quarter-metre off it, and the *same* gap measures a quarter-metre wider or narrower depending
only on where the grid fell. That swing is the sampling, not the geometry, so it survives any
obstacle shape — including trees placed at real positions rather than painted cell by cell.

> **Terrain can only tell two body radii apart if they differ by more than half a navigation cell,
> 0.25 m.** Below that there is no gap width that reliably admits the smaller and reliably stops the
> larger; the same tree line does either, depending where it landed.

**What a straight gap can measure.** Obstacle boxes have their faces on the 0.5 m lattice — painted
impassable cells, cliff edges half a cell off a centre, placement cells three nav cells wide, the map
bounds — and every cell centre sits a quarter-cell off it. So the clearances a corridor can produce
are 0.250, 0.750, 1.250, 1.750, and the bodies they admit are 0.215, 0.715, 1.215, 1.715. Four maps,
one of them cut at forty-five degrees to try to break it, return exactly that set and differ only in
how many cells sit on each rung.

**Which lands the classes on a single threshold, 0.715 m, doing three jobs at once:**

| passage | how it arises | nav clearance | admits |
|---|---|---|---|
| narrow clearing, wall gap | terrain, or one placement cell of 1.5 m | 0.750 | **≤ 0.715** |
| gate, road, wide clearing | deliberate, two placement cells of 3.0 m | 1.250 | ≤ 1.215 |

```
Villager / soldier / scout   0.37 m      UNCHANGED
Hauler cart                  0.45-0.60   the settlement band, same passage as a villager
Heavy — large mount, wagon   ~0.90       anywhere in (0.715, 1.215] behaves identically
```

- **The hauler shares the villager's passage deliberately.** It is the unit the whole economy runs
  on, and a settlement whose carts need different ground from its people is a second navigation
  problem for no design gain. Any radius from 0.32 to 0.715 is *provably the same body* to the
  router — walkability is monotone in radius, so equal cell counts across that band are equal sets,
  not similar ones.
- **A 1.5 m gap blocking the heavy class is the desired behaviour, not a defect.** Players should
  build gates and roads rather than wall their own wagons out, and a gate is twice the width of a
  wall — which is AoE2's rule arriving from the placement grid rather than being imposed on it.
- **The heavy class is where the second decomposition comes from**, and there are exactly two of
  them on any map however many unit types exist. On the 600 m map that is 9.2 ms / 73 KB plus
  4.4 ms / 76 KB, which is nothing.
- **Radius belongs to the class, not to the unit type.** `PathService.Mesh` keys its cache on the
  radius rounded to centimetres, so it already yields exactly two meshes for the roster above — but
  it would yield five if a designer gave the villager, the soldier and the scout radii of 0.35, 0.37
  and 0.40, which are the same body to the raster and would retain three byte-identical copies of
  one decomposition. Keying by *rung* instead was considered and **refused**: it is only correct
  while every obstacle face is on the lattice, and if that ever stops being true it fails by routing
  a wagon through a gap it does not fit, which is the worst available failure. Naming two radii and
  letting unit types share them costs nothing and cannot fail that way. The self-test
  `body radii inside one rung share a decomposition` is what will notice if the lattice assumption
  goes. **In the code this is `BodyClass.Foot` / `BodyClass.Heavy` with `AgentDefaults.RadiusOf`**,
  so a unit type names a class and never a number.
- **Mid-size discrimination is not available and should not be designed for.** A 0.37 villager and a
  0.55 cart cannot be separated by any terrain the generator can draw, because they are inside one
  half-cell. Wanting a third class means a finer navigation cell, which is the constant `plan-rts.md`
  is calibrated against — so it is a substrate decision, not a tuning one.
- **The condition to watch** is the lattice: an obstacle whose faces are not on it — a rotated
  building, an arbitrary footprint — makes the achievable clearances dense. It does *not* change the
  half-cell rule, which is about sampling, but it does move where the rungs are.

### Map sizes — **corrected, 2026-08-18: the game is 600 m**

Small / medium / large at 800 / 1000 / 1200 m was the answer, and it was wrong, for a reason worth
recording because the arithmetic was never the problem.

**The three derivations below all bound the map from *below*.** Response-must-exceed-raid says
bigger. Cores-times-catchment says bigger. And tenure — the one this document says actually decides
it — is monotonic in extent with no upper bound at all, so it says bigger without limit. Three
arguments that can only ever say "bigger", and the answer came out big. That is a ratchet, not a
derivation.

**The missing bound is density**, and nothing here represented it. At four players of 250 units:

| extent | m² per unit | vs AoE2 Large (4p) | contested | cross map (soldier) | response/raid | first move order |
|---|---|---|---|---|---|---|
| 400 m | 160 | 0.8x | 36% | 3.9 min | 1.5–2.9x | 0.43 s |
| **600 m** | **360** | **1.7x** | **16%** | **5.9 min** | **2.2–4.4x** | **0.81 s** |
| 800 m | 640 | 3.1x | 9.1% | 7.8 min | 2.9–5.9x | 1.6 s |
| 1200 m | 1,440 | **7.0x** | 4.0% | 11.8 min | 4.4–8.8x | 5.5 s |

A 1200 m map is **seven times emptier than an AoE2 Large game** — a unit every 38 m. That is what
playing it feels like, and it was never computed.

**600 m.** The only figure where nothing is badly wrong. The one thing it costs is the
response-to-raid ratio, 4.4–8.8x down to 2.2–4.4x — and what that ratio has to do is make central
response impossible against a 30 s raid. At 600 m a raid takes 30 s and your response takes 88 s.
**You still cannot get there in time, so the structures still have to answer and delegation is still
forced.** The thesis survives; the emptiness does not.

The old table stands as the *engineering* reference, because extent is a parameter and the routing
layer is measured across all of it:

| | Small | Medium | **Large** |
|---|---|---|---|
| extent | 800 m | 1000 m | **1200 m** |
| AoE2 Large multiples (area) | 4x | 6.3x | **9x** |
| walk across, sim | 8.9 min | 11.1 min | **13.3 min** |
| walk across, wall @1.5x | 5.9 min | 7.4 min | **8.9 min** |
| fine cells @ 0.5 m | 2.56M | 4.00M | **5.76M** |
| regions @ 32 m | 625 | 1,024 | **1,444** |
| settlement-core capacity | ~9 | ~14 | **~20** |
| one *flat* flow field | 10.2 MB | 16 MB | **23 MB** |
| agent-index buckets @1.5 m | 284k | 445k | **640k** |

### Three derivations of the size — kept, with the correction above

All three land at 800–1200 m and **all three bound the map only from below**, which is the flaw. They
are correct about direction and silent about the limit; §3's density table is what supplies it.

1. **Response time must exceed raid time at the periphery.** A raid takes ~20–40 s to destroy a
   hauling point. At 1200 m with four players, a territory is ~600 m and its periphery ~300 m from
   the core — 176 s at soldier speed. Response >> raid, so delegation is forced rather than optional.
2. **Tenure — the argument that actually decides it.** Every rung is capital spent on a *place*,
   recovered over the time you hold it. Contest frequency is the rate at which that capital is
   destroyed; when the contest interval falls below the payback period, the ladder is not merely
   harder to climb, it is **dominated** — the lowest-capital-exposure strategy wins, which is
   raiding. Reach R is set by speed and tolerable absence and **does not depend on map size**, so
   contested area is roughly P·πR² of overlap against L² and the *held* fraction grows as L².
   Doubling extent quarters the contested fraction. A small map does not make growth difficult, it
   makes growth wrong, and the low-attention mode dies — not because the player lacks patience but
   because building is a losing move.
3. **Cores × tenure radius.** A core's defensible/servable radius is ~150 m; a player holding 3–4 of
   them occupies ~500 m of frontage; four players in a 2×2 with a contested margin needs
   ~1,000–1,200 m.

### The clock

Compression is a **tick-rate** multiplier, never a speed multiplier. See §11 for why they are not
interchangeable. Default **1.5×**; implemented at `RtsGameLoop.cs:425` by scaling `time.Delta`
before it enters `simulationAccumulator`. `FixedDeltaSeconds` stays 1/30, so **not one constant
Thread B tuned is touched**, and the spiral guard (currently `Math.Clamp(..., 0.0, 0.25)`) scales
with it or accepts a 1.5× catch-up burst.

| | wall clock | sim seconds | day units |
|---|---|---|---|
| year | 1 hour | 5,400 | 270 |
| Spring (sow) | 13.3 min | 1,200 | 0–60 |
| Summer (build/cut) | 20 min | 1,800 | 60–150 |
| Harvest (crunch) | 11.1 min | 1,000 | 150–200 |
| Winter (drain/raid) | 15.6 min | 1,400 | 200–270 |
| day unit | 13.3 s | 20 | |

**Seasons are canonical and crop windows are derived from them**, the same way `PathCost` is derived
from `SpeedMultiplier`. The prototype had three disagreeing windows — `farmPressure` following the
season names, `cropCycleAt` following a crop model under which *nothing is harvested during the
season called Harvest*, and `summerRetention`'s comment following a third. They drifted because
nothing forced them to agree. Derive, don't duplicate.

**No day/night cycle.** Three reasons: half your check-ins would land in the dark, which taxes
exactly the mode whose premise is looking at things; a fixed sun means **static geometry's shadow
map only rebuilds when buildings change**, buying back render budget the simulation needs; and you
keep the visual variety anyway by changing sun angle and colour grade **per season**, four
transitions a year, still effectively static. That last point matters more than it sounds — **the
season must be readable from the screen in one second**, before any number is read, and a seasonal
sun is the cheapest and most important HUD element in the game.

---

## 4. The persistent world

### Idle is cheap exactly when it is needed

The no-LOD commitment looked like a straight cost. It is not, because the mode that demands it is
the mode that has stopped paying for rendering: idle is sim-only, no draw calls, no cascades, no
ImGui. Extrapolating Thread B's measured 2.3 ms/tick at 500 agents to ~9 ms at 2,000:

| | 30 Hz (1x) | 45 Hz (1.5x) | 60 Hz (2x) |
|---|---|---|---|
| sim work per wall-second | 270 ms (27%) | 405 ms (40%) | 540 ms (54%) |

Compression is a straight multiplier on the simulation budget, and 2× halves the headroom. It also
promotes the two area-scaling costs in §10 from hygiene to **product requirement**: a background
window quietly eating a full core is something people close.

### Idle is a strategy, not a penalty

Threat scales with **reach, not the clock** — escalation is driven by your expansion, not a timer.
Follow it through: attending buys expansion, expansion raises your threat tier, idling forgoes
expansion and therefore draws no additional pressure.

> **Presence buys growth and costs safety. Absence costs growth and buys safety.**

The skill rewarded is knowing when to spend attention and building the thing that holds without you.
Big maps and walking speed supply the rest of the margin: conquest is slow, and slowness is what
gives an absent player time to come back.

### The AI opponent is the player's delegation layer

Policy is **local and attached to buildings**, never a global slider (see §6). Given that, an AI
neighbour needs no decision system for most of its behaviour — it needs the same structure-level
policies the player uses, plus a thin strategic layer choosing what to build and where. Therefore:

- A player's settlement while idle runs **the identical code** to an AI settlement.
- Testing the AI *is* testing the idle mode. One autonomous layer, two consumers.

> **Acceptance test: the AI strategic layer should be able to play the player's settlement.** If
> handing it over produces something sensible, the delegation layer is good. If the AI needs special
> powers — teleported resources, omniscience, free labour — the delegation layer is too weak for the
> *player* to use either, and the low-attention mode was never going to work.

**Consequence for the build order:** soak tests and the AI are the same artefact. A soak test needs a
world doing economically meaningful things for a long time, and the strategic layer is what supplies
it. The original plan listed soak tests as a separate item; they merge.

**Consequence for world generation:** AI needs *dispositions* — producers, traders, raiders — and the
mix is a world parameter. If every neighbour plays raider the world eats itself and there is nothing
to raid. This is what will make one map feel different from another.

### The chronicle

"Come back and watch consequences propagate" only works if the consequences are *readable*, or you
return to find your things gone with no account of why. The persistent world needs a history:
*"the north granary was raided in spring; haulers rerouted through the south pass; the pass has been
congested since."* This is the exception-reporting HUD extended over time, and in LAN it becomes a
social object — what happened while you were away includes what other people did.

### LAN specifics, and three traps

Lockstep is right: only commands travel, and determinism is already an asserted property
(`identical simulations stay deterministic`).

1. **Compression is a world property, not a preference.** All clients tick at the same rate.
2. **The live tuning sliders are a desync bomb.** `plan-rts.md` already notes those values are now
   mutable process-wide state. Under lockstep one person nudging *turn rate* desyncs everybody. They
   must be dev-only or synchronised before any netcode exists.
3. **Late join needs snapshots.** Deterministic replay from a command log works, but replaying hours
   does not. Periodic state snapshots are the answer — and §5 promotes this from a nicety to a
   core-loop requirement anyway.

---

## 5. The career

**An average ruling career is 20 years; 30 is great; 40–45 is exceptional.** Beyond that it should be
practically impossible not to step on each other's toes and be destroyed many times over.

### The terminus is the large map becoming a small map

Run §3's tenure argument forward in time. Tenure is high because free space is abundant and reach
does not overlap. Everyone grows. Reach overlaps. The contested fraction climbs toward 1, tenure
collapses, and the ladder becomes unclimbable — at which point the dominant strategy reverts to
raiding, exactly as it does on a small map.

**The map size never changes. The free space does.** A terminus with no timer, no tech ceiling and no
victory screen: the world closes because it filled up, and how long you last in the closing world is
the score.

There is a **second, independent closing force** (see §6): your own arrangement's upkeep grows with
its height, so consumption at rest climbs toward production. The map closes from outside; the
settlement closes from inside. A 45-year career is exceptional because you have to hold off both.

### Does the map produce those numbers?

| | |
|---|---|
| core capacity at 1200 m | ~20 map-wide, ~5 per player at four players |
| founding rate to saturate by year 12–15 | one core per ~3 years |
| contested endgame begins | ~year 12–15 |
| careers end | 20 avg / 30 great / 45 exceptional |
| 20-year career in wall clock | ~20 hours of open-game time |

It lands, and it hands the economy its anchor for free: **founding a settlement core should cost
roughly three years of a mature settlement's surplus.** Not a guess — derived jointly from map
capacity and career length, and everything from storage capacities to production rates tunes
against it.

### Succession: fresh start on a marked map

The world outlives the ruler. A career ends — usually violently — and a new one begins on the same
map, which has not reset.

**Ruins must be mechanically live, not decorative.** Old roads still carry the speed multiplier;
cleared land is still cleared; foundations are re-usable; a burnt palisade line is still a line the
enemy must route around. That gives a map an **age**: a young world is wild and free, an old one is
dense with roads, ruins and cleared ground — *better infrastructure and less free space at once*, so
each successive career starts richer and closer to the closing.

**Consequence: serialization is a core-loop requirement, not a save feature.** The discipline is
cheap applied continuously and expensive retrofitted. Prefer stable ids over references — the
codebase already does this well (`AgentId` with tombstones, ids never reused).

---

## 6. The economy

### Storage shifts time. Hauling shifts space. Trade shifts both, across ownership.

That taxonomy is the whole economic layer, and it says what each mechanic is *for*.

### A resource is a physical thing until the moment it is consumed

**Added 2026-08-19, and it corrected an implementation that had it wrong.** Grain exists at a place:
grown in a yard, carried on a cart's back, stored in a granary, **lying in the road if the cart is
destroyed**. It never stops existing because whoever was carrying it did. The first version of the
economy booked a dead carrier's load to a "lost" ledger, which quietly made killing a loaded raider the
most effective way to destroy grain — the exact opposite of §7, where *"the return trip is the
defender's window"* only means anything if intercepting a raider **returns** the loot rather than
denying it.

So a dropped load becomes a **heap**, which is simply a node with stock and nobody looking after it.
The hauling board already looks for stock in the wrong place, so it collects heaps without being taught
what one is, and a heap belongs to no faction — which is the whole of looting, with no rule about theft
anywhere. The conservation identity got *shorter*: there is no loss term, because nothing is lost.

**The one exception is consumption, and it is the only abstraction in the chain.** A **house** is a
clocked sink: it draws for its occupants, and what it draws comes out of the store whose catchment
reaches it *without anybody carrying it*. That is this section's scoping decision made concrete —
households draw from their catchment directly, because a hauler per household would put hauler count in
proportion to population rather than buildings. Everything about distribution then falls out with no
further rules: reach decides whether a house is supplied at all, so you cannot sprawl past your
granaries, and a household outside every catchment goes hungry however full the stores are.

It also removed work rather than adding it. Bodies used to ask, every few seconds, which granary was
nearest them in route seconds — a routing query per person for an answer that never depended on the
person, and one that let a farmhand posted at the edge of a holding starve while living next door to a
full granary. A house does not move, so the catchment question is asked of the *building*, once, when
the set of stores changes.

### Consumption is the demand side of the hauling problem

Resources sink **by population type, year round, from the nearest source**. Everyone eats. Soldiers
eat more and sink other resources slightly. Repairs and construction sink stone and wood. **Season
affects consumption**: wood burns much faster in winter because every house needs heating.

The prototype papered this over with a generous distance limit, exactly as `totalSecured()` papered
over the supply side. Making it physical means consumption is spatial: a house pulls food from a
granary, a hearth pulls wood, and *reach* decides whether a building is supplied at all. Settlement
layout starts to matter with no new rule — you cannot sprawl past your distribution. Which is the
ladder in economic form: no store → consume where and when you produce · granary → houses cluster
around it · haulers → the granary can be fed from further out · second granary + link → a district
runs without me.

### The flow calendar — two commodities on offset cycles, two crunches

| season | food | wood | labour |
|---|---|---|---|
| Spring | drawing down | moderate | sowing crunch |
| Summer | drawing down | stockpiling **for winter** | free — build *or* cut |
| Harvest | **+++ spike** | moderate | harvest crunch |
| Winter | drawing down, no production | **+++ drain** (heating) | free — military, raids |

Food stores peak at the end of harvest and bottom just *before* the next one; wood peaks in autumn
and bottoms in late winter. Storage is needed for both, on different clocks, and summer's free
labour is a real allocation decision with a deadline — build now, or cut wood so you do not freeze.

**Winter is the combat season**, and it falls out rather than being designed: farm labour is free for
everyone, food is scarce so raiding is motivated, and granaries are full from harvest so there is
something worth taking. So the economic and military ladders **alternate in prominence across the
year instead of competing for attention simultaneously** — which is what you want in a game where
attention is the scarce resource. The year is: prepare → build → crunch → fight.

### Catchments are bounded flow fields

"Nearest source" asked per-consumer per-tick would be hot. Invert it: **sources own catchments.** A
granary runs one bounded Dijkstra out to a cost limit; consumers inside are bound to it; recomputed
only when the network changes. That is `BuildFlowField` with an economic reading and a bound —
which is exactly the *"flow fields bounded to a region rather than the whole map"* option the
routing work floated as an optimisation. **The economy independently wants the structure the router
needs.**

- **Catchments are measured in seconds, so they follow terrain and roads.** A catchment stretches
  along a road and stops at a ridge. Roads literally grow usable territory, and settlements form
  ribbons along them — nobody has to author that.
- **One granary per core, with a number: 60 s at hauler pace, which is 66 m.**
  **Re-derived 2026-08-19**, because the original — ~90 s at 1.5 m/s, ~135 m — was sized against a
  1200 m map and against walking pace, and both of those moved.

  Two corrections, and the second is the one that matters. A catchment is a *hauling* budget, so it
  is denominated at the hauler's 1.1 m/s (§3) and not at a villager's walk. And the original never
  checked the round trip it implies, which is what a player actually watches.

  | budget | radius | catchments per territory | round trip from the edge | mean round trip | soldier to the edge |
  |---|---|---|---|---|---|
  | 90 s at villager pace *(the old number)* | 161 m | **1.10** | 195 s | 130 s | 95 s |
  | 90 s at hauler pace | 99 m | 2.92 | 120 s | 80 s | 58 s |
  | 75 s at hauler pace | 82 m | 4.21 | 100 s | 67 s | 49 s |
  | **60 s at hauler pace** | **66 m** | **6.58** | **80 s** | **53 s** | **39 s** |
  | 45 s at hauler pace | 50 m | 11.69 | 60 s | 40 s | 29 s |

  Round trips are wall clock at the default 1.5x compression; a territory is a quarter of the 600 m
  map, 90,000 m², a disc of radius 169 m.

  **At the old number the mechanic is switched off.** 1.10 catchments per territory means one
  granary covers everything a player holds, so "expanding past it requires a second granary plus a
  hauling link" never fires and the whole hauling network is decorative. That is not a value that
  wanted tuning, it is a mechanic that had quietly stopped existing.

  At 66 m a player's territory holds six or seven catchments, so the second granary is forced early
  and the network is real. And the coincidence this bullet always claimed actually arrives:
  **39 s at soldier pace against a raid that takes 20–40 s**, so the radius you can supply is the
  radius you can defend, and the natural economic unit and the natural defensive unit are the same
  size — which at 161 m and 95 s they were not.

  **§3's derivation 3 uses a ~150 m core radius and is the same number**, so it is wrong in the same
  way and by the same factor. It is left as written because that section is kept as the record of a
  superseded argument, but nothing should be derived from it again.

### Scoping: hauling is node-to-node only

**Granary ↔ farm ↔ trade post ↔ forward depot.** Households draw from their catchment directly. A
person walking to the store daily is real, but a hauler per household puts hauler count in
proportion to *population* (hundreds) instead of *buildings* (tens), and every interesting decision
lives at the node level. This is the difference between a hauling network you play and a traffic
simulation you watch.

### Every resource needs a standing sink

A surplus should never be a chore; it should be a **signal**. The fundamental sink is **grain →
people**: drowning in food should grow your population on its own. Wood and stone sink into
construction and repair; food plus equipment sinks into soldiers. So a persistent surplus means *a
sink is blocked*, and the game says which:

> *"Grain surplus rising — population capped by housing."*

That converts "I must manually rebalance" into "one blocked sink, named, with the fix implied".

### Trade is a smoother, not an income source

The honest motivation: seasons create fiddly work where you drown in one resource and run a stupid
surplus on another. A trade route is a **standing conversion of surplus into deficit that runs
without you** — the economic equivalent of a standing garrison, and it sits on the ladder like one.

The trap, and the interesting part: **the season synchronises everybody's surplus.** After harvest
*nobody* wants grain — your neighbours are drowning in it too — so the price of what you have most of
collapses precisely when you have the most. Economically true, and mechanically essential, because it
stops trade trivially deleting the lever the seasons were built to be.

What breaks the synchronisation is **difference**, and difference grows with distance: other terrain,
other crops, other specialisations, other wars.

> **trade value ≈ price differential × throughput** — the differential grows with distance
> (decorrelation), throughput falls with it (round-trip time). There is an optimum, it is spatial,
> and the player solves it by placing trade posts and docks.

That makes distant partners and water routes valuable *for a reason*, rather than by the fiat that
AoE2's distance-scaled trade gestures at without justifying. It is also trade's third framing:
**time-shifting through somebody else's storage.**

---

## 7. Combat, offence, defence and alarms

### Micro is a multiplier, not a replacement for force

Settled 2026-08-18, after being assumed wrong for most of the design pass:

> **Effective combat power = material x micro.** Micro is a multiplier, so its value is highest at
> parity and irrelevant at the extremes.

Offence is therefore **not** an idle verb. You can click an army onto an opponent and go and make
coffee knowing they will be dust — but only if you are disproportionately stronger. Or you can
tactfully micro two soldiers onto two villagers and steal a season's food with a force that could
never win a battle. Both are correct play; the difference is where you spent your attention.

The earlier assumption — that combat should be decided by position, formation and attrition rather
than by reflex — protects the low-attention mode by **nerfing micro**. The multiplier model protects
it without nerfing anything, because material superiority is exactly what the economic ladder
produces:

> **The economy buys you the right to not pay attention.**

That is the delegation thesis in a second domain, and it is §2's identity again: attention and
material are substitutes in combat exactly as labour and attention are substitutes on the farm.

It also means **no mechanic needs forbidding.** You may order anything; whether it succeeds
unattended is governed by the force ratio. Rates, not gates.

### What the multiplier must be worth, and how to measure it

The single number that decides whether both halves of the design survive. Too high and material
never matters, so the economy is decoration. Too low and micro does not pay, so the high-attention
mode dies.

One counterintuitive piece of arithmetic, recorded because it will otherwise be mis-set: under
Lanchester's square law, fighting strength goes as *quality x N squared* — quality enters linearly,
numbers quadratically. **A micro edge worth 2x in quality is only sqrt(2) ~ 1.4x in numbers.** So
state the target the way a player perceives it:

> **Micro should be worth roughly 1.5-2x in numbers.** Ten well-handled soldiers beat fifteen to
> twenty on auto. That puts "disproportionately stronger" at about **2x to walk away, 3x to be
> certain** — which is also how it should feel.

**This is measurable, not arguable.** Build a **competent-combat bot** alongside the plain auto
layer — focus fire, pull wounded bodies out, kite on the speed differential, choose targets by value
— and run the two against each other at varying force ratios. The ratio at which the competent bot
draws against auto **is** the multiplier, in the units a player perceives. Tune to 1.5-2.0.

That bot has a second job, which is why it is worth building properly rather than as a test fixture:
it is what a hard AI spends its attention on.

### AI difficulty is an attention budget, not a cheat

If the AI is a player running the same delegation layer (§4), the honest difficulty dial is **how
many simultaneous engagements it micros.** An easy neighbour never spends attention; a hard one
handles three fights at once. No resource cheats, no vision cheats — nothing that would break the
"the AI can play the player's settlement" acceptance test.

It is diegetically consistent, since the AI's attention is scarce exactly like yours, and it yields a
strategic layer for free: **you can bait the AI's attention.** Feint in one place to pull its micro
there, and strike where it has fallen back to auto.

### Combat resolves by physical contact, never by abstract resolution

A pure square law punishes splitting forces quadratically, which means doom-stacking and no
distributed defence — and that would fight everything about multiple cores and territory. Two things
already in the design defeat it, and neither needs a rule:

1. **Frontage is physical, not modelled.** At a 1.5 m gate only two bodies can engage at once,
   because ORCA and the geometry say so. Concentration stops paying at a chokepoint, and the square
   law degrades toward the **linear** law, emergently, exactly as real fortifications work. Fifty
   attackers against ten defenders at a gate is a series of 2v2 fights, and the defender wins them.
2. **Walls multiply defence**, which is what makes a small garrison viable against a larger field
   force — rungs 3 through 6 doing precisely the job they are listed for.

So **units fight who they can physically reach.** The physicality is what buys the frontage limit,
the frontage limit is what makes the defence ladder mean anything, and combat cost is bounded by
geometry rather than by army size.

### Micro's arena is the window between alarm and response

Two soldiers against two villagers is the whole game in miniature: penetrate, catch the fleeing
villagers, **load the food**, and leave before the response lands. A timing puzzle against a clock
this document already derives:

> The raider's window is (R − A) minus the defender's response time A, for a threat spotted at radius
> **R** against an asset at radius **A**. **Every rung of the defence ladder shortens that window** —
> an outpost pushes R outward, a stationed garrison collapses A to zero. **Every point of micro skill
> stretches what can be done inside it.**

The defence ladder and combat micro are the same axis measured from opposite ends. And because loot
must be carried, the escape is where the defender gets their shot.

### Taking control is an interrupt, not a mode switch

A manual order is an **interrupt** in the three-layer jobs model: it overrides the current *activity*
and expires, leaving the *assignment* untouched. So you grab units mid-auto-fight, handle it, release
them, and they return to what they were doing.

You never toggle a unit into "manual mode", so there is no state to manage and no way to strand units
in it — a classic RTS failure designed out for free by a structure adopted for other reasons. This is
what makes "alarms, so you can micro if you want to" actually work.

### Offence: delegate the maintenance of pressure, never the act of aggression

The worry was that offence is maximally high-attention and silently deletes the low-attention mode.
That is true of *standard* offence, where the army is your whole economy converted into one fragile
bundle with no state between "at home" and "invading". The missing middle state is **pressure** — and
pressure is a *standing state*, which is exactly what makes it delegable where an assault is not.

| rung | what is delegated | standing, or act? |
|---|---|---|
| none | nothing — I only react | — |
| scouts / patrols | observation and denial; they fight what comes to them | standing |
| forward outpost | presence maintained in their ground | standing |
| route denial | interdiction of their traffic | standing |
| siege camp | attrition against a fixed arrangement | standing |
| raid order | a strike, unattended | **act** — succeeds only at the ratios above |
| annexation | their territory produces for me | standing, once taken |

That line — **maintenance of pressure yes, act of aggression no** — is sharper than the original
ladder and it needs no exception for the raid order, because the force ratio already governs it.

Note what all of it attacks: their **logistics**, not their army. The commitment that makes defence
meaningful ("logistics must be attackable") is the same one that makes pressure delegable.

### Raiding is hauling with a hostile source

Loot must be **carried home**. A raid is therefore a hauling operation whose source is somebody
else's granary — same routes, same congestion, same seconds, plus a threat term. Which gives, for
free:

- **Distance prices raids honestly.** A forward outpost near an enemy granary can be genuinely
  cheaper than hauling from home, so "supply a distant frontier partly by raiding" is a real
  calculation in the existing currency rather than a special case.
- **The return trip is the defender's window.** Loaded raiders are slow, so interception is emergent
  rather than scripted.
- **It is self-limiting.** A raider's income is bounded by somebody else's surplus. Parasitism needs
  hosts, so raiding cannot dominate a world — but it can absolutely sustain a small population.

### Threat is a cost term, not a barrier

Add threat to the route cost as an honest expected-seconds number:
`p(intercept) × (time to replace cargo + time to replace hauler)`. Then haulers avoid dangerous
ground because it is expensive; a raid succeeds when it makes a route expensive enough that the
network reroutes, which the player experiences as the economy quietly slowing; and guards, towers and
patrols are expenditure that *lowers* the threat term on routes you chose to care about.

**Two warnings, both already paid for once in this codebase.** Threat must be an honest number of
seconds, not a large one meaning "do not go here" — that is the barrier-as-cost mistake in a third
costume, and `plan-rts.md` §1 records it costing two attempts. And threat needs a **fade matched to
how fast danger actually clears**; invariant 15 is `DecaySeconds = 4.5` making every other term bid
against a jam that had already drained, and a threat field outliving its raiders reproduces it
exactly one layer up.

### Alarms are the interface between the two modes

Without them the low-attention mode is not passive, it is **blind**. Attacks and sightings ring; the
player reacts if they want to.

The important part is that the threshold is tunable, because **alarm policy is delegation of
attention itself** — and like all policy it attaches to *places*. Early career, ring for everything.
Late career, "ring only if the garrison is losing."

This finally gives the outpost rung a computable value, in seconds like everything else:

> Threat spotted at radius **R**, asset at radius **A**, both sides at soldier speed. The enemy needs
> (R−A) to reach the asset; you need A to get there from the core. You arrive in time only if
> **R > 2A**.

At a core tenure radius of A = 150 m the watch line must sit beyond **300 m**. And as territory
grows, A grows and the required R grows *twice as fast*, until it walks off the edge of your
holdings — **which is why rung 5 (guards stationed at the convergence) exists.** Each rung of the
ladder is forced by the previous one failing at scale, rather than being a list someone wrote down.
It is also a placement rule the AI can evaluate directly.

Two practical requirements: alarms in a background window need to be **OS-level** (window flash,
sound, notification); and they need **aggregation and escalation**, or the contested endgame — where
everything is under pressure all the time — becomes a continuous klaxon and the player learns to
ignore it. "Three raids in progress, north" is one bell.

---

## 8. Autonomy time — the score, the win condition and the soak assertion are one number

With continuous consumption there is no stall, there is **decline**:

> **Autonomy time = how long your stores last at current net flow, without you.**

Good delegation means production covers consumption and stores hold through the deficit seasons; poor
delegation means the settlement shrinks, gradually and legibly. It is computable every tick and
directly displayable — *"granary: 2.4 seasons at current draw"* — which is the one-verb HUD again.

The calendar gives it structure, so the unit is not minutes but **stress points survived**:

1. survives a quiet season
2. survives **harvest** unattended (the labour spike)
3. survives **winter** unattended (the drain, and the raids)
4. survives a **full year** unattended
5. survives several

Monotone in how well you built, legible to the player, a win condition that does not end the game
("it ran a full year without me"), and directly assertable as a soak test. It also fixes the
arbitrariness of "N minutes" — a settlement that runs 40 minutes but skips harvest has proved
nothing.

---

## 9. What Thread B bought, and the debts that go live

- **Routing cost is time.** `TerrainSurfaceRules.PathCost` is derived as `1/SpeedMultiplier` so
  router and physics agree, and congestion is expected delay in seconds. A hauling assignment can be
  priced in the same currency as everything else, and a jammed route makes a different hauler cheaper
  automatically. This is the decision the entire economy rests on.
- **The congestion field** is worth far more to many independent haulers sharing routes than it ever
  was to squad moves.
- **Static ORCA lines, turn cost and manoeuvre amplification** matter because the game has gates in
  palisades — the exact geometry they were built for.
- **`ColliderRole.Interactable` / `Damageable` and `FactionRelations`** finally get consumers.
- **Formations and cohort transit are core.** Real combat, allies and enemies, and armies to
  stabilise all need them.

### Debts that go live the moment there is more than one body type

- **One body model.** `AgentDefaults` assumes a single radius, speed and turn rate. Workers,
  soldiers, carts and raiders break that at once, and the logged "congestion delay does not scale
  with unit speed" item goes live the moment speeds differ. **Both settled in Session 4**: the
  roster is in §3, and congestion is now priced by the body's own speed on a dial, with the sweep
  and the reason it is a dial in `plan-rts.md` §7.
- **Carts want a turning circle.** The speed-scaled turn rate tried and reverted in Thread B
  (`ω = a/v`) was wrong for people because it lifted the anti-spin limit exactly where crowds need
  it — but it is *correct* for a loaded cart. Per-type turn models resolve the conflict.
- **Acceleration is 16 m/s²** and every self-test threshold is calibrated against a body that reaches
  its speed instantly. Settle the feel on the slider, then re-base the tests.
- **The flow-field cache keys on `(goal, radius, navRevision, congestionRevision, chargeTurns)`**, so
  unit types multiply retained fields by radius. Harmless at 16 KB per *regional* field; fatal at
  23 MB per global one. §10 retires this as a side effect.

---

## 10. Engineering consequences

### Flat global fields are dead at every candidate size — on memory, and, it turned out, on timing

One field is 10–23 MB, multiplied by distinct goal × body radius × retained congestion revision, and
discarded wholesale on every nav edit. That is arithmetic, not a performance guess, so **the
architecture decision does not wait on the scale proof and does not depend on which map size wins.**
What still needs measuring is the *constants* — region size, portal density, cache depth, and whether
the hauling workload behaves. Architecture from arithmetic; constants from measurement.

**Measured, Session 1:** they were dead on timing too, and by more than the memory argument suggested
— 4.1 s for the first route on a 1200 m map and ~390 ms of every tick after it. The arithmetic was
right about the conclusion and understated the case.

### Portal routing over 32 m regions

64×64 fine cells per region = 4,096, deliberately comparable to the 3,600-cell world where every
Thread B constant was measured, so they all keep their meaning. **The fine layer stays at 0.5 m and
is not touched.**

| | Small | Medium | **Large** |
|---|---|---|---|
| regions | 625 | 1,024 | **1,444** |
| portal nodes, open terrain | ~7.5k | ~12k | **~17k** |
| …with ~50% impassable | ~3.5k | ~6k | **~8.5k** |
| one local flow field | 16 KB | 16 KB | **16 KB** |

Global search becomes a Dijkstra over ≤~17k portal nodes at ~6 edges each. Today's 3,600-cell field
is 8 neighbours ≈ 28,800 edges. **At the large map the global route problem is single-digit
multiples of the one already solved on a 30 m map, and every local problem is smaller than it.**

### Jungle is an optimisation, not filler

A region of solid jungle has **no portals**: it contributes nothing to the abstract graph but the
fact that you cannot cross it. A map that is ~50% dense growth with rivers, ridges and passes has a
portal graph roughly half the size of an open map at the same extent, and better conditioned, because
real chokepoints are what portals are for. It bounds the agent problem the same way — bodies can only
be on walkable ground, so the congestion field's active set and the spatial index's occupied buckets
both shrink. **Filling a large map with jungle makes it cheaper than a small open one of the same
walkable area, and gives structure for free.** It also feeds four rungs at once: slow ground (a real
`SpeedMultiplier`), channelled routes, concealment that makes the outpost rung mean something, and
diffuse wood that turns clearing into labour.

### Seasonal terrain, and the second argument for portals

Rivers freeze (a dock closes; the ice becomes walkable), mud season slows roads, snow shuts a pass.
All of it is seasonal `SpeedMultiplier`, priced for free. But it bumps `grid.Revision`, and the
current cache treats a nav edit as invalidating **everything** — *"A navigation edit makes every
older field unreachable, so those go immediately."* Four times a year the whole route cache would
evaporate at once, on a schedule.

So: seasonal transitions want to be **staged over a few in-game days** rather than flipped in a
single tick — which is also how weather behaves. And region-scoped revisions rebuild only the
regions a freezing river touches. **Hierarchical routing is what makes seasons affordable**, not just
what makes scale affordable.

### Two area-scaling costs that no routing hierarchy touches — measured, then removed

Both were verified in source, both invisible at 30 m, both scaled with map **area** per tick. Session
1 measured them against predictions written in advance and then removed both; the numbers and the
falsification are in §13. What each one was, and what it is now:

1. **`CongestionField.Update`** decayed *every* cell of three arrays every tick — `pressure`,
   `flowX`, `flowZ`. At 1200 m that is 5.76M cells, ~132 MB of memory traffic per tick read and
   written, before any agent deposits anything. **Measured at 10.9–12.3 ms/tick and flat in agent
   count, as predicted in direction and 3× under in magnitude.** Now sweeps an ordered set of the
   cells actually holding pressure, which — because pressure comes only from bodies that want to move
   and cannot — is the ground where movement has recently failed rather than the ground anyone has
   walked over: **1.2k–4.2k cells under a full crowd, and zero when nothing is stuck.** The decay
   semantics invariant 15 depends on are untouched, which is not an argument, it is the diffed
   output: every metric in `--selftest` and `--benchmark` is bit-identical across the rewrite.
2. **`AgentSpatialIndex`** was `List<int>[width*height]`, dense over the whole terrain at 1.5 m, with
   `Rebuild` running `foreach (var bucket in buckets) bucket.Clear();` — six times per tick, once in
   the velocity solve and once per relaxation pass of the contact solve. At 1200 m that is 640,000
   Lists cleared six times over to index 2,000 agents. **Measured at 2.4 ms/tick under load, inside
   the predicted band.** Now clears only the buckets the previous rebuild wrote, which is bounded by
   the crowd. Deliberately *not* the bounding-box fit proposed here: the box is only small while the
   army is in one place, and units spread across their own territory is this game's normal state.
   Bucket assignment, sweep order and query results are unchanged — it stops visiting cells that were
   already empty, and nothing else.

**What the measurement found that this section did not predict** is in §13 under Session 1, and it is
larger than either of these by two orders of magnitude.

### Determinism must be extended, or it quietly stops meaning anything

The existing determinism self-test covers movement only. Every new system — jobs, hauling,
construction, AI — must extend it, or the test keeps passing while the game layer becomes
non-deterministic through an unordered dictionary iteration nobody noticed. Lockstep LAN and
career-to-career persistence both depend on it.

### Soak testing is tractable headless

At ~9 ms/tick with 2,000 agents, headless uncapped ticking runs ~3.7× real time; early-career loads
(~300 agents, ~1.4 ms/tick) run ~24×.

| | soak duration |
|---|---|
| early-career, 10 years, ~300 agents | ~40 min — a CI gate |
| full 20-year career to saturation | ~6–8 h — an overnight job |

Assertions: nothing permanently stalls, no counter drifts, no route churns forever, and **autonomy
time climbs monotonically with the structures built**.

---

## 11. Corrections made this pass — kept because each looked right

- **Speed and tick rate are not the same dial.** Both make a large map feel less sluggish and are
  indistinguishable from the player's chair on day one. Raising *speed* grows reach R in metres,
  which grows the contested fraction and hands back exactly the tenure the large map bought — a 2×
  speed bump on an 800 m map gives the contest geometry of a 400 m map. Raising *tick rate* preserves
  every in-game ratio and only shrinks wall-clock waiting. Compression also degrades micro without
  touching strategy, because human reaction time is fixed in wall clock — so it is a
  delegation-forcing dial and belongs on the ladder.
- **"The longest haul must fit inside a season" was the wrong constraint** and is dropped. A long
  year makes the map feel small under that rule. Distance actually binds through two mechanisms with
  different clocks: **response time** (~60–180 s, independent of year length) for military distance,
  and the **harvest window** (a genuine cliff — crops rot, winter comes) for economic distance. Both
  survive a one-hour year.
- **"Absence buys safety" was half right.** With year-round consumption, absence is safe from
  *enemies* — threat scales with reach and you are not expanding — but never from *entropy*. Idling
  still burns stores, and the cost scales with how much ladder you are carrying. Better balance than
  the original claim.
- **Carts are payload; roads are speed.** A loaded cart alone (1.1 m/s) is *slower* than a walker.
  Extending the economic radius needs both: a walker on a road reaches ~270 m, a cart on a road
  reaches ~200 m but with 4–5× the throughput.
- **The "44× at 160k cells" estimate is superseded.** It was arithmetic from cell counts, correctly
  flagged as not a measurement. The real map is larger still, and the architecture question is now
  settled by memory arithmetic rather than by timing — but see §10 on which constants still need
  measuring.

---

## 12. Open questions

*Catchment radius was here and is now answered in §6 — 60 s at hauler pace, 66 m — because the
hauler having a speed is what made the derivation possible.*

- **The micro multiplier is a target, not a measurement.** 1.5-2x in numbers is reasoned from
  Lanchester and from how "disproportionately stronger" ought to feel; nothing has measured it. Until
  the competent-combat bot exists and the ratio is run, the balance between the two modes of play is
  a hypothesis. **This is the highest-value measurement in the game layer.**
- **Which micro affordances ship.** Focus fire, pulling wounded bodies out, kiting on the speed
  differential, target selection by value. Each one raises the multiplier and together they may push
  it past the band, so the set is a tuning decision rather than a feature list.
- **The offence rungs have been restated but not pressured.** Maintenance-of-pressure versus
  act-of-aggression (§7) is a sharper line than the original ladder and it looked right immediately,
  which is exactly the condition under which §11-style refusals get discovered late.
- **What auto-combat does when it is losing.** Retreat preserves material and concedes ground;
  standing preserves ground and loses material. Unattended, one of them has to be the default, and it
  interacts directly with whether an absent player can be ground down without ever being alarmed.
- **AI dispositions and the world-generation mix.** How many producers, traders and raiders; whether
  disposition is fixed or drifts with circumstance.
- **What a "marked map" records**, and at what granularity — roads and cleared ground certainly;
  foundations, place names, graves, and how ruins decay over careers are open.
- **Population growth model.** Grain → people is the proposed fundamental sink, gated by housing.
  Whether everyone works, and whether there is any dependency ratio beyond soldiers, is unspecified.
- **Alarm aggregation and escalation.** The requirement is clear; the design is not.
- **Are trade partners AI-run settlements or abstract markets?** Affects whether trade prices emerge
  from neighbours' actual stores or are generated.

---

## 13. Roadmap

Nine sessions. Each has a deliverable and a **gate** — the thing that says it is done — because
"it works" has never been a state this codebase accepts as an answer.

Session 4 is the elastic one: it is the smallest of the nine and the natural place to absorb
overflow from either side.

### Session 1 — Make the large map affordable — **done, 2026-08-18**

**Delivered.** Extent is a parameter (`new SimulationWorld(extentMeters)`), snapped up to a whole
placement cell so the navigation and placement grids describe one square — 800 → 801 m, 1000 →
1000.5 m, 1200 → 1200 m exactly. **The default is untouched and now asserted:** a self-test pins it
at 30 m, 60×60 nav, 20×20 placement, origin (−15, −15), because a default that drifted to 32 m would
leave every threshold in the suite passing while quietly measuring a different world.

`--scale` runs the matrix. Each case reports two passes: **idle** (no destinations, so nothing routes
and nothing deposits — the area-scaled floor on its own) and **moving** (the same crowd under one
group move — the honest tick). Two new phases were added to the breakdown to make the predictions
falsifiable at all: `congestion`, which was outside every phase, and `index`, which is a *subset* of
steering and collision rather than a column beside them.

**The predictions, and what happened.** At 1200 m, idle unless stated:

| | predicted | measured | verdict |
|---|---|---|---|
| `CongestionField.Update` | ≥3.5 ms, flat in agent count | **10.9–12.3 ms**, 500→2000 agents within 3% | right in kind, **3× under in size** |
| `AgentSpatialIndex.Rebuild` | 1.3–3.2 ms, flat in agent count | **0.8–1.0 ms** idle (2 rebuilds), **2.4 ms** moving (6) | **in band** under load |
| together | 5–7 ms/tick | **~14 ms/tick** | under by 2× |

Agent-count independence held exactly: congestion measured 4.86 / 4.88 / 4.79 ms at 801 m for 500 /
1000 / 2000 agents. Cost is linear in cells at **~1.9 ms per million** — 2.57M cells → 4.8 ms, 4.0M →
7.5 ms, 5.76M → 11 ms.

**After the two fixes**, at 1200 m with 2000 agents: congestion **11.7 → 0.098 ms**, index **2.39 →
0.068 ms**, the whole idle tick **13.3 → 1.4 ms**. The live congestion set holds 1.2k–4.2k cells
under a moving crowd and **zero** when nothing is stalled. World construction is 107–189 ms and a
1200 m world costs ~310 MB resident, which is arithmetic and unchanged — the sweep got cheaper, the
allocation did not.

**What the measurement found that nobody predicted, and it is the real answer.** The area costs were
never the problem. At 1200 m the tick runs at **~400 ms** and ~390 ms of it is `preferred` +
`recovery` — **full-grid flow-field rebuilds**, 22–26 of them per 120 ticks, plus **4.1 s for the
first route after a single move order**. §10 argued flat global fields were dead on memory rather
than timing. They are dead on timing too, by two orders of magnitude more than both area costs
combined, and the scale scenario now measures it every run. **This is Session 3's, and Session 3 is
now the session the large map waits on.** Nothing else should be built against a 1200 m world first.

**Gate: met.** `--selftest` **40/40** (38 before, plus the two new guards); every quality metric in
`--selftest` and `--benchmark` **bit-identical** against `HEAD` before the session — diffed line for
line, with only wall-clock figures moving. The area-scaled per-tick cost is inside budget at every
candidate size. The tick as a whole is not, for the reason above.

Two guards were added rather than assumed, both covering failures no movement metric would catch:

- **`congestion sweeps every cell holding pressure`** — the sweep now visits a tracked set, so a cell
  that takes pressure without being admitted is never decayed again and a jam that cleared goes on
  charging routes for the rest of the game. Asserted directly, at a jam and after it drains.
- **`a larger world leaves the tuned one untouched`** — the handoff's first rule, made mechanical.

> **Do not add threat to the route cost until this lands.** It is a new term over the same
> machinery, and adding it first means writing it twice. — *Landed. Threat is unblocked.*

### Session 2 — The body, at the slider

**Different in kind: a feel session, not a headless one.** It wants a human at the tuning overlay,
and it wants the 1200 m map running smoothly so the body can be judged on the real thing rather than
extrapolated from 30 m.

> **That precondition now holds.** It did not after Session 1 — the area costs were gone but routing
> was not — and Session 3 was taken first for exactly this reason. A 1200 m map with 2,000 bodies
> ticks at 16.6 ms, so the body can be judged on the real thing.

Settle top speed, acceleration, deceleration and time compression on the live overlay — §3 has the
proposed starting points and the reasoning for each. **Then** re-base the self-test thresholds
against the answer, never the reverse.

Tractable because the blast radius is smaller than it looks: `walked/optimal` and the other ratio
metrics are speed-invariant, so only the **time-based** thresholds move.

**Gate: met, 2026-08-18.** `--selftest` **42/42** with the new body, and every moved threshold is
recorded with its old value beside it — a single `WalkingPace` factor of 3 applied at each site, so a
threshold that moved because the body slowed is visibly distinct from one that moved for a reason.

**What actually had to change, and it was not only the timeouts.** Six tests failed on the new body
and they fell into three kinds:

1. **Durations** — how long a crossing takes, how long a queue drains, how long a body may stall.
   Multiplied by three. The uninteresting ones.
2. **Thresholds that were secretly speeds.** `0.5 m/s` to count as travelling, `0.25 m/s` to count as
   wanting to move, `2 mm` of progress per tick. Each was written as an absolute and each was really
   a *fraction* of the body they were tuned on — 0.5 m/s is a ninth of a run and a third of a walk, so
   a body picking its way at a third of walking pace would have been declared stationary. They are
   fractions now, and mean the same thing at any speed.
3. **Constants denominated in seconds that compete with travel time.** This is the one that bit. What
   a queue costs is the number of bodies ahead times how long one takes to clear a cell — and the
   second half of that is a property of how fast the body walks. Held as a flat 0.40 s, congestion
   silently became three times cheaper relative to detours, and **the pen scenario funnelled all
   thirty units through one exit**, which is precisely the failure that coefficient was tuned to
   prevent. It is `CongestionCellsPerPressure` now, derived from the cell crossing time. The same
   applies to every duration describing how long a physical condition lasts — jam decay, route
   commitment, aperture abandonment, body-in-the-way delays — all now scaled by
   `AgentDefaults.PaceScale`, which records that they were measured at 4.5 m/s.

**What the body bought, measured:** `mean-turn` **4.8 → 1.4 deg/tick**, `turns>60deg` **1.01% →
0.34%**, `infeasible` **1.3% → 0.1%**. The bodies have momentum and stopped snapping. `walked/optimal`
held at 1.10 → 1.12 (pen) and 1.36 → 1.42 (gate), which is the evidence that route quality survived.

**One measured regression, recorded rather than buried:** `dead-stops` **1 → 19** in the pen and
**29 → 43** at the one-cell gate. A dead stop is the avoidance solver finding no feasible velocity and
falling back to zero for a tick. Deceleration went from 16 m/s² to 3, so manoeuvres that used to be
rescued by braking hard now cannot be. Nothing fails on it — every body still arrives — but it is the
first thing to look at if crowds start reading as hesitant, and the deceleration slider is where to
look.

### Session 3 — Portal routing — **done, 2026-08-18**

Built as specified: region partition at 32 m (64x64 fine cells), portals along region borders, a
graph over portal *sides*, region-local searches for edge costs, refinement into per-region tiles,
and per-region congestion stamps. **The fine layer is at 0.5 m and was not touched.**

Four decisions made inside the implementation, three of them load-bearing:

1. **Nodes are portal sides, not portals.** One node per crossing would make the crossing itself
   free; on a route over forty regions that is four seconds of cost that is not there, in a layer
   whose whole discipline is that cost means seconds. Twice the nodes, and worth it.
2. **Nothing is eager.** The graph is structural and built in 2-3 ms; edge costs are computed the
   first time a crossing is expanded and cached against *that region's* congestion stamp; a tile is
   refined the first time something asks what a cell in it costs. Route cost is therefore
   proportional to how far the asking bodies are from their goal rather than to the size of the map.
3. **The abstract search is goal-directed, and that is the whole ballgame.** A plain Dijkstra from
   the goal settles every crossing nearer than the one it wants — a disc, where a route needs a
   corridor — and since settling a crossing costs a region-local search, the disc measured **759
   searches and 1.1 s for one 60 m move order**. With an octile-distance heuristic over the fastest
   ground the game has (admissible, so settled costs stay exact) the same order is 37 searches.
4. **A tile is seeded from every crossing the search has *priced*, not only those it has settled.**
   An unsettled price is an upper bound, which only ever makes a crossing look dearer than it is. That
   is what lets the search stop the moment the best way out of a region is certain — see the measured
   horizon below.

**The horizon was a guess, then it was measured.** The search originally kept looking a region and a
half past the cheapest crossing, on the reasoning that a better alternative might still turn up.
`--routingtest` compares hierarchical cost against the flat whole-map search, cell by cell, on a
200 m map of staggered walls:

| horizon | mean ratio | p99 | worst | cells lost | searches |
|---|---|---|---|---|---|
| 0 cells | 1.0064 | 1.0621 | 1.678 | **0** | 643 |
| 32 cells | 1.0051 | 1.0621 | 1.678 | **0** | 685 |
| 96 cells | 1.0051 | 1.0621 | 1.678 | **0** | 693 |

It buys thirteen ten-thousandths of mean route cost, changes neither the tail nor the worst case, and
costs 4.5x the work at 1200 m — a move order at 460 ms against 102 ms. **So it is zero.** The dial is
left in the code with those numbers beside it rather than deleted, because the next person to wonder
whether the search stops too early should re-run the measurement rather than re-reason it.

**What it cost and what it bought,** at 1200 m, the whole tick:

| | before | after |
|---|---|---|
| one move order (500 / 2000 agents) | 4,108 / 4,022 ms | **42 / 93 ms** |
| moving tick, 500 agents | 549 ms | **2.9 ms** |
| moving tick, 1000 agents | 536 ms | **8.8 ms** |
| moving tick, 2000 agents | 571 ms | **16.6 ms** |
| graph | — | 1,444 regions, 11,100 portals, 22,200 nodes, built in 2-3 ms |

§10's portal arithmetic predicted ~17k nodes for the large map against 22.2k measured, and one local
field at 16 KB, which is what a tile is. The route quality that buys: **mean 1.006x the flat optimum,
p99 1.06x, worst cell 1.68x, and not one cell lost.**

**Gate: met, with one qualification.** `--selftest` **42/42**, and every metric on the tuned 30 m
world is **bit-identical** to before this session — that world is a single region with no borders, so
the hierarchy provably has nothing to say about it, which is why it could land without re-tuning a
constant. Two new tests cover what the old suite structurally cannot. The qualification: a move order
at 1200 m costs 42-93 ms against a 33 ms tick, so it is one to three frames rather than inside one.
It was 124 ticks.

**Both named risks were real and both are handled.** Congestion reaches the graph because the
region-local searches charge `CongestionCost` exactly as the fine layer does, and the per-region
stamps mean a jam in one corner of the map does not invalidate every region's cached crossing costs.
Boundary artefacts are measured rather than hoped for: `a group crosses region borders without
swinging` counts heading reversals for bodies within two cells of a border against bodies well inside
a region, because a seam in the cost field shows up as bodies changing their minds at borders and
nowhere else.

**Then the field cache, same session.** The moving tick was still two thirds `preferred` +
`recovery`, because congestion is published as one whole-map revision and every revision therefore
rebuilt every tile a crowd had walked across — for pressure that had moved somewhere else entirely.
Two changes, both leaning on the per-region stamps that already existed:

1. **A region is only declared changed when it has changed by enough to matter.** Its stamp now moves
   when its own pressure total shifts by the same quantum the field already uses to decide a revision
   is worth publishing at all. Without it, a crowd walking across a map re-stamps its own region on
   every revision and every crossing cost in that region is recomputed for a change too small to move
   a route.
2. **A tile is inherited when its inputs are unchanged.** A tile is a pure function of its region, that
   region's congestion, and the prices of the crossings that seeded it, so it records all three and the
   next field for the same goal adopts it outright. Identical inputs, identical answer, and the array is
   never written after it is built.

| 1200 m, moving tick | before Session 3 | portal routing | + field cache |
|---|---|---|---|
| 500 agents | 549 ms | 2.9 ms | **2.3 ms** |
| 1000 agents | 536 ms | 8.8 ms | **3.7 ms** |
| 2000 agents | 571 ms | 16.6 ms | **11.2 ms** |
| region searches (1000 agents) | — | 588 | **131** |

**Every candidate size at every agent count is now inside the 33 ms tick**, and fidelity is unchanged
at 1.006x mean with no cell lost — the cache changes what is recomputed, not what is computed. A move
order is 41-92 ms, so the hitch is one to three frames; it was 124 ticks.

**Then the rule that made all of it cheap.** *Resolve to the resolution where the expensive check
actually decides something, and stay broad everywhere else.* A Dijkstra over a region's four thousand
cells exists to discover which way round the obstacles the cheapest path goes. Where there are no
obstacles it discovers, at four thousand cells of expense, that the cheapest path between two points
is a straight line — and the straight line has a closed form.

A region is *plain* when it is open everywhere, level everywhere, one surface throughout, and nobody
in it is stuck. That is not an exotic case, it is most of a map most of the time. On plain ground
every term the search charges is constant or absent, so both things the search was being asked for
become arithmetic: the cost of crossing the region is the octile distance between two border cells,
and a cell's cost to the goal is the cheapest crossing out plus the octile distance to it.

Measured on a 600 m map, eight scattered move orders — which is what a player actually does, and what
the previous back-and-forth measurement had been quietly flattering:

| | before | after |
|---|---|---|
| cost per order | 160–515 ms | **0.8–14 ms** |
| region searches per order | 165–452 | **0–12** |
| route quality (mean / p99 / worst) | 1.0056 / 1.061 / 1.680 | **unchanged** |
| cells lost | 0 | **0** |

The searches that remain are in regions that genuinely hold congestion — which is precisely where the
fine check earns its cost. Route quality is not approximately preserved, it is identical: the closed
form computes what the search computed, without walking the frontier to get there.

> **Testing note.** The fidelity test runs on a 200 m map, not 1200 m, because it compares against
> the flat search it replaces and building that reference costs seconds at 1200 m. The 200 m map is
> 49 regions and 324 portals, which exercises every path the big map does; 1200 m behaviour is
> measured by `--scale` instead.

### Session 4 — Unit types

Per-type body radius, speed, turn model and carry capacity off `AgentDefaults`, rather than
re-littering literals — which `AgentDefaults`' own remarks record as a real bug source.

**The radius question is settled ahead of the session** — §3's radius classes. Two decompositions
serve any number of unit types, split at 0.715 m, and `PathService.Mesh` should key on that rung
rather than on the radius in centimetres, or each type retains a byte-identical copy of one of
them.

Landing it **activates two logged debts**: congestion delay does not scale with unit speed, which
goes live the moment speeds differ; and carts want the speed-scaled turn model (`ω = a/v`) that was
measured badly wrong for people and is correct for a loaded vehicle.

**Gate:** existing tests pass on the default type; new tests for a cart's turning circle and a
scout's speed differential.

**Done, 2026-08-19. Gate met, `--selftest` 50/50**, and the pen and gate benchmarks bit-identical
— 1.30x / 137.6 red / pile 17 and 1.78x / 335.0 / pile 20 / 33 dead stops — which is what "existing
tests pass on the default type" has to mean. Four new assertions: every unit type routes at its
class rather than its own radius, a wagon cannot come about like a person (6.4 s against 1.3), a
scout outpaces a villager in proportion to its speed, and warning does not shrink as bodies get
faster.

**Both logged debts came due, and one of them bit in a way the roadmap did not predict.**
Congestion delay now scales with the body's own speed — dimensionally unarguable, ambiguous on the
clock, and therefore on a dial with the sweep recorded rather than declared settled. And the
*velocity solve's* horizon turned out to have the same
disease as the flat neighbour distance did for large bodies: it is a distance, the argument for its
size is a reaction time, and two light cavalry closing at 7 m/s had **0.05 s** of warning against a
villager pair's 0.17. Scaled by the pair's speeds, clamped so it can only widen, and asserted.

**The elastic session** — smallest of the nine, and the natural place to absorb overflow, either
tail-ending Session 3 or heading Session 5.

### Session 5 — The jobs layer

*Assignment* (persistent commitment) / *activity* (what it is doing now) / *interrupt* (temporary,
never rewrites assignment), onto the `AgentCommand` and `AgentLocomotionState` seams. Do **not**
carry across `w.task`, which in the prototype is a `defineProperty` alias for `activity` kept for
legacy readers.

Testable before any economy exists, with trivial activities: walk here, wait, walk back, be
interrupted, resume. This is also where §7's "taking control is an interrupt, not a mode switch"
becomes real, and it is the layer an idle player and an AI neighbour both run.

**Gate:** a unit with a standing assignment survives an interrupt and resumes it; and **the
determinism self-test is extended to cover the new state**.

**Both halves met, and the second one differently than intended.** The rule had been owed something
for three sessions, and the reason was structural rather than anybody's forgetfulness: the check
compared five fields of a seventy-field `AgentState`, at the last tick of the run, and nothing above
the level of a body. Extending it fell on whoever added the sixty-first field — in a different commit
from the one that added it. **A rule whose cost is paid later than the change that incurs it is a
rule that will be owed something.** So the rule was replaced by a mechanism. `AgentStateSchema` walks
the struct to its primitive leaves by reflection and compiles a reader per leaf; the jobs layer's
eighteen new values were being compared before the jobs tests were written, without a line of the
determinism check being touched. A field of a type it cannot reduce is refused at construction, and
a new field of `SimulationWorld` fails a census by name with what to do about it. Both alarms were
verified by adding a field the way a future session would.

The three layers landed as `AgentJobs` on the body — plain data, no references, so a job saves with
its unit. An **activity** is one thing rather than several: *be at this place for this long*, which
covers walking there and standing there without a transition, so a unit shoved off its post mid-dwell
walks back and finishes the dwell. An **order** is marked as an interrupt in the command layer and
nowhere else, which is what stops the jobs layer interrupting itself, and it expires on a grace
countdown — so there is no manual mode to strand a unit in. `Assignment.None` is how a unit is
actually taken off work.

**One thing the design had not noticed: a workplace is not a spot.** The first run of the trace had
eight of forty-eight units permanently unable to get to work, all of them ones that lost the race to
a shared point. A looser tolerance is the wrong fix — it would make a job inside a wall look
reachable. The fix is to ask the world *why* the body stopped short: taken ground is worked from
wherever the crowd left room, ground nobody can stand on is waited out at one route query every five
seconds. Twelve hands now share one post, and a job inside a wall is still refused.

### Session 6 — Stock, catchments and hauling

Physical local storage; granary catchments as bounded fields; node-to-node hauling only
(granary ↔ farm ↔ trade post ↔ forward depot, never per-household); consumption by population type,
including the seasonal wood swing. Hauling assignment is **priced in seconds through the congestion
field**, which is what makes a jammed route pick a different hauler on its own. Assignment-level
graph, locomotion-level paths — the two must not be confused.

**The first milestone of the design rather than the engine** — autonomy time becomes measurable
here for the first time.

**Gate:** a settlement that produces, stores, hauls and consumes runs headless through a full year
with no counter drifting and no unit permanently stalled.

**Met, 2026-08-19, and "no counter drifting" is exact rather than tolerated.** Stock is counted in
whole units, so `seeded + produced − consumed − lost` must equal what is stored plus what is on
somebody's back, checked every tick. Over a two-year run — 324,000 ticks — it never moved, and the one
time it did it named the bug in a sentence. §17 is the record.

### Session 7 — The strategic layer and the soak harness

They are the same artefact. A soak test needs a world doing economically meaningful things for
hours, and the strategic layer is what supplies the activity; the strategic layer needs long
headless runs to be validated at all.

**Gate:** §4's acceptance test — **the AI strategic layer can play the player's settlement** without
special powers. Plus the early-career soak (10 years, ~300 agents, ~40 min) wired as a CI gate, and
the full-career soak as an overnight job.

### Session 8 — Combat, the bot, and the multiplier

Health, damage / rate / range, target acquisition and an attack activity — most of it landing on
seams that are declared but unproven: `AgentLocomotionState` already has Chase and Flee,
`ColliderRole.Damageable | Interactable` is assigned at spawn and read by nothing, and
`AgentStore.Despawn` has tombstones for death.

Build the **competent-combat bot alongside the auto layer, not after it**.

**Gate:** the micro multiplier measured, in numbers, against a target of **1.5-2.0**. Until that
number exists, the balance between the two modes of play is a hypothesis (§12).

### Session 9 — Trade, docks and seasonal markets

Last, because it depends on everything beneath it being real: physical stock, catchments, hauling,
and neighbours with their own surpluses to be out of phase with.

**Gate:** a standing trade route survives a season change and re-prices itself; and a settlement
drowning in one resource while short of another is smoothed without intervention — which is the
complaint the whole trade layer exists to answer.

### Held every session

1. **64/64 stays green**, or a threshold moves deliberately and is recorded with its old value.
   Was 53/53 through Session 4; Session 5 added eleven checks and rewrote three.
2. **The determinism test grows with each system — by construction, not by discipline.** It reads
   whatever `AgentState` declares, so a system that adds state is covered the moment it compiles.
   What a session still owes is a *scenario* that exercises the new state, because a fingerprint
   over a field nothing writes proves nothing about it, and an entry in `DeterminismCheck`'s ledger
   for any new state hung off `SimulationWorld` — which the census will demand by name.
3. **Serialization discipline** — stable ids over references. Fresh-start succession (§5) makes this
   core-loop rather than a save feature; it is cheap continuously and expensive retrofitted.
4. **Every new cost term is honest seconds with a matched fade.**
5. **Resolve only where the answer can change.** Five things were rewritten this way and every one
   paid: the congestion sweep, congestion storage, the navigation raster, the routing partition
   and the renderer. The habit it corrects is reaching for a distance cutoff or a fixed grid when
   the real question is *where does the information actually live*.

---

## 14. Where this stands, 2026-08-19

Measured, not asserted. Everything here is reproducible from the flags in `plan-rts.md` §5.

| | |
|---|---|
| suite | `--selftest` **73/73** (was 53/53 through Session 4) |
| determinism coverage | **102 values a body**, 184 a tick, 25,580 at a checkpoint — every one probed |
| catchment, measured | **12,320 m² on open ground**, effective radius 62.6 m against a nominal 66 — §15 |
| soak, early career | **1.44 ms a tick at 300 agents, 23x real time**; a year is 4 min, ten years 39 — §15 |
| interception | closing at **exactly the difference of speeds**; contact is a separate problem — §15 |
| body | **1.79 m/s**, accel 2.0, decel 3.0, turn 3.03 rad/s, compression **1.5x** |
| roster | **5 types, 1 body class** — §18; radius 0.37 / 0.55, routing 0.37 |
| world | **600 m** for the game; 30 m calibration world untouched and asserted |
| tick, 2,000 agents | **6.5 ms at 600 m** — measured back to back against the Session 4 build, which reads the same today; the 6.0 ms recorded there was a cooler machine, not a faster one |
| jobs phase, 2,000 agents | **0.005 ms** with nothing assigned, 0.03–0.07 ms with 48 units at work |
| move order, ridge map | **8–27 ms**, 3–4 searches |
| route quality vs the flat optimum | mean **1.0014**, p99 1.098, worst 1.187, **no cell lost** |
| resident at 1200 m | **159 MB** (was 177, and 315 before that), congestion **253 KB** |
| career save, 600 m | **7.4 MB in 13 ms**, loaded in 300 ms, bit-identical continuation — §16 |
| frame | 7.4 ms |
| tuning dials on the panel | **14**, plus **5 jobs dials** off it — reach, crowded reach, attempts, grace, retry |
| order grace, measured | **5.5 s** from standing free to back on the job; the walk back is separate and is a distance |
| catchment | **60 s at hauler pace, 66 m**; measured 62.6 m effective on open ground — §6, §15 |
| settlement | two years, drift **0**, exactly 270 grain and 120 wood eaten per person per year — §17 |
| resources | physical everywhere but consumption; a dead carrier drops a heap anyone may take — §6 |
| road | **1.45x**, raised from 1.10 so §6's ribbon claim is true — §17 |

### Debts this work is carrying

Named so the next reader does not have to rediscover them. Struck-through entries were closed and
are kept because the *reason* they closed is worth having.

1. **`walked/optimal` moved with the router** — pen 1.19 → 1.30, gate 1.62 → 1.78, and unchanged by
   everything since. Bodies walk further even though the field's *costs* are closer to optimal, most
   likely tiles seeded from a corner-graph field steering slightly wide near borders. Nothing fails
   on it. Still the first number to look at if crowds start reading as wandering.
2. **The congestion test is weak.** It compares a mean over 154,000 cells, and a 22 m jam barely
   moves that. The sharp version compares only the cells whose route passes through the jam.
3. **`PaceScale` was applied too broadly in Session 2.** *Half closed:* its input was wrong too and
   is fixed — it keys off `AgentDefaults.WorldPace` rather than off whatever the default unit
   happens to do, which stopped meaning anything at six speeds. The original question, whether each
   *user* of it should scale, is still unasked.
4. **33 dead stops at the one-cell gate.** Zero in the pen. Unmoved by any of this; the gate is the
   tighter case.
5. **Substrate stage 3 — dropping the fine grid — is still gated** on congestion and the steering
   gradient having a uniform-resolution home. `plan-rts.md` §8 has the argument.
6. **Dynamic rebuild granularity was deliberately deferred.** Placing a building re-rasterises; what
   it should do to the decomposition is undecided. Note this now costs **two** decompositions rather
   than one, which is still nothing (9.2 ms + 4.4 ms at 600 m) but is no longer one.
7. **Both congestion body terms are unmeasured at scale.** Width and speed both ship at 1 on dials.
   The arithmetic is sound and the single-geometry sweeps are thin — **Session 6 decides them**.
8. **`centroid-overshoot` moved 0.34 → 0.39 m** when arrival was taught to cross an order boundary.
   Inside tolerance and the only benchmark number that moved. Watch it if group arrivals read loose.
9. **A mixed group builds one flow field per distinct radius** — accepted as it stands, because the
   cost is bounded by the number of body classes rather than the number of units.
10. **The determinism census stops one layer below the world.** Every field of `SimulationWorld` and
    `MoveGroup` is classified, and each subsystem a carried field points at has a named surface the
    fingerprint reads it through — the congestion field through its live set and revision, the
    navigation raster cell by cell, the path pool through the routes bodies hold. Those surfaces are
    one-line arguments and they are reviewable, which is the most that can honestly be said for
    them. Walking every private array of the congestion field would be a second implementation of it
    living in a test. The boundary is where to look first if two runs ever disagree on something no
    field explains.
11. **A cohort assigned all at once stays synchronized.** The jobs trace shows a lane's throughput
    pulsing between 0 and 32 legs a minute rather than settling, because sixteen units given the
    same shuttle in the same tick arrive, dwell and leave together forever. Nothing fails on it and
    it is not a locomotion problem — the fix is staggering the assignment, which belongs to whatever
    hands work out in Session 6.
12. **`PlaceCrowdShare` and `CrowdedAttempts` are argued from one scenario.** Eight radii and two
    attempts, and both were chosen against a villager on open ground. The crowded reach is what
    decides how wide a workplace effectively is, and Session 6 gives workplaces real extents — at
    which point the reach should probably come from the place rather than from the body.

### What Sessions 5 to 9 now rest on

Sessions 1 to 4 are done, and the substrate arc that grew out of Session 3 was not on the roadmap at
all. Both things that had moved underneath the remaining sessions have been acted on rather than
left to be discovered mid-session.

- **The catchment is re-derived** — §6, 60 s at hauler pace, **66 m**, against a nominal 135. The
  old figure gave 1.10 catchments per territory: one granary covering everything a player holds, so
  "expand past it and you need a second granary plus a hauling link" never fired. That was not a
  value wanting tuning, it was a mechanic that had quietly stopped existing. It only became
  derivable once the hauler had a speed, which is why it waited for Session 4 rather than for an
  opinion.
- **Soak testing is affordable** — 6.0 ms a tick and about 5.5x real time, against §10's estimate of
  9 ms and 3.7x. The early-career CI gate can be wired whenever Session 7 builds the harness.

**Session 5 is done and Session 6, stock and catchments and hauling, is next.** The jobs layer is in
and its gate is met both ways: a standing assignment survives an interrupt and resumes it, and the
determinism check grew to cover the new state without being edited, because it now reads whatever the
struct declares. The rule that had been owed something for three sessions is no longer a rule anybody
can forget — it is a mechanism that fails by name in the session that breaks it.

**What Session 6 inherits, beyond the catchment number and the two congestion dials.**

- **An activity is already the right shape for work.** *Be at this place for this long* is what a
  hauling leg, a harvest and a repair all are; attaching a rate and a cargo is the change, not
  attaching a state machine. `Assignment.Shuttle` is hauling with the cargo left out, and its two
  points become node ids.
- **`--jobs` is the harness the congestion terms want.** It already runs two lanes at the two lengths
  the design predicts — the 66 m catchment and a 216 m haul through the ridge's single pass — with
  every unit on that second lane using the same gap in both directions, continuously. That is debt 7's
  workload. It wants haulers of differing sizes added to it and then it *is* the run that settles
  width and speed.
- **A workplace is not a spot, and the layer now knows the difference.** Ground that is taken is
  worked from wherever the crowd left room; ground nobody can stand on is waited out. Session 6's
  farms and granaries should carry their own extents, at which point debt 12 comes due.

**Three things to carry into it.**

1. **Radius belongs to the class, not the type.** A unit type names `BodyClass.Foot` or
   `BodyClass.Heavy` and takes its navigation radius from that, which is what keeps six types on two
   decompositions. A job that wants a new unit picks a class; it does not pick a number.
2. **Everything about a body is written in bodies.** Four separate bugs in Session 4 were a distance
   written as a flat number that had been tuned against a 0.37 m body walking at 1.79 m/s: the
   neighbour horizon twice, the crowded-arrival tolerance, and the free-turn speed. Session 5 held
   it — a job's reach is three radii and its crowded reach is eight, so a wagon works a post from
   2.70 m and a villager from 1.11, both asserted. Any threshold Session 6 adds about how near, how
   far or how long goes the same way.
3. **Do not tune the congestion body terms on a scenario.** Width and speed both ship at 1 on dials
   and both are argued from arithmetic and one thin sweep. **Session 6 is the run that settles
   them** — many haulers of differing sizes sharing routes continuously is the workload they exist
   for, and `--jobs` is now most of that run.
4. **"Busy" is not a locomotion state, it is a destination.** The jobs layer's first bug was reading
   `LocomotionState == Move` as being under orders. A body in `Move` with no destination has stopped —
   arrived, or never given a route at all — and a failed order leaves exactly that state, so a unit
   whose workplace was unreachable stood in place forever having asked for a route twice. Anything
   that waits for a unit to be free should ask whether it has somewhere to be, not what it was told
   to do.

**Session 6, stock and catchments and hauling, is the first milestone of the design rather than the
engine**, and the first place autonomy time becomes measurable. It inherits the catchment number
above and the two dials; its own gate — a settlement that produces, stores, hauls and consumes for a
full year with no counter drifting and no unit permanently stalled — is also the soak that decides
whether the congestion terms are right.

**The instrument for the radius argument is `--radiisweep`**, and it is worth re-running the moment
terrain generation is real: if the clearance rungs are still 0.250 / 0.750 / 1.250 then everything
in §3 holds unchanged, and if they are not, something has put an obstacle off the half-metre lattice
and §3's two classes want re-deriving before anything else is built on them.

---

## 15. What Sessions 6 to 9 owe — audited 2026-08-19

Made by checking the code and measuring, not by re-reading this document. Two new instruments came
out of it: `--catchment`, which measures what a catchment actually covers, and a self-test that
measures whether a fast body runs down a slow one.

### Verified, so the next session does not have to

**1. The catchment query already exists, and it is congestion-aware.** `PathService`'s cost-to-goal
field is denominated in seconds and already charges congestion; `TryOptimalTravelTime` reads it, and
`SimulationWorld.TryTravelSeconds` now exposes it point to point. So §6's claim that *"the economy
independently wants the structure the router needs"* is true in code and not only in prose: a
catchment is one field whose goal is the granary, and every consumer inside it is a comparison
against a number. **The bound is an optimisation, not a requirement** — the field is filled lazily,
so an unbounded field costs what is asked of it.

**2. The units trap, and it would switch the mechanic off for the second time.** Route seconds are at
the router's **reference pace of 1.79 m/s**, not at the asking body's — one field can serve every
unit precisely because a body's own speed scales every leg equally. So *60 s at hauler pace* is
**36.9 route-seconds**, not 60. Comparing the budget directly against field cost gives a 107 m
catchment instead of 66, which is 2.4 catchments per territory: back inside the range where the
second granary is never forced. The re-derivation in §6 fixed that mechanic once by correcting an
opinion; this would break it again by a units error. `CatchmentScenarios` does the conversion in one
place with the reason written next to it.

**3. A catchment is not round, and the number the mechanic rests on is an area.** §6 derives 66 m and
then converts it to 6.58 catchments per territory by treating it as a disc. Measured on the 600 m game
map, at a hauler's radius, `--catchment`:

| placement | area | effective radius | far | near | per territory |
|---|---|---|---|---|---|
| nominal disc *(what §6 assumed)* | 13,685 m² | 66.0 m | — | — | **6.58** |
| open ground | 12,320 m² | 62.6 m | 66 m | 60 m | **7.31** |
| on the road | 5,101 m² | 40.3 m | 72 m | 8 m | 17.64 |
| inside the pass | 8,979 m² | 53.5 m | 72 m | 17 m | 10.02 |
| by the lake shore | 3,327 m² | 32.5 m | 50 m | 3 m | 27.05 |
| beside the ridge | 2,261 m² | 26.8 m | 51 m | 6 m | 39.81 |

Open ground is **10% short of the disc** — close enough that §6's derivation is sound, and the
direction is safe: the mechanic is slightly stronger than designed, not weaker. Near terrain it is
far stronger, 10 to 40 catchments per territory. **The consequence for Session 6 is that anything
tuned against 6.58 is tuned against the best case.** Storage capacities, hauling costs and the
founding cost anchor (§5: three years of a mature settlement's surplus) should be tuned against a
measured catchment on the ground the settlement is actually on, and the trace should report it.

**4. The road-ribbon claim is quantitatively false at the current multiplier.** §6 says *"roads
literally grow usable territory, and settlements form ribbons along them — nobody has to author
that."* Measured: a catchment reaches **72 m along the road against 66 m on open ground — 1.09x**,
against a road speed multiplier of 1.10x. That is exact, and it is the ceiling: a road can grow a
catchment along it by precisely its speed multiplier and no more, because the catchment *is* a time
budget. **9% is not a ribbon.** The wide stretch factors in the table above (up to 16.7x) are terrain
*refusing* ground, not a road *granting* it — "stops at a ridge" holds emphatically, "stretches along
a road" does not.

This is a decision, not a bug, and it belongs to Session 6 because settlement layout is what §6 says
the mechanic is for. Either the road multiplier moves — and `PathCost` follows it by derivation, so
routing prefers roads more strongly at the same time, which is probably wanted — or the claim comes
out of §6. A useful anchor for the first option: reach along a road *is* the multiplier, so a
noticeable ribbon wants something in the 1.4–1.5x region, which is a maintained road against rough
ground rather than a highway.

**5. The soak arithmetic holds, but the CI gate is a year and not a decade.** Measured at
early-career scale: **300 agents on 600 m, 1.44 ms a tick, 23x real time** — §10 estimated 24x. A
year is 5,400 sim seconds (§3), so:

| run | wall clock | what it covers |
|---|---|---|
| **one year** | **~4 min** | §8's stress points 1–3: a quiet season, harvest, winter |
| ten years | ~39 min | the early-career climb |
| full career | ~6–8 h | saturation |

§10 calls the ten-year run "a CI gate". Forty minutes is a nightly job. **The per-PR gate is one
year**, it is four minutes, and it already covers three of the five stress points — which is most of
what §8 says the number means.

**6. Interception emerges exactly, and closing is not catching.** §7 derives the raider's window from
a response time and asserts that *"loaded raiders are slow, so interception is emergent rather than
scripted"*. Nothing had tested it: chase and flee were only ever pointed at a body that could not
move. Measured over open field, closing rate against the difference of the two speeds:

| pursuit | legs | measured | closest approach |
|---|---|---|---|
| light cavalry after a villager | +1.71 m/s | **+1.71** | 0.8 m |
| light cavalry after a wagon | +2.40 m/s | +1.99 | 1.4 m |
| a villager after light cavalry | −1.71 m/s | **−1.71** | 1.0 m |

So the window derivations in §7 rest on numbers now. **But the last metre is not delivered by the
rate.** An earlier form of the measurement waited for contact instead: light cavalry 14 m behind a
villager, closing at 1.71 m/s — eight seconds of arithmetic — first came within 1.34 m after **100
seconds**. It ends in a circling stalemate at about contact distance, because pure pursuit aims where
the quarry *is*, the quarry turns, and both bodies are correctly avoiding each other throughout.
Closest approach is 0.8 m against 0.74 m of combined radii, so the geometry is not the obstacle — the
pursuit curve is. §7 says combat resolves by physical contact and never by abstract resolution, so
**Session 8 cannot assume a chase delivers contact.** It needs an attack activity that commits: lead
the quarry rather than aim at it, and let a body and its declared target ignore each other in the
velocity solve, exactly as a mover and a settled ally already do — symmetrically, because the
one-directional version of that exclusion drove idle units metres down a corridor when it was tried
for the crowd case.

### Owed at the head of Session 6

1. ~~**Serialization, which has been a discipline for five sessions and has never once run.**~~
   **Done, 2026-08-19, before Session 6 started.** `Simulation/Persistence` saves a world and loads
   it back, and the acceptance test is the fingerprint in two parts: identical on loading, and
   identical again after two hundred ticks of both worlds stepped together. **The second part is what
   found things, and it found three.** See §16.
2. **A calendar, before consumption rather than with it.** §3 has the table — year 5,400 s, four
   seasons, a day unit of 20 s — and §6's consumption is seasonal. Seasons are canonical and crop
   windows derive from them; the prototype's three disagreeing windows are the warning, and they
   drifted because nothing forced them to agree.
3. **Nodes with stock.** Granary, farm, trade post, forward depot. This is the session's real new
   state and it will trip the determinism census by name, which is the census working.
4. **Autonomy time, in Session 6 rather than 7.** It is computable the moment stock and consumption
   exist, it is the HUD's one verb, it is the score (§8), and it is the soak's monotonicity
   assertion. Building it late means the economy lands with no way to read whether it is any good.
5. **Conservation assertions written with the stock, not after it.** Produced = consumed + stored + in
   transit, every tick. "No counter drifts" is one of §10's four soak assertions and it is cheap
   while there is one commodity and expensive once there are four.
6. **Debt 11 — stagger assignments.** Sixteen haulers given the same shuttle in one tick pulse
   forever; the jobs trace shows a lane's throughput swinging between 0 and 32 legs a minute. Session
   6 puts many more units on shared lanes.
7. **Debt 7 — mixed body sizes on the shared lane in `--jobs`.** The trace is most of the run that
   settles the congestion width and speed dials; what it lacks is haulers of differing sizes.

### Owed before Session 7 and 8

Session 7's strategic layer and soak harness need the economy above and little else — the jobs layer
is the layer an idle player and an AI neighbour both run, and it exists. Two things: use the one-year
gate from point 5, and settle §12's *"are trade partners AI-run settlements or abstract markets"*
here rather than in Session 9, because it decides whether prices emerge from neighbours' actual
stores, and the neighbours are what Session 7 builds.

Session 8 owes the attack activity from point 6 above, and it inherits two seams that are declared and
unproven: `ColliderRole.Damageable | Interactable` is assigned at spawn and read by nothing, and
`AgentLocomotionState.Chase` and `Flee` are now measured against a *moving* body for the first time.
The micro multiplier remains §12's highest-value measurement and nothing is owed before it except
combat itself.

### Where the first playable slice is

The roadmap runs 6 → 7 → 8, which puts the *threat* third and means nothing is playable until Session
8. That is one session later than it needs to be, and the reason is in §2: the thesis is that labour
and attention are substitutes, and **that cannot be felt without something that punishes
inattention.** An economy with no threat is a spreadsheet; combat with no economy is a skirmish
sandbox. The two halves of the thesis are the two halves of the slice.

**The MVP is Session 6 plus a thin slice of 8 — call it 6.5 — and it is one session and a bit from
here, not three.** Concretely:

- calendar, one commodity (grain), one farm, one granary, haulers on shuttle assignments, seasonal
  consumption, autonomy time on the HUD;
- a raiding party that walks in, takes stock, and walks out slower than it came;
- nothing else. No trade, no strategic layer, no tech, no construction beyond placing the granary.

That is playable in the only sense that matters for this design: **you can leave, and come back to
find out whether leaving was affordable.** Everything in §8's stress-point ladder is legible against
it from the first run.

**Recommended order: 6 → 6.5 (the slice) → 7 → 8 → 9.** Session 7's harness then has a complete loop
to soak rather than half of one, and Session 8's bot has a real economy to attack rather than a
sandbox.

**The argument against, kept because it is real.** §13 pairs the strategic layer and the soak harness
deliberately — "they are the same artefact" — and pulling a combat slice forward means the soak lands
a session later, so an economic regression could go a session unnoticed. The mitigation is cheap and
is point 5 above: the one-year gate is four minutes and can land with Session 6, well before the
harness proper.

---

## 16. What serialization cost, and the three things it found

Landed 2026-08-19 at the head of Session 6, before any economy, because rule 3's argument — cheap
continuously, expensive retrofitted — had been made five times and acted on zero. §5 makes this core
loop rather than a save feature: a career ends, usually violently, and the next begins on the same map,
which has not reset.

**What it cost.** A `Write`/`Read` pair on each component that carries state, next to the state, plus a
header and an orchestrator. A body's state needs no per-field code at all: `AgentState` is plain data
all the way down, so the whole array is one memory copy, and **a field added to a body is saved the day
it is declared** — the same property that made the determinism fingerprint automatic, enforced this time
by the compiler, because `Blob<T>` constrains `T` to `unmanaged` and putting a reference on a body stops
the build.

| | |
|---|---|
| save, 48-unit settlement on 600 m | **7.4 MB in 13 ms** |
| load | **300 ms**, most of it rebuilding the raster |
| save, 18 bodies on the 30 m test world | 83 KB |

The bulk is ground, not units: one height per vertex and one surface per cell of the whole map, whether
anything has happened on them or not. Narrowing `TerrainSurface` to a byte — five values did not need
32 bits — took **4.2 MB off every save and 18 MB off resident memory at 1200 m** (177 → 159 MB), which
is the same habit as everything else here: hold the information at the resolution that decides
something. What remains is honest and uncompressed; a deflate stream would take it to a few hundred KB
whenever save frequency makes that worth the format change.

### 1. A save manifest is a superset of what the fingerprint reads

The ledger in `DeterminismCheck` was assumed to *be* the manifest. It is not, and the reason is worth
stating precisely: **the fingerprint's job is to detect a divergence, a save's job is to reproduce a
future, and the second is strictly harder.** The ledger's boundary arguments are sound for the first and
insufficient for the second. Two of them:

- **The congestion field's running totals.** The ledger says they can only reach a decision through the
  published revision, which is true. But they are what decides *when the next revision publishes*, so a
  world loaded with zeroed accumulators publishes at a different tick and every route adopted after that
  is a different route.
- **The path pool's free list.** The ledger says a divergence in it can only matter by handing out a
  different handle, and handles live on bodies, which are read. Also true. But the first route planned
  after a load takes a different handle, and the two worlds part company on the next tick.

Both were left out of the first version and both were caught by the tick-forward half of the test.

### 2. Work counters describe the process, not the world

Counters of routes planned and fields built are read by the fingerprint on purpose: identical code must
do identical work, and a counter is the earliest place a diverged *decision* shows up. But a loaded
world resumes with a cold cache and has to redo work the original had already done, so it legitimately
disagrees. Saving the counters to paper over it — which was tried — only moved the disagreement to the
first tick, because the loaded world then went on to build five more fields than the world it came from.

So the comparison across a save excludes them, deliberately and by a named flag rather than by a
tolerance. The distinction it draws is real: those counters are a property of a *process*, and a save
crosses processes.

### 3. A cost field remembers the order it was asked

The one that took the longest to see. After the first two fixes the loaded world still diverged, by
**one unit in the last place of a body's facing, one tick after loading.** The cause is that a flow
field is built once and then *refined tile by tile as things ask about it*, so what it holds depends on
the order the questions arrived in — and that order is not state anybody could write down.

This is a limitation, not a bug, and the honest claim is narrower than "a save reproduces the world":

> **Two worlds agree once both have forgotten what they had been asked.**

Every peer loading the same save forgets equally, so lockstep survives one; a single career continuing
from a save is a new lineage regardless. `SimulationWorld.DropRouteCaches` exists so the test can
compare on equal terms and says so. With it, the continuation is bit-identical over two hundred ticks
of a world mid-everything — bodies walking, a group order in transit, standing assignments, an
interrupt in flight, congestion on the ground, built obstacles, a tombstone, and an order still sitting
in the queue unapplied.

### What keeps it honest from here

Three census-style alarms, all of the same shape as the determinism ledger's — they fail by name, in the
session that breaks them.

1. **The round trip itself.** State left out of the save shows up as a divergence a few ticks later,
   named down to the field. This is what replaces remembering to extend the save.
2. **A layout signature in the header**, derived from the determinism schema rather than
   hand-maintained, so it moves when `AgentState` does. Bodies are raw bytes; a save from a build with
   a different body shape would otherwise load as plausible garbage — units standing at coordinates
   read out of the middle of somebody's stall timer.
3. **An order-kind census.** A command kind with no save format would be silently dropped: a unit told
   to do something that never does it, once, after a load. The test reflects over the command hierarchy
   and requires every concrete kind to be accounted for.

---

## 17. Session 6 — the economy, and what an exact ledger caught

Landed 2026-08-19. Two years of a settlement run headless in six seconds, and the seasonal story §6
describes is visible in the trace rather than argued for.

| | year 1 spring | summer | harvest | winter | year 2 spring |
|---|---|---|---|---|---|
| grain stored | 2,000 | 440 | 665 | **5,034** | 3,356 |
| wood stored | 1,000 | 992 | **2,408** | 2,357 | 1,348 |
| grain lasts | 1.1 seasons | growing | growing | 2.9 seasons | 1.9 seasons |

Food bottoms just before the harvest and peaks after it; wood peaks in autumn and drains through
winter at three times the rate. Nobody wrote that sequence down — the seasons are canonical and every
rate is a normalised function of them, so the calendar produces the calendar. Over two years the
settlement ate **exactly** 270 grain and 120 wood per person per year, which are the nominal figures,
and went short zero times. Stores rose year over year, which is §10's soak assertion — autonomy time
climbing with the arrangement — observed for the first time.

### The decisions worth knowing about

**Stock is integers, and that is the load-bearing choice.** The gate says "no counter drifting", and a
float ledger accumulated over 162,000 ticks can only ever be checked against a tolerance somebody
picked. In whole units it is exact arithmetic. Rates stay continuous, as rule 1 demands — a node
accumulates fractional production and spills whole units — so nothing about the design gave anything
up for it.

**Hands are counted from the jobs layer, not from a parallel notion of employment.** A body posted at a
node under a `Hold` assignment, standing in its yard, is a pair of hands there; one walking toward it is
not yet, and one dragged away by an order is not any more. So §2's identity — labour and attention are
substitutes — is not modelled, it is what happens: pull a farmer away and the farm's output stops for
exactly as long as you keep them.

**A haul is one round trip, not a standing route.** Collect, deliver, go back on the board. A hauler
that kept a route for life would have been priced once, at the moment it was hired, and the promise
that a jammed lane makes a different hauler cheaper would be a promise about a decision nobody ever
revisits.

**Lean staffing wins, and the layout shows it.** Output has diminishing returns in hands at one place,
so twelve farms of one hand out-produce six of two by forty per cent for the same labour. The
settlement in the trace is twelve farms, seven woodcutters, five carts and two wagons, feeding 26.

**The calendar is derived from the tick number**, so it is not state and there is nothing to keep in
step. `EpochTicks` was added for succession — a career beginning in whatever year the map has reached —
and it turned out to earn its keep immediately: the economy self-test starts in a harvest rather than
spending three seasons simulating its way to one.

### What the exact ledger caught

**Seventeen grain vanished at tick 116,520.** `JobSystem.Assign` reset the whole jobs struct, cargo
included, so a wagon that arrived at a full granary had its remaining load destroyed when the finished
job was cleared. Cargo is a thing in the world and not a note about intent; `Assign` now preserves it,
and a delivery that arrives to a full store is re-pointed at whatever else has room rather than ending.
A tolerance-based check would have called seventeen units noise.

It also forced a third ledger. A body that dies carrying units has removed them from the economy, so
`Lost` is part of the identity rather than an afterthought — and Session 8 makes that the ordinary case,
because a raider killed on the way home is the defender's whole objective.

**One wagon starved the entire hauling network.** The board would not send a cart for a load smaller
than a share of *the largest cart in the world*, so adding a single 200-unit wagon to a settlement of
40-unit carts raised the bar to seventy units, no hundred-and-fifty-unit yard reached it in time, and
journeys fell from 342 a year to 163 while the settlement starved. The bar is the *smallest* cart:
a load worth the smallest cart's trip is worth dispatching, and which cart goes is the board's decision,
made in seconds.

**Two carts were being sent for the same grain.** Half of all haul jobs were being abandoned on arrival
at an emptied yard. A yard already being collected from is no longer offered to a second cart, which is
cheaper than reserving units and has the same effect: abandoned jobs went from 160 in half a year to 14
in a whole one.

### The road multiplier, decided

§15 measured that a catchment reaches along a road by **exactly** the road's speed multiplier — 72 m
against 66 m on open ground, at a multiplier of 1.10 — so §6's claim that "roads literally grow usable
territory, and settlements form ribbons along them" was false by construction. **Road is now 1.45**,
a maintained road against grass rather than a highway. Re-measured: reach along the road **82 m**, and
the catchment's area grows **36%**, from 5,101 m² to 6,925. It follows through the whole system for
free because path cost is derived from speed — routes prefer roads more strongly and hauling legs along
them are cheaper in the same seconds the board prices in.

### The correction that arrived after the gate

Session 6 shipped consumption as a draw against *bodies* bound to granaries, and a dead carrier's load
booked to a `Lost` ledger. Both were wrong, and §6 above now records why. What landed instead:

- **Heaps.** A body destroyed carrying leaves its load where it fell, merged into a heap already there
  if one is within 2.5 m. Heaps are collected by the same board that collects a full farmyard, belong to
  nobody, and are swept away when emptied. `Lost` is gone and the conservation identity is shorter.
- **Houses.** Consumption moved from bodies to **clocked sinks**, and the catchment binding moved with
  it — from every body every eight seconds to every house whenever the set of stores changes. Warm-up on
  the live map fell from 31 ms in the economy phase to 17, and it settles to **0.48 ms by tick 78**.
- **Unhoused is a signal.** A body with no household draws nothing, so a settlement with stores rising
  and people unhoused is §6's blocked sink — *"grain surplus rising, population capped by housing"* —
  reported rather than deduced.
- **Loads and heaps are drawn.** A loaded cart carries a visible block the colour of what is on it, and
  a heap is a low spread pile of the same colour. §7's return trip needs a player to be able to see
  which cart is worth intercepting.

The test is §7's interception in miniature, with the combat left out because none exists yet: load a
cart, destroy it mid-journey, and require the grain to be on the ground *at the position it fell*, to be
collected by another cart, to reach the granary, and for not one unit to have gone missing at any point.

### Buildings became solid, and three things fell out of it

Nodes were built with an `Interactable` collider and were never put in the **placement grid**, so bodies
walked through farms and the router did not know they existed. The reasoning at the time — "a granary a
hauler cannot walk up to is a granary nobody can use" — stopped being true the moment touching counted
as arrival. Both the navigation raster and the static side of the velocity solve are derived from the
placement grid, so occupying the cells buys routing *and* steering with no second description of the
same wall to drift.

**A building is exactly one placement cell.** The first attempt gave each kind its own radius and blocked
every cell the circle touched, and the quantisation bit at once: a 1.7 m granary blocked a plus-shape
4.5 m across, so the ring a cart was told to stand on was *inside the wall*. Every hand stalled and
nothing was delivered. One cell removes the class of problem, and the node snaps to that cell's centre so
the wall, the drawing and the arrival tolerance are the same square. A granary the size of a house is a
greybox simplification; real sizes arrive with real art as a cell count.

**Two predicates that disagree, and using the wrong one cost most of a session.**
`IsPositionNavigable` asks whether a body, as a circle, overlaps anything — continuous, against terrain
and placement boxes. `IsWalkable` asks whether the raster's clearance *at a cell centre* admits a body of
some radius, quantised to the clearance rungs, and that is what every route search actually consults.
They disagree most for the widest bodies: a 0.90 m wagon can stand half a metre from a wall in a cell
whose clearance is 0.75 and is therefore unroutable at 0.90. Choosing an approach point with the body
test and handing it to the router produced a wagon that asked for a legal position 228 times, was refused
every time, and sat in the movement layer's limbo state — `Move` with no destination — while a cost field
priced the same journey at 89 s. `plan-rts.md` already warns that cell clearance cannot bound where a
body is; the converse is as true and this is where it bites.

**A silent argument that was fatal.** A post given at a farm without the farm's extent gets a tolerance
in body radii — 1.11 m for a villager — while the wall reaches 1.12 m. One centimetre short, forever,
and nothing about the symptom points at the missing argument. The world fills it in now: any assignment
whose place coincides with a node adopts that node's position and footprint, so every path into an
assignment gets it right, including the ones written before nodes existed.

### The pen benchmark moved, and it moved the right way

Recorded because rule 1 requires it. Fixing the catchment query — a route priced from inside a building
now prices from the ground beside it — also fixed a latent inaccuracy in the simulation:
`RemainingRouteDistance` uses the same query for a body on flow transit, and when the body's own cell was
unwalkable it had been falling back to a **straight line through the wall**. That underestimates the
distance, so progress looked worse than it was and the crowd reacted to stalls that were not happening.

| | was | now |
|---|---|---|
| red agent-seconds | 137.6 | **119.2** |
| mean pile | 5.8 over 16.8 s | **5.1 over 15.9 s** |
| congestion reroutes | 1 | **0** |
| turns over 60° | 0.30% | **0.23%** |
| `walked/optimal` | 1.30 | **1.33** |

Every stall figure improved. `walked/optimal` rose and **is not comparable to the old number**: the ratio
is only accumulated for bodies that have a measurable optimum, and bodies whose start cell was unwalkable
previously had none. The metric now includes the ones starting in tight spots, which are exactly the ones
that walk furthest relative to the straight line. 1.33 over a wider population, not 1.33 against 1.30.

One-cell gate is unmoved at 1.78 with 33 dead stops.

### Debt 7 is still open, and now for a better reason

The congestion width and speed terms were to be settled by "many haulers of differing sizes sharing
routes continuously", and the settlement has exactly that — five carts at 0.55 m and two wagons at 0.90
sharing one granary approach. **Congestion peak over two years: 0 to 2.** A healthy settlement does not
jam. The terms need a deliberately over-subscribed lane rather than a working one, which is a different
scenario and an honest thing to have learned: the workload that was supposed to settle them is the
workload in which they never fire.

**And then a second reason, worse than the first.** The wagons had to come out of the settlement
altogether: **a Heavy-class body cannot be routed to a point beside a 1.5 m building on this map.** Two of
them accumulated a thousand refusals over a year while a cost field priced the same journey at 89 s, so
the route exists and the hierarchical search will not find it; remove the wagons and the stalls go to zero
with nothing else changed. That is a routing question and it belongs with **debt 6** — placing a building
re-rasterises, and what it should do to the decomposition is still undecided. Twenty scattered 1.5 m
buildings is a case `--routingtest`'s staggered walls do not cover, and its `lost` column — cells the
hierarchy cannot price — is the number to go and look at. **Until it is fixed, no wide body can work a
building**, which also blocks the wagon from ever hauling and is a bigger hole than debt 7.

| | |
|---|---|
| suite | `--selftest` **72/72** |
| two-year settlement | 324,000 ticks, drift **0**, short **0**, **0 unhoused**, on 7 carts and no wagons |
| pen escape | **1.33x** over a wider population (was 1.30 over a narrower one), red 119.2 agent-s (was 137.6) |
| economy phase | **0.01–0.03 ms** a tick headless; on the live 600 m map it warms up at 17 ms and settles to **0.48 ms by tick 78** |
| grain produced against nominal | **99.7%** — the harvest crunch nearly absorbed at seven haulers |
| new instrument | `--settlement [--years n]`, which is also §15's per-PR soak gate |

---

## 18. The second body class is retired, and buildings got their scale back

Three of these came from watching it run rather than from reading anything, which is the argument for
having a window at all.

### The Heavy class is gone

§3 derived a second body class at 0.90 m from the raster's clearance ladder — a body needing a 3 m gate
where a villager passes a 1.5 m clearing — and the derivation was correct. **What retired it was the
game.** Once buildings occupied the ground they stand on, a 0.90 m body could not be routed to a point
beside one at all: the raster's clearance one cell out from a wall is 0.75, which is below 0.90. Two
wagons accumulated a thousand refusals over a simulated year while a cost field priced the same journey
at 89 seconds. A hauler that cannot approach a granary is not a hauler, and a class every building
refuses is not a class.

| | was | now |
|---|---|---|
| body classes | 2 | **1** |
| decompositions | 2 meshes | **1** |
| roster radii | 0.37 / 0.55 / 0.90 | 0.37 / 0.55 |
| routing radius | per class | **0.37 for everything** |

The wagon is gone. Heavy cavalry stays and keeps the more interesting half of what made it heavy — a
2.6 m turning circle, so it cannot come about the way a person can — and that costs the router nothing.
`AgentDefaults.RoutingRadius` is deliberately its own name rather than an alias for `Radius`: they are
equal and they mean different things, and a second class, if a finer navigation cell ever makes one
affordable, changes the second and not the first.

**What that cost in coverage, and what it did not.** One self-test genuinely lost its subject — *"a heavy
body takes the gate a villager can skip"* asserted §3 end to end and there is no wide body to assert it
with. It is retired in place, with a comment where it stood, and the two-gap wall it was built on is
still there for whoever re-derives a second class. The others kept a subject and were re-based on the
cart rather than deleted: mixed-size avoidance, mixed crowds through a gate, and congestion priced by
width all work at 0.37 against 0.55. *"Body radii inside one rung share a decomposition"* became
*"every roster radius is inside one clearance rung"*, which is what is now load-bearing.

### Buildings are the size buildings are

A building occupied exactly one placement cell — 1.5 m, a garden shed standing next to a 1.45 m person.
That was not a design decision; it was **forced** by the class above. While a 0.90 m body existed,
anything larger left no cell beside it whose clearance that body could use, so the ring a cart was told
to stand on fell inside the wall. Retiring the class removed the constraint.

Footprints are now an **odd** number of placement cells, so a building centres on a cell and is symmetric
about its own position: five for a granary (7.5 m), three for everything else (4.5 m). The grid quantises
to 1.5 m and there is no point pretending to finer control than the thing being described. The collider,
the drawing and the arrival tolerance all read the same table, so what is drawn is the wall.

### Arrival is measured to the wall, and the wall of your own workplace is invisible to you

Two things followed from bigger buildings, and both were visible immediately.

**A circle round a square is a poor description of a square.** The arrival tolerance used the
half-diagonal, which is the safe figure for a circular test and means a body approaching a *face* stops
2.3 m short of a granary. It reads as hesitation. Arrival now measures the distance to the **box**, so a
body approaching a face stops at the face.

**A body does not need to be talked out of walking into the thing it is trying to reach.** Static
avoidance slowed bodies to a crawl for the last few metres of every approach, and the geometry it was
being careful about was the destination. The walls of a body's *own* workplace now contribute no
avoidance lines to it — only its own, only while it is working, never while interrupted — so it
approaches at pace and is stopped by contact, which is what depenetration is for. The same exclusion,
symmetric, is what a mover and a settled ally already do to each other, and it is what an attack activity
will need against the thing it is attacking.

### They still stood too far off, and it was three places measuring the same thing differently

Arrival was measured to the wall, which was right, but two other places were still measuring to a circle
drawn round the building — and a third to its centre. All three had to agree.

**The walk target.** It was placed at `circumscribing radius + body + slack` from the centre, which for a
granary is 2.23 m past the face while arrival wants 0.80 m from it. So a body walked to a point 1.4 m
outside its own arrival tolerance, failed to arrive, retried twice, gave up, and **settled where it was
standing** — which is the whole of the symptom. The target is now measured out from the wall along the
approach bearing: the half-width along a face, the half-diagonal into a corner, and the right answer
everywhere between, so it lands 0.68 m off the building whichever way the body came.

**The crowd fallback.** When the ground against a wall is taken, a body may settle short — and the figure
for that was eight radii, 4.4 m for a cart, inherited from the bare-point case where a crowd has to
arrange itself around a *point*. A building has a whole wall to line up along, so it is three radii now:
1.1 m for a villager, 1.65 for a cart, which is a second rank behind the first and no more.

**Counting hands.** A body was a pair of hands at a node if it was within a flat 4 m of the node's
*centre*, which is a figure that necessarily depends on how big the building is. At 4.5 m across, a hand
standing at the corner of its own woodcutter is 4.8 m from the middle of it and did not count: the
settlement quietly lost two of nineteen pairs of hands and a seventh of its wood. It is measured to the
wall now, at the same three radii the jobs layer settles a crowd at — because a hand *is* a body the jobs
layer considers to be at the node, and two definitions of that would drift apart.

| | |
|---|---|
| walk target | **0.68 m** off the wall (was 2.23 m off a granary's face) |
| arrival | 0.80 m from the wall |
| settle-short fallback | **1.65 m** for a cart (was 4.40) |
| hands at work | **19 of 19** (was 17), wood back to 3,493 a year from 2,994 |

### They were still standing off, and it was not avoidance — it was giving up

Measured rather than reasoned about, after two rounds of computing what the gap *should* be:

```
gaps to the wall: median 0.97 m over 19 bodies, 19 settled short
```

**Every body in the settlement was failing to arrive.** The tolerance asked for 0.62 m, they could only
reach 0.97, so each one walked to its place, did not qualify, waited out a five-second retry, and then
accepted the position it was already standing in. Nothing failed and nothing was reported. What it looks
like on screen is a body slowing to a stop short of its work and hesitating before starting — which is
exactly what it was, and it was never avoidance. The **own-workplace hatch was firing 18.7 million times**;
that part was working.

The missing term is the router. A body is routed to a cell the raster says it may occupy, and clearance
beside a wall is quantised — the achievable rungs here are 0.25, 0.75 and 1.25 — so the nearest cell a
0.37 m body may stand in is a whole rung out from the wall, not a body's width out. **Demanding closer
than the router can deliver is a tolerance that can never be met.** With one navigation cell added to it:

| | before | after |
|---|---|---|
| median gap to the wall | 0.97 m | 0.97 m |
| bodies settling short | **19 of 19** | **0** |
| pause before starting work | ~5 s | none |

The gap did not move, because the gap was never the problem — the *verdict* on it was. What remains,
0.6 m of air between a body's edge and a wall, is the raster's floor at 0.5 m cells and is the honest
limit until `plan-rts.md` §8 revisits cell size.

### A building was being drawn twice

Visible the moment there was a screenshot, and not deducible from any number: every farm was a tray of
nine grey cubes on a coloured plate. The node drew itself as one solid box **and** every placement cell it
occupied drew itself as an obstacle block, because those blocks predate buildings and know only that the
cell is built on. Granaries and houses hid it by being tall enough to swallow their own cubes; farms and
woodcutters, which are usually near-empty and therefore short, did not.

Buildings draw once now, and a wall built by hand still draws per cell because that is what it is. A
building is also at least a person and a bit tall whatever is in it — fullness raises it rather than
deciding whether it reads as a building at all.

### And a footgun closed

Buildings going from 1.5 m to 7.5 m put carts that used to muster beside the granary *inside* it,
standing on ground with zero clearance, unable to route anywhere, for a whole simulated year. Every
caller that places a body relative to a building would have to be found and corrected — or it can be true
once: **a body spawned inside a building appears beside it instead.** Two self-tests opt out by name,
because expelling an embedded body is a feature and clearance is asserted by putting a body where its own
radius does not fit.

---

## 19. What the prototype actually does — read from `swarm_strategy_v10_0.html`, 2026-08-19

Read before designing the MVP slice, because this document has been quoting the prototype in passing for
several sessions — `w.task`, `totalSecured()`, the three disagreeing crop windows — without anywhere
recording what its **fundamental loop** is. It is richer than the passing references imply, and three of
its central mechanics are simply absent from what has been built.

### The loop, as the prototype defines it

**One currency: a person-second of work at a place.** Farming, logging, quarrying and building are all
literally `+= dt` while a body stands at a thing. Nothing produces at a rate on its own; a thing produces
because somebody is stood at it working, and `w.work` accumulates what each villager has done.

**The seasons have verbs, and they are the emotional shape of the year:**

| season | days | verb |
|---|---|---|
| Spring | 60 | **COMMIT** |
| Summer | 90 | **EXPLOIT** |
| Harvest | 50 | **SCRAMBLE** |
| Winter | 70 | **SURVIVE** |

**A farm is a three-phase labour cycle with deadlines, run twice a year.** This is the mechanism the
verbs describe and it is the thing most worth carrying across:

- **PREP** — 28 labour-seconds, and it sets the *ceiling*: `potential = prep / 28`. Miss it and the
  ceiling is gone for that cycle. Nothing you do later recovers it.
- **MAINTAIN** — 7 labour-seconds, and it only *retains*: `0.76 + 0.24 × (maint / 7)`. Cheap, and
  ignoring it costs a quarter of the crop.
- **HARVEST** — 24 labour-seconds, and it is you physically reaping what you grew. Yield is
  `14 × potential`, earned second by second. Food left unreaped is food you never had.

Two overlapping cycles, A and B, on their own 270-day windows (A prep 0–45, maint 45–90, harvest 90–135;
B 135–175, 175–215, 215–255; rest 255–270) — which is why §6 insists the crop windows be *derived* from
the seasons: in the prototype they are independent, and B's harvest falls in the season called Winter.

Farm state is spoken plainly to the player: `PREPARING`, `PREPARED`, `UNPREPARED`, `NEGLECTED`,
`MAINTAINED`, `READY`, `HARVESTING`, `HARVESTED`, `FAILED`.

**The producer carries its own output.** A harvesting farmer accumulates food *in its hands* at
`14/24` per second, up to `CARRY_CAP = 3`, then walks to the nearest store and deposits. A logger walks to
the nearest tree, chops until `cutNeed` is met, carries the wood in. Hauling as a distinct task exists
**only to clean up loose stacks**. So the walk is the cost, it is paid by the person who made the thing,
and "resource regions are deliberately far apart now; distance is the terrain" is a comment in the seed
function.

**Resources are physical, placed, and finite.** Trees have `cutNeed` and a wood value; stone nodes have a
stone amount and clear the trees around themselves. Forests are seeded as five large frontiers, stone as
four fields. The map is the economy.

**Loose stacks lie on the ground** and are recovered by whoever is nearest. Killing a loaded raider drops
its stolen food back as a stack — `Recovered 2.4 stolen food.`

**Delegation is priorities plus self-assignment.** `state.priorities = {farm, build, wood, stone}`, and a
new villager auto-assigns by categorical preference: a vacant farm, else logging, else a quarry. An
unassigned villager takes local chores scored by priority against distance. The player nudges the shape of
the settlement rather than the individuals.

**Buildings cost labour and materials**, and the builder inherits the job: `setAssignment(w,'builder',
site,{after:'farmer'})` — whoever builds the farm becomes its farmer.

**Population is endogenous.** Growth needs 45 days of food, housing room and warmth. Shortage accumulates
*privation*, and privation spends itself first as **emigration** — the ordinary carrying-capacity
correction — and only at catastrophic levels as death. Housing caps the population, which is §6's
"grain → people, gated by housing" already working.

**Raids are pressure, not a timer.** Pressure grows with *wealth* — food per capita — so prosperity makes
raids **more serious rather than much more frequent**, and a cooldown of 78 s or more preserves real
recovery windows. Three tiers: thieves, then armed raiders, then a warband. Raiders enter at a map edge,
breach palisades and gates, steal food, and run for the nearest edge. Detection is **105 at the settlement
against 190 at an outpost** — §7's "an outpost pushes R outward", as a number.

**Soldiers are an assignment**, trained at a barracks for 16 s and demobilizable back to civilian life.
Civilians defend themselves locally as an **interrupt** — which is what the interrupt layer was invented
for, before it was ever a manual order.

**The year is visible.** A timeline strip across the top shows all seven crop phases as bands, with food
demand, wood demand and farm workload as curves and a playhead on today. You can see the winter wood
spike and the harvest scramble coming.

### Where we already agree

The three-layer jobs model is carried across faithfully — assignment as persistent commitment, activity as
what it is doing now, interrupt as temporary and never rewriting the assignment — and so are the season
lengths, the physicality of resources, and dropped loot. Conservation in whole units is *stronger* here
than in the prototype, which uses floats and a `shortageDebt`.

### Where we evolved deliberately, and should stay evolved

- **Node-to-node hauling by carts, priced in seconds through congestion**, instead of every producer
  walking its own output in. §6 chose this so hauler count tracks buildings rather than population.
- **Catchments in route-seconds** instead of the prototype's flat distance limits (620, 700, 720, 760
  pixels), so reach follows roads and stops at ridges.
- **Houses as clocked sinks**, so distribution is a property of layout.

### Where we have drifted without deciding — the list to work through

1. **A farm is a rate, not a cycle.** There is no PREP, no ceiling you can lose, no reaping deadline. This
   is the largest gap by a distance: *the three-phase cycle is what the four verbs describe*, and without
   it Spring is not COMMIT and Harvest is not SCRAMBLE — they are just multipliers on a rate.
2. **Buildings cost nothing.** They appear instantly. In the prototype construction is labour, which is
   how attention converts into infrastructure and how §2's identity is paid.
3. **Resources neither deplete nor sit anywhere.** No trees, no stone, no frontier — so "distance is the
   terrain" is not true of our map, and a farm is an abstract producer that can be put anywhere.
4. **No stone, quarry or barracks**, so no materials for the defence ladder.
5. **Population is fixed.** No growth, no privation, no emigration — so a surplus has no purpose and a
   shortage has no consequence beyond a counter.
6. **No priorities.** The player posts individuals; the prototype's delegation is a handful of sliders and
   villagers who find their own work. That *is* the low-attention mode.
7. **No timeline.** The player cannot see the year coming, which is most of how the prototype makes a
   season legible before any number is read.

---

## 20. Session 6.5, stage A — a field is three windows of labour

The MVP slice, first stage. §19 named the gap and this closes it: a field no longer produces at a rate
with a seasonal multiplier on it. It is **prepared, kept and reaped**, each inside its own window, and
missing a window costs something a later window cannot give back.

### The windows are the seasons, and the numbers came from them

| phase | season | labour | window | occupied | verb |
|---|---|---|---|---|---|
| **Prepare** | Spring | 900 s | 1,200 s | 75% | **commit** |
| **Maintain** | Summer | 300 s | 1,800 s | 17% | **exploit** |
| **Reap** | Harvest | 800 s | 1,000 s | **80% + 26% walking** | **scramble** |
| — | Winter | 0 | 1,400 s | 0% | **survive** |

One cycle a year, and it *is* the year — which is the correction §6 asked for. The prototype ran a crop
model on its own calendar and season names on another, so its second harvest fell in the season called
Winter and three functions disagreed about when anything happened. Here spring prepares, summer keeps,
harvest reaps, winter rests, and there is nothing to disagree with.

**Reaping deliberately does not fit.** One pair of hands spends 80% of the harvest window reaping and
another 26% carrying the crop in — 106% of a window that does not stretch. A single farmer cannot quite
bring in a whole field, so something has to come and help it. That is the scramble, and it is a
consequence of the numbers rather than a number chosen to feel tight.

**Prepare sets a ceiling nothing later raises.** A field never broken yields *nothing*, whatever happens
in summer or harvest — and it says so: `unbroken`, then `failed`. Tending only retains, from 76% of the
ceiling to all of it. Reaping earns the crop second by second, and grain still standing when the window
shuts is grain the settlement never had.

### The reaper carries its own crop, and that is why fields cluster

Grain goes **straight into the hands of whoever reaped it** and is walked to the nearest store. It never
sits in the field: grain a body is holding is grain the settlement has not got, which is what makes the
walk a cost instead of a decoration.

Which produces the layout rule without anybody writing one down. Measured, fields on a 36 m ring — the
layout that suited the old cart-hauling model — **lost half the crop to commuting**: twenty seconds out
and twenty back for every thirty units, inside a window that only just holds the reaping. Tiled next to
the granary the same fields bring in **97% of nominal**. A settlement clusters its fields because the
arithmetic makes it, and the far resource is wood.

`HandsEffect`, the square root that made a second farmer worth 0.41 of the first, is **retired**. The
deadline does that work and does it better: a field asks for 900 labour-seconds inside a 1,200-second
spring, so one pair of hands just manages and four finish early with nothing to do. Lean staffing is
efficient because it fills each window exactly; overstaffing is wasteful because the window closes, not
because output is taxed. §2's identity with no invented curve in it.

### Three bugs, and two of them announced themselves as suspiciously round numbers

**A villager carried eight units.** Which is 88 trips to bring in one field — more walking than the
harvest window contains. The settlement starved with twelve healthy fields standing in front of it. A
carry is derived now, not chosen: 30 units is 34 seconds of reaping and 23 trips, a quarter of the window
on the road.

**Both ends of a two-legged job used the same dwell.** A farmer stood at the granary for the full 45-second
work shift to put down a sack. The two ends of a job are not the same job; the far end has its own duration
now, and the near end ends early when the worker's hands are full.

**Two years of production came to exactly one year's nominal — 8,400 against 8,400.** A field starts its
year over when it finds itself in a year it has not worked, which is pulled rather than pushed because
nothing is notified when a season turns. It asked `WorldCalendar` what year it was *without giving it a
time*, which is always the first one, so no field ever reset and the second harvest reaped a crop the
first had already taken. The roundness of the number is what gave it away.

**And a farmer with nothing to carry walked to the granary and back all summer**, because the empty-handed
case fell through into the delivery leg. Twelve people commuting to deliver nothing cost a third of a
harvest. A farmer with no work to do belongs at its field.

| | |
|---|---|
| suite | `--selftest` **73/73** |
| two-year settlement | 16,314 grain against a nominal 16,800 — **97%**, drift 0, short 0 |
| hauling in a compact settlement | **zero journeys**, which is the point rather than an omission |
| stores year on year | 6,974 → 8,199 at winter; autonomy climbing |

---

## 21. The scene got a sun, and a field stopped being a building

Watched rather than measured, and the complaint was the right one: *the lack of lighting, shadows and the
dull colouring on what's clearly primitives makes the whole thing hard to read.* It is not a cosmetic
problem. A settlement is read from above as a plan, and a plan drawn in flat ambient with no cast shadow
has **no depth cue at all** — every building is a coloured rectangle lying in the same plane as the ground
it stands on, so its height, its footprint and its distance from its neighbour are equally unreadable.
Calibrating an economy you cannot see is guessing.

### The frame is now a graph, lifted rather than invented

Sun shadow depth pass → HDR scene (procedural sky, shadowed sun, aerial perspective) → present (expose,
grade, ACES). Taken from TankArena and Bulwark, which already prove the seam; only the mood is this
game's. Four things earned their place:

- **The sun sits at 42° above the horizon**, not overhead. A building then casts a shadow a little longer
  than it is tall, which is the entire reason its height is legible from a top-down camera. The
  near-vertical light this started with hides every shadow underneath the thing that cast it and reads as
  no lighting at all.
- **The shadow map is 150 m around the camera focus, not the map.** One 2048 map over 600 m is 0.3 m a
  texel and every shadow edge is a visible staircase; 150 m is a little wider than the camera can see and
  comes to 13 texels a metre. The box is **snapped to its own texel grid**, because a box that slides
  continuously slides in sub-texel steps and every shadow edge in the scene then crawls as you pan — an
  artefact far more distracting than the resolution it would be hiding.
- **Buildings have eaves.** A wall box plus a roof slab a hand's breadth wider on every side. The
  overhang throws a hard horizontal line down the wall, and that line is the difference between a
  building and a box. It is a lie about the footprint — the wall below is what blocks — and it is worth it.
- **The palette was re-authored as albedos.** The old colours were written when a value went to the screen
  more or less as typed, so they sat near white before any light hit them, which is exactly why everything
  read pastel. Now they are in the 0.2–0.7 band and the sun does the brightening.

### A field is ground, and that fixed three things at once

*Farms should be more like terrain than anything else.* Correct, and following it through was the largest
single improvement in the scene — but the interesting part is that it was **not only a drawing problem**.

`NodeFootprint.Blocks` is now a separate list from `CellsOf`, because *how big a thing is* and *whether you
can walk on it* are different questions. A field has a 4.5 m footprint — its hands are counted against its
wall, it is drawn at that size — and a field is **not a wall**. It is tilled ground.

Conflating the two had made every farm a 4.5 m obstacle. Twelve of them ringed round a granary turned the
middle of the settlement into a maze, every farmhand was routed to the outside face of its own field, and
the fields could not be laid edge to edge without sealing the settlement in. It is also, belatedly, what
the earlier complaint about bodies *getting stuck in farms* actually was: they were never stuck. They were
going round.

So the fields are now **one contiguous block abutting the granary**, marching away from the village rather
than ringed all round it, with the houses on an arc on the other side. Three things settled that shape and
none of them was taste: the reaper carries its own crop so every metre of the walk is a metre not spent
reaping (§20); a field is not a wall, so plots tile edge to edge into a patchwork the way fields do; and
putting them all on one side keeps the traffic between store and field from crossing the housing. The
one-year yield came out at **8,104 of a nominal 8,400 — 96.5%**, so the shape cost nothing.

### The crop cycle is now visible, which it was not before

A field's trouble is always in the past — a ceiling not set in spring cannot be diagnosed at harvest from
anything a body is doing — which is why §20 built `CropCycle.StateOf` to have the field say so out loud.
**Saying it in a console table is not saying it to a player.** The same fact is now the colour and the
height of the ground:

| the field says | the ground is |
|---|---|
| `unbroken` / `failed` | pale dry earth, flat |
| `preparing` / `prepared` | dark broken earth, furrowed |
| `tending` / `tended` | low green |
| `standing` | tall gold |
| `reaping` | gold, shrinking as it comes in |
| `reaped` / `resting` | bare soil |

Someone who never reads a number can see that one field in twelve is the wrong colour. That is what the
mechanic was for.

---

## 22. Session 6.5, stage B — wood is standing on the map, and that is why carts exist

### The one decision: wood is never produced

It is standing in trees when the world begins, and cutting moves it out of a trunk into a cutter's hands.
There is **no production term for wood at all** — `Produced.Wood` is permanently zero, and
`Seeded + Produced − Consumed = Stored + Carried` still holds exactly. The conservation identity got
*shorter* again, which by now is a reliable sign the correction was the right one.

The consequence is the point: **a settlement's total wood is bounded by the forest it can reach**,
permanently. Stores cannot grow their way out of it. The only answer to running out is to go further,
which is the pressure the whole game is supposed to run on.

It also retires the last rate in the economy. The woodcutter *building* is gone — it accrued
`WoodPerHandPerYear × seasonalShape × sqrt(hands)` whether or not a tree stood within a hundred metres,
which is the same mistake §20 undid for grain wearing a different hat: an abstract producer you can put
anywhere, in a design whose premise is that the map *is* the economy. `HandsEffect`, `ProductionPerSecond`
and `WoodShape` all went with it. Nothing in the economy produces at a rate any more.

`WoodShape` deserves its own note. "Wood arrives when labour is free, which is summer and winter" was a
curve describing **a decision the player now actually makes** — spring and harvest belong to the fields,
and whoever is not in a field can be at a tree. Keeping the curve would have been the game playing that
allocation on the player's behalf and then charging them for it.

### The numbers, derived from one dial

The dial is `CutterWalkShare = 0.10` — the share of its year one pair of hands may spend walking wood in.
It is a dial about *waste*, not about distance, and everything else falls out of it:

| | | |
|---|---|---|
| cut rate | `WoodPerHandPerYear / (year × (1 − walkShare))` | 0.103 wood/s |
| a load | villager carry | 30 units ≈ 4.9 min of chopping |
| **reach** | `walkShare × year / trips / 2 × pace` | **29 m** |
| a tree | chosen, not derived | 90 wood = 3 loads ≈ 16 min |

The reach came out at 29 m against a design intuition of 10–20 m guessed from the other end, which is
close enough to trust both. A tree being three loads is the one number chosen rather than derived, and the
reason is legibility: a settlement burns twenty-odd trees a year, so at three loads a tree the wood line
visibly recedes within a season and the player can watch their own logging happen. One load a tree needs
the forest packed at three-metre spacing to last a year; ten makes a tree an hour of work and nothing ever
changes on screen.

The shift length is `carry / cutRate`, not the 45-second `WorkShiftSeconds` a field uses. That figure is a
*fallback* for a body whose hands fill in seconds; applied to cutting it would send a woodcutter home with
four units and turn the job into nothing but walking.

### The reach is measured from the store, and that is the whole mechanic

A cutter picks the nearest tree within reach **of the store it last delivered to** — not of itself. One
choice, and the entire stage falls out of it:

1. **Trees near the granary.** The cutter walks a few metres a load and carries its own wood in. No cart
   is involved. A compact settlement completes a year with **zero hauling journeys**, which is the point
   rather than an omission — a cart shuttling between a tree and a granary forty metres away is pure
   overhead, and the producer carrying its own output is the design.
2. **Those trees felled.** No store can reach a tree. The cutters lose their assignment and become spare
   labour — they say so, instead of quietly walking further and further, which is the settlement being
   told it has outgrown its arrangement.
3. **A forward depot built at the tree line.** The cutters re-base onto it by themselves: the nearest
   store with a tree in reach. Their wood now piles up in a building no household draws from.

**A lumber camp is not a building.** It is a forward depot — a store — and the only thing that makes it
one is that nobody lives near it.

### The hauling trigger became "stock nothing can reach"

Which is what step 3 needs, and the old trigger could not express it. It was *any store above its
high-water mark*, and that fails in both directions: a compact settlement's granary is the only store,
sits below high water all year, and would never be collected from — while a forward depot at 70% would not
be collected either, because it was not full enough, so a frontier holding simply filled up and stopped.

A store is **stranded** if no house names it as its supply. Stranded stock is worth fetching at a cart's
load, and moves only toward a store something actually eats from — without that last clause two depots at
the tree line would pass the same wood between themselves forever, both equally unreachable. The urgency
is a producer's-yard urgency, because that is what it is: output accumulating where nobody can use it,
and when the building fills the people feeding it stop working.

The question is not how full a store is. **It is whether anybody can reach what is in it.** Nothing in
either half of the gate knows what a lumber camp is; the difference is entirely geometric.

### The gradient of the forest is the record of past logging

Stragglers within reach of the store, canopies at the edge of it, unbroken woodland beyond. That is not
decoration — it is what a settlement that has been cutting for years looks like from above, so **the map
tells the player which direction the wood ran out in before the simulation has run a tick.** "Distance is
the terrain" is now a fact about the ground rather than a comment in a seed function.

Deterministic, from a splitmix counter rather than a clock, because two runs of this world must be the
same world — the fingerprint checks it and the save relies on it. There is no `Random` in the simulation
and this was not the place to introduce one.

### Two bugs, and the second one was invisible in every column

**Seven cutters felled one tree.** "The nearest tree" is the same tree for every cutter based at the same
store, so all seven converged on one trunk, felled it in a seventh of the time, and walked to the next one
together. A tree is claimed if another body's standing job already names it — read off the assignments
rather than kept as a reservation table, for the same reason the board reads which carts are already
hauling: a second collection to keep in step with spawning, despawning and tombstoned slots is a
collection that is eventually wrong, and this one would have to survive a save too. The claim is a
*preference* and not a lock, because two axes on one tree really do fell it in half the time.

**And it was silently costing the hand count.** Six of the seven were shoved off the trunk by the crowd
and settled outside the reach that counts as working it, so a woodland with thirteen trees in reach was
being worked by one person. Nothing in any column said so — the wood arrived, just slowly — and it was
only visible as `hands` reading 12 instead of 19 in one seasonal sample.

### What the year does

| | |
|---|---|
| suite | `--selftest` **75/75** |
| one year, compact | 8,104 grain of a nominal 8,400 (96.5%), wood short **0**, drift **0**, **0 hauls** |
| trees in reach | 34 → 27 → 13 → 6 over year one, **0 by the spring of year two** |
| when reach runs out | axes swinging drops to 0, stores drain, and ten thousand trees holding **895,590 wood** stand out of reach |
| the depot, in the self-test | trees at hand → **0 hauls**; wood line pushed out with a depot on it → **9 hauls**, both drift 0 |
| benchmarks | pen 1.33x, gate 1.78x, 33 dead stops, red 119.2 agent-s — unmoved |

The second year is not a failure, it is the mechanic arriving on schedule: a settlement that does nothing
runs its own woodland out inside two years and is told so a season in advance. The answer is a depot at the
tree line, and the carts that appear when you build one are Stage B working.

### What remains of the slice

- **C — Population.** Houses with their own caps, a villager appearing at a house with room inside a
  feeding centre's reach, growth on food-days + housing + warmth, privation spending itself as emigration
  before death. The spare labour a receding wood line produces is the thing population growth wants.
- **D — Construction as labour.** Which is what makes the depot in step 3 cost something.
- **E — The raid, and civilian self-defence as an interrupt.**

---

## 23. The art pass, and the rule that came out of it

CC0 low-poly pack in, greybox out. The pack turned out to be an unusually close fit — a hundred and
thirty models, untextured, position-and-normal only, with a flat linear base colour per material and
**twenty-one distinct colours describing all of it**. No sampler, no cook, no texture memory: every
primitive becomes one instanced batch tinted by its material. It maps onto the design almost one to one,
including a farm family that is *literally* a tilled plot plus standing wheat at three growth stages, and
`_Cut` tree variants for stumps.

### One new engine primitive, because this was the third copy

`InstancedBatch` draws one mesh many times, which is right for a cube and useless for an imported model: a
low-poly building is one mesh split into a primitive per material, with no vertex colours, so the
material's colour has to arrive as the instance tint. That means N batches all needing the **same**
instance set, and keeping those in step by hand is how a roof ends up on a different building from its
walls. TankArena and Bulwark had each grown a private version.

- `Blix.Assets/MeshDataExtensions.Transformed` — transform positions and normals (inverse-transpose),
  recompute bounds. What TankArena's private `BakeMerge` should have been.
- `Blix.Render/PropModel` — parts → one shared instance list → a scene draw and a shadow draw that
  **cannot drift from each other**, because they read the same list by construction. Geometry only: the
  caller brings pipelines, pushes and lighting.
- `RTSGame/Rendering/SettlementArt` — which file is a granary, and how wide it stands.

### The fit is measured, and the size is the simulation's

The pack's convention is loose: identity node transforms and bases at Y ≈ 0, but horizontal centring
wanders by up to half a unit because a model is drawn wherever it looked right inside its tile. So the fit
is read off the geometry at load — recentre on the measured footprint, drop the measured base to the
ground, scale so the wider ground dimension is one unit. **There is no table of per-asset constants**, and
the hundred-and-thirty-first model needs no fitting pass.

The one thing not measured is how wide a building is in metres, and that is correct: it is not a fact about
the model. A granary is 7.5 m because `NodeFootprint` says five placement cells and bodies route around
exactly that square. The art is scaled to the footprint the game enforces, not the reverse.

### What looking at it caught, that no test would have

Four things, in the order they became obvious:

1. **The wheat was smeared into ribbons.** The crop had been stretched non-uniformly to fill its square
   footprint, which smears every individual stalk along the stretched axis — and a hundred stalks smeared
   identically stop being stalks. The *plot* is still stretched (it is flat, so the distortion is
   invisible); the crop is uniform.
2. **The tilled plot was invisible.** It is a flat slab and the coarse ground under it is a flat slab at
   exactly the same height; two coplanar surfaces z-fight per pixel. Lifted 2 cm.
3. **The ground was washed out.** Grass albedo was 0.42 green, written when the ground was the brightest
   thing in the frame by construction. The pack's greens sit between 0.09 and 0.23, so a plain read as a
   pale sheet with dark models scattered on it. The terrain is in the pack's palette now, matched to its
   own materials where there is one to match. Same for the villagers, who were tonemapping to near-white.
4. **The shadows were acne, not softness.** Broad dark smears on flat ground that correspond to nothing
   are a surface shadowing itself, and depth bias cannot fix it: the error a shadow map makes on a surface
   the light grazes is that one texel covers a long slice of it, and the amount grows without bound as the
   angle closes. Enough depth bias to cover it detaches every shadow from its caster. So
   `blix_shadow_normal_offset` moves the sample **along the surface normal**, off the surface, by a
   distance related to how wide a texel is in world units — the actual scale of the error. Depth bias then
   drops from 0.004 to 0.0012.

And a fifth, which is the one worth remembering: **softness is tap density, not kernel width.** Sixteen
binary comparisons spread over six texels of ground do not average into a gradient, they quantise into
blotches — a shadow blurrier and dirtier at the same time. Narrowing the kernel to a texel and a half
made it *softer looking*.

### The rule: what cannot be measured is a control

The lighting was flat, and the reason is worth writing down because it is not obvious. With a sun of 2.05
and a terminator wrap of 0.25, a face-on surface lands at 1.25 HDR and a side face at 0.43 — but **ACES
compresses both into its shoulder**, so they come out 0.78 and 0.42. An output ratio under two to one on a
light ratio of three. The wrap was doing most of the damage: at a quarter, a face turned ninety degrees
from the sun still collects a fifth of it.

Which is exactly the kind of thing nobody derives, and exactly the kind of thing that had been sitting in a
shader as `const float kSunIntensity = 2.05` looking like a fact. So `LookSettings` now carries every one
of them on a slider — sun elevation and bearing, intensity, ambient, terminator wrap, shadow penumbra and
normal offset, shadow box, fog, exposure, tonemap curve, saturation, contrast, checker contrast, ground
variation, tree draw distance — and the shaders take them through the push constants rather than declaring
them. The things that *are* facts stay out: the shadow map's texel size and world extent are geometry.

That is the same rule the movement layer already followed and for the same reason: **a number nobody can
measure should be visibly a question rather than quietly indistinguishable from an answer.** It is more
tempting to break here than anywhere else, because a lighting constant looks exactly like a physical one.

### A dense forest, and the two things it broke

The woodland went from 341 trees to **ten thousand**, which is what "distance is the terrain" needs to look
like from a camera. Two things had to change first, and both were latent bugs rather than optimisations:

**Trees are culled against the camera's focus.** The camera sees about ninety metres and there are now
thousands of models; without a cull the frame draws the whole map every frame. With it, the frame went
*down* from 16.7 ms to 14.4 despite thirty times the trees. The radius is a slider, and it is never
smaller than the shadow box — a tree behind the camera still casts into the frame.

**A hand is a body standing at the site it was assigned to.** `CountHands` used to find the nearest node to
each body by scanning every node in the world. Fine at twenty; at ten thousand it is nineteen scans of a
ten-thousand-element struct array per tick, thirty-odd megabytes of streaming, and the settlement gate went
from 0.19 ms a tick to **1.74**. Asking the assignment which site it named, and then whether the body is
actually standing there, is O(1) — and it is the sharper question: a hauler unloading in a farmyard is not
a farmhand, and a body still walking to its field is not working it yet. Back to **0.25 ms**.

**And the first band of the scatter is load-bearing.** Everything §22 measures depends on how much wood
stands within a cutter's reach of the store. Denser canopies just beyond reach scattered members *inward*,
which pushed the in-reach count from 46 to 66 — half a settlement's annual fuel, arriving as a side effect
of a density change. The band now starts clear of the reach radius. The crunch consequently lands a season
earlier than §22 recorded, which is better pacing: you finish year one and must act in year two.

| | |
|---|---|
| suite | `--selftest` **75/75** |
| one year, 10,005 nodes | 8,111 grain of 8,400 nominal, drift 0, short 0, **0 hauls**, 0 stalled |
| tick cost | 0.25 ms at ten thousand nodes (0.19 at three hundred) |
| frame | 14.4 ms with 4x MSAA, three passes, ~10,000 trees on the map |
| repo cost | 3.7 MB of art, 16 files, no textures, no cook step |
| benchmarks | pen 1.33x, gate 1.78x, 33 dead stops, red 119.2 agent-s — unmoved |

### Still open

- **A felled tree leaves nothing behind.** `Resource_Tree_Group_Cut` is loaded and waiting, but a stump
  needs a node that outlives the tree, which is a simulation change rather than a drawing one.
- **Villagers are static.** The pack has no rig, so a villager slides rather than walks. Best-effort —
  deleting the OBJ reverts to cylinders.
- **Large numbers in the reports render with the machine's digit grouping** (`8,95,590`), which is the
  locale doing as it is told and looks like a bug. One `CultureInfo.InvariantCulture` away.
- **Frame headroom.** The sky is drawn first, at 4x, over every pixel, before the world draws on top of
  it. Drawing it last with depth testing on would shade only the pixels the world did not cover, which on
  a top-down camera is a small fraction. First thing to try if the budget tightens.

---

## 24. Hauling is a job, not a kind of unit

A design correction, and the identity got shorter again — which by now is the reliable sign.

**There is no hauler unit.** A villager given a route spends a sack of the settlement's timber on a
handcart and wears the `HaulerCart` frame — wider, slower, holding more — until it is given something
else to do. `UnitType.HaulerCart` survives, but as the answer to *"how big and how fast is somebody
pulling a cart"* rather than as a thing you have seven of.

The design argument is §2's. A permanent cart unit is a decision made once and paid for forever; a role is
a decision whose cost you can see and change. And a settlement that needs no hauling should have **no
carts in it**, not seven idle ones — which is exactly what the gate now says. "Zero hauling journeys in a
year" became the stronger claim **"zero carts built"**: nobody even had to make one.

### A route is a standing commitment; a haul is one round trip

Two assignment kinds, not a flag, because they differ in the thing that matters:

| | who authors it | how long it lasts | why |
|---|---|---|---|
| `Haul` | the board | **one round trip** | so it can be re-priced. A body that kept a route for life would be priced once, at hiring, and the promise that a jammed lane makes a different body cheaper would be about a decision nobody revisits. |
| `Carry` | the player | until the source runs dry | nobody re-auctions it because nobody is meant to. "These three are on the timber run." |

So the board keeps the two cases nobody would ever micromanage — **stranded stock** (Stage B's trigger) and
**goods lying in the road** — and the player owns the rest. A carter between routes goes back on the board,
which is what a settlement's general carrier does between errands. A route running dry is `RoutesFinished`,
not `HaulsAbandoned`: it is a job ending on its own terms, and counting it as a failure would make the
abandoned column meaningless.

### The cost is the interesting part

A cart costs one villager's sack of timber — 30 — which is the smallest amount of anything anybody carries
in this game and therefore the natural unit for "token". A settlement burns two thousand a year, so a cart
is under two per cent of its fuel: cheap enough that the first one is never the decision, dear enough that
thirty of them is.

The wood is **consumed**, not moved: it has stopped being timber, so it belongs on the same side of the
identity as a loaf, and building a hauling network shows up in the ledger as something the settlement spent
wood on. And it is **refused** when the timber is not there — which is the dependency Stage B's receding
wood line exists to create: *you cannot cart wood in before you have wood.*

### What the three layers gave for free

`Y` takes a carter off work and the cart goes with the job. An **order does not** — an interrupt never
touches the assignment, so a carter sent somewhere by hand walks there and comes back to its route still
pulling its cart. That fell out of the jobs model rather than being written, which is the third time that
layer has paid for itself.

### Two things that had been quietly wrong

**Capacity is not what makes somebody a hauler.** The board recruited any body with `CarryCapacity > 0`,
which was true of exactly the carts when it was written — and is now true of every villager, because a
reaper walks its own crop in. Left alone, the board would have put its stranded-stock journeys on
farmhands.

**A collider had to be able to change size.** A body's dimensions were fixed at spawn, so all four of a
carter's proxies needed reshaping in place. Removing and re-adding them would work and would be wrong: ids
are handed out by position and never reused, so a body that took a cart and gave it back would leave eight
dead proxies behind and shift every id issued afterwards — which the determinism fingerprint reads and a
save has to reproduce.

| | |
|---|---|
| suite | `--selftest` **76/76** |
| one year, compact | 8,122 grain of 8,400 nominal, drift 0, short 0, **0 carts built, 0 hauls** |
| the role, measured | cart costs 30 wood and the ledger says so; a second refused for want of it; 0.37 → 0.55 m body and colliders; 17 legs on one standing route; kept through an order; scrapped when taken off work; drift 0 |
| benchmarks | pen 1.33x, gate 1.78x, 33 dead stops — unmoved |

### What Stage C inherits

The scenario's seven ex-carts are now **seven spare villagers**, which is the right thing for them to be:
spare labour is what a growing settlement has and what a receding wood line produces. Stage C is what gives
it somewhere to go — and new villagers will arrive **idle**, not auto-farming, because posting them is the
decision the game is made of.

---

## 25. Stage C — population, and the loop closes

A villager appears at a house that has room for them, inside the reach of a store that can feed them, when
the settlement has enough put by to see the extra mouth through a winter. That is the whole mechanic, and
every term in it is something the player built and can point at.

Which closes the loop the rest of the economy had been building toward. **A surplus had no purpose before
this**: stores climbed, autonomy climbed, and nothing happened. Now a surplus is people, people are labour,
and labour is the only thing that turns a field or a tree into anything.

### Housing is the cap, food is the brake

Growth accrues **per house with room**, not per capita. So it is proportional to housing the player has
built rather than to population — it does not compound on its own, and there is no invented damping curve
to justify. *Building houses is how you ask for people.*

And readiness — whether the stores would cover everyone here plus one, for a winter — is a **rate, not a
gate**. A settlement with half a winter put by grows at half speed, so there is no cliff to fall off and no
cliff to farm right up to the edge of. It is a **product** over grain and wood rather than a sum: bread and
firewood are not substitutes, and being rich in one covers nothing.

The winter is derived rather than picked. The year has one harvest and one season in which nothing grows and
everything burns three times as much wood, so *"can we feed one more"* is exactly the question *"would we
still get through the winter"* — and the threshold moves with the calendar instead of being a day count
somebody has to remember to update. The one figure that is **chosen** is the pace: half a year of good
conditions per free place. A year per place, measured, came out at under two births a year on a settlement
of twenty-six, which is an hour of play for one person.

### A newcomer arrives idle, on purpose

They could be sent to the nearest field that wants hands, and that would be the game playing itself.
Posting people is the decision §2 says attention is *for*, and auto-assigning them would quietly convert
the one interesting choice in the settlement into a notification. So they stand outside their house until
somebody gives them a job, and the spare-hands count is the prompt.

### Privation spends itself as emigration

A household whose store is empty accumulates privation; enough of it and somebody leaves. Not death —
there is nothing to die of yet, and that belongs with Stage E.

It is on the **house** rather than the settlement, which is what makes it legible: a house outside every
catchment empties itself while the ones inside do not, so the mistake is on the map rather than in a
shortfall total. And it drains three times faster than it fills, so a settlement that fixes its supply
stops losing people instead of going on losing them for as long as the shortage lasted.

### Three bugs, and two of them were the same mistake in different clothes

**Privation was measured on the wrong ticks.** A household draws a fraction of a unit per tick with the
rest accumulating in `Pending`, so "a whole unit came due and failed" is true about one tick in a hundred.
A settlement whose wood ran out for a *year* accrued about a minute of privation and **nobody ever left**.
Going without is a *state*: the store I draw from is empty and I want something, which is true every tick
of a famine.

**An empty settlement had perfect readiness.** Readiness measured the buffer against current draw and
treated no draw as an infinite buffer — reasoning that a settlement with no houses yet is not short of
food. True of the *start*, catastrophically untrue of the end: a settlement whose last household starved
out also has no draw, so it scored 100% and its empty houses began producing people out of an empty
granary. Measured, **a world with nothing in it grew a villager a year.** Asking whether the stores would
cover *everyone here plus one* has no zero case, because the answer always includes at least that one.

Both are the same error: treating a continuous condition as a discrete event, and treating an edge as a
special case instead of finding the formulation that has no edge.

**And an empty house kept its privation.** It neither accrues nor drains while nobody lives there, so the
next person to move in inherited a full measure of somebody else's famine and walked straight back out.

Plus one where the *test* was at fault: it zeroed a granary's stock to prove that food gates growth, which
destroys units outside the ledger. The drift check caught it, correctly — **a test that breaks conservation
to make a point has stopped testing the thing it was about.** It uses a second world now.

### What two years look like

| | year 1 spring | y1 winter | y2 summer | y2 winter | y3 spring |
|---|---|---|---|---|---|
| people | 26 | **30** | 30 | 22 | **14** |
| housing spare | 10 | 6 | 6 | 14 | 22 |
| readiness | 100% | 100% | 19% | **0%** | 0% |
| trees in reach | 34 | 6 | 0 | 0 | 0 |

It grows on food and then collapses on fuel. The collapse is the mechanic arriving on schedule rather than
a failure: by the second summer the in-reach woodland is gone, wood hits zero, readiness goes to zero
because readiness is a product — and then nine households all cross the privation threshold within a season
of each other and the settlement loses roughly a person per household per season. An unattended settlement
that has permanently exhausted its reachable fuel and does nothing about it depopulates, which is correct.
The answer, in a game with a player in it, is a forward depot at the tree line.

Worth naming: **the unattended failure mode is always wood.** Grain never binds — twelve fields against
thirty mouths is comfortable — so every collapse in the gate is the wood line, which is Stage B's mechanic
being the sharpest one in the economy.

| | |
|---|---|
| suite | `--selftest` **78/78** |
| one year, compact | 8,122 grain of 8,400 nominal, drift 0, short 0, **0 carts, 0 hauls, 0 emigrated** |
| two years | 5 born, 17 left, drift 0 — growth on food, collapse on fuel |
| per person per year | 270 grain against a nominal 270, measured in **mouth-years** |
| tick cost | 0.4 ms at ten thousand nodes |

### One reporting fix worth keeping

Per-person figures were divided by the *final* headcount, which reported 513 grain a head against a nominal
270 for a run that halved — the settlement had not eaten twice its ration, it had shrunk. It is measured in
**mouth-years** now, accumulated a tick at a time, and reads 270 against 270.

### What Stage D and E inherit

- Spare hands are now produced by two things — a receding wood line, and births — and **construction as
  labour** is what gives them somewhere to go that is not a field.
- Privation and emigration are the shape death will take in Stage E, one level up: the machinery for
  "somebody leaves the world and drops what they were carrying" already exists and is exercised.

---

## 26. Stage D — construction as labour, and a camera you can steer

### A building under construction is that building, unfinished

Not a separate kind of node with its own rules — the same node with its labour not yet spent. Which is why
the `IsBuilt` test went **inside** the predicates on `EconomyNode` rather than at their twenty-six call
sites: an unfinished granary stores nothing, feeds nobody and owns no catchment in all twenty-six places,
and there are no twenty-six chances to forget one.

It also means **the footprint never changes.** A site occupies exactly the ground the finished building
will, so bodies route around it from the moment it is placed and completion re-rasterises nothing — which
matters, because placing a building is already the one operation that rebuilds the navigation raster, and
doing it twice per building would double the cost of the most expensive thing the player can do.

### Paid for in the only two currencies the game has

| | timber | labour |
|---|---|---|
| granary | 18 sacks | 3,600 s |
| house | 6 sacks | 1,200 s |
| forward depot | **4 sacks** | 600 s |
| farm | — | — |

In *sacks*, because a villager's carry is the unit everything else in this economy is measured in and "six
sacks of timber" is a thing a player can hold in their head where "180 wood" is not. Labour is sized
against the seasons, like the crop windows: a house is 1,200 against a 1,200-second spring, so one pair of
hands takes a season and four take a quarter of one.

The depot is deliberately cheap. It is the answer to a receding wood line, and **the answer must not cost
more than the problem** — four sacks is affordable out of a settlement already running short, which is
exactly when it is wanted, and that is what makes acting early rather than late a real decision instead of
a hint.

**A field costs nothing**, and that is not an oversight. Breaking ground is already the crop cycle's Prepare
window — 900 labour-seconds inside a 1,200-second spring, the most expensive thing a farmhand does all year.
Charging construction on top would be charging twice for the same work, and a field that had to be *built*
and then *prepared* would put a season between deciding to plough and ploughing.

Labour accrues **per site from the hands standing at it**, unlike a crop. A field accrues per body because
the reaper carries the crop away in its own hands and the grain has to go somewhere; a building has no
output, so there is nothing to attribute and four builders are simply four times the work.

The timber is **consumed on completion**, not gradually. Spending it as it goes would be more physical and
would mean abandoning a half-built house destroyed material — so there would have to be a rule about
salvage. Consuming it at the end means an unfinished site is simply timber standing on the ground where
somebody left it, which is what conservation already knows how to describe.

### The one task the board reads backwards

Every other journey on the hauling board starts from **goods in the wrong place** and looks for somewhere
better — a heap, a stranded store, an uneven pair of granaries. A site is the opposite shape: a **demand at
a place**, where the question is which store can answer it. So it gets its own pass, and it outranks a
producer's overflowing yard, because hands standing at a site with no materials are hands doing nothing at
all — where a farm that stops producing still has its hands doing something.

This is also what makes a cart **necessary** rather than merely useful, and it closes a loop three stages
wide: a settlement with no carter cannot get timber to a site, a cart costs timber, and the timber comes
out of a woodland that is receding. The first cart comes from the founding stores; after that the hauling
network is what lets the settlement build at all.

### Two bugs, and the second was hiding behind the first

**`Assignment.Hold`'s node ids defaulted to node zero, not "no node".** `default(NodeId)` is `NodeId(0)`
and `NodeId.None` is `NodeId(-1)`, so a bare post named the first node in the world. Harmless only by
accident: the one caller that asks a Hold which node it serves went on to check the node was a work site,
and node zero never is.

**Fixing that exposed the save format.** `AssignGroupCommand` wrote four of an assignment's ten fields —
the kind, both anchors and the dwell — which *was* the whole of an assignment when the only ones a player
could issue were a post and a shuttle. It has not been the whole of one for three stages: a haul names two
nodes and a cargo, a shift of work names its site and has a different duration at each end, a route names
both. Everything unwritten came back as `default`, so **an order in flight across a save became an order
about node zero.** It went unnoticed because the round-trip test queued a shuttle, whose unwritten fields
were already default — making Hold and Shuttle say `NodeId.None` explicitly is what finally made the two
differ, and the census test failed on the next run.

Worth keeping as a pattern: *a test that only exercises the default case cannot tell you that you saved the
defaults.*

### The camera, which had three things wrong with it

- **The far plane was a flat 150 m while the zoom range went to 810** on a 600 m map. Pulling back past a
  hundred and fifty clipped the entire world away and the screen went to sky. It follows the zoom now, and
  the range is 8–240 m: near enough to read what one villager is carrying, far enough to hold the
  settlement and its tree line.
- **There was no panning at all.** The camera's only way of getting anywhere was that it followed whatever
  was selected — so looking at a corner of the map meant selecting something in it, and the view drifted
  whenever a selected body walked. Following is still on `Z` and is off by default now, because a camera
  that moves on its own is a surprise once there is a way to move it deliberately.
- **The arrows rotated**, so pressing Left to look left spun the world. Rotation is Q/E; the arrows pan, in
  *screen* space rather than world space, because "left" means left on the monitor and after a 90-degree
  rotation that is a different compass direction.

Middle-drag grabs the ground, and both ends of the drag are raycast against the terrain with the **same**
camera — so the world moves exactly as far under the cursor as the cursor moved. Approximating it as screen
pixels times some function of the zoom is off by the perspective and drifts under the cursor as you drag
toward the horizon.

| | |
|---|---|
| suite | `--selftest` **79/79** |
| one year, compact | 8,121 grain of 8,400 nominal, drift 0, short 0, **4 born, 0 left, 0 faults** |
| construction, measured | a depot at 40 m: 120 timber carted out by 185 s, two builders finished 600 labour-seconds by 485 s, then it stores and owns a catchment; timber consumed, drift 0 |
| tick cost | 0.77 ms at ten thousand nodes |

### The one thing accumulating

**Per-tick O(nodes) passes.** There are now six of them — the hand count's clear, the crop roll, the home
rebind's clear, consumption, population, and construction — plus the gate's own conservation check. Each is
about 0.1 ms at ten thousand nodes and together they are most of the tick. None is wrong and none is worth
fixing yet; the pattern is worth naming because the next one will make it 0.9 ms, and the fix when it comes
is the same one `CountHands` already got: ask the thing that knows rather than sweeping everything that
might.

### What Stage E inherits

- **Privation and emigration are the shape death will take**, one level up: the machinery for "somebody
  leaves the world and drops what they were carrying" exists and is exercised every run.
- **A site is a thing that can be interrupted**, which is what a raid on a half-built granary means.
- **Buildings can now be lost for a cost that is legible** — a burnt store is eighteen sacks and half a
  year of somebody's time, which is what makes defending one a decision rather than a reflex.

---

## 27. A forest you cut your way into — and the rasteriser cost it exposed

*Foot can still pass through trees, so no forest cover.* True, and it was a decision made in §22 with a
stated reason that weighed the wrong things. Re-opened, measured, and changed.

### The measurement that ruled out the obvious answer

Three facts, in the order they mattered:

**There is no cheap middle.** The velocity solve does not see static colliders at all — only a
depenetration pass does, and only for AABBs. Buildings are avoided by *routing*, not by steering. So
giving trees a collider would not make bodies weave between trunks; it would make them walk into trunks
and be shoved out, which is precisely the "they get stuck in farms/trees" complaint from Stage A. The
architecture's position is: **if a thing should be gone around, it belongs in the navigation raster.**

**At the density the map wants, individual trunks cannot enter the raster.** Measured over the scatter:

| nearest neighbour, deep woodland | |
|---|---|
| min | **2.20 m** |
| median | 2.52 m |
| p95 | 3.46 m |

A 0.45 m trunk leaves a 1.30 m gap at worst. A villager is 0.74 m across and a cart 1.10, so everything
*physically* fits — but the raster quantises clearance to rungs of 0.25/0.75/1.25, and a 1.30 m gap gives a
cell-centre clearance of about 0.65, which lands on the 0.25 rung and is refused to a 0.37 m routing
radius. **Ten thousand blocking trunks is ten thousand unroutable holes.**

**And "forest cover" is two asks.** Bodies not walking through trunks is one thing; a forest *concealing*
things is another, and needs a vision system that does not exist. Concealment goes with Stage E's raid.

### So the interior blocks and the fringe does not

A cell is forest if three trees stand within 2.6 m of it — about four in the deep woodland and about two at
the edge of a stand, so the threshold is what separates those. That gives a contiguous impassable mass,
which is what the routing hierarchy wants, and it means **the only trees anybody can reach are on the
edge.**

Which is the mechanic rather than a limitation: **you fell the fringe, and the fringe moves in.** A
settlement starts in a clearing and cuts its way out, and the wood line receding is literally the passable
edge moving outward. The near band — 46 trees at 3.4 m spacing, about two per radius — stays open by
construction, which is why the cutters who start there can work at all.

Two supporting decisions:

- **`TerrainSurface.Forest`, not `Impassable`.** Impassable is water, and conflating them would make every
  question anybody ever asks about water — can a boat cross it, does it put out a fire, does it stop an
  arrow — answer the same about a wood. Same mistake as a building's size standing in for whether you can
  walk on it, and a store's fullness standing in for whether anybody can reach what is in it. All three
  were fixed this session and all three were one enum value short of never happening.
- **A terrain surface rather than occupied placement cells**, because the terrain grid *is* the navigation
  grid at half a metre — so painting it blocks routing directly. As placement cells it would have been
  seventy thousand static colliders describing ground nothing ever touches.

And the cutters had to learn reachability. The nearest tree to a store is very often one buried inside a
stand; a cutter sent to one walks at it, fails to arrive, retries politely and never cuts anything — so the
settlement would starve for wood while standing next to a forest.

### The rasteriser was quadratic in obstacle density, and nobody knew

Painting 74,517 impassable cells took one rebuild from 350 ms to **12.7 seconds.** Instrumented: **eleven
billion box comparisons.**

The cause was that `ObstacleIndex` bucketed at `Reach` — *the clearance ceiling* — so every one of 1.44M
cells gathered a 96 × 96 m neighbourhood of obstacle boxes to find something usually a metre away. Perfectly
fine on a map whose obstacles were a pond and a few walls. Three changes:

1. **`Reach` is 6 m, not 32.** Nothing has ever needed a clearance value above about three: a constriction
   is `radius × 2`, open ground is `radius × 5` — 2.75 m for the widest body in the roster — and a passage
   axis is only sought below `radius × 3`. Above that the answer is "open", and how open does not matter to
   anybody.
2. **Buckets are 2 m, decoupled from `Reach`.** They were never the same thing: one is a clamp on a
   reported value, the other only has to make a nearest-obstacle search terminate.
3. **Gather once per 4 × 4 tile, widening only as far as needed.** Sixteen cells share a neighbourhood, and
   among trees the smallest radius already answers it.

**12,723 ms → 1,043 ms**, with every behavioural benchmark byte-identical: pen 1.33x, gate 1.78x, dead-stops
0 and 33, red 119.2 and 335.0 agent-seconds.

### Three wrong turns, and what caught each

**An exact Euclidean distance transform**, which was faster still at 620 ms, and was backed out. It
measures distance to a blocked cell's *centre* where the existing semantics measure distance to its *box* —
so it over-reports clearance on diagonals by 10 cm, and clearance is the number that decides whether a body
fits. Three clearance-rung tests failed and were right to. The box distance is not a function of the point
distance, so no post-hoc correction exists.

**Treating "nothing found, so the map edge is nearest" as settled**, which reports clearance *larger* than
the truth — the direction that tells a body it fits where it does not. The movement benchmarks caught it as
a mean clearance that had gone **up** (2.38 → 2.47) rather than down.

**Bailing out of the cell loop on the final pass**, which leaves the rest of a tile never written —
clearance zero, which reads as solid ground, and on open terrain that is most of the map. **Nineteen tests
failed at once.** The benchmarks missed this one entirely, because their scenarios are wall-dense and
almost every cell is near something; it took the general suite.

Worth keeping: *the benchmarks and the suite fail on different things, and neither is a substitute.*

| | |
|---|---|
| suite | `--selftest` **79/79** |
| forest | 9,985 trees close **270,327 of 1.44M cells** — 18.8% of the map, 67,582 m² |
| one year, compact | 8,049 grain of 8,400 nominal, drift 0, short 0, 4 born, 0 left, 0 faults |
| cutters | 7 working the fringe all year, wood never short |
| rasterise | 12,723 → **1,043 ms**; re-opening around one felled tree 0.4 ms |
| benchmarks | pen 1.33x, gate 1.78x, dead-stops 0/33 — unmoved |

`--forestcost` is the new diagnostic that produced these, and it stays: the cost of an impassable region is
the thing that decides whether the mechanic is affordable, and it should be measurable rather than
remembered.

### What Stage E gets

- **Approach routes are constrained**, which is most of what a raid needs from terrain: raiders can only
  come through the gaps, and the gaps are where the settlement has been cutting.
- **Concealment is the remaining half**, and it lands with the raid — a vision system that trees occlude,
  against §7's detection radii of 105 at the settlement and 190 at an outpost.
- **A settlement can wall itself in**, which is a real strategic position rather than a bug: the forest is
  a defence until you cut through it, and every path you open is a path in.

### The density is a dial, and the sweep settled its default

Looked at with the navigation overlay on, the first cut read wrong: the trees were dense and the
**obstacle** was not — 5.2% of the map closed, in speckled patches rather than masses. So `CoverTrees` and
`CoverRadius` are sliders now (`WoodlandSettings`), applied a third of a second *after* the value stops
moving, because a repaint is 80 ms and the rebuild behind it is a second — a slider dragged across its
range would fire fifty of them.

`--forestcost` prints the sweep, and the default came out of it:

| trees | reach | closed | in reach | cuttable |
|---|---|---|---|---|
| 2 | 2.2 | 10.5% | 34 | 9,919 |
| **2** | **2.6** | **18.8%** | **34** | **8,444** |
| 2 | 3.0 | 25.6% | 34 | 4,859 |
| 3 | 2.6 | 5.2% | 34 | 9,929 |
| 4 | 3.4 | 8.5% | 34 | 8,779 |

**The column that mattered was not the closed share.** It was *in reach* — how many trees a cutter based at
the granary can still get to — and it holds at 34 across every setting from two trees at 2.2 m to four at
3.4. The failure that would have been fatal, a settlement's own thinned stragglers sealing over its
starting supply, does not happen: the near band is scattered at a 3.4 m floor and a tree on a small closed
patch still has open ground beside it. Worth having measured rather than assumed.

*Cuttable* is the breadth of the fringe rather than a cap on the resource — the fringe advances as it is
cut, so a low number means a thin working edge, not a sealed forest.

### And the density exposed two more, both about asking the wrong layer

**Spawning only checked for buildings.** `NudgeOutOfBuildings` asked whether a position was clear of
placement obstacles, which was the whole question while the only impassable things were buildings and is
not the question at all once terrain can refuse ground. It asks the router's own question now — does the
raster admit a body of this radius here — which covers surfaces, buildings and clearance at once and cannot
disagree with the thing that will later be asked to route out of there.

**And the scenario was reading a raster from before the forest existed.** `RefreshForestCover` only bumps
the terrain revision; the rebuild happens on the next tick, which is exactly right for the running game and
wrong for setup code about to ask the raster questions. So choosing which trees a cutter could reach, and
nudging a spawn off unwalkable ground, both consulted a map with no forest in it, were told everything was
open, and posted a woodcutter inside a wood it could not leave — **236 route requests and no wood.** The
scenario rasterises explicitly before posting anybody.

Both are the same shape as the day's other findings: a question answered by the layer that happened to be
convenient rather than the layer that owns it.

---

## 28. Stage E — the raid, and defence as something nobody has to ask for

### What is scaffolding and what is the game

**Thieves and pressure-that-grows-with-wealth are playtesting scaffolding, not mechanics.** In the real game
the thing that comes over the hill is another player. They exist so that a single-player session has
something that punishes inattention — which §15 argues is the only way the thesis can be *felt* — and they
must never become the thing the design is about.

That distinction has consequences for the code, not just the prose:

- The **raider** lives in `Debug/` beside the scenarios, not in `Simulation/`. It is a scripted adversary of
  the same kind as the pen-escape crowd: a fixture that exercises the world.
- The **pressure schedule** — when a raid comes and how big — is scenario configuration with a slider, not a
  simulation rule. Nothing in the economy may come to depend on raids arriving on a curve.
- **Defence is the opposite.** It is a real mechanic, it belongs in the simulation, and it has to work
  against anything hostile — a scripted thief today, another player's warband later — because it is written
  against *hostility* and not against thieves.

The test of whether the line held: deleting the raider should leave a settlement that still works and a
defence layer with nothing to do, and it should not require touching the economy.

### Defence is not an order

**Civilians defend as an interrupt, always. Protecting your own food should not need asking.** That is the
thing the interrupt layer was invented for, long before it was ever a manual order — an interrupt overrides
the activity and expires, and never touches the assignment, so a villager who fights goes back to the field
afterwards with its shift intact and nothing to re-issue.

The decision is three questions, in order:

**1. What are the resources I want to protect, that I can see?**

Stores with stock in them, heaps on the ground, and bodies carrying a load — anything that would leave with
a raider. *Seen* is load-bearing and it is where the forest's second half arrives: a detection radius per
body, occluded by trees, so an approach through a wood is not noticed until it is close and the gaps a
settlement has cut are the ways in it can watch.

**2. Can I protect them?**

Strength of the assailants against the strength of **the group that can see the same thing.** Not the
individual's own strength, which is the whole point: everybody who can see the threatened granary is weighing
the same sum and reaching the same answer, so they act together with no leader, no rally order and no
formation. Five villagers facing two thieves fight; two villagers facing a warband do not.

**3. Fight, or flee toward the nearest larger group of your own.**

Fleeing *toward* rather than merely away, and toward a group **larger than yours**, is what makes a
settlement ball up under threat without anybody authoring a rally point — and the ball, once formed, may be
strong enough that question 2 answers differently. A retreat that aggregates is a retreat that can turn.

### The hazards, named before they are written

- **Oscillation.** Group composition changes every tick, so a body on the margin will flip between fight
  and flee forever. The answer is a commitment window, the same shape as the interrupt grace that already
  stops a player's orders fighting a unit's own job.
- **Determinism.** "The largest nearby group" and "the nearest one larger than mine" both need a tie-break
  by id, or two runs of the same raid diverge. The fingerprint will say so, several thousand ticks later and
  somewhere unrelated.
- **What "seen" costs.** Line of sight is per hostile rather than per pair: for each hostile, which of mine
  can see it. With a handful of raiders against thirty bodies that is a few hundred ray samples every few
  ticks, which is affordable; per-pair over ten thousand nodes would not be.
- **Death, and what it drops.** The machinery exists and is exercised every run — emigration already
  removes a body and leaves what it carried on the ground, which is exactly what killing a loaded raider
  has to do. §7's whole argument for interception is that killing a loaded raider *returns* the grain
  rather than denying it, and the return trip is the defender's window precisely because the loot is
  recoverable.

### Order of work

1. **Vision.** A detection radius, occluded by forest. Verifiable on its own: a body in the open is seen
   at range, the same body behind trees is not.
2. **Hostility and harm.** Strength on the roster, health on the body, contact does damage, death drops the
   load. Small, and it is what makes the rest measurable.
3. **The three questions**, as one interrupt. The mechanic.
4. **The raider**, in `Debug/`. Walks in at an edge, takes what it can carry from a store, runs for the
   nearest edge. Slower loaded than empty, which is what makes the return trip the window.
5. **Pressure**, on a slider, scenario-side.

And one piece of interface that is not polish: **a key that selects the spare hands.** Answering a raid *is*
reallocating labour under time pressure, and if that takes four drag-selects it is a chore rather than a
decision — which would confound the only thing this stage exists to find out.

## 29. Watching it, and the two things nobody had told the raiders

Four reports off one session, and they sort cleanly into two kinds: things drawn wrong, and things
*owned* wrong. The second kind is the one worth keeping.

### The granary was a monument

It was the pack's `TownCenter_SecondAge_Level3` — at level three, a stone plaza with a fountain, a
reflecting pool and a bronze of two stags. Handsome, and the wrong building entirely: the one place the
whole settlement carries its food to read as a civic ornament, so nothing on screen said where the grain
was. The report was that it "does look rather odd", which was generous.

A **windmill** says grain without a label on it. A **timber barn** says goods. They are different
silhouettes at different heights — 5.4 m against 2.2 m at the same footprint — which is the entire job a
building model has here. The town centre is out of the settlement altogether.

This is the same mistake as the FirstAge houses reading as tables, and it has the same shape: a model
was picked because the pack's naming suggested it, not because of what it looks like from the one camera
angle this game has. **The pack's names describe a tech tree we do not have.** Pick by silhouette.

### Fields were striped because of contrast, not alignment

The plot and the crop line up now, and the block still read as ribbons. The alignment was never the
whole problem:

- The pack's dirt is **0.09** linear and its wheat is **0.38**. A crop that covers its plot in rows puts
  four-to-one contrast between every row and the bare gap beside it.
- Every field carried the same orientation, so twelve of them tiled edge to edge lined their furrows up
  into **continuous forty-metre rows** across the whole block.

Two fixes, one per cause. Neighbouring plots now take a quarter turn on a checkerboard of their own
position, so the rows break at every field boundary while each field stays square to the grid — which is
what makes a block of fields a patchwork rather than a corduroy. And tilled soil is lifted off the pack's
near-black dirt by `LookSettings.SoilBrightness`, a dial because the right amount is a judgement about the
whole frame: too far and the fields stop reading as cut out of the grass.

### "The largest crates in existence"

Exactly right, and the cause is the normalisation rather than the number. Props are baked to a **unit
footprint**, so a model takes its height from its own proportions — and `Crate_Stack2` is 0.12 m across
and 0.25 m tall, better than twice as tall as it is wide. A heap of 120 units asked for nearly three
metres across and therefore got **three metres of crate** standing over the houses.

A single `Crate` is very nearly cubic, so a metre across is a metre tall, and the spread is capped: a
cart's worth is about a metre, and more than that spreads a little and then stops. The general lesson is
that *normalise to a footprint* silently makes height a property of the source model, which is fine for
buildings authored to sit on a tile and wrong for anything that scales with a quantity.

### The two ownership bugs

Both are the recurring finding again — *a question answered by the layer that happened to be convenient
rather than the layer that owns it* — and this makes five.

**The raiders were running the settlement's defence.** The civilian defence is written against
hostility rather than against raiders, which is the line §28 draws and the right one. The price of that
line is that nothing stopped it running on the raiders too: a raider standing over a heap sees loot worth
protecting and villagers reaching for it, answers the three questions, and gets marched somewhere its own
director never sent it. A raid dissolved into a milling crowd that walked its own way home. Watched, that
is precisely the "they surround the raiders as if to harass them" report — except that half the milling
was the raiders doing it to themselves.

The fix is *not* to teach the defence what a raider is. It is `AgentState.Directed`: a body already under
orders from elsewhere does not also make its own decisions. That is a true statement about ownership, and
it stays true when the thing over the hill is another player — *their* people run their own interrupt
layer in their own world, not ours.

**The interrupt only ever started things.** An interrupt that never stands down leaves its last order
standing: a defender marched at a raider, the raider ran, question one stopped finding anything worth
protecting — and the defender kept walking to where the raider *had been*, in a straight line, until it
got there. That is the column of villagers filing across the map after nothing at all. Standing down is
an action and has to be taken, so `Halt` is now half of the defence delegate pair, fired once on the tick
the danger passes and only for a body that actually had a commitment to drop.

### `--raidtest`, and what it measured

Stage E shipped without a test, and every single thing it got wrong was a thing a test would have said
out loud. So the defence now gets the same three questions asked of it, headless, at a raid every 45 s:

| | conservation | settlers lost | raiders alive | longest a raider lived | furthest a settler went |
|---|---|---|---|---|---|
| before | exact | **26 of 26** | 19 | **342 s** | 65 m |
| after | exact | 14 of 26 | 9 | 137 s | 47 m |

The round trip a raider is allowed is `3 × arrival / pace + loot` = 182 s, so 342 s was a raider that had
stopped walking and 137 s is one that went home. Both faults the harness raised on the first run were
real; both are gone.

Two details of the harness that are worth more than the harness:

- **`LiveCount` includes the raiders.** The wipe check read "nobody left alive at all" against the whole
  agent store, so a settlement annihilated while nineteen raiders stood about in it reported a healthy
  population. It is the HUD's "82 people" bug a second time, in a test this time — which is the argument
  for the test.
- **The furthest settler from its granary is the pursuit metric.** A defence that chases to the map edge
  is not a defence, and as a number in the hundreds it is impossible to miss, where on screen you have to
  happen to be looking.

### Combat is its own arc, and here is the question it opens with

Parked deliberately, at the point where the harness stops being about correctness and starts being about
design. What it measured, and what a combat pass has to answer:

**Everybody always stands.** `stood` peaked at 26 out of 26, every raid, without exception. Question two
sums the strength of everyone who can see the threatened thing and could reach it inside the rally
window — 26 villagers at strength 1 against 3 raiders at strength 3 is 26 against 9, so the answer is
always "we can take them". And then they arrive **one at a time**, down whatever lane the crowd allows,
and are killed individually by a raider that only ever fights one of them. Fourteen died proving it.

So: *strength that can arrive is not the same as strength that arrives together*. The sum is a potential
and the fight consumes it serially. That is not a bug in the three questions — the questions are right,
and a defence that gathers before it commits is exactly the emergent behaviour §28 wanted. It is that
there is no notion of a defence being *formed* rather than merely *summed*, and no fight model with any
front to it: two bodies within reach hurt each other, per second, at their strength, which is what makes
"surround them and kill them with body heat" the literal description of the mechanic.

That is where combat begins, and it should begin there rather than with numbers to tune.

## 30. "Am I needed?" is a sharper question than "can we take them?"

The playtest note, verbatim, because it is the design: *"else 20 people will surround 1 guy, push each
other around while others loot freely and these fools get killed."* That is question two's flaw stated
better than §29 stated it, and it comes with the fix in it — *are others in the same set closer and
numerous enough to take them without me, plus some buffer.*

### The change

Question two was **can we take them**: sum the strength of everyone who can see the threatened thing and
reach it inside the rally window, stand if the sum beats the assailants with a margin. Every villager
computes the same sum, so every villager reaches the same answer, so every villager goes. Correct, and
useless — the sum is a property of the settlement, not of the asker.

It is now **am I needed**, which asks about the asker. The same candidate set, ordered by *when each would
arrive*, and a body stands only if the people ahead of it in that queue are not already enough:

```
required = threat × StandMargin
ahead    = strength of candidates arriving strictly before me
total    = strength of all candidates

ahead >= required  →  surplus   — being handled, and by people closer than me. Back to work.
total >= required  →  needed    — stand.
otherwise          →  hopeless  — even everybody is not enough. Run.
```

Three answers where there were two, and the new one is the whole point. Ordered by **seconds** rather than
metres, so a body that is far but quick counts as nearer than one that is close and slow; ties break by id,
or two runs of one raid disagree about who went.

**It is still leaderless, and that property was worth protecting.** The candidate set is an objective fact
— every ally that can see the place and could reach it — so every observer builds the same ordered list and
finds its own name in the same position. Nobody is told to go and nobody is told to stay; twelve people
independently work out that they are the twelve.

### One more thing had to be stored

`AgentState.Guarding`: the place a body has committed to, while its commitment holds. Without it a
settlement's entire strength is counted against every alarm on the map at once, so two raiders at opposite
ends of a village each look answerable by everybody, the same people are notionally sent to both, and
neither is actually answered. A defence that has been raised has to stop being available.

Places within 8 m count as the same alarm, so a granary and the heap beside it are one fight and a
defender committed to either counts toward both.

### Measured, at a raid every 45 s

| | stood, peak | left it to somebody closer | settlers lost | survived |
|---|---|---|---|---|
| can we take them | **26 of 26** | 0, structurally | 14 of 26 | 12 |
| am I needed | 19 across ~3 fights | up to 22 | 10 of 26 | 16 |

`spare` was zero before, and not because nothing was surplus — because there was no such answer to give.
Peak `stood` of 19 is not 19 people on one raider: it is three overlapping raids drawing about six each,
which is what one or two raiders at strength 3 costs at the current ratio. And `fled` rising to 24 is
right, not wrong: a lone villager whose neighbours are all committed elsewhere correctly concludes that
nothing is coming.

### The self-test Stage E should have had

`only as many defend as the fight needs, and the nearest ones go` — one raider at a full granary, twenty
villagers in a line running away from it at a metre and a half apart. It asserts three things, and the
middle one is the new behaviour:

- **Enough go**, covering the assailant with the margin, so the settlement is not sending a defence it
  knows will lose.
- **No more than enough go** — one fewer than the standing set would have been short of the requirement,
  which is the tightest a prefix can be without under-committing.
- **The nearest go** — the standing set is a *prefix* of the line, not an arbitrary subset of it.

Measured: `one raider of strength 3 wants 3.8: 4 of 20 stood for 4, a prefix of the line=True, one fewer
would not do=True, 9 left it to somebody closer`. Four went, nine decided they were not wanted, and the
remaining seven never saw it — the line runs from 4 m to 32 m and sight is 22 m.

That third assertion is the one that would have caught the old rule instantly: under "can we take them"
the standing set was the whole line.

### Still parked for the combat arc

This fixes who commits. It does not give the fight a front — bodies within reach still hurt each other per
second with no facing and no engagement limit, so what a committed defence *does* on arrival is unchanged.
The two remaining questions belong to that arc: how many bodies can actually be brought to bear on one
assailant at once, and what a defence being *formed* means beyond being *chosen*.

## 31. Hands first

Another playtest note, and another one that is about the decision rather than the fight: *my people who
have resources should try to place them in a resource point around the encounter before joining in, or
drop resources where they were and then join the fight — rushing to fight while carrying resources is a
sure fire way to get villagers killed, resources stolen and no raiders killed.*

Exactly right, and the mechanism is worth stating: **the load is the prize.** A villager who runs at a
raider with forty grain on its back is carrying the raider's prize into its reach. It loses the fight, the
goods change hands on the spot without the raider walking anywhere, and the raid is paid for by the
defence that came to stop it.

### The order of preference

A defender that has answered "needed" and has its hands full does not go to the fight. It deals with the
load first:

1. **A store away from the trouble**, if one is within a settlement's width — and *away* is load-bearing:
   a store inside the reach of whatever is causing this is not somewhere to stow anything, because getting
   there means walking the load into the fight.
2. **The ground where it stands**, otherwise, which is instant. A heap in the open can be looted and that
   is the honest cost of the choice; a heap is at least stationary, and nobody has to die holding it.

Handing a load into a store is a **physical act** here rather than a delivery. Deliveries belong to an
assignment's legs, and this is an interrupt — an interrupt that rewrote an assignment in order to borrow
the haul machinery would break the jobs model's one prohibition. So the units move, the shift is left
exactly as it was, and the villager goes back to the same field afterwards.

### Two things this got wrong first, both instructive

**An errand has to outlive the reason for it.** The first version was driven from the defence's own
decision each tick: if the body could see a threatened thing, ask the world to stow. So a body that
carried its load *out of sight of the raid* stopped being asked to finish — and walked to the depot, and
stood in the yard holding forty grain for the rest of the session. `AgentState.PuttingDown` plus
`StowInto` now hold the errand, and `SimulationWorld.AdvanceStowing` runs it to completion whether or not
the raid is still there. Every way it can fail ends with the load on the ground rather than on the body:
the store was destroyed, or filled while the load was walking to it, or took only part of what was
offered.

A flag beside the id rather than a sentinel in it, because `default(NodeId)` is zero and node zero is a
real node — usually the granary. That trap has been walked into once already, when a default assignment
pointed every idle body at node zero.

**The bound was a rally window, and that had the priorities backwards.** "Don't spend the whole raid
walking" sounds right and made a depot nineteen metres away too far to bother with, so the villager put
its grain on the ground *beside the raid* rather than carrying it to a store. The load's safety outranks
this body's arrival, and it can afford to, precisely because the surplus rule means somebody closer is
already on their way. `HavenMetres` is a settlement's width: past that you are not stowing a load, you
are leaving with it.

### And one thing the measurement caught

Adding the stow made the settlement do **worse** — settlers lost went 10 of 26 to 14 of 26. Not variance;
the simulation is deterministic. The cause: a body walking a load to a store still sorts by where it is
*standing*, so the people behind it in the arrival queue read it as covering the fight while it was in
fact off delivering grain. The defence arrived two bodies short of what it had committed to.

So `PuttingDown` now excludes an ally from the muster — hands full is not available strength, on the same
principle as `Guarding`: committed elsewhere is not available. Never the asker, though, or a laden
villager reads the fight as hopeless and runs from something it could win the moment its hands were free.

| at a raid every 45 s | peak stood | putting loads down | settlers lost | furthest a settler went |
|---|---|---|---|---|
| no stowing | 19 | — | 10 of 26 | 40 m |
| stowing, counted as available | 16 | 2 | **14 of 26** | 23 m |
| stowing, and busy is busy | 14 | 3 | 12 of 26 | 23 m |

Two of the fatalities did not come back, and that is honest rather than tuned: stowing delays arrival, and
delayed arrival is worse in a fight that consumes its defenders **serially** — which is §29's parked
finding making itself felt. The benefit shows in the last column instead. Nobody hauls goods toward a raid
any more, and the furthest anyone strays from home fell from 40 m to 23 m.

### The self-test

`a defender puts its load down before it joins` pins both branches, and the second is the interesting one:

> `40 grain and a raid at the granary: stowed=True, into the depot 40, never carried into reach=True,
> shift intact=True; with nowhere safe, 40 left on the ground; drift 0/0`

Never within a raider's reach with its hands still full, checked every tick rather than at the end. The
shift is unchanged. And with the granary as the only store — so the only store is the one being raided —
all forty units are on the ground and the ledger still balances, which is the assertion that matters: what
must never happen is a load quietly ceasing to exist because the code found nowhere tidy to put it.

## 32. The handover was a tenth of the game spent standing still

*Putting loads down in this context and while storing/hauling/dropping is too slow — if there's a timer,
snap it to 0 or almost 0 for a while and try again.*

`EconomySystem.HandoverSeconds`, four seconds, now a quarter of one. The argument for four is worth
keeping written down because it was a real argument and it lost:

> a hauling network with instant transfer has no reason to want more haulers than routes, and the queue at
> a busy granary is one of the things the congestion field exists to price. Four seconds against a leg of
> sixty is a tenth of the round trip.

All true. What beats it is what a settlement *looks like*. Every transfer in the game pays this, several
times per round trip — at a farm, at a tree, at a granary, at a depot, at a building site — so watching
the settlement work meant watching people stand still at exactly the moments they were supposed to be
getting something done. **A tenth of a round trip spent motionless is a tenth of the game**, and a game
about fetching and carrying cannot spend a tenth of itself on people not moving.

Not exactly zero: a tick is 33 ms, and a transfer that completes on the tick of arrival makes the arrival
unobservable to anything reading a body's state. A quarter-second is under the eye's threshold and still a
distinct step in the trace. On a slider, so the queues can be brought back.

### `SettlementSettings`

Five dials, same proxy pattern as `WoodlandSettings` — the constants stay where they are used and this
exposes them, so a headless run reads the same numbers a watched one does. Handover, defence margin, rally
window, threat reach, and how far a body will carry a load to safety. None is derivable from another, and
every one changes how the game *feels* rather than only how it performs, which is the test for earning a
slider.

### What it cost the economy: nothing measurable

A year, before and after, at the season boundaries: **2,647 grain and 1,126 wood at midsummer against
2,647 and 1,125; 306 grain at the harvest crunch against 307.** The settlement's throughput is set by
field labour and walking, not by dwell — this scenario's trees are at hand, so there is no hauling to speed
up, and the work handover at the granary is a small share of a 45-second shift. The hauler-demand argument
would show up in a scenario with a receding wood line, and it is worth re-measuring there before deciding
the slider's home.

### And it broke a test, for the second time, the same way

`a cart is a job a villager takes, and pays for` went red: `route ran 20 legs, moved 400 grain, still
standing=False`. Not a regression — the route drained its source inside the test's 240-second window and
ended, **correctly**, because a standing route that has run its source dry is a route ending as designed.

The comment above that fixture already recorded this exact lesson from its first version, which had 120
grain and measured an ended route. 400 was comfortable at a four-second handover because the dwell ate
half the window; a quarter-second let the same cart move the lot. So the fixture is sized ten times over
now, with the reason written down: **a fixture whose margin depends on how long a transfer takes is
measuring the transfer, not the cart.** 22 legs, 440 grain, still standing.

## 33. Looting is indoors, and the reason nothing ever died

Two reports: *raiders can loot without even being near the granary*, and *they don't actually die even
surrounded*. Both true. The first was one bug; the second was **four**, in a chain, each of which had to be
fixed before the next became visible.

### Looting happens inside the building

Entering the looting mood required being at the store. *Taking the grain did not.* So a raider shoved out
of the yard by the crowd that came to stop it went on emptying the granary from wherever it had been
pushed to.

The first fix was to re-check the reach every tick — and it was the wrong fix, because it made shoving a
raider off a doorway into an accidental defence mechanic whose outcome depended on crowd physics. The
right shape, from the playtest note: **the raider goes inside.** For its rummaging window it cannot be
shoved and cannot be fought; then it pops out with everything it can carry and makes a break for it, and
the people who gathered while it was in there get their chance.

`AgentState.Sheltered` is reversible absence, which is a different thing from a despawn: the four collider
proxies are *disabled* rather than removed (`ColliderWorld.SetEnabled`), so the body comes back as itself
with every handle still valid. It stays in the world's roster the whole time, and **that is deliberate** —
a sheltered raider is still in the hostiles list, so the alarm holds while the granary is being robbed and
the defence gathers at the door it went in by. Excluding it there would have looked like tidiness and
would have meant a settlement that goes back to work while it is being burgled.

The grain is taken on the way *out*, not on the way in, so while a thief is in there the units are simply
still in the granary — no term in the ledger for goods in somebody's pockets inside a building, and a
store destroyed mid-raid cannot vanish anything.

**Rummaging time is per resource**, because it should be: grain is stored loose and a thief has to fill a
sack, timber is stacked and you pick it up and go. `WoodLootShare` at 0.4 makes a lumber camp a
qualitatively different target from a granary — it is robbed before anyone can gather, where a granary
gives you a window — which is a real asymmetry between two buildings for the price of one number.

### The chain of four, and why each hid the next

**1. Nobody pursued at all.** §29's stand-down was correct and complete: once a thief was twelve metres
from the granary it threatened nothing, so the defence went back to work — and the loot always got home.
The missing case is that question one asks what would *leave with a raider*, and **the raider already
carrying it is the most literal answer there is.** A loaded hostile is now a thing worth protecting, and
pursuit is leashed by sight rather than by a distance anybody chose.

**2. A loaded raider was as fast as an empty one.** 2.05 m/s in and 2.05 m/s out, against a villager's
1.79 — so no defence could ever catch one. `RaidDirector`'s own class comment had been claiming otherwise
for two sessions: *"slower loaded than empty, which is the whole of §7's argument for interception."* At
`LadenShare` 0.7 it makes 1.44 m/s and the walk home is a window that closes on it.

**3. Pursuit was by stale waypoint.** Defenders re-aimed every three seconds — which is the commitment
window, and exactly right for *deciding*. It meant each one was always walking to where the raider had
been: four metres of staleness against a closing speed of a third of a metre a second, so the gap could
never be shut. A column of villagers ninety metres from home, still following. **Deciding every three
seconds and steering every tick are different jobs**, and the movement layer already owns the second one —
so a defender that stands is now handed the *body* (`QueueChase`) rather than the ground.

**4. And then, with all of that fixed, still nothing died.** A chasing body settles at **0.95 m**. Two
0.37 m bodies reached each other at **0.81 m**. Every defender in the game came to a halt a hand's breadth
outside striking distance and stood there for the rest of the raid.

That is this project's recurring finding for the sixth time — *two layers each with their own definition of
"next to", and the one that happened to be convenient winning* — and it is the purest example of it yet,
because both numbers were right on their own. `AgentDefaults.ChaseStopMetres` is named now, and the harm
reach is written as a **floor** rather than by nudging `ReachShare` until it happened to clear: a body's
reach is its own business, *except* that it can never be shorter than the distance at which the movement
layer stops bringing bodies together.

### Measured, one fix at a time, at a raid every 45 s

| | raiders killed | settlers lost | furthest a settler went | grain carried off |
|---|---|---|---|---|
| as reported | 0 of 24 | 12 | 23 m | 600 |
| + loaded raiders are slow | 0 of 24 | 13 | 24 m | 600 |
| + a loaded raider is worth chasing | 0 of 24 | 10 | **109 m** | 600 |
| + chase the body, not the ground | 0 of 24 | 3 | 90 m | 600 |
| + reach cannot be shorter than the stop | **1** | 13 | 23 m | 560 |

Every row is informative. Chasing without tracking produced the hundred-metre column; tracking without
reach produced a defence that escorted raiders politely off the map and lost only three people doing it;
reach turned it into an actual fight, which cost thirteen.

At the **shipped** cadence of one raid every 240 s the settlement copes: three raids, one raider killed,
three settlers lost, 23 people still alive, and nobody more than 22 m from home.

### What is left is balance, and it is the roster's problem

A villager is strength 1 and health 20; a raider is strength 3 and health 40. So a villager dies in under
seven seconds and killing a raider takes forty villager-seconds of contact — six farmhands trade one of
their own for one thief, and that is the *intended* ordering, written down in `UnitType`: **a villager
fights badly on purpose.**

Which means the thing the numbers are now asking for is not a tuning pass, it is `UnitType.Soldier` —
strength 3, health 45, already on the roster and with no way to make one. That is the combat arc's
business and it is the right place for it to start: the mechanics are all working end to end now, and what
they reveal is a missing unit rather than a wrong number.

## 34. The four loose ends, and a gate to run before anything lands

Small things, all of them known and none of them blocking, closed out together because the list itself
had started to be the problem.

**Numbers are printed the same everywhere.** The forest line read `8,65,710 wood` — correct for this
machine's digit grouping and useless in a report you compare against yesterday's. `InvariantCulture` on
the way in to `Main`, which also means two machines' traces diff cleanly. That last part matters more than
the cosmetics for a project whose main tool is a determinism check.

**A felled tree leaves a stump.** `Resource_Tree_Group_Cut` had been loaded and never drawn, and the gap
it left was bigger than it sounds: cutting is otherwise *invisible in hindsight*. A tree shrinks while it
is felled and then simply is not there, so a decade of work on the wood line left the ground looking as
though nobody had ever been — and the receding wood line is Stage B's entire interface.

A stump cannot be a node. A felled tree's node is removed outright because nothing in the simulation has
any further use for it, and keeping thousands of dead ones alive would put them in the fingerprint, the
save file and every iteration over the economy, forever, to be looked at. So it is a **ring of the last
384 felling sites** — about a decade of a settlement's cutting — argued away in the census rather than
fingerprinted, on the grounds that no decision anywhere reads it. Deliberately not saved either: a loaded
world has no stumps until something is felled. Both are honest for a thing that decides nothing, and both
are cheaper than the alternative by a wide margin.

**Tab selects the spare hands.** The panel had been saying "5 SPARE" with no way to get at them, which is
a strange thing to have shipped: spare labour is what a new field is staffed from, what a cart is bought
for and what a building site is finished by, so telling somebody they have five and making them hunt for
which five is most of the friction in playing this. Idle is defined in exactly the same terms the HUD
counts it in — no assignment, not mid-interrupt — because two definitions of spare would drift and the
number on screen is the promise the key has to keep. Tab because every letter on the board is taken twice
over, which is its own signal about the interface.

**`tools/gate-rts-game.sh`** runs the three headless checks that have to be green. They cover different
failure modes and none subsumes another:

| | catches |
|---|---|
| `--selftest` | a broken rule — 82 assertions, seconds each |
| `--settlement --years 1` | an economy that no longer feeds itself, which no assertion can, because "starves in the fourth season" is a property of a year rather than of a tick |
| `--raidtest` | a defence that stops defending |

Conservation is checked every tick of all three, so a unit of grain going missing fails whichever run was
unlucky enough to be holding it. `--raidtest` in particular existed but was only ever run when somebody
remembered to, which is the same as not existing — Stage E's whole lesson, one level up.

The one item left open is the one that is not mine to close: the `LookSettings` sliders still hold my
guesses, and settling them is a judgement about how the game should look.

## 35. Session 7 goes last, and why that is not a slip

§15 recommended `6 → 6.5 → 7 → 8 → 9`, with the soak harness third. It goes **last** now, and the argument
for moving it is stronger than the argument that put it there:

> refine combat, refine building/enemy placement/maps, at least get the systems in place and behaving —
> then sketching 7 out makes any sense, else it's going to be a plain rewrite as soon as the other systems
> flow in.

§13 paired the strategic layer and the soak harness because "they are the same artefact". This applies the
same reasoning one step further out. **A soak harness is shaped by what it soaks**: it decides what to
assert, what counts as a regression, what a run is *for*. Written against half the systems it is written
twice, and the second writing throws the first away.

**The risk being accepted is the one §15 kept because it was real** — an economic regression going a
session unnoticed. And the mitigation §15 named in the same paragraph, "the one-year gate is four minutes
and can land well before the harness proper", is `tools/gate-rts-game.sh` as of §34. So the exposure is a
regression that survives 82 assertions, a simulated year and a settlement under raid, which is a narrower
gap than the one this trade is closing.

### The order, then

**1. Combat, and it starts with the front rather than the numbers.** §29 said to begin from
formation-versus-sum and not from balance dials, and §33 ended by pointing at `UnitType.Soldier`. Those are
in the wrong order: adding soldiers to a model where twelve bodies can all reach one assailant just puts
more bodies in the pile. The engagement limit comes first, because it is what makes *quality* matter
instead of *count* — and therefore what makes a soldier worth paying for.

- *An engagement limit that is not a dial.* Only so many bodies physically fit around one, and that is a
  fact about radii rather than a preference: at contact distance, `2π(R + r) / 2r` of them, which for two
  0.37 m bodies is six. Twelve villagers on one raider should be six villagers on one raider and six
  standing behind them.
- *Then the soldier*, which needs somewhere to come from, a cost, and the appetite it already has —
  1.35 against a villager's 1. That is the tension the whole design has been waiting for: **soldiers eat
  the grain that would otherwise have become people.**
- *Then whether a fight needs facing at all.* Possibly not, once it has a front.

**2. Placement, enemies and maps** — the three are one problem seen from three sides.

- Building placement has been flagged twice by eye and is still a 1.5 m grid with a key per kind.
- Raids appear on a ride at a fixed radius from the middle of the map, which is scaffolding pretending to
  be geography. A raid should come *from* somewhere that exists.
- And the map itself: the settlement sits on a fixed arc, the wood is concentric bands. Terrain that
  decides something — a chokepoint worth holding, a wood line that is not a ring — is what turns
  "approaches are knowable" from a sentence in §33 into a thing you can act on.

**3. Session 7's harness**, written against systems that have stopped moving.

### The front, and what measuring it found instead

The engagement limit is in, and it is a derivation rather than a dial. A body of radius `a` in contact
with one of radius `t` stands on a ring of radius `t + a` and takes up `2·asin(a / (t + a))` of it; sum
those until the ring is full and you have how many can physically get at it. **For two 0.37 m bodies,
exactly six.** Mixed radii fall out for free — a wide body takes more of the ring and so crowds out more
of its own side, and it also reaches further, both of which a flat count would have to be told.

Asserted, and the assertion is a *ceiling* rather than an equality, which is the interesting part: twelve
villagers packed onto one raider took **3.2 health in a second**, against 6 for the six that fit and 12
for all twelve. Not equality, because the twelve are also shoving each other and several drift out of
reach — asserting the arithmetic sum would be asserting that depenetration does nothing.

**And then the raid scenario said `most shut out of a fight they had reached: 0`.** The cap never binds.
Over eight raids, not once did a seventh body reach a raider that six were already on.

That is worth more than the feature. **The mob was never mechanically a mob.** "Twelve people surround one
guy and push each other around" was an accurate description of what it *looked* like and not of what was
happening: avoidance already held the crowd at a spacing where only three or four were ever in contact, so
the fight was being decided by a handful of bodies while the rest milled. The front is correct, it is
tested, and it will bind the moment bodies are wider or a formation packs them deliberately — but it is
not today's limiter.

Today's limiter is the opposite of the one expected: **not too many piling in, but too few arriving.**
Sixteen commit and three fight. Which sharpens the case for the soldier rather than weakening it — if only
three or four can ever be in contact, then three or four *good* bodies is the entire answer, and quality is
not an improvement on numbers, it is the only lever there is.

## 36. A ledger for the raid, and three things it immediately found

Asked for, and it earned itself inside one run:

> we need to distinctly count how many were triggered, how many engaged with how many, how many were
> stopped, for how long, how many on each side were killed, how much was stolen, how much was effectively
> lost considering simple projected output × time interrupted for.

**Three sessions of watching raids produced three wrong conclusions.** "They surround them and push each
other around" was a crowd held apart by avoidance with three bodies in contact. "They don't die even
surrounded" was a chase settling 0.14 m outside striking distance. Watching tells you something is wrong;
only counting tells you what. So `RaidLedger` counts the whole transaction, on both sides — observational,
in `Debug/`, writing nothing to the world.

The term nobody counts is the interesting one. **What the interruption cost**, because a raid that takes
nothing and stops twenty people working for a minute has still done damage. Grain here is per *farm* per
year, not per hand — hands only decide whether a crop's three windows are met — so "hands × a rate" is
the wrong arithmetic. What is countable is **labour-seconds withheld**, projected at the margin: twelve
farms yield `GrainPerFarmPerYear` each for about a hand-year of attention each, so a labour-second is
worth `700 / year` grain. It over-states whenever the window would have been met anyway, and that is
stated rather than corrected — the over-statement is a ceiling, which is the useful direction for a cost.

### What it found, first run

```
raids 8, raiders sent 24, of whom 14 got home and 10 did not
alarms raised 220, most answering at once 26, most ever in contact 12 — 197 body-seconds of fighting
killed: 1 of theirs, 8 of ours (8.0 of ours per raider)
stock: 560 carried off the map, 117 dropped and recoverable
interruption: 342 s with something on the map, 256 s with the alarm up,
             3456 labour-seconds withheld — about 448 grain of work not done
```

**The interruption cost is the same size as the theft.** 448 grain of work not done against 560 carried
off, and nothing in the game had been saying so. That reframes what a raid *is*: not a tax on your stores
but a tax on your year, and the second is the one a player would never have deduced.

**Eight of ours per raider.** A defence was a queue of people taking turns to lose.

### The three fixes it made obvious

**A surplus body tries the next alarm before going back to work.** From the note — *if only 3-4 can
surround this one guy, better surround the other(s), or not leave my work at all.* One threat and one
answer was the shape, and it wasted the surplus: being unneeded at the nearest alarm is not the same as
being unneeded. So question one is now asked repeatedly, nearest first, until one of them wants this body;
only when every alarm in sight is covered is the answer "back to work". Bodies ever in contact went 12 → 18.

**A raider is priced as a thief, not as a soldier.** Health 40 → 18, set by the fight it should lose:
three or four villagers kill it in four or five seconds and it takes rather less than one of them with it;
two trade one for one; one dies. *Numbers work, but only just, and only together.* Strength stays at 3,
because a raider losing to four farmhands and killing any one it catches alone are both wanted, and
strength carries the second. A villager still fights badly on purpose — measured against the soldier at
health 45, not against a thief.

**Hold the decision, not the target.** This one cost fourteen of twenty-four raiders their escape and is
the recurring finding again in a new costume. A body committed to the granary while a thief was *inside*
it kept that commitment when the thief came out and ran, because "still standing" looked like nothing had
changed — same answer, resolve unexpired, no new order. It walked to an empty doorway while the loot went
over the hill. **Whether to fight is worth holding for three seconds; what to fight is not.**

### Measured, one at a time

| | got home | raiders killed | ours per raider | stolen | furthest a settler went |
|---|---|---|---|---|---|
| as instrumented | 14 of 24 | 1 | **8.0** | 560 | 81 m |
| + a thief's health | 14 of 24 | 3 | 3.0 | 560 | 111 m |
| + hold the decision, not the target | **11 of 24** | 4 | **2.5** | **440** | **48 m** |

Two and a half farmhands per thief is a bad trade and no longer an absurd one, and it is bad in the
direction the design wants: a village of farmhands can stop a raid and should not enjoy it. That is the
argument for the soldier stated as a number rather than as a preference.

## 37. A raider's health is not the dial, and the sweep is how that was settled

Asked for a test at 15 or 16 against the 18 that had just landed. One run each said 15 was *worse* than 18
— 3.0 of ours per raider against 2.5 — and 16 worse still at 3.8. Non-monotonic, which is the signature of
a chaotic process being sampled once: change a number and a different body dies first, and everything after
that reroutes.

So the seed became an argument (`--raidtest --health H --seed S`), and the seed is the only thing that
varies — same map, same settlement, same schedule. Five seeds per value, fifteen runs:

| raider health | got home /24 | their dead | our dead | ours per raider | stolen | grain forgone | furthest a settler went |
|---|---|---|---|---|---|---|---|
| 15 | 12.2 ± 1.6 | 3.4 ± 1.5 | 9.8 ± 2.9 | 3.24 ± 1.50 | 488 ± 66 | 431 ± 30 | 87 ± 24 m |
| 16 | 12.0 ± 1.7 | 3.4 ± 2.2 | 10.0 ± 3.9 | 2.44 ± 0.53 | 480 ± 69 | 405 ± 52 | 58 ± 34 m |
| 18 | 11.8 ± 1.8 | 3.2 ± 1.8 | 12.0 ± 5.0 | 3.25 ± 1.31 | 472 ± 72 | 368 ± 59 | 48 ± 15 m |

**Every difference is inside its own spread.** A seventeen per cent cut in a raider's durability changes
nothing measurable about how many die, how many get home, or how much they take. The three single runs that
started this were three draws from one distribution.

That is worth more than a tuned number, because it says **durability is not the binding constraint** — and
the correct response to a dial that does nothing is to find the one that does rather than to keep turning it.

### Which meant measuring the thing that had been assumed twice

§35 concluded "sixteen commit and three fight — the limiter is arrival, not crowding", from *instantaneous
peaks*. A peak cannot tell three bodies fighting for a long time from thirty fighting for an instant, so the
ledger now counts each body once, over the whole run:

```
of ours, 26 ever left their work for a fight and 23 were ever in one
                        — 88% of those who went actually got there
```

Across five seeds: 69–92%. **Arrival is not the limiter either, and §35 was wrong about it** — that
conclusion was drawn from a peak and should not have been drawn at all.

### What it actually is

```
387 body-seconds of harm dealt in all, of which 272 went into something that died
                        — the rest into bodies that walked away
```

Thirty per cent of all the harm in a raid goes into bodies that survive. Twenty-four raiders take damage and
four die: the rest leave **wounded and alive**, carrying grain, and heal by virtue of the next raid being a
fresh party. Damage is being *spread* across many targets and parked just under the threshold on most of
them — which is precisely why health barely matters, because moving a threshold that nothing is clustered
against moves nothing.

So the next lever is **concentration**, not durability, arrival, or the front:

- A defender charges the hostile nearest the thing it is guarding, so as a raid scatters the defence
  scatters with it, four ways at once.
- Nothing prefers a target that is already engaged, and nothing prefers one that is already hurt.
- A raider that gets away at one health is a raid that succeeded; a raid that loses two bodies and takes
  half as much is a different game.

Which is the same shape as §30's finding one level down. §30 was about *who commits*; this is about **what
they commit to**, and the answer "whatever is nearest" is the convenient layer answering again.

## 38. Concentration: one hypothesis rejected, one kept

§37 found that a third of all the harm in a raid goes into bodies that walk away wounded, and named
concentration as the lever. Two ways of getting it were tried. **The elegant one lost.**

### Global triage, rejected on measurement

Score every fight by how soon it would be over if I joined it — `arrival + health / strength already on
it` — so a wounded thief with three people on it beats a fresh one standing nearer. No focus flag, no new
state, every defender scoring the same fights from the same public facts, converging without being told to.
It has the same shape as question two's leaderlessness and it reads beautifully.

It was worse on every metric over ten runs: their dead 3.2 → 2.4, stolen 472 → 504, harm wasted up by half
again. Two reasons, both obvious afterwards:

- **It sends people walking instead of fighting.** A distant engaged fight outscores a near fresh one, so
  the defence spends its time in transit. `furthest a settler went` went 48 m → 76 m.
- **It thrashes.** Joining a target makes that target more attractive, which re-scores it for *everybody*
  at once, so the whole defence oscillates between fights. A rule whose inputs change because of how
  people respond to the rule is a feedback loop, not a policy.

The first version also priced a sheltered raider at "settles instantly", which made a granary doorway the
most valuable place on the map to everyone simultaneously — the exact behaviour the rule was meant to end.
Fixing that did not save it.

### Concentration by commitment, kept

`AgentState.Quarry`: stay on the body you are already fighting while it lives and is reachable, and
otherwise take the nearest. One identity comparison, and **it cannot thrash, because nothing about my
target changes when somebody else picks theirs.**

| | got home /24 | their dead | stolen | % harm wasted | ours per raider |
|---|---|---|---|---|---|
| nearest, no memory | 11.8 ± 1.8 | 3.2 ± 1.8 | 472 ± 72 | — | 3.25 ± 1.31 |
| global triage *(rejected)* | 12.6 ± 0.9 | 2.4 ± 0.9 | 504 ± 36 | 46.5 | 4.20 ± 0.77 |
| **sticky quarry** | **10.6 ± 0.5** | **4.4 ± 0.5** | **424 ± 22** | **28.4** | 3.07 ± 0.46 |

Harm wasted from 46% to 28%; a third more raiders killed; a tenth less carried off. And — worth as much
as the means — **every spread collapsed.** Raiders killed went from ±1.8 to ±0.5, stolen from ±72 to ±22.
A mechanism that works makes outcomes consistent; the wide spreads were the defence being lucky or unlucky
rather than competent, which is also why the health sweep in §37 could not measure anything.

### The method note, because it is the transferable part

§37's health sweep and §38's two hypotheses are the same lesson twice. **A single run of a chaotic
simulation cannot distinguish a mechanism from a coin flip**, and the tell is the spread rather than the
mean: 3.2 ± 1.8 raiders killed is not a measurement of anything. Five seeds per variant, and the seed the
only thing varying, is what made both answers legible — including the one that said "this dial does
nothing" and the one that said "this elegant idea is worse".

`--raidtest --seed S` exists for that, and the next balance question should start there rather than end
there.

## 39. The granary has to fill its own ground, and being surrounded still costs nothing

### The granary was the third wrong answer, and the reason is reusable

Reported: the granary is too big and does not match the area of its visual model. Exactly right, and the
cause is worth stating as a rule. **A granary is five placement cells — 7.5 m of ground — and a windmill's
bounding box is mostly sails.** Normalised to that footprint it drew a slim tower in the middle of a large
square, so the ground the player cannot walk on was much larger than the thing they could see.

Three models, three lessons, each different:

- **Town centre**: reads as a monument, so nothing on screen said where the grain was.
- **Windmill**: reads as grain, and *fails the footprint test* — its silhouette is thin and its box is not.
- **Barn**: fills its box, and says goods.

So both stores are barns now, at the two sizes the game actually has — 7.5 m and 4.5 m — same family, with
size as the read. Which is also true: they are both stores and one is bigger. The rule: **a model has to
fill the ground it occupies, or the footprint is a lie the player walks into.**

### Being surrounded still costs nothing, and two attempts to fix it both made things worse

Reported, and it is the sharpest description of the problem yet: *the enemies slid in, got surrounded, got
wounded a little, reached the granary, picked the loot and more or less walked away while my people kept
trying to do some retaliation but couldn't turn that into anything.*

The diagnosis is right. Contact feeds the harm pass and **nothing else** — the avoidance solve treats an
enemy exactly like a neighbour to slide past, so a ring of villagers is scenery. Two ways of giving it
weight were tried, both derived from the ring the engagement limit already computes, and **both measured
worse than doing nothing:**

| | got home /24 | their dead | stolen | recovered | furthest | % harm wasted |
|---|---|---|---|---|---|---|
| **sticky quarry** (§38) | **10.6 ± 0.5** | **4.4 ± 0.5** | **424 ± 22** | **202 ± 13** | 52 ± 28 m | **28.4** |
| + held all ways | 11.8 ± 2.3 | 3.2 ± 2.3 | 472 ± 91 | 149 ± 74 | 65 ± 42 m | 37.5 |
| + held only when leaving | 12.4 ± 0.5 | 3.0 ± 1.2 | 496 ± 22 | 144 ± 54 | 81 ± 27 m | 34.9 |

**Held all ways** is symmetric and that is its flaw: a defender in contact with the thief it is chasing is
slowed *by the very contact it wanted*, falls out of reach, and the fight oscillates.

**Held only when leaving** fixes that asymmetry — push into a fight freely, cannot slide out of one, which
is both honest physics and the thing that should make a ring worth forming. It is still worse. And the
spreads went back up, which by §38's own test means the defence went back to being lucky rather than
competent.

Both are backed out. The likely cause is that this is the **locomotion layer's** business and a speed
multiplier bolted onto the preferred velocity fights it: ORCA is reciprocal, so a body that suddenly wants
to move slowly changes every neighbour's velocity obstacle, and the crowd resolves by flowing *around* the
held body rather than pressing on it. That is the same class of mistake as §27's exact distance transform —
a correct-sounding change to a delicate layer, measured, and reverted.

**So the report stands unfixed and it is now the clearest open question in the arc:** a body should not be
able to walk through people who are fighting it, and the mechanism belongs inside the avoidance solve —
where an enemy is not a neighbour to be politely avoided — rather than in a multiplier outside it.

## 40. The instrument was broken, and §38's conclusion with it

Taking the locomotion layer up in earnest started with an audit and five changes. **All five measured
worse. Then the sixth measurement showed that none of them did.**

### The audit, which is worth keeping

Two structural facts, both established by reading rather than guessing:

- **Nothing in the movement layer distinguishes an enemy from a stranger.** Every `RelationMask` in it is
  `All`, and in 829 lines of the velocity solver the only mention of a faction is `IsShovableAlly`.
- **The solve is not reciprocal-split.** It goes front-to-back and the lower-priority body takes *full*
  responsibility for the pair, with priority among movers running on distance still to travel. So a thief
  a metre from a defender that is itself a metre from its goal is the body doing all the avoiding — the
  layer hands the raider sole charge of not being caught, and it is good at it.

Those are real and they still want addressing. What follows is why nothing could be concluded about them.

### Five plausible mechanisms, all rejected

| | their dead | stolen |
|---|---|---|
| sticky quarry, seeds 11–55 *(§38 baseline)* | 4.4 ± 0.5 | 424 ± 22 |
| + being surrounded holds you, all directions | 3.2 ± 2.3 | 472 ± 91 |
| + held only when leaving | 3.0 ± 1.2 | 496 ± 22 |
| + a chase closes all the way instead of halting at 0.95 m | 3.0 ± 1.0 | 480 ± 40 |
| + hostiles excluded from the velocity solve entirely | 3.0 ± 1.6 | 480 ± 63 |

Five changes, five regressions, all to about the same place. That similarity is the tell, and it took too
long to notice: **a set of unrelated changes cannot all cost the same amount.** So the last measurement was
not a sixth change. It was the §38 baseline again, on five *fresh* seeds:

| | got home /24 | their dead | stolen | recovered |
|---|---|---|---|---|
| sticky quarry, seeds 11–55 | 10.6 ± 0.5 | **4.4 ± 0.5** | **424 ± 22** | 202 ± 13 |
| sticky quarry, seeds 66–111 | 12.6 ± 0.5 | **2.4 ± 0.5** | **504 ± 22** | 118 ± 50 |
| pooled, n = 10 | 11.6 ± 1.2 | 3.4 ± 1.2 | 464 ± 47 | 160 ± 56 |

**The same configuration, unchanged, produces 4.4 or 2.4 raiders killed depending on which five seeds you
draw.** So §38's headline is void: sticky quarry was never measured better than nearest-target — it drew a
lucky block. And the five "regressions" were regressions to the true mean, not damage. Four of the five may
have been fine, or better; nothing here can say.

### What was wrong with the instrument, precisely

**Tight spreads inside a block are not evidence of anything, and §38 read them as evidence of competence.**
Within a block of five seeds the spread is ±0.5; across blocks it is ±1.2. The seeds are *not independent
samples*: they all share one map, one settlement layout and one raid schedule, and vary only the bearings a
raid arrives on. Five draws from a narrow correlated slice look precise and are not accurate.

The deeper problem is that `--raidtest` measures **everything at once** — economy, jobs, hauling, threat,
pathing, and a scripted director — over six minutes. Anything that perturbs timing reroutes the whole run.
It is a fine gate for "did something break" and it is the wrong instrument for "is this locomotion change
an improvement", which is what it has been used for all session.

### What the arc actually needs first

Not a sixth hypothesis. **An instrument that isolates one mechanism**, in the style the movement
benchmarks already established for crowds — `pen escape`, `one-cell gate`, reporting walked/optimal,
dead-stops, mean clearance. The combat and pursuit equivalents want the same treatment:

- *N defenders against one raider, on open ground.* Time to kill, body-seconds of contact, contact duty
  cycle, and whether the raider gets clear. No economy, no director, no fields.
- *One pursuer against one quarry at a speed ratio.* Distance over time, and how much of the chase is
  spent inside reach — which is the number every one of this session's five hypotheses was really about,
  and not one of them measured it.
- *A body trying to cross a ring of enemies.* Does it get through, how long does it take, and how far does
  it deviate. That is the reported bug, stated as a measurement.

Each is seconds to run, deterministic, and has one thing in it. And each can be swept across many
configurations rather than five, because nothing in it costs six minutes.

The two audit findings above are the first candidates to put through it. Until then the honest position is
that **sticky quarry is unproven rather than good**, and it stays only because it is cheap, principled and
has not been shown to hurt.

## 41. `--fightbench`, and pursuit has never worked

The instrument §40 called for. Three scenarios, one mechanism each, open ground, no economy, seconds to
run: **a ring** (N bodies on one that cannot run), **a chase** (one pursuer, one quarry, a speed ratio), and
**a cordon** (one body crossing a line of enemies). It found four things on its first full run, and the
first one voids the premise of everything this session tried.

### A chase cannot make contact. At any speed ratio. Ever.

```
    quarry pace | caught | inside reach% | gap at end | closing m/s
       1.79 m/s |     no |            0% |    2.53 m |   -0.022
       1.43 m/s |     no |            0% |    2.05 m |   -0.014
       1.07 m/s |     no |            0% |    1.80 m |   -0.010
```

A pursuer at 1.79 m/s against a quarry at **1.07** ends the run 1.8 m behind it, having spent **none** of
the chase inside its own reach, and *losing* ground. Then the sweep on the stop distance:

| stop distance asked for | effective gap | inside reach% |
|---|---|---|
| 0.95 m | 1.92 m | 0% |
| 0.60 m | 1.56 m | 0% |
| 0.30 m | 1.26 m | 0% |
| 0.00 m | **0.96 m** | 0% |

**There is a fixed 0.96 m standoff on top of whatever the behaviour asks for** — the column is exactly
`stop + 0.96` at every row — and it does not go away at zero. That is path-arrival tolerance: a body is
"there" when it is within a raster cell and a touch of slack of the point it was sent to, which is correct
for walking to a granary and fatal for walking at a person.

Against an intrinsic harm reach of `(0.37 + 0.37) × 1.1 = 0.81 m`, a chase settles outside striking
distance **by construction**. So: **all fight contact in this game has come from stationary encounters** —
a raider looting, a raider blocked, a raider that stopped because the crowd around it stopped. Pursuit has
never once landed a blow. Which is precisely the report: *my people kept trying to do some retaliation but
couldn't turn that into anything.*

And it exposes a mistake of my own. §33 wrote the harm reach as a floor of `ChaseStopMetres + ContactSlack`
so that reach could never be shorter than where a chase stops. **That coupling is self-defeating**: lower
the stop distance to close the gap and the reach falls with it, so the two can never meet. Visible in the
table above — at stop 0.00 the gap is 0.96 m, which is inside the *old* 1.10 m floor and outside the 0.81 m
the floor had just shrunk to. A dependency written the wrong way round, which is the recurring finding
wearing a fourth hat.

### Three villagers refuse a fight they would win

```
    N | killed in | contact% | landed/N
     3 |     never |       0% |    0/3
     4 |     4.8 s |      94% |    5/4
```

One, two and three never engage at all: `StandMargin` 1.25 × a raider's strength 3 wants 3.75, and three
villagers muster 3.0, so they flee. But three villagers carry **60 health against 18** and deal 3 a second
— they would kill it in six seconds and lose at most one of their number.

**Question two compares strength and ignores health entirely.** It is asking "can we out-hit them" when the
question is "can we outlast them", and the two differ by exactly the ratio the roster puts between a
farmhand and a thief. Four is the threshold only because 4 × 1 clears 3.75; nothing in the decision knows
that a villager takes twenty seconds of punishment.

### The front binds, and crowding costs

`4.8 s` at N = 4, 6 and 8, `5.3 s` at N = 12, with **five** distinct bodies ever landing a blow whatever N
is. So the engagement limit works as designed, more than four is worth nothing at all, and twelve is
*worse* than four — the twelfth villager's contribution is to slow the other eleven down. §38's guess that
concentration was the lever had the right target and no way to see it.

### A cordon of twelve costs a raider nothing

```
    N | crossed | seconds | detour | held below half pace for
     2 |     yes |     9.6 |   1.00x |    1.4 s
    12 |     yes |     9.6 |   1.00x |    1.4 s
```

Identical at every N. It walks the straight line, at full speed, through twelve bodies standing shoulder to
shoulder across its path — the 1.4 s below half pace is its own acceleration ramp at each end, present with
nobody in the way at all. **The reported bug, measured, and completely independent of how many people are
standing there.**

### What this changes

The five hypotheses of §39–§40 were all trying to make a fight last longer or concentrate better. None of
them could have worked, because **contact was never happening in the first place** outside of a body
standing still. The order of work is now:

1. **The arrival standoff**, which is one number in the wrong place and the cause of the headline. A body
   sent *at another body* wants a different arrival test from one sent *to a place*.
2. **Health in question two**, so a defence weighs whether it can outlast rather than only out-hit.
3. **The cordon**, which is the avoidance layer and the genuinely invasive one — and now has a measurement
   to be judged by rather than a six-minute raid.

## 42. Pursuit works, and it took two changes that each do nothing alone

The benchmark earned itself in one sitting. §41 found that a chase settles at `stop + 0.96 m` against a
reach of 0.81 m, so pursuit had never landed a blow. Two candidate causes, tested one at a time and then
together:

| | effective gap | inside reach | catches a 0.7× quarry |
|---|---|---|---|
| stop 0.95 m, re-aim on 0.5 m of drift *(shipped)* | 1.92 m | 0% | never |
| stop **0.00 m**, re-aim on 0.5 m of drift | 0.96 m | 0% | never |
| stop 0.95 m, **re-aim on any drift** | 1.70 m | 0% | never |
| stop **0.00 m** and **re-aim on any drift** | **0.00 m** | **92%** | **21.7 s** |

**Neither change does anything alone, and together they are the whole difference.** A stop distance holds
the body off; a stale goal holds it off by as much again; remove either and the other still does the job.
Which is exactly why a session of single changes judged against a six-minute raid found nothing — and it is
worth noticing that *stop 0 was tried and rejected in §39*, on the broken instrument, with the verdict
"my mechanism story was wrong". The story was incomplete rather than wrong, and there was no way to tell
the difference without an instrument that could see 0.96 m.

The re-aim threshold is the subtler half. Half a metre of goal staleness is right for following somebody
about and is *permanent lag* in a chase: the body walks to where the quarry was, arrives, halts, and waits
to be re-aimed. So `Chase` re-aims on any drift at all and `Follow` keeps the slack, which is the honest
distinction — following somebody about is not the act of running them down and does not want to end in
contact.

### The gradient that falls out of it

```
    quarry pace | caught | inside reach% | gap at end
       2.06 m/s |     no |            0% |   16.97 m     gets clean away
       1.79 m/s |     no |            0% |    1.20 m     shadowed, never closed
       1.61 m/s |     no |           97% |    0.95 m     ground down, survives the minute
       1.25 m/s | 21.7 s |           94% |    0.00 m     run down
       1.07 m/s | 21.5 s |           95% |    0.00 m     run down
```

That is §7's promise as a measured curve: a raider that is faster than you gets away, one at your pace is
shadowed and harried, and one slowed by what it is carrying is caught. Nothing in it was tuned — it is what
the speeds and the reach already implied, once the layer stopped holding the pursuer off.

### And §33's floor is retired

`ReachShare` is a body's own business again. §33 made the harm reach a floor of
`ChaseStopMetres + ContactSlack`, and that dependency was written the wrong way round: closing the gap by
lowering the stop distance lowered the reach with it, so the two could never meet. The constraint it
expressed is still real — reach must not be shorter than where the movement layer stops bringing bodies
together — but it is satisfied now by the movement layer doing its job rather than by the harm rule
compensating for it not doing so.

Retiring it took the reach from 1.10 m to the honest 0.81 m, which turned the front's self-test red on an
absolute-damage assertion: 3.2 health a second became 1.1 with the cap unchanged. That assertion was
measuring depenetration jitter, not the cap, and now asserts what it meant to — the ceiling binds, count
alone does not decide the fight, and a fight happens.

### In the raid, over ten seeds

| | got home /24 | their dead | **our dead** | stolen | recovered |
|---|---|---|---|---|---|
| before | 11.6 ± 1.2 | 3.4 ± 1.2 | **12.6 ± 4.1** | 464 ± 47 | 160 ± 56 |
| pursuit works | 11.9 ± 1.2 | 3.1 ± 1.3 | **9.4 ± 3.5** | 476 ± 48 | 164 ± 50 |

Ten seeds this time, not five, and pooled across both blocks — §40's lesson applied. **Our dead falls from
12.6 to 9.4** and everything else is flat inside the noise. Which is the right shape: defenders that can
actually reach a fight stop trailing behind one getting killed piecemeal. That raiders still get away with
much the same amount is consistent with the benchmark — a laden raider at 1.44 m/s against a villager's
1.79 is a 0.8× quarry, and the curve says 0.8× survives the minute.

**The cordon is untouched and still broken**: `1.00x` detour at every N from two to twelve, identical to
nobody being in the way. That is item three, it is the avoidance layer proper, and it now has a measurement
to be judged by.

## 43. Three peers cannot rob twenty, and a raider is not a peer

The design objection, and it is correct: *with 15-20 people around, I shouldn't be able to let 3 guys walk
in and steal that much of my stuff.* Followed by the right experiment — **what would it look like if the
assailants were also numerically villagers?**

That question separates the only two possible explanations. Either a thief is individually much better
than the people it is robbing, or the settlement has no working way to stop anybody at all. `--peers`
answers it: same strength, same health, same pace, hostile faction, nothing else changed. Ten seeds each.

| | got home /24 | their dead | our dead | **stolen** | recovered |
|---|---|---|---|---|---|
| raider — 3 strength, 18 health, 2.05 m/s | 11.9 ± 1.2 | 3.1 ± 1.3 | 9.4 ± 3.5 | **476 ± 48** | 164 ± 50 |
| peer — 1 strength, 20 health, 1.79 m/s | **3.1 ± 1.5** | **12.9 ± 2.8** | 6.2 ± 1.9 | **93 ± 46** | 418 ± 86 |

**Twenty villagers comfortably see off three of their own kind.** Three of twenty-four get home, thirteen
die, and 93 grain leaves the map instead of 476 — most of what is picked up is dropped again and recovered.
Our own losses fall too, from 9.4 to 6.2.

So **the mechanics are exonerated.** The settlement can stop people. The 476 grain is not a broken defence,
it is a statement about how good a raider is: at three times a villager's strength it is worth about three
farmhands in a fight, so three of them are worth nine — against twenty who fight badly on purpose, arrive
in ones and twos, and can only fit five or six around a body at a time.

Which turns the objection into a design question with a number attached, rather than a bug hunt: **how much
better than a farmhand should a thief be?** Two obvious readings, and they are different games:

- *A thief is a peer with bad intentions.* Then a settlement defends itself by existing, and the interesting
  pressure is the interruption cost — 409 grain of work not done even in the peer runs, which is four times
  what was actually stolen.
- *A thief is a fighter and a farmhand is not.* Then a settlement needs somebody whose job is fighting, and
  the raid is the reason to pay for one. That is the roster's own position — `UnitType.Soldier` at strength
  3 and health 45 exists and cannot be built — and it is the answer §33 predicted the numbers would ask for.

### Correction: there is no thief in this game, and there never was going to be

The paragraph above asked "how much better than a farmhand should a thief be?" and that is the wrong
question twice over. **The game will have no thieves and no raids at all** — flagged in §28 and restated
plainly: *this was just a stepping stone.* The raid is a fixture, not content, and tuning a raider is
tuning a test rig.

So the peer result is not a balance finding to act on. Its value is narrower and better: **it says the
defence mechanics work against a peer adversary**, which is exactly what the real adversary is — another
player's villagers, not a scripted burglar with a strength bonus. Twenty peers see off three peers, they pay
409 grain of interrupted work for it, and nothing in the layer needed a special case to make that happen.
That is the whole thing the scaffolding was built to find out.

Which also means the strength-3 thief has no claim to be the default. It is a stress setting — useful for
asking "what if the thing over the hill is much better than us", worthless as a statement about balance —
and the representative case is peers. `--raidtest --peers` is the run to read; the other is a knob.

**And what the scaffolding actually bought**, now that it is finished: the ledger, the front, sticky
targets, and above all the discovery that pursuit had never once worked. None of those are about thieves.
Every one of them will still be true when the thing over the hill is a person.

### And the granary, twice reported

Two separate things, both real.

**The footprint still did not match the model.** The barn is 1.81 × 1.43, so normalised to a 7.5 m square
footprint it drew 7.5 × 5.93 and left a metre and a half of blocked ground with nothing standing on it. Both
stores are stretched to their square now. The cost is a 26% stretch in depth — a barn slightly the wrong
shape, which is much the cheaper of the two lies, because a footprint the player cannot see is one they walk
into.

**And assailants vanished behind it.** That one is §33's mechanic working exactly as designed: a raider
looting a granary *is inside it* and is therefore not drawn. But a body that disappears with no explanation
is a glitch whatever the reason, and the panel saying "1 INSIDE YOUR STORES" is not where the player is
looking. So a store with intruders in it is now drawn in a single alarm colour instead of its own materials
— losing a barn's seven materials for the six seconds somebody is rummaging in it, on the grounds that for
those six seconds the one thing worth knowing about that building is not what it is made of.

## 44. The cordon: one rule applied to the wrong people

A raider crossed a line of twelve enemies standing shoulder to shoulder in 9.6 s with a detour of
**1.00×** — the straight line, at full speed, as though nobody were there — and identically at every count
from two to twelve. The cause was one rule, and it was not in the avoidance mathematics at all.

`HasHigherPriority`: **a mover outranks a settled body**, so the settled body takes full responsibility for
the pair and gets out of the way. That is exactly right among one's own people — it is what
`IsShovableAlly` exists to make efficient, and a settlement where a carter negotiates with everybody
standing about is one that never gets anywhere. It is exactly wrong across a border. Nothing in the rule
asked whose side anybody was on, so **the defenders politely made way for the thief.**

`HoldsGroundAgainst`: a body with no destination does not dodge an enemy that has one. Asymmetric on
purpose, and the asymmetry is the point — the one holding its ground drops its constraint, the mover keeps
its own and must solve the problem alone.

| N in the line | before | | after | |
|---|---|---|---|---|
| | seconds | detour | seconds | detour |
| 2 | 9.6 | 1.00× | 15.5 | 1.21× |
| 4 | 9.6 | 1.00× | 15.2 | 1.20× |
| 6 | 9.6 | 1.00× | 15.4 | 1.25× |
| 8 | 9.6 | 1.00× | 15.9 | 1.30× |
| 12 | 9.6 | 1.00× | 16.8 | **1.43×** |

**The detour now grows with the width of the line**, which is the property that was entirely absent — before,
twelve bodies cost exactly what none did. Crossing takes 60–75% longer and the body spends 5.6 s below half
pace instead of 1.4 s, and 1.4 s was its own acceleration ramp with nobody there at all.

It still gets through, and that is right: a picket line should be something you go *round*, not an invisible
wall. What changed is that going round now costs what going round costs.

**No regression anywhere else**, and for a structural reason rather than luck: the rule fires only across
factions, so every single-faction crowd metric is untouched — pen escape 1.33× walked/optimal and 0
dead-stops, one-cell gate 1.78× and 33, both identical to the figures before the change. The suite passes.

And the raid is **flat inside the noise** (peers, ten seeds: their dead 12.9 → 11.7, stolen 93 → 117). That
is not a disappointment, it is the diagnosis: **nothing in the current scenario deliberately blocks
anybody.** Defenders are usually walking *toward* a threat, and the rule needs a body that has chosen to
stand still. The fix matters for holding a line on purpose, which nothing can be told to do yet.

## 45. What is actually being built, restated

Recorded because the plan has been reading like a game about farms with a burglar in it, and that is a
demo rather than the game:

> soldiers, armies — militia, ranged units, spears, swords, horses — all are on the table. We're only
> BEGINNING to build this. Cordon, then more sims/tests focused on navigation, combat, holding, defending,
> chasing, with different unit types and their interactions. We've built the eco and basic game loop demo,
> then we extend this side with buildings, upgrades, repairs, mining for stone.

So the economy, the calendar, the jobs model, the raid and everything §20–§44 measured are **the first
half of a foundation**, not a game with content to be balanced. Two consequences worth writing down:

- **The raid is finished as a subject.** It was scaffolding, it did its job, and what it bought is
  permanent: the ledger, the front, `--fightbench`, and the discovery that pursuit had never worked. §44's
  cordon fix is the last thing it needed to surface.
- **§44's flat raid result is the shape of everything that comes next.** A layer built for units that can
  be *told to hold* cannot be judged by a scenario in which nobody holds anything. The next instruments
  have to come with the mechanics they measure, not after them.

### The order

1. **More sims, and this time per mechanic and per unit type** — navigation, combat, holding, defending,
   chasing, and the interactions between kinds. `--fightbench` is the pattern and it has three scenarios;
   this wants a dozen. Crucially, holding and defending need something that can be *ordered to hold*, which
   is why they come with the roster rather than before it.
2. **The roster, and it is a roster rather than a soldier.** Militia, spears, swords, bows, horses. The
   interesting content is not any one of them but the interactions — reach against pace, a wall of spears
   against a charge, a bow that does not want contact at all. Note what already exists to build on: the
   front is derived from radii so a wide body already crowds out more of its own side and reaches further,
   and `--fightbench`'s chase curve already prices a speed advantage.
3. **The other half of the settlement**: more buildings, upgrades, repairs, and stone from mines. Stone is
   a third commodity and the first one that is *mined* rather than grown or felled, so it tests the economy's
   generality — everything in §17's ledger was written for two.

### Amended immediately, and for a good reason: combat is blocked on being able to see it

> without visuals the combat is just already too throwing off, as I discovered with the raider/thief test —
> we'll refine what we already have working placeholders for, the economy layer, and the combat scenario and
> modelling I'll find more assets for.

This is the §21 finding one level up. There, a field drawn as a grey block made the *economy* impossible to
read; here, a fight between two identical white cylinders with no weapons, no facing and no animation makes
**combat impossible to judge** — and this session proved it expensively. Five hypotheses were argued from
watching, and every one was wrong, because what was being watched carried almost no information. It took
`--fightbench` to say anything true at all.

So combat waits on assets rather than the reverse, and that is the right dependency: **a mechanic you cannot
see is a mechanic you cannot playtest**, and playtesting is how every real finding in §29–§44 arrived. The
benchmarks are not a substitute for looking — they are what tells you *which* looking was misleading.

**The work therefore reorders to: refine what already has working placeholders — the economy — first.** It
is the half with legible visuals, so judgement about it is reliable. Stone and new buildings are *additions*
to that half and can wait behind polishing what is there; the roster waits on the art that makes it
readable.

## 46. Two reported bugs, both confirmed by measuring instead of watching

`--placementcheck` builds the village, reports its state at tick zero, runs it, and reports again. Both
reports were exactly right, and one of them is not a bug at all.

```
  at tick 0:     1 standing on unwalkable ground, 0 overlapping something solid,  0/0  workers with a tree in reach
  after 3 min:   1 standing on unwalkable ground, 0 overlapping something solid, 19/19 workers with a tree in reach
  trees 9650 -> 9650 (0 felled), 942 wood in stores
```

### "One of our villagers pops in blocked by a tree every single run"

**Exactly one, every run, and still stuck three minutes later.** Deterministic, reproducible, and now
asserted rather than noticed. Not diagnosed yet — the candidates are the spawn nudge, which searches twelve
metres for open ground and cannot escape the interior of a stand, and the posting of cutters against a
raster that is rebuilt after forest cover is painted. It is one body out of twenty-six, which is why it has
survived this long: it is invisible unless you happen to watch that villager.

### "Trees don't actually fell" — true, and not a bug

Zero trees came down in three minutes with seven cutters at work, and the arithmetic says that is correct:

| wood per tree | cutter-minutes to fell one | trees a cutter fells in a year | trees a year for 7 cutters |
|---|---|---|---|
| **90 (shipped)** | **14.6** | 5.6 | **39** |
| 45 | 7.3 | 11.1 | 78 |
| 25 | 4.0 | 20.0 | 140 |
| 12 | 1.9 | 41.7 | 292 |

A cutter chops `WoodPerHandPerYear / (year × (1 − walkShare))` = **0.103 wood a second**, so a 90-unit tree
is **fifteen minutes of one cutter's life**. Thirty-nine trees a year out of nine and a half thousand. At
1.5× compression a session is perhaps ten minutes of simulated time, so **a player sees about one tree fall
per session, if they happen to be looking at it.**

Nothing is broken. **The tree is the wrong grain size**, and §22 said what that costs before it happened:
*"the wood line as a thing you look at"* was supposed to be Stage B's entire interface. An interface that
updates once a session is not one.

And the dial is nearly free. `WoodPerTree` does not touch the annual economy at all — a hand still cuts 500
wood a year whatever a tree holds — so it changes only *how granular felling is*. The one real cost is
walking: `CutterWalkShare = 0.10` was chosen against 90-unit trees, and smaller trees mean more trips
between them, so the effective rate falls. That is measurable with `--settlement --years 1` and is the next
thing to measure rather than guess.

### And the tools this argues for

The reports also asked for the full set — shader, collider, and the rest — and named the specific
suspicions: collider against mesh against renderer, and *geometries that should tie together and do not*
(tree draw distance against camera zoom against shadow frustum extent). Worth noting what the collider
overlay does today: it draws the four discs of a **selected agent** and nothing else. **No tree, no
building, no wall, no impassable cell.** So the one alignment anybody has actually doubted — does a trunk's
collider match the trunk that is drawn — has never been visible at all.

That is the same shape as §41: the instrument for the thing being doubted does not exist, so the doubt
cannot resolve. It is the next piece of work, and it wants to draw structure colliders and navigation
cells against the meshes they belong to, and to print the tie-together numbers on one line where a
mismatch is arithmetic rather than a feeling.

## 47. The collider overlay, and what its first line said

`C` now cycles three ways: off, a selected body's own four proxies, and **everything** — every collider in
the world drawn from its own numbers. That third setting is the one that did not exist, and it is the one
every suspicion has been about: the overlay drew a selected agent's discs and nothing else, so *no tree, no
building, no wall and no impassable cell had ever been visible*, and "does a trunk's collider match the
trunk that is drawn" could not be answered by looking.

Everything it draws comes from `ColliderWorld.All` — a proxy's own centre, kind and size — and deliberately
not from the drawing code, because a disc computed from the same numbers as the mesh would agree with the
mesh by construction and prove nothing. Colour says what a thing *does* rather than what it is, since that
is the question: solid ground, ground you cannot build on, something you can reach. **Disabled proxies are
drawn dimmed**, because "where did that collider go" is exactly what a body indoors makes you ask. Culled to
the tree draw radius, since nine and a half thousand trunks each have one.

Cost, measured: 50k triangles to 64k with everything on. Fine for a debug view.

### And the numbers that are supposed to agree, on one line

*"Geometries that should tie together don't"* is a feeling until it is arithmetic, so the HUD prints the
four distances that describe how far this game can see whenever the overlay is up. At the default zoom:

```
VIEW 46 m · TREES 150 m (3.3x) · SHADOW BOX 150 m (3.3x, texel 7.3 cm) · FOG 83-138 m
```

**The shadow box is 3.3× wider than anything you can see, so about nine tenths of the shadow map is spent on
ground nobody is looking at** — and the texel is what shadow quality *is*: 150 m over 2048 texels is 7.3 cm.
A box sized to the view with a margin for off-screen casters would be nearer 2.5 cm, which is a threefold
sharpening for nothing.

Note the half that *is* tied, because it explains how this survived: `treeDrawRadius` is
`max(TreeDrawMetres, SunOrthoExtent × 0.75)`, so trees follow the shadow box to guarantee that anything
casting into view exists. The dependency runs the wrong way round — the box is a fixed 150 m unrelated to a
`cameraDistance` that ranges 31 to 78 m, and the draw distance obeys the box rather than the eye. At the
closest zoom the box is **4.8×** the view.

That is one finding from one line of text on the first run, which is the argument for the overlay rather
than for any particular fix. The fix is worth measuring next: track the box to the view, and let the tree
distance fall out of it.

## 48. The shadow box tracks the view — and §47's finding was wrong

**First, the correction.** §47 read the geometry line as saying the shadow box was 3.3× oversized and a
threefold sharpening was free. That was wrong, and it was wrong because *the line's own terms were not
commensurable*: it compared the box's **full width** against the camera's **standoff**, which is not a
radius of anything. A tie-together line whose terms do not commensurate invents mismatches and hides real
ones, which is worse than not having it.

Written against the visible ground radius — derived from how far back the camera stands, how far down it
looks, its field of view and the window's aspect — the truth is more interesting:

| zoom | visible ground radius | against a 75 m half-box | |
|---|---|---|---|
| closest, 31 m | 43.5 m | **1.73×** | two thirds of the shadow map spent outside the view |
| default, 46 m | 64.5 m | 1.16× | correct — and this is where 150 m came from |
| furthest, 78 m | 109.4 m | **0.69×** | **the box is smaller than the view: shadows simply missing** |

The far case is a *visible bug*, not a quality question, and it is what a fixed number guarantees: right at
the zoom it was tuned for and wrong at both ends. Which is word for word the lesson already written in the
comment above `camera.FarPlane` — *"a flat 150 m, which is fine at the zoom it was written for and silently
wrong at any other"*. The same mistake, in the same file, thirty lines apart, with the explanation of it
sitting in between.

So `SunOrthoExtent` is now `2 × (VisibleGroundRadius + margin)`, floored. Texels go from 7.3 cm to 5.0 cm
pulled in, and pulled out the shadows exist. The margin is a slider and derived rather than guessed: a
shadow is `1/tan(elevation)` times its caster's height, 1.11× at 42°, and the tallest thing here is a
six-metre tree — so eight metres of margin covers anything off screen casting into view.

And `VisibleGroundRadius` is the number everything about seeing distance should have been written against.
Nothing was. Tree draw distance is still an independent 150 m, which the line now reports as a multiple of
what can be seen (2.3× at the default zoom) — the next thing to look at, and now legible.

## 49. The blocked villager, named and fixed — and the patches removed outright

### The patches

Averaging the block helped and did not fix it, so the honest answer is that the colour should not exist.
**Forest cover is a navigation fact, not a ground material.** Cells with two trees crowding them are closed,
and that lives in the surface channel because the surface channel is where the raster reads terrain from —
it was never meant to be *seen*. What you see under a wood is trees; the ground between them is the same
ground. `TerrainSurface.Forest` draws as grass now and the canopy above does the work.

The averaging stays, because it is right for its own reasons: a block that is half road or half mud should
be half-coloured, and a boundary drawn from one sample at a block's centre is a staircase where an edge
should be a gradient.

### The villager

`--placementcheck` was extended from counting to naming, which took it from "one body is stuck" to a
diagnosis in one run:

```
stuck: body 13 at (-18.2, 9.4), radius 0.37, doing None, surface Grass,
       nearest trunk 1.61 m, nearest legal ground 0.5 m
```

**Nothing was on top of it.** Grass, no trunk within a metre and a half, and legal ground half a metre away.
It was standing on a cell whose **clearance rung is 0.25 m** against a body needing 0.37 — the raster
quantises clearance to 0.25, 0.75 and 1.25, so a cell just inside the skirt of an impassable patch admits
nobody at all, and the impassable patches out there are *forest cover* rather than trunks. Which is why it
read as "blocked by a tree": it is blocked by the wood, one cell short of the ground it could have stood on.

And the cause is the recurring finding for the fourth time today. `NudgeOutOfBuildings` asked exactly one
question — `IsPositionFreeOfPlacement`, *is there a building here* — and **free of buildings and walkable are
different questions.** A body needs somewhere it can stand, and standing is the navigation layer's word, not
the placement layer's. It now asks both, of the candidate as well as of the original, because a ring search
that accepts a building-free cell it cannot walk on has only moved the problem.

`0 standing on unwalkable ground` at tick zero and after a minute. Suite passes, and the crowd benchmarks
are identical to the figure — pen escape 1.33× and 0 dead-stops, one-cell gate 1.78× and 33 — which
matters, because every spawn in the game goes through that nudge.

### The tally, because four is a pattern rather than a coincidence

Today's bugs, in one sentence each:

| | the question asked | the question that mattered |
|---|---|---|
| §41 | how far does harm reach | how close does a chase actually get |
| §48 | how wide should the shadow box be | how much ground can be seen |
| §48b | how many texels of bias | how many *metres* of bias |
| §49a | what colour is this cell | what colour is this five-metre block |
| §49b | is there a building here | can a body stand here |

Every one is a number that is correct in the layer that owns it and wrong where it meets another, and every
one was invisible until something put the two quantities side by side. That is the argument for the
overlay, the geometry line and the benchmarks — not for any of the individual fixes.

## 50. The map gets a near side and a far side

> not a covering woodline but player placed corner-ish, and trees are concentrated in dense forests on some
> sides, some sides might be open/patchy — and in the forests I'd dial up the density even further.

**A settlement in the exact centre of a square map has no geography.** Every direction is the same
direction: the same distance to the edge, the same amount of forest, the same everything. So nothing about
*where you are* can matter and "which way do I expand" has no answer. Two changes give the map a shape, and
both are cheap.

**The site is corner-ish** — a quarter of the extent out on both axes, 150 m on a 600 m map. Far enough that
the corner is close and the interior is open; near enough that the settlement is not pressed against the
border with half its catchment off the map.

**The woodland is shaped by bearing, and the shape comes from the site rather than from a choice.** Since
the settlement sits off a corner there is a direction with a country in it and a direction with a border in
it, so `inland` is just the bearing back toward the middle. Two deep masses either side of inland, an open
run toward the corner, and a floor of stragglers everywhere so that no side is a bald patch with a straight
edge. Rejected rather than relocated: nudging a refused anchor somewhere acceptable would pile the rejects
along the edge of the open sector and draw a wall exactly where the gap is meant to be. Off the map is a
refusal too — clamping would stack every out-of-bounds tree onto the border as a hedge, which is precisely
the artefact a corner-ish site invites.

**Denser in the forests**, as asked: spacing 2.2 m to 1.6 m is about twice the trunks per hectare, and the
anchor counts are up because shaping refuses most of what it is offered — the same anchors over a third of
the compass would have *thinned* the forest rather than concentrated it. 9,650 trees to **11,177**.

### One band is exempt, and it is not an oversight

The near band — 46 trees inside a cutter's reach — is **unshaped**. §22 wrote down why before this came up:
everything the economy gate measures depends on how much wood stands within reach, so that band is an
economic constant rather than scenery, and thinning it by bearing would have halved the settlement's
starting fuel as a side effect of a decision about how the map *looks*. It also happens to be true of
settlements: you found the place because there was wood round it.

### It costs nothing

| | before | after |
|---|---|---|
| trees | 9,650 | 11,177 |
| rasterise the woodland | 1,043 ms | **960 ms** |
| steady tick | — | 4.41 ms |
| a year: grain and wood per person | 270 / 126 | 270 / 126 |
| people alive after a year | 30 | 30 |

**Cheaper with a sixth more trees**, because shaping empties a third of the compass and the rasteriser's
cost is in the impassable area rather than the trunk count. The year gate is unchanged to the digit, which
is what exempting the near band was for. Suite passes.

## 51. Checkpoint — raids off, and what the next session is for

Raids are **off by default**. §45 settled that the game will have no thieves and no raids; the raid was
scaffolding, it bought the ledger, the front, `--fightbench` and the discovery that pursuit had never
worked, and then it was finished. It stays in the code because `--raidtest` is the gate's third leg and the
only exercise the threat layer gets — and because the next adversary is a person walking in through the same
code.

### What a fresh session should know about the instruments before touching anything

This is the transferable part, and it cost most of a session to learn.

- **`tools/gate-rts-game.sh`** — three headless runs before anything lands: `--selftest` (a broken rule),
  `--settlement --years 1` (an economy that stops feeding itself, which is a property of a year and not of a
  tick), `--raidtest` (a defence that stops defending). Conservation is checked every tick of all three.
- **`--fightbench`** — a ring, a chase, a cordon. One mechanic each, open ground, seconds to run. Built
  because five locomotion changes were judged by `--raidtest` and **all five verdicts were wrong**.
- **`--placementcheck`** — is the village one anybody can work in. It counts *and names*, which is what
  turned "one body is stuck" into a diagnosis in one run.
- **`C`** — cycles the collider overlay to *everything*, drawn from each proxy's own numbers, plus the
  geometry line: what can be seen, how far trees are drawn, the shadow box and its texel.
- **`--raidtest --seed S`** — because **a single run of a chaotic simulation cannot distinguish a mechanism
  from a coin flip**, and the tell is the spread rather than the mean. §40: the same unchanged build gave
  4.4 or 2.4 raiders killed depending on which five seeds were drawn, and a whole section's conclusion had
  to be withdrawn.

**And the recurring finding, which showed up five times in one day:** every bug was a number correct in the
layer that owns it and wrong where it meets another — harm reach against chase distance, shadow box against
visible ground, bias in texels against bias in metres, cell colour against block colour, building-free
against walkable. Each was invisible until something put the two quantities side by side. When something
feels wrong and cannot be found, the question to ask is *which two layers is this number crossing.*

### The next session: the economic machinery coming to life

> bring the interface in and polish the gameplay layer and build it out on the economic development,
> building, repairing, and unit training side, hauling mechanisms, multiple settlement points — the actual
> economic machinery coming to life.

The half with legible visuals, which §45 established is the half where judgement is reliable. Ordered by
what unblocks what rather than by size:

1. **Hauling, first, because it is currently invisible.** A simulated year of the default village reports
   `0 carts built, 0 board jobs given out, 0 standing routes run dry`. Three mechanics that exist and are
   unit-tested — the cart as a bought role (§24), the forward depot, the hauling board's stranded-stock
   trigger (§6) — have never once been seen in a session. The cause is that the near band puts wood inside
   every cutter's reach for the whole year, so nothing is ever stranded. Either push the wood line out at
   founding or add a scenario that starts stripped; the wood line as a thing you look at was Stage B's whole
   interface.
2. **Tree grain size**, which is the other half of the same problem. A 90-unit tree is **fifteen
   cutter-minutes**, so 39 trees fall a year out of 11,177 and a player sees about one per session.
   `WoodPerTree` does not touch the annual economy at all — a hand cuts 500 a year whatever a tree holds —
   so it is nearly a free legibility dial. The one real cost is walking, since `CutterWalkShare = 0.10` was
   chosen against 90-unit trees; sweep it against the year gate rather than guessing.
3. **Multiple settlement points**, which is what the corner-ish site and the shaped woodland were for.
   Note what already exists: catchments, forward depots, the hauling board, and a granary that is just a
   node. What does not exist is a second *centre* — anything that makes a place a place rather than a store.
4. **Building, repair and training**, which all want the same thing first: a **build queue and a cost the
   player can see**. Construction already works as timber carried out plus hands standing at it (§26); what
   is missing is the interface to commit to it and the feedback that says what it is waiting for.
5. **Interface**, threaded through all of the above rather than done once. The contextual HUD exists; what it
   does not do is tell you *why nothing is happening*, which is the question this economy generates most
   often — a house with nobody in it, a field outside every catchment, a cart nobody can afford.

Two loose ends carried forward, both small: the `LookSettings` values are still my guesses rather than
anybody's judgement, and stone is unstarted — the first commodity that is mined rather than grown or felled,
and therefore the real test of whether a ledger written for two generalises.

## 52. Two systems that were one: terrain generation, and terrain dressing

> I think we're getting into terrain dressing stuff, which should come atop a layer of terrain generation,
> which we sort of have in a primitive way — let's name and classify the systems as such.

Right, and worth naming precisely, because the two have been tangled in `SettlementScenarios` and
`RtsGameLoop` since §50 and the tangle is starting to cost. They answer different questions, they live at
different layers, and — the test that settles it — **one of them the simulation must agree with, and the
other it must never see.**

### Terrain generation: what the land *is*

**The simulation's own truth.** Heights, surfaces, what is passable, where the woodland stands, where the
settlement was founded. Every one of these is a fact the economy and the navigation raster read: a cell's
surface decides its path cost, forest cover closes ground, a tree is a node holding ninety units of wood.
Change any of it and the year's arithmetic changes.

What exists, and it is more than "primitive" in one respect and less in another:

| | |
|---|---|
| heights and surfaces | `TerrainMap` over a 0.5 m grid, five surfaces, one byte each |
| woodland | `ScatterWoodland` — bands by radius, shaped by bearing, an anchor band left unshaped for the economy's sake (§50) |
| the site | corner-ish, and the woodland's shape is derived *from* it |
| features | a lake and a road exist in the terrain lab and **nothing generates them in the village** |
| rivers, hills, cliffs, ore | none |

So: the *representation* is real and load-bearing, and the *generator* is one function that scatters trees.
That is the honest state.

### Terrain dressing: what the land *looks like*

**Cosmetic, derived, and never read by a decision.** Ground colour, macro variation, the wear that footfall
leaves, the grass and flowers, the skirt of undergrowth at a trunk, the stumps where trees came down.

The rule that makes this a layer rather than a pile: **nothing here is allowed to be state.** Not
fingerprinted, not saved, not iterated over by the economy. Everything is either a function of a world
position (so the same place always answers the same way, no storage) or a bounded ring of recent events
(fellings) or an observational field the renderer keeps for itself (wear). A loaded save has no stumps and
no paths and grows them again, which is honest for a record of watching rather than a fact about the world.

| | how it is derived | where |
|---|---|---|
| ground colour | terrain surface, averaged over the 5 m block, plus two octaves of world-space noise in the shader | `BuildCoarseGround`, `world.frag` |
| wear paths | footfall accumulated per tick into a 192-cell field, decayed, sampled by world position | `AdvanceWear`, `world.frag` |
| ground cover | patch density from two octaves of lattice noise, thickened where the terrain says woodland | `DrawScatter`, `PatchDensity` |
| undergrowth | one or two low plants per trunk, from the tree's own id | `DrawUndergrowth` |
| stumps | a bounded ring of felling sites | `RecentFellings`, `DrawStumps` |

### And the dressing pass this note came out of

Tuned as asked, and the reasoning is worth keeping because it generalises:

- **The round bushes are out of the open scatter entirely.** At any density they read as objects placed on
  a lawn rather than as ground cover, and a map dotted evenly with them looks *arranged*. They keep the job
  they are good at, which is hiding the foot of a trunk.
- **Patches, not a per-cell coin toss.** A uniform probability spreads cover evenly at whatever rate it is
  given, and evenly is the one thing ground cover never is: it grows in runs and drifts, thick here and bare
  a few metres away. Two octaves of lattice noise give a patch a length and an edge nobody authored.
- **Thicker in woodland**, and the terrain already knew — forest cover marks every cell with two trees
  crowding it, so "am I in a wood" is a lookup rather than a spatial query. Tall grass goes from occasional
  in the open to thick under a canopy on that one test.
- **Flowers are rare and never under a canopy**, because the thing that makes a flower read as a flower is
  that there is not another one next to it.
- And a hard budget, because grass is the most numerous thing in the scene and the least missed. The
  instanced batch refuses past 16,384 and the trees, their skirts and the cover all share it.

### The handoff

**Generation** is the layer with more missing, and it gates the interesting parts of the economy:

1. **Rivers and water.** The one feature that changes movement rather than decorating it — a crossing is a
   chokepoint, and §33's "approaches are knowable" becomes geography rather than a note about rides.
2. **Relief that means something.** Heights exist and nothing generates them in the village, so the map is
   flat and slope costs nothing. Hills give a settlement a back to defend and a reason to prefer one site.
3. **Deposits.** Stone is the next commodity and it must come *from somewhere on the map* — which makes
   generation and economy the same conversation, and is the first time where a resource *is* will matter.
4. **More than one biome**, once the above exist, because the seasonal palette already varies and a map
   with two kinds of country in it is what makes a second settlement site a decision.

**Dressing** is the layer that is nearly done and wants finishing rather than building:

1. **Seasonal models.** The pack ships `_Autumn`, `_Snow` and `_Dead` of every tree, plus `Rock_Moss`,
   `Bush_Snow`, `TreeStump_Snow`. Swapping the *model* by season is a far stronger read than grading the
   light, and the material classifier already handles all of them. This is the single biggest remaining win
   in the whole visual pass.
3. **Contact grounding.** The last item from the reference list that has not been done: a tight darkening
   where any object meets the ground. Undergrowth does it for trees; buildings and heaps still float
   slightly.
4. **Dressing follows the ground it dresses.** Once relief exists, cover wants to thin on slopes and
   gather in hollows — which is the moment the two layers start informing each other rather than merely
   stacking.

## 53. Tone: the season decides the grade, and the village lights itself

> the things about color, sun tone, grading profile, foliage motion/smoke etc effects, and then a stab into
> night time lighting

Two items of the list this came with were already done in §52 — the wear around the settlement and the macro
terrain colour — so this is the other four, in the order asked for. What it is really about is **composition
over the cycle**, which the user named and which is worth writing down because it reframes what the light is
for:

| | what the frame is about |
|---|---|
| day | terrain and economy: the ground and what is on it |
| golden hour | architecture and topology, because the shadows are what describe them |
| night | the settlement itself, because the surrounding territory recedes |

Three pictures out of one cycle. Nothing mechanical depends on it and it is still the strongest thing in the
session, because it means the same map is worth looking at three times.

### The two bugs in the light, and why neither was findable by looking

**The low-sun colour was one fixed copper for the whole year.** The ramp that reaches it is a function of the
sun's height alone, and at fifty degrees north the midwinter sun never clears seventeen degrees — so the ramp
was pinned all day and **winter noon was being painted as an autumn sunset**. The one season that should be
unmistakable was wearing another season's light, and it had been doing so since the palette was written.

**And the calendar's year and the sun's year did not start in the same place.** The calendar begins at spring;
the declination formula begins on the first of January; nothing had ever reconciled them. The light ran about
six weeks early all year, so the season called spring was lit as February — noon sun 25° against harvest's
50°, which is a spring dimmer than an autumn. Thirty days of offset lands all four season middles on their
solar counterparts at once: summer's on the June solstice, harvest's on the September equinox, winter's within
a day of December's, spring's in the second week of March. That it fits all four is what makes it an offset
rather than a taste.

Both were found by printing the year, not by watching it. Which is the point of the new instrument.

### `--skyprofile`, and the property it exists to check

A palette is the one part of this scene nobody can judge from a screenshot: two frames in two seasons look
different, and whether the difference is *the season* or merely the hour the shot was taken is a question
about numbers. So it prints the sun's height, the light's warmth and the grade at six watches of the day in
all four seasons, and then checks four properties. The load-bearing one is a comparison of two spreads:

> **the season has to move the light further than the hour does.**

Take the light at noon in four seasons — that is what a season is worth. Take it at five daylight hours
within one season — that is what an hour is worth. The first must be larger, or "glance at the frame and know
the month" is a wish rather than a description. It currently reads **2.71 to one**.

Two of its own first assertions were wrong, and both mistakes are worth keeping in the file because they are
the same mistake in two costumes — *comparing a quantity against something that is not the thing it names*.
It checked a season's midpoint against the atlas figure for a solstice, which no season's midpoint is; and it
read "seven in the evening" as dusk in seasons where the sun has already set, so what it actually measured was
the night palette. §51's recurring finding again: a number correct in the layer that owns it and wrong where
it meets another.

### The grade belongs to the season

Exposure, saturation and contrast were one global setting, which meant the palette could change every hue in
the scene and never change how the frame was *shot* — and a winter photograph is recognisable as much by
being flat and drained as by being blue. They are per-season now and the sliders are multipliers over them:
winter 0.94/0.80/0.93 against summer 1.02/1.02/1.06.

**Midday is pulled back against the sun's height rather than the clock**, which is what keeps time of day a
small oscillation inside the season rather than a competitor to it: only summer ever reaches a high sun, so
summer's noon gives up 0.22 of green's chroma and winter's gives up none. Green has its own dial because the
present pass cannot tell a green roof from a green field, and pulling the whole frame's saturation to fix the
field drains the earth and the tiles with it.

`NoonElevation` is gone from the palettes: authored five times, blended every frame, read by nothing since
the sun started coming from real solar geometry. A palette field that looks like a decision and changes
nothing is worse than a missing one.

### Wind, and the shape mattering more than the size

Plants lean in the vertex stage. It hinges at the ground, height enters as a square root because a tree is
stiffer than a blade of wheat, the phase carries a world-space term so a wood ripples rather than pulses, and
the gust is a travelling wave that never reaches zero.

**Reported immediately as too violent, and the cause was the shape rather than the amplitude.** Equal
amplitude on two axes at right angles makes a crown *orbit*, and an orbit reads as swinging because nothing
in wind goes round. Most of the displacement is now a steady lean along the wind's bearing with a fraction of
it oscillating and a smaller sway across — a tree that is mostly just leaning looks windy while hardly moving
at all, which is the cheapest calm there is. Amplitude also came down and the frequency halved: the moving
part of an eight-metre crown went from about ±28 cm at 0.4 Hz to ±5 cm at 0.13 Hz.

And a cap that the square root alone did not give: nothing may travel more than seven per cent of its own
height. Root-of-height makes small plants move relatively *more* than tall ones — 8 cm on a 60 cm tuft is a
seventh of it, and a field of grass shifting by a seventh shimmers.

The shadow casters do not lean, and that is measured rather than hoped: the sun's box is 150 m across a 2048
map, so a texel is about 12 cm and the crown's travel is under half of one. Past a sway of about 0.25 the
argument stops holding and the caster's push constants want the wind too.

### Smoke, which turned out to be a second channel for the season

A fire is lit for two reasons with two different shapes. **Heating is a season** — constant through a winter
day, absent in summer. **Cooking is an hour** — twice a day, every day of the year. Added rather than chosen
between, so a winter morning is the smokiest thing in the game and a summer village still looks lived in. One
function of the date and the hour, no state, and a glance at the village says the month even in flat light.

Dressing by §52's test: a puff is a position, a birth time and a seed, and everything else about it is a
function of its age worked out when it is drawn. Each chimney's turn comes round on its own id hashed into
the interval, so the houses are evenly staggered with no timer and no table, and identically after a reload.

The one trick that makes a sphere read as smoke is **fading its opacity at the silhouette**. A translucent
sphere drawn plainly is a bubble, because it is most opaque exactly where its outline is; a volume seen
edge-on is thin. With the rim gone, ten puffs at a fifth opacity stack into a plume instead of a bunch of
grapes. Lit by the sky rather than the sun, since smoke is optically thin — with one exception worth its two
lines, which is that a plume with a low sun behind it glows.

It is the first thing in this scene that is not opaque, so it brought a pipeline of its own: alpha blending, a
depth test that does not write, near-hemisphere culling, and its own shader pair — because it is also the
first surface whose fourth colour channel means opacity rather than which material it is.

### Night: small warm signals, and each one a fact

Lit windows, a lantern at the granary door, a brazier at a site, and the pools they throw. Every one of them
is information rather than decoration:

- **A house is lit if somebody lives in it and dark if nobody does.** So the cottages a settlement built and
  cannot fill are visibly empty — one of the questions §51 wanted the interface to answer, answered with no
  text at all.
- **The number of lit windows is how many live there.**
- The granary keeps a lantern because it is the building somebody is always at; a site has a brazier only
  while it is being built.

The spill is a **field**, not a light list, for the same reason the wear is one: every fire splats into it and
the shader reads it once by world position, so lighting the village costs the same at four hearths or forty —
no loop, no per-light bound, no popping when the ninth nearest becomes the eighth. What it gives up is shape,
which is the right trade for a soft warm patch on the ground. Rebuilt every sixth frame, skipped entirely by
day, so the feature is free for two thirds of the cycle.

Restraint is the whole brief here, and the failure mode is not one window being too bright — it is pools of
orange everywhere, at which point the settlement stops being a warm island in a cool landscape and the
composition is gone. So the glow is hot and small and the spill is dim and wide, on two separate dials
because they fail in opposite directions.

### Found on the way, and worth keeping

- **The push-constant block had four owners, not the three its own comment claimed**: two shaders, the
  declared range, and the payload's own length. The wind vec4 went into three of them and the draw-time error
  named the fourth. The range is the payload's length now, so those two cannot drift again.
- **The material classes moved to `Shaders/materials.glsl`**, shared by both stages, because the vertex stage
  has to know what a plant is and a second copy of a table of magic constants is a table that will drift.
- **One owner for which wall is the front.** Three callers need to agree — the windows, their pools and the
  chimney — and three copies of a rotation are three chances to hang the lantern on the back of the house.
- **The geometry line grew a dressing section**: `NIGHT` with its light count, `SMOKE` with its plumes.

### The handoff

Ordered by what the session actually left undone rather than by size:

1. **The seasonal models**, still. §52 called this the single biggest remaining win in the visual pass and it
   remains untouched: the pack ships `_Autumn`, `_Snow` and `_Dead` of every tree plus `Bush_Snow`,
   `Rock_Moss` and `TreeStump_Snow`, and the classifier already handles all of them. Everything this session
   did to the light makes the case stronger, not weaker — grading a summer canopy toward autumn is a poor
   substitute for an autumn canopy.
2. **Contact grounding**, the last item from the reference list: a tight darkening where an object meets the
   ground. The hearth field is a working precedent for how to do it cheaply — a field, sampled by position.
3. **Bloom**, which the emissive class now argues for. A window that clips is doing the right thing and there
   is nothing to spread it, and the shared library already has `bloom.glsl` and a `FullscreenPass` to hang
   it on.
4. **Wind that the shadows agree with**, if the sway ever wants to be stronger than 0.25.
5. And still carried, still unfixed: **the red raid gate leg** — 21 of 24 raiders stuck since the map was
   reshaped in §50.

## 54. Terrain generation, and approaches as the thing being generated

Settled in conversation on 2026-08-21, after §52 named the two layers and handed generation the list it
gates. Four questions were put and answered, and the answers decide more than they look:

> 1. primarily, a site worth choosing (defensible back) > slope as cost > ridges/other features.
> 2. Founding is definitely there in gameplay. 3. both technically, barrier first. 4. some here or there
> but mostly concentrated in places.

And on how it lands:

> free founding, start "around" a plausible site with no placed structures and go will be it — [deposits
> deplete] certainly — leave the tests/scenarios as they are, on flat ground, migrate them one by one
> checking green and tweaking once we're done building and validating the relief layer

### The line between the two layers, in metres

Relief's first job is making a site worth choosing. A landform therefore earns its place only if it changes
**what a site reaches** or **how many ways in it has** — and both of those are numbers this game already
has. Catchment reach is 66 m off a road and 82 m along one; the settlement is 36 m across.

> A feature smaller than a catchment cannot change what a site reaches, and one narrower than the
> settlement cannot give it a back. Below that it is dressing, and §52 already says where dressing goes.

So **generation owns features from about 60 m upward and dressing owns everything below**, with no overlap
and nothing to argue about. That is the same discipline as pricing a tree in cutter-minutes, applied one
layer earlier — and it is the answer to "how big is a hill" that does not require anybody's eye.

### What is actually being generated is approaches

The load-bearing idea, and the reason the priority order was worth asking for. "A defensible back" is not
about elevation; it is about **how many directions something can arrive from**, which is countable: sample
sixteen bearings, ask the router which of them reach the site without crossing closed ground or paying more
than a margin over the straight line. Four of sixteen open is a strong site; thirteen is an exposed one.

Three things follow, and together they are why this is the frame rather than "add hills":

- **Relief, water and woodland stop being three features and become three ways of closing a bearing.** A
  ridge closes a sector, a river closes a side, a dense stand closes a lane. They compose without any of
  them knowing the others exist, and a site's score is one number whatever produced it.
- **It is the same number the raid needs.** §53's fix centred the arrival ring on the settlement and had it
  pick a ride; "which bearings are open" is precisely what a raid should be choosing among, and §33's
  "approaches are knowable" becomes geography rather than a note about rides.
- **The machinery exists.** The region graph and the flow field can answer it today. Nothing new is needed
  to *score* a site — only to generate the ground that gets scored.

### Feature scale, from the clock

Walking round a landform has to cost something comparable to arriving at all, or a back is not a back. A
raid arrives from 120 m in about 59 s at raider pace; a 150 m flank costs about 84 s to walk around at
villager pace. So **landforms want a frontage of 150–250 m on a 600 m map**, which is a handful of them
rather than a field of hills.

And an accident worth having: **slope-as-cost needs no drama at all.** Pace drops about a sixth on a tenth
grade, and a sixth off a 66 m catchment is a larger effect than the road multiplier buys in the other
direction. So gentle gradient does the second job while a few big landforms do the first, and the two
priorities barely compete — which was not obvious before the numbers were put side by side.

### The opening: a band and a heap

Free founding, and the start state is what makes it real: **a band of settlers around a plausible site with
nothing built.** The generator still scores a starting position, but it places no structures, and the player
may walk them off it before laying the first stone.

The starting stores then have nowhere to be — and the answer is one this game already owns. §17's correction
made resources physical until consumed, so a starting stock is **a heap on the ground**: a `NodeKind.Pile`,
belonging to nobody, which the hauling board already collects with no new code. That gives the first
decision a cost in metres — where the granary goes is measured against where the supplies are lying — and it
means the opening exercises hauling, which §51 complained had never once been seen in a session.

The rest falls out of rules already written: nobody is housed, so the population does not grow until houses
exist (§6's "unhoused is a signal, not an error"); there is no store, so there is no catchment, so a
household draws from nothing until one is built. The founding sequence is not scripted anywhere. It is what
the existing rules do when you start with nothing.

### The technical risk, named early because it is the one that could sink it

**The router's premise is rectangles of uniform ground.** §-whatever's rebuild — flat field → portals over
fixed regions → rectangles of uniform ground plus a corner graph — is what took 1200 m with 2,000 agents
from 571 ms a tick to 5.8. Continuous relief makes every cell's cost differ from its neighbour's, which
degenerates every rectangle to a single cell and hands back the 571 ms.

So **slope enters the raster quantised**, into a few bands rather than as a float. Rectangles of uniform
*band* stay large, the corner graph stays small, and the router never learns that the ground now rolls. The
bands are a look at the economy rather than at the eye: enough of them that a cart minds a hill, few enough
that a hillside is one region.

Related and deliberately deferred: cost is **per cell, not per edge**, so "steep ground is slow" is
expressible and "uphill is slower than down" is not. That is most of the effect for a fraction of the
disturbance, and asymmetric cost is the follow-up if hauling routes turn out to need it — which is a thing
to measure rather than assume.

### Water and stone in the same frame

**River, barrier first.** Carved down the generated heights to a map edge: deep channel closed, a few
shallows crossable at a pace penalty. It closes a side, which serves the first job directly, and it is a
rate everywhere except the channel. Water as a resource — mills, drinking, irrigation — stays open and
blocks nothing.

**Stone, concentrated and depleting.** Two to four deposits biased toward high ground and steep faces, plus
a thin scatter. Biasing them to relief is the point: deposits stop being a fourth independent decision, and
the thing worth holding ends up somewhere with a shape around it. **They deplete**, and largely — a quarry's
life is measured in years rather than seasons. That is what gives territory an expiry and makes expansion
inevitable rather than optional, which is the terminus §1's tenure argument asked for; and it is what makes
a second settlement a decision rather than a duplicate.

### Flat is amplitude zero, which is the whole migration plan

The scenarios stay on flat ground and are migrated one at a time, checking green and tweaking, once relief
is built and validated downstream. That is only cheap if flat is not a legacy mode:

> **Relief is a parameter, and zero reproduces today's ground exactly.**

Then every existing calibration stays valid, the determinism fingerprint does not move, the year gate keeps
measuring the thing it was calibrated against, and migrating a scenario is a decision about that scenario
rather than a flag day. Whatever survives migration gets a controlled and measured variant; whatever does
not is left as it is, deliberately, on flat ground.

### The order

1. **Heights that mean something** — a generator of a few big landforms from a seed, amplitude as a
   parameter, slope quantised into bands, cliffs closed. Verified by the router's rectangle count and the
   tick time holding, and by every existing scenario being identical at amplitude zero.
2. **Approaches as a measured quantity** — the sixteen-bearing scorer, a profile that prints the
   distribution across a map and names its best and worst ground, and the raid's bearing choice reading it.
3. **Founding** — a band, a heap, no structures, and the score of wherever the cursor is.
4. **Water**, barrier first.
5. **Stone**, concentrated and depleting.
6. **Migration**, one scenario at a time.

The render side of relief is a known list rather than a discovery: the wear field is two-dimensional, the
hearth falloff fades from height zero rather than from the ground, the coarse ground colours a 5 m block
from one sample, and the shadow box assumes a flat receiver. None is hard, all are real, and doing heights
first is what stops them being retrofitted twice.

## 55. The look pass, the terrain layer, and the afternoon the frame cost twelve times what it should

A long session, and it divides into three arcs that turned out to be one: what the light does (§53's
follow-through), what the land is (§54's first milestones), and what a frame costs. The third only
happened because the first two spent it.

### The generator, and five ways a height field lies about its own grade

`ReliefPlan` is a list of shapes rather than a field of noise, for the reason §54 gave: fractal noise is
self-similar, so it puts five-metre bumps in the layer the simulation has to agree with. Feature size is
absolute rather than a share of the extent, because a catchment is 66 m and a raid arrives from 120 m
whatever the map's size.

`--relief` is the gate, and it is not "does it look like hills": **the partition stays sane, the tick
holds, and amplitude zero is bit-for-bit today's ground.** It found, in order:

1. **A raised cosine is invisible.** Zero slope at rim *and* summit concentrates all the fall into a thin
   ring, so a 7.6 m rise over 130 m peaks at 0.09 and reads as paper on the ground.
2. **Lobing steepens tangentially.** Modulating a rim by bearing adds gradient *along* it of
   `Lobing × Σ(coefficient × harmonic)` — 2.07 at harmonics 3/5/7, which multiplies the gradient by 2.3.
3. **Landforms were summed.** Placement permits neighbours to overlap by half a radius, and overlapping
   flanks add their gradients. Survived two rounds of budget-tightening because the budget was per
   landform and the breach was between them. They take the higher now — which also makes overlapping
   hills a range with a saddle, and a saddle is where an approach comes through.
4. **The softening of that maximum was a cliff made of a constant.** Log-sum-exp normalised to be exact
   at equality is wrong at the other end: it subtracts 1.31 m wherever one term dominates, so landforms
   contributing *nothing* still moved the ground — eight metres over six of them, across the two metres
   the blend acts over. Measured as a grade of **4.91** on a map whose steepest flank was 0.33.
5. **The crest dipped below the flank it met.** A summit falling to 0.84 of the height where the flank
   arrives at 1.0 is a step of 0.16 × height: 3.2 m at amplitude 20, in the same place every run.

> **A generated height field's stated grade and its actual grade are different numbers.** Three of those
> five were breaches of a budget the code stated correctly and did not keep, and the only reliable way to
> know the second number is to measure it. `--relief` prints the measured steepest grade and where it is,
> beside the claimed one.

### Slope already cost something, and the hierarchy was the half that did not know

The sharpest correction of the session. `PathService.FlowStepCost` has charged
`|height change| × ClimbSecondsPerMetre` on every edge since long before there was relief to charge it
on — a 30% penalty at a tenth grade. A slope band added to the rasteriser was therefore a **second
source of truth for how fast ground is**, the exact mistake `TerrainSurfaceRules`' own remarks record
having made once and fixed. The edge term is also the better model, because it is path-dependent: a
contour route pays almost nothing where a direct climb pays the lot, so a switchback is cheaper than
going straight up and nobody modelled switchbacks. The band was deleted.

What *was* broken is one layer up. The fine field charged climb and the hierarchy priced a rectangle
crossing as a straight line on the flat, so with relief the abstract layer was **cheaper than the routes
it abstracts** — 0.977 of the exact field at three metres of amplitude, 0.845 at twenty-four.
`RectangleFlowField.Expand` already states the invariant that breaks: an over-estimate is the safe
direction, because a body must never be told a shortcut exists when it does not. Legs charge climb now,
sampled along their length rather than end to end, since a leg over a summit climbs *and* descends and
its endpoints can be level.

And the fragmentation §54 feared was real but not where it was predicted: the rectangle mesh merged cells
whose heights agreed to within **five centimetres**, which is exactly what a tenth grade climbs across
one half-metre cell. The rule said *flat* where it meant *no unclimbable step* — a local property tested
globally, and the raster already knows the local one. Merging on whether a body can step between two
cells took 250,796 rectangles to 7,191, and once slope stopped being a per-cell cost, to **one**.

### One ground, and three representations of it deleted

The ground was drawn three ways: a whole-map mesh for worlds under 21,000 cells, flat plates every five
metres above that, and flat patches over the top wherever cells disagreed with their plate. Three
representations, three sets of artefacts, and this session found one in each — plates terracing on a
slope, patches banding on curves, and the mesh switching itself off at a size no played map is under.

The cap was a `ushort` index buffer, not a statement about terrain. **The render grid is not the
navigation grid**: half a metre is the resolution a body's clearance is decided at, and nobody can see
half a metre from a two hundred metre standoff. At a target of 316 render cells a side the whole map is
90,000 cells and about 200k triangles — less than the near-field mesh and the plates were costing
together — and the chunk count comes out roughly independent of the extent.

Deleted with them: `BuildCoarseGround`, `BuildTerrainFeatures`, `GroundColumn`, `SlopedGroundColumn`,
`BlockJitter`, `BlockColor`, `TerrainRectangles`, the plate and patch batches, the plate/chunk filtering,
`UsesFineGround`, and two look dials that were properties of a ground made of flat plates.

**"Flat is deliberate" was true and is retired**, and it is worth being exact about why it was true: it
was written against the plate ground, whose cap was an index format. It also predicted the banding
correctly — the detail patch layer *was* the banding, and had been dormant rather than correct, because
it skips any grass patch within two centimetres of zero and on a flat map that is every patch.

### The couplings, and the guarantee that made them safe

Relief, woodland and ground cover were three systems generated into the same space with no knowledge of
each other. Now: **woodland follows the slope**, because a slope is hard to plough so forest survives on
it and the level ground gets cleared — which puts wood uphill and farmland on the level, so a site is a
trade rather than a place with the same resources in every direction. Tree *kind* follows the same
signal, cover thins on slopes and goes rank in hollows, and four biomes — pasture, moor, scree, marsh —
carry real surfaces, so a moor costs a tenth more to cross and a marsh nearly half.

Every one of those is a **modulation around one, faded out as the map's height range goes to nothing.**
The first version of the woodland coupling was not, and it halved the trees on every map: 11,177 became
5,294 on the flat, which is §22's economic constant cut in half by a decision about terrain on a map with
no terrain in it. That is the shape of the whole migration plan and it holds everywhere now — flat maps
found where they founded, grow what they grew, and measure what they measured.

### And the frame

Reported as five frames a second where it used to fly. Four causes, none of them guessable, and the
render side had no instrument at all — which is how a factor of twelve accumulated over an afternoon.

1. **Ground cover was 68 ms a frame, and not from drawing.** Forty thousand candidate cells, each
   sampling the height field for the relief coupling *and* the biome classifier, before anything decided
   whether the cell places so much as a tuft. Answered on an eight metre grid once per terrain change
   instead: 2.7 ms.
2. **Three quarters of the frame's geometry was ground nobody could see** — 204,800 of 276,315 triangles,
   most of it behind the camera. Built everywhere, drawn where it can be seen.
3. **The triangle counter reports per draw, not per instance**, so a frame drawing nine thousand trees of
   six thousand triangles reported "276k" and looked cheap. It was **63.5 million**. Anything instanced
   needs the product.
4. **Trees.** Which is where the interesting part is.

> **What a thing costs to prepare and what it costs to submit are different budgets.** I made that
> mistake twice in one afternoon in opposite directions: first assuming the meshed ground was
> unaffordable at all, then assuming that because building it was cheap, drawing it was too.

### Foliage decimates — once the cook lets it

The tree fix went through billboards and came back. Impostors are right for foliage that is mostly alpha
to cut out, which is the Sponza Modern case; ours is solid low-poly geometry, and a crossed pair of quads
seen from above is a shard. **Two flags in the cook tool were the whole story:**

- **`LockBorder`**, passed unconditionally. It pins every mesh-boundary vertex so spatially split chunks
  stay watertight — which matters with `--split` on and is meaningless with it off. On a model of many
  open shells nearly every vertex is a border vertex: a blade of grass went 326/224 and stopped.
- **`Prune`**, absent. A canopy is hundreds of small disconnected clusters, and a simplifier that may not
  delete a component can only thin each until it would vanish. For foliage, deleting components is not a
  compromise — what a canopy looks like from further away is fewer, larger masses.

With both: **4,345 / 2,076 / 972 / 454**. Three tiers read out of one cooked chain sharing one vertex
buffer, so the far bands cost index lists and nothing else. Then frustum culling, because a camera looks
at a wedge and two trees in three were behind the viewer; no casting from the far band, whose shadows
fall on ground the fog has taken; and **density as a second LOD signal**, since a tree in a thicket is
part of a texture and the terrain already knows which trees those are.

A latent engine bug fell out of it: the cooked-mesh runtime path looked its texture coordinate up by
attribute location 3, which is the UV in the 48-byte tangent layout and the tangent slot in nothing. Every
cooked asset so far came from VulkanSponza, which cooks with `--tangents`, so loading a cooked mesh
without them had never been tried and threw.

**125 ms and 63,552k triangles to 17 ms and 4,592k**, and 16.6 ms at a working zoom.

### The handoff

M1 of §54 is done and M2 is next: **approaches as a measured quantity**, the sixteen-bearing scorer. The
site chooser exists but scores "something at its back" as a proxy for it, and says so — the real measure
asks the router which bearings can reach a place and costs nothing extra, because relief, water and
woodland are three ways of closing a bearing and the router already answers the question. After that:
founding as the band-and-heap opening, water as a barrier, and stone.

Two smaller things left where they are, deliberately. `--placementcheck` asserts that a tree comes down
in three minutes, which at fifteen cutter-minutes a tree it cannot see; that is §51's tree grain item and
the check should move when the grain does. And the wear field, the hearth falloff and the shadow box all
turned out to be relief-correct already — the wear is indexed by world x/z, which is exactly right for a
height field — so §54's "the dressing still assumes flat" item is retired without work.

## 56. Levels of detail belong to crowding, not to distance

Three selectors for a tree's level of detail were built in one sitting. Two of them are the obvious
answers and both were taken back out; writing down why is the point of this section, because the one that
survived is the one nobody proposes first.

**Screen-space error, which is the textbook answer.** The cook records each decimated level's world-space
geometric error, so `error × viewportHeight / (2 × distance × tan(fov/2))` is that level's mistake measured
in pixels, and the crossover distance is where it reaches a threshold. Two divisions a frame, correct at
every zoom and every window size, no tuning. Measured, it put the crossovers at **four hundred metres** and
left every tree in the frame at full detail. The reason is not a bug: decimating a canopy moves leaf
clusters by tens of centimetres, so a tree's geometric error is enormous and *invisible*, where the same
error on a wall would be a hole you could see through. The metric is calibrated for surfaces whose
silhouette is the thing being looked at, and a canopy's silhouette is a statistical impression of ten
thousand leaves. It does not qualify.

**Hand-tuned distance bands, which is what everybody ships.** These went through two wrong shapes before
the right one. Measured from `cameraFocus` — the point the camera aims at — they draw a detail ring around
the middle of the screen, reported from the chair as *"getting the trees more pronounced through a circular
lens in the middle of the screen looks like I have tree god eyes"*. Measured from the eye they collapse, because
the eye is `cameraDistance` from the focus and a near band of 38 m is empty at any standoff wider than 38 m.
The correct form is **depth past the focal plane** — `cameraDistance + Δ` — which runs the bands up the
screen the way a level of detail should and is zoom-adaptive for free. That version worked, and was still
wrong, for a reason that has nothing to do with geometry: *any* distance ladder thins the far half of the
map at once, and the coarse levels shed interior leaf cards, so a wood going sparse as the camera pulls
back reads as **logged**. A player cannot un-see that. Reported as *"at a distance LOD just seems to reduce
apparent density by too much"*.

**Crowding, which is what survived.** A tree standing on its own is being looked at and keeps every
triangle however far away it is; a tree in a thicket is texture, and nobody can tell which trunk is which
at any distance. Where trees overlap, the neighbours fill in the mass the coarse level lost, so the thinning
lands exactly where it is covered — which is the property the distance ladder could not have. It also spends
the detail where a settlement is, since the ground round a village is cleared, so the trees a player works
among are the uncrowded ones by construction.

The field is a count of standing trees per ten-metre cell, rebuilt once a frame from the node table: one
pass, no spatial query. Ten metres is about two canopies across — finer and a tree is alone in its own cell
however thick the wood, coarser and a village's cleared ring averages into the wood beside it. It is
deliberately **not** the terrain's forest cover, which is a binary that says "two trees crowd this cell"
and cannot tell a copse from a forest; three levels need more than one bit to choose between them. And it is
a frame's field rather than a cached one because felling changes it — clear a stand and the survivors stop
being texture and start being trees, which is exactly when they should get their triangles back. Dressing
side throughout, per §52: nothing is remembered between frames and nothing is fingerprinted.

Distance keeps exactly one job, and it is a limit rather than a ladder: `treeDrawRadiusSquared`, set beyond
what the camera can see, and soon beyond what the player has scouted.

### The shadow is allowed to disagree with the scene

Once every tree cast its own shadow again — the far tier had been casting nothing, which is a large part of
what made a distant wood read thin, since a wood with no darkness under it has no mass — the sun's pass
became the pass a woodland actually costs. It draws every caster in the box whether or not the camera can
see one, and at maximum zoom that was 5.7M triangles against the scene's 8.6M.

So `PropModel` learned to build its caster from **different geometry than it draws**, which is the one place
the two are allowed to differ. A shadow is a silhouette resolved to an eleven-centimetre texel, so it
survives geometry the camera would refuse: the full tree casts from two levels down, and the middle and far
tiers both cast from the coarsest. Measured back to back, that took the shadow pass from 5.7M to 3.7M
triangles and the frame at maximum zoom from 24.5 ms to 22.0 ms, with no tree losing its shadow.

What is still forbidden is a different *instance list*. Every list — scene parts and caster parts alike —
is filled by the same `Add` call, because a caster drifting from what it casts for is a shadow under
nothing. The substituted casters are their own parts rather than a second batch hung off the scene parts,
since a pruned decimation need not have the same number of primitives as the model it came from and pairing
them by index would silently mis-tint or crash.

### On measuring on this machine

Two hours of continuous headless runs and the same build read 22.0 ms and then 38-41 ms. The tell was
`overlay`, a CPU build phase the tree LOD cannot touch: it moved by 1.74× while the frame moved by 1.77×.
The machine was throttling, uniformly. **Only back-to-back comparisons inside one window mean anything
here**, and every figure in this section is from such a pair. The absolute numbers are not portable and
should not be quoted as a budget.

## 57. Geography, from the one thing that makes geography

The map was "a few mounds and tree types". The fix was not more mounds or more types.

`Biomes.At` already knew what was missing, in its own remarks: *"Three signals, all of them about water,
which is what actually decides what grows where."* Grade, height and a twenty-metre concavity test — three
**local** stand-ins for one **global** quantity, and the limit of a local measurement is exactly this: a
hollow cannot know whether half a hillside drains into it. So rivers were never a feature to add beside the
other terrain types. A river is the visible part of the drainage network, and the network is what the
classifier had been approximating all along.

### The three layers, and why they are in this order

**`Drainage`** — depression fill, flow direction, flow accumulation, on a four-metre lattice.

Priority-Flood first, because nothing downstream works without it: a composed height field is full of small
pits, a pit swallows every drop that reaches it, and so every river on an unfilled map is four cells long.
Filling is one pass and needs no iteration, because always expanding from the lowest lip reached so far means
the first path to a cell is the lowest path there is. What it fills is kept as `LakeDepth` — knowing which
holes would hold water is a subtraction, not a second algorithm.

D8 rather than multiple-flow-direction, and that is a choice about what a channel *is*: spreading flow over
every downhill neighbour gives smoother accumulation and a broad damp smear where a valley floor should have
a river in it. Concentration is the point.

Accumulation walks cells from the highest filled surface down, which makes one pass sufficient — a receiver
is always lower than its donor, so the height field already **is** the topological order.

**`Erosion`** — `K · A^m · S^n`, plus hillslope creep, plus uplift, forty-eight times.

One line of physics, and everything recognisable falls out of it unasked: valleys, because a channel deepens
itself and so gathers more water and so deepens faster; a *network*, because two neighbouring channels
compete for one divide and the winner captures the loser; ridges, because a divide is where nothing has won
yet; concave valley floors under convex hilltops, because A grows downstream while S falls.

Creep is not optional. Stream power acts only where water has gathered, so alone it cuts knife-thin slots and
leaves everything between them exactly as smooth as it was — a hill with grooves scratched in it. Soil moves
downhill *everywhere*, which is a Laplacian, and it is what rounds the interfluves into land.

Uplift is not optional either, because "erode hard" without it erodes to a plain. So the composed landforms
became an uplift **rate** rather than the finished ground: each pass adds a little of them back and then
cuts. The landforms still decide where the high ground is; erosion decides what high ground looks like.

**And the forty-five metre noise octave was deleted**, because it was a fake of exactly this. Its own comment
said so — *"what breaks that on earth is drainage"* — and the two cannot both run: unorganised roughness at
the scale erosion works at hands the flow solver a hillside full of pits to fill, so the fake was spending
the real one's fidelity.

**`GradeLimit`** — the budget, re-established on the surface that actually exists.

`ReliefPlan.SteepestGrade` is computed from the shapes, and its remarks state the deal plainly: *"from the
shapes rather than from the height field, so it can be checked before a single vertex is written."* That deal
held while the shapes **were** the ground. It stops holding the instant erosion runs, because steepening
valley sides is the mechanism rather than a side effect. Five grade bugs in this file's neighbourhood were
caught by measuring the finished field instead of trusting the claim; this is the same lesson one step
earlier — make the surface obey rather than hope the process did.

Ascending order, lowering only, one pass: a cell is only ever pulled down by a cell already settled below it,
and lowering a cell can only make compliance easier for everything above.

### What it measures

| amplitude | largest catchment | channel | outlets | standing water |
|---|---|---|---|---|
| 3 m | 43.6% | 0.6 ha / 347 cells | 6 | 0.01 ha, 0.05 m deep |
| 12 m | 42.9% | 0.6 ha / 380 cells | 6 | 0.05 ha, 0.07 m |
| 24 m | 48.9% | 0.6 ha / 365 cells | 7 | 0.76 ha, 0.61 m |

**A largest catchment of half the map is the figure that matters**, and it is why the sweep grew a drainage
line at all. Erosion cannot be checked against a slope histogram — noise and a river network produce the same
one, and the entire difference between them is organisation. A map whose biggest catchment is a few per cent
has no rivers on it however rough its slopes. Half the map, seven outlets, and about 1.4 km of channel on a
600 m tile is a trunk river with tributaries.

The country the classifier now produces, at 24 m on the village map:

    66% meadow · 20% moor · 4% scree · 2% marsh · 4% water · 4% floodplain

against one biome and two before.

### The classifier, rewritten around two questions

How much water arrives (upslope area) and how fast it leaves (grade). Their combination has a name — the
topographic wetness index, `ln(area / grade)` — and it is a logarithm because area spans four orders of
magnitude on one map, so a ridge and a valley floor ought to be a few units apart rather than a factor of ten
thousand. Being scale-free is what lets one set of thresholds hold on a 600 m map and a 1200 m one.

Two biomes were added. **Water**, which is the only class allowed to overrule the shape of the ground — a
channel on a slope is still a channel. **Floodplain**: level, low, *and* beside water, all three, because any
two of them describe something else — level and low without water is a dry pan, level and wet without being
low is a hanging bog. It draws as grass on purpose: the difference between floodplain and meadow is soil, so
it belongs in what a field yields and where a site scores, not in what a boot finds underfoot.

### Water as a barrier, and the two things this got wrong first

Width goes as `0.017 · sqrt(area)`, which is not a fit — discharge is proportional to catchment, a channel is
about as deep as it is wide, so width goes as the root of area. One coefficient makes the trunk six metres
across and its headwaters something you step over, and nobody placed either.

**`Shallows` is a new surface, and it costs exactly what `Mud` costs.** Same speed on purpose — wading and
wallowing are both about a stride and a half a second — so reusing mud was tempting. What that costs is the
picture: mud is dark brown, shallow water is pale, and a brook drawn in mud's colour flowing into a river
drawn in water's is the kind of thing nobody can name and everybody sees.

**Two measurement bugs, both found by disbelieving a number.**

*Interpolating upslope area painted the map three times too wet.* Area is the most non-linear field here — a
trunk cell carries a hundred times what the ground one cell away does — so a bilinear read hands a fraction
of the trunk's discharge to its neighbours, and a fraction of a hundred is still a river. Seven per cent of
the map came out as water on a map with about two per cent of it in channels. Interpolation is right for the
wetness index, which wants to vary smoothly across a hillside, and wrong for a question whose answer is a
boundary.

*The trace threshold has to be set by what the lattice can express, not by what a stream is.* Eighty
centimetres — narrower than a stride — is the honest answer to when water stops being a decision, and it
still painted six per cent, because every damp line on every hillside qualifies and each is painted a whole
four-metre cell wide. A metre and a half is where the two agree.

### Known, and deliberately not fixed yet

- **Four metres is the narrowest channel this can express.** A one-metre brook needs the centreline extracted
  as a polyline and the water painted about it — which is also what would let a river draw as a ribbon rather
  than as painted cells, the same fix the road's staircase wants.
- **Lakes are ponds.** 0.76 ha and 0.61 m deep at best, because erosion is a pit-*destroying* process: it
  removes the basins that would hold water. A lake worth being a landmark needs a reason to exist that
  survives erosion, not a lower fill threshold.
- **"Fordable at low water only" is absent**, and `FordableWidthMetres` carries the note saying so. It needs a
  seasonal water level for the router to read, and inventing one next to the field that already knows how
  much water there is would be a second source of truth about the same fact.
- **The `--relief` sweep does not paint biomes**, so it reports drainage but has never tested whether an
  impassable river closes ground or cuts a map in two. It says "crossings 0" because there is no water in it.
  That is the next thing the sweep owes.

## 58. The frontier, and seven kinds of country

Two asks: tune the distribution, and make the exact map edge unreachable — *"more of a preference"* than a
restriction. The second one turned out to pay for three things at once, which is why it went first.

### The edge already was a wall. This gives it a reason.

The relief sweep has always reported `599 closed, of which 599 are off the map edge`, and the comment beside
that count says what it is: *"being outside the map is the other way to fail, and a body's whole outline has
to fit."* The boundary of the world was an invisible line at the extent that a body simply could not cross.
High ground and deep water do the same job while being something a player can see and reason about.

**Uneven on purpose, because it is a preference and not a wall.** A rim of constant height reads as exactly
what it is — the edge of a level — and seals the map into a box. Modulated on a 205 m wavelength it becomes
country instead: two or three stretches that stand up as crag, two or three that are merely high ground
somebody could walk over, and the gates cut clean through. Discouraged in most places, stopped in some. That
is the difference between a region and an arena.

**It earns its keep three times.** The edge stops being an arbitrary line. Water is funnelled into two gates
instead of leaking off all four sides, so the trunk rivers roughly doubled their catchment and now read as
rivers. And the interior finally has ground enclosed behind high land — which is exactly what §57's "lakes
are ponds" needed, because erosion *destroys* basins and standing water needs a reason to exist that erosion
cannot take away.

### Three orderings that are the whole design

**Gates are chosen before the rim, from the un-rimmed field.** Choosing them afterwards would be choosing
them from ground the rim had already raised, and a gate cut through the highest part of a frontier is a gate
no water reaches. It also avoids the failure this arrangement invites: if every outlet sits above much of the
interior, depression filling floods everything below the lowest sill and the map becomes a lake. Two gates,
at least a third of the map apart — two side by side are one gate, whereas two on opposite quarters give the
drainage competing destinations, and that is what makes a divide run across the middle of the map.

**The grade budget applies to the interior and deliberately not to the frontier.** The budget exists so the
ground a settlement lives on stays connected; a frontier's whole job is to not be connected. Limiting it
would cap a mountain wall at a walkable slope. `GradeLimit` took an `insetCells` parameter for this.

**And that exemption is what makes `Crag` need no special case.** The new impassable-rock class is told from
`Scree` by a plain grade test at 0.55, and the test is sound only because the interior *cannot reach* that
grade — the budget is about a third and the worst a bilinear corner turns that into is a half. The threshold
sits in empty space with a gap either side, so which side of it a place falls on is never in doubt. It is not
a knob for tuning how much crag there is.

### Tuning: what moved and why

The rim changed the ground enough to invalidate four thresholds at once, which is the honest cost of making
the terrain causal — the numbers describe real ground rather than being free parameters.

| | before rim | after, untuned | tuned |
|---|---|---|---|
| meadow | 66% | 66% | 58% |
| moor | 20% | **0%** | 11% |
| scree | 4% | **16%** | 5% |
| marsh | 2% | 4% | 5% |
| water | 4% | 7% | 6% |
| crag | — | 6% | 6% |
| floodplain | 4% | **0%** | 10% |

**Moor vanished because height is normalised and the denominator moved.** A rimmed map's interior is incised
far more deeply than a bare one — the measured interior range went from 24 m to 41 m at the same amplitude —
so "above half of it" stopped being anywhere.

**And the fix was an ordering, not a number.** With scree tested first, high ground only became moor where it
was also gentle, and eroded high ground almost never is. But a moor is not level ground; it is ground that
*sheds water*, and in upland country most of it is on a slope. So high-and-dry now claims a place before
steep does, and scree takes what is left over. Which reads correctly as well: heather over the shoulder of a
hill, bare stone where the shoulder breaks.

**Floodplain at a wetness of 9.5 put a quarter of the map under silt**, because 9.5 is a hillslope with a few
hundred square metres above it — most of a hillside. Half a unit of a logarithm is a factor of *e* in the
catchment, which is the difference between "water passes here" and "a river laid this down".

**The width coefficient was recalibrated once, and only because the frontier arrived.** Funnelling the water
into two gates roughly doubled the largest catchment, so every channel on the map got wider for a reason that
had nothing to do with how wide a channel should be. `0.017 → 0.013`. That is the single coefficient working
as intended, and the reason to keep exactly one of it.

### Two mistakes worth recording

**An edit landed on the fallback branch.** `Biomes.At` has two scree tests — one in the no-drainage fallback,
one in the real path — and the first occurrence in the file is the fallback. Scree stayed at 16% through a
change that looked applied and was measured as having no effect. The measurement caught it; reading the diff
would not have.

**Floor and span had to become interior-only in two places**, and they are two separate copies of the same
100×100 sampling loop — one in `SettlementScenarios.PaintBiomes`, one in `RtsGameLoop`. Both now inset by the
rim width. That is one fact with two owners, which this file's history says is a thing that drifts; it wants
collapsing into one method next time either is touched.

Checked: `worst-stuck 0.000` over 400 frames with 12% of the map impassable, so neither the water nor the crag
traps anybody.

## 59. Flora belongs to the country, and a lake cannot be bigger than its catchment

Three reports from the chair, and each of them named a rule rather than a symptom.

### "Looks a bit curved inwards"

**Erosion cannot shape a divide, which means it cannot shape the one thing I asked it to shape.** Incision
goes as upslope area; a rim's crest has nothing above it and its outer face drains straight off the map edge
and gathers nothing either. So the frontier stayed the smooth analytic ramp it was added as, and forty-eight
passes of hillslope creep took out what little texture that ramp had. A smoothed ramp is a curve. The
complaint was precise.

Two modulations do the work erosion will not:

- **The foot wanders**, on a ninety-metre wavelength, half the rim's width of swing. This is most of the fix,
  because a wall is recognised by being *parallel to something* — and once the mountain front stops being an
  offset copy of the map edge it stops reading as a boundary and starts reading as a range with spurs and
  re-entrants.
- **The crest carries a second, shorter wavelength**, multiplied rather than added so it cannot lift the
  frontier where the long wavelength meant it to be low. A col has to stay a col.

Creep also came down from 0.22 to 0.14: over forty-eight passes the diffusion was outrunning the incision and
taking the definition back out of the valleys it had just cut.

### "Large water bodies can't be placed upstream"

Correct, and the code was violating it for a structural reason. **Priority-Flood raises every hollow to its
spill point, because that is what routing requires** — so a broad shallow dish high on a hillside comes back
carrying twenty centimetres of "water" across the whole of it, and drawn, that is a large lake sitting above
anything that could fill it.

The fix is the rule as stated: standing water now needs a catchment, `LakeCatchmentMetres2 = 12,000 m²`. The
threshold is deliberately the same order as the one a channel needs, so that standing water and running water
are the same claim about the same field — a lake sits on a watercourse, and a dry hollow stays a dry hollow
however deeply the filler filled it.

**It took water from 7% of the map to 1%.** Six of those seven points were never lakes.

### "Trees growing straight in the middle of flood plains"

Woodland density was a function of slope and height. The slope rule is a good one and human rather than
botanical — *a slope is hard to plough, so forest survives on it and the flat gets cleared* — and it is blind
to everything else about the ground. A floodplain is level, so the rule made it prime forest. A level silted
river-flat is in fact the first ground anybody clears and grazes.

`WoodlandFor(Biome)` now modulates it, and every number is a reason:

| | × pasture | why |
|---|---|---|
| water, crag | 0 | not soil |
| floodplain | 0.14 | cleared, grazed, seasonally wet — a willow fringe is what is left |
| marsh | 0.26 | drowns roots |
| moor | 0.38 | exposed, thin soil: stunted and scattered, not absent |
| scree | 0.42 | little to root in |
| meadow | 1 | the baseline, and the migration guarantee |

Meadow being exactly one is what keeps §22 safe *by construction*: a map with no relief classifies as all
meadow, so nothing here can move a flat map's tree count.

**Rooting is separate from density, and has to be.** The near band is exempt from every shaping rule because
it is the year's starting fuel — thinning it because of the terrain would cut an economic constant as a side
effect. But a tree standing in a river is not thinning, it is a lie about what that ground is. So `CanRoot`
vetoes water and crag everywhere, including the near band, while leaving the count alone.

Three more things followed from the same reading:

- **Species.** Floodplain gets the twisted form standing in for a willow — what survives on a river flat
  tolerates being underwater half the year and grazed the rest. Crag gets conifer.
- **Cover density, not just cover species.** The species mapping alone gave every country the same *amount*
  of cover in a different shape, so a moor and a water meadow were equally shaggy. `lushness` runs from 0.22
  on crag to 1.30 on floodplain, around one so pasture is untouched.
- **Crag scatters square broken stone and never rounded pebbles**, because a crag sheds angular rock and a
  rounded pebble is what a river makes — the other end of the map entirely.

### The interior-relief duplication, closed

§58 recorded that the 100×100 interior sampling loop had two owners and would drift. The third caller arriving
is when to fix it, so `InteriorRelief(world)` is now one method with three callers. `RtsGameLoop` keeps its own
copy because it is a different class and reads it per terrain change; that one is still outstanding.

### A gate failure that was mine

The year leg reported FAILED, and the cause was `pkill -f RTSGame` run to close a headed window while the gate
was mid-run. Run alone that leg is fine. Worth recording because the log looked exactly like a regression —
day 0 printed, then an immediate non-zero exit — and the only way to tell the difference was to reproduce it.

## 60. The map lab, and archetypes instead of noise

The framing that produced this section: at 600 m there is not enough canvas for geology, so a generator that
reasons about watersheds and mountain ranges is answering a question the map is too small to ask. What fits is
**one small landscape with a strong geographic idea** — two large geographic statements and three to five
secondary consequences, and anything beyond that is theme-park geography.

That changes the first layer and leaves the causal ones alone, which is the right shape: drainage, the wetness
classification, flora by land type and the ground blend all stay exactly as they are. They were always the
second and third layers.

### What it retired

- **`ReliefPlan`'s six random landforms.** Composing mounds and hoping is precisely "starting from noise and
  hoping interesting geography appears".
- **Erosion as a shape-maker.** Forty-eight passes was right while erosion was the only thing deciding what
  the map looked like; it is wrong once the shape is authored, because fifty rounds of stream power turn *any*
  input into the same mature dendritic texture. Twelve passes now — a finisher, enough for channels to
  organise and valley floors to go concave, not enough to forget what it was given. This reverses an earlier
  decision ("erode hard, many passes") deliberately rather than by drift.

### Three separators, three mechanisms, and the split is not arbitrary

- **A ridge becomes landforms** placed along its path — so it inherits the grade budget and the soft-maximum
  composition that took five measured bugs to get right. Realised any other way it would have to earn all of
  that again.
- **An escarpment becomes a lattice step.** It is the one statement here that is not symmetric, and a landform
  is radial: high on one flank and low on the other is a signed distance to a line, not a sum of hills. Built
  out of hills you get a ridge with a plain on both sides, which is the opposite of a shelf.
- **A river becomes a trough and an inflow**, carved *before* erosion so erosion deepens a valley that is
  already there instead of inventing one elsewhere. The trough decides where; erosion decides what it looks
  like.

**And a connector is the separator not being there.** A saddle is an absence — anything *added* to a ridge to
represent a way through is a bump in the middle of the gap. So connectors scale the relief down, which is the
same mechanism that already cut the frontier's gates.

### The lab

`--maplab`, on a canvas larger than the game's map. `Q`/`E` cycle archetype, `Y` rolls a new seed, the arrows
slide a 600 m window in eighths, `Enter` prints the pick:

    pick: --archetype DiagonalRiver --seed 1592594996 --window 0,0
        "settlements facing each other across a river valley"

The triple is the whole identity of a map, because everything under it is deterministic. **And the sentence is
the acceptance test, not a label** — a layout that cannot be said in a line has too much happening on 600 m of
ground, so it is printed next to the map it is judging.

**The larger canvas is the point, not a convenience.** A 600 m window cut from a coherent 1800 m landscape is a
*fragment*: its river genuinely comes from off-window, its ridge genuinely continues past the edge. That is the
property the inherited-inflow constant was faking, and framing a crop makes it true instead.

### The bug that took five rounds, and what it taught

Symptom: an 1800 m canvas came out a quarter marsh on thresholds tuned at 600 m. It looked exactly like
thresholds sliding with the extent, so I normalised the wetness index by the map's area — which fixed that end
and left the same canvas 81% pasture. **When both ends of a range go wrong together, the fault is upstream of
both.**

It was two faults, in fact:

1. `InheritedCatchments` was written as a multiple of the canvas. A river's upstream catchment is a fact about
   the country it came from; it does not change because the window you are looking through got wider. Viewing
   1800 m poured nine times the water into the same watercourse. Now an absolute area — two 600 m tiles' worth
   — so a picked window sees the river its own size implies whatever canvas it was framed on.
2. **An absolute wetness cannot be portable at all**, inflow aside. A bigger canvas has longer hillslopes and
   therefore genuinely bigger catchments, so ground that is merely damp at 600 m is a fen by the same number at
   1800 m. No rescaling of the index fixes that, because the difference is real.

So the classifier reasons in **ranks**. A rank has no units and cannot slide, and it is the more meaningful
claim anyway: "wetter than nineteen twentieths of this landscape" is what a fen *is*, whereas a number of
log-square-metres is a proxy that happens to work on maps of one size. The dials read as sentences — the
wettest twentieth is fen, the wettest fifth of the level low ground is floodplain, the driest high ground is
moor — and the distribution stopped being something that emerges and became something that is asked for.

Measured across extents, at one amplitude and one archetype:

| extent | meadow | moor | scree | marsh | water | crag | floodplain |
|---|---|---|---|---|---|---|---|
| 600 m | 51% | 16% | 9% | 3% | 2% | 5% | 15% |
| 1200 m | 59% | 13% | 5% | 4% | 1% | 3% | 15% |
| 1800 m | 64% | 12% | 2% | 3% | 3% | 1% | 15% |

The wetness classes are stable by construction. The grade classes still drift — scree 9%→2% — and that is
correct rather than outstanding: the same thirty metres of amplitude spread over three times the ground is
genuinely gentler.

### Outstanding

- **Cropping is not implemented.** The lab prints a window and nothing consumes it yet; `--village` still
  generates 600 m standalone. Until it does, the fragment property is available in the lab and not in the game.
- **Eight archetypes are authored, and only their ridge/river/escarpment realisations exist.** Marsh, cliff-band
  and dense-woodland separators, and the ford/narrows/valley-mouth/clearing connectors, are named in the enums
  and not yet realised.
- **Nothing expresses the four-player relationships yet.** `MapLayout.Regions` and `Contested` are carried and
  unread. The point of them is that terrain should make the six pairwise relationships different from each
  other; today they are only positions.

### 60a. Unblocking the lab's zoom found three things, not one

"Unblock the camera zoom on the canvas scenario" turned out to be three separate faults stacked, which is
worth recording because only one of them was the limit it sounded like.

**A limit tuned for a different question.** `CameraFurthestDistance = 118 m` is a judgement about a
*settlement* — it sees about 165 m of ground, which is the village, its fields, its tree line and the shoulder
of the nearest high ground, and it is exactly right for that. It is meaningless for a canvas three times the
map wide, where the thing being judged is whether a whole landscape has one idea in it. Now a field, set from
the extent in the lab, because the two limits scale differently: the game's is about how much detail is worth
drawing, the lab's is about fitting the canvas on the screen. It costs nothing, because the lab has no trees —
the far end of the game's zoom is expensive for reasons entirely about foliage.

**An argument that never arrived.** `--zoom` was applied inside `LoadSettlementScenario`, which the lab does
not run, so the camera sat at the default forty-six metres. That reads as a clamp too, because the wheel could
not get out of it either. Two causes, one symptom.

**And the world ended in a circle.** Ground chunks were culled against `DetailRadius`, which is capped by a
look dial because it sizes the sun's box — and a box stretched to a kilometre has metre-wide texels, so the cap
has to stay. But it was also deciding how much ground *existed*: pulling back past the cap showed less of the
map rather than more. `GroundDrawRadius` is now separate and uncapped, because ground is the cheapest thing on
the screen — a chunk is about eight thousand triangles and the whole 1800 m canvas is under two hundred
thousand. This is the same class of bug this file already has a note about at the far plane.

**Then the fill rate.** All 25 chunks drawing came to 81 ms, and the cost was the 126 alpha-blended transition
coats covering a full-screen canvas. A crossfade band is a *detail*: at a standoff where the whole canvas is in
frame the render step is six metres wide, which puts the band comfortably under a pixel. So base coats draw to
what can be seen and transition coats to the detail radius — 219 layers at 81 ms became 108 at 18.5 ms with
nothing visible lost.

**One thing the primitive got right.** Skipping only the *draw* of a layer left its batch open, and the next
frame's `Begin` threw `InstancedBatch.Begin called while a batch is already active`. That is the correct
behaviour: a Begin without an End is a staged instance list nobody submitted, and it should be loud rather than
silently leaking a frame's work. Staged and drawn are now the same set by construction.

Gate green on all three legs afterwards, and the game path unchanged at 20.1 ms.

## 61. A canvas holds more statements, not a bigger one

"Each 1800 m map contains one interesting element, max two, so there's just no interesting 600 m map
possible." Correct, and it was a bug of mine rather than a limit of the approach.

**The archetypes were authored on a unit square and scaled by the canvas extent.** But every size in §60's
brief is absolute — a ridge of 150-250 m standing 20 m over its surroundings, a valley 80 m across, a hill of
30-60 m — and scaling by the extent stretched all of them threefold. An 1800 m canvas got one ridge system a
mile and a half long, so no 600 m window in it contained anything at all. Which is precisely the failure the
brief warns about — *realistic scale is actively your enemy* — reintroduced by making the canvas the frame of
reference instead of the map.

### Frame scale, not canvas scale

A layout is authored on a 600 m **frame**, because the archetypes are statements about *a map* and not about a
region. `MapLayout.Field` then fills a canvas with as many instances as fit, on a grid jittered by a third of
a cell, each rotated and mirrored independently, and — this is the part that serves the actual workflow — each
drawn from the whole archetype family unless one is pinned. So one roll of an 1800 m canvas is nine
neighbourhoods of different character, and the windows over them are genuinely different maps rather than nine
views of one:

    "a river through 9 neighbourhoods: 3x CentralHighGround, 2x TwinBasins,
     2x DiagonalRiver, Escarpment, SplitValley"

**The river is the exception and spans the whole canvas**, because a river is the one feature that really is
regional: it is what makes the canvas one place rather than a patchwork, and it is what gives a window its
off-window context. Instance rivers become tributaries — carved, but given no inherited catchment, so they
carry only what the ground above them sheds, which is what a tributary is.

**A tributary is also cut shallower than the trunk, and not for looks.** Carved to the same depth, two channels
of equal authority meet at a junction and the flow router has no reason to prefer either — so the trunk wanders
into a tributary's bed and out again. Half the depth keeps the hierarchy the drainage is meant to discover.

The seams that tiling would produce are handled by not tiling: the instances overlap, and every layer
downstream composes rather than partitions — ridges combine through the soft maximum, and drainage and the
classifier only ever read the finished surface.

### The lab finds the maps

The rule this whole layer rests on — *one map, one geographic sentence* — is **computable**, so making somebody
hunt for a good framing was the wrong tool. `SurveyWindows` walks every framing on an eighth-of-a-window grid
(the same step the arrows move in, so every suggestion is reachable and nameable) and scores it as the rule
states:

- **Statements** — separators crossing the window. Peaks at two, falls off either side, because four is
  theme-park geography.
- **Ways through** — connectors inside it. Two to four; none means a window cut in half.
- **Variety** — kinds of country present at more than a token share. All-pasture is a window with nothing to
  decide about.
- **Somewhere to live** — the share that is level, dry and open. A dramatic window nobody can found in is not a
  map.

On the canvas above, five well-separated framings at 0.96-1.00:

    1.00  --window 525,225    2 statements, 3 ways through, 4 kinds of country, 43% you could found on
    1.00  --window -225,-525  2 statements, 2 ways through, 4 kinds of country, 45% you could found on
    1.00  --window -600,-450  2 statements, 3 ways through, 4 kinds of country, 66% you could found on
    1.00  --window 525,-600   2 statements, 2 ways through, 4 kinds of country, 61% you could found on
    0.96  --window 225,600    2 statements, 2 ways through, 4 kinds of country, 32% you could found on

It scores framings rather than choosing one — it is an instrument and the eye still decides. Its real value is
being able to say when a canvas has *nothing* good on it, because that is a fact about the generator rather
than about the person looking.

**Known: the score saturates.** Four candidates at exactly 1.00 means it cannot rank the top of the field, only
separate good from bad. That is adequate for its job and worth fixing if the top ever needs ordering.

### And one bug the survey found immediately

Every framing reported "1 kinds of country". `CountryAt` reads the renderer's country field, which is rebuilt
inside the render pass and guards on the graphics device — so anything asking about country during generation
gets an empty grid and the answer "all meadow". Measured directly from the terrain instead, the same canvas has
seven. Worth recording as a shape of bug rather than an incident: a cache that is populated by a later phase
answers confidently and wrongly when asked early, and the wrong answer was a plausible one.

## 62. Region: the axis that was missing

"The presets look mostly the same terrain type — grasslands or highlands with some water here or there, green
all over."

Correct, and structural rather than a tuning problem. `Biomes` was one classifier with one palette, so the
code contained exactly **one climate**: temperate north-west European. The archetype layer varies *topology*
and nothing varied *character*. Two orthogonal axes, and only one of them existed — which is why eight
archetypes produced eight shapes of the same country.

### It composes because the classifier reasons in ranks

§60 moved the classifier off absolute wetness thresholds onto quantiles of the landscape's own distribution,
and the reason at the time was portability between map sizes. The payoff turns out to be this: **a dry region
is the same rule with the fen rank pushed to the ceiling, and a fen country is the same rule with it pulled
down.** No second classifier, no special cases, and the causal story untouched — what makes a place wet is
still how much water arrives and how fast it leaves.

A `RegionProfile` carries eleven numbers and four colours. The ranks read as claims about rainfall; the rest
are what a person would notice.

| | meadow | moor | scree | marsh | floodplain | trees | conifer | lush |
|---|---|---|---|---|---|---|---|---|
| downland | 57% | 11% | 9% | 4% | 15% | 1.00× | 25% | 1.00 |
| fen country | 45% | 3% | 7% | **21%** | 19% | 0.55× | 5% | 1.30 |
| upland heath | 52% | **35%** | 3% | 0% | 5% | 0.32× | 60% | 0.70 |
| dry scrub | 65% | 17% | 9% | 0% | 5% | **0.22×** | 35% | 0.52 |
| boreal | 62% | 10% | 8% | 12% | 5% | **1.65×** | 92% | 0.92 |

Eight archetypes times five regions is forty kinds of map, out of two small tables.

**The tree density is the column that matters most**, and it is worth saying why: boreal and dry scrub differ
by a factor of seven and a half, and a wood is most of what a person actually sees. Shape does less to tell
two maps apart than that one number does.

**And the palette had to stop being constants.** Five `static readonly Vector4`s meant a climate fixed in the
code: the archetype layer could vary the land all it liked and the answer to "what colour is grass here" was
the same on every map. Water and road stay global, because they do not vary that way — water is water, and a
made road is the colour of what it was made from.

### Three findings

**The moor rank was inverted.** The test is `wetness < quantile(MoorRank)`, so it is the share of the landscape
dry enough to count and a *higher* number means more moor. Set backwards, upland heath came out with **less
heather than downland** — 6% against 11% — which is exactly the kind of inversion that is invisible in the code
and obvious in one measurement.

**`MoorAbove` matters as much as the rank.** Moor also has to be high, and on a heath the whole point is that
it is *not* confined to the tops. Dropping the height gate from 0.42 to 0.18 is what took upland heath from 6%
moor to 35%.

**Scree had to vary too, and its absence was a third of why five regions still looked like one.** Thin-soiled
upland shows stone on a gentler slope than pasture does; a fen holds its turf on a bank a heath would have
lost. Every region had had identical scree and crag.

### The same bug as §58, in the same place

An edit landed on the **fallback branch** of `Biomes.At` again — there are two scree tests, one for maps with
no drainage solved, and the no-drainage one comes first in the file. §58 recorded this exact hazard after it
happened the first time, and recording it did not prevent it. The honest conclusion is that a note is not a
fix: the two branches want merging, or the fallback wants moving below the real path so that "first occurrence"
and "the one that runs" are the same line.

### Picks are now typeable

`--region`, `--archetype` and `--mapseed` on the command line, and the lab prints them in that form. `I` cycles
region, `U` pins the canvas to one archetype instead of the family. A printed pick that cannot be typed back in
is a note rather than a record.

### Outstanding

- **Ground-cover species are still keyed off biome alone**, so a boreal moor and a downland moor scatter the
  same wispy grass. The region should choose the species list, not only how much of it there is.
- **Upland heath's scree fell to 3%** because moor is tested first and now claims most steep high ground. It
  reads acceptably — heather over the shoulder, stone where it breaks — but it is the ordering doing something
  I did not ask for.
- **Region is lab-only.** `--village` still generates downland, because nothing plumbs a chosen region into the
  game path yet. That goes with cropping, which is also still outstanding.

## 63. Six confirmed bugs in the macro layer, and the diagnosis that found them

A review of the terrain code arrived with a central claim: **`MapLayout` describes genuinely different
geographies, but `ReliefPlan` collapses most of them into arrangements of the same elongated positive mound.**
The sentences differ more than the land does. Every specific claim under it checked out; four are fixed here and
the structural one is not.

### Confirmed, and worse than described: the grade ceiling knew about landforms only

`SteepestGrade` iterated `landforms` and nothing else. But an escarpment is cut onto the lattice by `Shelve` and
a basin by `Sink` — neither places a landform. So a layout whose separators are all escarpments had **zero
landforms**, the ceiling fell back to the map's bare tilt of about one per cent, and `GradeLimit` then held the
whole interior to one per cent.

The measurement that proved it is worth keeping, because the symptom pointed the other way:

    Escarpment    : over a 89 m height range          <- looks like the tallest map of the set
    SplitValley   : 87 landforms, steepest flank 0.39

Escarpment printed **no relief line at all** — `Describe()` returns "flat" when there are no landforms — and its
entire 89 m was the frontier, which is exempt from limiting. A flattened interior hiding behind a tall border.

The ceiling now takes the max over everything that shapes the ground: tilt, landforms, each escarpment's
height-over-width, each basin's bowl-and-dam gradient. Same lesson this file keeps relearning in new costumes:
**a budget derived from a subset of the things that shape the ground is not a budget, it is a cap on the subset
it knows about.**

### Confirmed: per-call jitter made incidence probabilistic

`Place(x, z)` drew fresh randomness on every call, so two *identical* canonical coordinates — a river's bend and
the ford authored to sit on it — became two different points, up to ±15 m apart on each axis independently. A
ford not on its river is not a ford; a saddle not in its ridge is a hole in a field.

Replaced by a coherent warp: a function of position, so the same input gives the same output and nearby points
get similar displacement. Incidence is now structural rather than lucky, and it reads more geographical too,
because real landforms bend together rather than each wandering off alone.

### Confirmed: rotation was amputating archetypes

Canonical coordinates reach (±0.50, ±0.36), which is a radius of 0.616 — further from the centre than the
frame's own half-width. Rotating a square inside a square does not preserve containment, and
`ReliefPlan.Place` *silently drops* landforms that do not fit. So "rotate the archetype" sometimes meant
"randomly amputate the archetype". Now inscribed at 0.70, which contains the worst canonical radius at every
angle.

### Confirmed: `Escarpment` was not an escarpment

`Shelve` iterated only a band of 1.2× the width around the path, so the high side was lifted for one width and
then dropped back to zero — a low raised strip with plain on *both* sides. An escarpment is high **everywhere**
on one side; the width is how far the *face* takes to fall, not how far the shelf extends. It is now a signed
distance that saturates.

Which immediately produced the opposite failure and taught something the single-map case hides: nine pinned
escarpment instances each lifted their own half of the whole canvas and stacked to **99 m on a 30 m amplitude**.
So `Separator` gained `ReachMetres` — a separator authored at frame scale is bounded at frame scale, one
authored for a whole map reaches the whole map. Escarpment now measures 49 m against the others' 45-50.

### Confirmed: the frontier was eating the map, and it is now off

78 m of frontier each side of a 600 m map leaves 444 m of interior — the border is **45% of the map's area**, and
at 1.55× amplitude it is also the tallest thing on it. Every map made one overwhelming statement before its
archetype got a vote: *you live inside a mountain-rimmed arena.* That homogenises everything downstream, and it
explains a good deal of "the presets all look the same" that §62's region layer only partly answered.

**Deleted rather than tuned**, behind `ReliefPlan.Frontier`, defaulting off. A dominant feature present on every
map cannot be evaluated by comparing maps. The code and its notes stay, because the problem it solved is real —
the map ended in an invisible wall — and what it got wrong is *where*. A playable 600 m inside an 800-900 m
rendered extent puts the scenery outside the gameplay instead of eating half of it.

### Confirmed and not fixed: `ConnectorKind` means nothing

`Saddle`, `Ford` and `Ramp` are exactly the right abstraction and the kind is never read. All three become
`Opening(position)`, a circular suppression of relief. So a ford is a circular hole in the relief that does not
narrow, shallow or otherwise touch the river — `AuthoredWidths` paints the channel at full width straight
across it. A connector needs to belong to a *specific separator at a parameter along it*, and each separator
needs to know how its own connectors modify it: a saddle lowers crest elevation, a ramp reduces face gradient
over a corridor, a ford broadens and shallows a bed. Those are three different operations wearing one name.

### The structural problem, which none of the above touches

**A ridge is built as a row of overlapping stretched hills.** However well `SmoothMax` blends them, that is a
mountain built out of sausages, and every member brings its own summit, lobing, radial flank and taper — so the
eye keeps finding the primitive. A ridge should be one heightfield: distance along a spine and perpendicular
distance from it, with crest height and width varying slowly along the length, and one to three deliberate
spurs branching off.

**And the valleys do not exist.** `SplitValley` generates two positive ridges with *nothing* between them — the
"valley" is merely where no hills were put. `TwinBasins` contains no `Basin` at all; it is one central ridge,
so "two fertile basins joined by one saddle" is implemented as "one ridge with flat ground either side". That is
not the same geography.

What is missing is a **macro landform field** with areal and negative primitives, not only positive
separators — ridge, valley floor, basin, upland, shelf. Four would transform it. And with only eight
archetypes, bespoke realisers are the right call rather than over-abstraction: expressing `CentralHighGround`,
`Escarpment`, `YValley` and `TwinBasins` through one generic `Separator(Path, Width, Height)` is forcing four
different shapes through one hole.

Measured after this session's fixes, the five archetypes still land within a few points of each other on every
country share — which is the same finding from a different direction.

### The gate for that work

A merciless lab view, before erosion or vegetation is touched again: top-down orthographic, greyscale height,
contour lines at 2 or 5 m, ridge and valley skeleton, connectors marked. **No trees, no grass, no biome colour,
no water material, no shader.** Run all eight, hide the labels, and identify them. If they cannot be told apart
from the 30-50 m low-pass shape alone, nothing downstream can rescue it.

The encouraging half of the diagnosis: the drainage, erosion and biome work is not wasted. It is arguably
overqualified for the crude macro field it is being fed.

## 64. The merciless view, and what it found in ten minutes

§63 ended with a gate: print every archetype as bare height, hide the labels, try to name them. Built as
`--shapes` — headless, textual, ten height bands in a terminal.

**Textual on purpose, and that is the more important half.** Every judgement about this terrain had needed
somebody to look at a window and describe it, which makes the loop as slow as a conversation and leaves whoever
is writing the generator working blind. A contour map in a terminal is a far worse picture and an enormously
better instrument: seconds to produce, trivial to compare against the last one, and readable by whoever holds
the keyboard. It found three things immediately, one of which nobody had named.

### The tilt was a third of every map's relief

`Tilt = amplitude / extent × 0.5`, with a comment reading "half a metre of fall per hundred, far too little to
notice on foot". True of the amplitude it was written against. At the amplitudes in use it came to 2.3 m per
hundred — **fourteen metres of fall on a thirty-eight metre relief** — so a third of every map's entire range
was one global ramp, in one direction, underneath whatever the archetype was trying to say.

Every archetype shared a background and none of them was mostly its own shape. It is now an absolute
`TiltFallMetres = 3.5` end to end, which is what tilt is actually for: giving water a direction.

**Found by printing shapes and noticing they had a common background, not by reading the code** — where the
expression looks small and the comment agrees with it. The comment was right about a number the expression had
stopped producing.

### Two archetypes were straightforwardly lying, and the dumps said so

    === SplitValley  "two ridges with a fertile floor between them"
        low 23% · middle 60% · high 17%      <- 60% undulation, no floor, no ridge

    === TwinBasins   "two fertile basins joined by one saddle"
        ... 1 separators, 1 connectors, 0 basins     <- zero basins

### Two new primitives, and the rule for when each is applied

**`Trough`** — a valley floor, cut rather than left over. Flat-bottomed, meeting its banks with no crease. The
absence of this is why "a fertile floor between them" was implemented as *the gap between two mounds*, which is
a different shape: a floor is flat, wide, and lower than the ground beyond the ridges.

**`Upland`** — areal high ground as one field, not a chain of mounds. This one is subtler and it is what makes
the negatives legible at all: **a basin cut into ground that is already the lowest thing on the map is not a
basin.** TwinBasins got its two basins and still read as "a ridge with low ground either side", because there
was nothing for the depressions to be depressions *relative to*.

And the ordering rule, which fell out of the drainage work:

> **Open negatives before erosion, closed negatives after.** A trough drains, so erosion deepens it and hangs
> tributaries off it — cutting it early is what gives erosion something worth finishing. A basin is a closed
> depression and erosion's first act is to fill it, so that one has to wait until erosion has finished.

Uplands go before troughs, because a trough is cut *into* whatever is there.

### What the dumps say now

    === CentralHighGround   "four sides around one defensible hill"
        low 89% · high 5% · 0 landforms, 0 separators

                     .,:;;::,
                .....,;+#@#+;.
               .....,;*@@@@#+-:.
                   ,;+####@@@*-:..
                   .,:;::;+#+-:...

    === YValley   "three valleys meeting at one lowland junction"
        low 2% · middle 67% · high 31% · 0 landforms, 0 separators

        -++*#@@#*+;,.  .:-#@@@@#*+-;;;
        -++*##@@#+:.....:+#@@@@#*+-;;;
        -+++*#@@@#+-;;;--+#@@@##*+-;;;

`CentralHighGround` and `YValley` now have **no separators and no landforms at all** — they are made entirely
of areal primitives, and both are legible from the shape. Which is the review's other point demonstrated: with
only eight archetypes, forcing four different shapes through one generic `Separator(Path, Width, Height)` was
over-abstraction. Letting them be different shapes cost less code than the abstraction did.

### Still outstanding

- **A ridge is still a row of stretched hills.** `SplitValley`, `BrokenRidge` and `CornerHighlands` still use
  the landform chain, and it still reads as its primitive. A ridge wants to be one heightfield: distance along a
  spine, perpendicular distance from it, crest height varying slowly along the length, one to three spurs.
- **`ConnectorKind` still means nothing.** Saddle, ford and ramp are all one circular suppression, and a ford
  still does not narrow its river.
- **YValley is 67% middle band** — the Y is visible but most of the map is undifferentiated slope between the
  valleys. It wants either more valleys or a smaller upland.
- **The frontier is off**, and the eventual answer is a playable 600 m inside an 800-900 m rendered extent
  rather than a border eating 45% of the map.

## 65. Two statements on one 600 m map, and the canvas retired

Two questions arrived together: how does this get looped into the game, and is the 1200/1800 m canvas still
needed. The answers turn out to be the same answer.

### The canvas was a workaround, and it is gone

It existed to be **searched**. Archetypes were not legible at 600 m, so the response was to generate a lot of
ground and hunt for a framing that happened to contain something. §64's areal primitives and the ridge
heightfield make an archetype read at map scale — which is where `--shapes` judges them — so searching now
solves a problem that does not exist.

Its one real contribution was the fragment property: a river arriving from off-map, a ridge carrying on past
the edge. **That never needed a bigger canvas; it needed correct boundary conditions**, and those were already
right. `InheritedCatchmentMetres2` is an absolute two tiles' worth of upstream country, so a 600 m river comes
from somewhere by construction, and a ridge that leaves the frame is a path whose end is outside it.

What replaces searching is **re-rolling**: 347 ms a map against 4,400 ms a canvas, so twenty seeds cost less
than one canvas did. And choosing between whole maps is a better question than choosing between framings of
one. The window, the survey, the pick-plus-window and the never-finished cropping all go with it. A pick is now
three values: `--region --archetype --mapseed`.

### One archetype is a thin map, and the score had been saying so

The rule is two large statements plus three to five consequences. An archetype contributes about one. Measured:
SplitValley scored 0.95 with three statements in frame; **CentralHighGround scored 0.76 with one**. The
statement term peaks at two, and a lone plateau on a plain never reaches it however good the plateau is.

So neither one nor nine. `MapLayout.Composed` puts a primary statement at full scale and a secondary at half
scale and 0.78 relief, and **the secondary goes where the primary is open** — which is what `Regions` is for. It
had been carried and unread since it was added. A primary's regions are by construction the ground it does not
occupy, so centring the second statement on one of them is both the cheapest placement rule and the right one:
two statements competing for the same ground is mush.

The secondary's river is retargeted onto the primary's nearest watercourse, because two unconnected
watercourses on one map is the "meets nothing" complaint again, one composition layer up.

Result — 4 to 6 separators, 3 to 5 connectors, 0 to 2 basins per map, and pairings that read:

    "a high shelf above a low plain, with a few ways up,
     and two ridges with a fertile floor between them off to one side"

### Four bugs the composition exposed, all of them mine

**Every archetype but one had no river.** `Field` had been supplying the canvas trunk, so collapsing to a single
archetype left seven of eight dry — Escarpment's thickest water was 4 m. Each now authors a river *as a
consequence of its own shape*: down SplitValley's trough, along Escarpment's foot where a slope meets a plain,
as YValley's own Y so the archetype states itself twice in agreement. That is the "three to five consequences"
half of the rule, which had never been implemented at all.

**`Separator.WidthMetres` was absolute while paths scaled with the frame.** A secondary at half frame kept
hundred-metre ridges while its paths shrank to eighty-eight, making every ridge wider than it was long; the
end-taper flattened what was left and the second statement arrived in the bottom two height bands. A width is a
proportion of a statement, not a constant of the world.

**The secondary fell off the map.** A primary's regions sit near its corners by design, and a half-scale frame
centred on one reaches 130 m further out again — measured, a secondary centred at 226 m radius with a reach of
130 on a map whose half-width is 300. A third of the second statement was outside the world.

**The pairing ignored the primary.** Seeded from the seed alone, the first draw is the same draw whatever the
primary is, so a sweep across all eight at one seed paired seven with the same partner. Correct determinism
answering a question nobody asked.

### And amplitude finally means its own units

The shaping primitives add: uplands, ridges and spurs each contribute lift and where they overlap the lifts
sum, so a layout with a massif and a ridge system came out at **92.9 m from a 34 m amplitude**. Erosion's own
rescale preserves whatever relief it is handed, which is right for erosion and no use here. A final
normalisation to the requested amplitude puts all eight at 29.7-33.7 m against 34.

Worth more than the tidiness: **amplitude is a number a person sets, and it has spent this whole arc not
meaning what it says** — first because the tilt silently added half again (§64), now because the primitives
compound. A dial that does not mean its own units cannot be tuned, only fiddled with.

### Next

1. **Loop it into `--village`**: accept `--region/--archetype/--mapseed` and generate from a layout instead of
   `ReliefPlan.For`, so the game plays the same generator the lab judges. `ChooseSite` already reads terrain.
2. **`ConnectorKind` semantics.** Still one circular relief suppression for saddle, ford and ramp — so a ford
   does not narrow or shallow its river. This went from cosmetic to load-bearing the moment every archetype got
   a river: a crossing is most of what a river adds to a map.
3. **Region-specific ground cover** — a boreal moor and a downland moor still scatter identical grass.
4. **The frontier's proper home**: playable 600 m inside a larger *rendered* extent, so scenery sits outside
   gameplay rather than eating 45% of it.

## 66. The sea, three composition rules, and a bug that had been hiding behind all of them

Two-at-random was still throw-and-see, and two things were missing outright: a sea, and a climate that does
more than recolour. What came out of building them was one bug that had been quietly causing several of the
complaints from earlier sections.

### The sea, which is the first absolute height this terrain has ever had

Every water level before it was **relative** — a lake fills to its own spill point, a channel stands above its
own bed — and relative levels cannot answer "how high is this place". A sea is a height full stop, so any cell
below it is under water without needing a depression, a catchment or a channel width to justify it.

It also does the deleted frontier's job properly. §63 removed the mountain rim because it ate 45% of a 600 m map
to hide the boundary; a coast hides the same boundary at no gameplay cost, because water already blocks. There
is no need to invent impassable scenery when the map can end in the sea.

Realised as a warped shoreline along one side, pulling the land *down toward* a shelf rather than replacing it,
so whatever the archetype put near the coast still shows through as a headland or a cliff. Measured on a coastal
map: **19% of the map water, thickest 347 m**, against 4-7% inland.

And the classifier got simpler rather than more complex. Lake depth, channel width and sea level are three
reasons a place can be under water, and the level field already resolves all three into one number — so
`Biomes.At` now asks the level for a depth and the sea needed no case of its own. Fordability gained a depth
term at the same time: it was a question about how far you have to wade, which is meaningless for a body of
water reporting zero width, and "a narrow gorge is not a ford" is better physics anyway.

### Three rules, and each one came from a measurement

**Rule one — the sea lies where the river was already going.** Placed independently, a coast gives a river
running off the wrong edge while the sea sits behind it: the "flows into nothing" complaint with an extra
feature on top. The seaward direction is taken from the trunk's own course, so the water reaches the water
without anything being routed to make it.

**Rule two — the secondary may not dam the primary's drainage.** Placed by "furthest region" alone, a
secondary's ridges landed across the primary's valley. Priority-Flood was doing exactly its job; the
composition had built a dam and not noticed.

**Rule three — whatever is meant to cross the map, crosses it.** This is the one that had been hiding.

### The inscribe factor was fixing one bug and causing another

§63 pulled every canonical coordinate in to 0.70 so rotation could not push part of an archetype off the map,
because `ReliefPlan.Place` silently drops what does not fit. Right for a hill. Wrong for anything meant to
*leave*: a river authored to ±0.52 ended at ±218 m on a map whose half-width is 300, and a valley floor at ±0.48
stopped 98 m short of the edge.

**A trough that does not reach the edge is a closed basin.** That is what flooded SplitValley — sixteen per cent
of the map under water and a 132 m lake sitting in the fertile floor, because the valley had nowhere to drain.
It is also, in hindsight, most of "the river meets nothing and flows into nothing" from §61: it was ending in a
field.

Positives stay inscribed; linear features are now extended along their own terminal headings until clear of the
boundary. The distinction that was missing is that an inscribed hill is contained on purpose and an inscribed
river is a river that stops.

    water 16% -> 4%   ·   thickest 132 m -> 60 m   ·   the valley floor is farmland again

### And only then did the climate become visible

`WaterScale` multiplies the water a layout's statements carry — authored channel widths and basin depths — so
dry country gets a river in a bed too big for it. Measured, it changed nothing, because the largest body on the
map was **emergent**: a hollow the layout never mentioned, fed by the trunk and therefore holding water in any
climate. So climate also scales the catchment a hollow needs before it holds water at all, which is what
evaporation is when you have one number for it.

With the drainage unblocked, both finally show:

| region | water | thickest |
|---|---|---|
| Downland | 4% | 60 m |
| DryScrub | 2% | 12 m |
| FenCountry | 5% | 60 m |

A river in dry scrub is a twelve-metre stream where downland has a sixty-metre body. Climate is now an input to
composition rather than a palette over it.

### Where the role system stands

The four roles — frame, spine, low, accent — are implicit rather than declared: the primary supplies spine and
low, the secondary is the accent, and the coast is the frame. That is enough for the rules above to be written
and is not yet a table anybody could read. Making it explicit is worth doing when a fifth rule wants adding,
not before.

Still outstanding, unchanged from §65: `--village` does not use any of this yet, `ConnectorKind` still means
nothing (and a ford matters more now that every map has a river), and ground-cover species are still keyed off
biome alone.

## 67. Looped in: the game plays the generator, and the year is measured on it

### One generator

The village used to build its ground with `ReliefPlan.For` — six landforms scattered from a seed — while the
lab built composed archetypes. **Every judgement made in the lab was therefore about terrain nobody would ever
play on.** Both now go through `MapLayout.Composed`, and `--region / --archetype / --mapseed` work on
`--village` exactly as they do on the lab. The legacy path stays for `--relief`, which sweeps amplitudes and
needs a shape whose steepest grade is known analytically.

Every run prints its own pick, so a map somebody liked while playing can be asked for again:

    map: --region Downland --archetype SplitValley --mapseed 1592594996
      two ridges with a fertile floor between them, and ... off to one side
      downland: temperate pasture, rough grazing on the tops

Founding lands where it should without any change to `ChooseSite`: **grade 0.005, twelve metres below the map's
mean** — the valley floor, which is what the archetype's fertile floor is for. `worst-stuck 0.000` over 700
frames, 28-33 ms.

### §54's last milestone, and why it was worth waiting for

The plan has owed a migration of the calibrated scenarios off flat ground since relief existed. The reason to
hold off was sound: a year-long economy assertion recalibrated against terrain that changes next week measures
nothing. The generator has now stopped moving, so it is worth doing.

`--settlement` takes relief, and the first run of a full year on generated ground:

    produced 8,161 grain, ate 7,474, went short 0
    31 alive, 5 born, 0 left because their household went hungry
    forest: 7,030 of 7,065 trees left
    per person per year: 270 grain against a nominal 270 · 128 wood against 120

**270 against a nominal 270.** The terrain coupling — woodland density by biome, path cost by surface, climb
charged per edge, a river to route around — costs the economy essentially nothing. That is a real result rather
than a tidy one: those couplings were each added on the argument that they would change how the settlement
behaves, and the honest finding is that they change *where* it behaves without changing *whether* it survives.

Tree count is 7,065 against 11,177 on flat ground, which is the woodland coupling doing exactly what §59 said
it would — a third of the map is now moor, water, crag or floodplain, and none of those carry pasture's
woodland.

### The gate has a fourth leg

    --selftest                              a broken rule
    --settlement                            an economy that cannot feed itself, on the flat
    --settlement --relief-amplitude 30       an economy that only works on a plain   <- new
    --raidtest                              a defence that stops defending

Flat stays the control, because that is where every economic constant was measured; relief is the second
question. Two measurements answering two questions rather than one answering neither.

### What is left

- **`ConnectorKind` still means nothing.** Saddle, ford and ramp are one circular relief suppression. This is
  now the largest gap by some distance: every map has a river, so a crossing is the main thing a river
  contributes, and a ford that does not narrow or shallow its water is not a ford.
- **Ground-cover species are keyed off biome alone** — a boreal moor and a downland moor scatter the same grass.
- **The role table is implicit.** Frame, spine, low and accent exist as a shape in the code rather than as
  something readable. Worth making explicit when a fifth rule wants adding.
- **Amplitude wants a settled default.** It finally means its own units (§64, §65); the lab is at 34 and the
  village at 30, chosen by eye rather than measured.

## 68. Connectors that mean something, and a proxy that had to break

`Saddle`, `Ford` and `Ramp` were the right abstraction and the kind was never read. All three became
`Opening(position)` — suppress relief in a circle — so a saddle authored for a ridge also holed whatever upland
it overlapped, a ramp reduced no gradient, and a ford did not touch its river at all. Three names for one
operation.

### A connector knows what it is a way through

`Connector` gained `ConnectorOf` and an `Index`. The discriminator is explicit rather than inferred from the
kind, because the inference *nearly* works and that is worse: a saddle is always in a ridge and a ford always in
a river, but a ramp is a way up a **face**, and the two things with faces are an escarpment and an upland. "Ramp
means upland if the layout has one" is the sort of rule that reads fine and silently targets the wrong thing.

Three kinds, three operations:

- **Saddle** lowers its ridge's crest, and only its own ridge. `Crest` and its spurs share the index, so a col
  opens the spur running off it too, which is what a col looks like from the side.
- **Ramp** *stretches a face* rather than lowering it. Suppressing height at a ramp cuts a notch, and a notch
  is a gully, not a way up — widening the run the same height falls over is what makes ground climbable. Three
  times the width is a third of the gradient and the shelf behind it is untouched.
- **Ford** narrows and shallows a riverbed, acting on `AuthoredWidths` rather than on the ground.

### The proxy that had to break

Fordability was `channelWidth < 2 m`, and it had always worked — because depth was *derived* from width, so the
two could never disagree. **A ford is precisely the case where they must.** It is a broad shallow place, gravel
rather than gorge: wide and crossable at once. Under the width test a map could author a crossing and get an
unbroken barrier, which is exactly what it did.

Depth decides now. Width keeps one job as an escape hatch rather than a criterion — a channel narrower than a
stride is wadeable whatever the depth field says, because at four metres a lattice cell the depth of something
two metres across is not a number to trust.

This is the third time in this arc that a derived quantity turned out to be standing in for the thing that
actually mattered: slope cost for path cost (§-early), width for depth here, and per-cell tests for per-body
properties twice over. The pattern is worth naming — **a proxy that agrees with its target in every case you
have tested is indistinguishable from the target until you build the case that separates them.**

### Two bugs found while wiring it

**YValley's connectors pointed at features that no longer existed.** They were saddles in the enclosing ridges,
and §64 deleted those ridges when the Y became cut valleys. Two connectors had been aimed at nothing for
several sections. They are fords on the arms now, which is what that archetype's ways through actually are.

**Composition shifts what an index means.** A secondary authored with "saddle in separator 1" means its *own*
separator 1, and after concatenation that slot holds one of the primary's. Unshifted, every composed map would
cut the second statement's passes through the first statement's ridges — a bug that would have read as bad
authoring for a long time. Indices are rebased on merge.

### And crossings are measurable for the first time

    DiagonalRiver  : 1872 crossable samples against 986 blocked
    SplitValley    : 1108 crossable against 667 blocked
    YValley        : 2897 crossable against 886 blocked
    Escarpment     : 1005 crossable against 1837 blocked   <- coastal, so mostly deep

A map can have a beautiful river and be two maps if nothing can get over it, and no figure printed so far could
tell the difference — area, thickness and depth are all silent on whether there is a way across. Escarpment
inverting the ratio is the coast being genuinely impassable, which is the point of a coast.

**Gate is four legs and all four green**, including the year on generated terrain.

## 69. The underwater dams, and the river that was every map

Two reports from one screenshot: straight "dam-like separators" between the deeper waters, and the diagonal
river defining pretty much every landmass.

### The dams were chunk overlap, and only water could show them

`WaterRenderStep` was derived from an index budget — `ceil(GroundChunkCells / 96)`, which is six for a chunk of
five hundred and twelve cells. **512 is not a multiple of 6.** The loop runs to `x <= toX`, so the last quad in
every chunk reached four cells past the boundary into its neighbour.

The ground coats overlap in exactly the same way and it has never mattered, because they are opaque: drawing
the same ground twice looks like drawing it once. Translucent water drawn twice does not — the alpha compounds
and the overlap appears as a dark line along every chunk edge, which at a forty-five degree camera yaw is a
grid of diagonals under the surface.

A chunk is sixty-four render cells by construction, so the ground's own step tiles it exactly. What the
half-step was buying — a finer shoreline — turns out to cost nothing to give up, because **the shore is a depth
fade rather than a mesh edge**: opacity goes to zero as the water thins, whatever resolution the quads are.

### A rule that did nothing, because of a units mismatch

Culling blend coats against the detail radius works when the map is larger than the view and fails when it is
not: at 600 m the whole map sits inside the detail radius, so every blend coat drew, each covering a large share
of the screen with alpha blending on.

The fix is screen-relative — a transition band is about one and a half render cells across, and under three
pixels there is nothing in it to see. It did not fire, and the reason is worth recording: **`frame.Height` is
physical pixels and `VisibleGroundRadius` derives from `host.LogicalSize`.** On this display they differ by two,
so every screen-space size computed from the pair was doubled and the three-pixel test was measuring six.

    lab      16.8 ms -> 9.0 ms   (81 coats + 76 blends -> 81 coats + 0 blends)
    village  16.4 ms -> 16.4 ms  (33 blends, unchanged — a gameplay standoff needs them)

Also, and separately: `metresPerPixel` was being computed *inside* the per-chunk loop, and
`VisibleGroundRadius` asks the host for the window size. Twenty-five calls a frame took the terrain build phase
from 1.4 ms to 13.1. One number about the camera has no business being recomputed per chunk.

### And a measurement error of mine, twice in one sitting

I read 77 ms, then 91 ms, then 94 ms, and started fixing a regression that did not exist. Those were **startup
frames**: the ground chunks rebuild once, and with `--frames 40` the run ends before the steady state arrives.
Over 200 frames: `terrain 35.6 -> 13.0 -> 1.0` and `FRAME 133.5 -> 54.8 -> 15.2`. §56 already records that only
same-window comparisons mean anything on this machine; the new lesson is that a *short* run is its own
confound, and the tell is a build phase that should be one-off being large in the sample.

### The river was every map because every river was equally important

The authored widths were backwards: **`DiagonalRiver`'s river was the narrowest of the eight at twelve metres**,
while the archetypes where a river is a mere side effect ran fifteen to twenty. And the carve depth was a single
constant, so a ten-metre consequence got the same trough as a twenty-six-metre statement — and erosion then
deepened both.

Three changes, and none of them adds a field:

- `DiagonalRiver` goes to 26 m and every consequence-river drops to 10-15.
- **Trough depth follows width**, because the authored width already says how important a river is — that is
  what authoring a width means.
- The inherited catchment comes down from two 600 m tiles to a little over one. It was chosen when the map was a
  window on a canvas, and it is also an *erosion* input: stream power goes as the square root of catchment, so
  doubling the inflow makes the trunk cut about forty per cent harder than anything else on the map, every
  pass. Which is how a river becomes the only thing a map is about.

The eight archetypes now spread, where before they sat within a few points of each other:

| | low | middle | high |
|---|---|---|---|
| BrokenRidge | 91% | 7% | 2% |
| DiagonalRiver | 84% | 10% | 6% |
| CornerHighlands | 83% | 13% | 4% |
| TwinBasins | 76% | 15% | 8% |
| Escarpment | 63% | 18% | 19% |
| SplitValley | 24% | 64% | 12% |
| CentralHighGround | 23% | 70% | 7% |
| YValley | 17% | 56% | 27% |

## 70. The map reaches the economy, and the ring that was hiding the design

> what should we build/address next in the game?

> seems like we have something ensuring the settlement is surrounded by woods, I wouldn't necessarily do
> that rather place a settlement around existing woods rather than change the map to fit the player (which
> anyway, we have building settlements)

Thirteen sections of generated geography (§57–§69) and the simulation had never once asked the map a
question. `EconomyRates.GrainPerFarmPerYear` was a flat constant, so a field on a river flat and a field on
a scree shoulder yielded identically; `Soil` existed, documented as "farming will want this next", with no
caller in the economy at all. Where a player founded was a scoring function's opinion rather than a
consequence. **The map was decoration.**

### Fertility: the first thing the economy asks the ground

`Soil.FertilityAt` is two terms, and the second is deliberately not the same shape as the first. Depth is
monotonic — more soil is more crop, always. Moisture is a **plateau with both ends falling away**, because
grain wants moist ground and neither parched nor waterlogged ground, and that is the difference between
farmland and fen. Note that `DepthAt` already rewards wetness monotonically through its deposition term, so
a marsh reads as *deep* soil; the hump is what makes it deep and useless — which is exactly what the remark
on the peat line already said out loud and could not act on.

Three decisions worth keeping:

- **Expressed as a multiple of neutral, and the neutral is derived rather than pasted.** One is literally
  "the ground every calibrated scenario was measured on" — level, meadow, middling water — evaluated by
  calling the same arithmetic every other case goes through. A neutral written out by hand is a copy that
  stops agreeing the first time either is touched, and stops agreeing *silently*.
- **Absolute, not a rank within this map** — the opposite of the choice §60 made for the biome thresholds,
  and for a reason: a rank would make every landscape feed its people equally well, and dry country being
  poor country is the whole point of `WaterScale`.
- **Read once, when the field is placed, at the single choke point where nodes are made.** Not per tick: the
  economy tick has no business reaching into terrain, a save has to round-trip what a field is worth, and a
  field's fertility is settled when the ground is broken — the same thing `CropCycle` already says about the
  ceiling. A map with no relief has no soil field to ask, so **every flat calibrated scenario keeps a
  fertility of exactly one by construction** rather than by a default.

It discriminates, which is the whole point: SplitValley's ridge country came out at median 0.64 — feeding
20 against 25 living there — where CornerHighlands managed 1.03 and feeds 32.

### The ring, and the causation it had backwards

`ScatterWoodland` planted 46 trees between 19 m and 29 m of wherever the village landed, and defended itself
as an economic constant — §22's starting fuel, which the year gate was calibrated against — while ending on:
*"It is also true of settlements: you found the place because there was wood round it."* **The correct
causation, stated in the comment and inverted in the code.** It did not put the settlement where the wood
was; it put wood where the settlement was.

It was also the last thing holding §51's first finding in place. Hauling has never been seen in a session —
no cart ever built, no stranded stock ever boarded — because a guaranteed ring of fuel inside every cutter's
reach means nothing is ever far from anything. **Distance cannot bite while the map is edited to remove it.**

### Two bugs in the site scorer, both found by deleting the ring

**Wood was a proxy for wood.** `ChooseSite` scored *slope within a cutter's reach*, steep ground being where
woodland survives the plough. Fair while trees were planted in a ring regardless of the land, because then
nothing could contradict it. `WoodlandCover` is now the actual answer — geography, ground, shelter, aspect,
soil — and a proxy for a quantity sitting in a field one call away is just a worse copy of it. The sixth
instance of the same lesson: *a proxy agrees with its target in every tested case until something separates
them*, and what separates these two is a fertile valley floor thick with trees — high wood, no slope at all.

**The grade gate was a cross-layer number.** `core > 0.11f` on the **max** of five samples across the
footprint. Fair when relief was smooth mounds, because then the max and the mean agreed. Erosion dissects,
so they stopped: measured across the candidate grid, SplitValley's median max-of-five is 0.243 against a
mean-of-five of 0.160, and the gate was throwing out **814 sites of 841**. Eighteen survivors is not a
choice of where to found, it is one place with rounding — and none of the eighteen had a tree near it.
Gating on the mean with a separate cliff guard took it to 231–499 eligible sites and three of four
archetypes founding with real wood in reach.

Every bug in §51's list had this shape and so does this one: a threshold correct in the layer that owns it —
a buildable grade really is about 11% — and wrong where it meets another, because one erosion gully clipping
a sixteen-metre ring disqualifies a hillside a village would sit on quite happily.

### The wood line, which is a two-metre coincidence

```
the wood line: nearest tree 32 m from the store, and within
30/60/90/120/180/260 m there are 0/71/95/332/1443/2149 trees
— a cutter reaches 29 m
```

Nearest tree 31–48 m across the archetypes; reach 29 m. `FieldKeepOut` (16 m) plus the scatter's spacing
puts the first trunk at about 31 m, and `ReachMetres` derives 29 m from `CutterWalkShare` — **two numbers
that had never been compared.** The ring bridged exactly that gap by planting at `FieldKeepOut + 3` = 19 m,
*inside* reach. That is what the ring really was: a workaround for reach being smaller than the distance at
which a tree can actually stand.

**`CutterWalkShare` was not touched, because `ReachMetres`'s own docstring already prescribes the answer:**

> The reach is measured from the store the cutter delivers to, which is what makes a lumber camp a thing you
> build rather than a thing you are given: when the near trees are gone, no store is within reach of any
> tree, the cutters say so, and the answer is a forward depot at the tree line. Everything that follows from
> that — wood accumulating somewhere nobody eats, and therefore carts — follows on its own.

That is precisely the state the settlement is now in, for the first time. Raising the dial would undo the
design to avoid exercising it, and it costs output anyway — `CutPerSecond` divides by `1 − walkShare`.

### Left unfuelled on purpose

The year now produces **no wood at all**, goes short 1,835, and lands 45 wood per person against a nominal
120. Decided from the chair: leave it, and report it. The remedy is a forward depot at the wood line, the
thing that puts one there is a player, and there is no interface to commit to that yet — so an unfuelled
settlement is the honest reading of a village that has not solved its wood problem. **Quietly planting fuel
next to it to make the column look healthy is how the ring got there in the first place.**

So the run says so in words rather than leaving a zero to be read as a bug: *the nearest standing tree is N
metres from any store and a cutter reaches 29, so no wood can be cut at all — the answer is a forward depot
at the wood line, and nothing in this scenario builds one.*

### Instruments added, and why each one exists

- **`farmland: N fields at a–b fertility, median m — G grain a year, which feeds P`.** In grain as well as
  in multiples, because a multiple is not a quantity anybody can be hungry against.
- **`why here: farmland, wood, backdrop, worst grade`.** A score of 1.69 says a site won and nothing about
  what it won on. Named terms are how slope-as-wood would have been caught years earlier.
- **`chosen from N eligible sites, the woodiest of which had W`.** A site chosen from eighteen candidates and
  one chosen from four hundred are different claims about the map and the score cannot tell them apart. The
  second half is the honest ceiling: if the best in the whole map is nothing, the settlement is not being
  sited badly, it *cannot* be sited well — which is true of `DiagonalRiver`, whose eligible ground tops out
  at 0.07.
- **`the wood line: nearest tree, and counts at six radii`.** The measurement that separated "woodland
  pressure in reach" from "a trunk in reach", after the first fix produced 0.38 pressure and zero trees.

### Two things this leaves

`ReportFarmland` was written and not called for an hour, printing nothing while I read its absence as a
result. And the fertility ordering is still correct by accident: a field reads the soil when it is placed, so
the country must be painted first, and it is — by two separate call sites neither of which mentions the
other. Nothing stops a third path from placing fields before the soil exists, and the symptom would be every
field reporting exactly 1.00: not a crash, not a wrong-looking number, just a map that quietly stopped
mattering. The farmland line is the guard against that, which is why it prints the spread and not the mean.

## 71. Stone: the first resource the player cannot move toward

> first stone, then we'll pick placement/building/and that whole chain next

`Resource` had a docstring arguing against this section. Grain and wood, it said, because "two is the smallest
number that produces the mechanic — they peak and bottom at different times of year, so storage is needed for
both on different clocks and summer's free labour is a real allocation decision with a deadline. **A third
resource adds bookkeeping and no new question.**"

Fair, and worth answering rather than ignoring. The answer is about the map.

- **Grain** is something you *make*. A field goes on any level ground you like, so where it is is your decision.
- **Wood** is placed by the land, but placed nearly everywhere — §70's woodland pressure leaves about a third of
  the map open and covers the rest. Which trees you cut is a choice among many.
- **Stone** is where the rock is, and the rock is where the generator put crag and scree: steep, high, broken
  ground. And `ChooseSite` rejects exactly that ground as unbuildable. So stone is **the first resource whose
  location the player cannot influence at all**, and the first that is always uphill and always outside the
  arrangement.

That is why it is not a third clock. Wood could always be answered by founding well — which is precisely what
the deleted tree ring was doing on the player's behalf. Stone cannot be answered that way at all, so the forward
depot and the cart stop being *available* and become *necessary*. Measured across archetypes at 30 m amplitude:

| archetype | outcrops | stone | nearest | against a 60 m reach |
| --- | --- | --- | --- | --- |
| Escarpment | 30 | 9,000 | 35 m | workable from the granary |
| SplitValley | 15 | 4,500 | 52 m | just workable |
| CornerHighlands | 16 | 4,800 | 86 m | **needs a depot** |

Which kind of map you are on is something you find out by looking, and it is the same claim fertility makes
about grain: how rich in a thing a country is belongs to the country.

### The ledger was written for two, and the compiler was not going to say so

Every store's lookup was `resource == Resource.Grain ? Grain : Wood`. That is not a shorthand, it is a trap:
a third resource compiles perfectly and **silently reads and writes the second one's field**. Every unit of
stone would have been a unit of wood, conservation would have balanced, and nothing would have said a word.
The remarks on `NodeStock` promised that "adding a resource adds a field and the compiler finds every switch
that needs it" — true of the field, false of the lookup.

Three shapes of the same hole, all swept before the enum grew:

1. **Two-way ternaries** in `NodeStock`, `NodePending` and `ResourceTotals`, now switches that throw on
   anything they have no field for. The compiler will not name the sites — it demands a default arm for cast
   values, and a build carrying three standing warnings teaches people to ignore warnings — but a resource
   added to the enum and not to the stores now **fails on its first tick instead of quietly becoming wood.**
   Loud beats early when the alternative is silent.
2. **Sweeps that add fields by name.** `total.Grain += …; total.Wood += …` is the identical hole with none of
   the syntax to warn about. Now `foreach (var resource in Resources.All)`.
3. **Assertions that name what they check.** `drift.Grain != 0 || drift.Wood != 0` would have gone on passing
   while a third resource leaked, and the fault message would have gone on printing two columns of zeros. Now
   `!drift.IsZero`, which is per-resource on purpose: one unit appearing and one vanishing sums to nothing and
   is two broken ledgers rather than none.

### And then the one that got through the sweep

```
stone: 62 quarried and held (20 stored, 2 on backs), 8,938 still in 30 outcrops,
       42 consumed — 9,000/9,000 accounted for
```

**42 consumed, and nothing consumes stone.** `EconomyRates.DrawPerSecond` had the same ternary — `resource ==
Grain ? grainRation : woodRation` — so stone fell into the else and **every household began burning it at a
villager's firewood rate.** Conservation was perfectly happy, because the units really had left the world
through a real door.

Worse than an aliased store, because an aliased store is a wrong number and this was an *invented demand*. And
it only surfaced because the report prints every term of the identity rather than one figure: the first version
said "20 quarried" beside 62 gone from the rock and no fault, which are two claims that cannot both be true.
An instrument that cannot be wrong about its own arithmetic is worth the four extra numbers.

The third bug was the work dispatch: `if (nodes.Get(siteId).Kind == NodeKind.Tree)`. A quarrier stood at its
rock all year and fell through to the crop code, which returned it as "not a farm" — no error, no work, and a
stone column of zeros that read exactly like an out-of-reach quarry.

### Two things I did and reversed

**A walk-share dial set before measuring.** `QuarrierWalkShare = 0.25`, on the argument that rock is on the
steep tops and a tenth of a year would leave every outcrop unworkable. Then the scatter was measured: nearest
outcrop 52–86 m against the 151 m a quarter buys. The dial had made stone *trivially* reachable from the
granary, which is the opposite of the paragraph written above it, and it would have shipped as a resource whose
documentation contradicted its numbers. **How much of its year a body spends on the road is a fact about the
body, not about what it carries** — so it is the same tenth as a cutter's, and the difference falls out of the
annual figures instead: 240 stone a year against 500 wood is fewer, bigger trips, so 60 m rather than 29.

**A predicate widened by one word.** `IsStanding` became "any natural deposit" so nothing else would need
changing. It compiled, and it was wrong in eighteen of twenty-five call sites: most of them mean *tree* — they
count trees, pick a fringe tree, draw a canopy, mark the cells a canopy closes. An outcrop passing those tests
would have been posted to woodcutters, drawn as foliage and counted in the forest. **Widening a predicate is a
change at every call site whether or not the compiler says so.** `IsStanding` went back to meaning a tree, the
general concept got its own name in `IsNaturalDeposit`, and the eight sites that genuinely wanted it — the
held/on-the-map line, the spent sweep, the deposit searches, haul sources, raid loot, hearths — were widened
deliberately.

### One behaviour, not two

The chain that sends a body to work — find the nearest deposit, re-base on a store that can still reach one,
spend a shift taking units out of it, walk them home — was written against `Resource.Wood` by name. Copying it
for stone would have produced two of everything, and the two would have drifted: **the reach, the claim check
and the re-basing rule are one behaviour.** `Deposits` decides nothing and owns no numbers; it only knows
whether to ask `Woodland` or `Quarrying`. Grain is not a deposit and returns zero rather than pretending — a
field is not a stock on the ground, it is labour in three windows, which is what `CropCycle` is.

### The rocks were already loaded

`SettlementArt.Rocks` held two models and drew neither, with a note: *"stone is about to be a resource, mined
from a deposit somebody chooses to work. Strewing rocks over the whole map as decoration teaches the player
that a rock is nothing to look at, which is precisely the wrong lesson to teach a fortnight before rocks start
mattering."* A fortnight later they are drawn only where a deposit is, so a rock in this scene means stone.
Shrinking on the cube root of what is left rather than a tree's square root, because a quarry is eaten into in
three dimensions and a trunk is felled in one.

### What stone deliberately does not have

**A sink.** Nothing is built of it, so one quarrier is posted rather than a crew sized against a demand that
does not exist — enough to prove stone moves out of the rock, into a pair of hands, into a store, with
conservation holding across a resource that has no production term. What it is *for* belongs to the placement
and building chain, which is where stone stops being a stock and becomes a cost.

## 72. Six bugs behind one another, and a library fix worth more than all of them

> the villager idea is good, let's extend that to the default interface
> tint is way too strong, disc invisible
> looks weird and inconsistent … the picker also tends to cut through the surface
> bug - no relief above 0 generates any trees beyond tiny groups
> if 54% of the map is at closed canopy but that 54% is spread out across the entire map in bunches of
> 3-4 tiles it won't actually read as a forest, only as an area with a lot of trees around

A long session of reported symptoms, each of which turned out to be sitting on top of the next. Recorded in
the order they were *found*, which is not the order they were reported.

### The selection layer, and three wrong attempts at it

Asked for "an on ground perimeter/background so I can see selected sites". Built an outline of four bars.
Withdrawn on report — at any thickness it read as a strip laid on the earth rather than a property of the
thing. Tried tinting the model instead, which is what a selected villager already does; also withdrawn,
because `world.frag` reads a tint as `albedo = vTint.rgb; surface = vTint.a`, so a tint does not highlight a
building, it **replaces** it — six materials flattened to one colour and the material class swapped along
with them. There was no weaker version available.

Third attempt: a translucent decal. Reused the contact-shadow pipeline, whose docstring is the argument for
it — "a disc is not a thing in the world, it is a mark on the thing under it". Invisible, because
`contact.frag` hard-codes `vec4(0,0,0,…)` and reads the instance colour not at all. Its own shader, then —
and rebalanced twice, first too faint and then, over-correcting, a near-solid blob.

**What survived:** one colour at two alphas, round for what grew and square for what was built, leaned onto
the ground with the same shear the contact shadows use. The square needed a mesh that carries *Chebyshev*
distance in the channel a disc uses for radius, so one shader draws both — tessellated, because Chebyshev
distance is not linear across a quad.

### Picking: three fixes, each exposing the next

- **The ground under the cursor is not the thing the cursor is over.** `NodeAt` picked by distance from where
  the ray met the terrain, and a ray goes through a granary's roof to land ~5.7 m behind it at this pitch.
  Replaced with ray-versus-box.
- **The box was a lie about the tree.** 7 m tall against a 4.5 m tree, so the ray clipped two metres of empty
  sky above a tree ten metres to one side. Measured from the chair: a tree picked **11.1 m from where the
  pointer met the ground**. Generosity belongs sideways, not upward — width costs a near miss, height costs a
  distant false hit.
- **The ray did not know the ground is opaque.** Anything it reached after meeting the terrain was behind the
  terrain, and was being picked.

And the one that mattered most: `TryProject` transformed every agent at a hard-coded world **y = 0.8**.
Correct at y = 0, which is why it survived; on ground running −21 m to +55 m it projected villagers tens of
metres from where they are drawn, and click and marquee both went through it. **The sixth flat-ground
constant in a world with hills**, after the far plane, the detail radius, the ground draw radius, the shadow
box and the site scorer's grade gate.

### The deep tier never drew anything

Reported as trees missing and the log lying. Both true. `treesDeep` was absent from `owned` — the list
`Stage` submits from and `Begin` clears — so every tree in the deepest crowding tier was staged into a model
that is never submitted and never reset: counted as drawn, invisible, and accumulating instances for the life
of the process. Diagnosed from the chair by pushing both crowding thresholds to 12 and 20, which keeps every
tree in a tier that *is* in that list.

It also makes a docstring in that file false. The deep tier was justified by "78 ms against 21 ms"; it
delivered that saving by drawing nothing, so the number was never improved on.

**Adding a tier is three edits — the array, the branch that fills it, and the registration — and only two
have a compiler behind them.**

### The instance upload, which was worth more than everything else here

Ground coats are one instance each, and there were 85 of them on a hilly map costing 3.0 ms. Thirty-five
microseconds for one draw of one instance is absurd, and the cause was not the draw: `InstanceBuffer.Write`
uploaded its **whole 16,384-instance capacity** every call, because `MaterialBindings.WriteBuffer` demanded a
payload exactly the size of the block. 1.31 MB per batch per frame, 111 MB a frame for the ground alone.

```
                     before      after
ground submit       3.01 ms     0.07 ms
record             11.5  ms     1.0  ms
frame @ zoom 118   40.1  ms    19.3  ms
```

A library fix: every batch in every demo was paying it. And the two follow-ups I had proposed — a compact
tree index, caching species per tree — were sized against a frame that no longer existed, so they were
dropped rather than done. **Work whose justification evaporates should be abandoned, not delivered.**

### Forest: share is not shape

Three rounds, and the first two were wrong in instructive ways.

1. **The floor.** A pastoral roll left `cover = 0.012 + 1.9t²` sitting on its floor almost everywhere: 267
   trees on a 600 m map. Raised the floor — and made it worse, because lifting the whole field uniformly
   spreads trees more evenly, which is the opposite of forest.
2. **Renormalising the product.** Took the baked field's own distribution and stretched a chosen share into
   closed canopy. The share became honest and the structure did not: 54% of the map at closed canopy, in **17
   patches of which 94% were under a third of a hectare**. Reported exactly: "it won't actually read as a
   forest, only as an area with a lot of trees around."

   The instrument was the problem too — "% at closed canopy" cannot tell one wood from a hundred specks.
   Replaced with a flood fill over connected closed-canopy cells, four-connected so touching corners do not
   flatter it.
3. **The cut was on the wrong field.** `Compute` multiplies five sub-unity terms, and a product of unrelated
   fields makes *spikes*, not regions: a high value needs all five to agree, and they have different spatial
   patterns. Geography alone is coherent at 230 m and 95 m, so its level sets are blobs. Cut that, blur it
   twice first so a threshold does not fray into islands, and let the other four vary thickness *inside* a
   wood without being able to veto it.

   Even then the cut field has to be the raw noise, not `Geography`'s output — that clamps to a floor over
   most of a pastoral map, so the quantile landed on the floor and passed everything. DiagonalRiver came out
   100% forest.

**Result:** 15–20 ha connected woods, 23% of the map wooded on a pastoral roll and 66% on a deeply wooded
one. `Compute` and `Geography` deleted rather than left switched off.

### Two dials re-measured twice, in both directions

The anchor budget was 62,000/km² when acceptance was 2–3%; it was really a division by the acceptance rate.
Renormalising raised acceptance tenfold and the same budget produced 31,000–57,000 trees. Cut to 6,000 to
hold the old *total* — which was the wrong target, because the old total spread thinly over a whole map is
woodland pasture when concentrated into two thirds of one. Reported: "our older maps were at least 2-3x more
dense." Raised to 25,000, and acceptance squared so that a bigger budget fills woods instead of peppering the
open — where a scattered tree is the expensive kind, because LOD is by crowding and a tree standing alone is
drawn at full detail.

**The number to aim at is density inside a wood, not a total across a map.**

### A negative result worth keeping

Capped drawn trees per canopy cell and widened the survivors, on the deep tier's own argument. It worked as
claimed — 4,130 trees and 4.1M triangles down to 1,915 and 2.45M — and the frame went 85.2 to 74.4 ms, which
is nothing. Reverted. **Whatever that frame is spending itself on, it is not tree geometry**, and thinning the
forest to discover that was the wrong order of operations.

### Culling, and what it says about the architecture

Trees were culled by a radius from `cameraFocus`, where `VisibleGroundRadius` is frustum trigonometry
measured from the **camera**. Two different points: the visible ground is an asymmetric trapezoid reaching
past the focus, so a circle centred there under-covers its far side — and under-covers more the closer the
camera gets. Once the ground began following the frustum this session, the two disagreed visibly: grass drawn
where trees were culled, trees vanishing on zoom-in.

Trees now answer to `InView`, with the radius demoted to a coarse bound at twice the visible extent whose
only job is to keep the far half of a big map out of the loop. The frustum test also moved *ahead* of the
crown width, which needs a woodland lookup — a cheap conservative cull before an exact one, which is the
whole trick and was backwards.

**The general shape**, agreed for the next arc: there are a dozen independent camera-centred scalars —
`VisibleGroundRadius`, `GroundDrawRadius`, `DetailRadius`, `SunOrthoExtent`, the tree bound,
`ScatterRadiusMetres`, `ContactRadiusMetres`, `ShadowReachMetres`, `FrustumMarginMetres`, the far plane — and
`GeometryLine()` exists solely to print "the distances that are supposed to agree with each other", which is
this file already admitting the problem. Every one collapses 3D frustum information into a 2D distance, which
is the flat-ground bug class at its source. Next: one far plane feeding every distance as a fraction, then
delete the radius prefilters wherever `InView` already decides.

### Stone got a sink

Construction costs are per material — `Construction.CostFor(kind)` returns a `NodeStock`, and
`TimberWanted` became `Wanted(resource)` plus `WantsMaterials`. The old member was **deleted** rather than
kept as a shim, so the compiler named all seven callers including one in the self-tests.

A granary needs 8 sacks of stone against 18 of timber. A cottage is timber and thatch; the depot stays
timber-only for the reason already written above its cost — it is the answer to a receding wood line and
cannot be priced in a resource some maps put 88 m out of reach.

Three places would have half-worked: the hauling board collected demand for wood only, `Raise` consumed only
the timber, and the site's visual measured delivery as wood-over-timber so a granary waiting on stone showed
a full stack of logs.

### And the village was playing a calibration map

`--relief-amplitude` defaults to zero, which is right for the headless runs — every rate was measured on a
plain and a fertility of exactly 1.00 depends on there being no soil field — and wrong for the thing a person
opens. It is why the HUD kept reading FLAT GROUND, why there was never any stone (bare rock needs relief to
be bare on), and why the woodland never showed its shaped form. The village defaults to 32 m now, and to
YValley, because DiagonalRiver's roll on the default seed is the sparsest thing the generator makes.

### The gate splits in two

Two of the five legs are simulated years — 162,000 ticks, sweeping every node on each for conservation — and
they gate every change including the ones that cannot possibly affect them. Reported from the chair: they
"take impractically long". The failure mode of a slow gate is not waiting, it is **not running it**, which is
strictly worse.

So: three quick legs by default (a broken rule, a defence that stops defending, a generator that only works
at one setting — everything whose failure is a property of a tick or a single run) and `--years` for the two
that answer "does this economy still feed itself", which no assertion can, because starving in the fourth
season is a property of a year. Quick tier: 341 s. The summary line names what was skipped, because a gate
that quietly runs less than it used to is a gate that lies.

**And a regression found by the split.** Chasing why the year leg had slowed from 1.13 ms/tick to 2.5–4.4, I
first blamed my own timeout, then the tree count, then a retry storm in the job layer — all wrong. It was
`TotalStored` and `TotalHeld` looping `Resources.All` through the switch-based indexer, twice per resource per
node, swept once per node per tick by the conservation check. A hundred thousand switch dispatches a second.

Back to `Add(in NodeStock)` with named fields, and `WantsMaterials` checks `IsBuilt` once rather than per
resource. 1.465 ms/tick. **Third time in one session that a loop over `Resources.All` — correct by
construction — was far too slow where it was actually called.** The rule now written in the code: a
hand-written sum is allowed only with a self-test pinning it to the generic one, and there is one.

## 73. Three cascades, and the batch that aliased its own push

> culling is still clearly fundamentally broken btw
> shadow needs the same fix, why do I have to keep spelling these out
> I think cascades are the correct solve here
> I'd start with the splits tunable and renderable, along with the frustum bounds being renderable
> still broken
> shadows are all wrong

One fitted sun box could be correct or sharp, never both. Wide enough to reach the far corners meant texels
too coarse near the camera; capping its width to keep the texels left the far corners standing in flat light.
Three boxes is the answer to that rather than a better single one.

### The instrument first, and it earned that twice over

`ShadowCascades` draws each fitted box and the frustum slice it was fitted to, and tints every fragment by
the cascade that shaded it. Those are two different claims — where the maps *are* against which one each
pixel *read* — and the gap between them is where a cascaded map goes wrong: the fit can be perfect while the
select misses, and the fragment quietly falls to a coarser box or to no box at all. Both failures look like
nothing until the frame is painted red, green and blue.

The user asked for exactly this before any pass was wired, and it paid immediately.

### Slicing the frustum is wrong for a camera that stands back

Every CSM reference splits the frustum from zero to the shadowed depth, and every one assumes a camera among
the things it looks at. This one hangs about 1.4x its focus distance back at forty-seven degrees, so the
nearest ground on screen is already most of the way to the focus and **the slice from half a metre to thirty
is empty air above the terrain**. Cascade 0 shaded nothing, cascade 1 caught a wedge, and the 1024-texel far
map did nearly all the work — three passes doing one cascade's job, visibly worse than the single box.

Cascades now split `GroundBand`: the span of view-ray distances over which this camera can see ground at all,
from the same trigonometry `VisibleGroundRadius` already uses. The splits went from 0.12/0.38 to even thirds;
0.12 had been right only because it was the one value putting cascade 0 anywhere near the ground.

Relief applies to both ends of the band and asymmetrically on purpose. The **near** pad is capped at forty per
cent of the standoff — taken raw, thirty metres of relief under a camera thirty-four metres up claims the
nearest ground could be four metres away, which is true only if a hilltop sits directly under the eye, and if
it does the camera is inside the terrain and worse things are already wrong. The **far** pad keeps the whole
span, because ground falling away really is that much further along the ray and cutting it short is what
leaves a wood unshadowed.

### Containment cannot pick the cascade

The second thing the tint found. Every box is the bounding sphere of its slice plus a margin, so the middle
box is wide enough to hold the entire visible ground: it wins every test and the outer cascade is never
sampled. Two colours where there should be three, and a boundary that curved with the box instead of running
across the view.

Depth picks the cascade now and the box only gets to veto, **outward and never inward**. A nearer box is
smaller, so it is less likely to contain anything, and if it did the fragment would be sampling a map fitted
to a slice it is not in. Cascade 0 is fitted from the camera rather than from the band, which is nearly free —
a slice's bounding sphere is dominated by its far cross-section, so 0.5→141 m gives a 238 m box where
80→141 m gives 226 — and it covers the near ground that would otherwise fall out of every box.

### And the thing that made it all look broken was not the cascade maths

Reported as "shadows are all wrong": detached from their casters, wrong scale, blobby.

**A recorded draw keeps its push-constant array by reference,** and `InstancedBatch.Begin` copied each new
push into the same internal array whenever the length matched. Invisible while every batch was begun once per
frame; wrong the moment one is begun three times. All three cascade maps were rasterised with cascade 2's
matrix and sampled with their own. Fixed in the batch, where the contract lives — `Begin` takes a fresh array
when the previous one has already been handed to a draw, so the common path stays allocation-free.

A latent bug, and cascades were the first caller to trigger it. **Look there first whenever a batch is reused
within a frame.**

### Measured after

`SHADOW 61>122>187>264 m` at 11.5/15.1/42.0 cm texels, against the single box's 130 m at 23.4 cm. Twice the
reach and twice as sharp where the eye is. `TREES` exceeding `SHADOW` is now expected rather than a bug: the
cascades cover the depth the texel budget affords, the far plane covers what the frustum holds, and the gap
sits entirely inside fog that closes at 240 m.

The threefold caster redraw — which I had flagged as the next necessary work — costs 0.38 ms and 59k triangles
a cascade against a 488k frame, so the partition is **not** worth doing. It would also cost more than it
looks: all three passes share one `InstanceBuffer`, whose `Write` targets one buffer per frame slot, so
per-cascade instance subsets need a buffer per cascade or every pass silently draws the last subset. Noted at
both sites where someone would try it.

Two more from deleting the tree draw radius one commit earlier: the collider overlay inherited the far plane
and overran the prop batch, taking the process down on a debug toggle; and the caster cull was still measured
from the focus while the boxes are fitted down-view.

## 74. A caster that leans, a slider that lied, and where the node phase goes

> looks better, let's proceed with 1 and then 2, then dig into 3

### The shadow of a tree that sways

`shadow_caster.vert` transformed its instance and stopped while `world.vert` displaced the same geometry
downwind, so a swaying tree had a still shadow sitting under it, shimmering as the gust passed. The lean lives
in `Shaders/lean.glsl` now and takes the wind as a *parameter* rather than reading it out of a push block —
which is the whole reason the file exists, since the two shaders have different push layouts and a function
reading `uWind` directly could only ever live in one of them. A caster and its receiver have to agree about
where the geometry is, and sharing the function is the only way to guarantee it.

### A control whose label described something it stopped doing

I read `shadow box (m) 60` off the panel and argued it was inflating the near cascade's box by forty per cent.
It was not. That is `ShadowFloorMetres`, which has not sized a shadow box since the box became fitted, and the
pad that widens each cascade is derived already — `TallestCaster / tan(elevation)`.

The real defect was the label, and it is worse than no control: it invited exactly the reasoning I did against
it, where halving the number would have changed nothing about shadows and quietly halved the draw distance.
Renamed to `MinimumDetailRadiusMetres`, with the stray factor of a half — a leftover of a side becoming a
radius — folded in.

### The node phase is a sweep problem, not a per-tree problem

11.2 ms at zoom 110 on a 34k-tree map, and **63% of it is the sweep**: visiting all 34,051 standing nodes to
find the 11,567 the frustum keeps. Per-tree work is the minority — 2.8 ms of fields and placement, 1.4 ms of
submission.

Six hypotheses about an expensive per-tree call died to code reading first: the heightfield, woodland, country
and tier lookups are all plain array reads, a placement is two SIMD multiplies, a tree model turns out to have
**1.8 parts** so a submission is under four appends, and the instance lists clear rather than reallocate. The
cost was never the work done per tree. It is how many nodes are visited to decide which trees to do it for,
and `EconomyNode` is ~144 bytes, so the sweep streams about 4.9 MB of struct per frame to read a position and
a kind.

### Three harnesses, and the first two were both wrong

Worth writing down, because both failures print numbers that look fine.

**A block of frames per level drifts.** The world runs on between blocks — woodcutters fell trees, the camera
eases — so the last level measures a different scene from the first. Symptom: a *negative* cost for
submission, the whole phase reading cheaper than the cull alone. The same "compared across two different
worlds" error made earlier the same evening, when `nodes` from one run was held against `TREENODES` from
another and called a contradiction.

**Interleaving the levels frame by frame fixes the drift and biases worse.** A level-0 frame writes megabytes
of instances and evicts the node array, so with levels cycling the cull is always measured cold and the fuller
levels warm. It read 20.6 ms where the truth is 11.2 — nearly double — and nothing in its output said so.

**Blocks, plus the tree count reported per level, and VOID if the scene moved.** The piece missing both times
was any way to *check* that the levels describe the same world. The valid run reports
34,051/34,051/34,051 offered and 11,567/11,567/11,567 drawn, and only then are the differences differences.

Also: `sample(1)` cannot symbolise .NET JIT frames and macOS CoreCLR writes no perf map, so ablation is the
tool here rather than a sampling profiler. And the per-node timing probe was two `Stopwatch` calls thirty-four
thousand times a frame — about a sixth of what it reported. Sampled one in thirty-two now, and it says so.

### What fog of war does to the fix

> so fog of war etc is on the table too - so let's keep that in mind while designing solutions

It rules out the option I was leaning toward. A compact array of tree positions wins bandwidth but still
visits every tree, so it cannot exploit "this whole region is unexplored" — and it would be thrown away the
moment fog lands. Fog is spatially coherent exactly like frustum visibility, so both want the same shape: **a
per-cell gate, with the per-tree work running only inside cells that pass.** A tree index grid at the canopy
grid's existing 10 m cells, gated on `frustum AND explored`. The ground chunks already do the frustum half
correctly, tested against their own height range.

Two constraints settled while it is still a design. **The gate goes above `DrawTree`, not inside it** — fog
will gate agents, buildings, piles and stone by the same rule, and this file's signature failure is a local
rule reimplemented per call site until the copies disagree. And **fog state stays out of the simulation**: it
is deterministic, being derived from unit positions, but the moment a sim decision reads it, "what the player
can see" becomes "what the world does" and view state enters the fingerprint. A deliberate exclusion, not
something discovered when a replay diverges.

One consequence for shadows: a caster in unexplored ground casting onto explored ground leaks terrain the
player has not scouted. Most games ignore it. Decide it in the per-cascade caster cull rather than by
accident.

## 75. Fog of war, and the camera that was aiming at sea level

> we'll be starting with fog of war exploration/implementation/design first, then get back to perf!

### The vision system already existed

`SimulationWorld.CanSee` was already there — per-body, forest-occluded, ray-marched at the navigation
raster, with exactly the "per hostile rather than per pair" budget §28 specified — and `ThreatSystem` was
already using it to leash pursuit. So item 1 of the combat arc's order of work, "a detection radius, occluded
by forest", was built. §31's line saying otherwise is stale.

That decided the shape of everything else. Fog derives from that predicate rather than from a radius of its
own, which means the fog and the simulation cannot disagree about what a wood hides — and it inherits forest
occlusion for nothing. §26 promised that a settlement's cut gaps would be "both the ways in and the only ways
it can watch"; this is where that arrives, and dense woodland interiors become permanent unknowns you can
only open by cutting, because `CanSee` stops at Forest and the interior is impassable anyway.

### The seam, stated as a direction

**Fog may read the simulation; the simulation may never read fog.** §74 had it as a location — fog stays
outside — which is right about exploration and cannot survive contact with combat: a §30 defence deciding
whether it is needed must read what its faction *knows*, and in a lockstep world that knowledge is carried
state that has to be fingerprinted. And §5's acceptance test forbids vision cheats, so the AI has to read the
same structure the player's fog is drawn from.

So the sharper form: **what the camera dims is view state and excluded; what a faction knows is simulation
state and fingerprinted.** They are different structures. The second is not built. When it is, the player's
fog should be *derived* from it rather than computed alongside it, or the two drift in the one way a player
notices — seeing a unit the simulation has decided is hidden.

The rule holds cheaply because what crosses the seam is a pure predicate rather than state. `DeterminismCheck`
records the exclusion, and its census alarm now reads that list: a session adding state to the world is
offered a fourth answer besides Carried, Derived and WallClock — that it belongs to the renderer.

### Two masks, on the canopy grid, because the gate has to read both

Ten metre cells, indexed by the canopy density field's own arithmetic — `CanopyIndex` delegates to it and
`CanopyCellMetres` is an alias. Two ten-metre grids computed by two copies of one formula is this file's
signature failure in a sixth costume, and it would have surfaced as trees popping along a boundary the player
can *see the fog at*.

`explored` is monotonic, which is what makes the round-robin refresh safe: a partial update can only add, so
there is no frame where scouted ground reads unscouted because its watcher's turn has not come. `visible` is
accumulated into a scratch mask and swapped when a cycle closes, so it is never read half-stamped.

Watchers are refreshed a few per frame for a real reason rather than out of caution: `CanSee` marches at half
a metre, so one granary asking about its 105 m reach is some three hundred and fifty cells at up to two
hundred samples each. And they are collected once per *cycle*, not per frame, because finding the buildings
means sweeping all thirty-four thousand nodes — adding a second full sweep per frame to feed the thing meant
to remove the first would have been its own joke. An empty watcher list has to be guarded or it sweeps every
frame looking for a granary that is not there.

### What the fog hides, and the reversal that settled it

> how about we start all unexplored, explored then becomes dimmed, and what pushes for scouting is not
> resources already visible on the map, it's requirements/pressures?

I had recommended the opposite — geography always visible, fog hiding only what people have done — on the
grounds that it protects §71's "a quarry is a landmark". The reversal is better and for a reason I had not
weighed: need-driven discovery is a stronger loop than window-shopping from a map you have always been able
to read. And it does not undo §71 so much as *gate* it. §71's actual complaint was that stone was invisible
from anywhere a player would stand; invisible until you have been there is a different and defensible thing.

The other half of that exchange was a question about whether rendering cost should influence the choice:

> or should I not let that influence this or vice-versa?

It should not, and this file has already paid to learn it twice. §72's drawn-density cap did exactly what
"cheap out on dimmed regions" would do — 4,130 trees to 1,915, and the frame went 85.2 ms to 74.4, which is
nothing. And the choice that is better for the look turned out to be better for the cost anyway: if unexplored
means hidden, unexplored cells submit no geometry at all, which is not a quality trade but a refusal to draw
what nobody can see.

### The two tiers had to become two layers

> unknown needs to be much stronger and denser than it is today — layering the unknown atop the known might
> look nice, but trying to blend them might reduce visibility in known areas

Exactly right, and it was a real defect rather than a tuning complaint. One scalar lerped through three tiers
means unknown and remembered share a ramp, so the only way to make unscouted ground properly opaque is to drag
the whole ramp — paying for it in visibility on ground already scouted, which after the opening reveal is most
of the frame.

Composited instead: the memory layer, then the deep bank over the top of it. What that buys is a guarantee
rather than a compromise — on fully known ground the deep term is *identically zero*, so its density is free to
go as high as it likes. The blur that keeps the boundary soft is the only place the two still meet, and an
edge-falloff exponent is what that costs.

### The veil is weather, and it was the only air on screen with no opinion about the sun

Twenty lines above it, the aerial perspective picks between a cold scatter away from the sun and a warm glow
toward it. The veil was mixing toward flat sky ambient, which is precisely why it read as something laid over
the scene rather than as part of it. It shares that pair now, at its own intensity — a bank on the ground and
kilometres of distance haze are not the same thickness of air — so `Atmosphere.cs` drives the fog and it cannot
disagree with the haze standing beside it.

Two findings worth keeping. **Multiplying toward black could only ever make a darker version of the same
picture**, which is why more of it looked like less weather; mixing toward light is what made it read as
overcast. And **isotropic noise that merely translates reads as a texture sliding** however fast it moves — the
noise is sampled in wind-aligned coordinates with the along-wind axis compressed, so the billows are drawn out
downwind, and the gust arrives as bands *across* the wind because nothing breathes in unison.

### Blocky was three faults, not one

> the edges are too blocky especially the dim to lit transition

A binary mask interpolated linearly is continuous in value and not in slope, and it is the slope the eye reads
as a facet. Three fixes, and none of them alone was enough: blur the mask into the texture and never back into
it (the wear texture's rule, for the wear texture's reason); **warp the lookup** with the cloud noise so the
edge itself wanders rather than its thickness varying — thinning a veil leaves a ten-metre grid legible *as* a
grid; and ease the shown masks per frame, so the boundary stops stepping a whole cell every time a refresh
cycle closes eight frames apart.

### Three shader paths returned before the veil, and all three leaked

Water had its own early exit, so every lake sat in clear view inside unexplored ground. Worse was the lit
window: a night settlement would have announced itself as a row of bright dots on black. Water gives away
terrain; a window gives away people. The veil is a function called from all three now. `smoke.frag` is a
separate pipeline with no mask bound and still escapes — harmless until there is a second settlement, and the
same class of bug.

### The camera was aiming at sea level

> I feel like I can't zoom beyond a certain level that's a bit too high, and also after a certain zoom the
> camera speed massively slows down

Two complaints, one bug, and it took a wrong guess first — I read it as zooming out and blamed the 118 m cap.
The focus was pinned to `y = 0` while the village's ground sits at **-47.7 m**. So the camera aimed at a point
forty-eight metres up in clear air, and at full zoom-in its eye — 5.9 m above the focus — stood *fifty-three
metres above the terrain*. Hence the closest available view being a middle-distance one. And pan speed is a
share of the standoff, so at that zoom it panned at 8.8 m/s while looking at a hundred metres of ground. No
pan-speed change was needed once the datum was right, which is the tell that it was one bug.

It also puts every derived reach on its proper datum. `VisibleGroundRadius` and `GroundBand` both take the eye
height as `sin(pitch) x cameraDistance` — the height above *that point* — so with the focus off the ground,
every draw distance in §69's "one reach for every draw distance" was computed for a camera much lower than the
one drawing them. The ground-chunk cull's own comment had already described the symptom without naming the
cause: the far edge of the view comes from intersecting the frustum with a plane at the focus height, "exactly
right on the flat ground it was written against and wrong the moment the map has hills in it".

### The gate, and the measurement that cancelled its other half

The gate is one test above every per-kind branch, which is what §74 settled while it was still a design: static
things keyed to explored, because a tree does not move and where it stands is knowledge the player keeps;
bodies keyed to watched, because where a body was is not where it is. It reads the *blurred* masks, which
reverses a note I had written three commits earlier — "a gate wants a decision and not a gradient" is true of
the output and wrong about the input, since the veil is drawn from the blurred mask and a gate keyed to the
sharp one hides props three cells inside ground the cloud has already thinned over.

Two censuses started lying within one frame of it landing. `TREENODES` reported 4,295 trees alive on a map
holding 34,337, because the alive count sat inside the tree branch and the gate now runs above it. It did not
become wrong; it became a different number wearing the same label.

**And then the tree index grid was cancelled by measurement.** Two controlled runs at fixed zoom: the whole CPU
build is **7.7 ms of an 83 ms frame** — ground 0.5, nodes 3.6, agents 0.0, scatter 0.9, stage 0.1, record 2.6.
So the index could at best recover four per cent. §72's lesson arriving a third time: whatever this frame is
spending itself on, it is not tree geometry. What the gate did change is the *cost model* — tree work is now
bounded by explored area rather than by map size, identically 4,295 offered at both zooms against 30,058
veiled.

Where the rest of the frame goes is still unknown, and saying so is the honest end of this. `FRAME` is a
smoothed average of `time.Delta`, so it is the frame *period* and includes any block on present — which means
"GPU-bound" is not established, only "outside every CPU build phase". Frame time does not track zoom at all:
the fastest sample in one run, 42.5 ms, was at the furthest standoff, against 92 ms averaged at the same
standoff. The variance turned out to be the camera being panned, which fires chunk builds and ground-cover
rebuilds:

> I was moving it around too - the perf honestly looks ok right now to me

### Two instruments, and one of them is lying

`MappingFault` asserts the world-to-texel mapping per cell, because that mapping has two implementations and
one of them is in a shader that cannot be read, screenshotted or reasoned about from a log — a veil half a cell
out looks exactly like a veil. Which is not hypothetical: the grid spans `cells x CellMetres` and **not** the
map extent, and I wrote the comment warning about that and then used the span for both the offset and the
divisor.

The one still lying is not fog's. **The two triangle counters disagree by about eleven times.**
`StagedLoad` multiplies by instance count, so the readout's `LOAD` figure is real geometry at 2.8M;
`passes/scene/triangles` sums base-mesh triangles per draw call and does not multiply, so it reads 246k for the
same frame. Any conclusion drawn from the pass counters about instanced content understated its load —
including §73's "the threefold caster redraw is cheap, 0.38 ms and 59k triangles per cascade". Nothing should be
optimised against either number until one of them is relabelled.

## 76. The way to content is one settlement-development arc

Two lines immediately above are stale by one commit and are left where they were because this file is a
record rather than a rewritten verdict. `7329467` put smoke behind the veil, multiplied the pass counter by
the instance count and added the number of instances beside it. The quick three-leg gate was green there.
The two year legs are still owed after the terrain and stone work, before the next change that touches the
economy.

The next broad order is settled:

1. **Close the shader-include seam.** Make Blix's GLSL preprocessor the build-time owner of inclusion, make
   `#pragma once` real there, and only then remove the file-level `#ifndef` guards. A guard around a symbol
   such as `BLIX_PI` is not a file guard and stays.
2. **Pull the gameplay arcs forward as one vertical settlement-development arc:** gathering and hauling,
   construction, repair as a prepared structure, one real upgrade, and barracks training into militia.
3. **Return to fog with the actors that give it semantics:** factions, enemy knowledge and what is remembered
   when a body leaves sight.
4. **Build the enemy / strategic AI / soak / combat-bot arc on the player's verbs**, then tie it through
   vision and militia combat.
5. Only then begin multiplying the game's content — more military types, buildings, upgrades and the map
   content that gives those systems cases to answer.

This is an overarching order, not five sealed projects. The first arc is one causal chain:

> find wood and stone → establish work and hauling → build a barracks → turn a villager into militia →
> improve and, later, repair what the settlement built

That chain is the gate. Each mechanism gets a consumer as soon as it exists, and the result is a small game
loop rather than five foundations waiting for one another.

### What gathering means here

The simulation already has the difficult half: crops as seasonal labour, finite trees and outcrops, stores,
reach, depots, paid carts, standing routes and exact conservation. This arc is not permission to add another
resource. It makes the existing three **authorable and legible from the chair**:

- point at the actual fertility or deposit and understand what is there;
- post people to it deliberately;
- put a depot where distance has made one necessary;
- establish the route that moves the resulting stock;
- see why a job, route or project is waiting without reading the terminal; and
- save in the middle of all of it and continue as the same world.

Enough interface to playtest those verbs is part of the system. A general command UI, production framework or
finished presentation pass is not. Add the prompt, selection state, cost/progress line and queue display the
current verb needs, then stop.

### Construction, repair and upgrade share a physical rule, not necessarily a class

Materials are hauled to the structure first. Work then consumes them as it advances. The part already spent
has become the structure; the unspent part is still physical stock at the site. Cancelling, changing a project
or losing the site cannot refund consumed material and cannot delete unconsumed material — somebody must haul
the latter somewhere else. Conservation should be able to describe every intermediate tick without a refund
exception.

The existing construction implementation currently waits for the full cost and consumes it at completion.
Bringing construction and repair under the rule above is therefore part of this arc, not an assumption that
the code already behaves that way.

Do not represent damage as negative construction. A completed building, its structural condition and the work
of changing it are three different facts. Keep the same stable `NodeId` through all of them, and make a save in
the middle of each operation continue identically. A shared project representation is earned if construction,
repair and upgrade actually need the same carried state; it is not a prerequisite to writing the second one.

**Repair is designed now and exercised later.** The state, material-flow seam, assignment and persistence
surface should exist so combat does not force a structural rewrite. There is no invented weather damage or
debug-facing gameplay loop merely to make the button useful. A deterministic headless fixture may damage a
wall to prove the rule; real use waits for something in the game that can harm one.

### The first upgrade is a wall becoming a wall

There is no generic ladder in which every granary, house and depot wants a level number. The first and only
upgrade needed to prove the mechanism is:

> **palisade wall → stone wall**

It is a material change the player can see, it gives stone a second honest sink, and it sits directly on §2's
attention ladder rather than improving a storage number because an upgrade system wants a target. For this
first pass the upgraded wall keeps its identity and footprint. A later upgrade that changes footprint reopens
placement, navigation and what happens to bodies standing beside it; it is not smuggled into this proof.

The gate is stronger than “level became two”: timber and stone arrive physically, work consumes them, the
same node becomes the stone wall only when the project completes, its blocking geometry never disagrees with
what is drawn, and save/load through the middle has the same future.

### A barracks trains people; it does not manufacture them

The prototype's rule stands: training converts an existing villager. Population still comes from housing and
food; a barracks does not create a second population source hidden inside a production queue. The body keeps
its stable `AgentId`, leaves the workforce and becomes **militia**, permanently for this pass.

That makes training a real economic decision before combat balance exists:

- the settlement pays material and training time;
- one fewer body farms, cuts, quarries, builds or hauls;
- militia appetite and military capability replace the villager's economic capability; and
- the conversion, including one in progress, is fingerprinted and saved.

The barracks is the first new building this arc needs. It exists because training needs a legible place and
because constructing the thing that unlocks the roster closes the vertical chain. Training is a deliberate
standing reassignment, not a temporary manual-order interrupt. Demobilisation may eventually be useful, but
it is not part of this pass and no refund rule is invented for it yet.

Only militia is buildable. The existing roster entries beyond it remain movement fixtures or scaffolding
until content work reaches them. The arc needs enough contact behaviour to assert that a trained militia body
has the intended type, persists and can participate in the later combat seam; it does not tune spears, bows,
horses or the micro multiplier early.

### What follows, and why fog waits for it

Fog's visual layer is in place. Its next questions are not visual dials: exploration per faction, allied
sharing, remembered structures, vanished bodies, last-known information and what an AI is allowed to know.
Those questions need a second actor. The standing direction remains absolute: **fog may read the simulation;
the simulation may never read fog.** AI decisions read authoritative sight and knowledge, never the blurred
veil or a render mask.

The first strategic AI then plays through the same construct, gather, haul, upgrade and train verbs as the
player, without free stock, special placement or special vision. That AI and the soak harness are the same
artefact for the reason §35 kept: a soak is shaped by what it soaks, and an economic AI is validated by what it
does over years. Small deterministic fixtures still land with every earlier mechanism; “soak later” never
means “verification later”.

Enemy pressure and the competent combat bot come after militia and sight are real enough to read. The bot is
built alongside the automatic combat layer and measures §12's still-open 1.5–2× micro multiplier. At that
point the systems are in place and adding a spear, bow, stable, tower or workshop is content extending a known
loop rather than another foundational rewrite.

## 77. Shader inclusion has one owner now

The prerequisite at the head of §76 is complete. The old split was not merely untidy: runtime shader loading
expanded includes through `GlslPreprocessor`, while every offline `CompileSpirV` target handed the authored
files straight to `glslc`. The latter warns about `#pragma once` and does not honour it, so the exact diamond
Providence now has — `world.frag` reaching `noise.glsl` directly and through `veil.glsl` — needed a second,
compiler-specific set of `#ifndef` file guards.

`Blix.Tools.Shader` is now the build-only front door for all thirteen shader-bearing projects. It asks
`ShaderLoader.PreprocessFile` for one expanded source and then invokes `glslc`; Runner's variants enter as
Blix defines at the same seam and Sponza still reflects the resulting module afterward. The file-backed
resolver is relative to the file making each request, not forever relative to the root shader, so nested
local includes now mean what their spelling says. Canonical identities also make the once-only decision about
the file reached rather than the text used to reach it.

`#pragma once` is a Blix directive. The preprocessor consumes it, preserves its physical line for diagnostics,
and never emits it to the compiler. The redundant file guards are gone from `noise.glsl`, `materials.glsl`
and `veil.glsl`; the `BLIX_PI` and `PI` conditional definitions remain because they guard symbols, not files.
`veil.glsl` is also an explicit RTS build input now, closing the stale-output hole where editing it alone did
not necessarily recook `world.frag`.

The proof is deliberately at three levels:

- `Blix.Test.Graphics` is **389/389**: the new cases cover a once-only diamond, two spellings with one identity,
  repeatable files without the pragma, directive consumption and cycle detection;
- the whole Debug solution builds with **0 warnings / 0 errors**, exercising the shared engine shaders,
  Providence, Runner's base and FOG variants, and Sponza compilation plus reflection; and
- the Providence quick gate is **3/3**: simulation self-test, the six-minute raid and all 55 map-panel maps.
  The two simulated-year legs were not run because this change touches build-time shader preparation rather
  than economy rates, jobs, hauling or construction.

The next work therefore starts the vertical arc itself. Its first useful slice should expose and exercise the
existing resource sources and standing work from the chair, then use that same gathering/hauling path to feed
the construction material-flow change. That keeps the first new interface attached to a verb the barracks,
wall upgrade and later repair will all depend on.

## 78. Gathering and hauling can now be authored from the chair

The first slice of §76's settlement-development arc is complete. It did not add a second economy beside the
one the simulation already had; it closed the concrete seams that kept the existing grain, timber and stone
rules from being a playable loop.

Posting with `U` now treats an outcrop exactly as it treats a field or tree: the generic post becomes a
standing stone-work assignment, the quarrier works at the visible face, carries whole units to the nearest
eligible store and returns until that deposit is exhausted. Outcrops remain non-blocking terrain props, but
their interaction and workplace footprint now describes the rock the player can actually see rather than a
zero-sized point at its centre.

The chair also says what the arrangement means. Pointing at open ground reports field fertility before a
field is placed; a selected field reports its stored fertility and crop state; trees and outcrops name their
nearest eligible delivery store; work sites report assigned and presently working hands; selected people
separate farmers, cutters, quarriers, builders and delivery workers, including work they cannot reach. This is
the playtest interface §76 asked for, not a generic production panel.

### A route is between places, not permanently between commodities

The old route command chose wood or grain once when it was issued and never considered stone. A player now
authors the useful relationship instead: **move goods from this source to this destination**. At each pickup,
the route chooses among all three resources. A construction site prioritises missing material; an ordinary
store accepts whichever available stock makes the strongest useful load. Stable ties keep the previous cargo,
so the choice is deterministic and does not oscillate.

That distinction settles the route's lifetime too. The board's `Haul` remains one priced trip for one cargo
and is dropped when that trip is stale. The player's `Carry` is a standing commitment: an empty source or a
destination with no present demand makes the carter wait at the source and recheck, not drive empty laps and
not erase the player's arrangement. Only losing one of the two named nodes ends it. A single route can
therefore take the timber a site needs, change to stone when timber is satisfied, and remain ready for later
stock without a second order.

### The proof follows the physical goods

Two end-to-end assertions were added at the same seams the player uses. Posting at an outcrop now works and
delivers a 30-stone hand load with every unit accounted for. One standing route supplies a granary site with
all **540 timber and 240 stone**, carries both resource kinds, survives a save round-trip with an identical
300-tick future, remains assigned after the site's demand is satisfied and closes the conservation ledger at
zero drift.

The full gate is green, including the two year legs §76 still owed before this hauling change: simulation
self-test, six-minute raid, flat settlement year, generated-terrain settlement year and all 55 map-panel maps.
The generated year quarried 244 stone and ended with all **4,440 / 4,440** units in outcrops, stores or hands.
The whole Debug solution also builds with zero warnings and zero errors.

One #0 integration fault surfaced before that gate could run. A self-contained Providence publish was trying
to publish the build-only shader compiler beside the game, first without the app host .NET requires and then
with duplicate runtime files. Shader projects now keep the compiler as a non-private build-order dependency:
normal builds still produce it before shader preprocessing, while publish traversal neither ships nor
collides with its output. The self-contained publish path and the full solution build both pass.

The next slice is the construction material-flow correction from §76: deliver first, then consume timber and
stone incrementally as labour advances, leaving every unspent unit physically recoverable. The gathering and
route verbs that feed that rule are now in place.

## 79. Builders now own the physical construction loop

The construction correction at the end of §78 is complete. A posted builder is no longer a hand waiting at a
site for a cart to solve the whole material problem. The post is a stable project commitment with a small,
deterministic loop:

1. honour a useful load already being carried;
2. otherwise claim one still-needed material, up to the body's carrying capacity;
3. take it from the nearest reachable store or loose pile;
4. deliver only what the project still wants, then work at the site; and
5. when the material front catches the labour front, repeat until the building is complete.

Several builders can follow that loop together. Their source reservations, loads on backs and cart loads on
the road all count as incoming material, so two workers do not promise the same stone and the hauling board
does not dispatch a cart for a load a builder has already claimed. Material choice is the least-covered part
of the recipe first, then the nearest source, with stable resource and node ties. The existing one-resource
cargo invariant remains intact.

### Work follows deliveries instead of waiting behind them

`BuildConsumed` is now a persistent fact on the site: material already incorporated into the structure, as
distinct from unspent stock physically lying beside it. Labour may advance as far as the supplied fraction of
every recipe material allows. Crossing a whole-unit progress threshold moves that unit from site stock into
the consumed ledger immediately; completion incorporates the exact recipe and never performs a second bulk
charge.

This makes every intermediate state honest. A rising building can still need timber or stone. The visible
material stack grows when a load arrives and shrinks as it is incorporated, independently of the structure's
height. Selection reports the recipe as incorporated, on site, incoming and still unclaimed, while the plain
state line combines progress with whichever material shortage remains.

Surplus remains physical. A builder who brought an unrelated load first returns it to the nearest generic
store before fetching project material. Any recipe stock left at a finished non-storage building is cleared
the same way; a storehouse may retain what is left because the completed project has become a valid store.
The cancellation/lost-project seam likewise converts carried material into a real return trip rather than a
refund or deletion.

### Storage names now describe the design

The internal `Granary` kind remains unchanged for save and code continuity, but the chair now calls it a
**Storehouse**. Both a storehouse and a **Camp** are generic drop points for grain, timber and stone. Resource
type belongs to the deposit and to the project recipe, not to the building receiving the load. This is why a
single camp can support a wood line, quarry or future construction front without becoming three nearly
identical depot types.

Housing remains the population source. People appear through houses with room and food readiness; the civic
centre stays deferred until it has a distinct role rather than duplicating either housing or storage. That
keeps this layer attached to the decisions already settled in §76 instead of inventing a second population
rule while construction is being corrected.

### The proof is shared work, replay and conservation

The new end-to-end fixture assigns four villagers through the same generic player post used at a real site.
It observes concurrent timber and stone claims, both material kinds actually carried, work advancing before
the full recipe is present, and an unrelated grain load returned. The storehouse consumes exactly **540
timber and 240 stone**, every builder clears on completion, and a save made mid-project has an identical
300-tick future. A second case deliberately puts 13 surplus timber at a house site and proves the completed
house is emptied back into storage with zero drift.

The older cart-fed construction fixture remains green as the other entrance to the same demand: a camp 40 m
out receives 120 timber by 129 seconds and two builders finish its 600 labour-seconds by 340 seconds. The full
gate is **5/5 green, years included**. The generated-terrain year quarried 244 stone and closed with 240 in
storage, 4 on backs and 4,196 in its remaining outcrops — all **4,440 / 4,440** accounted for. All 55 map-panel
maps generated, the whole Debug solution built with zero warnings and errors, and a self-contained arm64 game
publish contained the cooked world shaders. The persisted simulation layout is version 4 because both the
site and job records gained state that must survive the middle of this loop.

A chair correction followed immediately: right-click now means "do the useful thing under the cursor" for a
construction site, field, tree or outcrop, while ordinary ground still issues a temporary move order. The old
`U` post remains as an explicit/debug shortcut. This closes the live gap where villagers could be sent to a
site and then stand there without ever being assigned to the project. The full simulation self-test and a
200-frame self-contained village smoke are green.

The first builder journey is useful now as well. Assigning empty hands to an unfed project immediately enters
the shared material decision: each builder reserves one needed load, walks to its chosen stocked store, fills
to its carrying capacity and only then approaches the site. A builder already holding recipe material goes
straight to the project; one holding unrelated cargo returns it to the nearest generic store first; and a
project with a workable material front still receives hands immediately. The focused proof asserts all three
starts after the assignment's first tick, including exact source/site legs and a zero-drift physical ledger.

The next slice can now put the same physical rule behind a structural operation rather than another building:
prepare condition/repair state without manufacturing gameplay damage, then prove the first real upgrade as a
palisade wall becoming a stone wall on the same stable node. Barracks construction and permanent
villager-to-militia training follow that project seam before fog and the enemy/AI arc resume.

## 80. Condition is not construction, and a palisade becomes stone in place

The structural-operation seam is now real. A node carries present and maximum condition independently of the
labour that originally built it, plus an explicit repair-or-upgrade project when a completed structure is
being changed. Construction keeps its established state for continuity; all three operations expose one
physical recipe, incorporated amount, labour front and material demand to builders and carts. The save layout
is version **5** because condition and the active operation must survive the middle of the work.

There is still no ambient, weather or combat damage. `DamageStructure` is the prepared simulation entrance and
is exercised only by the deterministic fixture until a real attacker has earned the verb. Repair captures the
starting condition, scales a fixed material-and-labour scope from the missing share, then restores condition
only as delivered material is incorporated. In the proof, a half-damaged stone wall rises from **125/250** to
**250/250** condition while consuming exactly **30 stone**. Builders carry the stone, a mid-repair save has an
identical 300-tick future, the same node and collider remain, every hand clears, and discrepancy stays zero.

The first real upgrade is exactly the one §76 named. A palisade is a one-cell blocking structure built from
**60 timber and 300 labour-seconds**. Its upgrade costs **120 stone and 600 labour-seconds**. Throughout that
project the node remains a palisade with its existing identity, collider, footprint and blocking ground; the
stone shell rises visibly over it, but the kind changes atomically only on completion. The proof observes
progress while the kind is still `PalisadeWall`, round-trips a save in that state, and closes as `StoneWall` on
the same `NodeId`. It also deliberately leaves 13 surplus stone at a project and proves a builder returns it
to storage rather than refunding or deleting it.

The chair can exercise the slice without a debug panel. **Ctrl+W** places a palisade. **Ctrl+right-click** on a
sound completed palisade opens the stone upgrade and posts the selected villagers through the same generic
structural assignment used by construction. A damaged structure takes the repair branch first. Ordinary
right-click on an active construction, repair or upgrade project assigns more hands, and the HUD names the
operation, condition, incorporated/on-site/incoming/unclaimed material and current work state.

The full gate is **5/5 green, years included**. Both new focused proofs are green, the six-minute raid remains
green, both settlement years complete, and all 55 panel maps generate. The generated-terrain ledger again
closes stone at **4,440/4,440**: 244 quarried, 240 stored, 4 carried and 4,196 left in 35 outcrops. The next
vertical slice is now the barracks and permanent villager-to-militia conversion: construct the place, pay the
training cost and time, preserve the body id through conversion, and make the lost economic hand visible.

## 81. A barracks converts hands into militia; it does not add bodies

The settlement-development arc in §76 is complete through its first combat-roster seam. A barracks is a real
five-cell blocking structure, placed with **Ctrl+D**, and follows the same physical construction rule as every
other building: **300 timber, 120 stone and 1,800 labour-seconds** are hauled, delivered and incorporated while
the building rises. It is deliberately using the granary-scale art as a readable placeholder; distinct barracks
content belongs to the later building-content pass, not to proving the system.

Right-clicking a completed barracks with villagers selected creates a persistent `Train` commitment on each
selected body. It does not enqueue new population. Each trainee reserves missing equipment against real source
stock, carries it to the barracks and trains there once the cohort's equipment is present. The first recipe is
one visible sack each of timber and stone — **30 + 30** — and **16 seconds**, preserving the prototype's training
time. Several villagers can join at once without claiming the same last sack. Equipment already delivered stays
physical at the barracks if the commitment is cancelled; equipment still on a body stays on that body. There is
no refund exception and no resource leaves the conservation ledger until a conversion actually completes.

### Identity is explicit now

An agent now carries a durable role rather than letting callers infer what it is from speed, appetite or carry
capacity. The conversion changes that role from villager to militia and swaps the same body's frame in place:
the same stable `AgentId`, collider handles, household and headcount remain, while pace becomes 1.70 m/s, carry
capacity becomes zero, appetite becomes 1.35, strength becomes 3 and health becomes 45. No demobilisation path
exists in this pass. Militia may still receive movement and garrison posts, but posting one on a field does not
turn it back into a farmhand and contributes zero economic hands.

That durable role also closes the interface seam. The settlement's spare-hands count and Tab selection include
villagers only; selected units report training and militia separately; a barracks reports trainee count, average
training time and the timber/stone equipment physically present. Militia use the existing person mesh with a
cool steel tint so the conversion is legible before dedicated military art arrives. Barracks are included in
the renderer's existing fog bookkeeping without allowing the simulation to read the fog.

### The proof constructs, equips, converts and resumes identically

The vertical fixture first constructs the barracks through the ordinary builder command and observes timber and
stone on builders' backs. It then commits two existing villagers together. Both fetch physical equipment, both
retain their ids, the live-agent count does not rise, both acquire the militia frame, and construction plus two
training recipes are consumed exactly with zero discrepancy. A save taken after training has begun has an
identical 300-tick future; a save after completion preserves both military roles. The same fixture posts one of
them on a field and proves it remains a garrison rather than entering the civilian work loop.

That save exposed an older adjacent hole: `Raised` was fingerprinted but not serialized, so the first save taken
after a building had completed diverged immediately. `Raised`, `Born` and `Emigrated` are now written with the
rest of the economy counters, and the raw-layout save version is **6** for the new role and training progress.

The full gate is **5/5 green, years included**: simulation self-test, six-minute raid, flat settlement year,
generated-terrain settlement year and all 55 map-panel maps. The generated year again closes stone at
**4,440/4,440** — 244 quarried, 240 stored, 4 carried and 4,196 still in 35 outcrops. The Debug build has zero
warnings and errors, and the self-contained arm64 village package completed a 200-frame live smoke with the new
control and HUD path active.

The causal chain is now playable through gathering, hauling, constructing, repairing, upgrading and training.
The next arc returns to fog with a real military actor: faction knowledge, memory and last-known information,
then the enemy/strategic-AI/soak/combat-bot work using the same player verbs.

## 82. Experience recovery changes the order: performance, groups, time, then Providence

The last sentence of §81 is no longer the immediate order. The settlement-development arc made the simulation
more complete and exposed that the thing on screen has accumulated enough local proofs to stop feeling like one
game. Before adding another semantic layer, the existing game has to become responsive, coherent in how people
are handled, and temporally readable.

One boundary is settled first:

> **Village remains the systems sandbox. Providence is a separate player-facing app / entry point.**

Village may expose direct keys, diagnostics, scenario controls, arbitrary maps and unfinished mechanics because
its job is to let a system be exercised from the chair. Providence will compose those systems into an opening,
an action language and an attention hierarchy. Trying to make one executable be both has been making sandbox
affordances carry product-experience responsibilities they were never designed for.

### 0. Performance and the camera envelope come first

The game is presently choppy enough that judgement of movement, fog, interaction and time is contaminated by
the frame itself. This pass is not “optimise later” housekeeping; it is the prerequisite for being able to tell
whether every later experience change is good.

Measure distinct cases rather than one average:

- first presentation and map generation, separately from steady state;
- a still camera, panning, rotating and zooming, because camera motion rebuilds and exposes different work;
- nearest, normal and furthest useful gameplay views, with fog on and off;
- day, dusk and night, including hearths, smoke, shadows and the veil; and
- a quiet settlement against a selected moving group, active construction and later combat.

The output is both a bottleneck account and a **supported camera envelope**. Minimum and maximum zoom are game
design: too close can make selection and command context unusable; too far makes people subpixel, exposes more
terrain and foliage than the renderer can sustain, and turns a settlement into an unreadable mark. The existing
6–118 m limits are prior judgements, not protected facts. Re-measure them against what Providence needs to see.

Do not thin the world or weaken the look before establishing where the time actually goes. Earlier passes have
already shown tree triangle count, CPU submission, present wait, startup rebuilds and camera-driven ground work
masquerading as one another. This pass ends with an agreed frame budget on the current machine, stable pacing
inside the supported view, and instrumentation that distinguishes a steady regression from a startup frame.

**First measured landing, 30 August 2026.** `--perf-run` now seals a frame run from real input, leaves the
interactive diagnostics panels out of the measurement and keeps the requested timing sink. On the current
34,374-node wooded Village, the quiet fixed tick had regressed to roughly 8-9.5 ms after warm-up: about 3.6-4.2
ms of settlement passes rediscovering a few dozen buildings among 34,337 trees, plus roughly 4.2-5.1 ms in the
defence layer rebuilding guarded resources once per villager when no enemy faction existed. Settlement nodes now have a stable derived
index, working-hand clearing touches only last tick's work sites, and peace is proved before any guarded-resource
search. The same run now spends roughly 0.18-0.30 ms on the whole steady tick, commonly 0.03-0.09 ms in economy
and 0.02-0.04 ms in threat. The full determinism, save/future, economy, construction and threat suite passes.

The first renderer cuts keep the look intact: static nodes are bucketed by the fog grid instead of asking the
same mask texel tens of thousands of times, and an unchanged camera reuses resolved procedural ground-cover
placements. At the 118 m stress view, steady command construction fell from roughly 16-29 ms samples to about
12 ms in the comparable sealed run, with scatter falling from 5-8 ms to effectively zero while still staging
the same instances. This is a landing, not the end of stage 0. Wide-view pacing is now dominated by the roughly
4.2 million scene triangles plus the same 2.9 million caster triangles recorded into each of three cascades;
the next decision is a per-cascade caster partition (which requires genuinely separate instance buffers), then
the still/pan/rotate/daylight camera matrix and the supported zoom envelope. Do not hide that remaining work by
calling the simulation win a complete frame-budget pass.

### 1. “Groups” is four linked problems, not merely a formation

Groups were deferred while individual locomotion and basic movement fixtures were being settled. That debt now
appears in four places at once:

1. **Behaviour:** bodies given one intention need shared direction, coherent transit and an arrival that reads as
   one action rather than a trail of independent ants.
2. **Affordance:** the player needs to create, recall, inspect and command a set without repeatedly reconstructing
   it by marquee selection.
3. **Organisation:** a persistent crew or military party needs a stable identity that can survive other commands,
   save/load and changes in membership.
4. **Performance:** common intent, routing and observation should be computed at group resolution where the answer
   is genuinely shared, rather than rediscovered per body.

The implementation discussion must keep four related representations distinct:

- a transient selection;
- the command cohort produced when that selection receives one order;
- a persistent player-authored group or crew; and
- a combat formation, which adds spatial roles and can wait until the combat arc needs it.

Collapsing all four into `Group` would make a control group accidentally own locomotion or a work crew
accidentally become a formation. The first pass should earn the minimum durable representation that solves
coherent commands, recall and shared computation. Formation geometry, facing and combat ranks remain a later
consumer, not a prerequisite.

Group acceptance must be experiential as well as mechanical: command-frame latency, shared directional intent,
cohesion through gates and around terrain, interruption and resumption, loose/blocked arrivals, membership edits
and persistence. Aggregate simulation timing cannot declare success while the first visible response hangs or
the cohort moves like unrelated agents.

### 2. The day/night cycle needs a time design, not another colour pass

The day/night cycle originally made Village feel alive. It now makes it feel fast, awkward and confusing. The
reason is explicit: `WorldCalendar.DaySeconds` is 20 simulated seconds, the sun was resynchronised to that period,
and the default game compression is 1.5x. A complete visual day therefore passes in about thirteen seconds of
wall time. The calendar and sky agree, but the player cannot inhabit either.

Re-open the relationship among three clocks:

- the fixed simulation step;
- the economic calendar that defines crop windows, consumption and seasons; and
- the perceptual day that moves light through dawn, day, dusk and night.

Changing one constant without naming which clock it belongs to will reproduce the confusion. The pass must
decide whether a visible solar day is an economic day, a slower presentation cycle over several ration-days, or
whether the economic calendar itself needs retiming. Whichever survives must keep seasons predictable, give each
lighting state enough wall time to be experienced, and avoid cycling from noon through night while the player is
still carrying out one ordinary command. Village remains the tuning ground; Providence receives the settled
cadence rather than the experiment.

### 3. Providence is the composed vertical slice

Only after the substrate above is comfortable to operate should Providence become its own executable / entry
point. It reuses the same simulation and rendering systems; it does not fork their rules. What differs is the
composition:

- a deliberate opening, readable light and an already-resolved founding reveal;
- one pressure the player can understand before several simultaneous shortages;
- contextual right-click as the ordinary verb, with a small stable action/build surface and shortcuts behind it;
- an attention queue expressed as **what needs me, where, and how long until it matters**;
- group and workplace affordances instead of repeated individual hunting; and
- diagnostics and tuning controls absent unless the app is explicitly launched in a development mode.

This is where the experience-recovery items from the audit belong. Village should not be polished into an
onboarding flow and Providence should not inherit the movement test bench's alphabet as its primary interface.

### Revised road forward

0. **Performance and supported camera envelope**, measured in Village.
1. **Groups**, first as coherent command/recall/shared work, with formations left for their real consumer.
2. **Day/night and time cadence**, settled across economic and perceptual clocks.
3. **Providence vertical slice**, as its own entry point with the composed opening, action surface and attention
   interface.
4. **Fog with actors:** faction knowledge, memory, allied sharing and last-known information.
5. **Enemy / strategic AI / soak / combat bot**, using the same construct, gather, haul, group, upgrade and train
   verbs as the player and giving militia, walls, sight and alarms their payoff.
6. **Content proper:** more units, buildings, upgrades and map content extending systems whose experience is now
   known.

This does not discard §76's direction. It protects it: the physical economic chain remains the spine, while the
new order makes it fast enough to feel, organised enough to command, paced enough to read and finally composed
as Providence rather than presented as a sandbox proof.

## 83. The frame, measured properly: it is the GPU, and the fog upload was hiding it

§82's stage 0 promised a per-cascade caster partition, then the still/pan/rotate/daylight matrix, then the
supported camera envelope. The first two are done. **The envelope is deliberately not set here**, and the
reason is the point of this section.

### The partition, and what it was worth

Prop, unit and canopy casters each own one instance list, one buffer and one batch per cascade instead of one
list drawn three times, and the fitted-circle coverage test became a mask rather than a boolean so a caster
overlapping two boxes still reaches both maps. On the wooded Village at the 118 m stress view, total submitted
caster triangles fell from about 8.78M to 6.38M, and the near cascade from 2.93M to about 0.82M.

The middle and far boxes legitimately overlap most of the visible woodland, and exclusivity was **not** forced
there: a caster dropped from a box that can shade the ground under it is a missing shadow, which is a look
regression paid for with a triangle count.

### The instrument came first, and it was wrong twice before it was right

The matrix is twenty sealed runs. The only instrument was a scrolling `FRAME/BUILD/LOAD` line, which is the
right thing from the chair and the wrong thing for a comparison — every wrong number this stage has produced
came from a figure recalled rather than recorded. So: `PerformanceRun` keeps raw per-frame samples and prints
one block plus one machine-readable `PERFCASE key=value` line, `tools/perf-matrix.sh` runs the cases against
one pinned map and generates the tables from those lines, and the plan's numbers can no longer disagree with
the runs that produced them.

Three corrections were needed before the tables meant anything, and each one had already produced a wrong
belief:

1. **Percentiles, not the smoothed average.** `FRAME` is an exponential average. A case at 45 ms p50 with a
   102 ms p95 is choppy, and no average can say so.
2. **Vsync off.** The swapchain takes FIFO unless told, so a frame time is otherwise how many refresh
   intervals the frame waited for. Checked rather than assumed, and the answer was interesting: the near view
   cost 25.7 ms uncapped against 26.4 ms capped, so vsync was never the floor — but it was adding jitter
   (max 31 ms against 45 ms). A sealed run now presents through Mailbox and the report says `vsync=off — cost`
   so a cadence figure can never be quoted as a cost.
3. **The frame closed against its own halves.** This is the one that mattered. `BUILD` reports the render
   build's phases and nothing else, so the whole of `OnUpdate` — the tick, the fog refresh, the fog texture
   upload, the cover resolve — was outside every number printed. **The first accounted matrix closed to 8 ms
   of an observed 45.** A frame that reports a small CPU and says nothing about the other 82% of itself is an
   instrument that will support whatever theory is held about it, and it did: the wide view was called
   GPU-bound on exactly that evidence, which was no evidence.

Every sample now carries `update` (with `fog` broken out of it), `render`, and an `outside` residual — what
neither half claimed, which is acquire, submit and waiting on the device.

### The matrix

Twenty cases, 600 frames each, 120 warm-up frames excluded and reported separately, one map
(`--village --mapseed 1592842292 --relief-amplitude 32`, 25,141 nodes), Debug, vsync off. Frame figures are
p50 ms.

```
case                       frame    p95   update    fog   render  outside   scene-tri  cast near/mid/far
still-12h-24m-fogon         26.7   28.9     19.5   19.2      6.2      0.8       4314k  403k/1358k/2736k
still-12h-60m-fogon         31.3   33.1     23.0   22.8      7.2      0.8       5985k  1411k/2813k/4885k
still-12h-118m-fogon        44.6  101.8     33.6   33.2      8.9      0.7       8120k  3354k/6562k/6813k
pan-12h-118m-fogon          47.2  113.8     34.7   34.2     11.0      0.8       8038k  3367k/6577k/6729k
rotate-12h-118m-fogon       47.7  106.7     36.2   35.6      9.2      0.7       8118k  3059k/6620k/6810k
still-19h-118m-fogon        56.8  104.4     42.0   41.0     11.4      1.1       8120k  3562k/6702k/6813k
still-0h-118m-fogon         49.9   99.1     38.5   37.7      9.6      0.8       8120k  3562k/6703k/6813k
still-12h-24m-fogoff        16.7   38.0      0.2    0.0      9.9      6.7       4398k  403k/1358k/2736k
still-12h-60m-fogoff        20.1   47.0      0.2    0.0     10.0      7.9       6494k  1411k/2813k/4981k
still-12h-118m-fogoff       35.2   87.8      0.4    0.0     18.3     19.3      10120k  3354k/6597k/8176k
```

Read the whole `fog` column against the whole `outside` column before drawing anything from either.

### What it says

**The frame is the GPU's, at every standoff, and the CPU is only ever waiting for it.** Fog on, the wait lands
inside `UploadFogTexture` — 33 ms of "fog" at the wide view. Fog off, the identical wait reappears in
`outside` at acquire, and the fog column is zero. The optimised build settles it: in Release the node phase
falls from 6.8 ms to 2.3 ms and the tick from 0.19 ms to 0.06 ms, and **the frame does not move at all**
(45.6 against 44.6 at 118 m). Managed work is not the frontier; nothing in C# will buy a millisecond here.

> **`fog_p50` is a wait, not work.** The fog texture upload is a device synchronisation point and therefore
> absorbs whatever the GPU still owes. Anybody reading 33 ms there and going to optimise the fog refresh
> would be optimising the place the bill is presented rather than the thing being billed for.

The rest, in the order the numbers support:

- **Zoom sets the cost, and the present envelope has no comfortable end.** 26.7 ms at 24 m, 31.3 at 60 m,
  44.6 at 118 m — 37, 32 and 22 fps. The near view is not a cheap frame; it is a 4.3M-triangle frame.
- **Casters are at least as expensive as the scene at every zoom**: 4.5M against 4.3M at 24 m, 16.7M against
  8.1M at 118 m. The partition gave each cascade its own list; it did not give the far cascades their own
  *geometry*, so the mid and far boxes still take near-tier caster meshes. That is the unpaid follow-up, and
  it is the largest single lever the tables point at.
- **Light is real and dusk is the worst hour**, not midnight: 44.6 noon, 56.8 dusk, 49.9 night at 118 m; about
  +6 ms at 60 m. Hearths, windows and smoke against a low sun.
- **Camera motion is cheap.** Still 44.6, pan 47.2, rotate 47.7 at the wide view. §82 expected motion to be
  the axis that rebuilds work; it costs 3 ms, and the fitted-box and cover caches are why.
- **Release does not change the cost but it halves the choppiness**: p95 49.1 against 101.8 at 118 m. Feel
  should be judged in Release, cost may be measured in either, and the two must be labelled.
- **First presentation is about 0.75 s** in the steady cases, and the excluded warm-up window carries it
  rather than contaminating a steady figure — which is what §75 was misled by once already.

### Why the envelope is not set in this section

Three reasons, all of them measurement rather than taste:

1. **"GPU-bound" is still an inference.** It is the only reading consistent with a wait that moves between two
   unrelated sync points and an optimised build that changes nothing — but no pass has been timed on the
   device. The next slice proves it directly, by pass, or disproves it.
2. **Variance is unmeasured.** One run per case, and the two matrices taken twenty minutes apart disagree by
   about 5 ms on some cases (`zoom-12h-118m`, 38.5 then 45.6). A zoom cap is a design decision that will be
   argued from a number; the number needs repeats and a spread first.
3. **Fixing the largest lever moves the answer.** If per-cascade caster tiers take 16.7M submitted triangles
   down near 8M at the wide view, the frame that the envelope is chosen against is not the frame in the table
   above. Setting a cap now would be capping the renderer as it exists for one more slice.

### The next slice

1. **Attribute the GPU.** Timestamp queries per pass — three cascades, scene, smoke, present — so "it is the
   GPU" becomes "it is these passes in this proportion". Cheap ablations beside it: one cascade instead of
   three, casters skipped entirely, canopy skipped.
2. **Per-cascade caster geometry.** The far box does not need the near tier's mesh; a shadow is a silhouette
   resolved to that map's texels. This is the partition's actual payoff and the tables say it is worth about
   half the submitted geometry at a wide view.
3. **Then** repeats, the vsync-on pacing pass — which is a real and different question, because the choppiness
   a player feels *is* which refresh intervals get missed — and only then the supported envelope, with 6-118 m
   re-argued against a measured frame budget.

## 84. The coarse cascades get a stand-in, and the laptop turns out to be part of the instrument

§83 named per-cascade caster geometry as the largest lever the tables pointed at and left it unbuilt. This
section builds it — and the first thing it found was that the art had already taken the obvious half of the
saving years ago.

### The lever was not where §83 said it was

The tree tiers ask for `casterLod: 3` — the coarsest level the cooked chain holds, 454 triangles against the
scene's 4,345 — so the shadow geometry was already as cheap as decimation could make it. The 16.7M submitted
caster triangles at the 118 m standoff were **three copies of an already-coarse mesh**, and there was no
coarser level left to give the far boxes. "Give each cascade its own geometry" was therefore not a
one-parameter change; it needed geometry that does not exist in the asset.

So the ceiling was measured before anything was built. `--perf-cascades N` masks casters out of every cascade
past the first N — one line, because the whole caster path passes through `CascadeMaskAt`:

```
standoff   casters into 3      into 1              into none
118 m      46.4 ms, 16.7M      34.2 ms, 3.4M       31.0 ms, 0
 60 m      32.8 ms,  9.1M      26.1 ms, 1.4M       26.2 ms, 0
```

**12.2 ms of a 46 ms frame at 118 m, 6.7 ms of 33 at 60 m, and p95 falling from 107 ms to 58.** That is the
most a proxy could ever return, and it is worth having. It also settles §83's open inference: 13.3M triangles
removed for 12.2 ms recovered is about 1.1M submitted triangles per millisecond, which is a geometry-bound
frame rather than merely a GPU-shaped suspicion.

### What the coarse cascades get

`PropModel` now keeps caster state per shadow pass rather than per part: each pass owns its geometry, its
buffers, its batches and one instance list. Two consequences beyond the obvious one — a pass may hold a mesh
set with no relation to the scene model's parts, and a placement is stored once per pass instead of once per
part per pass, which the old shape did.

The stand-in is a sixteen-triangle octagonal bipyramid sized from each model's own bounds, waisted at 55% of
its height. **This file has had a blob shadow before and deliberately removed it**, and the difference is
which cascade receives it: at the 118 m standoff the three maps resolve at 11.7, 15.9 and 45.1 cm and the
coarse two cover 124-284 m, where a tree canopy is nine to eighteen texels across. The near cascade keeps the
real silhouette, because 11.7 cm is precisely where the eye reads one. A box was rejected for its corners —
two of them along the light make a visibly square shadow.

Buildings, rocks and units keep real geometry in every cascade. They are few, and theirs are the shadows a
player actually reads.

The load figures had to change with it: a model's caster cost stopped being one number, so `StagedLoad` and
`StagedCasterLoad` sum instances x triangles **per pass**. Reporting it the old way would have shown the near
silhouette three times and hidden the entire saving — the same class of error as §75's pass counter, which is
why the art report now prints what a tree casts into each cascade separately. If those three numbers ever read
the same again, the proxy has been lost.

### The measurement that had to be thrown away

The first attempt compared a twenty-case matrix taken before the change against one taken after. The second
matrix came out **slower nearly everywhere** — `still-12h-118m-fogon` at 47.6 ms against 44.6 before — which
would have read as the proxy making things worse. The tell was the node phase: 6.8 ms before, 11.2 ms after,
for CPU work neither version touches. Twelve minutes of uncapped GPU load on this laptop moves every figure in
the table by a quarter, and the same case measured in isolation ten minutes earlier had come out at 35.6 ms.

> **A before-and-after separated by a rebuild is a comparison between two thermal states.** Two long matrices
> cannot be compared to each other at all, and a long matrix cannot be compared to a short run.

So `--perf-noproxy` puts both arms in one binary and the comparison is interleaved A/B/A/B in one session:

```
118 m   proxy   35.66  48.98  62.67      noproxy  45.29  69.02  64.39
 60 m   proxy   36.63  36.65  36.20      noproxy  43.90  41.71  45.34
```

**Every pair favours the proxy** — by 9.6, 20.0 and 1.7 ms at 118 m and by 7.3, 5.1 and 9.1 ms at 60 m. The
drift is visible in the rows: the 118 m block ran first and climbed throughout, while the 60 m proxy arm is
flat to half a millisecond across three runs and its noproxy partner is not, which is what a heavier arm
hitting a power limit looks like. Submitted caster triangles, which no thermal state can move, fell from
16.73M to 3.91M at 118 m and 9.11M to 1.84M at 60 m, and the per-cascade split went from 3354k/6562k/6813k to
3354k/274k/280k.

Call it **7-10 ms at both standoffs**, and do not quote a single absolute frame figure from a long run again.

### What this costs and what is still owed

- **The look is unverified by anything but arithmetic.** The texel sizes say the coarse maps cannot resolve a
  silhouette, but nobody has yet sat at 60 and 118 m and looked at the shadows under a wood. That is the one
  acceptance this section cannot do from a log, and it comes before the change is trusted.
- **The measurement protocol changes.** Long matrices are for relative reads within one run of one case;
  anything comparing two configurations must interleave them in one session. §83's absolute table should be
  re-read with that in mind — its ordering biases the later cases against the earlier ones.
- **The near cascade is now the caster cost**, at 3.35M triangles and about 3.2 ms by the ablation. Coarsening
  it is a look decision at 11.7 cm texels rather than a free win.
- **Still owed from §83:** the vsync-on pacing pass, repeats with error bars, per-pass GPU timestamps, and only
  then the supported camera envelope. The envelope is still not set, and after this section the frame it would
  be set against has moved again — which is exactly why it was not set earlier.

## 85. The frame was waiting on nothing: a queue drain inside the fog upload

Somebody sat in the chair, which is how this section exists.

### The eye's verdict on §84

§84's proxy was measured at 7-10 ms and shipped with one unverified claim: nobody had looked. Looked at, at
118 m, the real geometry's tree shadows read plainly better — and the frame was comfortable either way. So
**the proxy is off by default**, behind `--shadow-proxy`. The machinery stays: the measurement stands, a
weaker machine or a bigger map may want it, and keeping both arms in one binary is the only kind of
comparison §84 established as trustworthy.

That is the whole point of an acceptance the log cannot perform. A 26% frame saving lost to a look nobody
had checked would have been a good trade badly made.

### The same person reported 150 fps where the fixture reported 22

Which is a six-fold disagreement about one build, so one of us was wrong. Two suspects were cleared first,
in this order, because an instrument is guilty until measured:

1. **The fixture was turning on the diagnostics producer.** `--perf-run` forced `--timings`, and
   `timingDebug` keeps the whole producer alive whether or not a panel is drawn — `ReportDebug`'s own comment
   says `ReportEconomy` alone costs over four milliseconds in a wide frame. Interleaved, it came to 0.35 ms
   of 44 (44.37 clean against 44.72 instrumented), so §83 and §84 survive. The fixture no longer implies
   timings regardless: it had no business carrying the risk.
2. **The recorder might have been reading the wrong clock.** It samples `OnUpdate`'s delta; the engine's F1
   HUD counts `OnRender`'s. Both are now recorded and reported side by side, and they agree to a hundredth
   of a millisecond.

Which left the code. `UploadTextureMip` submits through `EndSingleTimeCommands`, and that ends in
**`vkQueueWaitIdle`**. The fog mask is uploaded on every frame its texels change, so most frames drained the
entire graphics queue in the middle of the update.

> **A frame that drains the queue costs CPU + GPU instead of max(CPU, GPU)**, and the wait is charged to
> whatever touched the device first rather than to the work it was waiting for.

That is the mechanism behind §83's "33 ms of fog" and its 1 ms `outside` column, and §83 named the symptom
without naming the cause. It also explains the argument: when the mask happens to be clean the upload is
skipped, the frame pipelines normally and runs at about 7 ms. **The frame was bimodal and the mode was set by
a dirty flag.** Both observers were right about the build they were looking at.

The wear mask takes the same path and escaped only by uploading every fifteenth frame — hiding in plain
sight beside the one that did not.

### The fix, and what it returned

`IGraphicsDevice.QueueTextureUpload` stages bytes into a ring of host-visible buffers and records the copy and
its barriers into the frame's own command buffer, before any render pass opens. Nothing waits. The ring is
`MaxFramesInFlight + 1` deep for the same reason the transient vertex arena and the indirect ring are —
uploads are queued during encode, before `Execute`'s fence wait — and it is built lazily, because it is twelve
megabytes and most Blix apps upload everything they need at load time.

`UploadTextureMip` stays exactly as it is. Its drain is what makes it safe to sample on the caller's next
line, which is what a streamed mip chain wants. The two now differ by their guarantee rather than by
accident.

Interleaved against `--perf-blocking-upload`, three rounds per standoff, one session:

```
             fog column        frame p50 (queued vs draining)
118 m    20.3 → 0.27 ms        27.1→16.7   28.8→19.8   36.9→26.6
 60 m    19.9 → 0.65 ms        33.9→24.7   33.2→25.3   34.1→25.4
```

**About 10 ms at both standoffs**, every pair, and 118 m goes from 37 to 60 fps on the village default map.
Validation-clean over twelve sealed runs; 389/389 graphics tests; gate 3/3 green. The remaining wait moved to
`outside` — 11-17 ms of honest GPU-bound waiting at acquire, where it can be attributed to the passes that
earn it instead of to a mask upload.

### What stage 0 has actually learned

Every real finding in §83-85 was an instrument or a mechanism, not a tuning knob:

- the frame did not close against its own halves, so 82% of it was unattributed (§83);
- absolute figures drift 25-75% under sustained load on this machine, so configurations must be compared
  interleaved in one session and never across two long matrices (§84);
- the art was already casting from the coarsest level it had, so the "obvious" lever was spent (§84);
- and the largest single cost in the frame was a device drain nobody had asked for (§85).

The pattern is consistent enough to state as a rule: **on this project, measure the instrument before
optimising the thing.** Three of the four findings above were invisible until an instrument was doubted.

### Still owed

- **The camera envelope**, still unset, and now for a better reason than before: the frame it would be set
  against has changed twice today. It wants the vsync-on pacing pass, repeats with error bars, and per-pass
  GPU timestamps to attribute the 11-17 ms that is left.
- **`outside` needs breaking down.** It is now the frame's largest term and it means "acquire, submit, or
  wait" — three different answers wearing one label, which is the same shape of hole §83 opened this arc by
  finding.
- **`Blix.Render/ResourceUploader` is the other per-frame caller**, and it is the streamed mip path: it drips
  levels in over frames, so a streaming frame drains the queue exactly as the fog did. It was left alone
  deliberately — its residency bookkeeping is written against the drain's guarantee, and the argument that the
  queued path is safe there is the same barrier argument made here but wants Sponza's streaming run to prove
  it rather than an assertion in this file.

## 86. The zoom cap comes off, and the fog turns out to bound the wide view

§82 put the 6-118 m limits explicitly back in play; §84 and §85 then moved the frame twice. A cap measured
against §73's frame has no authority over this one, and holding it while the envelope is being re-measured
means the measurement cannot see past the answer it is checking. So the wheel's ceiling is now derived:

    cameraFurthest = --zoom-limit, else max(118 m, extent x 0.75, the requested --zoom)

On the 600 m village that is **6-450 m**, printed in the startup control list. Three inputs, each of which
had been a bug on its own: the map's extent (the lab's own rule, now general, because a canvas three times the
map wide cannot be judged through a hole showing a seventh of it); an explicit `--zoom-limit`, so a measured
envelope can be pinned without a rebuild; and the requested opening standoff, because `--zoom 150` opening at
150 m and then having the first notch of wheel refuse to return there is the worst of both answers, and is
what actually happened.

### What the far end costs, now that it is reachable

Sealed, still, noon, fog on, village default map:

```
standoff   frame p50      trees   scene tri   cast tri   chunks   outside
118 m      26-28 ms       4,239      4.20M      6.20M      19      15.2 ms
250 m      22.5 ms        4,295      4.32M      8.50M      24      16.4 ms
450 m      16.7 ms        4,295      4.32M      8.91M      25      11.0 ms
```

**The staged geometry is identical at 250 and 450 m, and the frame gets cheaper as the camera pulls back.**
With fog on, the revealed region bounds what can be drawn, so zooming out past roughly 150 m adds dark ground
and no content while every tree covers fewer pixels. §83's "zoom sets the cost" is true between 24 and 118 m,
where pulling back progressively fills the revealed area, and stops being true past it.

Which means the 118 m cap was not standing in front of a cliff. The reason to have a far limit is what §73
said it was — a settlement becomes a smudge and the shadow map is spread thin — and that is a legibility
argument, not a frame-budget one. It should be settled by looking, at a standoff the wheel can now actually
reach.

Two things to check by eye at the far end, neither of which a log can answer: whether the ground runs out
before the map does (chunks drawn were flat at 24-25 across both wide standoffs, so the ground draw radius may
cap before the extent does), and whether a settlement at 450 m is worth being able to see at all.

## 87. The ceiling is free, the middle distance is not, and §86's plateau was one map's

A claim from the chair: *the max zoom affects the frame more than the current zoom does.* Worth taking
seriously — the chair has been right twice in this arc and the code reading wrong twice.

### The ceiling, measured fairly

`cameraFurthest` feeds three things and none of them is a draw bound: the scroll clamp, the lab's opening
standoff, and the sealed zoom motion. Every bound comes from the current distance through `VisibleReach` and
`DetailRadius`, and ground chunks are meshed map-wide regardless of the camera. So there should be no
coupling — but that is a code reading, and the point of this arc is that code readings lose to measurements.

The first measurement appeared to support the claim: at a fixed 78 m standoff, the 450 m ceiling came out
5 and 11 ms worse in two of three pairs. **It was the harness.** Each pair ran 118 first and 450 second, into
a machine that heats — the very error §84 was written about, committed by the script written to avoid it.
Re-run ABBA:

```
ceiling   frame p50 over four runs        staged geometry
118 m     36.5  40.5  43.9  46.3  (40.5)  6,729,150 scene / 11,511,072 cast
450 m     39.8  41.6  45.0  48.0  (41.6)  identical, byte for byte
```

About a millisecond apart, inside a spread that climbs from 36 to 48 ms across the sequence whichever arm is
running. **The ceiling costs nothing at a fixed standoff**, and the identical geometry says so mechanically as
well as statistically.

> A paired A/B is only paired if the order alternates. Fixed A-then-B against a drifting machine measures the
> drift and attributes it to B.

### But the cost is not monotonic in standoff

Sealed, still, noon, fog on, pinned heavy seed, ascending then descending so drift shows as a gap between
passes rather than a trend within one:

```
standoff   up      down    trees    scene tri   cast tri
 24 m      33.5    29.9     4,723      4.3M        4.5M
 60 m      37.1    34.1     6,783      6.0M        9.1M
118 m      50.0    54.3     9,238      8.1M       16.7M
200 m      86.6    71.7    11,398      9.9M       23.1M
300 m      73.1    71.5    12,585     10.7M       27.3M
450 m      50.1    60.5    12,754     10.8M       27.6M
```

**The worst place to stand is the middle distance**, and both sweep directions agree on the shape even though
their absolute figures differ by 10-15 ms. Submitted geometry rises monotonically the whole way, so this is
fill rather than triangles: at 200-300 m the trees are still large enough on screen to cost pixels and already
numerous, while at 450 m each one is tiny. A far limit chosen to protect the frame would therefore have to
be a *band* exclusion, which is absurd — confirming §86's conclusion from the other direction: the far limit
is a legibility decision, not a frame-budget one.

It also explains the chair's report without contradicting it. Raising the ceiling gave access to the worst
band, and the heat persists after leaving it: the same fixed 78 m view drifted from 28 to 48 ms across fifteen
runs today. You return to where you were and it is slower, which reads exactly as the ceiling having done it.

### §86's plateau was a property of one map

§86 reported staged geometry identical at 250 and 450 m and concluded that the fog bounds the wide view. That
was measured on the **village default seed**, where the revealed region does plateau at about 4,295 trees. On
the pinned heavy seed it keeps climbing to 12,754. The plateau belongs to that map's revealed area, not to
fog in general, and §86 should be read with the seed named.

Which is the same lesson as §83's heavy-map absolutes, arriving a second time: **every figure in this arc is
a figure about one map at one standoff on one thermal state**, and the only claims that have survived are the
ones about mechanism — a queue drain, a byte-identical instance list, a shape that both sweep directions agree
on.

## 88. The near end is free, and the middle-distance peak is fragments

Two questions left open by §87: what the near end costs, and whether its 200-300 m peak is fill or geometry.
Both needed a lever that did not exist, and one of the two levers turned out to be worthless — which is worth
recording, because a dud lever reads exactly like a null result.

### The near end

Sealed, still, noon, fog on, pinned heavy seed:

```
 6 m    16.7 ms (60 fps)   3,582 trees   3.31M scene   2.71M cast
12 m    12.8 ms (78 fps)   4,042 trees   3.71M scene   3.19M cast
24 m    16.7 ms (60 fps)   4,723 trees   4.31M scene   4.50M cast
```

Comfortable, and the cheapest standoff in the game is about 12 m. So the near limit has no frame-budget
defence either — like the far limit (§86), it is a legibility and control question: too close and selection
loses its context. Both ends of the envelope are now design calls with the frame out of the argument.

### Fill against triangles, at the peak

Two levers, chosen so that each moves one term and leaves the other alone:

- `--width/--height` — the scene target is sized from the window, so a quarter-area window is a quarter of
  the fragments and exactly the same geometry;
- `--perf-tier-bias N` — every tree one or more detail levels coarser, which is fewer triangles over the same
  pixels.

Rotated ABCCBA, four runs per arm, at 200 m:

```
arm                    median    spread     scene tri   changed
baseline               59.2 ms   50-80      9.87M       -
quarter-area window    40.1 ms   33-48      9.87M       pixels / 4
tier bias +1           77.1 ms   70-81      8.71M       triangles -12%
```

**The frame is fragment-bound at the peak.** A quarter of the pixels takes about 19 ms off the median, and the
split says it more clearly than the total does: `outside` falls from 33-66 ms to 8-14 ms while the node phase
rises to 18-20 ms. Take work off the GPU and the frame stops waiting and becomes CPU-bound — which is the
signature, not an inference from one number.

**The tier lever is a dud, and its 77 ms is not a result.** At 200 m most trees are already at the far and deep
tiers by crowding, so one level coarser moved 12% of the triangles and none of the casters, and the arm's
median sits inside the baseline's own spread. All it establishes is the negative: a 12% triangle cut buys
nothing where a 75% pixel cut buys 30%. Reported because a lever that barely moves its own term will happily
be mistaken for evidence that the term does not matter.

### What that indicates, and what it does not

MSAA is 4x on an R11G11B10F scene target at every standoff, and at 200-300 m what is being antialiased is a
mush of subpixel trees — the case where the resolve costs most and buys least. Scaling MSAA or scene
resolution with the standoff is what the measurement points at. It is a **look** decision, so it belongs to
whoever is judging the look, not to whoever measured the fill.

It does not indicate anything about the caster passes: 23M submitted caster triangles at 200 m survived both
levers untouched, and §84's ablation already showed those are worth real milliseconds. Fragments dominating
the peak and geometry mattering are not in competition.

### The machine, again

The baseline arm's own spread was 50 to 80 ms within one session, after hours of continuous load. Every
absolute figure in §87 and §88 should be read as "this machine, this afternoon, in this order". What survives
is the paired deltas and the CPU/GPU signature flip — which is the same conclusion §84 reached about
thermals, reached again the hard way, and the reason this arc's trustworthy findings are all mechanisms.

## 89. MSAA is free here, so §88's recommendation was wrong

§88 ended by pointing at MSAA: the 200-300 m band is fragment-bound, and a 4x resolve of subpixel foliage is
the case where antialiasing costs most and buys least. `--msaa 1|2|4|8` makes that testable — at one sample
the scene renders straight into the target the present pass samples, with no resolve attachment declared,
because a single-sample source with a resolve is invalid and the graph does not check.

The first measurement said MSAA off was **19 ms slower**, which is absurd on its face and was the ordering
mistake again: 4, 2, 1 in fixed order into a warming machine, for the third time in one afternoon. Rotated
ABBA over two rounds:

```
msaa 4    50.30  50.30  50.42  66.73   median 50.30 ms
msaa 1    50.51  50.62  65.56  66.58   median 50.62 ms
```

Once the machine settles every run lands at 50.3-50.6 ms whatever the sample count; the two 66 ms figures are
the first runs of the sequence rather than an arm. **MSAA 4x is free on this GPU**, and the reason is
architectural: Apple Silicon resolves in tile memory, and standard MSAA shades per pixel rather than per
sample, so 4x buys coverage and a tile resolve and costs neither shading nor main-memory bandwidth.

So §88's closing recommendation is dead, and the fill it correctly identified has to be paid for somewhere
else. What is left, given a quarter of the pixels bought 30% and a quarter of the samples bought nothing:

- **per-pixel shader cost** — `world.frag` samples three cascades, applies fog, the veil and aerial
  perspective for every fragment, at every standoff; and
- **overdraw** — there is no depth pre-pass, so a fragment shaded behind a tree is shaded for nothing, and a
  wood at 200 m is many layers of tree.

A depth pre-pass is the same lever the Sponza work already had on its list, and overdraw is the term that
grows exactly where §87 found the peak: many mid-size trees, deep in each other's way. That is where the next
fill measurement belongs — not in the sample count.

### Three ordering mistakes in one afternoon

§84 established that this machine's absolute figures drift 25-75% under sustained load and that configurations
must therefore be compared interleaved. Three separate scripts written after that finding still ran their arms
in fixed A-then-B order, and each time the second arm came out worse: the shadow proxy (reversed by ABBA), the
zoom ceiling (reversed by ABBA), and MSAA (reversed by rotation). Every one of those false results was
directionally the same — the arm that ran last lost — which is the signature to watch for.

**A harness that knows about drift is not a harness that cancels it.** The rule is not "interleave", it is
"alternate the order and check that the effect survives reversal".

## 90. The art bracket, and the discovery that "vsync off" never turned vsync off

A proposal from the chair: drop the two LOD thresholds, draw every tree in full at every distance, cull only
by fog — and make it affordable by giving the tree substantially fewer triangles to begin with. The look is
not settled, so the art is a lever, and it is one the renderer work had been treating as fixed.

The renderer half was measurable immediately. `--tree-crowd max` puts both thresholds at their maxima (12 and
20), which is as close to "nothing ever coarsens" as the scheme allows; `--perf-tier-bias 3` puts every tree
at the coarsest level the chain holds. Between them they bracket what tree detail costs, without the cheaper
art having to exist yet.

Rotated A B C C B A per standoff, 250 frames each, pinned heavy seed, fog on, noon:

```
standoff   all full     current tiers   all coarsest      trees
 24 m      13.35M          4.31M           3.54M          4,723
118 m      24.72M          8.12M           6.96M          9,238
200 m      29.77M          9.87M           8.42M         11,398
450 m      32.49M         10.84M           9.33M         12,754
```

Two findings, both from the exact column rather than the timed one:

1. **The tiered scheme is already within 15% of drawing everything at the coarsest level.** Crowding sends
   dense woodland to the far and deep tiers so aggressively that the remaining detail is a rounding error.
   There is no triangle win left in more LOD — which retires "tune the sliders" as a performance lever for
   good.
2. **All-full costs about three times the current triangle budget.** That is the number the proposal needs: a
   uniform model at roughly a third of the near model's 5,940 triangles drawn in full everywhere lands on
   today's budget, and at a quarter it comes in under — while deleting both thresholds, every tier boundary
   and three of the four `PropModel` sets per species. The chair's instinct is arithmetically sound.

Also worth recording: `--perf-tier-bias 3` also removes undergrowth and contact shadows, which are drawn only
for tier-0 trees. It is a fair proxy for the geometry question and **not** a fair preview of the look.

### The instrument, wrong again, and this time about all of it

The bracket's frame column came out suspiciously clean: 16.66, 33.33, 50.00, 66.67 ms, reproducing to a
hundredth across repeats. Those are exactly one, two, three and four multiples of 16.667 ms.

```
                p50            p95          max
Mailbox    33.33  33.33   33.8  33.6   49.6  34.3
FIFO       33.37  33.40   50.2  50.1   51.0  51.3
```

**Mailbox and FIFO have the same median.** On this MoltenVK/Metal path, "vsync off" does not uncap
presentation at all — Mailbox trims the tail and nothing more. Every `frame_p50` in §83-§89 is therefore a
cadence rather than a cost whenever the machine was cool enough to land on a step, which is precisely the
possibility §83 claimed to have eliminated. It passed then only because a hot machine was missing steps
untidily enough to look unquantised.

What that costs this arc, stated without softening:

- **Gone:** the millisecond magnitudes taken from frame p50 — §84's proxy "7-10 ms", §85's upload fix "~10 ms",
  §88's half-res "19 ms". Their *directions* survive where a phase split or a signature flip backed them; the
  sizes do not.
- **Standing:** anything measured by a stopwatch inside the frame or counted on the CPU. The fog column going
  20.3 to 0.27 ms is a direct measurement around the call, so the queue drain and its removal are real. The
  phase splits, the CPU/GPU signature flips, every triangle and instance count, and the byte-identical
  geometry that killed the zoom-ceiling theory all stand.

### The only instrument left worth building

A 16.7 ms ruler cannot measure a change worth five. Per-pass GPU timestamps are no longer the last item on the
list, they are the *only* way to get an unquantised figure for anything on the device — and `VkGpuPassTiming`
already exists in the engine, with the diagnostics line already printing an `execute` figure beside
`build-commands`. That is the next thing to build, before any further conclusion is drawn about the GPU.

Which makes four instrument corrections in one arc: the frame that did not close against its halves (§83), the
thermals that reversed three verdicts (§84, §87, §89), the fixture that measured its own diagnostics (§85, and
it turned out not to matter), and now the present mode that was never doing what its own console line claimed.
The pattern has stopped being a caution and become the method: **no figure from this game is believed until the
instrument that produced it has been attacked.**

## 91. The pre-kit trees were in the repo the whole time

Three things the chair asked for: separate the dressing from the tier-bias lever, get per-pass GPU timestamps,
and swap in the cheaper tree we already own. Two landed, one is impossible on this platform, and the third was
better than the arithmetic predicted.

### The dressing no longer follows the lever

`CanopyTierAt` returned the biased tier, and the same value chose the mesh *and* decided which trees get
undergrowth and a contact shadow. So `--perf-tier-bias 3` quietly previewed a world with no ground cover,
which made it useless for the look question it was meant to inform. The natural tier now decides the dressing
and the biased one decides only the mesh. A measurement lever has no business changing the dressing.

### Per-pass GPU timestamps: not on macOS, and the engine already said so

`VulkanGraphicsDevice.Swapchain.cs:69` has carried this note since the timing pool was written: on MoltenVK
`vkCmdWriteTimestamp` lowers to Metal counter samplers that resolve *after* the in-flight fence, so
`vkGetQueryPoolResults` returns NotReady even with `ResultWaitBit`, and "on macOS the CPU timers carry the
perf story". Promised before reading it; the report now distinguishes "device reports no support" from
"supported but the drain delivers nothing", and this device says the latter.

What did come out of the attempt is the substitute, and it is a good one. `VkCpuFrameTiming` splits the host's
frame into **wait** (`vkWaitForFences` — GPU throttle and present pacing together), **encode** (command
recording, the term draw count drives) and **submit+present**. Wait is not quantised by the 16.7 ms pacing
§90 found, so it is the first device-side number in this arc that can resolve a small change. Per-pass
attribution is then ablation plus wait — skip a pass, read the difference — which is what `--perf-cascades`
already provides.

### The cheap trees

`Resource_Tree1/2` and `Resource_PineTree` have been sitting in `Assets/models` since before the kit: **552,
384 and 345 triangles**, against the kit's 3,947 to 9,564 — cheaper than even the kit's coarsest cooked level
(603). §90 reasoned that a tree at a third of the kit's count could be drawn in full everywhere for today's
budget. The real thing is an eighth.

`--cheap-trees` fills all four tiers from those three models, mapped onto the ten species slots so
`TreeKindAt`'s ground-to-species meaning survives (twisted and dead lose their distinction, which is a real
loss and is why this is a switch). Measured cool, rotated A B B A, reproducing to a hundredth:

```
standoff  arm     frame    wait    encode   scene tri   cast tri
 24 m     kit     16.66    0.06    0.34      4.31M       4.50M
 24 m     cheap   16.67    0.04    0.18      2.06M       2.66M
118 m     kit     33.33   12.38    0.48      8.12M      16.73M
118 m     cheap   16.68    0.89    0.25      4.06M       9.67M
450 m     kit     33.39   18.27    0.51     10.84M      27.61M
450 m     cheap   16.69    2.07    0.28      5.51M      15.95M
```

**Thirty to sixty frames a second at both wide standoffs**, and the wait — real milliseconds, unpaced —
falls from 12.4 to 0.9 and from 18.3 to 2.1. Encode halves too, because fewer distinct models are staged. At
24 m nothing changes: that frame was already inside one interval.

So the chair's proposal is not merely affordable, it is the largest single win in stage 0, and it deletes both
crowding thresholds, every tier boundary and three quarters of the LOD machinery on the way. What it costs is
a look, and that is the only open question left on it — which is now a fair question to ask, because
`--cheap-trees` keeps the undergrowth the tier bias used to take away.

Note also that casters remain the dominant geometry even cheap: 9.67M of 13.7M submitted at 118 m, because
every tree casts into up to three cascades. Cheap trees and §84's coarse-cascade proxy are complementary, not
alternatives.

### Two more instrument failures, for the record

**A zsh word-splitting bug ran twelve identical configurations.** An inline `go() { ... $2 ... }` helper passed
`"--perf-cascades 0"` as one argument, and zsh — unlike bash — does not split unquoted expansions, so the app
saw an unknown argument and ignored it. Twelve runs of one configuration, presented as three arms of an
ablation. Caught only because `cast_tris` was identical in all of them, which is the kind of column that
exists for exactly this. The rule for these scripts is now: a bash script file with `"$@"`, never an inline
zsh function.

**And it accidentally produced the best variance measurement of the arc**: twelve runs, one configuration, one
session — frame p50 from 33.4 to 51.2 ms. Fifty-five per cent, in order, monotonically upward. Any absolute
figure quoted from a hot machine is worth about as much as that spread.

## 92. The proxy's gate is density, and it was on the tier that means "alone"

From the chair, after looking: the proxy is serviceable except on smaller groups at mid to far range, where the
shadows read as diamonds — and the gate should not be distance, it should be how surrounded a tree is.

Both halves of that are right, and the second explains the first. `distantProxy: true` was set on **all four**
tier arrays, including `trees` — the tier a tree lands in when it is *not* crowded. So an isolated tree cast an
octagonal bipyramid into the coarse maps with no neighbour to absorb the substitution, which is precisely the
case where the shadow is the only thing describing the tree. A diamond, seen as a diamond.

§56 settled this for the meshes: coarsening works where the neighbours put back the mass it loses, so it
belongs to crowding rather than to distance. The tiers are *already* chosen by crowding, so the gate needed no
new machinery — only the proxy withdrawn from the two tiers that mean "not crowded". It now applies to
`treesFar` and `treesDeep` alone, which is crowd at or above `TreeCrowdFar`.

**The gate is nearly free.** At 118 m the ungated proxy took submitted casters from 16.73M to 4.02M; the gated
one takes them to 4.25M. Isolated trees are a small minority of the casters in woodland, which is why the
version that looks right costs a fifth of a million triangles more than the version that does not.

### The two levers, composed

They did not compose at first: with `--cheap-trees` all four tiers shared three models, and one shared model
cannot express a per-tier gate. So the cheap path now builds two variants — open and dense, six models of about
four hundred triangles, still cheaper than one of the kit's near species.

```
118 m                    scene      cast      coarse maps
baseline                 8.12M     16.73M     6.56 / 6.81M
gated proxy              8.12M      4.25M     0.44 / 0.45M
cheap trees              4.06M     15.95M*    5.32 / 5.32M
cheap + gated proxy      4.06M      2.77M     0.35 / 0.36M

450 m
baseline                10.84M     27.61M     9.20 / 9.20M
gated proxy             10.84M     10.35M     0.57 / 0.57M
cheap trees              5.51M     15.95M     5.32 / 5.32M
cheap + gated proxy      5.51M      6.20M     0.44 / 0.44M
```

Together they take the wide view from 27.6M submitted caster triangles to 6.2M, and the scene from 10.8M to
5.5M — and at 450 m with cheap trees the frame sits inside a single 16.7 ms interval with a **wait of about a
tenth of a millisecond**. The GPU has stopped being the limiter at the widest standoff in the game.

Which moves the frontier: at that point the frame is CPU, and the node phase is about eleven milliseconds of
it. That is the next thing worth attacking, and it is the first time in this arc the answer has been on the
CPU side.

*A flag bug, found by the arms reporting identical numbers: `Legacy` ignored `distantShadowProxies`, so
`--cheap-trees` proxied whether or not `--shadow-proxy` was given, and the two arms of that comparison were the
same configuration. The third measurement failure of exactly this shape today — two arms, one behaviour — and
all three were caught by an exact column rather than by a timing. Keep the exact columns.

## 93. The freeze, attributed: two flow fields an order at 150 ms each

Reported from the chair: pathfinding can freeze the app for seconds on real maps, across larger distances or
for larger groups, independently. The report is right about the freeze and wrong about both causes, which is
worth stating carefully because the wrong causes are the plausible ones.

`--ordertest` already existed for this — "the order tick, and the ten ticks after it, as a player would feel
them". It says:

- **Not distance.** A 29 m order and a 559 m order cost the same to the millisecond.
- **Not group size.** Thirteen agents and fifty agents cost the same.
- **Not A\*.** Three region searches per order, and `PathQueries` barely moves.
- **Map area and terrain detail.** 600 m costs 260 ms, 1200 m costs 930 ms; flat repeat orders cost 28 ms and
  sculpted ones 340 ms.

Three counters were added to say which of the three candidate structures owns it — the rectangle mesh, the
region tiles, the field — because "probably the mesh" is not a diagnosis. On a sculpted 600 m map, per order:

```
order 1 | 568 ms | mesh 233 ms (1 build, 562 rects) | tiles 33 ms (3 fills) | field 301 ms (2 built)
order 2 | 336 ms | mesh   0 ms (cache hit)          | tiles 34 ms (3 fills) | field 301 ms (2 built)
...
order 8 | 337 ms | mesh   0 ms (cache hit)          | tiles 34 ms (3 fills) | field 303 ms (2 built)
```

**About 150 ms per flow field, two fields per order, every order, forever.** The mesh cache does work — it is
paid once at 233 ms and then hits — and the tiles are a tenth of the problem. The field is the freeze.

What a field's constructor does is a Dijkstra over the rectangle mesh's crossing corners: 562 rectangles and,
by the file's own note, on the order of 1,682 crossings. At 150 ms that is roughly ninety microseconds per
corner settled, which is two orders of magnitude more than a Dijkstra relaxation should cost — so the
suspicion is not the algorithm's shape but what `LegBetween` does per edge. That is the next measurement, not
the next assumption.

And the 233 ms mesh build is not retired by being cached: it is keyed on `grid.Revision`, and construction
changes the placement grid, which re-rasterises navigation, which bumps the revision. On a village that is
actively building, the 233 ms comes back — at 905 ms on a 1200 m map. That is the shape of "frozen for
seconds": a mesh rebuild and a pair of field builds landing in the same tick.

### What the fix has to separate

1. **Why a field costs 150 ms.** Measure `LegBetween` and the crossing graph's real size before restructuring
   anything. If a field can be built in five milliseconds, most of this problem stops existing and the rest of
   the list changes shape.
2. **Why two fields per order** rather than one. One is the cohort's shared transit field; the other is
   probably slot reachability at the class radius, or a second radius key. If they can share, the freeze
   halves for free.
3. **Nothing this size belongs on the tick.** Even at five milliseconds, a whole-map structure built
   synchronously inside a command is a stall waiting for a bigger map. It wants a budget and a slice across
   ticks, with bodies moving on a provisional heading until the field lands — which is a behaviour question as
   much as a performance one, and therefore belongs with groups.
4. **The mesh wants to survive a nav change.** A building finished in one corner of the map should not
   invalidate a decomposition of the whole of it. Region-local invalidation, or a mesh keyed per region.

Order matters here: (1) is a measurement, (2) is possibly free, (3) is the durable fix and needs the group
work beside it, and (4) is the one the village actually trips over. Stage 0's rule applies unchanged — measure
the instrument, then the mechanism, then change something.

## 94. The freeze is A* after all — in the ticks after the order, not in it

§93 attributed the order tick and stopped there, because the synthetic fixture said the ticks after an order
cost 0.13 ms. On the village the game actually builds, they cost up to 315 ms each for twenty ticks. Six
seconds, from one click.

`--pathprofile` builds the real village — `SettlementScenarios.BuildVillage`, 24,933 nodes, 20 people, 32 m
relief — and clicks the far corner, alternating corners so no order inherits the last one's goal:

```
order 1 |  435.0 ms | mesh 0 (5,871 rects) | tiles 118.5 ms (7 fills) | field 309.1 ms (2 built)
        |    2.27 ms/tick over the next 20 | paths   0.05 ms
order 4 |    0.3 ms | mesh 0 (5,871 rects) | tiles   0.0 ms (0 fills) | field   0.0 ms (0 built)
        |  314.61 ms/tick over the next 20 | paths 186.87 ms
```

**The order tick and the freeze are two different problems**, and the second is the one being reported:

- the order tick pays two flow fields and a handful of tile fills — 430 ms on this map;
- the ticks *after* it pay per-agent A\*, at up to 315 ms a tick sustained, which no routing counter I added
  could see because `RoutingCost` covers the mesh, the tiles and the field and not `FindPath`.

Why the synthetic world hid it: it has few blockers, so the cohort's shared flow transit serves every member
and nobody falls back. A real village has twenty-five thousand trees and outcrops. The walkable area cuts into
**5,871 rectangles** against the test world's 562, transit fails for members it cannot price, and each one
falls back to `AssignPath` → `FindPath` — a full A\* over a 1200x1200 cell grid, per body, on the tick, with no
node budget and no per-tick cap.

So the original report was right about the cause and I was wrong to retire it. What was wrong was only *when*:
not the order, the movement that follows it.

### Four stalls, and what each one wants

1. **Per-agent A\* on the tick, uncapped.** The immediate freeze. Wants two bounds — a node budget per search
   and a count budget per tick — and a rule for what a body does while its path is still pending. That rule is
   a group-behaviour decision, which is where this arc's two halves meet.
2. **Why transit fails at all on a village.** If the cohort's shared field served every member, no A\* would
   run. This is the real fix and the one that removes the class of problem rather than bounding it.
3. **Two flow fields an order at ~150 ms each** (§93), now measured at 300 ms on the real map. Still wants the
   `LegBetween` measurement.
4. **A 233 ms mesh rebuild on any nav change** — construction re-rasterises navigation, and this map is 5,871
   rectangles rather than 562, so the rebuilt cost is higher than §93's figure. Wants region-local
   invalidation.

Order: (1) to stop the bleeding and make the game playable enough to judge anything, (2) because it is the
actual bug, then (3) and (4).

### The lesson, again, in the same shape

Three fixtures in this arc reported that everything was fine: `--ordertest` on flat ground, `--ordertest`
sculpted, and the first `--pathprofile` before phase timings were printed. Each was measuring a real thing and
each omitted the term that mattered — the update half in §83, the fog upload in §85, `FindPath` here. **A
split that only covers the structures you suspected will always blame one of them.** The fix, every time, was
to make the frame or the tick close against its own total, and then look at the residue.

## 95. The bound and the invariant are in conflict, and the fallback is why

§94's first item was "cap and slice A\*, to stop the bleeding". The cap works and the bleeding does not stop,
which is a more useful result than it sounds.

### What was tried

Two changes, measured with `--pathprofile` on the real village and gated against the self-tests:

**A weighted heuristic.** The estimate prices a cell at the cheapest surface cost that exists, while the real
step charges surface, elevation, turning and congestion — so on sculpted ground the true cost runs several
times the estimate and A\* barely steers. That was the diagnosis, and the sweep refuted it: at weights of 2.2,
3.0, 4.5 and 6.0 the stall window does not move at all, every cross-map search still runs out of budget in the
same place. A heuristic that was steering would find the goal sooner as the push increased. This one does not.
At 2.2 it also fails two self-tests — the pen escape and the determinism fingerprint. **Left at one.**

**An expansion budget** with the partial route as its fallback. This works, and the price is exact:

```
budget      stall window        self-tests
 30,000     34 ms/tick  (9x)    "a group crosses region borders without swinging" FAILS
100,000    105 ms/tick  (3x)    still fails
250,000    262 ms/tick  (~0)    all pass
```

A bound tight enough to fix the freeze hands bodies truncated routes, and a body that re-plans from the end of
a truncated route swings where it used to hold a line. That invariant was won in the locomotion arc and is not
for trading against a hitch.

### What that says

**The bound is not the fix — the fallback is.** A search that runs out should not hand back half a polyline; it
should leave the body on the cohort's flow field, which already holds a non-swinging answer for that goal and
which the order tick has already paid for. The field is the coarse layer's whole purpose, and the A\* fallback
is currently going around it.

So the budget ships at 250,000 as a **ceiling and not a solution**: it caps a pathological search at about 650
ms against the 1.8 seconds measured, and it matters much more on a larger map — a 1200 m world is 5.76M cells,
where the same search would run for fifteen seconds. The 600 m freeze is untouched, deliberately, because the
alternative was shipping a locomotion regression to make a number look better.

### The next slice, now precisely stated

1. **Make the fallback the field, not a fragment.** `AssignPath` needs to know a search was bounded and choose
   transit instead of a partial route. Then the budget can come down to where it fixes the freeze.
2. **And ask why cell A\* is running for a cross-map order at all.** Order 4's field was already built and
   cached; the bodies were on transit; something moved them off it. Five queries in twenty ticks says whatever
   that is, it happens repeatedly. Finding it may remove the need for a fallback rather than improve one.

Also recorded, because it is the third time in two arcs: a fix that improves a number and breaks a test is not
a trade to make quietly. The measurement said 9x and the gate said no, and the gate is the one that knows what
the game is supposed to do.

## 96. Bodies were searching the map to reach a field they were standing beside

§95 said the bound was not the fix and the fallback was. It was, and the numbers are absurd in the good
direction: the stall window after a cross-map order falls from **300.6 ms a tick to 1.17**, and the expansions
that produced it from **1,348,421 to four**.

### Why transit dropped

Instrumented by cause, because "transit drops" is two different faults: a body the field keeps pointing into
something it cannot walk through (rejected steps), and a field with no answer where the body is standing (no
gradient). On the village, over twenty ticks: **zero rejected, two no-gradient.**

No gradient means `CostAt` returned infinity for the body's cell. The tile is filled on demand, so this is not
a missing tile — it is a tile that could not price that cell. Region tiles are seeded from their perimeter
through the analytic corner graph, and a cell in a pocket the perimeter cannot see into stays infinite. With
twenty-five thousand tree and outcrop blockers cutting the walkable area into 5,871 rectangles, those pockets
exist.

And the old answer to "the field cannot price you" was **search the whole map to the final goal**. The field
already knows the way from every cell it *has* priced, so the only question worth asking is where the nearest
one is. On the village, it was **one cell away**.

### The fix, and the two ways it was wrong first

`FindFieldEntry` is a bounded breadth-first walk for the nearest cell the cohort's field can price. A short
path there, and the body rejoins the shared route rather than replacing it.

Getting the rejoin condition right took three attempts, and the two failures are the interesting part:

1. **"Destination differs from RequestedDestination"** — on the reasoning that only an escape path produces
   that. False: `AssignPath` resolves an unwalkable goal to a nearby cell and leaves the same difference. It
   fired for bodies on deliberate individual routes and broke both pen tests plus the fingerprint census.
2. **"A group member with no path left"** — also true of bodies the congestion machinery is about to repath.
   Four crowd tests failed: chokepoint filing, the single-cell gate, pen backpressure, congestion sweeps.
3. **An explicit `AgentState.SeekingFieldEntry`.** A body's intention is not reliably inferable from its
   geometry, so it is written down. The save signature and the fingerprint both pick it up automatically —
   `Marshal.SizeOf<AgentState>()` and `AgentStateSchema` were built for exactly this, so a new body field costs
   one declaration.

### And the thing the old fallback was doing by accident

With the rejoin correct, two pen tests still failed — and the cause was not a bug. **A body that solves its own
route can pick a different exit; every body solving separately is diversity, expensively bought.** Replacing
that wholesale with one shared gradient funnels the cohort, and the pen tests exist to assert it does not.

Distance did not separate the cases: bounding the entry search to a few cells' radius changed nothing, because
pen bodies are also standing beside priced ground. **Crowd pressure does**, and that is what the pen tests are
for. Measured at the drop site rather than guessed: the village's cross-map drops sit at **0.083** — twenty
bodies with room, near each other only because they were ordered together — and a pen holding fifty against a
single-cell gate is an order of magnitude above. The ceiling is 0.5, with room on both sides. The first
attempt, "any pressure at all" at 0.01, blocked precisely the case the change exists to fix, which is what
measuring the drop site is for.

### Where the order tick stands now

```
order 1 | 414 ms | tiles 112 ms (7 fills) | field 296 ms (2 built) |  2.13 ms/tick after
order 2 | 436 ms | tiles 146 ms (9 fills) | field 289 ms (2 built) |  1.11 ms/tick after
order 3 |  17 ms | tiles  16 ms (1 fill)  | field   0 ms (cached)  |  0.26 ms/tick after
order 4 |   0 ms | nothing to build       | field   0 ms (cached)  |  1.17 ms/tick after
```

The freeze after an order is gone. What remains is the order tick itself on a *new* goal: two flow fields at
about 150 ms each, plus tile fills. That is §93's item 3, untouched and now the largest single cost in the
game's response to a click — and §95's `LegBetween` question is still the way in.

The expansion budget stays at 250,000. It never fires on this map any more, which is what a ceiling should
look like.

## 97. The abstract layer was recomputing the terrain half a million times a click

§96 left the order tick as the largest cost in a click's response: about 430 ms on the village, of which ~290
was two flow fields. §95 guessed `LegBetween`. It was, and the guess was right for the wrong reason.

### What it was

`LegBetween` charges a corner-to-corner leg the same climb the fine field charges, through
`ClimbSecondsAlong`, which samples heights along the segment — up to forty-eight of them. The corner Dijkstra
calls it for every edge relaxation. Measured per order:

```
climb 496,040 calls, 4,617,012 height samples
```

Half a million calls and four and a half million samples, **for every click**, recomputing a quantity that
cannot change: between two fixed corners the climb is a fact about the terrain, and both the corners and the
terrain are fixed for as long as the mesh is — all three are keyed by the navigation revision. So it is cached
on the mesh, and every field built on that decomposition inherits what the first one learned.

```
                field      order tick   climb calls
before          290 ms      430 ms       496,040
after            38 ms      158 ms        14,952
```

**Seven and a half times off the field build**, and the order tick's largest term is now the region tile fills
at 112-152 ms.

### Two ways it was wrong first, both instructive

**Symmetric keys cost eight times what they saved.** A climb charge is a sum of absolute height differences,
so reversing a leg changes nothing in exact arithmetic — but it is *sampled*, from one end, so the two
directions disagree by a sampling artefact. Sharing one entry between them made a leg cheaper one way than the
other, which is exactly the invariant `Expand` is written around, and the Dijkstra answered by settling
corners over and over: 290 ms became 2,353 while the calls it was meant to save fell by a factor of
thirty-three. The tell was that pairing: fewer computations, far more time.

**Then a packed key collided catastrophically.** `(from << 32) | to` is the obvious encoding, and .NET hashes
a `long` by folding its halves with XOR — for two corner indices under 2^16 that is `from ^ to`, so thousands
of distinct pairs share a bucket. Half a million entries turned every lookup into a chain walk: 4,487 ms, worse
than no cache at all, with the relaxation count unchanged. A `ValueTuple` key hashes through
`HashCode.Combine`, which mixes, and the same code then ran in 38 ms.

Both failures looked like "the cache is slower than the computation", which is nearly always false and was
worth disbelieving twice. The instrument that caught them both was the pairing of *calls* against *time*:
when the work goes down and the clock goes up, the answer is never the work.

### Where the click stands

```
order 1 | 158 ms | tiles 112 ms (7 fills) | field 38 ms (2 built)
order 2 | 192 ms | tiles 152 ms (9 fills) | field 39 ms (2 built)
order 3 |  18 ms | tiles  18 ms (1 fill)  | field  0 ms (cached)
order 4 |   0 ms | nothing to build       | field  0 ms (cached)
```

From 430 ms to 158, and from a six-second freeze two sections ago to nothing measurable. What is left:

1. **Region tile fills, 112-152 ms** — now the largest term. Seven to nine fills at ~16 ms each, and the same
   question applies: how much of a tile fill is recomputing something the mesh already knows.
2. **The 233 ms mesh rebuild on any nav change** (§93 item 4), untouched, and the one an actively building
   village trips over.

## 98. The tile fill, and the corner tests that were reading the same four cells ten times

§97 left region tile fills as the largest term in a click: 112-152 ms for seven to nine fills. Split into its
two halves — pricing the perimeter through the corner graph, and searching inward from it — the answer is not
where the seeding is:

```
seed 12.8 ms (1,764 perimeter cells priced) | search 100.4 ms (205,133 visits, 483 ns each)
```

A region is 64 x 64 cells and a tile runs to exhaustion, so a fill is about 32,000 neighbour visits. **483
nanoseconds a visit** is ten to twenty times what a grid Dijkstra step should cost, so the question was which
term owned it. Ablated, on the same fill:

```
baseline               100.6 ms   491 ns a visit
no turn charge          97.1 ms   473 ns      -- 3.5 ms, noise
no diagonal corners     55.0 ms   268 ns      -- 45% of the search
neither                 53.2 ms   260 ns
```

**The corner tests are nearly half the fill.** A diagonal step must not cut either corner, which
`CanTraverseFlow` asked by calling `CanTraverse` five times — (from,to), then both corners from each end. Those
five calls span exactly four distinct cells, so six of their ten `GroundAt` reads were re-reads, along with
six redundant bounds checks.

`NavigationGrid.CanTraverseDiagonal` now reads the four grounds once and applies the same five predicates. The
result is identical rather than approximate: the old answer was the AND of five side-effect-free predicates, so
evaluating them in any order over the same ground values gives the same boolean, and only the number of reads
changes. Measured: **491 to 393 ns a visit, the tile search 100.6 to 80.7 ms**, and the order tick from 158 to
138.

### What is left in a tile, and why it was left

The ablation says another ~125 ns a visit is still corner work, and it is the grade test's
`Vector2.Distance(CellCenter(from), CellCenter(to))` — two cell-centre reconstructions and a square root, five
times per diagonal edge, to recover a distance that is always one cell or one diagonal. Replacing it with a
constant is the obvious next step and is **deliberately not taken**: this file's own comment explains that a
different number in the last bit is a different route out of a Dijkstra, and the routing-fidelity suite exists
to catch exactly that. A cheap arithmetic identity that changes a float in the last place is not cheap.

If it is worth taking later, it wants the fidelity comparison run before and after rather than the gate alone —
green tests prove no invariant broke, not that the routes are the same routes.

### Where a click stands, end to end

```
                        order tick    field      tiles     freeze after
§94 (as reported)          430 ms     290 ms     112 ms     300 ms/tick
§96 (field fallback)       430 ms     290 ms     112 ms       1.2 ms/tick
§97 (climb cached)         158 ms      38 ms     113 ms       1.2 ms/tick
§98 (diagonal reads)       138 ms      38 ms      93 ms       1.2 ms/tick
```

A six-second freeze became a 138 ms click, and the remaining 138 is two thirds tile fills. Still open, in
order: the ~125 ns of distance arithmetic above (with fidelity checked, not assumed), and §93's 233 ms mesh
rebuild on any navigation change, which is the one an actively building village trips over.

## 99. What a finished building costs, and why the mesh is the wrong half to attack

§93 listed a 233 ms mesh rebuild on any navigation change as the thing an actively building village trips
over. Measured properly on the village the game builds, the event costs more than that and the mesh is not
most of it. `--pathprofile` now forces one placement change and orders again:

```
raster     774.1 ms  (1,440,000 cells) — terrain pass 288.5 | clearance 459.3 | apply 26.1
next order 677.4 ms  — mesh 380.8 | tiles 123.8 | field 170.4
```

**About 1.45 seconds per completed building**, and the split matters because the two halves land in different
places. The raster is paid *in the tick the building completes* — unavoidably, synchronously. The mesh and the
field are paid on the next click, and the field's 170 ms is the §97 climb cache being invalidated with the
mesh, which is correct and cheap to re-earn.

### Why the mesh is the harder half and the smaller one

`WalkableRectangles.Build` is a global row sweep that carries open rectangles down the grid and emits maximal
ones. Rectangles are not clipped to regions, so a change anywhere can split one that spans a quarter of the
map: "rebuild only what changed" is not expressible without changing what a rectangle *is*, and what a
rectangle is happens to be the thing the routing-fidelity suite is written against.

What is expressible is **amortisation**. The sweep's whole state is a handful of per-column arrays and a row
index, so it is resumable by row: build the new mesh over N ticks while the old one keeps serving. The click
pays nothing, and the staleness is bounded and behaviourally safe — the fine layer refuses to walk through a
new building whatever the abstract layer thinks, so a stale rectangle costs a re-plan and not a wall walked
through. It needs determinism care (the mesh in use at a tick becomes a function of build progress, so a save
must either finish the build or record it) which is exactly the sort of thing that has bitten this arc twice.

### Why the raster is the better target

Two thirds of it is provably unnecessary work rather than work that needs restructuring:

- **The terrain pass, 288.5 ms**, samples surface and height for 1.44M cells. A placement change cannot move
  terrain. Every one of those samples is recomputing a value that is already correct.
- **The clearance pass, 459.3 ms**, is a distance-to-nearest-obstacle over the whole map. It is already tiled
  and indexed — it used to be twelve seconds — but it is still whole-map, and only cells within
  `ObstacleIndex.Reach` of a *changed* obstacle can have changed.

Both want the same missing thing: the placement grid does not say **what** changed, only that something did.
Give it a dirty rectangle and both passes become local, and the 774 ms becomes tens.

So the order is the reverse of §93's guess: raster first, and the mesh by amortisation rather than by
incrementalisation. Recorded rather than acted on, because the raster fix touches the navigation grid's
contents and revision semantics, which is where a quiet divergence would live.

## 100. The distance in the grade test, and a fidelity harness earning its keep

§98 left ~125 ns a visit of corner cost on the table and declined to take it, because the candidate was
replacing `Vector2.Distance(CellCenter(from), CellCenter(to))` with a constant and this code's own comment
warns that a different float in the last place is a different route out of a Dijkstra. Taken now, lightly, and
the declining was right about the risk and wrong about the fix.

**A constant is not needed.** A cell centre is `Origin + (cell + 0.5) * CellSize`, so the difference between
two of them is `cellDelta * CellSize` — the same value, reached without rebuilding either centre. `CellDistance`
computes that and passes it to the same square root, so nothing about the arithmetic changes except how many
operations produce it.

Whether that is *bit*-identical is a property of the grid's parameters rather than of the method: half-metre
cells on a map within a few hundred metres of the origin make every product exactly representable, so the
subtraction is exact and both routes agree. An awkward cell size or a distant origin could round them
differently. So it was checked against the routing-fidelity harness rather than the test suite alone:

```
                 before                                              after
clear ground     mean 1.0014 | p99 1.0977 | worst 1.187 at 345,70    identical, to the cell
with a jam       mean 1.0019 | p99 1.0977 | worst 1.187              identical
lost             0/154104                                            0/154104
```

Identical including *which cell* the worst ratio is at, which is the part a summary statistic would have hidden.
That is what the harness is for, and it is the difference between "the tests still pass" and "the routes are
the same routes".

Measured cost: the tile search **80.7 to 69.5 ms**, 393 to 339 ns a visit, and the click's tile term 93 to 81
ms. Cumulatively with §98 the corner work has gone from 491 to 339 ns a visit, against a floor of 268 with the
corner tests removed entirely.

### A note on a number not to quote

The field term in the profile has become bimodal between runs — 38 ms in some, 66 in others, stable within a
run and unaffected by anything either section touched. It is tiered JIT in a Debug build reaching different
methods at different points, and it is a reminder that this profile's *deltas within one run* are the
trustworthy part. The tile figures above repeat to a tenth of a millisecond across runs; the field figures do
not, and are not claimed.

### Where the click stands

```
                        order tick    field      tiles
§97 (climb cached)         158 ms      38 ms     113 ms
§98 (diagonal reads)       138 ms      38 ms      93 ms
§100 (cell distance)       ~155 ms    ~66 ms      81 ms
```

The tile term is down 29% across the two sections. The order tick reads worse only because the field term
moved for unrelated reasons; the parts that were changed are the parts that improved.

Still open, from §99: the raster's 774 ms on a placement change — two thirds of it provably unnecessary — and
the mesh's 380 ms, which wants amortisation rather than incrementalisation.

## 101. A building no longer re-rasterises the map it did not touch

§99 measured a finished building at 774 ms of re-rasterisation in the tick it completes, and named two thirds
of it as provably unnecessary: a placement change cannot move terrain, and only cells within
`ObstacleIndex.Reach` of the change can have a different clearance. What was missing was that the placement
grid said *something* had changed and never *what*.

It says what now. `SetOccupied` accumulates one dirty rectangle — one rather than a list, because two buildings
finishing in the same tick want a single slightly larger window rather than two passes, and a rectangle can
only be too large, never wrong — and `ConsumeDirtyBounds` hands it over exactly once.

`NavigationRasterizer.RebuildWithin` then works in three nested windows, which is the part that took three
attempts to get right:

- **the dirty rectangle** — the only cells whose terrain-derived values are recomputed, and on a placement
  change that recomputation is redundant anyway;
- **plus one reach** — the cells whose clearance can have changed;
- **plus another reach** — the cells whose *obstacles* must be gathered, because a cell at the edge of the
  clearance window is itself within reach of ground outside it, and an obstacle set missing that box gives a
  wrong answer rather than a stale one.

```
                                  full      local
raster                           774 ms    100.6 ms
  terrain pass                   288 ms      0.0 ms   (skipped: terrain did not move)
  clearance and gathering        459 ms      0.7 ms   (1,208 cells, 4 boxes)
  apply                           26 ms     35.8 ms
```

The remaining ~64 ms is reading the chunked raster back into flat arrays so the untouched cells can be handed
through unchanged, and the apply that writes all of it back. Both are whole-map array work on a change that
touched ninety cells, and both would go with a windowed apply — but the mesh rebuild on the same event is
still 380 ms, so sharpening this further would be optimising the smaller half again.

### The test that makes it a saving rather than a gamble

Skipping 1.44M terrain samples is only sound if the answer is unchanged, and "should be identical" is exactly
the claim that stops being true quietly. So there is a self-test: build a sculpted world, block some ground,
refresh locally, then rebuild fully and compare **every cell** on blocked, clearance, height, traversal cost
and speed. It is sculpted deliberately — on flat ground the terrain half of the raster is uniform and a bug in
it cannot show, which is how a test like this passes while the feature is broken on every map anybody plays.

It earned itself immediately. Three separate faults surfaced through it or the profile beside it:

1. **A navigation cell index handed to the placement grid.** They have different cell sizes — 1.5 m against
   0.5 — so the test blocked nothing and said "the refresh did not take the local path", which is the failure
   message doing its job.
2. **The profile was measuring the wrong path.** It called `RebuildTerrainNavigation` directly, so it went on
   reporting 780 ms after the local pass existed. A harness that names the thing it calls would have said so.
3. **A whole-map loop left inside the local pass** — the cliff-edge gather, 108 ms of the first 196, walking
   1.44M cells to add boxes the window filter then discarded. Found by counting the cells the pass actually
   touched, which came out at four million for a four-cell wall because the counter was cumulative and not
   per-call. Two instrument bugs to find one real one.

### Where the event stands

```
                      raster (in the tick)   next click
before                       774 ms            677 ms
after                        100 ms            677 ms
```

The tick a building completes is no longer a freeze. The click after it still is, and it is the mesh — 380 ms
of global row sweep, which §99 argued wants amortisation across ticks rather than incrementalisation, and
which is now unambiguously the next thing in this arc if it is worth continuing before groups.

## 102. The catalogue of ways an order fails, and the freeze at the end of it

From the chair: *"I gave my first command, they stood in place (it was across the map, in the fog), then I
clicked another spot in the fog relatively closer halfway across the map, they walked and reached, then I
clicked somewhere around the initial spot again, and the game froze."* And the ask with it: before fixing
anything, find out what kinds of failure an order to a random spot can meet, because this is behaviour
territory now and best-effort may read better than accurate.

`--orderprobe` replays that sequence on the real village and then walks a set of deliberately awkward targets.
It reports, per order: how many bodies went onto the shared field, how many onto their own route, how many were
**refused** one — and when refused, which of six refusals it was — then samples progress every few seconds,
because "accepted an order" and "went somewhere" are different claims.

### What it found

```
target                          outcomes                        order tick   expansions   motion
far, across the map             20 field, 0 route, 0 refused        164 ms            0   closes steadily
halfway back                    20 field, 0 route, 0 refused        129 ms            8   MOVES, DOES NOT CLOSE
far again                       20 field, 0 route, 0 refused        106 ms           20   closes steadily
deep inside a wood               0 field, 0 route, 20 REFUSED    18,137 ms    5,543,377   stands still
where they already stand        20 field, 0 route, 0 refused        119 ms       17,723   drifts
off the map entirely            20 field, 0 route, 0 refused        125 ms            1   walks at the clamp
```

**The freeze and the stand-still are the same fault.** An order to unreachable ground — and "somewhere in the
fog" is usually unreachable, because unexplored correlates with dense woodland — goes: transit cannot price the
goal, the field-entry search finds nothing priced, each body falls back to a full-map A\*, each exhausts the
250,000-expansion budget, each is truncated back to its own start and refused. Twenty bodies, five and a half
million cells, **eighteen seconds in one tick**, and every body stands still at the end of it.

So §95's budget is doing exactly what it was written to do and is useless here: it caps a *search*, and an
order issues twenty of them. A cap per search cannot bound a cost per order.

### The second failure, unrelated and real

The halfway order moves every body — twenty of twenty in motion, fifty-one metres travelled — and closes two
metres in forty seconds: 91.5, 91.4, 91.7, 89.6 m to go. Motion without progress. Not a stall (the stall clock
reads under a second), not a refusal, not a cost problem. Something is steering them across the gradient rather
than down it, and it is invisible to every counter this arc has added because every one of them says the order
succeeded.

### What the numbers argue for, as design rather than as patch

1. **Resolve the goal once per order, not once per body.** Twenty bodies asking the same unanswerable question
   twenty times is the whole of the eighteen seconds. The cohort already shares a field; a refusal should be
   answered once, for the group, and the answer shared.
2. **Best effort beats refusal, and the chair predicted this.** A body told to walk somewhere it cannot reach
   should walk as near as it can get. That is what a person would do, it is what the fog makes ordinary — you
   cannot know the wood is impenetrable until you have been there — and it turns the worst case from "nothing
   happens for eighteen seconds" into "they set off". The nearest reachable point is available cheaply: the
   field already knows which cells it can price.
3. **A budget per order, not per search.** Whatever bound exists must be spent across the cohort, so twenty
   refusals cost what one does.
4. **And say so.** A cohort that stops short because the target was unreachable is doing the right thing and
   looks like a bug. Whatever the interface eventually is, the simulation should be able to report "as close as
   we could get" rather than leaving the player to infer it.

None of that is implemented here. The instrument is, and it now names all six refusals and samples progress —
which is what the next design conversation should be argued from.

## 103. Best effort, resolved once, budgeted per order, and said out loud

§102 catalogued how an order fails and argued for four changes. All four are in, in the order they were asked
for, and they turn out to be one change wearing four hats — which is the first evidence that "group" belongs
here rather than after.

### The four

**1. Best effort instead of refusal.** A target the cohort cannot reach resolves to the nearest ground it can,
and the bodies set off. This is not only cheaper, it is more truthful: with fog, a wood is not known to be
impassable until somebody has stood at its edge, so walking to the treeline and stopping is what actually
happened rather than a failure to obey.

**2. Resolved once per order, not once per body.** The mesh already knew reachability and never said so — two
rectangles joined by a crossing are walkable one to the other, so the connected components of the crossing
graph are the islands of ground a body can move within. `WalkableRectangles.ComponentOf` is a union-find built
with the mesh and shared by every field on it: about a millisecond, to replace a question twenty bodies were
each answering with a quarter of a million cell expansions.

**3. A budget per order rather than per search.** §95's cap stopped each search politely at 250,000 and the
tick still took eighteen seconds, because twenty ran. There is now a pooled allowance opened per command; when
it is gone, the remaining bodies get the cohort's field or stay where they are, which is the same best-effort
answer and costs nothing.

**4. Said out loud.** `LastOrderWasBestEffort`, `LastOrderShortfall` and `LastOrderFoundNothing` — because a
cohort that stops short for a good reason is behaving correctly and reads as broken. The simulation knows which
of the three happened and now reports it instead of leaving the player to infer it from bodies standing in a
field.

### Measured, on the case that froze

```
                            before                          after
order tick               18,137 ms                        99.5 ms
cells expanded            5,543,377                            23
outcomes           0 field, 20 REFUSED         20 field, 0 refused
resolution                        —    best effort, 8.7 m short
motion                   stood still     19.3 m in 20 s, closing
```

And the whole catalogue now resolves honestly: far targets taken as asked, unreachable ones moved 6.2 and 8.7
metres to reachable ground, off-map clamped, and nobody refused anywhere.

### Two things this got wrong first

**The cohort's centroid is not a place.** Reachability was first asked from the average of twenty positions,
and an average lands wherever it lands — inside a tree, in a river, in the wall of a barn — where the
decomposition has no rectangle and the question cannot be asked at all. The probe caught it immediately by
reporting a target unreachable that had been reached twice in the same run. It asks from the member nearest the
target now: a body is standing where it stands, so its ground is walkable by construction, and the one nearest
the target is the one whose island the answer is about. "Where the group is" turns out to need a definition,
which is a groups question arriving early.

**A test encoded the behaviour being replaced.** "Impassable slopes reject a route" asserted that a body
ordered across a cliff does not move. That was the old intent, and best effort contradicts it deliberately. The
invariant worth keeping is not "an impossible order produces no movement" but "a body never ends up on the far
side", so the test now asserts both halves — it must set off, and it must not cross. That is stronger than what
it replaced, which could have passed with a pathfinder that refused every route in the game.

### What is still open, and where groups start

The halfway order still moves twenty bodies fifty-one metres and closes two (§102). Nothing here touched it:
the order resolves as asked, everyone gets the field, everyone walks, and the cohort does not approach. That is
a steering or a formation question rather than a routing one, and it is the first item of the groups arc rather
than the last of this one.

## 104. The crash two of my own changes made between them

Reported by the run aborting: exit 134, `InstancedBatch.SetInstances given 16714 instances; max is 16384`, in
`PropModel.StageCascades`, at a 450 m standoff with cheap trees and fog off — 28,069 trees on screen.

Two changes from this session, each sound alone:

- **§91's cheap-tree swap** collapsed forty models (ten species at four levels of detail) into six, so every
  copy of a species now funnels into one instance list instead of four.
- **§86's zoom unpinning** made 450 m reachable, where a wooded village offers thirty thousand trees at once.

Neither is wrong and the combination overflows a buffer. Worth recording as the shape rather than the
incident: a change that concentrates work and a change that increases it are not independent, and the
arithmetic that makes each fine separately is not the arithmetic that matters.

### Why not raise the limit

`InstanceBuffer.MaxInstances` sizes the storage block — `16384 x 80` bytes — and a frame holds dozens of
buffers, several per model per cascade. Doubling the constant to serve one model would cost hundreds of
megabytes across all of them. The limit is not the problem; a primitive that cannot draw what it was handed is.

So `PropModel` chunks. Each mesh keeps a list of buffer-and-batch pairs, one per buffer's worth of copies,
added on demand and kept — a frame that needed six chunks once will need them again, and growing on the way up
beats allocating every frame. `Stage` slices the instance list across the chunks it needs and `Draw` ends
exactly the chunks `Stage` began. A buffer per chunk rather than per mesh, for the same reason the per-cascade
split needed a buffer per cascade: an instance buffer writes one current-frame slot, so two batches sharing one
would both draw whatever was uploaded last.

Both halves are chunked, scene and caster. The crash came from the caster path, and the scene path concentrates
identically — 31,172 instances staged in the reproduction — so fixing one and waiting for the other to abort
would have been a choice.

```
                          before          after
450 m, cheap trees, fog off   abort 134    22.2 ms, 30,298 trees, 31,172 instances
450 m, cheap trees, fog on    (untested)   16.7 ms, 60 fps
```

Gate 3/3 green, 389/389 graphics tests. Reproduction, for whenever this needs checking again:

```
RTSGame --village --cheap-trees --shadow-proxy --nofog --perf-run --frames 250 \
        --zoom 450 --zoom-limit 450 --perf-camera still --perf-hour 12
```

That run stages more copies than the one that aborted, which is the property worth keeping: the case is now
covered by something that runs in twenty seconds without a window.

## 105. The straggler is a villager going back to work, and one of my bounds was eating the game

§102's second failure — "the halfway order moves twenty bodies fifty-one metres and closes two" — was the first
item of the groups arc. It turned out to be three separate things, one of which was not a failure and one of
which was mine.

### The metric was wrong

Straight-line distance cannot see a detour. `--orderprobe` now reports the field's own cost-to-goal beside it,
and on the same order:

```
t+10s |  91.5 m straight | 176.1 s by route
t+20s |  91.4 m straight | 166.9 s by route
t+30s |  91.7 m straight | 154.5 s by route
t+40s |  89.6 m straight | 142.9 s by route
```

They were approaching the whole time, around something, on a route two and a half times the straight line. Run
for four minutes instead of forty seconds and they arrive: 3.1 seconds of route left. **There was no
motion-without-progress bug; there was a measurement that could not tell walking around a wood from walking on
the spot.** Recorded as a caution rather than a fix: every progress figure in this arc that used straight-line
distance was capable of saying that.

### The straggler is real, and it is the jobs layer

Reported from the chair: *"sometimes individuals path away correctly while some of the group just stalls midway
never catching the lead."* Quantified per body rather than per centroid, and split by cause, because "stopped
short" is two different faults — a body that has given up its destination has decided, and a body still holding
one is wedged:

```
order            arrived  travelling  stopped short   gave up   wedged   left group   hold a job
far                    1          19              0         0        0            0            0
halfway back           7           0             13        13        0           13           13
far again              8          12              0         0        0            0            0
deep in a wood         0          13              7         7        0            4            7
```

**Every stopped body gave up its destination; none is wedged; every one holds a standing job.** The mechanism is
documented behaviour: `JobSystem.Interrupt` parks an assignment when an order arrives, and `ServeInterrupt`
counts down `OrderGraceSeconds` — two seconds — from the moment the body is standing free, then walks it back to
its workplace through `BeginSoloMove`, which detaches it from the move group on the way out.

So near the settlement, where the workplaces are, a cohort disintegrates within seconds of arriving: the first
bodies to reach their slots are reclaimed while the rest are still walking, and from the chair that is
indistinguishable from half the group abandoning the order. At the far targets it never happens, because nobody
stands free long enough for the grace to expire.

**This is behaviour working as specified, and the specification is the thing to argue with.** Two seconds is
very short for "I told you to go there". The knob is `JobDefaults.OrderGraceSeconds`, and the constraint on any
change is a self-test that already exists and should keep existing: *an order never becomes a mode*. So the
options are about how long an order outranks a standing job, not about whether it does forever.

### And a bound of mine was eating every search in the game

§103 opened the per-order allowance per command and never closed it. Once one order spent the pool, **every
later search was refused** — job walks, congestion repaths, stuck recovery, all of it. The probe found **461
denied searches in an order that issues twenty**, and the denials produced exactly the symptom being
investigated: bodies losing their destinations, the jobs layer reclaiming them two seconds later, and
stragglers hundreds of metres from where they were sent.

It is now opened per command and closed when command processing ends, so nothing outside an order is charged.
The freeze it exists to prevent was twenty searches in one tick, which is what it still bounds. Denials: 461 to
zero.

Worth sitting with: the bound was added to fix a freeze, it was measured as fixing that freeze, the gate stayed
green, and it silently broke movement everywhere else for the rest of the run. Nothing in this arc's
instrumentation caught it — not the frame timings, not the routing counters, not the self-tests. What caught it
was counting *per body* what an order did, which is the one thing none of the previous instruments did.

## 106. An order holds until overridden

Decided from the chair: no automatic return to work. Reach the target, then idle — posted — unless something
overrides it.

`ServeInterrupt` no longer counts an order's grace down. The assignment is parked, not cancelled; being handed
work again resumes it, and clearing the assignment stops it for good.

Measured on the probe, 240 seconds an order:

```
                    before                                   after
halfway back   7 arrived, 13 gave up and left      20 arrived, 0 gave up
far again      8 arrived, 12 travelling            20 arrived, 0 gave up
```

Every body now arrives and stays. The stragglers were never a routing fault: they were villagers whose
two-second grace expired the moment they stood still, walking back to work through `BeginSoloMove` and
detaching from the group on the way out.

### The two tests that encoded the old rule

Both asserted the automatic return, so both had their intent rewritten rather than their expectations relaxed —
the same move as §103's impassable slope.

- *"a standing assignment survives an interrupt and resumes it"* is now **parked by an order and given back on
  request**: obey, keep the assignment and the leg count, then stay at the ordered spot through thirty seconds
  where the old grace would have expired fifteen times, and resume only when the work is handed back.
- *"an order never becomes a mode"* is now **holds until overridden and is never a trap**. What "not a mode"
  means changed; the invariant did not. A unit that waits where you put it is not a mode. A unit you cannot get
  out of would be — so the test still requires that a run of orders leaves one interrupt rather than a pile of
  state, that handing work back always takes, and that clearing the assignment stops the work for good.

Gate 3/3 green.

## 107. The cohort owns its roster, and every departure names a reason

The groups arc opens where §82 said it would have to: with the four representations kept apart. Three
questions were settled from the chair before any code, and the first is the one this section is about.

- **Player-authored groups need not enter the simulation.** A control group can be a list of ids in the view
  layer — no sim state, no save entry, no fingerprint cost. `AgentStore` tombstones rather than compacts and
  never reuses an id, precisely so a stale reference resolves to a dead body and is refused rather than
  resolving to somebody else, which is exactly the property a view-layer group needs. Recorded as a decision;
  not built here.
- **An order holds until overridden, and nothing reclaims a member implicitly except an interrupt** (§106).
- **Who owns membership** — worked through below, because the answer was not obvious from either side.

### It was already stored twice, and the two copies meant different things

`MoveGroup.Members` was an array fixed at `Create` and never edited: the roster at the moment of the order.
`AgentState.MoveGroupId` was the live truth. Every read of the roster — three loops in
`UpdateGroupFormations`, plus the station-keeping lookup — was filtered by the field to reconcile them.

So the question was never "which one", it was "which becomes the authority, and what does the other become".
And the reason the answer matters is what the old arrangement could not express: **a group could lose a member
without anything happening.** The filter matched one fewer body, and a departure was indistinguishable from a
body that had never joined. That is the exact shape of §105 — the jobs layer walked half a cohort back to work
two seconds after it arrived, `liveMembers` quietly came out lower, and nothing in the arc's instrumentation
said a word. It took a chair report and an instrument written afterwards, per body, on purpose.

Body-owned formalises today: despawn is free, save is free, the hot path stays a dictionary lookup — and the
roster still cannot be dropped, because "who is in group G" would otherwise be a scan of every body in the
world. Two copies either way, then, and no event.

**Cohort-owned it is.** The roster is the authority and it shrinks; `MoveGroupId` is demoted from truth to a
back-pointer cache, written in exactly two places. Leaving becomes a call that takes a reason.

### The six exits, and the one nobody asks for

`DetachFromMoveGroup` had six call sites and no idea why it was being called. They sort cleanly:

```
  a newer move order                       superseded    the player
  stop / follow / chase / flee / patrol    overridden    the player
  the cohort settled and retired           arrived       the order, finished
  the body left the world                  died          nobody
  the jobs layer walking it back to work   INTERRUPTED   nobody asked
```

The last row is the payoff of §106. Since an order now outranks a standing job until something overrides it,
`BeginSoloMove` from the jobs layer is the **only** way a body leaves a cohort without the player having said
anything — one call site to watch and one column to read. It is not dead, either: `JobSystem` is locked out of
a body for as long as it is under orders, so the window in which it can take a member is the one where that
member has reached its slot and the cohort has not retired because somebody else is still walking. That window
is §105's mechanism stated exactly.

Two supporting moves came with it. The dead are swept off rosters in `UpdateGroupFormations` — `AgentStore.Despawn`
neutralises a slot and has no way to reach the dictionary, so the sweep goes in the one method that already
walks every group. And the cohort loops now walk the roster directly instead of filtering it by the
back-pointer: a skip would go on hiding a stale field the way the old filter did, so the invariant is asserted
in a test rather than defended in a loop.

### Measured

New assertion, *a cohort owns its roster, and every departure names a reason*. Eight bodies that arrive at
once and one that cannot, so the reclaim window is built rather than hoped for:

```
roster/back-pointer disagreements=0 | reclaimed by the jobs layer=8 overridden=1
ledger superseded=0 overridden=1 interrupted=8 arrived=0 died=0 = 9 of 9 | still on a roster=0
```

`--orderprobe` gained the same ledger per order. On the 450 m village, seven orders, twenty bodies:

```
    left the cohort: 20 superseded, 0 overridden, 0 arrived, 0 died, 0 INTERRUPTED by the jobs layer
```

Every order books twenty superseded — each body leaving the previous cohort for this one — and **interrupted
is zero on every leg**, which is §106's rule confirmed on the real map by an instrument that did not exist
when it was written. Compare §105's table for the same probe, where the halfway order lost thirteen bodies to
this exact path and the probe could only report that thirteen "hold a standing job" and leave the join to a
reader.

Full self-test green. The save format goes to version 7 for the departure ledger, which is carried and
fingerprinted on the same argument as the solver's counters: two runs that lost members a different number of
times, or for a different set of reasons, have disagreed about a decision well before the positions show it.

### The seam that is named but not cut

The retire rule — everybody settled for thirty ticks, release and dissolve — is a **locomotion** lifetime. It
is still the right thing to do today, because the cohort has never been anything but the move. What changes
next is that ending the move stops meaning ending the set: `MoveGroup` is simultaneously the identity, the
formation geometry and the shared-transit state, which is precisely the collapse §82 warned against, and it is
owned by locomotion. Named in the code where it happens so the split is visible before anything is built on it.

Also left standing, deliberately: a cohort still cannot take a new member. `Remove` takes the slot with the
member, because slots are laid out once against the ground and the approach for the bodies that were there at
the time, and handing a departed member's slot to somebody else is a question for the pass that lets a cohort
grow.

## 108. Crews, on the digits the key enum did not have

§82's second complaint about groups is affordance: *the player needs to create, recall, inspect and command a
set without repeatedly reconstructing it by marquee selection*. This is that, and it is deliberately the whole
of it — the third representation, built as its own thing rather than as a use of either of the other two.

### It stays out of the simulation

Decided in §107 and acted on here. `ControlGroups` lives in `Control/`, holds ten lists of ids, and the world
has never heard of it: nothing is ticked, fingerprinted or saved. What makes that safe is a property
`AgentStore` already had on purpose — it tombstones rather than compacts and **never reuses an id**, so a
stale reference resolves to a dead body and is refused rather than resolving to somebody else. A list of ids
held outside the simulation needs exactly that and nothing more.

The two costs are real and are named rather than discovered later: **a crew does not survive a save**, and
nothing computed at group resolution can be shared through one. Both become live questions the moment
something wants either, and neither is a reason to put it in the world today.

### The keys did not exist

`Blix.Core.Key` had letters, arrows, Escape, Space, Tab, Backspace, Control and Super — and no digits at all,
so "bind this to 4" was not expressible anywhere in the engine. Added to the enum and to the Silk mapping,
appended rather than slotted in beside the letters so nothing renumbers. Number row and keypad map to the same
value, because which physical key produced a digit is a fact about the keyboard that no caller has ever
wanted. Shift went in at the same time; the note in `OnKeyDown` that "Shift is not in the `Key` enum at all"
is now stale, and has been corrected where it stands.

Three verbs on one key, matching thirty years of every other RTS exactly, because the affordance being tested
is muscle memory rather than novelty:

```
  Ctrl + digit    set the crew to the selection
  Shift + digit   add the selection to it
  digit           recall it
  digit again     jump the camera to it
```

The last line is arrived at without a double-tap timer. "The selection already equals this crew" is precisely
the state a second press produces, so it is asked directly rather than timed — one less piece of state, and it
also does the right thing when the set was reached some other way.

### The separation, asserted rather than assumed

New assertion, *a crew survives the orders given to it and forgets its dead*. It does to a crew the three
things that end the other two representations — an order, more orders, and losing members:

```
crew of 6: 6 through an order (cohorts=1, same members in one=True), 6 through two more and a stop
(cohorts=0), 4 after two died, 7 extended, 3 replaced
```

The middle clause is the one that matters. While the crew is carrying out its order there is a cohort holding
the same six bodies under a different identity; two orders and a stop later the cohort is gone and the crew is
not. **Collapsing the two would pass every other test in this file**, which is why the separation gets an
assertion of its own rather than a comment.

Gate 3/3 green.

### What one press from the chair still has to confirm

Whether digit and Shift presses actually arrive is not answerable by reading — the enum can be right, the
mapping right, the dispatch unfiltered, and the combination still swallowed by the window library or the OS.
That was true of Ctrl, and it is the reason the map-roll key was put on bare Space rather than on a
combination: an action somebody presses fifty times in a row should not rest on delivery nobody has tested. `--debug-all` prints one line per press, so a bare `1` reading `key Number1` settles the digits and
`key Unknown` would settle them the other way. Shift+digit is the one binding here that would be worth
retiring for a different chord if it does not survive that test; Ctrl+digit and the bare digit are the two
that carry the affordance.

## 109. A cohort outlives its move

Three rulings from the chair, and they compose into one design rather than three:

- **Adopt.** An order given to a set that is already a cohort takes that cohort, rather than building a new one.
- **Internal bookkeeping only.** A cohort is never a thing the player selects, sees outlined, or commands as
  an object. It shows up as movement being better and in no other way.
- **The player holds one cohort at a time.** Any ordered set resolves to exactly one cohort — old or new,
  never two.

### The seam §107 named, cut

Retiring on "everybody has settled for thirty ticks" was a **locomotion** lifetime wearing the cohort's
clothes. It is also precisely why adoption was not expressible: the set died the moment the walk did, so by
the time the next order arrived there was no group left to give it to.

So the arrival branch no longer releases anybody. The cohort sets `AtRest`, does its arrival bookkeeping once
on the way into that state, and keeps its people. Retirement moved to the only condition that actually ends a
set — **an empty roster** — asked immediately after the dead sweep so that it is asked of a resting cohort
too. A set whose last member the jobs layer took back is over whether or not it was walking at the time.

A resting cohort then costs one branch a tick: no transit average to take, no slot to peel off to, nothing to
rediscover by walking its members. The dead sweep still runs, because the roster has to stay honest whether or
not anybody is moving.

### Adoption is exact, and that is where the third ruling lives

An order adopts a cohort **iff the ordered set is exactly that cohort's roster**. Both sides are in id order —
the roster because it is built that way and removal preserves it, the command because every path sorts before
it queues — so the test is a walk.

Exactly, not overlapping, and the reason is that ordering six of a cohort's ten is a genuinely different
intention. There is no reading under which the four left behind should be dragged along, or under which the
six should inherit a formation laid out for ten. So a partial order forms its own cohort and the remainder
keeps the old one — which is also where "the player holds one cohort at a time" lives in code, as a property
rather than as a rule anybody has to enforce: any ordered set resolves to one cohort, never two.

What adoption preserves is the identity, the roster, and — deliberately — the transit centroid and flow
agreement. A cohort already moving together and turned toward somewhere else should carry its shape through
the turn. Clearing it would make every re-order start from "we have not agreed on a direction yet", which is
the one state station-keeping is written to stay out of. Only the slots are re-laid, because only the target
changed.

### There is no longer an "arrived" departure

Deleted rather than left to read zero. Reaching the target used to release every member and retire the group;
it now enters a state that nobody leaves by. A column that can never be anything but zero is worse than no
column, because it invites the reader to conclude that arrivals are being counted somewhere.

### Measured

New assertion, *a cohort outlives its move and is adopted by the next order*. Four claims, asserted apart
because they fail apart:

```
rested with its people=True | adopted by the next order=True (id 1, ledger unmoved=True)
half ordered away split it in two=True (one cohort each=True) | ended only when empty=True
```

`--orderprobe` on the 450 m village, seven orders to the same twenty bodies. Before this section every leg
after the first read `20 superseded`; now:

```
  far, across the map        cohort: new      | left it: 0 superseded, 0 overridden, 0 died, 0 INTERRUPTED
  halfway back               cohort: adopted  | left it: 0 superseded, 0 overridden, 0 died, 0 INTERRUPTED
  far again                  cohort: adopted  | ...
  deep inside impassable ground, deep inside a wood, where they already stand, off the map entirely
                             cohort: adopted  | left it: 0, 0, 0, 0
```

**One cohort formed and never rebuilt across seven orders, and a departure ledger that is zero in every column
for the whole run.** The twenty superseded departures an order were the group being destroyed and recreated
each time; there was nothing wrong with them except that they were describing work nobody needed done.

Gate 3/3 green. Save format goes to version 8 — a reason left the ledger and `AtRest` joined the cohort.

### One asymmetry found on the way

Skipping `LeaveCohort` on the adopted path meant skipping `ClearCohortFields`, and `SeekingFieldEntry` — a
fact about the route a body was on for the *last* order — survived into the new one. Downstream guards happen
to catch it today, which is not a reason to leave a stale per-order flag lying about. `JoinCohort` now clears
it, which makes the join symmetric with the leave and removes the need to reason about the guards at all.

## 110. A cohort is never grown, merged or reinforced

Decided from the chair, and it is a ruling rather than a feature. §107 shelved "a cohort still cannot take a
new member" as a question for a later pass. There is no later pass: **reinforcement is not a thing cohorts
do.** A set that has changed is a new cohort, not a grown one.

The whole rule is one word from §109's adoption test — *exactly*:

```
  the ordered set is exactly a roster    adopt that cohort
  anything else                          a new cohort
```

The case for keeping it that plain is that every alternative needs an answer to a question that does not have
a good one. A slot laid out for six, against particular ground and a particular approach, is not a vacancy a
seventh body can be given. Two cohorts merged have two travel states — two transit centroids, two flow
agreements — and no principled way to pick one, so the merge would either throw both away or silently prefer
whichever was found first.

### Three of the four corners were right by accident

Worth saying plainly, because it is the reason this section exists at all rather than being a comment. Only
the exact match was built deliberately, in §109. Subset, superset and reaching across two cohorts all fell out
of "no match means create one" — nobody wrote them, and nothing asserted them. Behaviour that is right by
accident is one refactor away from being wrong, and these corners are cheap to pin down.

New assertion, *a cohort is never grown, merged or reinforced*:

```
superset is a new cohort=True subset=True across two=True | exactly the roster still adopts=True
| one cohort each=True, no empty husks=True, 3 cohort(s) alive
```

The superset is the corner that matters most, because it is the obvious reinforcement gesture: the same six
plus two more. It must not extend the six, and it does not — it is a different set, so it is a different
cohort, and the six leave the old one which empties and retires. The last clause asserts the rule is a rule
and not a ban: exactly the roster still adopts.

### One observed consequence, left alone

A cohort can shrink to a single member and stay alive at rest — three cohorts alive at the end of that test,
two of them holding one body each. `Create` refuses to build a cohort of one, but nothing stops one from
becoming one, and a cohort of one is not a cohort in any useful sense.

Left as it is on purpose. It costs a branch a tick, station-keeping already needs more than one member to do
anything, and it dissolves the moment that body is ordered anywhere. Retiring it would mean inventing a
departure with no reason behind it — the body did not leave, was not overridden, and nothing interrupted it —
which is exactly the kind of unnamed exit §107 was written to abolish.

### And crews

Recorded so it stops being raised: a crew lasting as long as the session is what a crew *is*, not a debt. A
set the player can put back with one marquee and one keypress is a convenience, not state. The doc comment on
`ControlGroups` said "the costs are real" and now says what is actually true.

Gate 3/3 green.

## 111. The geometry the cohort was, and now carries

The last of §82's four collapses. `MoveGroup` was three things at once — the identity and roster of a set, the
lifetime and travel state of that set, and the block-with-frontage geometry that decides where each member
stands when it arrives. §107 and §109 sorted out the first two. This separates the third.

`SlotPlan` holds the target, one slot per member, the arrival envelope, and the layout itself: the block built
in the approach frame, wider than deep, with concentric rings as the fallback for ground the block cannot use,
and the rank-for-rank pairing that stops a cohort threading through itself. Moved verbatim — this is an
extraction and not a rewrite, and none of the geometry changed.

The cohort now carries a plan rather than being one:

```
  MoveGroup    Id, roster, AtRest, SettlingTicks, TransitCentroid, TransitFlow, HasTransitCentroid
  SlotPlan     Target, Slots, FormationRadius, and the layout that produces them
```

`Retarget` becomes a plan swap. That is the shape the split was looking for, because **the target is the only
thing a new order actually changes**: identity, roster and travel state all survive it, and after §109 they
have to, since adoption is worth having precisely because a cohort turned toward somewhere else keeps what it
had worked out about travelling together.

### Why this one was worth doing even though nothing was broken

Nothing was broken. The reason is the sentence §82 wrote about it: *a combat formation, which adds spatial
roles and can wait until the combat arc needs it*. A combat formation is roles, a facing and ranks — who is in
front, who is on the flank, which way the whole body points. It is a **consumer** of this geometry, or a
replacement for it, and it should be able to be either without touching the class that holds identity,
membership and travel state.

Which is also why the type is not called `Formation`. What is here answers a much smaller question than a
formation does — twenty people were sent to one square metre, so where does each of them stand — and taking
the word now would leave the real thing without one. `FormationRadius` kept its name: it is the envelope at
which a member stops following the shared route and goes to claim its ground, that is what it has always meant
in this codebase, and renaming a well-understood term to make room for a type that does not exist yet is churn.

### Measured

Nothing to measure: no behaviour changed and none was meant to. What stands in for a measurement is the
determinism census, which is why it exists. `SlotPlan` joins the censused types, its three fields join the
ledger with the note that they are read through the cohort's own accessors, and the run confirms it:

```
PASS  every field of the world is fingerprinted or argued away
PASS  a saved world has the same future
```

Save format goes to version 9 — the plan owns its own bytes now, so the group's record changed shape.

Gate 3/3 green.

### Where the four representations stand

```
  1  transient selection   SelectionController          unchanged all arc
  2  command cohort        MoveGroup                    §107 roster, §109 lifetime, §110 never grown
  3  persistent crew       ControlGroups                §108, view-layer by ruling
  4  combat formation      — (SlotPlan is its geometry) unbuilt, and correctly so
```

The fourth is not built and should not be. §82 said formation geometry, facing and combat ranks are a later
consumer rather than a prerequisite, and the combat arc is where the questions it would answer actually get
asked. What this section buys is that when that arc opens, the thing it extends is one class with one job.

## 112. The calendar is a knob, and a day I invented the length of

This section is the one that did not land what it set out to. It set out to retime the calendar so a day is
something a player can inhabit; it retimed it, measured the retiming, put it back, and kept the structure it
had built on the way. What it actually produced is the ability to ask the question, which is what it turned
out to be missing.

### The complaint, and the number I made up

`WorldCalendar.DaySeconds` was twenty simulated seconds — thirteen wall seconds — so a whole solar day passed
while the player was carrying out one ordinary command. §82.2's brief: give each lighting state enough wall
time to be experienced, and stop cycling noon-to-night inside one command.

From that prose I asserted that an inhabitable day is **about three wall minutes**, and then built a menu of
options on it. The menu was chosen from. **Nothing measured that figure.** It was the load-bearing constant of
the whole design pass and I invented it — in an arc about clocks, having written §105's caution that a
measurement which cannot tell walking round a wood from walking on the spot will happily report either.

Measured afterwards, using the renderer's own spherical trigonometry (which reproduces the 7.9 / 16.1 midwinter
and midsummer daylight hours the file already documents, so it is the model the game actually runs):

```
share of a cycle       midwinter   equinox   midsummer
night                      55.2%     39.5%       16.5%
twilight                    9.9%      8.8%       14.0%
the horizon                 9.3%      6.9%        8.5%
sunlit                     25.6%     44.8%       61.0%

wall seconds per state       winter night   twilight   horizon
  13 s day  (today)                    7s         1s        1s
  45 s day                            25s         4s        3s
  90 s day                            50s         8s        6s
 180 s day  (my invention)            99s        16s       12s
```

**The defect is not that a day is thirteen seconds. It is that dawn is one second long.** And an ordinary
command, from the villager's 1.79 m/s at 1.5x compression, is 11 wall seconds for a 30 m walk and 45 for
120 m — so the brief wants a day north of about 45 s, not 180.

Which demolishes the conclusion the menu rested on. At 270 days a 45 s day is a 3.4 hour year and a 90 s day
is 6.75; the 13.5 hour figure that made "a believable day count" look impossible came entirely from my number.
The chair's own argument — that sixty days does not read as a year, so a day whose count means nothing should
perhaps not exist — was reasoning correctly from a premise I had fabricated.

### What the retiming found on the way, which was worth the trip

Tripling the year and running the suite turned up four separate constants that had captured a consequence of
the calendar rather than a decision:

- **The foraging reaches tripled.** `CutterWalkShare` was a tenth *of the year*, and both reaches derive from
  it, so a cutter's arm went 29 m to 87 m and a quarrier's 60 m to 180 m. That silently deletes §70's receding
  wood line and the stone mechanic whose own comment says *the nearest rock is 52-86 m depending on the map,
  so some maps have stone in reach of the granary and some do not*. **No test failed.** Walking is physical —
  a fixed number of trips over a map that did not change — so the budget is now `WalkSecondsPerYear` and it is
  the share that moves when the calendar does.
- **The ration collapsed.** `GrainPerVillagerPerYear = DaysPerYear` reads as the identity "a day is a ration"
  and is a coincidence: two hundred and seventy is both how many days read as a year and how much a person
  eats against a farm's seven hundred. Welded, the calendar cannot be made more legible without rebalancing
  the settlement.
- **The crop windows and construction costs became errands.** Both were *documented* as shares of their season
  and *written* as the seconds those came to. Now written as the shares — and the share is the field with the
  seconds derived, because "three quarters of a spring" is the design statement and "900 seconds" is that
  statement evaluated at one setting.
- **Nine tests failed on hardcoded durations**, which are the same fault one layer out.

### The factorisation, which is the actual deliverable

Decided from the chair, and it corrected two of my instincts. The goal is not maximum independence — it is
welding what is genuinely one thing, so the few real knobs become visible instead of being spread across
places where they can be papered over. And everything is a knob; they differ in who turns them.

```
design knobs      days per year, season shares, latitude, body speed,
(set in code)     the walking budget, the per-year economic anchors

session knobs     YearSeconds, compression, map extent
(surfaced)

derived           day length, season lengths, the solar cycle, every per-second
(never authored)  rate, crop windows, construction costs, foraging reaches,
                  how long a sunrise lasts
```

Two welds, both of which I had wanted to break and should not have:

- **The solar cycle is the calendar day.** A day is one sunrise to the next. `Atmosphere.DayLengthSeconds` was
  a settable slider whose own comment offered *a longer, cosier day at the cost of the calendar agreeing with
  the sky* — an invitation to fix the symptom by lying, and the reason the trade never had to be faced. It is
  a property now.
- **Days per year is structure.** The derivation ran `DaysPerYear = Year / Day`, so the day was primary and
  the count fell out — which is how it drifted to sixty with nothing objecting. It runs the other way now.

What the welds force into the open: with the count fixed, **how long a sunrise lasts and how long a year lasts
are one decision.** There is no dial left that relieves it, which is the point.

### The guard

`the calendar is a knob and nothing wrote its answers down` turns the year to 0.5x, 3x and 10x, and the day
count to 270, 365 and 60, and asserts three different kinds of thing at every setting:

```
1.50 sim-h x270 ok (day 20.0 s, reach 29/60 m) | 0.75 sim-h x270 ok (day 10.0 s, reach 29/60 m)
4.50 sim-h x270 ok (day 60.0 s, reach 29/60 m) | 15.00 sim-h x270 ok (day 200.0 s, reach 29/60 m)
1.50 sim-h x365 ok (day 14.8 s, reach 29/60 m) | 4.50 sim-h x60 ok (day 270.0 s, reach 29/60 m)
```

- **Physical things must not move at all** — the reaches hold at 29 and 60 m across a twenty-fold range of
  year lengths, which is the exact invariant that broke silently.
- **Derived things must move exactly** — the day divides the year, the seasons are shares of it, the
  boundaries land where the shares say.
- **Balanced things must stay in proportion** — a year still draws 270 grain and 120 wood and still yields a
  cutter 500, whatever it costs in seconds.

Every fault above lives in one of those three rows. Not one of them was caught by a test; they were caught by
reading, one at a time, which is a method that works right up until the session somebody is in a hurry.

Writing the guard immediately caught a fifth: the crop constants were *fields* initialised from the season, so
they snapshot the calendar at class-init and would never have followed it. The normalisation would have read
correctly in the source and been wrong at runtime.

### Where this leaves the numbers

Unchanged. §3's year, §3's seasons, 270 days, a thirteen-second day and a one-second dawn. The complaint that
opened the section is still true and is now a decision that can be made rather than guessed at: pick a year,
and the day, the seasons, the rates and the length of a sunrise follow, in proportions a test defends.

Gate 3/3 green.

## 113. --clocks, and a criterion that cannot be met

§112 argued about numbers nobody could read off the running game. `--clocks` is the answer to that: it takes
the dials, prints every quantity in the unit it is actually experienced in, and puts the dimensionless ratios
at the top because those are what a setting is chosen on.

It samples `Atmosphere.For` rather than reimplementing the trigonometry beside it. That is the point of it
being in the game rather than in a scratch script — a report that computes its own answer is a second opinion
about one quantity, and this project's whole history is two opinions drifting. It agrees with the independent
Python that priced §112 (winter night 7.3 s against 7; a 45 s day's dawn 7 s against 4 + 3), which is what
gives either of them any standing.

```
  ratios — what a setting is chosen on
       270 days a year        reads as a year
      0.07 dawn per command   AN ORDER OUTLASTS THE SUNRISE
     16.76 days to cross      THE MAP CANNOT BE CROSSED IN A DAY
     0.048 reach per map      a cutter's arm is 4.8% of the map
```

Overrides price a proposal without editing a constant: `--clocks --year 3.4h --days 270`. Durations are
written the way a person says them and converted on the way in, because a year is chosen in wall hours and
stored in simulated seconds and that is the arithmetic that keeps going wrong — §112 got it wrong in a test
about units, and this file printed `16.76` beside "crossings a day" when the number was days per crossing.
Both are the same fault: a quantity that is right under a name that is its own reciprocal.

### What it found immediately

The report solves for the crossover rather than leaving it to be read off a table:

```
  solved
    a sunrise outlasts an 80 m order at a year of  14.23 h — 14.2x this one
    a sunrise is a tenth of one at 85.4 min, a third at 4.74 h
```

**§82.2's criterion cannot be met.** *Avoid cycling from noon through night while the player is still
carrying out one ordinary command* asks for a fourteen-hour year, because a dawn is a fixed share of a day
and an order is a fixed number of wall seconds and there is no setting where the first beats the second at a
length anybody would sit through. It is not a target the numbers missed; it is a target the geometry forbids.

Which is worth more than a number would have been. The brief was written as though the day were badly tuned,
and it is not tuned at all — it is structurally incapable of the thing being asked of it. That leaves two
honest readings and the arithmetic no longer favours ducking between them:

- a sunrise is a **transition you glimpse**, not a state you are in, in which case two seconds may be right
  and nothing needs changing; or
- the daily cycle **should not exist**, and the sun should turn with the season instead — which is the chair's
  own proposal, and now has arithmetic under it rather than taste.

Note the shape of that: the instrument's first useful act was not to tell us what the numbers should be, but
to retire a question. §112 asked "how long should a day be" for an afternoon on the strength of a figure I
invented. The answerable question is "what is a sunrise for", and it is a design question that no measurement
was ever going to settle.

Gate 3/3 green.

## 114. Two thirds of the worst event in the game was unattributed

The perf debts were picked up again because they are noticeable now that the large ones are gone. §101 left
the ledger reading: raster 100 ms, the click after a building 677 ms, of which the mesh sweep was 380 and
therefore the thing to attack. §99 had already argued how — amortise the row sweep across ticks and let the
old mesh serve until the new one lands.

Measured first, before touching any of it. The click is **not 677 ms and the mesh is not most of it.**

```
raster     ~76 ms  — terrain 0.0 | clearance 0.5 over 1,208 cells | apply ~27
order    ~1970 ms  — mesh 328 | tiles ~150 | field ~190 | 300,000 cells expanded
                     climb 252,732 calls, 2,529,839 samples
```

Three runs, within 3% of each other. `--pathprofile` timed a whole `Tick` and then named three routing
numbers that came to a third of it, so **two thirds of the worst event in the game had never been attributed**
— and the arc was preparing to spend its next section optimising the largest of the three names it happened
to print. The world has recorded a per-phase breakdown all along and nothing was asking it for one.

### The instrument first, and it was wrong in a way that announced itself

Asking produced `named 4,259.3 of 2,661.8 ms`, which is a total that refutes itself. `Pathfinding` and
`AgentIndex` are accumulators laid over the tick rather than stages of it — pathfinding adds up whatever
routing happened wherever it happened, and nearly all of it happens inside `Commands` — so summing every
counter gets more than the tick it describes. Stages are now summed and accumulators reported apart, and
`SimulationTimings.IsCrossCutting` says which is which at the definition instead of leaving each reader to
work it out.

```
  stages: Commands 1941.7 — 1941.7 of 1942.1 ms accounted
  of which, across all stages: pathfinding 1287.8 ms | agent index 0.0 ms
```

### What the attribution says, which reorders the debt

All of it is in `Commands`, and two thirds of *that* is pathfinding. So the event is not a mesh rebuild with
some overheads, it is **a mesh rebuild and the cascade it sets off**: the mesh dies, everything keyed to it
dies with it, and the next order pays 300,000 cell expansions and two and a half million height samples to
rebuild what it lost. Against a normal cold order — 132 ms, 237,100 samples — the placement order does
**twelve times the climb work**.

That changes both items on the list:

- **§99's amortisation is worth more than it looked, and less than enough.** An old mesh that keeps serving
  defers the whole cascade rather than just the 328 ms sweep. But deferring is not removing: when the new mesh
  lands the caches die anyway and the *next* order pays. Amortisation moves the hitch; it does not answer it.
- **The cascade has a cheaper answer, and it is §101's answer one layer up.** `CornerClimbCache` is keyed by
  navigation revision, and its own comment gives the reason: *the corners come from the decomposition and the
  climb comes from the heights, and a change to either bumps the revision*. True, and one revision too tight.
  A climb between two fixed points is a fact about **terrain**, and a placement change cannot move terrain —
  the same sentence that turned the raster's 774 ms into 100. The cache is thrown away because its keys are
  mesh-relative corner indices, not because its values expired.

The corners sit at half-cell positions — `crossing.MinimumX + 0.5f` and the like — so an exact
mesh-independent key exists at twice the coordinates, and the cache could be keyed to the terrain revision it
actually depends on. That is the next thing, and it needs a test that the same corner pair gets the same climb
across a placement change, because a cache that survives when it should not is a quiet divergence and this arc
has been bitten by one twice.

Gate 3/3 green.

## 115. A climb outlives a building, and the repath storm that was not there

§114's attribution said the cascade rather than the sweep was the click, and named the cheapest piece of it:
`CornerClimbCache` was keyed by the navigation revision, one revision tighter than the thing it depends on.

Two things had to move for the cache to survive a placement change:

- **The key was a pair of corner indices**, which are positions in one decomposition and mean nothing once
  the mesh is rebuilt. Corners sit at half-cell coordinates — `crossing.MinimumX + 0.5f` and the like — so
  twice the coordinate is an exact integer and the pair is an exact, mesh-independent key.
- **The radius bucket is gone.** It was in the key because *which corners exist* depends on radius; the climb
  between two points is not a property of who walks it. The coordinates carry that themselves, and a
  mixed-radius world now shares one cache as a side effect.

`NavigationGrid.TerrainRevision` is the new key: set from `TerrainMap.Revision` by the rasteriser, which is
the one place that knows, so it cannot become a second opinion about when the ground changed. A load rebuilds
the raster from restored terrain and gets the right number without anything having to restore it.

### Measured, three runs each

```
                 before        after
climb calls      252,732       27,624      9.1x fewer
climb samples  2,529,839      477,697      5.3x fewer
field             ~190 ms       ~51 ms     3.7x faster
the click        ~1970 ms     ~1762 ms     11% off
```

Worth stating plainly: **that is 210 ms of a 1,762 ms click.** The field collapsed and the event did not,
which is what attribution is for — the same fix presented without §114's breakdown would have read as a
success rather than as a tenth of one.

### The test, because a cache that survives when it should not is a divergence

*a cached climb outlives a building and not a hill* warms a cache on sculpted ground, puts a building up, and
compares every probe against a second world born with the building already in it and a cold cache. If any
surviving entry were stale the two could not agree.

```
across a building: terrain revision 231 -> 231 (navigation 2 -> 3), 24/24 probes priced identically
                   to a cold world, 0 differed | across a hill: terrain revision -> 276 (evicted)
```

Sculpted deliberately: on flat ground every climb is zero and a cache returning nonsense returns the right
nonsense. It failed on its first run and the code was right — raising a ridge moves the terrain revision once
per vertex, but the grid records which terrain it sampled only when the rasteriser next runs, so a revision
read before any rebuild is the number from before the ridge and the building then appears to have moved it.
The test now lets the raster catch up first, which is a fact about the two clocks worth having written down.

### And a repath storm that was not there

The obvious next hypothesis was that a placement change invalidates every villager's route and the settlement
repaths at once. The profile now counts it: **2 route queries for 20 bodies.** Twenty took the shared field
exactly as §96 intended; two did not.

So the remaining 1,232 ms of pathfinding is **two A\* searches**, each expanding about 150,000 cells — a tenth
of the map — at some 600 ms apiece. Not breadth, depth: two bodies fell off the shared field, took their own
route, and paid full price for a hierarchy that had just gone cold. That is roughly 60% of the whole click and
it is the next thing, against a mesh amortisation that §99 and §101 both nominated and that is worth 18% and
moves the hitch rather than removing it.

Counting it cost one line and saved a section spent budgeting repaths that were never happening.

Gate 3/3 green.

## 116. The click's two searches, attributed: a body twelve centimetres over a clearance line

§115 ended with the click after a placement change at ~1,762 ms, two thirds of it two A* searches, and a
sentence about what they were: *two bodies fell off the shared field, took their own route, and paid full
price for a hierarchy that had just gone cold*. That reading was available from a count of queries, and it
was wrong in every part that decides what to fix.

Every route request now names why it was asked for. `RouteReason` is required at the call site, on the same
reasoning as `CohortDeparture` — a request nobody knew was being issued is the failure mode — and each one is
charged its own expansions, its own milliseconds and its own ending. Threading it found **fifteen distinct
call sites**, which is itself the first result: routing is asked for in fifteen situations and until now they
all arrived at one counter. It also found a sixteenth that was dead (`AssignPathVia`, unreferenced), deleted
rather than given a reason it could never report.

```
  order    1793.6 ms | mesh 331.1 | tiles 157.8 | field 47.8 | 300,000 cells expanded
    2 route queries for 20 bodies
    routing, by who asked (2 queries)
      OrderSlot     2 queries  1254.2 ms  300,000 cells — 1 answered, 2 partial
      each request, in order
        #7  OrderSlot  Partial  routed     250,000 cells  1066.6 ms  over 503 cells of map
        #8  OrderSlot  Partial  NOTHING     50,000 cells   187.6 ms  over 513 cells of map
    the field refused: 0 off grid, 0 no reachable goal, 0 on walkable ground it could not price,
                       2 standing where the body does not fit (overhanging the cell's clearance
                       by up to 12 cm of a 37 cm body)
```

### It is not the revision bump, and that was the entire hypothesis

`NavigationChanged` — a body holding a stored polyline when the placement change invalidates it — scored
**zero**. Not one route in the settlement was invalidated by the raster. §114 had read the click as *the mesh
dies and everything keyed to it dies with it*, and for routes that is not what happens: the bodies under
orders are on the shared field, which is rebuilt rather than searched, and nobody else was holding a route at
all. The cascade §114 named is real and it is the climb cache and the tiles. It is not repaths. Two sections
had a mechanism for this event that the first attribution deleted.

### Neither search reached its goal, and one bought nothing at all

Both requests are `OrderSlot`: `ApplyMove`'s second choice, taken when `BeginFlowTransit` cannot put a body
on the shared field, which sends it to A* to its formation slot instead. Both were stopped by a budget — #7
by the per-search 250,000, #8 by the 50,000 left of the pooled 300,000 — so **the 300,000 cells §114 and §115
both reported is not what the routes needed, it is the ceiling.** The searches ran until something stopped
them.

And #8's partial route was rejected by the smoothing, so its 187 ms bought a body standing still. The honest
statement of the click's largest term is therefore not "two expensive searches" but: **1,254 ms, 70% of the
event, for one truncated route and one refusal.**

### Why the field refused them, which is the actual bug

Not a pocket the corner graph could not reach — that was the next guess and the split says no. Both bodies
stood on a cell whose **clearance is below their own radius, by up to 12 cm of 37**. A body resting a hand's
width over a clearance line, beside a wall or a tree, which from the chair is a body standing still on open
ground.

`RoutingRadius` aliases `Radius` deliberately, so this is not a conservative predicate refusing a body that
is really fine: the navigation grid genuinely says the cell does not admit it. What makes it a bug is the
**asymmetry between the two things that are asked**:

- `FindPath` handles this case. A start cell that does not admit the body is resolved outward by
  `FindRecoveryStart` within 2.25 cells, and the search proceeds from there.
- `SampleFlowGradient` does not. It reads `CostAt` at the body's own cell, gets infinity, and returns zero.

So the same body at the same position is routable to the A* and unroutable to the field, and the giving-up is
what buys the expensive path. The field refuses, the order falls back, and a 12 cm overhang costs a second and
a quarter of the worst event in the game.

### The instrument was wrong once, in this arc's usual way

The first classification tested nullness before the budget and filed #8 — budget-stopped, partial route
rejected by smoothing — as `Unresolvable`. That label points at goal resolution, and the next session would
have gone to look there. What a search did and what the body received are two axes and are now recorded as
two: every row carries `answered`, and *time bought and thrown away* is a column rather than an inference.

### And the figure is contingent, which explains why it keeps moving

At `--orders 2` the same placement change costs **500 ms and issues zero route queries**: no body happens to
be resting over a clearance line when the order lands, so the fallback never fires. This event has been
reported at 677 ms (§101), 1,970 (§114), 1,762 (§115) and 1,794 here. That spread is not drift and not
thermal — it is whether two bodies out of twenty are standing 12 cm over a line. **Any figure for this event
has to say how many orders preceded it**, and none of the four did.

### What this leaves

The next thing is not a cheaper cell search. It is to make the two questions agree: sample the field from
ground that admits the body — the resolution `FindPath` already does, or §96's `FindFieldEntry`, which costs
2 cells and 0.5 ms in the same profile — before falling back to a cross-map A*. The measurement predicts the
size: 1,254 ms of a 1,794 ms click, about 70%, against §99's mesh amortisation at 18%.

The A* itself keeps exactly one debt out of this, and it is smaller than it looked: 250,000 expansions that
fail to find a goal 503 cells away is the weak-heuristic finding §95 already recorded, and it is a ceiling on
a case that should now stop arising rather than the click's problem.

Gate 3/3 green.

## 117. The order path gets the answer the drop path has had since §96

§116 attributed the click after a placement change and found the 1,254 ms was two bodies resting a hand's
width over a clearance line: unroutable to `SampleFlowGradient`, which reads the cost at the body's own cell
and gets infinity, and perfectly routable to `FindPath`, which resolves such a start outward before searching.
The order path answered that with a cross-map A*.

The fix is `TryEnterFieldOnOrder`, and it is not new code so much as a wire that was missing. §96 built
`FindFieldEntry` for a body dropped mid-journey — *where is the nearest cell this field can serve* — and it has
been costing two cells and half a millisecond in the same profile ever since. `ApplyMove` now asks it before
falling back to a search, sets `SeekingFieldEntry`, and `RejoinFieldTransit` puts the body on the shared field
when it arrives. **The crowd gate is kept**: above `FieldEntryPressureCeiling` a body still solves its own
route, because a body that solves its own route can pick a different exit and in a pen that diversity is the
behaviour both pen-distribution self-tests assert.

### Measured, three runs each

```
                         before        after
the click             ~1793.6 ms     ~524.9 ms     3.4x faster
of which pathfinding  ~1254.2 ms       ~3.3 ms      380x less
cells expanded           300,000             6
bodies answered            1 of 2        2 of 2
```

Three runs at 523.7 / 528.3 / 522.7 ms, inside 1%. **The pathological term is gone rather than reduced**, and
there is a second reading that says so: §116 measured this same click at 500 ms under `--orders 2`, where no
body happened to be standing over a clearance line and the fallback never fired. The click now costs what it
costs when nothing goes wrong.

### The test, and the precondition it asserts rather than assumes

*an order from ground the body does not fit on joins the field* puts a body at the far edge of the cell beside
a block — that cell's centre offers 25 cm of clearance to a body needing 40.5, while a body at its far edge is
45 cm from the block and physically fine — orders it across the map with a companion, and asserts **the
reason**: exactly one `OrderFieldEntry` and no `OrderSlot`. Then it walks the body to the target, because an
entry hop that is a dead end would satisfy everything above.

Two things it does deliberately:

- **It checks where the body actually is, not where it was aimed.** The first version asserted the precondition
  about the intended position and passed while guarding nothing — the spawn nudge had walked the body out to
  ground its radius fits on, which is the right default and erases the case. `allowEmbedded` keeps it.
- **It asserts the reason and not a cost.** A threshold on expansions passes on a small map for the wrong
  reason, and the whole of §116 was that a count cannot tell two situations apart.

Verified against its own absence: with the fallback stubbed out it reports
`slotPaths=1, reasons=[OrderSlotx1], refusals=(0,0,0,1, 0.155, 0.25)` and fails.

### A figure corrected

§116 said the bodies overhung by "up to 12 cm of a 37 cm body". That is the overhang against the radius; the
predicate that actually refuses them is `IsWalkable`, which wants radius **plus `NavigationMargin`** — so the
shortfall is 15.5 cm, against a cell offering 25 cm where 40.5 is needed. The profile now prints the clearance
and the requirement rather than a difference, because a difference against the wrong side of a margin is a
number no predicate in the codebase uses.

### What the click is now

```
the click  ~525 ms   mesh 326 (62%) | tiles 142 (27%) | field 51 (10%) | A* 3.3 (0.6%)
```

So §99's mesh amortisation is now the largest term — which is what §99 and §101 both nominated, and it is
finally the right answer for the right reason rather than the largest number that happened to be printed. The
raster's ~70 ms sits beside it, of which ~25 is a whole-map apply that could be windowed. The weak heuristic
(§95) keeps no debt here at all: it is a ceiling on a case the order path no longer creates.

Gate 3/3 green, including the two pen-distribution tests that guard the crowd gate.

## 118. The config was worth more than anything left in the click, and the split moved under it

Every figure in §114–117 is Debug, which §83 already warned about — *judge feel in Release and label every
figure with its config* — and the click is the one event where that warning had not been applied. Measured
ABBA-interleaved, three samples each, on the same fixture:

```
                 Debug        Release
the click       ~520 ms       ~291 ms      1.8x
  mesh (1)       ~322          ~134        2.4x
  tiles (11)     ~143           ~78        1.8x
  field (2)       ~48           ~76        0.6x  <-- the wrong way
  A*               3.3           0.6
the raster        ~70 ms        ~14 ms      5.1x
```

Two things follow immediately.

**The raster stops being a debt.** §101's remaining item there was a ~25 ms whole-map apply that could be
windowed; in Release the whole raster is 13.8 ms and the apply is 8.6. There is nothing to win. That item is
struck from the list rather than carried, and the lesson is the general one: an optimisation ranked in Debug
can be ranked against a cost that does not exist.

**One term went the wrong way, and that is now the open instrument question.** The field solve is consistently
1.6× *slower* in Release — 75–77 ms against 45–51 — at identical work: two fields built, 27,624 climb calls,
byte-identical counts. The terms are additive in both configs (they close to within 0.3 ms of the total), so
this is not a boundary absorbing tile time. A term that gets slower under optimisation is either a real
deoptimisation or a mis-attributed region, those want opposite work, and **the honest position is that I do not
know which.** One measurement — per-phase timestamps inside the field construction, both configs — before
anybody optimises the field.

### Where the click can actually go

Reference points from the same Release run, which is what makes the projection more than arithmetic:

```
a normal cold order (mesh already warm)   ~140 ms   tiles 48-65 (7-9 fills) + field 74-88
a second order to nearby ground             7.7 ms  1 tile fill
an order needing nothing new                0.3 ms
```

- **Mesh amortisation (§99) takes the click to ~157 ms and no further.** That is not a coincidence, it is the
  definition: an old mesh that keeps serving turns the click into a normal cold order, and a normal cold order
  is ~140 ms. **The floor of that fix is the cost of an ordinary order**, which is itself eight frames.
- **Tiles: 78 ms over 11 fills, ~7 ms each.** The interesting half is that a normal order needs 7–9 and a
  follow-up needs 1 — so most of the click's eleven are re-derivations of what was already known, and the
  prize is reuse across a placement change rather than a faster fill. That is §115's move one layer up and it
  is a much harder claim: tiles are keyed to mesh regions, so a rebuilt mesh invalidates them structurally,
  and whether their *contents* are still valid is a real question about a changed decomposition rather than a
  formality. Faster fills are worth maybe 2× on their own (~240 ns a neighbour visit in Release, after §98
  already took 45% out of the corner tests).
- **Field: 76 ms for two builds** — and see above; this term is not ready to be optimised, and "why two" is
  the first thing to ask of it.

### The target, stated as the thing that actually matters

At 60 fps the budget is 16.7 ms a frame and §83 measured the gameplay frame at 16–45 ms, so the headroom for
extra work in any one tick is about **5 ms**. That reframes the whole remaining exercise:

> **No amount of reduction gets this event to smooth.** A perfect 3× on every term left in the click still
> leaves ~97 ms, which is six dropped frames. Smoothness is only reachable by *spreading* the work across
> ticks — 291 ms at a 5 ms ceiling is about sixty ticks, one second — and reduction's only job is to lower how
> much has to be spread.

So the target is not a click figure at all, it is a per-tick ceiling: **no tick more than ~5 ms above its
neighbours**, with the placement rebuild spread behind an old mesh that keeps serving until the new one lands.
Which is exactly what §99 proposed, and the reason to do it is not its 46% — it is that it is the only shape of
fix that can reach the goal.

One more thing worth keeping in view: **the ordinary click is already fine.** 7.7 ms for a second order,
0.3 ms for one that needs nothing. The event under discussion is the first order after a building completes,
which is rare and not player-initiated. That is a real argument about priority, not a reason the work is
wrong — but it belongs next to the 46%.

## 119. The stall was a click after all, and the stage it landed in was not the one that caused it

Run from the chair on the Village, `--timings`, and reported: *it did happen when I clicked.* The log agreed
and my reading of it had not.

```
tick 1130  jobs 154.586  paths 154.584  commands 0.065  nav 0.001  TOTAL 154.829
tick 1153  jobs 101.563  paths 101.561  commands 0.020  nav 0.001
tick 1176  jobs  86.852  paths  86.850   ... 38, 38, 25, 26, decaying to 0.24 by tick 1440
```

Sixty frames took **10.34 s** across that window (172 ms a frame), then 2.08 s for the next sixty. Not four
bad frames — some twelve seconds of degraded play in Debug.

### What I got wrong, and it is §114's mistake in a new costume

I read `commands 0.065 ms` as *no player order was involved* and went looking for a jobs-layer cause. But the
Commands stage only **applies** a command; when the routing is paid by the jobs layer acting on it over the
following ticks, the cost lands in the Jobs stage and `commands` stays near zero. **A cheap `commands` figure
does not mean a cheap click.** §114's lesson was that a report which only covers some of the tick will support
any theory held about it; this is the same error one level along — I attributed by *which stage holds the
time* and concluded *what caused it*. Stage is not cause.

The diagnostics say so plainly, and they were in the same log:

```
f1200   moving  6   jobs/assigned  8   working 8   interrupted 0
f1260   moving 13   jobs/assigned 13   working 8   interrupted 0
```

Thirteen bodies start moving and five assignments appear, inside the stalled window.

### Two facts that make the shape of it clear

- **`BeginOrderBudget`/`EndOrderBudget` wrap only the command batch.** Routing the jobs layer does afterwards
  runs with the budget inactive, so §102's pooled 300,000-expansion ceiling — the thing that bounds a click —
  **does not apply to it.** Only the per-search 250,000 does.
- **`MaxRoutePlansPerTick = 2` is checked in exactly one place**, inside `ReconsiderCongestedRoute`. It bounds
  congestion replans and nothing else.

So nothing bounds job routing in aggregate. The arithmetic fits: 10.34 s over the fourteen A\* searches in
that window is ~740 ms each, against the 1,058 ms / 250,000-expansion cold cross-map search §116 measured in
the same config. And the printed phase figures are a twenty-tick EMA, not single ticks, so the worst ticks
were worse than 154 ms.

### Why the fixture could not have found this

`--pathprofile` issues `QueueMove` and nothing else, so §114–118 ranked the debt on the one event the fixture
produces. `--jobs` — which does order a cohort and watch it — reproduces none of it either: `jobs ms` stays
under 0.2 there, because its bodies are on synthetic terrain beside their work, not in a founded village.
**Three sections ranked a 291 ms event while a multi-second one sat outside the fixture's reach**, and the
only thing that found it was somebody clicking.

So the instrument moves to where the event is. `RouteAttribution.Describe` is now shared by the fixture and
the live loop — `--timings` prints `ROUTES` lines once a second, silent when nothing asked — because a live
report that formats routing its own way is a second opinion nobody reconciles.

### Two things found on the way, both instruments that were lying

- **The launcher published Debug.** `tools/run-rts-game.sh` — the only way anybody plays this — built and ran
  `-c Debug` and said nothing about it, so every judgement from the chair since the launcher existed was made
  at roughly twice the shipped cost (§118: the click is 520 ms Debug against 291 Release). It now takes
  `--release`, keeps Debug as the default so earlier figures stay comparable, and prints which one it ran.
- **`--jobs` had been reporting a false FAULT since §106.** Its criterion was "nobody left holding an
  interrupt after being let go", and §106 decided from the chair that **an order holds until overridden** —
  reach the target, then idle. So sixteen correctly-parked villagers were a fault on every run, and two of its
  three per-unit moments ("interrupt expired", "first leg finished") measured behaviour that no longer exists
  and printed `NaN`. A trace that cries wolf is read as noise and then not read at all, which is worse than no
  trace. The criterion is now unreachable-only; the parked count is reported as the expectation it is, with a
  note if it differs from the ordered count because that means somebody left the cohort.

### What is next, and it is a measurement rather than a fix

Re-run it from the chair and click the same way. The `ROUTES` line will name the callers of those fourteen
searches, and the candidates want different answers: `SoloMove`/`OrderSlot` is the order path and §117's
question again; `Behavior` is the jobs layer walking people to work, which has no cohort and therefore no
shared field to fall back on, and would be the same "the field exists and this path does not use it" shape one
layer over. Not asserted — that is what the line is for.

## 120. Thirty routes found and binned, and the two drifts that hid them

§119 wired route attribution into the live loop and asked for one more run from the chair. It came back with a
sentence that settled the diagnosis and a rule that settled the fix:

> *I clicked the first time, the game hitched for a second but the guys didn't move, then I clicked on a second
> location and they started moving — both locations were random corners on the fog hidden map.*

And, asked what a villager should do when it cannot reach where it was sent:

> *Ideally I'd want a villager to walk up to at least the "last reachable/navigable" point if I click a random
> spot in the fog. I won't expect them to magically know they won't be able to reach something nobody can see.*

The live `ROUTES` lines, Release:

```
tick 1033  commands 0.046 ms   SoloMove 10 queries  845 ms  2,500,000 cells — 0 answered, 10 partial
tick 1189                      SoloMove 11 queries  907 ms  2,750,000 cells — 0 answered, 11 partial
tick 1344                      SoloMove 11 queries  926 ms  2,750,000 cells — 0 answered, 11 partial
   #7  SoloMove  Partial  NOTHING  250,000 cells  117.5 ms  over 797 cells of map
```

Every request exactly 250,000 cells — the per-search ceiling — and **nothing answered**, recurring on a
~155-tick cycle for the rest of the run. The caller is `UpdateJobs` → `JobStep.WalkTo` → `BeginSoloMove`, which
is why §119 found the time in the Jobs stage. Roughly 900 ms of searching per wall-clock second: the bodies had
no route *and* the fixed-step loop was starved, so "it hitched and nobody moved" is two symptoms of one cause.

### Two drifts stood between the fixture and the event

The first attempt to reproduce it headlessly passed on all four corners, twice, for two different reasons.

- **The village the gate builds is not the village the game founds.** `Populate` carries a comment saying it
  is *shared by the headless gate and the live game, deliberately: two settlement definitions would drift* —
  and they had drifted in the call. The gate founded twelve farms, seven cutters and seven carts at dawn; the
  game founded eight, four and five at `StartAtSeconds(3100)`. Nineteen people against thirteen, at different
  hours, doing different work. **A shared method with unshared arguments is two definitions wearing one name.**
  Both now take `SettlementScenarios.VillageRecipe` — `Gate` and `AsPlayed` — from one place.
- **The event needs the bodies to have walked away first.** Ordered to a corner, the cohort takes the shared
  field and issues no cell search at all; the expensive routing starts a couple of hundred metres later, when
  something wants a body back and the distance has become cross-map. A ten-second watch sees none of it. At
  three hundred seconds it appears.

With both fixed, `--fogclick` reproduces it — and names it more precisely than the live log could:

```
south-west (-290,-290)   worst tick 188.3 ms
  NoVelocity        18 queries  1671.3 ms  4,500,000 cells — 0 answered, 18 partial
  NoIntentRetry      9 queries   750.9 ms  2,250,000 cells — 0 answered,  9 partial
  TransitStranded    3 queries   327.3 ms    750,000 cells — 0 answered,  3 partial
  DISCARDED 30 routes the search had already found: 30 smoothed to nothing, 0 first step blocked
```

### The bug, and it is the requested behaviour being computed and thrown away

A budget-stopped search does not fail. `FindCellPath` returns `Reconstruct(cameFrom, startIndex, bestIndex)` —
a route to the furthest cell it reached toward the goal, which **is** the last reachable point the chair asked
for. Then `SmoothPath` discards it. Two lines do the damage:

- The goal is appended as the final candidate whether or not the search reached it, so a truncated route always
  ends in a jump to ground nobody proved is connected.
- When one candidate step fails `SegmentIsBodySafe`, the method returns `Array.Empty` — **throwing away every
  waypoint it had already accepted.** The comment above it says the cost and congestion filters *must not be
  able to destroy the route*; the body-safety check still could.

That reasoning was sound and it is about the *first* segment: with nothing accepted there is no valid prefix
and no route, and empty is the honest answer. With a prefix, the prefix passed every test the smoothing has —
it is not manufactured, it is the part that worked. So the fix is `result.Count > 0 ? result.ToArray() :
Array.Empty<Vector2>()`, and the original invariant survives intact.

### Measured, same fixture, same corner

```
                          before        after
routes discarded              30             0
queries answered         18 of 48       24 of 24
routing                  ~2757 ms       ~481 ms      5.7x
cells expanded              7.5M           1.0M
NoVelocity + NoIntentRetry    27              0
distance walked            173.9 m       203.4 m
```

**The retry classes vanish, and the bodies walk further.** Both follow from the same thing: a body handed a
partial route makes progress, so nothing asks again from the same place. `NoVelocity` and `NoIntentRetry` were
not causes, they were the loop.

### What is not fixed, said out loud

The worst tick is **219 ms**, against 188 before — the discards are gone but the searches that produce the
partials still run to their 250,000-cell ceiling at 80–130 ms each. `--fogclick` now faults on that, against a
stated 50 ms ceiling (three frames; §118 argues the real headroom is nearer five, and this is deliberately the
looser number — the line below which nobody would have complained). It also faults on any discarded route,
which is the regression guard for this section.

That remaining cost is §117's question one layer over: a cross-map cell search where a hierarchy exists. It is
now a hitch rather than a livelock, which is a different severity, and it is the next thing.

### Instruments added

- `PathSmoothedToNothing` and `PathFirstStepBlocked` — a route found and discarded is the most expensive
  refusal there is, because the caller cannot tell it from "no route exists" and therefore asks again. Neither
  was counted; the two want different fixes and only one of them fired.
- `ExpansionBudgetOverride` — the per-search ceiling was a `const`, so the truncated-route path was only
  reachable with a body eight hundred cells from its goal on a real map. That is why it had never been
  exercised. A fixture can now provoke it in a thirty-metre world, and says so in its output when it does.
- `--fogclick` — the click a player made, headless, on the same map, ordered to every corner rather than the
  one that failed. A fixture that tests the direction somebody happened to complain about is how §119 happened.

## 121. Twenty-seven times cheaper and five times longer: the guided search, measured and rejected

§120 left the click a hitch rather than a livelock and named what remained: a cross-map cell search costs
60–210 ms because it expands up to 17% of the map, while a rectangle hierarchy that answers exactly that
question sits unused beside it. §95 had proposed the fix years of sections ago — *the estimate prices a cell at
the cheapest surface cost that exists while the real step charges surface, elevation, turning and congestion on
top; the real fix is upstream of here.* This section tried it, and the answer is no.

### The measurement that made it look certain

Every route request now reports its goal and whether a cost field for that goal already existed. Across a
fog-click run:

```
TransitStranded     3 queries  327.2 ms  750,000 cells — 3 had a field
CongestionRecovery  3 queries  335.5 ms  750,000 cells — 3 had a field
RouteRepair         1 queries  112.3 ms  250,000 cells — 1 had a field
FieldEntry         20 queries   11.4 ms       51 cells — 0 had a field
ReturnToHold       26 queries    7.1 ms       39 cells — 0 had a field
```

**Eight of eight.** Every search that ran to its expansion ceiling had the answer already sitting in a cost
field and ignored it; every cheap search had none. That is as clean a correlation as this arc has produced,
and it pointed at a fix that costs nothing to obtain — read the field, never build it.

### Two gates it needed, and the second was found by a test rather than by thinking

Turned on everywhere, `group distributes across multiple pen exits` went to `exits=[0,2,28,0]`: thirty bodies,
one gate. Every body steered by one field agrees with every other body steered by it, and for three callers
that agreement is the bug — `TransitStranded` exists because §96 found a body solving its own route can pick a
different exit, and the two congestion reasons carry avoidance centres precisely to differ from the shared
answer. Exempting those three by name fixed the four-exit pen and left the two-exit one still failing, with two
bodies stranded whose reason was on the permitted list.

**It is not *who* asks, it is *where the body is standing*.** The second gate is the crowd itself, at the
pressure threshold §96 already measured for the same judgement, now read by both rules instead of one. With
that, every self-test passed.

### The half that made it reach anything

Gated, the guide almost never fired: across a session only four asks in twenty-four found a field, and the
expensive uncrowded searches — the ones a player actually reported — had none. But within a burst the picture
reverses. Thirteen villagers called home across the map are **thirteen asks for one goal**, so an expensive
search can build the field once and the other twelve ride it free. The trigger is the search itself rather than
a distance threshold guessing from outside: at twenty thousand expansions it abandons, builds, and starts again,
which bounds the waste by construction.

```
13 villagers called home        flat            guided
routing                    741 / 859 ms         113 ms
cells expanded                2,233,924         82,474      27x
worst tick                 80.3 / 89.5 ms   32.9 / 31.4 ms
```

### And then the raid leg failed, and it was right

```
                    flat                     guided
raiders home        13 of 24                 2 of 24
longest a raider    164 s                    297 s, against a 194 s round trip
```

Guide without the restart is byte-identical to flat — no field exists for a raider's goal, so nothing is
steered. Guide with it, and raiders stop getting home. The route quality harness says why, pair by pair:

```
out, south-west   flat 300 m | guided 538 m | 1.793x
out, west         flat 289 m | guided 546 m | 1.891x
out, south        flat 202 m | guided 276 m | 1.370x
out, north-west   flat 125 m | guided 627 m | 5.024x
back, west        flat 482 m | guided 551 m | 1.144x
```

A 125 m walk becomes 627 m. The corner-graph estimate is not an admissible lower bound and overestimates by
enough to turn A\* into something close to greedy best-first — which is precisely this shape: fast searches,
bad paths. A body walking five times round is not a performance win, and the raid leg found it independently
before the harness did.

**So the lever is off, on §84's precedent, rather than deleted.** The idea is still right and only the estimate
is wrong: the next attempt should steer by the field's own `CostAt` — exact where its tiles are filled — rather
than the analytic corner-graph figure, or scale the estimate until the route-quality leg stops complaining.

### The instrument that nearly agreed with me

The first version of the route-quality harness reported **1.000x on five pairs out of five**, and I believed
it. It was comparing two unguided arms: the restart did not exist yet, no field existed for any of those goals,
and both arms ran the identical flat search. A harness that cannot tell its two arms apart will report perfect
agreement forever, and perfect agreement is exactly what a hopeful author wants to see.

Two smaller versions of the same fault, both from this section:

- **`--flat-heuristic` was parsed two hundred lines below `--selftest`,** which calls `Environment.Exit`. So
  the control arm and the arm under test were the same arm, agreed perfectly, and nearly bought the conclusion
  that the pen failure predated the change. A lever parsed after the branch it is meant to affect is not a
  lever.
- **"had a field" was set from the guide rather than from the lookup,** so the moment the crowd gate began
  declining fields the report read `0 had a field` — "there was nothing to use" instead of "there was, and we
  declined it on purpose".

### What this section leaves

Kept, and all of it earned: goal identity and field availability on every logged request; `--fogclick`'s second
phase, which reproduces the uncrowded solo route the live stall was made of; the route-quality harness, which
is the acceptance test any future attempt has to pass; `ExpansionBudgetOverride`; and one pressure constant
where there were two.

The debt is unchanged and better specified. A cross-map cell search still costs 60–210 ms, `--fogclick` still
faults on a 135 ms worst tick in the crowded corner order, and the hierarchy still holds an answer nobody can
currently afford to use. What is now known is that the cheap way of using it costs five times the route, and
what the next attempt must measure before anything else.

## 122. The speed was real: the hierarchy is 28% out on the map the game actually generates

§121 rejected the guided search on route quality and reached for the nearest explanation — an inadmissible
estimate — without asking how far out it actually was. Challenged from the chair:

> *I think the speed wins might have been real, what if the raid leg failed because of the weird spaced out
> formation they're following?*

The formation half is answered below and the answer is no. The first half was right, and chasing it found
something worth more than the optimisation it was defending.

### Three explanations, measured in order, and the first two are wrong

**My fallback was inconsistent, and it never ran.** `Estimate` returned the analytic figure where the corner
graph could price a cell and the flat lower bound where it could not — two different scales, the second much
smaller, so an unpriceable cell would look *cheaper* than its priced neighbours and invite the search into
exactly the pockets the hierarchy cannot see into. A real bug, and irrelevant: **0 of 60,894 lookups fell
back.** The corner graph priced every cell either search ever asked about.

**The estimate is accurate, said the only measurement anybody had.** §100's fidelity harness reports mean
1.0014, p99 1.0977, worst 1.187 — and an estimate 19% high cannot send a body five times round. Except that
harness has only ever been pointed at the tuned world, and the tuned world **decomposes into one rectangle**.
The relief sweep is no better: at 32 m of amplitude it also reports one rectangle, because it builds the legacy
scattered-landform shape rather than the composed archetypes the game plays on.

**So it was measured where the failure was.** On the village — same seed, same archetype, 8,915 rectangles:

```
                             mean      p99     worst    cells
tuned world (1 rectangle)   1.0014   1.0977    1.187   154,104
village, from a villager    1.2767   2.2013    3.959   896,480
village, from the west      1.3583   2.4745   24.349   896,480
```

**The routing hierarchy is 28–36% out on average on the map the game generates, 2.2–2.5x at the ninety-ninth
percentile, and as much as twenty-four times out at worst.** Against 1.0014 and 1.187, which is the figure this
project has believed for twenty sections.

That fully accounts for §121's routes without any help: an estimate that overshoots by two to four times over
large stretches of ground is one that steers A\* around the cheap way rather than down it.

### It is not the tiles either

The obvious repair — steer by `CostAt`, the tile-filled cost, exact within a region and the thing transit
itself follows — moves the worst route from 5.02x to **4.86x**. A tile is seeded from its own perimeter priced
by the corner graph, so it inherits that error instead of correcting it. Reverted: it is no more accurate and
it fills tiles as a side effect of being asked a question.

### The formation hypothesis, answered

Raiders walk in, fight and walk home on stored routes; the formation station-keeping that could plausibly slow
them lives in `ResolveFlowTransitVelocity` and applies only to bodies on the shared field. And the arithmetic
closes without it: the guided routes measure 1.25x to 4.86x longer, the mid-range being about 1.8x, and
**164 s x 1.8 = 295 s against the 297 s a raider was measured to live.** A body walking 1.8 times as far at
the same speed takes 1.8 times as long, which is the whole of it. Route length is measured directly — the
summed polyline the body would actually walk — rather than inferred, so there is nothing left for formation to
explain.

### What this changes, and it is not the guided search

The guided search stays off; its verdict is unchanged and now properly attributed. What is new is a defect
underneath it, in production, unmeasured until now:

- **The economy asks this hierarchy how far things are.** `TryOptimalTravelTime` and `IsSlotReachable` price
  catchments and slot detours off `CostAt`. A systematic 28% overestimate makes the settlement believe its
  fields and quarries are further away than they are, and §70's catchment constants were tuned against it.
- **A 24x local error is a spike, not a scaling.** A smooth overestimate would leave gradients intact, and
  transit only ever asks for a direction — but a field with spikes in it points bodies at them. Whether that
  reaches cohort movement is a measurement nobody has taken.
- **§100's harness needs to run where the game runs.** It has been green for twenty sections on a map with one
  rectangle in it. That is the same failure as §120's village recipe: an instrument aimed at a world nobody
  plays, reporting on a world nobody tested.

The next thing on this thread is therefore not the search. It is to find out why the corner graph is this far
out on composed terrain — and the harness now runs on the real map inside `--fogclick`, which is where that
work starts.

## 123. The error, attributed: two thirds is climb, and the estimate has the wrong sign to be a heuristic

§122 found the hierarchy 28% out on generated ground and could not say on which term. An error in a sum is
attributed by subtraction, so the same map was priced four ways — the harness can now drop the bend charge and
the climb charge independently, and nothing in the game passes either override.

```
from a villager — 896,480 cells, 8,915 rectangles
  as shipped    mean 1.2767 | p99 2.2013 | worst  3.959
  no bend       mean 1.2743 | p99 2.1968 | worst  3.954
  no climb      mean 1.1017 | p99 2.0987 | worst  3.655
  neither       mean 1.0996 | p99 2.0942 | worst  3.650

from the west — same map, different goal
  as shipped    mean 1.3583 | p99 2.4745 | worst 24.349
  no bend       mean 1.3566 | p99 2.4728 | worst 24.332
  no climb      mean 1.1203 | p99 1.9137 | worst 17.658
  neither       mean 1.1187 | p99 1.9120 | worst 17.641
```

**Climb is about two thirds of the mean error. Bends are 0.2% of it.** The bend charge was added because the
first cut of this estimate came out 9% *under* the route it approximates, and on this map it is doing almost
nothing either way — worth knowing before anybody spends a section on it.

**And a third of the error is the graph itself.** With both charges removed the estimate is still 10–12% high
on average and, from the western goal, **17.6x out at worst** — that is what forcing every route through the
corners of crossings costs on ground that decomposes into nine thousand rectangles. Neither term explains it,
so it is the approximation and not the pricing.

### Why climb overcharges, which is a fact about what it measures

A climb charge is *the sum of absolute height differences along a line* between two corners — the total
variation of the straight leg. The route it is approximating does not have to walk that line: across a
rectangle it can follow a contour, gaining nothing where the straight line goes over a rise and back down. On
32 m of relief with 2 m of undulation, straight-line total variation is a large overcharge, and on flat ground
it is exactly zero.

Which is the other half of §122's discovery. The tuned world has one rectangle, no crossings, and no relief:
**every term that could be wrong is zero there.** 1.0014 was not a measurement of this estimate, it was a
measurement of the case in which the estimate is trivially exact.

### The structural finding, and it retires §121 permanently

The fidelity harness states its own contract in its header:

> *Ratio is the router's cost over the flat whole-map search it replaced, so 1.000 is the exact answer and
> anything below it is a route that does not exist.*

So this hierarchy is deliberately built **never to report below the true cost.** That is the right contract for
what it was built for — a route priced cheaper than reality is a promise the ground cannot keep, and the jam
test in `--routingtest` exists to catch it drifting under.

An A\* heuristic has the opposite contract: it must never report **above** the true cost, or the search stops
being able to recognise the best route when it finds one.

**They are incompatible by construction.** §121 did not fail because of an inadmissible estimate that needed
tuning, or a fallback bug, or the analytic answer rather than the tile-filled one. It failed because it used a
conservative upper bound as a heuristic, and the sign was wrong before a line of it was written. No amount of
work on this field fixes that.

### What a future attempt has to build instead

The attribution hands over the recipe, because both terms that push the estimate up have obvious lower-bound
counterparts on the same graph:

- **Climb: charge the net gain, not the total variation.** A body going from one height to another must gain at
  least the difference; charging `|h(to) − h(from)|` instead of the sum along the line is a provable lower
  bound and removes two thirds of the error in the direction a heuristic needs.
- **Legs: the straight-line distance between the ends, without the corner detour.** Forcing the route through
  crossing corners can only make a path longer than the free-space line, so dropping it is a lower bound too —
  and it is the term carrying the 17.6x worst case.
- **Bends: drop the charge.** It is worth 0.2% and a bend is not something a lower bound may assume.

That is a second, cheaper oracle over the existing decomposition rather than a change to the one the game
routes and prices catchments with — and its fidelity is measured with the same harness, read from the other
side: a lower bound is sound while the ratio stays **at or below** 1.000, and useful in proportion to how close
to it it stays.

### And the debt this leaves in production, unchanged

None of the above touches what the settlement currently believes. `TryOptimalTravelTime` and `IsSlotReachable`
still price catchments off an estimate that is 28–36% high on the map the game generates, with a 24x spike in
it, and §70's constants were tuned against those numbers. That is its own thread and it is now specified: the
mean is a scaling anyone could correct for, and the spike is not.

## 124. The lower-bound oracle: sound, and twenty-five times too loose to steer with

§123 handed over a recipe — a second oracle over the same decomposition, relaxed until it cannot exceed the
truth, so that a cell search finally has something admissible to steer by. `LowerBoundField` is that oracle,
and it works exactly as specified. It is also useless, for a reason worth having measured.

Every term relaxed until it cannot overshoot: crossings priced as the **segments** they are rather than through
their two corners (the nearest point on an axis-aligned run is a clamp), distance Euclidean rather than octile,
ground charged at the cheapest surface that exists anywhere, and no climb or bend charge at all — zero is a
lower bound on each. Any real path passes through a sequence of rectangles whose consecutive members share a
crossing, so it corresponds to a walk in this graph, and every leg of that walk is at least the straight-line
gap between the two borders it joins.

```
                     mean      p99     worst
from a villager     0.0394   0.1510   0.6878
from the west       0.1533   0.3736   0.5782
```

**It never exceeds the truth** — worst 0.688 and 0.578, so the bound is genuinely a bound, which is the one
thing it had to be. And it reports four to fifteen per cent of the real cost, which steers nothing: an
admissible heuristic that small is barely distinguishable from no heuristic at all, and the search it was meant
to rescue would explore the same quarter of a million cells.

### Where the tightness goes, counted

```
11,640 legs free of 37,206
```

**Thirty-one per cent of the graph's legs cost nothing**, and a Dijkstra chains precisely those. The cause is
structural rather than a matter of pricing the terms better: each leg is charged from the nearest point of one
crossing to the nearest point of the next, and **nothing requires two consecutive legs to use the same point on
the border they share.** The walk may reposition itself along every crossing for free. Where two crossings of
one rectangle touch or overlap in projection the leg between them is zero outright, and with ten thousand
crossings on this map there is a free chain across most of it.

That is the standard weakness of a portal-graph bound with segment nodes, and the standard fix is to carry the
entry point along the walk — any-angle propagation over the portals, which is a different and much larger piece
of work than this was.

### So the guided-search thread closes here, properly

Three sections tried to make a cell search cheaper by giving it what the hierarchy knows:

- **§121** used the cost field. Twenty-seven times cheaper, routes up to five times longer.
- **§122–123** found why, and it was not a bug: that field is built never to report below the true cost, which
  is the opposite of what a heuristic needs. Two thirds of its overestimate is climb charged as total variation
  along a straight line; a third is the corner detour, which is worst next to the goal where the true cost is
  smallest — the 24x cell turns out to sit one metre from its goal.
- **§124** built the admissible counterpart the recipe called for. Sound, and twenty-five times too loose,
  because a third of its legs are free.

**What is left is not a tuning problem.** Either somebody builds any-angle propagation over the crossing graph
— and then a tight admissible bound exists and the twenty-seven-fold saving is available — or a cross-map cell
search stays as expensive as it is. Both arms live behind `--guided-search`, both harnesses read the same map
from both sides, and the next attempt starts from a measurement rather than from §95's hopeful sentence.

The remaining per-tick cost in `--fogclick` is unchanged and correctly attributed: a search that reaches its
expansion ceiling still costs a visible hitch, and that is now a known price rather than an open question.

## 125. The 28% reaches one decision, and that decision is not currently taken

§122 flagged a consequence and did not measure it: `TryOptimalTravelTime` and `IsSlotReachable` price the
settlement's world off an estimate that runs 28% high, and §70's constants were tuned against those numbers.
The place to start was not the size of the error but where a number that size can change an answer.

### Most of the consumers cannot be affected at all

Both economy call sites that price a haul use travel seconds to **rank** candidates — `seconds < bestSeconds`
— and a systematic overestimate cancels out of a ranking exactly. `RemainingRouteDistance` uses it for a
flow-transit body's progress, compared against its own previous value, where a scaling also cancels.
`IsSlotReachable` compares against `direct * 2.0 + 0.45`, and a 1.17x displacement cannot cross a tolerance
with two-fold headroom — it would need the true ratio to sit between 1.56 and 2.0.

**One consumer thresholds it.** `EconomySystem`'s catchment test refuses a source outright once
`seconds > budget`, and there an estimate that runs high shrinks every catchment.

### Measured on the played village, against the flat truth

```
18,946 store/source pairs priced | mean 1.171x | worst 2.646x | budget 37 s
64 nodes inside the budget by the truth and outside it by the price
   — of which 0 hold stock today
```

So the displacement is real and its size is now known in the units that matter: **sixty-four nodes that a
hauler could reach and the settlement believes it cannot.** And none of them hold anything, because this
village keeps its entire store in one granary — one node holds stock, so the threshold has nothing to be wrong
about and no haul is currently lost to it.

That is the honest verdict: **a latent error of a specified size, not a live bug.** It becomes a live bug the
day a settlement keeps piles at between one and 1.3 times its catchment radius, and `--fogclick` now faults
exactly then — the wider count is printed every run as a standing figure and the fault fires only on nodes that
actually hold stock.

### Three things the measurement needed, all of them the same mistake

The first version reported zero pairs, three times over, and each cause is worth writing down because they are
one lesson wearing three hats:

- **A node stands on the ground it occupies.** Trees and buildings block their own cells, so a flat reference
  field reads infinity at every node position and all thirty thousand were skipped.
- **The store is under the store.** A flat field seeded on a blocked cell reaches nothing, so the whole
  measurement was empty until the granary's own cell was resolved outward.
- **Eight cells is not far enough to get out of a settlement.** The first resolution searched four metres and
  found nothing but more buildings, and reported a budget of zero rather than a failure.

`TryOptimalTravelTime` already carries the first two cares, with a comment explaining that a query from inside
a building means from the ground beside it. A measurement of a function has to reproduce the function's own
preconditions, and mine kept not doing it — quietly, by returning a plausible zero each time.

### What is left of §122's list

The mean is answered: it reaches one threshold, and that threshold is not currently exercised. The spike is
answered too, and by §123 rather than here — the 24x cell sits one metre from its goal, where the true cost is
near zero and any fixed error is a large ratio. Neither is a reason to change the hierarchy today.

What remains is the thing that has been true since §122 and is now the only open item on this thread: the
hierarchy is 28% out on the map the game generates, nobody had measured it because the harness only ever ran on
a world with one rectangle in it, and the cost of that is currently paid in a per-tick hitch on cross-map
searches rather than in any decision the settlement takes.

## 126. The field solve is a dictionary, and 480,000 lookups a build

§118 left one instrument question open: the field solve measured 45–51 ms in Debug and 75–77 in Release on
byte-identical work, and a term that improves when you stop optimising is either a real deoptimisation or a
mis-attributed region. Those want opposite work, so nothing could be done to the field until it was known
which.

Four phase timers inside the constructor — mark which rectangles hold pressure, lay out the corner positions,
seed from the goal's rectangle, run the corner Dijkstra — answer the first half immediately.

```
Release  search 75.2 / 75.3 / 75.4 ms
Debug    search 49.6 / 48.0 / 48.3 ms
             both: 34,340 corners, 29,248 settled, 480,048 legs, 479,756 climb hits, 292 misses
```

**All of it is the Dijkstra**, at identical counts, and the inversion is tight and reproducible rather than
thermal. The other three phases are a fraction of a millisecond between them.

### Which term, isolated with two stopwatch levers

`--field-noclimb` removes the climb charge; `--field-climbkey` forms the cache key and does not look it up.
Both say routes are wrong with them on, because they are.

```
                        Debug    Release
full                     47.7      81.6
key formed, not used     50.7      22.6
no climb at all          44.9      28.7
```

So **forming the key is free in both** — four `MathF.Round`s and a tuple, lost in the noise — and the
**dictionary lookup is 59 ms in Release and nothing in Debug.** Hit and miss counts are identical to the digit,
so the optimised build is not missing the cache: it is paying about 120 ns for each of 479,756 hits that Debug
gets for nothing measurable.

**I cannot explain that from black-box timing and will not invent a mechanism for it.** What is measurable is
that the lookup is **72% of the Release field solve** and therefore about **20% of the whole click**.

### And the finding does not depend on the explanation

Whatever makes a `Dictionary<(int,int,int,int), float>` cost 120 ns in one build and nothing in another, **the
real defect is asking it 480,048 questions per field build.** The legs are enumerated rectangle by rectangle,
which means the answers do not need a hash at all: a flat array of 480,048 floats — under two megabytes, held
against the mesh exactly as the climb cache is held against the terrain revision — indexed by a per-rectangle
offset, turns every one of those lookups into an array read.

The prize is the difference already measured: **the field solve goes from ~76 ms to ~23 ms, which is ~53 ms off
a ~290 ms click, about 18%.** It is a bigger, cheaper and better-specified win than §99's mesh amortisation,
which was the next item on the list and whose floor §118 put at the cost of an ordinary order.

### A note on the measurement, because it went wrong twice in one session

Two of the figures in this section were wrong before they were right, and both times the fault was in reading
the output rather than in the code:

- A `tail -22 | grep` pulled a quiet-leg field-split line instead of the click's, and reported the full arm at
  20.3 ms and 122.7 ms — a spread wide enough that **I concluded the inversion was thermal noise and said so.**
  Pinning the extraction to the section it belongs to gave three consecutive Release samples inside 0.2 ms.
- The build had been failing on a name collision for several runs while `--no-build` kept executing the
  previous binary, so a lever that did not exist yet appeared to have been measured.

Both are the same mistake as §121's `--flat-heuristic` parsed below `--selftest`: **an arm that is not the arm
you think it is will agree with whatever you already believe.** The fix in all three cases was to make the
harness say out loud which arm it ran — the levers print a banner, and the extraction now names the section it
reads.

## 127. The field solve, halved twice: a key that was the wrong type and a hash that was not needed

§126 pinned 72% of the field solve to the corner-climb dictionary lookup — 120 ns for each of 479,756 hits in
Release, nothing measurable in Debug, identical hit counts — and said the fix did not depend on explaining it.
Two changes, measured separately, and the second is the one that generalises.

### The key was the wrong type, which was worth thirty-three milliseconds

The cache had a `(int, int, int, int)` key, and the comment above it explains why: the obvious packed long
collided catastrophically, because .NET hashes a long by folding its halves with XOR and two corner indices
under 2^16 hash to `from ^ to`. That was true and the conclusion drawn from it was too strong. **A fold is a
fine hash when the thing being folded is already random.** Four doubled coordinates pack into 64 bits exactly
— a 1,200-cell map doubles to 2,400, and corner coordinates are never negative — and one round of splitmix64's
finaliser is a bijection, so no two pairs can collide that were not already equal.

```
Release field search    tuple key    75.2 / 75.3 / 75.4 ms
                        mixed long   43.8 / 43.9 / 42.7 ms
```

**And Debug went the other way**, 48 to 79. Whatever makes a ValueTuple lookup cheap in an unoptimised build
makes a long lookup dear, and the reverse. That is a reason to stop using either as the hot path rather than to
pick a favourite.

### The hash was not needed, which was worth another six and change

Every leg the corner Dijkstra prices joins two corners of **one rectangle**, and both are named by their
position in that rectangle's own crossing list. So the answers form a small square matrix per rectangle:
`CornerClimbMatrix`, one flat array with a per-rectangle offset, about eight hundred kilobytes on this map,
held against the mesh it is indexed by and evicted with it. A lookup is an array read, and one rectangle's legs
are contiguous.

`Expand` finds the local number of the corner it is expanding from once per expansion — O(crossings) against
O(1) per leg, and there are several legs per expansion — and the loop index gives the other for nothing.

**Filled from the durable table rather than from the ground**, because a matrix indexed by mesh-local positions
cannot survive a mesh rebuild and §115 exists so that climbs do. A miss consults the coordinate-keyed table; a
miss there samples terrain. The split says exactly that:

```
climb answers: 240,024 from the matrix, 239,732 from the table, 292 sampled
```

The first field built on a decomposition fills it and the second reads it, which in this click is a clean half
each. In play, where many goals share one mesh, the share keeps rising.

### What the click costs now

```
Release          field solve        the click
tuple key        ~76 ms             ~291 ms
mixed long       ~43 ms             ~247 ms
+ matrix         ~37 ms             ~245 ms
                 39.9 / 35.9 / 34.2 / 37.1 ms over four runs
```

**The field term is less than half what it was, and the click is about 16% down.** The floor for this structure
is the first field's table lookups plus the Dijkstra itself, which the key-only arm measured at ~22 ms — so
~37 ms is close to it, and there is nothing else cheap left here.

Two things worth keeping in view. The remaining largest term in the click is the mesh build at ~134 ms, and
§99's amortisation is still the only shape of fix that reaches a per-tick ceiling — §118's argument stands
unchanged. And Debug is now slower than it was, which costs the gate a little wall clock and nothing else: the
figure that ships is the Release one, which is the rule §83 wrote down and the launcher enforces since §119.

Gate 5/5 green, years included.

## 128. The vsync-on pacing pass, and the deadline that had to come from the display

Owed since §83 and deferred through nine sections of frame work. The matrix this arc has been run on measures
with vsync **off**, which answers what a frame costs — the right question for finding the work and the wrong
one for saying whether the game feels smooth. With the display in the loop, every frame that makes its deadline
costs exactly one refresh period, so p50 and p95 both read the refresh and say nothing at all. What a player
feels is the frames that took two periods, and how they clump.

`PerformanceRun` now reports cadence: the share of frames on time, at two periods, at three or more, the
longest consecutive run of late ones, and the mean periods per frame — which divides into the refresh to give
the rate actually presented. `tools/perf-pacing.sh` runs five cases, repeats them interleaved, and prints the
range beside the middle.

### The deadline cannot be inferred from the frames that miss it

Three estimators were written and all three were wrong, each in its own way:

- **The tenth-percentile interval.** Sound while some frames make the deadline. On the first run four cases out
  of five were missing every deadline, so it concluded the display refreshed at 33 ms and reported 60% to 97%
  "on time". A run that never once hits its cadence cannot measure that cadence.
- **The minimum interval.** 14.18 ms on a panel whose period is 16.67 — jitter, because an interval measured
  after a long one can appear short.
- **The median of a case chosen to be cheap.** Correct in principle and it read 63 ms once the machine was
  busy, because a near view with fog off is only cheap when nothing else is running.

So the period comes from the display. `IRenderHost.DisplayRefreshHz` reports the monitor's video mode, the
fixture prints what it got — `display reports 60 Hz (16.67 ms)` — and a run that cannot read one reports **no
cadence at all** rather than a flattering one. `--perf-refresh` overrides it in milliseconds only; an earlier
version accepted "hertz above five, milliseconds below, since one is unambiguous", was handed 14.18, read it as
14 Hz, and declared every case perfect against a 70 ms deadline. **A unit inferred from a magnitude is a unit
waiting to be wrong.**

### What the game actually presents

Release, three repeats of four hundred frames each, interleaved, on the 600 m village.

```
                     default trees & casters        as played (--cheap-trees --shadow-proxy)
case              on time   per frame    fps       on time      per frame    fps
still  60 m          0.0%      2.18     27.5       76.9%           1.23     48.7
still 118 m          0.0%      3.78     15.9        3.7%           2.01     29.9
pan   118 m          0.0%      3.70     16.2        8.7%           2.00     30.0
night 118 m          0.4%      4.14     14.5        0.2%           2.04     29.4
still 240 m          0.2%      5.29     11.3        0.4%           2.63     22.8
```

**Two things follow, and the second is the useful one.**

The longest run of late frames is the whole run in almost every case. **This is not stutter, it is a steady low
rate** — and those feel nothing alike. A game at a solid 30 fps and a game at 60 with a drop every second are
both "half the frames", and only one of them is a pacing problem. This one is a frame-cost problem, which is
where the arc has been working anyway.

And **as played at 118 m the game sits at 2.00 refreshes a frame.** Not 2.4, not 2.8 — two, almost exactly,
with the on-time share ranging from 0.3% to 19.7% across repeats. The frame is *just* over the 16.7 ms line, so
it rounds up to two refreshes and presents at 30. A few milliseconds either way is the difference between 30
and 60 fps, which is the sharpest thing this pass has to say: **at the standoff the game is actually played
from, the remaining frame work pays off nonlinearly.** §127's 53 ms off a placement click is worth little to
this; three milliseconds off the steady frame would be worth double the rate.

### And the configuration is part of the claim

The two columns differ by a factor of about two everywhere. `--cheap-trees --shadow-proxy` is what this project
is played with from the chair and the defaults are what a fixture runs, so **a pacing figure without its
configuration beside it is not a figure about the game.** The script records the flags in its own log directory
name, on the same reasoning as §118's config labelling and §119's launcher banner.

The supported camera envelope §82 asked for can now be stated in the units that matter, as played: **60 m is
comfortable at 49 fps, 118 m is exactly on the boundary at 30, and 240 m is 23.** That is a judgement to make
from the chair rather than from a table, but the table is finally in frames a player would see rather than in
milliseconds a frame cost.

## 129. Two settlements on one map, and the first one founded does not work

Perf is parked with its residual specified (§128), and the road forward is the one §82 set: a second player
that plays the same game through the same verbs, so that a rule-bot can be pitted against a person on one map,
with the combat roster arriving after there is something to fight over. Everything in that sentence rests on a
precondition nobody had tested: **can this world hold two settlements at all?**

Most of the answer was already yes. Nodes carry a faction, bodies carry a faction, and hauling, catchment and
housing all filter on it — the economy was written faction-aware and has simply never had a second faction in
it. The one gap was the recipe: `SettlementScenarios.Populate` never took a faction, so every settlement this
project has ever laid down belonged to faction zero by omission.

So: thread a faction through the recipe, found two villages a third of the map apart, and run the year leg's
own acceptance test twice over.

### What it found on the first run

```
faction 0 at (160, 205) founded first, faction 1 at (286, 52) second — 198 m apart
faction 0 owns 68,973 nodes (1 stores), faction 1 owns 16 (1 stores)

  date                 | f0 grain | f0 wood | f1 grain | f1 wood
  year 1, Summer, d 60 |    4,200 |   1,000 |    3,426 |     792
  year 1, Summer, d 67 |    4,200 |   1,000 |    3,327 |     779
```

**The first settlement founded is inert.** Thirteen people, a granary, sixteen buildings, and stores that do
not move by a single unit while its neighbour eats and spends normally. Conservation holds every tick, which
is exactly what an economy doing nothing looks like.

### Which is a fact about order, not about identity

Trees are nodes and they belong to faction zero by default — sixty-nine thousand of them — so "faction zero
behaves differently" was as live an explanation as "the first one founded does". Those want opposite fixes, so
the probe takes `--swapfactions` and the answer is unambiguous: with the identities exchanged the numbers are
identical and the columns swap. **The settlement founded first is the one that stops, whichever faction owns
it.** The forest is not involved.

### And my acceptance test passed it

Worth recording separately, because it is the same mistake this session has now made four times in different
clothes. The test asked whether each faction ended with people and with grain above zero — and **an economy
that does nothing at all satisfies both**. It reported "both settlements fed themselves" over a settlement
that had not eaten a grain in ninety days.

A test for "it works" has to name something that must *change*, not something that must remain. The next
version asserts that each faction's stores move and its people are fed, which is what the year leg means by
feeding itself and what this borrowed the words for without borrowing the substance.

### Where the diagnosis stands

`Populate` queues its hands' assignments as commands rather than applying them, so both batches should drain on
the first tick; the settlement-node index maintains both settlements correctly; and `BindCatchments` filters
stores by faction, which is right. None of those explain it. The next measurement is the one this section
stopped short of: per faction, how many bodies hold a working assignment and how many nodes have hands on them
— which separates "never assigned" from "assigned and not producing", and those want different fixes.

The probe is `--twovillages`, it takes `--years` and `--swapfactions`, and it is not in the gate until it
passes.

## 130. Populate is a map recipe with a settlement in it

§129 left the first of two settlements inert — thirteen people, sixteen buildings, stores that had not moved a
unit in ninety days — with three explanations eliminated and the diagnosis unfinished. Chased down, in the
order the measurements came:

- **Both factions are assigned and working.** Eight bodies each, eight nodes with hands each. Nobody is idle.
- **The difference is the supply binding.** Faction 0's houses were bound to its granary at founding and the
  **first tick unbound them**, 7/7 to 0/7. `BindCatchments` re-binds when the node revision moves and found no
  store for them.
- **It found none because the router refused the pair.** Counted apart, all four refusals were
  `TravelUnpriced` — not a goal off the grid, not a start that would not resolve, but a cost field with no
  finite answer for a house **eleven metres** from its own granary.
- **And unpriced in both directions**, which makes it a fact about the ground rather than about which end the
  Dijkstra started from.
- **The ground had changed.** Within twenty metres of faction 0's centre: 5,425 walkable cells with one
  settlement, **2,220 with two.** Split by cause, 792 cells marked solid in both — its own buildings, same
  footprint, same span — and 2,886 extra solid cells that appear only when a second village is founded.
- **They are trees, and they are alive.** Zero deposits standing inside twenty metres of faction 0 with one
  settlement; **144 with two.** The founding fells the trees it builds among, and the second founding puts
  them back.

### Which it does because `Populate` dresses the whole map

```csharp
PaintBiomes(world);
ScatterWoodland(world, centre);
ScatterOutcrops(world);
```

Three whole-map operations, inside the routine that lays down one settlement. So the second call repaints the
biomes and re-scatters the woodland across the entire map, including over the ground the first settlement had
already cleared to build on — and the trees that land there wall its houses off from its granary. The second
settlement is fine because its own clearing came last.

**`Populate` is not a settlement recipe. It is a map recipe with a settlement in it**, and with only ever one
settlement the two are the same act and nothing distinguishes them. That is the whole reason this stood: the
seam has been there since the routine was written and could not be seen until something asked it to run twice.

Fixed by making the dressing a parameter — `dressMap`, default true so every existing caller is unchanged, and
false for the second settlement. Both villages then run identically:

```
  f0 grain 3,426 -> 3,303 | wood 792 -> 777 | 7/7 sinks bound | 0 travel refusals
  f1 grain 3,426 -> 3,303 | wood 792 -> 777 | 7/7 sinks bound | 0 travel refusals
```

**One consequence recorded rather than hidden:** the woodland scatter takes the settlement centre and keeps a
near band of trees around it, which §22 established is an economic fact about a site and not scenery. A second
settlement founded with `dressMap: false` gets the map's ambient woodland and no ring of its own, so its wood
line is whatever the ground happened to give it. Splitting the band from the scatter is the proper fix and is
its own piece of work.

### And the test that passed an inert settlement

§129 recorded this and it is worth the repetition, because the correction is the transferable part. The
acceptance test asked whether each faction ended with people and with grain above zero — **which is exactly
what an economy doing nothing looks like.** It reported "both settlements fed themselves" over one that had
not eaten a grain in three months, and the books balanced every tick while it did, because conservation is
perfect when nothing moves.

It now asserts that each faction's stores **changed**, and `--dresstwice` keeps the old behaviour reachable so
the fault can still be produced on demand: with it, the probe says
`FAULT: faction 0 is inert: 13 people and 4200 grain, unchanged from the 4200 it started with`. A fixed bug
with no way left to reproduce it is a fix nobody can check.

**A test for "it works" has to name something that must change, not something that must remain.** That is the
fifth time this session that an instrument agreed with what was already believed, and the first four were all
about arms and units and stale binaries — this one was about the assertion itself.

### Where the arc stands

The precondition is met: two settlements, one map, both feeding themselves, conservation exact every tick, and
the travel-refusal counters at zero. `--twovillages` takes `--years`, `--swapfactions`, `--onevillage` and
`--dresstwice`, and it is worth adding to the gate now that it passes.

What comes next is the faction knowledge item — what a second player can see and remember, which §119 recorded
as simulation state that belongs in the fingerprint and is not built — and then a rule-bot that issues the same
queued commands a person does, so that "the AI plays the player's settlement" is checkable rather than claimed.

## 131. Faction knowledge, which is not the fog

§130 left two settlements running on one map, so the next item on §82's road is the one §119 reserved: what a
second player can see and remember. That note is the whole design brief and it draws a line in both
directions.

> **Fog of war** is renderer-side… derived from unit positions and therefore perfectly deterministic, which is
> the whole trap — the determinism argument for pulling it inside is sound and the consequence is not. The
> moment a simulation decision reads it, "what the player can see" becomes "what the world does"… The rule is
> a direction rather than a location: **fog may read the simulation; the simulation may never read fog.**
>
> **What is not an exclusion.** A per-faction knowledge aggregate — which cells a faction can currently see, as
> read by an AI opponent or by a defence deciding whether it is needed — is simulation state and belongs in
> `Carried` with a census entry, not here. **It is not built.**

So `FactionKnowledge` is built as simulation state: fingerprinted every tick, saved, and read by decisions.
One `long` per ten-metre cell per faction — the tick it was last seen on. Zero means never, and any other
value answers both *do I know about this* and *how stale is it* without a second structure or a decay pass. The
census entry says exactly that, so nobody adds a visible-now mask later and gets two answers.

**One authority for who watches how far.** The per-kind building reaches — a granary 105 m, a depot 190, a
dwelling 45 — lived in the renderer's fog settings and nothing outside it read them, which was fine while the
only thing that cared about seeing was the thing that draws it. They are now `NodeWatch` in the simulation,
because a fact decisions read cannot take its reach from a view tunable, and copying the numbers is precisely
what `CanSee`'s own comment warns about.

### Knowing is not seeing, and that turned out to be the design

The first version asked `SimulationWorld.CanSee` for every cell of every watcher's radius — the one authority
on what seeing means, and the obviously right call. **It cost 166 milliseconds a tick in a settlement.** That
predicate marches the navigation raster at a quarter of a metre, so a granary's hundred-and-five-metre reach is
four hundred samples per cell across five hundred cells; a simulated year of it is seven hours, which is why
the gate stopped making progress and stayed stopped.

The phase timer added alongside it is what said so, on the first run. **Which is the argument for adding the
timer with the pass rather than after somebody complains.**

Rather than invent a coarser ray — a second opinion about seeing being the thing to avoid — the question
changed to the one this aggregate is for. **A settlement knows the lie of its own land whether or not a ridge
stands between the granary and the far side of a field.** A bot asking "do I know what is over there" wants
ground it has had people on, not ground currently in line of sight. So knowledge is reach and the fog keeps
occlusion, and the two are allowed to differ because they answer different questions — which is the opposite
of the failure §119 warns about, that being a decision reading the *view*.

### And then it was still seven times the tick

Reach instead of rays brought it to 1.3 ms, measured against 161.0 with the pass switched off. In absolute
terms nothing; against a simulation whose whole tick is about 0.2 ms it is a sevenfold increase, and §85's rule
is that per-tick work over the whole map is a trap whatever it costs today.

Nothing needs it every tick. Cells are ten metres across and a body walks at 1.5 m/s, so it takes seven
seconds to change a single answer and a building never changes one at all. Watchers are refreshed one slot per
tick over a fifteen-tick interval, which makes the cost flat and brought it to **0.07 ms** — 161.306 without
the pass against 161.379 with it.

That changed one piece of the interface honestly: `CanSeeNow` became `SeenWithin(faction, at, tick, window)`,
because with a staggered refresh an exact-tick test would call ground in plain view of a granary unseen most of
the time. **The tightest question this structure can answer is the interval, so that is what it offers.**

### The test, and two ways it was wrong first

*a faction sees, remembers, and keeps its knowledge to itself* — the three properties that make this knowledge
rather than fog. The third is the one a rule-bot depends on and the one a bug would quietly remove: an
aggregate that answered for every faction at once would pass the first two.

Both of its first two failures were the test's own fault, and both are worth recording:

- **It asserted after two ticks**, and the refresh is staggered over fifteen — so it failed on a faction that
  had simply not been asked yet. It now waits an interval and reads the interval from the constant rather than
  assuming a number.
- **It put the two factions sixteen metres apart on the default thirty-metre map**, with twenty-two metres of
  sight each, so each could see the other's ground and privacy failed on a faction that was legitimately
  looking at it. The privacy claim needs distance to mean anything.

Nothing reads the knowledge yet, and it is gathered anyway — so that the state exists to be fingerprinted and
saved from the first tick rather than appearing the day something wants it. What reads it next is the rule-bot,
which is the last piece before two players can be put on one map: it must issue the same queued commands a
person does, so that "the AI plays the player's settlement" is checkable rather than claimed.

## 132. A rule-bot that plays through the same verbs

§130 put two settlements on one map and §131 gave each faction its own knowledge. What was left before two
players can share a map is a player that is not a person — and the acceptance test §4 set for it is
*the AI can play the player's settlement*, which is only checkable if the AI has no other way in.

So `SettlementBot` holds a `SimulationWorld` and touches it through `QueueAssign` and nothing else. No node's
fields, no body's state, no reaching into the jobs layer. Everything it decides is a command a mouse could
have issued, which makes "no resource cheats, no vision cheats" a property of the code rather than a promise.
It lives outside the world on the `RaidDirector`'s precedent — a player is not part of the world — and is
stepped inside the fixed tick, so a watched run and a headless one see the same game.

### Two places it could have cheated, and both were closed by measurement rather than intent

- **Its own larder, not the map's.** The bot decides on §8's autonomy time — seasons of eating left, the figure
  the HUD puts in front of a person — read through the same `Economy.Outlook` call the HUD makes. That call
  summed every node on the map, which until §130 was the only settlement there was and is now the opponent's
  stores as well. It takes a faction now; null keeps every existing caller answering exactly what it did.
  Recomputing the figure inside the bot from a rate of its own was the alternative and it is the same risk as a
  second opinion about what can be seen: a bot reasoning in units nobody displays is a bot whose decisions
  cannot be argued with from the chair.
- **Trees it has seen.** Posting a cutter reads §131's knowledge, so the bot cannot send somebody to a trunk
  its faction has never had eyes on. That is the one cheat this whole arrangement exists to make impossible,
  and it is three lines because the knowledge was built first.

**And a cadence, because §7 makes AI difficulty an attention budget rather than a cheat.** The bot decides
twice a second. A bot re-deciding every tick would be spending attention no person has, thirty times a second
on a settlement whose fastest meaningful change takes seconds.

### The test is a settlement standing idle

Founding posts every hand at a producer, so a bot handed a founded village has nothing to do and an acceptance
test that proves nothing. `--twovillages --bot` therefore **strips its faction of every assignment** before the
bot takes over, which makes the claim the sharp one: can a rule-bot take a settlement that is standing still
and put it to work. The pass condition is the year leg's own — it feeds itself and the books balance.

```
faction 1 is driven by a bot, and starts with 13 people and no work

  f0 (founded, hands posted)  grain 3,480 | wood   811
  f1 (bot-driven, stripped)   grain 3,481 | wood 1,244
  the bot: 1,080 decisions, 128 assignments issued
```

Fifty-five days in, the bot's settlement is level on grain and ahead on wood. **Stated as observed rather than
as skill**: the founding posts a fixed four cutters and the bot puts whoever is idle onto wood, so the
difference is a policy difference and not evidence that the bot plays better. What the run does establish is
the thing worth establishing — a settlement can be run from outside, through the player's own interface,
without reading anything a player could not see.

Added as the gate's fifth quick leg, so the claim stays true.

### What this deliberately does not do

It does not build, train, defend, or know that another faction exists. Those are the next decisions and each
wants its own measurement — and putting them in now would mean shipping four untested policies inside one
untested loop. The loop is what this section is for, and it works.

The pieces §82's road asked for are now all present: two settlements, per-faction knowledge, and a player that
is not a person. What follows is contact — the bot noticing a neighbour, and the combat roster arriving when
there is something to fight over.

## 133. The opponent on the map, and the peace that was proved by having nobody to fight

Three things had been proved headless and none had been on screen: two settlements that feed themselves
(§130), per-faction knowledge (§131), and a bot that plays through the player's own verbs (§132).
`--village --opponent` founds a neighbour a third of the map away, `dressMap: false` so it does not re-forest
the first one's ground, and gives it to a `SettlementBot`.

```
  opponent: faction 1 founded at (286, 52), 198 m away, run by a rule-bot
  settlement: 34390 nodes, 26 people
```

### One seam closed before it could be used, and it was the player's

**Selection had no faction filter.** A marquee walked every body in the store, so dragging across a
neighbour's village would have handed the player its villagers and the right to order them about — and no
`IsAlive` test either, which was harmless only because a tombstone was the only thing it could catch. That is
the same class of cheat `SettlementBot` is built to make impossible in the other direction, except this one
would have been the player's to commit, and it existed for as long as there was nobody else's units to take.

`RtsGameLoop.PlayerFaction` now exists as a name so the places that must ask *is this mine* say so rather than
assuming the answer.

### And a cost that only appears when somebody is there

The frame is unchanged — 16.69 and 18.94 ms alone against 16.88 and 18.68 with the opponent, interleaved. The
**simulation tick is not**: 0.038 ms to **3.3 ms**, near enough a hundredfold. Broken down by phase:

```
threat 3.120 | economy 0.013 | knowledge 0.004 | steering 0.030 | ... | total 3.211 ms
```

**All of it is the defence layer, and §82 had already found and fixed this exact cost.** That section measured
"roughly 4.2-5.1 ms in the defence layer rebuilding guarded resources once per villager when no enemy faction
existed", and removed it by proving peace first — one faction comparison per body pair, ahead of any
guarded-resource search.

The proof is global: *does any pair of living bodies stand in an enemy relation, anywhere on the map.* With one
faction it is false and the cheap path runs. **With a neighbour it is true from the moment they are founded**,
so every villager rebuilds its guarded-resource list every tick for a war that is not happening a hundred and
ninety-eight metres away. The optimisation was correct and its premise was that there was nobody to fight.

**Not fixed here, and the reason is a question rather than a scope call.** Making peace local needs a radius,
and the obvious one is wrong: `ThreatMetres` is twelve metres, the distance at which a hostile is reaching for
a granary, and the cheap branch also decays resolve and clears quarries — so proving peace at twelve metres
would stop alarms being raised at any distance, and §30's muster picks defenders by *arrival seconds*, which
needs warning well before contact. The right radius is the largest range at which the full pass can still
change an outcome, and that is the defence layer's own number to state. The spatial index would then answer it
in O(neighbours) instead of O(bodies squared), and it is not currently passed to `ThreatSystem.Update`.

**What it costs today is headroom rather than frames.** The frame is GPU-bound at the played standoff so 3.2 ms
of CPU hides under the wait — but §128 measured that standoff at exactly 2.00 refreshes a frame, and a few
milliseconds either way is thirty against sixty. Spending three of them on a war nobody is fighting is the
wrong place for them to go.

### What a chair run is for

None of the above needed a person: it came out of founding the opponent and then measuring, which is what
§118's habit of pairing every change with its instrument is for. What still needs a person is the half a
fixture cannot answer — whether two settlements *read* as two, whether it is clear whose is whose, and whether
the knowledge asymmetry means anything perceptible. That is the next thing to do with it, and it is a
five-minute run rather than a section.

### Judged from the chair, and one line added because of it

Reported: *both settlements are readable, bot is well at work.* So the half a fixture could not answer is
answered — two settlements do read as two, whose is whose is clear, and the bot's work is visible as work.

The run also showed what a chair cannot answer: **faction 1 was named exactly twice in three hundred and fifty
kilobytes of log**, once at its founding. A neighbour standing idle and a neighbour thriving looked identical
from here, and §130 is the whole reason that matters — an inert settlement satisfies every impression of a
working one, and the only thing that caught it was a probe asserting on a number that had to move. So
`--timings` now prints the neighbour beside the routing:

```
OPPONENT faction 1: 13 people, grain 4,195 (22.0 seas), wood 1,000 (5.0 seas) · 13 decisions, 5 assignments
```

**A diagnostic and never a HUD element.** It reports the neighbour's stores, which a player has no business
seeing; it is behind a development flag and must not migrate into anything a person plays with. The bot's own
counters are the honest half — they say whether it is deciding and acting, which is the question being asked.

Frame from the chair, Release, as played: median 18.9 ms, p90 24.6, max 89.1, with the threat phase at a median
of 1.44 ms and a maximum of 3.84 — the cost §133 attributed, now confirmed by somebody watching it rather than
inferred from a fixture. Two hundred and sixty routing windows, every request answered.


## 134. Peace, proved at the range it can be disproved

§133 measured the cost of having a neighbour and left the fix as a question rather than a scope call: making
peace local needs a radius, and the obvious candidate was wrong. Twelve metres is how close a hostile has to
be to a granary; the cheap branch also decays resolve and clears quarries, and §30's muster picks defenders by
arrival seconds, so proving peace at twelve metres would silence alarms at every distance.

**The right range is stated by the code and not chosen.** `Question` applies exactly two filters: a body only
considers a resource within its own `SightMetres`, and only counts a hostile within `ThreatMetres` of that
resource. Together they mean **no hostile further than `SightMetres + ThreatMetres` can change any decision
the pass makes** — thirty-four metres for a villager. A hostile carrying loot is itself the resource, so the
same bound covers it at a gap of zero. And it is an upper bound, which errs the safe way: the full pass still
runs in some cases where nothing would have changed, and never fails to run in one where something would.

So the peace proof keeps its shape and gains a distance test.

```
                     threat    total tick
one settlement        0.008      0.038 ms
neighbour, before     3.120      3.211 ms
neighbour, after      0.008      0.143 ms
```

Back to what it costs alone. §128 measured the played standoff at exactly two refreshes a frame, so three
milliseconds of headroom is the difference between thirty and sixty — and it was going on a war nobody was
fighting.

### Verified against a baseline, because this is the defence layer

Gate 7/7 green with years, and that is not enough on its own: §40 established that a single run of a chaotic
simulation cannot distinguish a mechanism from a coin flip, and the raid leg's kill counts move run to run
inside exactly that spread. So the leg was run at three seeds against a build of the parent commit.

```
seed  17   before  13 home of 24   after  13 home of 24
seed  73   before  14 home of 24   after  14 home of 24
seed 211   before  14 home of 24   after  14 home of 24
```

**Identical on every seed** — the change is a cost removal and not a behaviour change, which is also the
strongest evidence the derived bound is right: a bound one metre too tight would let raiders through and the
numbers would say so.

**And it found something pre-existing.** Seed 211 faults on both builds: *a raider lived 200 s against a round
trip of 194 s.* Six seconds over, three per cent, on a seed the gate's default does not draw — so it has been
there and unreported. Not touched here, because a marginal outlier in a fixture is a different investigation
from the one this section is, and it is written down so the next person to see it knows it is not theirs.

The next thing is what §133's order set: the bot can maintain a settlement but never grow one, so an opponent
is a fixed tableau rather than a rival. Building and training are what make it one.

## 135. The bot's settlement, and a comparison that was mostly about the soil

§133's order put building and training next, so that the opponent would grow instead of standing still. A year
was run first to see what was actually stopping it, and the answer was none of the things that were about to be
built.

```
                    people   grain   work
f0 (founded)        13 -> 20  5,235   f8 w0 o0 i11
f1 (bot)            13 -> 13  2,036   f9 w4 o0 i0
```

**Housing was never the constraint** — unhoused stayed at zero for both, all year, so a house-building policy
would have changed nothing and looked like it had been tried. The bot fed itself and never grew.

### Two real bugs, found by measuring rather than by reading

- **A threshold with nothing below it.** The bot moved hands onto grain only when the larder fell under two and
  a half seasons, and a settlement founded with twenty-two seasons in store is nowhere near that — so everybody
  went to the wood and the larder drained until there was nothing spare to grow on. Replaced with the
  founding's own ratio, two thirds to the fields, with the thresholds overriding it in either direction.
- **A cart is not a spare pair of hands.** The bot employed its haulers in the fields, and the hauling board
  finds carts by their being *idle* — so nothing carried the crop in and it sat in the farm yards. The board's
  own comment says why the test is `HasCart` and not carry capacity: every villager carries now, a reaper walks
  its own crop in, so capacity marks nobody. Grain went 680 to 2,036 and the bot's own churn fell from 316
  assignments a year to 131.

### One fix that is correct and did not help

`NextField` indexed `fields[handsPlaced % fields.Count]`, which distributes an opening round perfectly and
then wraps — a hand re-posted later returns to field zero while high-numbered fields lie fallow. Replaced with
the least-manned field, counted from the bodies. **It measured slightly worse: 1,109 against 2,036.** Kept
anyway, and labelled: the wrap is a genuine defect and the replacement is correct by construction, but §40's
rule holds — one run of a chaotic simulation cannot tell a mechanism from a coin flip, and this is one run on
one seed. It is not evidence of an improvement and is not claimed as one.

### And then the comparison turned out to be about the ground

Every figure above compares f1 against f0, which is two drivers **on two different sites**. The valid
comparison is the same faction with and without the bot:

```
f1 without the bot   grain 2,700   people 10   f7 w0 o0 i3
f1 with the bot      grain 1,109   people 12   f8 w4 o0 i0
```

More people and less grain — neither unambiguously better, and nothing like the gap against f0. The reason is
in the founding's own report:

```
village A alone      8 fields at 0.70-1.10 fertility, median 1.02
A and B together    16 fields at 0.41-1.10 fertility, median 0.76
```

**B's soil is about half A's, by construction.** `ChooseSite` scores farmland, wood, backdrop and grade over
every eligible site on the map; `NeighbourSite` — written in §130 to put a second settlement somewhere — scores
flatness and nothing else. So the neighbour is founded on the worst farmland the geometry allows, and a section
that set out to measure a bot spent most of its runs measuring dirt.

**The same shape as everything else this session**: §120's fixture ordering a different village about, §122's
fidelity harness aimed at a world nobody plays, §130's acceptance test passing an inert settlement. An
instrument that differs from the thing it is measuring in two ways at once cannot attribute either.

### What this leaves

The bot is roughly competitive with a fixed posting on the same ground, which is the honest claim available.
Before build and train are worth adding:

- **`NeighbourSite` needs to score what `ChooseSite` scores.** Two settlements founded on incomparable ground
  cannot be compared, and every later claim about an opponent inherits the confound.
- **The founding's own posting leaves hands idle** — f0 ends with eight working and twelve idle, and its
  cutters stop when their trees run out and are never re-posted. The bot keeps everybody employed, which is
  the one thing it plainly does better, and it is worth knowing which of the two is right before either is
  taken as the standard.

## 136. One scorer, and full employment turns out to be worse

§135 owed two things before the bot could be given more to do. The first is done and the second has an answer
nobody would have guessed.

### The neighbour is founded the way any settlement is

`NeighbourSite` scored flatness alone, so a second settlement went on the worst farmland the geometry allowed.
It is now `ChooseSite` with two extra parameters — a site already taken and the band to keep clear of it — so
one scorer answers for both settlements and there is nothing to drift.

```
                          before            after
village A alone     median 1.02       median 1.02
A and B together    median 0.76       median 1.00
neighbour            198 m away        423 m away
```

The pair now sit on comparable soil, which is the whole point: two settlements founded by different rules
cannot be compared, and every claim about an opponent inherited that.

### And with the soil equal, the bot is worse

The same faction, the same site, one year, with and without the bot driving it:

```
f1 without the bot   grain 3,259   wood 187   people 10   f5 w3 o0 i2
f1 with the bot      grain 2,028   wood  38   people 10   f7 w3 o0 i0
```

**Seven hands on the fields produced less than five, and nobody idle produced less than two idle.** So §135's
closing line — *the bot keeps everybody employed, which is the one thing it plainly does better* — is
withdrawn. It was said on the strength of the bad-soil comparison, where the bot's extra people looked like a
policy win, and on equal ground the same policy is a loss on both resources.

That is worth stating as a claim about the *economy* rather than about the bot: **on this settlement, more hands
on the fields yields less grain.** Something about a field's output is not linear in the hands posted to it,
and until that is understood a bot cannot be tuned — every allocation policy would be tuning against a
response nobody has measured.

### The next measurement, named

Not another allocation guess. The jobs layer already counts legs completed per body, and what is needed is that
figure per faction and per field: whether the bot's seventh hand is walking rather than working, whether two
hands on one field halve each other's shifts, or whether a field's yield is capped by something other than
labour and the extra hands are simply eating. Those are three different mechanisms with one symptom, which is
§51's recurring shape, and the counter that tells them apart already exists.

Kept honest in the meantime: the bot is *competitive and behind* on equal ground, and nothing above should be
read as it playing well.

## 137. A decision reading state it was in the middle of writing

§136 left a question that no allocation policy could answer: seven hands on the fields produced less than five.
The measurement it named — hands, legs finished and reap progress, per field, per faction — answered it in one
run.

```
bot          [12h 0L 0%] [0h] [0h] [0h] [0h] [0h] [0h] [0h]
founding     [1h] [1h] [1h 66L] [1h] [1h] [1h 66L] [1h] [1h]
```

**Every one of the bot's field hands was on the same field**, and the other seven lay fallow. Not a walking
problem, not a shift-halving problem, not a yield cap — the three mechanisms that report was built to tell
apart are all innocent. It was a counting bug in the bot, and it was mine.

`QueueAssign` enqueues: nothing a decision issues has taken effect while that decision is still running. So
`LeastMannedField` counted hands from applied state, every hand in the batch saw an empty field zero, and they
all went there. The version it replaced had avoided this **by accident** — a running counter that wrapped, so
it spread a batch and then piled later arrivals onto low-numbered fields.

Which makes the whole sequence of §135 and §136 coherent at last:

```
counter that wraps            partial spreading    2,036 grain
least-manned, applied only    no spreading         1,109
least-manned, batch counted   full spreading       3,259
```

The fix is that the caller counts once per decision and the chooser increments as it goes. **A read-only count
of applied state is the wrong shape for choosing between several things at once**, which is the same mistake in
miniature as reading a figure back out of a structure you are still writing — and this project has now made
that mistake at three different scales in one session: here, in §126's stale binary, and in §128's cadence
estimator reading a deadline off the frames that were missing it.

### On equal ground the bot now matches the founding

Same faction, same site, one year:

```
f1 without the bot   grain 3,259   wood 187   people 10   f5 w3 o0 i2
f1 with the bot      grain 3,259   wood  38   people 10   f7 w3 o0 i0
```

**Identical on grain and on population.** §136's withdrawal stands — full employment is not itself an
advantage — but the shortfall it reported was one bug and not a policy difference, and the honest claim is now
parity on the resource that decides growth.

**The wood gap is open and not chased.** Both settlements have three hands on wood and the bot ends with
thirty-eight against a hundred and eighty-seven. It could be which trees each posts to, it could be spending,
and on one run of one seed it could be neither — §40's rule applies and it is written down rather than
explained.

### What is now worth building

The bot reaches parity by keeping hands employed on the right fields, which was the whole of item 2's
precondition. Build and train can be added against a settlement that is no longer losing ground for reasons
nobody has attributed — and the per-field report stays, because it is the thing that would say so again.

## 138. One figure for the whole world, where there is now more than one world in it

Item 2 was going to be build-and-train. §135's lesson said find the constraint before writing a policy —
housing had turned out never to be one, and a house-building policy would have changed nothing while looking
like it had been tried. So the first move was to read the birth condition rather than write against it.

A birth needs, per house: a valid supply bound to a live store, no privation, and growth accumulated at

```csharp
house.Growth += deltaSeconds * Readiness;
```

and one line above it:

```csharp
var held = nodes.TotalHeld();
var mouths = 0f;
foreach (var id in nodes.SettlementNodes) { ...; mouths += sink.AppetiteSum; }
Readiness = Population.Readiness(held.Grain, held.Wood, mouths, season);
```

`TotalHeld()` is every store on the map and `mouths` is every appetite on it. **Readiness was a single
world-wide number, and every house of every faction accrued births at it.** A rich neighbour subsidised a poor
one's children; a poor one's mouths slowed the rich one's.

The third of exactly this shape in three sessions: `Outlook` (§132), `UnhousedCount` (§135), and now this. It
is worth naming as a class rather than fixing three times, because **nothing about the code looked different
in any of the three cases**. Each was a correct total over "the settlement", written when there was one, and
each stayed compiling, stayed conservation-exact and stayed plausible once there were two. There is no type
error in summing the wrong set. The only thing that catches it is a second faction and a report that shows
both.

Fixed by clearing three per-faction arrays at the top of the pass, filling them from live nodes by owner, and
rating each house at its own faction's figure. The reported `Readiness` stays the player's, which is what
every consumer of it displays — a HUD saying "can feed one more" is answering about the settlement the person
is looking at.

**One semantic change came with it, deliberately.** `TotalHeld()` is documented as "stores and heaps, not
standing timber", and a heap on the road is `FactionId.None` on purpose — see the note at its creation:
"nobody's, which is what makes looting a thing that happens rather than a rule that has to be written". A
per-faction sum has to skip it, so **grain lying on the ground no longer counts toward anybody's readiness**.
That is arguably the better answer — a heap is not in the larder, and no catchment feeds off it — but it is
a change, not a refactor, and heaps are transient and small enough that the year gate stays green either way.
The alternative, attributing a heap to whichever catchment covers it, would make looted grain feed the
looter's children, which is a design decision and not this section's.

The determinism census caught the four new arrays by name before any test did, and they are Derived: cleared
and refilled from the node store before anything reads them, so a save that restores the nodes restores these.

**The test asserts the two figures differ**, which is the thing a single world figure cannot do. Two
settlements far enough apart that no catchment couples them, one stocked and one empty: the stocked one grows
1 → 3 at 100%, the empty one 1 → 0 at 0%. Before the fix this test could not have failed the right way round.

### And it changed nothing, which is how the real fault surfaced

Re-run over a year with the fix in: the bot's settlement still shrank 17 → 10. The fix was right and it was
not the constraint. What was, was sitting in a line of the report that had been printing for two sessions:

```
faction 0's site: 5425 walkable, 1136 refused within 20 m (792 marked solid), 0 deposits alive inside 20 m
faction 1's site:  975 walkable, 5586 refused within 20 m (5095 marked solid), 219 deposits alive inside 20 m
faction 1: 12 assigned (12 active), 2 nodes with hands, 2/7 sinks bound to a store
  unbound House at (-14,-178) <-> Granary at (-5,-185), 12 m apart: unpriced
```

Six of the bot's eight fields read `0L 0%` — hands posted, nothing reaped, all year. Not a policy fault at
all. See §139.

## 139. A village founded in a forest, and two layers each right about its own question

`IsBlocked(cell) => !Contains(cell) || GroundAt(cell).Blocked`, and blocked comes from one rule:

```csharp
public static bool IsPassable(TerrainSurface surface) =>
    surface is not (TerrainSurface.Impassable or TerrainSurface.Forest);
```

**Navigation treats a painted Forest biome as solid ground.** The bot's settlement was founded inside one:
5,095 cells solid within twenty metres, the 219 standing deposits being the trees of it, its granary
unreachable from a house twelve metres away, and six fields that could not be worked. The books balanced every
tick throughout, because nothing happened — §135's fault in a new costume, and the reason the acceptance test
now asserts that stores *change*.

`ChooseSite` scores grade over a five-point ring, water depth out to the field keep-out, woodland cover, soil
fertility and how the site sits against the map. It never asked whether the ground could be stood on.

This is the §51 family and the cleanest case of it yet, because **neither layer is wrong**. "Level, dry,
wooded, sheltered, fertile" is a fair description of good ground to found on. "A forest is not walkable" is a
fair rule for a path. Their conjunction is a village that cannot reach its own granary. Unlike the proxies of
§113 and the site scorer's own wood term, nothing here stood in for anything — the scorer simply never
consulted the layer that decides whether ground can be occupied.

My first explanation of why the *first* settlement never caught this was that its site is chosen before the
biomes are painted, on ground that is still all open. **That is wrong, and worth leaving in with its
correction, because it was a comfortable story.** Every path paints first: `RtsGameLoop` at 1741 before
`ChooseSite` at 1834, and `--twovillages` at 55 before choosing either site. `PaintCountry` *is*
`PaintBiomes`, which makes the call inside `Populate`'s dressing block a second paint over an already-painted
map — redundant rather than harmful, since it is a pure function of the terrain.

So there is no accident of order. Both sites are chosen on a painted map by a scorer that never asks about
passability, and f0 is simply not forced: it scores into ground that is 12% solid, while f1's exclusion band
narrows the field until the best remaining candidate is a forest. **The second settlement is where an
unguarded scorer bites, and the reason is exclusion, not sequence** — which also means the fault was always
reachable with one settlement, on a map whose good ground happened to be wooded.

`PaintBiomes` has no settlement keep-out anywhere in it, which is the source-level version of the same thing.

Two fixes, prevention first, and they divide the problem cleanly:

- **`MostlyWalkable` gates the site** — a 5×5 lattice over the keep-out square, three quarters of it passable
  or the candidate is not a candidate. It samples the terrain surface, not the navigation raster, for the same
  reason the grade test samples the height field: the raster is a reading of the terrain, and a scorer that
  consults it is asking a cache whether the world is walkable.
- **`ClearSiteSurface` clears the ground the settlers built on** — forest to grass inside the keep-out square,
  unconditionally, whether or not this founding dressed the map. The gate keeps a village out of a forest; this
  clears the copses inside ground it does occupy. Surface only, and deliberately **not** the trees standing in
  it: a tree holds wood in the ledger, `Remove` zeroes a node's stock, and removing 219 stocked trees would
  write their wood out of existence — which the conservation check would catch, correctly. Trees do not block
  navigation anyway, so a settlement founded in woodland keeps its timber standing where its cutters can reach
  it.

### What it bought

| | before | after |
|---|---|---|
| f1 walkable cells within 20 m | 975 | 3,172 |
| f1 cells marked solid | 5,095 | 2,687 |
| f1 sinks bound to a store | 2/7 | 7/7 |
| f1 fields reaping at harvest | 2 of 8 | 8 of 8 |
| f1 grain at winter | 3,824 | 6,338 |
| f1 people over the year | 17 → 10 | 17 → 17 |
| f0 people over the year | 13 → 18 | 13 → 22 |

The site itself did not move — it was copse-heavy rather than majority forest, so the gate passed it and the
clearing did the work. f0's numbers moved too, and that is §138: no longer slowed by mouths it does not feed.

**Both changes were needed and neither would have shown alone.** Per-faction readiness with the village still
in a forest reads as no change at all; clearing the forest without per-faction readiness leaves both
settlements rated by a shared figure. The order they were found in is the point: the fix that was *reasoned*
to was not the constraint, and running it anyway is what put the site line in front of me.

### Still open

- **Wood.** The gap (§137) narrowed at winter — 367 against 618 — and then closed the wrong way: by the end
  of the year **both settlements are at zero**, f1 finishing on more grain than f0 (5,136 against 4,829) with
  five fewer mouths. Wood is now a shared constraint rather than a bot fault, which makes it the next thing a
  build policy would run into: there is nothing to build with. It is still not chased.
- **`PaintBiomes` has no settlement keep-out**, which is why f0 carries 792 solid cells of its own — patched
  after the fact by `ClearSiteSurface` rather than prevented at the source. The source fix is for the paint to
  know where the settlements are, which means choosing all the sites before dressing rather than the reverse.
- **`MostlyWalkable`'s three-quarters threshold is a first number**, not a measured one. It now has a test
  that shows it does *something*: paint forest over the ground the scorer chose and the choice moves 62 m,
  from (-50,190) to (-110,175), still walkable — a scorer that ignores passability returns the same point
  both times, so the test can only pass because the gate exists. What the test does not establish is that
  three quarters is the right fraction. It admitted the bot's copse-heavy site, which was the right call given
  the clearing, and nothing yet exercises the boundary.

## 140. Nobody steering, a cache of stone, and the same trap for the third time

Three things, asked for together: the whole map left to rule-bots, a founding cache of stone, and a bot that
spends it on walls, a barracks and a few militia. The first two are small. The third found the same bug twice
more in one sitting.

### The cache

`Populate` already seeded 4,200 grain and 1,000 wood; it now seeds 600 stone. Sized off the recipes rather
than picked: a barracks wants 120 stone and a sound palisade wants 120 to become a stone wall, so 600 is one
barracks and four walls turned to stone. **The timber those same recipes want was already there** — a
barracks at 300 and four palisades at 60 apiece is 540 of the existing 1,000 — which is worth stating because
it was checked rather than assumed. The one quarrier is what replaces the cache afterwards, slowly, which is
the intended shape: stone is the material you plan around, not the one you run on.

### Nobody steering

`--bots` on `--twovillages` and `--handsoff` in the live game put a `SettlementBot` on *both* settlements.
There is deliberately no second implementation for the player's side: a bot that ran the player's settlement
differently would be measuring the wrong thing. It also makes the two settlements comparable in the way §136
wanted and could not have — same recipe, same cache, same driver, different ground.

### And then the third costume of §137

The build-and-train step, first version, re-decided everything twice a second against applied state. A hand
posted at a site has not arrived yet, so the count says nobody is there, so it posts two more:

| | first version | after |
|---|---|---|
| assignments issued in a year | 24,693 | 318 |
| militia ordered against a target of four | 28 | 4 |
| hands on the sixteen fields, all year | 0 | 7–9 and 3 |
| outcome | both settlements starving | both fed, books balanced |

§137 was one decision reading a tally it was in the middle of writing. §138 was a total over a set that had
grown a second member. This is the first again, and I wrote it having written that section — which is the
useful part of recording it. **The shape is: any rule of the form "how many are already doing X" is a
question about the queue, not about the world, whenever the asker is the one filling the queue.**

The fix is not a cleverer count. It is that the bot remembers its own orders: a per-site cooldown
(`LookAgainTicks`, ten seconds — long enough for a hand to cross a settlement) and a `militiaOrdered` counter,
because *a villager walking to the barracks is not militia yet*. That is also what a player does: post two
villagers and go look at something else.

### The two-way ternary, again, and this time the bot armed it

With the thrash fixed, faction 1 ran a whole summer with **zero hands on eight fields** while thirteen stood
in the woods. Not the same bug. The allocation rule was

```csharp
grainLeft < ShortSeasons ? true : woodLeft < ShortSeasons ? false : ratio
```

— a two-way switch that held up for exactly as long as the bot did not spend anything. **Buying a barracks
costs 300 timber, which drops the woodpile under the threshold in a single act**, and the switch then sent
the entire workforce to the trees, walking a settlement that had been feeding itself into a harvest with
nothing planted.

§71 named this shape when stone was added: a two-way ternary between two resources has no room for the third
case, and "both matter, one more than the other" is the ordinary case rather than the exception. So a
shortage now *leans* the split — 0.85 to the fields when grain is short, 0.35 when wood is — and always
leaves the other resource somebody. Faction 1 went from 0 hands on fields to 3 and stopped starving.

What is new is who armed it: §71's version needed a third resource to expose. **This one was exposed by the
bot acquiring the ability to spend**, which no amount of measuring the allocation rule in isolation would have
found. A policy layer is a load on every rule beneath it.

### What it produces

A year, two bots, nobody steering:

```
faction 0 built: 1 barracks, 1 palisade, 0 stone wall, 1 still going up; 4 militia standing
faction 1 built: 1 barracks, 0 palisade, 1 stone wall, 1 still going up; 3 militia standing
faction 0's bot: 5,400 decisions, 318 assignments, 4 site(s) laid, 4 militia raised
faction 1's bot: 5,400 decisions, 290 assignments, 4 site(s) laid, 4 militia raised
both settlements fed themselves, and the books balanced every tick
```

The stone was spent as designed — faction 1's palisade is a stone wall.

**The report says what is standing, not how many orders were issued**, and the run faults if a bot finishes
no barracks or has a barracks and no militia. §135's rule: "four sites laid" is a count of the bot's own
decisions, and a run that lays four and finishes none looks identical in the counters. **And the threshold for that assertion came from a measurement after I got the arithmetic wrong.** I set it at
nine tenths of a year on the reasoning that a barracks is 1,800 labour-seconds and two hands cannot spend that
in the 540 a tenth of a year allows — and the gate's own short leg then finished one with four militia
standing. So it asserts from a tenth of a year, which is every leg that has been observed doing it. The
arithmetic is not the measurement; that is the whole method of this file and I still reached for the sum
first.

### Seams left open on purpose

- **Placement is not a queued command.** The bot calls `AddNode`, and so does the player's build key — see
  `RtsGameLoop.Build`. Placing a site is not an order to a unit and there is nothing about it to interrupt or
  to survive a save: it happens between one tick and the next or not at all. What the bot does not get is
  ground the player could not use, which is why it goes through the same `Terrain.CanPlace`.
- **The bot's memory does not survive a save.** `lastPosted` and `militiaOrdered` live outside the world, on
  the RaidDirector's precedent (§132). A reload would re-order a garrison it already has. Cheap to fix when
  saves matter for a bot game; wrong to fix before, because the bot's state has no business in the save
  format until the bot is part of the game rather than a test driver.
- **`StoneWallUpgradeCost` is duplicated in the bot** because `StructuralProjects` only prices a project that
  has already begun, and the bot needs the price before it commits. A duplicated constant nothing checks is a
  constant that drifts, so there is a self-test holding the two together.
- **The lean leaves fields fallow.** Faction 1 spends the year wood-short, so 0.35 puts three hands on eight
  fields: the three it works reap 100% and the other five stand empty all year. It ends on 1,369 grain and
  loses five people over the winter while faction 0, on better ground with more wood, holds seventeen. That is
  the ratio behaving exactly as written and it is still probably wrong — a hand on a fallow field is worth
  more than the fourth hand in a wood line — but it is now a question about field assignment rather than a
  settlement walking into a harvest with nothing planted.
- **Wood is still the binding constraint.** Both settlements end the year at zero, now with buildings to show
  for it. This is where a build policy has to get cleverer or the wood economy has to change (§70's open
  item), and it is the first time the two are the same question.

## 141. A vocabulary, and a planner whose rules cannot re-arm the trap

The bot works and is unreadable as design: a fixed cascade of imperative acts in C#, where the shape of the
code and the shape of the intent have nothing to do with each other. What follows is the layer that separates
them, and the reason to build it is not expressiveness.

### The vocabulary, as it is

Four tiers, and the fourth decides how big the planner is.

**Standing purposes** — what a body is *for*, through `QueueAssign`, persisting until replaced:
`Work(node, resource)`, `Haul(source → sink, cargo)`, `Carry(anchor, cargo)`, `Build(site)`,
`Train(barracks)`, `Hold/Post(point, dwell, extent)`, `Shuttle(a, b, dwell)`, `None`.

`Hold` is the useful one: it **auto-converts** to `Work` or `Build` when its anchor is a node, so one verb
covers "work this" and "help put this up" and the difference is a fact about the building rather than about
the order. See `PostedOnAWorkSite`.

**Orders** — interrupts that expire: `Move`, `Stop`, `Follow(body)`, `Patrol(to)`, `Chase(body)`,
`Flee(body)`. A multi-body `Move` forms a cohort implicitly, so a sortie gets §107's formation and flow field
for free and needs no group API.

**Settlement acts** — not orders to units: `Place(kind, at)`, `BeginRepair(node)`,
`BeginUpgrade(node, StoneWall)`, `Despawn`. The bot and the player use identical paths.

**What the simulation owns, and no planner may touch:** who commits to a defence (§30's am-I-needed, §134's
bounded proof), the hauling board, births and emigration and privation, the field year rollover, felling,
catchment binding. All of it happens whether anything thinks or not.

So the planner's job is narrower than "play the game": **decide standing purposes, place buildings, commit
force.** That is the whole of it.

**Gaps the vocabulary has**, found by writing the list: no cancel for a placed site (a planner that changes
its mind strands timber), no rally point, no production queue, and — the one that matters here — **no
standing purpose for a soldier**. See below.

### Why the layer exists

Three bugs this session, one shape: §137's tally, §138's world-wide total, §140's re-decided projects. Every
one was an imperative rule asking *how many are already doing X*, which is a question about the queue and not
about the world whenever the asker is the one filling the queue. Two of them I wrote after recording the
first.

So the planner is **declarative targets reconciled every decision**, not a cascade of acts. A rule states a
target; it never issues a command. The framework computes

```
gap = target - (standing + ordered-and-not-yet-applied)
```

and owns the `ordered` half itself, per intent, with the cooldown inside it. **A new rule cannot re-arm the
trap, because a rule has no way to issue anything.** That is the argument for the layer, and it has nothing
to do with being able to express more.

### The four concepts

- **Reading** — census → number, string-named, and the rule is that it must be a figure the HUD shows or
  could show. Already the rule for `Outlook` (a bot reasoning in units nobody displays cannot be argued with
  from the chair); now the rule for the whole vocabulary. Computed from one census per decision, so every
  reading is O(1) and no rule can pay for a sweep.
- **Condition** — `reading op number`, and `all`/`any`. **No arithmetic and no state in a rule**: the moment
  a script can accumulate, it can re-derive a figure nobody displays and hold state no save captures.
- **Intent** — a target with a quantity that knows how to measure its own gap and close part of it:
  `Employ(resource, share)`, `Structure(kind, count)`, `Upgrade(from → to, count)`, `Garrison(count)`,
  `StaffProjects(hands)`, and later `Guard(anchor, radius)`.
- **Plan** — an ordered list of `when → intent`. Order is priority; the first unsatisfied gap gets this
  decision's hands.

Typed C# to begin with, every reading and intent string-named from the first line so a text plan file is a
transcription rather than a redesign. The parser is the least interesting part and the vocabulary will churn
through the first few designs; it is an afternoon once the plan is thirty rules long.

**Rejected: behaviour trees and utility scoring.** Both hide the firing reason behind a score, which is
exactly what this project keeps finding bugs by making visible. A priority list is legible; a utility
function is a number nobody can argue with.

### Two things in the framework rather than in the rules

- **The explainer.** Every command records the rule that produced it; the report prints which rules fired,
  which gaps stayed open, and what was issued. A plan that cannot say why is untestable, and every finding in
  this file came from an instrument rather than from reasoning.
- **Attention as the budget.** §7 makes difficulty an attention budget and not a cheat. Here that is
  literal: **how many gaps a plan may close per decision**, plus how often it decides. One knob, and no
  resource or vision cheat anywhere in it.

### What contact needs, which is one verb

Chosen as the first thing to express, and writing the vocabulary out is what showed the obstacle: **militia
have no standing purpose.** `Train` converts a villager and leaves it on `Assignment.None`, so militia stand
about until §30 commits them. Posture is inexpressible not because the planner is dumb but because there is
no verb for where a soldier belongs.

`Assignment.Guard(anchor, radius)` — a standing purpose for a soldier, exactly parallel to `Work` for a
villager:

- §30 keeps deciding who commits. Untouched.
- **A body that finishes a fight returns to its Guard**, the way `TrySendBackToWork` returns a reaper to its
  field. That is §30's open half — "what a committed defence does on arrival" — answered in the terms the
  jobs layer already has, rather than as a new mechanism.
- A rally point is a Guard anchor. A stance is which anchor and radius the plan picks. No stance enum, no new
  interrupt, no new command.

Then contact is readings over §131's knowledge — `enemy_known_within(m)`, `enemy_last_seen_seconds` — and
rules like `when enemy_known_within(140) → Garrison(8)`, `→ Guard(store, 40)`.

### Order of work

1. The layer, with today's five rules ported and nothing new. The proof is the existing gate plus a test that
   the ported plan issues what the hand-written cascade issued.
2. `Guard`, and the return-to-post path.
3. Contact readings and posture rules, which is the first time faction knowledge decides anything.

## 142. The layer, and what it found in the plan it was carrying

Stage 1 of §141: `Census`, `Reading`, `Condition`, `Intent`, `Plan`, `Planner`, `OrderLedger`, `HandPool`.
`SettlementBot` is gone; the settler plan prints itself:

```
plan 'settler' for faction 0:
  when barracks < 1 and projects_open == 0 and timber >= 300 and stone >= 120 → structure barracks 1
  always → staff projects 2
  when barracks >= 1 → garrison 4
  when barracks >= 1 and walls < 4 and projects_open == 0 and wood_seasons >= 4 and timber >= 60
      → structure palisadewall 4
  when stone >= 120 and projects_open == 0 and wood_seasons >= 4 → upgrade palisade->stone 4
  when grain_seasons < 2.5 → employ grain 0.85
  when wood_seasons < 2.5 → employ grain 0.35
  always → employ grain 0.67
  always → employ wood with the rest
```

The goal was to port the cascade with no new behaviour. **It did not port cleanly, and every divergence was
worth having.**

### The explainer earned itself in the first run

The first run of the layer: no barracks, no militia, 1,804 gaps left open — and the report said why without
any thinking on my part.

```
t17073 always → employ wood with the rest: gap 4, closed 0 (short)
t17073 always → employ grain 0.67: gap 12, closed 0 (short)
t17073 always → staff projects 2: gap 2, closed 0 (short)
```

Every rule firing, every gap real, everything closing nothing. That is not a symptom a hand-written cascade
would have shown at all; the cascade's version of this bug was a settlement that looked busy.

Three faults came out of it in order, and each was found by an instrument rather than by reading:

**1. `Employ(Wood, 1f)` is not "the rest".** A share of one is a target the size of the settlement, so the
gap could never close — and being an always-rule ahead of the structures, it spent the whole attention budget
every decision. `Employ.Rest` targets the remainder, which is zero at equilibrium.

**2. Rule order is the whole of what a priority list says.** The employment rules want every unemployed hand,
so anything after them is reached only once the workforce is placed. Projects moved above them.

**3. A share has to be a share of what the sharing rule can reach.** The census printed
`workforce 9 = 1 grain + 3 wood + 0 idle + 5 elsewhere` — five hands on a building site — and "two thirds on
grain" therefore targeted seven of nine and could never be met. So the employment rule pulled builders back
to the fields twice a second while the staffing rule pulled them back to the site: **537 orders in 540
decisions**, against about forty from the cascade. `EconomyHands` is now idle + grain + wood, and the pool
shrinking is how a higher-priority rule expresses its priority. 537 → 75.

The instrument that found the third one was one line — the census printed beside the gaps — and it is the
whole reason to make a planner explain itself.

### Two stories I told that measurement refused

Worth recording because both were plausible and both were wrong, and the wrongness was free to discover.

**"The attention budget made it aggressive."** The layer builds three stone walls in a year where the cascade
built one, and 4 gaps per decision looked like the obvious cause. A/B at 1 and 2 gaps: **identical
construction and near-identical population.** Over 5,400 decisions a budget of one is still ample. The knob
is real and it was not the cause.

**"It takes cutters to build, and that is what costs the wood."** Removing the wood tier from the hand pool
produced a **byte-identical run** — the tier was never reached, because idle or field hands were always
available. The comment claiming a measured effect was reverted rather than kept, and the tier stays as the
honest rule for something that outranks the economy rather than as a fix for anything.

### What the divergence actually was

Two plans on one map, which is the instrument this layer exists to be — the same settler plan with the wall
rules removed:

| at winter of year 1 | walled | unwalled |
|---|---|---|
| f0 | 16 people, 4,073 grain, 0 wood | **19 people, 5,148 grain, 169 wood** |
| f1 | 12 people, 3,689 grain | **17 people, 4,621 grain** |

The unwalled plan beats the hand-written cascade it replaced (17 at winter). So the layer costs nothing:
**four walls is more than this economy affords while it is also feeding itself**, and the cascade escaped it
only by being worse at building.

### The fix is a condition, and the resource in it is the finding

`wood_seasons >= 4` on the wall and upgrade rules. I tried `grain_seasons` first and the run came back
byte-identical: 4,200 grain in the founding cache is far above any threshold, and all the building happens
before it runs down. **A condition has to name the resource the act actually spends** — a wall is 60 timber,
a stone upgrade takes the hands that would be cutting, and wood is half of what §138's readiness is computed
from, so a woodpile spent on walls stops the births two seasons later where nothing connects the two.

| at winter of year 1 | no gate | wood-gated | unwalled |
|---|---|---|---|
| f0 people | 16 | 18 | 19 |
| f1 people | 12 | 17 | 17 |
| f0 built | 1 palisade + 3 stone | 1 palisade + 3 stone | nothing |
| f1 built | 1 palisade + 3 stone | **nothing** | nothing |

**The wood-rich settlement builds its walls and the wood-poor one declines to**, from one condition, on the
merits of the ground each was founded on. That is the first thing in this arc that reads as a decision rather
than as a schedule, and it is the argument for the whole layer better than anything I wrote in §141.

### Still open

- **`Guard` and the return-to-post path** — stage 2, and §30's open half.
- **Contact readings** — stage 3, and the first time knowledge decides anything.
- **The attention budget has no measured default.** Four is arbitrary and 1 and 2 behave the same over a
  year. It will start to matter when a plan has rules that compete, which this one barely does.
- **A plan cannot cancel.** Nothing abandons a site, so a plan that changes its mind strands timber.
- **`grain_seasons` is in the vocabulary and does nothing in this plan.** Left in as a statement of when
  building is wanted, with the measurement recorded next to it.

## 143. Guard, and the first decision a faction makes about somebody else

Stages 2 and 3 of §141.

### The verb militia never had

`AssignmentKind.Guard` and `Assignment.Guard(post, radius, dwell)` — a standing purpose for a soldier,
exactly parallel to `Work` for a villager, and it earns the parallel: **a rally point is a Guard anchor, a
stance is which anchor and radius a plan picks, and what a committed defence does on arrival is that it goes
back to its Guard.** No stance enum, no new interrupt, no new command. It is in `RepeatsForever` with `Hold`
and `Work`, so the jobs layer re-arms it with no special handover, and appended to the enum rather than
inserted because the save format writes it by value.

### Where the return-to-post actually lives, after two wrong homes

`StandDown` said "the danger has passed: let the jobs layer have the body back" and did not do it. A stop
halts the walk and leaves the interrupt standing, and an `InterruptKind.Order` never expires — by design, so
that a body sent somewhere stays sent. **This had gone unnoticed because militia had no standing assignment
to go back to**, so there was nothing for the bug to be visibly stopping.

Fixing it in `StandDown` looked obvious and does not fire: **§134's bounded peace proof skips every body
with no hostile in range**, so at peace nothing calls it at all. The optimisation is right and its premise is
that a settlement at peace has nothing to decide. Measured as a guard that left its post for a raider at the
granary and stopped 8.8 m short of home, for good.

The rule that works is stated on the assignment, in `ServeInterrupt`:

```csharp
if (jobs.Interrupt == InterruptKind.Order && jobs.Assignment.Kind != AssignmentKind.Guard)
{
    return JobRequest.None;
}
```

**A guard always goes back to its post.** Deliberately not "an order from the defence expires but a player's
does not": that puts provenance into a layer that has done without it, and it would be wrong anyway — a
soldier told to go and look at something is expected to come back, and that expectation is what a post *is*.

Test: a guard posted 14 m out leaves for a raider at the granary door and comes home to 2.6 m of its post,
still a guard. It fails without the rule. The first attempt put the raider 38 m out, where it is outside a
militia's sight, so §30 never committed anybody and **the body sat at its post the whole time looking like a
pass** — a test that cannot fail is worse than no test, and this one could only fail once the threat was
somewhere a defence would actually answer.

### Contact

One reading, parameterised: `enemy_seen_within(m)` — hostiles within m of the store **standing on ground this
faction is watching this moment**, gated on `FactionKnowledge.SeenWithin`. Per body and not per cell, which
is what makes it affordable at all: §131 measured `CanSee` per cell at 166 ms a tick, and there are a handful
of bodies. It deliberately does not remember: a last-seen-here is simulation state that has to be
fingerprinted and saved, and it is worth having when a plan has a rule that wants it.

Two rules, and no stance enum:

```
when barracks >= 1 and enemy_seen_within(120) >= 1  →  garrison 8
when enemy_seen_within(120) >= 1                    →  guard the store within 18m
when barracks >= 1                                  →  garrison 4
always                                              →  guard the store within 40m
```

**`Guard`'s gap had to grow a second half for this to mean anything.** Counted as "militia with no post at
all" it can fill a garrison and can never *change* one — a body already guarding at forty metres is not
unposted, so the tight rule would state a target it never reaches. It now also counts militia standing at
somebody else's post, which is what makes "a posture is which anchor and radius the plan picks" a true
statement rather than a description.

### What the contact test had to learn

Introducing the neighbour *first* proved nothing, and the way it failed is the interesting part: **an alarm
interrupts every villager in the settlement**, the census excludes interrupted bodies from the workforce for
§7's reason, and so there was nobody left to train — the contact run came back with no militia at all. The
claim is that contact *moves* a garrison, and a garrison has to exist before it can be moved. So: garrison at
peace, then the neighbour arrives.

```
at peace 4 of the garrison stand at 40 m and 0 at 18 m
with a hostile in view 2 at 40 m and 2 at 18 m
```

Two of four drew in, and the two that did not are the ones §30 committed to the fight — a body under that
interrupt is not the planner's to re-post until it is released. My first assertion demanded all four, which
contradicted the reasoning written two lines above it; the assertion was wrong, not the code.

**Both halves are asserted, because only the pair is a finding.** A plan that always drew its garrison in
tight would pass the first half alone, and a knowledge layer that leaked would pass the second by accident.
The two settlements in the headless year never see each other, so these rules would otherwise be code that
has never run.

### Found on the way

**Militia cost 30 wood and 30 stone each, drawn from the barracks**, and training is a two-leg errand whose
source and sink are both the barracks. A barracks conjured already-built and empty equips nobody: the plan
orders four militia and none appear. In a played settlement the timber carted out to raise it is what stocks
it — which is a quiet argument for the founding stone cache being sized the way §140 sized it, since four
militia is 120 of its 600.

### Still open

- **A last-seen-here.** Contact is "can see now"; a plan cannot yet say "an enemy was here recently".
- **`Muster`** — sending a force somewhere is `QueueMove` over a set, which forms a cohort for free, but no
  intent does it. That is the first offensive verb and it belongs with the roster.
- **A plan cannot cancel**, still. Nothing abandons a site.

## 144. Three sun modes, 3x in the chair, and a shadow cost that was already paid

Three asks from the chair, and the third one turned into a measurement that found nothing — which is the
result, not a failure to find one.

### The village runs at 3x

`DefaultVillageCompression = 3f`, and `DefaultCompression` stays at 1.5 for everything else. 1.5 was judged
on the slider in §3, when a settlement was a handful of hands and the longest thing worth waiting for was a
shuttle's round trip. **The unit of interest is now a year** — three windows of field work, a harvest, a
barracks going up, a garrison raised — and at 1.5 that is twenty-four minutes of sitting. At 3x it is twelve,
a day is forty seconds and a season is three to six minutes. Explicit `--compression` still wins, and every
headless scenario and performance case starts where it did, so no figure quoted anywhere in this file becomes
incomparable.

### The sun has three modes because it always had two inputs

`Atmosphere.For` takes an **hour**, from the sim clock, and a **declination**, from the date. There was one
switch across both. Splitting it along the seam that was already there:

- **DayAndYear** — what the game looks like played.
- **YearOnly** — noon every day, the year still swinging the sun north and south. At 3x a day passes in
  forty seconds, so the daily cycle is what makes two frames incomparable while the seasonal swing is the
  thing worth watching.
- **Fixed** — pinned to the two dials. For judging a material, a shadow, or a cost.

`SunFollowsTheYear` is now derived from the mode rather than stored beside it — two fields that have to agree
about the same thing are two fields that will one day disagree — and every existing reader kept working.
`--sun dayandyear|yearonly|fixed` exposes it, because **a mode that can only be set from the panel cannot be
measured**: comparing the three needs paired runs of a sealed binary, and `--perf-run` does not draw the
panel.

### The shadow question, answered with numbers

"Are we building more shadow maps than we need in any of these?" No. Three findings, in the order they came:

**The threefold redraw was already fixed and I had it recorded as unpaid.** `CascadeMaskAt`'s remark still
said "inside one of them it is drawn into all three, which is the redraw this does not yet fix". Not true
since the fitted boxes landed: `PartitionCasters` puts an instance only into the cascades whose box contains
it, and the per-cascade instance buffers are what make that sound. **A stale comment claiming an unpaid cost
is worse than no comment** — it was in my notes as work outstanding, and I would have gone looking for it
again.

**Skipping shadows at night is not available, and the reason is a deliberate look.** At night
`above = 0`, so the directional light becomes the *moon*: `direction = Lerp(moonward, sunward, above)` and
`moon = Nightfall.SunStrength * (1 - above)` at 0.52 against a day's 2.1–3.4. A fifth of daylight is visible,
and a moonlit scene with no shadows would be flat and wrong. The obvious win is not one.

**The cost barely moves with the light.** Per-cascade caster load, still camera at 118 m:

| | near | mid | far | total |
|---|---|---|---|---|
| midnight | 825k | 2,627k | 2,926k | 6.38M |
| dawn | 825k | 2,626k | 2,926k | 6.38M |
| 9am | 750k | 2,479k | 2,926k | 6.16M |
| noon | 733k | 2,447k | 2,926k | 6.11M |

Four per cent across the whole day, and across the three sun modes the figures are the same to within the
same margin. **Caster staging is fitted to the camera and the cascade boxes and has nothing to do with where
the sun is**, which is what makes all three modes cost the same. My first spot check suggested a fivefold
difference and that was proxies being off, not elevation — measured before it was believed, which is the only
reason it did not become a section.

**And the one signal that looked real did not survive a paired run.** `render_p50` came back 3.45 and 3.51 ms
at midnight and dawn against 1.95 and 2.18 at nine and noon — a 75% penalty for 4% more triangles, which is
exactly the shape of a real finding. Interleaved ABBA, noon/midnight/midnight/noon:

```
noon      2.57      midnight  4.35
midnight  3.37      noon      6.15
```

**Noon produced both the lowest figure and the highest.** It is machine drift, and every frame figure in that
batch had drifted to 30–33 ms by the end from a 13.5 ms start. §83's rule about this laptop earns itself
again: a single-run comparison here measures the thermal state of the machine.

So there is no shadow work to remove. The standing cost is the far cascade's 2.9M triangles a frame, it is
constant regardless of the light, and it is camera-driven — which is where any future work on it belongs.

`--timings` now prints `CASTERS near/mid/far = tri` and `SUN <mode> <elevation>`, so the question is
answerable from the chair in each mode rather than only from a performance case.

## 145. Water, judged from the chair: four faults and two of them were the model's

"Our water kind of looks bad." It had three things right — a level sheet, depth-based opacity for the
shoreline, and correct flat lighting — and was missing everything that makes a surface read as one.

**No specular, no Fresnel, no normal.** The wave trains modulated *albedo* by six per cent, so there was
motion with no surface under it. Making them a height field and differentiating them analytically (a
screen-space derivative of a function this smooth quantises to the pixel and reads as facets) gives a normal,
and a normal is what every other cue needs: a mirror has to know which way it faces. Then Schlick Fresnel at
F0 = 0.02, a sky reflection out of the `uHazeAway`/`uHazeToward` palette — which already tracks the date, so
a winter dusk reflects a winter dusk without this code knowing the month — and a glint.

**Everything about the old surface was view-independent, and that is exactly why it read as paint**: it
looked the same whether you stood over it or across it. Fresnel also closes the surface at grazing angles,
which fixed a second thing nobody had named: opacity was depth alone, so a lake seen across the valley was as
see-through as a ford.

### Then: flow, and the two model faults behind "creeps upstairs and vanishes into nothing"

```csharp
level[i] = max(standing ? filled : ground, ground + channel)
```

One line, both faults. A channel's surface is a **fixed depth above the local bed**, so it follows the ground
wherever the ground goes — up a hillside included, where 30 cm of water is a wet ribbon draped on a slope.
And `channel` was gated on a hard step at `TraceWidthMetres`, so a headwater does not thin, it *stops*: one
cell has a river and its neighbour has dry grass.

- **Confinement** — channel depth tapers to zero as the bed's own gradient rises, 6% to 22%. Both figures are
  *shallower* than the 11% §51 calls buildable, which is the sense check: ground a village would stand on
  happily is already too steep to hold standing water.
- **A headwater ramp** over an octave of width instead of a step.

**And the flow field had been sitting there since the solve with nothing ever asking it.** `Drainage.FlowAt`
returns direction and a speed off the two quantities a real channel's comes from — discharge and fall — with
the fall measured on the *filled* surface, which is level across a depression by construction, so lakes come
out still. The shader advects the wave phase downstream, packs crests along the current and stretches them
across it, and reduces exactly to the old crossed trains at zero flow.

It rides in the vertex **normal** slot, which is free because this mesh is flat and derives its own. Said at
both ends, because a field called `Normal` carrying a current is what becomes a bug two sessions from now.

### The legibility ask was a request to depict a fact

"At closer resolutions I should be able to tell what people can walk over and what they can't." The
simulation has always known: `Biomes` splits water into `Shallows` and `Impassable` at
`WadeableDepthMetres = 1.1`. The renderer never showed it. So the mesh now carries depth **in wadeable
units** — 1.0 *is* the line — and there are two cues, for two different lines:

- **A waterline film** within a hand's depth of the edge, where the water keeps almost none of its own colour
  and takes a pale rim. That is the contact that was missing; without it the sheet ends at an alpha gradient
  and reads as laid on top of the ground.
- **A narrow darker band at wade depth 1.0.** Narrow on purpose — it should read as the water deepening, not
  as a painted contour.

### While we were in there

**Forests were not a look problem.** `share = 0.12 + 0.34 × woodedness` is the fraction of map reaching
closed canopy, and closed canopy becomes `Biome.Wood` → `TerrainSurface.Forest` → **impassable**. It was
hitting **66%** on Downland, printed by the farmland report every run. Two thirds of the map unwalkable — and
that is the whole of why §139's second settlement was founded in a forest with 5,095 of 6,561 cells solid
around it. Now `0.10 + 0.20` with the ceiling down from 0.68 to 0.44: **66% → 42%**, median cover 0.97 →
0.21, and that site went 3,172 → 3,604 walkable.

**And the map is 480 m, not 600.** §3's crossing-time arithmetic was sound and done before anything lived
here; what a player waits on is the distance between settlements, which was 270 m of mostly empty ground.
Found on the way: **eighteen scenario extents hardcoded 600** rather than reading the default, so the gate
had been asserting about a different map than the game plays.

## 146. A low sun, and three causes ruled out by measurement

Trees casting "weird, clipped yet clearly misplaced" shadows across a plain they were nowhere near, at mid
zoom. Three candidates, each of which would have been a bug, each killed by a number:

- **The caster proxy is a floating plate.** It is not — apex at `bounds.Max.Y`, nadir at `bounds.Min.Y`, a
  bipyramid anchored on the ground. I said it was and had to withdraw it; I had read `waistY` and stopped.
- **Wind lean shears the caster.** Capped at seven per cent of height — 80 cm on a tall tree.
- **The far cascade's filter smears them.** Measured at zoom 200: cascades 124/208/299/406 m, texels
  **19.9/24.3/65.0 cm**, penumbra 3.2 texels. About two metres of blur — enough to soften an edge, nowhere
  near enough to stretch one.

What was left is geometry, and it was **correct**. Winter noon at latitude 37 is about 30°, so every shadow is
already 1.75× its caster's height — and the archetype in the HUD was `A HIGH SHELF ABOVE A LOW PLAIN`, so the
tree line was casting onto ground that falls away. On a slope approaching the light's own angle the length
runs off toward the horizon.

So the shadow **fades as the light lowers** rather than the geometry being falsified: full strength above 30°,
a third of it by 10°, never nothing — a long soft shadow at dusk is worth having. `uSunDir.y` is the sine of
the elevation, so it needs nothing passed down and cannot disagree with where the light is.

**And a played village now defaults to seasonal-only sun.** At 3x a day is forty seconds: the daily cycle is
a strobe, swinging through dawn and dusk twice a minute with nothing in the scene judgeable against anything
else. The seasonal swing is the half worth watching and the half the economy turns on.

## 147. The chequerboard was mine, and a lake with no basin

Two screenshots, three faults, two of them introduced by §145.

**A discrete field asked to interpolate.** `FlowAt` read the D8 `receiver`: eight directions, constant inside
a cell, jumping at every boundary. Sampled per vertex and interpolated across a water quad, the phase — a dot
product with the flow direction — swung from one quad to the next, and the lake came out as a lattice of
bright blobs the size of the mesh's own quads. It reads from the **gradient of the filled surface** now:
continuous, downhill by construction, zero across a lake, and sampled bilinearly like every other reader here.

**And the glint was a comparator, not a highlight.** Exponent 260 at a gain of 26 means every facet that
lines up blows to white and its neighbour is black — invisible until the normals started varying at all, at
which point it *was* the artefact. 90 and 3.2, clamped. The wave packing came down from 2.6 to 0.7 in the
same breath: crests a metre apart on a surface tessellated every few metres is a moiré arriving from the
other end of the same mistake.

**The disjoint sheet was in the log all along.**

```
water: 1.8 ha (684 m across, 12.0 m deep), 0.2 ha (237 m across, 0.9 m deep), 0.1 ha (158 m across, 0.9 m deep)
```

237 m across and 0.9 m deep is not a lake. A depression fills to its outlet, and on a gentle valley floor an
outlet a metre above the low point floods two hundred metres of ground a metre deep — an apron with no basin,
which then breaks into patches as the depth fade cuts in and out along its margin. Hence "clearly disjoint",
and hence "nothing about the ground suggests there could be water here", which was a true observation about
the ground.

The two existing tests ask how deep a body gets and how much drains through it. **Neither asks how much
ground it covers to be that deep.** `LakeBasinSteepness = 1/80`: two hundred metres across wants two and a
half metres somewhere in it, and the genuine twelve-metre basin in that same report passes easily. A ratio
and not a depth, deliberately — `PondDepthMetres` at 35 cm is right for a pond, and what it cannot express is
that 35 cm over 300 m is a flooded field. Measured after: water is 2–6% of the map across every archetype.

### Open

- **The sawtooth margin** on a shore, which is the mesh boundary following cell edges at the `wetEnough`
  threshold. A real artefact, untouched, and deliberately not changed in the same pass as three other water
  changes.
- **Whether 1/80 removed water worth having**, and whether a third of shadow strength at 10° leaves a winter
  afternoon looking unlit. Both are judgements from the chair and neither has been made yet.

## 150. Drainage first: inverting the terrain pipeline

Decided after a session of patching water: the generator is old, tuned for a vision the game has left, and
**it indicts itself in its own log line on every run.**

```
relief: 1 uplands, 3 troughs, 6 separators, 6 connectors, 2 woods;
amplitude 32.0 m, steepest flank 1.26 grade against a limit of 0.82,
undulation 2.0 m, fall 0.73 m per 100 m
```

- **`steepest flank 1.26 against a limit of 0.82`.** It generates flanks 54% steeper than
  `TerrainMap.MaximumTraversableGrade`, prints the violation, and nothing fails. Ground nobody can walk on,
  produced by the layer that knows the limit.
- **`fall 0.73 m per 100 m`.** Three quarters of a per cent of overall tilt. **This is the cause of every
  water complaint in §145–149.** With that little hydraulic gradient a depression's outlet sits barely above
  its floor, so a fill spreads wide and paper-thin — the 237 m × 0.9 m apron — and a channel has no
  confinement to lie in. Every fix in those sections was teaching the *solver* to reject what the
  *generator* should not have made.

### The structural fault

The order is: sample authored landforms (troughs, uplands, separators, connectors, coast, undulation, tilt) →
`Erosion.Carve` → basins → `Drainage.Solve` **discovers** the water. The landform vocabulary has no concept
of a drainage network, so water has to find a story in ground that was not built to tell one.

So the pipeline inverts. **Author the drainage network first, then build the terrain around it**: a valley
exists because a river does, rather than a river being looked for in a valley.

1. **An outlet and a channel tree.** Pick an outlet — a map edge, or the sea where there is one — and grow a
   bifurcating network inland. Each reach carries an accumulated area, hence a width and a depth, off the
   curve `Drainage.WidthOf` already uses. **Monotone downhill by construction**, which is the property the
   whole class of §145 faults came from not having.
2. **Valleys around the tree.** Height is the reach's own profile plus distance-to-channel times a valley
   slope drawn from the archetype — with the slope **capped at the traversable grade** except where a cliff
   is asked for on purpose.
3. **Interfluves from the existing vocabulary.** Uplands, separators and plateaus keep their jobs: they shape
   the ground *between* valleys, which is what they were always good at.
4. **Erosion for texture**, constrained so it cannot break monotonicity — the check being the invariant
   below rather than an argument about strength.
5. **`Drainage.Solve` still runs, but now it confirms.** The flow it finds should follow the tree it was
   handed, and *that is a testable statement*: this turns map generation from something judged by eye into
   something the gate asserts.

### What the terrain answers to

Three criteria, chosen from the chair, and each one assertable:

- **Walkability** — no ground steeper than the traversable limit outside deliberate cliffs, and a stated
  walkable fraction. The 1.26-against-0.82 line stops being a complaint and becomes a failure.
- **Coherent drainage** — every watercourse runs monotonically downhill to an outlet, no lake without a
  basin, no water on ground that cannot hold it.
- **Legible for play** — chokepoints, fords and valley routes that read as decisions. A ford becomes an
  authored wide shallow reach of a known channel rather than an accident of the depth field.

Not chosen: settleable sites. The site scorer already earns its living (§139's `MostlyWalkable` and the
farmland/wood/backdrop terms), and constraining generation to please it would put the same opinion on both
sides of the test.

### Order of work

- **A. The acceptance test first**, over the sweep the panel already drives: archetype × region × seed, with
  grade violations, hydraulic fall, lake plausibility, walkable fraction and drainage connectivity. It fails
  on today's generator — that is the point, and it is the baseline the new one is judged against.
- **B. The drainage-first generator behind a flag**, so both can be measured on the same seeds in the same
  binary. §84's rule.
- **C. Flip the default** when A passes on the new path, and delete the flag.

The reason for that order rather than building the generator first: every water fault this session was found
by an instrument and none by reading, and a generator judged from two screenshots is how the present one got
here.

## 151. The ruler, corrected three times before it could be trusted

§150 stage A: what a generated map has to be true of, measured across the 55 maps the panel can ask for.
`TerrainCriteria` holds three criteria — walkability, coherent drainage, legible for play — and the sweep
now prints them for every map and **ratchets**: the count may fall and may not rise. A gate leg going red on
the first run would have blocked every other piece of work until the whole terrain arc landed.

### What it found, on the fourth try

| fault | maps affected (of 55) |
|---|---|
| watercourses running uphill | **all 55**, between 6 and 643 each |
| fall below 2 m per 100 m | 46 |
| lakes with no basin | 34 |
| flanks over the traversable 0.82 | 16 |
| crossable ground in pieces | 3 |

**Every map has watercourses running uphill, and §145's slope taper did not fix that.** It reduced how much
water *sat* on a slope without making the level field monotone, and I had reported the complaint as
addressed. This is the single most-confirmed fault in the generator and the clearest argument for §150's
inversion: monotone downhill has to be a property of construction, not something a taper approximates
afterwards.

### And three of the four runs were measuring the wrong thing

Worth recording in full, because the pattern is the finding.

**`walkable 100%` on every map.** The sweep painted the biomes and never rebuilt the navigation raster, so
`IsBlocked` was answering about bare relief — on a generator §145 had just measured at 42% closed canopy.
An instrument that cannot see the fault it was written for is worse than none, because it argues the other
way.

**`steepest 3.46` was the rim doing its job.** `GradeLimit.Apply` caps the lattice at 0.92 of the traversable
limit *inset from the border*, because a map's edge is deliberately unclimbable. Measured inside the rim and
skipping surfaces that are impassable on purpose — crag, deep water — the worst flank fell from 3.46 to 1.67.
§150 had said "outside deliberate cliffs" and the first implementation ignored its own brief.

**`largest piece 221% of it`**, which is arithmetic rather than terrain: the island fill runs over the whole
grid and I had put the inside-the-rim count in its denominator.

**Fifty-five basinless lakes that were one river each.** `Drainage.Bodies` floods every cell whose level is
above its bed, so a connected river network comes back as a single body spanning the map diagonal at a metre
and a half — and asking whether *that* has a basin under it reports a fault on every map ever generated.
§147's rule is about depressions, so the criterion had to be too. **34 of the 55 remain and those are real**,
which means §147's rule does less than its section claims.

166 → 149 shortfalls, all four corrections being to the ruler and none to the ground.

**The lesson is not "write instruments more carefully" — it is that a measurement is a claim and gets checked
like one.** Every one of the four was caught by reading the numbers for plausibility rather than by review:
100% walkable on a map known to be two-thirds wooded, a share above one, a fault present on literally every
map. A number that indicts everything is usually indicting its own definition.

## 152. The drainage-first generator, first cut: 148 → 134 and the uphill fault survives

§150 stage B. `RiverNetwork` grows a channel tree from an outlet on the map's edge — a stem inland,
tributaries hung off it at junctions, **every reach's height set by its distance from the outlet along its own
course**, which makes monotone downhill a property of construction. `ReliefPlan.DrainageFirst` switches the
lattice construction and nothing else: the grade limit, the solve and the terrain write are extracted into a
shared `Finish`, so "the new path differs only in how the lattice was made" is a fact about the code rather
than a claim here. `--drainage-first` runs it, so both are measurable on the same seeds in the same binary
(§84).

The ground is then the lowest surface any nearby water can put under a point: `Floor` minimises
`water + shoulder * (1 - e^(-distance * flank / shoulder))` over the reaches. Water is confined because the
ground rises away from it in every direction — there is nowhere for a sheet to lie on a hillside, because the
hillside *is* the rise. The flank is capped well below the traversable grade, and the authored undulation is
scaled by distance from water so it can roughen a watershed without damming a channel.

**148 → 134 shortfalls.** Real, and nowhere near passing.

### Four hypotheses, three of them wrong

The dominant fault is unchanged: **watercourses running uphill, on every map, in both generators at roughly
equal rates** — including the one that is monotone by construction. So something between the lattice and the
criterion inverts it, and I guessed at it four times:

- **`Floor` mixed two criteria** — it chose the reach with the lowest *water* and then used *that* reach's
  distance for the rise, so neighbouring cells could take unrelated heights. A real defect, and worth one
  shortfall.
- **`GradeLimit.Apply` un-builds drainage.** It is a relaxation, so a gentle channel looked like exactly the
  pattern that would invert under it. Skipping it on the new path changed the count by **nothing**, and the
  switch was reverted rather than kept — an unmeasured change that makes the two paths differ is worse than
  no change.
- **Channels entering lakes.** A stream arrives underneath a lake's surface by definition, so every shoreline
  cell should have counted as uphill, and with two to ten pools a map that looked like the whole of it.
  Excluding them moved 149 to **148**. One shortfall.
- **The remainder is real** and belongs to neither generator's shaping in any way I have attributed.

So the next move is not a fifth guess. It is the attribution instrument — which cells, on which reach, by how
much, and what their receiver is — the same thing §116 did for routing and §132 for the click. Every finding
in this file that stuck came from one of those, and every one that did not came from reasoning about the code.

### What is honestly established

- The inversion is built, switchable, and measurably better on the same seeds.
- Fall is now an input: the new path asks for 2.5 m per 100 m of channel and the old one produced 0.21 to
  1.45 across the map.
- Basinless lakes went **up** on the new path, 1–5 becoming 2–11. Untouched and unexplained; the fill is
  finding more depressions in valley-and-interfluve ground than in eroded ground, which is plausible and
  unverified.
- Five instrument corrections across §151 and §152 against four generator changes. The ratio is the lesson.

## 153. Overlays, because the fault that would not attribute itself had no picture

§152 spent four hypotheses on 134 shortfalls and got three of them wrong, and the reason is plain in
hindsight: **nobody had ever seen one of the faults.** The count said "watercourses running uphill, on every
map"; it could not say which, or where, or next to what. Every finding in this file that stuck came from an
attribution — §114's click, §116's routes, §132's bot — and every one that did not came from reasoning about
code.

Four overlays on `MapTuning.Overlay`, and `--overlay drainage|standing|grade|blocked` so a look can be the
reason you start the game rather than something you go hunting for once it is up. Each uses **the same test
the matching criterion in `TerrainCriteria` uses**, so the picture and the count cannot disagree — including
the exclusions, so a channel entering standing water is left out of both.

- **Drainage** — one line per channel cell toward the cell it drains into, fading from pale to deep with
  discharge, and **red where the water rises downstream**.
- **Standing** — the margin of every pool, ringed blue where there is a basin under it and orange where there
  is not. §147 claimed to have stopped those, §151 found 34 maps of them, §152 made it worse and shrugged.
- **Grade** — ground over the traversable limit on surfaces meant to be crossed, inside the rim.
- **Blocked** — everything a body cannot walk on, whatever the reason.

First run of the drainage overlay on the played village, in one line:

```
drainage: 706 reach(es) drawn, 40 of them running uphill
standing water: 2 pool(s), 1 without a basin under them
grade: 0 sample(s) over the traversable limit on ground meant to be crossed
blocked: 2,009 sample(s) a body cannot walk on
```

**Forty of seven hundred and six**, and now they have positions. That is the number §152 could not attribute
in four attempts, and it took a frame's worth of lines to localise.

Drawn only within 260 m of the camera and capped at six thousand lines a frame — an overlay of a whole map is
a fog of lines, and a debug draw that costs a frame is a debug draw nobody leaves on. Reported to the console
on change rather than every frame, for the same reason.

`--drainage-first` also reaches the played game now, not just the sweep, so §152's two generators are
comparable in the chair as well as in a table.

## 154. The map builder gets the verdict, and the new generator is worse at the thing it was for

Tooling on the builder rather than on the ground, because §152 had two generators and no way to look at
either: the comparison lived in a fifty-five-map table produced by a sweep, and nothing told somebody looking
at *this* map whether it was any good.

**The verdict is on screen.** `LabVerdict` puts `TerrainCriteria` beside the seed that produced it —
`PASSES — walkable 96% …` or `FAILS — 73 watercourses running uphill; 1 lake with no basin`. Cached on
generation, because it walks the navigation grid and floods every pool: fine once a map, absurd sixty times a
second. Measured *after* `PaintCountry` and after a rasterise, which is the same mistake §151 made in the
sweep and had to correct — an unpainted, un-rasterised map answers "all crossable".

**The generator is a toggle and its shape is four dials.** `drainage first`, `channel fall (m/100m)`,
`valley flank (grade)`, `valley shoulder (m)`, `tributaries`. All four were constants inside `PlantRivers`
that only a rebuild could change, which is the §148 lesson restated: a figure that can only be changed by a
rebuild is a figure set by whoever is not looking at it. Null means "derive from amplitude as before", so the
sweep and the gate keep measuring exactly what §152 measured. The flank is capped against the traversable
limit whatever the dial says — a dial that can generate ground nobody can cross is a dial that will.

### And the first thing the tooling said

Same seed, same binary, same everything but the lattice:

| | eroded | drainage-first |
|---|---|---|
| walkable | 96% | **100%** |
| steepest grade | 0.67 | **0.40** |
| fall m/100 m | 6.61 | 5.30 |
| basinless lakes | 1 | **5** |
| uphill reaches | 73 | **144** |
| fords | 768 | 946 |

The flank cap does exactly what it was built to do. And **the generator whose whole premise is monotone
drainage by construction produces twice the uphill reaches of the one it replaces** — 144 against 73, on
ground where every authored reach is monotone by arithmetic.

So the premise is too weak, and this is the finding: **an authored network being monotone does not make the
terrain derived from it monotone along the paths the solver actually finds.** `Floor` takes the minimum over
reaches, which raises a divide wherever two valleys meet; the solver then routes water along that divide's
saddles, over ground shaped by two different reaches and by the interfluve undulation on top. Those paths were
never authored and nothing made them fall.

Which means the next step is not more shaping. It is either to constrain the solver's paths to the authored
network, or to make the criterion ask about the authored network rather than every channel the solver can
find — and deciding which is a design question about what a watercourse *is* here, not a tuning one.

Recorded plainly because it is the second time this arc that the thing I built to fix a fault made it worse
and the instrument caught it: §147's lake rule (§151 found 34 maps still failing), and now this.

## 155. The rivers run uphill because the water is stacked on the ground instead of cut into it

Option 2 of §154: ask the criterion about watercourses rather than about every channel the solver can find.
It half worked, and what it uncovered is better than what it fixed.

### The definition was too broad, and correcting it was right

The criterion faulted anything wider than `TraceWidthMetres` — 1.5 m, on a lattice whose cells are four. It
was holding the solver's answer about something narrower than one sample to a standard, and the solver finds
such a channel wherever a trickle of upslope area collects, including in interfluve undulation nothing
authored. A watercourse is now a channel **at least as wide as the cell it is measured in**, which is the
same reasoning `Biomes` gives for `FordableWidthMetres` and §148 gives for the render width gate.

### Two things I reported confidently and had to withdraw

**"Not one real river runs uphill on any map."** Wrong, and not from the data — from my own shell. I summed
the width bands with `awk '{n+=$5; m+=$8; b+=$12}'`, where `$8` is the literal `m,` and `$12` is `to`, both
coerced to zero. The line read `0 in 4–12 m, 0 over 12 m` because I had asked awk for the wrong columns.
Correctly summed: **4,592 uphill reaches in 4–12 m channels and 2,490 in channels over 12 m.**

**"The drainage-first generator produces twice the uphill reaches."** That was measured on the over-broad
definition. On the corrected one it produces 5,896 against the eroded generator's 7,082 — about 17% *better*,
not twice as bad.

Two corrections in one section, both to claims I had already handed over. The pattern across §151–155 is now
six instrument errors against four generator changes, and the errors are increasingly not in the instruments
but in the one-liners I read them with.

### And then the actual cause, measured

```csharp
var channel = width > TraceWidthMetres ? 0.30f * MathF.Sqrt(width) : 0f;
var inland = MathF.Max(standing[i] ? filled[i] : ground[i], ground[i] + channel);
```

**The water surface is the bed plus a depth that grows with width, and width grows downstream.** Where the
bed's fall across one cell is smaller than the growth in that depth, the surface climbs while the bed
descends. Measured: **6,181 of the 7,082 uphill reaches — 87% — have a falling bed and a deepening channel.**

So it was never a terrain fault, and neither §150's inversion nor §145's slope taper could have fixed it.
Both were addressing the shape of the ground; the fault is that water is **stacked on top of** the ground
rather than **cut into** it. It is also the same defect as "streams creep upstairs" from §145 — a surface
sitting above the terrain around it is exactly what you get by adding depth upward.

### What the fix has to be

A river's bed is incised: it cuts into the valley floor, and its surface sits at or below the ground beside
it. So `level` should be the valley floor and the bed should be `ground - channel`, which makes the surface
monotone wherever the terrain is, keeps the depth width-derived and physical, and puts water in the ground
instead of on it.

The catch is that the terrain height field *is* `ground`, so incising only inside the drainage model gives a
surface and a bed at the same height and no visible water. The channel has to be carved into the lattice
before the terrain is written — and the width that decides how deep to carve comes out of the solve, so it is
a two-pass generation: solve, carve, re-solve. That is a real change to the shape of `Apply` and is where
this goes next.

## 157. The dump, read properly: the surface steps up at every confluence

§156 built the attribution and then misread it twice, both times because the dump and the rule were not
looking at the same numbers.

**The accessors interpolate; the rule does not.** `LevelAt` and `BedAt` go through `Sample`, which is
bilinear — correct for a renderer asking about a point between cells, wrong for a criterion checking the
arithmetic of a per-cell rule. The dump reported beds falling one centimetre beside water climbing twenty,
and the contradiction was four cells averaged where the rule had used one. `Drainage.LevelField` and
`Drainage.Ground` are now exposed and the criterion indexes them directly.

**And then the two ends were measured differently.** With sampling gone, width appeared to *collapse*
downstream — 4.4 m to 0.3 m — which accumulated area cannot do. The near figure was `WidthAt`, which takes
the maximum of area-derived width and the authored corridor; the far one was `WidthOf(Area)`, which does not.
One definition at each end of the same comparison.

### What it says once both ends agree

```
climb 0.26 m at (48,-40): bed -3.21->-3.22 (falls 0.01), width 4.4->8.9 m
```

- channel here = 0.30·√4.4 = **0.629 m**
- channel next = 0.30·√8.9 = **0.895 m**
- level here = −2.581, level next = −2.325, climb **+0.256** — the reported figure, to the centimetre.

The arithmetic closes. **Width roughly doubles across a single four-metre cell**, and accumulated area can
only do that where a tributary joins. So the fault has a name: `level = ground + 0.30·√width` **steps the
water surface up at every confluence**, because area is discontinuous at a junction and the depth is stacked
on top of the bed rather than cut into it.

A real confluence does not raise the water. The channel below a junction is *deeper*, and its surface
continues to fall. Which is the same conclusion as §156 reached and did not reach far enough: incision has to
be sized on the width the rule uses — including the authored corridor — and applied so the *surface*, not the
bed, is what comes out monotone.

**One of the six worst does not fit.** `climb 0.17 m … width 9.5->9.6 m` is five millimetres of new depth
against a centimetre of fall, and should read as falling. Left named rather than explained: there is a second
mechanism in here and pretending otherwise is how the last five hypotheses went.

### The count that matters

Eight instrument corrections now, against four generator changes. Every one of the eight was found by an
arithmetic check — a share above one, a fault on every map, a width that decreased downstream, a climb that
its own components contradicted. **Not one was found by reading the code, and not one of my five hypotheses
about the underlying fault survived contact with a number.** The lesson this file keeps writing is the same
one: build the instrument, then check the instrument against arithmetic that must hold, and only then
believe what it says.

## 158. A hydraulic profile, and the third definition of "lake"

The fix §157's dump justified, and one more instrument correction of the same family it has been correcting
all arc.

### The carve now cuts a profile, not just a trough

`Incise` walks the cells from the headwaters down — sorted by filled height, so every contributor is settled
before the cell it feeds — and wherever a cell's water surface would sit **above its own upstream**, it cuts
the bed deeper until it falls. That is the confluence step from §157 answered at its mechanism: a real
junction does not raise the water, it deepens the channel.

Two details are the difference between this and §156's failed attempt:

- **Gated on accumulated flow, sized on `WidthAt`.** Carving wherever the layout drew a corridor invented low
  paths the solver's accumulation never justified and made everything worse. But sizing depth from area alone
  cuts an authored river to a fraction of the depth its own level will claim. So the gate asks "does water
  come through here" and the depth asks "how wide will the level rule think this is" — two questions, two
  sources.
- **A minimum fall of two millimetres per cell**, which is monotone without carving gorges out of gentle
  country to satisfy arithmetic.

**Uphill reaches 6,278 → 5,540.**

### And the criterion had "lake" wrong for the third time

The carve on its own took the sweep from 148 shortfalls to **154** — the ratchet caught it — because basinless
lakes went from 34 maps to 48. That looked like the carve creating hollows. It was the criterion:

| what it flooded | what that is |
|---|---|
| `Bodies()` (§151) | every wet cell — so a whole river network came back as one body |
| `LakeDepth` (§151's fix) | `filled - ground` at **every** cell, which every hollow on the map has |
| `Standing` (now) | the labelled bodies: cleared a depth, a catchment, and §147's basin |

Only the third is the set §147's rule governs. **A hollow the model has already refused to call a lake is not
a basinless lake**, and the carve makes hollows, so it was being blamed for faults that were never faults.

|  | before | after |
|---|---|---|
| eroded | 148 | **106** |
| drainage-first | 134 | **93** |

Remaining: 54 maps with uphill reaches, 17 short of fall, 7 with flanks over the limit, 3 with crossable
ground in pieces. **And the drainage-first generator is now measurably ahead of the eroded one** on criteria
that measure what they claim to — the first time that has been true.

### The tally, and what it is actually telling me

Nine instrument corrections against five generator changes. The pattern is stable enough to name: this model
has **near-synonyms for its central quantities**, and I have been caught by all three pairs —

- `LakeDepth` (fill depth anywhere) against `Standing` (a body that counts)
- `WidthAt` (area *or* authored) against `WidthOf(Area)` (area alone)
- `LevelAt` (bilinear sample) against `LevelField` (the per-cell array the rule wrote)

Each pair has one member that answers "what does the renderer see here" and one that answers "what did the
rule decide here", and a criterion is always asking the second. Every wrong hypothesis this arc came from
reaching for whichever name was nearest.

That is worth more than the fix: the next criterion written against this model should start by naming which
of the pair it needs and why.

## 159. The renderer's own near-synonym, and a default held back by the ledger

Two things asked for: make the drainage-first generator the default, and make the renderer draw the water the
model says is there. The second landed. The first is ready by the criteria and stopped by the invariant.

### The renderer had the same bug the criterion had, three times over

The water mesh drew a quad wherever `LakeDepthAt` was above zero — which is `filled - ground`, true of
**every hollow on the map**. So the broad shallow apron that §147's rule had already refused to call a lake
was still being drawn as one. That is "clearly disjoint, nothing about the ground suggests there could be
water here", reported from the chair three sessions ago and fixed everywhere except where it was visible.

`Drainage.StandsAt` asks the labelled set instead. The renderer now draws what the model says is there.

**Which makes four places that confused those two ideas**: `Bodies()`, `LakeDepth` twice in the criterion,
and the mesh. The pattern named in §158 was not a curiosity — it was load-bearing in every layer.

### The default is held back, and the reason is worth more than the flip

Drainage-first scores **93 criteria shortfalls against 106**, and on its terrain the two-settlement economy
**breaks conservation at tick 695: wood +1, stone −1.**

Not a leak — a **swap**. One unit of stone became one unit of wood. The terrain does not cause it; it exposes
it, and the eroded generator's terrain happens never to reach the path that does. A default that breaks the
invariant this entire simulation rests on is not a default, whatever it is better at, so it stays opt-in until
the swap is found.

**The lead, for whoever picks it up:** `Assignment.Build` hard-codes `Resource.Wood` as its cargo, and a
builder takes from `source.Stock[jobs.Assignment.Cargo]` then sets `jobs.Carrying` to the same. A barracks
wants 300 timber *and 120 stone*, and a palisade becoming a stone wall wants stone alone. Somewhere on that
path a unit is removed as one resource and credited as the other. Tick 695 is about twenty-three seconds in,
which is early enough that the founding's own construction reaches it.

### And a false step recorded

The first attempt to confirm the terrain was the cause ran `--twovillages --eroded` and got the same fault —
which looked like exculpating the generator. It was not: **`--twovillages` never reads that flag**, so both
runs used the new default. The isolation only worked once the property default itself was flipped. A flag
that does not reach the scenario under test is a control that is not controlling anything, and it very nearly
sent me looking in the wrong layer.

## 160. The map lab opened flat

Reported from the chair as "all maps are entirely flat", and it was true of every map the lab could make.

`--village` has had a relief default since it was written: no `--relief-amplitude`, and it takes 32 m. The
lab was never included in that line, so `--maplab` opened at **amplitude zero** — which takes `Apply`'s early
return, produces no landforms and no drainage at all, and presents a perfectly flat plane to somebody who came
to compose terrain.

Both scenarios take the default now, and the flag is an override rather than a requirement.

Two things worth keeping from how this one went:

- **It survived because nobody had used the tool for its purpose.** The lab has existed for many sections and
  is exercised by `--shapes` and the sweep, both of which pass an amplitude explicitly. The one path nobody
  took was opening it the way a person would.
- **It was mine, from this session.** I booted the lab with `--maplab --timings` and reported it as ready to
  compose maps in, having never checked that it had made one.

## 161. Pressure and momentum: depth from flow, and water that backs up

"Our water lacks pressure and momentum throughout the modelling", from the chair. It is the best diagnosis
anybody has made of this arc, and it names why every fix since §145 has been a patch.

### The three absences

- **Depth did not know about slope.** `level = ground + 0.30·√width`, and width comes from catchment area, so
  depth was a function of catchment size *alone*. A torrent down a one-in-five grade and a sluggish reach
  across a floodplain carrying the same flow got identical depth.
- **Nothing was conserved along a channel.** Real flow satisfies `Q = v·A`. Here width, depth and velocity
  were three independent guesses — width from a curve, depth from width, and velocity invented in §145 purely
  so the shader had something to advect. Nothing constrained anything, so nothing propagated. That is
  precisely what "no pressure" means.
- **No head, so no backwater.** Water never piled up behind a constriction or slowed onto a flat. A river's
  surface answering to what is below it *is* pressure travelling upstream, and there was no mechanism for it.

### What replaced it

Manning, for a channel wide enough that its hydraulic radius is its depth:

```
Q = (1/n)·w·d^(5/3)·√S     →     d = (Q·n / (w·√S))^(3/5)
```

Discharge from the catchment, slope from the bed's own fall, roughness a real constant at 0.035, and one
calibration — runoff per square metre, set so a full catchment's trunk runs about a metre deep. **A steep
reach now runs fast and shallow and a flat one slow and deep**, which is the character a river changes along
its length.

And `Backwater` walks the surface from the outlets inland, raising any reach whose water would sit below what
it drains into. **Note which way that goes.** §158 solved the same non-monotonicity by cutting the bed
*down* at confluences, which is a bulldozer's answer. Water's answer is to pond, and ponding above a narrows
is what a person watching a river actually sees.

### What it did

| | before | after |
|---|---|---|
| uphill reaches, eroded | 6,278 | **0** |
| uphill reaches, drainage-first | 5,896 | **0** |
| shortfalls, eroded | 106 | **50** |
| shortfalls, drainage-first | 93 | **38** |

**Zero.** The fault this arc has chased since §145 — through a slope taper, an incision, a profile carve, a
definition change and five wrong hypotheses — is gone, and it went without being aimed at.

That is the lesson, and it is worth more than the section: **every one of those fixes was an attempt to
impose by hand a property that falls out of a solution for free.** A surface computed pointwise from local
width cannot be monotone; a surface computed from conserved flow is monotone because it is a solution.
Monotonicity stopped being a goal the moment depth and velocity had to agree with each other.

Conservation clean, self-tests pass, gate 6/6, ratchet down to 50. What remains is terrain shape and not
water: 17 maps short of hydraulic fall, 7 with flanks over the limit, 3 with crossable ground in pieces.

## 162. Water that fills crevices instead of tiling them

"Water is trying too hard to be tiles instead of filling the crevices of the map like a liquid." Two causes,
and the first one meant no amount of mesh work could have fixed it.

**It was being compared to the wrong ground.** The mesh measured depth as `level - BedAt`, and `BedAt` samples
the **four-metre drainage lattice** — so a crevice two metres across does not exist in the field the depth is
measured against. The surface stays where the solver put it, because it is smooth by construction and belongs
to the lattice; what it is compared to is now `terrain.SampleHeight`, the ground a player can see.

**And its outline was the render grid.** Whole cells were emitted wherever any corner was wet, so the
silhouette was a staircase of quads — a picture of the mesh's own resolution. Each triangle is now cut against
the line where depth reaches zero, so the edge is a contour. Two triangles clipped independently rather than
sixteen marching-squares cases: the contour is identical and a case table is a place for one entry to be
wrong for a year. The crossing point has zero depth by definition, so its opacity is zero and the sheet fades
out exactly at its own silhouette.

The water step is half the ground's now, which always divides a chunk — the seam that note warns about came
from a step that did not. It was not worth paying for before; with the depth measured against real terrain and
the cells cut to the waterline, the mesh can follow a crevice, and at the ground's step it had nowhere to put
the vertices to do it.

Measured: `ground_p50` 0.09 ms for the whole terrain and water build, so the finer step costs nothing worth
naming.

## 163. The swap: a body's cargo is one resource and one count

§159 held the drainage-first default back because the two-settlement economy broke conservation on its
terrain — wood +1, stone −1, a swap rather than a leak. Found, and the finding is smaller than the hunt.

`EconomySystem`, the deposit path:

```csharp
deposit.Stock[resource] -= taken;
body.Jobs.Carrying = resource;
body.Jobs.CarriedUnits += taken;
```

**A body's cargo is one resource and one count.** So a hand holding one stone that is put on a tree comes
away holding *two wood* — the stone silently re-labelled. The ledger hears about the wood that was cut and
nothing about the stone, which is exactly what the discrepancy reported. The reaping path had the same shape
and is guarded too.

It needed two things at once: the bot reassigning a **loaded** hand (§140's `TakeHand` pulls workers off jobs
mid-load) and terrain putting an outcrop and a tree near enough together. Which is why drainage-first reached
it and the eroded generator never did across fifty-five maps — the bug was latent, not new.

### How it was actually found, and a new way to be wrong

Three call sites were guarded on the hypothesis, and the drift did not move an inch. Then a probe: snapshot
every body's `(Carrying, CarriedUnits)` each tick and, when the discrepancy first appears, name any body whose
resource changed while it was holding something.

```
RECLASSIFIED body 25: was 1 Stone, now 2 Wood; job Work cargo Wood leg 0
```

One line, one body, one tick.

**And one of those three sites was the right one all along.** The edit that added it contained two
replacements; the second assert failed; the file is written after both asserts; so the `AssertionError`
discarded the correct guard along with the failed one. The other half was re-applied on its own, the drift
did not move, and the hypothesis was written off. **The fix was right and the build under test did not
contain it.**

That is a worse failure mode than the near-synonyms of §158, because it does not look like a mistake — it
looks like evidence. Anything scripted that edits several places at once has to either verify each landed or
write each separately.

### And the default flips

| | shortfalls |
|---|---|
| drainage-first, now the default | **38** |
| `--eroded` | 50 |

Ratchet at 38, `--eroded` correctly reports itself as worse, conservation clean, self-tests pass, **gate 8/8
green with years**. Water is solved from flow, cut into the ground rather than stacked on it, drawn against
the terrain a player sees and clipped to its own waterline — and the generator that makes valleys because
rivers exist is the one the game now runs on.

## 164. The fall floor was wrong by two orders of magnitude, and the authored river stood still

Item 2 of §159's list: seventeen maps short of hydraulic fall. Every one of them was at one, four or seven
metres of amplitude, landing between 1.35 and 1.92 against a floor of 2.0.

**The floor was mine and its justification was wrong.** §151's note said two metres per hundred was "the
gentlest real lowland river valley". Two metres per hundred is a **two per cent** gradient — a mountain
stream. The Rhine falls about 0.05%, the Mississippi about 0.01%. The criterion was faulting lowlands for
being lowlands, and it was doing so an order of magnitude above the `MinimumChannelSlope` at which the water
model itself stops solving a channel and hands over to ponding.

Lowered to 0.5%, and kept only as a floor on *being a landscape at all* — because the question it was
standing in for can now be asked directly.

### Velocity, from continuity rather than invention

§161 solves depth from discharge and slope, so `Q = v·A` gives velocity for nothing. `FlowAt` returns that
now instead of the heuristic it used to make up for the shader, so **the speed the water is drawn moving at
is the speed it was solved at** — which is the third of §161's independent guesses finally retired.

The new criterion asks whether two thirds of a map's watercourses are moving, at a tenth of a metre a second.
A low bar deliberately: the fault it looks for is a map of standing water, not a map of slow water. Slow
water is most of England.

### Which immediately found what the proxy could not

Standing water clustered in one archetype: **DiagonalRiver, at 21% to 48% moving against 80% to 97%
everywhere else.** The archetype whose entire point is an authored through-river had the only river that did
not flow.

`PlantRivers` grows a network *with* discharge; `PaintAuthoredWidth` then painted the layout's corridor over
it *without* any. `v = Q/(w·d)` with a small Q and a large w is a wide, shallow, motionless ribbon. **Two
authorships of one river** — §158's near-synonym pattern in yet another costume. In the drainage-first path
the network *is* the authored river, so the paint is a second opinion and it loses.

**38 → 19 → 4 shortfalls.**

Unmeasured and not claimed as a fix: the eroded path's inherited catchment now agrees with its authored width
(inverting `WidthOf`, since 400,000 m² supports a five-metre stream while the layout paints far wider), and
that constant is expressed in the map's own extent rather than the 600 m the map used to be. It had no
measured effect, because drainage-first is the default and that path did not run.

## 165. The ways up, and it was the rim's toe all along

The last four shortfalls were one archetype, `CentralHighGround`, whose own description is "four sides around
one defensible hill": flanks of 2.37 and 2.48 against a traversable 0.82, and eleven to twelve per cent of
crossable ground stranded.

**Three hypotheses, all wrong, and two of them acted on.**

- **The ramps leave a lip.** `1 - 0.80 · Opening` removes at most four fifths of an upland's lift, so every
  authored way up ended in a step of the remaining fifth — eight metres of it on a sixty-metre map. Changed
  to open fully, which changed *nothing* measurable. Kept, because four fifths of the way up is not a way up.
- **My own §158 profile carve running away.** It lowered any cell whose surface sat above its upstream by an
  unbounded amount, and propagated, so on flat ground it could trench. Changed nothing measurable.
  **Removed anyway, and that one I would defend**: §161's Manning depth plus Backwater makes the surface
  monotone as a property of the flow, so the carve was a second mechanism doing the same job with earthworks.
  Two mechanisms for one job, and the physical one wins.
- Both were water hypotheses. It was not water.

**It was the rim.** `steepest 2.37 at (96,-160)` — on a map spanning ±240, that is eighty metres from the
edge, and the criterion skipped exactly `RimWidthMetres`, seventy-eight. The rim's inner edge wanders inward
by up to `±0.275 · RimWidthMetres`, so its toe reaches ninety-nine metres in. **I was measuring the boundary
wall two metres inside the band meant to exclude it.** And the island fill ran over the whole grid, so the
map's edge was shattering the map's interior on paper.

`RimReachMetres` covers the wander now, and the island fill is confined to the interior. **4 → 2.**

### Where it stands

Two shortfalls across fifty-five maps, both `CentralHighGround`, at the extreme amplitudes:

```
steepest 2.03 at (120,-93) on Shallows, water there 0.24 m
steepest 2.48 at (116,-116) on Heath, water there 0.00 m
```

Attributed to a position and a surface, and *not* to a mechanism. One is a channel bank and one is dry
ground, both genuinely interior. That is a fourth hypothesis away and it is not being taken: the position and
the surface are written down so whoever picks it up starts where the evidence is rather than where the
plausible story is.

## 166. Attack, which is one verb because its end condition is one sentence

From the chair: muster is not a verb, it is a family of them, and the atomic one is **attack a particular
thing** — done when the thing cannot be found or seen any more, or is already dead. Buildings, people,
everything. Siege, blockade, raid and skirmish come later and are compositions: attack-this or guard-this plus
a rule about which target, what next, and when to leave, driven by the roster, the economy and the moment
("in winter I might raid, get out, and live off it for a while").

That is exactly right, and building it showed why.

### Almost all of it already existed

- **Bodies already fight when adjacent.** ThreatSystem's proximity rule, with §143's crowding limit. An
  attack order needs no combat of its own for people.
- **`Guard` already says where a soldier belongs**, and §143 built the path home.
- **A multi-body move already forms a cohort** with formation and flow field.

So the verb is its **end condition**, and that makes it an `Assignment` rather than an interrupt: what a body
is *for* until satisfied, after which the Guard takes it back with nobody issuing an order. One verb for
buildings and bodies, because the difference is a fact about the target and not about the order — and the
two live in separate fields rather than one "target of some sort", which is the shape that cost this session
nine corrections.

**Not called `Muster`.** `ThreatSystem.Muster` already means "how much strength can reach this place in
time": a query, defensive, automatic. Using the name would have manufactured the tenth near-synonym on
purpose.

### And nothing had ever damaged a building

`DamageStructure` has existed as long as repair has. Its only caller was a self-test. So §71's stone walls,
the condition bar and the material costs were a whole ledger for undoing damage that could not happen. A body
under an attack order that has *arrived* now takes a structure's condition down at its own strength — stated
only for bodies under orders, because a militia walking past an enemy granary should not knock it down by
proximity and a besieger standing on it should.

### Four faults getting there, and three were one pattern

- **`DwellSeconds: 0`.** A leg with no dwell finishes on the tick it begins, so an attacker spent every tick
  being re-aimed and none *standing* at what it was hitting. It chewed a palisade at a fraction of its
  strength and never broke through. One second is a swing.
- **A contact test of my own.** `FootprintRadius + radius + slack`, while the jobs layer stops a body by a
  distance to the building's *box* with its own slack and raster reach. Two notions of touching the same
  wall, and the tighter one won: a soldier stood at the palisade all day and did not scratch it. It asks
  `JobSystem.IsWorking` now — the predicate the economy already uses for "this hand has arrived".
- **`default(AgentId)` is agent zero, and agent zero is somebody.** An attack on a building was never
  released, because its end condition asked whether the body target was still alive and the first villager
  ever spawned usually is. `AgentId.None` is −1 now, as `NodeId.None` and `FactionId.None` already were.
- **"Dead" for a building is not removal.** `DamageStructure` floors the condition and leaves the node
  standing, because repair has to have something to repair. Gone means a ruin, not an absence.

Three of the four are the same fault: **two names for one idea, and I reached for whichever was nearest.**
`LakeDepth`/`Standing`, `WidthAt`/`WidthOf`, `LevelAt`/`LevelField`, and now contact/contact and
none/zero. It is the defining failure mode of this codebase as I have experienced it, and the defence is the
one §158 wrote down: name which of the pair you need, and why, before writing the line.

The test asserts all six properties of the sentence — a wall chewed, held while it stood, flattened, the
attacker released; a body held while it lived and let go when it died. The building half could not have been
written yesterday.

## 167. Loot, and the question of how much

Loot is the second offensive verb, and by §166's test it is a verb: **see a store that is not yours, take a
load, carry it home.** The end condition is one sentence, and the same three exits close it — the store is
gone, the store is empty, or the load is home.

The mechanics fell out of a question about interaction rather than about code. A raid is not a command in
this game; **raiding is what looting plus attacking plus a retreat look like when you do them well**, which
is the answer §166 already gave for siege and skirmish. So the only thing loot itself had to decide was how
a store gives up what it holds, and there were two candidates:

- **A pillage timer.** Stand on it for N seconds, receive a lump. It reads as a capture, and it makes the
  haul independent of who is standing there — which means the roster cannot say anything about raiding.
- **Leaking on contact at the hauling rate.** A body in contact takes from the store exactly the way a body
  in contact with a heap takes from a heap. Chosen. It reuses the economy's only transfer primitive, so the
  ledger balances with no new accounting, and **it makes carriers the unit of theft**: hands, capacity and
  walking speed are already what decide how fast grain moves, so they decide how fast it is stolen too.
  Militia carry nothing, so militia cannot loot — that falls out, it is not a rule.

### Laden bodies

Carrying now costs speed, but not linearly: **a body under half its capacity walks at full pace, and past
that loses up to a quarter.** The threshold is the point. A linear penalty would tax the whole economy — a
villager with one sheaf in hand would be slower than one with none, forever, everywhere — and taxes that
apply always are not decisions. The threshold means an ordinary carrying trip is free and only a *full* one
is slow, so "get out heavy" and "get out fast" become different things to want.

It applies at both speed sites: the preferred velocity, and the formation-flow path, which do not share a
line (§107's cohorts read the field, singletons do not).

### And how much is not on the order

I first wrote loot as **take a share of capacity**, with `loot(store, 0.5)` as the winter raid and
`loot(store, 1.0)` as greed, and asked how a player would say it. The question killed the design, correctly:

**no order in this game has ever carried a number.** Work that field. Guard that post. Attack that thing.
Build there. Every one points at a thing, and the only magnitudes anywhere are in the *planner's* rules —
garrison four, four walls, employ two thirds. Quantities live in plans; clicks point at things. A share on
the click would have been the first scalar a player had to express, and there is no honest gesture for it:
a modifier key does not say "heavier", and nobody types 0.5 into an RTS.

So how much comes off the roster instead.

- **How much in total** is how many carriers you sent — which is how players have always expressed
  magnitude in this genre, and it needs no interface because selection already is one.
- **How much each** is what that body can hold. Villager thirty, raider forty.
- **Whether you stay** is whether you issue it again.

Which turns the laden threshold from a knob into a **design axis for units**: a high-capacity body is a slow
thief and a light one is a quick thief, and a scout that carries ten at full pace is a genuinely different
raider from a bearer that carries sixty and crawls. That is a space to put units in later, not a widget.

The leave condition is therefore the simplest one available: **one load, then home, then done.** The first
version came back until the store was dry, which made staying the default — a column stood in a stranger's
granary because nobody had said stop. Leaving is the default now, and staying is a decision taken again,
which is what "raid in winter, get out, live off it a while" actually asks for.

The self-test asserts the property, because it is the kind of thing that quietly becomes greed the next time
somebody edits a handover: a store holding 240 loses **exactly thirty** however long the run goes on, all
thirty arrive, the carrier is released, and the ledger drifts by nothing.

Two failures came out of building it, both mine and both familiar:

- **An unguarded dereference.** A laden raider now satisfied a filter it never used to reach, and the test
  that hands are freed before a fight read an agent that was not there.
- **A budget that forgot the return leg.** The raid round-trip assertion priced three legs at full pace when
  the third is now laden — 196 s measured against 190 s allowed. The budget derives the slowdown from
  `LadenSlowdown` rather than restating it, so the next tune to the constant moves the assertion with it.

### The click, and the raid that nobody commanded

Both verbs were reachable only from tests until now, so the interface answer had to be built as well as
argued. It needed **no new key**. The right-click was already contextual — a work site posts hands, a
barracks trains them, ctrl on a wall repairs or upgrades it, bare ground moves — so hostility joins that
list as the first test rather than a new gesture: **a hostile node takes none of the friendly branches**,
which also fixes the absurdity of pointing at a foreign barracks and training your villagers inside it.

And *hostile* is not *not mine*, which is the mistake I wrote first and wrote twice — once in the command
and once in the hint describing it. `node.Faction != mine` is wrong in both directions: unowned timber is
not mine, and an **ally's** granary is not mine either, so the obvious spelling offers to rob your friends.
`FactionRelations.Between` has answered this since the threat system needed it — `None` on either side is
neutral, and diplomacy overrides win — so there is now one `IsHostile(hands, node)` on the world that both
callers ask. The acting faction comes off the hands, not from a notion of "the player": the simulation has
no business knowing who is holding the mouse. A test pins all four cases, because the two it rules out are
exactly what the naive version got wrong.

What happens then is decided by the party you sent, not by a modifier:

- Hands that can carry rob the place. The hint counts their capacity and says how much, because with no
  number on the order the selection is the only place a player can read the size of what they are about to
  take — so the panel doing that arithmetic is part of the design rather than decoration.
- Hands that cannot fight it.
- An empty store has nothing to say to carriers, so everybody attacks it — pointing at a stripped granary
  should do the obvious thing rather than nothing.

Which means **the raid falls out of one click and was never implemented as a command.** Send militia and
villagers together at a granary: the escort starts breaking it while the carriers take a load and leave.
That is exactly the composition §166 promised for siege and skirmish, and it arrived for free because the
verbs were kept small enough to combine. Nothing about "raid" appears in the code.

It also earns the one thing an interface like this must earn: **the answer to "how much" is legible without
being stated.** More carriers is more grain, heavier carriers is slower carriers, and both are visible in
the selection you made before you clicked.

### And a third sighting of the sentinel

Building it turned up the trap §166 named, in a new place: `AddNode` resolved an unspecified owner to
`new FactionId(0)` — and faction zero is the player. Every tree and outcrop on every map was nominally
player property. It had cost nothing, because deposits are found by `IsNaturalDeposit` and never by owner,
but it made "not yours" mean the wrong thing the moment a right-click started asking. Unowned kinds now
resolve to `FactionId.None`, and the kind predicate lives in one place with `IsNaturalDeposit` deferring to
it, because a second spelling of that list is how this fault reproduces.

Three sightings now — `AgentId`, `FactionId`, and `NodeId` before them. The rule is worth stating flatly:
**`default` of an id type in this codebase is a real entity, so every id type needs its `None` and no default
may stand in for absence.**

### What a raid is actually worth, which is where the roster's debt shows

Priced from the constants rather than guessed at, because "is one load worth the walk" is arithmetic. A
villager walks 1.79 m/s, carries 30, eats one grain a day, and a full load costs it a quarter of its pace;
a year is 5,400 s over 270 days.

**Villager looting breaks even against farming at 168 m.** Closer than that and a body steals faster than
a field feeds; further and it should have stayed home. The maps generate neighbours at 171 m and 272 m — so
as played, looting with villagers is at or past the line before anything else is counted.

And something else has to be counted: **a party eats while it walks.** Eight villagers at 272 m are away
17.7 game-days, bring 240 grain, and eat 142 of ours doing it — **net 98**, six days of food for fifteen
mouths. Against a 67-day winter costing 1,005 grain that is 10.2 trips and **181 game-days of walking to
cover 67 days of winter.** Villager looting cannot be a livelihood. It is opportunism, and the 168 m figure
says exactly how local it has to be.

**What closes it is a body with a big pannier and no stomach.** Take `UnitType.Raider`'s numbers as a
worked example rather than as a unit — carry 40, speed 2.05, `Appetite: 0f`. Eight of those at 272 m are
away 15.5 days, bring 320 and eat nothing: **net 320, three trips, 49 game-days of walking against 67 days
of winter.** That closes with room to spare, and it closes without a tuning pass.

So the sensitivity is worth naming, because it is what the roster will be choosing between: **appetite
dominates at range, and capacity dominates near home.** A party that eats is paying for the walk twice, so
the further the target the more the upkeep matters; close in, the trip is short enough that only the size of
the pannier does. A raiding body is therefore not "a villager with a sword" — it is a body that is cheap to
have away from home.

Which is the evidence that §167's interface answer was right rather than merely tidy. The identical click is
a losing errand with one body and a winter strategy with another, and a player expresses the difference
entirely by selection. No share, no modifier, no number.

**Correction, and it matters because it was written down as a direction.** `UnitType.Raider` is not a roster
entry and was never meant to be one — it is the fixture body `RaidDirector` and the fight benchmarks spawn
at the settlement, and I read it as a unit because its numbers happened to fit. "Start the roster by making
raiders fieldable" was therefore advice about a placeholder. The measurement is unaffected: villager looting
still breaks even at 168 m and still cannot carry a winter. What it licenses is narrower — **the economics
of a raiding body are known before the roster designs one**, which is a constraint to design against, not a
unit to ship.

## 168. Rigged bodies, and two bugs in the seam between accumulating and drawing

Reported from the chair, and precisely: "A pose people floating around and standing on spots for a while is
getting increasingly meaningless to read." Three complaints in one sentence, and they are not one problem —
the A-pose is an asset, the floating is a missing gait, and standing on spots is work having no visible
action. Only the first is content.

**The engine was never the gap.** The whole skeletal stack was already here: `GltfImporter` reading skins
and inverse-bind matrices, `Skeleton`, `Pose`, `BonePalette`, `AnimationClip`, blended and additive clips,
`AnimationHost`, `SkinnedGameObject` — and two demos already rendering skinned characters, one of them with
a bone-palette SSBO and a skinned shadow pipeline. What the settlement had was `Villager.obj`, whose own
header reads `# Blender v2.76 OBJ File: 'Animated Human.blend'`. **The rig existed in the source and the
export format discarded it.** The consequence was even written down at the load site: "this is the one
asset with no rig, so a villager slides rather than walks."

The float was checked before being believed: feet sit at the pivot (y −0.006 of a 5.26-unit mesh), so
nothing was hovering. A body sliding at 1.79 m/s with motionless legs simply reads as hovering.

### The decision that collapsed

The choice offered was prove-readability-first (a draw per body, bake later) against building instanced
skinning up front. It was answered, and then it dissolved: **the palette can hold every body's bones end to
end, and a body finds its slice at `gl_InstanceIndex * bonesPerBody`.** Nothing per-body is bound or
pushed, so the draw stays one draw per primitive for the whole crowd. There was no throwaway path to write
and no bake needed — indexing a palette by instance costs nothing over indexing it by zero.

`MaterialBindings` exposes no dynamic offsets, which is what made this the obvious shape rather than a
clever one: per-body palettes would have meant a material per body.

### What went in

- **`world_skinned.vert` and `shadow_caster_skinned.vert`**, sharing both *fragment* stages with their
  unskinned twins. A body is lit, fogged, shadowed and veiled by exactly the rules everything else obeys,
  because a second lighting path for people would drift from the first inside a session. Posed bodies cast
  posed shadows: the bind pose would put a standing silhouette under a walking villager, which reads as a
  second body and is worse than no shadow.
- **The push block extracted to `world_push.glsl`.** Twenty-seven members that four stages must agree on
  byte-for-byte, previously duplicated between `world.vert` and `world.frag`; the skinned pair would have
  made four copies of a fault that surfaces as a draw-time payload-length error far from its cause. The
  member sequence was diffed against the original before anything was built on it.
- **One generic parameter on `InstancedBatch.End`** — a caller-supplied extra material. The primitive is
  handed an opaque handle and binds it, exactly as it already takes an opaque pipeline and opaque push
  bytes. It is never told what a bone palette is, so §-the-layering-rule holds: geometry only.
- **The gait is driven by distance, not by the clock.** This is the actual answer to the float: phase
  advances by metres covered, so a body slowed by a crowd or by a full load trudges instead of striding on
  the spot — §167's laden penalty becomes something you can see — and a body stopped dead stops moving its
  legs.

### The two bugs, which were the same bug

Bodies were invisible. Every value that could be probed was **correct**: model matrix at the right world
position with the right scale, palette identity-ish, push offsets right, descriptors bound at the right
sets, vertex layout matching the shader attribute for attribute. The data was right up to the moment it was
thrown away.

- **`Begin()` ran after the adds**, clearing all thirteen bodies every frame. It had been placed beside
  `unitBatch.Begin`, on the assumption that batch is begun before the agent loop — it is not; the loop fills
  plain lists and the batch is begun afterwards and fed from them. Worse, the ordering was "checked" against
  line numbers read before those very edits, which is evidence that deserves no confidence at all.
- **`SetInstances` ran inside the pass callback**, and a pass callback does not run when it is registered.
  The graph records passes as closures and executes them later, so the batch read lists the next frame's
  `Begin` had already cleared. The log order gave it away: the draw probe printed *before* the add probe.

That second one is why `PropModel` has a separate `Stage()` and why `unitBatch` calls `SetInstances` in the
build phase — `SetInstances` copies into the batch's own staging, so the callback need only record. **That
split is an invariant of this renderer, and it was read as incidental structure.** Staging and drawing are
now separate here too, and the "bodies reached a draw" line survives as a permanent instrument, because an
empty skinned draw is invisible and looks identical to a rendering fault.

### Then the animation was wrong, and that was mine too

- **"Idle starts walking in place with legs weirdly flailing."** One `Pose` is shared by every body and a
  clip writes only the bones it has tracks for, so each body inherited whatever the last one left in the
  bones its own clip does not touch — an idle villager wearing a walker's legs. The required sequence is
  **reset, sample, palette**, and VulkanLit's comment says so in as many words. The reset was skipped.
- **The sway was root motion.** A walk clip authored to travel carries the character forward in its own
  translation tracks, which is right in an animation package and wrong here: position is the simulation's
  and only the simulation's. Horizontal translation on root bones is zeroed; vertical stays, because that
  is the gait's own bob and flattening it makes the walk a shuffle.

### Actions are data, because the content is somebody else's

The renderer asks for a `BodyAction` — Idle, Walk, Labour, Strike, Fall — and never for a clip name.
`CharacterClips.Table` lists the names each action accepts, so Quaternius's `Man_Walk`, Mixamo's `Walking`
and a hand-authored `Walk` all bind without a line of C# changing. Five actions and not fifteen: at this
distance what reads is moving or not, swinging or standing, fighting or working, upright or down, and adding
a sixth is a claim that a player could tell it apart.

What bound is printed at load, and **the marker had to be fixed before it was worth having.** The first
version starred any name matched past the first choice — which starred all five, since no pack uses this
file's preferred spelling, and a marker that fires on everything says nothing. Stand-ins are their own list
now, so the report reads `Labour=Man_SwordSlash*` and nothing else is flagged: that is the one action for
which no clip exists in either Quaternius pack, and a downward sword arc is covering for an axe.

Measured: 31 bones, 5 primitives, 11 clips, 13 posed bodies, 5 instanced draws per pass, no validation
errors.

### The pipeline, and what is not proved

Mixamo exports FBX, one file per animation; the importer takes glTF with one skin and every clip on it. So
the pipeline has two jobs — convert and merge — and Blender headless is the only tool that does both, which
is also what retargeting a CC0 library authored on another skeleton would need. `tools/cook-character.sh`
plus `tools/character_merge.py` import each clip file, keep one armature, carry every action across, strip
Mixamo's `mixamorig:` bone prefix, and write one GLB; they deliberately do not apply transforms, which would
move the skinning frame the importer depends on.

**Blender is not installed and the Blender half has not been run.** Recorded as unproved rather than
described as working. The glTF half is proved: the asset imports, binds, poses and draws.

One licence note, stated once because the repository is public: **Mixamo is not CC0.** Adobe's terms allow
royalty-free use in a project but not redistributing the animation data on its own, and committing either
the FBX or the merged GLB is arguably that. Every other asset here is CC0 with a `CREDITS.md` line.
Quaternius's Universal Animation Library is the clean alternative — CC0, 120+ clips, locomotion included —
on a different skeleton, which is the retargeting the pipeline already needs Blender for.

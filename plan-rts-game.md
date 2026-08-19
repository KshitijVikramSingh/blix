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
| suite | `--selftest` **72/72** (was 53/53 through Session 4) |
| determinism coverage | **102 values a body**, 184 a tick, 25,580 at a checkpoint — every one probed |
| catchment, measured | **12,320 m² on open ground**, effective radius 62.6 m against a nominal 66 — §15 |
| soak, early career | **1.44 ms a tick at 300 agents, 23x real time**; a year is 4 min, ten years 39 — §15 |
| interception | closing at **exactly the difference of speeds**; contact is a separate problem — §15 |
| body | **1.79 m/s**, accel 2.0, decel 3.0, turn 3.03 rad/s, compression **1.5x** |
| roster | **6 types, 2 body classes** — §3; radius 0.37 / 0.55 / 0.90 |
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

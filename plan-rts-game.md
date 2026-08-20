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

**The second is the better game and the numbers now say so out loud.** Note what the peer table also shows:
even losing decisively, three peers still cost the settlement 409 grain of interrupted work and six lives.
A raid you win is still expensive, which is the design working.

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

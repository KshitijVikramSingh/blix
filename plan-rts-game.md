# RTSGame — Thread A: the game layer

## Handoff — read this first

**You are picking up a design that is fully specified and a codebase that has none of it yet.**

- **The code** is Thread B: a greybox locomotion lab, ~11.3k lines in `src/RTSGame`. One fixed 30 Hz
  tick, ORCA velocity control that sees walls, flow-field group movement, congestion routing in
  seconds. `--selftest` **37/37** on branch `rts-locomotion`. Its tick order, its 16 invariants and
  its five measured refusals live in **`plan-rts.md`** — read that before touching movement, because
  most of what looks tunable there is load-bearing in two ways at once.
- **This document** is Thread A: the game that goes on top, settled across two long design sessions
  in August 2026. §1–§11 are decided, not speculative — the numbers are derived and the derivations
  are recorded beside them, because they move as a set. **§12 is what is genuinely still open.**
- **Start at §13, Session 1.** It is a measurement with its predictions written down in advance, and
  it is deliberately small.

Four things that will bite you if you skip them:

1. **Do not move the default world size.** All 37 self-tests and every tuned constant in
   `plan-rts.md` are calibrated against `GridTransform(60, 60, 0.5f)`. Extent becomes a *parameter*;
   the default stays exactly where it is.
2. **Any new term in the route cost is honest seconds, with a fade matched to how fast the real
   condition actually clears.** This project has paid for that lesson twice — `plan-rts.md` §1 on
   barriers-as-cost, and invariant 15 on the decay tail. Threat will be the third opportunity.
3. **The determinism self-test covers movement only.** Extend it as each system lands, or it keeps
   passing while the game layer quietly goes non-deterministic through an unordered iteration nobody
   noticed. Lockstep LAN and career-to-career persistence both depend on it.
4. **Do not start the jobs layer before the router.** It is the biggest new subsystem and the whole
   design rests on it; calibrating it against a router about to change shape means tuning it twice.

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

Proposed, to be settled on the feel slider and *then* used to re-base the self-test thresholds — not
the reverse:

```
Walk (worker)         1.5 m/s     was 4.5
Soldier               1.7 m/s
Loaded cart           1.1 m/s
Scout / mounted       3.5 m/s
Acceleration          2.0 m/s²    was 16;  loaded cart 0.8
Deceleration          3.0 m/s²    was 16
Body radius           0.37 m      UNCHANGED
Nav fine cell         0.5 m       UNCHANGED — this is what preserves Thread B's tuning
Road SpeedMultiplier  x1.8        -> PathCost 0.56, derived, no new constant
```

The road multiplier needs no new machinery: `TerrainSurfaceRules.PathCost = 1/SpeedMultiplier`
already exists, so a road is a surface type the router prices correctly for free.

### Map sizes

Small / medium / large at **800 / 1000 / 1200 m**. **Design and tune at large (1200 m).**

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

### Three independent derivations of the size, all landing at 800–1200 m

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
- **One granary per core, with a number.** ~90 s catchment at 1.5 m/s is ~135 m, essentially the core
  tenure radius derived from response time. The natural economic unit and the natural defensive unit
  are the same size. Expanding past it requires a second granary plus a hauling link — which is the
  *"the core is the root of the hauling graph"* idea with a radius attached.

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
  with unit speed" item goes live the moment speeds differ.
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

### Flat global fields are dead at every candidate size — on memory, not on timing

One field is 10–23 MB, multiplied by distinct goal × body radius × retained congestion revision, and
discarded wholesale on every nav edit. That is arithmetic, not a performance guess, so **the
architecture decision does not wait on the scale proof and does not depend on which map size wins.**
What still needs measuring is the *constants* — region size, portal density, cache depth, and whether
the hauling workload behaves. Architecture from arithmetic; constants from measurement.

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

### Two area-scaling costs that no routing hierarchy touches

Both verified in source, both invisible at 30 m, both scale with map **area** per tick:

1. **`CongestionField.Update`** (`CongestionField.cs:129-137`) decays *every* cell of three arrays
   every tick — `pressure`, `flowX`, `flowZ`. At 1200 m that is 5.76M cells × 3 × 4 B ≈ **69 MB of
   memory traffic per tick**, before any agent deposits anything. Must become sparse or
   region-scoped. Carefully: invariant 15 makes the decay *rate* the highest-leverage constant in
   routing, so a sparse rewrite must reproduce the exact decay semantics or it silently invalidates
   every reservation tuned against it.
2. **`AgentSpatialIndex`** is `List<int>[width*height]`, dense over the whole terrain at 1.5 m, and
   `Rebuild` runs `foreach (var bucket in buckets) bucket.Clear();` every tick. Its own doc comment
   names the assumption the game breaks: *"The map is bounded and small, so the whole grid is a few
   hundred lists."* At 1200 m that is **640,000 Lists cleared per tick** to index 2,000 agents. The
   structure is right; only its sizing premise is wrong — fit it to the agent bounding box, or bucket
   per region.

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
- **Catchment radius** (~90 s proposed) is a tuning dial that decides how dense settlements must be
  and how early a second granary is forced. Wants measuring against actual settlement layouts.
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

### Session 1 — Make the large map affordable

**State at start:** branch `rts-locomotion`, clean tree, `--selftest` 37/37.

Parameterise the world extent **without moving the default**. `SimulationWorld()` hardcodes it at
`SimulationWorld.cs:152` (`GridTransform(60, 60, 0.5f)`), the placement grid at `:154` (`20x20x1.5`)
and the agent index at `:158` (cell 1.5 m, spanning `Terrain.Minimum/Maximum`). Then add a scale
scenario reporting the existing per-phase breakdown at 800/1000/1200 m with 500/1000/2000 agents.

**Write the predictions down before running, so the measurement can falsify something.** At 1200 m:

| | prediction |
|---|---|
| `CongestionField.Update` | ~69 MB of traffic per tick → **≥3.5 ms/tick**, agent-count independent |
| `AgentSpatialIndex.Rebuild` | 640k `List.Clear()` per tick → **1.3-3.2 ms/tick**, agent-count independent |
| together | **5-7 ms/tick**, comparable to the *entire* current movement cost |

Then fix what it finds — expected to be sparse or region-scoped congestion, and the agent index
fitted to the agent bounding box rather than the terrain. If the predictions are wrong, the reason
is worth knowing before anything is built on top of it.

**Gate:** 37/37 unchanged; large-map per-tick cost inside budget; and **every quality metric
bit-identical** across the congestion rewrite — the same standard Thread B's memoisation pass met,
and the only evidence that the decay semantics survived.

**Risk:** the congestion rewrite. Invariant 15 makes the decay *rate* the highest-leverage constant
in the routing layer, so a sparse version that changes when or by how much a cell fades invalidates
the tuning of every reservation that bids against it.

> **Do not add threat to the route cost until this lands.** It is a new term over the same
> machinery, and adding it first means writing it twice.

### Session 2 — The body, at the slider

**Different in kind: a feel session, not a headless one.** It wants a human at the tuning overlay,
and it wants Session 1 finished so the 1200 m map runs smoothly and the body can be judged on the
real thing rather than extrapolated from 30 m.

Settle top speed, acceleration, deceleration and time compression on the live overlay — §3 has the
proposed starting points and the reasoning for each. **Then** re-base the self-test thresholds
against the answer, never the reverse.

Tractable because the blast radius is smaller than it looks: `walked/optimal` and the other ratio
metrics are speed-invariant, so only the **time-based** thresholds move.

**Gate:** 37/37 with the new body, and every moved threshold recorded with its old value and the
reason it moved — otherwise the next reader cannot tell a deliberate re-base from a regression.

### Session 3 — Portal routing

Region partition at 32 m (64x64 fine cells, deliberately comparable to the 3,600-cell world every
Thread B constant was measured on), portal identification along region borders, an abstract graph
with edge costs from local searches, refinement into per-region local fields, and region-scoped
revisions. **The fine layer stays at 0.5 m and is not touched.**

Two risks, both of the kind that gets discovered late unless planned for:

1. **Congestion has to reach the abstract graph.** It currently feeds `BuildFlowField` directly. If
   abstract edge costs do not carry it, routes stop responding to jams at map scale and the
   congestion layer is silently deleted for any route longer than one region. This is a design
   question inside the implementation, not a detail of it.
2. **Region-boundary artefacts** are the classic HPA* failure mode. The pen and gate tests should be
   unaffected because the fine layer is untouched — but the *handoff* from abstract path to local
   flow is new. Wants a purpose-built test that routes a group across several boundaries and
   measures direction stability at each crossing.

**Gate:** 37/37; new long-route tests at 1200 m; group-order cost inside the tick budget at 1200 m.

**This is the session most likely to cost a second one.** Sessions 1, 2 and 4 are contained; 5 and 6
are large but well understood; this is the one where a decision inside the implementation can send
you back.

### Session 4 — Unit types

Per-type body radius, speed, turn model and carry capacity off `AgentDefaults`, rather than
re-littering literals — which `AgentDefaults`' own remarks record as a real bug source.

Landing it **activates two logged debts**: congestion delay does not scale with unit speed, which
goes live the moment speeds differ; and carts want the speed-scaled turn model (`ω = a/v`) that was
measured badly wrong for people and is correct for a loaded vehicle.

**Gate:** existing tests pass on the default type; new tests for a cart's turning circle and a
scout's speed differential.

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

1. **37/37 stays green**, or a threshold moves deliberately and is recorded with its old value.
2. **The determinism test grows with each system.** It covers movement only today.
3. **Serialization discipline** — stable ids over references. Fresh-start succession (§5) makes this
   core-loop rather than a save feature; it is cheap continuously and expensive retrofitted.
4. **Every new cost term is honest seconds with a matched fade.**

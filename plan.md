# Blix — the current plan

> **Opened 2026-09-28.** One record, holding the portability arc and the one
> stage the character arc still owns. `plan-blix-character.md` is folded into
> this and removed; its completed history is in version control.

## The thesis

Blix is not short of architecture. It is short of **pressure from outside its
own tree**.

Every fault found on 2026-09-27 came from pushing an existing thing slightly
past where it had been pushed before, and the shape repeated: a private copy of
an engine step, missing a case the engine already handled, failing silently.
`CreateMesh` had no 32-bit branch. Vulkan Lit resolved its own albedo and knew
only the raw-PNG shape. A shader's includes were not build inputs, so a clean
build shipped a stale `.spv`. Three of the four were caught by a person looking
at the screen.

So this plan is a sequence of **boundary crossings**, not a feature list. Each
stage pushes the existing design through something it has never crossed, and
whatever cracks names the next piece of work.

---

## A — `blix publish <app> --target osx-arm64`

**Not a new vertical. A consolidation that is already half done.** Seven
`tools/run-*.sh` scripts each carry their own `dotnet publish -r osx-arm64
--self-contained`, and `run-vulkan-sponza.sh` hand-copies *three* globs —
`*.spv`, `*.spv.refl.json`, `*.spv.tune.json` — because `dotnet publish` does
not treat generated shader output as content. That is the same duplication
`./blix` was created to end, with a publish step bolted on.

The first work is diagnosing **why** staging does not survive publish, in the
targets, rather than reimplementing the copy inside a `publish` verb.

The machinery already knows what a project is (`blix.project`), what
applications it contains (`.blixapps.json`), which has an apphost, and how
shaders and cooked assets are staged. What is missing is
*application → target → publish build → staging → native closure → bundle*.

One enrichment it needs: the app index records the assembly and apphost, but
publishing is a **source-project** operation, so the indexer should record the
originating `.csproj`.

**Verified by:** Bulwark, from clone to a double-clickable `.app`.

## B — runtime closure

**The one genuinely new design question in this plan:** what constitutes the
runtime closure of a Blix application, per target?

Development assumes Homebrew throughout — `./blix` sets `DYLD_FALLBACK_LIBRARY_PATH`
and the MoltenVK ICD, `MoltenVkBootstrap` probes Homebrew, `OpenALAudioDevice`
probes Homebrew. That is reasonable *bootstrap* and cannot be the release story:
a shipped `.app` cannot tell someone to `brew install`.

So the stage forces a distinction worth having: **developer dependencies**
(`glslc`, `spirv-cross`, validation layers) versus **runtime dependencies that
belong inside the bundle** (MoltenVK, the loader or a direct link, OpenAL Soft),
plus rpath fixups and signing. The closure is target-dependent, which is what
makes it a design question rather than a copy.

Note `spirv-cross` became load-bearing for a fresh clone on 2026-09-28, when the
`.spv.refl.json` sidecars stopped being tracked. It is a documented prerequisite
and in CI, so this is consistent — but it is now a *build* dependency with teeth.

## C — Windows bring-up, as an audit

`Blix.Runtime.Silk` is not a macOS runtime. It is a desktop Silk runtime
verified only on macOS: **five** OS conditionals exist tree-wide, all
quarantined. The portability work is therefore not "abstract macOS out" but
"find the assumptions that survived because no second platform punished them".

Expect bootstrap and tooling, not rendering:

- `Blix.Recipes/NativeLibraries.cs` hardcodes `libmeshoptimizer.dylib` and
  `libblix_bc7.dylib`. **Cooking runs during the build**, so the build fails
  before a window opens. Two lines, and undiscoverable without trying.
- `./blix` is bash plus `brew --prefix`.
- `MacDockIcon`, and the window-icon split that already understands Windows.

**Do not** build an `IPlatform` abstraction. Silk already is most of one.

## D — the CI matrix

Cheap once C lands. 2026-09-27 defined precisely what it can and cannot be:

- **Static checks travel everywhere.** `Blix.Test.Graphics` section BG already
  checks that a shader's includes are declared build inputs and that shaders
  sharing a uniform block agree about it — no GPU, milliseconds, every platform.
- **Pipeline-time breakage needs a booted application**, and the seven suites
  build no Vulkan pipeline. CI runners have no Vulkan device and will not soon.
  That stays a **pre-commit** concern, deliberately: the spike to make a GPU
  work in CI is not worth its cost.

## E — keyboard and gamepad completion

Breadth on a contract that already exists: `IInputHandler` is six methods and
`Key` is 53 values. Finishing the keyboard enum and adding device
connect/disconnect, buttons and axes in the same **raw** vocabulary is not
growing the engine in a new direction — it is finishing one.

**Not** input actions, bindings, or remapping. That is policy, and games have
not asked twice.

---

## F — the character arc's last open stage

Folded in from `plan-blix-character.md`. Room (R-A…R-E) is built, Motion was
deleted, C-0 closed as a decided-no on 2026-09-27, and C-C dropped on evidence.
What remains:

**C-A — contact drives weights, and there are no states.** Speed and
groundedness come out of the resolver and drive **blend weights directly**. No
states, no transitions, no dwell timers: the thing that flickered in Motion does
not exist here to flicker. This is also `BoneMask`'s first working consumer —
locomotion on the legs, a one-shot on the upper body, a body that walks and
punches without its legs freezing mid-swing.

*Negative control:* the mask set to `All` must visibly break the legs, and set
to `None` must leave the walk bit-for-bit unchanged. A layer that changes
nothing and a layer that changes everything are a mask's two failures, and both
are invisible unless asked for.

**C-B — and then the clip drives the contact.** Root motion says how far, the
room says where you can go; `RootMotion.Strip` is that seam. Honest expectation
unchanged: the Rogue has 4 travelling clips of 76, so this may end a
**decided-no**. Reaching that with the instrument built is the output.

**C-D — acceptance, from the chair.** Walk the room: ramps, stairs, ledges,
walls. No scuffing, no snapping, upper body doing something the legs do not know
about. Every stage of this arc had at least one fault only a person watching
could see, and four of five were **legibility, not mechanism**.

**Probe work throughout** — headless, exit code, no device: room geometry
closed and consistently wound; R-E's invariants as a simulation rather than a
picture; every clip a graph names exists on the rig with a finite measured
stride.

**The selection question stays open.** Whether Blix ever defines "a state
machine" is undecided — it may be data, each game's code, or nothing. C-A's
answer is that contact can drive weights with nothing in between.

---

## Carried, not scheduled

**Probe density.** The confirmed cause of ambient banding is probe spacing:
over 14,072 adjacent pairs at 1.57 m, irradiance ratio is median 1.41x, p90
5.15x, p99 25.7x. It reads worst on curtains because they are large, smooth and
have nothing to hide it. Eight hypotheses are refuted by measurement and four
sessions of automatic placement heuristics have failed; the live idea is
**authored** density as nested uniform volumes, following the `.blixpatch`
precedent. This is real, scoped work with nothing to do with portability, and
it deserves its own record when it starts.

**`BakeMerge`.** Identical signatures in TankArena and Bulwark, no copy in
RTSGame. Two consumers wanting the same decision is §4's bar; not taken yet.

**Clip lookup by name.** Three copies, three lines each, fallback chains that
differ per game. Earns a place when something wants the *same* shape.

---

## Deliberately not in this plan

Audio beyond what exists — velocity, Doppler, cones, streaming, buses. The
positional model already works and no game has asked. Packfiles: the cooked
artifacts already carry provenance and MSBuild already stages incrementally, so
a `.pak` would solve an aesthetic. ECS, scene formats, save systems, scripting,
navmeshes, editors, networking.

Each waits for a consumer, which is the rule that has served every arc so far.

---

## Open decisions

1. **Is `--target` plural from day one?** Designing for two targets before the
   second exists is the speculation this tree avoids; designing for one
   guarantees rework. Current lean: **one target, honestly named**, and let
   Windows reshape it.
2. **Does F run beside A–D, or after?** Two arcs in flight is how things rot,
   but the portability spine does not need a character controller and C-A is
   small.
3. **Does anything adopt `PropModel` further?** It has an in-tree consumer now
   (TankArena) and 72 external ones. Bulwark declined on evidence.

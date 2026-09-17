# Cook arc — plan

> Blix does not ship three cooked formats. It ships **a way to declare one**, and currently has
> three instances of it written by hand, none of which knows it is an instance of anything.

---

## What this is

Cooking joins the blessed tooling set. That is the whole arc, and it means something specific:
cooking stops being a command somebody remembers to run and becomes **a declared transformation
from a known source format to a known engine-native one**, with coverage, staleness,
reproducibility and reporting that follow from the declaration rather than from discipline.

**Which three formats exist is deliberately not the point.** `.blixmesh`, `.blixtex` and
`.blixprobe` were three use-case-specific answers to Sponza. Any of them may turn out to be the
wrong answer and be deleted. What survives that is the idea, the conventions, and the format
representation — and the fact that the three happen to be *one load-time win, one memory win, and
one genuine pre-compute* is the strongest evidence available that the shape generalises, because
those are the only three reasons anything gets cooked in game development.

So the deliverable is not a finished cooker. It is a **recipe** being a thing you can write.

---

## What the measurement found

Counted at `2aa1faa`; the full census is `docs/blix-assets.html`.

| | |
|---|---|
| **62** mesh assets in the tree, **30** cooked | and the line between them is a **directory** — `kit/` entirely cooked, its sibling `models/` entirely not, same project, same vendor, same importer |
| **0** emitters of `AssetLoadReport` | the type names Source / Cooked / Fallback / Missing and its only references are the test that checks it round-trips |
| **`File.Exists`** | the loader's entire staleness check. 29 of 30 cooked files currently predate their source |
| **`--flip-v`, `--split`, `--no-split-foliage`** | change the output bytes and are recorded nowhere. The cooked tree is not reproducible from the repo |
| **`"BLIX"` vs `"BLXM"` vs `"BLXP"`** | and version as `u16` in one, `u32` in two — so no single function can read any Blix cooked file's magic and version |
| **0** external images | no `.png` under `src/`, and no asset with an external image URI, so the `.blixtex` sideload path cannot fire on anything in the repository |

### And one finding that decides the design

**Blix already has a cooking pipeline that works, and does not call it cooking.**
`Blix.Tools.Shader` — 144 lines, fan-in 15 — takes a known source format and produces known
transformed formats the runtime reads by path convention. It has none of the problems above:

- coverage is **declared** in the csproj (`@(GlslShader)`), not typed at a prompt
- staleness is **structural** — the target declares `Inputs`/`Outputs`, so it is incremental by
  construction and cannot silently go stale
- it runs **automatically**; nobody remembers to run it, and coverage is 100% because it cannot be
  anything else
- a project declares **what**; the rule decides **how**

The tree ran both experiments one layer apart. The declarative one has total coverage and no
staleness. The imperative one has 48% coverage decided by shell history. That is not a preference
between two designs; it is a result.

A third data point sits between them: **`.font.json` is a recipe with no cook.** It declares a
source TTF and `sizes: [24, 48, 96, 200]` — source plus parameters, exactly the right shape — and
then the atlas is rasterised at load anyway.

---

## The vocabulary, and the split it rests on

Three words, and the split is forced by one objection: **if Blix's cooks live in the engine next to
their format, they are blessed by location and yours is structurally a different kind of thing.**

| | what it is | where it lives | why |
|---|---|---|---|
| **Format** | byte layout, reader, writer, **and the preamble** | **engine** | the runtime reads it; a format is a thing Blix defines |
| **Capability** | BC7 encoding, GGX prefilter, meshopt simplify | **engine** | reusable work that cooking happens to call, not cooking itself |
| **Recipe** | *this source, these parameters, that format* | **not the engine** | Blix's own sit exactly where a project's would |

`CookToBlixMesh` currently lives in `Blix/GltfStaticImporter.cs` — inside the engine — which is the
blessing-by-location this split exists to remove. It moves out.

**Blessing then becomes a list, not a privilege.** Blix ships three recipes; a project ships its
own; both are the same kind of file. Deleting `.blixprobe` means deleting a recipe and a format,
not carving something out of the engine.

**A recipe is a file, not a project.** Blix's three live in one recipes project; RTSGame's lives in
RTSGame. Structurally identical, differing only in who owns the assembly — which also keeps the
cost of a recipe visible. If Blix's own mesh recipe is 300 lines, that is what one costs, and
nobody gets to claim theirs is harder for being outside the engine.

`Bc7Native` and `MeshoptNative` **travel with the recipes** rather than becoming capabilities. One
consumer each, native P/Invoke, and the consequence is named rather than discovered: two recipes
carry a dylib dependency the engine does not have.

---

## The preamble, and why nothing needs code generation

Every Blix cooked artifact begins with the same block. This is the "format representation" the arc
is actually about, and it is small:

```
magic     4cc     BLX + format
version   u32     the format's own, uniform width across all three
recipe    4cc     which recipe made this
flags     u32     bit 0 — source still required at load
source    hash / mtime of what it was made from
params    len + bytes — the recipe's own settings, verbatim
── format-specific header follows ──
```

Each line buys something currently missing. `recipe` + `params` makes a cook reproducible, which
`--flip-v` today is not. `source` makes staleness checkable **without knowing the format**. `flags`
bit 0 turns the material hole from an unwritten caveat into a declared property. And the preamble
being common is the difference between one tool that reads any cooked file's provenance and three
special cases.

### Stamping is enforced by the writer, not by an interface or a generator

A recipe never writes bytes. Each of the three writers already has exactly one external caller —
they are the chokepoint. So:

```csharp
BlixMeshWriter.Write(path, file, in CookStamp stamp);
```

`CookStamp` carries the recipe id, its version, the source, and the parameter block. **You cannot
call `Write` without one**, and the writer — engine code, owned by the format — lays the preamble
down itself. A recipe that forgets to stamp does not compile.

That is stronger than an interface, which can be implemented wrongly, and cheaper than a source
generator, which would generate what one struct already forces. There is no Roslyn codegen anywhere
in this tree and this arc does not introduce any.

---

## Stages

### K-A — the preamble, and three formats become a family

Define `CookStamp` and the preamble; every writer takes one. Fix the three disagreements —
`.blixtex`'s magic joins the `BLX*` namespace, version becomes `u32` everywhere, `kind` is either
generalised or dropped. All three formats version-bump and everything is re-cooked.

Readers refuse an unstamped or unknown file through `AssetImportException`, which is already the
engine's single word for *this is not something Blix can read*.

**First because it is the irreversible-feeling step and it is cheapest now**, with three formats and
no external consumers. `.blixtex` v1→v2 and `.blixmesh` v1→v3 both already took the re-cook route,
so the precedent is recent and is yours.

### K-B — recipes leave the engine

`[Recipe]` on a static method, like `[BlixApp]`. `CookToBlixMesh` moves out of the engine. Blix's
three recipes land in one project, with `Bc7Native` and `MeshoptNative`.

Two things in `Blix.Tools.Cook` are not cook verbs and **move rather than disappear**, pending your
say-so on each: `inspect` reports on a *source* asset and cooks nothing — it is the `inspect` verb;
`meshopt-selftest` proves a P/Invoke — it is a suite. Same finding as `check`, same fix.

### K-C — the index, and cook becomes a host

`[Recipe]` indexed by `Blix.Tools.Apps`, which already reads ECMA-335 metadata at build and loads
no assemblies. `blix cook` dispatches by name the way `BlixApps.Dispatch` does; `blix cook status`
reports coverage. A project's recipe is found with zero registration.

The attribute is for **finding**, not **constraining** — no base class, no interface, no contract
beyond a signature the build errors on. The same deal `[BlixApp]` already makes.

### K-D — the build rule, and coverage stops being shell history

```xml
<CookMesh    Include="Assets/models/**/*.glb" />
<CookTexture Include="Assets/textures/**/*.png" />
```

A target per recipe, incremental via `Inputs`/`Outputs`, exactly as the shader pipeline does it.
Coverage becomes declared and staleness becomes structural. `blix cook` stays as the manual
override rather than the interface.

`BlixCookCustomTarget` mirrors `BlixShaderCustomTarget`, and takes its rule verbatim: **settings are
parameters, a different algorithm replaces wholesale, and there is nothing in between.** That
argument is already made and shipped one layer down; this arc does not re-litigate it.

### K-E — a load says what it did, and a check can assert it

`AssetLoadReport` gets its first emitters, at every point a path is chosen. It carries which recipe,
from what source, whether the stamp is current, and **what it cost** — because `.blixmesh` is a
load-time win, `.blixtex` is a memory win and `.blixprobe` is work that cannot be done at load, and
a single Source/Cooked flag flattens three different economics into a checkmark.

Then `blix check --cooked <dir>` exits non-zero when anything resolved to the slow path. **Cooking
becomes enforceable per project without becoming mandatory anywhere**, and the exit code is already
the convention.

A thin version of this — which branch was taken, nothing more — is available from K-A onward and can
be pulled forward if we want evidence before the build rule lands.

### K-F — materials, and `flags` bit 0 gets cleared — **PARKED, see above**

`.blixmesh` ships from K-A declaring *source still required*, because it is true: materials are
parsed from the glTF in full on every load, so the source is a permanent runtime dependency. This
stage's entire job is to make that flag false.

It is what decides whether cooking is an optimisation or a pipeline — until it lands you can never
ship without sources, the cooked tree is not a distributable artifact, and *"open a cooked asset"*
stays an incoherent request.

### K-G — a second owner, which is the proof — **HALF DONE, and the other half is consumer-blocked**

A project declares a recipe and it costs the recipe. The named candidate is the **font atlas**: it
is already declared recipe-shaped in `.font.json`, the work already happens (at load, every time),
and it is owned by four projects rather than by Blix. If it cannot become a recipe cheaply, the
substrate is not real and we have written three formats a fourth time.

**The font atlas became a recipe, and nobody marked it.** `FontRecipe` is declared, `fnt1` is in the
index, `BlixFont` is in `Blix.Assets`, four projects each carry a cooked `.blixfont` beside their
`.font.json`, and `FontImporter` prefers the baked one. `Blix.Test.Recipes` covers it. **A fourth
format cost a recipe** — that half is proved.

**The other half is not, and cannot be proved honestly today.** The claim above is that *a PROJECT*
declares a recipe. All four recipes live in `Blix.Recipes`; nothing outside Blix has declared one.
Checked for a real candidate rather than assumed: the nature kit's `.obj`/`.mtl` looked like one and
is not — `.obj` is handled by `Blix.Assets/ObjImporter`, so an `.obj` recipe would be a FIFTH BLIX
recipe, and the fourteen files total 372 KB with no measurable cost.

So **no project in this tree owns a source format**, and proving it would mean inventing one — which
is the move this arc was written against. K-G's second half is in the same position as `.blixtex`:
waiting on a consumer, not on effort. The mechanism is ready and untested — `Blix.Cooked` has no
dependencies and `Directory.Build.targets` takes `BlixCook` directly.

---

## The skinned gap, measured — and it is not in the stages above

`blix check --cooked` says it on every rigged asset: **"a rigged glTF has no cooked form —
.blixmesh holds no skinned vertex layout."** The word *skinned* appears nowhere else in this plan.
Whole tree: **62 assets, 77 loads, 54 cooked, 23 on the slow path, 3,507 ms of CPU on source paths.**

The slow path has TWO causes and this plan only analysed one. Measured by stripping every texture
reference out of a copy and re-running the check, so the remainder is geometry and skin alone:

| | full load | geometry + skin only | decode share |
|---|---|---|---|
| `villager_peasant` | 824 ms | **405 ms** | ~50% |
| `villager_ranger` | 803 ms | **197 ms** | ~75% |
| `villager_dressed` | 163 ms | **212 ms** | none — it ships no images |
| `villager_universal` | 78 ms | ~78 ms | none |

**~890 ms of the four villagers' ~1,950 ms is geometry and skin**, and that half is not behind D5 at
all. At the 2.5x cooking gives the kit props it is roughly half a second off every launch.

Two corrections fall out. The importer's own note — *"605 ms of PNG decode each"* — is too strong:
two of the four ship no images. And the first reading of this table, that geometry was obviously the
big unblocked win, was also too strong before the measurement existed.

**It is an arc, not a stage.** A cooked rig is not a `layoutId` addition: it needs the skinned
vertices, the skeleton, and the clips — the Rogue carries 76 — which is a new format rather than a
fifth column in this one. It has what neither K-F nor K-G's second half has: **a live consumer and a
number**.

## The decisions, as settled

| | question | answer |
|---|---|---|
| **D1** | does a load report what it did? | **yes** — K-E, carrying cost, not just branch |
| **D2** | is a stale cooked file an error, a warning, or ignored? | **visible before enforced** — the stamp makes it checkable in K-A; the loader does not refuse |
| **D3 / Q-E** | do materials get cooked? | **yes** — K-F, and the flag exists from K-A so the debt is declared rather than implied |
| **D4 / Q-A** | is cook a transform or a state? | **state**, as a build rule — K-D. Decided by the shader pipeline, not by preference |
| **D5** | can the texture cook reach how Blix assets are authored? | **parked, with a wall** — see "the chain" |
| **Q-B** | who owns the options? | **header records what was done; project records what should be done** |
| **Q-C** | does cooking stay invisible at load? | **engine reports, check asserts** — K-E |
| **Q-D** | what is cooking for? | whatever a project needs. Three economics, and the report distinguishes them |
| **Q-F** | is cook one tool or three? | **a host for recipes** — K-C |

**D5 is deferred deliberately.** Every asset in the tree embeds its textures, and a sideloaded
`.blixtex` needs a file URI, so the texture cook is unreachable by construction for how Blix content
is authored. The two answers — an unpack step, or textures cooked *into* a container rather than
beside one — both want the format family settled first, and neither has a consumer asking. It is
recorded here so it is deferred rather than forgotten.

---

## What K-E measured, and the wall it found

Stages K-A through K-E landed. The substrate exists: recipes are declared, discovered, covered by a
build rule, reproducible byte for byte, and a load now says what it did. What that instrument then
reported changes the plan for what remains.

### The numbers

| | uncooked | cooked | saved |
|---|---|---|---|
| RTSGame's 29 kit props | 97 ms | **39 ms** | 58 ms, 2.5× |
| RTSGame's 4 villagers | 1,856 ms | **1,416 ms** | 440 ms, 24% |

`.blixmesh` earns its place — two and a half times on geometry-heavy assets. But on the only assets
that cost seconds it removes a quarter, because **the texture decode is the dominant term and
`.blixtex` cannot reach it**: every image in this tree is embedded in its container, and a
sideloaded `.blixtex` needs a file URI. Ten slow loads in the whole repository, all ten the four
villagers' PBR textures, 3.17 s of CPU per launch.

**Two of the three formats have no live consumer here at all.** `.blixtex` and `.blixprobe` exist
for Sponza, which is unverified and on an unmounted drive. The one with real consumers is the one
whose design is least Sponza-specific.

### The chain, which is why K-F is not independently doable

> **self-containment ← textures ← a grouping decision ← a requirement that does not exist**

**K-F cannot deliver what it was for.** Cooking materials was meant to clear `SourceRequired` and
make cooked output distributable. It cannot: with images embedded in the container, **the glTF *is*
the image store**, so the source stays required however completely the materials are cooked. K-F is
gated behind D5.

**D5 is gated behind a grouping decision.** The obvious fix — one texture artifact per source, so
the three formats share an addressing rule — was rejected, and correctly: texture packing across
models is a project's decision (atlases, arrays, shared palettes, streaming pools), and a
per-source artifact forecloses all of it permanently. Sibling-by-extension is a **1:1 assumption**;
it fits `.blixmesh` and `.blixprobe` because those genuinely are one-per-source, and textures are
the case where grouping is not the format's to decide.

**And no requirement is driving that decision.** Nothing in the tree is asking for atlases. The
measured cost is four character assets.

So this is a wall rather than a queue, and pushing on it means inventing the requirement that
justifies the design — which is the failure mode this whole arc was written against.

### What that makes K-F and D5

**Both parked, with the reason recorded rather than rediscovered.** Neither is "not done yet"; both
are blocked on a decision that has nothing to decide it. The day a project's numbers hurt — real PBR
content, or shipping without sources — the requirement will design the format, and
`blix check --cooked` is the instrument that will say when that day arrives, per project, in
milliseconds.

**A ground-up redesign is the door this opens, and it is a different arc.** Content-addressed
resource store, an index mapping sources to the resources they need, grouping as policy over the
store — that answers embedding, dedup, packing and self-containment together. It is also exactly
what every plan in this repository has declined to build, and the evidence does not support it yet.
Worth knowing where the door leads; not worth walking through it on four assets.

---

## Proving it

The negative controls, because a layer that can only succeed is not a layer.

- A file with a wrong magic is **refused by name** through `AssetImportException`, not a crash.
- A stamped file's parameters **round-trip**: read them back and they are what the recipe passed.
- A recipe **cannot** produce an unstamped file — the call does not compile.
- A `[Recipe]` with a wrong signature is a **build error naming the method**, not a silent skip.
- Two recipes with the same id is an **error naming both**, not last-wins.
- A deleted recipe **disappears from the index** on the next build, with nobody editing a file.
- Touching one source **re-cooks exactly one file**; touching none re-cooks nothing.
- A clean re-cook produces **byte-identical output** — reachable only because the parameters are
  stamped, and impossible today.
- A missing cooked sibling reports **Source**, not Cooked; a substituted asset reports **Fallback**.
- `blix check --cooked` **fails** on a tree with an uncooked asset and passes on the same tree cooked
  — both legs, because a check that only ever passes is not a check.
- The engine references **no recipe**; recipes reference the engine.

---

## What this arc will not build

A general build system · a dependency graph between assets · a revival of `AssetDatabase` as the
addressing layer · remote or distributed cooking · a content pipeline DSL · packaging, archiving or
compression · a plugin system · a source generator · an asset importer UI.

**And it will not make cooking mandatory.** Running from source stays correct and stays the
iteration path. What changes is that a project can *declare* it wants cooked assets and have that
checked, rather than hoping somebody typed the right directory.

---

## Order, and why

K-A → K-B → K-C → K-D → K-E → K-F, with K-G as soon as K-D lands.

K-A first because the preamble is the thing everything downstream reads, and because breaking three
formats at once is cheap today and expensive once there are six. K-B before K-C because the index
should index recipes that are already in their final home. K-D before K-E because a report about
coverage is worth more once coverage is declared. K-F last because it is the largest, and because by
then it is one flag bit to clear rather than an open question about what cooking is.

K-G is the proof and should happen the moment it can, not at the end — if the font atlas is painful
as a recipe, that is a finding about K-B and K-C and we want it while they are still warm.

The honest stopping point is after **K-E**. At that point cooking is declared, covered, reproducible
and reported, and whether materials ever get cooked is a separate decision made with the thing in
hand.

# Tooling arc — plan

> Five arcs ran into one wall from five sides and each answered it locally. This arc is the
> first time the whole graph was in one place — `docs/blix-topology.html`, generated from
> `src/*/*.csproj` — and the wall turned out to be a naming decision, a missing front door,
> and a rule that only ever says *no*.

Blix has a cooker, a validator, a viewer, a screenshotter and four test suites. It has no
command. It has importers, loaders and visualisers that every game reimplements, filed under
a word that means *may be deleted*. This arc does not add capability. It moves what exists
to where it can be found and used.

---

## The findings that start it

Counted off the tree at `2d3f478`, not remembered:

**Nothing is packaged as a tool.** No `PackAsTool`, no `ToolCommandName`, no
`.config/dotnet-tools.json` anywhere in the repo. There are eight CLI entry points —
`blix-cook`, two probes, four test suites, the capture — and to run any of them you must know
a project path.

**Fourteen launchers are fourteen copies of one prologue.** `set -euo pipefail`, `REPO_ROOT`,
`brew --prefix`, the libvulkan check, `DOTNET_ROOT` and the three `export`s each appear
exactly 14 times across 1,690 lines of shell in `tools/`. That is one function, pasted
fourteen times, because there was nowhere to put it.

**Six independent ways to draw a skinned mesh.** Runner, VulkanLit, the toolchain lab,
Bulwark, RTSGame and `Blix/SkinnedGameObject` — each with its own vertex shader. The pose
pipeline underneath is shared by all of them. Only the draw was ever baked into an
abstraction, and the draw is the part that differs.

**Two engine abstractions with one consumer each.** `SkinnedGameObject` (121 lines, used by
VulkanLit) and `Blix.Render/PropModel` (686 lines, used by RTSGame). Both are shaped as
shared infrastructure and neither is.

**RTSGame references no `Blix.Geometry`,** and no lab references `Blix.Assets`. A
72,720-line game with pathfinding, formations, sweeps and collision built its own geometry;
both labs import glTF through `Blix` directly and never touch the asset database. Two
different ways of not using what exists.

**A third of the game is instrumentation.** RTSGame's `Debug/` is 22,778 lines — larger than
any engine module and four times its own `Rendering/`. None of it is reachable by anything
else, and both labs rebuilt their own report and trace from scratch.

---

## The diagnosis

### §4 is a brake with no accelerator

*"Extract under pressure; a second consumer is the bar"* tells you precisely when **not** to
extract. It never fires when the bar is cleared — because clearing it happens in someone
else's project, next month, and that second consumer writes its own copy, since writing your
own is always locally cheaper than going back to extract someone else's.

Six skinned draw paths is what that rule looks like after five games. It worked exactly as
written and produced the opposite of its intent. The rule is not wrong; it is half a rule.

### "Lab" is carrying two incompatible meanings

One is *an experiment that may be deleted* — which is what Motion was, and deleting it was
correct. The other is *where the engine's tooling lives* — the viewer, the probe, the
capture, the importer, the skeleton view. When one word covers both, everything inside
inherits the disposability of the first, so nobody builds a workflow on it and the workflow
gets rebuilt instead.

The evidence that these are two different things is already in the tree: Motion was deleted
inside one session, while the toolchain family survived three arcs and grew three
executables and a second lab family modelled on it.

### The prior flags

This has been arrived at five times and answered locally each time:

- **View arc D** — should the ViewTable move? Decided no.
- **Animation arc D** — masks are policy, deferred until a consumer asked. Same question.
- **Chassis singulars** — "one world" turned out to be the toolchain lab; the lab was what
  pressured the engine.
- **Character arc C-0** — literally "extract rig loading, keep drawing local".
- **Bird's-eye assessment, 2026-06** — flagged the next frontier as *ownability by a second
  mind*. That is this, named three months ago and not acted on.

---

## Stage T-A — the split, and the test that decides it

**Naming first, deliberately.** The front door is the more satisfying stage and it is second,
because a CLI built against names that do not survive is a CLI built twice.

### The test

> **Does anything outside its own family need it, and would deleting it lose a workflow or
> only an experiment?**

A tool is used by someone doing something else. A lab exists to answer one question and is
finished when the question is answered. The test is written down here so the next one is
decidable without a discussion.

Applied to what exists:

| | verdict | why |
|---|---|---|
| `Blix.Labs.Toolchain` + Viewer/Probe/Capture | **tool** | Three arcs, three executables, used to inspect any asset in the tree. Deleting it loses a workflow. |
| `Blix.Labs.Character` + Room/Probe/Capture | **lab** | One question — where a body can go and what stops it. Its probe judges *this room*, not any room. |
| `Blix.Labs.Character.Motion` | **was a lab** | Built, argued against by its own dump, deleted inside a session. The test predicts this correctly. |

### The rename

`Blix.Labs.Toolchain` → **`Blix.Tools.Preview`**, with `.Viewer`, `.Probe` and `.Capture`
following it. It joins `Blix.Tools.Cook` and `Blix.Tools.Shader`, which were already named
correctly.

*Preview* names the subject rather than a verb: a rig or model, resident on the GPU, drawn so
it can be read. `Asset` would collide with `Blix.Assets`, and `Inspect` would collide with the
CLI verb that `blix-cook` already owns — `inspect` **lists** and always exits 0, `check`
**judges** and does not, and that distinction is deliberate and worth protecting.

**This is the one decision in the stage that is taste rather than evidence**, and it is cheap
to redirect before T-B is built on it. Everything else here is mechanical: csproj names,
namespaces, the solution, the launchers, the plan documents that cite them.

The character lab keeps its name. It is a lab, and after this arc that word will mean
something.

---

## Stage T-B — the front door

One `blix` command over the entry points that already exist, packaged as a dotnet tool with a
`.config/dotnet-tools.json` so it is on PATH after a restore:

```
blix cook textures <dir>      the asset cooker              (Blix.Tools.Cook)
blix inspect <asset>          lists what is in a file, exits 0
blix check <asset>            judges it, exits non-zero      (the preview probe)
blix view <asset>             the viewer                     (--rig --clip --mask ...)
blix shot <asset> --out p.png the capture                    (--time --xray --zoom ...)
blix test [suite]             the four harnesses
```

The environment prologue — the ICD path, the layer path, `DYLD_FALLBACK_LIBRARY_PATH`, and
the exec-the-apphost rule that exists because Homebrew's `dotnet` is a `#!/bin/bash` shim and
`/bin/bash` is SIP-protected — lives here **once**. That is the whole reason the 14 launchers
exist, and it is what deletes them.

**No new capability.** Every subcommand is an existing executable with an existing argument
parser behind a name. If a subcommand needs a flag that does not already exist, it is out of
scope for this stage.

**Negative controls**, because a CLI that cannot fail is not a CLI: `blix check` on a
deliberately broken asset must exit non-zero; `blix` with no arguments must not silently
succeed; and a subcommand naming a file that is not there must say which file.

---

## Stage T-C — extraction, and only what the graph proves

### T-C1 — rig residency

Formerly character-arc C-0, and it moves here because it is a tree-wide extraction that
happened to be discovered by one arc. Six duplicate paths is the evidence; nothing here is
anticipatory.

The extracted part is the half that contains no draw: glTF import, vertex and index buffers,
albedo upload, material scalars, bounds, weighted-bone analysis. The part that differs stays
with the consumer: the bone material, its shader program and set index, the palette buffer's
instance layout, the passes.

Two rules keep it from becoming the tree's **third** stranded abstraction:

1. **Nothing that names a shader program, a material set, a pass or an instance count crosses
   the line.** If a parameter exists only so a caller can say how it will be drawn, the line
   is in the wrong place.
2. **`LabRig` is rewritten on top of it, not left beside it.** An extraction whose first
   consumer keeps its own copy has been proved by nothing.

If rule 1 cannot be held, the outcome is a **decided-no**: the duplication is recorded, and
the tree gains no third stranded abstraction.

### T-C2 — investigate, then report, then maybe move

Two candidates are suggested by the graph and **not** proved by it. Each gets an
investigation and a written finding before any code moves, and a decided-no is a legitimate
output for either:

- **Session instrumentation.** RTSGame's `Debug/` is 22,778 lines; both labs built their own
  `LabReport` and `LabTrace`. Three implementations of "make a running session reportable"
  is either a missing module or three genuinely different jobs, and the difference is not
  visible from the line counts.
- **Camera rigs.** `RoomCamera` holds four — orbit, third-person, first-person, isometric —
  and the demos appear to hand-roll their own. *Appears* is the operative word: this has not
  been checked, and the check is the work.

---

## What this arc will not build

An editor · a plugin system · a GUI shell · a package or versioning scheme · an asset
pipeline rewrite · a project template · a scripting layer.

**And deliberately not: a standing second-consumer check.** It was considered — something
that notices when §4's bar has been cleared and says so, fixing the rule that produced six
skinned paths rather than only its output. It was set aside because a detector built before
the first extraction is a detector with nothing to calibrate against. If T-C1 and T-C2 make
its shape obvious, it becomes the next arc's opening rather than this one's assumption.

---

## Order, and why

T-A → T-B → T-C, and the order is chosen by blast radius rather than by interest.

**T-A is mechanical and total.** It touches every csproj, namespace, launcher and plan
document that names the toolchain lab, and it changes no behaviour at all. Doing it first
means the one irreversible-feeling thing happens while nothing is built on top of it.

**T-B is additive.** Nothing breaks while it is built; the 14 launchers keep working until
the day they are deleted, and they are deleted in one commit with the CLI already green.

**T-C is the only stage carrying design risk,** and it is last so that it is done with a
front door already in place to exercise it — an extracted loader is much easier to judge when
`blix view` and `blix check` both run through it.

The honest stopping point is after T-B. At that point Blix has a command, its tooling is
named for what it is, and the duplication is documented in `docs/blix-topology.html` rather
than fixed. That is a smaller arc than this document describes and a legitimate place to
stop.

---

## And then the character arc resumes

C pauses at T-A and returns after T-C1 — not where it left off, but with its stage C-0 gone
and its purpose changed. **C-A becomes the thing that proves the extraction**: the first
consumer built *on* the extracted loader rather than *around* it, a body walking the room
with contact driving blend weights and a mask putting a one-shot on its upper body.

That is better than either arc alone. The extraction gets a consumer that is not itself, and
the controller gets a foundation that is not a seventh copy.

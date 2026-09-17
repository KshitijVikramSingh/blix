# Tooling arc — plan

> **Rewritten 2026-09-15.** The first version of this document asked what had earned the right to
> move, and staged a rename, a CLI and three extractions around duplication counts. That was the
> wrong question twice over. The record of getting to the right one is in `docs/` — the counted
> graph, then two superseded drafts, then `blix-what-it-ships.html`.

---

## The model, settled

**Blix is the engine.** It ships formats, pipelines, conventions, a substrate, and a few tools and
applications of its own that are not special.

**A project is a folder.** `src/RTSGame` is one. It may contain many C# projects and even many
solutions; if they build on Blix, it is a Blix project. What a project eventually canonicalises,
puts a menu on, strips the panels from and ships is its own business — **"game" is not a word the
substrate learns.**

**A project is many Blix apps.** An app is any entry point built on Blix. A panel on a blank screen
is one. A script that reads four files, prints, and exits is one. A shipped game is one with the
debug turned off.

**A tool is a headless app; an application is one with a window.** The distinction is real but it is
not a category of project — both live side by side in the same folder, and a project has many of
each.

---

## The finding this arc exists for

Apps and tools have **no way to declare themselves, be discovered, or be invoked.** Every project
hand-rolls all three, and the two that exist rolled incompatible ones:

- **RTSGame** — one csproj, one launcher, and `Program.cs` is **726 lines of
  `if (args.Contains(...)) { ...; Environment.Exit(...) }`** over **110 distinct flags**, dispatching
  to ~35 tools plus the application itself.
- **The labs** — three csprojs each, three argument parsers, three launchers, and a fourth file for
  the shared library.

Both work. Neither is the app's business. Everything else previously diagnosed is downstream of
this:

- **The 14 launchers are not a packaging problem.** RTSGame needs one launcher for thirty-five tools
  because it dispatches internally; the labs need three each because they do not. The launcher count
  is a symptom of missing project dispatch, and all fourteen contain the same 11-line
  MoltenVK/DYLD prologue because there was nowhere else to put it.
- **`--rig` means three slightly different things** not because nobody agreed, but because three
  executables each parsed their own arguments with nothing to inherit.
- **Ten applications declare ten render graphs** — VulkanLit 10, toolchain 8, Sponza 7, RTSGame 6,
  Bulwark 5, TankArena 5, Particles 5, VulkanGraph 5, character 5, Pong 2 — all shadow → lit →
  present variants, Bulwark's lifted from TankArena by hand. That is the next layer's problem and it
  is not this arc.

---

## Stage A — the app layer, built and proved

Five pieces, all small, none of them a framework. **An app is still a `Main`.** Blix learns how to
find it, not how it is written.

### A1 — `[BlixApp]`, in `Blix.Core`

The attribute is a contract, and `Blix.Core` is where contracts live — 483 lines, 22 consumers, and
already the thing everything references. Nothing gains a dependency it did not have.

```csharp
[BlixApp("fightbench", Summary = "the fight bench matrix")]
static int Run(string[] args)
```

On a static method, so **one assembly can declare many apps** — which is the property that makes
this work at both extremes. RTSGame keeps its single executable and its 35 tools exactly where they
are and deletes the if-chain; a thirty-line tool declares one and drags nothing with it. Neither has
to restructure to benefit.

A wrong signature is a **build error naming the method**, not a silent skip. A tool that is not found
because it was declared slightly wrong is the worst failure this can have.

### A2 — the index, generated at build

`Blix.Tools.Shader` already establishes the pattern one layer down: run at build, reflect, write a
sidecar, read it cheap at runtime. This is the same move one level up.

A new build-time executable writes `<Assembly>.blixapps.json` next to the output. **It reads the
assembly's metadata and never loads it** — `System.Reflection.Metadata`, no dependency resolution,
no code executed, no side effects.

That gets what neither mechanism has alone: it cannot drift, because it is regenerated rather than
maintained; `blix ls` is instant and needs no build; a broken build leaves a stale index that can be
*reported as stale* rather than silently wrong; and it records the one thing an attribute cannot say
on its own — **which apphost runs which app**, which matters the moment a project has several
assemblies.

### A3 — convention, so nothing needs migrating to start

A csproj with a `Main` and no attribute is **one app, named after itself**. Undeclared works.
Declaring only adds metadata. Nothing in the tree breaks on the day this lands and nothing has to
move before it is useful.

### A4 — `blix`, which is only a resolver

One script at the repo root. It sets the ICD path, the layer path and
`DYLD_FALLBACK_LIBRARY_PATH`, then execs — because Homebrew's `dotnet` is a `#!/bin/bash` shim,
`/bin/bash` is SIP-protected, and dyld strips `DYLD_*` across that exec. **That prologue is the
reason fourteen launchers exist, and this is where it legitimately lives, once.**

```
blix ls                      what this project has, read from the index
blix run <app> [args]        resolve, build if stale, exec
blix run rts:fightbench      explicit cross-project addressing
```

The project comes from the **working directory**, walking up to the nearest `blix.project` marker —
a one-line file naming the project, which is also where the `rts:` prefix comes from. Outside any
project folder, `blix` falls back to Blix's own apps, which is what makes `blix cook` work from
anywhere.

### A5 — `BlixApps.Dispatch`, for the many-apps-in-one-assembly case

A runtime helper an app's `Main` calls: reflect the *current* assembly, match `--blix-app <name>`,
invoke it. About sixty lines.

```csharp
static int Main(string[] args) => BlixApps.Dispatch(args) ?? RunTheApplication(args);
```

This is what lets RTSGame delete its if-chain **one scenario at a time** rather than in one commit.

---

## Proving it

The negative controls, because a layer that can only succeed is not a layer:

- A method with a wrong signature carrying `[BlixApp]` **fails the build, naming it**.
- Two apps with the same name in one project is an **error naming both**, not a silent last-wins.
- `blix run` on an unknown name **lists what exists** and exits non-zero.
- An index older than its assembly is **reported as stale**, not used silently.
- `blix ls` works with `obj/` deleted — **discovery does not require a build.**
- An undeclared csproj is still runnable by its own name.
- A declared app that has been deleted from the source disappears from the index on the next build,
  without anyone editing a file.

---

## Stage B — migrate enough that the patterns are established

Not everything. Enough that every shape has a worked example and there is nothing left for rot to
creep into. One of each:

| shape | case | what it proves | |
|---|---|---|---|
| many apps, one assembly | RTSGame's scenarios | the if-chain deletes incrementally | **done** — 3 of ~35 |
| many assemblies, one project | the character lab | a project is a folder, launchers go | **done** — `src/Character/` |
| one app, one assembly | the character probe | convention alone is enough | **done** — undeclared, still runnable |
| headless tool | the test suites | `blix run` is how verification is invoked | **done** — all five, plus the RTS gate |
| headed application | the toolchain viewer | a window changes nothing about addressing | **done** — `view`, `shot`, `room`, `room-shot` |
| Blix's own | the cooker | Blix's tools are not special | **done** — `Blix.Tools.Cook.Program`, `[BlixApp("cook")]` |

**Stage B is closed.** The cooker was the last cell, and the row's point was the whole of why it
mattered: a launcher layer whose own cooker is the one thing it cannot address is a layer with an
exception in it. Top-level statements have no method to hang an attribute on; every other tool here
already declared a `Program` class, so the cooker now matches them and nothing about how it works
changed.

**The character lab is the case that forces the folder question**: it is four sibling directories
under `src/`, and a project is one folder. Moving it is what makes `blix.project` mean something.

---

## What this arc will not build

An editor · a plugin system · a host that apps live inside · a required contract · a project
template · packaging or distribution · a GUI shell.

**And explicitly not the render-graph layer.** Ten applications declaring ten graphs is real and is
the next thing worth looking at, and it is a different arc with a different argument. This one ends
when a project's apps and tools can be found and run.

---

## Order, and why

A1 → A2 → A3 → A4 → A5, then prove, then B.

The layer is **additive at every step**: nothing in the tree references it until something chooses
to, and stage A can land complete with zero migrations. That is deliberate — the migration is where
the judgement is, and it should happen against a layer that is already proved rather than alongside
one being designed.

The honest stopping point is after A. At that point a project can declare what it has, and one
command finds and runs it. Whether the tree moves onto it is then a decision made with the thing in
hand rather than on paper.

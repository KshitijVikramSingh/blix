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

**And it is worse than publish.** A library project stages its compiled shaders
with `<None Include="Shaders\*.spv">`, an item glob **evaluated at project
load** — before the build writes a single `.spv`. On a clean tree it therefore
matches nothing, and no consumer's output receives the shaders. Four projects
do this. It has been true for as long as the glob has existed and nobody saw
it, because the `.spv.refl.json` sidecars beside them *were* committed (the
`*.spv` ignore does not match a name ending in `.json`), so the output
directory existed and was populated by the one artifact that happened to be in
git.

Untracking those sidecars on 2026-09-28 exposed it, on CI, which is the only
machine here that builds from a clean tree. They were put back, with their
churn, because the alternative was shipping a half-fix: a staging target that
runs at the right time and with the right item list still did not reach a
consumer's output, and chasing that mid-landing was the wrong trade.

So stage A owns one problem in two costumes: **generated build output that does
not reliably reach an output directory** — `dotnet publish` ignoring it at one
end, and an item glob evaluated too early at the other. The tree already has the
answer in outline: `BlixStageCookedAssets` declares its items *inside* a target
for precisely this reason, and says so in a comment. Whatever fixes this should
let the sidecars go back to being ignored, and should delete the three
hand-copied globs from `run-vulkan-sponza.sh` as its acceptance test.

The machinery already knows what a project is (`blix.project`), what
applications it contains (`.blixapps.json`), which has an apphost, and how
shaders and cooked assets are staged. What is missing is
*application → target → publish build → staging → native closure → bundle*.

One enrichment it needs: the app index records the assembly and apphost, but
publishing is a **source-project** operation, so the indexer should record the
originating `.csproj`.

**Acceptance — and "double-clickable" is not part of it.** `clone → build →
publish → Bulwark.app`, with every managed, shader and cooked output arriving
through build declarations and no application-specific copy script, and the
bundle running when launched **with the development environment**.

That wording was tightened after measuring it. The bundle publishes correctly
and runs clean at exit 0 with the Homebrew Vulkan variables set; double-clicked
it dies, because `MoltenVkBootstrap` and GLFW find `libvulkan` through
`DYLD_FALLBACK_LIBRARY_PATH` and `VK_ICD_FILENAMES`, which a shell exports and
Finder does not. So the dependency is not merely *installed* on the build
machine, it is *discovered through environment a terminal happens to provide* —
and putting the loader inside the bundle where it needs no environment is
runtime closure. **Double-clicking belongs entirely to B, even on the machine
that built it.**

**Done:** `blix publish <app> --target osx-arm64` produces
`dist/<app>/<rid>/<App>.app` with `Contents/{MacOS,Resources,Info.plist,PkgInfo}`.
The app index now records its originating `.csproj`, because publishing is a
source-project operation and rediscovering that from an assembly path is
guesswork. `run` and `publish` share one resolver, so a name cannot mean two
different apps.

**One target per invocation**, and `--target osx-arm64` stays singular: designing
for a second before it exists is the speculation this tree avoids. Internally,
resolve it once into a value carrying the RID, platform and architecture rather
than threading `"osx-arm64"` around as a semantic string — not a target
abstraction, just the recognition that a RID is not a packaging policy, and that
`win-x64` will want different closure behaviour even when .NET hands back the
same kind of string. CI can invoke it three times later.

## B — runtime closure

**The one genuinely new design question in this plan:** what constitutes the
runtime closure of a Blix application, per target?

**Acceptance is a clean machine.** Take `Bulwark.app` to a Mac with no
Homebrew, no installed .NET, no Vulkan SDK and no OpenAL, and it launches. That
sentence *is* the definition of runtime closure, and it is the only test that
cannot be passed by accident on a developer's box.

### Done, 2026-09-28 — the bundle is closed and signed

`blix publish` assembles the closure itself: it copies the loader, MoltenVK and
OpenAL Soft out of the build machine's Homebrew into `Contents/Frameworks`
(resolving the symlink chain into the Cellar, since a link to a directory the
target machine does not have is not a dependency that travelled), writes a
bundle-relative ICD manifest, moves the assets to `Contents/Resources`, and
signs.

Four findings, each measured, each of which had a wrong belief in front of it:

- **Pre-loading `libvulkan` does NOT satisfy GLFW.** `MoltenVkBootstrap`'s
  header claimed dyld would answer a later bare-name `dlopen` from the mapped
  image. It does not — tested with `libvulkan.dylib`, the exact
  `libvulkan.1.dylib` soname, and a bare-leaf install name plus re-sign.
  `DYLD_FALLBACK_LIBRARY_PATH` was doing all the work.
- **So the process re-execs itself**, with the path computed from
  `Environment.ProcessPath`. The alternatives were measured and lost:
  `LSEnvironment` works but bakes an absolute path and dies the first time the
  `.app` is moved; a launcher shim adds a second binary to sign. The re-exec
  keeps working when the bundle moves, which is the property that matters.
  `setenv` + `execv`, because .NET's environment API does not reach `environ`.
- **`Environment.SetEnvironmentVariable` never reached the Vulkan loader.**
  On Unix it writes .NET's own dictionary and leaves `environ` alone, so
  `VK_ICD_FILENAMES` had been a no-op since it was written — invisible because
  the loader's built-in search covers Homebrew's prefix anyway. In a bundle it
  was not invisible: the loader found the bundle's manifest *and* Homebrew's,
  mapped two copies of MoltenVK into one process (objc reported duplicate
  `MVKBlockObserver`), and used the installed one. Native `setenv` fixed it —
  and unlike `DYLD_*`, it needs no re-exec, because the loader reads it at
  `vkCreateInstance`.
- **codesign refuses a bundle with data in `Contents/MacOS`.** One cooked
  `core.textures/` sidecar — a directory with an extension — is read as a
  malformed nested bundle and fails the whole signature. `--deep` is also
  required, because a self-contained publish puts managed `.dll`s beside the
  apphost and codesign will not seal those unsigned either. Assets therefore
  live in `Contents/Resources`, and `Blix.Core.AppFiles` is the one place that
  knows to look across for them — replacing eleven hand-written copies of
  `Path.Combine(AppContext.BaseDirectory, "Assets", …)`, every one of which
  would have been wrong in a published bundle and *quietly* wrong: Bulwark
  drew primitives instead of its turrets and exited 0. Pinned by
  `Blix.Test.Graphics` Section **BH**.

Two earlier dead ends are now moot but should not be re-tried: a bundled
Khronos loader failing `vkCreateInstance` with `ErrorExtensionNotPresent`, and
a loader-less bundle failing in Silk's own name resolution. Both were artefacts
of requesting `VK_KHR_portability_enumeration` unconditionally — a *loader*
extension, absent when talking to MoltenVK directly. It is now asked for only
when the runtime offers it, and the bundled loader works.

**What was measured on the finished bundle**, moved out of its publish
directory, with no environment set: exit 0; one ICD manifest; one driver, the
bundled one; zero images mapped from `/opt/homebrew`; OpenAL resolving to
`Contents/Frameworks`; `codesign --verify` valid and satisfying its designated
requirement; and a Launch Services double-click running all 12,000 frames in
the same 10.0s as a direct run.

**A correction, 2026-09-28.** The commit that made publish judge its own output claimed
signing failure was fatal. It was not: the `throw` sat inside a `try` whose
`catch (Exception)` swallowed it, so a failed signature printed and publish returned 0.
Measured with a bogus identity — exit 0, and the message arrived doubled, which was the
catch wrapping its own throw. The catch is now narrow, and the signature is VERIFIED
after it is applied rather than assumed from codesign's exit code, so the order is
assemble → verify closure → sign → verify signature. Both failures park the bundle.

**The acceptance test above is still unrun.** Every measurement here was taken
on the machine that built the bundle, and "zero images from `/opt/homebrew`" is
the strongest available proxy for a clean Mac, not a substitute for one. No
clean Mac or VM is available — that is the one open thing in this stage, and it
is an access problem rather than a work item.

### The distinction the stage settled

**Developer dependencies** — `glslc`, `spirv-cross`, the validation layers —
stay outside. They run on the build machine and never ship.

**Runtime dependencies** — the Khronos loader, MoltenVK, OpenAL Soft — go in
`Contents/Frameworks`. Both loader filenames are needed, not one: GLFW dlopens
`libvulkan.1.dylib` and Silk.NET's resolver asks for `libvulkan.dylib`, and a
bundle carrying only one gets past whichever asks first and dies on the other.

Development still assumes Homebrew — `./blix` sets `DYLD_FALLBACK_LIBRARY_PATH`
and the ICD, and both probes fall back to it. That stays, and is now explicitly
the *second* branch: a bundled runtime wins, so a developer machine that happens
to have Homebrew exercises what was shipped rather than what is lying around.

The one thing knowingly left crude: the bundle is sourced from the build
machine's Homebrew, so a publish inherits whatever version that machine has.
Pinning the runtime is a real question and not this one.

Note `spirv-cross` became load-bearing for a fresh clone on 2026-09-28, when the
`.spv.refl.json` sidecars stopped being tracked. It is a documented prerequisite
and in CI, so this is consistent — but it is now a *build* dependency with teeth.

## C — Windows bring-up, as an audit

`Blix.Runtime.Silk` is not a macOS runtime. It is a desktop Silk runtime verified
only on macOS. The portability work is therefore not "abstract macOS out" but
"find the assumptions that survived because no second platform punished them".

**Do not** build an `IPlatform` abstraction. Silk already is most of one — and
the audit below is the evidence: what is macOS-specific is small, quarantined,
and in two cases exists *only* because of dyld.

### The audit, 2026-09-28

Enumerated, not counted — the earlier "five OS conditionals" was stale.

| | |
|---|---|
| `MoltenVkBootstrap.EnsureLoaded` | early-returns off macOS |
| `VulkanGraphicsDevice.Init` portability bit | macOS-only, already conditional on the runtime offering it |
| `OpenALAudioDevice.TryOverrideMacOSLibraryPath` | early-returns off macOS |
| `Window.ApplyWindowIcon` | `SetWindowIcon` **is** the Windows/Linux path; the macOS branch adds the dock tile |
| `BuildMeshopt` / `BuildBc7` targets | `IsOSPlatform(OSX)`, and staging is guarded by `Exists()` |

Four in C#, two in MSBuild, all quarantined. Nothing to abstract.

**The headline, measured rather than assumed: `blix publish --target win-x64`
already works from macOS.** It produced an `.exe`, a full self-contained runtime,
`glfw3.dll` and `cimgui.dll` from the NuGet runtime packs, and **zero** `.dylib`
leakage. This matters because it splits a question the plan had as one: the cook
runs on the *host* whatever the target RID, so the native-library problem below
blocks **building on Windows**, not **producing Windows binaries**.

What the audit found, in the order it matters:

- **`MeshRecipe.DefaultSimplifier` had no fallback and no message.** `Bc7Native`
  has probed `Available` and degraded to the managed encoder since it was
  written; the simplifier did neither, so a missing native threw
  `DllNotFoundException` from inside a P/Invoke — during a *build*, since
  cooking is a build step. It now fails at the entry with a sentence naming the
  file, the target that builds it, and the fact that cross-publishing is
  unaffected. Verified by hiding the dylib and cooking.
- **Three places spelled the filename, all `.dylib`.** The resolver table,
  `Bc7Native.LibPath` (which the plan had not found), and nothing shared between
  them. `NativeLibraries.FileName` is now the only place that knows, and answers
  `lib*.dylib` / `lib*.so` / `*.dll`. Pinned by `Blix.Test.Graphics` Section
  **BI**, which also checks every `DllImport` name is one the resolver answers —
  an unregistered one falls through to bare-name probing, the exact path that
  does not reliably work.
- **A `win-x64` publish ships no OpenAL native.** `vulkan-1.dll` is correctly
  absent (the GPU driver installs it); OpenAL Soft is neither a system library
  nor in a runtime pack. This is stage B's closure question wearing a Windows
  hat, and it is **open**.
- **`blix.cmd` now exists**, and is a third the length of `./blix` for a reason
  worth recording: almost all of the bash script is a dyld workaround, not a
  front door. Windows resolves DLLs from the executable's directory, the loader
  is in System32, and the ICD comes from the driver registry. What is left is
  the bootstrap that was always the actual front door. **Unrun.**

Two things the audit expected to find and did not:

- **Cooked artifacts are already separator-safe.** `CookStamp` normalises
  recorded paths to `/` with a comment naming this exact reason, and
  `MeshRecipe` does the same for the paths it writes into a `.blixmesh`. Every
  other `GetRelativePath` builds a filesystem path or a console message.
- **`glslc` and `spirv-cross` already fall back to `PATH`**, which is what the
  Vulkan SDK gives you on Windows. No change needed.

### What is left, and why it is not written

**The native toolchain branch.** `BuildMeshopt` and `BuildBc7` shell out to
`clang++ -dynamiclib`. Linux wants `-shared`; Windows wants a different compiler
entirely. Writing either without a machine to run it on is how you get a
confidently wrong build script, and the failure is now loud and named rather
than silent — so this waits for the machine rather than for a guess.

**Acceptance is the same shape as stage B's and has the same gap:** clone on
Windows, build, run a demo. Nothing here has been run on Windows. `blix.cmd` in
particular is written from the audit and should be treated as a first draft.

## D — the CI matrix

**Why it comes straight after C:** a `windows-latest` runner *is* the Windows
machine the audit did not have. `blix.cmd` and the Windows branch of the native
build were both written from reading rather than running, and this is what turns
them into ordinary work with a feedback loop.

What it can and cannot be, settled 2026-09-27 and unchanged:

- **Static checks travel everywhere.** Sections BG, BH and BI need no GPU and run
  in milliseconds on every platform.
- **Pipeline-time breakage needs a booted application.** CI runners have no
  Vulkan device and will not soon. That stays a **pre-commit** concern,
  deliberately: the spike to make a GPU work in CI is not worth its cost.

**Measured, and better than the above assumed: not one of the seven suites
creates a `VulkanGraphicsDevice` or a `Window`.** So the Windows job runs the
*whole* gate rather than a chosen subset. The deviceless/device line falls
exactly on the suites/demos boundary already.

### What landed, 2026-09-28

- **A second job, not a matrix.** The two platforms share the build and the gate
  and agree about nothing else — brew versus an SDK installer, a front door that
  works around dyld versus one that need not. A matrix would express that as
  `if:` on most steps, which is a matrix in name and a fork in fact.
- **The native build targets became platform-aware**, which stage C had
  deliberately left as a guess. It is no longer a guess, because the obstacle
  turned out to be findable by reading the sources rather than the toolchain:
  `MESHOPTIMIZER_API` is an empty macro by default, so a Windows DLL exports
  *nothing*, and `blix_bc7.cpp` had only `extern "C"`. Both now carry an explicit
  export switch. Unix keeps working because default visibility exports
  everything, which is why this was invisible.
- **The MSBuild side had the same duplicate spelling the C# side did** — the
  staging step named `libmeshoptimizer.dylib` in a second place. Both halves now
  derive the name, and the two must agree: one names the file the build writes,
  the other the file the loader opens.
- **A failed native build now warns rather than stopping**, because the two
  natives differ in consequence: BC7 falls back to the managed encoder and costs
  time, while meshopt has no fallback and fails the cook with the message stage C
  added.

**Linux is deliberately absent.** A second red job teaches nothing the first has
not said; the shape of what Windows needs should be known before it is copied.

**The Windows job has never been green.** That is its purpose, not a defect.

## E — input state

Renamed. "Keyboard and gamepad completion" was the pressure; what it turned out to
need was the abstraction underneath both.

**The engine owed a mechanism and was charging every game for it.** `IInputHandler`
reported platform events, and a platform event is almost never what a game wants to
know. Three reconstructions of the same missing thing had grown in the tree:

- a `HashSet<Key>` filled on key-down and emptied on key-up — TankArena (11 reads),
  Character.Room (7), Pong, VulkanLit, Sponza;
- one `bool` per key doing that job by hand — Bulwark's four orbit flags, and a
  `dragging` / `mouseLook` / `orbiting` / `panning` bool in four more places, each a
  copy of a button's `Down` kept in step across two callbacks;
- gameplay run straight out of the callback — `StartWave()`, `Restart()`, a pause
  toggle — which meant it happened at platform-event time rather than at a point the
  loop had chosen, and repeated as often as the backend chose to repeat.

None of that is policy. Working out that a key went down *this tick* is reusable
mechanism, and mechanism is the engine's job. So `InputState` is a frame-stable
snapshot the host computes once per update, and the callback interface is **deleted**
rather than kept alongside it — two paths that can disagree about gesture ownership is
the same failure this tree keeps meeting.

### The shape

```
Silk events + sampled devices
        │  (event time)
        ▼
  physical state  ──────────→  runtime diagnostics OBSERVE here (F1, F12, `)
        │  GestureOwnership — who owned this press
        ▼
  owned state
        │  BeginTick() — exactly once, immediately before OnUpdate
        ▼
  InputState — what the game reads
```

`GestureOwnership` was already the middle layer and keeps its whole job. The runtime's
diagnostic shortcuts no longer `return` after handling a key: while `Key.F12` could not
be named, taking it was invisible; now that a game can bind it, silently removing three
keys would be a hole nobody could see from inside their own code.

### What it refuses to do

No actions, no bindings, no contexts, no chords, no rebinding, no controller profiles,
no engine-wide deadzone, and no idea that Space might mean jump. Sticks report `[-1, 1]`
and triggers `[0, 1]` as the backend gives them. `InputDeadzone.Radial`-style helpers
are reusable maths and can come later; a deadzone applied for everyone is aiming feel,
which is the game's.

### Decisions worth keeping

- **Three fields, not two.** `Down`/`WasDown` cannot express a key tapped and released
  inside one tick, which on a 16 ms frame is an ordinary thing for a person to do. So
  `Down: false, Pressed: true, Released: true` is a real state, not an illegal one.
- **`Pressed` is a tick transition, not a platform event**, so auto-repeat produces one
  press however chatty the backend is — and what the backend actually does stops being
  a question anyone has to answer.
- **Position is sampled, delta and wheel are summed.** Different kinds of number.
- **Focus loss and device loss are one rule**: both synthesise releases, because both
  leave a game holding an input that stopped existing.
- **`IRenderHost.Input`**, not a `Game` base class. Input is a host capability like the
  cursor and the refresh rate, and inheritance is the shape this engine has refused.

### Gamepads

Sampled once per tick, not subscribed. Silk offers button and axis events too, and taking
them would mean two models in one layer — edges for buttons, and for axes a threshold
nobody can justify deciding when a stick has moved enough to deserve one. Reading the lot
before the flip makes a pad exactly as analysable as a keyboard.

Indexed by the backend's id, never by position: unplug the first of two pads and a
positional index silently hands the game a different controller than the player is
holding. A pad that is not there reads neutral rather than null, so controller support is
not two code paths.

A pad is kept for exactly one tick after it vanishes, which is the only reason a game can
notice at all. That tick carries `DisconnectedThisTick`, the synthesised releases and the
zeroed axes — the last of which is the point: a pad unplugged at full throttle never sends
its trigger back to zero, and a game with no `ReleaseAll` accelerates forever with nothing
in its own logic to explain it.

Both lifetime edges are recorded live and revealed at the flip, like a button's press.
Setting the public flag directly is the obvious thing and it is wrong in both directions —
a connect cleared by the very flip that should show it, a disconnect forgotten before
anything reads it. The suite found each separately, before any of it was wired to Silk.

### Two faults review found in the seam, not the model

The abstraction was right and the bridge to the backend was not. Both were invisible
from inside Blix and both needed the backend's own behaviour read rather than assumed.

**Triggers arrived on GLFW's scale, not Blix's.** GLFW reports all six gamepad axes in
`[-1, 1]`, triggers included, so an untouched trigger is `-1`. Silk forwards the number
untouched — confirmed by disassembling `GlfwGamepad.Update`, which contains no
floating-point constants at all. Blix promises `[0, 1]`, so the bridge now converts, and
the suite pins `-1 → 0`, `0 → 0.5`, `+1 → 1`. Left alone it was quietly wrong rather than
obviously wrong: a controller resting on a desk reports both triggers fully pulled.

**Focus loss did not survive a polled device.** A keyboard stops being eligible by
itself — the platform stops sending its events to an unfocused window. A pad has no
events to stop, so releasing everything on focus loss was undone one tick later by the
next sample, and a game kept driving while the player was in another application. The
invariant held inside `InputState` and the host defeated it on the way in.

That is a refinement to the layering worth keeping:

```
physical device state
        │
        ▼
application eligibility   ← gesture ownership for events, FOCUS for polled devices
        │
        ▼
InputState live state
        │  BeginTick
        ▼
snapshot
```

`IRenderHost.Input` hands out `IInputState`, the reading half. The concrete class keeps
its recording surface, because a host is not the only legitimate driver — a test, a
replay and a recorded demo are the same shape — but game code receives a type it cannot
rewrite mid-frame. "Fixed for the length of an update" is enforced rather than promised.

### Breaking, and deliberately

`IInputHandler` is gone. Keypad digits are their own keys rather than aliases of the
number row — below bindings, a layer is not entitled to discard the distinction, and a
binding can always map both onto one action while the reverse is impossible.

**RTSGame pins its engine version and migrates on its own schedule.** What it will need:
implement no input interface, read `host.Input` in its update, replace `shiftHeld` and
the four pan/turn bools with `Down`, and handle `Key.Keypad0..9` alongside
`Key.Number0..9` if numpad control groups should keep working.

## F — the character experiments

**Placed, 2026-09-28.** These were at `src/Character/`, named `Blix.Labs.Character*`,
with their own project marker — which made `blix ls` present three peers: `blix`,
`character`, and `demos`. One room got equal billing with the entire demo collection,
and "Character" as a bare noun beside the engine's projects reads as a *feature* the
engine has. It does not have one.

The repository had already written down the test, in the commit that retired the
toolchain lab: **a thing is Blix's when its subject is a Blix format or Blix's own
health.** A character controller is neither. `BoneMask`, `PoseBlend` and `RootMotion`
are Blix's and live in `src/Blix/`; the thing using them is a project built on Blix,
which is what `src/Demos/` already is.

So: `src/Demos/Character/`, `Blix.Demos.Character*`, and the marker folded into
`demos` — which had declared **no gate at all** until this arrived carrying one. Eleven
demos and nothing verified, because a demo proves itself by being run and looked at. An
experiment is small enough to have an answer, so it can carry a probe; each new one
appends to that line.

The "lab" vocabulary is gone with it. It claimed an open question where the intent was
a **named collection of small character-ish experiments** — model loading and viewing
(which became the view tool), contact, movement, basic physics, and mixed or
multi-axis ones later. `LabReport` and `LabTrace` became `RoomReport` and `RoomTrace`,
matching the `Room*` family already there.

### What is in it

One shared library — room, motor, camera, renderer, resolver — and three roots over it:
`room` (walk it), `room-shot` (capture it), and the probe (87 assertions, headless).

### The open work

**The prerequisite nothing lists: there is no body in the room.** It draws a capsule,
and has zero references to `ClipPlayer`, `AnimationClip` or `Pose`. Every consumer of
`BoneMask` and `RootMotion` in the tree is a tool, a test or the preview lab — none is a
character that moves. So "contact drives weights" is not two existing things being
wired together; a skinned Rogue has to stand in the room first, and the rig-fit against
a 0.35 m radius, 1.8 m capsule is the unknown that should surface on its own rather
than tangled in a blending design.

**Contact drives weights, and there are no states.** Speed and groundedness come out of
the resolver and drive blend weights directly. No states, no transitions, no dwell
timers. `BoneMask`'s first consumer that is not a tool: locomotion on the legs, a
one-shot on the upper body. The Rogue has what this needs — `Walking_A/B/C`,
`Running_A/B`, `Idle`, and `Unarmed_Melee_Attack_Punch_A/B`.

*Negative control:* the mask set to `All` must visibly break the legs, and set to `None`
must leave the walk bit-for-bit unchanged. A layer that changes nothing and a layer that
changes everything are a mask's two failures, and both are invisible unless asked for.

**And then the clip drives the contact.** Root motion says how far, the room says where
you can go. Honest expectation unchanged: 4 travelling clips of 76, so this may end a
**decided-no**, and reaching that with the instrument built is the output.

**Acceptance is from the chair.** Walk the room: ramps, stairs, ledges, walls. Every
stage of this arc had at least one fault only a person watching could see, and four of
five were **legibility, not mechanism**.

**Input is no longer in the way.** "Walks and punches" is `Down` for the locomotion and
`Pressed` for the one-shot, from the frame's own answer. The room kept its own held-set
for this until stage E deleted it.

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

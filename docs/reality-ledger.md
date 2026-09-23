# Blix reality ledger

> Migration aid, not permanent product documentation.
>
> Verified against `a20f42095647` on 2026-09-21. This file records what the
> checkout currently does so the canonical documentation can be rebuilt from
> evidence rather than by editing old prose until it sounds plausible. Delete
> this ledger once every item has a canonical home and the drift checks exist.

## What this ledger is for

Blix's written record currently mixes five different kinds of statement:

1. current user-facing instructions;
2. current architectural contracts;
3. design plans, including plans whose work is complete;
4. experimental findings and measurements;
5. source comments that preserve the route to a decision.

Those are all useful, but they have different lifetimes. During the
documentation catch-up, a claim moves from here into one of four destinations:

| Destination | What belongs there |
| --- | --- |
| Canonical documentation | Current setup, workflow, concepts, supported paths, and public contracts |
| Generated reference | Projects, apps, recipes, commands, formats, tests, and other enumerable facts |
| Historical record | Completed arcs, rejected alternatives, measurements, and superseded designs |
| Source or test | Local invariants, ownership, units, lifetime, failure modes, and executable guarantees |

This is a ledger of facts to place, not a fifth destination.

## Current product model

Blix is still a library rather than a control-inverting framework: applications
own their world representation, update policy, render-graph composition, draw
grouping, culling, and LOD policy. That remains the stable center.

What has changed around that center is substantial:

- A **project** is a folder selected by the nearest `blix.project` marker.
- An **app** is any runnable entry point built on Blix, headed or headless. A
  project may contain many apps and an assembly may declare many apps.
- `./blix` is the repository front door. It discovers apps from build-generated
  indexes and supports listing, running, cross-project addressing, and a
  project-declared verification gate.
- Tools are ordinary apps. `view`, `shot`, `inspect`, `check`, and `cook` do not
  require a parallel launcher or registration system.
- Asset cooking is a declared, incremental build transformation. Blix ships
  recipes, and a project can ship another recipe through the same mechanism.
- `Blix.Tools.Studio` is an optional, opinionated composition for tools and a
  reference look. It is not a renderer imposed by the engine.
- Vulkan Sponza is a bespoke renderer-research application. It consumes and
  pressures reusable engine mechanisms; it is not the basic application
  template.

Evidence:

- [`../blix`](../blix) — the front door and its environment/bootstrap contract.
- [`../blix.project`](../blix.project) — this project's name and verification gate.
- [`../src/Blix.Core/BlixAppAttribute.cs`](../src/Blix.Core/BlixAppAttribute.cs) — the app declaration contract.
- [`../src/Blix.Cli/Program.cs`](../src/Blix.Cli/Program.cs) — project discovery, app resolution, launch, and gate execution.
- [`../Directory.Build.targets`](../Directory.Build.targets) — generated app indexes, shader compilation/reflection, and asset cooking.

## Project and layer inventory

The checkout currently contains 50 `*.csproj` files and four project markers:

- `blix` — the engine, its own tools, and its test suites;
- `demos` — executable specifications and complete sample games;
- `character` — the isolated character/contact laboratory family;
- `rts` — a separate, co-located game and its project-owned cooking recipe.

The canonical [architecture map](architecture.md#project-map) now describes
ownership and dependency direction rather than maintaining a brittle edge-by-edge
ASCII snapshot. `ProjectReference` items remain the source of truth for exact edges;
the conceptual layers the map explains are:

| Layer | Current responsibility | Principal projects or directories |
| --- | --- | --- |
| Contracts | Host, input, UI, views, app declarations | `Blix.Core` |
| Cooked contracts | Recipe declarations, stamps, preambles, load reports and refusals | `Blix.Cooked` |
| Command model | Backend-neutral graphics handles, commands, resources, layouts | `Blix.Graphics` |
| Backend | Vulkan execution, render graph, reflection, pipelines, residency | `Blix.Graphics.Vulkan` |
| Images | Decode, environment/probe processing, cooked image formats, CPU tonemap | `Blix.Graphics.Images` |
| Diagnostics | Values, controls, timings, events, views, selection, retained history | `Blix.Diagnostics`, `Blix.Diagnostics.Overlay` |
| Geometry | Bounds, primitives, intersections, sweeps, collision worlds | `Blix.Geometry` |
| Assets | Importers, runtime asset data, cooked mesh/font readers | `Blix.Assets` |
| Recipes | Blix-owned mesh, texture, probe, font, and visibility transformations | `Blix.Recipes` |
| Render primitives | Mesh upload/bundling, deferred upload, instancing, sprites, particles, fullscreen work | `Blix.Render` |
| Game-facing library | Loop, transforms, cameras, animation, physics, glTF composition | `Blix` |
| Runtime | Silk window/input, Vulkan surface, audio and diagnostics hosting | `Blix.Runtime.Silk` |
| Reference rendering pipeline | Studio renderer, look, model/rig/camera/gizmo composition | `Blix.Tools.Studio` |
| Tool shell | Reusable viewport/panel shell for headed tools | `Blix.Tools.Studio.Shell` |
| Workflow | Project/app resolution, indexing, verification, shared assertion tally | `Blix.Cli`, `Blix.Tools.Apps`, `Blix.Verify` |
| Tools | Inspect, check, cook, view, shot, shader build | `Blix.Tools.*` |
| Consumers | Demos, Character labs, and the separate co-located RTSGame | `src/Demos`, `src/Character`, `src/RTSGame` |

Not every project is a new architectural layer. The canonical architecture
should describe ownership and dependency direction, while a generated appendix
lists every project and edge.

## Expected developer workflow

The workflow implemented by the checkout is:

```sh
dotnet build Blix.sln
./blix ls
./blix run <app> [args...]
./blix run <project>:<app> [args...]
./blix test
```

`./blix <app>` is also accepted as the short form of `./blix run <app>`.

Important current behavior:

- The nearest `blix.project` scopes an invocation.
- From the repository root, `project:app` disambiguates apps below different
  project folders.
- `[BlixApp]` is indexed after build; the attribute remains the source of truth.
- An executable without a declaration remains runnable by convention.
- One assembly can expose several apps by calling `BlixApps.Dispatch(args)`.
- `blix test` runs the comma-separated gate declared by the selected project.
- Headed apps carry `Headed = true`; a headed gate leg should be bounded with
  `--frames` if it is ever included in a gate.
- `./blix` sets up the Vulkan loader environment and execs the apphost, avoiding
  the macOS/Homebrew `dotnet` shim problem.

Canonical documentation must stop teaching new applications to copy one of the
legacy `tools/run-*.sh` launchers. Those scripts may remain useful compatibility
or task-specific wrappers, but they are no longer the application model.

## Tool inventory and intended questions

The durable distinction between the asset tools is the question each answers:

| App | Question |
| --- | --- |
| `inspect` | What is in this source or cooked asset? |
| `check` | Does this asset satisfy the contracts Blix can judge? |
| `cook` | Which declared transformation produces the runtime artifact? |
| `view` | What does this model or rig look like under Blix's reference rendering pipeline? |
| `shot` | Can that view be rendered deterministically to an inspectable artifact? |

Other declared apps currently include the Character `room` and `room-shot`, the
Chassis tuning specimen, and RTS `selftest`, `movebench`, and `fightbench`.
Tests also carry fixture app and recipe declarations that prove the discovery
and dispatch machinery.

Canonical tool documentation should be organized by workflow rather than by
assembly name:

```text
discover -> inspect -> check -> cook -> view -> capture -> verify
```

Not every asset needs every step, and none of these tools should be described as
a hidden phase of another.

## Asset production and loading lifecycle

Canonical guide: [Assets](assets.md). This section remains as the archaeology
snapshot that led to it.

The current asset system is several cooperating mechanisms, not merely three
file extensions.

### 1. Declaration and build

A project declares source assets in its project file using `CookMesh`,
`CookTexture`, `CookProbe`, `CookFont`, or a general `BlixCook` item. Shared
targets translate those declarations into incremental inputs and outputs, invoke
the appropriate recipe, and stage newly produced artifacts in the same build.

Blix currently ships four discoverable recipes:

| Recipe | Transformation |
| --- | --- |
| `gmsh` | glTF/GLB to `.blixmesh` |
| `gtex` | PNG/JPEG to `.blixtex` |
| `gpro` | HDR environment to `.blixprobe` |
| `fnt1` | font source to `.blixfont` |

RTSGame supplies `omsh` from `RTSGame.Cooking`, proving that a project's recipe
uses the same declaration, index, and host as Blix's recipes.

### 2. Provenance and compatibility

Every cooked artifact has a common preamble carrying format version, recipe and
recipe version, source identity, parameters, flags, and the offset of the
format-specific header. Readers can therefore refuse stale or incompatible
artifacts with an actionable re-cook message.

`AssetLoadReport` records whether a load used source, cooked, fallback, or
missing data, as well as bytes, time, recipe, and warning. This is the observable
answer to whether a declared cook is actually used at runtime.

### 3. Deferred runtime work

Runtime loading has two different deferred stages:

- `AsyncLoadQueue<T>` performs heavyweight production or parsing off-thread and
  drains completed items on the calling thread within a per-frame budget.
- `ResourceUploader` budgets GPU texture work per mip. Cooked textures allocate
  a stable handle immediately, upload the smallest mip first, and sharpen over
  later frames without replacing that handle.

`GltfTextureLoader` adds glTF channel policy, source/cooked routing, neutral
fallbacks, deduplication through `TextureRegistry`, and explicit registry
sharing when two owners should share residency.

These are loading and residency mechanisms, not a general demand-paged streaming
system. The distinction must remain explicit in the canonical asset document.

## Rendering has two current tracks

### Studio reference rendering pipeline

`Blix.Tools.Studio` composes reusable engine capabilities into the reference
rendering setup used by Blix's own model and rig tools. `StudioLook` owns the
authored defaults: lighting, environment, shadow fit, exposure, tonemap, MSAA,
and other choices about how the reference should look.

This reference pipeline is deliberately optional:

- techniques and shader vocabulary belong to engine layers;
- Studio owns composition and policy;
- a tool can adopt the composition cheaply;
- a game may take all, some, or none of it;
- there is no `DefaultRenderer` in `Blix` core.

“Reference rendering pipeline” is the durable public name: it describes a
coherent, authored setup while retaining the fact that applications opt into
it rather than inheriting a universal renderer.

### Renderer research: Vulkan Sponza

Vulkan Sponza is the forward research and measurement path. Its current graph
goes materially beyond the README description and includes, among other work:

- cascaded shadows with caster/receiver reasoning and measured culling;
- a depth and normal pre-pass;
- a Hi-Z pyramid;
- GTAO and denoising;
- baked sky visibility and occupancy/albedo volumes;
- runtime probe injection, usage, sleeping, transport, and indirect light;
- a half-resolution incident-light field and resolve;
- colour TAA;
- temporally accumulated volumetric fog;
- GPU-driven indirect submission, culling, and SSE LOD;
- material extension response including sheen and transmission work;
- diagnostic views, A/B modes, censuses, captures, and path-traced references.

Sponza should document what is being researched, what has become a default in
that application, what was rejected, and which mechanisms were promoted to
shared engine code. It should not be the getting-started rendering example.

## Verification surfaces

The current verification story is broader than `dotnet build`:

- `blix test` runs the project-declared fast gate.
- `Blix.Verify` supplies the shared assertion/tally mechanism.
- `Blix.Test.Graphics`, `Blix.Test.Diagnostics`, `Blix.Test.Physics2D`,
  `Blix.Test.Physics3D`, `Blix.Test.Apps`, `Blix.Test.Studio`, and
  `Blix.Test.Recipes` form the root project's declared gate.
- The recipe leg covers shipped-format compatibility, native cooking
  capabilities, source-free cooked loading, driver behavior, and packaging
  invariants. Those are root engine claims, not a separate specialist tier.
- RTSGame has a quick `selftest` gate plus longer task-specific scripts and
  scenarios.
- Visual baselines and deterministic captures cover questions that scalar tests
  cannot answer.
- Sponza's A/B and capture modes are experimental instruments, not a general
  repository gate.

`Blix.Test.Recipes` joined the root fast gate when its scope grew from recipe
unit behavior into release-facing format, native-tooling, and packaging
contracts. Visual baselines and deterministic captures remain the deliberate
second tier for questions a scalar headless suite cannot answer.

## Confirmed drift to reconcile

This is the initial queue, not an exhaustive prose review.

- [x] README: replace launcher-centric usage with the project/app front door.
- [x] README: describe the declared recipe/build pipeline rather than only
      manually invoked Sponza cook commands.
- [x] README: update Sponza's capabilities without presenting its research graph
      as the ordinary Blix render path.
- [x] Architecture: regenerate the project graph and add the Cooked, Recipes,
      CLI, Verify, Studio, tool, Character, and project-cooking layers.
- [x] Architecture and API docs: remove the claim that an embedded rendered
      viewport is not achievable.
- [x] API roadmap: remove the particle system from pending work.
- [x] Renderer: split reusable primitives, Studio reference pipeline, and
      Sponza research; add the newer graph/resource capabilities.
- [x] Renderer correctness: GTAO and TAA now follow the render graph's
      match-swapchain resource generation and refuse history after target
      recreation. Froxel history remains separately owned and resets when its
      device-owned grid changes dimensions.
- [x] Demo relocation: repair the `src/Demos/*` project references that still
      resolve `..\Blix\Blix.csproj` under `src/Demos`, and reconcile Vulkan
      Sponza's pre-move `Blix.Demos.SponzaModern` asset include.
- [x] Demos/tools: stop calling the evolved Studio/View/Shot family merely a
      toolchain lab.
- [x] Assets: document declaration, recipes, stamps, reports, deferred CPU work,
      progressive GPU residency, registry identity, and project-owned recipes as
      one lifecycle.
- [x] Plans: classify each root `plan-*.md` as active, completed, superseded, or
      abandoned and move completed records out of the repository root.
- [x] HTML reports: retain measured reports as dated artifacts, but keep their
      status and measured revision visible in the documentation index.
- [ ] Source comments: separate current contract from experimental chronology,
      beginning with Sponza, Studio, build targets, and asset cooking/loading.
  - [x] Establish the repository rule and apply it to the shared build targets,
        mesh-recipe ownership header, and Studio renderer entry contract.
  - [x] Reconcile Studio's public look, graph-extension, view, and renderer
        contracts, plus Sponza's MSAA and GTAO/TAA history-sensitive paths;
        remove the obsolete GTAO slice/step controls after the shader made both
        loop bounds compile-time constants.
  - [x] Reconcile Studio rig/animation ownership and Sponza's transport/material
        shader contracts, including matching host defaults and asset-volume setup.
  - [x] Reconcile the remaining Sponza host-side measurement, culling, capture,
        and diagnostics chronology without discarding measurements that govern
        current defaults.
  - [x] Reconcile the remaining Sponza depth, incident-light, fog, and probe
        visualization shader commentary against their current pass contracts.
  - [ ] Reconcile asset loading/cooking implementation commentary beyond the
        build and recipe entry points.
    - [x] Reconcile lifecycle primitives, provenance and load reporting,
          deferred handoff, mip residency, and texture identity.
    - [x] Reconcile source, cooked, and fallback decisions across glTF, OBJ,
          texture, material, and font importers.
    - [x] Make `GltfIgnored` mode-aware: additional rig influences and the
          coloured static layout now reflect what each import actually consumes.
    - [x] Reconcile mesh, texture, probe, material, font, and scene-visibility
          recipe, baker, and cooked-format commentary.
    - [x] Close recipe identity gaps: stamp texture role, encoder backend, and
          native BC7 quality; format mesh split extent invariantly; decide
          whether uniform `gpro` accepts yaw; and reject or implement
          static-only mesh settings for rigged assets.
    - [x] Reconcile cook, check, and inspect tool commentary, scope, and exit
          semantics.
    - [x] Make `cook asset` enforce its packaging promise: fail missing referenced
          images, preserve material-channel texture roles, and refuse success
          while the mesh remains source-dependent.
    - [x] Make legacy driver incrementality use full stamp identity rather than
          mesh-format or source/destination timestamp shortcuts.
    - [x] Repair host accounting and argument edges: count unreadable outputs in
          `cook status`, distinguish ambiguous recipe claims, respect
          `CookOutcome.Wrote` in `batch`, and reject ignored extra options.
    - [x] Keep `check --rig` engine-level: Studio now enforces its own palette
          budget at load, its conformance suite checks both shaders, and mask
          naming policy no longer rejects otherwise valid rigs.
    - [x] Remove the out-of-place `cook mesh` source-glTF copy. Current
          `.blixmesh` carries geometry, materials, image references, hierarchy,
          and rigs; uncooked referenced images—not the glTF—are the remaining
          dependency, and `cook asset` owns standalone packaging.

RTS source journals are outside this engine-repository cleanup scope. RTSGame is
a separate game currently sharing the checkout; its comments and plans should
move with it if the game is extracted to another repository.

## Open questions

These do not block the first documentation pass, but canonical prose will need
an explicit answer.

1. **Legacy launchers.** Are `tools/run-*.sh` compatibility conveniences to keep
   and document secondarily, or should their eventual removal be part of the
   workflow migration?
2. **Sponza documentation cadence.** Which findings are stable enough for a
   current capabilities page, and which should remain dated experiment notes?

None of these requires guessing before the next step. The README and workflow
can describe the mechanisms that indisputably exist while leaving policy choices
clearly named.

## Evidence used for the next pass

- `blix`, `blix.project`, and `src/*/blix.project`
- `Directory.Build.targets`
- all `*.csproj` project references
- `[BlixApp]` and `[Recipe]` declarations
- `Blix.Cli`, `Blix.Core.BlixApps`, and `Blix.Tools.Apps`
- `Blix.Cooked`, `Blix.Recipes`, and `Blix.Tools.Cook`
- `AsyncLoadQueue`, `ResourceUploader`, `GltfTextureLoader`, and `TextureRegistry`
- `Blix.Tools.Studio`, `Blix.Tools.Studio.Shell`, View, Shot, Inspect, and Check
- Vulkan Sponza's current pass graph, shaders, diagnostics, and recent history
- the current README, canonical docs, root plans, and dated HTML reports

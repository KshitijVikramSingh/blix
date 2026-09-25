# Blix

Blix is a readable native game engine for C# and .NET. It is used as a set of
libraries, not as a framework that takes ownership of an application.

An application owns its world representation, update policy, render-graph
composition, draw grouping, culling, and LOD decisions. Blix supplies the
reusable mechanisms underneath: a typed graphics command model, Vulkan
execution, render graphs, cooked assets, deferred loading and upload,
diagnostics, audio, geometry and collision, animation, runtime hosting, and a
small set of rendering primitives.

The engine is intentionally explicit. There is no required ECS, scene format,
editor, scripting layer, fixed renderer, or engine-owned game loop.

## Start here

Blix currently targets .NET 8 and the Vulkan + Silk.NET runtime. On macOS,
install the native toolchain once:

```sh
brew install molten-vk vulkan-loader vulkan-headers vulkan-tools vulkan-validationlayers shaderc openal-soft
```

Then bootstrap the Blix front door, build the tree, and ask the project what it
contains:

```sh
./blix ls
dotnet build Blix.sln
./blix ls
./blix test
```

The first `./blix ls` builds the small app resolver and indexer when they are
absent. A fresh tree may then ask for one build before every app is indexed.

Run any discovered app by name:

```sh
./blix view --model path/to/model.glb
./blix view --rig path/to/character.glb --clip Walking_A
./blix inspect path/to/asset.glb
./blix check --model path/to/asset.glb
./blix rts:selftest
```

`./blix run <app>` is the explicit form; `./blix <app>` is its shorter twin.
Arguments after the app name belong to that app. `project:app` addresses an app
in a particular project when more than one project is visible.

See [Workflow](docs/workflow.md) for projects, app declaration and discovery,
bounded runs, verification gates, and how to add an application.

A game may keep Blix as a pinned source checkout rather than living in this
repository. The two imports under `build/` expose paths and build mechanisms
without adding engine references or application policy; the external-project
shape is documented in [Workflow](docs/workflow.md#using-blix-from-another-repository).

## The shape of the engine

The stable split is mechanism in the engine, policy at the call site:

| Layer | Owns |
| --- | --- |
| `Blix.Core` | Host, input, UI, view, and app contracts |
| `Blix.Cooked` | Recipe declarations, cooked provenance, load outcomes and refusals |
| `Blix.Graphics` | Backend-neutral handles, resources, layouts, and recorded commands |
| `Blix.Graphics.Vulkan` | Vulkan execution, render graphs, pipelines, binding and residency |
| `Blix.Graphics.Images` | Image decode, environment processing, cooked image formats and CPU tonemap |
| `Blix.Diagnostics` | Values, controls, timing, events, views, selection and history |
| `Blix.Geometry` | Geometry, intersections, sweeps, and collision worlds |
| `Blix.Assets` | Importers, runtime asset data, and cooked readers |
| `Blix.Recipes` | Blix's build-time asset transformations |
| `Blix.Render` | Mesh upload and bundling, deferred upload, instancing, sprites, particles and fullscreen work |
| `Blix` | The game-facing loop, transforms, cameras, animation, physics and glTF composition |
| `Blix.Runtime.Silk` | Window, input, Vulkan surface, audio and diagnostics hosting |

Applications compose these pieces directly. `Blix.Tools.Studio` sits beside,
not inside, the game-facing engine: it is an optional reference rendering
composition for tools, with an authored `StudioLook`. A game can take all, some,
or none of it.

## Projects, apps, and tools

A Blix project is a folder identified by `blix.project`. It can contain many
assemblies and many headed or headless apps. `[BlixApp]` declarations are
indexed during the build, so `./blix ls` discovers them without loading every
assembly. The same project marker may declare the apps run by `./blix test`.

Tools are ordinary apps:

| App | Purpose |
| --- | --- |
| `inspect` | Report what is in a source or cooked asset |
| `check` | Judge the asset contracts Blix can verify |
| `cook` | Run and report declared cooking recipes |
| `view` | View a model or rig using the Studio reference pipeline |
| `shot` | Render a deterministic model or rig capture |

The older `tools/run-*.sh` scripts remain for compatibility and specialized
setup, but they are not the pattern for a new application. Use `./blix` and an
app declaration instead.

## Assets: declared build, observable load

Asset cooking is part of the build rather than a command that must be
remembered. Projects declare mesh, texture, probe, font, or project-owned recipe
inputs in their project files. Shared build targets run only out-of-date recipes
and stage newly produced artifacts in the same build.

Every cooked artifact records its format and recipe versions, source identity,
settings, and provenance. Runtime loaders report whether they used source,
cooked, fallback, or missing data. Heavy CPU work can be produced off-thread and
drained within a frame budget; cooked texture mips upload progressively behind a
stable GPU handle, smallest first.

Blix currently ships `.blixmesh`, `.blixtex`, `.blixprobe`, and `.blixfont`
artifacts. Projects can declare their own recipes through the same mechanism;
`RTSGame.Cooking` is the working example.

See [Assets](docs/assets.md) for the full lifecycle: declarations, recipes,
provenance, runtime reports, deferred work, and residency ownership.

## Two rendering tracks

Blix deliberately distinguishes the Studio reference rendering pipeline from renderer
research.

**Studio** is the optional reference path used by `view` and `shot`. It composes
shared techniques into a good ordinary model/rig view and keeps visual policy in
`StudioLook`, outside engine core.

**Vulkan Sponza** is the forward research path. It is where heavy-scene asset
flow, GPU-driven submission, screen-space-error LOD, cascaded shadows, Hi-Z,
GTAO, probe-based indirect light, incident fields, TAA, temporal volumetric fog,
material response, and measurement tooling are stressed. Successful mechanisms
may move into shared engine layers; the complete Sponza graph remains bespoke.

![Intel Sponza rendered in Blix](docs/sponza.jpg)

Sponza's source assets are not committed. `tools/setup-sponza-modern.sh`
prepares the local asset tree; the application remains runnable through the
Blix front door once built.

## Demos and proving grounds

The repository contains complete games, focused executable specifications,
tools, and isolated laboratories:

- Pong, Runner, Tank Arena, and Bulwark are end-to-end playable games.
- Vulkan Hello, Graph, Lit, Instanced, Particles, and Chassis isolate engine
  surfaces.
- Vulkan Sponza is the renderer and heavy-asset research scene.
- Character labs isolate contact, controller, camera, rig, and capture work.
- RTSGame is a co-located but separate game project with its own simulations,
  scenarios, gates, tools, and cooking recipe. It is a consumer of Blix, not an
  engine subsystem; a future repository move does not change that boundary.

Use `./blix ls` for the runnable inventory. See [Demos](docs/demos.md) for the
current application catalogue, ownership boundaries, and verification routing.

## Documentation

- [Workflow](docs/workflow.md) — projects, apps, discovery, running, shared
  arguments, and verification gates.
- [Assets](docs/assets.md) — declared cooking, recipes, provenance, runtime load
  decisions, deferred work, and GPU residency.
- [Architecture](docs/architecture.md) — engine organization, host contracts,
  views, binding, and code locations.
- [Renderer](docs/renderer.md) — graphics commands, render graph, pipelines,
  shaders, and shared rendering techniques.
- [Game-facing API](docs/blix.md) — loops, transforms, cameras, animation,
  geometry, collision, audio, and picking.
- [Conventions](docs/conventions.md) — the design rules behind engine/caller
  boundaries and executable specifications.
- [Demos](docs/demos.md) — applications, proving grounds, tools, and how to verify them.
- [Plan status](docs/plans.md) — the active design records that still own open work.

These pages describe the current tree. Where prose and executable behavior ever
disagree, the code and tests are the present source of truth; correct the prose
instead of preserving a second historical account beside it.

## Shared application arguments

Applications that use `WindowOptions.FromArgs` inherit:

```text
--frames N       close after N rendered frames
--width N        set the window width
--height N       set the window height
--title TEXT     set the window title
--debug          start with diagnostics visible
--dump-frame N   write the diagnostics dump for frame N
```

Application-specific arguments remain owned by the application.

## Platform notes

- The root `./blix` script establishes the macOS Vulkan loader environment and
  execs an apphost. This avoids Homebrew's `dotnet` shell shim losing `DYLD_*`
  variables across macOS SIP boundaries.
- `BLIX_VK_VALIDATE=1` enables Vulkan validation layers.
- `BLIX_DIAG_INTERVAL=<frames>` controls the periodic console diagnostics
  cadence; `BLIX_DIAG=off` disables it.
- Keep shared GLSL ASCII. Shaders compile offline to SPIR-V through `glslc`.
- OpenAL Soft is required for working audio on current macOS; Apple's legacy
  OpenAL framework is deprecated and frequently silent.

Blix is under active development. APIs and compositions are still moving, but
the intended character is stable: serious native machinery that remains small
enough to inspect, understand, and change.

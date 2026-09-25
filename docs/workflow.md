# Workflow

This is the canonical guide to finding, running, and verifying work in a Blix
checkout. It describes the project/app model implemented by `blix`,
`Blix.Cli`, `[BlixApp]`, and the generated app indexes.

## First checkout

Blix currently targets .NET 8 and its headed applications use the Vulkan +
Silk.NET runtime. On macOS, install the native dependencies once:

```sh
brew install molten-vk vulkan-loader vulkan-headers vulkan-tools vulkan-validationlayers shaderc openal-soft
```

Bootstrap the front door before the first full build:

```sh
./blix ls
```

When absent, this builds two small pieces:

- `Blix.Tools.Apps`, which writes app and recipe indexes after an assembly is
  built;
- `Blix.Cli`, which reads those indexes, resolves an app, and launches it.

The bootstrap does not build every application. On a fresh tree, build the
solution once and list again:

```sh
dotnet build Blix.sln
./blix ls
```

An index is generated beside each built assembly. Discovery reads files rather
than loading every assembly, so it remains quick and can still report what was
last built when another part of the tree is temporarily broken.

## Using Blix from another repository

Blix is currently distributed as a source tree, not a set of binary packages.
An external game can pin that tree at `engine/blix` and keep one optional
`BLIX_ROOT` override for working against a sibling checkout. Its
`Directory.Build.props` chooses the checkout and imports the path vocabulary:

```xml
<Project>
  <PropertyGroup>
    <BlixCheckout Condition="'$(BLIX_ROOT)' != ''">$(BLIX_ROOT)</BlixCheckout>
    <BlixCheckout Condition="'$(BlixCheckout)' == ''">$(MSBuildThisFileDirectory)engine/blix</BlixCheckout>
  </PropertyGroup>
  <Import Project="$(BlixCheckout)/build/Blix.Source.props" />
</Project>
```

Its `Directory.Build.targets` imports the mechanisms after each consumer
project has declared its shaders and cooking items:

```xml
<Project>
  <Import Project="$(BlixRoot)/build/Blix.Source.targets" />
</Project>
```

The imports deliberately add no engine assemblies. Each application states the
libraries and build tools it actually consumes:

```xml
<ItemGroup>
  <ProjectReference Include="$(BlixSourceRoot)/Blix/Blix.csproj" />
  <ProjectReference Include="$(BlixSourceRoot)/Blix.Core/Blix.Core.csproj" />

  <ProjectReference Include="$(BlixCookProject)"
                    ReferenceOutputAssembly="false" PrivateAssets="all" Private="false" />
  <ProjectReference Include="$(BlixShaderCompilerProject)"
                    ReferenceOutputAssembly="false" PrivateAssets="all" Private="false" />
</ItemGroup>
```

The cook-host reference matters on the first build. Without it, an absent cook
host makes the guarded cooking target inapplicable; relying on a previous engine
build would produce the classic failure where a clean checkout behaves
differently from a developer's machine. A project that declares no cooked
assets does not need that reference. Likewise, only a shader-bearing project
references the shader compiler.

The app indexer is bootstrapped by Blix's launcher. An external repository can
keep this tiny `./blix` wrapper without changing the working directory:

```sh
#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
BLIX_CHECKOUT="${BLIX_ROOT:-$SCRIPT_DIR/engine/blix}"
exec "$BLIX_CHECKOUT/blix" "$@"
```

Project selection, index discovery, and the declared gate then remain owned by
the external repository's own `blix.project`.

The clean source-consumer sequence is the same shape as Blix's own checkout:

```sh
./blix ls       # bootstrap the indexer and resolver
dotnet build
./blix ls       # see the indexes produced by the build
./blix test
```

Pin the default checkout to an exact Blix commit or tag. `BLIX_ROOT` is a
development override, not a substitute for recording what revision the game
ships against.

## Projects

A project is a folder. The nearest `blix.project` at or above the working
directory selects it. If no marker exists, the nearest solution or repository
root is the fallback.

The marker is intentionally small:

```text
my-project

test: selftest, asset-check
```

The first non-comment line without a colon is the project name. The optional
`test:` line lists the apps that form that project's verification gate.

The current tree has four marked projects:

| Project | Folder | Gate |
| --- | --- | --- |
| `blix` | repository root | Graphics, Diagnostics, Physics2D, Physics3D, Apps, Studio, and Recipes suites |
| `demos` | `src/Demos` | none declared |
| `character` | `src/Character` | `Blix.Labs.Character.Probe` |
| `rts` | `src/RTSGame` | `selftest` |

Project scope follows the current working directory. From the repository root,
all projects below it are visible. From `src/RTSGame`, the `rts` marker is the
scope and a bare app name resolves within that project.

## Apps

An app is any runnable entry point built on Blix. A windowed game, a model
viewer, a headless asset check, a benchmark, and a test suite are all apps. The
only launcher-level distinction is whether an app opens a window.

List the apps visible from the current project:

```sh
./blix ls
```

Run one explicitly or with the short form:

```sh
./blix run view --model Assets/hero.glb
./blix view --model Assets/hero.glb
```

Arguments after the name are forwarded unchanged to the app. If the same name
is visible in more than one project, qualify it:

```sh
./blix run rts:selftest
./blix rts:selftest
```

Resolution tries an exact name, then case-insensitive equality, then an
unambiguous suffix after the last dot. Thus a conventionally discovered
`Blix.Test.Physics3D` can be addressed as `Physics3D` when no other visible app
has that suffix. Ambiguity is an error that lists the candidates.

## Declaring one app

An executable remains runnable by convention using its assembly name. Add
`[BlixApp]` when it needs a stable short name, a summary in `blix ls`, headed
metadata, or when one assembly contains several apps.

```csharp
using Blix.Core;

public static class Program
{
    [BlixApp("inspect-map", Summary = "report the authored map", Headed = false)]
    public static int Main(string[] args)
    {
        // app work
        return 0;
    }
}
```

An app method is static, takes either no arguments or one `string[]`, and
returns `void` or an exit code. The build-time indexer and runtime dispatcher
validate the same four signatures.

`Headed = true` says that the app opens a window. It lets discovery label the
app and lets a verification gate warn before starting an unbounded window. It
does not impose a base class or a hosting model.

The declaration belongs on the method it names. The generated index is derived
from that attribute; do not maintain a parallel app registry.

## Several apps in one assembly

An assembly can declare several app methods. Its entry point checks for a
launcher selection before performing its ordinary work:

```csharp
using Blix.Core;

public static class Program
{
    public static int Main(string[] args) =>
        BlixApps.Dispatch(args) ?? RunMainApplication(args);

    [BlixApp("selftest", Summary = "run the simulation invariants")]
    public static int SelfTest() => 0;

    private static int RunMainApplication(string[] args) => 0;
}
```

`BlixApps.Dispatch` removes the internal selector before invoking the selected
method. It returns `null` when the launcher did not select an app, which permits
an existing executable or hand-written dispatcher to migrate one branch at a
time. RTSGame uses this shape while its older scenario switches are gradually
converted.

## Headed applications

A headed application should let the runtime consume the arguments common to
every Blix window:

```csharp
var options = WindowOptions.FromArgs(args, WindowOptions.Default with
{
    Title = "My Blix app",
    Width = 1280,
    Height = 720,
});

using var window = new Window(loop, options);
window.Run();
```

The shared arguments are:

| Argument | Meaning |
| --- | --- |
| `--frames N` | Close after N rendered frames |
| `--width N` | Initial width |
| `--height N` | Initial height |
| `--title TEXT` | Window title |
| `--debug` | Start with diagnostics visible |
| `--dump-frame N` | Write the diagnostics JSON dump for frame N |

Unknown arguments are deliberately ignored by `WindowOptions`; they remain the
application's to parse. Bounded runs, sizing, diagnostics, and dumping should
not be reimplemented in each game or tool.

`Blix.Demos.Chassis` is the smallest executable specification of this surface:
an `IGameLoop`, optional `IUiSource`, optional `IInputHandler`, and a host-owned
bounded run. `Blix.Tools.View` is the fuller example with Studio composition,
an interface, input ownership, diagnostics, asset loading, and named views.

## Verification gates

Run the selected project's declared gate with:

```sh
./blix test
```

The launcher resolves each named app, runs all of them even if an earlier one
fails, and returns one final exit code. This is the quick, routine verification
tier chosen by the project; it is not a claim that every expensive scenario or
visual comparison belongs in every edit-build loop.

From a nested project, invoke the same root script while keeping that directory
as the project scope:

```sh
cd src/RTSGame
../../blix test
```

The current root gate is declared in `blix.project`. It covers graphics,
diagnostics, both physics layers, application composition, Studio, and the
asset recipe/cooking suite. The recipe leg belongs here because it verifies
shipped-format compatibility, native encoder availability, source-free cooked
loading, driver behavior, and packaging invariants; those are engine release
claims rather than an optional specialist check.

RTSGame deliberately keeps longer simulated-year scenarios in
`tools/gate-rts-game.sh`; its `blix test` entry is the quick `selftest` tier.

If a headed app belongs in a gate, pass `--frames N` so it terminates without a
person closing the window. Prefer headless probes and deterministic captures for
routine gates when they answer the same question.

## Tools as a workflow

Blix's asset tools are separate because they answer separate questions:

```text
discover -> inspect -> check -> cook -> view -> capture -> verify
```

| App | Contract |
| --- | --- |
| `inspect` | Describe a glTF/GLB source or common-preamble cooked artifact; reporting is not a verdict |
| `check` | Judge supported model, rig, animation, and cooked-load contracts |
| `cook` | Discover and run recipes, report coverage, or drive policy-bearing asset packages |
| `view` | Interactively inspect a model or rig under the Studio reference pipeline |
| `shot` | Render a deterministic PNG or sequence for comparison and review |

Examples:

```sh
./blix inspect path/to/asset.glb
./blix check --rig path/to/character.glb
./blix cook list
./blix view --rig path/to/character.glb --clip Walking_A
./blix shot --rig path/to/character.glb --clip Walking_A --out walk.png
```

Not every asset needs every step. In particular, `inspect` should not fail a
file merely because it contains something Blix does not consume; that belongs
to `check`. `view` should not silently choose between model and rig semantics;
the caller states `--model` or `--rig`.

### Reporting and enforcing exits

These commands deliberately do not assign the same meaning to exit zero:

- `blix inspect <file>` succeeds when it can produce its report. A stale or
  source-dependent cooked artifact is still inspectable and is labelled rather
  than rejected.
- `blix cook status <dir>` is also a report. Uncooked, stale, unknown,
  source-dependent, or old-recipe entries do not make it fail; an invalid root
  does. Use it to understand coverage, not as a shipping gate.
- `blix cook run` and the build-facing `batch` path are transformations. Missing
  input, unknown recipes, collisions, or recipe refusals fail. A recipe may
  explicitly return a successful skipped outcome.
- `blix check --cooked <dir>` loads only the supported mesh kinds—glTF, GLB,
  OBJ, and standalone `.blixmesh`—and fails when a load report resolves to
  source, fallback, or missing data. It exercises geometry and the image loads
  triggered by it. It does not compare source timestamps, so an accepted stale
  artifact can pass; pair it with `cook status` when freshness is policy.
- `blix check --model` routes static and rigged glTF through their matching
  importers and rejects non-finite clip durations. `blix check --rig` performs
  deeper engine-level hierarchy, rest-palette, and clip-sampling checks, then
  reports weighted-bone, track-coverage, and root-motion facts. Root-motion seam
  observations are advisory because glTF does not declare whether a clip is
  intended to loop.
- Studio owns its reference renderer's fixed bone and instance budget. Rig load
  rejects any skin that cannot fit before allocating GPU resources, while
  `Blix.Test.Studio` checks that the C# budget and both skinned shaders agree.
  Layer-mask algorithms and palette-slice independence are engine tests; an
  application's chosen mask roots and naming conventions are application policy.

An empty `check --cooked` supported set currently reports that there is nothing
to judge and exits successfully. Unsupported extensions are outside its sweep;
this is not a general directory-integrity validator.

## Build-generated indexes

`Directory.Build.targets` runs `Blix.Tools.Apps` after an executable assembly is
built. The resulting `*.blixapps.json` names:

- the assembly and optional apphost;
- whether it has an entry point;
- every `[BlixApp]` declaration and whether it is headed;
- every `[Recipe]` declaration used by the cook host.

The attribute remains the source of truth. The index is the cheap discovery
form and may be read even when a later build is broken. If an index is older
than its assembly, `blix` reports it rather than silently treating it as current.

On a truly fresh checkout, the first projects built before the indexer exists
cannot produce an index. Running `./blix ls` bootstraps the indexer; one following
solution build populates the complete set.

## Legacy launchers

The scripts under `tools/run-*.sh` predate project/app discovery. Some also
encode useful task-specific setup, and Sponza's setup scripts still prepare its
external asset tree. They are therefore not being declared obsolete wholesale.

They are no longer the extension point for a new application. Do not copy a
launcher merely to gain Vulkan environment variables, `--frames`, discovery, or
a short command name. The root `./blix` front door and `WindowOptions` own those
concerns once.

## Troubleshooting

### No apps are listed

On a fresh checkout:

```sh
./blix ls
dotnet build Blix.sln
./blix ls
```

The first command bootstraps the indexer; the build then generates the indexes.

### An app is indexed but not built

Build its project or the solution. Discovery can remember an app from an index
whose output has since been removed; launch refuses when neither its apphost nor
assembly exists.

### Vulkan is reported unavailable on macOS

Launch headed apps through the root `./blix` script. It exports the Vulkan
loader and MoltenVK paths and execs the native apphost. Going through Homebrew's
`dotnet` shell shim can lose the required `DYLD_*` environment under SIP.

### A gate opens a window and waits

The gate includes a headed app without a bound. Pass `--frames N`, or replace
that gate leg with the headless probe or capture that answers the same question.

### Discovery is stale

Rebuild the assembly. App and recipe indexes are build outputs and are renewed
from their attributes; do not edit an index by hand.

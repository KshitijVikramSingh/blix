# Workflow

This is the canonical guide to finding, running, and verifying work in a Blix
checkout. It describes the project/app model implemented by `blix`,
`Blix.Cli`, `[BlixApp]`, and the generated app indexes.

## First checkout

Blix currently targets .NET 8 and its headed applications use the Vulkan +
Silk.NET runtime. On macOS, install the native dependencies once:

```sh
brew install molten-vk vulkan-loader vulkan-headers vulkan-tools vulkan-validationlayers shaderc spirv-cross openal-soft
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
test: settlement --years 1 --map-seed 4
```

The first non-comment line without a colon is the project name. `test:` lines
list the apps that form that project's verification gate, in order, and several
lines add up. A line of names separated by commas runs each with no arguments. A
line with an option on it is one leg: its first word is the app and the rest are
its arguments, with double quotes keeping a value with spaces whole. Arguments
given to `blix test` itself follow a leg's own, so a single read takes the typed
value.

The current tree has two marked projects:

| Project | Folder | Gate |
| --- | --- | --- |
| `blix` | repository root | Graphics, Diagnostics, Physics2D, Physics3D, Apps, Studio, Recipes and Input suites |
| `demos` | `src/Demos` | `Blix.Demos.Character.Probe`, `chassis-tune --headless`, every other headed demo for 45 frames under `--validate`, and Sponza for 45 frames after its textures load, also validated (needs `BLIX_SPONZA_ASSETS`) |

Project scope follows the current working directory. From the repository root,
all projects below it are visible. From `src/Demos`, the `demos` marker is the scope
and a bare app name resolves within that project — including the experiments under
`Character/`, which are part of it rather than a project of their own.

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

Arguments after the name are forwarded unchanged to the app — which is also
where the boundary sits: blix's own options come *before* the name, so an app
stays free to take a `--build` of its own.

```sh
./blix run --build view --model Assets/hero.glb   # build the project first
```

`run` does not build by default. It compares the app's output against the
sources of its project, everything that project references, and the
`Directory.Build.props` / `Directory.Build.targets` MSBuild imports into each of
them — that last part matters here, where a single root `Directory.Build.targets`
drives shader compilation, reflection sidecars, cooked-asset staging and app-index
generation. When the output is older it names the file that moved and runs the old
binary anyway.

With `--build`, the name is resolved **again** after the build. A `.csproj` decides
`AssemblyName`, `OutputPath`, `TargetFramework`, `UseAppHost` and which apps an
assembly declares — every fact the index carries — so the resolution made before a
build can be a fossil. A name that no longer resolves afterwards is the correct
outcome and is reported as one; it is not worked around.
Reporting rather than acting keeps the resolver a resolver, and the failure it
prevents is the one you cannot otherwise see: an edit that appears to have done
nothing. Cooked assets and other content are outside the comparison.

If the same name is visible in more than one project, qualify it with its
project name:

```sh
./blix run demos:Blix.Demos.Character.Probe
```

Resolution tries an exact name, then case-insensitive equality, then an
unambiguous suffix after the last dot. Thus a conventionally discovered
`Blix.Test.Physics3D` can be addressed as `Physics3D` when no other visible app
has that suffix. Ambiguity is an error that lists the candidates.

## Declaring one app

Every Blix program's `Main` is one line that hands its command line to
`BlixApps.Main`. An executable that declares nothing is still runnable by
convention under its assembly name; `BlixApps.Main` then runs the function it
is given:

```csharp
using Blix.Core;

public static class Program
{
    public static int Main(string[] args) => BlixApps.Main(args, Run);

    private static int Run(AppArgs args)
    {
        // app work
        return 0;
    }
}
```

Add `[BlixApp]` when a program needs a stable short name, a summary in
`blix ls`, headed metadata, or when one assembly contains several apps.
`Default = true` marks the app the executable runs when nothing names one, as
when a published build is started directly; it is how an executable gets a
better name than its assembly's.

```csharp
public static class Program
{
    public static int Main(string[] args) => BlixApps.Main(args);

    [BlixApp("inspect-map", Summary = "report the authored map", Default = true)]
    public static int InspectMap(AppArgs args) => 0;
}
```

An app method is static, takes either nothing or one `AppArgs`, and returns
`void` or an exit code. `Main` itself cannot be an app, because it has to take
`string[]`; the indexer refuses a `[BlixApp]` on it. The build-time indexer and
the runtime dispatcher validate the same signatures.

`Headed = true` says that the app opens a window. It lets discovery label the
app and lets a verification gate warn before starting an unbounded window. It
does not impose a base class or a hosting model.

The declaration belongs on the method it names. The generated index is derived
from that attribute; do not maintain a parallel app registry.

## Setup every app shares

One static method marked `[BlixStartup]` runs before any app in its assembly,
including the one `Main` hands to `BlixApps.Main`. It takes `AppArgs` or nothing
and returns nothing; what it reads counts as read, and an `AppArgsException` it
throws exits 2. It is where a process culture, a shared lever or an opened log
belongs, so no app can run without it. At most one is allowed per assembly, and
the indexer checks both rules at build time. Blix itself sets no culture.

```csharp
[BlixStartup]
private static void Startup() =>
    CultureInfo.DefaultThreadCurrentCulture = CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
```

## Several apps in one assembly

An assembly can declare several app methods. The launcher names one with an
internal selector, which `BlixApps.Main` removes before the app sees its
arguments; at most one is `Default`.

```csharp
public static class Program
{
    public static int Main(string[] args) => BlixApps.Main(args);

    [BlixApp("game", Headed = true, Default = true)]
    public static int Game(AppArgs args) => 0;

    [BlixApp("selftest", Summary = "run the simulation invariants")]
    public static int SelfTest() => 0;
}
```

## Reading arguments

`AppArgs` is the command line, parsed once and read by whoever understands each
part of it. There is no schema: the window reads the flags about being a window,
the application reads its own, and nothing decides in advance what a flag means.

```csharp
var years = args.Int("years", 1);
var fog = args.Flag("fog");
var tints = args.All("tint");         // a repeated option
var size = args.Values("win", 2);     // --win 1280 720
var files = args.Positionals;         // read options first
```

`--name value` and `--name=value` are the same. Names match without dashes or
case, so a read of `map-seed` accepts `--mapseed`. Numbers are always read
culture-invariant, and everything after a bare `--` is positional.

Every read is recorded. After the app returns, `BlixApps.Main` prints one
warning line naming any argument nothing read, so a typo such as `--year 3` is
visible rather than silently ignored. A value that cannot mean what its reader
asked for, such as `--frames abc`, is an error: the message names the flag and
the program exits 2.

## Typed parameters

An app can declare its flags as parameters instead of reading them. This is
sugar over the same `AppArgs`, not a second parser:

```csharp
[BlixApp("settlement", Summary = "simulate a settlement")]
public static int Settlement(int years, int mapSeed = 0, bool fog = false, AppArgs? args = null) => 0;
```

A parameter is the flag its name spells in kebab case: `mapSeed` is
`--map-seed`, which also accepts `--mapseed`. A parameter with no default is
required, and a missing one exits 2 with a message naming it. A `bool` is a flag
by presence. The types a command line can spell are `int`, `long`, `float`,
`double`, `bool`, `string`, any enum, the nullable form of the value types (null
when not passed), and `IReadOnlyList<string>` for a repeated option. One
`AppArgs` parameter can sit alongside them for whatever the app reads
dynamically.

The indexer checks the same types from metadata, so a parameter nothing can
bind is a build failure, and records a usage line that `blix ls` prints under
the app's summary:

```text
settlement  simulate a settlement
            --years <int> --map-seed <int>=0 --fog
```

## Headed applications

A headed application lets the runtime read the arguments common to every Blix
window from the same `AppArgs`:

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
| `--validate` | Run under the Vulkan validation layers, and fail the run if they report an error |

`WindowOptions` reads only these; everything else is the application's to read,
and anything nobody reads is reported. Bounded runs, sizing, diagnostics, and
dumping should not be reimplemented in each game or tool.

`Blix.Demos.Chassis` is the smallest executable specification of this surface:
an `IGameLoop`, optional `IUiSource`, and a host-owned
bounded run. `Blix.Tools.View` is the fuller example with Studio composition,
an interface, input ownership, diagnostics, asset loading, and named views.

## Headless runs

`Blix.Runtime.Headless.HeadlessHost` gives a headless-capable `IGameLoop` the
same lifecycle a window does, with no window, no device and no wall clock. It
references only `Blix.Core` and `Blix.Diagnostics`, so a program that uses it
links no graphics backend.

It is not a way to remove the window from an arbitrary game. A loop that
creates GPU resources in `OnLoad`, or builds a `RenderGraph`, cannot run headless, and the device it is handed makes sure it finds out at the
first such call. Keeping a loop's headless path free of GPU work is a choice the
loop makes, as Chassis's `chassis-tune` does.

```csharp
if (args.Flag("headless"))
{
    new HeadlessHost(loop, HeadlessOptions.FromArgs(args)).Run();
    return 0;
}
```

Each frame runs in a window's order: input held still for the tick,
`OnUpdate`, the loop's diagnostics, `OnRender`, then the dump and the frame
bound. `HeadlessOptions.FromArgs` reads `--frames`, `--dump-frame`, `--width`,
`--height` and `--debug` as a window does, plus `--step`, the seconds per frame;
time is always that fixed step. A window's own `--title` is left unread.

What a headless run cannot have is stated rather than faked:

- `OnLoad` receives a `NoGraphicsDevice`, which describes itself and refuses to
  create or destroy anything, naming the call.
- `OnRender` records into a command list that nothing executes. Diagnostics
  still count what the loop recorded.
- Input comes from an optional callback that is handed the frame number and the
  `InputState` before each tick, which is how a test, a replay or a bot drives a
  loop.

`chassis-tune --headless` is the executable specification: run with the same
`--frames` and `--dump-frame`, headed and headless, it writes dumps of the same
schema with the same values and controls.

## Verification gates

Run the selected project's declared gate with:

```sh
./blix test
```

The launcher resolves each named app, runs all of them even if an earlier one
fails, and returns one final exit code. This is the quick, routine verification
tier chosen by the project; it is not a claim that every expensive scenario or
visual comparison belongs in every edit-build loop.

Like `run`, the gate does not build. When any leg's output is older than the
sources it was built from, the verdict is qualified where you read it:

```text
demos: all 1 green
demos: but 1 of 1 ran a build older than your sources — ... That verdict is
about what is on disk. Pass --build, or run `dotnet build`, to make it about
your code.
```

`./blix test --build` (`-b`) builds each leg's project first, once per project,
and a leg that fails to build fails the gate instead of running its previous
binary. Every build finishes before any leg runs, and the gate then discovers
the indexes again: a build rewrites them, so the app records read beforehand
describe what used to be true. A green gate over stale binaries is not a wrong answer — it is a right
answer to a question nobody asked — which is why it is said next to the verdict
rather than thousands of lines above it.

To run a nested project's gate, name it, from anywhere in the tree:

```sh
./blix test demos
```

or stand in its folder, which makes it the project in scope:

```sh
cd src/Demos
../../blix test
```

A leading word is always a project's name, because every other argument a gate
takes is an option for its legs. A word that names no project is an error that
lists the ones that exist. Before, it reached every leg of the gate you were
standing in as an argument, and that gate came out green.

The current root gate is declared in `blix.project`. It covers graphics,
diagnostics, both physics layers, application composition, Studio, and the
asset recipe/cooking suite. The recipe leg belongs here because it verifies
shipped-format compatibility, native encoder availability, source-free cooked
loading, driver behavior, and packaging invariants; those are engine release
claims rather than an optional specialist check.

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
./blix check --model path/to/character.glb
./blix cook list
./blix view --model path/to/character.glb --clip Walking_A
./blix shot --model path/to/character.glb --clip Walking_A --out walk.png
```

Not every asset needs every step. In particular, `inspect` should not fail a
file merely because it contains something Blix does not consume; that belongs
to `check`. `view`, `shot` and `check` take one `--model`: a file with a skin is
posed (and checked as a rig), one without is shown at its nodes. Whether a file
has a skin is a fact of the file, so no tool asks the caller to restate it.

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
- `blix check --model` reports a model's shape and rejects non-finite clip
  durations; on a model with a skin it also performs engine-level hierarchy,
  rest-palette, and clip-sampling checks, then reports weighted-bone,
  track-coverage, and root-motion facts. Root-motion seam
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

## What is left under tools/

The per-application launchers are gone. Eleven of them predated project and app
discovery, and each carried its own copy of the Vulkan environment because a
project had no way to say what it contained — so every application needed a
front door of its own. There is one now, and copying a launcher to obtain
environment variables, `--frames`, discovery, or a short name is no longer a
thing that can be done: `./blix` and `WindowOptions` own those concerns once.

What remains under `tools/` is not launchers. `setup-sponza-modern.sh` and
`cook-sponza-modern.sh` prepare an external asset tree, `fetch-gltf-corpus.sh`
pins a conformance corpus, and `lab-baseline.sh` and `check-resize.sh` are
instruments — they drive `./blix shot` and `./blix view` and judge what comes
back. Preparing inputs and measuring outputs are both work a front door does
not do.

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

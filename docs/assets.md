# Assets

Blix's asset path is a set of cooperating contracts, not one asset manager.
Projects declare work, recipes turn source data into engine-readable formats,
loaders choose between source and cooked forms, and applications decide how
loading and residency fit into their frame loop.

The ordinary path is:

```text
source file
  -> project declaration
  -> recipe
  -> stamped cooked artifact
  -> runtime loader decision + load report
  -> optional deferred CPU staging
  -> optional progressive GPU residency
```

Each arrow has a different owner. Keeping those owners separate is the main
asset-system convention.

## The layers

| Layer | Owns | Does not own |
| --- | --- | --- |
| Format | Byte layout, format version, reader, writer, common preamble | How a project chooses to produce or group assets |
| Capability | Reusable work such as image decoding, BC encoding, mesh simplification, or probe filtering | Source selection and build coverage |
| Recipe | One transformation: source plus settings to one cooked format | Directory walking, staging, or runtime lifetime |
| Project declaration | Which sources are cooked and with which options | The implementation of the transformation |
| Driver/tool | Tree walking, out-of-place packaging, progress, and parallelism | Runtime loading policy |
| Loader | Whether a request resolves to source, cooked, fallback, or missing data | Frame budgets and application lifetime |
| Residency mechanism | Budgeted handoff or upload work | Eviction, priority, and scene policy |
| Application | Composition, budgets, sharing scope, readiness, teardown | Format internals |

This is why a recipe is neither a file format nor an encoder. The engine owns
the formats it must read and the reusable capabilities that write them. A
recipe records the decision to apply those capabilities to particular source
data with particular settings.

## Declare cooking in the project

Cooking should normally be declared in the consuming project file:

```xml
<ItemGroup>
  <CookMesh Include="Assets/models/**/*.gltf" />
  <CookMesh Include="Assets/models/**/*.glb" />
  <CookTexture Include="Assets/textures/**/*.png" />
  <CookProbe Include="Assets/sky.hdr" Options="env=512" />
  <CookFont Include="Assets/fonts/*.font.json" />
</ItemGroup>
```

The convenience items map to general `BlixCook` declarations:

| Item | Recipe | Output |
| --- | --- | --- |
| `CookMesh` | `gmsh` | `.blixmesh` |
| `CookTexture` | `gtex` | `.blixtex` |
| `CookProbe` | `gpro` | `.blixprobe` |
| `CookFont` | `fnt1` | `.blixfont` |

`Directory.Build.targets` turns these declarations into incremental build
inputs and outputs. Only out-of-date items reach the cooker. Newly produced
files are added to the build output during that same build; they do not wait
for a second project evaluation.

The general form is the extension point:

```xml
<BlixCook Include="Assets/maps/**/*.hmap"
          Recipe="hmap"
          Cooked=".blixterrain"
          Options="tile=64 compression=fast" />
```

An output path may be claimed only once in one batch. A collision is an error,
not an order-dependent last writer. Set `BlixCookCustomTarget=true` only when a
project genuinely needs a different build algorithm; different settings belong
in `Options` and a new transformation belongs in a recipe.

### What incrementality observes

The build target watches:

- every declared source;
- the Blix cook assembly;
- every assembly listed as a project recipe assembly; and
- the derived cooked output.

There is one bootstrap caveat. Cooking invokes a pre-built tool and pre-built
recipe assemblies, as the shader and app-index targets do. A solution build
establishes the required build order. Building only a consuming project after
editing a recipe can use the previous recipe binary until that recipe project
has been rebuilt.

## Shipped recipes and formats

| Recipe | Input | Output | Current purpose |
| --- | --- | --- | --- |
| `gmsh` | `.gltf`, `.glb` | `.blixmesh` | Engine-native mesh, material and image references, hierarchy/rig data where present, and LOD chains |
| `gtex` | `.png`, `.jpg`, `.jpeg` | `.blixtex` | Mipped texture data, normally BC7/BC5 when the native encoder is available |
| `gpro` | `.hdr` | `.blixprobe` | Environment, sun, diffuse irradiance, GGX/Charlie prefilter chains, and lookup data for image-based lighting |
| `fnt1` | `*.font.json` | `.blixfont` | Rasterized glyph atlases for the sizes in the font specification |

The recipe host sees `fnt1` as consuming `.json`, because the filesystem
extension API has no compound-extension concept. The recipe itself declines
files that do not end in `.font.json`. Project declarations should therefore
use the narrow `*.font.json` glob rather than all JSON files. Today `cook
status` still sees any unrelated JSON under the directory as a candidate for
`fnt1` and can over-report it as uncooked; the host has no compound-extension
claim to distinguish those files before invoking the recipe.

`gmsh` accepts `flipV`, `tangents`, `split`, `splitExtent`, and
`splitFoliage`. `gpro` accepts `env`, `irr`, `prefilterBase`, `prefilterMips`,
`brdf`, `clamp`, and `yaw`. Recipe defaults are real authored choices and are
written into the artifact stamp.

Texture role is also policy, not merely compression. Base colour and emissive
data are sRGB; normals, metallic/roughness, and occlusion are linear and may
need different channel treatment. The general `gtex` entry point classifies by
filename when no caller knows more. A model cook or other caller that knows the
material channel should pass an explicit role rather than rely on invented or
extracted filenames.

### Current transformation policy

The shipped recipes are thin entry points over typed cooking APIs, but those
APIs still make authored choices that affect the result:

- `gmsh` first asks the rig importer whether a glTF is a supported rig. Rigged
  files retain skins, clips, joint attachments, and static parts; other files
  take the static path, which world-bakes vertices while retaining a reversible
  parent-first node hierarchy. The shipped path always builds meshoptimizer LOD
  chains. `tangents`, `flipV`, and spatial splitting are static-mesh options and
  are refused for a rig rather than accepted without effect.
- Static spatial splitting is both a draw/LOD policy and a topology policy.
  Oversized primitives are divided into bounded chunks, seam vertices are
  duplicated, and split borders are locked during simplification. Unsplit
  geometry uses pruning without border locking so disconnected foliage
  components can reduce.
- A mesh cook records only material-referenced images. Existing `.blixtex`
  files in the destination tree become source-independent image rows; remaining
  source images set `SourceRequiredForImagesOnly`. Embedded images are extracted
  and immediately offered to the texture cook. The material channel, not the
  generated filename, determines their texture role.
- Project-owned material patches are applied to the cooked material table by
  typed cook drivers. A patch can pin the source hash, assert match counts, and
  is represented in the mesh stamp by its filename and content hash. It is not
  a renderer-wide material heuristic and is not an option of the uniform
  `gmsh` recipe entry point.
- `gtex` builds a mip chain down to a four-texel minimum dimension. RGB is
  alpha-weighted so transparent padding does not tint cutouts, then alpha is
  rescaled per mip to preserve threshold coverage. Normal maps use BC5;
  colour/data roles use BC7 when the native encoder is available and otherwise
  default to RGBA8. `BLIX_COOK_FORMAT` can force that choice and
  `BLIX_BC7_QUALITY` controls native BC7 effort.
- `gpro` preserves the visible environment, derives sun direction and
  irradiance, removes the sun disc only from lighting integrals, and writes
  diffuse irradiance, GGX specular, Charlie sheen, and both lookup tables. The
  typed cook API, uniform `gpro` recipe, and dedicated `cook probe` driver can
  rotate the source by yaw before all measurements and convolution.
- `fnt1` delegates glyph rasterization to the same source importer used by the
  runtime fallback, then writes deterministic codepoint-ordered coverage
  atlases for every size named by the specification.

`cook sky` is deliberately not another one-file recipe. It consumes a set of
cooked meshes, voxelizes their coarsest LODs, traces directional visibility into
an L2 spherical-harmonic probe grid, and writes the occupancy and surface-colour
grids needed for dynamic sun-bounce injection. Scene bounds, sampling density,
and output grouping are driver policy, so the result is a scene-level
`.blixsky` artifact rather than a sibling transformation of one source file.

Recipe identity covers those policy inputs: texture stamps name the resolved
role, format, flags, mip count, encoder backend, and quality; mesh split extent
uses invariant formatting; probe yaw is normalized through the uniform recipe;
and rigged cooks refuse static-only settings. Environment-dependent encoder
selection remains visible rather than pretending two machines necessarily
produce identical texture bytes.

## Project-owned recipes

A project recipe is a public static method discovered through `[Recipe]`. It
does not implement an engine interface:

```csharp
[Recipe("hmap",
    Produces = ".blixterrain",
    Consumes = ".hmap",
    Version = 1,
    Summary = "a height map to project terrain tiles")]
public static CookOutcome Cook(CookRequest request)
{
    // Parse request.SourcePath and request.Options, then write request.OutputPath.
    return CookOutcome.Written("terrain tiles written");
}
```

A recipe ID is exactly four ASCII characters because it is stored as a 4cc in
the cooked preamble. `Produces` is one extension; `Consumes` is a semicolon-
separated list. Bump `Version` whenever the same source and normalized settings
would produce different bytes.

Put the recipe in a small project referenced by the consumer and register its
built assembly with the cook target:

```xml
<ProjectReference Include="$(BlixCookProject)"
                  ReferenceOutputAssembly="false"
                  PrivateAssets="all"
                  Private="false" />

<ProjectReference Include="../Game.Cooking/Game.Cooking.csproj"
                  ReferenceOutputAssembly="false"
                  PrivateAssets="all"
                  Private="false" />

<BlixCookRecipeAssembly
    Include="../Game.Cooking/bin/$(Configuration)/net8.0/Game.Cooking.dll" />
```

The build-only cook-host reference makes the first build honest when Blix lives
in another source checkout. In this repository a solution build already builds
the host, but an external consumer must not depend on that accidental order.
`$(BlixCookProject)` is supplied by `build/Blix.Source.props`; see
[Workflow](workflow.md#using-blix-from-another-repository).

Then use a normal `BlixCook` item. The deliberately foreign recipe in
`Blix.Test.ProjectRecipes` keeps metadata discovery across a project boundary
inside the engine gate; the source-consumer contract above is the application
shape used by a separate repository.

Recipe discovery comes from the generated app/recipe indexes. A built recipe
assembly becomes listable and runnable without a hand-maintained registry.
Blix's own recipes win an ID collision; other duplicate IDs are reported and
the first discovered declaration is retained.

## Provenance and freshness

Every shipped cooked writer requires a `CookStamp`, and every cooked format
starts with the common `CookPreamble`. The preamble carries:

- format identity and format version;
- recipe ID and recipe version;
- flags describing whether source data is still required;
- source time and size, plus a reserved optional content-hash field;
- source path; and
- normalized recipe parameters.

The recorded source path is relative to the cooked artifact and uses forward
slashes. Absolute developer paths do not belong in committed output.

Format and recipe versions answer different questions. A format version says
whether this runtime can interpret the bytes. A recipe version says whether the
current transformation would have produced different bytes from the same input
and settings. Readers refuse incompatible formats; tools can report an older
recipe independently.

Freshness has four states: current, stale, source missing, and unknown. Current
recipes record source time and size; none currently populate the content-hash
field, and freshness does not compute or consult it. Freshness is reported rather
than used as a universal runtime veto. A source-control checkout can rewrite
modification times without changing content, so timestamp mismatch alone is not
a safe reason to make an otherwise readable artifact fatal.

`CookedFlags.SourceRequired` means the cooked artifact is an optimization, not
a replacement. `SourceRequiredForImagesOnly` narrows that debt: the artifact
contains the model/material data but still needs source image bytes. How images
are grouped and addressed is project policy, so the common mesh format does not
silently choose atlases, arrays, palettes, or pools on a project's behalf.

## Runtime addressing and loading

Most importers accept the path the application already knows. They look for a
cooked sibling where that is supported, validate it, and fall back to parsing
the source when appropriate. Mesh importers can also open `.blixmesh` directly,
which is required for a cooked-only output tree.

`AssetDatabase` is an optional, small manifest layer over those importers. An
application registers stateless importers, loads `Assets/manifest.json`, and
then asks for a typed `AssetId`:

```csharp
var assets = new AssetDatabase()
    .RegisterImporter(new FontImporter())
    .LoadManifest(Path.Combine(AppContext.BaseDirectory, "Assets", "manifest.json"));

var font = assets.Load<FontData>(AssetId.Parse("fonts/hud"));
```

The database validates IDs, importer names, duplicate registrations, and output
types. It is not the recipe catalogue, a cache, a GPU resource owner, or a
required project model. Direct paths remain normal for applications whose code
already names their assets clearly.

### Importer boundaries

Runtime importer entry points prefer a compatible cooked sibling where one is
defined. Recipe entry points such as `GltfImporter.ImportSource`,
`WavefrontParts.ImportSource`, and `FontImporter.ImportSource` deliberately
bypass siblings so a recipe cannot consume its own earlier output.

The two glTF importers share image discovery, material extraction, texture
identity, and source-versus-`.blixtex` selection, but keep different geometry
contracts:

- `GltfStaticImporter` bakes node world transforms into flat geometry. It can
  also reconstruct a node hierarchy from a cooked mesh by unbaking each
  primitive with its recorded node transform.
- `GltfImporter` preserves one binding per skin, joint attachments, and static
  parts that share the rigged file. Multiple skins are supported; a shared
  animation-clip set requires them to resolve joints to the same parent-first
  ordering.
- A rigged vertex retains the strongest four influences across the contiguous
  `JOINTS_n`/`WEIGHTS_n` pairs and renormalizes those weights. Mesh indices use
  16 or 32 bits as required.
- Static tangent and colour modes are distinct vertex layouts. Colour mode also
  carries `TEXCOORD_1`; requesting tangents and colour together is refused
  because no current layout represents both.

`ObjImporter` returns one flattened mesh and falls back to source when a cooked
artifact contains several material parts. `WavefrontParts` is the separate
material-preserving OBJ shape. Both validate the vertex-affecting `recenter`
setting before accepting a sibling. `FontImporter` similarly prefers
`.blixfont` and otherwise rasterizes the declared TTF sizes.

`GltfIgnored` is a per-import-layout diagnostic. Static imports account for the
selected tangent or colour/second-UV layout, and rigged imports account for
contiguous complete influence pairs as well as coloured static parts in the
same file. The sweep remains subtractive, so unknown application semantics are
reported without being predeclared. Influence entries discarded by the
strongest-four reduction are a fidelity limit, not an ignored attribute.

### Loads are observable

The `AssetLoadReport` model distinguishes four modes:

- `Source` — the original file was parsed or decoded;
- `Cooked` — an engine-native artifact was used;
- `Fallback` — the requested data was absent or unusable and a substitute won;
- `Missing` — nothing usable was found.

Reports include source and cooked paths, bytes, elapsed time, recipe ID, and a
warning explaining a slow or fallback path. `AssetLoadLog` is off by default.
An instrument calls `Start()`, optionally listens to `Reported`, and uses
`Peek()` or `Drain()` to consume the thread-safe collection. It is deliberately
ambient because imports happen before a frame diagnostics context exists and
texture decoding may happen in parallel.

This report is the runtime truth. The presence of a `.blix*` file is not proof
that a loader accepted it.

## Deferred CPU work

`AsyncLoadQueue<T>` is the generic off-thread/calling-thread handoff:

```csharp
var load = new AsyncLoadQueue<StagedMesh>();
load.Start(() => ParseMeshes());

// In the frame loop, on the thread that owns the destination state:
load.Drain(budgetMs: 4.0, process: StageMesh);
```

The producer runs once on a worker thread. After the whole producer result is
ready, `Drain` transfers items on the caller's thread within a time budget and
always makes at least one item of progress. The caller owns parsing semantics,
fault handling, and what staging means; the queue has no graphics dependency.

This is deferred production plus budgeted integration. It is not incremental
I/O from the producer and it is not an asset scheduler.

## Progressive GPU residency

`ResourceUploader` budgets texture uploads by mip. It queues the smallest mip
first, then progressively finer levels, and drains work on the render thread.
The lazy path reads each mip's bytes only when that upload is processed, keeping
load-time CPU memory bounded.

`GltfTexture.ReleaseCpuMipBytes()` can drop an eager texture's retained byte
arrays after downstream upload work has captured them, but no current runtime
caller invokes it. Eager source-decoded textures therefore keep their CPU bytes
for the lifetime of the `GltfTexture` today.

`GltfTextureLoader` composes that mechanism with glTF policy:

- base-colour and emissive slots use sRGB formats;
- normal, metallic/roughness, and occlusion slots use linear formats;
- absent slots receive neutral 1x1 textures;
- cooked `.blixtex` data allocates one stable full-chain handle and fills its
  mips over later drains; and
- source-decoded images take the eager upload path.

Applications call `GltfTextureLoader.Drain(budgetMillis)` from the render loop
and use `PendingCount` to decide when full-detail rendering is ready. The handle
identity does not change as finer mips arrive. Unuploaded levels are undefined
and the uploader does not clamp sampler LOD, so current applications hold their
flat/loading presentation until `PendingCount` reaches zero rather than sampling
a partially resident chain.

This is progressive upload, not demand-paged streaming. Every enqueued mip is
unconditionally uploaded. `ResourceUploader` is not thread-safe: enqueue and
drain calls belong on the render thread. It contains no eviction, visibility,
priority, or memory-budget policy.

### Texture identity and sharing

`TextureRegistry` keys identified textures by `(ResourceId, TextureFormat)`.
Both parts matter: two imports of the same image should share, while the same
pixels requested once as sRGB and once as linear are different GPU resources.
Textures without a reproducible resource identity fall back to object identity;
they can deduplicate within one object graph but cannot safely share across
imports.

A `GltfTextureLoader` creates a private registry unless the caller passes one.
Sharing is explicit because it also chooses lifetime:

```csharp
var shared = new TextureRegistry();
var worldTextures = new GltfTextureLoader(device, shared);
var previewTextures = new GltfTextureLoader(device, shared);
```

The registry exposes resident count, identified resident bytes, unidentified
count, and avoided uploads. It does not own destruction or eviction. `Clear()`
forgets identities during teardown; it does not free GPU handles.

## Inspect, report, and enforce

Use the recipe host for discovery and file coverage:

```text
./blix cook list
./blix cook status path/to/Assets
./blix cook run gmsh path/to/model.glb
./blix cook run omsh path/to/tree.obj -Drecenter=0
```

These apps are discovered from generated indexes, so build the relevant tool
and recipe projects first on a fresh checkout. `./blix ls` shows what the
current build has indexed.

`cook status` reads common preambles and reports uncooked, unreadable,
ambiguous, stale, unknown, old-recipe, and source-required artifacts. An
unreadable output and an ambiguously claimed source both remain in the coverage
denominator: neither is a cooked success. It intentionally exits success for a
partially cooked tree because it reports state rather than deciding a project's
shipping rule. Its freshness result is the stamp's current mtime/size
comparison, not a loader veto.

The format-neutral host rejects surplus positional arguments instead of
silently discarding them. In `batch`, each line has exactly three or four
tab-separated fields, and the cooked/skipped totals follow the recipe's
`CookOutcome.Wrote` result rather than assuming every invocation wrote a file.

Use the runtime judge when a project requires every real load to stay on the
cooked path:

```text
./blix check --cooked path/to/Assets
```

That command loads supported meshes as an application would and fails if any
geometry or texture report resolves to source, fallback, or missing data. This
distinguishes “a cooked file exists” from “the loader actually used it.” It
does not fail merely because `cook status` calls an accepted artifact stale.

The older `cook mesh`, `cook textures`, and `cook probe` verbs remain useful
drivers. `cook asset` follows one glTF's referenced images, cooks those images
before the mesh, and writes a smaller output tree. Material channels supply each
texture's role; missing images, paths outside the tree, destination collisions,
and one image used in incompatible roles are refused before cooking begins. The
driver succeeds only when the resulting mesh stamp declares no source debt.
`check --cooked` remains the runtime-path judge for a whole output tree. `cook
sky` consumes a cooked scene and applies sampling policy to produce a
sky-visibility volume.
These carry behavior the uniform recipe host deliberately lacks, such as walking
trees, parallel work, progress, out-of-place packaging, and scene-level policy.
Prefer project declarations for normal build coverage and use drivers when
their packaging workflow is the thing you need.

`cook mesh --out` mirrors only the resulting `.blixmesh` files into the output
tree. It does not copy authored `.gltf` or `.glb` sources: current cooked meshes
carry their own geometry, materials, hierarchy, rigs, and image table. If an
image-table row still names an uncooked source image, that image—not the glTF—is
the remaining source dependency, and the mesh driver's completion line says so.
Use `cook asset` when the output must stand alone; use `cook mesh` when image
packaging is handled separately. The driver does not delete source copies left
by older runs; it reports them so cleanup remains an explicit, reviewable act.

The older `mesh` and `textures` drivers make their own incremental decisions.
Both now ask their recipe whether an existing artifact matches its format,
recipe ID/version, recorded source metadata, and normalized policy. Mesh
identity includes layout, splitting, simplification, and material-patch state;
texture identity includes role and the resolved encoder backend/quality. A
driver therefore re-cooks when options or encoder availability change even if
the destination is newer than its source. `textures --force` remains available
when an unconditional rewrite is wanted.

### Current adoption

The ordinary project declarations currently cover meshes and fonts. The shared
`CookTexture` and `CookProbe` items are available, but no checked-in project
currently declares them. `Blix.Test.ProjectRecipes` separately guards
project-owned recipe discovery without making a game part of the engine tree.

Vulkan Sponza is intentionally the exceptional, heavy-asset path. Its sources
live outside the repository, and `tools/cook-sponza-modern.sh` builds an
out-of-place shippable tree with `cook asset`, `cook probe`, and `cook sky`.
That script owns pack selection, material patches, tangent/splitting settings,
probe orientation, and sky-volume sampling. Those are Sponza packaging and
research decisions, not defaults for an ordinary Blix application.

## Invariants and common traps

- A cooked form must be substitutable for the source under the same importer
  settings. Smaller or faster but semantically different is not a valid cook.
- Record parsed, normalized settings in the stamp. Raw option spelling is not a
  stable description of what the recipe used.
- Bump recipe versions when output changes and format versions when byte layout
  or reader compatibility changes.
- Do not infer texture colour space from a generated filename when the material
  channel already knows the role.
- Do not treat cooked-file existence as load success; use load reports.
- Do not make registry sharing global. The caller that owns lifetime chooses
  the sharing scope.
- Do not describe progressive mip upload as general streaming: it has no demand,
  eviction, or priority model.
- Do not put project grouping policy into common formats merely to make a
  `SourceRequired` flag disappear.
- Do not maintain a parallel recipe registry. The attribute and generated index
  are the discovery source of truth.

## Code map

| Concern | Location |
| --- | --- |
| Build declarations and staging | `Directory.Build.targets` |
| Recipe contract and discovery | `src/Blix.Cooked/RecipeAttribute.cs`, `BlixRecipes.cs` |
| Common stamp and preamble | `src/Blix.Cooked/CookStamp.cs`, `CookPreamble.cs`, `CookedFile.cs` |
| Shipped recipes | `src/Blix.Recipes/` |
| Recipe host and drivers | `src/Blix.Tools.Cook/` |
| Runtime load reporting | `src/Blix.Cooked/AssetLoad.cs` |
| IDs, manifests, and importers | `src/Blix.Assets/` |
| Deferred CPU handoff | `src/Blix.Render/AsyncLoadQueue.cs` |
| Budgeted GPU upload | `src/Blix.Render/ResourceUploader.cs` |
| glTF texture policy and residency identity | `src/Blix/GltfTextureLoader.cs`, `TextureRegistry.cs` |
| Project-owned recipe fixture | `src/Blix.Test.ProjectRecipes/FixtureRecipe.cs`, `src/Blix.Test.Recipes/Program.cs` |

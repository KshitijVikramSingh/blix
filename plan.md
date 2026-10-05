# Blix — the current plan

What is open, and nothing else. Finished work is not recorded here: its reasoning lives where it
governs behaviour — code comments, [`docs/`](docs/), and the suites that pin it (conventions §9) — and
its history in git.

---

## The arc now: geometry and lighting for huge, dynamic worlds

**Why.** Not speed, as first thought: timed warm on the orbit (`--ab <term>`, 1500 frames), Bistro's frame
is ~17 ms and Sponza's ~19, all lighting together is 8.3 and 9.5 ms of it, and the terms the arc began
with are small (sun-bounce injection 0.35 / 3.0 ms, probe terms 0.4 / 0.4, GTAO ~0 / 0.7, shadows ~0 /
1.0); most of it is the lit pass's material shading. The 18 ms that started this was the cold regime.
The approach is the limit for what the constraints below ask: a regular probe grid sized to the scene's
bounds (cost and resolution follow world size), rays that march a cook-baked voxel grid rather than
geometry (so nothing that moves occludes or bounces light), and four unrelated structures (cascades, Hi-Z,
SH volume, occupancy) for one scene.

**The constraints, decided.** Design for dynamic geometry; assume huge worlds; ray tracing behind one
interface whose backends are software (compute traversal; this Mac's MoltenVK exposes no ray query) and
hardware (`VK_KHR_ray_query`) where a device has it.

**How moving things take part.** Receiving: the GI lives in space (probes), so anything samples it where it
stands. Occluding: characters through capsule or sphere proxies, not skinned triangles in a BVH. Large
dynamic geometry: per-mesh cooked BVHs placed as instances in a runtime top-level structure; a changed
instance dirties the probes near it, which retrace first, and that runtime trace overrides the static bake
exactly where the world has changed.

**Stages, in order.**
0. *Open from the instruments stage:* `./blix run` was seen dropping an app's flags (`--viz`, `--cam`,
   `--frames`, `--win`) with argv intact, and did not reproduce in six attempts (isolated, traced, beside a
   second instance). The unread warning now says which failure it was when it fires (nothing asked for the
   flag, or asked and not taken); the next occurrence is diagnosable. The scale scene is `blix city`
   (`tools/city/setup.sh`, `--scene city`): one seed is one city at any size.
1. *Cooked clusters and per-mesh BVH*: the format and the cook (meshoptimizer's meshlets; the cook already
   simplifies).
2. *GPU-driven culling, LOD and a visibility buffer* over those clusters, measured on Bistro and the
   generator. Bindless materials as its prerequisite. Where it starts, measured on the city (seed 1): the
   host keeps each unique primitive once (82,576 vertices at every size) and draws its placements
   instanced, so memory is flat to 64x64 blocks (123,422 placements, ~0.45 GB), and culling and LOD per
   placement run on the GPU (`scene_cull.comp`; `--cpu-cull` is the A/B): building a frame costs the CPU
   0.06-0.09 ms at every size, against 0.9 ms at 8x8 and 44 ms at 64x64 on the CPU path, for the same
   picture. Two-phase occlusion culling over placements follows it (`scene_occlusion.comp`; `--no-occlusion`
   is the A/B, `--occlusion-cut` makes every frame a camera cut): city 64x64 29.0 -> 5.4 ms, Bistro flat
   (not triangle-bound), Sponza ~1% dearer, every view and orbit the same picture. Cluster grain is
   measured and NOT built: the census (`WriteClusterCensus`) says it keeps 9.4% where placement occlusion
   keeps 13.5% (city), 75.4% against 91.5% (Sponza), 36.2% against 41.9% (Bistro); frustum and normal-cone
   tests alone are worth 2-12%. Priced at what triangles cost each scene, that is ~1 ms of the city's 5.4
   and ~0.8 ms of Sponza's ~25 (warm), nothing on Bistro, for a new draw path (no drawIndirectCount, no
   mesh shaders: meshlet vertex lists, GPU index compaction, vertex pulling in four vertex shaders). The
   visibility buffer is not the lever either: `--lit-flat` (flat.frag in the lit pass, all else equal)
   leaves Sponza's full-LOD cost where it was (8.7-9.6 ms against 7.4-8.0 with materials), so that cost
   is geometric, not shading on small triangles. The cull keeps its counts on the GPU, so caster and
   triangle counts read back only under `--cpu-cull`.
3. *The ray-query interface*: software backend first, hardware when a device can prove it. 3a is the CPU
   half (`Blix.Geometry`: `BvhBuilder`, `TriangleBvh`, `RayQueryScene`, Test.Graphics BV): binned-SAH
   hierarchies in 32-byte GPU-ready nodes, the watertight triangle test, Ize's widened box exits, hits
   in `rayQuery`'s terms (facing is the mesh's own). Built at load (`--ray-scene`), Release: Sponza
   12.8M triangles in 2.2 s and 353 MB of nodes, Bistro 2.8M in 0.87 s, the city's 123k placements in
   0.29 s; 0.07-0.37M CPU rays/s per core. 3b-i put them on the GPU (`RayQueryGpuData` packs five
   blocks, `Blix.Shaders/ray_query.glsl` traverses them, `ReadGpuBuffer` reads answers back): with
   MoltenVK's fast math off (`MVK_CONFIG_FAST_MATH_ENABLED=0`) the GPU equals the CPU oracle bit for bit
   on 65,536 rays per scene (`--ray-check`). With it on (the default) 1-3 grazing edge hits per ~50k
   disagree: division is not correctly rounded there; fused multiply-adds are forbidden by `precise`, which
   is what keeps the edges watertight. `--ray-probe` runs one ray against one triangle on both. 3b-ii:
   `--ray-bench <mixed|probe|camera>` counts each ray's work (nodes, placements entered, triangles; exact,
   unlike GPU timings here, which moved up to 50% between batches of unchanged code). Placements of meshes
   used once now merge into regions, cells of triangles in world space (`RayQueryScene`, 262k triangles
   each, owners per triangle): camera rays enter 2.3 cells on Bistro where they entered 12.1 placements,
   8.8 on Sponza where 18.2, nodes per ray 118 -> 79 and 157 -> 110; still bit-exact; Release build 1.7 s
   and 4.3 s. A coarser cell budget no longer changes Sponza's nodes per ray (105-118 from 65k to 4M), so
   the cost left is the traversal itself. `--ray-lod-error M` traces each mesh's coarsest cooked level
   within M metres: memory, not speed. Sponza packs to 781 / 475 / 234 MB at 0 / 2 cm / 20 cm and Bistro
   to 197 / 67 / 58 MB, while nodes per probe ray stay 42-46 and 24-27 (depth is the log of the triangle
   count) and the GPU stays bit-exact. The city does the same ~40 nodes per ray 3-4x faster from 1.3 MB of
   nodes, so traversal is bound by memory traffic: the structural lever is compressed wide nodes (eight
   quantised children in ~80 bytes), worth building once a consumer's ray budget says it is needed. 3c:
   `--ray-view` traces the camera's view (half resolution, through each pixel's depth sample) beside the
   raster depth: within 1% on 94-96% of pixels, the rest alpha-tested foliage (5.4% Sponza, 0.65% Bistro:
   rays have no alpha test yet), depth edges and LOD; with the raster at full detail too, Sponza leaves
   48 pixels unexplained in 518,400. Hits now carry a world-space geometric normal (checked against the
   CPU's). Far-from-origin rounding (the city at
   2 km moves an entry point 0.1 mm) is the huge-worlds origin question, not a traversal one. The cook
   takes the hierarchies once the layout stops moving.
4. *Runtime GI*: camera-relative clipmap probes traced through 3, relocated out of walls, rays amortised
   over frames, dirty-region updates, capsule occluders. In steps: 4a surfaces at hits (done: below);
   4b the sky/bounce injection traces rays into the atlas the lit pass already reads, A/B against the
   occupancy march on the same grid; 4c a camera-relative clipmap in place of the bounds-sized grid; 4d
   relocation, dirty regions, capsules. 4c holds what the lit pass already consumes, both traced: bounce
   (irradiance atlas) and directional sky visibility (the cosine-weighted share of a probe's rays that
   escape, in the depth atlas's fourth channel), so nothing baked is needed (no occupancy, sky SH or
   albedo grid). 4 levels of 32x16x32, spacing doubling, camera-centred and snapped, slots addressed
   toroidally with each slot remembering its world cell (a reused slot restarts its history); lookup at
   the finest level holding the point, blended at borders, Chebyshev only; buried = mostly back-face
   hits. Steps: 4c-i addressing (`probe_clipmap.glsl` and its C# twin, deviceless tests); 4c-ii the traced
   injection (budget, new slabs first, staleness, buried); 4c-iii the incident pass and fog read it under
   `--gi-clipmap`, A/B against today's field (pictures, the triangle reference at the same points); 4c-iv
   the city lit with no bake, and the clipmap the default if it holds. 4c-i and 4c-ii are in: the GPU's
   slots equal `ProbeClipmap` on all 65,536, all four levels solve within 300 frames (512 probes a frame,
   ~2.9 ms), the incident field can read it (`incident_clipmap.frag`). Two new arbiters (`--probe-
   reference`): sky visibility against triangles at baked probes, and the SURFACE reference, which judges
   what is shaded at camera-visible points (so it sees leaks). At Sponza's surfaces the clipmap's sky
   visibility is unbiased (median 0.99, error 0.008) where the bake is low (0.64, 0.014); sun-only bounce
   is over at the p90 in both (bounds 2.8x, clipmap 3.1x: leaks). Open, and why the clipmap is not the
   default: its 40% brighter picture was start-up glare (an unanswered hit took the sky as open; fixed:
   closed, and early solves averaged), settled it agrees with the bounds field. The surface reference now
   judges sky-carried bounce too, with the sky's radiance on the CPU (`SkyRadiance`: the cooked
   environment with the sun disc replaced as the cook does, checked against the cooked irradiance to
   0.97-1.03 for every normal). Both fields carry about 1.5x too much (bounds median 1.52, clipmap 1.45):
   the shared model, sky irradiance times the share of sky visible, credits a courtyard surface with the
   sky's average brightness when it sees the dim zenith. The lit pass's direct sky has the same bias even
   with the TRUE visibility (Sponza median 1.35, Bistro 0.80 with p90 2.07), so under `--gi-clipmap` the
   probes now hold sky radiance (escaped rays read the disc-free prefiltered sky; a hit takes the field's
   whole irradiance, no separate sky term) and the lit pass takes ALL indirect diffuse from the field
   (`uIncident.w`; sky visibility kept for specular only). Judged on total indirect diffuse (sun bounce +
   sky bounce + direct sky) at 1500 frames after load: Bistro bounds median 0.94, |err| 0.105 -> clipmap
   1.00, |err| 0.063; Sponza 1.52 (p90 5.45) -> 1.33 (p90 4.19), |err| 0.0130 -> 0.0127, but the mean
   rose (0.0332 -> 0.0350 against 0.0256). Sun off, Sponza's sky part is still 1.5x by mean (clipmap
   0.0141, bounds 0.0145, reference 0.0093; median 1.61 against 3.03). Next: find what still over-lights
   Sponza's surfaces in both terms (the p90 says leaks: courtyard probes read through arcade walls?)
   before the clipmap can be the default. Found on the way and fixed: the
   probe cook integrated diffuse irradiance with 64 samples a texel, which a bright aureole turned into
   +-25% texel noise (straight up, every floor's lookup, read 1.47x high); 4096 now (probe recipe 2). 4a: a bake at load (`ray_surface_bake.comp`, one dispatch per
   material, 16 area-stratified samples per triangle at a footprint-matched mip) gives every triangle a
   word in `BlixRaySurfaces`: albedo (sRGB) and coverage. Traversal meets a partly covered triangle by an
   integer hash of the ray's seed, the entry and its leaf position (`RayTests.Covered`), so the CPU oracle
   flips the same coins and stays bit-exact; hits carry linear albedo. Sponza: 1.35M of 12.8M triangles
   partly covered; the traced view's disagreement at foliage halves (5.45% -> 2.76%). 4b: `--gi-trace`
   swaps the injection's march for traced rays into the same atlas (`sky_inject_traced.comp`; the body is
   `sky_inject.glsl`, two programs so default runs build no ray scene; `--ab trace` alternates). Warm A/B
   on the orbit: Bistro 20.2 against 21.9 ms, Sponza 27.5 against 28.6: faster than the march. Pictures
   within 1.6/255 on average, traced a little brighter (Sponza's curtains and upper walls). The traced
   field is the right one: `--probe-reference` now also path-traces the triangles (baked albedo and
   coverage; the old reference traced the occupancy grid, the march's own geometry). Sun-only field
   against it, 48 probes: Sponza march median ratio 0.73 and mean error 0.0300, traced 0.98 and 0.0027
   (11x smaller); Bistro 0.0116 against 0.0052. The grid under-lights Sponza's arcades by about a
   quarter, and the grid reference shares the bias (18% under the triangles). Traced is now the
   default (`--gi-march` for the A/B), tracing at 2 cm (`--ray-lod-error`; same error against the
   full-detail reference, Sponza 506 MB on the GPU), built only where there is a probe field. Open: coloured
   transmission through leaves (the march had it), the probes' own placement still reads the occupancy
   grid (buried test), and the sky term has no reference yet (traced runs 7-16% brighter with the sun
   off).
5. *PRT baked by the cook*: per-region probe transfer, relit by the sun at runtime: the static base and
   warm start of 4.

Known synchronisation gap, found mapping the graph for GPU buffers (stage 1c) and left open: the graph's barrier
inference (`BarrierInference.Infer`) is an empty stub whose output nothing executes, so compute synchronisation is each
dispatch's own (storage-image transitions, and since 1c GPU-buffer fences).

Known instrument gap: a Sponza `--shot` raises 9 validation errors on `main` as well (a storage image
copied to a buffer without transfer-source usage, in the shot's readback). Not this arc's, and not fixed.
And the froxel fog animates on wall-clock time (`uFogParams.w = time.Total`), so two arms with different
frame rates reach a `--shot-frames` frame with different fog: 0.2 mean against a 0.04 floor at city 64x64,
gone with `--no-fog`. Compare arms that differ in speed with the fog off.
The first ~250 frames after load run slow and then settle: Bistro 25-34 ms, then 15-18 ms, with or
without the sky bounce (cause unproven; the GPU clock ramping under sustained load is the suspect). A
120-frame shot measures the cold regime, which is how an `--ab lod` on Bistro read 4.9 ms and then sign-
flipped. Time at `--shot-frames=1500` (each arm keeps its last 600 frames) and compare the quartiles.
`BLIX_CONFIG=Release ./blix run` ran a binary older than the Release build beside it, without saying so
(found by a node count that did not move); measure Release by exec'ing the apphost.
The scene host also ignores its `[Tune]` fields on the command line (`--lod-error-pixels` is reported
unread): three field names collide across its settings objects, so applying them is a naming decision.
The Bistro orbit at `--shot-frames=120` has two outcomes in either arm (3,931 pixels apart, max 74), so a
Bistro orbit A/B needs several runs per arm before a difference is one.

Huge also means camera-relative rendering and streaming by region (cluster geometry and BLAS per region,
the top level over what is loaded); both land where the stage that needs them does.

---

## Open decisions, and the arcs after them

**Every format through the cook, as glTF now is (decided; one format at a time).** The engine reads no
raw asset: each format gets a standard cook pipeline, and its source reader becomes the cook's. Ergonomics
of cooking may get better later; the split (a runtime source path beside the cooked one) does not come
back. Still read raw today: OBJ (`ObjImporter`, which nothing outside the tests uses, and `WavefrontParts`,
which the Check tool uses; no recipe cooks an OBJ), fonts (`FontImporter` rasterizes the TTF when no
`.blixfont` exists), materials (`MaterialImporter`), audio (`WavImporter`), images (`TextureImporter`, and
`CookedMaterials` decoding a source image when a cooked mesh's `.blixtex` is missing). These source readers
still throw `FileNotFoundException`, unlike the cooked readers' `AssetImportException`; each format's arc
closes that gap for it.

**Later, as their own arcs, when something asks:** morph deformation (targets plus a `weights` clip
channel; today refused where they take effect, `MeshRecipe.OpenSource`), then `KHR_animation_pointer` (not
forced into skeletal animation).

---

## Structure: three findings, open

**1. The getting-started game renders through a tool, and cooks at launch.** `hello-blix-3d` draws with
`Blix.Tools.Studio.StudioRenderer`, which `renderer.md` calls an optional reference for model and rig inspection
while saying there is no engine renderer. Studio is the de facto default: hello-3d, View, Shot and Studio all
stand on it. Through it the game's runtime reaches `Blix.Recipes` (SharpGLTF, BCnEncoder, the meshopt, MikkTSpace
and BC7 natives), ships a raw `Rogue.glb`, and cooks it on launch, so "the engine reads no raw asset" holds for
the engine and not for the first program a newcomer runs. Section BT does not see it: the example references the
engine as `$(BlixSourceRoot)/…`, the scan resolves no such path, and its closure comes back empty. The open
decision: promote a renderer out of the tools (four consumers is the evidence; it reverses "no engine renderer"),
or have the example own its pipeline and cook at build time as the demos do. BT's MSBuild-property blind spot is
a fix either way.

**2. Reading cooked formats is spread over four assemblies, and cook work sits in the runtime.** `Blix.Cooked`
holds the preamble, stamp and refusal type; `Blix.Assets` the `.blixmesh` and `.blixfont` readers beside the
source importers (StbTrueType, WAV, OBJ, materials) and `AssetDatabase`; `Blix.Graphics.Images` the `.blixtex`
and `.blixprobe` readers beside Stb image decode, environment conversion and probe baking; `Blix` the model
reader (`ModelData`, `CookedMaterials`, `CookedVertices`, `CookedSamplers`). The format arc above ends naturally
in one runtime layer of cooked readers, with every decoder and baker behind the cook, and BT's rule extended from
SharpGLTF to StbImage, StbTrueType and the rest.

**3. `Blix` is one flat namespace of about sixty files.** Animation is about twenty-two of them and the largest
coherent subsystem; beside it are the kinematic physics hosts and collision worlds, cameras and controllers,
lights, transforms, the model reader and residency, and audio sources. Not a case for more assemblies, which would
be predicting; an animation namespace would help finding things.

---

## F — a body in the character room

The one stage not started. `src/Demos/Character/` holds a shared library — room,
motor, camera, renderer, resolver — with `room` (walk it), `room-shot` (capture it)
and a probe (87 assertions, headless) over it.

**The prerequisite nothing listed: there is no body.** The room draws a capsule and
has zero references to `ClipPlayer`, `AnimationClip` or `Pose`. Every consumer of
`BoneMask` and `RootMotion` in the tree is a tool, a test or the preview tool — none
is a character that moves. So "contact drives weights" is not two existing things
being wired together.

**It is a sibling executable**, `Blix.Demos.Character.Body`, over the same library.
That is what the collection's shape is for, and it keeps `room` answering the
question it already answers. The body takes the room's geometry and motor from the
library; what it adds is a rig on top of the capsule.

**First observable, and the whole of the first step:** a skinned Rogue standing in
the room, facing the right way, feet on the floor. Nothing blended, nothing masked.
The unknown is the rig fit against a 0.35 m radius, 1.8 m capsule, and it should
surface on its own rather than tangled in a blending design.

Then, and only once a body walks:

**Contact drives weights, and there are no states.** Speed and groundedness come out
of the resolver and drive blend weights directly — no states, no transitions, no
dwell timers. `BoneMask`'s first consumer that is not a tool: locomotion on the legs,
a one-shot on the upper body. The Rogue carries what this needs — `Walking_A/B/C`,
`Running_A/B`, `Idle`, `Unarmed_Melee_Attack_Punch_A/B`.

*Negative control:* the mask set to `All` must visibly break the legs, and set to
`None` must leave the walk bit-for-bit unchanged. A layer that changes nothing and a
layer that changes everything are a mask's two failures, and both are invisible
unless asked for.

**And then the clip drives the contact.** Root motion says how far, the room says
where you can go. Honest expectation: 4 travelling clips of 76, so this may end a
**decided-no**, and reaching that with the instrument built is the output.

**Acceptance is from the chair.** Walk the room: ramps, stairs, ledges, walls. Every
stage of this arc had at least one fault only a person watching could see, and four
of five were **legibility, not mechanism**.

---

## Acceptance steps that are access, not work

These keep this record open. None can be closed by writing anything.

**No clean Mac has run a published bundle.** Every stage-B measurement was taken on
the machine that built it. "Zero images mapped from `/opt/homebrew`" is the strongest
available proxy and is not the test. Take a `.app` to a Mac with no Homebrew, no
.NET, no Vulkan SDK and no OpenAL, and launch it.

**A `win-x64` publish ships no OpenAL native.** `vulkan-1.dll` is correctly absent —
the GPU driver installs it — but OpenAL Soft is neither a system library nor in a
NuGet runtime pack. Stage B's closure question, unanswered for Windows.

**No physical gamepad has been held.** The lifetime, the trigger range and
re-acquisition are proven against the state machine; the Silk seam is not.
Specifically unverified: that thumbstick index 0/1 is left/right on a real device,
that a resting trigger reads 0.00 rather than 1.00 — the direct observable of the
range fix — and that holding a button while alt-tabbing back does not fire it.
`blix run Chassis --frames 900` prints the lifetime lines; plug, unplug, and
switch away holding something.

**`blix.cmd` is only as verified as CI exercises it**, and the Windows branch of the
native build targets has only ever run on a GitHub runner.

---

## Known gaps that are not scheduled

**The `demos` gate is not in CI, and now cannot be as it stands.** Its headed legs run each demo
for 45 frames under `--validate`, which needs a GPU and the validation layers, and CI runners have
neither. The root gate is the deviceless one. A CI home for the `demos` gate would first need its
deviceless legs (the probe, `chassis-tune --headless`) separable from the headed ones, which is
what named tiers are for.

**Linux is absent from the CI matrix.** A second red job teaches nothing the first
has not; the shape of what Windows needed should be known before it is copied.

**A skin that mirrors only some of its joints within one primitive** draws with one front face for the
whole draw, chosen from the first weighted joint's palette. No asset or consumer has it, and there is
no single correct front face for such a draw.

**What each renderer draws of a material** is the table in [`docs/renderer.md`](docs/renderer.md)
("glTF materials: read versus drawn"): Studio ignores eleven `KHR_materials_*` extensions, `unlit`
among them, so an unlit material is drawn lit. Each row closes when something asks for it.

---

## Parked

**The RTS moves to this engine on its next pin bump**, and these break for it. From PR #42:
`vk.LastCpuFrameTiming` and the tuple `vk.GpuPassTotals` are gone (read `host.Timing`); submitted work is
literal (a zero-instance draw counts zero, a negative count throws); a `uniform sampler` with no
`//@sampler` on the line before is a build error; `blix test <word>` reads a leading word as a project;
`RenderGraph`, its handles and builders, `TextureView`, `ShaderInterface`, `ShaderReflection` and
`UniformBlockLayout` are in `Blix.Graphics` (take `IGraphicsDevice`; the casts compile but nothing needs
them). From PRs #44-#51: `ModelData`/`Model` (and `BlixMeshFile.Flat`), `.blixcook`, `SkeletonPlacement`,
a format v18 re-cook, `CreateMaterial(arrayLengths:)` in element counts (was bytes), `WithArrayLength`,
`DestroyIndirectBuffer`, `Transformed` refusing skinned or unknown layouts, `PropModel.Dispose` freeing its
meshes, and `OnUnload` teardowns (`--validate` fails on a leftover). From §J (#52): the GameObject family,
`IAnimated` and the clip animations are gone; `IAnimation.Advance(delta)` replaces `Sample(Time)`;
`IRenderHost.ResetFixedClock` (an implementer must add it), `IFixedGameLoop`, and `Game`'s fixed step owned
by the host with no step cap. From §K (#53, #54, #56): `Bone(Name, ParentIndex, Rest, Offset)` with `Rest`
required and no inverse bind; a palette is `SkinBinding.ComputePalette(BoneWorlds, …)`
(`Skeleton.ComputeBonePalette` is gone); `BonePaletteSet(SkinBinding, capacity)` with `Add(worlds, post)`;
`JointCount` for `BoneCount` on `BonePalette`, `BonePaletteSet` and `BoneBuffers`; `ModelData.Skin(Binding,
JointNodes, Placement)` and `Model.Skins` as bindings; `FindWeightedJoints`; attachments' `BoneName`/
`BoneIndex` and `CarrierNode`; masks validated against their skeleton; recipe 18 (a re-cook); and a
skinned load with `Colour` gets the 92-byte vertex, so its pipeline must declare that layout. From this
arc: `Blix.Import` is gone — `GltfImporter`, `GltfStaticImporter` and the `Gltf*` types they returned — so a
glTF opens by cooking (`CookCache.Resolve`, then `ModelData.Load`); `AssetImportContext` takes an id and a
path only.

---

## Deferred until something asks

Probe density, `BakeMerge`, clip-lookup-by-name, further `PropModel` adoption.

**Asset manifests** (finding assets by logical name, declared external asset roots). Every
in-repo program finds its assets through `AppFiles` and the importer's cooked-sibling lookup,
and Sponza's external pack set is one required variable. The hand-run preparation steps do not
fit either: EXR conversion needs Blender, and the sky bake is a scene-level driver by design.
Each waits for a consumer, which is conventions §5 and §8, and waiting does not keep
a plan record open. The probe-density measurements and the approaches already refuted
are in [`docs/renderer.md`](docs/renderer.md) so the same ground is not walked twice.

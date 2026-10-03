# Blix — the current plan

What is open, and nothing else. Finished work is not recorded here: its reasoning lives where it
governs behaviour — code comments, [`docs/`](docs/), and the suites that pin it (conventions §9) — and
its history in git.

---

## The arc now: geometry and lighting for huge, dynamic worlds

**Why.** Sponza and Bistro (`--scene`, `tools/bistro/`) both sit in one frame-rate range, and on Bistro the
sun-bounce injection alone is 18 ms isolated against 7.5 ms for all of shading. Its knobs only trade
quality for a fraction of the cost: copying skipped tiles costs nothing, 8x fewer probes is only ~3x
cheaper. The approach is the limit: a regular probe grid sized to the scene's bounds (cost and resolution
follow world size), rays that march a voxel grid rather than geometry, transport re-solved every frame for
a static answer, and four unrelated structures (cascades, Hi-Z, SH volume, occupancy) for one scene.

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
   instanced, so memory is flat to 64x64 blocks (123,422 placements, ~0.45 GB), but the CPU still culls
   and picks LOD per placement for five passes: 4.5 ms at 8x8, 13.6 ms at 32x32, 31 ms at 64x64.
3. *The ray-query interface*: software backend first, hardware when a device can prove it.
4. *Runtime GI*: camera-relative clipmap probes traced through 3, relocated out of walls, rays amortised
   over frames, dirty-region updates, capsule occluders.
5. *PRT baked by the cook*: per-region probe transfer, relit by the sun at runtime: the static base and
   warm start of 4.

Known synchronisation gaps, found mapping the graph for GPU buffers (stage 1c) and left open: the graph's barrier
inference (`BarrierInference.Infer`) is an empty stub whose output nothing executes, so compute synchronisation is each
dispatch's own (storage-image transitions, and since 1c GPU-buffer fences); and a storage image is transitioned from
`Undefined` before every dispatch, so one a pass read-modify-writes across frames (`uProbeUsage`) is formally undefined
between them.

Known instrument gap: a Sponza `--shot` raises 9 validation errors on `main` as well (a storage image
copied to a buffer without transfer-source usage, in the shot's readback). Not this arc's, and not fixed.

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

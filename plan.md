# Blix — the current plan

Stages A–E of the portability and input arc are done and merged (PRs #37–#39).
Their reasoning lives where it governs behaviour — code comments, `docs/`, and the
suites that pin it — rather than here, per conventions §9. So does the arguments
and headless-host arc that followed (`AppArgs`, `HeadlessHost`, typed parameters,
`[BlixStartup]`, gate legs with arguments), recorded in [`docs/workflow.md`](docs/workflow.md)
and pinned by `Blix.Test.Apps` and the `demos` gate. The same holds for PR #42: the host's frame
timing (`IRenderHost.Timing`), graph targets' resting layouts, and separate images with
shader-declared samplers are in [`docs/architecture.md`](docs/architecture.md) and
[`docs/renderer.md`](docs/renderer.md). What follows is what is still open, and nothing else.

---

## G — the engine reads cooked models only

Decided 2026-09-30: the engine stops parsing glTF. Models reach it as `.blixmesh`, so every repair a
source needs — patches, normal-map conventions, tangents, validation — happens once, in the cook,
and there is one reader of each fact instead of two (the occlusion strength that both paths misread
is the case). Tools that open an arbitrary `.glb` cook it on open into a cache. RTSGame loads no
`.glb` at runtime.

**Done** (branch `cooked-only`): MikkTSpace in the cook (046c780); the complete static vertex,
`.blixmesh` v11, repacked to the layout a load asks for (2db2e0c); cook-on-open for tools,
`CookCache` (83cecf5); stage 1 below, the scene-graph `.blixmesh` v12 (b270e8d); stage 2, one
`ModelData` and one `Model`, with unread attributes recorded in the file, v13 (7c4dd90). Stage 4 goes
before 3: v12 dissolved the rig-or-static case that made configuration a prerequisite, and converting
source paths that stage 4 deletes would be wasted. Stage 4 done: runtime loads cooked only (d4bc611),
`Blix.Import` takes the parsers and SharpGLTF (ddfa39e), the engine's types lose `Gltf` — `PbrMaterial`
(not `MaterialData`, taken by the `.material` asset record), `TextureData`, `MaterialTextureLoader`,
`AlphaMode`, `UnreadAttribute` (2b6d43a). Stage 3 done: a project's cook configuration (`CookConfig`, `.blixcook`)
replaces `.blixpatch`, read by `blix cook --config`, `blix cook project` and the build's `<BlixCookConfig>`;
each entry stamped by its own hash; Sponza's four patches are one `sponza.blixcook` (972d4b1). Stage 5
done: Studio's static pipeline reads the complete vertex and the cooked tangent frame; the derivative
frame is gone (81a0f80). **§G is complete.**

**The format mirrors glTF's structure, not its encoding.** "Rig or static" is not a question glTF
asks: every mesh reaches a scene through a node, and a rigged file is a scene graph in which some node
has a skin. It was a split Blix made when it had two importers, and the cooked format inherited it —
a static cook bakes world transforms into vertices and keeps the hierarchy as a side table that the
loader un-bakes by inverse (lossy where a transform will not invert); a rig cook keeps no hierarchy at
all, which is why TankArena still parses `tank.glb` at runtime. What stays Blix's is the encoding:
the complete vertex, LOD chains, cooked images, patches applied, tangents generated. Accessors,
buffer views and sparse data stay behind in the cook.

1. **One scene graph in `.blixmesh` (v12).** A node table (hierarchy, local transforms, names) in
   every file; meshes referenced by nodes, stored once however many nodes place them; vertices in
   mesh space; skins as joint-node lists with inverse binds; clips targeting nodes. Splitting and LOD
   error are computed at each node's world scale, so they stay in metres. Read as one `ModelData`.
2. **One resident `Model`.** `Rig` folds into it: skins and clips are optional parts of a model, and
   skinning-only members (`CreateBoneBuffers`, palette packing) refuse by name without a skin.
   `device.CreateRig` goes. Drawing skinned, posed or at bind pose is a consumer's choice, not a type.
3. **A cook configuration per project.** What a project decides about each asset it cooks — material
   rules and normal-map conventions (once `.blixpatch`), split, flipV — in one file the project
   names, which the build, `blix cook` and the Sponza script all read. The stamp records each entry's
   hash. No configuration means glTF's defaults. It is what a cook UI would one day edit.
4. **The engine refuses a `.glb`**, naming the cook. The importers and SharpGLTF move out of `Blix`
   into a separate `Blix.Import` project the cook depends on; the data types lose `Gltf` (`ModelData`,
   `MaterialData`, `TextureData`, `MaterialTextureLoader`, …) and load through their own entry points
   (`ModelData.Load`).
5. **Studio reads the vertex frame.** A static layout carrying tangent, colour and uv1, so the
   derivative frame in `studio_lit.frag` goes.

---

## H — glTF animation as glTF defines it: nodes, not skin 0's joints

glTF animates NODES. Blix read clips only on skin 0's joints, as bone indices; everything else was
dropped or refused. A survey of the corpus (94 files added for this arc) found each case live:
rigid node animation with no skin (AnimatedTriangle, BoxAnimated, CesiumMilkTruck, InterpolationTest),
tracks on non-joint nodes above joints (BrainStem), skins with different joint lists under one clip set
(RecursiveSkeletons: 84 skins, refused), and STEP / CUBICSPLINE sampling (refused outright: the cooked
format stores only time/value keys). Morph weights and KHR_animation_pointer stay out of scope, recorded
as unread.

1. **Sampling.** `.blixmesh` v14 records each channel's interpolation and CUBICSPLINE's in/out tangents;
   the curves evaluate STEP and CUBICSPLINE by the spec's formulas. The cook writes tracks for every
   animated node, not only skin 0's joints.
2. **One animated hierarchy per model.** Its bones are every skin's joints plus every animated node
   (static in-between nodes stay offsets). Clips are bone indices against it; one pose drives every skin,
   each gathering its joints' worlds with its own inverse binds. For a one-skin file with no other
   animated node it is exactly skin 0's skeleton, so existing consumers see no change.
3. **Rigid parts follow the pose.** A part under an animated node is placed by that node's posed world;
   attachments are the special case where the node is a joint. A model with clips is posed whether or
   not it has a skin.
4. **Proof.** A conformance check samples every clip in the corpus at several times and holds skinned
   vertices and rigid part worlds to the spec: node locals from rest TRS plus spec-sampled channels,
   composed up the tree in the test. (Not SharpGLTF's evaluator: its cubic rotation disagrees with the
   spec, checked by hand.)

**Done** (branch `rig-alpha`): stage 1 (762e00a) — sampling, v14, every animated node cooked, 94 corpus
files. Stages 2-4 — the animated hierarchy (`ModelData.Skeleton`, `Skin.Bones`, palettes gathered),
rigid parts under any animated node, posing without a skin in the tools, and the conformance check over
22 animated files (RecursiveSkeletons' 84 skins, BrainStem, InterpolationTest, BoxAnimated, the milk truck).
Out of scope: morph weights, `KHR_animation_pointer`.

---

## I — glTF spec support: the audit, and what it found

The corpus is 150 files (after §H's additions). `Blix.Test.Recipes` now cooks and loads every one and
samples every clip: 137 load, 13 are refused for a listed reason (`ExpectedRefusals`), and the list can
only shrink on purpose. Loading is not reading: a grep of what the reader touches (with a known-positive
control) found the second half. In a proposed order — correctness of what already loads first, then the
refusals, then features:

1. **Primitive modes — done.** Strips, fans and non-indexed primitives unroll to triangle lists in the
   cook (recipe 10), checked triangle-for-triangle against the spec's unrolling; points and lines are
   refused by name (decided: pipelines for them wait for a consumer).
2. **Samplers — done.** Each cooked image row carries its glTF sampler (format v15); the engine maps
   wrap (incl. MIRRORED_REPEAT) and min/mag/mip filters; TextureSettingsTest's six sampler tests pass.
3. **One image, two roles — done.** An image used two ways cooks once per role and convention
   (`ImageVariants`); TextureEncodingTest and TextureLinearInterpolationTest load. Files whose REQUIRED
   extensions Blix does not read are now refused (glTF's rule): SheenChair and TextureTransformMultiTest
   wait for item 5.
3b. **Single-sided static materials are not culled.** Studio's static pipeline never culls, so
   TextureSettingsTest's single-sided row shows its red X. Found by the sampler capture.
3c. **Extension texture channels are not cooked.** Only the five core channels get image rows; sheen,
   clearcoat, transmission, iridescence, anisotropy and specular textures are dropped (factors survive).
4. **Refused valid files — done.** A sparse INDEX accessor (Accessor_Sparse_03), a skin with no inverse
   binds (identity, §5.27), and joints with no common root (Animation_Skin_06, which SharpGLTF's strict
   validator rejects — sources now load unvalidated, and the cook checks what it needs itself: POSITION,
   index range, required extensions).
5. **KHR_texture_transform** (required by TextureTransformMultiTest, used by two more).
6. **Scene structure the reader ignores:** scene selection (MultipleScenes), cameras, KHR_node_visibility,
   EXT_mesh_gpu_instancing, KHR_lights_punctual, KHR_materials_variants.
7. **Out of scope unless asked:** Draco, meshopt, KTX2/BasisU, WEB3D quantized (refused by name), morph
   targets and KHR_animation_pointer (recorded as unread), KHR_xmp (metadata).

Material extensions are already broadly read (transmission, diffuse transmission, sheen, volume,
specular, IOR, clearcoat, iridescence, anisotropy, dispersion, unlit, emissive strength); whether each
RENDERS right is the Compare* set's job, judged against each README.

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

**No corpus file animates a node between joints or above them with a changing value.** The animated
hierarchy includes such nodes (BrainStem has three tracks on them), but BrainStem's hold their rest, so
removing them from the hierarchy fails nothing. A derived asset, as for `RiggedSimple_cutout`, would close it.

**Linux is absent from the CI matrix.** A second red job teaches nothing the first
has not; the shape of what Windows needed should be known before it is copied.

---

## Parked

**The RTS moves to this engine on its next pin bump**, and five things will break for it:
`vk.LastCpuFrameTiming` and the tuple `vk.GpuPassTotals` are gone (read `host.Timing`, whose
`GpuPassTotals` is live, so copy the entries to start a window); submitted work is literal, so a
zero-instance draw counts zero and a negative count throws; a `uniform sampler` with no
`//@sampler` on the line before is a build error; and `blix test <word>` reads a leading word as a
project name. PR #42 lists those four. The fifth: `RenderGraph`, its handles and builders, `TextureView`,
`ShaderInterface`, `ShaderReflection` and `UniformBlockLayout` are in `Blix.Graphics` now, so a file
that had only `using Blix.Graphics.Vulkan;` for them needs `using Blix.Graphics;`. Its casts still
compile, but nothing needs them: take `IGraphicsDevice`, and read isolation from `host.Timing`.

**The teardown SIGSEGV** (exit 139 after a demo's exit line; `UMEntryThunk::Decode` under
`GetDelegateForFunctionPointer`, with a small number, `0xb`, `0xd` or `0x80000001`, where a function
pointer belongs). Pre-existing and rare: two legs in one `demos` gate run, then none in fifteen
targeted reruns and two full gates. `VulkanGraphicsDevice.Init.cs` holds what is known. Waiting for
a report taken with `BLIX_TEARDOWN_TRACE=1` exported, which names the step; gate legs inherit it.

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

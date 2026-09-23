# Inlet arc — plan

> **Archived 2026-09-21 — completed.** The importer/viewer completeness sweep
> and its conformance follow-up shipped. See `docs/plans.md` for status.

> What a glTF contains, and what Blix takes from it. The gap between those two is not one feature —
> it is a list, and every entry on it was invisible until someone compared the file against the
> import.

---

## What this is

**The intake path, made complete enough to trust** — the engine's side and the viewer's side, which
are two different completenesses and both matter. The engine's is *does this data survive import*.
The viewer's is *can this tool express what survived*. An asset can pass the first and fail the
second, and today several do.

**It is not the cook.** Turning `tools/character_merge.py` into a recipe is real work and is
deliberately not here; it is the outlet, and it wants the inlet settled first.

---

## How this arc was found, and how to find the rest

Three faults in three days, all the same shape and all found the same way: **ask what is in the
file, then ask what came out.** Bone-attached meshes silently dropped. Secondary skins silently
dropped. Vertex colours silently dropped. None was visible from a test, a warning, or a log line;
each was visible in thirty seconds of comparing a JSON dump against an importer's output.

So the first thing this plan does is finish that sweep rather than react to the last thing noticed.

### What the sweep found — 62 assets, every mesh primitive

| in the files | reaches the engine |
|---|---|
| `POSITION`, `NORMAL` ×155 | **yes** |
| `TEXCOORD_0` ×98 | **yes** |
| `JOINTS_0`, `WEIGHTS_0` ×39 | yes, one skin only |
| **`COLOR_0` ×49** | **no** |
| **`TEXCOORD_1` ×9, `_2` ×4, `_3` ×4** | **no** |
| `COLOR_1` ×7, `COLOR_2` ×3 | no |
| `alphaMode` MASK ×26, BLEND ×1 | imported; **the viewer cannot express it** |
| `doubleSided` ×96 | imported; the stage happens to draw NoCulling anyway |

### And what it found nothing of, which bounds the arc

**Zero** morph targets. **Zero** sparse accessors. **Zero** cameras. **Zero** glTF extensions used
by any asset. **Every** animation sampler is `LINEAR` — 40,497 of them, no STEP, no CUBICSPLINE.

That is worth as much as the gaps. Blend shapes, extension handling and interpolation modes are all
things an importer *could* be missing, and none of them is a real hole in this tree. The arc is the
six rows above and nothing wider.

---

## Stages

### I-A — vertex colours survive import — **DONE**

`COLOR_0` on **49 primitives**, and it is not decoration. Sampled from the content: greyscale,
0.0 to 1.0, 152 distinct values on one tree trunk and 51 on a blade of grass, with the tree's leaf
card uniformly 1.0. **That is baked ambient occlusion** — grass darkening at its base, bark in its
crevices — thrown away at import on every piece of scatter RTSGame draws, which is thousands per
frame.

~~The engine already has the layouts: `VertexPosition3TextureColor` and `VertexPosition3Color`
exist and the glTF path simply never reaches for them.~~ **Wrong, and checked rather than assumed.**
Both exist and neither carries a NORMAL, so neither can be lit — they are unlit debug layouts, and
the thing vertex AO has to survive is the lighting. `VertexPosition3NormalTextureColor` is new, at
36 bytes, with the colour packed to four bytes rather than sixteen.

**This does not change the hand-tinting**, and the distinction matters. `SettlementArt` colours the
kit by material name on purpose, and its comment argues the case: the pack's textures are palettes
rather than pictures, and a texture path for flat-shaded low-poly props is a great deal of work for
little. Vertex AO is a *second* channel that multiplies what that already decides. Reading it as
"the colour we were missing" would be the wrong lesson from the right finding.

**Negative controls — all three run**
- An asset with no `COLOR_0` produces a **byte-identical** capture. ✔ `Crate.gltf` captured
  identically with the channel live and forced to white; `CommonTree_1` differs across the same
  pair.
- A blade of grass visibly darkens at its base and not at its tip — the thing AO is *for*, checked
  by looking, because a uniform multiply would also pass a numeric test. ✔ `Grass_Common_Tall`
  renders near-black at the base and white at the tips, gradient between. This is the control the
  numbers could not have made: a uniform multiply darkens the whole blade and passes every
  stride, range and distinctness check in section AZ.
- The white leaf card (`COLOR_0` = 1.0 everywhere) is unchanged, which is the control that catches a
  channel being applied in the wrong space. ✔ `CommonTree_1` prim1 imports uniform white through
  the same branch, in the same model, as the trunk's 82 distinct values.

And the one the A/B added on its own: across the tree capture **21,551 pixels changed and every
single one darkened — zero got lighter.** That is the invariant that separates a multiply into
albedo from an add or an inversion, and no test written up front had asked for it.

**What the scope fear turned out to be.** The arc was opened expecting a fork: six applications
pin `VertexPosition3NormalTexture` in a pipeline of their own, Vulkan walks a vertex buffer at the
*pipeline's* stride, and one `.blixmesh` per asset is read by both the viewer and RTSGame — so
showing AO in the viewer looked like it required changing the cooked files a live game reads.
**It did not, and the reason is a fact about the tool rather than a compromise:** `StudioModel.Load`
imports from source glTF, not from a cooked sibling. So the viewer reads `COLOR_0` directly, the
cook is untouched, and all six pipeline-pinning consumers are untouched. Whether `.blixmesh` ever
carries colour is now a separate decision with nothing forcing it — which is where it should sit,
since no consumer has asked.

**Shape of the change.** `includeColour` is opt-in on `BuildStaticMeshData` and on
`AssetImportContext`, alongside the existing `includeTangents` precedent, and the two refuse to
combine rather than silently dropping one (0 of 33 `COLOR_0` primitives in the tree carry a
`TANGENT`, so nothing is lost by the refusal and a future asset gets a sentence instead of a
mystery). The studio takes the opposite default: its static pipeline declares the 36-byte layout
outright and the ground, the boxes, models and attachments all ride it, with anything colourless
widened to white. **That is what kept this from needing a pipeline variant at all** — the arc said
it would not build a shader permutation matrix, and it did not have to.

Attachments carry colour *unconditionally* where static meshes opt in. Not an inconsistency: an
attachment has exactly one consumer in the tree and it is the studio, where a static mesh has six.

**Quantisation — reopened on request, and the measurement closed it.** The kit authors `COLOR_0` as
float (21 primitives) and normalised ushort (12), never as bytes, so `UByte4Norm` quantises. Across
all 33 primitives and 93,121 vertices the worst error is **0.00196 — exactly half of 1/255**, the
floor for round-to-nearest at 8 bits rather than a shortfall against it. The distinct-value drop
(CommonTree_1's trunk: 239 authored → 78) is the same fact stated the other way round: the authored
values sit closer together than one step. An earlier note here called that "a real loss"; it
overstated the case, and this is the correction.

**And it is greyscale on every primitive** — largest R/G/B spread on any vertex is 0.0002, which is
ushort round-off, not hue. (Checked because a first pass with a 1e-4 threshold flagged 21 primitives
as coloured; the threshold was tighter than the encoding noise.) So the four bytes carry one
meaningful byte and the only move available is *downward*, not up — declined, because a single-byte
attribute buys three bytes against an unaligned stride.

Covered by **Test.Graphics section AZ** (16 assertions). Negative control run: reintroducing the
drop fails the four AZ.2 value assertions and leaves the layout and identity claims green — the
discrimination the section was written for.

### The spec is the inventory, not the assets — **method correction**

I-A was found by grepping assets: 49 primitives carried a `COLOR_0` nothing read. That works and
it also only ever finds **what this tree happens to own**. glTF 2.0 defines a closed, small set of
mesh attribute semantics (§3.7.2.1, the `mesh.primitive` table —
<https://registry.khronos.org/glTF/specs/2.0/glTF-2.0.html#meshes-overview>), so the complete gap
list is derivable without opening a single file. Recorded in `GltfStaticImporter` where the
accessors are read.

| semantic | status |
|---|---|
| `POSITION` `NORMAL` `TEXCOORD_0` `JOINTS_0` `WEIGHTS_0` | read |
| `TANGENT` | read, opt-in |
| `COLOR_0` | read, opt-in — I-A |
| `TEXCOORD_1` | read with the coloured static layout — I-E |
| `TEXCOORD_2+` | ignored, no current layout |
| `COLOR_1+` | ignored, no consumer |
| `JOINTS_1+` / `WEIGHTS_1+` | read as contiguous pairs; strongest four retained — I-G |
| `_CUSTOM` | ignored; spec reserves the underscore prefix for these |

**The one the asset sweep could never have found is `JOINTS_1`.** Every other gap is a missing
feature; that one is a wrong result — the fifth and later influences are dropped and the vertex
deforms incorrectly, with nothing said. No asset in this tree has it today, which is exactly why
grepping was silent on it, and exactly why that is not evidence.

~~Nothing in the tree warns when an ignored set is present.~~ **Built** — `GltfIgnored`, the
complement of `GltfSkipped`: that one reports whole mesh nodes the rigged import declined, this one
reports the channels declined inside the nodes it took. On both importers, on `AssetLoadLog`, and
covered by Test.Graphics section BA.

**Subtractive, not a list of known-missing names.** It asks what the primitive declares and removes
what the importer reads, so an exporter emitting something nobody anticipated is caught — which a
hardcoded list is deaf to, and which is the one case worth hearing about. Section BA proves that
with a `_CUSTOM_THING` attribute no code anywhere names.

**The explanation separates omission from corruption**, because a reader who cannot tell them apart
triages them the same way: an unread UV set is a missing capability, an unread `JOINTS_1` is a skin
**truncated to its first four influences with vertices deforming incorrectly**. The list is ordered
to put the corrupting kind first.

**And it found one the sweep-by-grep had missed.** `villager_ranger.glb` carries `COLOR_1` and
`COLOR_2` on three primitives each, alongside the `TEXCOORD_1` this plan already knew about. Nothing
had ever said so.

### I-B — the viewer can express what the material already says — **DONE (reshaped on measurement)**

`GltfMaterial` carries `AlphaMode`, `AlphaCutoff` and `DoubleSided` and has done for a long time.
`StudioRig.Part` and `StudioModel` carry none of them.

**The original premise was wrong, and checking it before writing code is the only reason that was
caught.** This section said 26 MASK materials meant `view --model CommonTree_1.gltf` drew every
leaf card as an opaque rectangle. Measured:

| | measured |
|---|---|
| MASK materials | 26 — **all 26 with no base-colour texture and `baseAlpha = 1.00`**, `cutoff = 0.20` |
| BLEND materials | 1 — `core.glb / Material.004`, the only one with a real texture alpha |
| `doubleSided` | 96, of which **8 sit on SKINNED assets** |

Alpha on those 26 is `baseColorFactor.w × texture.a` = `1.0 × 1.0` = **1.0 everywhere**, and
`1.0 < 0.20` is never true. Implementing MASK faithfully would discard nothing and produce a
byte-identical image. The leaf "cards" are not alpha-cut quads at all — `CommonTree_1` prim1 is
**3,840 vertices of modelled leaf geometry**. The exporter stamped `MASK` on foliage as a default,
with no alpha channel for it to act on. **So the MASK third of this stage is decided-no on
measurement**, in the same way the skinned `.blixmesh` layout was.

**What is live is the third this section dismissed as "accidentally satisfied".** Every material on
all three rigged characters is `doubleSided` — `Rogue.glb` (1 material, 12 primitives),
`villager_peasant.glb` (4, including **`MI_Hair_1`** at 646 verts) and `villager_ranger.glb` (3) —
and a rig draws on `skinnedPipeline`, the one pipeline on this stage that culls back faces. On a
closed body that is invisible and the culling comment is right about it. **Hair cards are not a
closed body**, and that is a defect visible in the viewer today on the assets this arc has been
looking at all session.

So the stage is: carry `DoubleSided` through to the rig path and let it pick a non-culling pipeline,
which is rung three of the studio ladder and needs no new mechanism. `AlphaMode`/`AlphaCutoff` are
carried at the same time because they are free once the material fields are threaded — but they are
carried, not demonstrated, and the plan should not claim otherwise.

**Built:** `AlphaMode` / `AlphaCutoff` / `DoubleSided` carried on both `StudioModel.Part` and
`StudioRig.Part`; a `skinnedDoubleSidedPipeline` (the same program and layout, `NoCulling`) beside
the culling one, because face culling is pipeline state in Vulkan and cannot be pushed per draw;
`RigView` picks per part. The caster pass already used `NoCulling`, so it needed nothing.

**Negative controls — run**
- The static path is **byte-identical** across the change (same grass capture hash before and
  after), which is what confines this to the rig path. ✔
- The peasant differs: 731 px (0.02%), max delta 203/255, **mean +40.4** — positive, so surfaces
  are appearing rather than shading shifting — with **81% of the changed pixels in the head band**
  where `MI_Hair_1` sits. A small effect from the default camera, and a real one. ✔
**MASK is now consumed, and demonstrated.** The cutoff and the material's `baseColorFactor.a` ride
the spare slot in the push block's material vector, and the fragment stage discards below it.
**Zero means never**, which is what lets OPAQUE and MASK share one pipeline: a cutout is a
comparison a fragment can make, where blending is pipeline state and cannot be.

Verified against glTF-Asset-Generator's `Material_AlphaMask` — six permutations with a declared
expected result, which is what made this assertable rather than a look:

| effective cutoff | plane pixels |
|---|---|
| 0.0 | 183,304 |
| 0.4 | 176,699 |
| 0.5 (default) | 176,665 |
| 0.571 (0.4 with `baseColorFactor.a` 0.7) | 176,632 |
| 0.7 | 168,082 |
| 1.1 | **0** (only its shadow remains) |

Monotonic, and the `baseColorFactor.a` case lands between 0.5 and 0.7 exactly where multiplying it
in puts it — an implementation that ignored it would sit on top of the 0.4 row.

**Two things NOT built, both stated rather than discovered later.**

*Cutout shadows — **BUILT**, after being deferred on the reasoning §7 exists to forbid.* The
deferral said "no asset in the tree has an alpha source, so nothing asks", which measures our library
rather than the engine; `Material_AlphaMask_03` asks, and it is a download.

At cutoff 1.1 the plane vanished and its shadow stayed. Now the caster cuts out too: **54,269 pixels
of that capture lift by a uniform +132**, which is a shadow of nothing disappearing.

The static caster's push grows 64 → 80 for the cutoff and the material's alpha. **The skinned caster
stays at 16** — they ride components of the vec4 it was already pushing and ignoring. The two casters
push different blocks, so the skinned one needed its own fragment stage; a fragment shader reads
whatever its vertex stage declares, and one shared frag cannot serve both layouts.

**Opaque captures are byte-identical**, unlike the lit cutout: a depth-only pass resolves no colour,
so losing early-Z there changes nothing about which fragment wins.

*BLEND — **the conformance half BUILT, the ordering half deliberately not.*** Deferred before on
"`core.glb` is the only asset in the tree", which §7 forbids; `Material_AlphaBlend` is seven
permutations and a download.

**Built:** a blend pipeline and its skinned twin — depth-tested but not depth-WRITING, never culled,
`BlendState.AlphaBlend`. Alpha reaches the output as the product of the texture's, the material's
`baseColorFactor.a` and the vertex colour's, the same three the cutout tests, because glTF says alpha
is all three whatever the mode does with it. Blended parts draw after every opaque one and are
skipped in the caster pass — a blended caster writes a solid silhouette into a depth-only shadow map,
which is a transparent surface casting an opaque shadow.

**Not built, and stated rather than half-done:** order *within* the blended group. Two overlapping
transparent surfaces composite in the order the asset lists them, not by depth. Per-object sorting,
per-triangle sorting, depth peeling and order-independent blending are all defensible, which by §5 is
exactly when a policy waits for a consumer to choose. Rendering the material is conformance; ordering
it is not.

Verified on all seven permutations: `baseColorFactor.a = 0.7` shows the ground's grid through the
plane, and casts nothing. **Opaque captures byte-identical** across all eleven modes.

**One accepted cost, measured.** A shader containing `discard` cannot be early-Z tested even on
fragments that never take the branch, so every opaque draw now resolves depth late. That moves
between 1 and 60 pixels of a 3.7M-pixel capture — silhouette fragments where a different one wins.
Removing the `discard` restores byte-identity exactly, which is how it was attributed after the
expression was blamed first. Accepted rather than split into a MASK pipeline variant: this stage
draws a handful of objects so early-Z buys it little, and a variant is the first tile of the
permutation matrix this arc said it would not build.

### Where COLOR_0 belongs — **settled by the reference corpus, after two wrong turns**

`COLOR_0` went into `albedo`, then into `ambient`, then back into `albedo`. Worth recording as a
sequence, because each move was made on the best evidence available at the time and the first two
were wrong in different ways.

1. **Into `albedo`.** Correct placement, reached for the wrong reason, and it hid a real bug: the
   BRDF's `max(dot(N, V), 0.0)` was switching the specular lobe off along the camera's eye-height
   plane, which read as the channel being applied too strongly.
2. **Into `ambient`.** Moved on the reasoning that occlusion has no authority over a direct sun ray.
   Sound about *occlusion* and wrong about *this attribute* — and it made the effect nearly
   invisible, since `AmbientStrength` is 0.06.
3. **Back into `albedo`**, once `NdotV` was fixed and **Khronos's `BoxVertexColors` was run**. That
   model is the reference test for vertex colour and it rendered **white**. `COLOR_0` is a
   base-colour multiplier; the nature kit's use of it as greyscale occlusion is an authoring
   convention, not what the attribute means.

**The lesson is about the evidence, not the arithmetic.** Every reading in steps 1 and 2 came from
the only assets this tree owned, and all 33 of those use the channel the same non-standard way. A
corpus of one convention cannot tell you what a convention is. The Khronos sample assets answered it
in a single run.

**An import-time "occlusion vs colour" switch was considered and is NOT built.** With the `NdotV`
fix in place the kit's grass reads correctly under the reference behaviour — bright tips, dark
bases, smooth gradient — so the near-black blade bases are the asset's own authored values being
drawn as written. Nothing is asking for the switch; if an asset later wants occlusion semantics,
`AssetImportContext.IncludeColour` is the flag that would grow the second meaning.

### Sample assets — the corpus, and what it caught immediately

Six Khronos sample models were run through the viewer. Five loaded first try; the sixth was a `.gltf`
whose sibling `.bin` had not been fetched, which was a download error and not a reader gap. Sparse
accessors load fine.

| model | what it showed |
|---|---|
| `BoxVertexColors` | rendered **white** — settled the `COLOR_0` question above |
| `AlphaBlendModeTest` | all five panels **fully opaque** — the demonstration I-B could not have |
| `MultiUVTest` | loads; the second UV set is ignored (I-E) |
| `MorphPrimitivesTest` | loads; morph targets are not read at all |
| `SimpleSparseAccessor` | correct |
| `NormalTangentTest` | loads |

**And a gap the corpus does not cover either:** nothing in it exercises `JOINTS_1` / more than four
bone influences per vertex, which is the one glTF gap here that produces a wrong result rather than
a missing feature.

**Three corpora, and they do different jobs.** Worth keeping straight, because reaching for the
wrong one wastes the trip:

| corpus | what it is for |
|---|---|
| **glTF-Sample-Assets** | realistic models and feature demos — "does this asset look right" |
| **glTF-Asset-Generator** | systematic PERMUTATION MATRICES per feature, with a `Manifest.json`, per-group `README` tables, reference thumbnails, and validator results. Also **negative tests** (`Mesh_NoPosition`, `Mesh_PrimitiveRestart`) — assets that must be REFUSED |
| **Cesium `Specs/Data/Models/glTF-2.0`** | engine-hardening oddities nothing else carries: `BoxInterleaved`, `BoxVertexColorsDracoRGB`, `BoxWeb3dQuantizedAttributes`, `MeshoptCubeTest`, `BoxTexturedKtx2Basis`, `BoxNoNormals`, `BoxInverted`, `BoxBackFaceCulling`, `BoxCutout` |

**The generator immediately paid for itself.** `Mesh_PrimitiveVertexColor` is exactly the six
permutations of `COLOR_0` — vec3/vec4 × float/ubyte-norm/ushort-norm — that the importer's comment
CLAIMED `AsColorArray` collapses. All six now render byte-identically, with the control that makes
that mean something: the image carries 415,625 strongly coloured pixels, so six identical *white*
renders cannot pass. The vec3 cases are the ones worth the trip — their alpha must arrive as 1.0.

That is the shape to reuse: the generator's groups come with a declared expected result, so "all N
render the same" is a real assertion rather than a coincidence of everything being broken equally.

**Not committed.** The models live outside the repo pending a decision on where they belong and on
licensing — Khronos sample assets carry per-model licences (a mix of CC0 and CC-BY), so they need a
`CREDITS.md` entry the way the poly.pizza assets do. The generator's output is Apache-2.0.

### I-F — the viewer can supply the colour the asset does not have — **DONE**

**Measured while answering "is the grass supposed to be greyscale?" — yes, it is.** All 40 materials
across the nature kit are `baseColorFactor = (1,1,1,1)` with **no texture**. The asset ships
geometry, baked vertex AO, and a material NAME. Nothing else. `SettlementArt` is what turns
`"Grass"` into `(0.110, 0.155, 0.060)` and `"Bark_NormalTree"` into `(0.085, 0.058, 0.038)`.

So the viewer renders these correctly and still **cannot show what any kit asset looks like in the
game**, which is a strange limitation for the tool whose job is looking at assets.

`StudioModel.Part` drops the material name — it keeps `BaseColour`, which is the white the file
supplied — so the viewer cannot currently even say which material a part uses. The stage:

1. Carry `Name` through `Part` (one field; it is already in `GltfMaterial` at the call site).
2. A materials panel listing the distinct names with a colour swatch each, defaulting to the
   asset's own value.
3. `ModelView`/`RigView` push the override in place of `part.BaseColour`.

**The tint table stays in the game.** `SettlementArt` lives in RTSGame and the studio must not
reference a game — but because the panel keys on material NAME, it shows the exact surface a game
tints against while knowing nothing about any game. Audition a green, read off the RGB.

**Settled: session-only.** Persisting would make this a store of colours, and the game's table
already is one — a second store that can disagree with the first is worse than none. What is
auditioned here is a number to go and write down where the table lives.

**Done, in the three steps the stage named.**

1. `MaterialName` is carried through all four part records — `StudioModel.Part`, and the rig's
   `Part`, `Attachment` and `StaticPart`. It was already on `GltfMaterial` at every call site.
2. `StudioTints` is the mechanism: a colour per material name, `Resolve(name, assetColour)` so no
   view has to write the "or the one from the file" branch four times. The materials panel lists the
   distinct names with a swatch each, defaulting to what the file said, and prints the RGB beside any
   it is overriding — because the RGB is the deliverable, going into a table somewhere else by hand.
3. All four colour pushes route through it, and both views take an optional `Tints`. Null is the
   default and null means draw the asset, so a capture of an asset still shows the asset.

**The premise was re-measured rather than carried forward, and the first place I looked was wrong.**
The nature kit is `.obj`/`.mtl` and those DO carry colours (`Kd 0.069954 0.121857 0.047888` for
"Green"). The assets this is for are `src/RTSGame/Assets/models/kit/` — **29 glTF files with an empty
`pbrMetallicRoughness`**, so no factor and no texture, carrying **12 distinct material names** between
them: `Grass`, `Bark_NormalTree`, `Leaves_Pine`, `PathRocks`… exactly the keys `SettlementArt` tints
against.

**`--tint Name=R,G,B` on the capture tool, repeatable.** The panel is where a colour is auditioned;
this is how the answer reaches something diffable, so "what a kit asset looks like in the game" is a
picture that can be compared rather than a window somebody was looking at. Verified on `Pine_1.gltf`:
as-authored is a white tree, and `--tint Bark_NormalTree=0.085,0.058,0.038 --tint
Leaves_Pine=0.094,0.179,0.012` is a green pine with brown bark — from a viewer that references no
game.

`Blix.Test.Studio` 20/20, five of them on the table's own behaviour including the one that matters:
an unnamed material must not become a key, or a single entry under `""` would tint every nameless
material together and read as a bug in the panel.



> **Chased later, noted so it is not lost:** `--debug` arms the diagnostics system
> (`Window.cs:98` sets `State.Enabled`) but the overlay also needs `State.ShowOverlay`, toggled by
> the backtick key (`Window.cs:307`). That part works. What is missing is the **controls tab inside
> the debug panel** — the stage's `[Tune]` properties are reachable as CLI flags through
> `ObjectTunables.Apply` and are drawn in the viewer's own `lab` window, but they do not appear as a
> tab in the debug overlay where one would look for them.

### I-C — per-instance attachments — **DONE**

Deferred from the attachment arc with the reason recorded: `RigView` draws N bodies from one sliced
palette, and N bodies each holding a different weapon needs a per-instance selection.

**And the instrument for it already exists.** The viewer draws N instances on independent clocks,
with a lockstep control whose entire purpose is demonstrating they are *not* frame-locked, and a
pose fingerprint that makes "they differ" a count rather than an impression. Instance 0 with a knife
and instance 1 with a crossbow answers the question on screen, with an existing control proving the
independence is real rather than a coincidence of timing.

**Built as a delegate, and the reason is the interesting part.** `RigAnimation.InstanceBoneWorlds`
hands back a SHARED scratch for every body past the first. `RigView` is built fresh each frame but
`Draw` runs later, during pass recording — so the obvious implementation, collecting the worlds into
an array at construction, fills it with N references to one buffer and draws every body's gear in
the LAST echo's pose. No crash, no warning; the same aliasing that once gave every part of a model
the last part's albedo. The alternatives were materialising N real arrays (correct, allocates
boneCount matrices per body per frame) or handing `RigView` the session (correct, and it collapses
the one property that keeps durable state and a per-frame draw description separable).

So the view takes `Func<int, IReadOnlyList<Matrix4x4>>` and asks at draw time, one body at a time.
**`DrawAttachments` must stay instance-outer and attachment-inner** for that to hold, and says so.
`InstanceAttachments` follows the same shape, with per-body overrides sparse in the viewer so the
ordinary case (everyone carrying the same thing) costs no per-body state.

**Controls — run**
- Three Rogues with `--attach Knife`: **before, only the leftmost carried it**; after, all three do,
  each knife tracking its own body's arm. ✔
- **One instance is byte-identical** across the change, which is what shows the delegate reproduces
  the old path exactly rather than merely resembling it. ✔
- `--lockstep` still reports one pose in every slot, so the instrument I-C rides on is intact. ✔

**Found on the way:** `RigAnimation`'s header says it was extracted because "both lab executables" had
grown their own copy — but `Blix.Tools.Shot` still keeps its own `ClipPlayer`s and `BonePaletteSet`
and does not reference `RigAnimation` at all. The wiring here is therefore duplicated in both tools
rather than shared. Not addressed; recorded so the header stops being believed.

### I-D — read every skin the file declares — **DONE**

**Marked late.** It shipped with the arc — no "primary skin" concept survives anywhere, nodes are
grouped by the skin that drives them, and each gets its own remap, frame and palette. Verified again
on 2026-09-17 by booting it: `tank.glb` reports **45 bones, 4 clips, 7 primitives, 10,104 vertices**,
where before it took one skin and dropped six named primitives. `GltfSkipped.cs` is deleted; there
is nothing left that skips.


Today the importer picks the first node carrying both a mesh and a skin, calls that skin primary,
and drops every skinned node referencing a different one. `tank.glb` is the asset: eleven primitives
across three skins, five imported, six named and lost.

**The previous text for this stage reasoned in a circle, and the reasoning is retracted.** It said:
*"`tools/character_merge.py` exists — 689 lines of Blender — specifically to avoid needing this, so
the honest outcome may be a decided-no with the merge named as the answer."* And
`character_merge.py`'s own header says: *"The importer's rules this script exists to satisfy: ONE
skin per file."* The workaround exists because of the engine limit, and the engine limit was
justified by the workaround existing. That is not an argument, and it was quoted as one more than
once.

**Nor is this a hard problem, which the old framing obscured.** "One skeleton or a mapping between
them" reads like a research question. In glTF a skin is SELF-CONTAINED — a joints array plus its own
inverse bind matrices — and every skinned node names the skin it uses. There is nothing to map. N
skins are N joint arrays and N palettes. The obstacle is entirely our data model:

- `GltfModel` carries a single `Skeleton`
- `GltfPrimitive` is `(MeshData, GltfMaterial?)` and has no skin identity at all
- so the importer must choose one skin and discard the rest

**And the constraint is not neutral about which assets Blix can read.** It is comfortable with
anything authored through our own Blender and hostile to anything downloaded — the category that
found three real faults in a single afternoon (the `COLOR_0` placement, the alpha fields with no
asset to exercise them, the six-encoding claim that had never been checked). An engine whose bar is
"what our exporter emits" rather than "what the format defines" cannot read other people's work.

**The narrow version was proposed and withdrawn.** "Accept a secondary skin when its joint set and
order match the primary" would recover `tank.glb`'s two track meshes and nothing else — it encodes
that one asset's shape into the reader, and would look principled while being a second workaround on
top of the first.

**What tank.glb measures**, which is useful as data even though it no longer decides the design:
identical joint sets (45, pairwise intersection 45/45), identical joint order, rotation and scale
components of the inverse bind matrices identical to `0.000000` — and **bind translations differing
by up to 0.0397, about 20% of the model's diagonal.** So the skins are NOT interchangeable, and
reusing the primary's palette would misplace the tracks by a fifth of the tank. That is the trap the
narrow version would have had to avoid anyway.

**Stages.** Additive rather than breaking, so the six single-skin assets and their consumers
(RTSGame, Bulwark, Runner) are untouched at every step:

- **I-D1 — DONE.** The importer reads every skin, and there is **no primary skin any more** —
  nodes are grouped by the skin that drives them, every skeleton is built the same way, and the
  order is only the order they were met in. `GltfModel.Skins` carries them, `GltfPrimitive` and
  `GltfAttachment` say which one they belong to, and `Skeleton`/`MeshNodeTransform` remain as
  shorthand for skin 0 because most rigs have exactly one. Joint remapping is per skin.
  **Remaining edge, recorded not hidden:** animation clips are bone INDICES, so they are built
  against skin 0's ordering. Correct wherever skins agree on joint order — which `tank.glb` does —
  and the file that breaks it should force the next shape.
- **I-D2 — DONE.** One palette buffer per skin on `StudioRig`, one `BonePaletteSet` per skin in
  `RigAnimation`, and `RigView` picks by the part's `SkinIndex`. The pose is shared — the joints
  are the same nodes — so this is N uploads of one posed hierarchy through N sets of inverse binds
  and N authored frames, not N poses. **Negative control:** forcing every skin to skin 0's bind
  collapses tank.glb's tracks into the hull instead of flanking it, which is the 3.97 offset made
  visible. `Blix.Tools.Shot` needed the same packing written a second time, because it keeps its own
  players rather than a `RigAnimation` — the duplication already noted above, now with a second
  instance of it.
- **I-D3 — DONE, and it ended by deleting the reporting.** `GltfSkipped` existed to make the
  importer's refusals visible. There were two: a mesh on a second skin, and a static mesh under no
  joint. **Neither was a format restriction** — a glTF node with a mesh and no skin is an ordinary
  mesh in the scene, and "not equipment" was this importer's rule. Both are now read, so nothing is
  skipped and the type has gone with them. `GltfStaticPart` carries the second case, placed by the
  world matrix its node already has.

  Kept separate from `GltfAttachment` deliberately: an attachment follows a joint and a turret does
  not, so folding them together would need a joint index meaning "no joint" — a contradiction in a
  field name — and would drop scenery into the viewer's weapon picker.

  `blix check`'s block for this used to FAIL the check, calling a half-read asset unsound. Right
  about the symptom, wrong about the cause; it is now a listing.

  **tank.glb imports 11 of 11 primitives**, the number it always had. Control: every other rigged
  asset reports zero static parts, and the Rogue's six attachments are still attachments.

**`character_merge.py` keeps its job and loses one bullet.** Merging N single-animation FBX files
onto one rig, retargeting a CC0 library onto another skeleton, and flattening interpolation to
LINEAR are real authoring work no importer replaces. "ONE skin per file" is the only line in it that
exists to serve this limitation, and it is the only line this arc removes.

**Negative controls**
- Every single-skin asset in the tree imports **byte-identically** — six characters across three
  exporters. This is the control that matters most: the change is additive or it is not.
- `tank.glb` gains its two track meshes, and they land in the right place, which the 0.0397 bind
  translation makes a real test rather than a formality.
- A file whose second skin genuinely cannot be read is still reported rather than silently dropped.

### I-E — the second UV set — **DONE**

`TEXCOORD_1` on 9 primitives, `_2` and `_3` on 4. Measured first: the sets are genuinely distinct
(every vertex of `villager_peasant`'s differs from its first, by up to 3.64 in UV space), and **zero
of the 18 texture channels in this tree sample anything but set 0**.

That measurement was used once to close the stage, which §7 forbids — "a test wanting it is not
content wanting it" dismissed `MultiUVTest`, which exists precisely because a reader is supposed to
handle this. The tree's contents are an accident of what was downloaded.

**Built.** The importer reads `TEXCOORD_1` on the same opt-in as the colour, because both widen one
studio layout and separate flags would let a caller ask for a layout no vertex type declares. A mesh
with no second set gets its first one **mirrored rather than zeroed**: a material naming set 1 anyway
then samples the same place, where (0,0) would collapse the surface onto one texel and read as a
broken texture rather than a broken asset. `GltfMaterial` records which set its base colour samples,
because that is a property of the CHANNEL — glTF lets every texture choose independently.

**Verified by changing one field.** `MultiUVTest`'s base colour names set 0; a copy naming set 1 —
one JSON edit, nothing else — moves **615,936 pixels**. With the shader's selection disabled the two
are byte-identical, which is what says those pixels came from honouring `texCoord` and not from
anything else.

**Scope stated:** the studio samples base colour and nothing else, so that is the channel that
honours its set. `MultiUVTest` puts its second set on EMISSIVE, which this stage does not have — a
different gap, and not one a UV set fixes.

---

### I-G — more than four bone influences — **DONE**

**The one glTF gap here that produced a wrong result rather than a missing feature**, and the one no
corpus covers: not this tree, not the Khronos sample assets, not the asset generator. Conventions §7
says that is not a reason to leave it, so the fixture was authored — the way BB.2's was.

glTF allows `JOINTS_1`/`WEIGHTS_1` and beyond. Weights sum to 1 across **all** sets, so reading only
the first left them summing to less, and a skinning matrix scaled by 0.8 drags its vertex a fifth of
the way to the origin. Nothing counted down and nothing warned.

**Built:** every influence set is read; the **strongest four** are kept and **renormalised**. Not the
first four, because glTF does not require the sets to be sorted and "first" can discard the influence
that actually shapes the vertex.

**Four stays the limit, and that is now a decision rather than an accident.** Widening the vertex to
eight costs 32 bytes on every skinned vertex in every asset, for influences that are almost always
negligible — bandwidth against fidelity, which by §5 is a trade to make deliberately. Approximate
deformation is a limitation; a collapsing mesh was a bug. The first implementation reported the
whole additional set as ignored; the later mode-aware collector corrected that category error,
because those attributes are inputs to the strongest-four selection even though not every entry is
retained.

**Negative control:** removing the renormalisation puts the sum back to **0.8000** — the collapse,
named exactly. **Control:** a single influence set passes through untouched, compared against the
file's own accessor rather than against what the builder was handed, because SharpGLTF sorts
influences as it writes and a first draft of that assertion failed on the fixture rather than the
importer.

---

## Order, and why

I-A → I-B → I-C → I-D, with I-E parked.

**I-A first because it is the only one with a live consumer losing something today** — thousands of
scatter models per frame, in a game that already runs. **I-B second because it is the viewer's own
completeness**, and the rest of this arc is judged by looking through it; a tool that cannot draw a
cutout is a poor instrument for deciding anything else about foliage. **I-C third because it is
bounded and its instrument exists.** **I-D last because it is the only one that might end in a
decided-no**, and the other three inform whether it is worth it.

---

## What this arc will not build

The Blender pipeline as a recipe · skeleton or clip sharing between files · morph targets · glTF
extension handling · a material system · a shader permutation matrix · retargeting · an asset
authoring path.

**And it will not move boundaries to make room.** Nothing enters or leaves the engine unless that is
right on its own terms — the rule that kept `BodyResolver` in a lab, `PropModel` at one consumer,
and the studio outside the engine entirely. A capability can be proved without being promoted.

**The instrument is the viewer first, a proof-gate demo only when the viewer cannot show it.**
`Blix.Demos.VulkanInstanced` is the precedent for the second case: 180 lines whose whole job is that
5,000 cubes go through one draw call, auto-exiting after more frames than there are frames in flight
so both replicated SSBO slots are written and bound at least once. That is how a capability gets
proved when nothing is waiting for it — but the viewer already draws instances, masks, attachments
and materials, and it should be asked first every time.

---

## After the arc: banking the corpus, widening the baseline

Two loose ends the closing assessment named, both now closed.

### The corpus was banked as knowledge, not as bytes

The three corpora had been load-bearing for four findings while living in a scratchpad that
disappears with the session. Banked as **`third_party/gltf-corpus.manifest`** — which asset, from
which pinned commit, and **what each one proves** — plus **`tools/fetch-gltf-corpus.sh`**, which
pulls 190 files into a gitignored tree in under a minute.

**Not committing the bytes is §7 applied to this repo's own tree**: the specification is the
requirement, and owning an asset that exercises it is a download. It also keeps three upstream
licences out of this history, and each Sample-Assets model's own `LICENSE.md` is fetched beside it,
so the licence travels with the bytes rather than depending on someone remembering to check.

Three things the fetcher learned the hard way, all recorded in it:

- **The GitHub contents API is the wrong instrument**, at 60 requests an hour unauthenticated —
  exhausted while the manifest was still being written. Worse than slow: a rate-limited enumeration
  returns an *empty directory*, not an error, so a silent no-op looks like a model with no files.
  Every path is explicit now, and a rename upstream surfaces as a 404 naming the file.
- **Plan first, then one parallel fetch.** A curl per file costs ~15 s of handshake here; fifty
  minutes became under one.
- **A trailing `#` comment must be stripped before the line is split.** Reading the manifest
  straight into positionals made "what this asset is for" into arguments, and built a URL ending
  `.../AlphaBlendModeTest/#/with`.

One asset is **derived rather than downloaded**, expressed as the edit and not as bytes:
`MultiUVTest_uv1.gltf` is `MultiUVTest` with `baseColorTexture.texCoord` set to 1. Upstream ships a
second UV set and no material that *names* it, so the stock file cannot tell a reader that honours
`texCoord` from one that always samples set 0.

### The baseline was widened where it was blind

`tools/lab-baseline.sh` — 23 modes, 68 hashed artifacts, `record` / `check` / `list`.

**The blind spot, stated plainly.** The eleven modes it grew from were all single-body wherever
motion was involved: every `--drive-root` mode ran one instance. So the set was structurally blind
to the whole multi-body class, and all three faults of that class in this arc — root motion fanning
out to every body, travel distance read from body 0, reset resetting only body 0 — were found by the
user running the viewer. **An instrument that cannot fail on a bug is not evidence about that bug.**

Two changes close it:

1. **`inst3-travel`, `inst3-travel-lockstep`, `inst5-travel`.** Five as well as three, because an
   index fault that reads body 0 or body N-1 passes at three.
2. **Each mode hashes the tool's filtered stdout as well as its image.** The capture tool already
   prints a per-body pose fingerprint, a distinct-pose verdict and an integrated root travel —
   numbers that *state* what a picture only implies. Filtered, because stdout is evidence only in
   the part that is the same on any machine.

**Checked, not asserted.** Re-introducing the singular `DriveRoot` fault turns exactly those three
modes red and leaves `advance`, `inst3` and `lockstep` green — and the lockstep mode names it rather
than merely differing: *"CONTROL FAILED — one clip at one instant produced 2 poses"*, with body 0
unchanged and bodies 1+ changed, which is the singular-bug signature readable off the diff.

Corpus-backed modes are skipped when the corpus is absent, and **`check` then fails rather than
passing quietly** — §5's corollary, since a control that silently shrinks to what you can run
reports green for the wrong reason.

### What the negative modes found on their first run

Two modes assert an **exit code** rather than an image, and one of them was red immediately:
`Mesh_NoPosition` **aborted with a stack trace (134) instead of refusing**.

The cause is one line from a remark that already described this bug. Every importer wrapped
`AssetImportException.Refusing` around `ModelRoot.Load` — **the parse, and not the import**. So
SharpGLTF's refusals were dressed and Blix's own conformance checks were not, and a file the parser
*accepts* while glTF's rules reject it — valid JSON, invalid mesh — escaped as an unhandled
exception. `Mesh_PrimitiveRestart` refused cleanly the whole time, because that one is caught during
the parse; the two look like the same test and exercise different halves.

Fixed in all three entry points (`GltfImporter.Import`, `GltfStaticImporter.Import`,
`GltfStaticImporter.ImportNodes`) by wrapping the import body rather than the load call.

**The fix changed one existing test, and the test was the thing that was weaker.** AZ.5 asserted a
bare `NotSupportedException` for tangents-plus-colour; it now arrives as the engine's one refusal
type, naming the file, with the reason on `InnerException`. AZ.5 asserts **both halves** now — which
took `TestRunner.ExpectThrows<T>` returning the exception rather than void, since `mustMention` can
express a substring and not a type.

`Blix.Test.Graphics` 649/649 · `Blix.Test.Diagnostics` 235/235 · `Blix.Test.Physics2D` 43/43.

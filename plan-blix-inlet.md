# Inlet arc — plan

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

### I-A — vertex colours survive import

`COLOR_0` on **49 primitives**, and it is not decoration. Sampled from the content: greyscale,
0.0 to 1.0, 152 distinct values on one tree trunk and 51 on a blade of grass, with the tree's leaf
card uniformly 1.0. **That is baked ambient occlusion** — grass darkening at its base, bark in its
crevices — thrown away at import on every piece of scatter RTSGame draws, which is thousands per
frame.

The engine already has the layouts: `VertexPosition3TextureColor` and `VertexPosition3Color` exist
and the glTF path simply never reaches for them.

**This does not change the hand-tinting**, and the distinction matters. `SettlementArt` colours the
kit by material name on purpose, and its comment argues the case: the pack's textures are palettes
rather than pictures, and a texture path for flat-shaded low-poly props is a great deal of work for
little. Vertex AO is a *second* channel that multiplies what that already decides. Reading it as
"the colour we were missing" would be the wrong lesson from the right finding.

**Negative controls**
- An asset with no `COLOR_0` produces a **byte-identical** capture.
- A blade of grass visibly darkens at its base and not at its tip — the thing AO is *for*, checked
  by looking, because a uniform multiply would also pass a numeric test.
- The white leaf card (`COLOR_0` = 1.0 everywhere) is unchanged, which is the control that catches a
  channel being applied in the wrong space.

### I-B — the viewer can express what the material already says

`GltfMaterial` carries `AlphaMode`, `AlphaCutoff` and `DoubleSided` and has done for a long time.
`StudioRig.Part` and `StudioModel` carry none of them, so the stage draws every surface opaque.

**26 materials in this tree are `MASK`, and all 26 are the nature kit's foliage.** So
`view --model CommonTree_1.gltf` draws every leaf card as an opaque rectangle — the viewer cannot
show the asset the way the asset says it should look, which is a peculiar thing for the tool whose
job is looking at assets.

`doubleSided` is true on 96 materials and currently *accidentally* satisfied, because the stage's
pipelines mostly use `NoCulling`. Accidentally correct is still worth making deliberate: the one
pipeline that culls is the character pipeline, and a double-sided cape is exactly the case that
would find it.

**Negative control**: an opaque asset is byte-identical; a cutout asset visibly gains holes, and
setting `AlphaCutoff` to 0 and 1 moves them in the directions the numbers say.

### I-C — per-instance attachments

Deferred from the attachment arc with the reason recorded: `RigView` draws N bodies from one sliced
palette, and N bodies each holding a different weapon needs a per-instance selection.

**And the instrument for it already exists.** The viewer draws N instances on independent clocks,
with a lockstep control whose entire purpose is demonstrating they are *not* frame-locked, and a
pose fingerprint that makes "they differ" a count rather than an impression. Instance 0 with a knife
and instance 1 with a crossbow answers the question on screen, with an existing control proving the
independence is real rather than a coincidence of timing.

### I-D — a second skin is read, not only reported

Today the importer says what it dropped and drops it anyway. `tank.glb` is the asset: eleven
primitives across three skins, five imported, six named and lost.

The design question is the whole stage and should not be pre-answered here: **one palette or
several, one skeleton or a mapping between them.** `tools/character_merge.py` exists — 689 lines of
Blender — specifically to avoid needing this, so the honest outcome may be a **decided-no** with the
merge named as the answer. Reaching that conclusion with `tank.glb` in a window is the output;
assuming it now is not.

### I-E — the second UV set, parked with a reason

`TEXCOORD_1` on 9 primitives, `_2` and `_3` on 4 — the two textured villagers, the mannequin, and
nothing else. Nothing in the tree reads a second UV set and no shader has a slot for one, so what it
is *for* in these assets is unknown, and building a channel for an unknown purpose is how a vertex
layout grows a field nobody can explain in a year. **Recorded, not built**, until something asks.

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

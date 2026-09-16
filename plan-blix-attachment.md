# Attachment arc — plan

> The Rogue has twelve primitives and Blix loads six. The other six are a knife, two crossbows, a
> throwable and a cape, each parented to a joint — which is how every game character holds anything,
> and a shape this engine has no word for.

---

## What this is

**A static mesh whose parent is a joint, posed by that joint.** Not an equipment system, not an
inventory, not sockets-by-name: the single missing seam between two things the engine already does
well.

It is also the arc the viewer existed to produce. `view --rig Rogue.glb` has been run dozens of
times across several arcs, and it shows a character with empty hands — which nobody noticed, because
a character with empty hands looks exactly like a character.

---

## The finding

`Rogue.glb`: 54 nodes, 12 mesh-bearing, **6 skinned and 6 not**. The six that are not:

| mesh | parented to |
|---|---|
| `Knife_Offhand` | `handslot.l` |
| `1H_Crossbow`, `2H_Crossbow`, `Knife`, `Throwable` | `handslot.r` |
| `Rogue_Cape` | `chest` |

**Neither importer loads the asset.**

- **Rigged** (`view --rig`) takes nodes carrying both a mesh and a skin: 6 primitives, 41 bones, 76
  clips. Body only. It drops the other six **silently** — no warning, no count, no report.
- **Static** (`view --model`) takes all 12 mesh nodes, in bind pose, with five weapons overlapping
  in one hand and no animation at all.

And nothing in the engine has the concept. `grep -rn "attach\|socket\|AttachPoint"` across `Blix`,
`Blix.Render` and `Blix.Tools.Studio` returns only unrelated uses of the word.

### How general is it — honestly, not very

Every `.glb` in the tree, checked:

| asset | skinned prims | static prims | attached to a joint |
|---|---|---|---|
| **Rogue.glb** | 6 | **6** | **yes** |
| villager_dressed | 11 | 0 | — |
| villager_peasant / ranger / universal | 4 / 3 / 2 | 0 | — |
| enemy.glb (Bulwark) | 5 | 0 | — |
| cesium_man | 1 | 0 | — |

**One asset.** So this is not §4 evidence and should not pretend to be — §4 is about extracting
shared code once a second consumer exists, and there is one consumer.

**The argument is different and it is stronger: the importer silently discards half of a file it
reports as loaded.** That is the same class of fault this tree has spent a week finding — a silent
fallback, an invisible decision, a load that does less than it says. It would be worth fixing if the
count of interested consumers were zero.

The second argument is scheduling: the Rogue is the tree's **reference rig**, the subject of every
toolchain and character-lab capture, and the character arc's Controller stage is about a body that
walks while swinging a weapon. It currently has no way to hold one.

---

## What the engine already has, and the one thing it lacks

**Parenting a static mesh to an articulated transform is solved.** TankArena is hull → turret →
barrel through `Transform3D.SetParent`, and shells detach mid-flight with `keepWorldPose`. That is
the same shape as a knife in a hand.

**Skinning is solved.** `Skeleton.ComputeBonePalette` → `BonePalette` → the skinned vertex shader.

**What is missing is the joint's world transform — and it is already computed.**
`ComputeBonePalette` builds a local `worldMatrices[]` array every frame and **discards it**, keeping
only `worldMatrices[i] × InverseBindPose[i]`. That product is a *skinning* matrix: it maps a rest
vertex to its posed position. An attachment does not have rest vertices in the skin's space — it has
its own mesh and its own offset — so the palette is exactly the wrong thing to hand it, and the
right thing is thrown away one line earlier.

That is the whole gap: **one array, computed, dropped.**

---

## Stages

### W-A — the importer stops dropping them

`GltfModel` gains `Attachments`: for each static mesh whose transitive parent is a joint —
the mesh data, the material, **the joint index in the skeleton's remapped ordering**, its local
transform relative to that joint, and its node name.

Nothing is drawn yet. `blix check --rig` reports them; the probe asserts the count and the parent
joint by name.

**Why first, and alone**: it is the half that is a correctness fix rather than a feature. Once the
data survives import, whether anything draws it is a separate decision — and if the rest of this arc
were abandoned tomorrow, `check --rig` would still say the Rogue has six attachments instead of
pretending it has none.

**Negative controls**
- An asset with no attachments reports none, and its body primitives are unchanged **bit for bit**.
- An attachment's joint index resolves to the bone the glTF names — `Knife_Offhand` → `handslot.l`,
  not to bone 0 and not to whatever index the unremapped skin used.
- A static mesh parented to a *non-joint* node is **not** an attachment and stays out.

### W-B — the skeleton stops discarding the world transforms

`ComputeBonePalette` gains a sibling that fills a caller-provided `Matrix4x4[]` with joint world
transforms, on the same walk. No second traversal, no new allocation per frame.

**Negative control**: for the rest pose, `world[i] × InverseBindPose[i]` is the identity for every
bone — which is the same invariant `CreateRestPose` already documents, read from the other end.

### W-C — the studio draws them

`StudioRig` carries the attachments; `RigView` draws each through the **standard lit pipeline**, not
the skinned one, with its model matrix composed as `local × jointWorld × rigTransform`.

**This is a rung-2 case and should cost nothing structurally** — an attachment is a draw, and
`IStudioView` was opened for exactly that. If it needs a fourth pipeline or a change to `StudioPush`,
the ladder was wrong and that is worth knowing.

**Negative controls**
- With every attachment hidden, the frame is **byte-identical** to today's capture.
- With one shown and the clip paused at t=0, it sits where the glTF's bind pose puts it.

### W-D — the viewer shows and controls them

An Attachments panel: each by name, its joint, a visibility toggle. Default — **all hidden**, because
five weapons in one hand is not a picture of anything.

`--attach <name>` so a bounded run and a capture can reach one, for the same reason `--mask` exists:
a mode reachable only through a checkbox is a mode nothing checks.

**This is the stage the whole arc is for.** A knife that is one frame behind the hand, or attached to
the wrong joint, or composed in the wrong order, is invisible in a test and obvious in a window.

### W-E — acceptance, from the chair — **PASSED 2026-09-16**

Checked from the chair across **every clip and every weapon combination**: nothing lags the hand,
nothing detaches, the cape follows the chest. No fault found — which is worth recording precisely
because four of the character arc's five stages had one that only a person watching could see, and
planning for a fifth was the reason this stage existed.

The original text follows.

### W-E — acceptance, from the chair

The knife stays in the hand through **all 76 clips**, not just the rest pose and not just a walk. The
cape follows the chest. Nothing separates during `2H_Melee_Attack_Spin`, which is the clip with the
most rotation in it.

Every stage of the character arc had at least one fault only a person watching could see, and four of
five were legibility rather than mechanism. Planning for one here is cheaper than being surprised.

---

## The decisions, and where they are made

**Which attachment is visible is not the engine's business.** Five meshes hang off `handslot.r`; a
game shows one. The engine exposes them by name with their joints and draws what it is told, exactly
as stage D gives the caller weights and no structure. An engine that picked would be an equipment
system, which is the thing this will not build.

**Composition happens in the consumer, not the importer.** The importer records
(joint index, local transform). Where the attachment ends up is `local × jointWorld × whatever the
consumer's own placement is` — and the consumer is the only one that knows the last term.

**Instancing is deferred and named.** `RigView` draws N bodies from one sliced palette. N bodies each
holding a different weapon is a real case and is not this arc; the shape that would serve it is a
per-instance attachment selection, and nothing is asking yet.

**A skinned attachment is out of scope.** A cape *can* be skinned; `Rogue_Cape` is not. If one turns
up, it is not an attachment at all — it is another skinned mesh sharing the skin, which the importer
already handles.

---

## What this arc will not build

An equipment or inventory system · sockets registered by name · attachment authoring · per-instance
loadouts · physics on attachments · a second skeleton type · retargeting between rigs.

---

## Order, and why

W-A → W-B → W-C → W-D → W-E.

W-A first because it is the correctness half and stands alone. W-B before W-C because drawing needs
the transform and the transform is a two-line change to something already computed. W-D last of the
building stages because it is the instrument, and an instrument built before the thing it measures
has nothing to show.

**The arc is complete.** W-A through W-E are in. The honest stopping point is after **W-D**: at that point the Rogue loads completely, draws
completely, and you can see which joint each piece hangs from. Whether anything else in the tree ever
grows an attachment is a separate question, and the answer today is that nothing has.

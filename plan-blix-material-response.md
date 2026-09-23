# Material response arc — plan

> **Status 2026-09-21 — active.** Shared sheen vocabulary, probe data, and
> Sponza's raster response exist. The planned per-material response in the
> light-transport bake is not yet represented by the current global control.

> The engine now *reads* thirteen material extensions and *responds* to one. Reading a property and
> shading it are different obligations, and only the first one is finished.

---

## What this is

`ac58448` taught the importer every `KHR_materials_*` property SharpGLTF surfaces, and `ed477d1`
gave a scene a way to author properties its asset declined to. Both are about the numbers existing.
Neither changes a pixel: `lit.frag` reads `TransmissionFactor` and nothing else, so Sponza's
curtains carry sheen and diffuse transmission in their cooked artifact and still shade as painted
board.

This arc is the response half. It is deliberately a separate arc, because reading a spec is
conformance and shading it is a BRDF decision — and conflating those is how a renderer acquires a
"fast path" nobody measured.

---

## The boundary, which is the actual deliverable

**The spec answer is the default, everywhere.** It ships in `Blix.Shaders/` as vocabulary — the same
place `pbr.glsl` and `shadow.glsl` live — and Sponza and Studio include the *same file*, so "the
engine's answer" and "the studio default" cannot drift into two answers.

**A game may substitute, and has to earn it.** A cheaper lobe lives in that game's own shader, at
its own call site (§6). The engine never ships a second implementation beside the real one labelled
fast, because that is how the fast one silently becomes the default.

**A substitution is A/B-able against the library version**, the way `--ab` already isolates GTAO and
IBL in Sponza. "Cheaper" is then a measured claim rather than an assumed one.

---

## What the cost actually looks like

Assessed before building, so the boundary above is affordable rather than aspirational.

| | frame cost | asset cost |
|---|---|---|
| **diffuse transmission** | `max(dot(-N, L), 0)` for the sun, irradiance cube along `-N` for ambient — the cube already exists | none |
| **sheen, analytic lobe** | Charlie distribution + Ashikhmin visibility: one `pow`, one reciprocal | none |
| **sheen, energy compensation** | one LUT fetch | one small LUT |
| **sheen, IBL** | one cube fetch | **a second prefiltered cubemap per probe**, convolved with Charlie rather than GGX |

So the shape is **frame cost negligible, asset cost real** — the opposite of a killer, and it means
the thing a budget-constrained game would drop is the second cubemap. That is an asset-budget
decision rather than a shading one, which makes it an unusually clean thing to deviate on.

---

## Stages

### A — the shared vocabulary
`Blix.Shaders/sheen.glsl` and the diffuse-transmission terms: Charlie D, Ashikhmin visibility, the
sheen albedo term, and `blix_diffuseTransmission`. Vocabulary, not a turn-key evaluator — `pbr.glsl`
says why. Tested in isolation before any scene consumes them.

### B — the terms are checked in isolation
A BRDF that cannot be evaluated on its own gets debugged by staring at Sponza. That is how the GTAO
slice-direction Y-flip survived: the floor was dark, and a dozen other things could also have made
the floor dark.

Narrowly scoped on purpose — enough to assert the invariants that catch the real mistakes (the lobe
carries its energy to the horizon rather than the normal; the visibility term is NOT divided by
4·NdotL·NdotV a second time; sheen scaling stays in [0,1]; transmission is zero from the front and
positive from behind). **Studio growing views for every material extension and asset type — probe
previews among them — is its own arc**, and folding it in here would make this one about the
laboratory instead of about the BRDF.

### C — the probe grows a Charlie cube
`.blixprobe` gains the sheen-prefiltered environment. Conformance, §7, and a format bump the cook
already knows how to do.

### D — Sponza responds
`lit.frag` includes the library and shades the curtains. The first scene-level check of the whole
chain: patch → cook → artifact → loader → shader.

### E — the bake responds too
A curtain that scatters light in the raster and blocks it in the voxel grid is the producer/consumer
split this session closed twice already. The albedo grid is RGBA8 with **alpha unused**, so per-
material `DiffuseTransmissionFactor` packs into it at zero storage cost, and the injection's global
`Translucency` control becomes its default rather than its source — the end condition written down
when that slider was added.

---

## What is deliberately not here

**No second BRDF.** Until something measures a reason.

**No `KHR_materials_volume` response.** Thickness and attenuation are read and stored; responding to
them means refraction against a scene-colour copy, which is a transmission pass and its own arc.

**No sheen on the voxel bake.** Sheen is a view-dependent rim; a probe has no view. Diffuse
transmission is the half of cloth that indirect lighting can carry.

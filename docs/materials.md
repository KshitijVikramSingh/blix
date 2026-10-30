# Materials: what the engine models, what holds it, and what is left

glTF's materials, as the lit pass, the rays and the references take them. The BSDF itself lives in `lab/bsdf` (Python first, then the GLSL twin `src/Blix.Shaders/bsdf_gltf.glsl`); this
page is the map: which extension is read where, which tool judges it, and what each gap is.

## The three consumers

| | reads | holds it |
|---|---|---|
| **the lit pass** (`lit_body.glsl`) | the material's UBO and textures | the engine suite (Section CB: every uniform the scene writes is declared), by eye |
| **the rays** (GI, shadows, the RTGI cook; `ray_query.glsl`, `blix_hwx.mm`) | one surface word a triangle row (albedo, coverage, diffuse roughness), the alpha atlas | the engine suite (Section CA), `tools/testbeds/bsdf.sh` (RTGI's light at the reference's points) |
| **the reference** (`reference_wave.comp`, `--gpu-reference --ref-bsdf`) | the material table, per-row baked means, its own tracer with the panes | `lab/bsdf` tests (the BSDF), the GPU twin test (the GLSL) |

## By extension

| extension | lit pass | reference | rays (GI) |
|---|---|---|---|
| metallic-roughness, normal, occlusion, emissive | yes | yes (factors x row means; emissive texture mean per row) | albedo only |
| alpha MASK / BLEND | cutout; BLEND folded to 0.5 | -- | per-hit alpha atlas; BLEND stochastic; `--ray-blend-cutoff` |
| `KHR_materials_ior`, `_specular` | f0, f90 from both (colour and factor; no textures) | yes | -- |
| `_transmission` | thin: two blended draws; rough or thick: the backdrop pass (blur, refraction) | thin sheet BTDF; a solid refracts | the frame's rays pass panes (no coverage) |
| `_volume` | thickness (x the placement's scale, x its texture) and Beer-Lambert along the refracted chord | media, three deep, Beer-Lambert along the real path | -- |
| `_diffuse_transmission` | yes | yes | the sheet machinery (per-triangle share) |
| `_clearcoat` | yes (factors) | yes (factors) | -- |
| `_sheen` | yes (the engine's rule) | yes (factors) | -- |
| `_iridescence` | yes (factors; maximum thickness) | yes (factors; maximum thickness) | -- |
| `_anisotropy` | yes (factors; the authored tangent) | yes (factors; the tangent from the triangle's UVs) | -- |
| `_emissive_strength`, `_unlit` | yes | yes | unlit as any surface |
| `_dispersion` | -- | -- | -- |

`--material "name:key=value,..."` overrides any of the factors above for the lit shader, the bake and both references at once (`SponzaLoop.MaterialOverrides.cs`), including the diffuse
roughness glTF has no field for.

## Known gaps (and what closing each takes)

1. **Specular indirect light from the scene.** The lit pass reflects the sky cube, occluded; a smooth metal or a coated floor does not reflect the room. The reference does (it traces its
   specular lobes), so the largest remaining gap in `bsdf.sh` is a smooth surface's reflection of its surroundings. A design, not yet built: trace one reflection ray per pixel whose
   lobe is smooth enough to need it (roughness below ~0.5, a Fresnel weight worth the ray) with the frame's Metal tracer (`EncodeTrace`, the hit's albedo, normal, row), and light the
   hit from what the GI already has there -- the RTGI world radiance cache (a cache query per hit), the sky where the ray escapes. Filter by roughness: a rougher lobe reads a blurred
   neighbourhood, so its ray count and its hit shading can drop (a mip of the cache, a shorter ray). The reference validates it: `--ref-bsdf` with specular only (the diffuse response and
   the specular response are separate sums in the wave) against the frame's reflection term, per pixel.
2. **Backdrop panes do not stack.** The backdrop pass draws rough and thick panes over a copy of the scene taken before it: a pane seen through another is not in that copy, and two overlapping
   show the one drawn last. Smooth thin panes (the two blended draws) stack correctly. Closing it: draw the backdrop panes far to near with the chain rebuilt between them (a pane count of
   chains), or sort and write depth in that pass (it changes the depth every later pass reads).
3. **A solid's far wall, and refraction at the screen's edge.** One chord of attenuation and the first surface's bend; what is behind a pane's edge clamps to the screen.
4. **Textures on the extension factors** (clearcoat, sheen, iridescence thickness, anisotropy, specular, transmission, volume thickness in the reference): factors only, in the lit pass and the
   reference alike (they agree, so the comparison is fair; the engine does not yet show them). Each is a bake of a row mean for the reference (as the emissive's is, `BlixRayMr`'s upper half)
   and a sampler on the material for the lit pass.
5. **The rays pass panes whole** (the GI sees the room behind a window through it, unattenuated). `--ray-panes` (built, not yet run) gives a pane's rows a coverage -- the share of rays the glass
   reflects or absorbs, by the hemispherical Fresnel and `1 - T luminance(base)` -- through the partly covered rows' coin, so the light through is `(1 - F) T luminance(base)` as the reference has
   it, in grey (the tint is lost: a per-channel pass needs the rays to carry a colour); what stops reflects diffusely what the glass reflects. First run (2026-10-26, TransmissionTest, open ground): frame / reference 0.988 with the flag against 1.007 without, and the partly covered rows' coin, keyed on the triangle, leaves
   dark speckle inside and under the spheres at this ray count -- not an improvement worth the flag there, so it stays off. To judge it properly: a room with a window, the GI against
   `--ref-bsdf` with and without the flag (`tools/testbeds/bsdf.sh room -- --ray-panes`); the per-triangle coin may want a finer one to be smooth.
6. **Per-texel diffuse transmission** in the GI is a per-triangle share.
7. **Caustics**: none, in the reference or the frame. The sun reaches what is behind a solid by the sky and the bounces only.
8. **Dispersion**: not modelled anywhere.

## Under RTGI: what the cache does not do yet

Measured 2026-10-27 against `--ref-bsdf` on a scratch merge of `origin/bistro` and the material track, with the old `bsdf.sh` (the camera-path reference, `--ref-view`, since
removed). Frame / reference over the camera's pixels, ratio of means -- the cooked lightmap, then RTGI: sheen 1.002, 1.002; coat 0.999, 0.999; transmission 1.007, 0.999; plant 1.007, 1.007;
teacup 0.975, **0.915**; drapes 1.020, **0.837**. `bsdf.sh` now judges at the GPU reference's points instead (below); its first runs replace these numbers.
What the materials do through the lit pass holds on both; what has to pass through the *cache* does not.

RTGI shades a hit as `albedo x (E_ind + sun)`: an opaque diffuse surface. So, by what the cache is asked to carry:

| gap | state | where it goes |
|---|---|---|
| a sheet's far face in the lit pass (cloth, leaves seen from the room) | built on a scratch tree only (`8aee9013`), not merged: the lit pass reads the cache's -N cells (`rtgiBackIrradiance`, `--rtgi-no-back`) | lit_body.glsl; the cells hold nothing until something trains that face |
| a hit on a transmitting sheet (a bounce off a curtain) | built on a scratch tree only (S1), not merged: the hit gives back `(1 - share) albedo E_front + share tint E_back`, and requests the far face's cells | rtgi_train.comp; `--rtgi-no-hit-materials` |
| emission at a hit (a lamp lights the walls it faces) | built on a scratch tree only (S1), not merged: the material table's emission (and the row's texture mean) added as `PI L_e` | rtgi_train.comp |
| foliage | the tracer's per-hit alpha test is the ray's (atlas, stochastic BLEND); translucency is the sheet rows, so S1 | -- |
| glass in the GI | rays pass a pane whole (`--ray-panes` stops a share, in grey); the tint is lost | see below |
| glass's sun shadow | with `--ray-panes` the shadow ray met a pane by a coin per ray: a speckled, colourless shadow (material-room goblets, 2026-10-28). Built, checked on the bench only (static): the ray multiplies the panes' rgb `(1 - F) T base` as it crosses; the mask word carries the luminance (bits 4-7, read by the filter, fill, upsample and lit pass as a fractional visibility) and the chroma (a lit word's upper half, sqrt rgb565), which `lit_body.glsl` multiplies into the sun's irradiance: a red goblet throws red light. Not yet: motion, San Miguel, textured base colour (the factor only), the half-res upsample path (visibility, no tint), the GI's own sun (grey, uncoloured) | `docs/rtgi-glass-shadow.patch` (made against a scratch merge; it no longer applies to the merged tree -- the traced shadows' filter, fill, upsample and lit shader have moved on -- and wants porting by hand; the Metal part is `blix_shadow_cover`) |
| RT reflections, RT refractions | not built | see below |

**Glass tint in the GI.** The stopped-ray share is right in the mean; the colour is not. A ray that passes a pane should carry its tint (`T x base`, per channel) to whatever it reaches. In Metal's
intersector the passed candidates are decided one by one (`blix_covered`), so the throughput can be multiplied there and returned: into the hit record's albedo word at a hit (the albedo the cache
multiplies by), into a miss's unused word (the sky the cache reads: `e = PI sky(d) x tint`). The record is full otherwise, so a miss's word is the one place for the sky case. Needs the intersection function's
payload (blix_hwx.mm, `hwx_trace`) -- untried, since it cannot be tested without the GPU.

**RT reflections and refractions** (the lit pass's smooth specular and a pane's refraction, from the scene instead of the sky cube). The investigation found what the design has to work around:
the pre-pass has no per-pixel material (its surface key is a surface identity, not a row), so a ray pass before the lit pass cannot tell which pixels want a ray. The shape that fits:
1. The lit pass writes, for the pixels that want one, roughness and the specular weight (F x specular colour) into a small target, and leaves their sky specular out of the colour (`specularIBL` for those pixels).
2. A pass after it traces one ray a flagged pixel -- the mirror direction (GGX-sampled above roughness ~0.1), or the refracted one for a transmitting pane (the pane's own chord for a solid) -- with the frame tracer
   (`EncodeTrace`, as `RecordRtgi` does), from the depth and normal.
3. The hit is shaded from the cache: its cell's light at its face (`irradianceAt`, rtgi_train.comp) times its albedo, its sun from the hit's own sun ray, its emission and sheet transmission as S1 -- the sky where the ray escapes.
4. The colour, times the weight, is added to the scene colour; a bilateral filter on the roughness (a rough lobe is a blurred neighbourhood) before it.
`--ref-bsdf` with the specular sums alone is the judge (the wave keeps them apart). Not built: one pass of this size needs the GPU to be debugged as it is written.

## Tools

- `lab/bsdf` -- `uv run python -m pytest tests` (the GPU twin test runs the TonemapLab demo; `--ignore=tests/test_gltf_gpu.py` keeps it off the GPU).
- `tools/testbeds/bsdf.sh [scene...]` -- the material samples (Khronos', drapes, teacup, plant, the gallery) under RTGI against the GPU reference with the glTF BSDF
  (`--gpu-reference --ref-bsdf`), at the reference's points as the gate judges a scene: the frame's incident light against the path trace's irradiance, grouped by the material each
  point sits on (`bsdf_view.py`; `<scene>-points.png` marks them on the shot). It judges the light the GI carries to a point, not the lit pass's own response at the camera (that was the
  camera-path reference, `--ref-view`, removed with the texel GI); no points on transmitting panes (their pixels' light is the surface behind). No ratchets yet.
- `tools/testbeds/gallery.sh` -- cooks `material-gallery`, every sample in the Cornell room.
- `tools/testbeds/setup.sh` with `BLIX_TESTBED_KHRONOS=1` -- the samples' scenes.

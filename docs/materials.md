# Materials: what the engine models, what holds it, and what is left

glTF's materials, as the lit pass, the rays and the references take them. The BSDF itself lives in `lab/bsdf` (Python first, then the GLSL twin `src/Blix.Shaders/bsdf_gltf.glsl`); this
page is the map: which extension is read where, which tool judges it, and what each gap is.

## The three consumers

| | reads | holds it |
|---|---|---|
| **the lit pass** (`lit_body.glsl`) | the material's UBO and textures | `tools/testbeds/bsdf.sh` (frame beside reference), `materials.sh` (the diffuse response) |
| **the rays** (GI, shadows, the cook; `ray_query.glsl`, `blix_hwx.mm`) | one surface word a triangle row (albedo, coverage, diffuse roughness), the alpha atlas | `materials.sh`'s ray view against the raster, the engine suite (Section CA) |
| **the reference** (`reference_wave.comp`, `--ref-bsdf`) | the material table, per-row baked means, its own tracer with the panes | `lab/bsdf` tests (the BSDF), the GPU twin test (the GLSL) |

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
   hit from what the GI already has there -- the lightmap record's resolved light where the hit has a texel (`SponzaLoop.HitCells.cs` is that lookup), or the RTGI world radiance cache
   on the `bistro` branch (a cache query per hit; the natural provider once that branch is the trunk), the sky where the ray escapes. Filter by roughness: a rougher lobe reads a blurred
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
   `--ref-bsdf` with and without the flag; the per-triangle coin may want a per-texel one (the hashed texels') to be smooth.
6. **Per-texel diffuse transmission** in the GI is a per-triangle share.
7. **Caustics**: none, in the reference or the frame. The sun reaches what is behind a solid by the sky and the bounces only.
8. **Dispersion**: not modelled anywhere.

## Tools

- `lab/bsdf` -- `uv run python -m pytest tests` (the GPU twin test runs the TonemapLab demo; `--ignore=tests/test_gltf_gpu.py` keeps it off the GPU).
- `tools/testbeds/materials.sh` -- the ratchets for the diffuse response, the alpha ray view and EON.
- `tools/testbeds/bsdf.sh [scene...]` -- Khronos' material samples through the frame and the BSDF reference, with how they agree (`bsdf_view.py`).
- `tools/testbeds/setup.sh` with `BLIX_TESTBED_KHRONOS=1` -- the samples' scenes.

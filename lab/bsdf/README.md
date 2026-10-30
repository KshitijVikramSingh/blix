# BSDF lab

Reference implementations and property tests for Blix's surface models. Offline; the engine holds its own copies (GLSL and a test-local C# twin),
answerable to these. Same arrangement as `lab/tonemap`: python reference, tests that pin the claims, generated goldens, and a GPU check of the shader.

    cd lab/bsdf && uv run python -m pytest

## EON, the diffuse lobe (`bsdflab/eon.py`)

The fixed Lambert lobe (`albedo / pi`) is replaced by **EON**, the energy-preserving Oren-Nayar BRDF of Portsmouth, Kutz and Hill
(*EON: A practical energy-preserving rough diffuse BRDF*, JCGT 14(1), 2025, arXiv 2410.18026; reference GLSL at github.com/portsmouth/EON-diffuse).
The formulas and constants are the paper's; the reference reproduces its claims:

- `tests/test_eon.py`: r = 0 is Lambert exactly; reciprocity and positivity; the paper's quartic fit of G follows the exact function (2e-3); the
  closed-form average and directional albedos equal the quadrature; **a white surface (rho = 1) conserves energy at every roughness and view angle**
  (quadrature within 6e-3 of 1, the closed form exactly 1); and the visible character (retroreflective at grazing, hemisphere-average albedo of a
  mid grey near Lambert's).
- `tests/test_eon_gpu.py`: the shared GLSL (`src/Blix.Shaders/diffuse_eon.glsl`) against the python, per pixel, through the TonemapLab demo's
  `--pattern 4` (pi * f_EON over 5 roughnesses x 3 view cosines x 3 azimuths x 256 light cosines, half-float tolerance). Needs the demo built in Release.
- `tools/generate_eon_golden.py` writes `src/Blix.Test.Graphics/fixtures/eon_golden.json`; Section BZ of `Blix.Test.Graphics` holds a test-local C# twin
  to it and to the shader's constants, and checks the lit shader still calls EON and has no fixed `albedo / PI` diffuse term left.

### In the engine

`src/Blix.Shaders/diffuse_eon.glsl` is vocabulary, like `pbr.glsl`: `blix_eonBrdf` (a light from a known direction) and `blix_eonAlbedo` (light from
everywhere: radiance out is `E / pi * albedo(mu_o)`, which is Lambert's `E / pi * rho` at r = 0). The RTGI demo's lit shader
(`Blix.Demos.VulkanSponza/Shaders/lit_body.glsl`) uses it for the sun, the sheet glow, the sky's irradiance and the incident field's bounce.

- **On by default** (since the merge into the RTGI tree, 2026-10-31; `--diffuse lambert` for the old lobe, `--ab diffuse` alternates the two in one process).
  It was opt-in on the material track because the frame approximates EON in two ways the reference does not (below): with EON on, the texel GI sat 2-8% off
  the camera-path reference where Lambert sat within 1-3%, and `materials.sh` held both to their own ratchets. That reference and `materials.sh` went with the
  texel GI; under RTGI the gate's GPU reference (`reference_wave.comp`) shades EON from the same baked words, and the gap under RTGI is unmeasured
  (`tools/testbeds/bsdf.sh` and the gate are where it shows). The diffuse roughness is the material's roughness times `--diffuse-roughness-scale` (default 1);
  glTF has no diffuse roughness.
- **The transport conserves what the roughness loses, and the references evaluate EON.** A patch-to-patch bounce has no direction to evaluate a lobe against,
  so the rays' surface bake (`ray_surface_bake.comp`) stores EON's **hemispherical albedo** `rho <E> + rho_ms (1 - <E>)` = `A rho / (1 - (1 - A) rho)`
  (`A = <E>`) at the material's diffuse roughness in the word's low three bytes: every ray consumer (RTGI, the reference) reads it unchanged, and the GPU reference cache's
  key includes it. The top byte now carries the coverage in sixteenths (bits 4..0) and the roughness level the albedo was baked at (bits 7..5, eight levels
  spaced where materials sit: 0, .2, .4, .55, .7, .8, .9, 1). Because the series sums in closed form it **inverts exactly**
  (`rho = hh / (A + (1 - A) hh)`, `eon.base_from_hemispherical`), so the references recover the material's own albedo from the word and the level, which
  the hit record's alpha byte carries. The GPU reference (`reference_wave.comp`; `reference_path.glsl` was the monolithic one's, removed) shades every hit with
  EON itself: the sun with `f_EON(sun, toPrevious) cos`, and the next direction drawn at the hit, cosine-sampled about the normal, so the bounce carries
  `pi f_EON(next, toPrevious)` for the direction actually taken (not an expectation over directions, which would treat the incoming light as isotropic).
  At roughness 0 all of it is Lambert exactly: `--diffuse eon --diffuse-roughness-scale 0` reproduces the Lambert verdicts line for line.
  Four decoders of the top byte changed in step (`ray_query.glsl`, the Metal tracer's source, `RayQueryGpuData`, the texel GI's cutout tests, since removed); a fifth
  reader of it (the patch placement's "cutout" test) had been missed and, with every triangle suddenly under 255, placed no patches: Section BZ now pins
  the packing and the decoders, and the demo logs the roughness levels a bake wrote.
- **What the EON frame still approximates** (and the ratchets record, 2026-10-25): the lit shader multiplies an irradiance by EON's albedo toward the eye
  (exact for a cosine-distributed field; a room seen from inside has its light on the camera's side, where EON retroreflects, so the opaque views read
  2-8% under: drapes 0.921, plant 0.952), and the transport bounces with the hemispherical albedo (the GI's irradiance records read 1.6-1.9% over the
  reference's). Using the view-independent hemispherical albedo instead of the eye-dependent one in the lit shader moved neither (0.923, 0.953), so the
  view-dependence is not the cause. (`--viz 26` and `materials.sh`, which made these measurements, are gone with the texel GI.) One
  discrepancy is NOT explained: khronos-test's two-sided view ratio is 0.808 in EON (0.954 in Lambert).
- **Measured** (scene renders, same camera, EON against Lambert), lit shader only: cornell -1.4% and the Khronos plant -4.8% mean luminance; 4.8% and
  1.3% of pixels move by more than 8/255, the largest moves (57 and 64 of 255) at grazing rims. With the transport as well, in the cornell room: the
  transport alone takes the room's mean x0.9605 (the indirect-lit pixels x0.932), and the room is 7.8% below Lambert in all (0.2449 to 0.2258). A rough mid-grey face-on reflects about 12% less than Lambert
  (E_EON(1) = 0.44 against 0.5 at rho 0.5, r 1) and more at grazing and back-lit angles: EON's flatter look, not a loss of energy overall.

### Not done

Per-material diffuse roughness (a cook-time override, `MaterialPatch`); the CPU reference (`--probe-reference`, since removed) bounced with Lambert; the two-sided
discrepancy above; a directional (not isotropic) lit-shader indirect; the Studio and other older shaders (`pbr.glsl`'s
`blix_cookTorranceBrdf` and the Studio lit shader still use Lambert).

## The glTF surface model as a BSDF (`bsdflab/gltf_bsdf.py`)

The reference every renderer and reference path is built from: the spec's metallic-roughness BRDF (Appendix B) with `KHR_materials_ior`, `_specular`,
`_transmission` (the spec's thin-walled rule: the BTDF is the reflection lobe read at the light's mirror image, so a smooth sheet passes the ray straight on)
and `_diffuse_transmission`, as `eval`, `pdf` and `sample` (one lobe a sample, so the estimator is the mixture's). `tests/test_gltf_bsdf.py` pins it: Fresnel's
numbers and the specular extension's tint and f90, reciprocity, the pdf integrating to what its lobes can reach, **sampling against evaluation** (the mean
sample weight equals the integral of f cos, for seven materials at two view angles), no energy gained at any view angle, and a clear pane reflecting and
transmitting nearly all of what it gets.

### The refracting interface and the volume (`bsdflab/dielectric.py`)

What a transmitting **solid** does at its surface where the thin sheet only passes the ray on: Walter et al.'s rough dielectric (GGX, exact unpolarised Fresnel,
reflection with F, refraction with 1 - F along Snell at the microfacet), its transmission lobe the BTDF for **radiance** (the eta^2 is in it, so a path applies
nothing at a crossing and a closed solid nets 1), the frame flipped when the ray is inside; and `KHR_materials_volume`'s Beer-Lambert attenuation
(sigma = -ln(colour) / distance per channel). `tests/test_dielectric.py`: Fresnel at normal incidence and the critical angle, a smooth interface reflecting 4% and
passing 0.96 / eta^2 as radiance, total internal reflection from inside at grazing, generalised reciprocity (f / eta_o^2 symmetric) from both sides, sampling
against evaluation over alpha, angle and side, the pdf's integral, and the attenuation distance leaving exactly the colour.

### The layers (`tests/test_gltf_layers.py`)

`gltf_bsdf.py` also carries **clearcoat** (a smooth GGX layer of ior 1.5; the base beneath is darkened by the coat's reflectance toward the *view*, the Khronos viewer's rule:
the spec's fresnel_mix read literally weighs it by F(V.H), which gave a plastic under a rough coat 1.12 at cos 0.15), **sheen** (Charlie, the engine's `sheen.glsl` rule,
the directional albedo by a midpoint grid that the GLSL repeats), **iridescence** (Belcour and Barla's thin film as the Khronos viewer evaluates it; at zero thickness it is
the plain Fresnel at normal incidence only: the viewer's film interface adds (1 - cos)^5 off the normal) and **anisotropy** (GGX with alpha_t = mix(alpha, 1, s^2) along the
rotated tangent). Held by: every layer off is the base model exactly; anisotropy 0 is isotropic and a quarter-turn swaps the axes; the coat over a black base reflects 4% at
normal incidence; the film is a reflectance in [0, 1] that swings with thickness and parts the colours; the sheen albedo converges; reciprocity where the model has it (not the
sheen's or the coat's base darkening, which read the view angle); sampling against evaluation for eight layered materials at two angles; no energy gained; a nested interface's
Snell between 1.33 and 1.5. `KHR_materials_specular`'s factor now scales f0 as well as f90 (as the extension's formula has it).

Emission and nested solids are the path tracer's: `Material.eta_out` is the index of the medium a surface is entered from.

Next layers, each with its tests beside these: then the GLSL twin (`src/Blix.Shaders/bsdf_gltf.glsl`), a per-row material table beside the surface words, and
`reference_wave.comp` shading with it (camera paths and the gate's points both).

### In the engine: `--ref-bsdf`

`src/Blix.Shaders/bsdf_gltf.glsl` is the GLSL twin of both modules (python first; same names where they carry over). `Blix.Demos.TonemapLab`'s patterns 6 and 7 render its
evaluation and densities for a table of materials, view angles and light directions, and `tests/test_gltf_gpu.py` holds them to the python per pixel
(needs the demo built in Release and a GPU).

`reference_wave.comp` shades with it under `--ref-bsdf` (camera paths and the gate's points alike): per triangle row the material (`ReferenceMaterialRows` into
`ReferenceMaterialTable`, `SponzaLoop.ReferenceMaterials.cs`) and the baked specular roughness and metallic means (`ray_surface_bake.comp`, `BlixRayMr`); **thin or
solid per material** (a transmitting material with a `KHR_materials_volume` thickness above 0 is a solid: the path refracts in, is attenuated by Beer-Lambert
inside, and leaves through the interface from inside; with none it is a thin-walled sheet). The frame's rays pass the panes (they carry no coverage), so the reference
runs on **its own tracer** with the pane rows solid in a copy of the surface words. The sun goes on through a thin pane by its straight-through transmission in
two extra rounds; a solid blocks it (no caustics).

`--material "name:key=value,..."` overrides a material's parameters for the lit shader, the bake and both references alike (roughness, metallic, base, ior,
specular, transmission, thickness, att-distance, att, dt, and `diffuse-roughness`, the EON lobe's, which glTF has no field for).

### In the lit shader (`lit_body.glsl`): transmission and clearcoat

- **Clearcoat** follows the python: the base (diffuse, specular, sheen, the bounce, emission) is scaled by `1 - coat F(N.V)` and the coat adds the sun's GGX highlight (F at the
  half vector) and the prefiltered sky at the coat's roughness through the split-sum LUT (F0 0.04). Factors only (`uMaterialParams3.yz`); a branch most pixels never take, so
  materials with a coat join `richMaterials` and the lean build compiles it out.
- **Transmission** is two draws per pane instead of one alpha blend (`BlendMode.Multiply`, new in `Blix.Graphics`): the scene behind filtered by `(1 - F) T baseColor` times the
  volume's Beer-Lambert attenuation along the refracted chord (`uMaterialParams4`, `uAttenuation`, the thickness texture in `uThicknessTex`), then `F` times the sky's reflection added
  (`lit_glass_filter.frag`, `lit_glass_reflect.frag`; F from the material's ior and specular factor). What it cannot do: bend the view of what is behind a solid and blur it
  for roughness, which need the scene as a texture (a solid shows what is behind it unbent; a rough pane is as clear as a smooth one).
- `tools/testbeds/bsdf.sh [scene...]` renders Khronos' TransmissionTest, DragonAttenuation, ClearCoatTest, AnisotropyDiscTest and SheenChair through the frame and the reference and
  prints how they agree (`bsdf_view.py`; `-compare.png` is frame | reference | ratio). 2026-10-26: the thin panes' tints and the coat's swatches agree to a few percent in the mean;
  the attenuating dragon's body reads darker and redder in the frame than in the reference (the thickness factor times a texture against the true chord).

- **Sheen** was already in the lit shader (the engine's `sheen.glsl` rule, the LUT's E); against the reference on Khronos' SheenChair the frame sits within 1% in the mean (the swatch box 0.96).
- **Iridescence** is `iridescence.glsl` (shared with `bsdf_gltf.glsl`, so the python, the BSDF and the lit pass are one function): the film's Fresnel replaces Schlick's in the sun's lobe and
  the sky's, weighed by the factor, on the mixed F0 (the reference mixes a dielectric's film and a metal's; the two part only at fractional metallic). Factors only; the maximum thickness
  stands where a texture would vary it. Against IridescenceSuzanne the film's hues show in the frame, but the picture is dominated by something else the frame lacks: **specular indirect
  light from the scene** -- the frame reflects the sky cube only, the reference reflects the floor and everything else, so smooth metals read darker in the frame.

- **The glass backdrop** (`SponzaLoop.GlassBackdrop.cs`, single-sample; `--no-glass-backdrop` for the two-draw panes alone): a forward pass cannot read the colour it writes, so a ROUGH pane (blur) or
  a SOLID (a volume thickness: the view bends) is drawn in its own pass after a copy of the scene so far and four halvings of it (`scene_behind_copy.frag`, `scene_behind_down.frag`): each
  pixel replaced by what is behind it -- read at the screen position of the exit point of the refracted ray (a solid), at the level the pane's roughness names -- times `(1 - F) T baseColor`
  and the attenuation, plus the sky's reflection (`lit_glass_backdrop.frag`). Smooth thin panes stay on the two blended draws, which stack. On TransmissionTest the rough row now blurs
  as the reference's does, and the dragon bends the checker through its body; frame / reference over the picture reads 1.007 and 0.995. Not done: panes seen through a backdrop pane (not in
  the chain), two backdrop panes overlapping (the last drawn shows), the far wall of a solid, and the dragon's colour (the frame's red against the reference's amber: its thickness
  factor times a texture against the true chord).

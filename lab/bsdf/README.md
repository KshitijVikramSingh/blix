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

- **Default on.** The diffuse roughness is the material's roughness times `--diffuse-roughness-scale` (default 1); glTF has no diffuse roughness.
  `--diffuse lambert` is the old lobe; `--ab diffuse` alternates the two in one process.
- **The transport conserves what the roughness loses.** A patch-to-patch bounce (the patch solve, a gather's hits, the probes, both references) has no
  direction to evaluate a lobe against, so the rays' surface bake (`ray_surface_bake.comp`) stores EON's **hemispherical albedo** at the material's
  diffuse roughness in place of the base colour: `rho <E> + rho_ms (1 - <E>)`, the fraction of the light that arrives from anywhere and leaves
  whichever way (`eon.hemispherical_albedo`: the cosine average of the directional albedo, tested against quadrature; below rho for a rough grey,
  rho itself for white). Every consumer reads that word, and the transport cache's key includes it, so nothing else changed and the two modes keep
  separate caches. Roughness is the material's factor times its metallic-roughness texture's mean green over the bake's sixteen points. The view-dependent
  albedo toward the eye is applied at the final shading. Not modelled: the lobe's shape at a hit (a sun-lit hit reflects its sun with Lambert's cosine).
  The bake runs once at load, so `--diffuse` is read at start and `--ab diffuse` toggles only the lit shader.
- **Measured** (scene renders, same camera, EON against Lambert), lit shader only: cornell -1.4% and the Khronos plant -4.8% mean luminance; 4.8% and
  1.3% of pixels move by more than 8/255, the largest moves (57 and 64 of 255) at grazing rims. With the transport as well, in the cornell room: the
  transport alone takes the room's mean x0.9605 (the indirect-lit pixels x0.932), and the room is 7.8% below Lambert in all (0.2449 to 0.2258). A rough mid-grey face-on reflects about 12% less than Lambert
  (E_EON(1) = 0.44 against 0.5 at rho 0.5, r 1) and more at grazing and back-lit angles: EON's flatter look, not a loss of energy overall.

### Not done

Per-material diffuse roughness (a cook-time override, `MaterialPatch`); the lobe's shape at a hit; the Studio and other older shaders (`pbr.glsl`'s
`blix_cookTorranceBrdf` and the Studio lit shader still use Lambert).
